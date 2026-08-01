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

public partial class PetWindow : Window
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
    private readonly MapViewportWindow _mapViewportWindow;
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
    private bool _isExiting;
    private Models.MapInfo? _currentMap;          // #6：渲染完成回调落地时用
    private bool _placePetAfterRender;            // #6：ApplyMapSelection 后等渲染完成才落地
    private int _lastOriginX;
    private int _lastOriginY;
    private double _lastFrameW;
    private double _lastFrameH;
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
        // #6：渲染（裁紧）完成后再落地——同步时 EffectiveCrop* 尚未就绪。
        _backgroundWindow.MapRendered += () => Dispatcher.UIThread.Post(OnMapRendered);

        // 模式2 摄像机窗口：RequestHide → 切回静态模式（与「背景模式」菜单一致）；RequestSwitchMap → 进设置中心。
        _mapViewportWindow = new MapViewportWindow();
        _mapViewportWindow.RequestHide += () => Dispatcher.UIThread.Post(() => SwitchToStaticMode());
        _mapViewportWindow.RequestSwitchMap += () => Dispatcher.UIThread.Post(() => OpenBackgroundTab());

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
                _mapViewportWindow.Close();
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

            // 开机预载地图数据（不自动显示背景——背景是手动开关，静态/摄像机分类）
            // 用户点「开启背景」才按 MapMode 显示对应分类；避免"没点开启背景却出现摄像机"
            if (settings != null && !string.IsNullOrEmpty(settings.MapId) && _wzService.IsLoaded)
            {
                try
                {
                    var map = _mapService.LoadMap(settings.MapId);
                    if (map != null)
                    {
                        _currentMap = map;
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PreloadMap: {ex.Message}"); }
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

                // 窗口大小 = 帧像素 × 缩放；只在尺寸变化时 resize，用 double 避免 int 截断抖动
                double scaledW = fw > 0 ? fw * _scale : 0;
                double scaledH = fh > 0 ? fh * _scale : 0;
                if (scaledW > 0 && scaledH > 0 && (Math.Abs(scaledW - _lastFrameW) > 0.5 || Math.Abs(scaledH - _lastFrameH) > 0.5))
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

    /// <summary>激活桌宠窗口焦点（背景窗口已降级 Topmost=false，PetWindow Topmost=true 天然在上，无需翻转）。</summary>
    private void RaisePet()
    {
        try { Activate(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RaisePet: {ex.Message}"); }
    }

    /// <summary>
    /// 把宠物图片中心点的最底部对齐到地图地面（foothold）：
    /// 横向居中于背景窗口，纵向让精灵图底部落在地面线上（foothold 缺失时落在背景窗口底部）。
    /// </summary>
    /// <summary>#6：渲染（裁紧透明 padding）完成回调——EffectiveCrop* 此刻才就绪，此刻落地才精准。</summary>
    private void ShowSettingsWindow()
    {
        try
        {
            // 复用单例：SettingsWindow.OnClosing 把关闭改成 Hide()，所以 IsVisible=false 时是"隐藏未销毁"。
            // 此前每次重开都 new 一个新实例 → 旧窗口泄露（_config.OnSettingsChanged 订阅/事件处理器不释放，逐次堆积）。
            // 修复：实例只在 _settingsWindow==null 时创建；已可见→Activate，已隐藏→Show 复用。
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_configService, _wzService, _paperdoll, _mapService);
                _settingsWindow.OnPetApplied += (id, name) =>
                {
                    try { SwitchToPet(id, name, "Mob"); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnPetApplied: {ex.Message}"); }
                };
                _settingsWindow.OnPaperdollApplied += appearance => ApplyPaperdoll(appearance);
                _settingsWindow.OnBackgroundMapApplied += sel => Dispatcher.UIThread.Post(() => ApplyMapSelection(sel));
                _settingsWindow.OnLayerTogglesChanged += () => Dispatcher.UIThread.Post(ReRenderBackground);
            }
            if (_settingsWindow.IsVisible)
                _settingsWindow.Activate();
            else
                _settingsWindow.Show();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowSettingsWindow: {ex.Message}"); }
    }
}
