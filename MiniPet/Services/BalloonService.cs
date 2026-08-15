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
        private CancellationTokenSource? _hideCts;
        private readonly HashSet<string> _seeding = new();

        /// <summary>气泡渲染完成回调：(text, data, pngBytes) — 由上层在 UI 线程显示</summary>
        public event Action<string, BalloonData, byte[], int?>? OnBalloonReady;

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
        public byte[]? RenderBalloon(string text, BalloonData balloonData)
        {
            var tiles = balloonData.Tiles;
            if (tiles == null) return null;

            // 加载各切片
            using var nw = TileToBitmap(tiles.NW);
            using var n  = TileToBitmap(tiles.N);
            using var ne = TileToBitmap(tiles.NE);
            using var w  = TileToBitmap(tiles.W);
            using var c  = TileToBitmap(tiles.C);
            using var e  = TileToBitmap(tiles.E);
            using var sw = TileToBitmap(tiles.SW);
            using var s  = TileToBitmap(tiles.S);
            using var se = TileToBitmap(tiles.SE);
            using var arrow = TileToBitmap(tiles.Arrow);
            using var head = TileToBitmap(tiles.Head);

            if (nw == null || n == null || ne == null ||
                w == null || c == null || e == null ||
                sw == null || s == null || se == null)
                return null;

            if (nw.Width <= 0 || nw.Height <= 0 || c.Width <= 0 || c.Height <= 0) return null;

            int colW = c.Width;
            int colH = c.Height;

            // 文字颜色：clr 为有符号 ARGB（常为负数，-1 = 白）；0/未设置时默认白色不透明。
            // ⚠️ 2026-08-08 曾重写丢失 Color → SKPaint 默认黑色文字（用户报黑字）
            int clr = balloonData.Clr;
            uint color = clr != 0 ? (uint)clr : 0xFFFFFFFFu;
            byte ca = (byte)((color >> 24) & 0xFF);
            byte cr = (byte)((color >> 16) & 0xFF);
            byte cg = (byte)((color >> 8) & 0xFF);
            byte cb = (byte)(color & 0xFF);
            if (ca == 0) ca = 0xFF; // 默认不透明
            var textColor = new SKColor(cr, cg, cb, ca);

            // 文字断行（中文逐字符，对齐 MapleSalon2：wordWrapWidth=90, lineHeight=16, SimSun/MingLiU 系）
            using var textPaint = new SKPaint
            {
                TextSize = 12,
                Color = textColor,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("PingFang SC")
                    ?? SKTypeface.FromFamilyName("SimSun")
                    ?? SKTypeface.FromFamilyName("MingLiU")
                    ?? SKTypeface.Default
            };
            const int wrapW = 90;
            const float lineHeight = 16f;
            var lines = WrapText(text, wrapW, textPaint);

            int minW = Math.Max(80, wrapW);
            int minH = Math.Max(40, lines.Count * (int)lineHeight);
            int colCount = Math.Max(1, (int)Math.Ceiling(minW / (double)colW));
            int rowCount = Math.Max(1, (int)Math.Ceiling(minH / (double)colH));

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
            // 中心块位置 = (leftOffset2, topLeftPadding.y)，无 origin 偏移（MapleSalon2 addCenterTile 不用 pivot）
            rects.Add((c, leftOffset2, centerTop, leftOffset2 + colW * colCount, centerTop + colH * rowCount, true));

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
        public async Task Show(string text, BalloonData data, int? durationMs = null)
        {
            _hideCts?.Cancel();
            _hideCts = new CancellationTokenSource();
            var token = _hideCts.Token;
            try
            {
                byte[]? rendered = null;
                await Task.Run(() =>
                {
                    try { rendered = RenderBalloon(text, data); }
                    catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Render: {ex.Message}"); }
                }, token);

                if (rendered != null)
                    OnBalloonReady?.Invoke(text, data, rendered, durationMs);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Show: {ex.Message}"); }
        }

        /// <summary>
        /// 隐藏气泡
        /// </summary>
        public void Hide()
        {
            _hideCts?.Cancel();
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
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
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
    }
}