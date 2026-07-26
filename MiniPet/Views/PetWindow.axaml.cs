using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using MiniPet.Services;
using MiniPet.Views.Controls;

namespace MiniPet.Views;

public class PetWindow : Window
{
    private readonly ConfigService _configService;
    private readonly WzService _wzService;
    private readonly AnimService _animService;
    private readonly SpriteService _spriteService;
    private readonly TrayService _trayService;
    private readonly EventBus _eventBus;
    private readonly SpriteCanvas _spriteCanvas;
    private readonly BalloonControl _balloonControl;
    private readonly TextBlock _infoText;
    private Point _dragStart;
    private bool _isDragging;
    private PixelPoint _windowStartPos;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch;
    private int _tickCount;
    private double _lastElapsed;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;

    public PetWindow(ConfigService configService, WzService wzService, AnimService animService, SpriteService spriteService, TrayService trayService, EventBus eventBus)
    {
        _configService = configService;
        _wzService = wzService;
        _animService = animService;
        _spriteService = spriteService;
        _trayService = trayService;
        _eventBus = eventBus;

        Title = "MiniPet";
        Width = 320;
        Height = 300;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Content = grid;

        _infoText = new TextBlock
        {
            Text = "MiniPet",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4)
        };
        Grid.SetRow(_infoText, 0);
        grid.Children.Add(_infoText);

        // 精灵画布（含 origin 对齐）
        _spriteCanvas = new SpriteCanvas { Width = 300, Height = 200 };
        Grid.SetRow(_spriteCanvas, 1);
        grid.Children.Add(_spriteCanvas);

        // 聊天气泡控件（不阻挡拖拽）
        _balloonControl = new BalloonControl { IsHitTestVisible = false };
        Grid.SetRow(_balloonControl, 2);
        grid.Children.Add(_balloonControl);

        ContextMenu = BuildContextMenu();
        SetupDragEvents();

        // 帧循环：30fps ≈ 33ms
        _stopwatch = new Stopwatch();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        _animService.EnableRandomIdle(true);
        _stopwatch.Start();
        _timer.Start();

        // 启动时加载配置中保存的默认宠物
        bool loadedPet = false;
        try
        {
            var settings = _configService.LoadSettings();
            if (settings != null && settings.CurrentMobId > 0)
            {
                var mobId = settings.CurrentMobId;
                _animService.SetCurrentMobId(mobId);
                _spriteService.PreloadMob(_wzService, mobId);
                _animService.Play("stand", PlayMode.Loop);
                loadedPet = true;
                Console.WriteLine($"[PetWindow] 加载默认宠物: {mobId}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] LoadDefaultPet: {ex.Message}"); }

        if (!loadedPet)
            _animService.Play("stand", PlayMode.Loop);

        // 托盘集成
        try
        {
            _trayService.SetMainWindow(this);
            _trayService.ShowTrayIcon();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetWindow] TrayInit: {ex.Message}");
        }

        _eventBus.Subscribe(EventType.UserDragStart, _ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { _animService.SwitchTo("fly"); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] DragStart anim: {ex.Message}"); }
            });
        });
        _eventBus.Subscribe(EventType.UserDragEnd, _ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { _animService.SwitchTo("stand"); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] DragEnd anim: {ex.Message}"); }
            });
        });

        _eventBus.Subscribe(EventType.AppExit, _ =>
        {
            try
            {
                _isExiting = true;
                _trayService.HideTrayIcon();
                _settingsWindow?.Close();
                Close();
                Dispatcher.UIThread.InvokeShutdown();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PetWindow] AppExit: {ex.Message}");
                Environment.Exit(1);
            }
        });

        _eventBus.Subscribe(EventType.ConfigChanged, data =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _configService.SaveSettings(_configService.LoadSettings());
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ConfigChanged: {ex.Message}"); }
            });
        });

        _eventBus.Subscribe(EventType.PetChanged, data =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (data is ValueTuple<int, string> pet)
                    {
                        _trayService.SetTooltip($"MiniPet - {pet.Item2} (ID:{pet.Item1})");
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetChanged: {ex.Message}"); }
            });
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try
        {
            if (!_isExiting)
            {
                // 关闭时隐藏到托盘，不退出应用
                e.Cancel = true;
                Hide();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnClosing: {ex.Message}"); }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _trayService.HideTrayIcon(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnClosed: {ex.Message}"); }
        base.OnClosed(e);
    }

    private void SetupDragEvents()
    {
        PointerPressed += (_, e) =>
        {
            try
            {
                var point = e.GetCurrentPoint(this);
                if (point.Properties.IsRightButtonPressed)
                {
                    ContextMenu?.Open(this);
                    return;
                }
                _isDragging = true;
                _dragStart = e.GetPosition(this);
                _windowStartPos = Position;
                _eventBus.Publish(EventType.UserDragStart);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerPressed: {ex.Message}"); }
        };
        PointerMoved += (_, e) =>
        {
            try
            {
                if (!_isDragging) return;
                var pos = e.GetPosition(this);
                Position = new PixelPoint(
                    (int)(_windowStartPos.X + pos.X - _dragStart.X),
                    (int)(_windowStartPos.Y + pos.Y - _dragStart.Y));
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerMoved: {ex.Message}"); }
        };
        PointerReleased += (_, _) =>
        {
            try
            {
                if (!_isDragging) return;
                _isDragging = false;
                _eventBus.Publish(EventType.UserDragEnd);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerReleased: {ex.Message}"); }
        };
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            _tickCount++;
            var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
            var delta = elapsed - _lastElapsed;
            _lastElapsed = elapsed;

            if (delta <= 0) delta = 33;
            if (delta > 500) delta = 33; // 防止大跳跃（窗口失焦后恢复）

            var (fd, pngData) = _animService.Tick(delta);
            if (pngData != null)
            {
                _spriteCanvas.SetFrame(pngData);
            }
            var action = _animService.GetCurrentAction();
            _infoText.Text = $"{action} #{_tickCount}";
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] Tick: {ex.Message}"); }
    }

    private ContextMenu BuildContextMenu()
    {
        var cm = new ContextMenu();
        try
        {
            // ── 切换人物 ──
            var charItem = new MenuItem { Header = "切换人物" };
            var mobSub = new MenuItem { Header = "怪物 (MOB)" };
            mobSub.Items.Add(new MenuItem { Header = "素材浏览器..." });
            (mobSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try
                {
                    var browser = new MaterialBrowserWindow(_wzService);
                    browser.OnMaterialSelected += (id, name) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                _animService.SetCurrentMobId(id);
                                _spriteService.PreloadMob(_wzService, id);
                                _animService.SwitchTo("stand");
                                _eventBus.Publish(EventType.PetChanged, (id, name));
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BrowserSelect: {ex.Message}"); }
                        });
                    };
                    browser.Show();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MaterialBrowser: {ex.Message}"); }
            };
            charItem.Items.Add(mobSub);

            var npcSub = new MenuItem { Header = "NPC" };
            npcSub.Items.Add(new MenuItem { Header = "素材浏览器..." });
            (npcSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try
                {
                    var browser = new MaterialBrowserWindow(_wzService);
                    browser.OnMaterialSelected += (id, name) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                _animService.SetCurrentMobId(id);
                                _spriteService.PreloadMob(_wzService, id);
                                _animService.SwitchTo("stand");
                                _eventBus.Publish(EventType.PetChanged, (id, name));
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BrowserSelect: {ex.Message}"); }
                        });
                    };
                    browser.Show();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MaterialBrowser NPC: {ex.Message}"); }
            };
            charItem.Items.Add(npcSub);

            var playerSub = new MenuItem { Header = "玩家 (PLAYER)" };
            playerSub.Items.Add(new MenuItem { Header = "纸娃娃编辑器..." });
            (playerSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SettingsWindow: {ex.Message}"); }
            };
            charItem.Items.Add(playerSub);
            cm.Items.Add(charItem);

            // ── 切换动画 ──
            var animItem = new MenuItem { Header = "切换动画" };
            var animNames = new[] { "stand", "move", "fly", "say", "mouse", "laugh", "eye", "hit", "skill", "die" };
            foreach (var name in animNames)
            {
                var capturedName = name;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) =>
                {
                    try { _animService.SwitchTo(capturedName); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedName}: {ex.Message}"); }
                };
                animItem.Items.Add(item);
            }
            cm.Items.Add(animItem);

            cm.Items.Add(new Separator());

            // ── 切换背景 ──
            var bgItem = new MenuItem { Header = "切换背景" };
            bgItem.Items.Add(new MenuItem { Header = "设置中心 > 背景设置" });
            (bgItem.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BgSettings: {ex.Message}"); }
            };
            cm.Items.Add(bgItem);

            // ── 切换气泡 ──
            var balloonItem = new MenuItem { Header = "切换气泡" };
            balloonItem.Items.Add(new MenuItem { Header = "设置中心 > 气泡设置" });
            (balloonItem.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonSettings: {ex.Message}"); }
            };
            cm.Items.Add(balloonItem);

            cm.Items.Add(new Separator());

            // ── Hermes 终端 ──
            var terminalItem = new MenuItem { Header = "Hermes 终端" };
            terminalItem.Items.Add(new MenuItem { Header = "待实现" });
            cm.Items.Add(terminalItem);

            // ── 设置中心 ──
            var settingsItem = new MenuItem { Header = "设置中心" };
            settingsItem.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SettingsWindow: {ex.Message}"); }
            };
            cm.Items.Add(settingsItem);

            cm.Items.Add(new Separator());

            // ── 退出 ──
            var exitItem = new MenuItem { Header = "退出" };
            exitItem.Click += (_, _) =>
            {
                try
                {
                    _isExiting = true;
                    _trayService.HideTrayIcon();
                    _settingsWindow?.Close();
                    _eventBus.Publish(EventType.AppExit);
                    Close();
                    Dispatcher.UIThread.InvokeShutdown();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PetWindow] Exit: {ex.Message}");
                    Environment.Exit(1);
                }
            };
            cm.Items.Add(exitItem);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BuildMenu: {ex.Message}"); }
        return cm;
    }

    private void ShowSettingsWindow()
    {
        try
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_configService, _wzService);
            _settingsWindow.OnPetApplied += (id, name) =>
            {
                try
                {
                    _animService.SetCurrentMobId(id);
                    _animService.SwitchTo("stand");
                    _eventBus.Publish(EventType.PetChanged, (id, name));
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnPetApplied: {ex.Message}"); }
            };
            _settingsWindow.Show();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowSettingsWindow: {ex.Message}"); }
    }
}
