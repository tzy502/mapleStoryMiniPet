using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// 素材浏览弹窗 — 三栏布局：分类树 | 条目列表 | 详情预览
/// </summary>
public partial class MaterialBrowserWindow : Window
{
    // ── 颜色常量 ──
    private static readonly Color BgColor = Color.FromArgb(255, 28, 28, 34);
    private static readonly Color CardColor = Color.FromArgb(255, 36, 36, 44);
    private static readonly Color BorderColor = Color.FromArgb(255, 56, 56, 66);
    private static readonly Color AccentColor = Color.FromArgb(255, 94, 156, 255);
    private static readonly Color TxtPrimary = Color.FromArgb(255, 235, 235, 240);
    private static readonly Color TxtSecondary = Color.FromArgb(255, 155, 155, 170);
    private static readonly Color TxtDim = Color.FromArgb(255, 100, 100, 115);
    private static readonly Color InputBg = Color.FromArgb(180, 38, 38, 46);

    private static readonly SolidColorBrush BrushBg = new(BgColor);
    private static readonly SolidColorBrush BrushCard = new(CardColor);
    private static readonly SolidColorBrush BrushBorder = new(BorderColor);
    // input 边框与全项目统一（AI 配置样式：半透明浅灰，胶水 2026-08-15）
    private static readonly SolidColorBrush InputBorder = new(Color.FromArgb(77, 120, 120, 140));
    private static readonly SolidColorBrush BrushAccent = new(AccentColor);
    private static readonly SolidColorBrush BrushTxtPrimary = new(TxtPrimary);
    private static readonly SolidColorBrush BrushTxtSecondary = new(TxtSecondary);
    private static readonly SolidColorBrush BrushTxtDim = new(TxtDim);
    private static readonly SolidColorBrush BrushInputBg = new(InputBg);

    // ── 数据 ──
    private readonly List<MaterialEntry> _allEntries;
    private List<MaterialEntry> _filteredEntries;
    private string _activeCategory = "全部";
    private readonly WzService? _wzService;
    private readonly ConfigService? _configService;
    private readonly PaperdollService? _paperdoll;
    private readonly string? _initialCategory;
    // 纸娃娃草稿外观（素材库选件用；穿身上预览 + 应用到主窗口）
    private CharacterAppearance _draftAppearance = CreateDefaultDraft();
    private readonly List<string> _previewActions = new() { "stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die" };

    private static CharacterAppearance CreateDefaultDraft() => new()
    {
        Gender = 0,
        Hair = new ItemInfo { Id = "30000" },
        Face = new ItemInfo { Id = "20000" },
    };

    /// <summary>纸娃娃部件分类（皮肤/发型/脸型/各部位/坐骑/椅子）：选中走草稿 + 穿身上预览。</summary>
    private static bool IsPaperdollCategory(string category) =>
        category is "皮肤" or "发型" or "脸型" or "帽子" or "上衣" or "长袍" or "裤子" or "鞋子"
            or "手套" or "武器" or "盾牌" or "披风" or "饰品" or "坐骑" or "椅子";

    // ── 分页 ──
    private int _currentPage = 1;
    private int _pageSize = 10;
    private int _totalPages = 1;

    // ── 列表展示行（发型同名分组后：组头行 + 变体行 + 普通条目行混排）──
    private List<object> _displayRows = new();
    private readonly HashSet<string> _expandedHairGroups = new(StringComparer.Ordinal);
    private Border? _hairDyePanel;
    private int _previewRenderSeq;

    // ── 搜索防抖（300ms）+ 清除按钮 ──
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Button? _searchClearBtn;
    private TextBlock? _countLabel;

    // ── UI 引用 ──
    private TextBox? _pageLabel;
    private Button? _prevBtn;
    private Button? _nextBtn;
    private TextBox? _searchInput;
    private ListBox? _itemList;
    private Border? _previewPanel;
    private Border? _previewBox;
    private TextBlock? _detailTitle;
    private TextBlock? _detailInfo;
    private TextBlock? _detailType;
    private Button? _selectBtn;
    private ComboBox? _outfitCombo;
    private readonly Dictionary<string, Border> _categoryButtons = new();

    // ── 事件 ──
    public event Action<string, string, string>? OnMaterialSelected;
    /// <summary>纸娃娃草稿应用到主窗口（素材库选件完成后）。</summary>
    public event Action<CharacterAppearance>? OnPaperdollDraftApplied;

    /// <summary>
    /// 素材条目
    /// </summary>
    public record MaterialEntry(string Category, string Id, string Name, string Emoji = "📦")
    {
        /// <summary>缩略图（默认动画 stand 首帧，后台懒加载缓存；胶水规则 2026-08-09）。</summary>
        public Bitmap? Thumb { get; set; }
        public string Display => $"{Emoji} {Name}  [{Id}]";
    }

    public MaterialBrowserWindow(WzService? wzService = null, ConfigService? configService = null, PaperdollService? paperdoll = null, string? initialCategory = null)
    {
        _wzService = wzService;
        _configService = configService;
        _paperdoll = paperdoll;
        _initialCategory = initialCategory;
        // 草稿初始化为当前主窗口搭配（如有），素材库选件延续当前外观
        try
        {
            var saved = configService?.LoadSettings().CurrentPaperdoll;
            if (saved != null)
            {
                _draftAppearance = CloneDraft(saved);
            }
        }
        catch { }
        var hasWzConfig = false;
        if (wzService != null && !wzService.IsLoaded && configService != null)
        {
            try
            {
                var wzConfig = configService.LoadWzConfig();
                hasWzConfig = !string.IsNullOrWhiteSpace(wzConfig.BaseWzPath);
                if (hasWzConfig)
                {
                    var (success, error) = wzService.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
                    if (!success)
                        Console.Error.WriteLine($"[MaterialBrowser] WZ 加载失败: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MaterialBrowser] WZ 自动加载失败: {ex.Message}");
            }
        }

        if (wzService != null && wzService.IsLoaded)
            _allEntries = LoadFromWz(wzService);
        else
            _allEntries = new List<MaterialEntry>
            {
                hasWzConfig
                    ? new("提示", "0", "WZ 加载失败，请检查已保存的 WZ 路径", "⚠️")
                    : new("提示", "0", "WZ 未配置，请先在设置中心配置 WZ 路径并保存", "⚠️")
            };
        _filteredEntries = new List<MaterialEntry>(_allEntries);

        Title = "素材浏览";
        Width = 860;
        Height = 600;
        // 自适应：加最小尺寸，防拖太小布局错乱
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushBg;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        // ── 主体三栏（筛选/搜索/分页已并入条目列表列，分类树与详情列满高）──
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(200)));   // 分类树
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));     // 分隔线
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));       // 条目列表
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));     // 分隔线
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));   // 详情预览

        body.Children.Add(BuildCategoryPanel());
        Grid.SetColumn(body.Children[^1], 0);

        body.Children.Add(BuildDivider());
        Grid.SetColumn(body.Children[^1], 1);

        body.Children.Add(BuildListPanel());
        Grid.SetColumn(body.Children[^1], 2);

        body.Children.Add(BuildDivider());
        Grid.SetColumn(body.Children[^1], 3);

        body.Children.Add(BuildDetailPanel());
        Grid.SetColumn(body.Children[^1], 4);

        root.Children.Add(body);
        Grid.SetRow(body, 0);

        Content = root;

        // 防抖定时器随窗口关闭停止（防 DispatcherTimer 残留）
        Closed += (_, _) => _searchDebounceTimer.Stop();

        // 初始加载（有预选分类则选该分类）
        SelectCategory(_initialCategory ?? "全部");
    }

    // ═══════════════════════════════════════════
    // 分类面板
    // ═══════════════════════════════════════════
    private Border BuildCategoryPanel()
    {
        var stack = new StackPanel { Margin = new Thickness(6, 8) };

        var title = new TextBlock
        {
            Text = "📂 分类",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = BrushTxtPrimary,
            Margin = new Thickness(8, 0, 0, 10)
        };
        stack.Children.Add(title);

        var categories = new[] { "全部", "怪物", "NPC", "皮肤", "发型", "脸型", "帽子", "上衣", "长袍", "裤子", "鞋子", "手套", "武器", "盾牌", "披风", "饰品", "坐骑", "椅子" };
        foreach (var cat in categories)
        {
            var btn = BuildCategoryButton(cat);
            _categoryButtons[cat] = btn;
            stack.Children.Add(btn);
        }

        return new Border { Child = new ScrollViewer { Content = stack } };
    }

    private Border BuildCategoryButton(string category)
    {
        var isActive = category == "全部";
        var txt = new TextBlock
        {
            Text = category,
            FontSize = 12,
            FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
            Foreground = isActive ? BrushAccent : BrushTxtSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var indicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = isActive ? BrushAccent : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 6)
        };

        var row = new DockPanel();
        row.Children.Add(indicator);
        row.Children.Add(txt);

        var container = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Background = isActive ? new SolidColorBrush(Color.FromArgb(26, 94, 156, 255)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
            Margin = new Thickness(0, 1)
        };

        container.PointerPressed += (_, _) => SelectCategory(category);
        container.PointerEntered += (_, _) =>
        {
            if (_activeCategory != category)
                container.Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255));
        };
        container.PointerExited += (_, _) =>
        {
            if (_activeCategory != category)
                container.Background = Brushes.Transparent;
        };

        return container;
    }

    // ═══════════════════════════════════════════
    // 列表面板（筛选/搜索/分页并入本列：筛选行 Auto + 列表 Star + 分页行 Auto，分类树与详情列满高）
    // ═══════════════════════════════════════════
    private Border BuildListPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 筛选行：标题 + 搜索 + 计数
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 条目列表
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 分页行

        // 筛选行（与条目列表同处一列：搜索/计数并入列表列头部，不再单列全宽筛选行）
        var filterBar = BuildListFilterBar();
        Grid.SetRow(filterBar, 0);
        grid.Children.Add(filterBar);

        // 列表
        _itemList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0)
        };
        _itemList.SelectionChanged += OnItemSelected;
        Grid.SetRow(_itemList, 1);
        grid.Children.Add(_itemList);

        // 分页行（原底栏分页移入列表列底部，功能不变）
        var pager = BuildPager();
        Grid.SetRow(pager, 2);
        grid.Children.Add(pager);

        return new Border { Child = grid };
    }

    // ═══════════════════════════════════════════
    // 详情面板
    // ═══════════════════════════════════════════
    private Border BuildDetailPanel()
    {
        _previewPanel = new Border
        {
            Margin = new Thickness(8, 8),
            CornerRadius = new CornerRadius(8),
            Background = BrushCard,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1)
        };

        // 自适应：详情面板改 Grid（预览区 Star 随面板伸缩，下方文字/按钮 Auto 行内滚动）
        var detailGrid = new Grid();
        detailGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        detailGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var stack = new StackPanel { Margin = new Thickness(14, 16), Spacing = 10 };

        // 预览区（独立字段：LoadPreview 只替换预览框内容，保留下方标题/信息/按钮行）
        // 自适应：去掉固定 Height=180，改 Star 行填满 + 图片 Uniform 等比缩放
        var previewBox = new Border
        {
            Margin = new Thickness(14, 16, 14, 4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 94, 156, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };
        _previewBox = previewBox;

        var placeholder = new TextBlock
        {
            Text = "🎨\n选择条目查看预览",
            FontSize = 13,
            Foreground = BrushTxtDim,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 1.5
        };
        previewBox.Child = placeholder;

        // 详情信息
        _detailTitle = new TextBlock
        {
            Text = "",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = BrushTxtPrimary,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_detailTitle);

        _detailInfo = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = BrushTxtSecondary,
            FontFamily = new FontFamily("SF Mono, monospace")
        };
        stack.Children.Add(_detailInfo);

        _detailType = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = BrushTxtDim
        };
        stack.Children.Add(_detailType);

        // 按钮行
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };

        _selectBtn = new Button
        {
            Content = "✅ 选择",
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(16, 6),
            Background = BrushAccent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        UiStyle.ApplyHover(_selectBtn);
        _selectBtn.Click += (_, _) =>
        {
            // 未选中条目：忽略点击（保持原行为，不关闭）
            if (_itemList?.SelectedItem is not MaterialEntry entry)
            {
                return;
            }
            try
            {
                if (IsPaperdollCategory(entry.Category) && _paperdoll != null)
                {
                    // 纸娃娃部件：草稿已由 LoadPaperdollPreview（ApplyDraftPart）写入选中部件，点按钮应用草稿到主窗口
                    OnPaperdollDraftApplied?.Invoke(_draftAppearance);
                    return;
                }
                // 怪物/NPC：OnMaterialSelected 并关闭
                OnMaterialSelected?.Invoke(entry.Id, entry.Name, entry.Category);
            }
            catch (Exception ex)
            {
                // Invoke 订阅方异常不吞：记录日志，finally 仍保证窗口关闭
                Console.Error.WriteLine($"[MaterialBrowser] Select({entry.Id}): {ex.Message}");
            }
            finally
            {
                // 选择后素材库窗口必须关闭（即使订阅方抛异常）
                Close();
            }
        };
        btnRow.Children.Add(_selectBtn);

        var cancelBtn = new Button
        {
            Content = "取消",
            FontSize = 11,
            Padding = new Thickness(16, 6),
            Background = Brushes.Transparent,
            Foreground = BrushTxtSecondary,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        UiStyle.ApplyHover(cancelBtn);
        cancelBtn.Click += (_, _) => Close();
        btnRow.Children.Add(cancelBtn);

        stack.Children.Add(btnRow);

        // 发型组内染发调色（仅选中发型时显示：HSV 滑块联动草稿 Hair，预览实时合成）
        _hairDyePanel = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
            Padding = new Thickness(8, 6),
            IsVisible = false
        };
        stack.Children.Add(_hairDyePanel);

        // 搭配管理（纸娃娃草稿保存/加载，走 SavedPaperdolls 磁盘存档）
        // 搭配行改用两列 Grid：保存按钮列 Auto 自适应，下拉列 Star 填充剩余宽度（原 StackPanel 无法拉伸子项）
        var outfitRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        outfitRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        outfitRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var saveOutfitBtn = new Button
        {
            Content = "💾 保存搭配",
            FontSize = 11,
            Padding = new Thickness(10, 5),
            Background = Brushes.Transparent,
            Foreground = BrushTxtSecondary,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        UiStyle.ApplyHover(saveOutfitBtn);
        saveOutfitBtn.Click += (_, _) => { try { SaveDraftOutfit(); } catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] SaveOutfit: {ex.Message}"); } };
        outfitRow.Children.Add(saveOutfitBtn);
        _outfitCombo = new ComboBox
        {
            Height = 26,
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            BorderBrush = InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(6, 0, 0, 0),
            PlaceholderText = "加载搭配"
        };
        _outfitCombo.SelectionChanged += (_, _) => { try { LoadDraftOutfit(); } catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] LoadOutfit: {ex.Message}"); } };
        Grid.SetColumn(_outfitCombo, 1);
        outfitRow.Children.Add(_outfitCombo);
        stack.Children.Add(outfitRow);
        RefreshOutfitList();

        // 自适应：下方文字/按钮区包 ScrollViewer（窗口矮时不被挤没），预览区仍占 Star 行
        var stackScroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 240
        };
        Grid.SetRow(previewBox, 0);
        detailGrid.Children.Add(previewBox);
        Grid.SetRow(stackScroll, 1);
        detailGrid.Children.Add(stackScroll);
        _previewPanel.Child = detailGrid;
        return _previewPanel;
    }

    // ═══════════════════════════════════════════
    // 列表列筛选行：标题 + 搜索框 + 计数（搜索防抖 / ×清除逻辑与原底栏一致）
    // ═══════════════════════════════════════════
    private Border BuildListFilterBar()
    {
        var bar = new Grid { Margin = new Thickness(14, 10, 14, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 标题
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // 搜索框
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 计数

        var header = new TextBlock
        {
            Text = "📋 条目列表",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = BrushTxtPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(header, 0);
        bar.Children.Add(header);

        // 搜索框：标准 input 样式（白底黑字、InputBorder 边框、圆角 6），TextBox 自带边框圆角，外层仅做定位容器
        var searchPanel = new Border
        {
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0)
        };
        var searchGrid = new Grid();
        var searchIcon = new TextBlock
        {
            Text = "🔍",
            FontSize = 12,
            Foreground = BrushTxtDim,
            VerticalAlignment = VerticalAlignment.Center,
            // 图标不拦截鼠标事件，点击放大镜区域透传给下层输入框聚焦
            IsHitTestVisible = false,
            Margin = new Thickness(8, 0, 0, 0)
        };
        _searchInput = new TextBox
        {
            PlaceholderText = "按名称 / ID 搜索…",
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            BorderBrush = InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            // 左侧留 30 给放大镜图标（图标 Margin 左 8 + 字形宽约 14，30 足够不压字），右侧 26 给 × 清除按钮
            Padding = new Thickness(30, 6, 26, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        // 300ms 防抖：输入停顿后才过滤（防每键全列表重建）
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _currentPage = 1;
            FilterAndRefresh();
        };
        _searchInput.TextChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
            UpdateSearchClearVisibility();
        };

        // × 清除按钮（悬停/输入时显示，点击清空搜索并聚焦）
        _searchClearBtn = new Button
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
        UiStyle.ApplyHover(_searchClearBtn);
        _searchClearBtn.Click += (_, _) =>
        {
            _searchInput.Text = "";
            _searchDebounceTimer.Stop();
            _currentPage = 1;
            FilterAndRefresh();
            _searchInput.Focus();
        };

        // 先加输入框再加图标/清除按钮：Grid 后加的子项绘制在上层，保证 🔍/× 不被白底 TextBox 盖住
        searchGrid.Children.Add(_searchInput);
        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(_searchClearBtn);
        searchPanel.Child = searchGrid;

        Grid.SetColumn(searchPanel, 1);
        bar.Children.Add(searchPanel);

        // 计数（原底栏右侧移入筛选行右侧，UpdatePager 写入不变）
        _countLabel = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Foreground = BrushTxtDim,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(_countLabel, 2);
        bar.Children.Add(_countLabel);

        return new Border { Child = bar };
    }

    // ═══════════════════════════════════════════
    // 分页行：◀ 页码 ▶（原底栏分页移入列表列底部，逻辑不变）
    // ═══════════════════════════════════════════
    private StackPanel BuildPager()
    {
        var pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _prevBtn = new Button
        {
            Content = "◀",
            FontSize = 13,
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = BrushTxtSecondary,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        UiStyle.ApplyHover(_prevBtn);
        _prevBtn.Click += (_, _) => GoPage(-1);
        pager.Children.Add(_prevBtn);

        // 分页可输入：输入页码 Enter 跳转（胶水需求 2026-08-09）
        // 样式与全项目 input 统一（白底黑字圆角 6，胶水 2026-08-15）
        _pageLabel = new TextBox
        {
            Text = "1/1",
            FontSize = 12,
            Foreground = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Background = Brushes.White,
            BorderBrush = InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6)
        };
        _pageLabel.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            var t = _pageLabel.Text ?? "";
            var num = t.Split('/')[0].Trim();
            // 绝对跳页：越界由 GoToPage clamp，成功后 UpdatePager 统一恢复 "当前页/总页数" 显示
            if (int.TryParse(num, out var p))
            {
                GoToPage(p);
                return;
            }
            // 解析失败：恢复当前页显示，防误导
            _pageLabel.Text = $"{_currentPage}/{_totalPages}";
        };
        // 失焦：解析失败或越界时把文本恢复为当前页显示（未按 Enter 的无效输入不留存）
        _pageLabel.LostFocus += (_, _) =>
        {
            var t = _pageLabel.Text ?? "";
            var num = t.Split('/')[0].Trim();
            if (!int.TryParse(num, out var p) || p < 1 || p > _totalPages)
            {
                _pageLabel.Text = $"{_currentPage}/{_totalPages}";
            }
        };
        pager.Children.Add(_pageLabel);

        _nextBtn = new Button
        {
            Content = "▶",
            FontSize = 13,
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = BrushTxtSecondary,
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = false
        };
        UiStyle.ApplyHover(_nextBtn);
        _nextBtn.Click += (_, _) => GoPage(+1);
        pager.Children.Add(_nextBtn);

        return pager;
    }

    private static Border BuildDivider()
    {
        return new Border
        {
            Width = 1,
            Background = BrushBorder,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    // ═══════════════════════════════════════════
    // 分类切换
    // ═══════════════════════════════════════════
}
