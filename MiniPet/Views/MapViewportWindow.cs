using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using SkiaSharp;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MiniPet.Views;

/// <summary>
/// 模式2 摄像机地图背景窗口（ADR-0001 三层架构）。
/// 左键拖动相机（camX/camY += −dx/zoom）+ 滚轮缩放（以光标为中心，0.25~4）+ back 视差实时（viewTimeMs 累加）。
/// 800×600 透明无边框，Topmost，非点击穿透（始终交互）。后台线程 RenderViewport→PNG，UI 线程贴 Bitmap（代际保护防切换串台）。
/// 渲染算法 1:1 对齐 WzComparerR2.MapRender（MapService.RenderViewport）。
/// </summary>
public class MapViewportWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.None };
    private MapInfo? _map;
    private MapService? _mapService;
    private float _camX, _camY, _zoom = 1f;
    private long _viewTimeMs;
    private int _renderGen;
    private int _rendering; // 0=idle, 1=busy（防后台渲染堆积）
    private readonly DispatcherTimer _timer;

    // 左键拖动状态（屏幕坐标 → camCenter 增量）
    private bool _dragging;
    private Point _dragStart;
    private float _dragStartCamX, _dragStartCamY;

    // 右键拖动窗口位置状态（屏幕坐标 → Position 增量，仿 BackgroundWindow.OnPointerMoved）
    private bool _winDragging;
    private Point _winDragStartScreen;
    private PixelPoint _winDragStartWindow;

    public event Action? RequestHide;
    public event Action? RequestSwitchMap;

    public MapViewportWindow()
    {
        Title = "MiniPet-MapViewport";
        Width = 800;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = true; // #4 摄像机窗口可设置大小

        // 始终交互（非点击穿透）：Panel 透明背景使其可命中
        var panel = new Panel { Background = Brushes.Transparent };
        _image.IsHitTestVisible = false; // 由 Panel 收事件，避免 Image 吞事件
        panel.Children.Add(_image);
        Content = panel;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        // #4 窗口缩放后立即按新 (int)Width/(int)Height 重渲（bitmap 大小=窗口大小 1:1）
        Resized += (_, _) => KickRender();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
        _timer.Tick += OnTimerTick;

        // #4 去掉摄像机窗口自身的右键 ContextMenu（退出/切换地图/隐藏 已在桌宠右键菜单「背景模式」里有，避免与右键拖动冲突）
    }

    /// <summary>
    /// 设置地图并开始渲染：清精灵缓存（旧地图资源释放）→ camX/camY=地图中心，zoom=1，viewTimeMs=0 → 启动 timer。
    /// </summary>
    public void SetMap(MapInfo? map, MapService? mapService)
    {
        _map = map;
        _mapService = mapService;
        if (map == null || mapService == null) { _image.Source = null; _timer.Stop(); return; }
        mapService.ClearSpriteCache();
        var (cx, cy) = MapService.GetMapCenter(map);
        _camX = cx; _camY = cy;
        _zoom = 1f;
        _viewTimeMs = 0;
        _image.Source = null;
        _renderGen++; // 让上一张地图在途的后台渲染结果失效
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>停止渲染（切静态/退出时）：停 timer + 清精灵缓存 + 清图。</summary>
    public void Stop()
    {
        _timer.Stop();
        _mapService?.ClearSpriteCache();
        _image.Source = null;
        _renderGen++;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // timer 仅推进 viewTimeMs（back 自动滚动动画）；拖动跟手由 PointerMoved → KickRender 即时触发。
        _viewTimeMs += 33;
        KickRender();
    }

    /// <summary>
    /// 即时触发一帧渲染（_rendering CAS 防堆积：上一帧在途则本次跳过，等下一事件再试）。
    /// 拖动 PointerMoved 每次调用 → 拖完一帧立刻渲下一帧（不等 33ms tick）；timer tick 也调（推 viewTime）。
    /// 跳过 PNG 编码/解码：RenderViewport 输出 BGRA8888/Premul SKBitmap → WriteableBitmap 直拷像素（省 ~20-40ms/帧）。
    /// </summary>
    private void KickRender()
    {
        if (!IsVisible || _map == null || _mapService == null) return;
        if (Interlocked.CompareExchange(ref _rendering, 1, 0) != 0) return;

        // clamp camX/camY 到地图边界（AdjustToWorldRect）
        var (cx, cy) = MapService.ClampCamera(_map, _camX, _camY, _zoom, (int)Width, (int)Height);
        _camX = cx; _camY = cy;

        var map = _map; var svc = _mapService;
        float zoom = _zoom;
        long vt = _viewTimeMs;
        int sw = (int)Width, sh = (int)Height;
        int gen = _renderGen;

        Task.Run(() =>
        {
            SKBitmap? bmp = null;
            try
            {
                bmp = svc.RenderViewport(map, cx, cy, zoom, vt, sw, sh);
                if (bmp == null) return;
                // SKBitmap(BGRA8888 Premul) → WriteableBitmap 直拷，跳过 PNG 编码 + Bitmap 解码往返。
                var wb = new WriteableBitmap(new PixelSize(bmp.Width, bmp.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = wb.Lock())
                {
                    int srcRow = bmp.RowBytes, dstRow = fb.RowBytes;
                    int copyRow = Math.Min(srcRow, dstRow);
                    unsafe
                    {
                        byte* src = (byte*)bmp.GetPixels();
                        byte* dst = (byte*)fb.Address;
                        if (srcRow == dstRow)
                        {
                            // stride 相等（BGRA8888 两端均 w*4）：整块拷贝
                            long total = (long)srcRow * bmp.Height;
                            Buffer.MemoryCopy(src, dst, total, total);
                        }
                        else
                        {
                            for (int y = 0; y < bmp.Height; y++)
                                Buffer.MemoryCopy(src + y * srcRow, dst + y * dstRow, copyRow, copyRow);
                        }
                    }
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _renderGen) return; // 切换/Stop 后丢弃
                    _image.Source = wb;
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapViewportWindow] Render: {ex.Message}"); }
            finally
            {
                bmp?.Dispose();
                Interlocked.Exchange(ref _rendering, 0);
            }
        });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            // 右键：移动窗口位置（手动 Position delta）。
            // 关键：GetPosition(null) 是窗口客户区坐标（非屏幕坐标），直接用它的差做 delta 会形成反馈循环（窗口动→相对坐标变→再动→抖）。
            // 正确：Position + GetPosition(null)*RenderScaling = 鼠标绝对屏幕坐标（物理像素），窗口移动时两者反向抵消，稳定。
            _winDragging = true;
            var rel = e.GetPosition(null);
            _winDragStartScreen = new Point(Position.X + rel.X * RenderScaling, Position.Y + rel.Y * RenderScaling);
            _winDragStartWindow = Position;
            try { e.Pointer.Capture(this); }
            catch (Exception ex) { Console.Error.WriteLine($"[MapViewportWindow] WinPointerCapture: {ex.Message}"); }
            return;
        }
        // 左键：开始拖动相机（camX/camY += −dx/zoom）
        _dragging = true;
        _dragStart = e.GetPosition(this); // DIP 坐标
        _dragStartCamX = _camX;
        _dragStartCamY = _camY;
        try { e.Pointer.Capture(this); }
        catch (Exception ex) { Console.Error.WriteLine($"[MapViewportWindow] PointerCapture: {ex.Message}"); }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_winDragging)
        {
            var rel = e.GetPosition(null);
            var absScreen = new Point(Position.X + rel.X * RenderScaling, Position.Y + rel.Y * RenderScaling);
            int winDx = (int)(absScreen.X - _winDragStartScreen.X);
            int winDy = (int)(absScreen.Y - _winDragStartScreen.Y);
            Position = new PixelPoint(_winDragStartWindow.X + winDx, _winDragStartWindow.Y + winDy);
            return;
        }
        if (!_dragging) return;
        var cur = e.GetPosition(this);
        float dx = (float)(cur.X - _dragStart.X);
        float dy = (float)(cur.Y - _dragStart.Y);
        // 屏幕像素差 → 世界坐标差：camX -= dx/zoom（拖动方向与相机移动方向相反：右拖→相机左移）
        _camX = _dragStartCamX - dx / _zoom;
        _camY = _dragStartCamY - dy / _zoom;
        // 即时触发渲染（不等 33ms tick）：拖动跟手，_rendering CAS 防堆积（在途则跳过，下一事件再试）。
        KickRender();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // #4 右键拖动窗口结束
        if (_winDragging)
        {
            _winDragging = false;
            try { e.Pointer.Capture(null); }
            catch (Exception ex) { Console.Error.WriteLine($"[MapViewportWindow] WinPointerRelease: {ex.Message}"); }
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        try { e.Pointer.Capture(null); }
        catch (Exception ex) { Console.Error.WriteLine($"[MapViewportWindow] PointerRelease: {ex.Message}"); }
    }

    /// <summary>
    /// 滚轮缩放：以光标为中心（保持光标下世界点不变）。新 zoom = clamp(zoom*(Δ.Y>0?1.1:0.9), 0.25, 4)。
    /// 光标下世界点 world = camCenter + (mouse − screenCenter)/oldZoom；改 zoom 后调 camCenter = world − (mouse−screenCenter)/newZoom。
    /// </summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var mouse = e.GetPosition(this); // DIP
        float oldZoom = _zoom;
        float newZoom = (float)Math.Clamp(oldZoom * (e.Delta.Y > 0 ? 1.1 : 0.9), 0.25, 4);
        if (Math.Abs(newZoom - oldZoom) < 1e-4) return;
        float halfW = (float)Width / 2, halfH = (float)Height / 2;
        float worldX = _camX + ((float)mouse.X - halfW) / oldZoom;
        float worldY = _camY + ((float)mouse.Y - halfH) / oldZoom;
        _zoom = newZoom;
        _camX = worldX - ((float)mouse.X - halfW) / newZoom;
        _camY = worldY - ((float)mouse.Y - halfH) / newZoom;
    }
}
