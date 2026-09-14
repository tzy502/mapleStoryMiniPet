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
    private readonly PetManager _petManager;          // 只用于菜单读取宠物总数（增删/主宠仍走 EventBus 命令事件）
    private readonly PetInstanceConfig _cfg;          // V0.1.3：本窗口对应的宠物实例配置（身份来源）
    private readonly string _petId;
    private bool _isPrimaryRole;                      // 主宠：挂地图背景 + 响应 Agent 特效
    private Action<PetSettings>? _settingsChangedHandler;   // 命名订阅（可在 OnClosed 退订）
    // I1：气泡回调也改命名订阅（BalloonService 是 Singleton，匿名 lambda 无法退订 → 移除宠物时整棵窗口图泄漏）
    private Action<string, BalloonData, byte[], int?, string?>? _balloonReadyHandler;
    private readonly WzService _wzService;
    private readonly AnimService _animService;
    private readonly SpriteService _spriteService;
    private readonly TrayService _trayService;
    private readonly EventBus _eventBus;
    private readonly SpriteCanvas _spriteCanvas;
    private readonly BalloonWindow _balloonWindow;
    private readonly BalloonService _balloonService;
    private readonly PaperdollService _paperdoll;
    private readonly MapService _mapService;
    private readonly CacheManager _cacheManager;
    // 音乐模块 V0.2.0（F15-F17）：目录服务（透传给 SettingsWindow 音乐 tab）+ 播放编排服务（气泡/菜单/场景联动挂钩）
    private readonly MusicCatalogService _musicCatalog;
    private readonly MusicPlayerService _musicService;
    private readonly BackgroundWindow _backgroundWindow;
    private readonly MapViewportWindow _mapViewportWindow;
    private readonly DispatcherTimer _balloonTimer;
    private readonly Random _rng = new();
    private bool _balloonSilentRound;   // 随机气泡静默轮（胶水 2026-08-17：空窗期，本轮不弹）
    private AgentState _agentReflect = AgentState.Idle;   // 最近一次 Agent 反映态（PetReflectStateChanged 订阅，随机气泡仅在 Idle 触发）
    private int _paperdollGen;
    private int _petSwitchGen;
    /// <summary>切换素材 loading 的超时兜底计时器（后台 PreloadMob 卡住时复位 UI，见 SwitchToPet）。</summary>
    private DispatcherTimer? _petSwitchTimeout;
    /// <summary>切换素材 loading 超时阈值（ms）。</summary>
    private const int PetSwitchTimeoutMs = 10000;
    private static readonly string[] _randomLines = {
        "今天天气真好~", "加油！", "记得喝水哦", "嘿嘿~", "我在看着你呢",
        "累了就休息一下吧", "你今天也很努力呢", "要一起冒险吗？",
        "悄悄告诉你，我其实会飞哦~", "要不要一起去打怪？", "我想吃鲷鱼烧了…",
        "你认真工作的样子真帅！", "再玩十分钟就去睡觉！", "我在这里陪着你呀",
        "好想出去冒险啊！", "摸摸我的头好不好？", "今天也要元气满满哦！",
        "嘘——别告诉别人我会说话", "累了就靠在我身上休息吧", "我们是最好的搭档！",
        "你有没有好好吃饭呀？", "窗外的云看起来好软~", "猜猜我现在在想什么？",
        "一天不见，想我了吗？", "给你一个么么哒~", "发呆的时候也在想你哦",
        "要不要听我讲个笑话？", "好喜欢和你一起的时光", "肚子饿了，喂我吃点啥呗",
        "你笑起来真好看！", "保持好心情，好运自然来~"
    };
    private readonly TextBlock _infoText;
    private ProgressBar? _loadingBar;
    private TextBlock? _loadingText;
    private Point _dragStart;
    private bool _isDragging;
    private bool _dragCandidate;      // 点击候选拖拽（位移超阈值才转正；点击不破坏 origin 补偿基准）
    private Point _pressPoint;        // 按下位置（候选拖拽判定用）
    private PixelPoint _windowStartPos;
    private int _dragEndAnchorX;      // 拖拽结束时的角色锚点屏幕位置（释放后第一帧绝对定位用）
    private int _dragEndAnchorY;
    private bool _pendingOriginSync;  // 启动/切宠物首帧：只同步 origin 基准不移动窗口（避免基准 0 首帧大跳）
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch;
    private double _lastElapsed;
    private bool _isExiting;
    private Models.MapInfo? _currentMap;          // #6：渲染完成回调落地时用
    private bool _placePetAfterRender;            // #6：ApplyMapSelection 后等渲染完成才落地
    private int _lastOriginX;
    private int _lastOriginY;
    private bool _syncOriginOnNextFrame;
    private double _lastFrameW;
    private double _lastFrameH;
    private double _scale = 1.0;
    private string _currentEntityType = "Mob";
    // 气泡锚点基准（胶水规则 2026-08-08）：按动作优先级从前到后第一个存在动作计算气泡位置，
    // 不随每个动画重新计算 → 动画切换时气泡位置稳定。
    private static readonly string[] BalloonAnchorPriority = { "stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "move", "die" };
    private string? _anchorActionName;   // 已解析的锚点基准动作名
    private string? _anchorMobId;        // 锚点对应的 mobId（变了才重算）
    private double _anchorFrameW;        // 基准动作帧宽（物理像素，未乘 scale）
    private double _anchorFrameH;        // 基准动作帧高（物理像素，未乘 scale）
    private double _balloonAnchorX;      // 气泡锚点基准 X（屏幕像素，拖拽结束更新 → 动画切换气泡不动）
    private double _balloonAnchorY;      // 气泡锚点基准 Y（屏幕像素）
    private bool _balloonWasVisible;     // 拖拽前气泡是否可见（移动时隐藏，释放后恢复）
    private bool _balloonWasVisibleBeforeHide;  // 隐藏到托盘前气泡是否可见（恢复时跟随显示，Y7）
    // A15：已登记的 EventBus 订阅（窗口销毁时统一退订——匿名 lambda 无法单独退订）
    private readonly List<(EventType Type, Action<object?> Handler)> _eventSubscriptions = new();
    // 音乐模块 V0.2.1：pet:moved 发布节流状态——上次已发布的窗口位置 + 上次发布时间戳（Environment.TickCount64，10Hz gate）
    private PixelPoint? _petMovedLastSent;
    private long _petMovedLastMs;

    public PetWindow(ConfigService configService, WzService wzService, AnimService animService, SpriteService spriteService, TrayService trayService, EventBus eventBus, BalloonService balloonService, PaperdollService paperdoll, MapService mapService, CacheManager cacheManager, MusicCatalogService musicCatalog, MusicPlayerService musicService, PetInstanceConfig cfg, PetManager petManager)

    {
        _configService = configService;
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _petId = string.IsNullOrEmpty(cfg.Id) ? "pet-1" : cfg.Id;
        _petManager = petManager;
        _wzService = wzService;
        _animService = animService;
        _spriteService = spriteService;
        _trayService = trayService;
        _eventBus = eventBus;
        _balloonService = balloonService;
        _paperdoll = paperdoll;
        _animService.RegisterFrameProvider(paperdoll); // 纸娃娃实时合成（ADR-0005）；Mob/NPC 走图条
        // V0.1.3：宠物身份来自本窗口的实例配置（不再读全局单值字段——多只宠物各自身份独立）
        _currentEntityType = (cfg.MobId == PaperdollService.SentinelMobId
            || string.Equals(cfg.EntityType, "player", StringComparison.OrdinalIgnoreCase))
            ? "player"
            : NormalizeEntityType(cfg.EntityType);
        _mapService = mapService;
        _cacheManager = cacheManager;
        _musicCatalog = musicCatalog;
        _musicService = musicService;
        _backgroundWindow = new BackgroundWindow();
        _backgroundWindow.RequestHide += () => Dispatcher.UIThread.Post(() => { _backgroundWindow.HideMap(); _backgroundWindow.Hide(); RaisePet(); });
        _backgroundWindow.RequestSwitchMap += () => Dispatcher.UIThread.Post(() => OpenBackgroundTab());
        // #4：背景窗口交互模式 makeKeyAndOrderFront 改变 z-order 后，重新把宠物窗口提至最前
        _backgroundWindow.InteractiveChanged += () => Dispatcher.UIThread.Post(() => RaisePet());
        // #6：渲染（裁紧）完成后再落地——同步时 EffectiveCrop* 尚未就绪。
        _backgroundWindow.MapRendered += () => Dispatcher.UIThread.Post(OnMapRendered);
        // 静态图渲染完成 → 落盘 PNG 缓存（无 WZ 时背景可离线显示）
        // M6：追加裁紧后世界原点（trimWorldX/Y）随 sidecar 持久化——离线恢复时 EffectiveCrop 对齐裁紧图左上角，宠物落地不偏移
        _backgroundWindow.OnMapRenderCached = (png, cx, cy, cw, ch, trimWorldX, trimWorldY) =>
        {
            try
            {
                var s = _configService.LoadSettings();
                // V0.1.3：用本窗口实际渲染的地图 id（旧版用全局 settings.MapId —— 多宠物不同地图会串缓存）
                var mapId = _currentMap?.Id;
                if (string.IsNullOrEmpty(mapId)) { mapId = s.MapId; }
                if (!string.IsNullOrEmpty(mapId))
                    _cacheManager.SaveMapPng(mapId, cx, cy, cw, ch, png, trimWorldX, trimWorldY);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CacheMapPng: {ex.Message}"); }
        };

        // 模式2 摄像机窗口：RequestHide → 切回静态模式（与「背景模式」菜单一致）；RequestSwitchMap → 进设置中心。
        _mapViewportWindow = new MapViewportWindow();
        var vpSettings = _configService.LoadSettings();
        _mapViewportWindow.SetViewportSize(vpSettings.ViewportW, vpSettings.ViewportH);
        _mapViewportWindow.RequestHide += () => Dispatcher.UIThread.Post(() => SwitchToStaticMode());
        _mapViewportWindow.RequestSwitchMap += () => Dispatcher.UIThread.Post(() => OpenBackgroundTab());

        Title = "MiniPet";
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 64;
        MinHeight = 64;
        Topmost = true;
        // 2026-08-17 用户要求：桌宠不进任务栏（Windows），仅托盘展示；设置中心正常显示
        ShowInTaskbar = false;
        // V0.1.3 备注：这里**不能**设 ShowActivated=false —— 实测会让桌宠窗口收不到右键/拖拽
        // （非 key window 不弹 ContextMenu，胶水反馈"菜单点不出来"）。抢焦点的真正来源是气泡窗口
        // 与子窗口的 Show，已在 BalloonWindow 里单独处理。
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var grid = new Grid { Background = Brushes.Transparent, ClipToBounds = false };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Content = grid;

        // 切换桌宠 loading（进度条 + 文本，覆盖动画区；切宠物未缓存时显示。Avalonia 12.1 无 ProgressRing，用 indeterminate ProgressBar）
        _loadingBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 120,
            Height = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsVisible = false
        };
        _loadingText = new TextBlock
        {
            Text = "切换中…",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 34, 0, 0),
            IsVisible = false
        };
        Grid.SetRow(_loadingBar, 1);
        Grid.SetRow(_loadingText, 1);
        grid.Children.Add(_loadingBar);
        grid.Children.Add(_loadingText);

        _infoText = new TextBlock
        {
            Text = "MiniPet",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4),
            // 固定宽度：状态文字不随内容变宽 → 不撑大窗口（窗口宽度 = max(精灵宽, 此宽)）
            Width = 100,
            Height = 20,
        };
        Grid.SetRow(_infoText, 3);  // 状态文字在窗口底部（胶水规则：气泡在顶部，状态显示在底部，两者不混淆）
        grid.Children.Add(_infoText);

        // 精灵画布（含 origin 对齐）
        _spriteCanvas = new SpriteCanvas();
        Grid.SetRow(_spriteCanvas, 1);
        grid.Children.Add(_spriteCanvas);

        // 聊天气泡独立窗口（macOS 窗口顶部不能超过菜单栏 → 主窗口不含 headRoom 才能拖到顶；
        // 气泡窗口独立跟随固定锚点，动画切换位置不动）
        _balloonWindow = new BalloonWindow();
        ContextMenu = BuildContextMenu();
        ContextMenu.Opening += (_, _) => { RefreshAnimationMenu(); RefreshPaperdollMenuItem(); RebuildBgMenu(); RefreshMiniBarMenuItem(); RefreshPetMenu(); };

        SetupDragEvents();

        // 帧循环：30fps 轮询（预裁剪帧缓存 + 追帧，多帧动画顺滑）
        _stopwatch = new Stopwatch();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        _balloonTimer = new DispatcherTimer();
        _animService.EnableRandomIdle(true);
        _lastOriginX = 0;
        _lastOriginY = 0;
        // 启动首帧：基准尚未初始化（0），直接补偿会从 (0,0) 跳到真实 origin → 窗口首帧大跳。
        // 首帧只同步基准不移动窗口（_pendingOriginSync 在 OnTimerTick 消费）。
        _pendingOriginSync = true;

        // 动画启动由 LoadDefaultPet 控制（WZ 加载后或缓存命中后）

        // 托盘集成（V0.2.1 音乐：先注入音乐服务再建托盘，托盘菜单首建即带音乐快捷分组；
        // V0.1.3 多桌宠：登记为「一只」，显示 / 隐藏 / 居中作用于全部桌宠）
        try
        {
            _trayService.SetMusicService(_musicService);
            _trayService.RegisterPet(this);
            _trayService.ShowTrayIcon();

            _trayService.ShowTrayIcon();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetWindow] TrayInit: {ex.Message}");
        }

        // 随机聊天气泡定时器（30~90s，仅在 stand 时触发）
        InitBalloonTimer();

        SubscribeBus(EventType.UserDragStart, data =>
        {
            // V0.1.3 多桌宠定向：拖谁谁动，其他桌宠不受影响（旧版无源广播会让所有宠物一起走）
            if (data is PetDragEvent d0 && d0.PetId != _petId) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var mobId = _animService.GetCurrentMobId();
                    if (mobId == PaperdollService.SentinelMobId)
                    {
                        // 拖拽默认 walk1/walk2/fly（用户要求；只从 MapleSalon2 动作集里选）
                        var pa = _paperdoll.GetAvailableActions();
                        var candidates = pa.Where(a => a.StartsWith("walk") || a.StartsWith("fly")).ToList();
                        var pdAction = candidates.Count > 0 ? candidates[_rng.Next(candidates.Count)] : (pa.Contains("walk1") ? "walk1" : "stand1");
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
        SubscribeBus(EventType.UserDragEnd, data =>
        {
            if (data is PetDragEvent d1 && d1.PetId != _petId) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (IsSentinelActive()) { _animService.Play("stand1", PlayMode.Loop); }
                    else _animService.SwitchTo("stand");
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] DragEnd anim: {ex.Message}"); }
            });
        });

        SubscribeBus(EventType.AppExit, _ =>
        {
            try
            {
                _isExiting = true;
                _trayService.HideTrayIcon();
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

        SubscribeBus(EventType.ConfigChanged, data =>
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

        SubscribeBus(EventType.PetChanged, data =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // V0.1.3 定向：只有发起切换的那只更新托盘提示 / 最近使用（旧版所有宠物都会写一遍）
                    if (data is not PetChangedEvent pet || pet.PetId != _petId) { return; }
                    if (_isPrimaryRole)
                    {
                        _trayService.SetTooltip($"MiniPet - {pet.Name} (ID:{pet.MobId})");
                    }
                    // 记录最近使用
                    RecordRecentItem(pet.MobId, pet.Name, _currentEntityType);
                    // 切宠物：旧宠物 origin 基准不适用 → 首帧只同步不补偿（避免切换瞬间窗口大跳）
                    _pendingOriginSync = true;
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetChanged: {ex.Message}"); }
            });
        });

        SubscribeBus(EventType.OpenSettings, data =>
        {
            // V0.1.3 定向：托盘入口（PetId 空）= 主宠；右键菜单入口带自己的 id。
            // 旧版无源广播会让 N 只宠物各弹一个设置窗。
            var target = (data as OpenSettingsEvent)?.PetId;
            var isMine = string.IsNullOrEmpty(target) ? _isPrimaryRole : target == _petId;
            if (!isMine) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenSettings: {ex.Message}"); }
            });
        });


        // V0.1.3 定向切实体：设置中心「📦 桌宠设置」点「确定应用」时只有目标宠物执行
        SubscribeBus(EventType.EntityApplied, data =>
        {
            if (data is not PetEntityAppliedEvent ev) { return; }
            if (ev.PetId != _petId) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                try { SwitchToPet(ev.MobId, ev.Name, ev.EntityType); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] EntityApplied({_petId}): {ex.Message}"); }
            });
        });

        // V0.1.3 定向换装：设置中心点「应用到桌宠」时只有目标宠物执行（按 petId 过滤）
        SubscribeBus(EventType.PaperdollApplied, data =>
        {
            if (data is not PetPaperdollEvent ev || ev.Appearance == null) { return; }
            if (ev.PetId != _petId) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                try { ApplyPaperdoll(ev.Appearance); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollApplied({_petId}): {ex.Message}"); }
            });
        });

        // 桌宠缩放：本宠物自己的 WindowScale（V0.1.3 per-pet；旧版为全局单值，所有宠物共用）
        _scale = cfg.WindowScale > 0 ? cfg.WindowScale : 1.0;
        _settingsChangedHandler = OnSettingsChangedHandler;
        _configService.OnSettingsChanged += _settingsChangedHandler;
    }

    /// <summary>配置变更回调：只跟随本宠物自己的缩放（窗口销毁时在 OnClosed 退订）。</summary>
    private void OnSettingsChangedHandler(PetSettings? s)
    {
        try
        {
            var mine = s?.Pets?.FirstOrDefault(p => p.Id == _petId);
            var sc = mine?.WindowScale ?? 0;
            _scale = sc > 0 ? sc : 1.0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnSettingsChanged({_petId}): {ex.Message}"); }

// 音乐模块 V0.2.0（设计 §11.8）：换曲气泡——CurrentChanged 即换曲事件（用户点播/自动续播/场景联动），
        // 可能从后台线程触发，经 Dispatcher.UIThread.Post 切回 UI 线程后走现有气泡链路（ShowBalloonNow）弹
        // 「🎵 曲名」；track==null 忽略；启动恢复是静默预选不发 CurrentChanged（不出启动气泡，PLAN 步骤 7）。
        _musicService.CurrentChanged += track =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (track == null) { return; }
                    _ = ShowBalloonNow($"🎵 {track.Track}");
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MusicBalloon: {ex.Message}"); }
            });
        };

    }

    /// <summary>EventBus 订阅登记（A15：窗口销毁时统一退订；匿名 lambda 无法单独退订）。</summary>
    private void SubscribeBus(EventType eventType, Action<object?> handler)
    {
        _eventSubscriptions.Add((eventType, handler));
        _eventBus.Subscribe(eventType, handler);
    }

    /// <summary>退订全部已登记 EventBus 订阅（A15，OnClosed 调用）。</summary>
    private void UnsubscribeBusAll()
    {
        foreach (var (type, handler) in _eventSubscriptions)
        {
            try { _eventBus.Unsubscribe(type, handler); }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] Unsubscribe {type}: {ex.Message}"); }
        }
        _eventSubscriptions.Clear();
    }

    // ───────────────── V0.1.3 多桌宠：身份 / 主宠角色 / 生命周期 ─────────────────

    /// <summary>本窗口对应的宠物实例 id（多桌宠身份；事件定向、配置读写都用它）。</summary>
    public string PetId => _petId;

    /// <summary>是否主宠（挂地图背景 + 响应 Agent 状态特效）。</summary>
    public bool IsPrimaryRole => _isPrimaryRole;

    /// <summary>本窗口的动画服务（每只宠物一份）；主宠时被 EffectLayerService 作为 Agent 特效驱动。</summary>
    public AnimService Animation => _animService;

    /// <summary>
    /// 主宠角色派发（由 PetManager 调用）：失去主宠身份时立即收起地图背景——
    /// 背景窗口是「全局单份」语义（同一张地图 + 全局层开关），多只同时挂会互相遮挡。
    /// </summary>
    internal void SetPrimaryRole(bool isPrimary)
    {
        try
        {
            _isPrimaryRole = isPrimary;
            if (!isPrimary)
            {
                if (_backgroundWindow.IsVisible)
                {
                    _backgroundWindow.HideMap();
                    _backgroundWindow.Hide();
                }
                if (_mapViewportWindow.IsVisible)
                {
                    _mapViewportWindow.Hide();
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SetPrimaryRole({_petId}): {ex.Message}"); }
    }

    /// <summary>设置中心「背景」页应用地图（设置中心全局唯一 → 背景落到主宠；背景是全局单份语义）。</summary>
    internal void ApplyMapSelectionFromSettings(MapSelection sel) => ApplyMapSelection(sel);

    /// <summary>设置中心镜头大小应用（落到主宠的摄像机窗口）。</summary>
    internal void ApplyViewportSizeFromSettings(int w, int h) => _mapViewportWindow.SetViewportSize(w, h);

    /// <summary>设置中心层开关变化后，主宠重渲染背景。</summary>
    internal void ReRenderBackgroundFromSettings() => ReRenderBackground();

    /// <summary>启动位置（PetManager 摆放用）：转 Manual 避免 WindowStartupLocation 居中覆盖。</summary>
    internal void SetStartupPosition(PixelPoint p)
    {
        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = p;
            _windowStartPos = p;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SetStartupPosition({_petId}): {ex.Message}"); }
    }

    /// <summary>真正关闭本窗口（「移除这只桌宠」）：不走「关闭 = 隐藏到托盘」。</summary>
    internal void CloseForRemoval()
    {
        try
        {
            _isExiting = true;   // 让 OnClosing 不取消关闭（区别于「关闭 = 隐藏到托盘」）
            // I1：Close() 不会把 IsVisible 置 false（Avalonia 12.1：Close→PlatformImpl.Dispose），
            // OnPetHidden 不会触发 → 必须在这里显式停帧循环/气泡调度，否则被移除的桌宠 30fps 空转到进程结束。
            try { _timer.Stop(); } catch { }
            try { _balloonTimer.Stop(); } catch { }
            try { _stopwatch.Stop(); } catch { }
            // I1：气泡是 Singleton 的多播事件，被移除窗口不退订会连带整棵窗口对象图无法回收
            try { if (_balloonReadyHandler != null) { _balloonService.OnBalloonReady -= _balloonReadyHandler; _balloonReadyHandler = null; } } catch { }
            try { _backgroundWindow.Close(); } catch { }
            try { _mapViewportWindow.Close(); } catch { }
            try { _balloonWindow.Close(); } catch { }
            Close();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CloseForRemoval({_petId}): {ex.Message}"); }
    }

    public void LoadDefaultPet(WzService? wzService)
    {
        try
        {
            var settings = _configService.LoadSettings();
            // V0.1.3：只恢复「本窗口这只」的实例配置（旧版读全局单值 CurrentMobId → 多只会全变成同一只）
            SyncCfgFromSettings(settings);
            var mobId = _cfg.MobId;
            if (settings != null && !string.IsNullOrEmpty(mobId))
            {
                if (mobId == PaperdollService.SentinelMobId)
                {
                    // 纸娃娃恢复：上次实体为纸娃娃 → 恢复搭配（WZ 断线时 AnimService 回退离线图条）
                    var pa = _cfg.Paperdoll;
                    if (pa != null && _wzService.IsLoaded)
                    {
                        _paperdoll.SetAppearance(pa);
                        Console.WriteLine($"[PetWindow] 恢复纸娃娃: hair={pa.Hair?.Id} face={pa.Face?.Id} ear={pa.Ear} body={pa.BodyId} coat={pa.Coat?.Id} cap={pa.Cap?.Id}");
                    }
                    else
                    {
                        Console.WriteLine($"[PetWindow] 纸娃娃恢复跳过: CurrentPaperdoll={(pa == null ? "无" : "有")} WZ加载={_wzService.IsLoaded}");
                    }
                    _currentEntityType = "player";
                    _animService.SetCurrentMobId(PaperdollService.SentinelMobId);
                    StartAnimation();
                    // 后台预热常用动作缓存（避免恢复后首帧合成卡顿抖动）
                    System.Threading.Tasks.Task.Run(() => { try { _paperdoll.Warmup(); } catch { } });
                }
                else
                {
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

    /// <summary>
    /// 空闲随机节奏（V0.1.3，胶水反馈「stand 12 的切换不要那么频繁」）：
    /// 纸娃娃动作丰富但站立变体外观相近，切换太密像抖动 → 25~60s；Mob/Npc 保持 8~15s 的活泼节奏。
    /// </summary>
    private void ApplyIdlePolicy()
    {
        try
        {
            if (IsSentinelActive())
            {
                _animService.SetIdleTimeout(25000, 60000);
            }
            else
            {
                _animService.SetIdleTimeout(8000, 15000);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ApplyIdlePolicy({_petId}): {ex.Message}"); }
    }

    public void StartAnimation()
    {
        ApplyIdlePolicy();
        // V0.1.3：用本宠物自己的实体 id（旧版读全局 CurrentMobId —— 多只宠物时会把刚设好的实体又覆盖回去）
        if (!string.IsNullOrEmpty(_cfg.MobId))
        {
            _animService.SetCurrentMobId(_cfg.MobId);
        }
        _animService.Play("stand", PlayMode.Loop);
        _stopwatch.Start();
        _timer.Start();
        Console.WriteLine($"[PetWindow] StartAnimation({_petId}): 动画已启动 ({_cfg.MobId})");
    }

    /// <summary>把配置里属于本宠物的字段同步进内存快照 <see cref="_cfg"/>（启动 / 外部改配置后调用）。</summary>
    private void SyncCfgFromSettings(PetSettings? settings)
    {
        try
        {
            var mine = settings?.Pets?.FirstOrDefault(p => p.Id == _petId);
            if (mine == null) { return; }
            _cfg.EntityType = mine.EntityType;
            _cfg.MobId = mine.MobId;
            _cfg.Paperdoll = mine.Paperdoll;
            _cfg.WindowScale = mine.WindowScale;
            _cfg.Primary = mine.Primary;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SyncCfg({_petId}): {ex.Message}"); }
    }

    private void InitBalloonTimer()
    {
        try
        {
            // 随机气泡（2026-08-15 恢复）：仅 Agent 空闲（Idle）时触发，随机 3~8s 间隔；
            // Agent 执行任务时（Run/Review/Wave 等）状态气泡优先，不打扰。
            _balloonTimer.Tick += (_, _) => TryShowRandomBalloon();
            _balloonTimer.Start();
            ScheduleRandomBalloon();

            // Agent 反映态订阅：非 Idle 时抑制随机气泡（状态气泡为主）。
            // V0.1.3：Agent 特效只作用于主宠 → 非主宠不跟随全局反映态，保持自己的随机气泡节奏。
            SubscribeBus(EventType.PetReflectStateChanged, data =>
            {
                if (!_isPrimaryRole) { return; }
                if (data is AgentState s)
                {
                    _agentReflect = s;
                }
            });

            _balloonReadyHandler = (text, data, png, durationMs, targetPetId) =>
            {
                // V0.1.3 多桌宠定向：指定目标宠物时只有它显示（null = 广播给所有桌宠）
                if (targetPetId != null && targetPetId != _petId) { return; }
                // I1：本窗口已移除/退出（气泡窗口已 Close，Show 会抛「Cannot re-show a closed window」）
                // → 丢弃在途回调。移除瞬间可能已有一笔气泡在合成中，这里兜住。
                if (_isExiting) { return; }
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (png == null) { return; }
                        // Y8：拖拽期间不弹新气泡——拖拽中窗口移动但锚点基准不更新，此时显示的气泡
                        // 会停在旧位置漂移成「幽灵」；释放后下一轮定时器/状态变化再触发。
                        // 同理：窗口已隐藏到托盘（在途气泡回调晚到）也不显示，防隐藏后幽灵气泡。
                        if (_isDragging || !IsVisible) { return; }
                        // I1：窗口已被移除（本回调可能在 Post 之后才执行）→ 气泡窗口已 Close，
                        // 再 Show 会抛「Cannot re-show a closed window」，直接丢弃。
                        if (_isExiting) { return; }
                        using var ms = new MemoryStream(png);
                        var bmp = new Bitmap(ms);
                        // 2026-08-16：尺寸必须在 ShowBalloon 前读取——一次性气泡（autoHide 非空）内部
                        // 自动隐藏时 HideBalloon 会 Dispose 位图，返回后再访问 bmp.PixelSize 抛 ObjectDisposedException。
                        var bw = bmp.PixelSize.Width;
                        var bh = bmp.PixelSize.Height;
                        TimeSpan? autoHide = durationMs.HasValue ? TimeSpan.FromMilliseconds(durationMs.Value) : null;
                        await _balloonWindow.BalloonControl.ShowBalloon(text, data, bmp, autoHide);
                        _balloonWindow.Width = bw;
                        _balloonWindow.Height = bh;
                        // 锚点基准 = 当前角色锚点（拖拽结束会更新）
                        _balloonAnchorX = Position.X + _lastOriginX;
                        _balloonAnchorY = Position.Y + _lastOriginY;
                        _balloonWindow.ShowBalloonWindow();
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonShow: {ex.Message}"); }
                });
            };
            _balloonService.OnBalloonReady += _balloonReadyHandler;   // I1：可退订的命名订阅

            // 胶水 2026-08-17：气泡淡出完成后隐藏外层窗口（旧实现空壳窗口常驻，观感「气泡一直在」）
            _balloonWindow.BalloonControl.BalloonHidden += () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { _balloonWindow.HideBalloonWindow(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonHidden: {ex.Message}"); }
                });
            };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] InitBalloonTimer: {ex.Message}"); }
    }

    private async void TryShowRandomBalloon()
    {
        try
        {
            // Agent 非空闲（干活中）：状态气泡优先，不发随机气泡。
            if (_agentReflect != AgentState.Idle)
            {
                ScheduleRandomBalloon();
                return;
            }

            // 静默轮（胶水 2026-08-17）：本轮不弹气泡，只留空窗——上一轮 Schedule 已掷骰
            if (_balloonSilentRound)
            {
                Console.WriteLine("[PetWindow] RandomBalloon: 静默轮（空窗期，不弹）");
                return;
            }

            // 常驻模式：不限制动画动作，每次定时器触发都换气泡文本
            var balloonId = _configService.LoadSettings().BalloonId;
            if (string.IsNullOrEmpty(balloonId)) { balloonId = "560"; }
            var data = _balloonService.LoadBalloon(balloonId);
            Console.WriteLine($"[PetWindow] TryShowRandomBalloon: id={balloonId}, data={(data != null ? "ok" : "null(种子中)")}");
            if (data == null) { return; } // 缓存未就绪，后台种子中，下一轮再试
            var line = _randomLines[_rng.Next(_randomLines.Length)];
            Console.WriteLine($"[PetWindow] BalloonShow: line={line}");
            // 胶水 2026-08-19：随机气泡显示 15s 后自动隐藏（原 6s；调度期望 1min/条，显示期占 ~25%）
            // V0.1.3：targetPetId = 本宠物 → 只在本窗口弹（多只各自随机说话，不互相顶掉）
            await _balloonService.Show(line, data, durationMs: 15000, targetPetId: _petId);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RandomBalloon: {ex.Message}"); }
        finally
        {
            // A2：隐藏到托盘后不再调度下一轮（否则隐藏期间的异步续延会重启定时器，白烧 CPU）
            if (IsVisible)
            {
                ScheduleRandomBalloon();
            }
        }
    }

    /// <summary>调度下一次随机气泡（胶水 2026-08-17：降密度 + 空窗期）。
    /// 间隔 8~20s；每次 30% 概率进入静默轮（到点只重排不弹），平均约 12~30s 一条，有安静期。</summary>
    private void ScheduleRandomBalloon()
    {
        try
        {
            _balloonSilentRound = _rng.Next(100) < 40; // 40% 静默轮
            _balloonTimer.Interval = TimeSpan.FromSeconds(_rng.Next(30, 43)); // 30~42s（胶水 2026-08-19：期望约 1 分钟一条——均值 36s ÷ 60% 出泡率 = 60s）
            _balloonTimer.Stop();
            _balloonTimer.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ScheduleRandomBalloon: {ex.Message}"); }
    }

    public async Task ShowBalloonNow(string text)
    {
        try
        {
            var balloonId = _configService.LoadSettings().BalloonId;
            if (string.IsNullOrEmpty(balloonId)) { balloonId = "560"; }
            var data = _balloonService.LoadBalloon(balloonId);
            if (data == null) { return; }
            await _balloonService.Show(text, data, targetPetId: _petId);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowBalloonNow: {ex.Message}"); }
    }

    /// <summary>外部（托盘「重置到桌面中央」）改动位置后落盘。</summary>
    internal void SavePositionNow() => SavePositionToConfig();

    /// <summary>保存本宠物窗口位置到自己的实例配置（拖拽结束调用；多只各存各的坐标）。</summary>
    private void SavePositionToConfig()
    {
        try
        {
            var pos = Position;
            _configService.UpdatePet(_petId, p =>
            {
                p.WindowX = pos.X;
                p.WindowY = pos.Y;
            });
            _cfg.WindowX = pos.X;
            _cfg.WindowY = pos.Y;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SavePosition({_petId}): {ex.Message}"); }
    }

    /// <summary>
    /// 记录最近使用的宠物到配置文件
    /// </summary>
    private void RecordRecentItem(string id, string name, string type)
    {
        try
        {
            // V0.1.3：改走原子更新（Load→改→Save 串行化）——多只宠物同时写配置时不再互相回滚
            var normalizedType = NormalizeEntityType(type);
            _configService.UpdateSettings(s =>
            {
                if (s.RecentItems == null)
                {
                    s.RecentItems = new System.Collections.Generic.List<Models.RecentItem>();
                }
                // 移除旧的同名记录
                s.RecentItems.RemoveAll(r => r.Id == id);
                // 插入到最前
                s.RecentItems.Insert(0, new Models.RecentItem
                {
                    Type = normalizedType,
                    Id = id,
                    Name = name,
                    LastUsedAt = DateTime.Now
                });
                // 最多保留20条
                while (s.RecentItems.Count > 20)
                {
                    s.RecentItems.RemoveAt(s.RecentItems.Count - 1);
                }
            });
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
        try
        {
            // V0.1.3：托盘按窗口注销（只剩最后一只被销毁时才撤图标）
            _trayService.UnregisterPet(this);
            // A15：窗口销毁时退订全部 EventBus 订阅（防匿名 lambda 泄漏）
            UnsubscribeBusAll();
            // V0.1.3：退订配置变更（旧版匿名 lambda 无 -=，多实例会逐个泄漏）
            if (_settingsChangedHandler != null)
            {
                try { _configService.OnSettingsChanged -= _settingsChangedHandler; } catch { }
                _settingsChangedHandler = null;
            }
            // I1：退订气泡回调（Singleton 事件，不退订会让被移除窗口的整棵对象图无法回收）
            if (_balloonReadyHandler != null)
            {
                try { _balloonService.OnBalloonReady -= _balloonReadyHandler; } catch { }
                _balloonReadyHandler = null;
            }
            // I1：兜底停帧循环/气泡调度（Close() 不产生 IsVisible 变化，OnPetHidden 不会跑）
            try { _timer.Stop(); } catch { }
            try { _balloonTimer.Stop(); } catch { }
            try { _stopwatch.Stop(); } catch { }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnClosed: {ex.Message}"); }
        base.OnClosed(e);
    }

    // ── A2/Y7：隐藏到托盘/恢复显示的生命周期处理 ──

    /// <summary>
    /// 监听 IsVisible 变化（Hide→托盘 / Show→恢复），停/启帧循环与随机气泡定时器（A2），
    /// 气泡窗口跟随宠物隐藏/显示（Y7）。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            try
            {
                if (change.GetNewValue<bool>())
                {
                    OnPetShown();
                }
                else
                {
                    OnPetHidden();
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] IsVisibleChanged: {ex.Message}"); }
        }
    }

    /// <summary>隐藏到托盘：停 30fps 帧循环 + 随机气泡定时器，隐藏气泡窗口（A2/Y7）。</summary>
    private void OnPetHidden()
    {
        try
        {
            if (_timer != null && _timer.IsEnabled)
            {
                _timer.Stop();
            }
            if (_balloonTimer != null && _balloonTimer.IsEnabled)
            {
                _balloonTimer.Stop();
            }
            // Y7：宠物隐藏时气泡窗口跟随隐藏（否则托盘隐藏后气泡仍悬浮成幽灵）
            _balloonWasVisibleBeforeHide = _balloonWindow.IsVisible;
            if (_balloonWindow.IsVisible)
            {
                _balloonWindow.HideBalloonWindow();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnPetHidden: {ex.Message}"); }
    }

    /// <summary>从托盘恢复显示：重启帧循环 + 随机气泡定时器，恢复气泡（追帧保护已有：OnTimerTick delta&gt;500 钳 33）。</summary>
    private void OnPetShown()
    {
        try
        {
            // 重置计时基准，避免恢复首帧算出隐藏期间的大 delta（虽有钳制，但保持基准干净）
            _lastElapsed = _stopwatch.Elapsed.TotalMilliseconds;
            if (_timer != null && !_timer.IsEnabled)
            {
                _timer.Start();
            }
            if (_balloonTimer != null && !_balloonTimer.IsEnabled)
            {
                ScheduleRandomBalloon(); // 内含 Stop+Start
            }
            if (_balloonWasVisibleBeforeHide)
            {
                _balloonWasVisibleBeforeHide = false;
                _balloonWindow.ShowBalloonWindow();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnPetShown: {ex.Message}"); }
    }

    private void SetupDragEvents()
    {
        // 左键拖拽 + 右键菜单，直接绑定在 Window 上（冒泡模式）。
        // ⚠️ 不用 BeginMoveDrag：OS 原生拖动限制窗口不能拖出屏幕顶部（窗口含头顶气泡区后角色拖不到顶）。
        // 手动拖拽：鼠标绝对屏幕坐标 = Position + GetPosition(this)*RenderScaling（窗口移动时互相抵消，防抖），
        // Position 允许负值 → 角色可部分拖出显示器（胶水规则 2026-08-08）
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
                // 点击≠拖拽（胶水 2026-08-15）：仅记录按下位置，位移超阈值才进拖拽。
                // 否则「点击」也会 _isDragging=true + 释放同步 origin 基准 → stand↔move 切换补偿错位 → 角色跳位置。
                _pressPoint = e.GetPosition(this);
                _dragCandidate = true;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerPressed: {ex.Message}"); }
        };
        PointerMoved += (_, e) =>
        {
            try
            {
                if (!_isDragging)
                {
                    // 候选拖拽：位移超过阈值才真正开始（点击移动几像素不算拖拽）
                    if (_dragCandidate)
                    {
                        var p = e.GetPosition(this);
                        double rs = RenderScaling > 0 ? RenderScaling : 1.0;
                        double dist = Math.Abs(p.X * rs - _pressPoint.X * rs) + Math.Abs(p.Y * rs - _pressPoint.Y * rs);
                        if (dist < 4)
                        {
                            return;
                        }
                        _isDragging = true;
                        _dragStart = e.GetPosition(this);
                        _windowStartPos = Position;
                        // 移动时默认气泡关闭（胶水规则 2026-08-08）：拖拽期间隐藏气泡，释放后恢复
                        _balloonWasVisible = _balloonWindow.IsVisible;
                        if (_balloonWasVisible) { _balloonWindow.HideBalloonWindow(); }
                        _eventBus.Publish(EventType.UserDragStart, new PetDragEvent(_petId));
                        return;
                    }
                    return;
                }
                var p2 = e.GetPosition(this);
                double rs2 = RenderScaling > 0 ? RenderScaling : 1.0;
                double dx = (Position.X + p2.X * rs2) - (_windowStartPos.X + _dragStart.X * rs2);
                double dy = (Position.Y + p2.Y * rs2) - (_windowStartPos.Y + _dragStart.Y * rs2);
                // 贴顶 clamp：窗口顶 ≥ 0（大素材 8880837 按下偏移大，不 clamp 会推到屏外看似拖不到顶，胶水反馈 2026-08-09）
                int ny = _windowStartPos.Y + (int)dy;
                if (ny < 0) { ny = 0; }
                Position = new PixelPoint(_windowStartPos.X + (int)dx, ny);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerMoved: {ex.Message}"); }
        };
        PointerReleased += (_, _) =>
        {
            try
            {
                _dragCandidate = false;
                if (!_isDragging) { return; }
                _isDragging = false;
                _eventBus.Publish(EventType.UserDragEnd, new PetDragEvent(_petId));
                // 气泡锚点基准 = 拖拽结束时的角色锚点（动画切换气泡位置不动）
                _balloonAnchorX = Position.X + _lastOriginX;
                _balloonAnchorY = Position.Y + _lastOriginY;
                // V0.1.3：位置持久化（每只各存各的坐标，重启后回到原位；旧版 WindowX/WindowY 是死字段）
                SavePositionToConfig();
                // 释放后第一帧绝对定位用：记录拖拽结束时的角色锚点屏幕位置
                _dragEndAnchorX = (int)_balloonAnchorX;
                _dragEndAnchorY = (int)_balloonAnchorY;
                // 释放后第一帧先同步 origin 基准（不补偿）：否则动画帧 oy 已变 → 补偿恢复瞬间角色跳一下
                _syncOriginOnNextFrame = true;
                Console.WriteLine($"[PetWindow] DragEnd: anchor=({_balloonAnchorX},{_balloonAnchorY})");
                // 移动结束恢复气泡（若拖拽前可见）
                if (_balloonWasVisible) { _balloonWindow.ShowBalloonWindow(); }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerReleased: {ex.Message}"); }
        };
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
            var delta = elapsed - _lastElapsed;
            _lastElapsed = elapsed;

            if (delta <= 0) { delta = 33; }
            // 2026-08-17：大跳（失焦恢复/卡顿）原 clamp 到 33ms 会吞掉实际时间——慢渲染/低帧
            // 环境下空闲随机累计永远到不了阈值（Windows 纯净版「定时器不切动画」的根因候选）。
            // 改为封顶 500ms：保留大部分实际时间，既防失焦跳变失控，又不饿死空闲累计。
            if (delta > 500) { delta = 500; }

            var fr = _animService.Tick(delta);
            byte[]? pngData = null;
            int ox = 0, oy = 0, fw = 0, fh = 0;
            if (fr.Bitmap != null)
            {
                // 纸娃娃 provider：合成帧位图（BGRA premul）+ union 公共 origin
                ox = fr.OriginX; oy = fr.OriginY; fw = fr.FrameW; fh = fr.FrameH;
            }
            else if (fr.Png != null)
            {
                pngData = fr.Png;
                // A13：直接读 AnimService 持有的当前图条，不再每帧二次 GetCachedStrip（锁 + LRU 链表维护）
                var strip = _animService.CurrentStrip;
                ox = strip?.OriginX ?? 0;
                oy = strip?.OriginY ?? 0;
                fw = strip?.FrameWidth ?? 0;
                fh = strip?.FrameHeight ?? 0;
            }
            else if (fr.Empty)
            {
                // A9：在播但无帧数据（条带缺失/解码失败/离线兜底空）→ 清画布，避免旧帧冻结
                _spriteCanvas.Clear();
            }
            if (pngData != null || fr.Bitmap != null)
            {
                if (fr.Bitmap != null)
                {
                    _spriteCanvas.SetFrameBitmap(fr.Bitmap, ox, oy, _scale);
                    fr.Bitmap.Dispose();
                }
                else
                {
                    _spriteCanvas.SetFrame(pngData!, ox, oy, _scale);
                }

                // 窗口尺寸 = max(当前帧, 气泡锚点基准帧) × 缩放（无 headRoom：macOS 窗口顶不能超过菜单栏，
                // 含头顶预留区会导致角色拖不到顶。气泡在独立 BalloonWindow，不占主窗口高度）
                EnsureBalloonAnchor();
                _spriteCanvas.SetAnchorHeight(_anchorFrameH * _scale);
                double scaledW = Math.Max(fw > 0 ? fw * _scale : 0, _anchorFrameW * _scale);
                double scaledH = Math.Max(fh > 0 ? fh * _scale : 0, _anchorFrameH * _scale);
                // ±2px 容忍 + 取整：超长帧 PNG 宽偶有 1-2px 微变，窗口尺寸重设会抖动（胶水反馈 2026-08-09）
                if (scaledW > 0 && scaledH > 0 && (Math.Abs(scaledW - _lastFrameW) > 2 || Math.Abs(scaledH - _lastFrameH) > 2))
                {
                    // ⚠️ macOS NSWindow frame origin 是左下角：仅改尺寸（保持左下不动）会让左上角（Position）漂移，
                    //    随后 origin 补偿基于漂移后的 Position 计算 → 锚点（脚底）跟着跳（stand→move 实测位置变更）。
                    //    修复：resize 前记录左上角，resize 后强制恢复 → 尺寸变化不移动窗口左上角。
                    var posBeforeResize = Position;
                    Width = Math.Round(scaledW);
                    Height = scaledH;
                    _lastFrameW = scaledW;
                    _lastFrameH = scaledH;
                    if (Position != posBeforeResize)
                    {
                        Position = posBeforeResize;
                    }
                }

                // 窗口位置补偿锚点偏移（锚点也乘缩放），使精灵图锚点对齐屏幕固定位置。
                // ⚠️ 拖拽期间暂停补偿（_isDragging）：否则拖到顶后动画切换（oy 变）会把窗口拉回锚点 →
                // 「拖不到顶」+ 看起来像 origin 变动。拖拽中只更新基准，拖完从新位置继续补偿
                int oxScaled = (int)(ox * _scale);
                int oyScaled = (int)(oy * _scale);

                // 启动/切宠物首帧：只同步基准不移动窗口（基准尚未初始化，直接补偿会从 (0,0) 大跳）
                if (_pendingOriginSync)
                {
                    _lastOriginX = oxScaled;
                    _lastOriginY = oyScaled;
                    _pendingOriginSync = false;
                }
                else if (_isDragging)
                {
                    _lastOriginX = oxScaled;
                    _lastOriginY = oyScaled;
                }
                else if (_syncOriginOnNextFrame)
                {
                    // 拖完第一帧：用「拖拽结束时的角色锚点屏幕位置」绝对定位（不简单同步基准）。
                    // ⚠️ 若释放瞬间动作已切换（move→stand），同步基准=新动作 origin 会让角色视觉跳
                    // （窗口还在旧动作锚点位置，基准却已是新 origin → 差值永不补偿）。
                    // 绝对定位：窗口 = 锚点 - 当前帧 origin → 无论下一帧什么动作，角色停在释放位置。
                    int newX = _dragEndAnchorX - oxScaled;
                    int newY = _dragEndAnchorY - oyScaled;
                    if (Position.Y <= 0 && newY < 0)
                    {
                        newY = Position.Y; // 贴顶：保持顶贴屏顶
                    }
                    Position = new PixelPoint(newX, newY);
                    _lastOriginX = oxScaled;
                    _lastOriginY = oyScaled;
                    _balloonAnchorX = Position.X + oxScaled;
                    _balloonAnchorY = Position.Y + oyScaled;
                    _syncOriginOnNextFrame = false;
                }
                else
                {
                    int dx = oxScaled - _lastOriginX;
                    int dy = oyScaled - _lastOriginY;
                    if (dx != 0 || dy != 0)
                    {
                        // 贴顶（窗口顶=0）时垂直不补偿：保持角色顶贴屏顶（大素材动画切换 oy 变大不会把角色顶推出屏外）
                        int ny = Position.Y - dy;
                        if (Position.Y <= 0 && ny < 0)
                        {
                            Position = new PixelPoint(Position.X - dx, Position.Y);
                        }
                        else
                        {
                            Position = new PixelPoint(Position.X - dx, ny);
                        }
                    }
                    _lastOriginX = oxScaled;
                    _lastOriginY = oyScaled;
                }

                // 气泡独立窗口位置：底 = 角色头顶（锚点基准固定 → 动画切换气泡不动）。
                // 水平：纸娃娃 = MapleSalon2 character.ts L136 固定 x=-10（arrow 在气泡左下 ≈ origin x 同一竖线）；
                // mob/npc = 居中于锚点（原逻辑，胶水规则 2026-08-08：arrow 修复不改动其他类型）
                if (_balloonWindow.IsVisible)
                {
                    // Y8：拖拽中气泡锚点跟随当前角色位置（拖拽期间窗口移动而锚点基准不更新 → 残留气泡漂移成幽灵）
                    if (_isDragging)
                    {
                        _balloonAnchorX = Position.X + _lastOriginX;
                        _balloonAnchorY = Position.Y + _lastOriginY;
                    }
                    double aw = _anchorFrameW > 0 ? _anchorFrameW : fw;
                    double ah = _anchorFrameH > 0 ? _anchorFrameH : fh;
                    double ratio = aw > 0 ? ah / aw : 1.5;
                    int bw = Math.Max(1, (int)_balloonWindow.Width);
                    int bh = Math.Max(1, (int)_balloonWindow.Height);
                    double bx, by;
                    if (IsSentinelActive())
                    {
                        // 纸娃娃 = MapleSalon2 原样：navel 是容器原点（画布 0,0），气泡 position(-10,-60) 相对容器
                        // 气泡左 = 窗口内 −10（navel 锚定）；气泡底 = 角色头顶（用户实证 y 可用，上移 2px 微调）
                        // 水平不再减 ox（navel 画布锚定为 0，不是 navel 帧内 − minX）——胶水纠正 2026-08-09：水平严重不对
                        bx = Position.X - 10 * _scale;
                        by = Position.Y - bh - 2 * _scale;
                    }
                    else
                    {
                        // Mob/NPC 原逻辑：锚点=脚底，正常 0.75/超长 -0.5；NPC 水平 -10（arrow 对齐 origin x），mob 居中
                        double headMargin = ah * (ratio <= 1.8 ? 0.75 : -0.5) * _scale;
                        bx = _currentEntityType == "Npc" ? _balloonAnchorX - 10 * _scale : _balloonAnchorX - bw / 2.0;
                        by = _balloonAnchorY - headMargin - bh;
                    }
                    _balloonWindow.Position = new PixelPoint(
                        (int)bx,
                        (int)by);
                }
            }
            // 状态文字只显示动作名（去掉原调试用 tick 计数）
            _infoText.Text = _animService.GetCurrentAction();

            // 音乐模块 V0.2.1：宠物位置哨兵（pet:moved）——帧尾统一比对发布，
            // 拖拽 / 释放绝对定位 / 动画 origin 补偿与落地 / 重置桌面中央（含托盘菜单路径）/
            // 摄像机模式落地（PetWindow.Mode.cs）等一切改 Position 的路径都在此被捕获
            PublishPetMovedThrottled();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] Tick: {ex.Message}"); }
    }

    /// <summary>
    /// 发布宠物桌面位置变化 pet:moved（V0.2.1 迷你控制条跟随契约）：EventBus 通道 MiniPetEvents.PetMoved，
    /// payload = (double X, double Y) **角色脚底中心**桌面像素坐标（2026-08-27 修订：原发窗口左上角
    /// 导致控制条叠在角色头部——现纸娃娃取锚点帧底边水平居中，mob/npc 取脚底锚点）。OnTimerTick 每帧
    /// 尾部调用做位置哨兵——与上次已发布坐标一致零开销跳过；有变化时限速 10Hz（100ms gate），gate
    /// 窗口内的变更不丢弃（每帧复查坐标差，停稳后的最终位置必然在下帧补发）。try-catch 保护不影响主流程。
    /// </summary>
    private void PublishPetMovedThrottled()
    {
        try
        {
            var now = Environment.TickCount64;
            if (_petMovedLastSent.HasValue && _petMovedLastSent.Value == Position)
            {
                return; // 无变化不发（30fps 轮询下的常态快路径）
            }
            if (now - _petMovedLastMs < 100)
            {
                return; // 10Hz 节流：窗口期暂缓（下帧仍不一致时补发）
            }
            // 脚底中心换算：与气球「气泡底=头顶」同一坐标系反推——头在窗口顶 → 脚 = Top + 锚点帧高×scale；
            // 纸娃娃水平居中用锚点帧宽×scale 的一半；mob/npc 的 _balloonAnchorX/Y 本身即脚底中心。
            double feetX;
            double feetY;
            if (IsSentinelActive())
            {
                feetX = Position.X + (_anchorFrameW > 0 ? _anchorFrameW * _scale / 2 : 40);
                feetY = Position.Y + (_anchorFrameH > 0 ? _anchorFrameH * _scale : 80);
            }
            else
            {
                feetX = _balloonAnchorX;
                feetY = _balloonAnchorY;
            }
            _petMovedLastMs = now;
            _petMovedLastSent = Position;
            _eventBus.Publish(MiniPetEvents.PetMoved, (X: feetX, Y: feetY));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetMovedPublish: {ex.Message}"); }
    }

    /// <summary>
    /// 解析气泡锚点基准动作（胶水规则 2026-08-08）：按
    /// ["stand","say","mouse","move","hand","laugh","eye","fly","move","die"] 从前到后第一个存在的动作
    /// 计算气泡位置基准，不随每个动画重新计算（动画切换时气泡位置稳定）。
    /// 动作列表：WZ 加载时用 GetActionList，否则用磁盘缓存清单。结果缓存到 _anchorActionName。
    /// </summary>
    private void EnsureBalloonAnchor()
    {
        try
        {
            var mobId = _animService.GetCurrentMobId();
            if (string.IsNullOrEmpty(mobId)) { return; }
            if (mobId == PaperdollService.SentinelMobId)
            {
                // 纸娃娃锚点基准固定 stand1（站姿 union），不随 BalloonAnchorPriority 找错动作 → 窗口/气泡稳定
                if (_anchorMobId == mobId && _anchorActionName != null) { return; }
                _anchorActionName = "stand1";
                _anchorMobId = mobId;
                var (aw, ah, _, _) = _paperdoll.GetActionBounds("stand1");
                _anchorFrameW = aw;
                _anchorFrameH = ah;
                Console.WriteLine($"[PetWindow] EnsureBalloonAnchor: 纸娃娃 anchor=stand1 frame={_anchorFrameW}x{_anchorFrameH}");
                return;
            }
            if (_anchorMobId == mobId && _anchorActionName != null) { return; } // 同 mob 已解析

            List<string> actions;
            try
            {
                actions = _wzService.IsLoaded
                    ? new List<string>(_wzService.GetActionList(mobId))
                    : GetCachedActionList(mobId);
            }
            catch { actions = GetCachedActionList(mobId); }

            string? anchor = null;
            foreach (var a in BalloonAnchorPriority)
            {
                if (actions.Contains(a)) { anchor = a; break; }
            }
            anchor ??= actions.Count > 0 ? actions[0] : null;
            if (anchor == null) { return; }

            var strip = _spriteService.GetCachedStrip(mobId, anchor).Item1;
            // ⚠️ 锚点动作未缓存 → strip null → FrameH=0 → headMargin fallback 当前帧高（随动画变 → 气泡跳）。
            // 修复：fallback 到已缓存的常用动作（stand/move/fly/walk/jump），保证锚点帧高固定
            if (strip == null)
            {
                foreach (var alt in new[] { "stand", "move", "fly", "walk", "jump" })
                {
                    var altStrip = _spriteService.GetCachedStrip(mobId, alt).Item1;
                    if (altStrip != null) { strip = altStrip; anchor = alt; break; }
                }
            }
            _anchorActionName = anchor;
            _anchorMobId = mobId;
            _anchorFrameW = strip?.FrameWidth ?? 0;
            _anchorFrameH = strip?.FrameHeight ?? 0;
            Console.WriteLine($"[PetWindow] EnsureBalloonAnchor: mob={mobId} anchor={anchor} frame={_anchorFrameW}x{_anchorFrameH}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] EnsureBalloonAnchor: {ex.Message}"); }
    }

    /// <summary>
    /// 读磁盘缓存的动作清单（无 WZ 时菜单/锚点仍可用）。缓存目录统一走 CacheManager.GetCacheDir()
    /// —— macOS 为 ~/Library/Caches/MiniPet/v1，Windows 为 %LOCALAPPDATA%/MiniPet/cache/v1（原硬编码
    /// macOS 路径在 Windows 上读不到任何动作，属于跨平台兼容性 bug，2026-08-16 修复）。
    /// </summary>
    private List<string> GetCachedActionList(string mobId)
    {
        var result = new List<string>();
        try
        {
            var cacheDir = Path.Combine(_cacheManager.GetCacheDir(), "sprites", mobId);
            if (!Directory.Exists(cacheDir)) { return result; }

            foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
            {
                var action = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(action) || action == "config") { continue; }
                // 跳过无有效画面的空动作（FrameWidth<=1）
                if (IsBlankCachedAction(file, action)) { continue; }
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
                    // 2026-08-16 神之子头冠卡顿修复：先 WarmupFor 预热新外观（不切换、不清缓存，
                    // 旧动画在预热期间照常播放），全部就绪后再 SetAppearance 切换——切换即热，无锁竞争卡顿。
                    try { _paperdoll.WarmupFor(appearance); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollWarmup: {ex.Message}"); }
                    _paperdoll.SetAppearance(appearance); // 切换外观清依赖外观缓存；引用赋值原子，帧循环安全
                    // 持久化当前搭配（启动恢复）+ 构建离线精灵图条（WZ 断线兜底）
                    // PLAN 步骤 8：Task.Run 内「LoadSettings→改→SaveSettings」整对象读改写迁移到 Update 事务
                    // （序列化锁内「读→改→写」+ 派发 UI 线程与设置窗口整对象回存同线程串行化，
                    // 消除与音乐 PlayHistory 高频写入的「缓存读↔整存」碰撞窗口；字段改动语义不变）
                    try
                    {
                        // V0.1.3：只写本宠物这一只的实例配置（旧版写全局单值 = 所有宠物一起变纸娃娃）
                        _configService.UpdatePet(_petId, p =>
                        {
                            p.MobId = PaperdollService.SentinelMobId;
                            p.EntityType = "player";
                            p.Paperdoll = appearance;
                        });
                        Console.WriteLine($"[PetWindow] Paperdoll 持久化成功({_petId}): CurrentMobId={PaperdollService.SentinelMobId} Paperdoll={(appearance != null ? $"有 {appearance.Hair?.Id}" : "无")}");

                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollSave 失败: {ex}"); }
                    try { _paperdoll.BuildOfflineStrips(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OfflineStrips: {ex.Message}"); }
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (gen != _paperdollGen) { return; } // 被更新的选择覆盖
                            _animService.SetCurrentMobId(PaperdollService.SentinelMobId);
                            _animService.Play("stand1", PlayMode.Loop);
                            ApplyIdlePolicy();
                            _currentEntityType = "player";
                            if (_isPrimaryRole)
                            {
                                _trayService.SetTooltip("MiniPet - 纸娃娃");
                            }
                            // 后台构建完成：恢复设置窗口「应用到主窗口」按钮（合成中 → ✅ 已应用，体验 B）
                            // I2：发起换装的设置窗口可能不是本窗口的设置窗口（编辑目标 ≠ 创建者）→
                            // 广播完成回执，由「目标宠物 id == 自己目标」的设置窗口解除 busy。
                            _eventBus.Publish(EventType.PaperdollAppliedDone, _petId);
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollApply: {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollBuild: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ApplyPaperdoll: {ex.Message}"); }
    }

    /// <summary>显示/隐藏切换桌宠 loading（进度条 + 文本，覆盖动画区）。</summary>
    private void ShowPetLoading(bool show, string? name = null)
    {
        if (_loadingBar == null || _loadingText == null) { return; }
        _loadingBar.IsVisible = show;
        _loadingText.IsVisible = show;
        if (show && !string.IsNullOrEmpty(name)) { _loadingText.Text = $"切换中: {name}…"; }
    }

    /// <summary>
    /// 启动切换超时兜底：PreloadMob 后台任务若因异常情况（WZ 锁长占用/磁盘不可写）迟迟不返回，
    /// 原实现没有任何取消或超时，loading 会永久停在「切换中…」。超时后复位 UI 并提示，不再无限等待。
    /// </summary>
    private void StartPetSwitchTimeout(int gen, string name)
    {
        StopPetSwitchTimeout();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PetSwitchTimeoutMs) };
        timer.Tick += (_, _) =>
        {
            StopPetSwitchTimeout();
            if (gen != _petSwitchGen) { return; }         // 已被更新的切换接管
            if (_loadingBar?.IsVisible != true) { return; } // loading 已正常结束
            ShowPetLoading(false);
            Console.Error.WriteLine($"[PetWindow] SwitchToPet 超时({PetSwitchTimeoutMs}ms)：gen={gen} name={name}");
            ShowInfoPopup("切换宠物", $"素材 [{name}] 加载超时（{PetSwitchTimeoutMs / 1000}s），已保持当前宠物。");
        };
        _petSwitchTimeout = timer;
        timer.Start();
    }

    /// <summary>停止切换超时兜底计时器。</summary>
    private void StopPetSwitchTimeout()
    {
        _petSwitchTimeout?.Stop();
        _petSwitchTimeout = null;
    }

    /// <summary>
    /// 切换到指定素材桌宠：stand 已缓存则立即切；未缓存则后台预加载（显示 loading 菊花），期间保持旧角色播放，加载完成后切换。
    /// 避免首次切换（WZ 构建/磁盘读取）在 UI 线程同步执行导致的明显卡顿。代际 token 防快速连点竞态。
    /// A8：加载失败不再静默——提示用户且不发布 PetChanged（托盘 tooltip 与最近使用记录不脏数据）。
    /// </summary>
    private void SwitchToPet(string id, string name, string type)
    {
        try
        {
            _currentEntityType = type;
            var gen = ++_petSwitchGen;
            if (_spriteService.GetCachedStrip(id, "stand").Item1 != null)
            {
                StopPetSwitchTimeout();
                ShowPetLoading(false);
                _spriteCanvas.ForceSizeRefresh(); // 切换素材强制容器重设（防截断，胶水反馈 2026-08-09）
                _lastFrameW = 0; _lastFrameH = 0;
                _animService.SetCurrentMobId(id);
                _animService.SwitchTo("stand");
                ApplyIdlePolicy();
                // 切换成功才写配置 + 发布 PetChanged（V0.1.3：只写本宠物这一只 / 只通知自己）
                PersistEntity(id, type, name);
                _eventBus.Publish(EventType.PetChanged, new PetChangedEvent(_petId, id, name));
            }
            else
            {
                ShowPetLoading(true, name);
                StartPetSwitchTimeout(gen, name);
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool ok;
                    try { ok = _spriteService.PreloadMob(_wzService, id, type); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetPreload: {ex.Message}"); ok = false; }
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (gen != _petSwitchGen) { return; } // 被更新的切换覆盖（loading 由新切换接管）
                            StopPetSwitchTimeout();
                            ShowPetLoading(false);
                            if (!ok)
                            {
                                // A8：失败提示 + 不发布 PetChanged（不脏 tooltip/最近使用）
                                Console.Error.WriteLine($"[PetWindow] SwitchToPet({id}) 加载失败");
                                ShowInfoPopup("切换宠物", $"素材 [{name}] ({id}) 加载失败，已保持当前宠物。\n若为离线状态，请确认该素材已有本地缓存。");
                                return;
                            }
                            _spriteCanvas.ForceSizeRefresh(); // 切换素材强制容器重设（防截断，胶水反馈 2026-08-09）
                            _lastFrameW = 0; _lastFrameH = 0;
                            _animService.SetCurrentMobId(id);
                            _animService.SwitchTo("stand");
                            ApplyIdlePolicy();
                            // 切换成功才写配置 + 发布 PetChanged（V0.1.3：只写本宠物这一只 / 只通知自己）
                            PersistEntity(id, type, name);
                            _eventBus.Publish(EventType.PetChanged, new PetChangedEvent(_petId, id, name));
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PetSwitch: {ex.Message}"); }
                    });
                });
            }
        }
        catch (Exception ex)
        {
            // 同步段异常也要复位 loading/超时，避免「切换中…」永久残留
            StopPetSwitchTimeout();
            ShowPetLoading(false);
            Console.Error.WriteLine($"[PetWindow] SwitchToPet: {ex.Message}");
        }
    }

    /// <summary>
    /// 把本宠物当前实体写入自己的实例配置（V0.1.3：只动这一只，不碰其他桌宠）。
    /// name 非空时同步显示名（Mob/Npc 用 WZ 名）；纸娃娃（哨兵）保留已有名字（预设名）。
    /// </summary>
    private void PersistEntity(string mobId, string entityType, string? name = null)
    {
        try
        {
            var normalized = mobId == PaperdollService.SentinelMobId ? "player" : NormalizeEntityType(entityType);
            _configService.UpdatePet(_petId, p =>
            {
                p.MobId = mobId;
                p.EntityType = normalized;
                if (!string.IsNullOrEmpty(name) && mobId != PaperdollService.SentinelMobId)
                {
                    p.Name = name;
                }
            });
            _cfg.MobId = mobId;
            _cfg.EntityType = normalized;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PersistEntity({_petId}): {ex.Message}"); }
    }

    private bool IsSentinelActive() => _animService.GetCurrentMobId() == PaperdollService.SentinelMobId;

    /// <summary>把记录里的类型归一化成 WZ 目录名（Mob/Npc）。</summary>
    private static string NormalizeEntityType(string? type)
    {
        if (string.IsNullOrEmpty(type)) { return "Mob"; }
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
    /// <summary>公开入口：WZ 未配置/加载失败时 App 启动后自动打开设置页（2026-08-17 用户要求「没配置 WZ 默认打开设置」）。</summary>
    public void OpenSettingsForWzConfig()
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenSettingsForWzConfig: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenSettingsForWzConfig: {ex.Message}"); }
    }

    private void ShowSettingsWindow()
    {
        try
        {
            // V0.1.3：设置中心**全局唯一**（由 PetManager 持有）——旧版每只桌宠各持一个实例，
            // 多只桌宠点「设置中心」会开出多个窗口。这里只请求显示，并把编辑目标切到自己。
            _petManager.ShowSettings(_petId);

        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowSettingsWindow: {ex.Message}"); }
    }
}
