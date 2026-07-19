using System;
using System.IO;
using Avalonia.Controls;
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
    }

    /// <summary>
    /// 设置当前帧并定位（origin偏移）
    /// </summary>
    public void SetFrame(System.Drawing.Bitmap frame, int originX, int originY)
    {
        if (frame == null)
        {
            Clear();
            return;
        }

        // System.Drawing.Bitmap -> Avalonia.Bitmap
        _currentBitmap = ConvertToAvaloniaBitmap(frame);

        SpriteImage.Source = _currentBitmap;

        // origin偏移：图片左上角相对于实体锚点的偏移
        // Canvas.SetLeft/SetTop 实现负偏移定位
        Canvas.SetLeft(SpriteImage, -originX);
        Canvas.SetTop(SpriteImage, -originY);
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

    private static Bitmap? ConvertToAvaloniaBitmap(System.Drawing.Bitmap bitmap)
    {
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}