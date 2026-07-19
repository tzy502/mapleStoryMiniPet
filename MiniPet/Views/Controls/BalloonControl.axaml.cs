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
/// 聊天气泡控件 - 9-slice合成气泡显示
/// </summary>
public partial class BalloonControl : UserControl
{
    private CancellationTokenSource? _fadeCts;

    public BalloonControl()
    {
        InitializeComponent();
        Opacity = 0;
        IsVisible = false;
    }

    /// <summary>
    /// 显示气泡
    /// </summary>
    public async Task ShowBalloon(string text, BalloonData balloonData, Bitmap? renderedBitmap = null)
    {
        // 取消之前的淡入淡出
        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var cts = _fadeCts;

        IsVisible = true;

        // 设置文字
        BalloonText.Text = text;
        BalloonText.FontSize = 12;
        BalloonText.Foreground = Brushes.Black;
        BalloonText.TextAlignment = TextAlignment.Center;

        // 设置合成气泡图
        if (renderedBitmap != null)
        {
            BalloonImage.Source = renderedBitmap;

            // 调整控件尺寸匹配气泡图
            Width = renderedBitmap.PixelSize.Width;
            Height = renderedBitmap.PixelSize.Height;

            // 更新TextBlock位置到C区域（粗略居中）
            Canvas.SetLeft(BalloonText, 20);
            Canvas.SetTop(BalloonText, 20);
            BalloonText.Width = Width - 40;
            BalloonText.Height = Height - 40 - (renderedBitmap.PixelSize.Height > 60 ? 30 : 0);
        }

        // 淡入：0 → 1
        await FadeTo(1, TimeSpan.FromSeconds(0.2), cts.Token);
    }

    /// <summary>
    /// 隐藏气泡
    /// </summary>
    public async Task HideBalloon()
    {
        // 取消之前的淡入淡出
        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var cts = _fadeCts;

        // 淡出：1 → 0
        await FadeTo(0, TimeSpan.FromSeconds(0.3), cts.Token);

        if (!cts.Token.IsCancellationRequested)
        {
            IsVisible = false;
            BalloonImage.Source = null;
            BalloonText.Text = null;
        }
    }

    /// <summary>
    /// 透明度动画
    /// </summary>
    private async Task FadeTo(double targetOpacity, TimeSpan duration, CancellationToken ct)
    {
        double startOpacity = Opacity;
        double diff = targetOpacity - startOpacity;
        int steps = Math.Max(1, (int)(duration.TotalMilliseconds / 16));
        double step = diff / steps;

        for (int i = 0; i < steps; i++)
        {
            if (ct.IsCancellationRequested) return;

            Opacity = startOpacity + step * i;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        if (!ct.IsCancellationRequested)
        {
            Opacity = targetOpacity;
        }
    }
}