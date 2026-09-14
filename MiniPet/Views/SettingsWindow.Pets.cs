using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow「🐾 桌宠」主 tab（V0.1.3 多桌宠管理页，partial 拆分）：宠物列表 / 添加 / 移除 /
/// 设为主宠 / 单只缩放。
/// <para>
/// 只读使用公共契约（不改这些文件的签名）：<see cref="PetInstanceConfig"/>、
/// <see cref="ConfigService.LoadSettings"/> / <see cref="ConfigService.UpdatePet"/>、
/// <see cref="PetManager.Pets"/> / <see cref="PetManager.Find"/> / <see cref="PetManager.AddPetLike"/> /
/// <see cref="PetManager.RemovePet"/> / <see cref="PetManager.SetPrimary"/> / <see cref="PetManager.PetsChanged"/>。
/// </para>
/// <para>
/// 数据源：以配置 <c>LoadSettings().Pets</c> 为准（列表内容 / 顺序 / 主宠标记 / 缩放 / 位置），
/// <see cref="PetManager"/> 的窗口列表只用于判断该只是否正在显示。
/// </para>
/// </summary>
public partial class SettingsWindow
{
    /// <summary>「🐾 桌宠」主 tab 在 tab 栏与 _mainPanes 里的索引（与 BuildMainTabBar 的 tabs 数组一一对应）。</summary>
    private const int PetsMainTabIndex = 1;

    // ── 「桌宠」页 UI 引用 ──
    private ListBox? _petsListBox;
    /// <summary>页内操作结果 / 当前目标提示。</summary>
    private TextBlock? _petsStatusText;
    /// <summary>「当前编辑目标」提示（右侧）。</summary>
    private TextBlock? _petsTargetText;
    /// <summary>缩放当前值（如 ×1.05）。</summary>
    private TextBlock? _petsScaleValueLabel;
    private Slider? _petsScaleSlider;
    private Button? _petsRemoveBtn;
    private Button? _petsPrimaryBtn;
    /// <summary>纸娃娃预设下拉（V0.1.3：在桌宠页直接给所选桌宠套用存档搭配）。</summary>
    private ComboBox? _petsPresetCombo;
    private Button? _petsPresetApplyBtn;

    /// <summary>列表 / 滑块程序性刷新中：抑制 SelectionChanged 回写与 ValueChanged 回写配置（防回环）。</summary>
    private bool _petsRefreshing;

    /// <summary>已同步过纸娃娃草稿的目标 id：只有「换了目标」才重载草稿，
    /// 避免无关的配置写入（其他 tab 保存 / 拖缩放滑块）把用户编辑中的纸娃娃草稿冲掉。</summary>
    private string _petsPaperdollDraftPetId = string.Empty;

    /// <summary>「桌宠」页列表行数据（配置为准 + 运行时是否在显示）。</summary>
    private sealed class PetListRow
    {
        public string PetId { get; set; } = string.Empty;
        /// <summary>列表序号（1 起）。</summary>
        public int Index { get; set; }
        /// <summary>实体描述：Mob/Npc = WZ id；纸娃娃 = 「纸娃娃」。</summary>
        public string EntityText { get; set; } = string.Empty;
        /// <summary>主宠标记（⭐ 主宠 / 空）。</summary>
        public string PrimaryBadge { get; set; } = string.Empty;
        public string ScaleText { get; set; } = string.Empty;
        public string PosText { get; set; } = string.Empty;
        /// <summary>是否在显示（来自 PetManager 窗口列表）。</summary>
        public string RunningText { get; set; } = string.Empty;
    }

    /// <summary>
    /// 构建「🐾 桌宠」主 tab 内容：左栏 = 宠物列表 + 添加 / 移除 / 设为主宠；
    /// 右栏 = 选中宠物的缩放滑块 + 说明文案。
    /// </summary>
    private Border BuildPetsPane()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(320)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // 左栏：列表 Star 行（随窗口伸缩 + ListBox 内部滚动），标题 / 按钮 / 提示 Auto
            var sidebar = new Grid { Margin = new Thickness(10), RowSpacing = 8, VerticalAlignment = VerticalAlignment.Stretch };
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 标题
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 宠物列表
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 操作按钮
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 结果提示

            var title = new TextBlock { Text = "🐾 桌宠列表", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetRow(title, 0);
            sidebar.Children.Add(title);

            _petsListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
            _petsListBox.ItemTemplate = new FuncDataTemplate<PetListRow>((row, _) => row == null ? null : BuildPetRowControl(row));
            // 选中项 = 当前编辑目标（「素材」「纸娃娃」tab 的「应用」作用于它）
            _petsListBox.SelectionChanged += (_, _) => OnPetsSelectionChanged();
            Grid.SetRow(_petsListBox, 1);
            sidebar.Children.Add(_petsListBox);

            var btnColumn = new StackPanel { Spacing = 6 };
            var addBtn = MakePrimaryBtn("➕ 添加桌宠", OnPetsAdd);
            addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnColumn.Children.Add(addBtn);
            _petsRemoveBtn = MakeSmallBtn("🗑 移除所选", OnPetsRemove);
            _petsRemoveBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnColumn.Children.Add(_petsRemoveBtn);
            _petsPrimaryBtn = MakeSmallBtn("⭐ 设为主宠", OnPetsSetPrimary);
            _petsPrimaryBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnColumn.Children.Add(_petsPrimaryBtn);
            Grid.SetRow(btnColumn, 2);
            sidebar.Children.Add(btnColumn);

            _petsStatusText = new TextBlock { Text = "选中一只桌宠 = 当前编辑目标", FontSize = 10, Foreground = BrushTxtDim, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_petsStatusText, 3);
            sidebar.Children.Add(_petsStatusText);

            Grid.SetColumn(sidebar, 0);
            grid.Children.Add(sidebar);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            // 右栏（V0.1.3 胶水反馈重排）：当前目标 → 纸娃娃预设 → 说明（自适应） → 缩放（移到底部）
            var right = new Grid { Margin = new Thickness(14), RowSpacing = 10 };
            right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 当前编辑目标
            right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 纸娃娃预设：下拉 + 应用
            right.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 说明文案
            right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 缩放标题 + 当前值
            right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 缩放滑块

            _petsTargetText = new TextBlock { Text = "当前编辑目标：—", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_petsTargetText, 0);
            right.Children.Add(_petsTargetText);

            // ── 纸娃娃预设：直接在这里给「所选桌宠」套用某个存档搭配（不用再跳到纸娃娃页）──
            var presetBox = new StackPanel { Spacing = 6 };
            presetBox.Children.Add(new TextBlock
            {
                Text = "🎭 套用预设（纸娃娃 / 怪物，作用于所选桌宠）",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = BrushTxtDim
            });
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _petsPresetCombo = new ComboBox { Width = 260, PlaceholderText = "选择预设…" };
            presetRow.Children.Add(_petsPresetCombo);
            _petsPresetApplyBtn = MakeSmallBtn("✅ 应用到所选桌宠", OnPetsApplyPreset);
            _petsPresetApplyBtn.VerticalAlignment = VerticalAlignment.Center;
            presetRow.Children.Add(_petsPresetApplyBtn);
            presetBox.Children.Add(presetRow);
            presetBox.Children.Add(new TextBlock
            {
                Text = "预设 = 「纸娃娃桌宠设置」页保存过的搭配（🧍），或收藏 / 最近用过的怪物（👾 / 🧑）；应用后这只桌宠换成对应实体，名字一并记下。",
                FontSize = 10,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(presetBox, 1);
            right.Children.Add(presetBox);

            var hint = new TextBlock
            {
                FontSize = 11,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                VerticalAlignment = VerticalAlignment.Top,
                Text = "ℹ️ 主宠 = 挂地图背景 + 响应 Agent 状态特效的那只（同一时刻有且仅有一只，其他桌宠只显示自己）。\n" +
                       "• 列表选中项 = 当前编辑目标：「素材桌宠设置」「纸娃娃桌宠设置」的「应用」都作用于它。\n" +
                       "• 「➕ 添加桌宠」复制当前目标的实体与外观新建一只，位置自动错开。\n" +
                       "• 也可以直接右键桌面上的任意桌宠 → 「🐾 桌宠」菜单添加 / 移除 / 设为主宠。\n" +
                       "• 仅剩一只时不允许移除（移除后不可恢复，请谨慎）。"
            };
            Grid.SetRow(hint, 2);
            right.Children.Add(hint);

            var scaleHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            scaleHead.Children.Add(new TextBlock { Text = "🔍 缩放（仅作用于所选桌宠）", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = BrushTxtPrimary, VerticalAlignment = VerticalAlignment.Center });
            _petsScaleValueLabel = new TextBlock { Text = "×1.00", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = BrushAccent, VerticalAlignment = VerticalAlignment.Center };
            scaleHead.Children.Add(_petsScaleValueLabel);
            Grid.SetRow(scaleHead, 3);
            right.Children.Add(scaleHead);

            _petsScaleSlider = new Slider
            {
                Minimum = 0.5,
                Maximum = 2.0,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = true,
                Value = 1.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            _petsScaleSlider.ValueChanged += (_, e) => OnPetsScaleChanged(e.NewValue);
            Grid.SetRow(_petsScaleSlider, 4);
            right.Children.Add(_petsScaleSlider);

            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            var pane = new Border { Child = grid };
            RefreshPetList();
            return pane;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsWindow] BuildPetsPane: {ex.Message}");
            return new Border();
        }
    }

    /// <summary>列表行控件：序号 + 实体描述（+ ⭐ 主宠）/ 缩放 + 位置 + 是否在显示。</summary>
    private static Control BuildPetRowControl(PetListRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock { Text = row.EntityText, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = BrushTxtPrimary, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(row.PrimaryBadge))
        {
            line.Children.Add(new TextBlock { Text = row.PrimaryBadge, FontSize = 10, Foreground = new SolidColorBrush(Colors.Gold), VerticalAlignment = VerticalAlignment.Center });
        }
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sub.Children.Add(new TextBlock { Text = row.ScaleText, FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
        sub.Children.Add(new TextBlock { Text = row.PosText, FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(row.RunningText))
        {
            sub.Children.Add(new TextBlock { Text = row.RunningText, FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
        }

        var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3) };
        wrap.Children.Add(new TextBlock { Text = row.Index.ToString(), FontSize = 10, Width = 18, TextAlignment = TextAlignment.Right, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
        wrap.Children.Add(new StackPanel { Spacing = 2, Children = { line, sub } });
        return wrap;
    }

    /// <summary>
    /// 刷新列表：配置为准（顺序 / 主宠 / 缩放 / 位置），<see cref="PetManager"/> 只判断是否在显示；
    /// 刷新后保持「当前目标」选中（目标失效则退回主宠 / 第一只）。
    /// </summary>
    private void RefreshPetList()
    {
        try
        {
            if (_petsListBox == null)
            {
                return;
            }
            var pets = _config.LoadSettings().Pets ?? new List<PetInstanceConfig>();
            if (pets.Count == 0)
            {
                // 边界：配置为空（EnsurePets 正常不会让它空）→ 清列表 + 禁按钮，不崩
                _petsRefreshing = true;
                _petsListBox.ItemsSource = new List<PetListRow>();
                _petsRefreshing = false;
                SetPetsTargetUi(null, 0);
                if (_petsStatusText != null)
                {
                    _petsStatusText.Text = "⚠️ 没有桌宠配置（settings.json 无 pets 且无法迁移）";
                }
                return;
            }

            var rows = new List<PetListRow>();
            for (int i = 0; i < pets.Count; i++)
            {
                var p = pets[i];
                var win = _petManager.Find(p.Id);
                rows.Add(new PetListRow
                {
                    PetId = p.Id,
                    Index = i + 1,
                    EntityText = EntityDescribe(p),
                    PrimaryBadge = p.Primary ? "⭐ 主宠" : string.Empty,
                    ScaleText = $"缩放 ×{(p.WindowScale > 0 ? p.WindowScale : 1.0):0.00}",
                    PosText = (p.WindowX == int.MinValue || p.WindowY == int.MinValue) ? "位置 未设置" : $"位置 ({p.WindowX}, {p.WindowY})",
                    RunningText = win == null ? "· 未显示" : (win.IsVisible ? "· 显示中" : "· 已隐藏")
                });
            }

            // 目标 id 失效（被移除 / 手改配置）→ 退回主宠 / 第一只（只改本地目标，不写配置）
            if (!pets.Any(p => p.Id == _targetPetId))
            {
                _targetPetId = (pets.FirstOrDefault(p => p.Primary) ?? pets.FirstOrDefault())?.Id ?? string.Empty;
            }

            RefreshPetsPresetCombo();

            _petsRefreshing = true;
            _petsListBox.ItemsSource = rows;
            var selected = rows.FirstOrDefault(r => r.PetId == _targetPetId) ?? rows[0];
            _petsListBox.SelectedItem = selected;
            _petsRefreshing = false;

            _targetPetId = selected.PetId;
            var target = pets.FirstOrDefault(p => p.Id == selected.PetId);
            SetPetsTargetUi(target, pets.Count);
            // 目标被换掉（选中项被移除 → 退回主宠 / 第一只）时同样重载纸娃娃草稿；
            // 用 id 比对做闸门：无关的配置写入不重载，用户编辑中的草稿不会被冲掉
            if (_targetPetId != _petsPaperdollDraftPetId)
            {
                _petsPaperdollDraftPetId = _targetPetId;
                SyncPaperdollDraftToTarget(target);
            }
        }
        catch (Exception ex)
        {
            _petsRefreshing = false;
            Console.Error.WriteLine($"[SettingsWindow] RefreshPetList: {ex.Message}");
        }
    }

    /// <summary>实体描述：纸娃娃（EntityType=player 或 MobId 为哨兵）显示「纸娃娃」，Mob/Npc 显示 WZ id。</summary>
    /// <summary>
    /// 「套用预设」下拉项：纸娃娃搭配存档 / 收藏的怪物 / 最近用过的怪物。
    /// （V0.1.3 胶水反馈：不只纸娃娃，怪物预设也要能在这里直接选。）
    /// </summary>
    private sealed class PetPresetItem
    {
        /// <summary>paperdoll / Mob / Npc。</summary>
        public string Kind { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>怪物 / NPC id（纸娃娃为空）。</summary>
        public string Id { get; set; } = string.Empty;

        public override string ToString()
        {
            if (Kind == "paperdoll")
            {
                return $"🧍 {Name}";
            }
            return string.Equals(Kind, "Npc", StringComparison.OrdinalIgnoreCase)
                ? $"🧑 {Name} [{Id}]"
                : $"👾 {Name} [{Id}]";
        }
    }

    /// <summary>把配置里的类型字符串归一成 Mob / Npc（其他返回 null，表示不是可套用的怪物条目）。</summary>
    private static string? NormalizePetKind(string? type)
    {
        if (string.IsNullOrEmpty(type)) { return null; }
        if (type.Equals("Npc", StringComparison.OrdinalIgnoreCase)) { return "Npc"; }
        if (type.Equals("Mob", StringComparison.OrdinalIgnoreCase)) { return "Mob"; }
        return null;
    }

    /// <summary>
    /// 列表里的实体描述：优先「怪物名 [id]」（WZ 名字，便于一眼分辨多只桌宠），
    /// 名字取不到（离线/无 WZ/NPC 无名）时回退纯 id；纸娃娃固定显示「纸娃娃」。
    /// </summary>
    private string EntityDescribe(PetInstanceConfig p)
    {
        var isPaperdoll = string.Equals(p.EntityType ?? string.Empty, "player", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(p.MobId ?? string.Empty, PaperdollService.SentinelMobId, StringComparison.Ordinal);
        if (isPaperdoll)
        {
            return ResolvePaperdollName(p);
        }
        var id = (p.MobId ?? string.Empty).Trim();
        var displayName = (p.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id))
        {
            return string.IsNullOrEmpty(displayName) ? "(未设置实体)" : displayName;
        }
        // 显式显示名优先（纸娃娃=预设名；Mob/Npc=应用时记下的 WZ 名），便于区分多只同类桌宠
        if (!string.IsNullOrEmpty(displayName))
        {
            return $"{displayName} [{id}]";
        }
        try
        {
            var isNpc = string.Equals(p.EntityType ?? string.Empty, "Npc", StringComparison.OrdinalIgnoreCase);
            var name = isNpc ? _wzService.GetNpcName(id) : _wzService.GetMobName(id);
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, id, StringComparison.Ordinal))
            {
                return $"{name} [{id}]";
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] EntityDescribe({id}): {ex.Message}"); }
        return id;
    }

    /// <summary>把「当前目标」同步到缩放滑块 / 值标签 / 目标提示 / 按钮可用态（滑块赋值经 _petsRefreshing 抑制回写）。</summary>
    private void SetPetsTargetUi(PetInstanceConfig? target, int count)
    {
        try
        {
            var scale = (target != null && target.WindowScale > 0) ? target.WindowScale : 1.0;
            if (_petsScaleValueLabel != null)
            {
                _petsScaleValueLabel.Text = $"×{scale:0.00}";
            }
            if (_petsScaleSlider != null)
            {
                // 配置值可能超出滑块范围（旧配置）→ 夹到范围内显示，不反写配置
                _petsRefreshing = true;
                _petsScaleSlider.Value = Math.Clamp(scale, _petsScaleSlider.Minimum, _petsScaleSlider.Maximum);
                _petsRefreshing = false;
            }
            if (_petsTargetText != null)
            {
                _petsTargetText.Text = target == null
                    ? "⚠️ 当前没有可编辑的桌宠"
                    : $"当前编辑目标：{target.Id}（{EntityDescribe(target)}）";
            }
            if (_petsRemoveBtn != null)
            {
                _petsRemoveBtn.IsEnabled = count > 1 && target != null;
                _petsRemoveBtn.Content = count > 1
                    ? "🗑 移除所选"
                    : (count == 0 ? "🗑 移除所选（无桌宠）" : "🗑 移除所选（仅剩一只，不可移除）");
            }
            if (_petsPrimaryBtn != null)
            {
                var isPrimary = target != null && target.Primary;
                _petsPrimaryBtn.Content = isPrimary ? "✓ 主宠（当前）" : "⭐ 设为主宠";
                _petsPrimaryBtn.IsEnabled = !isPrimary && target != null;
            }
        }
        catch (Exception ex)
        {
            _petsRefreshing = false;
            Console.Error.WriteLine($"[SettingsWindow] SetPetsTargetUi: {ex.Message}");
        }
    }

    /// <summary>列表选中 = 切换当前编辑目标（「素材」「纸娃娃」tab 的「应用」作用于它）。</summary>
    private void OnPetsSelectionChanged()
    {
        try
        {
            if (_petsRefreshing)
            {
                return;
            }
            if (_petsListBox?.SelectedItem is not PetListRow row)
            {
                return;
            }
            _targetPetId = row.PetId;
            var pets = _config.LoadSettings().Pets ?? new List<PetInstanceConfig>();
            var target = pets.FirstOrDefault(p => p.Id == row.PetId);
            SetPetsTargetUi(target, pets.Count);
            // 切目标同时重载纸娃娃编辑草稿：否则设置窗开着切目标后，纸娃娃页预览/输入框/侧栏
            // 还显示上一只的搭配（「应用」虽写对目标，界面误导）
            _petsPaperdollDraftPetId = _targetPetId;
            SyncPaperdollDraftToTarget(target);
            SetPetsStatus($"已切换编辑目标：{row.PetId}（{row.EntityText}）");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPetsSelectionChanged: {ex.Message}"); }
    }

    /// <summary>
    /// 切换编辑目标后把纸娃娃页草稿同步成该目标的外观（<c>_paperdollCurrent</c> + 输入框 + 预览）。
    /// 目标没存过纸娃娃外观时保留当前草稿（与构造函数初始化语义一致：不拿默认外观顶替）。
    /// </summary>
    private void SyncPaperdollDraftToTarget(PetInstanceConfig? target)
    {
        try
        {
            if (target == null)
            {
                return;
            }
            if (target.Paperdoll != null)
            {
                _paperdollCurrent = CloneAppearance(target.Paperdoll);
            }
            SyncInputsFromPaperdoll();
            UpdatePaperdollPreview();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncPaperdollDraftToTarget: {ex.Message}"); }
    }

    /// <summary>➕ 添加桌宠：复制当前目标的实体与外观新建一只（位置自动错开）。</summary>
    private void OnPetsAdd()
    {
        try
        {
            var win = _petManager.AddPetLike(_targetPetId);
            if (win == null)
            {
                SetPetsStatus("❌ 添加失败（没有可复制的桌宠配置）");
                return;
            }
            RefreshPetList();
            SetPetsStatus($"已添加 {win.PetId}（复制自 {_targetPetId}，点列表可切换编辑目标）");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPetsAdd: {ex.Message}"); }
    }

    /// <summary>🗑 移除所选：仅剩一只时拒绝（与 PetManager / 右键菜单规则一致）。</summary>
    private void OnPetsRemove()
    {
        try
        {
            var id = SelectedPetId();
            if (string.IsNullOrEmpty(id))
            {
                SetPetsStatus("请先在列表里选择一只桌宠");
                return;
            }
            var pets = _config.LoadSettings().Pets ?? new List<PetInstanceConfig>();
            if (pets.Count <= 1)
            {
                SetPetsStatus("仅剩一只，不可移除");
                RefreshPetList();
                return;
            }
            if (_petManager.RemovePet(id))
            {
                if (_targetPetId == id)
                {
                    // 目标被移除 → 退回主宠 / 第一只（PetManager 已保证至少剩一只）
                    var rest = _config.LoadSettings().Pets ?? new List<PetInstanceConfig>();
                    _targetPetId = (rest.FirstOrDefault(p => p.Primary) ?? rest.FirstOrDefault())?.Id ?? string.Empty;
                }
                RefreshPetList();
                SetPetsStatus($"已移除 {id}，剩余 {_petManager.Count} 只");
            }
            else
            {
                SetPetsStatus($"❌ 移除 {id} 失败（仅剩一只或该桌宠不存在）");
                RefreshPetList();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPetsRemove: {ex.Message}"); }
    }

    /// <summary>⭐ 设为主宠（主宠 = 挂地图背景 + 响应 Agent 状态特效的那只）。</summary>
    private void OnPetsSetPrimary()
    {
        try
        {
            var id = SelectedPetId();
            if (string.IsNullOrEmpty(id))
            {
                SetPetsStatus("请先在列表里选择一只桌宠");
                return;
            }
            if (_petManager.SetPrimary(id))
            {
                RefreshPetList();
                SetPetsStatus($"已设为主宠：{id}");
            }
            else
            {
                SetPetsStatus($"❌ 设为主宠失败：{id}（该桌宠未在显示）");
                RefreshPetList();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPetsSetPrimary: {ex.Message}"); }
    }

    /// <summary>
    /// 缩放滑块（0.5 ~ 2.0，步进 0.05）→ 原子写目标宠物的 WindowScale；
    /// PetWindow 已订阅配置变更，写入后该只桌宠实时缩放。
    /// </summary>
    private void OnPetsScaleChanged(double value)
    {
        try
        {
            if (_petsRefreshing)
            {
                return;   // 程序性赋值（刷新同步）不回写配置
            }
            var id = _targetPetId;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            var scale = Math.Round(Math.Clamp(value, 0.5, 2.0), 2);
            _config.UpdatePet(id, p => p.WindowScale = scale);
            if (_petsScaleValueLabel != null)
            {
                _petsScaleValueLabel.Text = $"×{scale:0.00}";
            }
            SetPetsStatus($"已设置 {id} 缩放 ×{scale:0.00}（实时生效）");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPetsScaleChanged: {ex.Message}"); }
    }

    /// <summary>PetManager 宠物集合变化（增 / 删 / 主宠切换）→ 刷新列表（构造里只注册一次，不在这里 +=）。</summary>
    private void OnPetsChanged()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                RefreshPetList();
            }
            else
            {
                Dispatcher.UIThread.Post(() => RefreshPetList());
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPetsChanged: {ex.Message}"); }
    }

    /// <summary>
    /// 纸娃娃桌宠的显示名（V0.1.3 胶水反馈：列表里全是「纸娃娃」分不清哪只）：
    /// ① 配置里的 Name（套用预设 / 复制时写入）→ 直接用；
    /// ② Name 为空时按外观反查已保存的预设（SavedPaperdolls，JSON 全等比对）→ 显示预设名；
    /// ③ 都匹配不上 → 「纸娃娃」。
    /// </summary>
    private string ResolvePaperdollName(PetInstanceConfig p)
    {
        var name = (p.Name ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }
        try
        {
            if (p.Paperdoll == null)
            {
                return "纸娃娃";
            }
            var presets = _config.LoadSettings().SavedPaperdolls;
            if (presets == null || presets.Count == 0)
            {
                return "纸娃娃";
            }
            var targetJson = JsonSerializer.Serialize(p.Paperdoll);
            foreach (var kv in presets)
            {
                if (kv.Value == null) { continue; }
                if (string.Equals(JsonSerializer.Serialize(kv.Value), targetJson, StringComparison.Ordinal))
                {
                    return kv.Key;   // 外观与某预设完全一致 → 视为该预设
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ResolvePaperdollName({p.Id}): {ex.Message}"); }
        return "纸娃娃";
    }

    /// <summary>
    /// 刷新「纸娃娃预设」下拉：列出配置里保存过的预设名（SavedPaperdolls），尽量保持当前选中项。
    /// </summary>
    private void RefreshPetsPresetCombo()
    {
        try
        {
            if (_petsPresetCombo == null)
            {
                return;
            }
            var settings = _config.LoadSettings();
            var items = new List<PetPresetItem>();

            // ① 纸娃娃搭配存档（SavedPaperdolls）
            if (settings.SavedPaperdolls != null)
            {
                foreach (var key in settings.SavedPaperdolls.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    items.Add(new PetPresetItem { Kind = "paperdoll", Name = key });
                }
            }

            // ② 收藏的怪物 / NPC（收藏列表里 Type=Mob/Npc 的条目）
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (settings.Favorites != null)
            {
                foreach (var fav in settings.Favorites)
                {
                    var kind = NormalizePetKind(fav.Type);
                    if (kind == null || string.IsNullOrEmpty(fav.Id)) { continue; }
                    if (!seen.Add(fav.Id)) { continue; }
                    items.Add(new PetPresetItem
                    {
                        Kind = kind,
                        Id = fav.Id,
                        Name = string.IsNullOrEmpty(fav.Name) ? fav.Id : fav.Name
                    });
                }
            }

            // ③ 最近用过的怪物 / NPC（含本次会话切过的）
            if (settings.RecentItems != null)
            {
                foreach (var recent in settings.RecentItems.OrderByDescending(r => r.LastUsedAt))
                {
                    var kind = NormalizePetKind(recent.Type);
                    if (kind == null || string.IsNullOrEmpty(recent.Id)) { continue; }
                    if (!seen.Add(recent.Id)) { continue; }
                    items.Add(new PetPresetItem
                    {
                        Kind = kind,
                        Id = recent.Id,
                        Name = string.IsNullOrEmpty(recent.Name) ? recent.Id : recent.Name
                    });
                }
            }

            var previous = _petsPresetCombo.SelectedItem as PetPresetItem;
            _petsPresetCombo.ItemsSource = items;
            // 保持原选中（按 Kind + 名称/id 匹配）；匹配不到则默认选第一项
            var keep = previous == null
                ? null
                : items.FirstOrDefault(i => i.Kind == previous.Kind && i.Name == previous.Name && i.Id == previous.Id);
            _petsPresetCombo.SelectedItem = keep ?? items.FirstOrDefault();
            if (_petsPresetApplyBtn != null)
            {
                _petsPresetApplyBtn.IsEnabled = items.Count > 0;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] RefreshPetsPresetCombo: {ex.Message}"); }
    }

    /// <summary>
    /// 把选中的纸娃娃预设套用到「当前编辑目标」：写目标宠物的实例字段（外观 + 哨兵 mobId + 显示名=预设名），
    /// 再经定向事件让那只宠物窗口立即换装（与「纸娃娃桌宠设置」页的应用同一条链路）。
    /// </summary>
    private void OnPetsApplyPreset()
    {
        try
        {
            var target = TargetPet();
            if (target == null)
            {
                SetPetsStatus("没有可应用的桌宠");
                return;
            }
            var item = _petsPresetCombo?.SelectedItem as PetPresetItem;
            if (item == null)
            {
                SetPetsStatus("请先选择一个预设（纸娃娃搭配存档，或收藏 / 最近用过的怪物）");
                return;
            }

            if (item.Kind == "paperdoll")
            {
                var settings = _config.LoadSettings();
                if (settings.SavedPaperdolls == null
                    || !settings.SavedPaperdolls.TryGetValue(item.Name, out var saved)
                    || saved == null)
                {
                    SetPetsStatus($"纸娃娃预设「{item.Name}」不存在或已删除");
                    return;
                }
                var clone = CloneAppearance(saved);
                if (clone == null)
                {
                    SetPetsStatus("预设外观读取失败");
                    return;
                }
                _config.UpdatePet(target.Id, p =>
                {
                    p.Paperdoll = clone;
                    p.MobId = PaperdollService.SentinelMobId;
                    p.EntityType = "player";
                    p.Name = item.Name;
                });
                // 定向换装：只有目标宠物窗口响应（PetWindow 按 petId 过滤）
                _eventBus.Publish(EventType.PaperdollApplied, new PetPaperdollEvent(target.Id, CloneAppearance(clone)));
                SetPetsStatus($"已把纸娃娃预设「{item.Name}」应用到 {target.Id}");
            }
            else
            {
                // 怪物 / NPC：写实体 + 定向切换（与设置中心「确定应用」同一条链路）
                _config.UpdatePet(target.Id, p =>
                {
                    p.MobId = item.Id;
                    p.EntityType = item.Kind;
                    p.Name = item.Name;
                });
                _eventBus.Publish(EventType.EntityApplied,
                    new PetEntityAppliedEvent(target.Id, item.Id, item.Name, item.Kind));
                SetPetsStatus($"已把 {item.Name} [{item.Id}] 应用到 {target.Id}");
            }
            RefreshPetList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsWindow] OnPetsApplyPreset: {ex.Message}");
            SetPetsStatus($"应用预设失败：{ex.Message}");
        }
    }

    /// <summary>当前列表选中的宠物 id（无选中返回空串）。</summary>
    private string SelectedPetId() => (_petsListBox?.SelectedItem as PetListRow)?.PetId ?? string.Empty;

    /// <summary>操作结果：同时写页内提示与窗口底部状态栏（与其他 tab 的反馈方式一致）。</summary>
    private void SetPetsStatus(string message)
    {
        try
        {
            if (_petsStatusText != null)
            {
                _petsStatusText.Text = message;
            }
            if (_statusBarText != null)
            {
                _statusBarText.Text = message;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SetPetsStatus: {ex.Message}"); }
    }
}
