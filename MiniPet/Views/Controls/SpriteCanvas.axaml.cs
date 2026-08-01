using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MiniPet.Views.Controls;

/// <summary>
/// 精灵图渲染控件 - 容器大小 = 精灵图实际像素，锚点补偿由 PetWindow 通过窗口位置偏移实现
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
    /// 设置当前帧，图片放在 (0,0) 不偏移，容器大小 = 精灵图实际像素
    /// </summary>
    public void SetFrame(byte[] pngData, int originX = 0, int originY = 0, double scale = 1.0)
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

        // 缩放：容器与图片尺寸 = 实际像素 × scale（Image 默认 Stretch=Uniform，设宽高后等比填满，不变形）
        double s = scale > 0 ? scale : 1.0;
        double w = size.Width * s;
        double h = size.Height * s;
        this.Width = w;
        this.Height = h;
        RootCanvas.Width = w;
        RootCanvas.Height = h;
        SpriteImage.Width = w;
        SpriteImage.Height = h;
        SpriteImage.Source = _currentBitmap;
        // 图片放在 (0,0)，不偏移
        Canvas.SetLeft(SpriteImage, 0);
        Canvas.SetTop(SpriteImage, 0);
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