using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services
{
    /// <summary>
    /// 聊天气泡服务 - 9-slice合成、缓存加载、显示控制
    /// </summary>
    public class BalloonService
    {
        private readonly CacheManager _cache;
        private readonly WzService _wz;
        private int _displayDurationMs = 5000;
        // V0.1.3 多桌宠：按目标宠物分槽（key = petId，空串 = 广播槽）。旧实现单槽会在 B 弹气泡时
        // 取消 A 正在渲染/显示的气泡（跨宠物互相打断），多只桌宠同屏时表现为「谁说话谁把别人掐掉」。
        private readonly Dictionary<string, CancellationTokenSource> _hideCtsMap = new(StringComparer.Ordinal);
        private readonly object _hideCtsLock = new();
        private readonly HashSet<string> _seeding = new();

        // 补-13/S12：按 balloonId 缓存的解码后瓦片（预览与显示反复 RenderBalloon，避免每次重解 11 张 PNG）。
        // SKBitmap 解码后只读，可被多个 SKCanvas.DrawBitmap 并发读取；WZ 重载边缘场景（同 id 瓦片内容变更）
        // 会复用旧解码——实际重载极低频且磁盘缓存不自动重提，可接受。
        private readonly object _tileCacheLock = new();
        private readonly Dictionary<string, DecodedTiles> _tileCache = new(StringComparer.Ordinal);
        private readonly Queue<string> _tileOrder = new();
        private const int MaxTileCacheEntries = 24;

        /// <summary>
        /// 气泡渲染完成回调：(text, data, pngBytes, durationMs, targetPetId) — 由上层在 UI 线程显示。
        /// targetPetId 非空时只有该宠物显示（多桌宠定向）；null = 广播给所有桌宠。
        /// </summary>
        public event Action<string, BalloonData, byte[], int?, string?>? OnBalloonReady;

        public BalloonService(CacheManager cache, WzService wz)
        {
            _cache = cache;
            _wz = wz;
        }

        /// <summary>
        /// 从缓存加载气泡数据。缓存未命中时在后台线程种子（提取 9-slice 切片），
        /// 本次返回 null（不阻塞 UI 线程做 WZ 读取），下一轮命中缓存。
        /// </summary>
        public BalloonData? LoadBalloon(string balloonId)
        {
            try
            {
                if (string.IsNullOrEmpty(balloonId)) return null;
                var tiles = _cache.LoadBalloonTiles(balloonId);
                if (tiles == null || IsPlaceholderTiles(tiles))
                {
                    // 缓存缺失或为 1×1 占位（无 WZ 时代提取失败的残留）→ 后台重种子
                    SeedInBackground(balloonId);
                    return null;
                }
                return new BalloonData
                {
                    Id = balloonId,
                    Tiles = tiles,
                    Clr = _wz.GetBalloonClr(balloonId)
                };
            }
            catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] LoadBalloon: {ex.Message}"); return null; }
        }

        private void SeedInBackground(string balloonId)
        {
            try
            {
                lock (_seeding)
                {
                    if (_seeding.Contains(balloonId)) return;
                    _seeding.Add(balloonId);
                }
                if (!_wz.IsLoaded) { lock (_seeding) _seeding.Remove(balloonId); return; }
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var tiles = _wz.ExtractBalloonTiles(balloonId);
                        Console.WriteLine($"[BalloonService] Seed({balloonId}): extract={(tiles != null ? "ok" : "null")}, nw={(tiles?.NW?.Length ?? 0)}B");
                        if (tiles != null)
                            _cache.SaveBalloonTiles(balloonId, tiles);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Seed: {ex.Message}"); }
                    finally { lock (_seeding) _seeding.Remove(balloonId); }
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] SeedInBackground: {ex.Message}"); }
        }

        /// <summary>
        /// 9-slice 合成气泡位图，返回 PNG 编码字节数组。
        /// 拼装参考 MapleSalon2 renderer/chatBalloon/chatBalloonBackground.ts：
        /// origin-aware 定位（每片 origin 做 pivot 偏移）、角块原尺寸平铺、中心区拉伸、
        /// head 画在顶行中间、arrow 画在底行开头（s 宽>arrow 宽时回退对齐）。
        /// </summary>
        public byte[]? RenderBalloon(string text, BalloonData balloonData, bool bold = false)
        {
            var tiles = balloonData.Tiles;
            if (tiles == null) return null;

            // 补-13/S12：解码瓦片按 balloonId 缓存复用（SKBitmap 解码后只读，多线程 DrawBitmap 安全）
            var decoded = GetDecodedTiles(balloonData);
            if (decoded == null) return null;

            var nw = decoded.NW;
            var n  = decoded.N;
            var ne = decoded.NE;
            var w  = decoded.W;
            var c  = decoded.C;
            var e  = decoded.E;
            var sw = decoded.SW;
            var s  = decoded.S;
            var se = decoded.SE;
            var arrow = decoded.Arrow;
            var head = decoded.Head;

            // ⚠️ 不同气泡样式的瓦片组合天生不同（WZ 实测：id=1 无 NW/NE/SW/SE，id=520 **无中央 C**）——
            // 原实现要求九宫格齐全，导致这些样式**永远渲染失败**（设置中心预览空白，胶水 2026-09-24 报）。
            // 现在：缺失瓦片用 1×1 透明占位（画了也看不见），布局尺寸用「有数据的同类瓦片」兜底。
            static SKBitmap Blank1() => new SKBitmap(1, 1);
            nw ??= Blank1();
            n ??= Blank1();
            ne ??= Blank1();
            w ??= Blank1();
            e ??= Blank1();
            sw ??= Blank1();
            s ??= Blank1();
            se ??= Blank1();
            c ??= Blank1();
            if (w.Width <= 0 || w.Height <= 0) { w = Blank1(); }
            if (e.Width <= 0 || e.Height <= 0) { e = Blank1(); }
            if (n.Width <= 0 || n.Height <= 0) { n = Blank1(); }
            if (s.Width <= 0 || s.Height <= 0) { s = Blank1(); }
            if (nw.Width <= 0 || nw.Height <= 0) { nw = Blank1(); }
            if (ne.Width <= 0 || ne.Height <= 0) { ne = Blank1(); }
            if (sw.Width <= 0 || sw.Height <= 0) { sw = Blank1(); }
            if (se.Width <= 0 || se.Height <= 0) { se = Blank1(); }

            // 中央瓦片缺失/退化时：列宽高用左右/上下边兜底（保证还能排版出文字）
            int colW = c.Width > 1 ? c.Width : Math.Max(12, w.Width > 1 ? w.Width : n.Height);
            int colH = c.Height > 1 ? c.Height : Math.Max(12, n.Height > 1 ? n.Height : 16);
            if (c.Width <= 1 || c.Height <= 1)
            {
                c = new SKBitmap(Math.Max(1, colW), Math.Max(1, colH));   // 透明中央
            }

            // 文字颜色：WZ 的 clr 是「从白色减去的 RGB 补值」（MapleSalon2 getWzClrColor：
            // color = 0x1000000 + clr，clr 常为负数如 -1 = 白、-0x10000 = 红）；clr=0 同 MapleSalon2 视为白。
            // ⚠️ 2026-08-08 曾重写丢失 Color → SKPaint 默认黑色文字（用户报黑字）
            int clr = balloonData.Clr;
            // ⚠️ clr = -1 / 0 经 `0x1000000 + clr` 解析出来是**纯白**，而气泡底多是浅色
            //（实测 id=233 粉黄底、id=520 浅青底）→ 白字几乎不可见（胶水 2026-09-24 报预览失败，
            //  实际是「渲染成功但文字看不见」）。这两个值属默认占位，按游戏观感用深色；
            // 只有显式指定了颜色的（如 -0x10000 = 红）才用 WZ 色。
            SKColor textColor;
            if (clr == 0 || clr == -1)
            {
                textColor = new SKColor(0x22, 0x22, 0x22, 0xFF);
            }
            else
            {
                uint rgb = (uint)((0x1000000 + clr) & 0xFFFFFF);
                textColor = new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0xFF);
            }

            // 文字断行（中文逐字符，对齐 MapleSalon2：wordWrapWidth=90, lineHeight=16, SimSun/MingLiU 系）
            // 加粗仅「思考中…」（Review 状态气泡）使用（胶水 2026-08-15：只针对思考加粗）
            var fontWeight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            using var textPaint = new SKPaint
            {
                TextSize = 12,
                Color = textColor,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = CreateBalloonTypeface(fontWeight)
            };
            const int wrapW = 90;
            const float lineHeight = 16f;
            var lines = WrapText(text, wrapW, textPaint);

            int minW = Math.Max(80, wrapW);
            int minH = Math.Max(40, lines.Count * (int)lineHeight);
            // 无中央瓦片的样式（如 520）：平铺会产生重复的边块 → 强制 1×1
            bool tiledCenter = tiles.C.Length > 1;
            if (!tiledCenter)
            {
                // 中央块按「文字所需尺寸」生成**白底**块：否则气泡太窄会把文字截断，
                // 且中央透明会让文字悬空（520 实测）。白底 + 深色字 = 可读。
                colW = Math.Max(colW, Math.Max(80, wrapW));
                colH = Math.Max(colH, Math.Max(20, lines.Count * (int)lineHeight + 10));
                // 无中央瓦片的样式（WZ 实测：520 只有 nw/n/ne/w/e/sw/s/se，没有 c）：
                // 9-slice 的标准兜底 = 用**上边瓦片的中间像素色**铺出中央底色（纯色，不会有重复装饰图案）。
                // 之前填白块会把边框内侧装饰盖成矩形（胶水对比图）；纯透明则完全不像气泡（胶水 2026-09-24）。
                SKColor centerColor = new SKColor(0xF2, 0xF2, 0xF5, 0xFF);
                try
                {
                    if (n.Width > 0 && n.Height > 0)
                    {
                        centerColor = n.GetPixel(n.Width / 2, n.Height / 2);
                    }
                }
                catch { }
                var centerBmp = new SKBitmap(Math.Max(1, colW), Math.Max(1, colH));
                using (var ccv = new SKCanvas(centerBmp))
                {
                    ccv.Clear(centerColor);
                }
                c = centerBmp;
            }
            int colCount = tiledCenter ? Math.Max(1, (int)Math.Ceiling(minW / (double)colW)) : 1;
            int rowCount = tiledCenter ? Math.Max(1, (int)Math.Ceiling(minH / (double)colH)) : 1;

            // origin-aware 拼装（MapleSalon2）：topOffset = max(nw.oy, n.oy)；leftOffset = nw.ox
            int topOffset = Math.Max(tiles.NWOrigin.Y, tiles.NOrigin.Y);
            int xOffsetByArrow = (arrow != null && s.Width > arrow.Width) ? s.Width - arrow.Width : 0;
            // MapleSalon2 addSpriteWithPos：origin.x == -1 是「未定义」哨兵值 → pivot.x = 0（仅当无 arrow 偏移）
            float PivotX(int ox) => (ox == -1 && xOffsetByArrow == 0) ? 0 : ox;

            // 收集所有块的左上角与宽高（position - origin = 图像左上角）
            var rects = new List<(SKBitmap Bmp, float L, float T, float R, float B, bool Stretch)>();
            float x, y;

            // top 行：nw → n×colCount（i==half 处画 head）→ ne
            rects.Add((nw, -(float)PivotX(tiles.NWOrigin.X), -(float)tiles.NWOrigin.Y, -tiles.NWOrigin.X + nw.Width, -tiles.NWOrigin.Y + nw.Height, false));
            x = nw.Width - PivotX(tiles.NWOrigin.X);
            int half = colCount / 2;
            for (int i = 0; i < colCount; i++)
            {
                if (i == half && head != null && head.Width > 1 && head.Height > 1)
                    rects.Add((head, x - PivotX(tiles.HeadOrigin.X), -tiles.HeadOrigin.Y, x - PivotX(tiles.HeadOrigin.X) + head.Width, -tiles.HeadOrigin.Y + head.Height, false));
                else
                    rects.Add((n, x - PivotX(tiles.NOrigin.X), -tiles.NOrigin.Y, x - PivotX(tiles.NOrigin.X) + n.Width, -tiles.NOrigin.Y + n.Height, false));
                x += n.Width;
            }
            x -= xOffsetByArrow;
            rects.Add((ne, x - PivotX(tiles.NEOrigin.X), -tiles.NEOrigin.Y, x - PivotX(tiles.NEOrigin.X) + ne.Width, -tiles.NEOrigin.Y + ne.Height, false));

            // middle：w×rowCount + 中心拉伸 + e×rowCount
            y = nw.Height - topOffset;
            float leftOffset2 = w.Width - PivotX(tiles.WOrigin.X);
            float rightX = leftOffset2 + colW * colCount - xOffsetByArrow;
            float centerTop = y;
            for (int i = 0; i < rowCount; i++)
            {
                rects.Add((w, -(float)PivotX(tiles.WOrigin.X), y - tiles.WOrigin.Y, -tiles.WOrigin.X + w.Width, y - tiles.WOrigin.Y + w.Height, false));
                rects.Add((e, rightX - PivotX(tiles.EOrigin.X), y - tiles.EOrigin.Y, rightX - PivotX(tiles.EOrigin.X) + e.Width, y - tiles.EOrigin.Y + e.Height, false));
                y += colH;
            }
            // 中心块 = (leftOffset2, topLeftPadding.y)，无 origin 偏移（MapleSalon2 addCenterTile 不用 pivot）。
            // ⚠️ 参考实现用的是 **TilingSprite 平铺**（chatBalloonBackground.ts addCenterTile:
            //    `TilingSprite.from(assets[4])` + `setSize(w,h)`）——即按 colWidth×colHeight 网格**重复** c 瓦片，
            // 不是把一块 c 拉伸铺满（拉伸会把图案拉长，与游戏观感不同，胶水 2026-09-24 指出）。
            for (int ty = 0; ty < rowCount; ty++)
            {
                for (int tx = 0; tx < colCount; tx++)
                {
                    float cx0 = leftOffset2 + tx * colW;
                    float cy0 = centerTop + ty * colH;
                    rects.Add((c, cx0, cy0, cx0 + colW, cy0 + colH, false));
                }
            }

            // bottom：sw → s×colCount（i==0 画 arrow）→ se
            rects.Add((sw, -(float)PivotX(tiles.SWOrigin.X), y - tiles.SWOrigin.Y, -tiles.SWOrigin.X + sw.Width, y - tiles.SWOrigin.Y + sw.Height, false));
            x = sw.Width - PivotX(tiles.SWOrigin.X);
            for (int i = 0; i < colCount; i++)
            {
                if (i == 0 && arrow != null && arrow.Width > 1 && arrow.Height > 1)
                {
                    // arrow 画在底行开头（对齐 MapleSalon2：s 宽 > arrow 宽时回退对齐）
                    rects.Add((arrow, x - PivotX(tiles.ArrowOrigin.X), y - tiles.ArrowOrigin.Y, x - PivotX(tiles.ArrowOrigin.X) + arrow.Width, y - tiles.ArrowOrigin.Y + arrow.Height, false));
                    if (s.Width > arrow.Width) x -= (s.Width - arrow.Width);
                }
                else
                {
                    rects.Add((s, x - PivotX(tiles.SOrigin.X), y - tiles.SOrigin.Y, x - PivotX(tiles.SOrigin.X) + s.Width, y - tiles.SOrigin.Y + s.Height, false));
                }
                // 每轮都前进 colWidth（MapleSalon2：x += this.colWidth 在循环尾），否则 arrow 与下一块 s 重叠
                x += colW;
            }
            rects.Add((se, x - PivotX(tiles.SEOrigin.X), y - tiles.SEOrigin.Y, x - PivotX(tiles.SEOrigin.X) + se.Width, y - tiles.SEOrigin.Y + se.Height, false));

            // 求边界并平移（origin 会产生负坐标）
            float minL = float.MaxValue, minT = float.MaxValue, maxR = float.MinValue, maxB = float.MinValue;
            foreach (var (_, L, T, R, B, _) in rects)
            {
                minL = Math.Min(minL, L); minT = Math.Min(minT, T);
                maxR = Math.Max(maxR, R); maxB = Math.Max(maxB, B);
            }
            if (rects.Count == 0 || minL >= maxR || minT >= maxB) return null;
            int totalW = (int)Math.Ceiling(maxR - minL);
            int totalH = (int)Math.Ceiling(maxB - minT);
            if (totalW <= 0 || totalH <= 0) return null;

            var result = new SKBitmap(totalW, totalH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-minL, -minT);

            foreach (var (bmp, L, T, R, B, stretch) in rects)
            {
                if (bmp == null) continue;
                if (stretch)
                    canvas.DrawBitmap(bmp, new SKRect(L, T, R, B));
                else
                    canvas.DrawBitmap(bmp, new SKRect(L, T, L + bmp.Width, T + bmp.Height));
            }

            // 文字：文字块起点 = topLeftPadding（MapleSalon2：textNode.position.copyFrom(topLeftPadding)），
            // 每行在 wordWrapWidth 内水平居中（align:center），行高 lineHeight，首行基线 = 起点 + ascent
            float textBlockX = (nw.Width - tiles.NWOrigin.X);
            float textBlockY = (nw.Height - topOffset);
            float textCenterX = textBlockX + wrapW / 2f;
            var fm = textPaint.FontMetrics;
            float firstBaseline = textBlockY - fm.Ascent; // Ascent 为负，减负=加
            for (int i = 0; i < lines.Count; i++)
                canvas.DrawText(lines[i], textCenterX, firstBaseline + i * lineHeight, textPaint);

            // 编码为 PNG 字节数组
            using var image = SKImage.FromBitmap(result);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>
        /// 中文友好的逐字符断行（MapleSalon2 wordWrap=true, breakWords=true；宽超 wrapW 处断行）
        /// </summary>
        private static List<string> WrapText(string text, int wrapW, SKPaint paint)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(string.Empty); return lines; }
            var cur = new System.Text.StringBuilder();
            foreach (char ch in text)
            {
                if (cur.Length > 0 && paint.MeasureText(cur.ToString() + ch) > wrapW)
                {
                    lines.Add(cur.ToString());
                    cur.Clear();
                }
                cur.Append(ch);
            }
            if (cur.Length > 0) lines.Add(cur.ToString());
            return lines;
        }

        /// <summary>
        /// 计算箭头指向精灵的锚点位置
        /// </summary>
        public (int x, int y) CalculateArrowPosition(int balloonW, int balloonH, int originX, int originY)
        {
            // 箭头底部指向精灵的 origin 点，气泡在精灵上方居中
            return (
                originX - balloonW / 2,
                originY - balloonH
            );
        }

        /// <summary>
        /// 检测缓存切片是否为 1×1 占位（无 WZ 时代提取失败的残留，70 字节 PNG）。
        /// 只解析 PNG IHDR（偏移 16-23 的宽高），不整图解码。
        /// </summary>
        private static bool IsPlaceholderTiles(BalloonTiles tiles)
        {
            try
            {
                var probe = new[] { tiles.NW, tiles.N, tiles.C, tiles.Arrow };
                foreach (var data in probe)
                {
                    if (data == null || data.Length < 24) return true;
                    // PNG 魔数校验：89 50 4E 47 0D 0A 1A 0A
                    if (data[0] != 0x89 || data[1] != 0x50) return true;
                    int w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                    int h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                    if (w <= 1 || h <= 1) return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 后台线程合成气泡 PNG，完成后通过 OnBalloonReady 回调（由上层在 UI 线程显示）。
        /// 自动淡出计时由 BalloonControl 内部负责。
        /// </summary>
        public async Task Show(string text, BalloonData data, int? durationMs = null, bool bold = false, string? targetPetId = null)
        {
            var key = targetPetId ?? string.Empty;
            CancellationTokenSource cts;
            lock (_hideCtsLock)
            {
                if (_hideCtsMap.TryGetValue(key, out var prev))
                {
                    try { prev.Cancel(); } catch { }
                    try { prev.Dispose(); } catch { }
                }
                cts = new CancellationTokenSource();
                _hideCtsMap[key] = cts;
            }
            var token = cts.Token;
            try
            {
                byte[]? rendered = null;
                await Task.Run(() =>
                {
                    try { rendered = RenderBalloon(text, data, bold); }
                    catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Render: {ex.Message}"); }
                }, token);

                if (rendered != null)
                    OnBalloonReady?.Invoke(text, data, rendered, durationMs, targetPetId);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Show: {ex.Message}"); }
        }

        /// <summary>
        /// 隐藏气泡（targetPetId 为空 = 隐藏广播槽；多桌宠时各宠物各隐藏自己的）。
        /// </summary>
        public void Hide(string? targetPetId = null)
        {
            var key = targetPetId ?? string.Empty;
            lock (_hideCtsLock)
            {
                if (_hideCtsMap.TryGetValue(key, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                }
            }
        }

        /// <summary>
        /// 设置默认显示时长
        /// </summary>
        public void SetDisplayDuration(int ms)
        {
            _displayDurationMs = ms;
        }

        /// <summary>
        /// 测量文字所需尺寸（限制最大宽度，自动换行）
        /// </summary>
        private static (int w, int h) MeasureText(string text, int lineHeight, int maxWidth)
        {
            using var paint = new SKPaint
            {
                TextSize = 12,
                // 与渲染同字体链（原 Arial 无中文字形 → 中文气泡宽度测量不准，窗口尺寸偏小）
                Typeface = CreateBalloonTypeface(SKFontStyleWeight.Bold)
            };

            // 近似换行：按 maxWidth 截断，计算行数
            float lineW = paint.MeasureText(text);
            int lines = Math.Max(1, (int)Math.Ceiling(lineW / maxWidth));

            int w = (int)Math.Ceiling(Math.Min(lineW, maxWidth));
            int h = (int)Math.Ceiling(paint.TextSize * 1.35f * lines);

            // 确保最小尺寸
            w = Math.Max(w, 50);
            h = Math.Max(h, lineHeight);

            return (w, h);
        }

        /// <summary>
        /// 聊天气泡字体（按平台分流）：
        /// Windows → 宋体系优先（SimSun/MingLiU，对齐 MapleSalon2 的冒险岛宋体风格）；
        /// macOS → 苹方 PingFang SC 优先（系统黑体，宋体风格在 mac 上非原版观感，保持苹方）。
        /// </summary>
        private static SKTypeface CreateBalloonTypeface(SKFontStyleWeight weight = SKFontStyleWeight.Normal)
        {
            bool isWindows = OperatingSystem.IsWindows();
            return isWindows
                ? SKTypeface.FromFamilyName("SimSun", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.FromFamilyName("MingLiU", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.FromFamilyName("PingFang SC", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.Default
                : SKTypeface.FromFamilyName("PingFang SC", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.FromFamilyName("SimSun", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.FromFamilyName("MingLiU", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.Default;
        }

        /// <summary>
        /// 字节数组转 SKBitmap
        /// </summary>
        private static SKBitmap? TileToBitmap(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                return SKBitmap.Decode(data);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>取（或解码并缓存）balloonId 的解码瓦片；无稳定 Id（异常数据）时不缓存直接解码。</summary>
        private DecodedTiles? GetDecodedTiles(BalloonData data)
        {
            if (data == null || data.Tiles == null)
            {
                return null;
            }
            if (string.IsNullOrEmpty(data.Id))
            {
                // 无稳定 Id：不缓存，直接解码
                return DecodeTiles(data.Tiles);
            }
            lock (_tileCacheLock)
            {
                if (_tileCache.TryGetValue(data.Id, out var cached))
                {
                    return cached;
                }
                var decoded = DecodeTiles(data.Tiles);
                if (decoded == null)
                {
                    return null;
                }
                _tileCache[data.Id] = decoded;
                _tileOrder.Enqueue(data.Id);
                // FIFO 淘汰（气泡瓦片小，24 条 ≈ 1MB 级，封顶足够）。
                // ⚠️ 只移除不 Dispose：渲染（RenderBalloon）在后台 Task 持锁外引用瓦片合成，
                // 此处显式 Dispose 会与在途 DrawBitmap 竞态（SKBitmap 原生句柄已释放仍被使用
                // → SkiaSharp abort，实测崩溃）；原生内存交 GC 终结器回收（与 PaperdollService
                // 位图缓存上限同结论）。
                while (_tileCache.Count > MaxTileCacheEntries)
                {
                    var oldest = _tileOrder.Dequeue();
                    _tileCache.Remove(oldest);
                }
                return decoded;
            }
        }

        /// <summary>把瓦片字节解码为 SKBitmap 集合（空/损坏瓦片为 null，与 TileToBitmap 语义一致）。</summary>
        private static DecodedTiles DecodeTiles(BalloonTiles tiles)
        {
            return new DecodedTiles
            {
                NW = TileToBitmap(tiles.NW),
                N = TileToBitmap(tiles.N),
                NE = TileToBitmap(tiles.NE),
                W = TileToBitmap(tiles.W),
                C = TileToBitmap(tiles.C),
                E = TileToBitmap(tiles.E),
                SW = TileToBitmap(tiles.SW),
                S = TileToBitmap(tiles.S),
                SE = TileToBitmap(tiles.SE),
                Arrow = TileToBitmap(tiles.Arrow),
                Head = TileToBitmap(tiles.Head)
            };
        }

        /// <summary>解码后的气泡 9-slice 瓦片（RenderBalloon 按 balloonId 缓存复用，避免每次重解 PNG）。</summary>
        private sealed class DecodedTiles : IDisposable
        {
            public SKBitmap? NW, N, NE, W, C, E, SW, S, SE, Arrow, Head;

            public void Dispose()
            {
                NW?.Dispose();
                N?.Dispose();
                NE?.Dispose();
                W?.Dispose();
                C?.Dispose();
                E?.Dispose();
                SW?.Dispose();
                S?.Dispose();
                SE?.Dispose();
                Arrow?.Dispose();
                Head?.Dispose();
            }
        }
    }
}