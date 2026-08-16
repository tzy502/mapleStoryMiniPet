using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
/// SettingsWindow 气泡 tab 与其余（partial 拆分）：BuildBalloonTab 至文件尾。原实现从 SettingsWindow.axaml.cs 平移，行为完全一致。
/// </summary>
public partial class SettingsWindow
{
    private TextBlock? _balloonApplyHint;   // 气泡应用结果提示
    /// <summary>气泡搜索 300ms 防抖（S11：删每键日志 + 防全列表重建风暴）。</summary>
    private readonly DispatcherTimer _balloonSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private Border BuildBalloonTab()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // 自适应：侧栏改 Grid（标题/搜索 Auto + 列表 Star + 底部 Auto），列表随窗口伸缩并内部滚动；
            // 不包外层 ScrollViewer —— ScrollViewer 给无限高度会使 Star 行塌缩，Star 行 + ListBox 内部滚动等效保护短窗口
            var sidebar = new Grid { Margin = new Thickness(10), RowSpacing = 8, VerticalAlignment = VerticalAlignment.Stretch };
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 标题
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 搜索框
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 气泡列表（Star：随窗口伸缩 + 内部滚动）
            sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 应用按钮 + 提示
            var sidebarTitle = new TextBlock { Text = "💬 气泡样式", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetRow(sidebarTitle, 0);
            sidebar.Children.Add(sidebarTitle);

            // 素材库式：搜索框（ID/名称过滤）
            var balloonSearch = new TextBox
            {
                Watermark = "搜索气泡 ID / 名称",
                FontSize = 12,
                Padding = new Thickness(10, 6),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                // 自适应：Left → Stretch，搜索框占满侧栏宽度
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(balloonSearch, 1);
            sidebar.Children.Add(balloonSearch);

            // 自适应：列表改 Grid Star 行（有界高度）→ ListBox 内部滚动，天然防 461 项全展开（原 MaxHeight=300 固定已移除）
            var balloonListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
            // 分组列表：string = 组头（🕘最近使用 / 📋全部气泡），ValueTuple = 气泡项 → 历史与可筛选明显区分
            balloonListBox.ItemTemplate = new FuncDataTemplate<object>((data, _) =>
            {
                if (data is string header)
                {
                    return new TextBlock { Text = header, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(10, 10, 0, 2) };
                }
                if (data is ValueTuple<int, string> item)
                {
                    return new Border
                    {
                        Padding = new Thickness(10, 6),
                        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = item.Item2, FontSize = 12, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = $"#{item.Item1}", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center } } }
                    };
                }
                return null;
            });
            balloonListBox.SelectionChanged += (_, _) =>
            {
                try
                {
                    if (balloonListBox.SelectedItem is ValueTuple<int, string> item)
                    {
                        UpdateRealBalloonPreview(item.Item1);
                        AddBalloonHistory(item.Item1);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonSelect: {ex.Message}"); }
            };
            // 双击气泡 = 直接确认切换（应用）
            balloonListBox.DoubleTapped += (_, _) =>
            {
                try
                {
                    if (balloonListBox.SelectedItem is ValueTuple<int, string> item)
                        ApplyBalloonSelection(item);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonDbl: {ex.Message}"); }
            };
            // 搜索过滤：历史优先排序 + query 过滤（300ms 防抖，删每键调试日志）
            _balloonSearchDebounce.Tick += (_, _) =>
            {
                _balloonSearchDebounce.Stop();
                try
                {
                    ApplyBalloonFilter(balloonListBox, balloonSearch.Text ?? "");
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonSearch: {ex.Message}"); }
            };
            balloonSearch.TextChanged += (_, _) =>
            {
                _balloonSearchDebounce.Stop();
                _balloonSearchDebounce.Start();
            };
            // 可视化加载：后台枚举真实 WZ 气泡列表（UI 线程不阻塞）
            Task.Run(() =>
            {
                try { return _wzService.GetBalloonList(); }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonList: {ex.Message}"); return new List<(int Id, string Name)>(); }
            }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!t.IsCompletedSuccessfully || t.Result.Count == 0)
                    {
                        _balloonPreviewCanvas.Child = new TextBlock { Text = "未加载到气泡素材（WZ 不可用？）", FontSize = 12, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                        return;
                    }
                    _realBalloonList = t.Result;
                    ApplyBalloonFilter(balloonListBox, balloonSearch.Text ?? "");
                    // 默认选中当前配置的气泡（无则选第一个）
                    int curId = -1;
                    if (int.TryParse(_settings.BalloonId, out var cid)) curId = cid;
                    int selIdx = -1;
                    for (int i = 0; i < balloonListBox.Items.Count; i++)
                    {
                        if (balloonListBox.Items[i] is ValueTuple<int, string> it && it.Item1 == curId) { selIdx = i; break; }
                    }
                    if (selIdx >= 0)
                    {
                        balloonListBox.SelectedIndex = selIdx;
                    }
                    else if (balloonListBox.Items.Count > 0) balloonListBox.SelectedIndex = 0;
                    if (balloonListBox.SelectedItem is ValueTuple<int, string> first)
                        UpdateRealBalloonPreview(first.Item1);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonListPost: {ex.Message}"); }
            }), TaskScheduler.Default);
            Grid.SetRow(balloonListBox, 2);
            sidebar.Children.Add(balloonListBox);

            // 确认切换：应用按钮（选中后点击应用）或双击列表项；按钮+提示同处底部 Auto 行
            var balloonFooter = new StackPanel { Spacing = 6 };
            var applyBtn = MakeSmallBtn("✔ 应用气泡", () =>
            {
                if (balloonListBox.SelectedItem is ValueTuple<int, string> sel)
                    ApplyBalloonSelection(sel);
            });
            balloonFooter.Children.Add(applyBtn);
            _balloonApplyHint = new TextBlock { Text = "点击预览，双击或「应用」确认切换", FontSize = 10, Foreground = BrushTxtDim, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
            balloonFooter.Children.Add(_balloonApplyHint);
            Grid.SetRow(balloonFooter, 3);
            sidebar.Children.Add(balloonFooter);
            Grid.SetColumn(sidebar, 0);
            grid.Children.Add(sidebar);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            _balloonPreviewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Stretch };
            _balloonPreviewCanvas.Child = new TextBlock { Text = "加载中…", FontSize = 12, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_balloonPreviewCanvas, 2);
            grid.Children.Add(_balloonPreviewCanvas);
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildBalloonTab: {ex.Message}"); return new Border(); }
    }

    /// <summary>
    /// 可视化加载：从 WZ 提取真实气泡切片 → BalloonService 渲染 → 显示预览（后台线程合成，UI 不阻塞）。
    /// </summary>
    private async void UpdateRealBalloonPreview(int id)
    {
        try
        {
            if (_balloonPreviewCanvas == null)
            {
                return;
            }
            _balloonPreviewCanvas.Child = new TextBlock { Text = $"加载中… #{id}", FontSize = 12, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var idStr = id.ToString();
            var (tiles, clr) = await Task.Run(() =>
            {
                try
                {
                    var t = _wzService.ExtractBalloonTiles(idStr);
                    var c = _wzService.GetBalloonClr(idStr);
                    return (t, c);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonTiles: {ex.Message}"); return ((BalloonTiles?)null, -1); }
            });
            if (tiles == null)
            {
                _balloonPreviewCanvas.Child = new TextBlock { Text = "无素材", FontSize = 12, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                return;
            }

            var data = new BalloonData { Id = idStr, Tiles = tiles, Clr = clr };
            var png = await Task.Run(() =>
            {
                try { return _balloonService.RenderBalloon("你好！我是 MiniPet 桌宠", data); }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonRender: {ex.Message}"); return null; }
            });
            if (png == null || png.Length == 0)
            {
                _balloonPreviewCanvas.Child = new TextBlock { Text = "渲染失败", FontSize = 12, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                return;
            }

            using var ms = new MemoryStream(png);
            var bmp = new Bitmap(ms);
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
            var wrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12 };
            wrap.Children.Add(img);
            wrap.Children.Add(new TextBlock { Text = $"样式: {idStr}（真实 WZ 渲染）", FontSize = 10, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center });
            _balloonPreviewCanvas.Child = wrap;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdateRealBalloonPreview: {ex.Message}"); }
    }

    /// <summary>
    /// 气泡列表过滤（素材库式分组）：无搜索 → [🕘 最近使用] + [📋 全部气泡] 两组（组头区分）；
    /// 搜索 → [🔍 搜索结果] 单组（历史优先排序）。string = 组头，ValueTuple = 气泡项。
    /// </summary>
    private void ApplyBalloonFilter(ListBox listBox, string query)
    {
        try
        {
            if (_realBalloonList.Count == 0) { listBox.ItemsSource = null; return; }
            var q = (query ?? "").Trim();
            var items = new List<object>();
            if (q.Length == 0)
            {
                // 最近使用组（≤5，按历史顺序）
                var histIds = _settings.BalloonHistory.Take(5).ToHashSet();
                var recent = _realBalloonList
                    .Where(x => histIds.Contains(x.Id.ToString()))
                    .OrderBy(x => { var idx = _settings.BalloonHistory.IndexOf(x.Id.ToString()); return idx < 0 ? int.MaxValue : idx; })
                    .ToList();
                if (recent.Count > 0)
                {
                    items.Add("🕘 最近使用");
                    items.AddRange(recent.Cast<object>());
                }
                // 全部气泡组（按 id）
                items.Add("📋 全部气泡");
                items.AddRange(_realBalloonList.OrderBy(x => x.Id).Cast<object>());
            }
            else
            {
                // 搜索结果组（历史优先排序）
                var filtered = _realBalloonList
                    .Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Id.ToString().Contains(q))
                    .OrderByDescending(x => { var idx = _settings.BalloonHistory.IndexOf(x.Id.ToString()); return idx < 0 ? int.MaxValue : idx; })
                    .ThenBy(x => x.Id)
                    .ToList();
                items.Add($"🔍 搜索结果（{filtered.Count}）");
                items.AddRange(filtered.Cast<object>());
            }
            listBox.ItemsSource = items;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyBalloonFilter: {ex.Message}"); }
    }

    /// <summary>
    /// 确认切换气泡（胶水规则：点击预览 + 应用确认）：写入配置并持久化。
    /// </summary>
    private void ApplyBalloonSelection((int Id, string Name) item)
    {
        try
        {
            _settings.BalloonId = item.Id.ToString();
            _config.SaveSettings(_settings);
            Console.WriteLine($"[SettingsWindow] 应用气泡: {item.Id} {item.Name}");
            if (_balloonApplyHint != null)
            {
                _balloonApplyHint.Text = $"✔ 已应用：{item.Name} #{item.Id}";
                _balloonApplyHint.Foreground = new SolidColorBrush(Color.FromRgb(126, 217, 156));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyBalloon: {ex.Message}"); }
    }

    /// <summary>
    /// 记录气泡使用历史（插入头部、去重、截断 8 条）并持久化到 settings.json。
    /// </summary>
    private void AddBalloonHistory(int id)
    {
        try
        {
            var idStr = id.ToString();
            _settings.BalloonHistory.Remove(idStr);
            _settings.BalloonHistory.Insert(0, idStr);
            if (_settings.BalloonHistory.Count > 8)
                _settings.BalloonHistory.RemoveRange(8, _settings.BalloonHistory.Count - 8);
            _config.SaveSettings(_settings);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] AddBalloonHistory: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════
    private static Button MakeSmallBtn(string text, Action onClick)
    {
        var btn = new Button { Content = text, FontSize = 10, Padding = new Thickness(10, 4), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        UiStyle.ApplyHover(btn);
        btn.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Btn {text}: {ex.Message}"); } };
        return btn;
    }

    // ═══════════════════════════════════════════
    // 素材浏览集成
    // ═══════════════════════════════════════════
    private void OpenMaterialBrowser()
    {
        try
        {
            var browser = new MaterialBrowserWindow(_wzService, _config);
            browser.OnMaterialSelected += (id, name, _) =>
            {
                Console.WriteLine($"[SettingsWindow] OnMaterialSelected received: Id={id}, Name={name}");
                Dispatcher.UIThread.Post(() => SelectPet(name, id));
            };
            browser.Show(this);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenMaterialBrowser: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 预览动画
    // ═══════════════════════════════════════════
    private void StartPreviewAnimation()
    {
        try
        {
            _previewAnimTimer.Stop();

            if (_previewPngData == null || _previewStrip == null)
                return;

            _previewFrameIndex = 0;

            var delay = 240;
            if (_previewStrip.FrameData != null && _previewStrip.FrameData.Count > 0)
                delay = _previewStrip.FrameData[0].Delay;
            _previewAnimTimer.Interval = TimeSpan.FromMilliseconds(delay);
            _previewAnimTimer.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] StartPreviewAnimation: {ex}"); }
    }

    private void OnPreviewAnimTick(object? sender, EventArgs e)
    {
        try
        {
            if (_previewCanvas == null || _previewStrip == null || _previewSourceBitmap == null)
            {
                return;
            }
            // 预览内容已改 Grid（自适应），用 Panel 判断兼容
            if (_previewCanvas.Child is Panel stack)
            {
                foreach (var child in stack.Children)
                {
                    if (child is Border figure2 && figure2.Child is Image img2 && img2.Tag?.ToString() == "previewSprite")
                    {
                        var frameWidth = _previewStrip.FrameWidth;
                        var frameHeight = _previewStrip.FrameHeight;

                        var frameRect = new PixelRect(frameWidth * _previewFrameIndex, 0, frameWidth, frameHeight);
                        try
                        {
                            // ⚠️ 先换 Source 再 Dispose 旧 CroppedBitmap（防每帧泄漏 + 防布局测量已释放位图崩溃）
                            var oldSrc = img2.Source;
                            img2.Source = new CroppedBitmap(_previewSourceBitmap, frameRect);
                            (oldSrc as IDisposable)?.Dispose();
                            _balloonPreviewImage = img2;
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TickFrame: {ex.Message}"); }
                        _previewFrameIndex++;
                        if (_previewFrameIndex >= _previewStrip.FrameCount)
                        {
                            _previewFrameIndex = 0;
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPreviewAnimTick: {ex}"); }
    }

    // ═══════════════════════════════════════════
    // WZ 宠物列表加载
    // ═══════════════════════════════════════════
    private void LoadPetListFromWz()
    {
        try
        {
            // 只刷新 UI 列表显示，不修改用户数据
            Dispatcher.UIThread.Post(() =>
            {
                RefreshFavoriteList();
                RefreshRecentList();
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPetListFromWz: {ex.Message}"); }
    }
}
