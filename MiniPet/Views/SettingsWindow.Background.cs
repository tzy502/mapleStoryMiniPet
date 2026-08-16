using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using System.IO;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
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
/// SettingsWindow 背景 tab（partial 拆分）：OpenBackgroundTab 至 GetMapIcon。原实现从 SettingsWindow.axaml.cs 平移，行为完全一致。
/// </summary>
public partial class SettingsWindow
{
    // 自适应：预览图显示缩放（画布坐标 = 图像像素 × 缩放 + 居中偏移；crop 几何随缩放换算）
    private double _bgImageScale = 1;
    private double _bgImageOffsetX;
    private double _bgImageOffsetY;

    private Border BuildBackgroundTab()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(300)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // 左：区域树（唯一选择器，叶子=具体地图，选中直接预览）+ 选择历史(F2) + 镜头大小（摄像机专属，独立区域）
            var left = new Grid();
            // 自适应：区域树行 Auto → Star，左栏总高随窗口伸缩（树内滚动）
            left.RowDefinitions.Add(new RowDefinition(GridLength.Star));    // 区域树（Star：随窗口伸缩，树内滚动）
            left.RowDefinitions.Add(new RowDefinition(new GridLength(150))); // 选择历史
            left.RowDefinitions.Add(new RowDefinition(GridLength.Auto));    // 镜头大小（摄像机窗口尺寸）

            // ── F6 区域树（世界 → 区域 → 子区域 → 地图叶子；搜索框整合进树：搜索时逆向渲染匹配路径）──
            var cascadePanel = new Grid { Margin = new Thickness(10, 8, 5, 2) };
            cascadePanel.RowDefinitions.Add(new RowDefinition(new GridLength(30)));  // 搜索框
            cascadePanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));     // 树
            cascadePanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            cascadePanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var regionLabel = new TextBlock { Text = "区域", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
            var searchBox = new TextBox
            {
                Watermark = "搜索地图，树状过滤…",
                FontSize = 12,
                Padding = new Thickness(10, 6),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                // 自适应：所在列为 Star，Stretch 填充该列可用宽度（原 Left 只占内容宽度）
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _bgRegionTree = new TreeView
            {
                FontSize = 11,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // 自适应：所在行已改 Star，去掉固定 MaxHeight=380 上限，树内滚动随窗口伸缩
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _bgRegionTree.SelectionChanged += (_, e) => BgOnRegionChanged(e.AddedItems.Count > 0 ? e.AddedItems[0] as WorldMapTreeNode : null);
            // 点击区域行任意位置展开/折叠子节点（箭头除外——箭头走默认 toggle；叶子无子不处理）。胶水规则 2026-08-09。
            _bgRegionTree.PointerPressed += (_, e) =>
            {
                try
                {
                    if (e.Source is ToggleButton) return;
                    if (e.Source is Control c)
                    {
                        var tvi = c.FindAncestorOfType<TreeViewItem>();
                        if (tvi?.Header is WorldMapTreeNode node && node.Children.Count > 0 && !node.IsLeafMap)
                        {
                            tvi.IsExpanded = !tvi.IsExpanded;
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TreeExpand: {ex.Message}"); }
            };
            searchBox.TextChanged += (_, _) =>
            {
                try
                {
                    RebuildRegionTree(searchBox.Text ?? "");
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TreeSearch: {ex.Message}"); }
            };
            Grid.SetRow(regionLabel, 1); Grid.SetColumn(regionLabel, 0);
            Grid.SetRow(searchBox, 0); Grid.SetColumn(searchBox, 1);
            Grid.SetRow(_bgRegionTree, 1); Grid.SetColumn(_bgRegionTree, 1);
            cascadePanel.Children.Add(regionLabel);
            cascadePanel.Children.Add(searchBox);
            cascadePanel.Children.Add(_bgRegionTree);
            Grid.SetRow(cascadePanel, 0);
            left.Children.Add(cascadePanel);

            // ── F2 选择历史：点一条直接应用该地图+crop ──
            var historyPanel = new DockPanel { Margin = new Thickness(10, 2, 5, 8), LastChildFill = true };
            var historyHeader = new TextBlock { Text = "🕐 选择历史", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(historyHeader, Dock.Top);
            historyPanel.Children.Add(historyHeader);
            _bgHistoryList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), BorderBrush = BrushBorder };
            _bgHistoryList.SelectionChanged += (_, _) =>
            {
                try { if (_bgHistoryList.SelectedItem is MapHistoryEntry he) { _bgHistoryList.SelectedIndex = -1; BgApplyHistoryEntry(he); } }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] HistoryClick: {ex.Message}"); }
            };
            RefreshBgHistoryList();
            historyPanel.Children.Add(_bgHistoryList);
            Grid.SetRow(historyPanel, 1);
            left.Children.Add(historyPanel);

            // ── 摄像机镜头大小（独立区域，与静态选区尺寸分开；写 PetSettings.ViewportW/H + 事件应用到窗口）──
            var viewportPanel = new StackPanel { Margin = new Thickness(10, 6, 5, 2), Spacing = 2 };
            viewportPanel.Children.Add(new TextBlock { Text = "🎥 摄像机镜头大小", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim });
            var viewportRow = new Grid { ColumnSpacing = 4 };
            viewportRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // "宽"
            viewportRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // 宽值（占一半剩余宽度）
            viewportRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // "高"
            viewportRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // 高值（占一半剩余宽度）
            viewportRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 应用
            var vpW = new TextBox { Text = _settings.ViewportW.ToString(), FontSize = 12, Padding = new Thickness(10, 6), Background = Brushes.White, Foreground = Brushes.Black, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            var vpH = new TextBox { Text = _settings.ViewportH.ToString(), FontSize = 12, Padding = new Thickness(10, 6), Background = Brushes.White, Foreground = Brushes.Black, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            var vpBtn = new Button { Content = "应用", FontSize = 9, Padding = new Thickness(8, 2), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            UiStyle.ApplyHover(vpBtn);
            vpBtn.Click += (_, _) =>
            {
                try
                {
                    if (int.TryParse(vpW.Text?.Trim(), out var w) && int.TryParse(vpH.Text?.Trim(), out var h) && w >= 320 && h >= 200)
                    {
                        _settings.ViewportW = w;
                        _settings.ViewportH = h;
                        _config.SaveSettings(_settings);
                        OnViewportSizeApplied?.Invoke(w, h);
                        if (_statusBarText != null) _statusBarText.Text = $"镜头大小已应用: {w}×{h}";
                    }
                    else if (_statusBarText != null) _statusBarText.Text = "镜头大小无效（最小 320×200）";
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ViewportApply: {ex.Message}"); }
            };
            var vpWLabel = new TextBlock { Text = "宽", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
            var vpHLabel = new TextBlock { Text = "高", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(vpWLabel, 0);
            Grid.SetColumn(vpW, 1);
            Grid.SetColumn(vpHLabel, 2);
            Grid.SetColumn(vpH, 3);
            Grid.SetColumn(vpBtn, 4);
            viewportRow.Children.Add(vpWLabel);
            viewportRow.Children.Add(vpW);
            viewportRow.Children.Add(vpHLabel);
            viewportRow.Children.Add(vpH);
            viewportRow.Children.Add(vpBtn);
            viewportPanel.Children.Add(viewportRow);
            viewportPanel.Children.Add(new TextBlock { Text = "仅摄像机模式生效（右键菜单 🎨 背景 → 摄像机）", FontSize = 8, Foreground = BrushTxtDim, TextWrapping = TextWrapping.Wrap });
            Grid.SetRow(viewportPanel, 2);
            left.Children.Add(viewportPanel);

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            // 右：大预览 Canvas + 选区框+8手柄(F3) + 信息 + 预设/微调 + 应用
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            _bgPreviewCanvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(255, 20, 20, 26)), Margin = new Thickness(5, 10, 10, 4) };
            // 自适应：Uniform 等比缩放 + 居中（尺寸/位置由 BgUpdatePreviewScale 按画布大小计算，替换原 Stretch=None 480×360 固定）
            _bgPreviewImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // 选区框：亮黄描边 + 淡白填充，深色预览底上清晰可辨
            _bgCropRect = new Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 225, 77)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), IsVisible = false };
            _bgPreviewCanvas.Children.Add(_bgPreviewImage);
            _bgPreviewCanvas.Children.Add(_bgCropRect);
            // 8 个手柄（4 角 + 4 边），初始隐藏，选区可见时由 BgUpdateCropHandles 定位
            var handleFill = new SolidColorBrush(Color.FromArgb(255, 255, 225, 77));
            var handleStroke = new SolidColorBrush(Color.FromArgb(255, 30, 30, 38));
            for (int i = 0; i < 8; i++)
            {
                var h = new Rectangle { Width = BgHandleSize, Height = BgHandleSize, Fill = handleFill, Stroke = handleStroke, StrokeThickness = 1, IsVisible = false };
                _bgCropHandles.Add(h);
                _bgPreviewCanvas.Children.Add(h);
            }
            Canvas.SetLeft(_bgPreviewImage, 0);
            Canvas.SetTop(_bgPreviewImage, 0);
            _bgPreviewCanvas.PointerPressed += BgPreviewPressed;
            _bgPreviewCanvas.PointerMoved += BgPreviewMoved;
            _bgPreviewCanvas.PointerReleased += BgPreviewReleased;
            // 自适应：画布尺寸变化时重算预览图缩放/偏移，并同步选区框与手柄（crop 几何按图像像素换算）
            _bgPreviewCanvas.LayoutUpdated += (_, _) => BgUpdatePreviewScale();
            Grid.SetRow(_bgPreviewCanvas, 0);
            right.Children.Add(_bgPreviewCanvas);

            var bottom = new StackPanel { Margin = new Thickness(10, 4, 10, 10), Spacing = 6 };
            _mapNameLabel = new TextBlock { Text = "选择地图查看预览", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = BrushTxtPrimary };
            bottom.Children.Add(_mapNameLabel);
            _bgCropInfo = new TextBlock { Text = "拖出选区 / 选预设尺寸 / 点选区拖动或拉手柄微调", FontSize = 10, Foreground = BrushTxtDim };
            bottom.Children.Add(_bgCropInfo);

            // ── F3 预设视口尺寸 + 整图 + 清除 ──
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            presetRow.Children.Add(new TextBlock { Text = "选区尺寸", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            foreach (var (lbl, w, h) in new (string, int, int)[] { ("800×600", 800, 600), ("1024×768", 1024, 768), ("1280×720", 1280, 720) })
            {
                var captured = (lbl, w, h);
                var b = new Button { Content = captured.lbl, FontSize = 9, Padding = new Thickness(6, 2), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                UiStyle.ApplyHover(b);
                b.Click += (_, _) => BgApplyPresetSize(captured.w, captured.h);
                presetRow.Children.Add(b);
            }
            var fullBtn = new Button { Content = "整图", FontSize = 9, Padding = new Thickness(6, 2), Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            UiStyle.ApplyHover(fullBtn);
            fullBtn.Click += (_, _) => BgApplyFullMap();
            presetRow.Children.Add(fullBtn);
            var clearBtn = new Button { Content = "✕ 清除", FontSize = 9, Padding = new Thickness(6, 2), Background = BrushInputBg, Foreground = BrushTxtDim, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            UiStyle.ApplyHover(clearBtn);
            clearBtn.Click += (_, _) => BgClearCrop();
            presetRow.Children.Add(clearBtn);
            bottom.Children.Add(presetRow);

            // ── F3 微调（点选区后用箭头平移，步长=世界坐标 BgNudgeStep）──
            var nudgeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            nudgeRow.Children.Add(new TextBlock { Text = "微调", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            foreach (var (dir, dx, dy) in new (string, int, int)[] { ("◀", -1, 0), ("▶", 1, 0), ("▲", 0, -1), ("▼", 0, 1) })
            {
                var captured = (dir, dx, dy);
                var b = new Button { Content = captured.dir, Width = 26, Height = 22, Padding = new Thickness(0), FontSize = 10, Background = BrushInputBg, Foreground = BrushTxtSecondary, BorderBrush = BrushBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                UiStyle.ApplyHover(b);
                b.Click += (_, _) => BgNudgeCrop(captured.dx, captured.dy);
                nudgeRow.Children.Add(b);
            }
            bottom.Children.Add(nudgeRow);

            // ── point 2 分层显隐 toggle（背景/地面/物件/NPC/传送门/地面线）── flag 在 PetSettings + MapService（共享单例），toggle 即时影响预览与桌面
            // 构建前先把保存的 flag 同步到 MapService（设置窗口为单例只构建一次；启动后 MapService 为类默认值全开，
            // 不同步则预览/应用会渲染出用户已勾掉「不显示」的层——与 OpenBackgroundTab 的同步互为兜底）
            BgSyncLayerFlagsFromSettings();
            var layerToggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            layerToggleRow.Children.Add(new TextBlock { Text = "显示", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            var layerToggles = new (string Label, Action<bool> Set, Func<bool> Get)[]
            {
                ("背景", v => { _settings.MapShowBack = v; _mapSvc.MapShowBack = v; }, () => _settings.MapShowBack),
                ("地面", v => { _settings.MapShowTile = v; _mapSvc.MapShowTile = v; }, () => _settings.MapShowTile),
                ("物件", v => { _settings.MapShowObj = v; _mapSvc.MapShowObj = v; }, () => _settings.MapShowObj),
                ("NPC", v => { _settings.MapShowLife = v; _mapSvc.MapShowLife = v; }, () => _settings.MapShowLife),
                ("传送门", v => { _settings.MapShowPortal = v; _mapSvc.MapShowPortal = v; }, () => _settings.MapShowPortal),
                ("地面线", v => { _settings.MapShowFoothold = v; _mapSvc.MapShowFoothold = v; }, () => _settings.MapShowFoothold),
            };
            foreach (var (lbl, setter, getter) in layerToggles)
            {
                var captured = (apply: setter, read: getter);
                var cb = new CheckBox { Content = lbl, FontSize = 9, Foreground = BrushTxtSecondary, Padding = new Thickness(2, 0), IsChecked = captured.read() };
                cb.IsCheckedChanged += (_, _) =>
                {
                    try
                    {
                        captured.apply(cb.IsChecked ?? true);
                        _config.SaveSettings(_settings);
                        _mapSvc.InvalidateFullRenderCache();
                        if (_bgCurrentMap != null) BgRenderPreview(_bgCurrentMap.Id, null);
                        // 通知 PetWindow：桌面背景窗口按新 flag 重渲染（否则桌面仍显示旧图）
                        OnLayerTogglesChanged?.Invoke();
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LayerToggle: {ex.Message}"); }
                };
                layerToggleRow.Children.Add(cb);
            }
            bottom.Children.Add(layerToggleRow);

            var applyBtn = new Button { Content = "✅ 应用到主窗口", FontSize = 12, Padding = new Thickness(20, 8), Background = BrushAccent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Right };
            UiStyle.ApplyHover(applyBtn);
            applyBtn.Click += (_, _) => BgApplySelection();
            bottom.Children.Add(applyBtn);
            // 自适应：底部内容包 ScrollViewer（窗口矮时不被挤没），MaxHeight=210 上限保留预览区空间
            var bottomScroll = new ScrollViewer
            {
                Content = bottom,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 210
            };
            Grid.SetRow(bottomScroll, 1);
            right.Children.Add(bottomScroll);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            BgLoadCatalog();
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildBackgroundTab: {ex.Message}"); return new Border(); }
    }

    /// <summary>N3：切换到背景设置 sub-tab 并确保可见。PetWindow.OpenBackgroundTab 委托到此。</summary>
    public void OpenBackgroundTab()
    {
        try
        {
            // 分层 flag 以保存的 PetSettings 为准回填 MapService：层开关只在「变化」时写 MapService
            // （BuildBackgroundTab 的 IsCheckedChanged），启动后 MapService 停留在类默认值（全开），
            // 与用户保存的「不显示」flag 脱节 → 预览/桌面应用渲染出已勾掉的层。每次打开背景 tab 同步一次。
            BgSyncLayerFlagsFromSettings();
            SwitchSubTab(2); if (IsVisible) Activate(); else Show();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenBackgroundTab: {ex.Message}"); }
    }

    /// <summary>把保存的 PetSettings 分层显隐同步到 MapService（打开背景 tab / 构建层开关时调用）。</summary>
    private void BgSyncLayerFlagsFromSettings()
    {
        try
        {
            _mapSvc.MapShowBack = _settings.MapShowBack;
            _mapSvc.MapShowTile = _settings.MapShowTile;
            _mapSvc.MapShowObj = _settings.MapShowObj;
            _mapSvc.MapShowLife = _settings.MapShowLife;
            _mapSvc.MapShowPortal = _settings.MapShowPortal;
            _mapSvc.MapShowFoothold = _settings.MapShowFoothold;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgSyncLayerFlags: {ex.Message}"); }
    }

    private void BgLoadCatalog()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (!_wzService.IsLoaded)
                {
                    Dispatcher.UIThread.Post(() => { if (_bgCropInfo != null) _bgCropInfo.Text = "⚠️ WZ 未加载，请先在 ⚙️ WZ 配置 设置路径"; });
                    return;
                }
                _wzService.WaitForMapCatalog(15000);
                var ids = _wzService.ListMapIds();
                _bgAllMaps.Clear();
                foreach (var id in ids) _bgAllMaps.Add(new MapEntry(id, _wzService.GetMapName(id)));
                // F6：构建区域→街道→地图 层级（供级联下拉）
                _bgHierarchy = _wzService.GetMapHierarchy();
                Dispatcher.UIThread.Post(() =>
                {
                    // 填充区域树（WorldMap 层级，Java familyMap/mapListCode 逻辑；节点 = 名称 + 地图数；
                    // 最底层区域挂占位子节点 → 展开时懒加载叶子地图）
                    if (_bgRegionTree != null)
                    {
                        _bgRegionTree.ItemTemplate = new FuncTreeDataTemplate<WorldMapTreeNode>(
                            (node, _) =>
                            {
                                var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                                if (node.IsLeafMap)
                                {
                                    stack.Children.Add(new TextBlock { Text = "📍", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
                                    stack.Children.Add(new TextBlock { Text = node.Name, FontSize = 11, Foreground = BrushTxtPrimary, VerticalAlignment = VerticalAlignment.Center });
                                }
                                else if (node.IsLeafPlaceholder)
                                {
                                    stack.Children.Add(new TextBlock { Text = "…", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
                                    stack.Children.Add(new TextBlock { Text = node.Name, FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
                                }
                                else
                                {
                                    // 展开/折叠指示由 TreeView 原生箭头承担（移除自定义 ▾/▪，避免与原生箭头重叠）
                                    stack.Children.Add(new TextBlock { Text = node.Name, FontSize = 11, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center });
                                    stack.Children.Add(new TextBlock { Text = $"[{node.TotalCount}]", FontSize = 9, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
                                }
                                return stack;
                            },
                            node => node.Children);
                        _bgRegionTree.ItemsSource = _wzService.GetWorldMapTree();
                        // 展开占位子节点 → 后台懒加载叶子（MapList 地图 + portal 传送门关联），完成后 UI 刷新
                        _bgRegionTree.AddHandler(TreeViewItem.ExpandedEvent, (_, e) =>
                        {
                            try
                            {
                                if (e.Source is TreeViewItem tvi && tvi.DataContext is WorldMapTreeNode node)
                                    EnsureNodeLeaves(node);
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TreeExpand: {ex.Message}"); }
                        });
                    }
                    if (_bgCropInfo != null) _bgCropInfo.Text = $"共 {_bgAllMaps.Count} 张地图；区域树选叶子直接预览";
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgLoadCatalog: {ex.Message}"); }
        });
    }

    /// <summary>收集节点及全部子孙的 MapIds（多级动态聚合）。</summary>
    private static void CollectNodeMapIds(WorldMapTreeNode node, HashSet<string> acc)
    {
        if (node == null) return;
        foreach (var id in node.MapIds) acc.Add(id);
        foreach (var ch in node.Children) CollectNodeMapIds(ch, acc);
    }

    /// <summary>叶子解析：数字 LeafMapId → 单地图；toolTip 名（非数字）→ 树内反查同名区域（含子孙）地图。</summary>
    private void ResolveLeafMapIds(WorldMapTreeNode leaf, HashSet<string> acc)
    {
        if (leaf == null) return;
        if (int.TryParse(leaf.LeafMapId, out _)) { acc.Add(leaf.LeafMapId); return; }
        WorldMapTreeNode? found = null;
        void Find(WorldMapTreeNode n)
        {
            if (found != null) return;
            if (n.Name == leaf.LeafMapId) { found = n; return; }
            foreach (var c in n.Children) Find(c);
        }
        foreach (var r in _wzService.GetWorldMapTree()) Find(r);
        if (found != null) CollectNodeMapIds(found, acc);
        else acc.Add(leaf.LeafMapId);
    }

    /// <summary>
    /// 展开最底层区域节点 → 懒加载叶子地图（MapList + portal 传送门关联）。
    /// 胶水规则 2026-08-08：点击才渲染，避免低性能；后台加载完成后 UI 线程替换占位子节点。
    /// </summary>
    private void EnsureNodeLeaves(WorldMapTreeNode node)
    {
        if (node == null || node.IsLeafMap || node.IsLeafPlaceholder || node.LeafLoaded) return;
        node.LeafLoaded = true;
        bool hasChildRegions = node.Children.Any(c => !c.IsLeafPlaceholder);
        if (!hasChildRegions && node.Children.Count > 0)
        {
            // 最底层区域：占位替换（MapList + MapLink toolTip + portal 传送门关联）
            Console.WriteLine($"[SettingsWindow] 懒加载叶子: {node.Name}");
            _ = Task.Run(() =>
            {
                try
                {
                    var maps = _wzService.LoadLeafMaps(node);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            node.Children.Clear();
                            foreach (var m in maps) node.Children.Add(m);
                            Console.WriteLine($"[SettingsWindow] 叶子加载完成: {node.Name} → {maps.Count} 张地图");
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] 叶子刷新: {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] 叶子加载: {ex.Message}"); }
            });
        }
        else if (node.MapIds.Count > 0)
        {
            // 有子区域的区域节点：自身 MapList 地图作为叶子追加到子区域之后（子目录在前，胶水规则 2026-08-09）
            Console.WriteLine($"[SettingsWindow] 懒加载区域自身地图: {node.Name} ({node.MapIds.Count} 张)");
            _ = Task.Run(() =>
            {
                try
                {
                    var maps = _wzService.LoadSelfMaps(node);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            foreach (var m in maps) node.Children.Add(m);
                            Console.WriteLine($"[SettingsWindow] 区域自身地图追加: {node.Name} → {maps.Count} 张");
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] 区域自身地图刷新: {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] 区域自身地图加载: {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// 搜索框 → 逆向过滤区域树：mapInfo 全量匹配地图（id/街道/名称）→ 保留含匹配地图的区域路径并挂匹配叶子；
    /// 空搜索显示完整树。胶水规则 2026-08-09：搜索整合进区域树，不再单独过滤地图列表。
    /// </summary>
    private void RebuildRegionTree(string query)
    {
        if (_bgRegionTree == null) return;
        var q = (query ?? "").Trim();
        var full = _wzService.GetWorldMapTree();
        if (q.Length == 0) { _bgRegionTree.ItemsSource = full; return; }
        var info = _wzService.GetMapStreetInfo();
        var matchIds = info
            .Where(kv => kv.Key.Contains(q) || kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();
        Console.WriteLine($"[SettingsWindow] TreeSearch: q='{q}' info={info.Count} match={matchIds.Count}");
        if (matchIds.Count == 0) { _bgRegionTree.ItemsSource = new List<WorldMapTreeNode>(); return; }
        var filtered = FilterTreeByMaps(full, matchIds, q, info);
        Console.WriteLine($"[SettingsWindow] TreeSearch: 过滤后根节点 {filtered.Count} 个");
        _bgRegionTree.ItemsSource = filtered;
    }

    /// <summary>逆向过滤：保留「自身名匹配 / MapIds 含匹配地图 / 子孙含匹配」的区域路径；匹配地图作为叶子挂到区域下。</summary>
    private static List<WorldMapTreeNode> FilterTreeByMaps(List<WorldMapTreeNode> nodes, HashSet<string> matchIds, string q, Dictionary<string, string> info)
    {
        var result = new List<WorldMapTreeNode>();
        foreach (var n in nodes)
        {
            if (n.IsLeafPlaceholder) continue;
            if (n.IsLeafMap)
            {
                if (matchIds.Contains(n.LeafMapId) || n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    result.Add(n);
                continue;
            }
            var kids = FilterTreeByMaps(n.Children.ToList(), matchIds, q, info);
            bool selfMatch = n.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            bool mapMatch = n.MapIds.Any(matchIds.Contains);
            if (mapMatch)
            {
                // 区域 MapIds 含匹配地图 → 无条件构建匹配叶子（即使子区域也有匹配，保证 streetName/mapName 任一命中都显示）
                foreach (var id in n.MapIds.Where(matchIds.Contains).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string name;
                    if (info.TryGetValue(id, out var v))
                    {
                        var parts = v.Split('|');
                        name = parts.Length > 1 && !string.IsNullOrEmpty(parts[0]) && parts[0] != "(未分类街道)"
                            ? parts[0] + ":" + parts[1] : parts[1];
                    }
                    else { name = id; }
                    kids.Add(new WorldMapTreeNode { IsLeafMap = true, LeafMapId = id, Key = id, Name = name });
                }
            }
            if (kids.Count > 0 || selfMatch || mapMatch)
            {
                result.Add(new WorldMapTreeNode
                {
                    Key = n.Key,
                    Name = n.Name,
                    MapIds = n.MapIds,
                    TotalCount = n.TotalCount,
                    Children = new System.Collections.ObjectModel.ObservableCollection<WorldMapTreeNode>(kids)
                });
            }
        }
        return result;
    }

    /// <summary>区域树选中：叶子（具体地图）→ 直接预览；区域节点 → 仅选中（无预览）。</summary>
    private void BgOnRegionChanged(WorldMapTreeNode? node = null)
    {
        node ??= _bgRegionTree?.SelectedItem as WorldMapTreeNode;
        if (node == null) return;
        if (!node.IsLeafMap) return;
        var ids = new HashSet<string>();
        ResolveLeafMapIds(node, ids);
        var first = ids.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        if (!string.IsNullOrEmpty(first))
        {
            var info = _wzService.GetMapStreetInfo();
            var name = info.TryGetValue(first, out var v) ? v.Split('|')[^1] : first;
            BgSelectMap(first, name);
        }
    }

    /// <summary>F2 刷新选择历史列表（数据源 PetSettings.MapHistory，由 PetWindow.ApplyMapSelection 持久化）。</summary>
    private void RefreshBgHistoryList()
    {
        if (_bgHistoryList == null) return;
        var items = (_settings.MapHistory ?? new System.Collections.Generic.List<MapHistoryEntry>()).ToList();
        _bgHistoryList.ItemsSource = items;
        _bgHistoryList.ItemTemplate = new FuncDataTemplate<MapHistoryEntry>((data, _) =>
        {
            if (data == null) return new StackPanel();
            var name = !string.IsNullOrEmpty(data.Name) ? data.Name : data.Id;
            var cropTxt = (data.CropW > 0 && data.CropH > 0) ? $"{data.CropW}×{data.CropH}" : "整图";
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2),
                Children = {
                    new TextBlock { Text = "🗺️", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { Spacing = 0, Children = {
                        new TextBlock { Text = name, FontSize = 11, Foreground = BrushTxtSecondary },
                        new TextBlock { Text = $"[{data.Id}] · {cropTxt}", FontSize = 8, Foreground = BrushTxtDim, FontFamily = new FontFamily("SF Mono, monospace") } } } } };
        });
    }

    /// <summary>F2 点击历史项：复用 OnBackgroundMapApplied 路径应用地图+crop（PetWindow 负责持久化与显示），并刷新预览/crop 可视化。</summary>
    private void BgApplyHistoryEntry(MapHistoryEntry e)
    {
        try
        {
            var name = !string.IsNullOrEmpty(e.Name) ? e.Name : (_wzService.IsLoaded ? _wzService.GetMapName(e.Id) : e.Id);
            var sel = new MapSelection(e.Id, name, e.CropX, e.CropY, e.CropW, e.CropH);
            _selectedMapId = e.Id;
            if (_mapNameLabel != null) _mapNameLabel.Text = $"{name}  [{e.Id}]";
            // 立即应用（复用 OnBackgroundMapApplied → PetWindow.ApplyMapSelection 持久化+显示）
            OnBackgroundMapApplied?.Invoke(sel);
            if (_statusBarText != null) _statusBarText.Text = $"已从历史应用: {name}";
            // 预览地图并按历史 crop 还原选区（CropW/H 全零 = 整图，由 BgShowPreviewWithCrop 自动判定）
            BgShowPreviewWithCrop(e.Id, e.CropX, e.CropY, e.CropW, e.CropH, null);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgApplyHistoryEntry: {ex.Message}"); }
    }

    /// <summary>
    /// 区域树/搜索选中地图：**仅预览/记录待应用状态，绝不应用到桌面**（用户要求：点击只预览，点「✅ 应用到主窗口」才生效）。
    /// 与 BgApplySelection（应用按钮）/BgApplyHistoryEntry（历史点击，明确应用操作）区分——本方法不调 OnBackgroundMapApplied。
    /// </summary>
    private void BgSelectMap(string mapId, string name)
    {
        _selectedMapId = mapId;
        _bgHasCrop = false;
        if (_bgCropRect != null) _bgCropRect.IsVisible = false;
        BgUpdateCropHandles();
        if (_mapNameLabel != null) _mapNameLabel.Text = $"{name}  [{mapId}]";
        if (_statusBarText != null) _statusBarText.Text = $"已预览 {name} —— 点「✅ 应用到主窗口」才应用到桌面";
        BgRenderPreview(mapId, null);
    }

    /// <summary>后台渲染地图缩略图并写入 _bgPreviewImage/_bgThumb*；onLoaded 在成功后于 UI 线程回调（用于历史 crop 还原）。</summary>
    private void BgRenderPreview(string mapId, Action? onLoaded)
    {
        var gen = ++_bgPreviewGen;
        // 立即反馈：预览区显示加载中（切换地图一次点击即响应，胶水反馈 2026-08-09）
        if (_bgCropInfo != null) _bgCropInfo.Text = "⏳ 加载中…";
        // M5：预览渲染改后台线程（LoadMap+CompositeMapThumbnail 不触 UI，原实现在 UI 线程持 WZ 锁整帧渲染冻结 UI）；
        // UI 更新（Bitmap 解码/赋值）仍在 UI 线程，gen 代际防串台。与 MapViewportWindow/BackgroundWindow 同一套后台渲染模式
        // （Skia 渲染在后台线程 + WZ/缓存锁保护，2026-08-09 的 Hide 并发 SIGSEGV 根因已由锁串行化消除）。
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var map = _mapSvc.LoadMap(mapId);
                byte[]? png = map != null ? _mapSvc.CompositeMapThumbnail(map, BgThumbW, BgThumbH) : null;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (gen != _bgPreviewGen) return;
                        _bgCurrentMap = map;
                        if (png == null || map == null)
                        {
                            if (_bgPreviewImage != null) _bgPreviewImage.Source = null;
                            if (_bgCropInfo != null) _bgCropInfo.Text = "渲染失败";
                            return;
                        }
                        using var ms = new MemoryStream(png);
                        var bmp = new Bitmap(ms);
                        if (_bgPreviewImage != null) _bgPreviewImage.Source = bmp;
                        int worldW = map.MaxX - map.MinX;
                        int worldH = map.MaxY - map.MinY;
                        _bgThumbW = (int)bmp.Size.Width;
                        _bgThumbH = (int)bmp.Size.Height;
                        _bgThumbScale = _bgThumbW > 0 ? (double)worldW / _bgThumbW : 1;
                        if (_bgCropInfo != null) _bgCropInfo.Text = $"地图 {worldW}×{worldH}，拖动选 crop（当前：整图）";
                        onLoaded?.Invoke();
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgRenderPreview: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgRenderPreview bg: {ex.Message}"); }
        });
    }

    /// <summary>渲染指定地图并将世界坐标 crop 还原为选区框（历史项回放）；cropW/H<=0 视为整图（不显示选区）。</summary>
    private void BgShowPreviewWithCrop(string mapId, int cropX, int cropY, int cropW, int cropH, Action? onLoaded = null)
    {
        _selectedMapId = mapId;
        BgRenderPreview(mapId, () =>
        {
            if (_bgCurrentMap == null) { onLoaded?.Invoke(); return; }
            if (cropW > 0 && cropH > 0)
            {
                double px = (cropX - _bgCurrentMap.MinX) / _bgThumbScale;
                double py = (cropY - _bgCurrentMap.MinY) / _bgThumbScale;
                double pw = cropW / _bgThumbScale;
                double ph = cropH / _bgThumbScale;
                BgSetCropRectPx(px, py, pw, ph, true);
            }
            else
            {
                BgClearCrop();
            }
            onLoaded?.Invoke();
        });
    }

    // ── 自适应：预览图缩放与 crop 坐标换算（画布坐标 = 图像像素 × 缩放 + 居中偏移）──
    /// <summary>自适应：按画布当前尺寸重算预览图 Uniform 缩放与居中偏移，并同步选区框/手柄位置。</summary>
    private void BgUpdatePreviewScale()
    {
        try
        {
            if (_bgPreviewCanvas == null || _bgPreviewImage == null || _bgThumbW <= 0 || _bgThumbH <= 0) return;
            double cw = _bgPreviewCanvas.Bounds.Width;
            double ch = _bgPreviewCanvas.Bounds.Height;
            if (cw <= 0 || ch <= 0) return;
            // Uniform 等比缩放（上限 2 倍防小图过度放大模糊），居中显示
            double s = Math.Min(cw / _bgThumbW, ch / _bgThumbH);
            s = Math.Min(s, 2.0);
            _bgImageScale = s;
            _bgImageOffsetX = (cw - _bgThumbW * s) / 2;
            _bgImageOffsetY = (ch - _bgThumbH * s) / 2;
            _bgPreviewImage.Width = _bgThumbW * s;
            _bgPreviewImage.Height = _bgThumbH * s;
            Canvas.SetLeft(_bgPreviewImage, _bgImageOffsetX);
            Canvas.SetTop(_bgPreviewImage, _bgImageOffsetY);
            // 有持久选区：按世界坐标重算画布几何（世界 → 图像像素 → 画布坐标），保持与缩放后的图对齐
            if (_bgHasCrop && _bgCurrentMap != null && _bgCropW > 0 && _bgCropH > 0)
            {
                double ix = (_bgCropX - _bgCurrentMap.MinX) / _bgThumbScale;
                double iy = (_bgCropY - _bgCurrentMap.MinY) / _bgThumbScale;
                BgSetCropRectPx(ix, iy, _bgCropW / _bgThumbScale, _bgCropH / _bgThumbScale, false);
            }
            else if (_bgCropRect != null && _bgCropRect.IsVisible)
            {
                // 拖拽中的临时选区：按图像像素换算现有画布几何
                double ix = (Canvas.GetLeft(_bgCropRect) - _bgImageOffsetX) / s;
                double iy = (Canvas.GetTop(_bgCropRect) - _bgImageOffsetY) / s;
                BgSetCropRectPx(ix, iy, _bgCropRect.Width / s, _bgCropRect.Height / s, false);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BgUpdatePreviewScale: {ex.Message}"); }
    }

    /// <summary>画布坐标 → 图像像素坐标（X）。</summary>
    private double BgCanvasToImgX(double cx) => _bgImageScale > 0 ? (cx - _bgImageOffsetX) / _bgImageScale : cx;
    /// <summary>画布坐标 → 图像像素坐标（Y）。</summary>
    private double BgCanvasToImgY(double cy) => _bgImageScale > 0 ? (cy - _bgImageOffsetY) / _bgImageScale : cy;
    /// <summary>图像像素坐标 → 画布坐标（X）。</summary>
    private double BgImgToCanvasX(double ix) => _bgImageOffsetX + ix * _bgImageScale;
    /// <summary>图像像素坐标 → 画布坐标（Y）。</summary>
    private double BgImgToCanvasY(double iy) => _bgImageOffsetY + iy * _bgImageScale;
    /// <summary>以画布坐标设置选区（内部换算为图像像素后走 BgSetCropRectPx，供鼠标交互使用）。</summary>
    private void BgSetCropRectCanvas(double x, double y, double w, double h, bool persist)
    {
        BgSetCropRectPx(BgCanvasToImgX(x), BgCanvasToImgY(y), w / _bgImageScale, h / _bgImageScale, persist);
    }

    // ── F3 crop 选区：像素几何读写 + 手柄定位 ──
    /// <summary>以像素坐标设置选区框（含 clamp + 同步世界坐标 + 手柄），persist=true 时写入 _bgCropX/Y/W/H 与 _bgHasCrop。</summary>
    private void BgSetCropRectPx(double x, double y, double w, double h, bool persist)
    {
        if (_bgCropRect == null || _bgCurrentMap == null) return;
        x = Math.Max(0, Math.Min(x, _bgThumbW));
        y = Math.Max(0, Math.Min(y, _bgThumbH));
        w = Math.Min(Math.Max(0, w), _bgThumbW - x);
        h = Math.Min(Math.Max(0, h), _bgThumbH - y);
        _bgCropRect.IsVisible = true;
        // 自适应：画布坐标 = 图像像素 × 缩放 + 居中偏移（世界坐标换算仍按图像像素，见下方 persist）
        Canvas.SetLeft(_bgCropRect, BgImgToCanvasX(x));
        Canvas.SetTop(_bgCropRect, BgImgToCanvasY(y));
        _bgCropRect.Width = w * _bgImageScale;
        _bgCropRect.Height = h * _bgImageScale;
        if (persist)
        {
            _bgCropX = _bgCurrentMap.MinX + (int)(x * _bgThumbScale);
            _bgCropY = _bgCurrentMap.MinY + (int)(y * _bgThumbScale);
            _bgCropW = (int)(w * _bgThumbScale);
            _bgCropH = (int)(h * _bgThumbScale);
            _bgHasCrop = true;
        }
        BgUpdateCropHandles();
        if (_bgCropInfo != null)
        {
            var worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
            var worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
            if (w < 1 || h < 1) _bgCropInfo.Text = $"地图 {worldW}×{worldH}，当前：整图";
            else
            {
                // 拖拽中（persist=false）也实时显示世界坐标，松手后才写入 _bgCropX/Y/W/H
                int liveX = _bgCurrentMap.MinX + (int)(x * _bgThumbScale);
                int liveY = _bgCurrentMap.MinY + (int)(y * _bgThumbScale);
                int liveW = (int)(w * _bgThumbScale);
                int liveH = (int)(h * _bgThumbScale);
                _bgCropInfo.Text = $"crop: {liveW}×{liveH} @({liveX},{liveY}) 世界坐标";
            }
        }
    }

    /// <summary>按当前选区框几何重定位 8 个手柄（4 角 + 4 边中点），选区不可见时隐藏。</summary>
    private void BgUpdateCropHandles()
    {
        if (_bgCropRect == null) return;
        var visible = _bgCropRect.IsVisible && _bgCropRect.Width > 0 && _bgCropRect.Height > 0;
        double x = Canvas.GetLeft(_bgCropRect);
        double y = Canvas.GetTop(_bgCropRect);
        double w = _bgCropRect.Width;
        double h = _bgCropRect.Height;
        // 顺序：nw, n, ne, e, se, s, sw, w
        double[] xs = { x, x + w / 2, x + w, x + w, x + w, x + w / 2, x, x };
        double[] ys = { y, y, y, y + h / 2, y + h, y + h, y + h, y + h / 2 };
        for (int i = 0; i < _bgCropHandles.Count && i < 8; i++)
        {
            var hd = _bgCropHandles[i];
            hd.IsVisible = visible;
            if (!visible) continue;
            Canvas.SetLeft(hd, xs[i] - BgHandleHalf);
            Canvas.SetTop(hd, ys[i] - BgHandleHalf);
        }
    }

    /// <summary>命中测试：返回手柄代号（"nw"..)"或 "move"（选区内）或 null（空白区，用于新建选区）。</summary>
    private string? BgHitTestHandle(Point p)
    {
        if (_bgCropRect == null || !_bgCropRect.IsVisible || _bgCropRect.Width < 1 || _bgCropRect.Height < 1) return null;
        double x = Canvas.GetLeft(_bgCropRect);
        double y = Canvas.GetTop(_bgCropRect);
        double w = _bgCropRect.Width;
        double h = _bgCropRect.Height;
        string[] names = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };
        double[] xs = { x, x + w / 2, x + w, x + w, x + w, x + w / 2, x, x };
        double[] ys = { y, y, y, y + h / 2, y + h, y + h, y + h, y + h / 2 };
        for (int i = 0; i < 8; i++)
        {
            if (Math.Abs(p.X - xs[i]) <= BgHandleHalf + 2 && Math.Abs(p.Y - ys[i]) <= BgHandleHalf + 2) return names[i];
        }
        if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h) return "move";
        return null;
    }

    private void BgPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_bgCurrentMap == null || _bgPreviewCanvas == null) return;
        var p = e.GetPosition(_bgPreviewCanvas);
        var hit = BgHitTestHandle(p);
        _bgCropOrigin = p;
        if (hit != null && hit != "move")
        {
            // 缩放模式：从对应手柄拉
            _bgCropMode = "resize";
            _bgActiveHandle = hit;
        }
        else if (hit == "move")
        {
            // 移动模式：在选区内拖动平移
            _bgCropMode = "move";
            _bgActiveHandle = null;
        }
        else
        {
            // 新建模式：从空白处拖出新选区（自适应：画布坐标入参，内部换算图像像素）
            _bgCropMode = "create";
            _bgActiveHandle = null;
            if (_bgCropRect != null)
            {
                BgSetCropRectCanvas(p.X, p.Y, 0, 0, false);
            }
        }
        // 记录拖拽起始的选区几何（move/resize 用）
        if (_bgCropRect != null && _bgCropMode != "create")
        {
            _bgCropStartX = Canvas.GetLeft(_bgCropRect);
            _bgCropStartY = Canvas.GetTop(_bgCropRect);
            _bgCropStartW = _bgCropRect.Width;
            _bgCropStartH = _bgCropRect.Height;
        }
        e.Pointer.Capture(_bgPreviewCanvas);
    }

    private void BgPreviewMoved(object? sender, PointerEventArgs e)
    {
        if (_bgCropRect == null || _bgPreviewCanvas == null || _bgCropMode == null) return;
        var p = e.GetPosition(_bgPreviewCanvas);
        if (_bgCropMode == "create")
        {
            double x = Math.Min(_bgCropOrigin.X, p.X);
            double y = Math.Min(_bgCropOrigin.Y, p.Y);
            double w = Math.Abs(p.X - _bgCropOrigin.X);
            double h = Math.Abs(p.Y - _bgCropOrigin.Y);
            BgSetCropRectCanvas(x, y, w, h, false);
            return;
        }
        double dx = p.X - _bgCropOrigin.X;
        double dy = p.Y - _bgCropOrigin.Y;
        if (_bgCropMode == "move")
        {
            BgSetCropRectCanvas(_bgCropStartX + dx, _bgCropStartY + dy, _bgCropStartW, _bgCropStartH, false);
        }
        else if (_bgCropMode == "resize" && _bgActiveHandle != null)
        {
            // 按手柄方向调整对应边：left/right/top/bottom
            double left = _bgCropStartX, top = _bgCropStartY, right = _bgCropStartX + _bgCropStartW, bottom = _bgCropStartY + _bgCropStartH;
            if (_bgActiveHandle.Contains('w')) left = _bgCropStartX + dx;
            if (_bgActiveHandle.Contains('e')) right = _bgCropStartX + _bgCropStartW + dx;
            if (_bgActiveHandle.Contains('n')) top = _bgCropStartY + dy;
            if (_bgActiveHandle.Contains('s')) bottom = _bgCropStartY + _bgCropStartH + dy;
            double nx = Math.Min(left, right), nw = Math.Abs(right - left);
            double ny = Math.Min(top, bottom), nh = Math.Abs(bottom - top);
            BgSetCropRectCanvas(nx, ny, nw, nh, false);
        }
    }

    private void BgPreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_bgCropRect == null || _bgCurrentMap == null || _bgCropMode == null) return;
        e.Pointer.Capture(null);
        var mode = _bgCropMode;
        _bgCropMode = null;
        _bgActiveHandle = null;
        double x = Canvas.GetLeft(_bgCropRect);
        double y = Canvas.GetTop(_bgCropRect);
        double w = _bgCropRect.Width;
        double h = _bgCropRect.Height;
        if (w < 8 || h < 8)
        {
            // 过小选区视为误操作：清空（move 模式尺寸不变不会命中此分支）
            BgClearCrop();
            return;
        }
        BgSetCropRectCanvas(x, y, w, h, true);
    }

    /// <summary>F3 预设视口尺寸：以当前选区中心（无选区则地图中心）为中心放置 w×h 选区。</summary>
    private void BgApplyPresetSize(int vw, int vh)
    {
        if (_bgCurrentMap == null) return;
        int worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
        int worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
        // 当前选区中心（世界坐标），无选区则用地图中心
        int cx, cy;
        if (_bgHasCrop) { cx = _bgCropX + _bgCropW / 2; cy = _bgCropY + _bgCropH / 2; }
        else { cx = (_bgCurrentMap.MinX + _bgCurrentMap.MaxX) / 2; cy = (_bgCurrentMap.MinY + _bgCurrentMap.MaxY) / 2; }
        int cw = Math.Min(vw, worldW);
        int ch = Math.Min(vh, worldH);
        int cxc = Math.Max(_bgCurrentMap.MinX, Math.Min(cx - cw / 2, _bgCurrentMap.MaxX - cw));
        int cyc = Math.Max(_bgCurrentMap.MinY, Math.Min(cy - ch / 2, _bgCurrentMap.MaxY - ch));
        double px = (cxc - _bgCurrentMap.MinX) / _bgThumbScale;
        double py = (cyc - _bgCurrentMap.MinY) / _bgThumbScale;
        double pw = cw / _bgThumbScale;
        double ph = ch / _bgThumbScale;
        BgSetCropRectPx(px, py, pw, ph, true);
    }

    /// <summary>F3 整图：清除选区，应用时按 cw<=0 → 整图（与 PetWindow/MapService 归一化一致）。</summary>
    private void BgApplyFullMap()
    {
        if (_bgCurrentMap == null) return;
        BgClearCrop();
        int worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
        int worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
        // 整图 = 全零 crop（PetWindow/历史归一化为 CropFull）
        _bgCropX = _bgCurrentMap.MinX; _bgCropY = _bgCurrentMap.MinY; _bgCropW = 0; _bgCropH = 0;
        _bgHasCrop = true;
        if (_bgCropInfo != null) _bgCropInfo.Text = $"地图 {worldW}×{worldH}，当前：整图";
    }

    /// <summary>清除选区（恢复到「不裁剪」状态）。</summary>
    private void BgClearCrop()
    {
        _bgHasCrop = false;
        _bgCropW = 0; _bgCropH = 0;
        if (_bgCropRect != null) _bgCropRect.IsVisible = false;
        BgUpdateCropHandles();
        if (_bgCropInfo != null && _bgCurrentMap != null)
        {
            var worldW = _bgCurrentMap.MaxX - _bgCurrentMap.MinX;
            var worldH = _bgCurrentMap.MaxY - _bgCurrentMap.MinY;
            _bgCropInfo.Text = $"地图 {worldW}×{worldH}，当前：整图";
        }
    }

    /// <summary>F3 微调：按世界坐标步长平移选区（自动 clamp 在地图范围内）。</summary>
    private void BgNudgeCrop(int dxWorld, int dyWorld)
    {
        if (!_bgHasCrop || _bgCurrentMap == null) return;
        // 整图（cw<=0）不可平移
        if (_bgCropW <= 0 || _bgCropH <= 0) return;
        int nx = _bgCropX + dxWorld * BgNudgeStep;
        int ny = _bgCropY + dyWorld * BgNudgeStep;
        nx = Math.Max(_bgCurrentMap.MinX, Math.Min(nx, _bgCurrentMap.MaxX - _bgCropW));
        ny = Math.Max(_bgCurrentMap.MinY, Math.Min(ny, _bgCurrentMap.MaxY - _bgCropH));
        double px = (nx - _bgCurrentMap.MinX) / _bgThumbScale;
        double py = (ny - _bgCurrentMap.MinY) / _bgThumbScale;
        double pw = _bgCropW / _bgThumbScale;
        double ph = _bgCropH / _bgThumbScale;
        BgSetCropRectPx(px, py, pw, ph, true);
    }

    private void BgApplySelection()
    {
        if (string.IsNullOrEmpty(_selectedMapId) || _bgCurrentMap == null)
        {
            if (_statusBarText != null) _statusBarText.Text = "请先选择地图";
            return;
        }
        int cx, cy, cw, ch;
        if (_bgHasCrop) { cx = _bgCropX; cy = _bgCropY; cw = _bgCropW; ch = _bgCropH; }
        else
        {
            // 未拖 crop：默认中心 800×600 视口（避免整图超大窗口；N1 的 cw<=0→整图仅作用于历史/零 crop）
            int vw = Math.Min(800, _bgCurrentMap.MaxX - _bgCurrentMap.MinX);
            int vh = Math.Min(600, _bgCurrentMap.MaxY - _bgCurrentMap.MinY);
            cx = (_bgCurrentMap.MinX + _bgCurrentMap.MaxX) / 2 - vw / 2;
            cy = (_bgCurrentMap.MinY + _bgCurrentMap.MaxY) / 2 - vh / 2;
            cw = vw; ch = vh;
        }
        var entry = _bgAllMaps.FirstOrDefault(m => m.Id == _selectedMapId);
        var name = entry?.Name ?? _selectedMapId;
        _pendingMapSelection = new MapSelection(_selectedMapId, name, cx, cy, cw, ch);
        OnBackgroundMapApplied?.Invoke(_pendingMapSelection);
        if (_statusBarText != null) _statusBarText.Text = "已应用到主窗口";
    }

    private void OpenMapBrowserFromSettings()
    {
        // 已并入背景设置 tab（OpenBackgroundTab），保留为空防误调
        try { OpenBackgroundTab(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenMapBrowserFromSettings: {ex.Message}"); }
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

            // 真实渲染地图预览（320×200），失败则保留占位；UI 线程（崩溃修复 2026-08-09：后台 Skia 与窗口 Hide 并发 SIGSEGV）
            var visualRef = mapVisual;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!_wzService.IsLoaded) return;
                    var m = _mapSvc.LoadMap(mapId);
                    if (m == null) return;
                    var png = _mapSvc.CompositeMapThumbnail(m, 320, 200);
                    if (png == null) return;
                    using var ms = new MemoryStream(png);
                    visualRef.Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MapPreviewImg: {ex.Message}"); }
            });
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

}
