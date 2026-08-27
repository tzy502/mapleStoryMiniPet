using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

public partial class SettingsWindow : Window
{
    // ── 颜色常量 ──
    private static readonly Color BgColor = Color.FromArgb(255, 30, 30, 38);
    private static readonly Color BorderColor = Color.FromArgb(255, 60, 60, 70);
    private static readonly Color AccentColor = Color.FromArgb(255, 94, 156, 255);
    private static readonly Color TxtPrimary = Color.FromArgb(255, 240, 240, 244);
    private static readonly Color TxtSecondary = Color.FromArgb(255, 160, 160, 176);
    private static readonly Color TxtDim = Color.FromArgb(255, 112, 112, 128);
    private static readonly Color InputBg = Color.FromArgb(180, 30, 30, 36);
    private static readonly Color InputBorder = Color.FromArgb(77, 120, 120, 140);
    private static readonly Color OverlayBg = Color.FromArgb(166, 0, 0, 0);

    private static readonly SolidColorBrush BrushAccent = new(AccentColor);
    private static readonly SolidColorBrush BrushTxtPrimary = new(TxtPrimary);
    private static readonly SolidColorBrush BrushTxtSecondary = new(TxtSecondary);
    private static readonly SolidColorBrush BrushTxtDim = new(TxtDim);
    private static readonly SolidColorBrush BrushInputBg = new(InputBg);
    private static readonly SolidColorBrush BrushBorder = new(BorderColor);
    private static readonly SolidColorBrush BrushBg = new(BgColor);

    // ── 服务 ──
    private readonly ConfigService _config;
    private readonly WzService _wzService;
    private readonly PaperdollService _paperdollSvc;
    private readonly MapService _mapSvc;
    private PetSettings _settings;
    private WzConfig _wzConfig;

    // ── 状态 ──
    private int _activeMainTab = 0;
    private int _activeSubTab = 0;
    private int _activeAiSubTab = 0;
    private bool _isDarkMode = true;
    private string _selectedBgMode = "tp";
    private string _previewMobId = string.Empty;
    private bool _previewIsMob = true;

    // ── 预览动画 ──
    private readonly DispatcherTimer _previewAnimTimer;
    private int _previewFrameIndex;
    private byte[]? _previewPngData;
    private SpriteStrip? _previewStrip;
    private Avalonia.Media.Imaging.Bitmap? _previewSourceBitmap;
    private Avalonia.Controls.Image? _previewSpriteImage;
    /// <summary>气泡 tab 预览 Image（与素材预览共享 _previewSourceBitmap；源重建时必须一并摘除）。</summary>
    private Avalonia.Controls.Image? _balloonPreviewImage;
    private int _previewGen;
    private string _previewAction = "";
    private List<string> _previewActions = new();

    // ── 事件 ──
    public event Action<string, string, string>? OnPetApplied; // (id, name, type: Mob/Npc)
    public event Action<CharacterAppearance>? OnPaperdollApplied;
    /// <summary>摄像机镜头大小应用（宽, 高）→ PetWindow 调整 MapViewportWindow 尺寸。</summary>
    public event Action<int, int>? OnViewportSizeApplied;
    public event Action<MapSelection>? OnBackgroundMapApplied;
    /// <summary>分层显隐 toggle（背景/地面/物件/NPC/传送门/地面线）变化后触发，PetWindow 据此重渲染桌面背景窗口。</summary>
    public event Action? OnLayerTogglesChanged;
    /// <summary>WZ 素材刷新成功（含保存后自动刷新）后触发，PetWindow 据此重载当前宠物。</summary>
    public event Action? OnWzRefreshed;

    // ── 素材桌宠 UI 引用 ──
    private TextBlock? _petNameLabel;
    private TextBox? _petIdInput;
    private ListBox? _favoriteList;
    private ListBox? _recentList;
    private TextBlock? _statusBarText;
    private Border? _cacheToast;
    private TextBlock? _cacheToastText;
    private ProgressBar? _cacheToastProgress;
    // 列表缩略图缓存（id|type → stand 首帧；胶水规则 2026-08-09：最近/收藏用真实预览图）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> _listThumbs = new();
    /// <summary>列表缩略图缓存上限（超出丢弃最早条目；S3）。</summary>
    private const int ListThumbMax = 200;
    // 素材 tab 搜索（S1：死搜索框 → 300ms 防抖过滤收藏/最近 + × 清除）
    private readonly DispatcherTimer _petSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Button? _petSearchClearBtn;
    private string _petSearchQuery = "";
    private Border? _previewCanvas;
    private Slider? _zoomSlider;
    private TextBlock? _zoomLabel;
    private DispatcherTimer? _zoomSaveTimer;
    private List<Border> _bgSwatches = new();

    // ── WZ 配置弹窗 ──
    private Border? _wzOverlay;
    private TextBox? _wzLibInput;
    private TextBox? _baseWzInput;
    private Button? _wzRefreshBtn;
    private TextBlock? _wzOverlayToast;

    // ── 主 Tab 头部 ──
    private List<TextBlock> _mainTabHeaders = new();
    private List<TextBlock> _subTabHeaders = new();
    private List<Border> _mainPanes = new();
    private List<TextBlock> _aiSubTabHeaders = new();
    private List<Border> _aiSubPanes = new();
    private List<Border> _subPanes = new();

    // ── 纸娃娃状态 ──
    private CharacterAppearance _paperdollCurrent = CreateDefaultAppearance();
    private readonly List<(string Name, CharacterAppearance Data)> _paperdollPresets = new();
    private ListBox? _presetListBox;
    private Border? _paperdollPreviewCanvas;
    private ComboBox? _genderCombo;
    private ComboBox? _earCombo;
    private Button? _hairIdInput, _faceIdInput;
    private Button? _capIdInput, _capeIdInput, _coatIdInput, _pantsIdInput, _overallIdInput;
    private Button? _faceAccessoryIdInput, _eyeAccessoryIdInput, _earringIdInput;
    private Button? _shoesIdInput, _weaponIdInput, _shieldIdInput, _gloveIdInput;
    private Button? _mountIdInput, _chairIdInput;

    // ── 背景状态 ──
    private MapSelection? _pendingMapSelection;
    private string _selectedMapId = "100000000";
    private TextBlock? _mapNameLabel;
    private Border? _mapPreviewCanvas;

    // ── 背景选图器（并入设置中心：区域树选叶子直接预览 / 大预览 / 拖 crop）──
    private readonly List<MapEntry> _bgAllMaps = new();
    private Canvas? _bgPreviewCanvas;
    private Image? _bgPreviewImage;
    private Rectangle? _bgCropRect;
    private TextBlock? _bgCropInfo;
    private MapInfo? _bgCurrentMap;
    private int _bgPreviewGen;
    private int _bgCropX, _bgCropY, _bgCropW, _bgCropH;
    private bool _bgHasCrop;
    private double _bgThumbScale = 1;
    private int _bgThumbW, _bgThumbH;
    private const int BgThumbW = 480;
    private const int BgThumbH = 360;

    // ── F2 选择历史：点一条直接应用该地图+crop（数据源 PetSettings.MapHistory，PetWindow.ApplyMapSelection 持久化）──
    private ListBox? _bgHistoryList;
    // ── F6 级联筛选：区域 → 街道 → 地图（WzService.GetMapHierarchy）──
    private TreeView? _bgRegionTree;
    private List<MapRegionNode> _bgHierarchy = new();
    private const string BgAllLabel = "全部";
    // ── F3 crop 交互：选区框 + 8 个手柄 + 移动/缩放模式 ──
    private readonly List<Rectangle> _bgCropHandles = new();
    private const double BgHandleHalf = 5;       // 手柄命中半径（像素）
    private const double BgHandleSize = 10;       // 手柄方块边长
    private const int BgNudgeStep = 16;           // 微调按钮步长（世界坐标）
    private string? _bgCropMode;                  // null | "create" | "move" | "resize"
    private string? _bgActiveHandle;              // "nw"/"n"/"ne"/"e"/"se"/"s"/"sw"/"w"
    private Point _bgCropOrigin;                  // 拖拽起点（用于 move 的 delta）
    private double _bgCropStartX, _bgCropStartY, _bgCropStartW, _bgCropStartH; // 拖拽开始时的选区几何

    // ── 气泡状态（可视化加载：真实 WZ ChatBalloon 列表 + 真实渲染预览）──
    private readonly BalloonService _balloonService;
    private List<(int Id, string Name)> _realBalloonList = new();
    private Border? _balloonPreviewCanvas;


    public SettingsWindow(ConfigService config, WzService wzService, PaperdollService paperdollSvc, MapService mapSvc, BalloonService balloonSvc, MusicCatalogService musicCatalog, MusicPlayerService musicPlayer)
    {
        _config = config;
        _wzService = wzService;
        _paperdollSvc = paperdollSvc;
        _mapSvc = mapSvc;
        _balloonService = balloonSvc;
        // 音乐模块服务（V0.2.0，A4 接线：PetWindow 从 App 容器获取后透传；末尾追加参数，现有调用方顺序不动）
        _musicCatalog = musicCatalog;
        _musicPlayer = musicPlayer;
        _settings = _config.LoadSettings();
        _wzConfig = _config.LoadWzConfig();
        _isDarkMode = _settings.Theme != "light";

        // 纸娃娃页初始显示当前主窗口搭配（2026-08-17：原固定默认外观，用户在设置中心「查看
        // 现在用的搭配」时发型/脸型/装备全被默认值顶替，看不到当前搭配；有存档则克隆恢复，
        // 无存档保持默认外观）。窗口单例复用，仅首次构造生效，后续保留用户本次编辑状态。
        try
        {
            var current = _settings.CurrentPaperdoll;
            if (current != null)
            {
                _paperdollCurrent = CloneAppearance(current);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PaperdollInit: {ex.Message}"); }

        Title = "MiniPet 设置中心";
        Width = 1020;
        Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushBg;

        // 预览动画定时器 — 必须在 BuildBody 之前初始化
        _previewAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _previewAnimTimer.Tick += OnPreviewAnimTick;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(new GridLength(50)));
        root.RowDefinitions.Add(new RowDefinition(new GridLength(36)));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        root.Children.Add(BuildTitleBar());
        Grid.SetRow(root.Children[^1], 0);
        root.Children.Add(BuildMainTabBar());
        Grid.SetRow(root.Children[^1], 1);
        var body = BuildBody();
        root.Children.Add(body);
        Grid.SetRow(root.Children[^1], 2);

        root.Children.Add(BuildWzOverlay());
        Grid.SetRowSpan(root.Children[^1], 3);

        // 缓存提示 Toast（右上角浮动，非模态；应用桌宠缓存中/完成提示，胶水规则 2026-08-09）
        _cacheToast = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(235, 42, 42, 50)),
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10),
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            ZIndex = 200
        };
        var toastStack = new StackPanel { Spacing = 6 };
        _cacheToastText = new TextBlock { Text = "正在缓存…", FontSize = 12, Foreground = BrushTxtPrimary };
        _cacheToastProgress = new ProgressBar { IsIndeterminate = true, Width = 180, Height = 4 };
        toastStack.Children.Add(_cacheToastText);
        toastStack.Children.Add(_cacheToastProgress);
        _cacheToast.Child = toastStack;
        root.Children.Add(_cacheToast);

        Content = root;

        _config.OnSettingsChanged += s =>
        {
            _settings = s;
            Dispatcher.UIThread.Post(() => RefreshFromSettings());
        };

        // WZ 未加载时禁用资源相关操作
        if (!_wzService.IsLoaded && _statusBarText != null)
        {
            _statusBarText.Text = "⚠️ WZ 未配置或加载失败 — 请先点击 ⚙️ WZ 配置 设置路径并保存";
        }

        // 音乐 tab：订阅播放服务事件（一律 Post 到 UI 线程）+ 初始控件同步（服务字段在构造函数开头已赋值）
        InitMusic();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try
        {
            // 取消关闭，改为隐藏
            e.Cancel = true;
            _previewAnimTimer.Stop();
            _petSearchDebounce.Stop();
            _balloonSearchDebounce.Stop();
            // 音乐 tab：隐藏即停表（进度条三条件之「窗口可见」丢失；IsVisible 变化事件亦会兜底停）
            _musicSearchDebounce.Stop();
            _musicProgressTimer.Stop();
            Hide();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnClosing: {ex.Message}"); }
    }

    /// <summary>
    /// 打开设置中心并切到音乐 tab（PetWindow 右键菜单「🎵 音乐」入口，设计 §11.9；A4 接线调用）。
    /// 已在音乐 tab 时 SwitchMainTab 早退，故再兜底调 OnMusicTabActivated 触发懒加载与进度条三条件重估。
    /// </summary>
    public void OpenMusicTab()
    {
        try
        {
            Show();
            Activate();
            SwitchMainTab(MusicTabIndex);
            OnMusicTabActivated();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenMusicTab: {ex.Message}"); }
    }

    private Panel BuildTitleBar()
    {
        var bar = new DockPanel { Height = 50, Margin = new Thickness(0) };

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        titlePanel.Children.Add(new TextBlock { Text = "🐾 MiniPet 桌宠", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, VerticalAlignment = VerticalAlignment.Center });
        titlePanel.Children.Add(new TextBlock { Text = "— 设置中心", FontSize = 11, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        bar.Children.Add(titlePanel);

        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        var wzBtn = new Button { Content = "⚙️ WZ 配置", FontSize = 10, Padding = new Thickness(10, 4), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(wzBtn);
        wzBtn.Click += (_, _) => ShowWzOverlay();
        rightPanel.Children.Add(wzBtn);

        // ⚠️ 深夜/白天模式切换暂隐藏（2026-08-15 记录：切换逻辑只写 _settings.Theme 未真正应用主题，
        // 且与 FluentTheme + 全局 Button hover 覆写叠加出现显示异常——待修复后恢复，勿删 _isDarkMode 相关读取逻辑）
        // var themeLabel = new TextBlock { Text = _isDarkMode ? "暗黑" : "白天", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
        // rightPanel.Children.Add(themeLabel);
        // var themeToggle = new ToggleSwitch { IsChecked = _isDarkMode, Width = 44, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        // themeToggle.IsCheckedChanged += (_, _) => { _isDarkMode = themeToggle.IsChecked ?? true; themeLabel.Text = _isDarkMode ? "暗黑" : "白天"; _settings.Theme = _isDarkMode ? "dark" : "light"; _config.SaveSettings(_settings); };
        // rightPanel.Children.Add(themeToggle);
        DockPanel.SetDock(rightPanel, Dock.Right);
        bar.Children.Add(rightPanel);
        return bar;
    }

    private Panel BuildMainTabBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 0) };
        var tabs = new[] { "📦 桌宠设置", "🔗 AI 设置", "💬 对话记录", "🎵 音乐" };
        for (int i = 0; i < tabs.Length; i++)
        {
            var idx = i;
            var tb = new TextBlock { Text = tabs[i], FontSize = 12, FontWeight = FontWeight.Medium, Padding = new Thickness(20, 8, 20, 10), Foreground = i == 0 ? BrushAccent : BrushTxtDim, Cursor = new Cursor(StandardCursorType.Hand) };
            tb.PointerPressed += (_, _) => SwitchMainTab(idx);
            _mainTabHeaders.Add(tb);
            bar.Children.Add(tb);
        }
        return bar;
    }

    private Grid BuildBody()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(24)));
        var pane0 = BuildPetSettingsPane();
        _mainPanes.Add(pane0);
        grid.Children.Add(pane0);
        var pane1 = BuildAiSettingsPane();
        pane1.IsVisible = false;
        _mainPanes.Add(pane1);
        grid.Children.Add(pane1);
        var pane2 = BuildPlaceholder("💬 对话记录", "对话记录功能开发中，敬请期待...");
        pane2.IsVisible = false;
        _mainPanes.Add(pane2);
        grid.Children.Add(pane2);
        // 第 4 个顶层 tab：🎵 音乐（SettingsWindow.Music.cs partial 构建；懒加载在 OnMusicTabActivated 触发）
        var pane3 = BuildMusicPane();
        pane3.IsVisible = false;
        _mainPanes.Add(pane3);
        grid.Children.Add(pane3);

        // 底部状态栏
        var statusBar = new Border
        {
            Height = 24,
            Background = new SolidColorBrush(Color.FromArgb(255, 22, 22, 28))
        };
        _statusBarText = new TextBlock
        {
            Text = "就绪",
            Margin = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = BrushTxtDim
        };
        statusBar.Child = _statusBarText;
        grid.Children.Add(statusBar);
        Grid.SetRow(statusBar, 1);

        return grid;
    }

    /// <summary>
    /// AI 设置主 pane（2026-08-16 重构：通用设置独立 tab 放最前——阈值/Adapter 开关/7 态映射/WZ 节点；
    /// Hermes tab 只留 Hermes 连接与 Profile；Deepseek harness tab = DSH 监控 + DSH 对话）。
    /// 子 tab：通用 / Hermes / Deepseek harness / Claude Code / Codex（后两者占位待实现）。
    /// </summary>
    private Border BuildAiSettingsPane()
    {
        var container = new DockPanel();
        var subTabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 4, 14, 0) };
        var subTabs = new[] { "通用", "Hermes", "Deepseek harness", "Claude Code", "Codex" };
        for (int i = 0; i < subTabs.Length; i++)
        {
            var idx = i;
            var tb = new TextBlock { Text = subTabs[i], FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(14, 5, 14, 7), Foreground = i == 0 ? BrushAccent : BrushTxtDim, Cursor = new Cursor(StandardCursorType.Hand) };
            tb.PointerPressed += (_, _) => SwitchAiSubTab(idx);
            _aiSubTabHeaders.Add(tb);
            subTabBar.Children.Add(tb);
        }
        DockPanel.SetDock(subTabBar, Dock.Top);

        var body = new Grid();
        var sub0 = BuildGeneralTab();
        _aiSubPanes.Add(sub0);
        body.Children.Add(sub0);
        var sub1 = BuildAgentTab();
        sub1.IsVisible = false;
        _aiSubPanes.Add(sub1);
        body.Children.Add(sub1);
        var sub2 = BuildDshChatTab();
        sub2.IsVisible = false;
        _aiSubPanes.Add(sub2);
        body.Children.Add(sub2);
        for (int i = 3; i < subTabs.Length; i++)
        {
            var sub = BuildPlaceholder("🚧", subTabs[i] + " 待实现");
            sub.IsVisible = false;
            _aiSubPanes.Add(sub);
            body.Children.Add(sub);
        }

        container.Children.Add(subTabBar);
        container.Children.Add(body);
        return new Border { Child = container };
    }

    private Border BuildPetSettingsPane()
    {
        var container = new DockPanel();
        var subTabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 4, 14, 0) };
        var subTabs = new[] { "素材桌宠设置", "纸娃娃桌宠设置", "背景设置", "聊天气泡设置" };
        for (int i = 0; i < subTabs.Length; i++)
        {
            var idx = i;
            var tb = new TextBlock { Text = subTabs[i], FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(14, 5, 14, 7), Foreground = i == 0 ? BrushAccent : BrushTxtDim, Cursor = new Cursor(StandardCursorType.Hand) };
            tb.PointerPressed += (_, _) => SwitchSubTab(idx);
            _subTabHeaders.Add(tb);
            subTabBar.Children.Add(tb);
        }
        DockPanel.SetDock(subTabBar, Dock.Top);

        var body = new Grid();
        var sub0 = BuildMaterialTab();
        _subPanes.Add(sub0);
        body.Children.Add(sub0);

        var sub1 = BuildPaperdollTab();
        sub1.IsVisible = false;
        _subPanes.Add(sub1);
        body.Children.Add(sub1);

        var sub2 = BuildBackgroundTab();
        sub2.IsVisible = false;
        _subPanes.Add(sub2);
        body.Children.Add(sub2);

        var sub3 = BuildBalloonTab();
        sub3.IsVisible = false;
        _subPanes.Add(sub3);
        body.Children.Add(sub3);

        container.Children.Add(subTabBar);
        container.Children.Add(body);
        return new Border { Child = container };
    }

    private Border BuildMaterialTab()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        // 自适应：侧栏改 Grid（搜索/按钮/标题 Auto + 列表 Star），收藏/最近列表随窗口伸缩并内部滚动（原无高度约束全展开）
        var sidebar = new Grid { Margin = new Thickness(10), RowSpacing = 8, VerticalAlignment = VerticalAlignment.Stretch };
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 搜索框
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 浏览素材库
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 重新获取素材
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // ★ 收藏 标题
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 收藏列表（Star：随窗口伸缩 + 内部滚动）
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 🕐 最近使用 标题
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 最近列表（Star：随窗口伸缩 + 内部滚动）

        var searchPanel = new Border { CornerRadius = new CornerRadius(8), Background = Brushes.White };
        var searchGrid = new Grid();
        var searchInput = new TextBox { PlaceholderText = "过滤当前列表…", FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(30, 6, 26, 6), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left };
        // 🔍 图标后添加 → 绘制在 TextBox 之上（白底不遮图标）；Padding 左侧 30 为图标留位；
        // IsHitTestVisible=false 让点击图标区域仍落到输入框（否则上层 TextBlock 会拦截焦点）
        var searchIcon = new TextBlock { Text = "🔍", FontSize = 13, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), IsHitTestVisible = false };
        // S1：死搜索框接 TextChanged → 300ms 防抖过滤收藏/最近列表
        _petSearchDebounce.Tick += (_, _) =>
        {
            _petSearchDebounce.Stop();
            try
            {
                _petSearchQuery = searchInput.Text ?? "";
                RefreshFavoriteList();
                RefreshRecentList();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PetSearch: {ex.Message}"); }
        };
        searchInput.TextChanged += (_, _) =>
        {
            _petSearchDebounce.Stop();
            _petSearchDebounce.Start();
            if (_petSearchClearBtn != null)
            {
                _petSearchClearBtn.IsVisible = !string.IsNullOrEmpty(searchInput.Text);
            }
        };
        _petSearchClearBtn = new Button
        {
            Content = "×",
            FontSize = 12,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = BrushTxtDim,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        UiStyle.ApplyHover(_petSearchClearBtn);
        _petSearchClearBtn.Click += (_, _) =>
        {
            searchInput.Text = "";
            _petSearchDebounce.Stop();
            _petSearchQuery = "";
            RefreshFavoriteList();
            RefreshRecentList();
            searchInput.Focus();
        };
        searchGrid.Children.Add(searchInput);
        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(_petSearchClearBtn);
        searchPanel.Child = searchGrid;
        Grid.SetRow(searchPanel, 0);
        sidebar.Children.Add(searchPanel);

        var browseBtn = new Button { Content = "🔍 浏览素材库", FontSize = 11, Padding = new Thickness(0, 6), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(browseBtn);
        browseBtn.Click += (_, _) => OpenMaterialBrowser();
        Grid.SetRow(browseBtn, 1);
        sidebar.Children.Add(browseBtn);

        var refreshBtn = new Button { Content = "🔄 重新获取素材", FontSize = 10, Padding = new Thickness(0, 5), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(refreshBtn);
        refreshBtn.Click += (_, _) =>
        {
            try
            {
                if (_wzService.IsLoaded)
                {
                    var mobCount = _wzService.ListMobIds().Count;
                    if (_statusBarText != null)
                    {
                        _statusBarText.Text = $"素材已就绪: {mobCount} 个怪物可用";
                    }
                }
                else
                {
                    if (_statusBarText != null)
                    {
                        _statusBarText.Text = "WZ 未加载，请先配置 WZ 路径";
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Refresh: {ex.Message}"); }
        };
        Grid.SetRow(refreshBtn, 2);
        sidebar.Children.Add(refreshBtn);

        var favLabel = new TextBlock { Text = "★ 收藏", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) };
        Grid.SetRow(favLabel, 3);
        sidebar.Children.Add(favLabel);
        _favoriteList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
        RefreshFavoriteList();
        _favoriteList.SelectionChanged += (_, _) => { if (_favoriteList.SelectedItem is FavoriteItem fi) SelectPet(fi.Name, fi.Id, fi.Type); };
        _favoriteList.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "🗑️ 取消收藏",
                    [!MenuItem.CommandParameterProperty] = new Avalonia.Data.Binding("Id"),
                }
            }
        };
        (_favoriteList.ContextMenu.Items[0] as MenuItem)!.Click += (s, _) =>
        {
            try
            {
                if (_favoriteList.SelectedItem is FavoriteItem fi)
                {
                    _settings.Favorites.RemoveAll(f => f.Id == fi.Id && f.Type == fi.Type);
                    _config.SaveSettings(_settings);
                    RefreshFavoriteList();
                    if (_statusBarText != null)
                    {
                        _statusBarText.Text = $"已取消收藏: {fi.Name}";
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Unfavorite: {ex.Message}"); }
        };
        Grid.SetRow(_favoriteList, 4);
        sidebar.Children.Add(_favoriteList);

        var recentLabel = new TextBlock { Text = "🕐 最近使用", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) };
        Grid.SetRow(recentLabel, 5);
        sidebar.Children.Add(recentLabel);
        _recentList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
        RefreshRecentList();
        _recentList.SelectionChanged += (_, _) => { if (_recentList.SelectedItem is RecentItem ri) SelectPet(ri.Name, ri.Id, ri.Type); };
        _recentList.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "🗑️ 删除",
                    [!MenuItem.CommandParameterProperty] = new Avalonia.Data.Binding("Id"),
                }
            }
        };
        (_recentList.ContextMenu.Items[0] as MenuItem)!.Click += (s, _) =>
        {
            try
            {
                if (_recentList.SelectedItem is RecentItem ri)
                {
                    _settings.RecentItems.RemoveAll(r => r.Id == ri.Id && r.Type == ri.Type);
                    _config.SaveSettings(_settings);
                    RefreshRecentList();
                    if (_statusBarText != null)
                    {
                        _statusBarText.Text = $"已删除: {ri.Name}";
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] DeleteRecent: {ex.Message}"); }
        };
        Grid.SetRow(_recentList, 6);
        sidebar.Children.Add(_recentList);

        Grid.SetColumn(sidebar, 0);
        grid.Children.Add(sidebar);

        var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        // 自适应：右栏预览容器 StackPanel → Grid（预览区 Star 行占满剩余高度，参数栏 Auto 固定），让 _previewCanvas 随窗口伸缩
        var preview = new Grid { Margin = new Thickness(14), RowSpacing = 8 };
        preview.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        preview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        // 自适应：去掉 MinHeight=400 固定下限，改由 Grid Star 行撑满（保留 MinWidth=320 防过窄）
        _previewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), MinWidth = 320, VerticalAlignment = VerticalAlignment.Stretch };
        var defaultMobId = _config.LoadSettings().CurrentMobId;
        var defaultName = !string.IsNullOrEmpty(defaultMobId) ? _wzService.GetMobName(defaultMobId) : "";
        if (string.IsNullOrEmpty(defaultName))
        {
            // 类型标注：NPC 素材显示「NPC_{id}」而非「怪物_{id}」（胶水反馈 2026-08-09）
            var det = _wzService.IsLoaded ? _wzService.DetectEntityType(defaultMobId ?? "") : "";
            defaultName = !string.IsNullOrEmpty(defaultMobId) ? $"{(det == "Npc" ? "NPC" : "怪物")}_{defaultMobId}" : "";
        }
        UpdatePreviewContent(defaultName, defaultMobId ?? "");
        Grid.SetRow(_previewCanvas, 0);
        preview.Children.Add(_previewCanvas);

        var paramBar = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        paramBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 宠物名
        paramBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // 宠物 id 输入（占满剩余宽度）
        paramBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 播放
        paramBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 收藏
        paramBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 确定应用
        _petNameLabel = new TextBlock { Text = !string.IsNullOrEmpty(defaultMobId) ? defaultName : "未选择宠物", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 4, 6, 4) };
        Grid.SetColumn(_petNameLabel, 0);
        paramBar.Children.Add(_petNameLabel);
        _petIdInput = new TextBox { Text = !string.IsNullOrEmpty(defaultMobId) ? defaultMobId : "", FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(_petIdInput, 1);
        paramBar.Children.Add(_petIdInput);

        var playBtn = new Button { Content = "▶ 播放", FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(10, 3), Background = new SolidColorBrush(InputBorder), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(playBtn);
        Grid.SetColumn(playBtn, 2);
        paramBar.Children.Add(playBtn);

        var favBtn = new Button { Content = "★ 收藏", FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(10, 3), Background = new SolidColorBrush(InputBorder), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(favBtn);
        favBtn.Click += (_, _) => ToggleFavorite();
        Grid.SetColumn(favBtn, 3);
        paramBar.Children.Add(favBtn);

        var applyBtn = new Button { Content = "确定应用", FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(14, 3), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(applyBtn);
        applyBtn.Click += (_, _) => ApplyCurrentPet(applyBtn);
        Grid.SetColumn(applyBtn, 4);
        paramBar.Children.Add(applyBtn);
        Grid.SetRow(paramBar, 1);
        preview.Children.Add(paramBar);

        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);
        return new Border { Child = grid };
    }

    private Border MakeBgSwatch(string label, string mode, IBrush swatch)
    {
        var active = mode == _selectedBgMode;
        var container = new Border { Width = 40, Cursor = new Cursor(StandardCursorType.Hand), BorderBrush = active ? BrushAccent : Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(1), Background = Brushes.Transparent };
        var stack = new StackPanel();
        var sw = new Border { Width = 40, Height = 26, CornerRadius = new CornerRadius(3), Background = swatch, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1) };
        stack.Children.Add(sw);
        stack.Children.Add(new TextBlock { Text = label, FontSize = 7, Foreground = BrushTxtDim, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
        container.Child = stack;
        container.PointerPressed += (_, _) =>
        {
            _selectedBgMode = mode;
            foreach (var s in _bgSwatches) s.BorderBrush = s == container ? BrushAccent : Brushes.Transparent;
            if (_previewCanvas != null)
            {
                _previewCanvas.Background = GetBgBrush(mode);
            }
        };
        return container;
    }

    private static IBrush GetCheckerboardBrush() => new SolidColorBrush(Color.FromArgb(13, 200, 200, 200));
    private static IBrush GetTransparentSwatch() => new SolidColorBrush(InputBorder);
    private static IBrush GetGridSwatch() => new SolidColorBrush(Color.FromArgb(13, 200, 200, 200));

    private static IBrush GetHenesysSwatch()
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Color.FromArgb(255, 45, 90, 45), 0), new(Color.FromArgb(255, 74, 140, 74), 0.5), new(Color.FromArgb(255, 107, 168, 107), 1) }
        };
    }

    private static IBrush GetMarketSwatch()
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Color.FromArgb(255, 74, 58, 32), 0), new(Color.FromArgb(255, 196, 164, 90), 0.5), new(Color.FromArgb(255, 232, 212, 138), 1) }
        };
    }

    private static IBrush GetForestSwatch()
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Color.FromArgb(255, 26, 46, 26), 0), new(Color.FromArgb(255, 42, 90, 42), 0.5), new(Color.FromArgb(255, 30, 58, 30), 1) }
        };
    }

    private static IBrush GetBgBrush(string mode) => mode switch
    {
        "gr" => GetGridSwatch(), "hn" => GetHenesysSwatch(), "mk" => GetMarketSwatch(), "ft" => GetForestSwatch(), _ => GetCheckerboardBrush(),
    };

    private static Border BuildPlaceholder(string icon, string text)
    {
        return new Border
        {
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12,
                Children = { new TextBlock { Text = icon, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = text, FontSize = 14, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center } }
            }
        };
    }

    private Border BuildWzOverlay()
    {
        _wzOverlay = new Border { Background = new SolidColorBrush(OverlayBg), IsVisible = false, ZIndex = 9999 };

        var dialogHost = new Border
        {
            Width = 500, Height = 280, Background = new SolidColorBrush(Color.FromArgb(242, 38, 38, 44)), CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(153, 80, 80, 90)), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 40, Color = Color.FromArgb(128, 0, 0, 0) }), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        var dialogStack = new StackPanel();
        var titleBar = new Grid { Height = 38, Margin = new Thickness(12, 0) };
        var dts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new[] { Color.FromArgb(255, 255, 95, 86), Color.FromArgb(255, 254, 188, 46), Color.FromArgb(255, 40, 200, 64) })
            dts.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(c) });
        titleBar.Children.Add(dts);
        titleBar.Children.Add(new TextBlock { Text = "📦 WZ 配置", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = BrushTxtSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        dialogStack.Children.Add(titleBar);
        dialogStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(153, 80, 80, 90)) });

        var form = new StackPanel { Margin = new Thickness(24, 16), Spacing = 14 };
        var wzlibRow = BuildConfigRow("wzlib 路径", "WzComparerR2.WzLib.dll", out _wzLibInput);
        _wzLibInput.Text = _wzConfig.WzLibPath;
        form.Children.Add(wzlibRow);
        var baseWzRow = BuildConfigRow("BaseWZ 路径", "Data 目录", out _baseWzInput);
        _baseWzInput.Text = _wzConfig.BaseWzPath;
        form.Children.Add(baseWzRow);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        _wzRefreshBtn = new Button { Content = "刷新当前素材", FontSize = 11, Padding = new Thickness(20, 8), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(_wzRefreshBtn);
        _wzRefreshBtn.Click += async (_, _) => await RefreshWzMaterialAsync();
        btnRow.Children.Add(_wzRefreshBtn);
        var cancelBtn = new Button { Content = "取消", FontSize = 11, Padding = new Thickness(20, 8), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(cancelBtn);
        cancelBtn.Click += (_, _) => HideWzOverlay();
        btnRow.Children.Add(cancelBtn);
        var saveBtn = new Button { Content = "保存", FontSize = 11, Padding = new Thickness(20, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(saveBtn);
        saveBtn.Click += (_, _) => SaveWzConfig();
        btnRow.Children.Add(saveBtn);
        form.Children.Add(btnRow);
        dialogStack.Children.Add(form);

        _wzOverlayToast = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 230, 196, 106)), TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(24, 0, 24, 0) };
        dialogStack.Children.Add(_wzOverlayToast);

        var footer = new Border
        {
            Height = 28, Background = Brushes.Transparent,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0), Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "💡 配置后即可浏览和选择冒险岛素材", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center } } }
        };
        dialogStack.Children.Add(footer);

        dialogHost.Child = dialogStack;
        _wzOverlay.Child = dialogHost;
        _wzOverlay.PointerPressed += (_, e) => { if (e.GetCurrentPoint(_wzOverlay).Properties.IsLeftButtonPressed) HideWzOverlay(); };
        return _wzOverlay;
    }

    private static StackPanel BuildConfigRow(string label, string hint, out TextBox input)
    {
        var row = new StackPanel { Spacing = 5 };
        var header = new DockPanel();
        header.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.Medium, Foreground = BrushTxtSecondary });
        header.Children.Add(new TextBlock { Text = hint, FontSize = 9, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Right });
        row.Children.Add(header);
        var inputRow = new DockPanel();
        input = new TextBox { PlaceholderText = $"选择 {label}…", FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), TextAlignment = TextAlignment.Left };
        var browseBtn = new Button { Content = "浏览…", FontSize = 10, Height = 28, Padding = new Thickness(10, 0), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(browseBtn);
        DockPanel.SetDock(browseBtn, Dock.Right);
        var capturedInput = input;
        browseBtn.Click += async (sender, _) =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel((Visual)sender!);
                if (topLevel == null)
            {
                return;
            }
                if (label == "wzlib 路径")
                {
                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "选择 wzlib 目录",
                        AllowMultiple = false
                    });
                    if (folders.Count > 0)
                        capturedInput.Text = folders[0].Path.LocalPath;
                }
                else if (label == "BaseWZ 路径")
                {
                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "选择 BaseWZ 目录",
                        AllowMultiple = false
                    });
                    if (folders.Count > 0)
                        capturedInput.Text = folders[0].Path.LocalPath;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Browse: {ex.Message}"); }
        };
        // 先挂浏览按钮（Dock.Right），输入框作为最后一个子元素填充剩余宽度（DockPanel LastChildFill）
        inputRow.Children.Add(browseBtn);
        inputRow.Children.Add(input);
        row.Children.Add(inputRow);
        return row;
    }

    private void ShowWzOverlay()
    {
        try { _wzConfig = _config.LoadWzConfig(); _wzLibInput!.Text = _wzConfig.WzLibPath; _baseWzInput!.Text = _wzConfig.BaseWzPath; _wzOverlay!.IsVisible = true; }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ShowWzOverlay: {ex.Message}"); }
    }

    private void HideWzOverlay()
    {
        try { _wzOverlay!.IsVisible = false; } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] HideWzOverlay: {ex.Message}"); }
    }

    private void SaveWzConfig()
    {
        try
        {
            _wzConfig.WzLibPath = _wzLibInput!.Text ?? string.Empty;
            _wzConfig.BaseWzPath = _baseWzInput!.Text ?? string.Empty;
            // wzlib 路径在进程启动早期由 WzLibRuntimeLoader 加载，保存后需重启才生效；无效路径直接提示
            var configuredDll = WzLibRuntimeLoader.ResolveDllPath(_wzConfig.WzLibPath);
            bool invalidWzLibPath = !string.IsNullOrWhiteSpace(_wzConfig.WzLibPath) && configuredDll == null;
            bool wzLibNeedsRestart = configuredDll != null
                && !string.Equals(configuredDll, WzLibRuntimeLoader.LoadedPath, StringComparison.OrdinalIgnoreCase);
            _config.SaveWzConfig(_wzConfig);
            HideWzOverlay();

            if (invalidWzLibPath)
            {
                ShowWzOverlayToast("wzlib 路径无效（找不到 WzComparerR2.WzLib.dll），已保存但不会生效");
            }
            else if (wzLibNeedsRestart)
            {
                ShowWzOverlayToast("wzlib 路径已保存，重启后生效");
            }

            if (!string.IsNullOrEmpty(_wzConfig.BaseWzPath))
            {
                // 保存后自动刷新（异步，走同一路径），内部已 try-catch
                _ = RefreshWzMaterialAsync();
            }
            else
            {
                if (_statusBarText != null) { _statusBarText.Text = "未配置 BaseWZ 路径"; }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SaveWzConfig: {ex.Message}"); }
    }

    /// <summary>异步刷新 WZ 素材（防 UI 冻结）：Task.Run 加载，回主线程更新 UI + 通知。</summary>
    private async Task RefreshWzMaterialAsync()
    {
        try
        {
            if (_wzRefreshBtn == null) { return; }
            _wzRefreshBtn.IsEnabled = false;
            _wzRefreshBtn.Content = "刷新中…";

            var libPath = _wzLibInput?.Text ?? string.Empty;
            var basePath = _baseWzInput?.Text ?? string.Empty;

            // 异步加载 WZ（目录加载耗时，不能卡 UI 线程）
            var (success, error) = await Task.Run(() => _wzService.LoadWz(libPath, basePath));

            // 回主线程更新 UI
            if (success)
            {
                var mobCount = _wzService.ListMobIds().Count;
                var mapCount = _wzService.ListMapIds().Count;
                if (_statusBarText != null) { _statusBarText.Text = $"素材刷新完成：Mob {mobCount} 个 / Map {mapCount} 个"; }
                // 刷新宠物列表
                try { LoadPetListFromWz(); }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPetListFromWz: {ex.Message}"); }
                // 通知 PetWindow 重载当前宠物
                OnWzRefreshed?.Invoke();
                // 音乐 tab：WZ 状态变化后重估灰占位；正停在音乐 tab 且目录未建则触发懒加载
                UpdateMusicWzState();
                if (_activeMainTab == MusicTabIndex)
                {
                    OnMusicTabActivated();
                }
            }
            else
            {
                var errText = WzService.GetErrorText(error ?? WzError.Unknown);
                if (_statusBarText != null) { _statusBarText.Text = $"素材刷新失败：{errText}"; }
                // 弹窗内提示，不关闭弹窗（用户可改路径重试）
                ShowWzOverlayToast($"素材刷新失败：{errText}" + (_wzService.LastWzLibWarning != null ? $"\n{_wzService.LastWzLibWarning}" : ""));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsWindow] RefreshWzMaterialAsync: {ex.Message}");
            if (_statusBarText != null) { _statusBarText.Text = $"素材刷新失败：{ex.Message}"; }
        }
        finally
        {
            if (_wzRefreshBtn != null)
            {
                _wzRefreshBtn.IsEnabled = true;
                _wzRefreshBtn.Content = "刷新当前素材";
            }
        }
    }

    /// <summary>WZ overlay 内的临时提示（2 秒后自动隐藏），保证弹窗打开时可见。</summary>
    private void ShowWzOverlayToast(string msg)
    {
        try
        {
            if (_wzOverlayToast == null) { return; }
            _wzOverlayToast.Text = msg;
            _wzOverlayToast.IsVisible = true;
            DispatcherTimer? timer = null;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    timer?.Stop();
                    if (_wzOverlayToast != null)
                    {
                        _wzOverlayToast.IsVisible = false;
                        _wzOverlayToast.Text = string.Empty;
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ShowWzOverlayToast tick: {ex.Message}"); }
            };
            timer.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ShowWzOverlayToast: {ex.Message}"); }
    }

    private void SwitchMainTab(int index)
    {
        try { if (_activeMainTab == index) return; _activeMainTab = index; for (int i = 0; i < _mainTabHeaders.Count; i++) { _mainTabHeaders[i].Foreground = i == index ? BrushAccent : BrushTxtDim; _mainPanes[i].IsVisible = i == index; } if (index == MusicTabIndex) { OnMusicTabActivated(); } else { UpdateMusicProgressTimer(); } }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SwitchMainTab: {ex.Message}"); }
    }

    private void SwitchSubTab(int index)
    {
        try { if (_activeSubTab == index) return; _activeSubTab = index; for (int i = 0; i < _subTabHeaders.Count; i++) { _subTabHeaders[i].Foreground = i == index ? BrushAccent : BrushTxtDim; _subPanes[i].IsVisible = i == index; } }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SwitchSubTab: {ex.Message}"); }
    }

    private void SwitchAiSubTab(int index)
    {
        try { if (_activeAiSubTab == index) return; _activeAiSubTab = index; for (int i = 0; i < _aiSubTabHeaders.Count; i++) { _aiSubTabHeaders[i].Foreground = i == index ? BrushAccent : BrushTxtDim; _aiSubPanes[i].IsVisible = i == index; } }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SwitchAiSubTab: {ex.Message}"); }
    }

    private void RefreshFavoriteList()
    {
        try
        {
            if (_favoriteList == null)
            {
                return;
            }
            // S1：搜索过滤（名称/ID 包含查询）
            IEnumerable<FavoriteItem> items = _settings.Favorites;
            var q = _petSearchQuery.Trim();
            if (q.Length > 0)
            {
                items = items.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Id.Contains(q));
            }
            _favoriteList.ItemsSource = items.ToList();
            _favoriteList.ItemTemplate = new FuncDataTemplate<FavoriteItem>((data, _) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3) };
                var iconBox = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), ClipToBounds = true, Child = new TextBlock { Text = "👾", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                row.Children.Add(iconBox);
                if (data != null)
                {
                    var key = data.Id + "|" + data.Type;
                    if (_listThumbs.TryGetValue(key, out var tb))
                    {
                        iconBox.Child = MakeThumbImage(tb);
                    }
                    else
                    {
                        LoadListThumbAsync(key, data.Id, data.Type, iconBox);
                    }
                }
                row.Children.Add(new StackPanel { Spacing = 1, Children = { new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 11, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255)) }, new TextBlock { [!TextBlock.TextProperty] = new Binding("Id"), FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), FontFamily = new FontFamily("SF Mono, monospace") } } });
                row.Children.Add(new TextBlock { Text = "★", FontSize = 10, Foreground = new SolidColorBrush(Colors.Gold), VerticalAlignment = VerticalAlignment.Center });
                return row;
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshFavoriteList: {ex.Message}"); }
    }

    private void RefreshRecentList()
    {
        try
        {
            if (_recentList == null)
            {
                return;
            }
            IEnumerable<RecentItem> items = _settings.RecentItems;
            // 名字修正：旧数据可能存「怪物_{id}」（NPC 被误标），刷新时用 WZ 权威名覆盖（胶水反馈 2026-08-09）
            foreach (var ri in items)
            {
                if (string.IsNullOrEmpty(ri.Name) || ri.Name.StartsWith("怪物_") || ri.Name.StartsWith("NPC_"))
                {
                    var n = _wzService.GetMobName(ri.Id);
                    if (string.IsNullOrEmpty(n))
                    {
                        n = _wzService.GetNpcName(ri.Id);
                    }
                    if (!string.IsNullOrEmpty(n))
                    {
                        ri.Name = n;
                    }
                }
            }
            // S1：搜索过滤（名称/ID 包含查询）
            var q = _petSearchQuery.Trim();
            if (q.Length > 0)
            {
                items = items.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(q));
            }
            _recentList.ItemsSource = items.ToList();
            _recentList.ItemTemplate = new FuncDataTemplate<RecentItem>((data, _) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3) };
                var iconBox = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), ClipToBounds = true, Child = new TextBlock { Text = "🦊", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                row.Children.Add(iconBox);
                if (data != null)
                {
                    var key = data.Id + "|" + data.Type;
                    if (_listThumbs.TryGetValue(key, out var tb))
                    {
                        iconBox.Child = MakeThumbImage(tb);
                    }
                    else
                    {
                        LoadListThumbAsync(key, data.Id, data.Type, iconBox);
                    }
                }
                row.Children.Add(new StackPanel { Spacing = 1, Children = { new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 11, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255)) }, new TextBlock { [!TextBlock.TextProperty] = new Binding("Id"), FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), FontFamily = new FontFamily("SF Mono, monospace") } } });
                row.Children.Add(new TextBlock { Text = "☆", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
                return row;
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshRecentList: {ex.Message}"); }
    }

    private void RefreshFromSettings()
    {
        try { RefreshFavoriteList(); RefreshRecentList(); RefreshBgHistoryList(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshFromSettings: {ex.Message}"); }
    }

    private static Image MakeThumbImage(Bitmap bmp) => new Image { Source = bmp, Width = 26, Height = 26, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>列表条目缩略图后台加载（默认动画 stand 首帧；窗口可见才更新，防 Skia 与关闭并发）。</summary>
    private void LoadListThumbAsync(string key, string id, string type, Border iconBox)
    {
        if (_wzService == null || !_wzService.IsLoaded)
        {
            return;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                var t = type switch { "mob" => "Mob", "npc" => "Npc", _ => "" };
                if (string.IsNullOrEmpty(t)) { t = _wzService.DetectEntityType(id ?? ""); }
                if (string.IsNullOrEmpty(t)) return;
                var actions = _wzService.GetActionList(id, t);
                if (actions.Count == 0)
                {
                    return;
                }
                var action = actions.Contains("stand") ? "stand" : actions[0];
                var (pngData, strip, _) = _wzService.BuildSpriteStrip(id, action, t);
                if (pngData == null || strip == null || strip.FrameWidth <= 0 || strip.FrameHeight <= 0)
                {
                    return;
                }
                using var src = SkiaSharp.SKBitmap.Decode(pngData);
                if (src == null)
                {
                    return;
                }
                int fw = Math.Min(strip.FrameWidth, src.Width);
                int fh = Math.Min(strip.FrameHeight, src.Height);
                using var crop = new SkiaSharp.SKBitmap(fw, fh);
                using (var canvas = new SkiaSharp.SKCanvas(crop))
                {
                    canvas.Clear(SkiaSharp.SKColors.Transparent);
                    canvas.DrawBitmap(src, new SkiaSharp.SKRect(0, 0, fw, fh), new SkiaSharp.SKRect(0, 0, fw, fh));
                }
                using var img = SkiaSharp.SKImage.FromBitmap(crop);
                var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray();
                using var ms = new System.IO.MemoryStream(png);
                bmp = new Bitmap(ms);
            }
            catch { bmp = null; }
            if (bmp == null)
            {
                return;
            }
            var captured = bmp;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // ⚠️ 只移除不 Dispose 旧位图：同一 Bitmap 可能正被收藏/最近列表的 Image.Source
                    // 共享引用（MakeThumbImage 直接用缓存实例），显式 Dispose 会让列表在下一布局
                    // pass 测量已释放位图 → ObjectDisposedException 崩溃（实测 SIGABRT）。
                    // 原生内存交 GC 终结器回收（与 BalloonService 瓦片缓存同结论）。
                    _listThumbs.TryRemove(key, out _);
                    _listThumbs[key] = captured;
                    TrimListThumbs();
                    if (iconBox.IsLoaded && iconBox.IsVisible && iconBox.Child is TextBlock)
                    {
                        iconBox.Child = MakeThumbImage(captured);
                    }
                }
                catch
                {
                    captured.Dispose();
                }
            });
        });
    }

    /// <summary>缩略图缓存上限：超出丢弃条目（只移除不 Dispose——共享引用场景显式 Dispose 会崩，见 LoadListThumbAsync）。</summary>
    private void TrimListThumbs()
    {
        if (_listThumbs.Count <= ListThumbMax)
        {
            return;
        }
        int excess = _listThumbs.Count - ListThumbMax;
        foreach (var kv in _listThumbs)
        {
            if (excess <= 0)
            {
                break;
            }
            if (_listThumbs.TryRemove(kv.Key, out _))
            {
                excess--;
            }
        }
    }

    /// <summary>缓存完成 Toast 延迟隐藏（非模态弹窗提示）。</summary>
    private async System.Threading.Tasks.Task HideCacheToastAfter(int ms)
    {
        await System.Threading.Tasks.Task.Delay(ms);
        Dispatcher.UIThread.Post(() =>
        {
            if (_cacheToast != null)
            {
                _cacheToast.IsVisible = false;
            }
        });
    }

    private void SelectPet(string name, string id, string type = "")
    {
        try
        {
            _petNameLabel!.Text = name; _petIdInput!.Text = id;
            _previewMobId = id ?? string.Empty;
            // 类型：WZ 检测权威（覆盖收藏/最近显式 Type——1540421/1012000 曾被误记 "mob"，胶水反馈 2026-08-09）
            if (_wzService.IsLoaded)
            {
                var det = _wzService.DetectEntityType(id ?? "");
                if (!string.IsNullOrEmpty(det)) type = det;
            }
            else if (string.IsNullOrEmpty(type))
            {
                type = "Mob";
            }
            _previewIsMob = !type.Equals("Npc", StringComparison.OrdinalIgnoreCase);
            UpdatePreviewContent(name, id);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SelectPet: {ex.Message}"); }
    }

    private void UpdatePreviewContent(string name, string id)
    {
        try
        {
            if (_previewCanvas == null)
        {
            return;
        }
            // 自适应：预览内容 StackPanel → Grid（动作行 Auto / 预览图 Star / 名称 Auto），预览图随预览区伸缩
            var stack = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, RowSpacing = 10 };
            stack.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            stack.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            stack.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // 动画预览区域
            // 自适应：去掉固定 280×280，Star 行内填满（MaxWidth/MaxHeight 保留上限防过大）
            var previewFigure = new Border { MaxWidth = 560, MaxHeight = 560, CornerRadius = new CornerRadius(10), Background = GetCheckerboardBrush(), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ClipToBounds = true };

            if (_wzService.IsLoaded)
            {
                var mobId = string.IsNullOrEmpty(id) ? _previewMobId : id;

                _previewActions = _wzService.GetActionList(mobId, _previewIsMob ? "Mob" : "Npc");
                if (_previewActions.Count > 0)
                {
                    if (!_previewActions.Contains(_previewAction))
                        _previewAction = _previewActions[0];
                    LoadPreviewActionAsync(mobId, _previewAction, () =>
                    {
                        if (_previewSpriteImage != null)
                        {
                            ShowPreviewFrame(_previewSpriteImage, 0);
                        }
                        StartPreviewAnimation();
                    });
                }

                // ── 动作切换按钮行 ──
                var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var act in _previewActions.Take(10))
                {
                    var capturedAct = act;
                    var btn = new Button
                    {
                        Content = capturedAct,
                        FontSize = 9,
                        Padding = new Thickness(6, 2),
                        Background = capturedAct == _previewAction ? BrushAccent : new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)),
                        Foreground = capturedAct == _previewAction ? Brushes.White : BrushTxtSecondary,
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(3),
                        Cursor = new Cursor(StandardCursorType.Hand),
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    UiStyle.ApplyHover(btn);
                    btn.Click += (_, _) =>
                    {
                        _previewAction = capturedAct;
                        LoadPreviewAction(mobId, capturedAct);
                        StartPreviewAnimation();
                        // 增量更新预览图，不重建面板（预览内容已改 Grid，故用 Panel 判断）
                        if (_previewCanvas?.Child is Panel sp)
                        {
                            foreach (var c in sp.Children)
                            {
                                if (c is Border b && b.Child is Image img && img.Tag?.ToString() == "previewSprite")
                                {
                                    ShowPreviewFrame(img, 0);
                                    break;
                                }
                            }
                            // 更新动作按钮样式
                            foreach (var c in sp.Children)
                            {
                                if (c is StackPanel ar)
                                {
                                    foreach (var ac in ar.Children)
                                    {
                                        if (ac is Button ab && ab.Content?.ToString() == capturedAct)
                                        { ab.Background = BrushAccent; ab.Foreground = Brushes.White; }
                                        else if (ac is Button ob)
                                        { ob.Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)); ob.Foreground = BrushTxtSecondary; }
                                    }
                                    break;
                                }
                            }
                        }
                    };
                    actionRow.Children.Add(btn);
                }
                Grid.SetRow(actionRow, 0);
                stack.Children.Add(actionRow);

                // 自适应：去掉固定 260×260，随预览图填满并等比缩放（帧由 CroppedBitmap 逐帧替换）
                var spriteImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(10) };
                spriteImage.Tag = "previewSprite";
                spriteImage.Name = "previewSprite";
                _previewSpriteImage = spriteImage;
                previewFigure.Child = spriteImage;
            }
            else
            {
                previewFigure.Child = new TextBlock { Text = "请先配置 WZ 路径", Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
            }

            Grid.SetRow(previewFigure, 1);
            stack.Children.Add(previewFigure);

            var nameBlock = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center };
            Grid.SetRow(nameBlock, 2);
            stack.Children.Add(nameBlock);
            _previewCanvas.Child = stack;

            // 启动预览动画
            StartPreviewAnimation();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdatePreview: {ex}"); }
    }

    private void LoadPreviewAction(string mobId, string action)
    {
        try
        {
            _previewPngData = _wzService.BuildSpriteStripPng(mobId, action, _previewIsMob ? "Mob" : "Npc");
            var (strip, _) = _wzService.ExportSpriteStrip(mobId, action);
            _previewStrip = strip;
            _previewAction = action;
            RebuildPreviewSourceBitmap();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPreviewAction: {ex.Message}"); }
    }

    /// <summary>从 _previewPngData 构建源 Bitmap（切换动作时重建一次，避免每帧重新解码 + 磁盘 IO）。
    /// S4：先换 Image.Source 再 Dispose 旧位图——在飞 CroppedBitmap 不再引用已释放位图。
    /// ⚠️ 必须同时清理所有共享 _previewSourceBitmap 的 Image（素材预览 + 气泡 tab 预览），
    /// 否则旧源 Dispose 后另一预览的 CroppedBitmap 悬空 → 布局测量崩（实测 SIGABRT）。</summary>
    private void RebuildPreviewSourceBitmap()
    {
        var old = _previewSourceBitmap;
        _previewSourceBitmap = null;
        // 先摘掉在飞 Image.Source（旧 CroppedBitmap 引用旧源位图）并释放，再 Dispose 旧源位图
        ClearPreviewSourceRefs();
        old?.Dispose();
        if (_previewPngData == null || _previewPngData.Length == 0)
        {
            return;
        }
        try
        {
            using var ms = new MemoryStream(_previewPngData);
            _previewSourceBitmap = new Bitmap(ms);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SrcBmp: {ex.Message}"); }
    }

    /// <summary>把引用旧预览源的 Image.Source 全部摘掉（素材预览 + 气泡 tab 预览共享同一源位图）。</summary>
    private void ClearPreviewSourceRefs()
    {
        try
        {
            // 曾作为 Image.Source 的 CroppedBitmap 只摘除不 Dispose（交 GC）——异步布局 pass
            // 可能仍持有测量请求（实测 SIGABRT，与 ShowFrame/AnimTick 同款结论）
            if (_previewSpriteImage != null)
            {
                _previewSpriteImage.Source = null;
            }
            if (_balloonPreviewImage != null)
            {
                _balloonPreviewImage.Source = null;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ClearPreviewRefs: {ex.Message}"); }
    }

    /// <summary>把精灵图条第 frameIndex 帧设到 image（CroppedBitmap 轻量包装，不重新解码、不落盘；
    /// 替换前释放旧 CroppedBitmap，防每帧泄漏）。</summary>
    private void ShowPreviewFrame(Avalonia.Controls.Image image, int frameIndex)
    {
        if (_previewSourceBitmap == null || _previewStrip == null)
        {
            return;
        }
        int fw = _previewStrip.FrameWidth;
        int fh = _previewStrip.FrameHeight;
        if (fw <= 0 || fh <= 0 || _previewStrip.FrameCount <= 0)
        {
            return;
        }
        int idx = frameIndex % _previewStrip.FrameCount;
        try
        {
            // ⚠️ 换 Source 后旧 CroppedBitmap 不显式 Dispose（交 GC 终结器回收）：Avalonia 异步布局
            // pass 可能仍持有旧 Source 的测量请求，显式 Dispose 会触发 ObjectDisposedException（实测
            // SIGABRT）。预览图小（单帧 240ms 节奏），GC 压力可接受。
            image.Source = new CroppedBitmap(_previewSourceBitmap, new PixelRect(fw * idx, 0, fw, fh));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ShowFrame: {ex.Message}"); }
    }

    /// <summary>后台解码精灵图条（打开预览时不阻塞 UI 线程），完成后回 UI 线程建 Bitmap + 回调。代际保护防快速切宠物串台。</summary>
    private void LoadPreviewActionAsync(string mobId, string action, Action? onLoaded)
    {
        var gen = ++_previewGen;
        System.Threading.Tasks.Task.Run(() =>
        {
            byte[]? png = null;
            SpriteStrip? strip = null;
            try
            {
                png = _wzService.BuildSpriteStripPng(mobId, action, _previewIsMob ? "Mob" : "Npc");
                // 修复：ExportSpriteStrip 必须带 type（否则默认 Mob——NPC 预览 strip 为 null，预览空白，胶水反馈 2026-08-09）
                var (s, _) = _wzService.ExportSpriteStrip(mobId, action, _previewIsMob ? "Mob" : "Npc");
                strip = s;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPreviewActionAsync: {ex.Message}"); }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _previewGen)
                {
                    return;
                }
                _previewPngData = png;
                _previewStrip = strip;
                _previewAction = action;
                RebuildPreviewSourceBitmap();
                onLoaded?.Invoke();
            });
        });
    }

    private void ToggleFavorite()
    {
        try
        {
            var idText = _petIdInput!.Text; var id = idText?.Trim() ?? ""; if (string.IsNullOrEmpty(id)) return; var name = _petNameLabel!.Text ?? "";
            // Type 按实际类型记录（修复：NPC 被误记 mob，胶水反馈 2026-08-09）
            var type = _previewIsMob ? "mob" : "npc";
            var existing = _settings.Favorites.FirstOrDefault(f => f.Id == id && f.Type == type);
            if (existing != null)
            {
                _settings.Favorites.Remove(existing);
            }
            else _settings.Favorites.Insert(0, new FavoriteItem { Type = type, Id = id, Name = name, AddedAt = DateTime.Now });
            _config.SaveSettings(_settings);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ToggleFavorite: {ex.Message}"); }
    }

    private void ApplyCurrentPet(Button? applyBtn = null)
    {
        try
        {
            var idText = _petIdInput!.Text; var id = idText?.Trim() ?? ""; if (string.IsNullOrEmpty(id)) return;
            var name = _petNameLabel!.Text ?? "";

            // 手动输入 id：自动检测类型（Npc/Mob），覆盖 _previewIsMob（1540802 NPC 修复，胶水反馈 2026-08-09）
            if (_wzService.IsLoaded)
            {
                var det = _wzService.DetectEntityType(id);
                if (!string.IsNullOrEmpty(det)) _previewIsMob = !det.Equals("Npc", StringComparison.OrdinalIgnoreCase);
            }

            // ── 立即反馈（胶水规则 2026-08-09）：按钮禁用变灰 + 状态栏明确「正在缓存」──
            if (applyBtn != null)
            {
                applyBtn.IsEnabled = false;
                applyBtn.Content = "⏳ 缓存中…";
                applyBtn.Opacity = 0.55;
            }
            if (_statusBarText != null)
            {
                _statusBarText.Text = $"⏳ 正在缓存素材: {name}（{id}），首次加载较慢，请稍候…";
            }
            // 弹窗 Toast 提示（右上角浮动，非模态）
            if (_cacheToast != null)
            {
                if (_cacheToastText != null)
                {
                    _cacheToastText.Text = $"⏳ 正在缓存素材: {name}（{id}）\n首次加载较慢，请稍候…";
                }
                if (_cacheToastProgress != null)
                {
                    _cacheToastProgress.IsVisible = true;
                }
                _cacheToast.IsVisible = true;
            }

            // ── 后台线程预加载所有动作到磁盘缓存 ──
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (_wzService.IsLoaded)
                    {
                        var actions = _wzService.GetActionList(id, _previewIsMob ? "Mob" : "Npc");
                        var preloadOrder = new[] { "stand", "move", "fly", "walk", "jump" };
                        var ordered = preloadOrder.Where(actions.Contains).Concat(actions.Except(preloadOrder)).ToList();

                        foreach (var action in ordered)
                        {
                            // 检查磁盘缓存是否存在
                            var cacheKey = $"{id}_{action}";
                            // 用 CacheManager 检查
                            // 直接调用 SpriteService 的缓存逻辑
                            // 先构建并保存到磁盘
                            var pngData = _wzService.BuildSpriteStripPng(id, action, _previewIsMob ? "Mob" : "Npc");
                            if (pngData == null)
                            {
                                continue;
                            }
                            var (strip, config) = _wzService.ExportSpriteStrip(id, action);
                            if (strip == null || config == null)
                            {
                                continue;
                            }

                            var configJson = System.Text.Json.JsonSerializer.Serialize(config);
                            var configBytes = System.Text.Encoding.UTF8.GetBytes(configJson);

                            // 写入磁盘缓存
                            var cacheManager = new CacheManager();
                            cacheManager.SaveSpriteStrip(id, action, pngData, configBytes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SettingsWindow] PreloadCache: {ex.Message}");
                }

                // ── UI 线程：完成 ──
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _settings.CurrentMobId = id;
                        _settings.CurrentEntityType = _previewIsMob ? "mob" : "npc";
                        var existing = _settings.RecentItems.FirstOrDefault(r => r.Id == id);
                        if (existing != null)
                        {
                            existing.LastUsedAt = DateTime.Now;
                        }
                        else
                        {
                            _settings.RecentItems.Insert(0, new RecentItem { Type = _previewIsMob ? "mob" : "npc", Id = id, Name = name, LastUsedAt = DateTime.Now });
                            if (_settings.RecentItems.Count > 20)
                                _settings.RecentItems = _settings.RecentItems.Take(20).ToList();
                        }
                        _config.SaveSettings(_settings);
                        _previewAnimTimer.Stop();

                        if (applyBtn != null)
                        {
                            applyBtn.IsEnabled = true;
                            applyBtn.Content = "确定应用";
                            applyBtn.Opacity = 1;
                        }
                        if (_statusBarText != null)
                        {
                            _statusBarText.Text = $"✅ 预加载完成，应用 {name}";
                        }
                        // 弹窗 Toast 完成态：进度条隐藏 + 文案 ✅，1.5s 后关闭（窗口即将 Hide）
                        if (_cacheToast != null)
                        {
                            if (_cacheToastText != null)
                            {
                                _cacheToastText.Text = $"✅ 缓存完成，应用 {name}";
                            }
                            if (_cacheToastProgress != null)
                            {
                                _cacheToastProgress.IsVisible = false;
                            }
                            _ = HideCacheToastAfter(1500);
                        }

                        OnPetApplied?.Invoke(id, name, _previewIsMob ? "Mob" : "Npc");
                        Hide();
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyPost: {ex.Message}"); }
                });
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyCurrentPet: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 🎎 纸娃娃桌宠 Tab
    // ═══════════════════════════════════════════
}
