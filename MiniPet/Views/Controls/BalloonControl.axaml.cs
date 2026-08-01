using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MiniPet.Models;

namespace MiniPet.Views.Controls;

/// <summary>
/// 聊天气泡控件 - 淡入 0.2s -> 显示 3~5s -> 淡出 0.3s，不阻挡拖拽
/// </summary>
public partial class BalloonControl : UserControl
{
    private CancellationTokenSource? _fadeCts;
    private CancellationTokenSource? _autoHideCts;
    private readonly Random _rng = new();

    public BalloonControl()
    {
        InitializeComponent();
        Opacity = 0;
        IsVisible = false;
        IsHitTestVisible = false;
    }

    /// <summary>
    /// 显示气泡（淡入 0.2s -> 自动 3~5s 后淡出 0.3s）
    /// </summary>
    public async Task ShowBalloon(string text, BalloonData balloonData, Bitmap? renderedBitmap = null)
    {
        try
        {
            _fadeCts?.Cancel();
            _autoHideCts?.Cancel();
            _fadeCts = new CancellationTokenSource();
            var cts = _fadeCts;

            IsVisible = true;
            IsHitTestVisible = false;

            BalloonText.Text = text;
            BalloonText.FontSize = 12;
            BalloonText.Foreground = Brushes.Black;
            BalloonText.TextAlignment = TextAlignment.Center;

            if (renderedBitmap != null)
            {
                BalloonImage.Source = renderedBitmap;
                Width = renderedBitmap.PixelSize.Width;
                Height = renderedBitmap.PixelSize.Height;
                Canvas.SetLeft(BalloonText, 20);
                Canvas.SetTop(BalloonText, 20);
                // 防止气泡高度小于 40 时 Height 计算为负（Avalonia Height 不允许负值，会抛异常）
                BalloonText.Width = Math.Max(0, Width - 40);
                BalloonText.Height = Math.Max(0, Height - 40 - (renderedBitmap.PixelSize.Height > 60 ? 30 : 0));
            }

            // 淡入 0.2s
            await FadeTo(1, TimeSpan.FromSeconds(0.2), cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            // 自动隐藏：3~5s 随机
            _autoHideCts = new CancellationTokenSource();
            var autoCts = _autoHideCts;
            var displayMs = _rng.Next(3000, 5001);
            try
            {
                await Task.Delay(displayMs, autoCts.Token);
                if (!autoCts.Token.IsCancellationRequested)
                    await HideBalloon();
            }
            catch (TaskCanceledException) { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BalloonControl] ShowBalloon: {ex.Message}");
        }
    }

    /// <summary>
    /// 隐藏气泡（淡出 0.3s）
    /// </summary>
    public async Task HideBalloon()
    {
        try
        {
            _autoHideCts?.Cancel();
            _fadeCts?.Cancel();
            _fadeCts = new CancellationTokenSource();
            var cts = _fadeCts;

            await FadeTo(0, TimeSpan.FromSeconds(0.3), cts.Token);

            if (!cts.Token.IsCancellationRequested)
            {
                IsVisible = false;
                BalloonImage.Source = null;
                BalloonText.Text = null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BalloonControl] HideBalloon: {ex.Message}");
        }
    }

    private async Task FadeTo(double targetOpacity, TimeSpan duration, CancellationToken ct)
    {
        double startOpacity = Opacity;
        double diff = targetOpacity - startOpacity;
        int steps = Math.Max(1, (int)(duration.TotalMilliseconds / 16));
        double step = diff / steps;
        int delayMs = (int)(duration.TotalMilliseconds / steps);

        for (int i = 0; i < steps; i++)
        {
            if (ct.IsCancellationRequested) return;
            Opacity = startOpacity + step * i;
            try { await Task.Delay(delayMs, ct); }
            catch (TaskCanceledException) { return; }
        }

        if (!ct.IsCancellationRequested)
            Opacity = targetOpacity;
    }
}
