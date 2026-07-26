using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MiniPet.Views.Controls;

/// <summary>
/// 精灵图渲染控件 - 支持origin偏移定位
/// </summary>
public partial class SpriteCanvas : UserControl
{
    private Bitmap? _currentBitmap;

    public SpriteCanvas()
    {
        InitializeComponent();
        SpriteImage.Stretch = Stretch.UniformToFill;
    }

    /// <summary>
    /// 设置当前帧（origin 已在 strip 构建时按 GifUtil 逻辑内嵌，无需二次偏移）
    /// </summary>
    public void SetFrame(byte[] pngData)
    {
        if (pngData == null || pngData.Length == 0)
        {
            Clear();
            return;
        }

        _currentBitmap?.Dispose();
        using var ms = new MemoryStream(pngData);
        _currentBitmap = new Bitmap(ms);

        SpriteImage.Source = _currentBitmap;
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