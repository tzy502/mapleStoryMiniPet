using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MiniPet.Models;
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
    private readonly BalloonService _balloonService;
    private readonly PaperdollService _paperdoll;
    private readonly MapService _mapService;
    private readonly BackgroundWindow _backgroundWindow;
    private readonly DispatcherTimer _balloonTimer;
    private readonly Random _rng = new();
    private int _paperdollGen;
    private int _petSwitchGen;
    private static readonly string[] _randomLines = {
        "今天天气真好~", "加油！", "记得喝水哦", "嘿嘿~", "我在看着你呢",
        "累了就休息一下吧", "你今天也很努力呢", "要一起冒险吗？"
    };
    private readonly TextBlock _infoText;
    private Point _dragStart;
    private bool _isDragging;
    private PixelPoint _windowStartPos;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch;
    private int _tickCount;
    private double _lastElapsed;
    private SettingsWindow? _settingsWindow;
    private MenuItem? _bgInteractiveItem;
    private MenuItem? _bgMenu;
    private bool _isExiting;
    private int _lastOriginX;
    private int _lastOriginY;
    private int _lastFrameW;
    private int _lastFrameH;
    private double _scale = 1.0;
    private string _currentEntityType = "Mob";

    public PetWindow(ConfigService configService, WzService wzService, AnimService animService, SpriteService spriteService, TrayService trayService, EventBus eventBus, BalloonService balloonService, PaperdollService paperdoll, MapService mapService)
    {
        _configService = configService;
        _wzService = wzService;
        _animService = animService;
        _spriteService = spriteService;
        _trayService = trayService;
        _eventBus = eventBus;
        _balloonService = balloonService;
        _paperdoll = paperdoll;
        _mapService = mapService;
        _backgroundWindow = new BackgroundWindow();
        _backgroundWindow.RequestHide += () => Dispatcher.UIThread.Post(() => { _backgroundWindow.HideMap(); _backgroundWindow.Hide(); RaisePet(); });
        _backgroundWindow.RequestSwitchMap += () => Dispatcher.UIThread.Post(() => OpenBackgroundTab());
        // #4：背景窗口交互模式 makeKeyAndOrderFront 改变 z-order 后，重新把宠物窗口提至最前
        _backgroundWindow.InteractiveChanged += () => Dispatcher.UIThread.Post(() => RaisePet());

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
        ContextMenu.Opening += (_, _) => { RefreshAnimationMenu(); RebuildBgMenu(); };
        SetupDragEvents();

        // 帧循环：30fps 轮询（预裁剪帧缓存 + 追帧，多帧动画顺滑）
        _stopwatch = new Stopwatch();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        _balloonTimer = new DispatcherTimer();
        _animService.EnableRandomIdle(true);
        _lastOriginX = 0;
        _lastOriginY = 0;

        // 动画启动由 LoadDefaultPet 控制（WZ 加载后或缓存命中后）

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

        // 随机聊天气泡定时器（30~90s，仅在 stand 时触发）
        InitBalloonTimer();

        _eventBus.Subscribe(EventType.UserDragStart, _ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var mobId = _animService.GetCurrentMobId();
                    if (mobId == PaperdollService.SentinelMobId)
                    {
                        var pa = GetCachedActionList(mobId);
                        var pdAction = pa.Contains("walk1") ? "walk1" : "stand1";
                        _animService.Play(pdAction, PlayMode.Loop);
                        return;
                    }
                    var actions = _wzService.IsLoaded
                        ? _wzService.GetActionList(mobId)
                        : GetCachedActionList(mobId);
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
                try
                {
                    if (IsSentinelActive()) _animService.Play("stand1", PlayMode.Loop);
                    else _animService.SwitchTo("stand");
                }
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
                _backgroundWindow.Close();
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
                    if (data is ValueTuple<string, string> pet)
                    {
                        _trayService.SetTooltip($"MiniPet - {pet.Item2} (ID:{pet.Item1})");
                        // 记录最近使用
                        RecordRecentItem(pet.Item1, pet.Item2, _currentEntityType);
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

        // 诊断：启动时用默认外观合成 stand1/0，打印逐部件/锚点信息
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 60; i++) { if (_wzService.IsLoaded) break; System.Threading.Thread.Sleep(500); }
                if (!_wzService.IsLoaded) { Console.WriteLine("[Paperdoll DIAG] WZ 未加载，跳过"); return; }
                var app = new CharacterAppearance { Gender = 0, Hair = new ItemInfo { Id = "30000" }, Face = new ItemInfo { Id = "20000" } };
                _paperdoll.LoadAppearance(app);
                var png = _paperdoll.CompositeFrame("stand1", 0);
                Console.WriteLine($"[Paperdoll DIAG] stand1/0 默认外观合成结果: {(png != null ? png.Length + " 字节" : "null")}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Paperdoll DIAG] {ex.Message}"); }
        });

        // 桌宠缩放：启动读 WindowScale，订阅配置变更实时跟随（设置中心拖动滑块即生效）
        try { var sc = _configService.LoadSettings().WindowScale; _scale = sc > 0 ? sc : 1.0; } catch { _scale = 1.0; }
        _configService.OnSettingsChanged += s => { try { _scale = s.WindowScale > 0 ? s.WindowScale : 1.0; } catch { } };
    }

    public void LoadDefaultPet(WzService? wzService)
    {
        try
        {
            var settings = _configService.LoadSettings();
            if (settings != null && !string.IsNullOrEmpty(settings.CurrentMobId))
            {
                var mobId = settings.CurrentMobId;
                // 从最近使用记录推断实体类型（Mob/Npc），否则默认 Mob
                var recentMatch = settings.RecentItems?.FirstOrDefault(r => r.Id == mobId);
                _currentEntityType = NormalizeEntityType(recentMatch?.Type);
                Console.WriteLine($"[PetWindow] 延迟加载默认宠物: {mobId} ({_currentEntityType})");
                if (_spriteService.PreloadMob(wzService, mobId, _currentEntityType))
                {
                    _animService.SetCurrentMobId(mobId);
                    StartAnimation();
                }
            }

            // 开机自动展示背景地图（穿透态）；clamping/归一化交给 SetMap/CropFull（F12）
            if (settings != null && !string.IsNullOrEmpty(settings.MapId) && _wzService.IsLoaded)
            {
                try
                {
                    var map = _mapService.LoadMap(settings.MapId);
                    if (map != null)
                    {
                        _backgroundWindow.SetMap(map, _mapService, settings.CropX, settings.CropY, settings.CropW, settings.CropH);
                        _backgroundWindow.Show();
                        RaisePet();
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AutoMap: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] LoadDefaultPet: {ex.Message}"); }
    }

    public void StartAnimation()
    {
        _animService.SetCurrentMobId(_configService.LoadSettings().CurrentMobId);
        _animService.Play("stand", PlayMode.Loop);
        _stopwatch.Start();
        _timer.Start();
        Console.WriteLine($"[PetWindow] StartAnimation: 动画已启动");
    }

    private void InitBalloonTimer()
    {
        try
        {
            _balloonTimer.Interval = TimeSpan.FromSeconds(_rng.Next(30, 91));
            _balloonTimer.Tick += (_, _) => TryShowRandomBalloon();
            _balloonTimer.Start();

            _balloonService.OnBalloonReady += (text, data, png) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (png == null) return;
                        using var ms = new MemoryStream(png);
                        var bmp = new Bitmap(ms);
                        await _balloonControl.ShowBalloon(text, data, bmp);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonShow: {ex.Message}"); }
                });
            };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] InitBalloonTimer: {ex.Message}"); }
    }

    private async void TryShowRandomBalloon()
    {
        try
        {
            _balloonTimer.Interval = TimeSpan.FromSeconds(_rng.Next(30, 91));
            if (_animService.GetCurrentAction() != "stand") return;
            var balloonId = _configService.LoadSettings().BalloonId;
            if (string.IsNullOrEmpty(balloonId)) balloonId = "560";
            var data = _balloonService.LoadBalloon(balloonId);
            if (data == null) return; // 缓存未就绪，后台种子中，下一轮再试
            var line = _randomLines[_rng.Next(_randomLines.Length)];
            await _balloonService.Show(line, data);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RandomBalloon: {ex.Message}"); }
    }

    public async Task ShowBalloonNow(string text)
    {
        try
        {
            var balloonId = _configService.LoadSettings().BalloonId;
            if (string.IsNullOrEmpty(balloonId)) balloonId = "560";
            var data = _balloonService.LoadBalloon(balloonId);
            if (data == null) return;
            await _balloonService.Show(text, data);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowBalloonNow: {ex.Message}"); }
    }

    /// <summary>
    /// 记录最近使用的宠物到配置文件
    /// </summary>
    private void RecordRecentItem(string id, string name, string type)
    {
        try
        {
            var settings = _configService.LoadSettings();
            if (settings.RecentItems == null)
                settings.RecentItems = new System.Collections.Generic.List<Models.RecentItem>();

            var normalizedType = NormalizeEntityType(type);
            // 移除旧的同名记录
            settings.RecentItems.RemoveAll(r => r.Id == id);
            // 插入到最前
            settings.RecentItems.Insert(0, new Models.RecentItem
            {
                Type = normalizedType,
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
                int fw = strip?.FrameWidth ?? 0;
                int fh = strip?.FrameHeight ?? 0;
                _spriteCanvas.SetFrame(pngData, ox, oy, _scale);

                // 窗口大小 = 帧像素 × 缩放；只在尺寸变化时 resize，避免每帧重布局卡顿
                int scaledW = fw > 0 ? (int)(fw * _scale) : 0;
                int scaledH = fh > 0 ? (int)(fh * _scale) : 0;
                if (scaledW > 0 && scaledH > 0 && (scaledW != _lastFrameW || scaledH != _lastFrameH))
                {
                    Width = scaledW;
                    Height = scaledH;
                    _lastFrameW = scaledW;
                    _lastFrameH = scaledH;
                }

                // 窗口位置补偿锚点偏移（锚点也乘缩放），使精灵图锚点对齐屏幕固定位置
                int oxScaled = (int)(ox * _scale);
                int oyScaled = (int)(oy * _scale);
                int dx = oxScaled - _lastOriginX;
                int dy = oyScaled - _lastOriginY;
                if (dx != 0 || dy != 0)
                {
                    Position = new PixelPoint(Position.X - dx, Position.Y - dy);
                }
                _lastOriginX = oxScaled;
                _lastOriginY = oyScaled;
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
                    var capturedType = NormalizeEntityType(recent.Type);
                    var recentItem = new MenuItem { Header = $"🕐 {capturedName} [{capturedId}]" };
                    recentItem.Click += (_, _) =>
                    {
                        try { SwitchToPet(capturedId, capturedName, capturedType); }
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
                            try { SwitchToPet(id, name, "Mob"); }
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
                            try { SwitchToPet(id, name, "Npc"); }
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

            // ── 切换背景（子菜单 Opening 时动态重建：开启/关闭 + 最近×4 + 调整地图）──
            _bgMenu = new MenuItem { Header = "切换背景" };
            cm.Items.Add(_bgMenu);

            // ── 背景交互模式（勾选态，Opening 刷新；无背景时禁用）──
            _bgInteractiveItem = new MenuItem { Header = "🎨 背景交互模式" };
            _bgInteractiveItem.Click += (_, _) => _backgroundWindow.SetInteractive(!_backgroundWindow.IsInteractive);
            cm.Items.Add(_bgInteractiveItem);

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
                    _backgroundWindow.Close();
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
            var isSentinel = mobId == PaperdollService.SentinelMobId;
            // 哨兵优先走缓存目录枚举；否则 WZ 可用走 WZ，不可用走缓存
            var actions = isSentinel
                ? GetCachedActionList(mobId)
                : (_wzService.IsLoaded ? _wzService.GetActionList(mobId, _currentEntityType) : GetCachedActionList(mobId));

            // 注：不在打开菜单时后台预热所有动作——逐帧 PNG 解码占满 CPU 会拖垮 UI（右键菜单卡顿主因）。
            // 动作切换时按需后台解码（PreloadAction 已支持后台加载 + 完成后切换）。

            foreach (var action in actions)
            {
                var capturedAction = action;
                var item = new MenuItem { Header = capturedAction };
                item.Click += (_, _) =>
                {
                    try
                    {
                        if (isSentinel)
                        {
                            _animService.Play(capturedAction, PlayMode.Loop);
                            return;
                        }
                        // 已缓存 → 立即切换；未缓存 → 后台加载（不卡 UI），完成后切换
                        if (_spriteService.GetCachedStrip(mobId, capturedAction).Item1 != null)
                        {
                            _animService.SwitchTo(capturedAction);
                            return;
                        }
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { _spriteService.PreloadAction(_wzService, mobId, capturedAction, _currentEntityType); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimPreload {capturedAction}: {ex.Message}"); }
                            Dispatcher.UIThread.Post(() =>
                            {
                                try { _animService.SwitchTo(capturedAction); }
                                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedAction}: {ex.Message}"); }
                            });
                        });
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedAction}: {ex.Message}"); }
                };
                animationMenu.Items.Add(item);
            }

            if (actions.Count == 0)
            {
                animationMenu.Items.Add(new MenuItem { Header = "无可用动画", IsEnabled = false });
            }

            // 刷新背景交互模式勾选态 + 可用性（无背景窗口时禁用）
            if (_bgInteractiveItem != null)
            {
                _bgInteractiveItem.IsChecked = _backgroundWindow.IsInteractive;
                _bgInteractiveItem.IsEnabled = _backgroundWindow.IsVisible;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshAnimationMenu: {ex.Message}"); }
    }

    /// <summary>
    /// 从磁盘缓存目录枚举 mob 的动作列表（WZ 不可用时的回退方案）
    /// </summary>
    private static List<string> GetCachedActionList(string mobId)
    {
        var result = new List<string>();
        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "MiniPet", "v1", "sprites", mobId);
            if (!Directory.Exists(cacheDir)) return result;

            foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
            {
                var action = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(action) || action == "config") continue;
                // 跳过无有效画面的空动作（FrameWidth<=1）
                if (IsBlankCachedAction(file, action)) continue;
                result.Add(action);
            }
            result.Sort(StringComparer.Ordinal);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] GetCachedActionList: {ex.Message}"); }
        return result;
    }

    /// <summary>读磁盘 config.json 判断该动作是否为空动作（FrameWidth<=1，无有效画面）。</summary>
    private static bool IsBlankCachedAction(string jsonPath, string action)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("sprites", out var sprites)
                && sprites.TryGetProperty(action, out var strip)
                && strip.TryGetProperty("frameWidth", out var fw)
                && fw.TryGetInt32(out var w))
                return w <= 1;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 应用纸娃娃到主窗口：后台构建+缓存哨兵精灵图条，完成后切到哨兵并循环播放 stand1。
    /// 代际 token 防止过期构建覆盖更新的选择。
    /// </summary>
    private void ApplyPaperdoll(CharacterAppearance appearance)
    {
        try
        {
            var gen = ++_paperdollGen;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var ok = _paperdoll.BuildAndCacheAppearance(appearance);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (gen != _paperdollGen) return; // 被更新的选择覆盖
                            if (!ok) return;
                            _animService.SetCurrentMobId(PaperdollService.SentinelMobId);
                            _animService.Play("stand1", PlayMode.Loop);
                            _trayService.SetTooltip("MiniPet - 纸娃娃");
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollApply: {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollBuild: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ApplyPaperdoll: {ex.Message}"); }
    }

    /// <summary>
    /// 切换到指定素材桌宠：stand 已缓存则立即切；未缓存则后台预加载，期间保持旧角色播放，加载完成后切换。
    /// 避免首次切换（WZ 构建/磁盘读取）在 UI 线程同步执行导致的明显卡顿。代际 token 防快速连点竞态。
    /// </summary>
    private void SwitchToPet(string id, string name, string type)
    {
        try
        {
            _currentEntityType = type;
            var gen = ++_petSwitchGen;
            if (_spriteService.GetCachedStrip(id, "stand").Item1 != null)
            {
                _animService.SetCurrentMobId(id);
                _animService.SwitchTo("stand");
            }
            else
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool ok;
                    try { ok = _spriteService.PreloadMob(_wzService, id, type); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetPreload: {ex.Message}"); ok = false; }
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (gen != _petSwitchGen) return; // 被更新的切换覆盖
                            if (!ok) return;
                            _animService.SetCurrentMobId(id);
                            _animService.SwitchTo("stand");
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetSwitch: {ex.Message}"); }
                    });
                });
            }
            _eventBus.Publish(EventType.PetChanged, (id, name));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SwitchToPet: {ex.Message}"); }
    }

    private bool IsSentinelActive() => _animService.GetCurrentMobId() == PaperdollService.SentinelMobId;

    /// <summary>把记录里的类型归一化成 WZ 目录名（Mob/Npc）。</summary>
    private static string NormalizeEntityType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return "Mob";
        return type.Equals("Npc", StringComparison.OrdinalIgnoreCase) ? "Npc" : "Mob";
    }

    /// <summary>把宠物窗口重新提到背景窗口之上（macOS 同级 Topmost z-order 脆弱的兜底）。</summary>
    private void RaisePet()
    {
        try { Topmost = false; Topmost = true; Activate(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RaisePet: {ex.Message}"); }
    }

    /// <summary>
    /// 把宠物图片中心点的最底部对齐到地图地面（foothold）：
    /// 横向居中于背景窗口，纵向让精灵图底部落在地面线上（foothold 缺失时落在背景窗口底部）。
    /// </summary>
    private void PlacePetOnMapGround(MapInfo map, int cropX, int cropY)
    {
        try
        {
            var bg = _backgroundWindow;
            int bgW = (int)bg.Width;
            int bgH = (int)bg.Height;
            int centerScreenX = bg.Position.X + bgW / 2;
            // crop 中心世界 x = cropX + bgW/2；窗口顶部对应世界 y = cropY
            int groundScreenY;
            int? gy = MapService.GetGroundY(map, cropX + bgW / 2);
            if (gy != null)
                groundScreenY = bg.Position.Y + (gy.Value - cropY);
            else
                groundScreenY = bg.Position.Y + bgH - 24;
            int fw = (int)Width;
            int fh = (int)Height;
            Position = new PixelPoint(centerScreenX - fw / 2, groundScreenY - fh);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PlacePetOnMapGround: {ex.Message}"); }
    }

    private void OpenBackgroundTab()
    {
        try { ShowSettingsWindow(); _settingsWindow?.OpenBackgroundTab(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenBackgroundTab: {ex.Message}"); }
    }

    /// <summary>开启/关闭背景 toggle：开着→关；关着→恢复上次 MapId+crop（后台 LoadMap；失败/无图→进设置中心背景 tab）。</summary>
    private void ToggleBackground()
    {
        try
        {
            if (_backgroundWindow.IsVisible)
            {
                _backgroundWindow.HideMap();
                _backgroundWindow.Hide();
                RaisePet();
                return;
            }
            var s = _configService.LoadSettings();
            if (string.IsNullOrEmpty(s.MapId) || !_wzService.IsLoaded)
            {
                OpenBackgroundTab();
                return;
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                MapInfo? map = null;
                try { map = _mapService.LoadMap(s.MapId); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ToggleBg LoadMap: {ex.Message}"); }
                Dispatcher.UIThread.Post(() =>
                {
                    if (map == null) { OpenBackgroundTab(); return; }
                    _backgroundWindow.SetMap(map, _mapService, s.CropX, s.CropY, s.CropW, s.CropH);
                    _backgroundWindow.Show();
                    RaisePet();
                });
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ToggleBackground: {ex.Message}"); }
    }

    /// <summary>Opening 时重建「切换背景」子菜单：开启/关闭 + 最近使用×4（空→占位）+ 调整地图。</summary>
    private void RebuildBgMenu()
    {
        if (_bgMenu == null) return;
        try
        {
            _bgMenu.Items.Clear();

            var toggle = new MenuItem { Header = _backgroundWindow.IsVisible ? "🚫 关闭背景" : "🌙 开启背景" };
            toggle.Click += (_, _) => ToggleBackground();
            _bgMenu.Items.Add(toggle);
            _bgMenu.Items.Add(new Separator());

            var s = _configService.LoadSettings();
            var recents = (s.MapHistory ?? new System.Collections.Generic.List<MapHistoryEntry>()).Take(4).ToList();
            if (recents.Count == 0)
            {
                _bgMenu.Items.Add(new MenuItem { Header = "（暂无最近）", IsEnabled = false });
            }
            else
            {
                foreach (var entry in recents)
                {
                    var captured = entry;
                    var name = !string.IsNullOrEmpty(entry.Name) ? entry.Name
                        : (_wzService.IsLoaded ? _wzService.GetMapName(entry.Id) : entry.Id);
                    var item = new MenuItem { Header = $"🗺️ {name} [{entry.Id}]" };
                    item.Click += (_, _) => ApplyMapSelection(new MapSelection(captured.Id, name, captured.CropX, captured.CropY, captured.CropW, captured.CropH));
                    _bgMenu.Items.Add(item);
                }
            }
            _bgMenu.Items.Add(new Separator());

            var adjust = new MenuItem { Header = "🛓 调整地图…" };
            adjust.Click += (_, _) => OpenBackgroundTab();
            _bgMenu.Items.Add(adjust);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RebuildBgMenu: {ex.Message}"); }
    }

    private void ApplyMapSelection(MapSelection sel)
    {
        try
        {
            var map = _mapService.LoadMap(sel.Id);
            if (map == null) return;
            _backgroundWindow.SetMap(map, _mapService, sel.CropX, sel.CropY, sel.CropW, sel.CropH);
            _backgroundWindow.Show();
            RaisePet();
            PlacePetOnMapGround(map, sel.CropX, sel.CropY);
            var s = _configService.LoadSettings();
            s.MapId = sel.Id;
            s.CropX = sel.CropX; s.CropY = sel.CropY; s.CropW = sel.CropW; s.CropH = sel.CropH;
            s.MapHistory.RemoveAll(x => x.Id == sel.Id);
            s.MapHistory.Insert(0, new MapHistoryEntry { Id = sel.Id, Name = sel.Name, CropX = sel.CropX, CropY = sel.CropY, CropW = sel.CropW, CropH = sel.CropH });
            if (s.MapHistory.Count > 10) s.MapHistory = s.MapHistory.Take(10).ToList();
            _configService.SaveSettings(s);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ApplyMapSelection: {ex.Message}"); }
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

            _settingsWindow = new SettingsWindow(_configService, _wzService, _paperdoll, _mapService);
            _settingsWindow.OnPetApplied += (id, name) =>
            {
                try { SwitchToPet(id, name, "Mob"); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnPetApplied: {ex.Message}"); }
            };
            _settingsWindow.OnPaperdollApplied += appearance => ApplyPaperdoll(appearance);
            _settingsWindow.OnBackgroundMapApplied += sel => Dispatcher.UIThread.Post(() => ApplyMapSelection(sel));
            _settingsWindow.Show();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowSettingsWindow: {ex.Message}"); }
    }
}
