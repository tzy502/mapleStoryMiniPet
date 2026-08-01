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
        public event Action<string, BalloonData, byte[]>? OnBalloonReady;

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
                if (tiles == null)
                {
                    // 后台种子（重 WZ 提取不放在 UI 线程）
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
        /// 9-slice合成气泡位图，返回 PNG 编码字节数组
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

            if (nw == null || n == null || ne == null ||
                w == null || c == null || e == null ||
                sw == null || s == null || se == null)
                return null;

            // 计算尺寸：角部固定，边缘拉伸
            int cornerW = nw.Width;
            int cornerH = nw.Height;

            // 尺寸合法性保护：角块异常（宽/高 <= 0）时直接放弃本次渲染，
            // 避免 new SKBitmap(负尺寸) 返回无效句柄 → SKImage.FromBitmap native 崩溃（try-catch 抓不到 SIGSEGV）
            if (cornerW <= 0 || cornerH <= 0) return null;

            // 测量文字尺寸
            int textMaxW = Math.Max(100, cornerW * 4);
            var textSize = MeasureText(text, c.Height, textMaxW);

            int contentW = textSize.w + cornerW;
            int contentH = textSize.h + cornerH;

            int totalW = cornerW + contentW + cornerW;
            int totalH = cornerH + contentH + cornerH + (arrow?.Height ?? 0);

            // 最终尺寸合法性保护（负尺寸 SKBitmap 会 native 崩）
            if (totalW <= 0 || totalH <= 0) return null;

            // 创建目标位图
            var result = new SKBitmap(totalW, totalH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.Transparent);

            // 绘制9-slice
            // 左上角
            canvas.DrawBitmap(nw, new SKRect(0, 0, cornerW, cornerH));
            // 上边（水平拉伸）
            canvas.DrawBitmap(n, new SKRect(cornerW, 0, cornerW + contentW, cornerH));
            // 右上角
            canvas.DrawBitmap(ne, new SKRect(cornerW + contentW, 0, totalW, cornerH));

            // 左边（垂直拉伸）
            canvas.DrawBitmap(w, new SKRect(0, cornerH, cornerW, cornerH + contentH));
            // 中心（双向拉伸）
            canvas.DrawBitmap(c, new SKRect(cornerW, cornerH, cornerW + contentW, cornerH + contentH));
            // 右边（垂直拉伸）
            canvas.DrawBitmap(e, new SKRect(cornerW + contentW, cornerH, totalW, cornerH + contentH));

            // 左下角
            canvas.DrawBitmap(sw, new SKRect(0, cornerH + contentH, cornerW, totalH - (arrow?.Height ?? 0)));
            // 下边（水平拉伸）
            canvas.DrawBitmap(s, new SKRect(cornerW, cornerH + contentH, cornerW + contentW, totalH - (arrow?.Height ?? 0)));
            // 右下角
            canvas.DrawBitmap(se, new SKRect(cornerW + contentW, cornerH + contentH, totalW, totalH - (arrow?.Height ?? 0)));

            // 绘制文字在C区域
            var textRect = new SKRect(cornerW, cornerH, cornerW + contentW, cornerH + contentH);
            // clr 为有符号 ARGB（常为负数，-1 = 白）；0/未设置时默认白色不透明
            int clr = balloonData.Clr;
            uint color = clr != 0 ? (uint)clr : 0xFFFFFFFFu;
            byte a = (byte)((color >> 24) & 0xFF);
            byte r = (byte)((color >> 16) & 0xFF);
            byte g = (byte)((color >> 8) & 0xFF);
            byte b = (byte)(color & 0xFF);
            if (a == 0) a = 0xFF; // 默认不透明
            var textColor = new SKColor(r, g, b, a);

            using var paint = new SKPaint
            {
                Color = textColor,
                TextSize = 12,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            };

            // 计算文字居中位置
            float textX = textRect.MidX;
            float textY = textRect.MidY + paint.TextSize / 3f; // 基线偏移居中
            canvas.DrawText(text, textX, textY, paint);

            // 绘制箭头（S区域下方正中）
            if (arrow != null)
            {
                int arrowX = (totalW - arrow.Width) / 2;
                int arrowY = totalH - arrow.Height;
                canvas.DrawBitmap(arrow, new SKRect(arrowX, arrowY, arrowX + arrow.Width, arrowY + arrow.Height));
            }

            // 编码为 PNG 字节数组
            using var image = SKImage.FromBitmap(result);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
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
                    OnBalloonReady?.Invoke(text, data, rendered);
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