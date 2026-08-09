using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace MiniPet.Views.Controls;

/// <summary>
/// 精灵图渲染控件 - 容器大小 = 精灵图实际像素，锚点补偿由 PetWindow 通过窗口位置偏移实现
/// </summary>
public partial class SpriteCanvas : UserControl
{
    private Bitmap? _currentBitmap;
    private WriteableBitmap? _wb;
    private int _wbW = -1;
    private int _wbH = -1;
    private int _originX;
    private int _originY;
    private int _lastPixelW;
    private int _lastPixelH;
    private double _lastScale = -1;
    private double _anchorH;   // 气泡锚点高度下限（缩放后像素）：气泡位置基准，动画切换不缩到基准以下

    public SpriteCanvas()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 强制下一帧 SetFrame 重设容器尺寸（切换桌宠时调用：9834755→9300688 曾因新旧帧像素尺寸相同
    /// 跳过尺寸重设导致容器不足显示截断，胶水反馈 2026-08-09）。
    /// </summary>
    public void ForceSizeRefresh()
    {
        _lastPixelW = -1;
        _lastPixelH = -1;
    }

    /// <summary>
    /// 设置容器高度下限（气泡锚点基准）：容器高 = max(帧高, 锚点高)，
    /// 保证 Grid 布局中气泡行位置不随动画帧高度变化（胶水规则：气泡位置按首个动作固定）。
    /// </summary>
    public void SetAnchorHeight(double h)
    {
        if (Math.Abs(h - _anchorH) < 0.5) return;
        _anchorH = Math.Max(0, h);
        // 立即按新锚点刷新容器高度（不等下一帧 SetFrame）
        if (_lastPixelH > 0 && _lastScale > 0)
        {
            double h2 = Math.Max(_lastPixelH * _lastScale, _anchorH);
            this.Height = h2;
            RootCanvas.Height = h2;
        }
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

        double s = scale > 0 ? scale : 1.0;
        // 尺寸不变时不重设 Width/Height，避免每帧触发 Avalonia 布局传递导致亚像素抖动；
        // ±2px 容忍：超长帧（chibi 大透明）PNG 宽偶有 1-2px 微变，重设窗口会抖动（胶水反馈 2026-08-09）
        bool sizeChanged = (Math.Abs(size.Width - _lastPixelW) > 2 || Math.Abs(size.Height - _lastPixelH) > 2 || s != _lastScale);
        if (sizeChanged)
        {
            double w = size.Width * s;
            double h = Math.Max(size.Height * s, _anchorH);  // 高度下限 = 气泡锚点基准
            this.Width = w;
            this.Height = h;
            RootCanvas.Width = w;
            RootCanvas.Height = h;
            SpriteImage.Width = w;
            SpriteImage.Height = h;
            _lastPixelW = size.Width;
            _lastPixelH = size.Height;
            _lastScale = s;
        }
        // Source 必须每帧设（换帧核心）
        SpriteImage.Source = _currentBitmap;
        Canvas.SetLeft(SpriteImage, 0);
        Canvas.SetTop(SpriteImage, 0);
    }

    /// <summary>
    /// 纸娃娃合成帧直拷：SKBitmap(BGRA8888 Premul) → WriteableBitmap 直拷像素，
    /// 跳过 PNG 编码 + Bitmap 解码往返（对齐 MapViewportWindow 直拷，30fps 热路径）。
    /// 容器尺寸/缩放逻辑与 SetFrame 一致（±2px 容忍）。
    /// </summary>
    public void SetFrameBitmap(SKBitmap bmp, int originX = 0, int originY = 0, double scale = 1.0)
    {
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
        {
            Clear();
            return;
        }

        _originX = originX;
        _originY = originY;

        double s = scale > 0 ? scale : 1.0;
        int w = bmp.Width;
        int h = bmp.Height;

        // ±2px 容忍 + 取整：超长帧偶有 1-2px 微变，窗口尺寸重设会抖动（胶水反馈 2026-08-09）
        bool sizeChanged = (Math.Abs(w - _lastPixelW) > 2 || Math.Abs(h - _lastPixelH) > 2 || s != _lastScale);
        if (sizeChanged)
        {
            double dw = w * s;
            double dh = Math.Max(h * s, _anchorH);  // 高度下限 = 气泡锚点基准
            this.Width = dw;
            this.Height = dh;
            RootCanvas.Width = dw;
            RootCanvas.Height = dh;
            SpriteImage.Width = dw;
            SpriteImage.Height = dh;
            _lastPixelW = w;
            _lastPixelH = h;
            _lastScale = s;
        }

        // 直拷 BGRA premul 像素。
        // ⚠️ 必须每次 new WriteableBitmap（Source 引用变化 → Image 必然重绘）：
        // 复用同引用时 Avalonia 不重绘 → 画面静止 = 动画"不动"（回归 bug，旧代码每帧 new Bitmap 所以动）
        _wb?.Dispose();
        _wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = _wb.Lock())
        {
            int srcRow = bmp.RowBytes;
            int dstRow = fb.RowBytes;
            int copyRow = Math.Min(srcRow, dstRow);
            unsafe
            {
                byte* src = (byte*)bmp.GetPixels();
                byte* dst = (byte*)fb.Address;
                if (srcRow == dstRow)
                {
                    long total = (long)srcRow * h;
                    Buffer.MemoryCopy(src, dst, total, total);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + y * srcRow, dst + y * dstRow, copyRow, copyRow);
                }
            }
        }
        SpriteImage.Source = _wb;
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
        _wb?.Dispose();
        _wb = null;
    }
}