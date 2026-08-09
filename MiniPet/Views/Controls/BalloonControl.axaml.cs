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
    /// 显示气泡（常驻：淡入 0.2s 后保持显示，不自动隐藏；新气泡到来时替换内容）。
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
                // PNG 已含文字（BalloonService.RenderBalloon 绘制），不再叠加 TextBlock（旧版双重渲染 → 黑字盖白字）
                BalloonImage.Source = renderedBitmap;
                BalloonText.IsVisible = false;
                Width = renderedBitmap.PixelSize.Width;
                Height = renderedBitmap.PixelSize.Height;
            }
            else
            {
                BalloonText.IsVisible = true;
                BalloonImage.Source = null;
                Width = 260;
                Height = 90;
            }

            // 淡入 0.2s，然后常驻（不自动隐藏，等待下一条替换）
            await FadeTo(1, TimeSpan.FromSeconds(0.2), cts.Token);
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
