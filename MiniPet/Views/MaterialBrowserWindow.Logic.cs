using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
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
        _itemList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MaterialEntry>((data, _) =>
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
            // 缩略图 = 默认动画 stand 首帧（后台懒加载；无图时 emoji 兜底）
            var entry = data;
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
        if (_selectBtn != null)
        {
            _selectBtn.Content = IsPaperdollCategory(entry.Category) && _paperdoll != null ? "✅ 应用到主窗口" : "✅ 选择";
        }
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
        if (_wzService == null || !_wzService.IsLoaded || _previewBox == null) return;

        try
        {
            // 纸娃娃部件分类：穿在身上合成预览（草稿外观 + 该部件）
            if (IsPaperdollCategory(entry.Category) && _paperdoll != null)
            {
                LoadPaperdollPreview(entry);
                return;
            }

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

                    _previewBox.Child = image;
                    return;
                }
            }

            // 没找到任何动作，显示占位
            _previewBox.Child = new TextBlock
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
            _previewBox.Child = new TextBlock
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

    /// <summary>纸娃娃部件预览：把选中部件写入草稿外观，穿在身上合成 stand1 首帧。</summary>
    private void LoadPaperdollPreview(MaterialEntry entry)
    {
        try
        {
            ApplyDraftPart(entry.Category, entry.Id);
            var png = _paperdoll!.CompositeFrame(_draftAppearance, "stand1", 0);
            if (png == null || png.Length == 0)
            {
                _previewBox!.Child = new TextBlock
                {
                    Text = "🎨\n无预览",
                    FontSize = 13,
                    Foreground = BrushTxtDim,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    LineHeight = 1.5
                };
                return;
            }
            using var ms = new MemoryStream(png);
            var bmp = new Bitmap(ms);
            _previewBox!.Child = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 220,
                MaxHeight = 260
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MaterialBrowser] LoadPaperdollPreview({entry.Id}): {ex.Message}");
            _previewBox!.Child = new TextBlock
            {
                Text = "🎨\n预览失败",
                FontSize = 13,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 1.5
            };
        }
    }

    /// <summary>按素材库分类/id 前缀更新草稿外观对应部位。</summary>
    private void ApplyDraftPart(string category, string id)
    {
        var it = new ItemInfo { Id = id };
        switch (category)
        {
            case "皮肤":
                if (int.TryParse(id, out var bodyId)) _draftAppearance.BodyId = bodyId;
                return;
            case "发型": _draftAppearance.Hair = it; return;
            case "脸型": _draftAppearance.Face = it; return;
            case "帽子": _draftAppearance.Cap = it; return;
            case "上衣": _draftAppearance.Coat = it; return;
            case "长袍": _draftAppearance.Overall = it; return;
            case "裤子": _draftAppearance.Pants = it; return;
            case "鞋子": _draftAppearance.Shoes = it; return;
            case "手套": _draftAppearance.Glove = it; return;
            case "武器": _draftAppearance.Weapon = it; return;
            case "盾牌": _draftAppearance.Shield = it; return;
            case "披风": _draftAppearance.Cape = it; return;
            case "饰品": _draftAppearance.Earring = it; return;
            case "坐骑": _draftAppearance.Mount = new MountInfo { Id = id }; return;
            case "椅子": _draftAppearance.Chair = new ChairInfo { Id = id }; return;
        }
        // 装备（"全部"分类选中时）：按 id 前缀映射部位
        if (!int.TryParse(id, out var n)) return;
        int part = n / 10000;
        switch (part)
        {
            case 100: _draftAppearance.Cap = it; break;
            case 101:
            case 102:
            case 103: _draftAppearance.Earring = it; break;
            case 104: _draftAppearance.Coat = it; break;
            case 105: _draftAppearance.Overall = it; break;
            case 106: _draftAppearance.Pants = it; break;
            case 107: _draftAppearance.Shoes = it; break;
            case 108: _draftAppearance.Glove = it; break;
            case 109: _draftAppearance.Shield = it; break;
            case 110: _draftAppearance.Cape = it; break;
            default:
                if (part >= 121 && part <= 170) _draftAppearance.Weapon = it;
                break;
        }
    }

    /// <summary>保存当前草稿为搭配存档（SavedPaperdolls）。</summary>
    private void SaveDraftOutfit()
    {
        var cfg = _configService?.LoadSettings();
        if (cfg == null) return;
        string name = $"搭配 {cfg.SavedPaperdolls.Count + 1}";
        cfg.SavedPaperdolls[name] = CloneDraft(_draftAppearance);
        _configService!.SaveSettings(cfg);
        RefreshOutfitList();
    }

    /// <summary>从搭配存档加载草稿并合成预览。</summary>
    private void LoadDraftOutfit()
    {
        if (_outfitCombo?.SelectedItem is not string name) return;
        var cfg = _configService?.LoadSettings();
        if (cfg == null || !cfg.SavedPaperdolls.TryGetValue(name, out var app)) return;
        _draftAppearance = CloneDraft(app);
        if (_previewBox == null || _paperdoll == null) return;
        var png = _paperdoll.CompositeFrame(_draftAppearance, "stand1", 0);
        if (png is { Length: > 0 })
        {
            using var ms = new MemoryStream(png);
            var bmp = new Bitmap(ms);
            _previewBox.Child = new Image { Source = bmp, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, MaxHeight = 260 };
        }
    }

    private void RefreshOutfitList()
    {
        if (_outfitCombo == null) return;
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
            Earring = CloneItem(src.Earring),
            Mount = src.Mount != null ? new MountInfo { Id = src.Mount.Id } : null,
            Chair = src.Chair != null ? new ChairInfo { Id = src.Chair.Id } : null,
        };
    }

    private static ItemInfo? CloneItem(ItemInfo? it)
    {
        if (it == null) return null;
        return new ItemInfo { Id = it.Id, Dye = it.Dye, Hue = it.Hue, Saturation = it.Saturation, Brightness = it.Brightness };
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
            _pageLabel.Text = $"{_currentPage}/{_totalPages}";
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
                ("Cap", "帽子", "⛑️"), ("Coat", "上衣", "🛡️"), ("Longcoat", "长袍", "🧥"),
                ("Pants", "裤子", "👖"), ("Shoes", "鞋子", "👢"), ("Glove", "手套", "🧤"),
                ("Weapon", "武器", "🗡️"), ("Shield", "盾牌", "🛡️"), ("Cape", "披风", "🧣"),
                ("Accessory", "饰品", "💍"), ("TamingMob", "坐骑", "🏍️"),
            })
            {
                AddCharacterCategory(wz, entries, folder, cat, emoji);
            }
            // 椅子：Item/Install/0301.img（探测，失败跳过）
            try
            {
                foreach (var (id, _) in wz.GetDirectoryChildren("Item/Install/0301.img"))
                {
                    var cn = wz.GetItemName("Install", id);
                    entries.Add(new MaterialEntry("椅子", id, string.IsNullOrEmpty(cn) ? $"椅子_{id}" : cn, "🪑"));
                }
            }
            catch { }
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

    /// <summary>后台加载条目缩略图（默认动画 stand 首帧；窗口可见才更新，防 Skia 与关闭并发）。</summary>
    private void LoadThumbAsync(MaterialEntry entry, Control emojiText)
    {
        if (entry == null || _wzService == null || !_wzService.IsLoaded) return;
        var id = entry.Id;
        // 分类映射：素材浏览条目 Category 是中文（怪物/NPC）——漏映射导致缩略图不加载（胶水反馈 2026-08-09）
        var type = entry.Category switch
        {
            "Mob" or "怪物" => "Mob",
            "NPC" or "Npc" => "Npc",
            _ => ""
        };
        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                if (string.IsNullOrEmpty(type)) return;
                var actions = _wzService.GetActionList(id, type);
                if (actions.Count == 0) return;
                var action = actions.Contains("stand") ? "stand" : actions[0];
                var (pngData, strip, _) = _wzService.BuildSpriteStrip(id, action, type);
                if (pngData == null || strip == null || strip.FrameWidth <= 0 || strip.FrameHeight <= 0) return;
                using var src = SKBitmap.Decode(pngData);
                if (src == null) return;
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
                bmp = new Bitmap(ms);
            }
            catch { bmp = null; }
            if (bmp == null) return;
            var captured = bmp;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    entry.Thumb = captured;
                    var parent = emojiText.Parent as Border;
                    if (emojiText.IsLoaded && emojiText.IsVisible && parent != null)
                    {
                        parent.Child = new Image { Source = captured, Width = 32, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    }
                }
                catch { captured.Dispose(); }
            });
        });
    }
}
