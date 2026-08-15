using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Data;
using System.IO;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow 纸娃娃 tab（partial 拆分）：BuildPaperdollTab 至 ApplyPaperdoll。原实现从 SettingsWindow.axaml.cs 平移，行为完全一致。
/// </summary>
public partial class SettingsWindow
{
    private ComboBox? _skinCombo;
    /// <summary>皮肤 id → 显示名 缓存：避免 BuildSkinList 每次构建重复 WZ 探测（窗口生命周期有效，WZ 刷新时清空）。</summary>
    private readonly Dictionary<int, string> _skinNameCache = new();
    /// <summary>皮肤下拉是否已挂钩 WZ 刷新重建（防重复订阅）。</summary>
    private bool _skinRefreshHooked;
    /// <summary>预览合成版本号：防旧 Task 合成结果覆盖新预览。</summary>
    private int _previewSeq;
    /// <summary>「✅ 应用到主窗口」按钮（点击后禁用 + 合成中文案，PetWindow 后台构建完成回调恢复）。</summary>
    private Button? _applyMainBtn;
    /// <summary>纸娃娃预览当前动作（动作切换按钮组点选后按该动作合成）。</summary>
    private string _paperdollPreviewAction = "stand1";
    /// <summary>纸娃娃预览固定动作集（显示名 → 动作名，对齐 REVIEW-FINDINGS 九-C）。</summary>
    private static readonly (string Label, string Action)[] PaperdollPreviewActions =
    {
        ("站立", "stand1"), ("行走", "walk1"), ("跳跃", "jump"), ("攻击", "swingOF"), ("警戒", "alert"),
    };
    /// <summary>装备按钮边框闪烁计时器（素材选件应用后的短暂高亮反馈，0.6s 还原）。</summary>
    private DispatcherTimer? _flashTimer;
    private Button? _flashBtn;
    private IBrush? _flashBtnOriginBrush;
    /// <summary>「✅ 已应用」短暂提示计时器（按钮恢复后闪一下再还原文案）。</summary>
    private DispatcherTimer? _revertApplyBtnTimer;

    private sealed class SkinItem
    {
        public int Id;
        public string Label = "";
    }

    private static int EarIndex(string ear)
    {
        for (int i = 0; i < PaperdollService.AllEarTypes.Length; i++)
            if (PaperdollService.AllEarTypes[i] == ear) { return i; }
        return 0;
    }

    private static string EarTypeAt(int i) =>
        i >= 0 && i < PaperdollService.AllEarTypes.Length ? PaperdollService.AllEarTypes[i] : PaperdollService.EarHuman;

    private List<SkinItem> BuildSkinList()
    {
        var list = new List<SkinItem>();
        try
        {
            var bodies = _wzService != null && _wzService.IsLoaded
                ? _wzService.GetDirectoryChildren("Character")
                    .Where(x => int.TryParse(x.Id, out var b) && b >= 2000 && b <= 2999)
                    .Select(x => int.Parse(x.Id)).Distinct().OrderBy(x => x).ToList()
                : Enumerable.Range(2000, 6).ToList();
            foreach (var b in bodies)
            {
                // 名称优先走缓存，未命中再探测（TryGetSkinName 内部逐级回退，避免每次构建重复 WZ 访问）
                if (!_skinNameCache.TryGetValue(b, out var cn))
                {
                    cn = TryGetSkinName(b) ?? "";
                    _skinNameCache[b] = cn;
                }
                list.Add(new SkinItem { Id = b, Label = string.IsNullOrEmpty(cn) ? $"皮肤 {b}" : cn });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsWindow] BuildSkinList: {ex.Message}");
            list = Enumerable.Range(2000, 6).Select(b => new SkinItem { Id = b, Label = $"皮肤 {b}" }).ToList();
        }
        return list;
    }

    /// <summary>皮肤中文名探测：先 Character/Body.img/{id}/info/name（真实皮肤名位置；Body.img 为 Wz_Image 则 TryExtract 后读，与 GetItemName 的 Eqp.img 同模式），再回退 GetItemName（String.wz），最后 null（调用方回退"皮肤 {id}"）。</summary>
    private string? TryGetSkinName(int bodyId)
    {
        try
        {
            if (_wzService == null || !_wzService.IsLoaded) { return null; }
            var idStr = bodyId.ToString();
            var pad = bodyId.ToString("D8");
            // 一级：Character/Body.img（桌宠 WZ 的 String.wz 为空，真实皮肤名只能在此找）
            var bodyImg = _wzService.FindNodeByPath("Character/Body.img");
            if (bodyImg != null)
            {
                var root = bodyImg;
                var img = bodyImg.GetValue<Wz_Image>();
                if (img != null && img.TryExtract()) { root = img.Node; }
                foreach (var key in new[] { idStr, pad })
                {
                    var n = root.FindNodeByPath($"{key}/info/name")?.GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(n)) { return n; }
                }
            }
            // 二级：String/Eqp.img/Eqp/Skin/{key}/name（启动日志实证 String.wz 有数据且 Eqp 子节点含
            // Skin 分类；原 GetItemName 的 9 条路径不含 Skin 分类，皮肤名需专用路径；Wz_Image 需 TryExtract）
            var skinImg = _wzService.FindNodeByPath("String/Eqp.img");
            if (skinImg != null)
            {
                var sRoot = skinImg;
                var sImg = skinImg.GetValue<Wz_Image>();
                if (sImg != null && sImg.TryExtract()) { sRoot = sImg.Node; }
                foreach (var key in new[] { idStr, pad })
                {
                    var n = sRoot.FindNodeByPath($"Eqp/Skin/{key}/name")?.GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(n)) { return n; }
                }
            }
            // 三级：String.wz 通用名称（GetItemName 已含 9 条路径探测）
            var cn = _wzService.GetItemName("Body", idStr);
            if (!string.IsNullOrEmpty(cn)) { return cn; }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TryGetSkinName({bodyId}): {ex.Message}"); }
        return null;
    }

    /// <summary>WZ 素材刷新成功后重建皮肤下拉（名称数据随 WZ 路径可能变化）：清名称缓存 → 重建 ItemsSource → 恢复当前选择。挂在 OnWzRefreshed 上（配置保存自动刷新也走该事件）。</summary>
    private void RebuildSkinCombo()
    {
        if (_skinCombo == null) { return; }
        try
        {
            var prevId = _skinCombo.SelectedItem is SkinItem si ? si.Id : _paperdollCurrent.BodyId;
            _skinNameCache.Clear();
            _skinCombo.ItemsSource = BuildSkinList();
            _skinCombo.SelectedIndex = SkinIndex(prevId);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RebuildSkinCombo: {ex.Message}"); }
    }

    private int SkinIndex(int bodyId)
    {
        if (_skinCombo?.ItemsSource is not IReadOnlyList<SkinItem> list) { return -1; }
        for (int i = 0; i < list.Count; i++) if (list[i].Id == bodyId) return i;
        return -1;
    }

    private StackPanel BuildDyeSlider(string label, int min, int max, int value, Action<int> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40, TextAlignment = TextAlignment.Right });
        // 去固定 Height：Fluent Slider 模板自带垂直 Padding + 20px Thumb，Height=32 会把 Thumb 垂直裁切只剩上半圆；去掉后按模板自然定高（约 40px）
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var valText = new TextBlock { Text = value.ToString(), FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Width = 28 };
        slider.ValueChanged += (_, e) => { valText.Text = ((int)e.NewValue).ToString(); try { onChanged((int)e.NewValue); } catch { } };
        row.Children.Add(slider);
        row.Children.Add(valText);
        return row;
    }

    /// <summary>打开统一素材库（纸娃娃部件选件 → 草稿应用到本页）。</summary>
    private void OpenPaperdollMaterialBrowser()
    {
        try
        {
            var browser = new MaterialBrowserWindow(_wzService, _config, _paperdollSvc);
            browser.OnPaperdollDraftApplied += appearance =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _paperdollCurrent = CloneAppearance(appearance);
                        SyncInputsFromPaperdoll();
                        UpdatePaperdollPreview();
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] DraftApply: {ex.Message}"); }
                });
            };
            browser.Show();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenPaperdollBrowser: {ex.Message}"); }
    }

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
        presetBtnRow.Children.Add(MakeSmallBtn("新建", () => NewPaperdollPreset()));
        presetBtnRow.Children.Add(MakeSmallBtn("保存", () => SavePaperdollPreset()));
        presetBtnRow.Children.Add(MakeSmallBtn("加载", () => LoadPaperdollPreset()));
        content.Children.Add(presetBtnRow);

        // 自适应：预设列表改随内容 Auto + MaxHeight 上限 200（原固定 Height=100 不随预设数伸缩；超限内部滚动）
        _presetListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), MaxHeight = 200, Margin = new Thickness(-10, 0, -10, 0) };
        // 选中态强化（暗色主题默认选中高亮不明显）：选中项文字 accent + 背景微亮（体验 A-3）
        _presetListBox.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent) }
        });
        _presetListBox.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters = { new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromArgb(64, 94, 156, 255))) }
        });
        _presetListBox.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, BrushAccent) }
        });
        RefreshPresetList();
        content.Children.Add(_presetListBox);

        content.Children.Add(new TextBlock { Text = "👤 基础设置", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });

        var genderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        genderRow.Children.Add(new TextBlock { Text = "性别", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _genderCombo = new ComboBox { Height = 26, FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ItemsSource = new[] { "男 (0)", "女 (1)" }, SelectedIndex = _paperdollCurrent.Gender };
        _genderCombo.SelectionChanged += (_, _) => { try { _paperdollCurrent.Gender = _genderCombo.SelectedIndex; UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Gender: {ex.Message}"); } };
        genderRow.Children.Add(_genderCombo);
        content.Children.Add(genderRow);

        // 皮肤：body id 选择（head=body+10000 成对）
        var skinRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        skinRow.Children.Add(new TextBlock { Text = "皮肤", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _skinCombo = new ComboBox { Height = 26, FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        // WZ 刷新成功后重建皮肤下拉（配置保存自动刷新也走 OnWzRefreshed；只挂一次防重复订阅）
        if (!_skinRefreshHooked)
        {
            _skinRefreshHooked = true;
            OnWzRefreshed += RebuildSkinCombo;
        }
        _skinCombo.ItemsSource = BuildSkinList();
        _skinCombo.SelectedIndex = SkinIndex(_paperdollCurrent.BodyId);
        _skinCombo.SelectionChanged += (_, _) => { try { if (_skinCombo.SelectedItem is SkinItem si) { _paperdollCurrent.BodyId = si.Id; UpdatePaperdollPreview(); } } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Skin: {ex.Message}"); } };
        skinRow.Children.Add(_skinCombo);
        content.Children.Add(skinRow);

        var earRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        earRow.Children.Add(new TextBlock { Text = "耳朵", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _earCombo = new ComboBox { Height = 26, FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ItemsSource = new[] { "人类", "精灵", "精灵(Lef)", "高等精灵" }, SelectedIndex = EarIndex(_paperdollCurrent.Ear) };
        _earCombo.SelectionChanged += (_, _) => { try { var i = _earCombo?.SelectedIndex ?? 0; _paperdollCurrent.Ear = EarTypeAt(i); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Ear: {ex.Message}"); } };
        earRow.Children.Add(_earCombo);
        content.Children.Add(earRow);

        content.Children.Add(BuildEquipSelectRow("发 型", "发型", out _hairIdInput));
        content.Children.Add(BuildEquipSelectRow("脸 型", "脸型", out _faceIdInput));

        content.Children.Add(new TextBlock { Text = "🛡️ 装备栏", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        content.Children.Add(BuildEquipSelectRow("帽 子", "帽子", out _capIdInput));
        content.Children.Add(BuildEquipSelectRow("披 风", "披风", out _capeIdInput));
        content.Children.Add(BuildEquipSelectRow("上 衣", "上衣", out _coatIdInput));
        content.Children.Add(BuildEquipSelectRow("长 袍", "长袍", out _overallIdInput));
        content.Children.Add(BuildEquipSelectRow("裤 子", "裤子", out _pantsIdInput));
        content.Children.Add(BuildEquipSelectRow("鞋 子", "鞋子", out _shoesIdInput));
        content.Children.Add(BuildEquipSelectRow("武 器", "武器", out _weaponIdInput));
        content.Children.Add(BuildEquipSelectRow("盾 牌", "盾牌", out _shieldIdInput));
        content.Children.Add(BuildEquipSelectRow("手 套", "手套", out _gloveIdInput));
        content.Children.Add(BuildEquipSelectRow("饰 品", "饰品", out _earringIdInput));

        content.Children.Add(new TextBlock { Text = "🐴 坐骑 / 椅子", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        content.Children.Add(BuildEquipSelectRow("坐 骑", "坐骑", out _mountIdInput));
        content.Children.Add(BuildEquipSelectRow("椅 子", "椅子", out _chairIdInput));

        // 染发（发型 HSV 调整）
        content.Children.Add(new TextBlock { Text = "🎨 染发（发型）", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 6, 0, 0) });
        content.Children.Add(BuildDyeSlider("色相", 0, 360, _paperdollCurrent.Hair.Hue, v => { _paperdollCurrent.Hair.Hue = v; UpdatePaperdollPreview(); }));
        content.Children.Add(BuildDyeSlider("饱和度", 0, 100, _paperdollCurrent.Hair.Saturation, v => { _paperdollCurrent.Hair.Saturation = v; UpdatePaperdollPreview(); }));
        content.Children.Add(BuildDyeSlider("亮度", 0, 100, _paperdollCurrent.Hair.Brightness, v => { _paperdollCurrent.Hair.Brightness = v; UpdatePaperdollPreview(); }));
        var dyeBtn = MakeSmallBtn("✅ 应用染发", () => { try { _paperdollCurrent.Hair.Dye = 1; UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Dye: {ex.Message}"); } });
        content.Children.Add(dyeBtn);

        content.Children.Add(MakeSmallBtn("🧩 打开素材库", OpenPaperdollMaterialBrowser));

        var applyBtn = new Button { Content = "预览", FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(0, 8), Background = new SolidColorBrush(InputBorder), Foreground = BrushTxtSecondary, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(applyBtn);
        applyBtn.Click += (_, _) => { try { SyncPaperdollFromInputs(); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyPaperdoll: {ex.Message}"); } };
        content.Children.Add(applyBtn);

        _applyMainBtn = new Button { Content = "✅ 应用到主窗口", FontSize = 11, FontWeight = FontWeight.Medium, Padding = new Thickness(0, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(_applyMainBtn);
        _applyMainBtn.Click += (_, _) => { try { ApplyPaperdoll(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyMain: {ex.Message}"); } };
        content.Children.Add(_applyMainBtn);

        // 用默认外观填充输入框
        SyncInputsFromPaperdoll();
        return content;
    }

    /// <summary>装备选择行：input 样式（白底黑字，与 AI 配置 input 统一——胶水 2026-08-15：这是 input 不是 button），
    /// 点击打开素材选择器（预选部位分类），选件应用到当前外观；× 清除。</summary>
    private StackPanel BuildEquipSelectRow(string label, string category, out Button selectBtn)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 44, TextAlignment = TextAlignment.Right });
        selectBtn = new Button
        {
            Content = "选择…",
            Width = 140,
            Height = 28,
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        var btnRef = selectBtn;
        UiStyle.ApplyHover(selectBtn);
        selectBtn.Click += (_, _) => { try { OpenEquipPicker(category, btnRef); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] EquipPicker {label}: {ex.Message}"); } };
        row.Children.Add(selectBtn);

        var clearBtn = new Button { Content = "×", Width = 22, Height = 22, FontSize = 10, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = BrushTxtDim, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(clearBtn);
        clearBtn.Click += (_, _) => { try { ClearEquipPart(category, btnRef); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ClearEquip {label}: {ex.Message}"); } };
        row.Children.Add(clearBtn);
        return row;
    }

    /// <summary>打开素材选择器（预选部位分类），选件草稿应用该部位到当前外观。</summary>
    private void OpenEquipPicker(string category, Button btn)
    {
        var browser = new MaterialBrowserWindow(_wzService, _config, _paperdollSvc, category);
        browser.OnPaperdollDraftApplied += appearance =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    ApplyDraftPartToCurrent(category, appearance);
                    btn.Content = CategoryDisplayName(category);
                    FlashEquipButton(btn); // 选件应用后边框闪 accent 蓝 0.6s（体验 A-2）
                    UpdatePaperdollPreview();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] EquipPick {category}: {ex.Message}"); }
            });
        };
        browser.Show();
    }

    private void ApplyDraftPartToCurrent(string category, CharacterAppearance draft)
    {
        switch (category)
        {
            case "发型": _paperdollCurrent.Hair = draft.Hair; break;
            case "脸型": _paperdollCurrent.Face = draft.Face; break;
            case "帽子": _paperdollCurrent.Cap = draft.Cap; break;
            case "披风": _paperdollCurrent.Cape = draft.Cape; break;
            case "上衣": _paperdollCurrent.Coat = draft.Coat; break;
            case "长袍": _paperdollCurrent.Overall = draft.Overall; break;
            case "裤子": _paperdollCurrent.Pants = draft.Pants; break;
            case "鞋子": _paperdollCurrent.Shoes = draft.Shoes; break;
            case "手套": _paperdollCurrent.Glove = draft.Glove; break;
            case "武器": _paperdollCurrent.Weapon = draft.Weapon; break;
            case "盾牌": _paperdollCurrent.Shield = draft.Shield; break;
            case "饰品": _paperdollCurrent.Earring = draft.Earring; break;
            case "坐骑": _paperdollCurrent.Mount = draft.Mount; break;
            case "椅子": _paperdollCurrent.Chair = draft.Chair; break;
        }
    }

    private string CategoryCurrentId(string category) => category switch
    {
        "发型" => _paperdollCurrent.Hair?.Id ?? "", "脸型" => _paperdollCurrent.Face?.Id ?? "",
        "帽子" => _paperdollCurrent.Cap?.Id ?? "", "披风" => _paperdollCurrent.Cape?.Id ?? "",
        "上衣" => _paperdollCurrent.Coat?.Id ?? "", "长袍" => _paperdollCurrent.Overall?.Id ?? "",
        "裤子" => _paperdollCurrent.Pants?.Id ?? "", "鞋子" => _paperdollCurrent.Shoes?.Id ?? "",
        "手套" => _paperdollCurrent.Glove?.Id ?? "", "武器" => _paperdollCurrent.Weapon?.Id ?? "",
        "盾牌" => _paperdollCurrent.Shield?.Id ?? "", "饰品" => _paperdollCurrent.Earring?.Id ?? "",
        "坐骑" => _paperdollCurrent.Mount?.Id ?? "", "椅子" => _paperdollCurrent.Chair?.Id ?? "",
        _ => ""
    };

    private static string CategoryFolder(string category) => category switch
    {
        "发型" => "Hair", "脸型" => "Face", "帽子" => "Cap", "上衣" => "Coat",
        "长袍" => "Longcoat", "裤子" => "Pants", "鞋子" => "Shoes", "手套" => "Glove",
        "武器" => "Weapon", "盾牌" => "Shield", "披风" => "Cape", "饰品" => "Accessory",
        "坐骑" => "TamingMob", "椅子" => "Install",
        _ => ""
    };

    /// <summary>装备按钮显示中文名（读 String.wz），读不到回退 id。</summary>
    private string CategoryDisplayName(string category)
    {
        var id = CategoryCurrentId(category);
        if (string.IsNullOrEmpty(id)) { return "选择…"; }
        var folder = CategoryFolder(category);
        if (string.IsNullOrEmpty(folder)) { return id; }
        var cn = _wzService != null && _wzService.IsLoaded ? _wzService.GetItemName(folder, id) : "";
        return string.IsNullOrEmpty(cn) ? id : $"{cn} [{id}]";
    }

    private void ClearEquipPart(string category, Button btn)
    {
        switch (category)
        {
            case "发型": _paperdollCurrent.Hair = new ItemInfo(); break;
            case "脸型": _paperdollCurrent.Face = new ItemInfo(); break;
            case "帽子": _paperdollCurrent.Cap = null; break;
            case "披风": _paperdollCurrent.Cape = null; break;
            case "上衣": _paperdollCurrent.Coat = null; break;
            case "长袍": _paperdollCurrent.Overall = null; break;
            case "裤子": _paperdollCurrent.Pants = null; break;
            case "鞋子": _paperdollCurrent.Shoes = null; break;
            case "手套": _paperdollCurrent.Glove = null; break;
            case "武器": _paperdollCurrent.Weapon = null; break;
            case "盾牌": _paperdollCurrent.Shield = null; break;
            case "饰品": _paperdollCurrent.Earring = null; break;
            case "坐骑": _paperdollCurrent.Mount = null; break;
            case "椅子": _paperdollCurrent.Chair = null; break;
        }
        btn.Content = "选择…";
        UpdatePaperdollPreview();
    }

    private void SyncPaperdollFromInputs()
    {
        try
        {
            _paperdollCurrent.Gender = _genderCombo?.SelectedIndex ?? 0;
            if (_skinCombo?.SelectedItem is SkinItem si) { _paperdollCurrent.BodyId = si.Id; }
            _paperdollCurrent.Ear = EarTypeAt(_earCombo?.SelectedIndex ?? 0);
            // 装备部位由素材选择器直接改 _paperdollCurrent（按钮点击 → 选件应用），此处无需同步
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncPaperdoll: {ex.Message}"); }
    }

    /// <summary>生成唯一预设名："预设 N"（N 从 1 递增，跳过列表与磁盘已占用名）。</summary>
    private string NextPresetName()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _paperdollPresets) used.Add(p.Name);
        try
        {
            var s = _config.LoadSettings();
            foreach (var key in s.SavedPaperdolls.Keys) used.Add(key);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] NextPresetName: {ex.Message}"); }
        var n = 1;
        while (used.Contains($"预设 {n}")) n++;
        return $"预设 {n}";
    }

    private void NewPaperdollPreset()
    {
        try
        {
            _paperdollCurrent = CreateDefaultAppearance();
            var clone = CloneAppearance(_paperdollCurrent);
            var name = NextPresetName();
            _paperdollPresets.Add((name, clone));
            // 磁盘持久化（重启保留）
            try
            {
                var s = _config.LoadSettings();
                s.SavedPaperdolls[name] = clone;
                _config.SaveSettings(s);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] NewPresetPersist: {ex.Message}"); }
            RefreshPresetList();
            foreach (var p in _paperdollPresets)
            {
                if (p.Name == name)
                {
                    _presetListBox.SelectedItem = p;
                    _presetListBox.ScrollIntoView(p); // 新建后滚动到可见（体验 A-1：预设多时不盲选）
                    break;
                }
            }
            if (_statusBarText != null)
            {
                _statusBarText.Text = "已新建预设";
            }
            SyncInputsFromPaperdoll();
            UpdatePaperdollPreview();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] NewPreset: {ex.Message}"); }
    }

    private void SavePaperdollPreset()
    {
        try
        {
            SyncPaperdollFromInputs();
            var name = NextPresetName();
            var clone = CloneAppearance(_paperdollCurrent);
            _paperdollPresets.Add((name, clone));
            // 磁盘持久化（重启保留）
            try
            {
                var s = _config.LoadSettings();
                s.SavedPaperdolls[name] = clone;
                _config.SaveSettings(s);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SavePresetPersist: {ex.Message}"); }
            RefreshPresetList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SavePreset: {ex.Message}"); }
    }

    private void LoadPaperdollPreset()
    {
        try
        {
            if (_presetListBox?.SelectedItem is not ValueTuple<string, CharacterAppearance> selected)
            {
                if (_statusBarText != null)
                {
                    _statusBarText.Text = "请先选择要加载的预设";
                }
                return;
            }
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
            if (_genderCombo != null) { _genderCombo.SelectedIndex = _paperdollCurrent.Gender; }
            if (_skinCombo != null) { _skinCombo.SelectedIndex = SkinIndex(_paperdollCurrent.BodyId); }
            if (_hairIdInput != null) { _hairIdInput.Content = CategoryDisplayName("发型"); }
            if (_faceIdInput != null) { _faceIdInput.Content = CategoryDisplayName("脸型"); }
            if (_capIdInput != null) { _capIdInput.Content = CategoryDisplayName("帽子"); }
            if (_capeIdInput != null) { _capeIdInput.Content = CategoryDisplayName("披风"); }
            if (_coatIdInput != null) { _coatIdInput.Content = CategoryDisplayName("上衣"); }
            if (_overallIdInput != null) { _overallIdInput.Content = CategoryDisplayName("长袍"); }
            if (_pantsIdInput != null) { _pantsIdInput.Content = CategoryDisplayName("裤子"); }
            if (_shoesIdInput != null) { _shoesIdInput.Content = CategoryDisplayName("鞋子"); }
            if (_gloveIdInput != null) { _gloveIdInput.Content = CategoryDisplayName("手套"); }
            if (_weaponIdInput != null) { _weaponIdInput.Content = CategoryDisplayName("武器"); }
            if (_shieldIdInput != null) { _shieldIdInput.Content = CategoryDisplayName("盾牌"); }
            if (_earringIdInput != null) { _earringIdInput.Content = CategoryDisplayName("饰品"); }
            if (_mountIdInput != null) { _mountIdInput.Content = CategoryDisplayName("坐骑"); }
            if (_chairIdInput != null) { _chairIdInput.Content = CategoryDisplayName("椅子"); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncInputs: {ex.Message}"); }
    }

    private void RefreshPresetList()
    {
        try
        {
            // 首次：从磁盘加载预设存档
            if (_paperdollPresets.Count == 0)
            {
                try
                {
                    var s = _config.LoadSettings();
                    foreach (var kv in s.SavedPaperdolls) _paperdollPresets.Add((kv.Key, kv.Value));
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPresetsPersist: {ex.Message}"); }
            }
            if (_presetListBox == null) { return; }
            // 刷新前记录选中项名称（ItemsSource 重建后 SelectedItem 是新元组实例，按 Name 恢复）
            var prevName = _presetListBox.SelectedItem is ValueTuple<string, CharacterAppearance> prevSelected ? prevSelected.Item1 : null;
            _presetListBox.ItemsSource = null;
            _presetListBox.ItemsSource = _paperdollPresets;
            // 预设项：文字颜色交给样式（选中态 accent 由 :selected 样式覆写，见 BuildPaperdollSidebar）；
            // 右键菜单：删除 / 重命名 / 复制（REVIEW-FINDINGS I1 缺失项）
            _presetListBox.ItemTemplate = new FuncDataTemplate<(string Name, CharacterAppearance Data)>((item, _) =>
            {
                var tb = new TextBlock { [!TextBlock.TextProperty] = new Binding("Name"), FontSize = 11, Padding = new Thickness(4, 3) };
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "🗑️ 删除" };
                del.Click += (_, _) => { try { DeletePaperdollPreset(item); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PresetDelete: {ex.Message}"); } };
                menu.Items.Add(del);
                var rename = new MenuItem { Header = "✏️ 重命名" };
                rename.Click += (_, _) => { try { RenamePaperdollPreset(item); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PresetRename: {ex.Message}"); } };
                menu.Items.Add(rename);
                var copy = new MenuItem { Header = "📋 复制" };
                copy.Click += (_, _) => { try { DuplicatePaperdollPreset(item); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PresetCopy: {ex.Message}"); } };
                menu.Items.Add(copy);
                tb.ContextMenu = menu;
                return tb;
            });
            if (prevName != null)
            {
                foreach (var p in _paperdollPresets)
                {
                    if (p.Name == prevName)
                    {
                        _presetListBox.SelectedItem = p;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshPresetList: {ex.Message}"); }
    }

    private void UpdatePaperdollPreview()
    {
        try
        {
            if (_paperdollPreviewCanvas == null) { return; }
            int seq = ++_previewSeq;
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12 };

            // 动作切换按钮组（体验 C-1：stand1/walk1/jump/swingOF/alert，当前动作高亮 accent）
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var (label, action) in PaperdollPreviewActions)
            {
                var captured = action;
                var btn = new Button
                {
                    Content = label,
                    FontSize = 9,
                    Padding = new Thickness(8, 3),
                    Background = action == _paperdollPreviewAction ? BrushAccent : new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)),
                    Foreground = action == _paperdollPreviewAction ? Brushes.White : BrushTxtSecondary,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                UiStyle.ApplyHover(btn);
                btn.Click += (_, _) => { try { _paperdollPreviewAction = captured; UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] PreviewAction: {ex.Message}"); } };
                actionRow.Children.Add(btn);
            }
            panel.Children.Add(actionRow);

            // 合成中占位（体验 C-2）：Task 启动前设，完成替换为图；WZ 未加载时保持 🧑 占位
            var figure = new Border { Width = 160, Height = 200, CornerRadius = new CornerRadius(12), Background = GetCheckerboardBrush(), HorizontalAlignment = HorizontalAlignment.Center, Child = new TextBlock { Text = _wzService.IsLoaded ? "⏳ 合成中…" : "🧑", FontSize = _wzService.IsLoaded ? 13 : 60, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            panel.Children.Add(figure);

            panel.Children.Add(new TextBlock { Text = _paperdollCurrent.Gender == 0 ? "♂ 男性" : "♀ 女性", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center });

            var equipSummary = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
            var parts = new (string Label, string? Id)[] {
                ("发型", _paperdollCurrent.Hair?.Id), ("脸型", _paperdollCurrent.Face?.Id),
                ("帽子", _paperdollCurrent.Cap?.Id), ("披风", _paperdollCurrent.Cape?.Id),
                ("上衣", _paperdollCurrent.Coat?.Id), ("长袍", _paperdollCurrent.Overall?.Id),
                ("裤子", _paperdollCurrent.Pants?.Id), ("鞋子", _paperdollCurrent.Shoes?.Id),
                ("武器", _paperdollCurrent.Weapon?.Id), ("盾牌", _paperdollCurrent.Shield?.Id),
                ("手套", _paperdollCurrent.Glove?.Id), ("饰品", _paperdollCurrent.Earring?.Id),
                ("坐骑", _paperdollCurrent.Mount?.Id), ("椅子", _paperdollCurrent.Chair?.Id),
            };
            foreach (var (label, id) in parts)
            {
                var has = !string.IsNullOrEmpty(id);
                var color = has ? BrushTxtSecondary : BrushTxtDim;
                var val = has ? id : "—";
                equipSummary.Children.Add(new TextBlock { Text = $"{label}: {val}", FontSize = 9, Foreground = color, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center });
            }
            panel.Children.Add(equipSummary);
            _paperdollPreviewCanvas.Child = panel;

            // 后台合成当前预览动作第 0 帧真实预览（_previewSeq 防旧 Task 串台；动作无帧时回退 stand1）
            var snapshot = CloneAppearance(_paperdollCurrent);
            var previewAction = _paperdollPreviewAction;
            var figureRef = figure;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!_wzService.IsLoaded) { return; }
                    var png = _paperdollSvc.CompositeFrame(snapshot, previewAction, 0);
                    if (png == null) { png = _paperdollSvc.CompositeFrame(snapshot, "stand1", 0); }
                    if (png == null) { return; }
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (seq != _previewSeq) { return; }
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

    /// <summary>应用到主窗口：点击后禁用按钮 + 合成中文案 → 触发 OnPaperdollApplied（PetWindow 后台构建完成后 SetPaperdollApplyBusy(false) 恢复，体验 B）。</summary>
    private void ApplyPaperdoll()
    {
        try
        {
            SyncPaperdollFromInputs();
            UpdatePaperdollPreview();
            SetPaperdollApplyBusy(true);
            OnPaperdollApplied?.Invoke(CloneAppearance(_paperdollCurrent));
            if (_statusBarText != null) { _statusBarText.Text = "⏳ 正在合成，请稍候…"; }
        }
        catch (Exception ex)
        {
            SetPaperdollApplyBusy(false);
            Console.Error.WriteLine($"[SettingsWindow] ApplyPaperdoll: {ex.Message}");
        }
    }

    /// <summary>「应用到主窗口」按钮忙碌状态切换：busy=禁用 + 合成中；完成=恢复 + 「✅ 已应用」闪 1.2s 再还原文案（PetWindow 后台构建完成回调调用）。</summary>
    public void SetPaperdollApplyBusy(bool busy)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_applyMainBtn == null) { return; }
                _applyMainBtn.IsEnabled = !busy;
                _applyMainBtn.Opacity = busy ? 0.7 : 1.0;
                _applyMainBtn.Content = busy ? "⏳ 合成中…" : "✅ 已应用";
                if (busy)
                {
                    _revertApplyBtnTimer?.Stop();
                }
                else
                {
                    if (_statusBarText != null) { _statusBarText.Text = "✅ 已应用到主窗口"; }
                    _revertApplyBtnTimer?.Stop();
                    _revertApplyBtnTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                    _revertApplyBtnTimer.Tick += (_, _) =>
                    {
                        try
                        {
                            _revertApplyBtnTimer?.Stop();
                            if (_applyMainBtn != null) { _applyMainBtn.Content = "✅ 应用到主窗口"; }
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RevertApplyBtn: {ex.Message}"); }
                    };
                    _revertApplyBtnTimer.Start();
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SetPaperdollApplyBusy: {ex.Message}"); }
        });
    }

    /// <summary>装备按钮短暂高亮反馈：边框闪 accent 蓝 0.6s 后还原（提示该部位已更新，体验 A-2）。</summary>
    private void FlashEquipButton(Button btn)
    {
        if (btn == null) { return; }
        _flashBtn = btn;
        _flashBtnOriginBrush = btn.BorderBrush;
        btn.BorderBrush = BrushAccent;
        if (_flashTimer == null)
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _flashTimer.Tick += (_, _) =>
            {
                try
                {
                    _flashTimer?.Stop();
                    if (_flashBtn != null)
                    {
                        _flashBtn.BorderBrush = _flashBtnOriginBrush ?? BrushBorder;
                    }
                    _flashBtn = null;
                    _flashBtnOriginBrush = null;
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] FlashEquipBtn: {ex.Message}"); }
            };
        }
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    // ═══════════════════════════════════════════
    // 预设管理：删除 / 重命名 / 复制（REVIEW-FINDINGS I1 缺失项，右键菜单入口，列表 + 磁盘 SavedPaperdolls 同步）
    // ═══════════════════════════════════════════

    private void DeletePaperdollPreset((string Name, CharacterAppearance Data) preset)
    {
        try
        {
            var name = preset.Name;
            _paperdollPresets.RemoveAll(p => p.Name == name);
            try
            {
                var s = _config.LoadSettings();
                s.SavedPaperdolls.Remove(name);
                _config.SaveSettings(s);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] DeletePresetPersist: {ex.Message}"); }
            RefreshPresetList();
            if (_statusBarText != null) { _statusBarText.Text = $"已删除预设: {name}"; }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] DeletePreset: {ex.Message}"); }
    }

    private void RenamePaperdollPreset((string Name, CharacterAppearance Data) preset)
    {
        var oldName = preset.Name;
        var win = new Window
        {
            Title = "重命名预设",
            Width = 340,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 38)),
            CanResize = false,
        };
        try
        {
            var stack = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
            stack.Children.Add(new TextBlock { Text = "输入新的预设名称：", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 244)) });
            var input = new TextBox
            {
                Text = oldName,
                FontSize = 12,
                Foreground = Brushes.Black,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
            };
            stack.Children.Add(input);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "确定", Padding = new Thickness(20, 6) };
            var cancelBtn = new Button { Content = "取消", Padding = new Thickness(20, 6) };
            UiStyle.ApplyHover(okBtn);
            UiStyle.ApplyHover(cancelBtn);
            okBtn.Click += (_, _) =>
            {
                try
                {
                    var newName = input.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(newName) || newName == oldName)
                    {
                        win.Close();
                        return;
                    }
                    if (_paperdollPresets.Any(p => p.Name == newName))
                    {
                        if (_statusBarText != null) { _statusBarText.Text = $"预设名已存在: {newName}"; }
                        win.Close();
                        return;
                    }
                    var idx = _paperdollPresets.FindIndex(p => p.Name == oldName);
                    if (idx >= 0)
                    {
                        _paperdollPresets[idx] = (newName, preset.Data);
                    }
                    try
                    {
                        var s = _config.LoadSettings();
                        s.SavedPaperdolls.Remove(oldName);
                        s.SavedPaperdolls[newName] = preset.Data;
                        _config.SaveSettings(s);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RenamePresetPersist: {ex.Message}"); }
                    RefreshPresetList();
                    // 重命名后重新选中新名并滚动到可见（避免刷新后选中丢失）
                    foreach (var p in _paperdollPresets)
                    {
                        if (p.Name == newName)
                        {
                            _presetListBox.SelectedItem = p;
                            _presetListBox.ScrollIntoView(p);
                            break;
                        }
                    }
                    if (_statusBarText != null) { _statusBarText.Text = $"已重命名: {oldName} → {newName}"; }
                    win.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SettingsWindow] RenamePreset: {ex.Message}");
                    win.Close();
                }
            };
            cancelBtn.Click += (_, _) => win.Close();
            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            stack.Children.Add(btnRow);
            win.Content = stack;
            win.ShowDialog(this);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RenamePresetWin: {ex.Message}"); }
    }

    private void DuplicatePaperdollPreset((string Name, CharacterAppearance Data) preset)
    {
        try
        {
            var clone = CloneAppearance(preset.Data);
            var name = NextPresetName();
            _paperdollPresets.Add((name, clone));
            try
            {
                var s = _config.LoadSettings();
                s.SavedPaperdolls[name] = clone;
                _config.SaveSettings(s);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] CopyPresetPersist: {ex.Message}"); }
            RefreshPresetList();
            // 选中新副本并滚动到可见
            foreach (var p in _paperdollPresets)
            {
                if (p.Name == name)
                {
                    _presetListBox.SelectedItem = p;
                    _presetListBox.ScrollIntoView(p);
                    break;
                }
            }
            if (_statusBarText != null) { _statusBarText.Text = $"已复制为: {name}"; }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] CopyPreset: {ex.Message}"); }
    }

    /// <summary>默认外观：男体 + 默认发型/脸型（参考 MapleSalon2 生产默认 body 2000 / head 12000）。</summary>
    private static CharacterAppearance CreateDefaultAppearance() => new()
    {
        Gender = 0,
        BodyId = 2000,
        Hair = new ItemInfo { Id = "30000" },
        Face = new ItemInfo { Id = "20000" },
    };

    private static CharacterAppearance CloneAppearance(CharacterAppearance src)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, Skin = src.Skin, BodyId = src.BodyId, Ear = src.Ear,
            Hair = CloneItemFull(src.Hair), Face = CloneItemFull(src.Face),
            Cap = src.Cap != null ? CloneItemFull(src.Cap) : null,
            Cape = src.Cape != null ? CloneItemFull(src.Cape) : null,
            Coat = src.Coat != null ? CloneItemFull(src.Coat) : null,
            Overall = src.Overall != null ? CloneItemFull(src.Overall) : null,
            Pants = src.Pants != null ? CloneItemFull(src.Pants) : null,
            Shoes = src.Shoes != null ? CloneItemFull(src.Shoes) : null,
            Weapon = src.Weapon != null ? CloneItemFull(src.Weapon) : null,
            Shield = src.Shield != null ? CloneItemFull(src.Shield) : null,
            Glove = src.Glove != null ? CloneItemFull(src.Glove) : null,
            Earring = src.Earring != null ? CloneItemFull(src.Earring) : null,
            Mount = src.Mount != null ? new MountInfo { Id = src.Mount.Id } : null,
            Chair = src.Chair != null ? new ChairInfo { Id = src.Chair.Id } : null,
        };
    }

    private static ItemInfo CloneItemFull(ItemInfo it)
    {
        return new ItemInfo { Id = it.Id, Dye = it.Dye, Hue = it.Hue, Saturation = it.Saturation, Brightness = it.Brightness };
    }

    // ═══════════════════════════════════════════
    // 🌄 背景设置 Tab
    // ═══════════════════════════════════════════

}
