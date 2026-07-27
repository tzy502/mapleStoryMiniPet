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
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// 素材浏览弹窗 — 三栏布局：分类树 | 条目列表 | 详情预览
/// </summary>
public class MaterialBrowserWindow : Window
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
    private readonly List<string> _previewActions = new() { "stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die" };

    // ── 分页 ──
    private int _currentPage = 1;
    private int _pageSize = 10;
    private int _totalPages = 1;

    // ── UI 引用 ──
    private TextBlock? _pageLabel;
    private Button? _prevBtn;
    private Button? _nextBtn;
    private TextBox? _searchInput;
    private ListBox? _itemList;
    private Border? _previewPanel;
    private TextBlock? _detailTitle;
    private TextBlock? _detailInfo;
    private TextBlock? _detailType;
    private readonly Dictionary<string, Border> _categoryButtons = new();

    // ── 事件 ──
    public event Action<int, string>? OnMaterialSelected;

    /// <summary>
    /// 素材条目
    /// </summary>
    public record MaterialEntry(string Category, int Id, string Name, string Emoji = "📦")
    {
        public string Display => $"{Emoji} {Name}  [{Id}]";
    }

    public MaterialBrowserWindow(WzService? wzService = null, ConfigService? configService = null)
    {
        _wzService = wzService;
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
                    ? new("提示", 0, "WZ 加载失败，请检查已保存的 WZ 路径", "⚠️")
                    : new("提示", 0, "WZ 未配置，请先在设置中心配置 WZ 路径并保存", "⚠️")
            };
        _filteredEntries = new List<MaterialEntry>(_allEntries);

        Title = "素材浏览";
        Width = 860;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushBg;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(new GridLength(46)));

        // ── 主体三栏 ──
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

        // ── 底栏 ──
        var bottomBar = BuildBottomBar();
        root.Children.Add(bottomBar);
        Grid.SetRow(bottomBar, 1);

        Content = root;

        // 初始加载全部
        SelectCategory("全部");
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

        var categories = new[] { "全部", "怪物", "NPC", "装备", "发型", "脸型", "坐骑", "椅子" };
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
    // 列表面板
    // ═══════════════════════════════════════════
    private Border BuildListPanel()
    {
        var dock = new DockPanel();

        // 标题
        var header = new TextBlock
        {
            Text = "📋 条目列表",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = BrushTxtPrimary,
            Margin = new Thickness(14, 10, 0, 6)
        };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        // 列表
        _itemList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0)
        };
        _itemList.SelectionChanged += OnItemSelected;
        dock.Children.Add(_itemList);

        return new Border { Child = dock };
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

        var stack = new StackPanel { Margin = new Thickness(14, 16), Spacing = 10 };

        // 预览区
        var previewBox = new Border
        {
            Height = 180,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 94, 156, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

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
        stack.Children.Add(previewBox);

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

        var selectBtn = new Button
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
        selectBtn.Click += (_, _) =>
        {
            if (_itemList?.SelectedItem is MaterialEntry entry)
            {
                Console.WriteLine($"[MaterialBrowser] OnMaterialSelected invoked: Id={entry.Id}, Name={entry.Name}");
                OnMaterialSelected?.Invoke(entry.Id, entry.Name);
                Close();
            }
        };
        btnRow.Children.Add(selectBtn);

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
        cancelBtn.Click += (_, _) => Close();
        btnRow.Children.Add(cancelBtn);

        stack.Children.Add(btnRow);

        _previewPanel.Child = stack;
        return _previewPanel;
    }

    // ═══════════════════════════════════════════
    // 底栏：搜索 + 分页
    // ═══════════════════════════════════════════
    private Border BuildBottomBar()
    {
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(200)));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(280)));
        bar.Height = 46;
        bar.Background = new SolidColorBrush(Color.FromArgb(255, 22, 22, 28));
        bar.Margin = new Thickness(0);

        // 搜索框
        var searchPanel = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(1),
            Background = BrushInputBg,
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
            Margin = new Thickness(8, 0, 0, 0)
        };
        _searchInput = new TextBox
        {
            PlaceholderText = "按名称 / ID 搜索…",
            FontSize = 12,
            Foreground = BrushTxtPrimary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(28, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _searchInput.TextChanged += OnSearchChanged;

        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(_searchInput);
        searchPanel.Child = searchGrid;

        Grid.SetColumn(searchPanel, 0);
        bar.Children.Add(searchPanel);

        // 分页控件
        var pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
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
        _prevBtn.Click += (_, _) => GoPage(-1);
        pager.Children.Add(_prevBtn);

        _pageLabel = new TextBlock
        {
            Text = "第 1/1 页",
            FontSize = 11,
            Foreground = BrushTxtSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 80,
            TextAlignment = TextAlignment.Center
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
        _nextBtn.Click += (_, _) => GoPage(+1);
        pager.Children.Add(_nextBtn);

        Grid.SetColumn(pager, 1);
        bar.Children.Add(pager);

        // 右侧 — 条目计数
        var countLabel = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Foreground = BrushTxtDim,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 0)
        };
        Grid.SetColumn(countLabel, 2);
        bar.Children.Add(countLabel);

        return new Border { Child = bar };
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
    private void SelectCategory(string category)
    {
        _activeCategory = category;
        _currentPage = 1;

        // 更新分类按钮样式
        foreach (var (cat, border) in _categoryButtons)
        {
            var isActive = cat == category;
            border.Background = isActive
                ? new SolidColorBrush(Color.FromArgb(26, 94, 156, 255))
                : Brushes.Transparent;

            if (border.Child is DockPanel row)
            {
                if (row.Children[0] is Border indicator)
                    indicator.Background = isActive ? BrushAccent : Brushes.Transparent;
                if (row.Children[1] is TextBlock txt)
                {
                    txt.Foreground = isActive ? BrushAccent : BrushTxtSecondary;
                    txt.FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal;
                }
            }
        }

        // 过滤 + 刷新
        FilterAndRefresh();
    }

    // ═══════════════════════════════════════════
    // 搜索过滤
    // ═══════════════════════════════════════════
    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _currentPage = 1;
        FilterAndRefresh();
    }

    private void FilterAndRefresh()
    {
        var query = (_searchInput?.Text ?? "").Trim();

        _filteredEntries = _allEntries
            .Where(e => _activeCategory == "全部" || e.Category == _activeCategory)
            .Where(e =>
                string.IsNullOrEmpty(query) ||
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Id.ToString().Contains(query))
            .ToList();

        _totalPages = Math.Max(1, (int)Math.Ceiling(_filteredEntries.Count / (double)_pageSize));
        if (_currentPage > _totalPages)
            _currentPage = _totalPages;

        RefreshList();
        UpdatePager();
    }

    // ═══════════════════════════════════════════
    // 列表刷新
    // ═══════════════════════════════════════════
    private void RefreshList()
    {
        if (_itemList == null) return;

        var page = _filteredEntries
            .Skip((_currentPage - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        _itemList.ItemsSource = page;
        _itemList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MaterialEntry>((_, _) =>
        {
            var row = new DockPanel { Margin = new Thickness(12, 4), Height = 42 };

            var iconBorder = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = new TextBlock
            {
                [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Emoji"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(iconBorder);

            var info = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            info.Children.Add(new TextBlock
            {
                [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Name"),
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = BrushTxtPrimary
            });
            info.Children.Add(new TextBlock
            {
                [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Id"),
                FontSize = 10,
                Foreground = BrushTxtDim,
                FontFamily = new FontFamily("SF Mono, monospace")
            });
            row.Children.Add(info);

            var catBadge = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(26, 94, 156, 255)),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            catBadge.Child = new TextBlock
            {
                [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Category"),
                FontSize = 9,
                Foreground = BrushAccent
            };
            row.Children.Add(catBadge);

            return row;
        });
    }

    // ═══════════════════════════════════════════
    // 条目选中
    // ═══════════════════════════════════════════
    private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_itemList?.SelectedItem is not MaterialEntry entry) return;
        UpdateDetail(entry);
    }

    private void UpdateDetail(MaterialEntry entry)
    {
        if (_detailTitle != null) _detailTitle.Text = entry.Name;
        if (_detailInfo != null) _detailInfo.Text = $"ID: {entry.Id}";
        if (_detailType != null) _detailType.Text = $"类型: {entry.Category}";

        // 加载预览图
        LoadPreview(entry);
    }

    private void LoadPreview(MaterialEntry entry)
    {
        if (_wzService == null || !_wzService.IsLoaded || _previewPanel == null) return;

        try
        {
            // 按动作顺序找第一个有帧的动作
            string category = entry.Category switch
            {
                "怪物" => "Mob",
                "NPC" => "Npc",
                _ => "Mob"
            };

            foreach (var action in _previewActions)
            {
                var pngData = _wzService.BuildSpriteStripPng(entry.Id, action, category);
                if (pngData != null && pngData.Length > 0)
                {
                    using var ms = new MemoryStream(pngData);
                    var bmp = new Bitmap(ms);

                    var strip = _wzService.ExportSpriteStrip(entry.Id, action, category).Item1;
                    int frameW = strip?.FrameWidth ?? bmp.PixelSize.Width;
                    int frameH = strip?.FrameHeight ?? bmp.PixelSize.Height;

                    var image = new Image
                    {
                        Source = bmp,
                        Width = frameW,
                        Height = frameH,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    _previewPanel.Child = image;
                    return;
                }
            }

            // 没找到任何动作，显示占位
            _previewPanel.Child = new TextBlock
            {
                Text = "🎨\n无预览",
                FontSize = 13,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 1.5
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MaterialBrowser] LoadPreview({entry.Id}): {ex.Message}");
            _previewPanel.Child = new TextBlock
            {
                Text = "🎨\n预览加载失败",
                FontSize = 13,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 1.5
            };
        }
    }

    // ═══════════════════════════════════════════
    // 分页
    // ═══════════════════════════════════════════
    private void GoPage(int delta)
    {
        var newPage = _currentPage + delta;
        if (newPage < 1 || newPage > _totalPages) return;
        _currentPage = newPage;
        RefreshList();
        UpdatePager();
    }

    private void UpdatePager()
    {
        if (_pageLabel != null)
            _pageLabel.Text = $"第 {_currentPage}/{_totalPages} 页";
        if (_prevBtn != null)
            _prevBtn.IsEnabled = _currentPage > 1;
        if (_nextBtn != null)
            _nextBtn.IsEnabled = _currentPage < _totalPages;
    }

    // ═══════════════════════════════════════════
    // 示例数据
    // ═══════════════════════════════════════════
    private static List<MaterialEntry> LoadFromWz(WzService wz)
    {
        var entries = new List<MaterialEntry>();
        try
        {
            var mobIds = wz.ListMobIds();
            foreach (var id in mobIds)
            {
                var name = wz.GetMobName(id);
                if (string.IsNullOrEmpty(name)) name = $"怪物_{id}";
                entries.Add(new MaterialEntry("怪物", id, name, "👾"));
            }

            var npcIds = wz.ListNpcIds();
            foreach (var id in npcIds)
            {
                var name = wz.GetNpcName(id);
                if (string.IsNullOrEmpty(name)) name = $"NPC_{id}";
                entries.Add(new MaterialEntry("NPC", id, name, "🧑"));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MaterialBrowser] LoadFromWz: {ex.Message}");
        }
        return entries;
    }

    private static List<MaterialEntry> BuildSampleData()
    {
        return new List<MaterialEntry>
        {
            // 怪物
            new("怪物", 8880150, "路西德", "👼"),
            new("怪物", 8860000, "品克缤", "🎀"),
            new("怪物", 9300573, "狐狸", "🦊"),
            new("怪物", 8600001, "龙", "🐉"),
            new("怪物", 2230000, "机器人", "🤖"),
            new("怪物", 9400121, "蝙蝠怪", "🦇"),
            new("怪物", 9300730, "雪人", "⛄"),
            new("怪物", 8810114, "黑龙", "🐲"),
            new("怪物", 8880000, "扎昆", "🪨"),
            new("怪物", 8500002, "闹钟", "⏰"),
            new("怪物", 8800002, "品克缤(大)", "🎀"),
            new("怪物", 9300013, "企鹅", "🐧"),
            new("怪物", 9400113, "暗黑龙王", "🐲"),
            new("怪物", 9300452, "蜗牛", "🐌"),

            // NPC
            new("NPC", 11000, "达夫", "🧙"),
            new("NPC", 12000, "希拉", "🧝"),
            new("NPC", 1012100, "弓箭手教练", "🏹"),
            new("NPC", 1022000, "魔法师教练", "🔮"),
            new("NPC", 1052100, "商店老板", "💊"),
            new("NPC", 9201000, "克丽斯", "👩‍🦰"),
            new("NPC", 9201001, "奥利弗", "👨‍🦰"),

            // 装备
            new("装备", 1002140, "青龙头盔", "⛑️"),
            new("装备", 1050100, "青龙战甲", "🛡️"),
            new("装备", 1082001, "青龙手套", "🧤"),
            new("装备", 1072158, "青龙靴", "👢"),
            new("装备", 1302056, "青龙刀", "🗡️"),

            // 发型
            new("发型", 30000, "经典发型", "💇"),
            new("发型", 30100, "短发", "💇‍♂️"),
            new("发型", 30200, "长发", "💇‍♀️"),
            new("发型", 30300, "双马尾", "👧"),

            // 脸型
            new("脸型", 20000, "标准脸", "😊"),
            new("脸型", 20100, "可爱脸", "😋"),
            new("脸型", 20200, "酷脸", "😎"),
            new("脸型", 20300, "惊讶脸", "😲"),

            // 坐骑
            new("坐骑", 1902000, "红色摩托车", "🏍️"),
            new("坐骑", 1902001, "蓝色龙", "🐉"),
            new("坐骑", 1902002, "云朵", "☁️"),
            new("坐骑", 1902003, "独角兽", "🦄"),

            // 椅子
            new("椅子", 3010000, "木椅", "🪑"),
            new("椅子", 3010001, "王座", "👑"),
            new("椅子", 3010002, "沙发", "🛋️"),
            new("椅子", 3010003, "月亮椅", "🌙"),
        };
    }
}
