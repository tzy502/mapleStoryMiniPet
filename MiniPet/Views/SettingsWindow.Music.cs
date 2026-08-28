using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow 音乐 tab（partial 拆分，V0.2.0 F16，设计 §11.8 + PLAN 步骤 7）：纯 C# 代码构建 UI。
/// 顶栏（刷新目录 / 搜索 / 联动地图 BGM 开关）+ 左分类右曲目双列表 + 底部播放控制区
/// （进度条点击 Seek / 上一首-播放暂停-停止-下一首 / 模式循环按钮 / 音量滑块）+ 状态栏小文本。
/// 懒加载：首次进入音乐 tab 才 GetCatalogAsync（首次约 2 秒，loading 提示）；
/// WZ 未加载 → 整面板禁用 + 居中灰字占位（不崩溃不弹错）；
/// 进度条三条件（PLAN Rev 5/6）：窗口可见 && 音乐 tab 选中 && 播放器非 Stopped 同时成立才跑
/// DispatcherTimer，任一丢失即停（窗口 Hide 不关闭，靠 IsVisible 变化事件兜底停表）；
/// 事件订阅全部经 Dispatcher.UIThread.Post 回 UI 线程（窗口单例 Hide 不关，订阅存活整个应用周期，
/// 与 _config.OnSettingsChanged 同款容忍策略）。
/// </summary>
public partial class SettingsWindow
{
    // ====== 常量 ======
    /// <summary>音乐 tab 在顶层 tab 行的索引（第 4 个：桌宠设置 / AI 设置 / 对话记录 / 音乐）。</summary>
    private const int MusicTabIndex = 3;
    /// <summary>进度条轮询间隔（毫秒）——只刷新进度与 mm:ss 时间文本，无需高频。</summary>
    private const int MusicProgressIntervalMs = 250;
    /// <summary>搜索输入即过滤的防抖间隔（毫秒，同素材/气泡 tab 的防抖模式）。</summary>
    private const int MusicSearchDebounceMs = 200;
    /// <summary>tooltip 全量关联地图条目展示上限，超出截断加省略说明（防御超长列表撑爆 tooltip）。</summary>
    private const int TrackMapTooltipMaxEntries = 30; // 兼容保留（tooltip 截断口径已改切片 moreCount 文案）

    // ── 音乐服务（axaml.cs 构造函数末尾两个追加参数注入，A4 接线透传） ──
    private readonly MusicCatalogService _musicCatalog;
    private readonly MusicPlayerService _musicPlayer;

    // ── 音乐 tab UI 引用 ──
    /// <summary>主内容容器（WZ 未加载时整体禁用 + 隐藏）。</summary>
    private Grid? _musicMainPanel;
    /// <summary>WZ 未加载灰占位（居中灰字，不崩溃不弹错）。</summary>
    private Border? _musicWzPlaceholder;
    /// <summary>左分类列表（GetCategories，"Bgm00" 形式无 .img 后缀）。</summary>
    private ListBox? _musicCategoryList;
    /// <summary>右曲目列表（当前分类曲目或搜索结果，行显示 MusicTrack.Display）。</summary>
    private ListBox? _musicTrackList;
    /// <summary>目录加载中提示（曲目区居中覆盖，首次约 2 秒）。</summary>
    private TextBlock? _musicLoadingHint;
    /// <summary>搜索框（输入即过滤，防抖）。</summary>
    private TextBox? _musicSearchInput;
    /// <summary>刷新目录按钮（InvalidateCatalog + 重载）。</summary>
    private Button? _musicRefreshBtn;
    /// <summary>联动地图 BGM 开关（绑定/回写 MusicPlayerService.SceneLinkEnabled，setter 立即持久化）。</summary>
    private CheckBox? _musicSceneLinkToggle;

    // ── V0.2.1 增量：反向地图展示 + 本地目录入口 ──
    /// <summary>本地目录入口按钮（顶栏「📁 本地」，点击 → 文件夹选择器 → SetLocalRootAsync → 重建目录；再次点击换目录）。</summary>
    private Button? _musicLocalBtn;
    // （2026-08-27 重写）反向地图切片直读服务层 TryGetTrackMaps——不再维护本地字典缓存，
    // 消除「切分类不刷新/缓存陈旧」一类状态漂移；刷新经 ReverseIndexChanged 事件驱动重建列表。

    // ── 播放控制区 ──
    /// <summary>当前曲名（"▶ 曲名"，CurrentChanged 驱动）。</summary>
    private TextBlock? _musicNowLabel;
    /// <summary>时间文本（mm:ss / mm:ss，进度条 tick 驱动）。</summary>
    private TextBlock? _musicTimeText;
    /// <summary>进度条轨道（点击 → SeekAsync，按点击位置比例换算目标时间）。</summary>
    private Border? _musicProgressTrack;
    /// <summary>进度条填充（宽度 = 比例 × 轨道实际宽度）。</summary>
    private Border? _musicProgressFill;
    /// <summary>播放/暂停按钮（内容随 State 在 ▶/⏸ 间切换）。</summary>
    private Button? _musicPlayPauseBtn;
    /// <summary>播放模式按钮（点击循环切 Sequential→RepeatOne→Random，显示当前）。</summary>
    private Button? _musicModeBtn;
    /// <summary>音量滑块（0-100 → SetVolume，初始读服务 Volume）。</summary>
    private Slider? _musicVolumeSlider;
    /// <summary>音量数值文本。</summary>
    private TextBlock? _musicVolumeLabel;
    /// <summary>状态栏小文本（PlaybackError 错误串与状态提示，不弹模态）。</summary>
    private TextBlock? _musicStatusText;

    // ── 音乐 tab 状态 ──
    /// <summary>当前选中分类（无 .img 后缀，如 "Bgm00"），空 = 未选。</summary>
    private string _musicSelectedCategory = "";
    /// <summary>当前搜索关键字（防抖后生效值；非空时右列表显示全库搜索结果）。</summary>
    private string _musicSearchQuery = "";
    /// <summary>右列表当前曲目（PlayAsync 的队列快照来源）。</summary>
    private List<MusicTrack> _musicVisibleTracks = new();
    /// <summary>音量事件回写滑块时的防回环标志（服务 VolumeChanged ↔ 滑块 ValueChanged）。</summary>
    private bool _musicVolumeSyncing;
    /// <summary>搜索防抖（输入即过滤，MusicSearchDebounceMs）。</summary>
    private readonly DispatcherTimer _musicSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(MusicSearchDebounceMs) };
    /// <summary>进度条轮询定时器（三条件同时成立才运行，见 UpdateMusicProgressTimer）。</summary>
    private readonly DispatcherTimer _musicProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(MusicProgressIntervalMs) };

    // ═══════════════════════════════════════════
    // 面板构建（BuildBody → BuildMusicPane，学现有三 tab 的机制）
    // ═══════════════════════════════════════════

    /// <summary>构建音乐 tab 主面板：主内容（顶栏/列表/控制区/状态栏）+ WZ 未加载灰占位二选一显示。</summary>
    private Border BuildMusicPane()
    {
        var root = new Grid();

        // 主内容四行：顶栏 / 列表区 / 播放控制区 / 状态栏
        _musicMainPanel = new Grid { Margin = new Thickness(10, 6, 10, 4), RowSpacing = 8 };
        _musicMainPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _musicMainPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _musicMainPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _musicMainPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var topBar = BuildMusicTopBar();
        Grid.SetRow(topBar, 0);
        _musicMainPanel.Children.Add(topBar);

        var lists = BuildMusicListsArea();
        Grid.SetRow(lists, 1);
        _musicMainPanel.Children.Add(lists);

        var controls = BuildMusicControls();
        Grid.SetRow(controls, 2);
        _musicMainPanel.Children.Add(controls);

        _musicStatusText = new TextBlock { Text = "就绪", FontSize = 10, Foreground = BrushTxtDim, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_musicStatusText, 3);
        _musicMainPanel.Children.Add(_musicStatusText);

        root.Children.Add(_musicMainPanel);

        // WZ 未加载灰占位：整面板不可用，居中灰字提示（不崩溃不弹错）
        _musicWzPlaceholder = new Border
        {
            IsVisible = false,
            Child = new TextBlock
            {
                Text = "WZ 未加载，无法读取音乐",
                FontSize = 14,
                Foreground = BrushTxtDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        root.Children.Add(_musicWzPlaceholder);

        UpdateMusicWzState();
        return new Border { Child = root };
    }

    /// <summary>顶栏：[刷新目录] + 搜索框（占满剩余宽度）+ [📁 本地] 目录入口 + [x] 联动地图 BGM 开关。</summary>
    private Grid BuildMusicTopBar()
    {
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        _musicRefreshBtn = new Button
        {
            Content = "🔄 刷新目录",
            FontSize = 10,
            Padding = new Thickness(10, 5),
            Background = BrushInputBg,
            Foreground = BrushTxtSecondary,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        UiStyle.ApplyHover(_musicRefreshBtn);
        _musicRefreshBtn.Click += (_, _) => OnMusicRefreshClick();
        Grid.SetColumn(_musicRefreshBtn, 0);
        bar.Children.Add(_musicRefreshBtn);

        _musicSearchInput = new TextBox
        {
            Watermark = "搜索曲名 / 分类（输入即过滤）",
            FontSize = 12,
            Padding = new Thickness(10, 6),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        _musicSearchInput.TextChanged += (_, _) =>
        {
            _musicSearchDebounce.Stop();
            _musicSearchDebounce.Start();
        };
        Grid.SetColumn(_musicSearchInput, 1);
        bar.Children.Add(_musicSearchInput);

        // 「📁 本地」本地目录入口（V0.2.1 增量④，联动开关旁）：点击弹文件夹选择器，选完即设根并重建
        _musicLocalBtn = new Button
        {
            Content = "📁 本地",
            FontSize = 10,
            Padding = new Thickness(10, 5),
            Background = BrushInputBg,
            Foreground = BrushTxtSecondary,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(_musicLocalBtn, "选择本地音乐目录（再次点击可更换）");
        UiStyle.ApplyHover(_musicLocalBtn);
        _musicLocalBtn.Click += (_, _) => OnMusicLocalFolderClick();
        Grid.SetColumn(_musicLocalBtn, 2);
        bar.Children.Add(_musicLocalBtn);

        _musicSceneLinkToggle = new CheckBox
        {
            Content = "联动地图 BGM",
            FontSize = 11,
            Foreground = BrushTxtSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _musicPlayer.SceneLinkEnabled
        };
        _musicSceneLinkToggle.IsCheckedChanged += (_, _) => OnMusicSceneLinkChanged();
        Grid.SetColumn(_musicSceneLinkToggle, 3);
        bar.Children.Add(_musicSceneLinkToggle);

        return bar;
    }

    /// <summary>列表区：左分类列表（固定宽）+ 分隔线 + 右曲目列表（含加载中提示覆盖层）。</summary>
    private Grid BuildMusicListsArea()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(200)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var sidebar = new Grid { Margin = new Thickness(0, 0, 10, 0), RowSpacing = 4 };
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        sidebar.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var sidebarTitle = new TextBlock { Text = "📁 分类", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim };
        Grid.SetRow(sidebarTitle, 0);
        sidebar.Children.Add(sidebarTitle);
        _musicCategoryList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _musicCategoryList.ItemTemplate = new FuncDataTemplate<string>((data, _) =>
            new TextBlock { Text = data ?? "", FontSize = 11, Foreground = BrushTxtSecondary, Padding = new Thickness(6, 3) });
        _musicCategoryList.SelectionChanged += OnMusicCategoryChanged;
        Grid.SetRow(_musicCategoryList, 1);
        sidebar.Children.Add(_musicCategoryList);
        Grid.SetColumn(sidebar, 0);
        grid.Children.Add(sidebar);

        var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        // 曲目区：列表 + 居中加载提示（首次约 2 秒）
        var listHost = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        _musicTrackList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _musicTrackList.ItemTemplate = new FuncDataTemplate<MusicTrack>((data, _) => BuildMusicTrackRow(data));
        _musicTrackList.DoubleTapped += (_, _) => OnMusicTrackDoubleTapped();
        listHost.Children.Add(_musicTrackList);
        _musicLoadingHint = new TextBlock
        {
            Text = "加载目录中…（首次约 2 秒）",
            FontSize = 12,
            Foreground = BrushTxtDim,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        listHost.Children.Add(_musicLoadingHint);
        Grid.SetColumn(listHost, 2);
        grid.Children.Add(listHost);

        return grid;
    }

    /// <summary>
    /// 曲目行模板（V0.2.1 增量⑤两行化）：主行 = [▶ 当前播放标记] 显示名 [▶ 播放按钮]（现状不动）；
    /// 副行 = dim 小字关联地图（从 _trackMapLines 快照取，无关联整行隐藏不留空行）。
    /// 行级 tooltip 显示全量关联（城镇分组在前），超出上限截断。当前播放行高亮 accent 色。
    /// ▶ 标记唯一真相源 = 构建时 _musicPlayer.Current 快照（无点播记录等第二真相源），
    /// Current 变化后由 OnMusicCurrentChanged → RebuildMusicListKeepSelection 重建列表触发重画。
    /// </summary>
    private Control BuildMusicTrackRow(MusicTrack? data)
    {
        var current = _musicPlayer.Current;
        bool isCurrent = data != null && current != null
                         && string.Equals(data.Key, current.Key, StringComparison.Ordinal);
        var row = new Grid { ColumnSpacing = 6 };
        row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // ── 主行 ──
        var marker = new TextBlock
        {
            Text = "▶",
            FontSize = 9,
            Foreground = BrushAccent,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = isCurrent
        };
        Grid.SetRow(marker, 0);
        Grid.SetColumn(marker, 0);
        row.Children.Add(marker);

        var name = new TextBlock
        {
            Text = data?.Display ?? "",
            FontSize = 11,
            Foreground = isCurrent ? BrushAccent : BrushTxtSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(name, 0);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var captured = data;
        var playBtn = new Button
        {
            Content = "▶",
            FontSize = 9,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = BrushTxtSecondary,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        UiStyle.ApplyHover(playBtn);
        playBtn.Click += (_, _) =>
        {
            if (captured != null)
            {
                PlayMusicFromUi(captured);
            }
        };
        Grid.SetRow(playBtn, 0);
        Grid.SetColumn(playBtn, 2);
        row.Children.Add(playBtn);

        // ── 副行：反查地图 dim 小字（跨全宽；直读服务层切片，无记录整行隐藏不留空行）──
        string? mapLine = null;
        string? tooltip = null;
        if (captured != null && _musicCatalog.TryGetTrackMaps(captured.Key, out var sliceItems, out var sliceMore))
        {
            mapLine = FormatTrackMapLine(sliceItems, sliceMore);
            tooltip = BuildTrackMapTooltip(sliceItems, sliceMore);
        }
        var mapsSub = new TextBlock
        {
            Text = mapLine ?? "",
            FontSize = 9,
            Foreground = BrushTxtDim,
            Margin = new Thickness(18, 1, 0, 2),
            IsVisible = !string.IsNullOrEmpty(mapLine),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(mapsSub, 1);
        Grid.SetColumnSpan(mapsSub, 3);
        row.Children.Add(mapsSub);

        // 行级 tooltip：全量关联地图（含未进副行的部分）
        if (!string.IsNullOrEmpty(tooltip))
        {
            ToolTip.SetTip(row, tooltip);
        }
        return row;
    }

    /// <summary>底部控制区两行：进度行（当前曲名 + 时间 + 可点击进度条）+ 按钮行（⏮⏯⏹⏭ + 模式 + 音量）。</summary>
    private Panel BuildMusicControls()
    {
        var container = new StackPanel { Spacing = 8 };

        // 第一行：当前曲名 + 时间文本 + 进度条（点击 → SeekAsync）
        var progressRow = new Grid { ColumnSpacing = 10 };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        progressRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        progressRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        _musicNowLabel = new TextBlock
        {
            // 统一格式（▶ 节点 · 曲名 / ▶ 曲名 / 未播放），与 OnMusicCurrentChanged 同源（FormatNowPlayingLabel）
            Text = FormatNowPlayingLabel(_musicPlayer.Current),
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = BrushTxtPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_musicNowLabel, 0);
        progressRow.Children.Add(_musicNowLabel);

        _musicTimeText = new TextBlock
        {
            Text = "--:-- / --:--",
            FontSize = 10,
            Foreground = BrushTxtDim,
            FontFamily = new FontFamily("SF Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_musicTimeText, 1);
        progressRow.Children.Add(_musicTimeText);

        _musicProgressFill = new Border
        {
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = BrushAccent,
            CornerRadius = new CornerRadius(3)
        };
        _musicProgressTrack = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = BrushInputBg,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = _musicProgressFill
        };
        _musicProgressTrack.PointerPressed += OnMusicProgressClick;
        Grid.SetColumn(_musicProgressTrack, 2);
        progressRow.Children.Add(_musicProgressTrack);

        container.Children.Add(progressRow);

        // 第二行：传输按钮 + 模式按钮（左）与 音量图标 + 滑块 + 数值（右）
        var buttonRow = new DockPanel { LastChildFill = false };

        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        transport.Children.Add(MakeMusicTransportButton("⏮️", () => RunMusicActionAsync(() => _musicPlayer.PrevAsync(), "上一首")));
        _musicPlayPauseBtn = MakeMusicTransportButton("▶", OnMusicPlayPauseClick);
        transport.Children.Add(_musicPlayPauseBtn);
        transport.Children.Add(MakeMusicTransportButton("⏹️", () => RunMusicActionAsync(() => _musicPlayer.StopAsync(), "停止")));
        transport.Children.Add(MakeMusicTransportButton("⏭️", () => RunMusicActionAsync(() => _musicPlayer.NextAsync(), "下一首")));
        _musicModeBtn = MakeMusicTransportButton(MusicModeLabel(_musicPlayer.Mode), CycleMusicPlayMode);
        transport.Children.Add(_musicModeBtn);
        DockPanel.SetDock(transport, Dock.Left);
        buttonRow.Children.Add(transport);

        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        volumeRow.Children.Add(new TextBlock { Text = "🔊", FontSize = 11, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center });
        _musicVolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(_musicPlayer.Volume, 0, 100),
            Width = 140,
            VerticalAlignment = VerticalAlignment.Center
        };
        _musicVolumeSlider.ValueChanged += OnMusicVolumeSliderChanged;
        volumeRow.Children.Add(_musicVolumeSlider);
        _musicVolumeLabel = new TextBlock
        {
            Text = _musicPlayer.Volume.ToString(),
            FontSize = 10,
            Foreground = BrushTxtDim,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 24
        };
        volumeRow.Children.Add(_musicVolumeLabel);
        DockPanel.SetDock(volumeRow, Dock.Right);
        buttonRow.Children.Add(volumeRow);

        container.Children.Add(buttonRow);
        return container;
    }

    /// <summary>传输/模式按钮统一样式（小号圆角边框按钮 + 悬浮效果 + 异常兜底）。</summary>
    private Button MakeMusicTransportButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            Background = BrushInputBg,
            Foreground = BrushTxtSecondary,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        UiStyle.ApplyHover(btn);
        btn.Click += (_, _) =>
        {
            try
            {
                onClick();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicBtn {text}: {ex.Message}"); }
        };
        return btn;
    }

    // ═══════════════════════════════════════════
    // 初始化与事件订阅（axaml.cs 构造函数末尾调用）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 音乐 tab 初始化：订阅播放服务事件（可能从后台/BASS 回调线程触发，一律 Post 到 UI 线程；
    /// 窗口单例 Hide 不关，订阅存活整个应用周期——与 _config.OnSettingsChanged 同款容忍策略）+
    /// 进度条三条件的可见性钩子 + 初始控件同步。
    /// </summary>
    private void InitMusic()
    {
        try
        {
            _musicPlayer.CurrentChanged += track => Dispatcher.UIThread.Post(() => OnMusicCurrentChanged(track));
            _musicPlayer.StateChanged += state => Dispatcher.UIThread.Post(() => OnMusicStateChanged(state));
            _musicPlayer.VolumeChanged += v => Dispatcher.UIThread.Post(() => OnMusicVolumeChanged(v));
            _musicPlayer.PlaybackError += msg => Dispatcher.UIThread.Post(() => ShowMusicStatus(msg, isError: true));
            // 反向索引增量发布（可能从扫描线程触发）：重建列表使副行/tooltip 浮现（选中保持）
            _musicCatalog.ReverseIndexChanged += () => Dispatcher.UIThread.Post(RebuildMusicListKeepSelection);

            _musicProgressTimer.Tick += OnMusicProgressTick;
            _musicSearchDebounce.Tick += (_, _) =>
            {
                _musicSearchDebounce.Stop();
                try
                {
                    _musicSearchQuery = _musicSearchInput?.Text ?? "";
                    RefreshMusicTrackList();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicSearch: {ex.Message}"); }
            };

            // 窗口 Hide/Show 可见性变化 → 重估进度条三条件（本窗口隐藏不关闭，IsVisible 变化是唯一可靠信号）
            PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                {
                    UpdateMusicProgressTimer();
                }
            };

            SyncMusicControlsFromService();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] InitMusic: {ex.Message}"); }
    }

    /// <summary>初始控件同步：音量滑块/数值、联动开关、模式按钮、⏯ 文案、当前曲名。</summary>
    private void SyncMusicControlsFromService()
    {
        try
        {
            if (_musicVolumeSlider != null)
            {
                _musicVolumeSlider.Value = Math.Clamp(_musicPlayer.Volume, 0, 100);
            }
            if (_musicVolumeLabel != null)
            {
                _musicVolumeLabel.Text = _musicPlayer.Volume.ToString();
            }
            if (_musicSceneLinkToggle != null)
            {
                _musicSceneLinkToggle.IsChecked = _musicPlayer.SceneLinkEnabled;
            }
            // 本地目录按钮初始反馈：配置里存过路径即标示启用中（tooltip 恢复为完整路径）
            try
            {
                string savedLocal = _config.LoadSettings().Music?.LocalMusicPath ?? "";
                SetLocalBtnState(!string.IsNullOrWhiteSpace(savedLocal), string.IsNullOrWhiteSpace(savedLocal) ? null : savedLocal);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LocalBtnInit: {ex.Message}"); }
            UpdateMusicModeButton();
            UpdateMusicPlayPauseButton();
            if (_musicNowLabel != null)
            {
                // 统一格式（FormatNowPlayingLabel），与 OnMusicCurrentChanged 同源，避免初始态文案漂移
                _musicNowLabel.Text = FormatNowPlayingLabel(_musicPlayer.Current);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SyncMusic: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // tab 激活 / 懒加载 / 目录填充
    // ═══════════════════════════════════════════

    /// <summary>
    /// 音乐 tab 激活（切 tab / OpenMusicTab / WZ 刷新后调用）：重估 WZ 灰占位与进度条三条件，
    /// 目录未建则触发懒加载（GetCatalogAsync 单飞，重复触发只复用同一在途任务）。
    /// </summary>
    private void OnMusicTabActivated()
    {
        try
        {
            UpdateMusicWzState();
            UpdateMusicProgressTimer();
            if (_wzService.IsLoaded && _musicCatalog.GetCatalogSnapshot() == null)
            {
                _ = LoadMusicCatalogAsync();
            }
            // 按需反查当前分类节点（切分类即查；磁盘缓存命中则零开销）
            EnsureTrackMapsForCurrentSelection();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicTabActivated: {ex.Message}"); }
    }

    /// <summary>
    /// 懒加载目录（首次约 2 秒）：加载中提示 → GetCatalogAsync → 填充分类/曲目列表 →
    /// RestoreLastTrackAsync 恢复上次曲目静默预选（服务保证不发 CurrentChanged——不触发播放不弹气泡）。
    /// 失败（WZ 未加载 / WZ 重载后目录过期等 InvalidOperationException）不崩溃不弹模态，状态栏提示。
    /// </summary>
    private async Task LoadMusicCatalogAsync()
    {
        try
        {
            ShowMusicLoading(true);
            ShowMusicStatus("加载目录中…（首次约 2 秒）", isError: false);
            var catalog = await _musicCatalog.GetCatalogAsync();
            ShowMusicLoading(false);
            RefreshMusicCategoryList();
            ShowMusicStatus($"目录就绪：{catalog.Count} 首曲目", isError: false);
            // 恢复预选：静默选中上次曲目（只选不播；滚动到可见）
            await _musicPlayer.RestoreLastTrackAsync();
            SelectRestoredMusicTrack();
            // 目录此刻才就绪的场景（含首次进入）：补触发当前分类的按需反查
            EnsureTrackMapsForCurrentSelection();
        }
        catch (Exception ex)
        {
            ShowMusicLoading(false);
            ShowMusicStatus($"目录加载失败: {ex.Message}", isError: true);
            UpdateMusicWzState();
        }
    }

    /// <summary>刷新目录按钮：WZ 未加载 → 灰占位 + 提示；已加载 → InvalidateCatalog + 重载。</summary>
    private void OnMusicRefreshClick()
    {
        try
        {
            if (!_wzService.IsLoaded)
            {
                UpdateMusicWzState();
                ShowMusicStatus("WZ 未加载，无法读取音乐", isError: true);
                return;
            }
            // WZ 数据本身未变，反向索引内存态与磁盘缓存保持有效；真 WZ 重载由服务层 ResetReverseIndex 清理
            _musicCatalog.InvalidateCatalog();
            _ = LoadMusicCatalogAsync();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicRefresh: {ex.Message}"); }
    }

    /// <summary>WZ 加载状态切换：主面板整体启用/禁用 + 灰占位显隐（不崩溃不弹错）。</summary>
    private void UpdateMusicWzState()
    {
        try
        {
            bool loaded = _wzService.IsLoaded;
            if (_musicMainPanel != null)
            {
                _musicMainPanel.IsEnabled = loaded;
                _musicMainPanel.IsVisible = loaded;
            }
            if (_musicWzPlaceholder != null)
            {
                _musicWzPlaceholder.IsVisible = !loaded;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicWzState: {ex.Message}"); }
    }

    /// <summary>加载中提示显隐（同时禁用刷新按钮防重复触发）。</summary>
    private void ShowMusicLoading(bool loading)
    {
        if (_musicLoadingHint != null)
        {
            _musicLoadingHint.IsVisible = loading;
        }
        if (_musicRefreshBtn != null)
        {
            _musicRefreshBtn.IsEnabled = !loading;
        }
    }

    /// <summary>填充分类列表并保持/修正当前选中分类（选中变化会联动刷新曲目列表）。</summary>
    private void RefreshMusicCategoryList()
    {
        if (_musicCategoryList == null)
        {
            return;
        }
        var categories = _musicCatalog.GetCategories();
        _musicCategoryList.ItemsSource = categories;
        if (categories.Count == 0)
        {
            _musicSelectedCategory = "";
            RefreshMusicTrackList();
            return;
        }
        if (_musicSelectedCategory.Length == 0 || !categories.Contains(_musicSelectedCategory))
        {
            _musicSelectedCategory = categories[0];
        }
        int idx = categories.IndexOf(_musicSelectedCategory);
        if (_musicCategoryList.SelectedIndex != idx)
        {
            // 触发 OnMusicCategoryChanged → RefreshMusicTrackList
            _musicCategoryList.SelectedIndex = idx;
        }
        else
        {
            RefreshMusicTrackList();
        }
    }

    /// <summary>
    /// 刷新右曲目列表：搜索关键字非空 → 全库搜索结果；否则当前分类曲目。
    /// 完成后选中并滚动到当前播放/恢复预选曲目（不触发播放）。
    /// </summary>
    private void RefreshMusicTrackList()
    {
        if (_musicTrackList == null)
        {
            return;
        }
        List<MusicTrack> tracks;
        var q = _musicSearchQuery.Trim();
        if (q.Length > 0)
        {
            tracks = _musicCatalog.Search(q);
        }
        else if (_musicSelectedCategory.Length > 0)
        {
            if (string.Equals(_musicSelectedCategory, MusicCatalogService.LocalCategoryName, StringComparison.Ordinal))
            {
                // 「📁 本地」伪分类：服务层契约——曲目 Img 字段为 LocalImgTag 占位标记
                tracks = _musicCatalog.Search("").Where(t => string.Equals(t.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal)).ToList();
            }
            else
            {
                string img = _musicSelectedCategory + ".img";
                tracks = _musicCatalog.Search("").Where(t => string.Equals(t.Img, img, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        else
        {
            tracks = new List<MusicTrack>();
        }
        _musicVisibleTracks = tracks;
        // 新实例强制模板重建（行内 ▶ 标记/高亮基于构建时比对 Current）
        _musicTrackList.ItemsSource = new List<MusicTrack>(tracks);
        FocusMusicTrackInList();
    }

    /// <summary>把列表选中态对齐到当前播放/恢复预选曲目并滚动到可见（不触发播放）。</summary>
    private void FocusMusicTrackInList()
    {
        if (_musicTrackList == null)
        {
            return;
        }
        var focus = _musicPlayer.Current ?? _musicPlayer.RestoredLastTrack;
        if (focus == null)
        {
            return;
        }
        var match = _musicVisibleTracks.FirstOrDefault(t => string.Equals(t.Key, focus.Key, StringComparison.Ordinal));
        if (match == null)
        {
            return;
        }
        _musicTrackList.SelectedItem = match;
        _musicTrackList.ScrollIntoView(match);
    }

    /// <summary>
    /// 恢复预选（只选不播）：优先当前播放曲目，其次 RestoredLastTrack；用户未在搜索态时
    /// 切到曲目所在分类，再选中并滚动到可见。
    /// </summary>
    private void SelectRestoredMusicTrack()
    {
        try
        {
            var track = _musicPlayer.Current ?? _musicPlayer.RestoredLastTrack;
            if (track == null)
            {
                return;
            }
            var q = _musicSearchQuery.Trim();
            if (q.Length == 0)
            {
                // 未在搜索态：切到曲目所在分类（Img 形如 "Bgm00.img" → 分类名去 .img 后缀）
                string category = track.Img.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? track.Img[..^4] : track.Img;
                _musicSelectedCategory = category;
                if (_musicCategoryList != null)
                {
                    for (int i = 0; i < _musicCategoryList.Items.Count; i++)
                    {
                        if (_musicCategoryList.Items[i] is string c && string.Equals(c, category, StringComparison.Ordinal))
                        {
                            if (_musicCategoryList.SelectedIndex != i)
                            {
                                // 触发 OnMusicCategoryChanged → RefreshMusicTrackList（内含选中+滚动）
                                _musicCategoryList.SelectedIndex = i;
                            }
                            else
                            {
                                RefreshMusicTrackList();
                            }
                            return;
                        }
                    }
                }
            }
            RefreshMusicTrackList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicRestore: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 交互事件
    // ═══════════════════════════════════════════

    /// <summary>分类选中变化：切换当前分类并刷新曲目列表。</summary>
    private void OnMusicCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_musicCategoryList?.SelectedItem is string cat)
            {
                _musicSelectedCategory = cat;
                RefreshMusicTrackList();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicCategory: {ex.Message}"); }
    }

    /// <summary>双击曲目行 = 播放（队列 = 当前可见列表快照）。</summary>
    private void OnMusicTrackDoubleTapped()
    {
        try
        {
            if (_musicTrackList?.SelectedItem is MusicTrack track)
            {
                PlayMusicFromUi(track);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicDbl: {ex.Message}"); }
    }

    /// <summary>联动地图 BGM 开关变化：回写 MusicPlayerService.SceneLinkEnabled（setter 立即持久化）。</summary>
    private void OnMusicSceneLinkChanged()
    {
        try
        {
            if (_musicSceneLinkToggle == null)
            {
                return;
            }
            bool on = _musicSceneLinkToggle.IsChecked == true;
            _musicPlayer.SceneLinkEnabled = on;
            ShowMusicStatus(on ? "已开启联动地图 BGM（切地图自动换歌）" : "已关闭联动地图 BGM", isError: false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicSceneLink: {ex.Message}"); }
    }

    /// <summary>⏯ 播放/暂停：Playing → 暂停；Paused → 继续；Stopped → 播放选中曲目（兜底当前/恢复预选曲目）。</summary>
    private void OnMusicPlayPauseClick()
    {
        try
        {
            if (_musicPlayer.State == PlaybackState.Playing)
            {
                RunMusicActionAsync(() => _musicPlayer.PauseAsync(), "暂停");
                return;
            }
            if (_musicPlayer.State == PlaybackState.Paused)
            {
                RunMusicActionAsync(() => _musicPlayer.ResumeAsync(), "继续");
                return;
            }
            MusicTrack? track = _musicTrackList?.SelectedItem as MusicTrack;
            if (track == null)
            {
                track = _musicPlayer.Current ?? _musicPlayer.RestoredLastTrack;
            }
            if (track == null)
            {
                ShowMusicStatus("请先在列表中选择曲目", isError: true);
                return;
            }
            PlayMusicFromUi(track);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicPlayPause: {ex.Message}"); }
    }

    /// <summary>模式按钮：循环切 Sequential→RepeatOne→Random 并立即持久化。</summary>
    private void CycleMusicPlayMode()
    {
        try
        {
            var next = _musicPlayer.Mode switch
            {
                PlaybackMode.Sequential => PlaybackMode.RepeatOne,
                PlaybackMode.RepeatOne => PlaybackMode.Random,
                _ => PlaybackMode.Sequential
            };
            _musicPlayer.SetPlayMode(next);
            UpdateMusicModeButton();
            ShowMusicStatus($"播放模式: {MusicModeLabel(next)}", isError: false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicMode: {ex.Message}"); }
    }

    /// <summary>音量滑块拖动 → SetVolume（服务内部 800ms 防抖持久化）。</summary>
    private void OnMusicVolumeSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_musicVolumeSyncing)
        {
            return;
        }
        try
        {
            int v = (int)e.NewValue;
            if (_musicVolumeLabel != null)
            {
                _musicVolumeLabel.Text = v.ToString();
            }
            _musicPlayer.SetVolume(v);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicVolume: {ex.Message}"); }
    }

    /// <summary>进度条点击 → 按点击位置比例换算目标时间 → SeekAsync（服务内部 clamp 到 [0,Duration)）。</summary>
    private void OnMusicProgressClick(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (_musicProgressTrack == null)
            {
                return;
            }
            var dur = QueryMusicDuration(_musicPlayer);
            if (!dur.HasValue || dur.Value <= TimeSpan.Zero)
            {
                return;
            }
            double width = _musicProgressTrack.Bounds.Width;
            if (width <= 0)
            {
                return;
            }
            double ratio = e.GetCurrentPoint(_musicProgressTrack).Position.X / width;
            if (ratio < 0)
            {
                ratio = 0;
            }
            if (ratio > 1)
            {
                ratio = 1;
            }
            var target = TimeSpan.FromMilliseconds(ratio * dur.Value.TotalMilliseconds);
            RunMusicActionAsync(() => _musicPlayer.SeekAsync(target), "进度跳转");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicSeek: {ex.Message}"); }
    }

    /// <summary>从 UI 播放指定曲目：队列 = 当前可见列表快照（曲目不在其中时退化为单元素队列）。
    /// 状态栏「正在播放」不在此写（点播值会随后续自动切歌/随机/联动换曲陈旧），统一由 OnMusicCurrentChanged 驱动。</summary>
    private void PlayMusicFromUi(MusicTrack track)
    {
        var queue = new List<MusicTrack>(_musicVisibleTracks);
        bool inList = queue.Any(t => string.Equals(t.Key, track.Key, StringComparison.Ordinal));
        if (!inList)
        {
            queue = new List<MusicTrack> { track };
        }
        RunMusicActionAsync(() => _musicPlayer.PlayAsync(track, queue), "播放");
    }

    /// <summary>UI 触发的异步播放操作统一包装：异常必捕获（业务失败走服务 PlaybackError 事件，这里只兜意外异常）。</summary>
    private async void RunMusicActionAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsWindow] {what}: {ex.Message}");
            ShowMusicStatus($"{what}失败: {ex.Message}", isError: true);
        }
    }

    // ═══════════════════════════════════════════
    // 服务事件 → UI（全部已在 InitMusic 经 Dispatcher.UIThread.Post 派发）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 当前曲目变化（唯一换曲入口：点播 / 自动切歌 / 随机跨节点 / 场景联动全走这里）：
    /// 曲名标签（带 BGM 节点前缀）+ 无条件重建列表使行模板 ▶ 标记按最新 Current 重画 +
    /// 选中并滚动到新曲 + 状态栏统一写「正在播放」+ 进度条三条件重估 + 按需反查地图。
    /// </summary>
    private void OnMusicCurrentChanged(MusicTrack? track)
    {
        try
        {
            if (_musicNowLabel != null)
            {
                // 带所属 BGM 节点前缀（"▶ BgmJp4_Zipang · Kinesis Theme I"），与列表 ▶ 标记同源于 Current
                _musicNowLabel.Text = FormatNowPlayingLabel(track);
            }
            // ▶ 标记唯一真相源 = _musicPlayer.Current：行模板在构建时比对 Current，必须重建列表才重画；
            // 先无条件重建（覆盖新曲不在可见列表时 HighlightMusicTrackInList 提前返回的陈旧盲区）
            RebuildMusicListKeepSelection();
            // 再选中新曲并滚动到可见（滚动必须放在重建之后，否则会被替换 ItemsSource 冲掉）
            HighlightMusicTrackInList();
            // 状态栏唯一真相源：所有换曲路径统一经此处写「正在播放」（点播处的旧写入已删除，防陈旧）
            if (track != null)
            {
                var node = FormatBgmNode(track);
                ShowMusicStatus(node == null
                    ? $"正在播放: {track.Display}"
                    : $"正在播放: {node} · {track.Display}", isError: false);
            }
            UpdateMusicProgressTimer();
            // 切歌（含场景联动换曲）顺带反查该曲所属节点：磁盘缓存命中零开销，未命中后台补扫
            if (track != null && !string.Equals(track.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal))
            {
                _musicCatalog.KickTrackMapsForImg(track.Img);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicCurrent: {ex.Message}"); }
    }

    /// <summary>播放状态变化：⏯ 文案切换 + 进度条三条件重估（Stopped 时进度归零）。</summary>
    private void OnMusicStateChanged(PlaybackState state)
    {
        try
        {
            UpdateMusicPlayPauseButton();
            if (state == PlaybackState.Stopped)
            {
                SetMusicProgress(null, null);
            }
            UpdateMusicProgressTimer();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicState: {ex.Message}"); }
    }

    /// <summary>音量变化（服务事件）：防回环地同步滑块与数值。</summary>
    private void OnMusicVolumeChanged(int volume)
    {
        try
        {
            _musicVolumeSyncing = true;
            if (_musicVolumeSlider != null)
            {
                _musicVolumeSlider.Value = volume;
            }
            if (_musicVolumeLabel != null)
            {
                _musicVolumeLabel.Text = volume.ToString();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicVolumeEvt: {ex.Message}"); }
        finally
        {
            _musicVolumeSyncing = false;
        }
    }

    /// <summary>
    /// 当前播放行选中 + 滚动到可见：比对基准只认 _musicPlayer.Current?.Key（无第二真相源）；
    /// 行模板 ▶ 标记/高亮的重画由 RebuildMusicListKeepSelection 统一重建负责（本方法在其后调用），
    /// 新曲不在可见列表时静默返回（无行可选，不滚动）。
    /// </summary>
    private void HighlightMusicTrackInList()
    {
        if (_musicTrackList == null)
        {
            return;
        }
        var cur = _musicPlayer.Current;
        if (cur == null)
        {
            return;
        }
        var match = _musicVisibleTracks.FirstOrDefault(t => string.Equals(t.Key, cur.Key, StringComparison.Ordinal));
        if (match == null)
        {
            return;
        }
        _musicTrackList.SelectedItem = match;
        _musicTrackList.ScrollIntoView(match);
    }

    /// <summary>状态栏小文本：isError 用琥珀色（同 WZ 配置弹窗提示色），普通提示用暗灰。</summary>
    private void ShowMusicStatus(string message, bool isError)
    {
        if (_musicStatusText == null)
        {
            return;
        }
        _musicStatusText.Text = message;
        _musicStatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromArgb(255, 230, 196, 106))
            : BrushTxtDim;
    }

    // ═══════════════════════════════════════════
    // 进度条三条件（PLAN Rev 5/6）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 进度条三条件评估：窗口可见 && 音乐 tab 选中 && 播放器非 Stopped 同时成立才运行轮询定时器，
    /// 任一丢失即停（窗口 Hide 不关——IsVisible 变化事件兜底；切走 tab 由 SwitchMainTab 兜底）。
    /// </summary>
    private void UpdateMusicProgressTimer()
    {
        try
        {
            bool shouldRun = IsVisible
                             && _activeMainTab == MusicTabIndex
                             && _musicPlayer.State != PlaybackState.Stopped;
            if (shouldRun && !_musicProgressTimer.IsEnabled)
            {
                _musicProgressTimer.Start();
            }
            else if (!shouldRun && _musicProgressTimer.IsEnabled)
            {
                _musicProgressTimer.Stop();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicTimerState: {ex.Message}"); }
    }

    /// <summary>进度条 tick：复核三条件（防事件遗漏导致空转）后读 Position/Duration 更新进度条与时间文本。</summary>
    private void OnMusicProgressTick(object? sender, EventArgs e)
    {
        try
        {
            if (!IsVisible || _activeMainTab != MusicTabIndex || _musicPlayer.State == PlaybackState.Stopped)
            {
                _musicProgressTimer.Stop();
                return;
            }
            SetMusicProgress(QueryMusicPosition(_musicPlayer), QueryMusicDuration(_musicPlayer));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MusicProgressTick: {ex.Message}"); }
    }

    /// <summary>按 Position/Duration 更新进度条填充宽度与时间文本（Duration 缺失时归零显示 --:--）。</summary>
    private void SetMusicProgress(TimeSpan? position, TimeSpan? duration)
    {
        if (_musicProgressTrack == null || _musicProgressFill == null || _musicTimeText == null)
        {
            return;
        }
        double ratio = 0;
        if (duration.HasValue && duration.Value > TimeSpan.Zero && position.HasValue)
        {
            ratio = position.Value.TotalMilliseconds / duration.Value.TotalMilliseconds;
            if (ratio < 0)
            {
                ratio = 0;
            }
            if (ratio > 1)
            {
                ratio = 1;
            }
        }
        double trackWidth = _musicProgressTrack.Bounds.Width;
        _musicProgressFill.Width = trackWidth > 0 ? ratio * trackWidth : 0;
        _musicTimeText.Text = $"{FormatMusicTime(position)} / {FormatMusicTime(duration)}";
    }

    /// <summary>时间格式化：mm:ss；null 显示 --:--（BGM 时长普遍在分钟级）。</summary>
    private static string FormatMusicTime(TimeSpan? t)
    {
        return t.HasValue ? t.Value.ToString(@"mm\:ss") : "--:--";
    }

    /// <summary>
    /// 曲目所属 BGM 节点显示名：Img 去 .img 后缀（"BgmJp4_Zipang.img" → "BgmJp4_Zipang"）；
    /// 本地曲目（Img 为 __LOCAL__ 占位，无节点概念）返回 null。
    /// </summary>
    private static string? FormatBgmNode(MusicTrack track)
    {
        if (string.Equals(track.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal))
        {
            return null;
        }
        return track.Img.Replace(".img", "");
    }

    /// <summary>
    /// 进度行曲名标签统一格式（唯一真相源 = _musicPlayer.Current，经 CurrentChanged / 初始同步驱动）：
    /// WZ 曲目 "▶ 节点名 · 曲名"，本地曲目 "▶ 曲名"，无当前曲目 "未播放"。
    /// </summary>
    private static string FormatNowPlayingLabel(MusicTrack? track)
    {
        if (track == null)
        {
            return "未播放";
        }
        var node = FormatBgmNode(track);
        if (node == null)
        {
            return $"▶ {track.Track}";
        }
        return $"▶ {node} · {track.Track}";
    }

    // ═══════════════════════════════════════════
    // Position/Duration 查询（服务已提供 Position/Duration 转发属性，直接访问）
    // ═══════════════════════════════════════════

    /// <summary>读当前播放位置（无活动流为 null）。</summary>
    private static TimeSpan? QueryMusicPosition(MusicPlayerService player)
    {
        return player?.Position;
    }

    /// <summary>读当前曲目总时长（无活动流为 null）。</summary>
    private static TimeSpan? QueryMusicDuration(MusicPlayerService player)
    {
        return player?.Duration;
    }

    /// <summary>模式按钮文案。</summary>
    private static string MusicModeLabel(PlaybackMode mode)
    {
        return mode switch
        {
            PlaybackMode.RepeatOne => "🔂 单曲循环",
            PlaybackMode.Random => "🔀 随机",
            _ => "🔁 顺序"
        };
    }

    /// <summary>更新模式按钮显示当前模式。</summary>
    private void UpdateMusicModeButton()
    {
        if (_musicModeBtn != null)
        {
            _musicModeBtn.Content = MusicModeLabel(_musicPlayer.Mode);
        }
    }

    /// <summary>⏯ 按钮内容随状态切换：Playing 显示 ⏸（可暂停），其余显示 ▶（可播放/继续）。</summary>
    private void UpdateMusicPlayPauseButton()
    {
        if (_musicPlayPauseBtn != null)
        {
            _musicPlayPauseBtn.Content = _musicPlayer.State == PlaybackState.Playing ? "⏸️" : "▶";
        }
    }

    // ═══════════════════════════════════════════
    // 反向地图展示（V0.2.1 增量⑤，2026-08-27 重写）—— 服务层新口径：
    //   KickTrackMapsForImg(imgName)（按节点懒查，打开 tab 不触发全量扫描——修卡死）
    //   TryGetTrackMaps(trackKey, out items ≤5 名图, out moreCount)（同步直读）
    //   event ReverseIndexChanged（扫描线程触发，本类在 InitMusic 里 Post 重建列表）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 按需反查当前选中分类对应的 BgmXX 节点：切分类/目录就绪时调用，fire-and-forget。
    /// 磁盘缓存命中时服务层立即返回（零开销）；未命中启动后台增量扫描，
    /// 每个分组收尾经 ReverseIndexChanged → RebuildMusicListKeepSelection 渐次浮现副行。
    /// </summary>
    private void EnsureTrackMapsForCurrentSelection()
    {
        try
        {
            if (!_wzService.IsLoaded || string.IsNullOrEmpty(_musicSelectedCategory)
                || string.Equals(_musicSelectedCategory, MusicCatalogService.LocalCategoryName, StringComparison.Ordinal))
            {
                return;
            }
            _musicCatalog.KickTrackMapsForImg(_musicSelectedCategory + ".img");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] MapEnsure: {ex.Message}"); }
    }

    /// <summary>反向索引数据到位后重建列表模板使副行浮现：选中项尽量保持（不滚动、不触发播放）。</summary>
    private void RebuildMusicListKeepSelection()
    {
        if (_musicTrackList == null)
        {
            return;
        }
        var selected = _musicTrackList.SelectedItem as MusicTrack;
        // 新实例强制模板重建（同 HighlightMusicTrackInList 的既有惯例）
        _musicTrackList.ItemsSource = new List<MusicTrack>(_musicVisibleTracks);
        if (selected != null)
        {
            var match = _musicVisibleTracks.FirstOrDefault(t => string.Equals(t.Key, selected.Key, StringComparison.Ordinal));
            if (match != null)
            {
                _musicTrackList.SelectedItem = match;
            }
        }
    }

    /// <summary>关联地图短行（副行）：切片已按城镇优先排序，取前 3 条，格式「🗺 名A · 村庄 名B · 名C …下略」；无关联返回 null（副行整行隐藏）。</summary>
    private static string? FormatTrackMapLine(IReadOnlyList<TrackMapRef> maps, int moreCount)
    {
        if (maps.Count == 0)
        {
            return null;
        }
        var parts = new List<string>();
        for (int i = 0; i < maps.Count && parts.Count < 3; i++)
        {
            parts.Add(DescribeTrackMap(maps[i]));
        }
        string text = "🗺 " + string.Join(" · ", parts);
        if (moreCount > 0)
        {
            text += " …下略";
        }
        return text;
    }

    /// <summary>tooltip 文本（标题 + 每条一行，已按城镇优先排序；moreCount > 0 时末行「下略」口径）；无关联返回 null。</summary>
    private static string? BuildTrackMapTooltip(IReadOnlyList<TrackMapRef> maps, int moreCount)
    {
        if (maps.Count == 0)
        {
            return null;
        }
        var lines = new List<string>
        {
            moreCount > 0
                ? $"🗺 关联地图：收录有名字的前 {maps.Count} 张（其余 {moreCount} 张略）"
                : $"🗺 关联地图 {maps.Count} 张"
        };
        foreach (var m in maps)
        {
            lines.Add(DescribeTrackMap(m));
        }
        if (moreCount > 0)
        {
            lines.Add($"……其余 {moreCount} 张，下略");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>条目描述（副行用）：城镇加「村庄 」前缀 + 显示名。</summary>
    private static string DescribeTrackMap(TrackMapRef entry)
    {
        return (entry.IsTown ? "村庄 " : "") + TrackMapDisplayName(entry);
    }

    /// <summary>条目显示名：MapName 非空白优先，否则回退 mapId。</summary>
    private static string TrackMapDisplayName(TrackMapRef entry)
    {
        return string.IsNullOrWhiteSpace(entry.MapName) ? entry.MapId : entry.MapName;
    }

    // ═══════════════════════════════════════════
    // 本地目录入口（V0.2.1 增量④ UI）—— 依赖 B1 新 API（待落盘 TODO）：SetLocalRootAsync(path)
    // ═══════════════════════════════════════════

    /// <summary>
    /// 「📁 本地」点击：OpenFolderPickerAsync 选目录（用法照抄设置中心 WZ 配置的 StorageProvider 模式）→
    /// SetLocalRootAsync(path)（扫描本地库并自行失效合成目录）→ LocalMusicPath 持久化（B1 注释明示
    /// 由 UI/上层写入 MusicConfig）→ LoadMusicCatalogAsync 重建分类/曲目。再次点击即换目录。
    /// </summary>
    private async void OnMusicLocalFolderClick()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择本地音乐目录",
                AllowMultiple = false
            });
            if (folders.Count == 0)
            {
                return;
            }
            string path = folders[0].Path.LocalPath;
            ShowMusicStatus($"已选择本地目录: {path}，正在扫描…", isError: false);
            await _musicCatalog.SetLocalRootAsync(path);
            // 路径持久化：ConfigService.Update 同一事务写 Music 子对象（数据层只存不引配置依赖）
            _config.Update(s => { s.Music ??= new MusicConfig(); s.Music.LocalMusicPath = path; });
            SetLocalBtnState(true, path);
            // SetLocalRootAsync 内部已 InvalidateCatalog：直接重载拉起「WZ + 本地」合成视图
            _ = LoadMusicCatalogAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsWindow] MusicLocal: {ex.Message}");
            ShowMusicStatus($"本地目录设置失败: {ex.Message}", isError: true);
        }
    }

    /// <summary>「📁 本地」按钮反馈：启用态改文案提示当前目录名（tooltip 始终为完整路径），未启用恢复默认。</summary>
    private void SetLocalBtnState(bool enabled, string? path)
    {
        if (_musicLocalBtn == null)
        {
            return;
        }
        if (enabled && !string.IsNullOrEmpty(path))
        {
            _musicLocalBtn.Content = "📁 本地✓";
            ToolTip.SetTip(_musicLocalBtn, path);
        }
        else
        {
            _musicLocalBtn.Content = "📁 本地";
            ToolTip.SetTip(_musicLocalBtn, "选择本地音乐目录（再次点击可更换）");
        }
    }
}
