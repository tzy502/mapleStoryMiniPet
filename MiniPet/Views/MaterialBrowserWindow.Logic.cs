using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiniPet.Models;
using MiniPet.Services;
using SkiaSharp;

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
    private void FilterAndRefresh()
    {
        var query = (_searchInput?.Text ?? "").Trim();

        // 2026-08-16：去除性别过滤（用户要求——男女角色不限制，发型/脸型全量展示）
        _filteredEntries = _allEntries
            .Where(e => _activeCategory == "全部" || e.Category == _activeCategory)
            .Where(e =>
                string.IsNullOrEmpty(query) ||
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Id.Contains(query))
            .ToList();

        // 发型同名折叠分组（组头 + 展开变体行）
        _displayRows = BuildDisplayRows(query);

        _totalPages = Math.Max(1, (int)Math.Ceiling(_displayRows.Count / (double)_pageSize));
        if (_currentPage > _totalPages)
        {
            _currentPage = _totalPages;
        }

        RefreshList();
        UpdatePager();
    }

    // ═══════════════════════════════════════════
    // 发型同名折叠分组（E：同名变体 → 组头 + 展开变体行）
    // ═══════════════════════════════════════════

    /// <summary>发型条目按显示名分组：多变体同名折叠为组；缺名（回退名）或单例保持原样。纯函数，可单测。</summary>
    internal static List<HairGroupRow> GroupHairByName(IEnumerable<MaterialEntry> hairEntries)
    {
        var groups = new Dictionary<string, HairGroupRow>(StringComparer.Ordinal);
        foreach (var e in hairEntries)
        {
            if (e == null || IsFallbackHairName(e.Name))
            {
                continue;
            }
            if (!groups.TryGetValue(e.Name, out var g))
            {
                g = new HairGroupRow { Name = e.Name };
                groups[e.Name] = g;
            }
            g.Variants.Add(e);
        }
        var result = new List<HairGroupRow>();
        foreach (var g in groups.Values)
        {
            if (g.Variants.Count > 1)
            {
                g.Variants.Sort((a, b) => NumericId(a.Id).CompareTo(NumericId(b.Id)));
                result.Add(g);
            }
        }
        return result;
    }

    /// <summary>构建列表展示行（组头 + 展开变体 + 普通条目，保持原过滤顺序）；搜索时自动展开匹配组。</summary>
    private List<object> BuildDisplayRows(string query)
    {
        var rows = new List<object>();
        var multiGroups = GroupHairByName(_filteredEntries.Where(e => e.Category == "发型")).ToDictionary(g => g.Name, StringComparer.Ordinal);
        if (multiGroups.Count == 0)
        {
            rows.AddRange(_filteredEntries);
            return rows;
        }

        var q = (query ?? "").Trim();
        if (q.Length > 0)
        {
            foreach (var g in multiGroups.Values)
            {
                if (g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.Variants.Any(v => v.Id.Contains(q) || v.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    _expandedHairGroups.Add(g.Name);
                }
            }
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _filteredEntries)
        {
            if (e.Category == "发型" && multiGroups.ContainsKey(e.Name))
            {
                if (!emitted.Add(e.Name))
                {
                    continue; // 变体已随组头发过
                }
                var g = multiGroups[e.Name];
                g.IsExpanded = _expandedHairGroups.Contains(g.Name);
                rows.Add(g);
                if (g.IsExpanded)
                {
                    rows.AddRange(g.Variants);
                }
            }
            else
            {
                rows.Add(e);
            }
        }
        return rows;
    }

    /// <summary>组头点击：展开/折叠（状态记入 _expandedHairGroups，过滤刷新不丢）。</summary>
    private void ToggleHairGroup(HairGroupRow g)
    {
        if (!_expandedHairGroups.Add(g.Name))
        {
            _expandedHairGroups.Remove(g.Name);
        }
        RefreshList();
    }

    /// <summary>缺名回退判断：AddCharacterCategory 缺名时生成 "{cat}_{id}"（发型_30000），此类不折叠。</summary>
    internal static bool IsFallbackHairName(string name) =>
        name.StartsWith("发型_", StringComparison.Ordinal);

    private static int NumericId(string id) => int.TryParse(id, out var n) ? n : int.MaxValue;

    /// <summary>性别联动过滤：男（Gender=0）只显示 30000–30999 发型 / 20000–20999 脸型，女相反；非发型/脸型不过滤。纯函数，可单测。</summary>
    /// <summary>发型/脸型性别过滤（2026-08-16 对齐 MapleSalon2 getHairGender/getFaceGender）：
    /// 按 id 千位判定——发型 0/3/5/6 男、1/4/7/8 女、其余通用；脸型 0/3/5/7 男、1/4/6/8 女、其余通用。
    /// 原规则 n>=31000 为女（36633 黄色电蓬发型千位 6 是男发却被当女发 → 男性角色搜索「黄色电」被过滤，用户反馈缺件）。</summary>
    internal static bool MatchesGender(string category, string id, int gender)
    {
        if (category != "发型" && category != "脸型")
        {
            return true;
        }
        if (!int.TryParse(id, out var n))
        {
            return true;
        }
        int tag = (n / 1000) % 10;
        if (category == "发型")
        {
            int hairGender = tag switch
            {
                0 or 3 or 5 or 6 => 0,   // 男
                1 or 4 or 7 or 8 => 1,   // 女
                _ => 2,                  // 通用（2/9）
            };
            return hairGender == 2 || hairGender == gender;
        }
        int faceGender = tag switch
        {
            0 or 3 or 5 or 7 => 0,   // 男
            1 or 4 or 6 or 8 => 1,   // 女
            _ => 2,                  // 通用
        };
        return faceGender == 2 || faceGender == gender;
    }

    // ═══════════════════════════════════════════
    // 列表刷新
    // ═══════════════════════════════════════════

    /// <summary>发型同名分组行（列表折叠组头；展开后紧跟变体条目行）。</summary>
    internal sealed class HairGroupRow
    {
        public string Name = "";
        public List<MaterialEntry> Variants = new();
        public bool IsExpanded;
    }

    private void RefreshList()
    {
        if (_itemList == null)
        {
            return;
        }

        var page = _displayRows
            .Skip((_currentPage - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        // 空态提示（搜索无匹配 / 性别过滤空 / 分类空目录）
        if (page.Count == 0 && _displayRows.Count == 0)
        {
            page.Add("🔍 无匹配条目（试试清除搜索或切换性别）");
        }

        _itemList.ItemsSource = page;
        _itemList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((data, _) =>
        {
            switch (data)
            {
                case HairGroupRow g:
                    return BuildHairGroupRow(g);
                case MaterialEntry entry:
                    return BuildEntryRow(entry);
                default:
                    return new TextBlock
                    {
                        Text = data?.ToString() ?? "",
                        FontSize = 12,
                        Foreground = BrushTxtDim,
                        Margin = new Thickness(12, 10)
                    };
            }
        });
    }

    /// <summary>条目行：缩略图（优先 icon，见 LoadThumbAsync）+ 名称/ID + 分类角标 + 悬停 tooltip。</summary>
    private Control BuildEntryRow(MaterialEntry entry)
    {
        var row = new DockPanel { Margin = new Thickness(12, 4), Height = 42 };

        var iconBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        if (entry != null && entry.Thumb != null)
        {
            iconBorder.Child = new Image { Source = entry.Thumb, Width = 32, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            var emojiText = new TextBlock
            {
                [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Emoji"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = emojiText;
            LoadThumbAsync(entry, emojiText);
        }
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

        // 类型徽标：emoji + 分类名，怪物红橙 / NPC 绿 / 纸娃娃 accent 蓝（「全部」混排时一眼区分 Mob·NPC 与纸娃娃）
        var (badgeBg, badgeFg) = CategoryBadgeColors(entry?.Category ?? "");
        var catBadge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(badgeBg),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0)
        };
        catBadge.Child = new TextBlock
        {
            Text = entry == null ? "" : $"{entry.Emoji} {entry.Category}",
            FontSize = 9,
            Foreground = new SolidColorBrush(badgeFg)
        };
        row.Children.Add(catBadge);

        // 悬停 tooltip：显示完整名称（MapleSalon2 参考）
        ToolTip.SetTip(row, entry?.Name ?? "");
        return row;
    }

    /// <summary>条目类型徽标配色：怪物红橙、NPC 绿、纸娃娃部件 accent 蓝、其余（提示等）灰——与分类组头同色系。</summary>
    private static (Color Bg, Color Fg) CategoryBadgeColors(string category)
    {
        if (category == "怪物")
        {
            return (MobBadgeBg, MobBadgeColor);
        }
        if (category == "NPC")
        {
            return (NpcBadgeBg, NpcBadgeColor);
        }
        if (IsPaperdollCategory(category))
        {
            return (Color.FromArgb(26, 94, 156, 255), AccentColor);
        }
        return (Color.FromArgb(26, 120, 120, 140), TxtSecondary);
    }

    /// <summary>发型分组组头行：名称 + 变体数 + 展开箭头，点击折叠/展开。</summary>
    private Control BuildHairGroupRow(HairGroupRow g)
    {
        var row = new DockPanel
        {
            Margin = new Thickness(12, 4),
            Height = 42,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var arrow = new TextBlock
        {
            Text = g.IsExpanded ? "▼" : "▶",
            FontSize = 10,
            Foreground = BrushTxtSecondary,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(arrow);
        var name = new TextBlock
        {
            Text = $"💇 {g.Name}（{g.Variants.Count} 色变体）",
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = BrushTxtPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        row.Children.Add(name);
        ToolTip.SetTip(row, g.IsExpanded ? "点击折叠" : $"点击展开 {g.Variants.Count} 个颜色变体");
        // 组头点击 = 折叠切换（标记 Handled 尽量不触发条目选中；OnItemSelected 对组头也做了忽略兜底）
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ToggleHairGroup(g);
        };
        return row;
    }

    // ═══════════════════════════════════════════
    // 条目选中
    // ═══════════════════════════════════════════
    private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        // 组头选中忽略（折叠切换已在 BuildHairGroupRow 处理），只处理条目
        if (_itemList?.SelectedItem is not MaterialEntry entry)
        {
            return;
        }
        UpdateSelectButtonText(entry);
        UpdateDetail(entry);
    }

    private void UpdateDetail(MaterialEntry entry)
    {
        if (_detailTitle != null)
        {
            _detailTitle.Text = entry.Name;
        }
        if (_detailInfo != null)
        {
            _detailInfo.Text = $"ID: {entry.Id}";
        }
        if (_detailType != null)
        {
            _detailType.Text = $"类型: {entry.Category}";
        }
        // 发型：显示组内染发调色面板（HSV 滑块联动草稿 Hair）
        if (_hairDyePanel != null)
        {
            _hairDyePanel.IsVisible = entry.Category == "发型";
            if (_hairDyePanel.IsVisible)
            {
                RefreshHairDyePanel();
            }
        }

        // 加载预览图
        LoadPreview(entry);
    }

    private void LoadPreview(MaterialEntry entry)
    {
        if (_wzService == null || !_wzService.IsLoaded || _previewBox == null)
        {
            return;
        }

        try
        {
            // 纸娃娃部件分类：详情预览 = 穿身上合成（草稿外观 + 该部件，stand1 首帧），
            // 预览即"穿在身上看到效果"；icon 只用于列表缩略图（LoadThumbAsync 已用 TryExtractIcon），
            // 详情预览不再单独显示装备 icon
            if (IsPaperdollCategory(entry.Category) && _paperdoll != null)
            {
                LoadPaperdollPreview(entry);
                return;
            }

            // 怪物/NPC：按动作顺序找第一个有帧的动作（无 icon 字段）
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

                    // 自适应：去掉固定帧尺寸约束，随预览框（Star 行）填满并等比缩放（纵横比不裁切）
                    var image = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };

                    SetPreviewChild(image);
                    return;
                }
            }

            // 没找到任何动作，显示占位
            SetPreviewChild(new TextBlock
            {
                Text = "🎨\n无预览",
                FontSize = 13,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 1.5
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MaterialBrowser] LoadPreview({entry.Id}): {ex.Message}");
            SetPreviewChild(new TextBlock
            {
                Text = "🎨\n预览加载失败",
                FontSize = 13,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 1.5
            });
        }
    }

    // ═══════════════════════════════════════════
    // icon 预览（D：WZ 装备节点自带 icon/iconRaw，ExtractPng 直取，比合成快）
    // ═══════════════════════════════════════════

    /// <summary>替换预览框内容：⚠️ 先换 Child 再 Dispose 旧 Image.Source——先 Dispose 会让旧 Image
    /// 在下一布局 pass 测量已释放位图 → ObjectDisposedException 崩溃（实测 SIGABRT，全项目同款修复）。</summary>
    private void SetPreviewChild(Control child)
    {
        if (_previewBox == null)
        {
            return;
        }
        var oldImg = _previewBox.Child as Image;
        _previewBox.Child = child;
        if (oldImg?.Source is IDisposable oldSrc)
        {
            oldSrc.Dispose();
        }
    }

    /// <summary>分类 → WZ img 根路径（icon 提取用；纸娃娃部件按 8 位补零）。纯函数，可单测。
    /// 椅子分组见 ChairIconRoot。</summary>
    internal static string? CategoryWzRoot(string category, string id)
    {
        return category switch
        {
            "发型" => $"Character/Hair/{id.PadLeft(8, '0')}.img",
            "脸型" => $"Character/Face/{id.PadLeft(8, '0')}.img",
            "帽子" => $"Character/Cap/{id.PadLeft(8, '0')}.img",
            "上衣" => $"Character/Coat/{id.PadLeft(8, '0')}.img",
            "套服" => $"Character/Longcoat/{id.PadLeft(8, '0')}.img",
            "裤子" => $"Character/Pants/{id.PadLeft(8, '0')}.img",
            "鞋子" => $"Character/Shoes/{id.PadLeft(8, '0')}.img",
            "手套" => $"Character/Glove/{id.PadLeft(8, '0')}.img",
            "武器" => $"Character/Weapon/{id.PadLeft(8, '0')}.img",
            "盾牌" => $"Character/Shield/{id.PadLeft(8, '0')}.img",
            "披风" => $"Character/Cape/{id.PadLeft(8, '0')}.img",
            "面饰" or "眼饰" or "耳环" => $"Character/Accessory/{id.PadLeft(8, '0')}.img",
            "坐骑" => $"Character/TamingMob/{id.PadLeft(8, '0')}.img",
            "椅子" => ChairIconRoot(id),
            _ => null
        };
    }

    /// <summary>
    /// 椅子 id → Item/Install 分组 img 路径（R-chair2，2026-10-10 修：原恒用 0301.img，但本 WZ
    /// （Install_000.wz 分片）无 0301.img → 素材浏览器椅子 icon 全部提取失败（列表有名字无图）。
    /// probe 实测分组：3010xxx-3014xxx 与 3016xxx-3019xxx 按 id/1000（03010.img…03019.img）、
    /// 3015xxx 按 id/100（030150.img…030159.img）、3020000+ 按 id/10000（0302.img）——
    /// 与 PaperdollService.ChairRoot 候选序的首选一致。</summary>
    internal static string ChairIconRoot(string id)
    {
        if (!int.TryParse(id, out var n) || n <= 0)
        {
            return $"Item/Install/0301.img/{id.PadLeft(8, '0')}";
        }
        string node = n.ToString("D8");
        string img = (n >= 3015000 && n < 3016000) ? $"{n / 100:D6}"
            : (n >= 3020000 ? $"{n / 10000:D4}" : $"{n / 1000:D5}");
        return $"Item/Install/{img}.img/{node}";
    }

    /// <summary>
    /// 尝试提取条目 icon PNG（对齐 MapleSalon2 getIconPath 语义，真实 WZ 实测存在）：
    /// 装备 → {root}/info/icon（帽/衣/裤/鞋/手套/武器/盾/披风/面饰/眼饰/耳环/坐骑）；
    /// 发型 → {root}/default/hair；脸型 → {root}/blink/0/face；
    /// 椅子 → {root}/info/icon（Item/Install/0301.img/{id}）；
    /// 回退根 icon/iconRaw；均无返回 null。
    /// </summary>
    private byte[]? TryExtractIcon(WzService wz, string category, string id)
    {
        var root = CategoryWzRoot(category, id);
        if (root == null)
        {
            return null;
        }
        string primary = category switch
        {
            "发型" => $"{root}/default/hair",
            "脸型" => $"{root}/blink/0/face",
            _ => $"{root}/info/icon",
        };
        var png = wz.ExtractPng(primary);
        if (png != null && png.Length > 0)
        {
            return png;
        }
        foreach (var sub in new[] { "icon", "iconRaw" })
        {
            png = wz.ExtractPng($"{root}/{sub}");
            if (png != null && png.Length > 0)
            {
                return png;
            }
        }
        return null;
    }

    private static Bitmap? DecodePngBytes(byte[]? png)
    {
        if (png == null || png.Length == 0)
        {
            return null;
        }
        try
        {
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════
    // 发型组内染发调色（E：HSV 滑块联动草稿 Hair）
    // ═══════════════════════════════════════════

    private void RefreshHairDyePanel()
    {
        if (_hairDyePanel == null)
        {
            return;
        }
        var h = _draftAppearance.Hair ?? new ItemInfo();
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(new TextBlock { Text = "🎨 染发调色（HSV，实时联动草稿）", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim });
        stack.Children.Add(BuildDyeRow("色相", 0, 360, h.Hue, v => ApplyHairDye(v, h.Saturation, h.Brightness)));
        stack.Children.Add(BuildDyeRow("饱和度", 0, 100, h.Saturation, v => ApplyHairDye(h.Hue, v, h.Brightness)));
        stack.Children.Add(BuildDyeRow("亮度", 0, 100, h.Brightness, v => ApplyHairDye(h.Hue, h.Saturation, v)));
        _hairDyePanel.Child = stack;
    }

    private StackPanel BuildDyeRow(string label, int min, int max, int value, Action<int> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40, TextAlignment = TextAlignment.Right });
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 110, VerticalAlignment = VerticalAlignment.Center };
        var valText = new TextBlock { Text = value.ToString(), FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Width = 28 };
        slider.ValueChanged += (_, e) =>
        {
            valText.Text = ((int)e.NewValue).ToString();
            try { onChanged((int)e.NewValue); } catch { }
        };
        row.Children.Add(slider);
        row.Children.Add(valText);
        return row;
    }

    /// <summary>滑块变化：写入草稿 Hair HSV 并重新合成穿身上预览（染发效果可见）。</summary>
    private void ApplyHairDye(int hue, int sat, int bright)
    {
        var hair = _draftAppearance.Hair ?? new ItemInfo();
        hair.Hue = hue;
        hair.Saturation = sat;
        hair.Brightness = bright;
        _draftAppearance.Hair = hair;
        RefreshWearingPreviewAsync();
    }

    /// <summary>后台合成穿身上预览（stand1 首帧，代际防串台）；染发滑块拖动时预览实时更新。</summary>
    private void RefreshWearingPreviewAsync()
    {
        if (_paperdoll == null || _previewBox == null)
        {
            return;
        }
        int seq = ++_previewRenderSeq;
        var snapshot = CloneDraft(_draftAppearance);
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var png = _paperdoll.CompositeFrame(snapshot, "stand1", 0);
                if (png == null || png.Length == 0)
                {
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (seq != _previewRenderSeq)
                        {
                            return;
                        }
                        using var ms = new MemoryStream(png);
                        var bmp = new Bitmap(ms);
                        SetPreviewChild(new Image { Source = bmp, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[MaterialBrowser] WearingPreview: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MaterialBrowser] WearingComposite: {ex.Message}");
            }
        });
    }

    /// <summary>搜索框 × 清除按钮显隐（有输入才显示）。</summary>
    private void UpdateSearchClearVisibility()
    {
        if (_searchClearBtn == null)
        {
            return;
        }
        _searchClearBtn.IsVisible = !string.IsNullOrEmpty(_searchInput?.Text);
    }

    /// <summary>纸娃娃部件预览：把选中部件写入草稿外观，穿在身上合成 stand1 首帧。
    /// 2026-08-16：合成移后台 Task（冰凌披风特效首次合成 4.6s——WZ 惰性加载 + 大特效位图——
    /// 原 UI 线程同步 CompositeFrame 卡死界面；_previewRenderSeq 防旧任务串台）。</summary>
    private void LoadPaperdollPreview(MaterialEntry entry)
    {
        try
        {
            ApplyDraftPart(entry.Category, entry.Id);
            int seq = ++_previewRenderSeq;
            var snapshot = CloneDraft(_draftAppearance);
            var paperdoll = _paperdoll;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (paperdoll == null) { return; }
                    var png = paperdoll.CompositeFrame(snapshot, "stand1", 0);
                    if (png == null || png.Length == 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (seq != _previewRenderSeq) { return; }
                            SetPreviewChild(new TextBlock
                            {
                                Text = "🎨\n无预览",
                                FontSize = 13,
                                Foreground = BrushTxtDim,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center,
                                TextAlignment = TextAlignment.Center,
                                LineHeight = 1.5
                            });
                        });
                        return;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (seq != _previewRenderSeq) { return; }
                            using var ms = new MemoryStream(png);
                            var bmp = new Bitmap(ms);
                            // 自适应：去掉 MaxWidth=220/MaxHeight=260 约束，随预览框填满并等比缩放
                            SetPreviewChild(new Image
                            {
                                Source = bmp,
                                Stretch = Stretch.Uniform,
                                HorizontalAlignment = HorizontalAlignment.Stretch,
                                VerticalAlignment = VerticalAlignment.Stretch
                            });
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] PreviewImg: {ex.Message}"); }
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MaterialBrowser] LoadPaperdollPreview({entry.Id}): {ex.Message}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (seq != _previewRenderSeq) { return; }
                        SetPreviewChild(new TextBlock
                        {
                            Text = "🎨\n预览失败",
                            FontSize = 13,
                            Foreground = BrushTxtDim,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextAlignment = TextAlignment.Center,
                            LineHeight = 1.5
                        });
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MaterialBrowser] LoadPaperdollPreview({entry.Id}): {ex.Message}");
            SetPreviewChild(new TextBlock
            {
                Text = "🎨\n预览失败",
                FontSize = 13,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 1.5
            });
        }
    }

    /// <summary>按素材库分类/id 前缀更新草稿外观对应部位。</summary>
    private void ApplyDraftPart(string category, string id)
    {
        var it = new ItemInfo { Id = id };
        switch (category)
        {
            case "皮肤":
                if (int.TryParse(id, out var bodyId))
                {
                    _draftAppearance.BodyId = bodyId;
                }
                return;
            case "发型":
                // 保留现有染发参数：组内换变体时 HSV 延续（染发调色入口联动草稿 Hair）；
                // 草稿 Hair 可能为 null（当前外观未设发型），空值按默认参数处理，防 NRE 吞草稿更新
                var prevHair = _draftAppearance.Hair;
                _draftAppearance.Hair = new ItemInfo
                {
                    Id = id,
                    Dye = prevHair?.Dye ?? 0,
                    Hue = prevHair?.Hue ?? 0,
                    Saturation = prevHair?.Saturation ?? 0,
                    Brightness = prevHair?.Brightness ?? 0
                };
                return;
            case "脸型": _draftAppearance.Face = it; return;
            case "帽子": _draftAppearance.Cap = it; return;
            case "上衣": _draftAppearance.Coat = it; return;
            case "套服": _draftAppearance.Overall = it; return;
            case "裤子": _draftAppearance.Pants = it; return;
            case "鞋子": _draftAppearance.Shoes = it; return;
            case "手套": _draftAppearance.Glove = it; return;
            case "武器": _draftAppearance.Weapon = it; return;
            case "盾牌": _draftAppearance.Shield = it; return;
            case "披风": _draftAppearance.Cape = it; return;
            case "面饰": _draftAppearance.FaceAccessory = it; return;
            case "眼饰": _draftAppearance.EyeAccessory = it; return;
            case "耳环": _draftAppearance.Earring = it; return;
            case "坐骑": _draftAppearance.Mount = new MountInfo { Id = id }; return;
            case "椅子": _draftAppearance.Chair = new ChairInfo { Id = id }; return;
        }
        // 装备（"全部"分类选中时）：按 id 前缀映射部位
        if (!int.TryParse(id, out var n))
        {
            return;
        }
        int part = n / 10000;
        switch (part)
        {
            case 100: _draftAppearance.Cap = it; break;
            // 饰品三分类（对齐 MapleSalon2）：101 面饰 / 102 眼饰 / 103 耳环
            case 101: _draftAppearance.FaceAccessory = it; break;
            case 102: _draftAppearance.EyeAccessory = it; break;
            case 103: _draftAppearance.Earring = it; break;
            case 104: _draftAppearance.Coat = it; break;
            case 105: _draftAppearance.Overall = it; break;
            case 106: _draftAppearance.Pants = it; break;
            case 107: _draftAppearance.Shoes = it; break;
            case 108: _draftAppearance.Glove = it; break;
            case 109: _draftAppearance.Shield = it; break;
            case 110: _draftAppearance.Cape = it; break;
            default:
                if (part >= 121 && part <= 170)
                {
                    _draftAppearance.Weapon = it;
                }
                break;
        }
    }

    /// <summary>保存当前草稿为搭配存档（SavedPaperdolls）；2026-08-16 弹名字输入框（原自动命名「搭配 N」无输入处）。</summary>
    private void SaveDraftOutfit()
    {
        var cfg = _configService?.LoadSettings();
        if (cfg == null)
        {
            return;
        }
        string defaultName = $"搭配 {cfg.SavedPaperdolls.Count + 1}";
        var win = new Window
        {
            Title = "保存搭配",
            Width = 340,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 38)),
            CanResize = false,
        };
        var stack = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = "输入搭配名称：", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 244)) });
        var input = new TextBox
        {
            Text = defaultName,
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            BorderBrush = InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
        };
        stack.Children.Add(input);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        // 确认（主操作）：accent 蓝底白字（2026-08-16 统一规范，同素材选择器「✅ 选择」）
        var okBtn = new Button
        {
            Content = "确定",
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(16, 6),
            Background = BrushAccent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        // 取消（次级）：透明底 + 边框 + 次级亮字（2026-08-16 统一规范，同素材选择器「取消」）
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
        UiStyle.ApplyHover(okBtn);
        UiStyle.ApplyHover(cancelBtn);
        okBtn.Click += (_, _) =>
        {
            try
            {
                var name = input.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(name))
                {
                    win.Close();
                    return;
                }
                // 2026-08-16：同名直接覆盖（用户要求「保存搭配是覆盖不是新增」）；
                // 原逻辑 ContainsKey 时直接关闭不保存——同名存档无法更新

                // V0.2.0：改走 ConfigService.Update 事务（锁内重读最新配置再改写）——
                // 模态对话框期间提交的音乐等并发持久化不再被对话框前捕获的旧 cfg 整对象覆盖
                var draft = CloneDraft(_draftAppearance);
                _configService!.Update(s => { s.SavedPaperdolls[name] = draft; });

                RefreshOutfitList();
                win.Close();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] SaveOutfit: {ex.Message}"); win.Close(); }
        };
        cancelBtn.Click += (_, _) => win.Close();
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        stack.Children.Add(btnRow);
        win.Content = stack;
        win.ShowDialog(this);
    }

    /// <summary>从搭配存档加载草稿并合成预览。</summary>
    private void LoadDraftOutfit()
    {
        if (_outfitCombo?.SelectedItem is not string name)
        {
            return;
        }
        var cfg = _configService?.LoadSettings();
        if (cfg == null || !cfg.SavedPaperdolls.TryGetValue(name, out var app))
        {
            return;
        }
        _draftAppearance = CloneDraft(app);
        if (_previewBox == null || _paperdoll == null)
        {
            return;
        }
        // 2026-08-16：后台合成（冰凌披风特效首帧 4.6s 不能卡 UI 线程）
        int seq = ++_previewRenderSeq;
        var snapshot = CloneDraft(_draftAppearance);
        var paperdoll = _paperdoll;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var png = paperdoll.CompositeFrame(snapshot, "stand1", 0);
                if (png is { Length: > 0 })
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (seq != _previewRenderSeq) { return; }
                            using var ms = new MemoryStream(png);
                            var bmp = new Bitmap(ms);
                            SetPreviewChild(new Image { Source = bmp, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch });
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] OutfitPreviewImg: {ex.Message}"); }
                    });
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] LoadDraftOutfit: {ex.Message}"); }
        });
    }

    private void RefreshOutfitList()
    {
        if (_outfitCombo == null)
        {
            return;
        }
        var names = _configService?.LoadSettings().SavedPaperdolls.Keys.OrderBy(x => x).ToList() ?? new List<string>();
        _outfitCombo.ItemsSource = names;
    }

    private static CharacterAppearance CloneDraft(CharacterAppearance src)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, Skin = src.Skin, BodyId = src.BodyId, Ear = src.Ear,
            Hair = CloneItem(src.Hair), Face = CloneItem(src.Face),
            Cap = CloneItem(src.Cap), Cape = CloneItem(src.Cape), Coat = CloneItem(src.Coat),
            Overall = CloneItem(src.Overall), Pants = CloneItem(src.Pants), Shoes = CloneItem(src.Shoes),
            Glove = CloneItem(src.Glove), Shield = CloneItem(src.Shield), Weapon = CloneItem(src.Weapon),
            FaceAccessory = CloneItem(src.FaceAccessory), EyeAccessory = CloneItem(src.EyeAccessory),
            Earring = CloneItem(src.Earring),
            Mount = src.Mount != null ? new MountInfo { Id = src.Mount.Id } : null,
            Chair = src.Chair != null ? new ChairInfo { Id = src.Chair.Id } : null,
        };
    }

    private static ItemInfo? CloneItem(ItemInfo? it)
    {
        if (it == null)
        {
            return null;
        }
        return new ItemInfo { Id = it.Id, Dye = it.Dye, Hue = it.Hue, Saturation = it.Saturation, Brightness = it.Brightness };
    }

    // ═══════════════════════════════════════════
    // 分页
    // ═══════════════════════════════════════════
    /// <summary>绝对跳页：clamp 到 [1, _totalPages] 后刷新列表与分页显示（UpdatePager 同时恢复页码框文本）。</summary>
    private void GoToPage(int page)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (page > _totalPages)
        {
            page = _totalPages;
        }
        _currentPage = page;
        RefreshList();
        UpdatePager();
    }

    private void GoPage(int delta) => GoToPage(_currentPage + delta);

    private void UpdatePager()
    {
        if (_countLabel != null)
        {
            _countLabel.Text = $"{_filteredEntries.Count} 条 / {_totalPages} 页";
        }
        // 页码输入框只显示数字；总页数显示在右侧纯文本（如 "/ 5"）
        if (_pageInput != null)
        {
            _pageInput.Text = _currentPage.ToString();
        }
        if (_pageTotalLabel != null)
        {
            _pageTotalLabel.Text = $"/ {_totalPages}";
        }
        if (_prevBtn != null)
        {
            _prevBtn.IsEnabled = _currentPage > 1;
        }
        if (_nextBtn != null)
        {
            _nextBtn.IsEnabled = _currentPage < _totalPages;
        }
    }

    // ═══════════════════════════════════════════
    // WZ 素材加载
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
                if (string.IsNullOrEmpty(name))
                {
                    name = $"怪物_{id}";
                }
                entries.Add(new MaterialEntry("怪物", id, name, "👾"));
            }

            var npcIds = wz.ListNpcIds();
            foreach (var id in npcIds)
            {
                var name = wz.GetNpcName(id);
                if (string.IsNullOrEmpty(name))
                {
                    name = $"NPC_{id}";
                }
                entries.Add(new MaterialEntry("NPC", id, name, "🧑"));
            }

            // 皮肤：body 2000-2999（换肤 Body/Head 成对，head=body+10000；中文名从 String.wz Body 分类读）
            try
            {
                foreach (var (id, _) in wz.GetDirectoryChildren("Character"))
                {
                    if (int.TryParse(id, out var b) && b >= 2000 && b <= 2999)
                    {
                        var cn = wz.GetItemName("Body", id);
                        entries.Add(new MaterialEntry("皮肤", id, string.IsNullOrEmpty(cn) ? $"皮肤 {id}" : cn, "🧑"));
                    }
                }
            }
            catch { }

            // 纸娃娃分类（WZ 真实加载，替换死分类）：发型/脸型/装备各部位/坐骑/椅子
            AddCharacterCategory(wz, entries, "Hair", "发型", "💇");
            AddCharacterCategory(wz, entries, "Face", "脸型", "😊");
            foreach (var (folder, cat, emoji) in new[]
            {
                ("Cap", "帽子", "⛑️"), ("Coat", "上衣", "🛡️"), ("Longcoat", "套服", "🧥"),
                ("Pants", "裤子", "👖"), ("Shoes", "鞋子", "👢"), ("Glove", "手套", "🧤"),
                ("Weapon", "武器", "🗡️"), ("Shield", "盾牌", "🛡️"), ("Cape", "披风", "🧣"),
                ("TamingMob", "坐骑", "🏍️"),
            })
            {
                AddCharacterCategory(wz, entries, folder, cat, emoji);
            }
            // 饰品三分类（对齐 MapleSalon2 拆分）：Accessory 目录按 id 前缀拆「面饰 101 / 眼饰 102 / 耳环 103」，可同时穿戴
            AddAccessoryCategories(wz, entries);
            // 椅子：Item/Install/{分组}.img（03010.img…03019.img / 030150.img…030159.img / 0302.img，
            // 按 id/1000、id/100、id/10000 分组；本 WZ 无 0301.img）。
            // 2026-08-16 修复：img 文件是 Wz_Image 节点，GetDirectoryChildren（目录专用，.img 后缀过滤 +
            // 未 TryExtract 时 Nodes 空）对 img 路径返回空 → 椅子分类空列表（用户反馈「椅子没有内容」）。
            // 改用 GetImgChildren：FindNodeByPath + Wz_Image.TryExtract + 遍历数字 id 子节点；
            // 椅子 id 范围 3010000–3020999（MapleSalon2 isChairId；Install 内 0304.img 等是其他家具，排除）。
            try
            {
                foreach (var (_, imgFile) in wz.GetDirectoryChildren("Item/Install"))
                {
                    foreach (var id in wz.GetImgChildren($"Item/Install/{imgFile}"))
                    {
                        if (!int.TryParse(id, out var chairId) || chairId < 3010000 || chairId >= 3021000)
                        {
                            continue;
                        }
                        var cn = wz.GetItemName("Install", id);
                        entries.Add(new MaterialEntry("椅子", id, string.IsNullOrEmpty(cn) ? $"椅子_{id}" : cn, "🪑"));
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] LoadFromWz(椅子): {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MaterialBrowser] LoadFromWz: {ex.Message}");
        }
        return entries;
    }

    private static void AddCharacterCategory(WzService wz, List<MaterialEntry> entries, string folder, string cat, string emoji)
    {
        try
        {
            foreach (var (id, _) in wz.GetDirectoryChildren($"Character/{folder}"))
            {
                // 中文名从 String.wz 读取（Item.img/{folder}/{id}/name），读不到回退 "分类_id"
                var cn = wz.GetItemName(folder, id);
                entries.Add(new MaterialEntry(cat, id, string.IsNullOrEmpty(cn) ? $"{cat}_{id}" : cn, emoji));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] AddCharacterCategory({folder}): {ex.Message}"); }
    }

    /// <summary>
    /// 饰品分类加载：Character/Accessory 目录按 id 前缀拆三分类（101 面饰 / 102 眼饰 / 103 耳环，可同时穿戴）。
    /// 名字仍走 GetItemName("Accessory", id)（String.wz Item.img/Accessory 分类）；其余前缀（如 112/113 戒指）不展示。
    /// </summary>
    private static void AddAccessoryCategories(WzService wz, List<MaterialEntry> entries)
    {
        try
        {
            foreach (var (id, _) in wz.GetDirectoryChildren("Character/Accessory"))
            {
                if (!int.TryParse(id, out var n))
                {
                    continue;
                }
                int part = n / 10000;
                string cat = part switch
                {
                    101 => "面饰",
                    102 => "眼饰",
                    103 => "耳环",
                    _ => "",
                };
                if (cat.Length == 0)
                {
                    continue;
                }
                var cn = wz.GetItemName("Accessory", id);
                string emoji = part == 103 ? "💍" : part == 102 ? "👁️" : "🎭";
                entries.Add(new MaterialEntry(cat, id, string.IsNullOrEmpty(cn) ? $"{cat}_{id}" : cn, emoji));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MaterialBrowser] AddAccessoryCategories: {ex.Message}"); }
    }


    /// <summary>后台加载条目缩略图：纸娃娃部件优先 icon（快），无 icon 回退默认动画 stand 首帧；窗口可见才更新，防 Skia 与关闭并发。</summary>
    private void LoadThumbAsync(MaterialEntry entry, Control emojiText)
    {
        if (entry == null || _wzService == null || !_wzService.IsLoaded)
        {
            return;
        }
        var id = entry.Id;
        var category = entry.Category;
        // 分类映射：素材浏览条目 Category 是中文（怪物/NPC）——漏映射导致缩略图不加载（胶水反馈 2026-08-09）
        var type = category switch
        {
            "Mob" or "怪物" => "Mob",
            "NPC" or "Npc" => "Npc",
            _ => ""
        };
        var wz = _wzService;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // 纸娃娃部件：优先 icon（WZ 装备节点自带 icon/iconRaw）
                if (IsPaperdollCategory(category))
                {
                    var iconPng = TryExtractIcon(wz, category, id);
                    if (iconPng != null)
                    {
                        var iconBmp = DecodePngBytes(iconPng);
                        if (iconBmp != null)
                        {
                            PostThumb(entry, emojiText, iconBmp);
                        }
                    }
                    return;
                }

                if (string.IsNullOrEmpty(type))
                {
                    return;
                }
                var actions = wz.GetActionList(id, type);
                if (actions.Count == 0)
                {
                    return;
                }
                var action = actions.Contains("stand") ? "stand" : actions[0];
                var (pngData, strip, _) = wz.BuildSpriteStrip(id, action, type);
                if (pngData == null || strip == null || strip.FrameWidth <= 0 || strip.FrameHeight <= 0)
                {
                    return;
                }
                using var src = SKBitmap.Decode(pngData);
                if (src == null)
                {
                    return;
                }
                int fw = Math.Min(strip.FrameWidth, src.Width);
                int fh = Math.Min(strip.FrameHeight, src.Height);
                using var crop = new SKBitmap(fw, fh);
                using (var canvas = new SKCanvas(crop))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawBitmap(src, new SKRect(0, 0, fw, fh), new SKRect(0, 0, fw, fh));
                }
                using var img = SKImage.FromBitmap(crop);
                var png = img.Encode(SKEncodedImageFormat.Png, 90).ToArray();
                using var ms = new MemoryStream(png);
                PostThumb(entry, emojiText, new Bitmap(ms));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MaterialBrowser] LoadThumbAsync({id}): {ex.Message}");
            }
        });
    }

    /// <summary>缩略图回主线程：写入条目缓存 + 替换 emoji 占位（防窗口关闭/不可见时并发更新）。</summary>
    private void PostThumb(MaterialEntry entry, Control emojiText, Bitmap bmp)
    {
        var captured = bmp;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                entry.Thumb = captured;
                var parent = emojiText.Parent as Border;
                if (emojiText.IsLoaded && emojiText.IsVisible && parent != null)
                {
                    // 同条目重复加载时：替换前 Dispose 旧 Image.Source（防缩略图泄漏）
                    if (parent.Child is Image oldImg && oldImg.Source is IDisposable oldSrc && !ReferenceEquals(oldSrc, captured))
                    {
                        oldSrc.Dispose();
                    }
                    parent.Child = new Image { Source = captured, Width = 32, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
            }
            catch
            {
                captured.Dispose();
            }
        });
    }
}
