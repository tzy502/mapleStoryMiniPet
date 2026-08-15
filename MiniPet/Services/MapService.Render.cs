using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services;

/// <summary>
/// MapService 渲染部分（partial 拆分）：RenderViewport（R2 摄像机/静态图共用算法）、
/// 精灵/背景绘制、back 视差。原实现从 MapService.cs 平移，行为完全一致。
/// </summary>
public partial class MapService
{
    private static int GetBackTileMode(int type) => type switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 5,
        5 => 10,
        6 => 7,
        7 => 11,
        _ => 0
    };

    // ════════════════════════════════════════════════════════════════════════
    // 摄像机渲染（RenderViewport）— 模式1 与模式2 共用同一套 R2 算法
    // 算法 1:1 对齐 WzComparerR2.MapRender（MonoGame → SkiaSharp 移植）：
    //   相机：Origin = camCenter − screen/(2*zoom)；world→screen = (world−camCenter)*zoom + screen/2
    //   back 视差基于 camCenter（非 Origin），带符号 rx，整体 position 取 floor；portal 在 front-back 之前。
    // 模式1 静态 = RenderViewport(map, GetMapCenter, zoom=1, viewTimeMs=0) 的快照（RenderMapFull 调用）。
    // ════════════════════════════════════════════════════════════════════════

    // M4：MiniPet 摄像机固定为 R2 默认 DisplayMode=0（800×600）。
    // 对齐 R2 BackPatch.cs:136-138：screenMode!=0 时仅当 screenMode == Camera.DisplayMode+1 才渲染（不 cull）。
    // 故 screenMode==1 的 back 是正常可见层（原实现 `ScreenMode != 0 跳过` 整层丢弃）；screenMode>=2 属其他显示模式，不可见。
    private const int CameraDisplayMode = 0;

    private static bool IsBackVisible(MapBack back) => back.ScreenMode == 0 || back.ScreenMode == CameraDisplayMode + 1;

    /// <summary>
    /// 地图是否含自动滚动 back（tileMode 的 ScrollH/ScrollV 位）——有则每帧需随 viewTimeMs 重渲（M10 判断依据）。
    /// 与 DrawBackViewport 的可见性判定一致（spine/a=0 不可见者不参与）。
    /// </summary>
    public static bool HasScrollingBacks(MapInfo map)
    {
        if (map == null) { return false; }
        foreach (var back in map.Backs)
        {
            if (!IsBackVisible(back) || back.Ani == 2 || back.Alpha == 0) { continue; } // 不可见层不参与（渲染同样跳过）
            int tm = GetBackTileMode(back.Type);
            if ((tm & 4) != 0 || (tm & 8) != 0) { return true; } // ScrollH / ScrollV 自动滚动
        }
        return false;
    }

    /// <summary>
    /// 渲染摄像机视口（模式2）。world→screen 变换后用缓存精灵绘制，back 视差/平铺按 GetMeshBack 公式。
    /// 渲染顺序对齐 MapScene.cs:13-19：Back(非Front) → Layer[0..7](Obj按Z升序→Tile) → Life → Portal → Front(IsFront back)。
    /// 关键：Portal 在 Front back 之前（对齐 Fly 阶段；模式1 RenderCore 把它放 front-back 之后是历史 bug，模式2 不重蹈）。
    /// back 视差用 camCenter（不是地图中心），无 (Camera.Height−600) Y 偏移（V2 已删）。
    /// M5：整帧不再持 WZ 锁——改持轻量 _spriteCacheLock（与 ClearSpriteCache/InvalidateFullRenderCache 串行，防 M1 Dispose 竞态）；
    ///     精灵懒加载（ExtractPng/GetOrigin）在 GetCachedSprite 内部单独持 WZ 锁，锁区间只覆盖 WZ 读取。
    /// M11：target 非空且尺寸匹配时复用（模式2 双缓冲由调用方持有），避免每帧 new SKBitmap ≈1.9MB。
    /// </summary>
    public SKBitmap? RenderViewport(MapInfo map, float camCenterX, float camCenterY, float zoom, long viewTimeMs, int screenW, int screenH, SKBitmap? target = null)
    {
        if (map == null || screenW <= 0 || screenH <= 0 || zoom <= 0) return null;
        lock (_spriteCacheLock)
        {
            try
            {
                // 固定 BGRA8888/Premul：与 Avalonia WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul) 一致，
                // 模式2 可 SKBitmap→WriteableBitmap 直拷像素（跳过 PNG 编码/解码，拖动跟手）。
                var result = target != null && target.Width == screenW && target.Height == screenH
                    ? target
                    : new SKBitmap(screenW, screenH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(result))
                {
                    canvas.Clear(SKColors.Transparent);
                    float halfW = screenW / 2f, halfH = screenH / 2f;

                    // 1. Back(非 IsFront) — back 视差基于 camCenter，跳过 spine(Ani==2) 与 screenMode 不可见层（M4）
                    if (MapShowBack)
                    {
                        foreach (var back in map.Backs)
                        {
                            if (back.Front || back.Ani == 2) continue;
                            DrawBackViewport(canvas, back, camCenterX, camCenterY, zoom, viewTimeMs, screenW, screenH);
                        }
                    }

                    // 2. 8 层 obj + tile：同层混合按 (Z0,Z1) 升序排序（对齐 R2 容器内排序）。
                    //    obj.Z0 = obj.Z(WZ z 字段)，tile.Z0 = 精灵帧 z（M3：R2 GetMeshTile mesh.Z0 = frame.Z），同 z 按 Id。
                    //    修正层级 bug：原"obj 全画完再 tile"导致地面(tile)盖建筑(obj)；混合排序后 z 小先画被盖，建筑在上。
                    //    排序结果按 (mapId, layer, showObj, showTile) 缓存——地图不变时顺序固定，免每帧 LINQ（P5-2）。
                    for (int i = 0; i < 8; i++)
                    {
                        var layer = map.Layers[i];
                        var sortKey = $"{map.Id}|{i}|{MapShowObj}|{MapShowTile}";
                        if (!_layerSortCache.TryGetValue(sortKey, out var items))
                        {
                            items = new List<(int Z0, int Z1, bool IsObj, MapObj? Obj, MapTile? Tile)>();
                            if (MapShowObj)
                                foreach (var o in layer.Objs) items.Add((o.Z, o.Id, true, o, null));
                            if (MapShowTile)
                                foreach (var t in layer.Tiles) items.Add((t.Z, t.Id, false, null, t));
                            items = items.OrderBy(d => d.Z0).ThenBy(d => d.Z1).ToList();
                            _layerSortCache[sortKey] = items;
                        }
                        foreach (var it in items)
                        {
                            if (it.IsObj)
                            {
                                if (it.Obj!.Resource.Frames.Count == 0) continue;
                                DrawWorldSprite(canvas, it.Obj.Resource.Frames[0].ResourceUrl, it.Obj.X, it.Obj.Y, it.Obj.FlipX, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                            }
                            else
                            {
                                DrawWorldSprite(canvas, it.Tile!.Resource.ResourceUrl, it.Tile.X, it.Tile.Y, false, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                            }
                        }
                    }

                    // 3. life — 定位 (X, Cy)（Cy 缺失回退 Y），flip，hide 跳过
                    if (MapShowLife)
                    {
                        foreach (var life in map.Lifes)
                        {
                            if (life.Hide || string.IsNullOrEmpty(life.Resource.ResourceUrl)) continue;
                            int ly = life.Cy != 0 ? life.Cy : life.Y;
                            DrawWorldSprite(canvas, life.Resource.ResourceUrl, life.X, ly, life.Flip, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                        }
                    }

                    // 4. Portal — 必须在 front-back 之前（对齐 Fly 阶段）。定位 (X,Y)，无视差无 flip。
                    if (MapShowPortal)
                    {
                        foreach (var portal in map.Portals)
                        {
                            if (string.IsNullOrEmpty(portal.Resource.ResourceUrl)) continue;
                            DrawWorldSprite(canvas, portal.Resource.ResourceUrl, portal.X, portal.Y, false, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                        }
                    }

                    // 5. Front(IsFront back) — 同 back 视差算法，最后绘制覆盖前景
                    if (MapShowBack)
                    {
                        foreach (var back in map.Backs)
                        {
                            if (!back.Front || back.Ani == 2) continue;
                            DrawBackViewport(canvas, back, camCenterX, camCenterY, zoom, viewTimeMs, screenW, screenH);
                        }
                    }

                    // 6. foothold 调试覆盖（默认关）— M11：SKPaint 提字段复用
                    if (MapShowFoothold)
                    {
                        foreach (var fh in map.Footholds)
                        {
                            float x1 = (fh.X1 - camCenterX) * zoom + halfW, y1 = (fh.Y1 - camCenterY) * zoom + halfH;
                            float x2 = (fh.X2 - camCenterX) * zoom + halfW, y2 = (fh.Y2 - camCenterY) * zoom + halfH;
                            canvas.DrawLine(x1, y1, x2, y2, _paintFoothold);
                        }
                    }
                }
                return result;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapService] RenderViewport: {ex.Message}"); return null; }
        }
    }

    /// <summary>
    /// 绘制 world 坐标精灵（obj/tile/life/portal）：world→screen 后调 DrawSpriteScreen。
    /// screen = (world − camCenter) * zoom + (screenW/2, screenH/2)。
    /// M11：SKPaint 提字段复用（_paintSprite），不再每精灵 new。
    /// </summary>
    private void DrawWorldSprite(SKCanvas canvas, string wzPath, int worldX, int worldY, bool flipX,
        float camCenterX, float camCenterY, float zoom, float halfW, float halfH, int alpha)
    {
        var (bmp, ox, oy) = GetCachedSprite(wzPath);
        if (bmp == null) return;
        float sx = (worldX - camCenterX) * zoom + halfW;
        float sy = (worldY - camCenterY) * zoom + halfH;
        ApplySpriteAlpha(_paintSprite, alpha);
        DrawSpriteScreen(canvas, bmp, ox, oy, sx, sy, flipX, zoom, _paintSprite);
    }

    /// <summary>设置精灵 paint 颜色（复用 paint 需显式复位：上一次可能设置了透明色）。</summary>
    private static void ApplySpriteAlpha(SKPaint paint, int alpha)
    {
        paint.Color = alpha >= 255 ? SKColors.White : new SKColor(255, 255, 255, (byte)alpha);
    }

    /// <summary>
    /// 绘制 back（摄像机模式）— 视差/平铺对齐 FrmMapRender2.SceneRendering.cs:798-908 GetMeshBack：
    /// position = (back.X, back.Y)；水平 ScrollH: position.X += (rx*5*viewTimeMs/1000) % cx；否则视差 position.X += floor(camCenterX*(100+rx)/100)；
    /// 垂直同理。tileMode 映射沿用 GetBackTileMode（bit0 H, bit1 V, bit2 ScrollH, bit3 ScrollV）。
    /// 平铺（tileMode!=None）：world position→screen 后，tileScreenStep = cx*zoom（缩放下），覆盖 screenW×screenH（l/r/t/b 用 floor/ceil ±1）。
    /// 关键：带符号 rx，基于 camCenter（不是 Origin/地图中心），无 (Camera.Height−600) Y 偏移。
    /// M4：screenMode 可见性按 R2（默认 DisplayMode=0 → screenMode==1 可见）；M13：a==0 跳过（R2 中为全透明）。
    /// </summary>
    private void DrawBackViewport(SKCanvas canvas, MapBack back, float camCenterX, float camCenterY, float zoom, long viewTimeMs, int screenW, int screenH)
    {
        if (!IsBackVisible(back)) { return; }   // M4：screenMode 判定（原 `ScreenMode != 0 跳过` 丢掉了正常可见的 screenMode==1 层）
        if (back.Alpha == 0) { return; }        // M13：R2 a=0 → GetRenderObject 生成全透明帧，等价不可见；跳过并省一次精灵加载
        var (bmp, ox, oy) = GetCachedSprite(back.Resource.ResourceUrl);
        if (bmp == null) return;

        int tm = GetBackTileMode(back.Type);
        int cx = back.Cx > 0 ? back.Cx : bmp.Width;
        int cy = back.Cy > 0 ? back.Cy : bmp.Height;

        // 视差偏移（world space）— 逐行对齐 R2 GetMeshBack(FrmMapRender2.SceneRendering.cs:830-859)
        float worldX = back.X;
        if ((tm & 4) != 0) { if (cx > 0) worldX += (back.Rx * 5f * viewTimeMs / 1000f) % cx; }
        else worldX += camCenterX * (100 + back.Rx) / 100f;
        float worldY = back.Y;
        if ((tm & 8) != 0) { if (cy > 0) worldY += (back.Ry * 5f * viewTimeMs / 1000f) % cy; }
        else worldY += camCenterY * (100 + back.Ry) / 100f;
        // R2: 对整体 position 取整（position.X = floor(position.X)），非对视差偏移单独取整。
        worldX = (float)Math.Floor(worldX);
        worldY = (float)Math.Floor(worldY);

        // world → screen
        float baseX = (worldX - camCenterX) * zoom + screenW / 2f;
        float baseY = (worldY - camCenterY) * zoom + screenH / 2f;

        // M11：SKPaint 提字段复用（_paintSprite），平铺循环同一 paint
        int alpha = back.Alpha > 0 ? back.Alpha : 255;
        ApplySpriteAlpha(_paintSprite, alpha);

        bool tileH = (tm & 1) != 0, tileV = (tm & 2) != 0;
        if (!tileH && !tileV)
        {
            DrawSpriteScreen(canvas, bmp, ox, oy, baseX, baseY, back.FlipX, zoom, _paintSprite);
            return;
        }
        // 平铺覆盖屏幕：tileScreenStep = cx*zoom / cy*zoom
        float stepX = tileH ? cx * zoom : 0;
        float stepY = tileV ? cy * zoom : 0;
        int l = 0, r = 1, t = 0, b = 1;
        if (tileH)
        {
            l = (int)Math.Floor((0 - baseX) / stepX) - 1;
            r = (int)Math.Ceiling((screenW - baseX) / stepX) + 1;
        }
        if (tileV)
        {
            t = (int)Math.Floor((0 - baseY) / stepY) - 1;
            b = (int)Math.Ceiling((screenH - baseY) / stepY) + 1;
        }
        for (int iy = t; iy < b; iy++)
            for (int ix = l; ix < r; ix++)
                DrawSpriteScreen(canvas, bmp, ox, oy, baseX + ix * stepX, baseY + iy * stepY, back.FlipX, zoom, _paintSprite);
    }

    /// <summary>
    /// 在屏幕坐标 (screenX, screenY) 处绘制精灵（origin=(ox,oy) 为放置点）：
    /// translate→scale(flipX?−zoom:zoom, zoom)→drawBitmap(−ox,−oy)。绕放置点镜像 + 缩放。
    /// paint 由调用方创建/释放（平铺循环复用同一 paint）。
    /// </summary>
    private static void DrawSpriteScreen(SKCanvas canvas, SKBitmap bmp, int ox, int oy, float screenX, float screenY, bool flipX, float zoom, SKPaint paint)
    {
        canvas.Save();
        canvas.Translate(screenX, screenY);
        canvas.Scale(flipX ? -zoom : zoom, zoom);
        canvas.DrawBitmap(bmp, -ox, -oy, paint);
        canvas.Restore();
    }

}
