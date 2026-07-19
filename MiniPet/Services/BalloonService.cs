using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// 聊天气泡服务 - 9-slice合成、缓存加载、显示控制
    /// </summary>
    public class BalloonService
    {
        private readonly CacheManager _cache;
        private int _displayDurationMs = 5000;
        private CancellationTokenSource? _hideCts;

        public BalloonService(CacheManager cache)
        {
            _cache = cache;
        }

        /// <summary>
        /// 从缓存加载气泡数据
        /// </summary>
        public BalloonData? LoadBalloon(int balloonId)
        {
            var tiles = _cache.LoadBalloonTiles(balloonId);
            if (tiles == null) return null;

            return new BalloonData
            {
                Id = balloonId,
                Tiles = tiles
            };
        }

        /// <summary>
        /// 9-slice合成气泡位图
        /// </summary>
        public Bitmap? RenderBalloon(string text, BalloonData balloonData)
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

            // 测量文字尺寸
            int textMaxW = Math.Max(100, cornerW * 4);
            var textSize = MeasureText(text, c.Height, textMaxW);

            int contentW = textSize.Width + cornerW;
            int contentH = textSize.Height + cornerH;

            int totalW = cornerW + contentW + cornerW;
            int totalH = cornerH + contentH + cornerH + (arrow?.Height ?? 0);

            var result = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(result);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // 绘制9-slice
            // 左上角
            g.DrawImage(nw, 0, 0, cornerW, cornerH);
            // 上边（水平拉伸）
            g.DrawImage(n, cornerW, 0, contentW, cornerH);
            // 右上角
            g.DrawImage(ne, cornerW + contentW, 0, cornerW, cornerH);

            // 左边（垂直拉伸）
            g.DrawImage(w, 0, cornerH, cornerW, contentH);
            // 中心（双向拉伸）
            g.DrawImage(c, cornerW, cornerH, contentW, contentH);
            // 右边（垂直拉伸）
            g.DrawImage(e, cornerW + contentW, cornerH, cornerW, contentH);

            // 左下角
            g.DrawImage(sw, 0, cornerH + contentH, cornerW, cornerH);
            // 下边（水平拉伸）
            g.DrawImage(s, cornerW, cornerH + contentH, contentW, cornerH);
            // 右下角
            g.DrawImage(se, cornerW + contentW, cornerH + contentH, cornerW, cornerH);

            // 绘制文字在C区域
            var textRect = new Rectangle(cornerW, cornerH, contentW, contentH);
            using var brush = new SolidBrush(Color.FromArgb(balloonData.Clr > 0 ? balloonData.Clr : 0xFFFFFF));
            using var font = new Font("Arial", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(text, font, brush, textRect, sf);

            // 绘制箭头（S区域下方正中）
            if (arrow != null)
            {
                int arrowX = (totalW - arrow.Width) / 2;
                int arrowY = cornerH + contentH + cornerH;
                g.DrawImage(arrow, arrowX, arrowY, arrow.Width, arrow.Height);
            }

            return result;
        }

        /// <summary>
        /// 计算箭头指向精灵的锚点位置
        /// </summary>
        public Point CalculateArrowPosition(Size balloonSize, Point originPoint)
        {
            // 箭头底部指向精灵的 origin 点，箭头在气泡底部居中
            return new Point(
                originPoint.X - balloonSize.Width / 2,
                originPoint.Y - balloonSize.Height
            );
        }

        /// <summary>
        /// 显示气泡并自动淡出
        /// </summary>
        public async Task Show(string text, BalloonData data, int? durationMs = null)
        {
            // 取消之前的隐藏定时器
            _hideCts?.Cancel();
            _hideCts = new CancellationTokenSource();

            var ms = durationMs ?? _displayDurationMs;

            try
            {
                await Task.Delay(ms, _hideCts.Token);
                Hide();
            }
            catch (TaskCanceledException)
            {
                // 被新的 Show 取消，忽略
            }
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
        private static Size MeasureText(string text, int lineHeight, int maxWidth)
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            using var font = new Font("Arial", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            var sf = new StringFormat { FormatFlags = StringFormatFlags.LineLimit };

            // 二分查找合适的宽度
            var measured = g.MeasureString(text, font, maxWidth, sf);
            int w = (int)Math.Ceiling(measured.Width);
            int h = (int)Math.Ceiling(measured.Height);

            // 确保最小尺寸
            w = Math.Max(w, 50);
            h = Math.Max(h, lineHeight);

            return new Size(w, h);
        }

        /// <summary>
        /// 字节数组转Bitmap
        /// </summary>
        private static Bitmap? TileToBitmap(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(data);
                return new Bitmap(ms);
            }
            catch
            {
                return null;
            }
        }
    }
}