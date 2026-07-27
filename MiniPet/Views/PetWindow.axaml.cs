using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
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
    private int _lastOriginX;
    private int _lastOriginY;

    public PetWindow(ConfigService configService, WzService wzService, AnimService animService, SpriteService spriteService, TrayService trayService, EventBus eventBus)
    {
        _configService = configService;
        _wzService = wzService;
        _animService = animService;
        _spriteService = spriteService;
        _trayService = trayService;
        _eventBus = eventBus;

        Title = "MiniPet";
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 64;
        MinHeight = 64;
        Topmost = true;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var grid = new Grid { Background = Brushes.Transparent };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Content = grid;

        _infoText = new TextBlock
        {
            Text = "MiniPet",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4),
        };
        Grid.SetRow(_infoText, 0);
        grid.Children.Add(_infoText);

        // 精灵画布（含 origin 对齐）
        _spriteCanvas = new SpriteCanvas();
        Grid.SetRow(_spriteCanvas, 1);
        grid.Children.Add(_spriteCanvas);

        // 聊天气泡控件
        _balloonControl = new BalloonControl();
        Grid.SetRow(_balloonControl, 2);
        grid.Children.Add(_balloonControl);

        ContextMenu = BuildContextMenu();
        ContextMenu.Opening += (_, _) => RefreshAnimationMenu();
        SetupDragEvents();

        // 帧循环：根据动画帧延迟动态间隔，基础轮询 66ms（≈15fps）
        _stopwatch = new Stopwatch();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += OnTimerTick;
        _animService.EnableRandomIdle(true);
        _stopwatch.Start();
        _timer.Start();

        _lastOriginX = 0;
        _lastOriginY = 0;

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
                try
                {
                    var mobId = _animService.GetCurrentMobId();
                    var actions = _wzService.GetActionList(mobId);
                    var dragAction = actions.Contains("fly") ? "fly" :
                        actions.Contains("move") ? "move" : "stand";
                    _animService.SwitchTo(dragAction);
                }
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
                        // 记录最近使用
                        RecordRecentItem(pet.Item1, pet.Item2);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetChanged: {ex.Message}"); }
            });
        });

        _eventBus.Subscribe(EventType.OpenSettings, _ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenSettings: {ex.Message}"); }
            });
        });
    }

    public void LoadDefaultPet(WzService wzService)
    {
        try
        {
            var settings = _configService.LoadSettings();
            if (settings != null && settings.CurrentMobId > 0)
            {
                var mobId = settings.CurrentMobId;
                _spriteService.PreloadMob(wzService, mobId);
                _animService.SetCurrentMobId(mobId);
                _animService.Play("stand", PlayMode.Loop);
                Console.WriteLine($"[PetWindow] 延迟加载默认宠物: {mobId}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] LoadDefaultPet: {ex.Message}"); }
    }

    /// <summary>
    /// 记录最近使用的宠物到配置文件
    /// </summary>
    private void RecordRecentItem(int id, string name)
    {
        try
        {
            var settings = _configService.LoadSettings();
            if (settings.RecentItems == null)
                settings.RecentItems = new System.Collections.Generic.List<Models.RecentItem>();

            // 移除旧的同名记录
            settings.RecentItems.RemoveAll(r => r.Id == id);
            // 插入到最前
            settings.RecentItems.Insert(0, new Models.RecentItem
            {
                Type = "mob",
                Id = id,
                Name = name,
                LastUsedAt = DateTime.Now
            });
            // 最多保留20条
            while (settings.RecentItems.Count > 20)
                settings.RecentItems.RemoveAt(settings.RecentItems.Count - 1);

            _configService.SaveSettings(settings);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RecordRecentItem: {ex.Message}"); }
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
        // 左键拖拽 + 右键菜单，直接绑定在 Window 上（冒泡模式）
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
                // 用 OS 原生拖拽
                BeginMoveDrag(e);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerPressed: {ex.Message}"); }
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
                var strip = _spriteService.GetCachedStrip(_animService.GetCurrentMobId(), _animService.GetCurrentAction()).Item1;
                int ox = strip?.OriginX ?? 0;
                int oy = strip?.OriginY ?? 0;
                _spriteCanvas.SetFrame(pngData, ox, oy);

                // 窗口大小 = 精灵图像素尺寸（容器不再膨胀）
                using var ms = new MemoryStream(pngData);
                using var bmp = new Bitmap(ms);
                Width = bmp.PixelSize.Width;
                Height = bmp.PixelSize.Height;

                // 窗口位置补偿锚点偏移：窗口向上/左移 (ox, oy)
                // 使精灵图的锚点对齐到屏幕正确位置
                int dx = ox - _lastOriginX;
                int dy = oy - _lastOriginY;
                if (dx != 0 || dy != 0)
                {
                    Position = new PixelPoint(Position.X - dx, Position.Y - dy);
                }
                _lastOriginX = ox;
                _lastOriginY = oy;
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

            // 最近使用（最多5个）
            var settings = _configService.LoadSettings();
            if (settings.RecentItems != null && settings.RecentItems.Count > 0)
            {
                var recentItems = settings.RecentItems
                    .OrderByDescending(r => r.LastUsedAt)
                    .Take(5)
                    .ToList();
                foreach (var recent in recentItems)
                {
                    var capturedId = recent.Id;
                    var capturedName = recent.Name;
                    var recentItem = new MenuItem { Header = $"🕐 {capturedName} [{capturedId}]" };
                    recentItem.Click += (_, _) =>
                    {
                        try
                        {
                            _animService.SetCurrentMobId(capturedId);
                            _spriteService.PreloadMob(_wzService, capturedId);
                            _animService.SwitchTo("stand");
                            _eventBus.Publish(EventType.PetChanged, (capturedId, capturedName));
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RecentSelect: {ex.Message}"); }
                    };
                    charItem.Items.Add(recentItem);
                }
                charItem.Items.Add(new Separator());
            }

            var mobSub = new MenuItem { Header = "怪物 (MOB)" };
            mobSub.Items.Add(new MenuItem { Header = "素材浏览器..." });
            (mobSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try
                {
                    var browser = new MaterialBrowserWindow(_wzService, _configService);
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
                    var browser = new MaterialBrowserWindow(_wzService, _configService);
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
            var animItem = new MenuItem { Header = "切换动画", Name = "AnimationMenu" };
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

            // ── 重置到桌面中央 ──
            var centerItem = new MenuItem { Header = "重置到桌面中央" };
            centerItem.Click += (_, _) =>
            {
                try
                {
                    var screens = Screens;
                    if (screens != null && screens.ScreenCount > 0)
                    {
                        var screen = screens.Primary ?? screens.All[0];
                        var bounds = screen.Bounds;
                        var w = Width;
                        var h = Height;
                        Position = new PixelPoint(
                            (int)(bounds.X + (bounds.Width - w) / 2),
                            (int)(bounds.Y + (bounds.Height - h) / 2));
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CenterWindow: {ex.Message}"); }
            };
            cm.Items.Add(centerItem);

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

    private void RefreshAnimationMenu()
    {
        try
        {
            if (ContextMenu == null) return;

            MenuItem? animationMenu = null;
            foreach (var entry in ContextMenu.Items)
            {
                if (entry is MenuItem { Name: "AnimationMenu" } menu)
                {
                    animationMenu = menu;
                    break;
                }
            }
            if (animationMenu == null) return;

            animationMenu.Items.Clear();
            var mobId = _animService.GetCurrentMobId();
            var actions = _wzService.GetActionList(mobId);
            foreach (var action in actions)
            {
                var capturedAction = action;
                var item = new MenuItem { Header = capturedAction };
                item.Click += (_, _) =>
                {
                    try
                    {
                        if (_spriteService.LoadSpriteStrip(mobId, capturedAction) == null)
                        {
                            _spriteService.PreloadAction(_wzService, mobId, capturedAction);
                        }
                        _animService.SwitchTo(capturedAction);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedAction}: {ex.Message}"); }
                };
                animationMenu.Items.Add(item);
            }

            if (actions.Count == 0)
            {
                animationMenu.Items.Add(new MenuItem { Header = "无可用动画", IsEnabled = false });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshAnimationMenu: {ex.Message}"); }
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
                    _spriteService.PreloadMob(_wzService, id);
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
