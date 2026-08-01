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

}
