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

public class SettingsWindow : Window
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
    private Border BuildPaperdollTab()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(280)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var sidebar = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = BuildPaperdollSidebar() };
            Grid.SetColumn(sidebar, 0);
            grid.Children.Add(sidebar);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            _paperdollPreviewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Stretch };
            UpdatePaperdollPreview();
            Grid.SetColumn(_paperdollPreviewCanvas, 2);
            grid.Children.Add(_paperdollPreviewCanvas);
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildPaperdollTab: {ex.Message}"); return new Border(); }
    }

    private StackPanel BuildPaperdollSidebar()
    {
        var content = new StackPanel { Margin = new Thickness(10), Spacing = 8 };

        content.Children.Add(new TextBlock { Text = "📋 预设管理", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 0, 0, 0) });
        var presetBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        presetBtnRow.Children.Add(MakeSmallBtn("新 建", () => NewPaperdollPreset()));
        presetBtnRow.Children.Add(MakeSmallBtn("保 存", () => SavePaperdollPreset()));
        presetBtnRow.Children.Add(MakeSmallBtn("加 载", () => LoadPaperdollPreset()));
        content.Children.Add(presetBtnRow);

        _presetListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Height = 100, Margin = new Thickness(-10, 0, -10, 0) };
        RefreshPresetList();
        content.Children.Add(_presetListBox);

        content.Children.Add(new TextBlock { Text = "👤 基础设置", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });

        var genderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        genderRow.Children.Add(new TextBlock { Text = "性别", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _genderCombo = new ComboBox { Width = 100, Height = 26, FontSize = 11, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ItemsSource = new[] { "男 (0)", "女 (1)" }, SelectedIndex = _paperdollCurrent.Gender };
        _genderCombo.SelectionChanged += (_, _) => { try { _paperdollCurrent.Gender = _genderCombo.SelectedIndex; UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Gender: {ex.Message}"); } };
        genderRow.Children.Add(_genderCombo);
        content.Children.Add(genderRow);

        var earRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        earRow.Children.Add(new TextBlock { Text = "耳朵", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _earCombo = new ComboBox { Width = 100, Height = 26, FontSize = 11, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ItemsSource = new[] { "人类", "精灵", "精灵(Lef)", "高等精灵" }, SelectedIndex = (int)_paperdollCurrent.Ear };
        _earCombo.SelectionChanged += (_, _) => { try { _paperdollCurrent.Ear = (EarType)(_earCombo?.SelectedIndex ?? 0); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Ear: {ex.Message}"); } };
        earRow.Children.Add(_earCombo);
        content.Children.Add(earRow);

        content.Children.Add(BuildEquipRow("发 型", out _hairIdInput, "30000"));
        content.Children.Add(BuildEquipRow("脸 型", out _faceIdInput, "20000"));

        content.Children.Add(new TextBlock { Text = "🛡️ 装备栏", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        content.Children.Add(BuildEquipRow("帽 子", out _capIdInput, "10000"));
        content.Children.Add(BuildEquipRow("披 风", out _capeIdInput, "11000"));
        content.Children.Add(BuildEquipRow("上 衣", out _coatIdInput, "10400"));
        content.Children.Add(BuildEquipRow("裤 子", out _pantsIdInput, "10600"));
        content.Children.Add(BuildEquipRow("鞋 子", out _shoesIdInput, "10700"));
        content.Children.Add(BuildEquipRow("武 器", out _weaponIdInput, "13000"));
        content.Children.Add(BuildEquipRow("盾 牌", out _shieldIdInput, "10900"));
        content.Children.Add(BuildEquipRow("手 套", out _gloveIdInput, "10800"));

        content.Children.Add(new TextBlock { Text = "🐴 坐骑 / 椅子", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        content.Children.Add(BuildEquipRow("坐 骑", out _mountIdInput, "1900000"));
        content.Children.Add(BuildEquipRow("椅 子", out _chairIdInput, "3010000"));

        var applyBtn = new Button { Content = "预览", FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(0, 8), Background = new SolidColorBrush(InputBorder), Foreground = BrushTxtSecondary, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        applyBtn.Click += (_, _) => { try { SyncPaperdollFromInputs(); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyPaperdoll: {ex.Message}"); } };
        content.Children.Add(applyBtn);

        var applyMainBtn = new Button { Content = "✅ 应用到主窗口", FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(0, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        applyMainBtn.Click += (_, _) => { try { ApplyPaperdoll(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyMain: {ex.Message}"); } };
        content.Children.Add(applyMainBtn);

        // 用默认外观填充输入框
        SyncInputsFromPaperdoll();
        return content;
    }

    private static StackPanel BuildEquipRow(string label, out TextBox input, string placeholder)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 44, TextAlignment = TextAlignment.Right });
        input = new TextBox { PlaceholderText = placeholder, Width = 120, Height = 26, FontSize = 11, FontFamily = new FontFamily("SF Mono, monospace"), Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0), TextAlignment = TextAlignment.Left };
        row.Children.Add(input);
        var capturedInput = input;

        var clearBtn = new Button { Content = "×", Width = 22, Height = 22, FontSize = 10, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = BrushTxtDim, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        clearBtn.Click += (_, _) => { try { capturedInput.Text = ""; } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ClearEquip {label}: {ex.Message}"); } };
        row.Children.Add(clearBtn);
        return row;
    }

    private void SyncPaperdollFromInputs()
    {
        try
        {
            _paperdollCurrent.Gender = _genderCombo?.SelectedIndex ?? 0;
            _paperdollCurrent.Ear = (EarType)(_earCombo?.SelectedIndex ?? 0);
            _paperdollCurrent.Hair = new ItemInfo { Id = (_hairIdInput?.Text ?? "").Trim() };
            _paperdollCurrent.Face = new ItemInfo { Id = (_faceIdInput?.Text ?? "").Trim() };
            _paperdollCurrent.Cap = ParseEquip(_capIdInput?.Text);
            _paperdollCurrent.Cape = ParseEquip(_capeIdInput?.Text);
            _paperdollCurrent.Coat = ParseEquip(_coatIdInput?.Text);
            _paperdollCurrent.Pants = ParseEquip(_pantsIdInput?.Text);
            _paperdollCurrent.Shoes = ParseEquip(_shoesIdInput?.Text);
            _paperdollCurrent.Weapon = ParseEquip(_weaponIdInput?.Text);
            _paperdollCurrent.Shield = ParseEquip(_shieldIdInput?.Text);
            _paperdollCurrent.Glove = ParseEquip(_gloveIdInput?.Text);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncPaperdoll: {ex.Message}"); }
    }

    private static ItemInfo? ParseEquip(string? text)
    {
        var t = (text ?? "").Trim();
        return string.IsNullOrEmpty(t) ? null : new ItemInfo { Id = t };
    }

    private static string IdOrEmpty(string? id) => string.IsNullOrEmpty(id) ? "" : id;

    private void NewPaperdollPreset()
    {
        try { _paperdollCurrent = CreateDefaultAppearance(); SyncInputsFromPaperdoll(); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] NewPreset: {ex.Message}"); }
    }

    private void SavePaperdollPreset()
    {
        try { SyncPaperdollFromInputs(); var name = $"预设 {_paperdollPresets.Count + 1}"; _paperdollPresets.Add((name, CloneAppearance(_paperdollCurrent))); RefreshPresetList(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SavePreset: {ex.Message}"); }
    }

    private void LoadPaperdollPreset()
    {
        try
        {
            if (_presetListBox?.SelectedItem is not ValueTuple<string, CharacterAppearance> selected) return;
            _paperdollCurrent = CloneAppearance(selected.Item2);
            SyncInputsFromPaperdoll();
            UpdatePaperdollPreview();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPreset: {ex.Message}"); }
    }

    private void SyncInputsFromPaperdoll()
    {
        try
        {
            if (_genderCombo != null) _genderCombo.SelectedIndex = _paperdollCurrent.Gender;
            if (_hairIdInput != null) _hairIdInput.Text = IdOrEmpty(_paperdollCurrent.Hair?.Id);
            if (_faceIdInput != null) _faceIdInput.Text = IdOrEmpty(_paperdollCurrent.Face?.Id);
            if (_capIdInput != null) _capIdInput.Text = IdOrEmpty(_paperdollCurrent.Cap?.Id);
            if (_capeIdInput != null) _capeIdInput.Text = IdOrEmpty(_paperdollCurrent.Cape?.Id);
            if (_coatIdInput != null) _coatIdInput.Text = IdOrEmpty(_paperdollCurrent.Coat?.Id);
            if (_pantsIdInput != null) _pantsIdInput.Text = IdOrEmpty(_paperdollCurrent.Pants?.Id);
            if (_shoesIdInput != null) _shoesIdInput.Text = IdOrEmpty(_paperdollCurrent.Shoes?.Id);
            if (_weaponIdInput != null) _weaponIdInput.Text = IdOrEmpty(_paperdollCurrent.Weapon?.Id);
            if (_shieldIdInput != null) _shieldIdInput.Text = IdOrEmpty(_paperdollCurrent.Shield?.Id);
            if (_gloveIdInput != null) _gloveIdInput.Text = IdOrEmpty(_paperdollCurrent.Glove?.Id);
            if (_mountIdInput != null) _mountIdInput.Text = IdOrEmpty(_paperdollCurrent.Mount?.Id);
            if (_chairIdInput != null) _chairIdInput.Text = IdOrEmpty(_paperdollCurrent.Chair?.Id);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncInputs: {ex.Message}"); }
    }

    private void RefreshPresetList()
    {
        try
        {
            if (_presetListBox == null) return;
            _presetListBox.ItemsSource = null;
            _presetListBox.ItemsSource = _paperdollPresets;
            _presetListBox.ItemTemplate = new FuncDataTemplate<(string Name, CharacterAppearance Data)>((_, _) =>
                new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 11, Foreground = BrushTxtSecondary, Padding = new Thickness(4, 3) });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshPresetList: {ex.Message}"); }
    }

    private void UpdatePaperdollPreview()
    {
        try
        {
            if (_paperdollPreviewCanvas == null) return;
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12 };

            var figure = new Border { Width = 160, Height = 200, CornerRadius = new CornerRadius(12), Background = GetCheckerboardBrush(), HorizontalAlignment = HorizontalAlignment.Center, Child = new TextBlock { Text = "🧑", FontSize = 60, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            panel.Children.Add(figure);

            panel.Children.Add(new TextBlock { Text = _paperdollCurrent.Gender == 0 ? "♂ 男性" : "♀ 女性", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center });

            var equipSummary = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
            var parts = new (string Label, ItemInfo? Item)[] { ("发型", _paperdollCurrent.Hair), ("脸型", _paperdollCurrent.Face), ("帽子", _paperdollCurrent.Cap), ("披风", _paperdollCurrent.Cape), ("上衣", _paperdollCurrent.Coat), ("裤子", _paperdollCurrent.Pants), ("鞋子", _paperdollCurrent.Shoes), ("武器", _paperdollCurrent.Weapon), ("盾牌", _paperdollCurrent.Shield), ("手套", _paperdollCurrent.Glove), };
            foreach (var (label, item) in parts)
            {
                var color = item != null && !string.IsNullOrEmpty(item.Id) ? BrushTxtSecondary : BrushTxtDim;
                var val = item != null && !string.IsNullOrEmpty(item.Id) ? item.Id : "—";
                equipSummary.Children.Add(new TextBlock { Text = $"{label}: {val}", FontSize = 9, Foreground = color, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center });
            }
            panel.Children.Add(equipSummary);
            _paperdollPreviewCanvas.Child = panel;

            // 后台合成 stand1 第 0 帧真实预览
            var snapshot = CloneAppearance(_paperdollCurrent);
            var figureRef = figure;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!_wzService.IsLoaded) return;
                    _paperdollSvc.LoadAppearance(snapshot);
                    var png = _paperdollSvc.CompositeFrame("stand1", 0);
                    if (png == null) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            using var ms = new MemoryStream(png);
                            figureRef.Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PreviewImg: {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PreviewComposite: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdatePaperdollPreview: {ex.Message}"); }
    }

    private void ApplyPaperdoll()
    {
        try
        {
            SyncPaperdollFromInputs();
            UpdatePaperdollPreview();
            OnPaperdollApplied?.Invoke(CloneAppearance(_paperdollCurrent));
            if (_statusBarText != null) _statusBarText.Text = "纸娃娃已应用到主窗口";
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyPaperdoll: {ex.Message}"); }
    }

    /// <summary>默认外观：男体 + 默认发型/脸型（参考 MapleSalon2 生产默认 body 2000 / head 12000）。</summary>
    private static CharacterAppearance CreateDefaultAppearance() => new()
    {
        Gender = 0,
        Hair = new ItemInfo { Id = "30000" },
        Face = new ItemInfo { Id = "20000" },
    };

    private static CharacterAppearance CloneAppearance(CharacterAppearance src)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, Ear = src.Ear, Skin = src.Skin, Hair = new ItemInfo { Id = src.Hair.Id }, Face = new ItemInfo { Id = src.Face.Id },
            Cap = src.Cap != null ? new ItemInfo { Id = src.Cap.Id } : null, Cape = src.Cape != null ? new ItemInfo { Id = src.Cape.Id } : null,
            Coat = src.Coat != null ? new ItemInfo { Id = src.Coat.Id } : null, Pants = src.Pants != null ? new ItemInfo { Id = src.Pants.Id } : null,
            Shoes = src.Shoes != null ? new ItemInfo { Id = src.Shoes.Id } : null, Weapon = src.Weapon != null ? new ItemInfo { Id = src.Weapon.Id } : null,
            Shield = src.Shield != null ? new ItemInfo { Id = src.Shield.Id } : null, Glove = src.Glove != null ? new ItemInfo { Id = src.Glove.Id } : null,
            Mount = src.Mount != null ? new MountInfo { Id = src.Mount.Id } : null, Chair = src.Chair != null ? new ChairInfo { Id = src.Chair.Id } : null,
        };
    }

    // ═══════════════════════════════════════════
    // 🌄 背景设置 Tab
    // ═══════════════════════════════════════════
    private Border BuildBackgroundTab()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(300)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // 左：级联筛选(F6) + 搜索 + 列表 + 分页 + 选择历史(F2)
            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition(new GridLength(62)));  // 级联：区域/街道
            left.RowDefinitions.Add(new RowDefinition(new GridLength(32)));  // 搜索
            left.RowDefinitions.Add(new RowDefinition(GridLength.Star));     // 地图列表
            left.RowDefinitions.Add(new RowDefinition(new GridLength(30)));  // 分页
            left.RowDefinitions.Add(new RowDefinition(new GridLength(150))); // 选择历史

            // ── F6 级联筛选：区域 → 街道 ──
            var cascadePanel = new Grid { Margin = new Thickness(10, 8, 5, 2) };
            cascadePanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            cascadePanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            cascadePanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            cascadePanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var regionLabel = new TextBlock { Text = "区域", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            _bgRegionCombo = new ComboBox { FontSize = 11, Height = 26, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            _bgRegionCombo.SelectionChanged += (_, _) => BgOnRegionChanged();
            Grid.SetRow(regionLabel, 0); Grid.SetColumn(regionLabel, 0);
            Grid.SetRow(_bgRegionCombo, 0); Grid.SetColumn(_bgRegionCombo, 1);
            var streetLabel = new TextBlock { Text = "街道", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            _bgStreetCombo = new ComboBox { FontSize = 11, Height = 26, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            _bgStreetCombo.SelectionChanged += (_, _) => { _bgPage = 1; BgApplyFilter(); };
            Grid.SetRow(streetLabel, 1); Grid.SetColumn(streetLabel, 0);
            Grid.SetRow(_bgStreetCombo, 1); Grid.SetColumn(_bgStreetCombo, 1);
            cascadePanel.Children.Add(regionLabel);
            cascadePanel.Children.Add(_bgRegionCombo);
            cascadePanel.Children.Add(streetLabel);
            cascadePanel.Children.Add(_bgStreetCombo);
            Grid.SetRow(cascadePanel, 0);
            left.Children.Add(cascadePanel);

            var searchPanel = new Border { BorderBrush = BrushBorder, BorderThickness = new Thickness(1), Margin = new Thickness(10, 2, 5, 4), CornerRadius = new CornerRadius(5), Background = BrushInputBg };
            _bgSearchInput = new TextBox { PlaceholderText = "搜索 id 或名称…", FontSize = 12, Foreground = BrushTxtPrimary, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center };
            _bgSearchInput.TextChanged += (_, _) => { _bgPage = 1; BgApplyFilter(); };
            searchPanel.Child = _bgSearchInput;
            Grid.SetRow(searchPanel, 1);
            left.Children.Add(searchPanel);

            _bgMapList = new ListBox { Background = BrushBg, BorderThickness = new Thickness(0), Margin = new Thickness(10, 0, 5, 0) };
            _bgMapList.SelectionChanged += (_, _) => { if (_bgMapList.SelectedItem is MapEntry entry) BgSelectMap(entry.Id, entry.Name); };
            Grid.SetRow(_bgMapList, 2);
            left.Children.Add(_bgMapList);

            var pageRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var prev = new Button { Content = "◀", Width = 30, Height = 24, Padding = new Thickness(0), FontSize = 11, Background = BrushBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1) };
            prev.Click += (_, _) => { if (_bgPage > 1) { _bgPage--; BgApplyFilter(); } };
            _bgPageLabel = new TextBlock { Text = "1/1", FontSize = 11, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
            var next = new Button { Content = "▶", Width = 30, Height = 24, Padding = new Thickness(0), FontSize = 11, Background = BrushBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1) };
            next.Click += (_, _) => { if (_bgPage < _bgTotalPages) { _bgPage++; BgApplyFilter(); } };
            pageRow.Children.Add(prev);
            pageRow.Children.Add(_bgPageLabel);
            pageRow.Children.Add(next);
            Grid.SetRow(pageRow, 3);
            left.Children.Add(pageRow);

            // ── F2 选择历史：点一条直接应用该地图+crop ──
            var historyPanel = new DockPanel { Margin = new Thickness(10, 2, 5, 8), LastChildFill = true };
            var historyHeader = new TextBlock { Text = "🕐 选择历史", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(historyHeader, Dock.Top);
            historyPanel.Children.Add(historyHeader);
            _bgHistoryList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), BorderBrush = BrushBorder };
            _bgHistoryList.SelectionChanged += (_, _) =>
            {
                try { if (_bgHistoryList.SelectedItem is MapHistoryEntry he) { _bgHistoryList.SelectedIndex = -1; BgApplyHistoryEntry(he); } }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] HistoryClick: {ex.Message}"); }
            };
            RefreshBgHistoryList();
            historyPanel.Children.Add(_bgHistoryList);
            Grid.SetRow(historyPanel, 4);
            left.Children.Add(historyPanel);

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            // 右：大预览 Canvas + 选区框+8手柄(F3) + 信息 + 预设/微调 + 应用
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            _bgPreviewCanvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(255, 20, 20, 26)), Margin = new Thickness(5, 10, 10, 4) };
            _bgPreviewImage = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            // 选区框：亮黄描边 + 淡白填充，深色预览底上清晰可辨
            _bgCropRect = new Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 225, 77)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), IsVisible = false };
            _bgPreviewCanvas.Children.Add(_bgPreviewImage);
            _bgPreviewCanvas.Children.Add(_bgCropRect);
            // 8 个手柄（4 角 + 4 边），初始隐藏，选区可见时由 BgUpdateCropHandles 定位
            var handleFill = new SolidColorBrush(Color.FromArgb(255, 255, 225, 77));
            var handleStroke = new SolidColorBrush(Color.FromArgb(255, 30, 30, 38));
            for (int i = 0; i < 8; i++)
            {
                var h = new Rectangle { Width = BgHandleSize, Height = BgHandleSize, Fill = handleFill, Stroke = handleStroke, StrokeThickness = 1, IsVisible = false };
                _bgCropHandles.Add(h);
                _bgPreviewCanvas.Children.Add(h);
            }
            Canvas.SetLeft(_bgPreviewImage, 0);
            Canvas.SetTop(_bgPreviewImage, 0);
            _bgPreviewCanvas.PointerPressed += BgPreviewPressed;
            _bgPreviewCanvas.PointerMoved += BgPreviewMoved;
            _bgPreviewCanvas.PointerReleased += BgPreviewReleased;
            Grid.SetRow(_bgPreviewCanvas, 0);
            right.Children.Add(_bgPreviewCanvas);

            var bottom = new StackPanel { Margin = new Thickness(10, 4, 10, 10), Spacing = 6 };
            _mapNameLabel = new TextBlock { Text = "选择地图查看预览", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = BrushTxtPrimary };
            bottom.Children.Add(_mapNameLabel);
            _bgCropInfo = new TextBlock { Text = "拖出选区 / 选预设尺寸 / 点选区拖动或拉手柄微调", FontSize = 10, Foreground = BrushTxtDim };
            bottom.Children.Add(_bgCropInfo);

            // ── F3 预设视口尺寸 + 整图 + 清除 ──
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            presetRow.Children.Add(new TextBlock { Text = "选区尺寸", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            foreach (var (lbl, w, h) in new (string, int, int)[] { ("800×600", 800, 600), ("1024×768", 1024, 768), ("1280×720", 1280, 720) })
            {
                var captured = (lbl, w, h);
                var b = new Button { Content = captured.lbl, FontSize = 9, Padding = new Thickness(6, 2), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                b.Click += (_, _) => BgApplyPresetSize(captured.w, captured.h);
                presetRow.Children.Add(b);
            }
            var fullBtn = new Button { Content = "整图", FontSize = 9, Padding = new Thickness(6, 2), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            fullBtn.Click += (_, _) => BgApplyFullMap();
            presetRow.Children.Add(fullBtn);
            var clearBtn = new Button { Content = "✕ 清除", FontSize = 9, Padding = new Thickness(6, 2), Background = BrushInputBg, Foreground = BrushTxtDim, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            clearBtn.Click += (_, _) => BgClearCrop();
            presetRow.Children.Add(clearBtn);
            bottom.Children.Add(presetRow);

            // ── F3 微调（点选区后用箭头平移，步长=世界坐标 BgNudgeStep）──
            var nudgeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            nudgeRow.Children.Add(new TextBlock { Text = "微调", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            foreach (var (dir, dx, dy) in new (string, int, int)[] { ("◀", -1, 0), ("▶", 1, 0), ("▲", 0, -1), ("▼", 0, 1) })
            {
                var captured = (dir, dx, dy);
                var b = new Button { Content = captured.dir, Width = 26, Height = 22, Padding = new Thickness(0), FontSize = 10, Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                b.Click += (_, _) => BgNudgeCrop(captured.dx, captured.dy);
                nudgeRow.Children.Add(b);
            }
            bottom.Children.Add(nudgeRow);

            // ── point 2 分层显隐 toggle（背景/地面/物件/NPC/传送门/地面线）── flag 在 PetSettings + MapService（共享单例），toggle 即时影响预览与桌面
            var layerToggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            layerToggleRow.Children.Add(new TextBlock { Text = "显示", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            var layerToggles = new (string Label, Action<bool> Set, Func<bool> Get)[]
            {
                ("背景", v => { _settings.MapShowBack = v; _mapSvc.MapShowBack = v; }, () => _settings.MapShowBack),
                ("地面", v => { _settings.MapShowTile = v; _mapSvc.MapShowTile = v; }, () => _settings.MapShowTile),
                ("物件", v => { _settings.MapShowObj = v; _mapSvc.MapShowObj = v; }, () => _settings.MapShowObj),
                ("NPC", v => { _settings.MapShowLife = v; _mapSvc.MapShowLife = v; }, () => _settings.MapShowLife),
                ("传送门", v => { _settings.MapShowPortal = v; _mapSvc.MapShowPortal = v; }, () => _settings.MapShowPortal),
                ("地面线", v => { _settings.MapShowFoothold = v; _mapSvc.MapShowFoothold = v; }, () => _settings.MapShowFoothold),
            };
            foreach (var (lbl, setter, getter) in layerToggles)
            {
                var captured = (apply: setter, read: getter);
                var cb = new CheckBox { Content = lbl, FontSize = 9, Foreground = BrushTxtSecondary, Padding = new Thickness(2, 0), IsChecked = captured.read() };
                cb.IsCheckedChanged += (_, _) =>
                {
                    try
                    {
                        captured.apply(cb.IsChecked ?? true);
                        _config.SaveSettings(_settings);
                        _mapSvc.InvalidateFullRenderCache();
                        if (_bgCurrentMap != null) BgRenderPreview(_bgCurrentMap.Id, null);
                        // 通知 PetWindow：桌面背景窗口按新 flag 重渲染（否则桌面仍显示旧图）
                        OnLayerTogglesChanged?.Invoke();
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LayerToggle: {ex.Message}"); }
                };
                layerToggleRow.Children.Add(cb);
            }
            bottom.Children.Add(layerToggleRow);

            var applyBtn = new Button { Content = "✅ 应用到主窗口", FontSize = 12, Padding = new Thickness(20, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Right };
            applyBtn.Click += (_, _) => BgApplySelection();
            bottom.Children.Add(applyBtn);
            Grid.SetRow(bottom, 1);
            right.Children.Add(bottom);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            BgLoadCatalog();
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildBackgroundTab: {ex.Message}"); return new Border(); }
    }

    /// <summary>N3：切换到背景设置 sub-tab 并确保可见。PetWindow.OpenBackgroundTab 委托到此。</summary>
    public void OpenBackgroundTab()
    {
        try { SwitchSubTab(2); if (IsVisible) Activate(); else Show(); }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenBackgroundTab: {ex.Message}"); }
    }

    private void BgLoadCatalog()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (!_wzService.IsLoaded)
                {
                    Dispatcher.UIThread.Post(() => { if (_bgCropInfo != null) _bgCropInfo.Text = "⚠️ WZ 未加载，请先在 ⚙️ WZ 配置 设置路径"; });
                    return;
                }
                _wzService.WaitForMapCatalog(15000);
                var ids = _wzService.ListMapIds();
                _bgAllMaps.Clear();
                foreach (var id in ids) _bgAllMaps.Add(new MapEntry(id, _wzService.GetMapName(id)));
                // F6：构建区域→街道→地图 层级（供级联下拉）
                _bgHierarchy = _wzService.GetMapHierarchy();
                Dispatcher.UIThread.Post(() =>
                {
                    // 填充区域下拉（首项「全部」）
                    if (_bgRegionCombo != null)
                    {
                        var regions = new List<string> { BgAllLabel };
                        regions.AddRange(_bgHierarchy.Select(r => r.Name));
                        _bgRegionCombo.ItemsSource = regions;
                        _bgRegionCombo.SelectedIndex = 0;
                    }
                    if (_bgStreetCombo != null && (_bgStreetCombo.ItemsSource == null || ((System.Collections.IList)_bgStreetCombo.ItemsSource).Count == 0))
                    {
                        _bgStreetCombo.ItemsSource = new List<string> { BgAllLabel };
                        _bgStreetCombo.SelectedIndex = 0;
                    }
                    _bgPage = 1;
                    BgApplyFilter();
                    if (_bgCropInfo != null) _bgCropInfo.Text = $"共 {_bgAllMaps.Count} 张地图，{_bgHierarchy.Count} 个区域；拖出选区或选预设尺寸";
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgLoadCatalog: {ex.Message}"); }
        });
    }

    private void BgApplyFilter()
    {
        // F6 级联筛选：区域 → 街道 → 关键字 三级过滤
        var keyword = (_bgSearchInput?.Text ?? "").Trim();
        var regionSel = _bgRegionCombo?.SelectedItem as string;
        var streetSel = _bgStreetCombo?.SelectedItem as string;
        HashSet<string>? allow = null;
        if (!string.IsNullOrEmpty(regionSel) && regionSel != BgAllLabel)
        {
            var region = _bgHierarchy.FirstOrDefault(r => r.Name == regionSel);
            if (region != null)
            {
                allow = new HashSet<string>();
                var streets = string.IsNullOrEmpty(streetSel) || streetSel == BgAllLabel ? region.Streets
                    : region.Streets.Where(s => s.Name == streetSel).ToList();
                foreach (var s in streets) foreach (var m in s.Maps) allow.Add(m.Id);
            }
        }
        IEnumerable<MapEntry> src = _bgAllMaps;
        if (allow != null) src = src.Where(m => allow.Contains(m.Id));
        if (!string.IsNullOrEmpty(keyword)) src = src.Where(m => m.Id.Contains(keyword) || m.Name.Contains(keyword));
        _bgFiltered = src.ToList();
        _bgTotalPages = Math.Max(1, (_bgFiltered.Count + BgPageSize - 1) / BgPageSize);
        if (_bgPage > _bgTotalPages) _bgPage = _bgTotalPages;
        var pageItems = _bgFiltered.Skip((_bgPage - 1) * BgPageSize).Take(BgPageSize).ToList();
        if (_bgMapList != null)
        {
            _bgMapList.ItemsSource = pageItems;
            _bgMapList.ItemTemplate = new FuncDataTemplate<MapEntry>((data, _) =>
                new TextBlock { Text = data?.Display ?? "", FontSize = 11, Foreground = BrushTxtSecondary, Padding = new Thickness(6, 3) });
        }
        if (_bgPageLabel != null) _bgPageLabel.Text = $"{_bgPage}/{_bgTotalPages}";
    }

    /// <summary>F6 区域下拉变更：重建街道列表（首项「全部」），重置到第 1 页并刷新地图列表。</summary>
    private void BgOnRegionChanged()
    {
        if (_bgStreetCombo == null) return;
        var streets = new List<string> { BgAllLabel };
        if (_bgRegionCombo?.SelectedItem is string regionSel && regionSel != BgAllLabel)
        {
            var region = _bgHierarchy.FirstOrDefault(r => r.Name == regionSel);
            if (region != null) streets.AddRange(region.Streets.Select(s => s.Name));
        }
        _bgStreetCombo.ItemsSource = streets;
        _bgStreetCombo.SelectedIndex = 0;
        _bgPage = 1;
        BgApplyFilter();
    }

    /// <summary>F2 刷新选择历史列表（数据源 PetSettings.MapHistory，由 PetWindow.ApplyMapSelection 持久化）。</summary>
    private void RefreshBgHistoryList()
    {
        if (_bgHistoryList == null) return;
        var items = (_settings.MapHistory ?? new System.Collections.Generic.List<MapHistoryEntry>()).ToList();
        _bgHistoryList.ItemsSource = items;
        _bgHistoryList.ItemTemplate = new FuncDataTemplate<MapHistoryEntry>((data, _) =>
        {
            if (data == null) return new StackPanel();
            var name = !string.IsNullOrEmpty(data.Name) ? data.Name : data.Id;
            var cropTxt = (data.CropW > 0 && data.CropH > 0) ? $"{data.CropW}×{data.CropH}" : "整图";
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2),
                Children = {
                    new TextBlock { Text = "🗺️", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { Spacing = 0, Children = {
                        new TextBlock { Text = name, FontSize = 11, Foreground = BrushTxtSecondary },
                        new TextBlock { Text = $"[{data.Id}] · {cropTxt}", FontSize = 8, Foreground = BrushTxtDim, FontFamily = new FontFamily("SF Mono, monospace") } } } } };
        });
    }

    /// <summary>F2 点击历史项：复用 OnBackgroundMapApplied 路径应用地图+crop（PetWindow 负责持久化与显示），并刷新预览/crop 可视化。</summary>
    private void BgApplyHistoryEntry(MapHistoryEntry e)
    {
        try
        {
            var name = !string.IsNullOrEmpty(e.Name) ? e.Name : (_wzService.IsLoaded ? _wzService.GetMapName(e.Id) : e.Id);
            var sel = new MapSelection(e.Id, name, e.CropX, e.CropY, e.CropW, e.CropH);
            _selectedMapId = e.Id;
            if (_mapNameLabel != null) _mapNameLabel.Text = $"{name}  [{e.Id}]";
            // 立即应用（复用 OnBackgroundMapApplied → PetWindow.ApplyMapSelection 持久化+显示）
            OnBackgroundMapApplied?.Invoke(sel);
            if (_statusBarText != null) _statusBarText.Text = $"已从历史应用: {name}";
            // 预览地图并按历史 crop 还原选区（CropW/H 全零 = 整图，由 BgShowPreviewWithCrop 自动判定）
            BgShowPreviewWithCrop(e.Id, e.CropX, e.CropY, e.CropW, e.CropH, null);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgApplyHistoryEntry: {ex.Message}"); }
    }

    private void BgSelectMap(string mapId, string name)
    {
        _selectedMapId = mapId;
        _bgHasCrop = false;
        if (_bgCropRect != null) _bgCropRect.IsVisible = false;
        BgUpdateCropHandles();
        if (_mapNameLabel != null) _mapNameLabel.Text = $"{name}  [{mapId}]";
        BgRenderPreview(mapId, null);
    }

    /// <summary>后台渲染地图缩略图并写入 _bgPreviewImage/_bgThumb*；onLoaded 在成功后于 UI 线程回调（用于历史 crop 还原）。</summary>
    private void BgRenderPreview(string mapId, Action? onLoaded)
    {
        var gen = ++_bgPreviewGen;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var map = _mapSvc.LoadMap(mapId);
                byte[]? png = map != null ? _mapSvc.CompositeMapThumbnail(map, BgThumbW, BgThumbH) : null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _bgPreviewGen) return;
                    _bgCurrentMap = map;
                    if (png == null || map == null)
                    {
                        if (_bgPreviewImage != null) _bgPreviewImage.Source = null;
                        if (_bgCropInfo != null) _bgCropInfo.Text = "渲染失败";
                        return;
                    }
                    using var ms = new MemoryStream(png);
                    var bmp = new Bitmap(ms);
                    if (_bgPreviewImage != null) _bgPreviewImage.Source = bmp;
                    int worldW = map.MaxX - map.MinX;
                    int worldH = map.MaxY - map.MinY;
                    _bgThumbW = (int)bmp.Size.Width;
                    _bgThumbH = (int)bmp.Size.Height;
                    _bgThumbScale = _bgThumbW > 0 ? (double)worldW / _bgThumbW : 1;
                    if (_bgCropInfo != null) _bgCropInfo.Text = $"地图 {worldW}×{worldH}，拖动选 crop（当前：整图）";
                    onLoaded?.Invoke();
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgRenderPreview: {ex.Message}"); }
        });
    }

    /// <summary>渲染指定地图并将世界坐标 crop 还原为选区框（历史项回放）；cropW/H<=0 视为整图（不显示选区）。</summary>
    private void BgShowPreviewWithCrop(string mapId, int cropX, int cropY, int cropW, int cropH, Action? onLoaded = null)
    {
        _selectedMapId = mapId;
        BgRenderPreview(mapId, () =>
        {
            if (_bgCurrentMap == null) { onLoaded?.Invoke(); return; }
            if (cropW > 0 && cropH > 0)
            {
                double px = (cropX - _bgCurrentMap.MinX) / _bgThumbScale;
                double py = (cropY - _bgCurrentMap.MinY) / _bgThumbScale;
                double pw = cropW / _bgThumbScale;
                double ph = cropH / _bgThumbScale;
                BgSetCropRectPx(px, py, pw, ph, true);
            }
            else
            {
                BgClearCrop();
            }
            onLoaded?.Invoke();
        });
    }

    // ── F3 crop 选区：像素几何读写 + 手柄定位 ──
    /// <summary>以像素坐标设置选区框（含 clamp + 同步世界坐标 + 手柄），persist=true 时写入 _bgCropX/Y/W/H 与 _bgHasCrop。</summary>
    private void BgSetCropRectPx(double x, double y, double w, double h, bool persist)
    {
        if (_bgCropRect == null || _bgCurrentMap == null) return;
        x = Math.Max(0, Math.Min(x, _bgThumbW));
        y = Math.Max(0, Math.Min(y, _bgThumbH));
        w = Math.Min(Math.Max(0, w), _bgThumbW - x);
        h = Math.Min(Math.Max(0, h), _bgThumbH - y);
        _bgCropRect.IsVisible = true;
        Canvas.SetLeft(_bgCropRect, x);
        Canvas.SetTop(_bgCropRect, y);
        _bgCropRect.Width = w;
        _bgCropRect.Height = h;
        if (persist)
        {
            _bgCropX = _bgCurrentMap.MinX + (int)(x * _bgThumbScale);
            _bgCropY = _bgCurrentMap.MinY + (int)(y * _bgThumbScale);
            _bgCropW = (int)(w * _bgThumbScale);
            _bgCropH = (int)(h * _bgThumbScale);
            _bgHasCrop = true;
        }
        BgUpdateCropHandles();
        if (_bgCropInfo != null)
        {
            var worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
            var worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
            if (w < 1 || h < 1) _bgCropInfo.Text = $"地图 {worldW}×{worldH}，当前：整图";
            else
            {
                // 拖拽中（persist=false）也实时显示世界坐标，松手后才写入 _bgCropX/Y/W/H
                int liveX = _bgCurrentMap.MinX + (int)(x * _bgThumbScale);
                int liveY = _bgCurrentMap.MinY + (int)(y * _bgThumbScale);
                int liveW = (int)(w * _bgThumbScale);
                int liveH = (int)(h * _bgThumbScale);
                _bgCropInfo.Text = $"crop: {liveW}×{liveH} @({liveX},{liveY}) 世界坐标";
            }
        }
    }

    /// <summary>按当前选区框几何重定位 8 个手柄（4 角 + 4 边中点），选区不可见时隐藏。</summary>
    private void BgUpdateCropHandles()
    {
        if (_bgCropRect == null) return;
        var visible = _bgCropRect.IsVisible && _bgCropRect.Width > 0 && _bgCropRect.Height > 0;
        double x = Canvas.GetLeft(_bgCropRect);
        double y = Canvas.GetTop(_bgCropRect);
        double w = _bgCropRect.Width;
        double h = _bgCropRect.Height;
        // 顺序：nw, n, ne, e, se, s, sw, w
        double[] xs = { x, x + w / 2, x + w, x + w, x + w, x + w / 2, x, x };
        double[] ys = { y, y, y, y + h / 2, y + h, y + h, y + h, y + h / 2 };
        for (int i = 0; i < _bgCropHandles.Count && i < 8; i++)
        {
            var hd = _bgCropHandles[i];
            hd.IsVisible = visible;
            if (!visible) continue;
            Canvas.SetLeft(hd, xs[i] - BgHandleHalf);
            Canvas.SetTop(hd, ys[i] - BgHandleHalf);
        }
    }

    /// <summary>命中测试：返回手柄代号（"nw"..)"或 "move"（选区内）或 null（空白区，用于新建选区）。</summary>
    private string? BgHitTestHandle(Point p)
    {
        if (_bgCropRect == null || !_bgCropRect.IsVisible || _bgCropRect.Width < 1 || _bgCropRect.Height < 1) return null;
        double x = Canvas.GetLeft(_bgCropRect);
        double y = Canvas.GetTop(_bgCropRect);
        double w = _bgCropRect.Width;
        double h = _bgCropRect.Height;
        string[] names = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };
        double[] xs = { x, x + w / 2, x + w, x + w, x + w, x + w / 2, x, x };
        double[] ys = { y, y, y, y + h / 2, y + h, y + h, y + h, y + h / 2 };
        for (int i = 0; i < 8; i++)
        {
            if (Math.Abs(p.X - xs[i]) <= BgHandleHalf + 2 && Math.Abs(p.Y - ys[i]) <= BgHandleHalf + 2) return names[i];
        }
        if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h) return "move";
        return null;
    }

    private void BgPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_bgCurrentMap == null || _bgPreviewCanvas == null) return;
        var p = e.GetPosition(_bgPreviewCanvas);
        var hit = BgHitTestHandle(p);
        _bgCropOrigin = p;
        if (hit != null && hit != "move")
        {
            // 缩放模式：从对应手柄拉
            _bgCropMode = "resize";
            _bgActiveHandle = hit;
        }
        else if (hit == "move")
        {
            // 移动模式：在选区内拖动平移
            _bgCropMode = "move";
            _bgActiveHandle = null;
        }
        else
        {
            // 新建模式：从空白处拖出新选区
            _bgCropMode = "create";
            _bgActiveHandle = null;
            if (_bgCropRect != null)
            {
                _bgCropRect.IsVisible = true;
                _bgCropRect.Width = 0;
                _bgCropRect.Height = 0;
                Canvas.SetLeft(_bgCropRect, p.X);
                Canvas.SetTop(_bgCropRect, p.Y);
            }
        }
        // 记录拖拽起始的选区几何（move/resize 用）
        if (_bgCropRect != null && _bgCropMode != "create")
        {
            _bgCropStartX = Canvas.GetLeft(_bgCropRect);
            _bgCropStartY = Canvas.GetTop(_bgCropRect);
            _bgCropStartW = _bgCropRect.Width;
            _bgCropStartH = _bgCropRect.Height;
        }
        e.Pointer.Capture(_bgPreviewCanvas);
    }

    private void BgPreviewMoved(object? sender, PointerEventArgs e)
    {
        if (_bgCropRect == null || _bgPreviewCanvas == null || _bgCropMode == null) return;
        var p = e.GetPosition(_bgPreviewCanvas);
        if (_bgCropMode == "create")
        {
            double x = Math.Min(_bgCropOrigin.X, p.X);
            double y = Math.Min(_bgCropOrigin.Y, p.Y);
            double w = Math.Abs(p.X - _bgCropOrigin.X);
            double h = Math.Abs(p.Y - _bgCropOrigin.Y);
            BgSetCropRectPx(x, y, w, h, false);
            return;
        }
        double dx = p.X - _bgCropOrigin.X;
        double dy = p.Y - _bgCropOrigin.Y;
        if (_bgCropMode == "move")
        {
            BgSetCropRectPx(_bgCropStartX + dx, _bgCropStartY + dy, _bgCropStartW, _bgCropStartH, false);
        }
        else if (_bgCropMode == "resize" && _bgActiveHandle != null)
        {
            // 按手柄方向调整对应边：left/right/top/bottom
            double left = _bgCropStartX, top = _bgCropStartY, right = _bgCropStartX + _bgCropStartW, bottom = _bgCropStartY + _bgCropStartH;
            if (_bgActiveHandle.Contains('w')) left = _bgCropStartX + dx;
            if (_bgActiveHandle.Contains('e')) right = _bgCropStartX + _bgCropStartW + dx;
            if (_bgActiveHandle.Contains('n')) top = _bgCropStartY + dy;
            if (_bgActiveHandle.Contains('s')) bottom = _bgCropStartY + _bgCropStartH + dy;
            double nx = Math.Min(left, right), nw = Math.Abs(right - left);
            double ny = Math.Min(top, bottom), nh = Math.Abs(bottom - top);
            BgSetCropRectPx(nx, ny, nw, nh, false);
        }
    }

    private void BgPreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_bgCropRect == null || _bgCurrentMap == null || _bgCropMode == null) return;
        e.Pointer.Capture(null);
        var mode = _bgCropMode;
        _bgCropMode = null;
        _bgActiveHandle = null;
        double x = Canvas.GetLeft(_bgCropRect);
        double y = Canvas.GetTop(_bgCropRect);
        double w = _bgCropRect.Width;
        double h = _bgCropRect.Height;
        if (w < 8 || h < 8)
        {
            // 过小选区视为误操作：清空（move 模式尺寸不变不会命中此分支）
            BgClearCrop();
            return;
        }
        BgSetCropRectPx(x, y, w, h, true);
    }

    /// <summary>F3 预设视口尺寸：以当前选区中心（无选区则地图中心）为中心放置 w×h 选区。</summary>
    private void BgApplyPresetSize(int vw, int vh)
    {
        if (_bgCurrentMap == null) return;
        int worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
        int worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
        // 当前选区中心（世界坐标），无选区则用地图中心
        int cx, cy;
        if (_bgHasCrop) { cx = _bgCropX + _bgCropW / 2; cy = _bgCropY + _bgCropH / 2; }
        else { cx = (_bgCurrentMap.MinX + _bgCurrentMap.MaxX) / 2; cy = (_bgCurrentMap.MinY + _bgCurrentMap.MaxY) / 2; }
        int cw = Math.Min(vw, worldW);
        int ch = Math.Min(vh, worldH);
        int cxc = Math.Max(_bgCurrentMap.MinX, Math.Min(cx - cw / 2, _bgCurrentMap.MaxX - cw));
        int cyc = Math.Max(_bgCurrentMap.MinY, Math.Min(cy - ch / 2, _bgCurrentMap.MaxY - ch));
        double px = (cxc - _bgCurrentMap.MinX) / _bgThumbScale;
        double py = (cyc - _bgCurrentMap.MinY) / _bgThumbScale;
        double pw = cw / _bgThumbScale;
        double ph = ch / _bgThumbScale;
        BgSetCropRectPx(px, py, pw, ph, true);
    }

    /// <summary>F3 整图：清除选区，应用时按 cw<=0 → 整图（与 PetWindow/MapService 归一化一致）。</summary>
    private void BgApplyFullMap()
    {
        if (_bgCurrentMap == null) return;
        BgClearCrop();
        int worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
        int worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
        // 整图 = 全零 crop（PetWindow/历史归一化为 CropFull）
        _bgCropX = _bgCurrentMap.MinX; _bgCropY = _bgCurrentMap.MinY; _bgCropW = 0; _bgCropH = 0;
        _bgHasCrop = true;
        if (_bgCropInfo != null) _bgCropInfo.Text = $"地图 {worldW}×{worldH}，当前：整图";
    }

    /// <summary>清除选区（恢复到「不裁剪」状态）。</summary>
    private void BgClearCrop()
    {
        _bgHasCrop = false;
        _bgCropW = 0; _bgCropH = 0;
        if (_bgCropRect != null) _bgCropRect.IsVisible = false;
        BgUpdateCropHandles();
        if (_bgCropInfo != null && _bgCurrentMap != null)
        {
            var worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
            var worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
            _bgCropInfo.Text = $"地图 {worldW}×{worldH}，当前：整图";
        }
    }

    /// <summary>F3 微调：按世界坐标步长平移选区（自动 clamp 在地图范围内）。</summary>
    private void BgNudgeCrop(int dxWorld, int dyWorld)
    {
        if (!_bgHasCrop || _bgCurrentMap == null) return;
        // 整图（cw<=0）不可平移
        if (_bgCropW <= 0 || _bgCropH <= 0) return;
        int nx = _bgCropX + dxWorld * BgNudgeStep;
        int ny = _bgCropY + dyWorld * BgNudgeStep;
        nx = Math.Max(_bgCurrentMap.MinX, Math.Min(nx, _bgCurrentMap.MaxX - _bgCropW));
        ny = Math.Max(_bgCurrentMap.MinY, Math.Min(ny, _bgCurrentMap.MaxY - _bgCropH));
        double px = (nx - _bgCurrentMap.MinX) / _bgThumbScale;
        double py = (ny - _bgCurrentMap.MinY) / _bgThumbScale;
        double pw = _bgCropW / _bgThumbScale;
        double ph = _bgCropH / _bgThumbScale;
        BgSetCropRectPx(px, py, pw, ph, true);
    }

    private void BgApplySelection()
    {
        if (string.IsNullOrEmpty(_selectedMapId) || _bgCurrentMap == null)
        {
            if (_statusBarText != null) _statusBarText.Text = "请先选择地图";
            return;
        }
        int cx, cy, cw, ch;
        if (_bgHasCrop) { cx = _bgCropX; cy = _bgCropY; cw = _bgCropW; ch = _bgCropH; }
        else
        {
            // 未拖 crop：默认中心 800×600 视口（避免整图超大窗口；N1 的 cw<=0→整图仅作用于历史/零 crop）
            int vw = Math.Min(800, _bgCurrentMap.MaxX - _bgCurrentMap.MinX);
            int vh = Math.Min(600, _bgCurrentMap.MaxY - _bgCurrentMap.MinY);
            cx = (_bgCurrentMap.MinX + _bgCurrentMap.MaxX) / 2 - vw / 2;
            cy = (_bgCurrentMap.MinY + _bgCurrentMap.MaxY) / 2 - vh / 2;
            cw = vw; ch = vh;
        }
        var entry = _bgAllMaps.FirstOrDefault(m => m.Id == _selectedMapId);
        var name = entry?.Name ?? _selectedMapId;
        _pendingMapSelection = new MapSelection(_selectedMapId, name, cx, cy, cw, ch);
        OnBackgroundMapApplied?.Invoke(_pendingMapSelection);
        if (_statusBarText != null) _statusBarText.Text = "已应用到主窗口";
    }

    private void OpenMapBrowserFromSettings()
    {
        // 已并入背景设置 tab（OpenBackgroundTab），保留为空防误调
        try { OpenBackgroundTab(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenMapBrowserFromSettings: {ex.Message}"); }
    }

    private void UpdateMapPreview(string mapId, string name, string desc)
    {
        try
        {
            if (_mapPreviewCanvas == null) return;
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 14 };
            var mapVisual = new Border { Width = 320, Height = 200, CornerRadius = new CornerRadius(10), Background = GetMapBackgroundGradient(name), HorizontalAlignment = HorizontalAlignment.Center };
            var overlayGrid = new Grid();
            overlayGrid.Children.Add(new TextBlock { Text = GetMapIcon(name), FontSize = 60, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var housesRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Spacing = 24, Margin = new Thickness(0, 0, 0, 12) };
            for (int hi = 0; hi < 3; hi++) housesRow.Children.Add(new TextBlock { Text = "\U0001f3e0", FontSize = 18 });
            overlayGrid.Children.Add(housesRow);
            mapVisual.Child = overlayGrid;
            panel.Children.Add(mapVisual);
            _mapNameLabel = new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, HorizontalAlignment = HorizontalAlignment.Center };

            // 后台真实渲染地图预览（320×200），失败则保留占位
            var visualRef = mapVisual;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!_wzService.IsLoaded) return;
                    var m = _mapSvc.LoadMap(mapId);
                    if (m == null) return;
                    var png = _mapSvc.CompositeMapThumbnail(m, 320, 200);
                    if (png == null) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            using var ms = new MemoryStream(png);
                            visualRef.Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MapPreviewImg: {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MapPreview: {ex.Message}"); }
            });
            panel.Children.Add(_mapNameLabel);
            panel.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = $"地图ID: {mapId}", FontSize = 9, Foreground = BrushTxtDim, FontFamily = new FontFamily("SF Mono, monospace"), HorizontalAlignment = HorizontalAlignment.Center });
            _mapPreviewCanvas.Child = panel;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdateMapPreview: {ex.Message}"); }
    }

    private static IBrush GetMapBackgroundGradient(string name) => name switch
    {
        "射手村" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 135, 200, 235), 0), new(Color.FromArgb(255, 90, 160, 80), 0.6), new(Color.FromArgb(255, 60, 130, 50), 1) } },
        "魔法密林" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 80, 60, 120), 0), new(Color.FromArgb(255, 40, 90, 70), 0.6), new(Color.FromArgb(255, 20, 60, 40), 1) } },
        "废弃都市" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 40, 40, 55), 0), new(Color.FromArgb(255, 60, 50, 70), 0.6), new(Color.FromArgb(255, 35, 35, 45), 1) } },
        "天空之城" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 180, 220, 255), 0), new(Color.FromArgb(255, 140, 185, 240), 0.6), new(Color.FromArgb(255, 200, 230, 255), 1) } },
        "神木村" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 200, 160, 80), 0), new(Color.FromArgb(255, 150, 110, 50), 0.6), new(Color.FromArgb(255, 100, 70, 30), 1) } },
        "水下世界" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 40, 120, 200), 0), new(Color.FromArgb(255, 20, 80, 160), 0.6), new(Color.FromArgb(255, 10, 40, 100), 1) } },
        "玩具城" => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 255, 200, 140), 0), new(Color.FromArgb(255, 255, 160, 100), 0.6), new(Color.FromArgb(255, 230, 130, 80), 1) } },
        _ => new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.FromArgb(255, 160, 210, 140), 0), new(Color.FromArgb(255, 120, 170, 100), 0.6), new(Color.FromArgb(255, 80, 130, 60), 1) } },
    };

    private static string GetMapIcon(string name) => name switch
    {
        "射手村" => "🏘️", "魔法密林" => "🌲", "废弃都市" => "🏙️", "天空之城" => "☁️", "神木村" => "🐉", "水下世界" => "🐠", "玩具城" => "🧸", "童话村" => "🏮", _ => "🗺️",
    };

    // ═══════════════════════════════════════════
    // 💬 聊天气泡设置 Tab
    // ═══════════════════════════════════════════
    private Border BuildBalloonTab()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var sidebar = new StackPanel { Margin = new Thickness(10), Spacing = 8 };
            sidebar.Children.Add(new TextBlock { Text = "💬 气泡样式", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 0, 0, 0) });

            var balloonListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
            balloonListBox.ItemsSource = _balloonPresets;
            balloonListBox.ItemTemplate = new FuncDataTemplate<(int Id, string Name, Color Color, Color BgColor)>((data, _) =>
                new Border
                {
                    Padding = new Thickness(10, 6),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Border { Width = 28, Height = 20, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(data.BgColor), BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1) }, new TextBlock { Text = data.Name, FontSize = 12, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center } } }
                });
            balloonListBox.SelectedIndex = 0;
            balloonListBox.SelectionChanged += (_, _) =>
            {
                try { if (balloonListBox.SelectedItem is ValueTuple<int, string, Color, Color> item) { _selectedBalloonId = item.Item1; UpdateBalloonPreview(item.Item1, item.Item2, item.Item4, item.Item3); } }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonSelect: {ex.Message}"); }
            };
            sidebar.Children.Add(balloonListBox);
            Grid.SetColumn(sidebar, 0);
            grid.Children.Add(sidebar);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            _balloonPreviewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Stretch };
            var first = _balloonPresets[0];
            UpdateBalloonPreview(first.Id, first.Name, first.BgColor, first.Color);
            Grid.SetColumn(_balloonPreviewCanvas, 2);
            grid.Children.Add(_balloonPreviewCanvas);
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildBalloonTab: {ex.Message}"); return new Border(); }
    }

    private void UpdateBalloonPreview(int id, string name, Color bgColor, Color textColor)
    {
        try
        {
            if (_balloonPreviewCanvas == null) return;
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 16 };

            var balloonContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            balloonContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            balloonContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var balloonBody = new Border
            {
                CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(bgColor), BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(28, 18),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Color = Color.FromArgb(80, 0, 0, 0), OffsetX = 0, OffsetY = 4 })
            };
            var balloonContent = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            balloonContent.Children.Add(new TextBlock { Text = name, FontSize = 10, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center });
            _balloonPreviewText = new TextBlock { Text = "你好！我是 MiniPet 桌宠 🐾", FontSize = 14, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(textColor), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            balloonContent.Children.Add(_balloonPreviewText);
            balloonBody.Child = balloonContent;
            Grid.SetRow(balloonBody, 0);
            balloonContainer.Children.Add(balloonBody);

            var arrow = new Polygon { Points = new List<Point> { new(0, 0), new(12, 10), new(24, 0) }, Fill = new SolidColorBrush(bgColor), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(arrow, 1);
            balloonContainer.Children.Add(arrow);

            panel.Children.Add(balloonContainer);
            panel.Children.Add(new TextBlock { Text = "🐾", FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { new TextBlock { Text = $"样式: {name}", FontSize = 10, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = $"ID: {id}", FontSize = 9, Foreground = BrushTxtDim, FontFamily = new FontFamily("SF Mono, monospace"), HorizontalAlignment = HorizontalAlignment.Center } } });
            _balloonPreviewCanvas.Child = panel;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdateBalloonPreview: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════
    private static Button MakeSmallBtn(string text, Action onClick)
    {
        var btn = new Button { Content = text, FontSize = 10, Padding = new Thickness(10, 4), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        btn.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Btn {text}: {ex.Message}"); } };
        return btn;
    }

    // ═══════════════════════════════════════════
    // 素材浏览集成
    // ═══════════════════════════════════════════
    private void OpenMaterialBrowser()
    {
        try
        {
            var browser = new MaterialBrowserWindow(_wzService, _config);
            browser.OnMaterialSelected += (id, name) =>
            {
                Console.WriteLine($"[SettingsWindow] OnMaterialSelected received: Id={id}, Name={name}");
                Dispatcher.UIThread.Post(() => SelectPet(name, id));
            };
            browser.Show(this);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenMaterialBrowser: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 预览动画
    // ═══════════════════════════════════════════
    private void StartPreviewAnimation()
    {
        try
        {
            _previewAnimTimer.Stop();

            if (_previewPngData == null || _previewStrip == null)
                return;

            _previewFrameIndex = 0;

            var delay = 240;
            if (_previewStrip.FrameData != null && _previewStrip.FrameData.Count > 0)
                delay = _previewStrip.FrameData[0].Delay;
            _previewAnimTimer.Interval = TimeSpan.FromMilliseconds(delay);
            _previewAnimTimer.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] StartPreviewAnimation: {ex}"); }
    }

    private void OnPreviewAnimTick(object? sender, EventArgs e)
    {
        try
        {
            if (_previewCanvas == null || _previewStrip == null || _previewSourceBitmap == null) return;
            if (_previewCanvas.Child is StackPanel stack)
            {
                foreach (var child in stack.Children)
                {
                    if (child is Border figure2 && figure2.Child is Image img2 && img2.Tag?.ToString() == "previewSprite")
                    {
                        var frameWidth = _previewStrip.FrameWidth;
                        var frameHeight = _previewStrip.FrameHeight;

                        var frameRect = new PixelRect(frameWidth * _previewFrameIndex, 0, frameWidth, frameHeight);
                        try { img2.Source = new CroppedBitmap(_previewSourceBitmap, frameRect); }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TickFrame: {ex.Message}"); }
                        _previewFrameIndex++;
                        if (_previewFrameIndex >= _previewStrip.FrameCount)
                            _previewFrameIndex = 0;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPreviewAnimTick: {ex}"); }
    }

    // ═══════════════════════════════════════════
    // WZ 宠物列表加载
    // ═══════════════════════════════════════════
    private void LoadPetListFromWz()
    {
        try
        {
            // 只刷新 UI 列表显示，不修改用户数据
            Dispatcher.UIThread.Post(() =>
            {
                RefreshFavoriteList();
                RefreshRecentList();
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPetListFromWz: {ex.Message}"); }
    }
}
