using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiniPet.Models;

namespace MiniPet.Views;

/// <summary>
/// MaterialBrowserWindow 逻辑部分（partial 拆分）：分类筛选、搜索、分页、详情/预览、
/// WZ 素材加载。原实现从 MaterialBrowserWindow.cs 平移，行为完全一致。
/// </summary>
public partial class MaterialBrowserWindow
{
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
            new("怪物", "8880150", "路西德", "👼"),
            new("怪物", "8860000", "品克缤", "🎀"),
            new("怪物", "9300573", "狐狸", "🦊"),
            new("怪物", "8600001", "龙", "🐉"),
            new("怪物", "2230000", "机器人", "🤖"),
            new("怪物", "9400121", "蝙蝠怪", "🦇"),
            new("怪物", "9300730", "雪人", "⛄"),
            new("怪物", "8810114", "黑龙", "🐲"),
            new("怪物", "8880000", "扎昆", "🪨"),
            new("怪物", "8500002", "闹钟", "⏰"),
            new("怪物", "8800002", "品克缤(大)", "🎀"),
            new("怪物", "9300013", "企鹅", "🐧"),
            new("怪物", "9400113", "暗黑龙王", "🐲"),
            new("怪物", "9300452", "蜗牛", "🐌"),

            // NPC
            new("NPC", "11000", "达夫", "🧙"),
            new("NPC", "12000", "希拉", "🧝"),
            new("NPC", "1012100", "弓箭手教练", "🏹"),
            new("NPC", "1022000", "魔法师教练", "🔮"),
            new("NPC", "1052100", "商店老板", "💊"),
            new("NPC", "9201000", "克丽斯", "👩‍🦰"),
            new("NPC", "9201001", "奥利弗", "👨‍🦰"),

            // 装备
            new("装备", "1002140", "青龙头盔", "⛑️"),
            new("装备", "1050100", "青龙战甲", "🛡️"),
            new("装备", "1082001", "青龙手套", "🧤"),
            new("装备", "1072158", "青龙靴", "👢"),
            new("装备", "1302056", "青龙刀", "🗡️"),

            // 发型
            new("发型", "30000", "经典发型", "💇"),
            new("发型", "30100", "短发", "💇‍♂️"),
            new("发型", "30200", "长发", "💇‍♀️"),
            new("发型", "30300", "双马尾", "👧"),

            // 脸型
            new("脸型", "20000", "标准脸", "😊"),
            new("脸型", "20100", "可爱脸", "😋"),
            new("脸型", "20200", "酷脸", "😎"),
            new("脸型", "20300", "惊讶脸", "😲"),

            // 坐骑
            new("坐骑", "1902000", "红色摩托车", "🏍️"),
            new("坐骑", "1902001", "蓝色龙", "🐉"),
            new("坐骑", "1902002", "云朵", "☁️"),
            new("坐骑", "1902003", "独角兽", "🦄"),

            // 椅子
            new("椅子", "3010000", "木椅", "🪑"),
            new("椅子", "3010001", "王座", "👑"),
            new("椅子", "3010002", "沙发", "🛋️"),
            new("椅子", "3010003", "月亮椅", "🌙"),
        };
    }
}
