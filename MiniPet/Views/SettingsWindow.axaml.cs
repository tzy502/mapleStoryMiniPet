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
    private PetSettings _settings;
    private WzConfig _wzConfig;

    // ── 状态 ──
    private int _activeMainTab = 0;
    private int _activeSubTab = 0;
    private bool _isDarkMode = true;
    private string _selectedBgMode = "tp";
    private int _previewMobId;
    private bool _previewIsMob = true;

    // ── 预览动画 ──
    private readonly DispatcherTimer _previewAnimTimer;
    private int _previewFrameIndex;
    private byte[]? _previewPngData;
    private SpriteStrip? _previewStrip;
    private string _previewAction = "";
    private List<string> _previewActions = new();

    // ── 事件 ──
    public event Action<int, string>? OnPetApplied;

    // ── 素材桌宠 UI 引用 ──
    private TextBlock? _petNameLabel;
    private TextBox? _petIdInput;
    private ListBox? _favoriteList;
    private ListBox? _recentList;
    private TextBlock? _statusBarText;
    private Border? _previewCanvas;
    private Slider? _zoomSlider;
    private TextBlock? _zoomLabel;
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
    private CharacterAppearance _paperdollCurrent = new();
    private readonly List<(string Name, CharacterAppearance Data)> _paperdollPresets = new();
    private ListBox? _presetListBox;
    private Border? _paperdollPreviewCanvas;
    private ComboBox? _genderCombo;
    private TextBox? _hairIdInput, _faceIdInput;
    private TextBox? _capIdInput, _capeIdInput, _coatIdInput, _pantsIdInput;
    private TextBox? _shoesIdInput, _weaponIdInput, _shieldIdInput, _gloveIdInput;
    private TextBox? _mountIdInput, _chairIdInput;

    // ── 背景状态 ──
    private readonly List<(string Id, string Name, string Desc)> _mapPresets = new()
    {
        ("100000000", "射手村", "冒险家出发地，绿意盎然的小镇"),
        ("101000000", "魔法密林", "精灵族的故乡，魔法森林"),
        ("102000000", "废弃都市", "暗影中的都市，下水道纵横"),
        ("103000000", "天空之城", "云端之上的浮空城市"),
        ("240010500", "神木村", "龙族栖息的神圣村庄"),
        ("221000000", "水下世界", "蔚蓝深海的水族王国"),
        ("220000000", "玩具城", "路德斯湖的玩具都市"),
        ("211000000", "童话村", "韩国传统风情的童话村落"),
    };
    private string _selectedMapId = "100000000";
    private TextBlock? _mapNameLabel;
    private Border? _mapPreviewCanvas;

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


    public SettingsWindow(ConfigService config, WzService wzService)
    {
        _config = config;
        _wzService = wzService;
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
        var searchInput = new TextBox { PlaceholderText = "过滤当前列表…", FontSize = 12, Foreground = BrushTxtPrimary, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left };
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
        _favoriteList.SelectionChanged += (_, _) => { if (_favoriteList.SelectedItem is FavoriteItem fi) SelectPet(fi.Name, fi.Id.ToString()); };
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
        _recentList.SelectionChanged += (_, _) => { if (_recentList.SelectedItem is RecentItem ri) SelectPet(ri.Name, ri.Id.ToString()); };
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
        var defaultName = defaultMobId > 0 ? _wzService.GetMobName(defaultMobId) : "";
        if (string.IsNullOrEmpty(defaultName)) defaultName = defaultMobId > 0 ? $"怪物_{defaultMobId}" : "";
        UpdatePreviewContent(defaultName, defaultMobId > 0 ? defaultMobId.ToString() : "");
        preview.Children.Add(_previewCanvas);

        var paramBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _petNameLabel = new TextBlock { Text = defaultMobId > 0 ? defaultName : "未选择宠物", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
        paramBar.Children.Add(_petNameLabel);
        _petIdInput = new TextBox { Text = defaultMobId > 0 ? defaultMobId.ToString() : "", Width = 80, FontSize = 11, FontFamily = new FontFamily("SF Mono, monospace"), Foreground = new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)), Background = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3), TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
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
        _zoomSlider = new Slider { Minimum = 0.3, Maximum = 2.0, Value = 1.0, Width = 120, Height = 36 };
        _zoomSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) _zoomLabel!.Text = $"{(int)(_zoomSlider.Value * 100)}%"; };
        zoomRow.Children.Add(_zoomSlider);
        _zoomLabel = new TextBlock { Text = "100%", FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Width = 30 };
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
        try { RefreshFavoriteList(); RefreshRecentList(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshFromSettings: {ex.Message}"); }
    }

    private void SelectPet(string name, string id)
    {
        try
        {
            _petNameLabel!.Text = name; _petIdInput!.Text = id;
            if (int.TryParse(id, out var numId))
            {
                _previewMobId = numId;
                _previewIsMob = true;
            }
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
                if (!int.TryParse(id, out var mobId)) mobId = _previewMobId;

                _previewActions = _wzService.GetActionList(mobId);
                if (_previewActions.Count > 0)
                {
                    if (!_previewActions.Contains(_previewAction))
                        _previewAction = _previewActions[0];
                    LoadPreviewAction(mobId, _previewAction);
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
                                    if (_previewPngData != null)
                                    {
                                        var tp = System.IO.Path.GetTempFileName() + ".png";
                                        System.IO.File.WriteAllBytes(tp, _previewPngData);
                                        try { img.Source = new Avalonia.Media.Imaging.Bitmap(tp); }
                                        catch { }
                                        finally { try { System.IO.File.Delete(tp); } catch { } }
                                    }
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
                if (_previewPngData != null)
                {
                    var tmpPath = System.IO.Path.GetTempFileName() + ".png";
                    System.IO.File.WriteAllBytes(tmpPath, _previewPngData);
                    try { spriteImage.Source = new Avalonia.Media.Imaging.Bitmap(tmpPath); }
                    catch (Exception ex) { Console.Error.WriteLine($"[Preview] Bitmap: {ex.Message}"); }
                    finally { try { System.IO.File.Delete(tmpPath); } catch { } }
                }
                spriteImage.Tag = "previewSprite";
                spriteImage.Name = "previewSprite";
                previewFigure.Child = spriteImage;
            }
            else
            {
                previewFigure.Child = new TextBlock { Text = "请先配置 WZ 路径", Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
            }

            stack.Children.Add(previewFigure);

            stack.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)) });
            stack.Children.Add(new TextBlock { Text = id, FontSize = 10, Foreground = BrushTxtDim, Margin = new Thickness(0, -6, 0, 0) });
            _previewCanvas.Child = stack;

            // 启动预览动画
            StartPreviewAnimation();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdatePreview: {ex}"); }
    }

    private void LoadPreviewAction(int mobId, string action)
    {
        try
        {
            _previewPngData = _wzService.BuildSpriteStripPng(mobId, action);
            var (strip, _) = _wzService.ExportSpriteStrip(mobId, action);
            _previewStrip = strip;
            _previewAction = action;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPreviewAction: {ex.Message}"); }
    }

    private void ToggleFavorite()
    {
        try
        {
            var idText = _petIdInput!.Text; if (!int.TryParse(idText, out var id)) return; var name = _petNameLabel!.Text ?? "";
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
            var idText = _petIdInput!.Text; if (!int.TryParse(idText, out var id)) return;
            _settings.CurrentMobId = id; _settings.CurrentEntityType = "mob";
            var name = _petNameLabel!.Text ?? "";
            var existing = _settings.RecentItems.FirstOrDefault(r => r.Id == id);
            if (existing != null) existing.LastUsedAt = DateTime.Now;
            else { _settings.RecentItems.Insert(0, new RecentItem { Type = "mob", Id = id, Name = name, LastUsedAt = DateTime.Now }); if (_settings.RecentItems.Count > 20) _settings.RecentItems = _settings.RecentItems.Take(20).ToList(); }
            _config.SaveSettings(_settings);
            _previewAnimTimer.Stop();
            OnPetApplied?.Invoke(id, name);
            Hide();
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

        var applyBtn = new Button { Content = "应用", FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(0, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        applyBtn.Click += (_, _) => { try { SyncPaperdollFromInputs(); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyPaperdoll: {ex.Message}"); } };
        content.Children.Add(applyBtn);
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
            _paperdollCurrent.Hair = new ItemInfo { Id = ParseInt(_hairIdInput?.Text) };
            _paperdollCurrent.Face = new ItemInfo { Id = ParseInt(_faceIdInput?.Text) };
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

    private static ItemInfo? ParseEquip(string? text) => string.IsNullOrWhiteSpace(text) ? null : new ItemInfo { Id = ParseInt(text) };

    private static int ParseInt(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : int.TryParse(text, out var v) ? v : 0;

    private void NewPaperdollPreset()
    {
        try { _paperdollCurrent = new CharacterAppearance(); SyncInputsFromPaperdoll(); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] NewPreset: {ex.Message}"); }
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
            if (_hairIdInput != null) _hairIdInput.Text = _paperdollCurrent.Hair.Id > 0 ? _paperdollCurrent.Hair.Id.ToString() : "";
            if (_faceIdInput != null) _faceIdInput.Text = _paperdollCurrent.Face.Id > 0 ? _paperdollCurrent.Face.Id.ToString() : "";
            if (_capIdInput != null) _capIdInput.Text = _paperdollCurrent.Cap?.Id > 0 ? _paperdollCurrent.Cap.Id.ToString() : "";
            if (_capeIdInput != null) _capeIdInput.Text = _paperdollCurrent.Cape?.Id > 0 ? _paperdollCurrent.Cape.Id.ToString() : "";
            if (_coatIdInput != null) _coatIdInput.Text = _paperdollCurrent.Coat?.Id > 0 ? _paperdollCurrent.Coat.Id.ToString() : "";
            if (_pantsIdInput != null) _pantsIdInput.Text = _paperdollCurrent.Pants?.Id > 0 ? _paperdollCurrent.Pants.Id.ToString() : "";
            if (_shoesIdInput != null) _shoesIdInput.Text = _paperdollCurrent.Shoes?.Id > 0 ? _paperdollCurrent.Shoes.Id.ToString() : "";
            if (_weaponIdInput != null) _weaponIdInput.Text = _paperdollCurrent.Weapon?.Id > 0 ? _paperdollCurrent.Weapon.Id.ToString() : "";
            if (_shieldIdInput != null) _shieldIdInput.Text = _paperdollCurrent.Shield?.Id > 0 ? _paperdollCurrent.Shield.Id.ToString() : "";
            if (_gloveIdInput != null) _gloveIdInput.Text = _paperdollCurrent.Glove?.Id > 0 ? _paperdollCurrent.Glove.Id.ToString() : "";
            if (_mountIdInput != null) _mountIdInput.Text = _paperdollCurrent.Mount?.Id > 0 ? _paperdollCurrent.Mount.Id.ToString() : "";
            if (_chairIdInput != null) _chairIdInput.Text = _paperdollCurrent.Chair?.Id > 0 ? _paperdollCurrent.Chair.Id.ToString() : "";
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

            var genderColor1 = _paperdollCurrent.Gender == 0 ? Color.FromArgb(255, 60, 90, 160) : Color.FromArgb(255, 200, 100, 150);
            var genderColor2 = _paperdollCurrent.Gender == 0 ? Color.FromArgb(255, 30, 50, 100) : Color.FromArgb(255, 150, 60, 100);
            var figure = new Border { Width = 120, Height = 180, CornerRadius = new CornerRadius(12), Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(genderColor1, 0), new(genderColor2, 1) } }, HorizontalAlignment = HorizontalAlignment.Center };
            var figureInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
            figureInfo.Children.Add(new TextBlock { Text = "🧑", FontSize = 60, HorizontalAlignment = HorizontalAlignment.Center });
            figure.Child = figureInfo;
            panel.Children.Add(figure);

            panel.Children.Add(new TextBlock { Text = _paperdollCurrent.Gender == 0 ? "♂ 男性" : "♀ 女性", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, HorizontalAlignment = HorizontalAlignment.Center });

            var equipSummary = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            var parts = new (string Label, ItemInfo? Item)[] { ("发型", _paperdollCurrent.Hair), ("脸型", _paperdollCurrent.Face), ("帽子", _paperdollCurrent.Cap), ("披风", _paperdollCurrent.Cape), ("上衣", _paperdollCurrent.Coat), ("裤子", _paperdollCurrent.Pants), ("鞋子", _paperdollCurrent.Shoes), ("武器", _paperdollCurrent.Weapon), ("盾牌", _paperdollCurrent.Shield), ("手套", _paperdollCurrent.Glove), };
            foreach (var (label, item) in parts)
            {
                var color = item != null && item.Id > 0 ? BrushTxtSecondary : BrushTxtDim;
                var val = item != null && item.Id > 0 ? item.Id.ToString() : "—";
                equipSummary.Children.Add(new TextBlock { Text = $"{label}: {val}", FontSize = 9, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center });
            }
            panel.Children.Add(equipSummary);
            _paperdollPreviewCanvas.Child = panel;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdatePaperdollPreview: {ex.Message}"); }
    }

    private static CharacterAppearance CloneAppearance(CharacterAppearance src)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, Hair = new ItemInfo { Id = src.Hair.Id }, Face = new ItemInfo { Id = src.Face.Id },
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
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var sidebar = new StackPanel { Margin = new Thickness(10), Spacing = 8 };
            sidebar.Children.Add(new TextBlock { Text = "🗺️ 地图列表", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 0, 0, 0) });

            var mapListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
            mapListBox.ItemsSource = _mapPresets;
            mapListBox.ItemTemplate = new FuncDataTemplate<(string Id, string Name, string Desc)>((_, _) =>
                new Border { Padding = new Thickness(10, 6), Child = new StackPanel { Spacing = 2, Children = { new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 12, FontWeight = FontWeight.Medium, Foreground = BrushTxtSecondary }, new TextBlock { [!TextBlock.TextProperty] = new Binding("Desc"), FontSize = 9, Foreground = BrushTxtDim } } } });
            mapListBox.SelectedIndex = 0;
            mapListBox.SelectionChanged += (_, _) =>
            {
                try { if (mapListBox.SelectedItem is ValueTuple<string, string, string> item) { _selectedMapId = item.Item1; UpdateMapPreview(item.Item1, item.Item2, item.Item3); } }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MapSelect: {ex.Message}"); }
            };
            sidebar.Children.Add(mapListBox);
            Grid.SetColumn(sidebar, 0);
            grid.Children.Add(sidebar);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            _mapPreviewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Stretch };
            UpdateMapPreview("100000000", "射手村", "冒险家出发地，绿意盎然的小镇");
            Grid.SetColumn(_mapPreviewCanvas, 2);
            grid.Children.Add(_mapPreviewCanvas);
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildBackgroundTab: {ex.Message}"); return new Border(); }
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
                Dispatcher.UIThread.Post(() => SelectPet(name, id.ToString()));
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
            if (_previewCanvas == null || _previewPngData == null || _previewStrip == null) return;
            if (_previewCanvas.Child is StackPanel stack)
            {
                foreach (var child in stack.Children)
                {
                    if (child is Border figure2 && figure2.Child is Image img2 && img2.Tag?.ToString() == "previewSprite")
                    {
                        var frameWidth = _previewStrip.FrameWidth;
                        var frameHeight = _previewStrip.FrameHeight;

                        var tmpPath = System.IO.Path.GetTempFileName() + ".png";
                        System.IO.File.WriteAllBytes(tmpPath, _previewPngData);
                        try
                        {
                            var fullBitmap = new Avalonia.Media.Imaging.Bitmap(tmpPath);
                            var frameRect = new PixelRect(frameWidth * _previewFrameIndex, 0, frameWidth, frameHeight);
                            var croppedBitmap = new CroppedBitmap(fullBitmap, frameRect);
                            img2.Source = croppedBitmap;

                            _previewFrameIndex++;
                            if (_previewFrameIndex >= _previewStrip.FrameCount)
                                _previewFrameIndex = 0;
                        }
                        finally { try { System.IO.File.Delete(tmpPath); } catch { } }
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
