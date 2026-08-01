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
    private int _previewGen;
    private string _previewAction = "";
    private List<string> _previewActions = new();

    // ── 事件 ──
    public event Action<string, string>? OnPetApplied;
    public event Action<CharacterAppearance>? OnPaperdollApplied;
    public event Action<MapSelection>? OnBackgroundMapApplied;
    /// <summary>分层显隐 toggle（背景/地面/物件/NPC/传送门/地面线）变化后触发，PetWindow 据此重渲染桌面背景窗口。</summary>
    public event Action? OnLayerTogglesChanged;

    // ── 素材桌宠 UI 引用 ──
    private TextBlock? _petNameLabel;
    private TextBox? _petIdInput;
    private ListBox? _favoriteList;
    private ListBox? _recentList;
    private TextBlock? _statusBarText;
    private Border? _previewCanvas;
    private Slider? _zoomSlider;
    private TextBlock? _zoomLabel;
    private DispatcherTimer? _zoomSaveTimer;
    private List<Border> _bgSwatches = new();

    // ── WZ 配置弹窗 ──
    private Border? _wzOverlay;
    private TextBox? _wzLibInput;
    private TextBox? _baseWzInput;

    // ── 主 Tab 头部 ──
    private List<TextBlock> _mainTabHeaders = new();
    private List<TextBlock> _subTabHeaders = new();
    private List<Border> _mainPanes = new();
    private List<Border> _subPanes = new();

    // ── 纸娃娃状态 ──
    private CharacterAppearance _paperdollCurrent = CreateDefaultAppearance();
    private readonly List<(string Name, CharacterAppearance Data)> _paperdollPresets = new();
    private ListBox? _presetListBox;
    private Border? _paperdollPreviewCanvas;
    private ComboBox? _genderCombo;
    private ComboBox? _earCombo;
    private TextBox? _hairIdInput, _faceIdInput;
    private TextBox? _capIdInput, _capeIdInput, _coatIdInput, _pantsIdInput;
    private TextBox? _shoesIdInput, _weaponIdInput, _shieldIdInput, _gloveIdInput;
    private TextBox? _mountIdInput, _chairIdInput;

    // ── 背景状态 ──
    private MapSelection? _pendingMapSelection;
    private string _selectedMapId = "100000000";
    private TextBlock? _mapNameLabel;
    private Border? _mapPreviewCanvas;

    // ── 背景选图器（并入设置中心，端口自 MapBrowserWindow：搜索/列表/分页/大预览/拖 crop）──
    private readonly List<MapEntry> _bgAllMaps = new();
    private List<MapEntry> _bgFiltered = new();
    private int _bgPage = 1;
    private const int BgPageSize = 10;
    private int _bgTotalPages = 1;
    private TextBox? _bgSearchInput;
    private ListBox? _bgMapList;
    private TextBlock? _bgPageLabel;
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
    private ComboBox? _bgRegionCombo;
    private ComboBox? _bgStreetCombo;
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

    // ── 气泡状态 ──
    private readonly List<(int Id, string Name, Color Color, Color BgColor)> _balloonPresets = new()
    {
        (1, "经典白", Color.FromArgb(255, 255, 255, 255), Color.FromArgb(220, 50, 50, 60)),
        (2, "樱花粉", Color.FromArgb(255, 255, 200, 210), Color.FromArgb(220, 80, 50, 60)),
        (3, "阳光黄", Color.FromArgb(255, 255, 245, 180), Color.FromArgb(220, 90, 80, 40)),
        (4, "天空蓝", Color.FromArgb(255, 180, 220, 255), Color.FromArgb(220, 40, 60, 100)),
        (5, "薄荷绿", Color.FromArgb(255, 200, 240, 220), Color.FromArgb(220, 40, 80, 60)),
        (6, "薰衣紫", Color.FromArgb(255, 220, 200, 240), Color.FromArgb(220, 70, 50, 90)),
    };
    private int _selectedBalloonId = 1;
    private TextBlock? _balloonPreviewText;
    private Border? _balloonPreviewCanvas;


    public SettingsWindow(ConfigService config, WzService wzService, PaperdollService paperdollSvc, MapService mapSvc)
    {
        _config = config;
        _wzService = wzService;
        _paperdollSvc = paperdollSvc;
        _mapSvc = mapSvc;
        _settings = _config.LoadSettings();
        _wzConfig = _config.LoadWzConfig();
        _isDarkMode = _settings.Theme != "light";

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
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try
        {
            // 取消关闭，改为隐藏
            e.Cancel = true;
            _previewAnimTimer.Stop();
            Hide();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnClosing: {ex.Message}"); }
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
        wzBtn.Click += (_, _) => ShowWzOverlay();
        rightPanel.Children.Add(wzBtn);

        var themeLabel = new TextBlock { Text = _isDarkMode ? "暗黑" : "白天", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
        rightPanel.Children.Add(themeLabel);

        var themeToggle = new ToggleSwitch { IsChecked = _isDarkMode, Width = 44, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        themeToggle.IsCheckedChanged += (_, _) => { _isDarkMode = themeToggle.IsChecked ?? true; themeLabel.Text = _isDarkMode ? "暗黑" : "白天"; _settings.Theme = _isDarkMode ? "dark" : "light"; _config.SaveSettings(_settings); };
        rightPanel.Children.Add(themeToggle);
        DockPanel.SetDock(rightPanel, Dock.Right);
        bar.Children.Add(rightPanel);
        return bar;
    }

    private Panel BuildMainTabBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 0) };
        var tabs = new[] { "📦 桌宠设置", "🔗 AI 设置", "💬 对话记录" };
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
        var pane1 = BuildPlaceholder("🔗 AI 设置", "AI 设置功能开发中，敬请期待...");
        pane1.IsVisible = false;
        _mainPanes.Add(pane1);
        grid.Children.Add(pane1);
        var pane2 = BuildPlaceholder("💬 对话记录", "对话记录功能开发中，敬请期待...");
        pane2.IsVisible = false;
        _mainPanes.Add(pane2);
        grid.Children.Add(pane2);

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

        var sidebar = new DockPanel();
        var sidebarContent = new StackPanel { Margin = new Thickness(10), Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };

        var searchPanel = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), Background = BrushInputBg, Height = 30 };
        var searchGrid = new Grid();
        var searchIcon = new TextBlock { Text = "🔍", FontSize = 13, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var searchInput = new TextBox { PlaceholderText = "过滤当前列表…", FontSize = 12, Foreground = BrushTxtPrimary, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(30, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left };
        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(searchInput);
        searchPanel.Child = searchGrid;
        sidebarContent.Children.Add(searchPanel);

        var segment = new Border { CornerRadius = new CornerRadius(5), BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), Height = 28 };
        var segGrid = new Grid();
        segGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        segGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var mobBtn = MakeSegmentButton("怪物", true, 0);
        var npcBtn = MakeSegmentButton("NPC", false, 1);
        Grid.SetColumn(mobBtn, 0);
        Grid.SetColumn(npcBtn, 1);
        segGrid.Children.Add(mobBtn);
        segGrid.Children.Add(npcBtn);
        segment.Child = segGrid;
        sidebarContent.Children.Add(segment);

        var browseBtn = new Button { Content = "🔍 浏览素材库", FontSize = 11, Padding = new Thickness(0, 6), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        browseBtn.Click += (_, _) => OpenMaterialBrowser();
        sidebarContent.Children.Add(browseBtn);

        var refreshBtn = new Button { Content = "🔄 重新获取素材", FontSize = 10, Padding = new Thickness(0, 5), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        refreshBtn.Click += (_, _) =>
        {
            try
            {
                if (_wzService.IsLoaded)
                {
                    var mobCount = _wzService.ListMobIds().Count;
                    if (_statusBarText != null) _statusBarText.Text = $"素材已就绪: {mobCount} 个怪物可用";
                }
                else
                {
                    if (_statusBarText != null) _statusBarText.Text = "WZ 未加载，请先配置 WZ 路径";
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Refresh: {ex.Message}"); }
        };
        sidebarContent.Children.Add(refreshBtn);

        sidebarContent.Children.Add(new TextBlock { Text = "★ 收藏", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        _favoriteList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
        RefreshFavoriteList();
        _favoriteList.SelectionChanged += (_, _) => { if (_favoriteList.SelectedItem is FavoriteItem fi) SelectPet(fi.Name, fi.Id); };
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
                    if (_statusBarText != null) _statusBarText.Text = $"已取消收藏: {fi.Name}";
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Unfavorite: {ex.Message}"); }
        };
        sidebarContent.Children.Add(_favoriteList);

        sidebarContent.Children.Add(new TextBlock { Text = "🕐 最近使用", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        _recentList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
        RefreshRecentList();
        _recentList.SelectionChanged += (_, _) => { if (_recentList.SelectedItem is RecentItem ri) SelectPet(ri.Name, ri.Id); };
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
                    if (_statusBarText != null) _statusBarText.Text = $"已删除: {ri.Name}";
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] DeleteRecent: {ex.Message}"); }
        };
        sidebarContent.Children.Add(_recentList);

        sidebar.Children.Add(new ScrollViewer { Content = sidebarContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(sidebar, 0);
        grid.Children.Add(sidebar);

        var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        var preview = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        _previewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), MinHeight = 400, MinWidth = 320, VerticalAlignment = VerticalAlignment.Stretch };
        var defaultMobId = _config.LoadSettings().CurrentMobId;
        var defaultName = !string.IsNullOrEmpty(defaultMobId) ? _wzService.GetMobName(defaultMobId) : "";
        if (string.IsNullOrEmpty(defaultName)) defaultName = !string.IsNullOrEmpty(defaultMobId) ? $"怪物_{defaultMobId}" : "";
        UpdatePreviewContent(defaultName, defaultMobId ?? "");
        preview.Children.Add(_previewCanvas);

        var paramBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _petNameLabel = new TextBlock { Text = !string.IsNullOrEmpty(defaultMobId) ? defaultName : "未选择宠物", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 4, 6, 4) };
        paramBar.Children.Add(_petNameLabel);
        _petIdInput = new TextBox { Text = !string.IsNullOrEmpty(defaultMobId) ? defaultMobId : "", Width = 80, FontSize = 11, FontFamily = new FontFamily("SF Mono, monospace"), Foreground = new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3), TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        paramBar.Children.Add(_petIdInput);

        var playBtn = new Button { Content = "▶ 播放", FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(10, 3), Background = new SolidColorBrush(InputBorder), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        paramBar.Children.Add(playBtn);

        var favBtn = new Button { Content = "★ 收藏", FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(10, 3), Background = new SolidColorBrush(InputBorder), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        favBtn.Click += (_, _) => ToggleFavorite();
        paramBar.Children.Add(favBtn);

        var applyBtn = new Button { Content = "确定应用", FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(14, 3), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        applyBtn.Click += (_, _) => ApplyCurrentPet();
        paramBar.Children.Add(applyBtn);
        preview.Children.Add(paramBar);

        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        zoomRow.Children.Add(new TextBlock { Text = "缩放", FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
        var initScale = _settings.WindowScale > 0 ? _settings.WindowScale : 1.0;
        if (initScale < 0.3) initScale = 0.3; else if (initScale > 2.0) initScale = 2.0;
        _zoomSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _zoomSaveTimer.Tick += (_, _) => { _zoomSaveTimer!.Stop(); try { _config.SaveSettings(_settings); } catch { } };
        _zoomSlider = new Slider { Minimum = 0.3, Maximum = 2.0, Value = initScale, Width = 120, Height = 36 };
        _zoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            var v = _zoomSlider.Value;
            if (_zoomLabel != null) _zoomLabel.Text = $"{(int)(v * 100)}%";
            _settings.WindowScale = v;
            _zoomSaveTimer.Stop();
            _zoomSaveTimer.Start();
        };
        zoomRow.Children.Add(_zoomSlider);
        _zoomLabel = new TextBlock { Text = $"{(int)(initScale * 100)}%", FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Width = 30 };
        zoomRow.Children.Add(_zoomLabel);
        preview.Children.Add(zoomRow);

        var bgRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
        bgRow.Children.Add(new TextBlock { Text = "背景设置", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var bgModes = new[] { ("透明", "tp", GetTransparentSwatch()), ("网格", "gr", GetGridSwatch()), ("弓箭手村", "hn", GetHenesysSwatch()), ("市场", "mk", GetMarketSwatch()), ("森林", "ft", GetForestSwatch()) };
        foreach (var (label, mode, swatchBrush) in bgModes)
        {
            var swatch = MakeBgSwatch(label, mode, swatchBrush);
            _bgSwatches.Add(swatch);
            bgRow.Children.Add(swatch);
        }
        preview.Children.Add(bgRow);

        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);
        return new Border { Child = grid };
    }

    private Button MakeSegmentButton(string text, bool active, int index)
    {
        return new Button { Content = text, FontSize = 10, FontWeight = FontWeight.Medium, Padding = new Thickness(0, 3), Background = active ? new SolidColorBrush(Color.FromArgb(38, 94, 156, 255)) : Brushes.Transparent, Foreground = active ? BrushAccent : new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
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
            if (_previewCanvas != null) _previewCanvas.Background = GetBgBrush(mode);
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
        var cancelBtn = new Button { Content = "取消", FontSize = 11, Padding = new Thickness(20, 8), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        cancelBtn.Click += (_, _) => HideWzOverlay();
        btnRow.Children.Add(cancelBtn);
        var saveBtn = new Button { Content = "保存", FontSize = 11, Padding = new Thickness(20, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        saveBtn.Click += (_, _) => SaveWzConfig();
        btnRow.Children.Add(saveBtn);
        form.Children.Add(btnRow);
        dialogStack.Children.Add(form);

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
        input = new TextBox { PlaceholderText = $"选择 {label}…", Height = 32, FontSize = 12, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0), TextAlignment = TextAlignment.Left };
        inputRow.Children.Add(input);
        var browseBtn = new Button { Content = "浏览…", FontSize = 10, Height = 28, Padding = new Thickness(10, 0), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(browseBtn, Dock.Right);
        var capturedInput = input;
        browseBtn.Click += async (sender, _) =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel((Visual)sender!);
                if (topLevel == null) return;
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
        inputRow.Children.Add(browseBtn);
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
            _config.SaveWzConfig(_wzConfig);
            HideWzOverlay();

            // 尝试加载 WZ 文件
            if (!string.IsNullOrEmpty(_wzConfig.BaseWzPath))
            {
                var (success, error) = _wzService.LoadWz(_wzConfig.WzLibPath, _wzConfig.BaseWzPath);
                if (success)
                {
                    Console.WriteLine($"[SettingsWindow] WZ 加载成功");
                    if (_statusBarText != null) _statusBarText.Text = "WZ 加载完成";
                    // 刷新宠物列表
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { LoadPetListFromWz(); }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPetListFromWz: {ex.Message}"); }
                    });
                }
                else
                {
                    Console.Error.WriteLine($"[SettingsWindow] WZ 加载失败: {error}");
                    if (_statusBarText != null) _statusBarText.Text = $"WZ 加载失败: {error}";
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SaveWzConfig: {ex.Message}"); }
    }

    private void SwitchMainTab(int index)
    {
        try { if (_activeMainTab == index) return; _activeMainTab = index; for (int i = 0; i < _mainTabHeaders.Count; i++) { _mainTabHeaders[i].Foreground = i == index ? BrushAccent : BrushTxtDim; _mainPanes[i].IsVisible = i == index; } }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SwitchMainTab: {ex.Message}"); }
    }

    private void SwitchSubTab(int index)
    {
        try { if (_activeSubTab == index) return; _activeSubTab = index; for (int i = 0; i < _subTabHeaders.Count; i++) { _subTabHeaders[i].Foreground = i == index ? BrushAccent : BrushTxtDim; _subPanes[i].IsVisible = i == index; } }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SwitchSubTab: {ex.Message}"); }
    }

    private void RefreshFavoriteList()
    {
        try
        {
            if (_favoriteList == null) return;
            var items = _settings.Favorites;
            _favoriteList.ItemsSource = items;
            _favoriteList.ItemTemplate = new FuncDataTemplate<FavoriteItem>((_, _) =>
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3),
                    Children = { new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), Child = new TextBlock { Text = "👾", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }, new StackPanel { Spacing = 1, Children = { new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 11, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255)) }, new TextBlock { [!TextBlock.TextProperty] = new Binding("Id"), FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), FontFamily = new FontFamily("SF Mono, monospace") } } }, new TextBlock { Text = "★", FontSize = 10, Foreground = new SolidColorBrush(Colors.Gold), VerticalAlignment = VerticalAlignment.Center } }
                });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshFavoriteList: {ex.Message}"); }
    }

    private void RefreshRecentList()
    {
        try
        {
            if (_recentList == null) return;
            var items = _settings.RecentItems;
            _recentList.ItemsSource = items;
            _recentList.ItemTemplate = new FuncDataTemplate<RecentItem>((_, _) =>
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3),
                    Children = { new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), Child = new TextBlock { Text = "🦊", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }, new StackPanel { Spacing = 1, Children = { new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 11, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255)) }, new TextBlock { [!TextBlock.TextProperty] = new Binding("Id"), FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), FontFamily = new FontFamily("SF Mono, monospace") } } }, new TextBlock { Text = "☆", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center } }
                });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshRecentList: {ex.Message}"); }
    }

    private void RefreshFromSettings()
    {
        try { RefreshFavoriteList(); RefreshRecentList(); RefreshBgHistoryList(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshFromSettings: {ex.Message}"); }
    }

    private void SelectPet(string name, string id)
    {
        try
        {
            _petNameLabel!.Text = name; _petIdInput!.Text = id;
            _previewMobId = id ?? string.Empty;
            _previewIsMob = true;
            UpdatePreviewContent(name, id);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SelectPet: {ex.Message}"); }
    }

    private void UpdatePreviewContent(string name, string id)
    {
        try
        {
            if (_previewCanvas == null) return;
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10 };

            // 动画预览区域
            var previewFigure = new Border { Width = 280, Height = 280, CornerRadius = new CornerRadius(10), Background = GetCheckerboardBrush(), HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };

            if (_wzService.IsLoaded)
            {
                var mobId = string.IsNullOrEmpty(id) ? _previewMobId : id;

                _previewActions = _wzService.GetActionList(mobId);
                if (_previewActions.Count > 0)
                {
                    if (!_previewActions.Contains(_previewAction))
                        _previewAction = _previewActions[0];
                    LoadPreviewActionAsync(mobId, _previewAction, () =>
                    {
                        if (_previewSpriteImage != null) ShowPreviewFrame(_previewSpriteImage, 0);
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
                    btn.Click += (_, _) =>
                    {
                        _previewAction = capturedAct;
                        LoadPreviewAction(mobId, capturedAct);
                        StartPreviewAnimation();
                        // 增量更新预览图，不重建面板
                        if (_previewCanvas?.Child is StackPanel sp)
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
                stack.Children.Add(actionRow);

                var spriteImage = new Image { Stretch = Stretch.Uniform, Width = 260, Height = 260, Margin = new Thickness(10) };
                spriteImage.Tag = "previewSprite";
                spriteImage.Name = "previewSprite";
                _previewSpriteImage = spriteImage;
                previewFigure.Child = spriteImage;
            }
            else
            {
                previewFigure.Child = new TextBlock { Text = "请先配置 WZ 路径", Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
            }

            stack.Children.Add(previewFigure);

            stack.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center });
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
            _previewPngData = _wzService.BuildSpriteStripPng(mobId, action);
            var (strip, _) = _wzService.ExportSpriteStrip(mobId, action);
            _previewStrip = strip;
            _previewAction = action;
            RebuildPreviewSourceBitmap();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPreviewAction: {ex.Message}"); }
    }

    /// <summary>从 _previewPngData 构建源 Bitmap（切换动作时重建一次，避免每帧重新解码 + 磁盘 IO）。</summary>
    private void RebuildPreviewSourceBitmap()
    {
        _previewSourceBitmap?.Dispose();
        _previewSourceBitmap = null;
        if (_previewPngData == null || _previewPngData.Length == 0) return;
        try { using var ms = new MemoryStream(_previewPngData); _previewSourceBitmap = new Bitmap(ms); }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SrcBmp: {ex.Message}"); }
    }

    /// <summary>把精灵图条第 frameIndex 帧设到 image（CroppedBitmap 轻量包装，不重新解码、不落盘）。</summary>
    private void ShowPreviewFrame(Avalonia.Controls.Image image, int frameIndex)
    {
        if (_previewSourceBitmap == null || _previewStrip == null) return;
        int fw = _previewStrip.FrameWidth;
        int fh = _previewStrip.FrameHeight;
        if (fw <= 0 || fh <= 0 || _previewStrip.FrameCount <= 0) return;
        int idx = frameIndex % _previewStrip.FrameCount;
        try { image.Source = new CroppedBitmap(_previewSourceBitmap, new PixelRect(fw * idx, 0, fw, fh)); }
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
                png = _wzService.BuildSpriteStripPng(mobId, action);
                var (s, _) = _wzService.ExportSpriteStrip(mobId, action);
                strip = s;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPreviewActionAsync: {ex.Message}"); }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _previewGen) return;
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
            var existing = _settings.Favorites.FirstOrDefault(f => f.Id == id && f.Type == "mob");
            if (existing != null) _settings.Favorites.Remove(existing);
            else _settings.Favorites.Insert(0, new FavoriteItem { Type = "mob", Id = id, Name = name, AddedAt = DateTime.Now });
            _config.SaveSettings(_settings);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ToggleFavorite: {ex.Message}"); }
    }

    private void ApplyCurrentPet()
    {
        try
        {
            var idText = _petIdInput!.Text; var id = idText?.Trim() ?? ""; if (string.IsNullOrEmpty(id)) return;
            var name = _petNameLabel!.Text ?? "";

            // ── 显示 loading ──
            if (_statusBarText != null) _statusBarText.Text = "⏳ 正在预加载素材到本地缓存...";

            // ── 后台线程预加载所有动作到磁盘缓存 ──
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (_wzService.IsLoaded)
                    {
                        var actions = _wzService.GetActionList(id);
                        var preloadOrder = new[] { "stand", "move", "fly", "walk", "jump" };
                        var ordered = preloadOrder.Where(actions.Contains).Concat(actions.Except(preloadOrder)).ToList();

                        foreach (var action in ordered)
                        {
                            // 检查磁盘缓存是否存在
                            var cacheKey = $"{id}_{action}";
                            // 用 CacheManager 检查
                            // 直接调用 SpriteService 的缓存逻辑
                            // 先构建并保存到磁盘
                            var pngData = _wzService.BuildSpriteStripPng(id, action);
                            if (pngData == null) continue;
                            var (strip, config) = _wzService.ExportSpriteStrip(id, action);
                            if (strip == null || config == null) continue;

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
                        _settings.CurrentEntityType = "mob";
                        var existing = _settings.RecentItems.FirstOrDefault(r => r.Id == id);
                        if (existing != null) existing.LastUsedAt = DateTime.Now;
                        else
                        {
                            _settings.RecentItems.Insert(0, new RecentItem { Type = "mob", Id = id, Name = name, LastUsedAt = DateTime.Now });
                            if (_settings.RecentItems.Count > 20)
                                _settings.RecentItems = _settings.RecentItems.Take(20).ToList();
                        }
                        _config.SaveSettings(_settings);
                        _previewAnimTimer.Stop();

                        if (_statusBarText != null) _statusBarText.Text = $"✅ 预加载完成，应用 {name}";

                        OnPetApplied?.Invoke(id, name);
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
