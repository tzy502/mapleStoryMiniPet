using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MiniPet.Views.Controls;

/// <summary>
/// 精灵图渲染控件 - 支持 origin 偏移定位，保持锚点固定
/// </summary>
public partial class SpriteCanvas : UserControl
{
    private Bitmap? _currentBitmap;
    private int _originX;
    private int _originY;

    public SpriteCanvas()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 设置当前帧，根据 origin 偏移图片使锚点固定在画布左上
    /// </summary>
    public void SetFrame(byte[] pngData, int originX = 0, int originY = 0)
    {
        if (pngData == null || pngData.Length == 0)
        {
            Clear();
            return;
        }

        _currentBitmap?.Dispose();
        using var ms = new MemoryStream(pngData);
        _currentBitmap = new Bitmap(ms);

        var size = _currentBitmap.PixelSize;
        _originX = originX;
        _originY = originY;

        // 根据精灵图尺寸调整画布
        int canvasW = size.Width + Math.Max(0, originX);
        int canvasH = size.Height + Math.Max(0, originY);
        RootCanvas.Width = canvasW;
        RootCanvas.Height = canvasH;
        SpriteImage.Width = size.Width;
        SpriteImage.Height = size.Height;
        SpriteImage.Source = _currentBitmap;
        Canvas.SetLeft(SpriteImage, -originX);
        Canvas.SetTop(SpriteImage, -originY);

        // 通知父容器调整大小
        InvalidateMeasure();
    }

    /// <summary>
    /// 清空显示
    /// </summary>
    public void Clear()
    {
        SpriteImage.Source = null;
        _currentBitmap?.Dispose();
        _currentBitmap = null;
    }
}