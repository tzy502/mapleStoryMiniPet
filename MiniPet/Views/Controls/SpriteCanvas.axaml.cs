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

        // 容器大小 = 精灵图实际像素，不过度撑大
        this.Width = size.Width;
        this.Height = size.Height;
        RootCanvas.Width = size.Width;
        RootCanvas.Height = size.Height;
        SpriteImage.Width = size.Width;
        SpriteImage.Height = size.Height;
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