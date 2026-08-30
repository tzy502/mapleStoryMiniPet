using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MiniPet.Services;
using MiniPet.Services.Road;
using SkiaSharp;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MiniPet.Views;

/// <summary>
/// F18 无限之路背景窗口（模式3，ADR-0003 穿透骨架仿 BackgroundWindow + 双缓冲管线仿 MapViewportWindow）。
/// 800×600 透明无边框 Topmost，屏幕居中偏下；默认整窗原生 ignoresMouseEvents 点击穿透，
/// SetInteractive(true) 关穿透后可右键拖动窗口位置 + 拖边框缩放（CanResize 同步）。
/// 渲染：D4 控制器每 tick 调 Render(frame, w, h) kick 一帧（存 volatile 最新快照引用），
/// 33ms DispatcherTimer 兜底重画最新快照；后台线程 InfiniteRoadRenderer.RenderFrame →
/// SKBitmap(BGRA8888/Premul) → WriteableBitmap 直拷像素（双缓冲复用，跳过 PNG 编解码）。
/// </summary>
public class InfiniteRoadWindow : Window
{
    /// <summary>最近显示的路窗实例（PetWindow 菜单「🛣 路窗可交互」定位用；HideRoad/关闭后置 null → 菜单 no-op）。</summary>
    public static InfiniteRoadWindow? Current { get; private set; }
    private double _lastPetX = double.NaN, _lastPetY; // 最近宠物脚底中心（跟随定位用；pet:moved 驱动）

    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly InfiniteRoadRenderer _renderer;
    private readonly DispatcherTimer _timer;
    private volatile RoadFrame? _latestFrame;
    private RoadFrame? _lastRenderedFrame;
    private int _rendering;                 // 0=idle, 1=busy（CAS 防后台渲染堆积）
    private int _renderGen;                 // Show/Hide 代际（防切窗在途帧串台）
    private double _pendingW, _pendingH;    // Render 入参兜底尺寸（窗口未布局完成时 Width 可能为 NaN）
    private bool _interactive;

    // M11 式 SKBitmap/WriteableBitmap 双缓冲复用（窗口侧仅 WriteableBitmap；SKBitmap 双缓冲在渲染器内部轮转）
    private WriteableBitmap? _wbA, _wbB;
    private int _wbSlot;

    // 右键拖动窗口位置（照抄 MapViewportWindow._winDragging 路径；穿透模式收不到事件，仅交互态可用）
    private bool _winDragging;
    private Point _winDragStartScreen;
    private PixelPoint _winDragStartWindow;

    // 首次交互提示条（顶部覆盖层 TextBlock；全局只弹一次，5 秒后自动隐藏）
    private readonly TextBlock _hintText;
    private static bool _hintShown;
    private DispatcherTimer? _hintTimer;

    /// <summary>渲染器实例（D4 接线 EntityBitmapResolver / 换图清柱位图缓存用）。</summary>
    public InfiniteRoadRenderer Renderer => _renderer;

    /// <summary>宠物右键菜单（模式3 人物实体化后右键入口——由控制器注入 PetWindow.ContextMenu）。</summary>
    public Avalonia.Controls.ContextMenu? PetMenu { get; set; }

    /// <summary>最新帧快照（D4 推送面：set 即 kick 渲染；get 供控制器读取当前显示态）。</summary>
    public RoadFrame? LatestFrame
    {
        get => _latestFrame;
        set
        {
            _latestFrame = value;
            if (value != null) { KickRender(); }
        }
    }

    public bool IsInteractive => _interactive;

    public InfiniteRoadWindow(WzService? wz = null, EventBus? events = null)
    {
        Title = "MiniPet-InfiniteRoad";
        Width = 800;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;                 // 默认穿透态：CanResize 随 SetInteractive 同步（交互态才可缩放）

        // 穿透态 Panel/Image 不参与命中（照 BackgroundWindow）；SetInteractive(true) 时改为可命中
        var panel = new Panel { IsHitTestVisible = false, Background = null };
        _image.IsHitTestVisible = false;
        panel.Children.Add(_image);

        // 首次交互提示条：顶部半透明黑底白字，不参与命中（不挡拖动/缩放），默认隐藏
        _hintText = new TextBlock
        {
            Text = "提示：路窗右键拖动 · 大小随窗口缩放 · 停止音乐即退出路模式",
            IsHitTestVisible = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            Foreground = Brushes.White,
            FontSize = 13
        };
        panel.Children.Add(_hintText);

        Content = panel;

        _renderer = new InfiniteRoadRenderer(wz);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _interactive) { SetInteractive(false); } };
        // 2026-09-01 甲方反馈「路窗右键无法呼出菜单」：默认穿透态收不到任何指针事件 → 改默认交互
        // （右键=宠物菜单在交互态生效；ESC 切回穿透；左键拖窗见 OnPointerPressed）
        SetInteractive(true);
        Resized += (_, _) => KickRender();   // 缩放窗口后按新 (int)Width/(int)Height 重渲（垂直 62% 锚随 h 重算）

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };   // ~30fps 兜底
        _timer.Tick += OnTimerTick;
    }

    /// <summary>渲染入口（D4 控制器每 tick 调用）：存 volatile 最新快照引用并 kick 一帧（只读快照，不持引用外泄可变态）。</summary>
    public void Render(RoadFrame frame, double windowW, double windowH)
    {
        if (frame == null) { return; }
        _latestFrame = frame;
        if (windowW > 0) { _pendingW = windowW; }
        if (windowH > 0) { _pendingH = windowH; }
        KickRender();
    }

    /// <summary>
    /// 交互态切换广播（参数=切换后的交互态）：路窗与 PetWindow 同级 Topmost，任何 Show/makeKeyAndOrderFront
    /// 都会把路窗顶到宠物窗之上（人物被盖）。订阅方在「退出交互（false）」时重申宠物窗在最前；
    /// 进入交互（true）时路窗在前属有意设计。
    /// </summary>
    public event Action<bool>? InteractiveChanged;

    public void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        if (Content is Panel panel)
        {
            panel.IsHitTestVisible = interactive;
            panel.Background = interactive ? Brushes.Transparent : null;
        }
        _image.IsHitTestVisible = interactive;
        CanResize = interactive;
        SetNativeIgnoresMouseEvents(!interactive);
        if (interactive)
        {
            // 交互态边界保障：仍置顶（路窗不被其他窗盖住），但不抢焦点（ShowActivated=false 基线不变）
            Topmost = true;
            ShowHintOnce();
            // makeKeyAndOrderFront: 强制 AppKit 重挂事件链 + 刷新 ignoresMouseEvents 切换后的 hit-test 状态
            // （Activate/Focus 对 ShowActivated=false 窗口首次不可靠；Post 到下一 runloop 确保穿透切换先落地）
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
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
                catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] KeyActivate: {ex.Message}"); }
            });
        }
        else
        {
            // 恢复穿透：立即收起提示条（穿透态下残留视觉会误导用户以为仍可交互）
            StopHint();
        }
        InteractiveChanged?.Invoke(interactive);
    }

    /// <summary>首次进入交互态时弹提示条（进程内仅一次），5 秒后自动隐藏。</summary>
    private void ShowHintOnce()
    {
        if (_hintShown) { return; }
        _hintShown = true;
        Dispatcher.UIThread.Post(() =>
        {
            _hintText.IsVisible = true;
            // 2026-08-29 用户要求：拖动期间提示条不隐藏（常驻至退出交互态——StopHint 在
            // SetInteractive(false)/HideRoad 收口），不再 5 秒自动淡出
        });
    }

    private void HintTimer_Tick(object? sender, EventArgs e)
    {
        StopHint();
    }

    /// <summary>收起提示条并停止其定时器（提示 5 秒到期或退出交互态时调用）。</summary>
    private void StopHint()
    {
        if (_hintTimer != null)
        {
            _hintTimer.Stop();
            _hintTimer.Tick -= HintTimer_Tick;
            _hintTimer = null;
        }
        _hintText.IsVisible = false;
    }

    /// <summary>显示路窗并启动渲染循环（Current 注册供菜单定位）。</summary>
    public void ShowRoad()
    {
        _renderGen++;
        Current = this;
        Topmost = true;                    // 每次 Show 重申置顶基线（防运行中置顶被外部改动）
        if (!_timer.IsEnabled) { _timer.Start(); }
        if (!double.IsNaN(_lastPetX))
        {
            Position = new PixelPoint((int)(_lastPetX - Width / 2), (int)(_lastPetY + 8)); // 默认在人物脚下
        }
        Show();
        if (!_interactive)
        {
            // 2026-09-01 右键修复：ShowRoad 不得无条件强制穿透——覆盖 _interactive=true（交互态）时
            // 右键/菜单事件被系统吃掉（右键 bug 反复的根因）——跟随当前态
            // 2026-09-01 右键保底（再次收敛）：ShowRoad 一律保持交互态（右键菜单始终可达）——
            // ESC 才切穿透（用户可主动）——右键失效 bug 的最终保险
            try { SetInteractive(true); } catch { }
            try { if (!_interactive) { SetNativeIgnoresMouseEvents(true); } }
            catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] ShowRoad穿透基线: {ex.Message}"); }
        }
        KickRender();
    }

    /// <summary>隐藏路窗：停渲染 + 清快照/柱位图缓存 + 复位穿透态（Current 注销 → 菜单 no-op）。</summary>
    public void HideRoad()
    {
        _timer.Stop();
        _latestFrame = null;
        _lastRenderedFrame = null;
        _image.Source = null;
        _renderGen++;
        _renderer.ClearColumnCache();
        StopHint();                        // 隐藏路窗时收起未到期的提示条
        if (_interactive) { SetInteractive(false); }
        if (Current == this) { Current = null; }
        Hide();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 首次 Show 后显式确立 ignoresMouseEvents=YES 基线（默认 NO 下首次关穿透是 no-op 的根因修复，照抄 BackgroundWindow）
        try { if (!_interactive) { SetNativeIgnoresMouseEvents(true); } }
        catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] OnOpened: {ex.Message}"); }
        // 屏幕居中偏下：CenterScreen 居中后下移 12% 屏高（路窗重心贴地面观感）
        try
        {
            var scr = Screens?.Primary;
            if (scr != null)
            {
                int dy = (int)(scr.Bounds.Height * 0.12);
                Position = new PixelPoint(Position.X, Math.Min(Position.Y + dy, (int)(scr.Bounds.Y + scr.Bounds.Height - Height)));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] 居中偏下: {ex.Message}"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (Current == this) { Current = null; }
        base.OnClosed(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // 兜底重画最新快照：帧引用未变且尺寸未变时跳过（静止画面不重渲烧 CPU）；新快照到达即重画（含暂停静止帧）
        if (_latestFrame == null) { return; }
        if (ReferenceEquals(_latestFrame, _lastRenderedFrame) && _wbA != null && _wbA.PixelSize.Width == (int)Width && _wbA.PixelSize.Height == (int)Height && _wbB != null)
        {
            return;
        }
        KickRender();
    }

    /// <summary>
    /// 即时触发一帧渲染（_rendering CAS 防堆积：在途则跳过，下一 tick/Render 再试）。
    /// 渲染器输出 BGRA8888/Premul SKBitmap → WriteableBitmap 直拷像素（照抄 MapViewportWindow，跳过 PNG 编解码往返）。
    /// </summary>
    private void KickRender()
    {
        if (!IsVisible) { return; }
        var frame = _latestFrame;
        if (frame == null) { return; }
        if (Interlocked.CompareExchange(ref _rendering, 1, 0) != 0) { return; }

        int sw = (int)Width, sh = (int)Height;
        if (sw <= 0 && _pendingW > 0) { sw = (int)_pendingW; }
        if (sh <= 0 && _pendingH > 0) { sh = (int)_pendingH; }
        if (sw <= 0 || sh <= 0)
        {
            Interlocked.Exchange(ref _rendering, 0);
            return;
        }
        int gen = _renderGen;
        int slot = _wbSlot; _wbSlot ^= 1;

        Task.Run(() =>
        {
            try
            {
                var rendered = _renderer.RenderFrame(frame, sw, sh);
                if (rendered == null) { return; }
                // WriteableBitmap 双缓冲复用（尺寸变化才重建；旧 wb 在 UI 线程换源后释放）
                WriteableBitmap? oldWb = null;
                var wb = slot == 0 ? _wbA : _wbB;
                if (wb == null || wb.PixelSize.Width != sw || wb.PixelSize.Height != sh)
                {
                    oldWb = wb;
                    wb = new WriteableBitmap(new PixelSize(sw, sh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    if (slot == 0) { _wbA = wb; } else { _wbB = wb; }
                }
                // SKBitmap(BGRA8888 Premul) → WriteableBitmap 直拷
                using (var fb = wb.Lock())
                {
                    int srcRow = rendered.RowBytes, dstRow = fb.RowBytes;
                    int copyRow = Math.Min(srcRow, dstRow);
                    unsafe
                    {
                        byte* srcP = (byte*)rendered.GetPixels();
                        byte* dst = (byte*)fb.Address;
                        if (srcRow == dstRow)
                        {
                            long total = (long)srcRow * rendered.Height;
                            Buffer.MemoryCopy(srcP, dst, total, total);
                        }
                        else
                        {
                            for (int y = 0; y < rendered.Height; y++)
                            {
                                Buffer.MemoryCopy(srcP + y * srcRow, dst + y * dstRow, copyRow, copyRow);
                            }
                        }
                    }
                }
                var wbToSet = wb;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (gen != _renderGen) { return; }
                        _image.Source = wbToSet;
                        _lastRenderedFrame = frame;
                    }
                    finally
                    {
                        oldWb?.Dispose();
                    }
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] Render: {ex.Message}"); }
            finally
            {
                Interlocked.Exchange(ref _rendering, 0);
            }
        });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_interactive) { return; }
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            // 右键 = 宠物菜单（人物实体化后用户右键点路窗＝期望菜单；穿透态改默认交互后生效）
            if (PetMenu != null)
            {
                try { PetMenu.Open(this); } catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] PetMenu: {ex.Message}"); }
                return;
            }
            _winDragging = true;
            var rel = e.GetPosition(null);
            _winDragStartScreen = new Point(Position.X + rel.X * RenderScaling, Position.Y + rel.Y * RenderScaling);
            _winDragStartWindow = Position;
            try { e.Pointer.Capture(this); }
            catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] PointerCapture: {ex.Message}"); }
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            // 左键拖窗（右键让给宠物菜单——2026-09-01）
            if (PetMenu != null)
            {
                _winDragging = true;
                var rel = e.GetPosition(null);
                _winDragStartScreen = new Point(Position.X + rel.X * RenderScaling, Position.Y + rel.Y * RenderScaling);
                _winDragStartWindow = Position;
                try { e.Pointer.Capture(this); }
                catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] PointerCapture: {ex.Message}"); }
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_winDragging) { return; }
        var rel = e.GetPosition(null);
        var absScreen = new Point(Position.X + rel.X * RenderScaling, Position.Y + rel.Y * RenderScaling);
        int winDx = (int)(absScreen.X - _winDragStartScreen.X);
        int winDy = (int)(absScreen.Y - _winDragStartScreen.Y);
        Position = new PixelPoint(_winDragStartWindow.X + winDx, _winDragStartWindow.Y + winDy);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_winDragging) { return; }
        _winDragging = false;
        try { e.Pointer.Capture(null); }
        catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] PointerRelease: {ex.Message}"); }
    }

    // ====== 原生点击穿透（照抄 BackgroundWindow：macOS NSWindow.ignoresMouseEvents / Windows WS_EX_TRANSPARENT） ======

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
            if (handle == IntPtr.Zero) { return; }
            if (OperatingSystem.IsWindows())
            {
                Utils.WindowsNative.SetClickThrough(handle, ignore);
                return;
            }
            objc_msgSend(handle, sel_registerName("setIgnoresMouseEvents:"), ignore ? new IntPtr(1) : IntPtr.Zero);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[InfiniteRoadWindow] NativeClickThrough: {ex.Message}"); }
    }
}
