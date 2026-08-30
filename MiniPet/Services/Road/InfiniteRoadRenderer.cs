using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniPet.Services.Road
{
    /// <summary>
    /// F18 无限之路渲染器（纯 SkiaSharp 绘制，无窗口依赖；算法 §4/§6）。
    /// 层序（对齐 MapService.RenderViewport 的 back→内容→front-back 骨架）：
    /// ①BacksNonFront（视差/平铺，公式照抄 DrawBackViewport）→ ②FarObjs（0.5× 视差带）
    /// → ③柱序列（90×900 柱位图缓存 blit）→ ④实体（EntityBitmapResolver 注入帧位图）→ ⑤BacksFront。
    /// crossfade（Incoming 非空）：整帧先画 alpha=1-CrossfadeAlpha 的 Current，再叠 alpha=CrossfadeAlpha 的
    /// Incoming（双路各自 ScrollOriginX 独立）。只读 RoadFrame 快照：一切流逝型视觉效果
    /// （back 自动滚动）一律取 frame.RoadClockMs 绝对时钟，禁止对快照累加 delta。
    /// 坐标归一：源图世界 (X,Y) − (SourceAnchorX,SourceAnchorY) 进路空间；
    /// 垂直映射固定 screenY = 窗口高×62% + (路y − InitialSurfaceY)，缩放窗口时随 h 重算并 clamp。
    /// </summary>
    public sealed class InfiniteRoadRenderer : IDisposable
    {
        /// <summary>
        /// 实体帧位图解析注入点（契约外，D4 接线到路属条带缓存：
        /// 输入 RoadEntityState（含 Action/FrameIndex/FacingLeft/Alpha），输出已裁剪帧位图；null = 该实体本帧不画）。
        /// </summary>
        public Func<RoadEntityState, SKBitmap?>? EntityBitmapResolver { get; set; }

        // WZ 精灵来源（null = 离线/无 WZ，柱与 back 位图不画，实体仍可经注入点渲染）
        private readonly WzService? _wz;

        // 全渲染器单锁：RenderFrame 串行 + 柱/精灵缓存并发保护（渲染只发生在窗口渲染线程，锁为双保险）
        private readonly object _gate = new();

        // 帧输出双缓冲（窗口直拷期间不复用同一位图；_rendering CAS 保证渲染串行，轮转即安全）
        private SKBitmap? _frameA, _frameB;
        private int _frameSlot;

        // 柱位图缓存：键 = 素材代际|MapId|模板id|SurfaceY（crossfade 双包并存全局唯一，不跨源图碰撞）
        private readonly Dictionary<string, (SKBitmap Bmp, int TopPad)> _columnCache2 = new();
        private readonly LinkedList<string> _columnLru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _columnLruNodes = new();

        // 精灵缓存：wzPath → (位图, originX, originY)（null 位图作负缓存，防重复 WZ 提取）
        private readonly Dictionary<string, (SKBitmap? Bmp, int Ox, int Oy)> _spriteCache = new();
        private SKColor? _skyBaseColor; // 天空底色（首次 Sky 绘制采样缓存；天空固定不参与换歌——无需失效）
        private readonly LinkedList<string> _spriteLru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _spriteLruNodes = new();
        private const int MaxSpriteCacheEntries = 256;

        // 复用 paint（整帧持 _gate，单实例安全；FilterQuality.None 保像素风）
        private readonly SKPaint _paint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };

        public InfiniteRoadRenderer(WzService? wz = null)
        {
            _wz = wz;
        }

        /// <summary>
        /// 渲染一帧（返回渲染器内部池化位图——调用方只读直拷，禁止 Dispose/长期保留；
        /// 下一帧渲染会复用轮转位图）。IsPaused 帧照常重画（时钟静止即静止画面）。
        /// </summary>
        private long _slowFrameCount;
        public SKBitmap? RenderFrame(RoadFrame f, int w, int h)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var __result = RenderFrameInner(f, w, h);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 40)
            {
                _slowFrameCount++;
                Console.Error.WriteLine($"[Road] 慢帧 {sw.ElapsedMilliseconds}ms（累计 {_slowFrameCount}）w={w}");
            }
            return __result;
        }

        private SKBitmap? RenderFrameInner(RoadFrame f, int w, int h)
        {
            if (f == null || w <= 0 || h <= 0) { return null; }
            lock (_gate)
            {
                // 第一步：输出位图双缓冲（尺寸变化才重建）
                var bmp = _frameSlot == 0 ? _frameA : _frameB;
                if (bmp == null || bmp.Width != w || bmp.Height != h)
                {
                    bmp?.Dispose();
                    bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                    if (_frameSlot == 0) { _frameA = bmp; } else { _frameB = bmp; }
                }
                _frameSlot ^= 1;
                // 第二步：crossfade 双路叠加（Incoming 非空：Current 淡出 + Incoming 淡入，各自 scroll 原点独立）
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    // Sky 层先画（不参与 crossfade；换歌不动）
                    if (f.Sky != null)
                    {
                        try { DrawSkyBacks(canvas, f.Sky, f, w, h); }
                        catch (Exception ex) { Console.Error.WriteLine($"[Road] DrawSkyBacks 异常: {ex.Message}"); }
                    }
                    double a = Math.Clamp(f.CrossfadeAlpha, 0, 1);
                    if (f.Incoming != null && a > 0.999)
                    {
                        try { DrawRoadCore(canvas, f.Incoming, f, w, h, 1.0); }
                        catch (Exception ex) { Console.Error.WriteLine($"[Road] DrawRoadCore(Incoming) 异常: {ex.Message}"); }
                    }
                    else if (f.Incoming != null)
                    {
                        try { DrawRoadCore(canvas, f.Current, f, w, h, 1.0 - a); }
                        catch (Exception ex) { Console.Error.WriteLine($"[Road] DrawRoadCore(Current) 异常: {ex.Message}"); }
                        if (a > 0.001)
                        {
                            try { DrawRoadCore(canvas, f.Incoming, f, w, h, a); }
                            catch (Exception ex) { Console.Error.WriteLine($"[Road] DrawRoadCore(Incoming) 异常: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        try { DrawRoadCore(canvas, f.Current, f, w, h, 1.0); }
                        catch (Exception ex) { Console.Error.WriteLine($"[Road] DrawRoadCore 异常: {ex.Message}"); }
                    }
                    // Sky 前置层最后画
                    if (f.Sky != null)
                    {
                        try { DrawSkyFrontBacks(canvas, f.Sky, f, w, h); }
                        catch (Exception ex) { Console.Error.WriteLine($"[Road] DrawSkyFront 异常: {ex.Message}"); }
                    }
                }
                return bmp;
            }
        }

        /// <summary>清柱位图缓存（换源图交换完成/WZ 重载时释放旧代际；由 D4 控制器调用）。</summary>
        public void ClearColumnCache()
        {
            lock (_gate)
            {
                foreach (var bmp in _columnCache2.Values)
                {
                    try { bmp.Bmp.Dispose(); } catch { }
                }
                _columnCache2.Clear();
                _columnLru.Clear();
                _columnLruNodes.Clear();
            }
        }

        /// <summary>清全部缓存与帧缓冲（窗口 HideRoad/销毁时调用，释放 Skia 原生句柄）。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var bmp in _columnCache2.Values)
                {
                    try { bmp.Bmp.Dispose(); } catch { }
                }
                _columnCache2.Clear();
                _columnLru.Clear();
                _columnLruNodes.Clear();
                foreach (var sp in _spriteCache.Values)
                {
                    try { sp.Bmp?.Dispose(); } catch { }
                }
                _spriteCache.Clear();
                _spriteLru.Clear();
                _spriteLruNodes.Clear();
                try { _frameA?.Dispose(); } catch { }
                try { _frameB?.Dispose(); } catch { }
                _frameA = null;
                _frameB = null;
                _paint.Dispose();
            }
        }

        // ====== private ======

        /// <summary>画一条路（路层三层层序），roadAlpha 为 crossfade 整路透明度（0~1）。
        /// back 层已分离至 RoadSky（RenderFrame 层从 Sky 画），本方法只画路面层。</summary>
        private void DrawRoadCore(SKCanvas canvas, RoadRenderRoad road, RoadFrame frame, int w, int h, double roadAlpha)
        {
            // ①远景 obj 剪影带（0.5× 慢速视差，坐标同归一）
            DrawFarObjs(canvas, road, w, h, roadAlpha);
            // ②柱序列（位图缓存 blit，按世界 x − 本路 ScrollOriginX 定位）——纯地板（tile+填充）
            DrawColumns(canvas, road, w, h, roadAlpha);
            // ②b obj 独立层（2026-09-01 甲方：先平铺地板再在地板上放 obj——完整绘制不裁剪；
            // 柱位图 90px 烤入 obj 会截断宽 obj（树半棵根因）——世界坐标独立层 + (Z,Id) 排序）
            DrawObjLayer(canvas, road, w, h, roadAlpha);

            // ③实体（NPC/怪，帧位图经 EntityBitmapResolver 注入）
            DrawEntities(canvas, road, w, h, roadAlpha);
        }

        /// <summary>画 Sky 非前置层（层序最前；先铺天空底色带——back sprite 底边与路表面线之间存在
        /// 源图无覆盖带，透明会透出桌面）。</summary>
        private void DrawSkyBacks(SKCanvas canvas, RoadSky sky, RoadFrame frame, int w, int h)
        {
            if (!_skyBaseColor.HasValue)
            {
                _skyBaseColor = SampleSkyBaseColor(sky);
            }
            _paint.Color = _skyBaseColor.Value;
            canvas.DrawRect(0, 0, w, (float)(h * 0.62), _paint);
            DrawSkyBacksInner(canvas, sky, frame, w, h, front: false);
        }

        /// <summary>
        /// 天空底色采样：取首个非前置 back 位图上半部不透明像素均值（back 序首元素 = 全幅天空
        /// 竖strip，其上半部即深空色）；无可用位图回退枫色天空蓝。每 Sky 生命周期只采样一次
        /// （_skyBaseColor 缓存；天空不参与换歌 crossfade，无需失效）。
        /// </summary>
        private SKColor SampleSkyBaseColor(RoadSky sky)
        {
            if (sky.BacksNonFront != null)
            {
                foreach (var b in sky.BacksNonFront)
                {
                    if (b.SpineAni == 2) { continue; }
                    var (bmp, _, _) = GetSprite(BackPath(b));
                    if (bmp == null || bmp.Height < 100) { continue; }
                    long r = 0, g = 0, bl = 0;
                    int n = 0;
                    int half = bmp.Height / 2;
                    for (int y = 0; y < half; y += 4)
                    {
                        for (int x = 0; x < bmp.Width; x += 4)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (c.Alpha == 0) { continue; }
                            r += c.Red;
                            g += c.Green;
                            bl += c.Blue;
                            n++;
                        }
                    }
                    if (n > 0)
                    {
                        return new SKColor((byte)(r / n), (byte)(g / n), (byte)(bl / n));
                    }
                }
            }
            return new SKColor(154, 196, 236); // 无可用采样：枫色天空蓝兜底
        }

        /// <summary>画 Sky 前置层（层序最后——覆盖柱/实体）。</summary>
        private void DrawSkyFrontBacks(SKCanvas canvas, RoadSky sky, RoadFrame frame, int w, int h)
        {
            DrawSkyBacksInner(canvas, sky, frame, w, h, front: true);
        }

        /// <summary>画 Sky 层 back（非前置→层序最前；前置→层序最后）；相机用 Sky 冻结锚点/CamX。</summary>
        private void DrawSkyBacksInner(SKCanvas canvas, RoadSky sky, RoadFrame frame, int w, int h, bool front)
        {
            var backs = front ? sky.BacksFront : sky.BacksNonFront;
            if (backs == null || backs.Count == 0) { return; }
            // Sky 有自己的坐标锚和相机——不引用任何 RoadRenderRoad 字段
            double camX = sky.CamX + w / 2.0;
            double camY = sky.AnchorSurfaceY;
            foreach (var back in backs)
            {
                DrawBackSingle(canvas, back, sky.SourceAnchorX, sky.SourceAnchorY, camX, camY, frame.RoadClockMs, w, h, 1.0);
            }
        }

        /// <summary>
        /// 单 back 绘制——视差/自动滚动公式对齐 MapService.DrawBackViewport（R2 GetMeshBack）：
        /// 水平 ScrollH: worldX += (rx×5×clock/1000) % cx；否则视差 worldX += camX×(100+rx)/100；
        /// 垂直同理（camY 路空间取 InitialSurfaceY）；整体取 floor；tileMode 位标志 bit0-H bit1-V bit2-ScrollH bit3-ScrollV；
        /// ScreenMode（契约 bool，提取期已按 R2 DisplayMode=0 判定可见性）false / Alpha==0 / spine 跳过。
        /// 平铺段 = PIXI 权威语义（MaplestoryTilingSprite.update）：%cx/%cy 求模对齐首格 + ceil 精确裁剪计数 +
        /// rect 用 width−originX / height−originY（见下方实现注释），非旧版无脑 ±1 循环。
        /// 差异点：源图坐标先经锚点归一进路空间，world→screen 的 zoom=1 且
        /// 垂直锚由 screenH/2 改为 h×62%（路表面锚），Dx/Dy 作为提取期附加位移并入归一坐标。
        /// 当前唯一消费方 = Sky 天空层（路层 back 已分离至 RoadSky；锚点/相机由调用方传入）。
        /// </summary>
        private void DrawBackSingle(SKCanvas canvas, RoadBack back, int sourceAnchorX, int sourceAnchorY, double camX, double camY, double clockMs, int w, int h, double roadAlpha)
        {
            if (!back.ScreenMode) { return; }
            if (back.Alpha == 0) { return; }
            if (back.SpineAni == 2) { return; }
            var (bmp, ox, oy) = GetSprite(BackPath(back));
            if (bmp == null) { return; }
            int tm = back.TileMode;
            int cx = back.Cx > 0 ? back.Cx : bmp.Width;
            int cy = back.Cy > 0 ? back.Cy : bmp.Height;
            // 坐标归一：源图世界 → 路空间（+ 提取期 Dx/Dy 附加位移）
            double nx = back.X - sourceAnchorX + back.Dx;
            double ny = back.Y - sourceAnchorY + back.Dy;
            if ((tm & 4) != 0)
            {
                if (cx > 0) { nx += back.Rx * 5.0 * clockMs / 1000.0 % cx; }
            }
            else
            {
                nx += camX * (100 + back.Rx) / 100.0;
            }
            if ((tm & 8) != 0)
            {
                if (cy > 0) { ny += back.Ry * 5.0 * clockMs / 1000.0 % cy; }
            }
            else
            {
                ny += camY * (100 + back.Ry) / 100.0;
            }
            nx = Math.Floor(nx);
            ny = Math.Floor(ny);
            // world → screen（zoom=1；垂直 62% 表面锚）
            float baseX = (float)((nx - camX) + w / 2.0);
            float baseY = (float)((ny - camY) + h * 0.62);
            _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(back.Alpha * roadAlpha, 0, 255));
            bool tileH = (tm & 1) != 0, tileV = (tm & 2) != 0;
            if (!tileH && !tileV)
            {
                DrawSpriteAt(canvas, bmp, ox, oy, baseX, baseY, false, 1f);
                return;
            }
            // 平铺（对齐 PIXI 权威语义 mapRederWeb/frontend/src/index.ts MaplestoryTilingSprite.update :214-242 精确裁剪）：
            // ①首格求模对齐：tileStartRight = (basePos.x + rect.right − screenRect.left) % cx（结果进 (0,cx]），
            //    再 tileStartLeft = tileStartRight − rect.width —— 网格锚定屏幕（screenRect.left/top=0 已并入 baseX/baseY），
            //    非旧实现「baseX 直接起铺」的裸循环；②裁剪计数：tileCount = ceil((right − tileStartLeft)/cx)，
            //    首格完全在屏幕右/下缘外 → tileCount=0 不画（不再 ±1 冗余铺满）；③rect 用 width−originX / height−originY
            //    （PIXI Sprite rect = (−pivot.x,−pivot.y,width,height)）。锚点公式 baseY = back.Y−SourceAnchorY+Dy+视差项不变。
            int tileCountX = 1, tileCountY = 1;
            if (tileH && cx > 0)
            {
                double tileStartRight = baseX + (bmp.Width - ox);
                tileStartRight %= cx;
                if (tileStartRight <= 0) { tileStartRight += cx; } // JS % 同号语义：负数/0 归位进 (0, cx]
                double tileStartLeft = tileStartRight - bmp.Width;
                if (tileStartLeft >= w)
                {
                    tileCountX = 0;
                }
                else
                {
                    tileCountX = (int)Math.Ceiling((w - tileStartLeft) / cx);
                    baseX = (float)(tileStartLeft + ox); // basePos.x = tileStartLeft − rect.x（rect.x = −originX）
                }
            }
            if (tileV && cy > 0)
            {
                double tileStartBottom = baseY + (bmp.Height - oy);
                tileStartBottom %= cy;
                if (tileStartBottom <= 0) { tileStartBottom += cy; }
                double tileStartTop = tileStartBottom - bmp.Height;
                if (tileStartTop >= h)
                {
                    tileCountY = 0;
                }
                else
                {
                    tileCountY = (int)Math.Ceiling((h - tileStartTop) / cy);
                    baseY = (float)(tileStartTop + oy);
                }
            }
            if (tileCountX <= 0 || tileCountY <= 0) { return; } // PIXI tileCount=0：首格已完全在屏幕右/下缘外 → 整层不画
            for (int iy = 0; iy < tileCountY; iy++)
            {
                for (int ix = 0; ix < tileCountX; ix++)
                {
                    DrawSpriteAt(canvas, bmp, ox, oy, baseX + ix * cx, baseY + iy * cy, false, 1f);
                }
            }
        }

        /// <summary>远景 obj 剪影带：0.5× 视差（相机位移减半感知），坐标经 SourceAnchor 归一，垂直同 62% 锚。</summary>
        private void DrawFarObjs(SKCanvas canvas, RoadRenderRoad road, int w, int h, double roadAlpha)
        {
            if (road.FarObjs == null || road.FarObjs.Count == 0) { return; }
            double camX = road.ScrollOriginX + w / 2.0;
            double camY = road.InitialSurfaceY;
            foreach (var fo in road.FarObjs)
            {
                var (bmp, _, _) = GetSprite(ObjPath(fo.OS, fo.L0, fo.L1, fo.L2));
                if (bmp == null) { continue; }
                double nx = fo.X - road.SourceAnchorX;
                double ny = fo.Y - road.SourceAnchorY;
                float sx = (float)((nx - camX) * 0.5 + w / 2.0);
                float sy = (float)((ny - camY) * 0.5 + h * 0.62);
                _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * roadAlpha, 0, 255));
                DrawSpriteAt(canvas, bmp, 0, 0, sx, sy, false, 1f); // 提取期已烤入 origin（可见坐标）
            }
        }

        /// <summary>
        /// 柱序列 blit：screenX = WorldX − ScrollOriginX（窗口左缘 = 本路 scroll 原点）；
        /// 垂直 screenY = h×62% + (SurfaceY − InitialSurfaceY)（表面精确落位）；
        /// 位图底部对齐窗口底（DrawBitmap 目标矩形 (screenX, sy, screenX+90, h)——垂直按需拉伸，水平 1:1）。
        /// </summary>
        private void DrawColumns(SKCanvas canvas, RoadRenderRoad road, int w, int h, double roadAlpha)
        {
            if (road.Columns == null) { return; }
            float prevRight = float.MinValue; // 链式对齐：消除浮点累积的段间白缝
            foreach (var col in road.Columns)
            {
                float screenX = (float)(col.WorldX - road.ScrollOriginX);
                float colW = RoadConstants.ColW * Math.Max(1, col.WidthCols);
                // 剪裁：柱完全在窗口外（含全宽）跳过
                if (screenX + colW <= 0 || screenX >= w) { prevRight = screenX + colW; continue; }
                if (prevRight > float.MinValue && Math.Abs(screenX - prevRight) < 1.5f)
                {
                    screenX = prevRight; // 亚像素缝贴齐前柱右缘（白缝修复）
                }
                float sy = (float)(h * 0.62 + (col.SurfaceY - road.InitialSurfaceY));
                // clamp：表面落到窗口底以下 → 柱不可见跳过（缩放窗口后的边界保护）
                if (sy >= h - 1) { continue; }
                if (sy < 0) { sy = 0; }
                var built = GetColumnBitmap(road, col);
                if (built == null) { continue; }

                var bmp = built.Value.Bmp;
                float drawY = sy - built.Value.TopPad; // 位图含表面上方的装饰区（topPad），表面对齐 sy 须上移
                _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * roadAlpha, 0, 255));
                using var blitPaint = new SkiaSharp.SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * roadAlpha, 0, 255)), IsAntialias = false };
                                // 1:1 直贴（2026-08-28 修「素材扭曲/砖块破碎」：此前 900 全图拉伸到 (h−sy) ≈4× 纵向压缩
                // 把表面砖块压成细线、pad 区压成密集碎纹）。位图 y=0 即表面行，(screenX, sy) 原比例贴，
                // 柱身向下自然延伸出窗底由裁剪收口；表面以上（Dy<0 的装饰）会随位图上缘露出。
                float bw = RoadConstants.ColumnBmpW * col.Template.WidthCols;
                canvas.DrawBitmap(bmp, new SKRect(screenX, drawY, screenX + bw, drawY + bmp.Height), blitPaint);
            }
        }

        /// <summary>
        /// 取/建柱位图（缓存键 = 素材代际|MapId|模板id|SurfaceY，LRU 上限 RoadConstants.BscCacheMax）。
        /// 柱位图构建（90×900 固定源，与窗口高无关）：tiles 按相对表面 Dy 堆叠（tile.x 为柱中心 → origin 对齐 x=45）；
        /// BscPad 底块循环堆到位图底；绑定 obj 画在 (45+Dx, Dy)（算法 §4.1「绑定 obj 画柱位图时入缓存」）。
        /// </summary>
        private (SKBitmap Bmp, int TopPad)? GetColumnBitmap(RoadRenderRoad road, RoadColumn col)
        {
            // 调用方已持 _gate（DrawColumns ← RenderFrame 锁内）
            string key = $"{road.BuildGeneration}|{road.MapId}|{col.Template.Id}|{col.SurfaceY}";
            if (_columnCache2.TryGetValue(key, out var cached))
            {
                TouchColumn(key);
                return cached;
            }
            var builtOpt = BuildColumnBitmap(road, col);
            if (builtOpt == null) { return null; }
            var built = builtOpt.Value;
            _columnCache2[key] = built;
            _columnLruNodes[key] = _columnLru.AddLast(key);
            // LRU 淘汰：超上限逐出最久未用（Dispose 释放原生句柄）
            while (_columnCache2.Count > RoadConstants.BscCacheMax && _columnLru.First != null)
            {
                var oldest = _columnLru.First.Value;
                _columnLru.RemoveFirst();
                _columnLruNodes.Remove(oldest);
                if (_columnCache2.TryGetValue(oldest, out var ev))
                {
                    try { ev.Bmp.Dispose(); } catch { }
                    _columnCache2.Remove(oldest);
                }
            }
            return built;
        }

        /// <summary>跨 fade 预构建（f5e 诊断：Incoming 柱位图首建 43~54ms 在渲染线程 → crossfade 期间空白/丢帧）：
        /// 换歌交换后立即预热 Incoming 首屏列（键 = buildGen 同缓存，预建后渲染线程零构建）。UI 线程调用。</summary>
        public void PrewarmColumns(RoadRenderRoad road, IReadOnlyList<RoadColumn> cols)
        {
            lock (_gate) // BuildColumnBitmap/GetSprite 契约须持 _gate（渲染线程并发读缓存——无锁写坏/竞态崩溃源）
            {
            if (road == null || cols == null) { return; }
            int warm = 0;
            foreach (var col in cols)
            {
                var tpl = col.Template;
                if (tpl.Tiles.Count == 0 && tpl.Objs.Count == 0) { continue; }
                string key = $"{road.BuildGeneration}\u0001{col.Template.Id}";
                if (_columnCache2.ContainsKey(key)) { continue; }
                var built = BuildColumnBitmap(road, col);
                if (built.HasValue) { _columnCache2[key] = built.Value; }
                // 2026-09-02：预构建全部列（素材池块数有限）——渲染零首建慢帧（悬空冻结瞬间根治）
            }
            }
        }

        /// <summary>柱内绘制操作（合并排序单元）。Kind：0=tile 1=obj 2=顶行补满 3=向下填充 4=底块循环。
        /// X/Y = 位图内绘制点（tile/obj 已含 topPad 平移；填充 op 的 Y = 起始行）。</summary>
        private readonly record struct ColumnDrawOp(int Z, int Id, int Kind, SKBitmap Bmp, float X, int Y)
        {
            public const int KindTile = 0;
            public const int KindObj = 1;
            public const int KindTopFill = 2;
            public const int KindFillDown = 3;
            public const int KindBottomLoop = 4;
        }

        /// <summary>
        /// 构建单柱 90×900 位图（位图 y=0 = 模板表面行；tiles/obj 的 Dy 均相对表面）。
        /// 柱内 tile+obj 按 compositeZIndex(Z, Id)（Z 主 Id 次）混排绘制：Z 低先画=后面、Z 高后画=前面——
        /// 对齐权威 MapService.RenderViewport「同层混合按 (Z0,Z1) 升序排序」与 PIXI index.ts compositeZIndex(z, id)
        /// （normalize 单调非降 → OrderBy(Z).ThenBy(Id) 与 compositeZIndex 值序完全等价）。
        /// 旧顺序「先全部 tile 再全部 obj」废弃（obj 恒压 tile 层 → 松树/装饰错误压地面）；
        /// tile 的 Id = 源图层内序原值；obj 契约仅带 Z（同 Z 用模板收集序=层序稳定兜底）。
        /// 填充 op（顶行补满/向下填充/底块循环）是源 tile 内容的延伸：取源 tile 的 Z、Id 置 -1
        /// （同 Z 组内最先画——保持旧「填充恒在 obj 下」关系，杜绝填充带横切同 Z 高 obj）。
        /// </summary>
        private (SKBitmap Bmp, int TopPad)? BuildColumnBitmap(RoadRenderRoad road, RoadColumn col)
        {
            var tpl = col.Template;
            if (tpl.Tiles.Count == 0 && tpl.Objs.Count == 0) { return null; }
            var bmpW = RoadConstants.ColumnBmpW * tpl.WidthCols;
            // 顶部预留：段内最高装饰（草缘/树冠 Dy<0）不再被裁——位图 y=0=段最高可见点，
            // 表面行位于 y=topPad；blit 仍以「表面行对齐 sy」不变
            int minDy = 0;
            foreach (var t in tpl.Tiles) { minDy = Math.Min(minDy, t.Dy); }
            int topPad = -minDy;
            var bmp = new SKBitmap(bmpW, RoadConstants.ColumnBmpH + topPad, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {

                // obj 分支地板垫层（2026-09-01）：模板无 tile（纯 obj 图）时垫「天空底色采样」——
                // 与源图背景同色（蓝晶图=蓝晶、树海=树色），脚下色与背景统一（不再突兀暗棕块）
                if (tpl.Tiles.Count == 0 && tpl.Objs.Count > 0 && _skyBaseColor.HasValue)
                {
                    using var pad = new SkiaSharp.SKPaint { Color = _skyBaseColor.Value, IsAntialias = false };
                    canvas.DrawRect(0, topPad, bmpW, bmp.Height - topPad, pad);
                }
                canvas.Clear(SKColors.Transparent);
                _paint.Color = SKColors.White;

                // ── ① 解码/选块（保持旧口径：仅确定可画集与填充用块，绘制推迟到 ② 合并队列）──
                var drawTiles = new List<(RoadTile Tile, SKBitmap Bmp)>();
                SKBitmap? topTile = null; int topDx = 0, topDy = int.MaxValue; RoadTile? topTileRec = null;
                SKBitmap? lastTileBmp = null; int lastDx = 0, lastDy = 0; RoadTile? lastTileRec = null;
                SKBitmap? fillTile = null; int fillDx = 0; int fillBestDy = int.MinValue;
                bool dbgFirst = true;
                foreach (var tile in tpl.Tiles)
                {
                    var dbgPath = TilePath(tile.Ts ?? road.TileSetName, RoadMaterialExtractor.UToString(tile.U), tile.No);
                    var (tb, _, _) = GetSprite(dbgPath);
                    if (dbgFirst) { Console.Error.WriteLine($"[TileDBG] seg id={tpl.Id} W={tpl.WidthCols} first={dbgPath} ok={(tb != null)} tiles={tpl.Tiles.Count} ts={tile.Ts ?? "(null)"} setName={road.TileSetName}"); dbgFirst = false; }
                    if (tb == null) { continue; } // 解码失败/负缓存：跳过该 tile（防 Skia 异常丢整帧——"爬到一半路空"诱因）
                    drawTiles.Add((tile, tb));
                    // 顶行横向闭合：把最高块（可见顶最小）向右平铺补满到 90，消除邻柱接缝缺口
                    if (topTile == null || tile.Dy < topDy)
                    {
                        topTile = tb; topDy = tile.Dy; topDx = tile.Dx; topTileRec = tile;
                    }
                    // 末块 = 模板序最后可画者（旧 lastTile 同口径——向下填充从它底边起）
                    lastTileBmp = tb; lastDx = tile.Dx; lastDy = tile.Dy; lastTileRec = tile;
                    // 最深 bsc 块（Dy 最大=最靠下的纯填充；草缘 bsc 变体在浅处会被跳过）
                    if (string.Equals(RoadMaterialExtractor.UToString(tile.U), "bsc", StringComparison.Ordinal)
                        && tile.Dy > fillBestDy)
                    {
                        fillTile = tb; fillDx = tile.Dx; fillBestDy = tile.Dy;
                    }
                }


                // ── ② 柱内 tile+obj 合并绘制队列：按 compositeZIndex(Z, Id)（Z 主 Id 次）稳定排序 ──
                // 同柱内 obj 相对 tile 的遮挡关系按 Z：Z 低先画=后面、Z 高后画=前面（权威同层混合排序口径）。
                // tile 画法 (Dx, Dy+topPad) 与 obj 锚点画法 (Dx−ox, Dy−oy+topPad) 公式不变——只改绘制顺序。
                var ops = new List<ColumnDrawOp>(drawTiles.Count + tpl.Objs.Count + 3);
                foreach (var (tile, tb) in drawTiles)
                {
                    ops.Add(new ColumnDrawOp(tile.Z, tile.Id, ColumnDrawOp.KindTile, tb, tile.Dx, tile.Dy + topPad));
                }
                // 顶行横向补满（旧 第二步b 同坐标）：顶块平铺到柱右缘，消除邻柱接缝缺口
                if (topTile != null && topTileRec != null)
                {
                    ops.Add(new ColumnDrawOp(topTileRec.Z, -1, ColumnDrawOp.KindTopFill, topTile, topDx + topTile.Width, topDy + topPad));
                }
                // 地表厚 2 层填充（2026-09-02 甲方「脚底下必须有东西」）：表面材质向下补 2 tile 高——
                // 脚下实（地表带），以下透明（平台下方可空）
                if (fillTile != null && topTile != null && topTileRec != null)
                {
                    ops.Add(new ColumnDrawOp(topTileRec.Z, -1, ColumnDrawOp.KindFillDown, fillTile, fillDx, topDy + topTile.Height + topPad));
                }
                if (lastTileBmp != null && lastTileRec != null)
                {
                    ops.Add(new ColumnDrawOp(lastTileRec.Z, -1, ColumnDrawOp.KindBottomLoop, lastTileBmp, RoadConstants.ColW / 2f + lastDx, lastDy + lastTileBmp.Height));
                }
                                // 阶段一：obj（容器在下——参考 objContainer 先）
                foreach (var op in ops.Where(o => o.Kind == ColumnDrawOp.KindObj).OrderBy(o => o.Z).ThenBy(o => o.Id))
                {
                    canvas.DrawBitmap(op.Bmp, op.X, op.Y, _paint);
                }
                /// 参考一致排序：KindObj=0(先/在下) → 其余(1..4=tile+填充/后/在上)，组内 (Z,Id)
                foreach (var op in ops.OrderBy(o => o.Kind == ColumnDrawOp.KindObj ? 0 : 1).ThenBy(o => o.Z).ThenBy(o => o.Id))
                {
                    switch (op.Kind)
                    {
                        case ColumnDrawOp.KindTile:
                        case ColumnDrawOp.KindObj:
                            canvas.DrawBitmap(op.Bmp, op.X, op.Y, _paint);
                            break;
                        case ColumnDrawOp.KindTopFill:
                        {
                            float xx = op.X;
                            while (xx < bmpW)
                            {
                                canvas.DrawBitmap(op.Bmp, xx, op.Y, _paint);
                                xx += op.Bmp.Width;
                            }
                            break;
                        }
                        case ColumnDrawOp.KindFillDown:
                        {
                            int y = op.Y;
                            int rows = 0; // 地表厚 2 层（脚下实；以下透明——甲方「脚底下必须有东西」）
                            while (y < bmp.Height && rows < 2)
                            {
                                for (float xx = op.X; xx < bmpW; xx += op.Bmp.Width)
                                {
                                    canvas.DrawBitmap(op.Bmp, xx, y, _paint);
                                }
                                for (float xx = op.X - op.Bmp.Width; xx > -op.Bmp.Width; xx -= op.Bmp.Width)
                                {
                                    if (xx + op.Bmp.Width <= 0) { break; }
                                    canvas.DrawBitmap(op.Bmp, xx, y, _paint);
                                }
                                y += op.Bmp.Height;
                                rows++;
                            }
                            break;
                        }
                        case ColumnDrawOp.KindBottomLoop:
                        {
                            // 底块 1 层（地表厚补足——脚下实）
                            if (op.Y < bmp.Height)
                            {
                                canvas.DrawBitmap(op.Bmp, op.X, op.Y, _paint);
                            }
                            break;
                        }
                    }
                }
            }
            return (bmp, topPad);
        }


        /// <summary>obj 独立层：遍历激活列模板的全部 obj，按 (Z,Id) 排序后以世界坐标完整绘制
        /// （锚点画法 Dx−ox/Dy−oy；不裁剪——obj 恒完整，站在地板之上）。</summary>
        private void DrawObjLayer(SKCanvas canvas, RoadRenderRoad road, int w, int h, double roadAlpha)
        {
            if (road.Columns == null) { return; }
            double camX = road.ScrollOriginX + w / 2.0;
            var items = new List<(int Z, RoadObj Obj, RoadColumn Col, SKBitmap Bmp, int Ox, int Oy)>();
            foreach (var col in road.Columns)
            {
                var tpl = col.Template;
                if (tpl.Objs == null || tpl.Objs.Count == 0) { continue; }
                foreach (var obj in tpl.Objs)
                {
                    try
                    {
                        var (bmp, ox, oy) = GetSprite(ObjPath(obj.OS, obj.L0, obj.L1, obj.L2));
                        if (bmp == null) { continue; }
                        items.Add((obj.Z, obj, col, bmp, ox, oy));
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[Road] ObjLayer: {ex.Message}"); }
                }
            }
            _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * roadAlpha, 0, 255));
            foreach (var it in items.OrderBy(x => x.Z))
            {
                double worldX = it.Col.WorldX + it.Obj.Dx;
                double worldY = it.Col.SurfaceY + it.Obj.Dy; // 锚点相对表面
                float sx = (float)((worldX - camX) + w / 2.0);
                float sy = (float)(h * 0.62 + (worldY - road.InitialSurfaceY));
                canvas.DrawBitmap(it.Bmp, sx - it.Ox, sy - it.Oy, _paint);
            }
        }
        /// <summary>实体绘制：帧位图底边中心对齐 (WorldX, FeetY) 的屏幕投影；FacingLeft 水平镜像；Alpha 淡出乘整路 alpha。</summary>
        private void DrawEntities(SKCanvas canvas, RoadRenderRoad road, int w, int h, double roadAlpha)
        {
            if (road.Entities == null || EntityBitmapResolver == null) { return; }
            double camX = road.ScrollOriginX + w / 2.0;
            foreach (var e in road.Entities)
            {
                SKBitmap? bmp = null;
                try { bmp = EntityBitmapResolver(e); }
                catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadRenderer] EntityBitmap: {ex.Message}"); }
                if (bmp == null) { continue; }
                float sx = (float)((e.WorldX - camX) + w / 2.0);
                float sy = (float)(h * 0.62 + (e.FeetY - road.InitialSurfaceY));
                byte alpha = (byte)Math.Clamp(Math.Clamp(e.Alpha, 0, 1) * roadAlpha * 255, 0, 255);
                if (alpha == 0) { continue; }
                _paint.Color = SKColors.White.WithAlpha(alpha);
                if (e.FacingLeft)
                {
                    canvas.Save();
                    canvas.Translate(sx, sy);
                    canvas.Scale(-1, 1);
                    canvas.DrawBitmap(bmp, -bmp.Width / 2f, -bmp.Height, _paint);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawBitmap(bmp, sx - bmp.Width / 2f, sy - bmp.Height, _paint);
                }
            }
        }

        /// <summary>在屏幕坐标 (sx,sy) 处以 origin=(ox,oy) 为放置点绘制精灵（照 MapService.DrawSpriteScreen，zoom=1 时简化）。</summary>
        private void DrawSpriteAt(SKCanvas canvas, SKBitmap bmp, int ox, int oy, float sx, float sy, bool flipX, float zoom)
        {
            if (flipX || zoom != 1f)
            {
                canvas.Save();
                canvas.Translate(sx, sy);
                canvas.Scale(flipX ? -zoom : zoom, zoom);
                canvas.DrawBitmap(bmp, -ox, -oy, _paint);
                canvas.Restore();
                return;
            }
            canvas.DrawBitmap(bmp, sx - ox, sy - oy, _paint);
        }

        /// <summary>back 纹理路径（对齐 MapService.ParseBacks：ani=1 取 ani/{no}/0，否则 back/{no}）。</summary>
        private static string BackPath(RoadBack back)
        {
            if (back.SpineAni == 1)
            {
                return $"Map/Back/{back.BS}/ani/{back.No}/0"; // BS 契约含 .img（ParseBackUrl 原样），不得再追加
            }
            return $"Map/Back/{back.BS}/back/{back.No}";
        }

        /// <summary>tile 纹理路径（Map/Tile/{tS}.img/{u}/{no}，对齐 MapService 解析）。</summary>
        private static string TilePath(string tileSetName, string u, int no)
        {
            // tileSetName 契约含 .img（ParseTileUrl 原样保留 URL 段），此处不得再追加
            return $"Map/Tile/{tileSetName}/{u}/{no}";
        }

        /// <summary>obj 纹理路径（Map/Obj/{oS}.img/{l0}/{l1}/{l2}/0，对齐 MapService.cs:294）。</summary>
        private static string ObjPath(string os, string l0, string l1, string l2)
        {
            return $"Map/Obj/{os}.img/{l0}/{l1}/{l2}/0";
        }

        /// <summary>懒加载精灵（命中缓存直返；未命中走 WzService 提取+解码，null 负缓存防重复提取）。调用方须持 _gate。</summary>
        private (SKBitmap? Bmp, int Ox, int Oy) GetSprite(string wzPath)
        {
            if (string.IsNullOrEmpty(wzPath)) { return (null, 0, 0); }
            if (_spriteCache.TryGetValue(wzPath, out var hit))
            {
                TouchSprite(wzPath);
                return hit;
            }
            SKBitmap? bmp = null;
            int ox = 0, oy = 0;
            if (_wz != null)
            {
                try
                {
                    var (png, org) = _wz.WithWzLock(() => (_wz.ExtractPng(wzPath), _wz.GetOrigin(wzPath)));
                    if (png != null && png.Length > 0)
                    {
                        bmp = SKBitmap.Decode(png);
                        ox = org.x;
                        oy = org.y;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[InfiniteRoadRenderer] GetSprite({wzPath}): {ex.Message}");
                }
            }
            var entry = (bmp, ox, oy);
            _spriteCache[wzPath] = entry;
            _spriteLruNodes[wzPath] = _spriteLru.AddLast(wzPath);
            // 上限保护：超出时淘汰最久未用的一半（照 MapService.GetCachedSprite 策略）
            while (_spriteCache.Count > MaxSpriteCacheEntries && _spriteLru.First != null)
            {
                int evictCount = _spriteCache.Count / 2;
                for (int i = 0; i < evictCount && _spriteLru.First != null; i++)
                {
                    var oldest = _spriteLru.First.Value;
                    _spriteLru.RemoveFirst();
                    _spriteLruNodes.Remove(oldest);
                    if (_spriteCache.TryGetValue(oldest, out var ev))
                    {
                        try { ev.Bmp?.Dispose(); } catch { }
                        _spriteCache.Remove(oldest);
                    }
                }
            }
            return entry;
        }

        /// <summary>柱缓存 LRU 触碰（移到队尾 = 最近使用）。</summary>
        private void TouchColumn(string key)
        {
            if (_columnLruNodes.TryGetValue(key, out var node) && node.List != null)
            {
                _columnLru.Remove(node);
                _columnLru.AddLast(node);
            }
        }

        /// <summary>精灵缓存 LRU 触碰（移到队尾 = 最近使用）。</summary>
        private void TouchSprite(string key)
        {
            if (_spriteLruNodes.TryGetValue(key, out var node) && node.List != null)
            {
                _spriteLru.Remove(node);
                _spriteLru.AddLast(node);
            }
        }
    }
}
