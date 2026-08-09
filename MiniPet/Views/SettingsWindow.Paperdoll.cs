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
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow 纸娃娃 tab（partial 拆分）：BuildPaperdollTab 至 ApplyPaperdoll。原实现从 SettingsWindow.axaml.cs 平移，行为完全一致。
/// </summary>
public partial class SettingsWindow
{
    private ComboBox? _skinCombo;

    private sealed class SkinItem
    {
        public int Id;
        public string Label = "";
    }

    private static int EarIndex(string ear)
    {
        for (int i = 0; i < PaperdollService.AllEarTypes.Length; i++)
            if (PaperdollService.AllEarTypes[i] == ear) return i;
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
                var cn = _wzService != null && _wzService.IsLoaded ? _wzService.GetItemName("Body", b.ToString()) : "";
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

    private int SkinIndex(int bodyId)
    {
        if (_skinCombo?.ItemsSource is not IReadOnlyList<SkinItem> list) return 0;
        for (int i = 0; i < list.Count; i++) if (list[i].Id == bodyId) return i;
        return 0;
    }

    private StackPanel BuildDyeSlider(string label, int min, int max, int value, Action<int> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40, TextAlignment = TextAlignment.Right });
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 120, Height = 24, VerticalAlignment = VerticalAlignment.Center };
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

        // 皮肤：body id 选择（head=body+10000 成对）
        var skinRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        skinRow.Children.Add(new TextBlock { Text = "皮肤", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _skinCombo = new ComboBox { Width = 100, Height = 26, FontSize = 11, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        _skinCombo.ItemsSource = BuildSkinList();
        _skinCombo.SelectedIndex = SkinIndex(_paperdollCurrent.BodyId);
        _skinCombo.SelectionChanged += (_, _) => { try { if (_skinCombo.SelectedItem is SkinItem si) { _paperdollCurrent.BodyId = si.Id; UpdatePaperdollPreview(); } } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Skin: {ex.Message}"); } };
        skinRow.Children.Add(_skinCombo);
        content.Children.Add(skinRow);

        var earRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        earRow.Children.Add(new TextBlock { Text = "耳朵", FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _earCombo = new ComboBox { Width = 100, Height = 26, FontSize = 11, Foreground = BrushTxtPrimary, Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ItemsSource = new[] { "人类", "精灵", "精灵(Lef)", "高等精灵" }, SelectedIndex = EarIndex(_paperdollCurrent.Ear) };
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
        content.Children.Add(BuildDyeSlider("色相", 0, 360, _paperdollCurrent.Hair.Hue, v => { _paperdollCurrent.Hair.Hue = v; }));
        content.Children.Add(BuildDyeSlider("饱和度", 0, 100, _paperdollCurrent.Hair.Saturation, v => { _paperdollCurrent.Hair.Saturation = v; }));
        content.Children.Add(BuildDyeSlider("亮度", 0, 100, _paperdollCurrent.Hair.Brightness, v => { _paperdollCurrent.Hair.Brightness = v; }));
        var dyeBtn = MakeSmallBtn("✅ 应用染发", () => { try { _paperdollCurrent.Hair.Dye = 1; UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Dye: {ex.Message}"); } });
        content.Children.Add(dyeBtn);

        content.Children.Add(MakeSmallBtn("🧩 打开素材库", OpenPaperdollMaterialBrowser));

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

    /// <summary>装备选择行：按钮点击 → 打开素材选择器（预选部位分类），选件应用到当前外观；× 清除。</summary>
    private StackPanel BuildEquipSelectRow(string label, string category, out Button selectBtn)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 44, TextAlignment = TextAlignment.Right });
        selectBtn = new Button
        {
            Content = "选择…",
            Width = 120,
            Height = 26,
            FontSize = 11,
            Foreground = BrushTxtPrimary,
            Background = BrushInputBg,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        var btnRef = selectBtn;
        selectBtn.Click += (_, _) => { try { OpenEquipPicker(category, btnRef); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] EquipPicker {label}: {ex.Message}"); } };
        row.Children.Add(selectBtn);

        var clearBtn = new Button { Content = "×", Width = 22, Height = 22, FontSize = 10, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = BrushTxtDim, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
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
        if (string.IsNullOrEmpty(id)) return "选择…";
        var folder = CategoryFolder(category);
        if (string.IsNullOrEmpty(folder)) return id;
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

    private static string EquipBtnText(string? id) => string.IsNullOrEmpty(id) ? "选择…" : id;

    private void SyncPaperdollFromInputs()
    {
        try
        {
            _paperdollCurrent.Gender = _genderCombo?.SelectedIndex ?? 0;
            if (_skinCombo?.SelectedItem is SkinItem si) _paperdollCurrent.BodyId = si.Id;
            _paperdollCurrent.Ear = EarTypeAt(_earCombo?.SelectedIndex ?? 0);
            // 装备部位由素材选择器直接改 _paperdollCurrent（按钮点击 → 选件应用），此处无需同步
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncPaperdoll: {ex.Message}"); }
    }

    private void NewPaperdollPreset()
    {
        try { _paperdollCurrent = CreateDefaultAppearance(); SyncInputsFromPaperdoll(); UpdatePaperdollPreview(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] NewPreset: {ex.Message}"); }
    }

    private void SavePaperdollPreset()
    {
        try
        {
            SyncPaperdollFromInputs();
            var name = $"预设 {_paperdollPresets.Count + 1}";
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
            if (_skinCombo != null) _skinCombo.SelectedIndex = SkinIndex(_paperdollCurrent.BodyId);
            if (_hairIdInput != null) _hairIdInput.Content = CategoryDisplayName("发型");
            if (_faceIdInput != null) _faceIdInput.Content = CategoryDisplayName("脸型");
            if (_capIdInput != null) _capIdInput.Content = CategoryDisplayName("帽子");
            if (_capeIdInput != null) _capeIdInput.Content = CategoryDisplayName("披风");
            if (_coatIdInput != null) _coatIdInput.Content = CategoryDisplayName("上衣");
            if (_overallIdInput != null) _overallIdInput.Content = CategoryDisplayName("长袍");
            if (_pantsIdInput != null) _pantsIdInput.Content = CategoryDisplayName("裤子");
            if (_shoesIdInput != null) _shoesIdInput.Content = CategoryDisplayName("鞋子");
            if (_gloveIdInput != null) _gloveIdInput.Content = CategoryDisplayName("手套");
            if (_weaponIdInput != null) _weaponIdInput.Content = CategoryDisplayName("武器");
            if (_shieldIdInput != null) _shieldIdInput.Content = CategoryDisplayName("盾牌");
            if (_earringIdInput != null) _earringIdInput.Content = CategoryDisplayName("饰品");
            if (_mountIdInput != null) _mountIdInput.Content = CategoryDisplayName("坐骑");
            if (_chairIdInput != null) _chairIdInput.Content = CategoryDisplayName("椅子");
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
                    var png = _paperdollSvc.CompositeFrame(snapshot, "stand1", 0);
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
            Gender = src.Gender, Skin = src.Skin, BodyId = src.BodyId, Ear = src.Ear,
            Hair = CloneItemFull(src.Hair), Face = CloneItemFull(src.Face),
            Cap = src.Cap != null ? new ItemInfo { Id = src.Cap.Id } : null,
            Cape = src.Cape != null ? new ItemInfo { Id = src.Cape.Id } : null,
            Coat = src.Coat != null ? new ItemInfo { Id = src.Coat.Id } : null,
            Overall = src.Overall != null ? new ItemInfo { Id = src.Overall.Id } : null,
            Pants = src.Pants != null ? new ItemInfo { Id = src.Pants.Id } : null,
            Shoes = src.Shoes != null ? new ItemInfo { Id = src.Shoes.Id } : null,
            Weapon = src.Weapon != null ? new ItemInfo { Id = src.Weapon.Id } : null,
            Shield = src.Shield != null ? new ItemInfo { Id = src.Shield.Id } : null,
            Glove = src.Glove != null ? new ItemInfo { Id = src.Glove.Id } : null,
            Earring = src.Earring != null ? new ItemInfo { Id = src.Earring.Id } : null,
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
