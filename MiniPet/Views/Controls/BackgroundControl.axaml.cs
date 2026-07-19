using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System;
using System.IO;
using MiniPet.Models;

namespace MiniPet.Views.Controls;

public partial class BackgroundControl : UserControl
{
    public BackgroundControl()
    {
        InitializeComponent();
    }

    public void SetMap(MapInfo? map)
    {
        if (map == null) { BgImage.Source = null; return; }
        // Render map via MapService and set source
    }

    public void MoveViewport(double offsetX, double offsetY)
    {
        Canvas.SetLeft(BgImage, -offsetX);
        Canvas.SetTop(BgImage, -offsetY);
    }

    public void SetBitmap(System.Drawing.Bitmap? bmp)
    {
        if (bmp == null) { BgImage.Source = null; return; }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        BgImage.Source = new Bitmap(ms);
    }
}
