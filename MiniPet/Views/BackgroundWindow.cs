using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MiniPet.Views;

/// <summary>
/// 地图背景独立窗口（ADR-0001 三层架构）。
/// 默认点击穿透（原生 NSWindow.ignoresMouseEvents，ADR-0003）；交互模式可左键拖动 + 右键菜单。
/// crop 渲染：窗口尺寸 = crop 尺寸 1:1，后台渲染带代际保护。
/// </summary>
public class BackgroundWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly Panel _panel;
    private MapInfo? _map;
    private MapService? _mapService;
    private bool _interactive;
    private int _renderGen;
    // #5：手动拖动状态（替代 BeginMoveDrag，大窗口/屏外窗口下原生 API 鼠标映射异常）
    private bool _bgDragging;
    private Point _bgDragStartScreen;
    private PixelPoint _bgDragStartWindow;

    public bool IsInteractive => _interactive;
    public event Action? RequestHide;
    public event Action? RequestSwitchMap;
    // #4：交互模式切换且 z-order 被原生 makeKeyAndOrderFront 改变后触发，PetWindow 据此重新置顶
    public event Action? InteractiveChanged;

    // #6：实际渲染返回图（裁紧透明 padding 后）对应的「世界坐标 crop」与尺寸。
    // PetWindow.PlacePetOnMapGround 应改用本组属性（而非 SetMap 入参的 cropX/cropY），以保证裁紧后宠物落地仍对齐地面。
    // 渲染未完成前为入参 crop 的占位值；渲染完成后在 UIThread Post 内更新为裁紧后的实际值。
    public int EffectiveCropX { get; private set; }
    public int EffectiveCropY { get; private set; }
    public int EffectiveCropW { get; private set; }
    public int EffectiveCropH { get; private set; }

    // #6：渲染（裁紧透明 padding）完成后触发。PetWindow 据此用 EffectiveCrop* 重新落地——
    // 同步调用 SetMap 后渲染仍在后台线程，裁紧值未就绪；落地必须在渲染完成后。
    public event Action? MapRendered;

    public BackgroundWindow()
    {
        Title = "MiniPet-Background";
        Width = 800;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;

        _panel = new Panel { IsHitTestVisible = false };
        _image.IsHitTestVisible = false;
        _panel.Children.Add(_image);
        Content = _panel;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _interactive) SetInteractive(false); };

        var exitItem = new MenuItem { Header = "退出交互模式" };
        exitItem.Click += (_, _) => SetInteractive(false);
        var switchItem = new MenuItem { Header = "切换地图…" };
        switchItem.Click += (_, _) => RequestSwitchMap?.Invoke();
        var hideItem = new MenuItem { Header = "隐藏背景" };
        hideItem.Click += (_, _) => RequestHide?.Invoke();
        ContextMenu = new ContextMenu { Items = { exitItem, new Separator(), switchItem, hideItem } };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // #4 根因修复：首次 Show 后显式确立 ignoresMouseEvents=YES 基线。
        // 默认 NO 状态下首次 SetInteractive(true) 的 setIgnoresMouseEvents:NO 是 no-op，AppKit 不刷新事件路由（首次拖不动的根因）。
        try { if (!_interactive) SetNativeIgnoresMouseEvents(true); }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] OnOpened: {ex.Message}"); }
    }

    /// <summary>渲染并显示地图的 crop 区域（后台线程，_renderGen 代际保护防快速切换串台）。
    /// 整图渲染(相机=地图中心)→按像素裁 crop（CropFull 深拷贝 + #6 裁紧透明 padding），桌面与设置预览同源（ADR-0004）。
    /// N1：cropW<=0 归一化为整图（与 CropFull 同规则）。
    /// #6：窗口 Width/Height 改为按「实际渲染返回图尺寸」（Bitmap.Size / CroppedMap.Width/Height）而非输入 cropW/cropH，
    ///     透明 padding 不再计入窗口高度，用户拖动可直达顶部。EffectiveCrop* 同步更新为裁紧后的世界坐标 crop。</summary>
    public void SetMap(MapInfo? map, MapService? mapService, int cropX, int cropY, int cropW, int cropH)
    {
        _map = map;
        _mapService = mapService;
        if (map == null || mapService == null) { _image.Source = null; return; }
        int cx = cropX, cy = cropY, cw = cropW, ch = cropH;
        if (cw <= 0 || ch <= 0) { cx = map.MinX; cy = map.MinY; cw = map.MaxX - map.MinX; ch = map.MaxY - map.MinY; }
        if (cw <= 0 || ch <= 0) { _image.Source = null; return; }
        // 先用入参 crop 占位（窗口立即出现，尺寸接近），渲染完成后按返回图实际尺寸修正。
        Width = cw;
        Height = ch;
        EffectiveCropX = cx; EffectiveCropY = cy; EffectiveCropW = cw; EffectiveCropH = ch;
        _image.Source = null;
        var gen = ++_renderGen;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var result = mapService.CropFull(map, cx, cy, cw, ch);
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _renderGen) return;
                    if (result == null) return;
                    // #6：窗口尺寸 = 实际渲染返回图尺寸（裁紧后），透明 padding 不再计入高度。
                    Width = result.Width;
                    Height = result.Height;
                    EffectiveCropX = result.WorldX;
                    EffectiveCropY = result.WorldY;
                    EffectiveCropW = result.Width;
                    EffectiveCropH = result.Height;
                    using var ms = new MemoryStream(result.Png);
                    _image.Source = new Bitmap(ms);
                    MapRendered?.Invoke();
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] Render: {ex.Message}"); }
        });
    }

    /// <summary>兼容旧调用：渲染整图（crop = 全图边界）。</summary>
    public void SetMap(MapInfo? map, MapService? mapService)
    {
        if (map == null) { SetMap(null, null, 0, 0, 0, 0); return; }
        SetMap(map, mapService, map.MinX, map.MinY, map.MaxX - map.MinX, map.MaxY - map.MinY);
    }

    public void HideMap()
    {
        _image.Source = null;
        _map = null;
        if (_interactive) SetInteractive(false);
    }

    /// <summary>
    /// 切换交互模式。interactive=true：恢复鼠标事件（左键 BeginMoveDrag 拖动 + 右键菜单），Activate 聚焦接收 Esc/拖动；
    /// false（默认穿透）：IsHitTestVisible=false + 原生 NSWindow.ignoresMouseEvents=true。句柄不可用时安全 no-op。
    /// </summary>
    public void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        _panel.IsHitTestVisible = interactive;
        _image.IsHitTestVisible = interactive;
        // 交互模式：Panel 设透明背景使其可命中（null 背景 Avalonia 不收事件，点击穿透）；穿透模式 null
        _panel.Background = interactive ? Brushes.Transparent : null;
        SetNativeIgnoresMouseEvents(!interactive);
        if (interactive)
        {
            // #4 根因：Activate()/Focus() 对 ShowActivated=false 的窗口首次不可靠，窗口未成为 key window 接收 mouseDown。
            // 修复：makeKeyAndOrderFront: 强制 AppKit 把窗口重挂事件链 + 刷新 ignoresMouseEvents 切换后的 hit-test 状态。
            // Post 到下一 runloop，确保 setIgnoresMouseEvents:NO 先被 AppKit 处理落地（同帧调用会被旧状态判定忽略）。
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (handle != IntPtr.Zero)
                    {
                        objc_msgSend(handle, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
                    }
                    Focus();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] KeyActivate: {ex.Message}"); }
                // makeKeyAndOrderFront 改变了同级 Topmost 窗口的 z-order，通知 PetWindow 重新把宠物窗口提至最前
                InteractiveChanged?.Invoke();
            });
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_interactive) return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            ContextMenu?.Open(this);
            return;
        }
        // #5：BeginMoveDrag 在射手村大窗口（crop 全图 2000+ × 1000+）/ CenterScreen 居中后左上角为负（屏外）场景下，
        // 原生 performDrag: 的鼠标位置映射异常，导致拖动跳跃/卡死。改手动 Position delta 拖动，对任意尺寸/位置一致可预测。
        _bgDragging = true;
        _bgDragStartScreen = e.GetPosition(null);
        _bgDragStartWindow = Position;
        try { e.Pointer.Capture(this); }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] PointerCapture: {ex.Message}"); }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_bgDragging) return;
        var current = e.GetPosition(null);
        var scale = RenderScaling;
        int dx = (int)((current.X - _bgDragStartScreen.X) * scale);
        int dy = (int)((current.Y - _bgDragStartScreen.Y) * scale);
        Position = new PixelPoint(_bgDragStartWindow.X + dx, _bgDragStartWindow.Y + dy);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_bgDragging) return;
        _bgDragging = false;
        try { e.Pointer.Capture(null); }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] PointerRelease: {ex.Message}"); }
    }

    // ── macOS 原生点击穿透（ADR-0003）：NSWindow.ignoresMouseEvents ──
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport("/usr/lib/libobjc.dylib", CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    private void SetNativeIgnoresMouseEvents(bool ignore)
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            // macOS 上 TryGetPlatformHandle().Handle 即 AvnWindow（NSWindow 子类），直接调 setIgnoresMouseEvents
            objc_msgSend(handle, sel_registerName("setIgnoresMouseEvents:"), ignore ? new IntPtr(1) : IntPtr.Zero);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] NativeClickThrough: {ex.Message}"); }
    }
}
