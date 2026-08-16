using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Utils;
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
    // M2：渲染失败/全透明时的窗口内错误提示（深色底保证透明窗口上可读）；成功渲染时隐藏。
    private readonly Border _errorCard;
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

    // 渲染完成回调（带请求 crop + PNG + 裁紧后世界原点）：PetWindow 注入实现 SaveMapPng 落盘（含 M6 裁紧原点 sidecar），无 WZ 时背景可离线显示。
    public Action<byte[], int, int, int, int, int, int>? OnMapRenderCached;

    public BackgroundWindow()
    {
        Title = "MiniPet-Background";
        Width = 800;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 静态图置顶（仅次于桌宠）：背景作为壁纸层覆盖普通窗口，点击穿透由 ignoresMouseEvents 控制（非交互模式）
        // 同级 Floating 层内 z-order：BackgroundWindow Show / 交互切换后 PetWindow 调 Activate 保持在上
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;

        _panel = new Panel { IsHitTestVisible = false };
        _image.IsHitTestVisible = false;
        _panel.Children.Add(_image);
        // M2：错误提示（居中，深色半透明底，透明窗口上可读）；默认隐藏
        var errorText = new TextBlock
        {
            Text = "⚠️ 地图渲染失败",
            FontSize = 14,
            Foreground = Brushes.White,
            Padding = new Thickness(16, 10),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        _errorCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 48)),
            CornerRadius = new CornerRadius(8),
            Child = errorText,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false
        };
        _panel.Children.Add(_errorCard);
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
        // 超大窗口（宽 > 屏幕宽）CenterScreen 会算出负 X（窗口主体在屏外，看起来"消失了"），强制 clamp 到屏幕内
        Dispatcher.UIThread.Post(ClampToScreen);
    }

    /// <summary>把窗口位置 clamp 到屏幕可见区域：超大窗口左缘贴屏幕左缘（X>=0），避免窗口主体在屏外。</summary>
    public void ClampToScreen()
    {
        try
        {
            var scr = Screens?.Primary;
            if (scr == null) return;
            int sw = (int)scr.Bounds.Width;
            int sh = (int)scr.Bounds.Height;
            int x = Position.X;
            int y = Position.Y;
            // 窗口比屏幕宽：左缘对齐屏幕左缘（X=0），保证可见
            if (Width >= sw) x = 0;
            else if (x < 0) x = 0;
            else if (x + Width > sw) x = Math.Max(0, sw - (int)Width);
            if (Height >= sh) y = 0;
            else if (y < 0) y = 0;
            else if (y + Height > sh) y = Math.Max(0, sh - (int)Height);
            if (x != Position.X || y != Position.Y)
                Position = new PixelPoint(x, y);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] ClampToScreen: {ex.Message}"); }
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
        if (_errorCard != null) _errorCard.IsVisible = false; // M2：新渲染开始前隐藏错误提示
        var gen = ++_renderGen;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var result = mapService.CropFull(map, cx, cy, cw, ch);
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _renderGen) return;
                    if (result == null)
                    {
                        // M2：渲染失败/全透明（CropFull 返回 null）→ 显示错误提示，不再静默空白
                        if (_errorCard != null) _errorCard.IsVisible = true;
                        Console.Error.WriteLine($"[BackgroundWindow] 地图渲染失败或全透明 (map={map.Id}, crop={cx},{cy},{cw}×{ch})");
                        return;
                    }
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
                    // 落盘缓存（用请求 crop 作 key；PNG 为裁紧后的实际图；M6：裁紧后世界原点随 sidecar 持久化，离线恢复不丢）
                    try { OnMapRenderCached?.Invoke(result.Png, cx, cy, cw, ch, result.WorldX, result.WorldY); }
                    catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] CacheMap: {ex.Message}"); }
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

    /// <summary>
    /// 离线模式：直接显示缓存的地图渲染 PNG（无 WZ 时背景可用）。crop 为渲染时的世界坐标窗口。
    /// M6：cropX/cropY 应传「裁紧后世界原点」（sidecar 恢复的值）——PNG 已是裁紧图，EffectiveCrop 必须对齐其左上角世界坐标。
    /// </summary>
    public void ShowCachedMapPng(byte[] pngData, int cropX, int cropY, int cropW, int cropH)
    {
        _map = null;
        _mapService = null;
        if (pngData == null || pngData.Length == 0) { _image.Source = null; return; }
        try
        {
            using var ms = new MemoryStream(pngData);
            using var bmp = new Bitmap(ms);
            Width = bmp.PixelSize.Width;
            Height = bmp.PixelSize.Height;
            EffectiveCropX = cropX; EffectiveCropY = cropY; EffectiveCropW = bmp.PixelSize.Width; EffectiveCropH = bmp.PixelSize.Height;
            _image.Source = new Bitmap(new MemoryStream(pngData));
            if (_errorCard != null) _errorCard.IsVisible = false;
            MapRendered?.Invoke();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] ShowCachedMapPng: {ex.Message}"); _image.Source = null; }
    }

    public void HideMap()
    {
        _image.Source = null;
        if (_errorCard != null) _errorCard.IsVisible = false;
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
                    // #4 根因：Activate()/Focus() 对 ShowActivated=false 的窗口首次不可靠，窗口未成为 key window 接收 mouseDown。
                    // 修复（macOS 专属）：makeKeyAndOrderFront: 强制 AppKit 把窗口重挂事件链 + 刷新 ignoresMouseEvents 切换后的 hit-test 状态。
                    // Windows 无此问题（Focus 即可），且 objc 调用在 Windows 上会抛 DllNotFoundException——平台守卫跳过。
                    if (!OperatingSystem.IsWindows())
                    {
                        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                        if (handle != IntPtr.Zero)
                        {
                            objc_msgSend(handle, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
                        }
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
        // 坐标源用 CGEvent 全局鼠标坐标（GetGlobalMousePosition），避免超大透明窗口下 e.GetPosition(null) 跳变导致抖动。
        _bgDragging = true;
        _bgDragStartScreen = GetGlobalMousePosition();
        _bgDragStartWindow = Position;
        try { e.Pointer.Capture(this); }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] PointerCapture: {ex.Message}"); }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_bgDragging) return;
        var current = GetGlobalMousePosition();
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

    // ── macOS 全局鼠标坐标：NSEvent.mouseLocation（AppKit 原生，返回左下原点 DIP，无需辅助功能权限）──
    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X; public double Y; }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSPoint objc_msgSend_NSPoint(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_getClass(string name);

    /// <summary>取全局鼠标坐标（DIP，左上原点），交互模式手动拖动用。</summary>
    private Point GetGlobalMousePosition()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows：user32 GetCursorPos 返回物理像素 → 除以 RenderScaling 转 DIP，
                // 与 macOS NSEvent.mouseLocation（DIP）语义对齐，拖动 delta 统一乘 RenderScaling。
                // 注：光标在异缩放显示器时按本窗口 RenderScaling 换算，多显示器混缩放属已知边角。
                var pos = WindowsNative.GetCursorPosition();
                if (pos.HasValue)
                {
                    double scale = RenderScaling > 0 ? RenderScaling : 1.0;
                    return new Point(pos.Value.X / scale, pos.Value.Y / scale);
                }
                return new Point(0, 0);
            }
            // macOS：NSEvent.mouseLocation 是左下原点 y 向上，翻转成 Avalonia 屏幕坐标。
            var cls = objc_getClass("NSEvent");
            var sel = sel_registerName("mouseLocation");
            var p = objc_msgSend_NSPoint(cls, sel);
            var scr = Screens?.Primary;
            double screenH = scr != null && scr.Bounds.Height > 0 ? scr.Bounds.Height : 1440;
            return new Point(p.X, screenH - p.Y);
        }
        catch
        {
            return new Point(0, 0);
        }
    }

    private void SetNativeIgnoresMouseEvents(bool ignore)
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            if (OperatingSystem.IsWindows())
            {
                // Windows：WS_EX_TRANSPARENT 点击穿透（等价 macOS ignoresMouseEvents）；
                // 失败最坏 = 窗口拦截点击（与旧实现 macOS-only 时的 Windows 行为一致，不抛）。
                WindowsNative.SetClickThrough(handle, ignore);
                return;
            }
            // macOS 上 TryGetPlatformHandle().Handle 即 AvnWindow（NSWindow 子类），直接调 setIgnoresMouseEvents
            objc_msgSend(handle, sel_registerName("setIgnoresMouseEvents:"), ignore ? new IntPtr(1) : IntPtr.Zero);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BackgroundWindow] NativeClickThrough: {ex.Message}"); }
    }
}
