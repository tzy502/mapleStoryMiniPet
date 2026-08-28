using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// 音乐迷你控制条窗口（V0.2.1 增量②）：无边框置顶透明小条，横向 [⏮][⏯][⏭] + 超短曲名
/// （hover tooltip 显示完整曲名），水平跟随桌宠显示。
/// 窗口形态学 BalloonWindow / BackgroundWindow 先例：WindowDecorations.None +
/// TransparencyLevelHint.Transparent + Topmost + ShowInTaskbar=false；
/// 与 BalloonWindow 的两点差异：① 不做点击穿透（按钮必须可点）；② ShowActivated=false（HUD 不抢焦点）。
/// 显隐规则（2026-08-27 用户改悬浮式）：MusicConfig.MiniBarEnabled && Current != null &&
/// State != Stopped && **鼠标悬停在桌宠上或本控制条上** → Show，否则 Hide。
/// 输入源：StateChanged / CurrentChanged / EventBus「music:minibar-changed」/ pet:mouseenter、
/// pet:mouseleave（桌宠侧 PointerEntered/Exited 发布）+ 本窗口自身指针进出五路，任一变化即重评。
/// 定位：跟随宠物**脚底中心**（EventBus「pet:moved」，payload = ValueTuple&lt;double,double&gt;(X,Y)
/// 桌面像素坐标，2026-08-27 修订为脚底口径——原发窗口左上角致控制条叠在角色头部；发布方 10Hz 节流），
/// 条顶 = 脚底 + AnchorOffsetYPx 下移，水平以脚底为中心；无 pet:moved 历史时兜底放主屏中下。
/// 跨窗消息通道（agent-B3 定义，TrayService.MiniPetEvents）：现有 EventBus 为 EventType 枚举制，
/// 字符串契约 key 以未占用数值段强转扩展——"pet:moved"→PetMoved(100)、
/// "music:minibar-changed"→MiniBarChanged(101)；payload 做容错解析（实际形态以运行时为准）。
/// 生命周期：App DI 单例，构造即挂订阅；显隐自评驱动 Show/Hide；应用退出 Environment.Exit 硬退无需清理。
/// </summary>
public class MusicMiniBarWindow : Window
{
    // ====== 常量 ======
    /// <summary>小条宽度（约 150x34 口径，含 3 按钮 + 短曲名区域）。</summary>
    private const int MiniBarWidth = 150;
    /// <summary>小条高度。</summary>
    private const int MiniBarHeight = 34;
    /// <summary>条顶相对宠物锚点的下移像素（⚠️ 可调：正=脚下下方，负=头顶上方；默认 10 让开气泡）。</summary>
    private const int AnchorOffsetYPx = 10;
    /// <summary>兜底位置：主屏工作区高度的比例点（0.78 ≈ 屏幕中偏下）。</summary>
    private const double FallbackScreenYRatio = 0.78;

    // ====== 悬停显隐状态 ======
    /// <summary>鼠标是否悬停在桌宠窗口上（pet:mouseenter/leave 驱动；UI 线程读写）。</summary>
    private bool _petHover;
    /// <summary>鼠标是否驻留在本控制条上（防「移过去路上就消失」，OnPointer* 驱动；UI 线程读写）。</summary>
    private bool _barHover;

    /// <summary>设置中心同款配色（暗底半透明 + 暗字，透明桌面上可读且不喧宾夺主）。</summary>
    private static readonly IBrush BarBg = new SolidColorBrush(Color.FromArgb(216, 30, 30, 38));
    private static readonly IBrush BarBorder = new SolidColorBrush(Color.FromArgb(255, 60, 60, 70));
    private static readonly IBrush BtnBg = new SolidColorBrush(Color.FromArgb(180, 30, 30, 36));
    private static readonly IBrush TxtColor = new SolidColorBrush(Color.FromArgb(255, 160, 160, 176));
    private static readonly IBrush TxtDimColor = new SolidColorBrush(Color.FromArgb(255, 112, 112, 128));

    // ── 服务 ──
    private readonly MusicPlayerService _player;
    private readonly ConfigService _config;
    private readonly EventBus _events;

    // ── UI 引用 ──
    /// <summary>⏯ 按钮（内容随 State 在 ⏸️/▶ 切换，与设置中心同字形口径）。</summary>
    private Button? _playPauseBtn;
    /// <summary>超短曲名（宽度受限裁剪，hover tooltip 完整名）。</summary>
    private TextBlock? _titleText;

    // ── 定位状态 ──
    /// <summary>最近一次 pet:moved 的宠物锚点屏幕坐标；未收到过为 false（Show 时走屏幕中下兜底）。</summary>
    private bool _hasAnchor;
    private double _anchorX;
    private double _anchorY;

    public MusicMiniBarWindow(MusicPlayerService player, EventBus events, ConfigService config)
    {
        _player = player;
        _events = events;
        _config = config;

        // 窗口形态：置顶 / 无边框 / 透明 / 不进任务栏 / 不抢焦点 / 不可缩放（学 BackgroundWindow:65-71）
        Title = "MiniPet 迷你控制条";
        Width = MiniBarWidth;
        Height = MiniBarHeight;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        Content = BuildBarContent();

        // 服务事件三路之一/之二：状态与当前曲目变化 → 重评显隐 + 刷新 ⏯ 文案与曲名（可能后台线程，Post 回 UI）
        _player.StateChanged += state => Dispatcher.UIThread.Post(() =>
        {
            UpdatePlayPauseText();
            EvaluateVisibility();
        });
        _player.CurrentChanged += track => Dispatcher.UIThread.Post(() =>
        {
            UpdateTitle(track);
            EvaluateVisibility();
        });

        // 服务事件之三：B3 右键菜单开关写入 MusicConfig.MiniBarEnabled 后发布的广播 → 重评显隐
        // （发布在写入之后，此处读到的是最新值；payload bool 仅作日志用）
        _events.Subscribe(MiniPetEvents.MiniBarChanged, data => Dispatcher.UIThread.Post(EvaluateVisibility));

        // 宠物移动跟随：payload 锚点坐标容错解析后存基准并立即重定位（可见才移动，藏着的等 Show 时应用）
        _events.Subscribe(MiniPetEvents.PetMoved, data => Dispatcher.UIThread.Post(() => OnPetMoved(data)));

        // 悬停显隐（悬浮式）：桌宠侧指针进出经 EventBus 转达；本条自身的驻留由 OnPointer* 重载记账
        _events.Subscribe(MiniPetEvents.PetMouseEnter, _ => Dispatcher.UIThread.Post(() =>
        {
            _petHover = true;
            EvaluateVisibility();
        }));
        _events.Subscribe(MiniPetEvents.PetMouseLeave, _ => Dispatcher.UIThread.Post(() =>
        {
            _petHover = false;
            EvaluateVisibility();
        }));

        // 初次构造即评估一次（恢复配置开着且上次曲目已预选时会直接现身在兜底位）
        Dispatcher.UIThread.Post(() =>
        {
            UpdatePlayPauseText();
            UpdateTitle(_player.Current);
            EvaluateVisibility();
        });
    }

    // ====== private ======

    /// <summary>构建小条内容：半透明圆角横排容器 [⏮][⏯][⏭] + 曲名（剩余宽度裁剪）。</summary>
    private Control BuildBarContent()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(MakeBarButton("⏮️", () => RunPlayerActionAsync(() => _player.PrevAsync(), "上一首")));
        _playPauseBtn = MakeBarButton("▶", OnPlayPauseClick);
        row.Children.Add(_playPauseBtn);
        row.Children.Add(MakeBarButton("⏭️", () => RunPlayerActionAsync(() => _player.NextAsync(), "下一首")));

        _titleText = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Foreground = TxtDimColor,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 40,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 0, 0)
        };
        row.Children.Add(_titleText);

        return new Border
        {
            Background = BarBg,
            BorderBrush = BarBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Width = MiniBarWidth - 8,
            Height = MiniBarHeight - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = row
        };
    }

    /// <summary>小按钮统一样式（复用 UiStyle hover；异常兜底同 MakeMusicTransportButton 模式）。</summary>
    private Button MakeBarButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(4, 2),
            Background = BtnBg,
            Foreground = TxtColor,
            BorderBrush = BarBorder,
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
            catch (Exception ex) { Console.Error.WriteLine($"[MiniBar] Btn {text}: {ex.Message}"); }
        };
        return btn;
    }

    /// <summary>⏯ 三态逻辑与设置中心一致：Playing→暂停、Paused→继续、Stopped→播放恢复曲目（无则忽略不提示）。</summary>
    private void OnPlayPauseClick()
    {
        if (_player.State == PlaybackState.Playing)
        {
            RunPlayerActionAsync(() => _player.PauseAsync(), "暂停");
            return;
        }
        if (_player.State == PlaybackState.Paused)
        {
            RunPlayerActionAsync(() => _player.ResumeAsync(), "继续");
            return;
        }
        var track = _player.Current ?? _player.RestoredLastTrack;
        if (track == null)
        {
            return;
        }
        var queue = new System.Collections.Generic.List<MusicTrack> { track };
        RunPlayerActionAsync(() => _player.PlayAsync(track, queue), "播放");
    }

    /// <summary>播放操作的统一异步包装：异常必捕获只打 stderr（迷你条无处展示错误串，音乐 tab 有）。</summary>
    private async void RunPlayerActionAsync(Func<System.Threading.Tasks.Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MiniBar] {what}: {ex.Message}");
        }
    }

    /// <summary>⏯ 文案随 State 切换（字形与设置中心一致：▶ / ⏸️ / ⏮️ / ⏭️）。</summary>
    private void UpdatePlayPauseText()
    {
        if (_playPauseBtn != null)
        {
            _playPauseBtn.Content = _player.State == PlaybackState.Playing ? "⏸️" : "▶";
        }
    }

    /// <summary>短曲名 + 完整名 tooltip（CurrentChanged 驱动）。</summary>
    private void UpdateTitle(MusicTrack? track)
    {
        if (_titleText == null)
        {
            return;
        }
        _titleText.Text = track?.Track ?? "";
        ToolTip.SetTip(_titleText, track == null ? null : $"{track.Track}（{track.Img}）");
    }

    /// <summary>
    /// 显隐评估（核心规则）：MiniBarEnabled && Current != null && State != Stopped → Show，否则 Hide。
    /// music:minibar-changed 到达若早于 B3 写盘完成会读到旧值——下次事件自然校正，可接受（尽力而为语义）。
    /// </summary>
    private void EvaluateVisibility()
    {
        try
        {
            bool enabled = ReadMiniBarEnabled();
            // 悬浮式：仅播放中且指针在桌宠或本条上才现身（条上驻留保证移过去路上不消失）
            bool shouldShow = enabled && _player.Current != null
                && _player.State != PlaybackState.Stopped
                && (_petHover || _barHover);
            if (shouldShow)
            {
                ApplyTargetPosition();
                if (!IsVisible)
                {
                    Show();
                }
            }
            else
            {
                if (IsVisible)
                {
                    Hide();
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MiniBar] Visibility: {ex.Message}"); }
    }

    /// <summary>读 MusicConfig.MiniBarEnabled（默认 true 兜底）。</summary>
    private bool ReadMiniBarEnabled()
    {
        try
        {
            return _config.LoadSettings().Music?.MiniBarEnabled ?? true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MiniBar] 读 MiniBarEnabled 失败用默认 true: {ex.Message}");
            return true;
        }
    }

    /// <summary>宠物移动：解析锚点 → 存基准 → 可见时跟随（不可见只记账，Show 时再应用）。</summary>
    /// <summary>指针进入本条：记驻留并重评显隐（离开桌宠后经由条的路径不至于立刻消失）。</summary>
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _barHover = true;
        EvaluateVisibility();
        base.OnPointerEntered(e);
    }

    /// <summary>指针离开本条：清除驻留并重评显隐。</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        _barHover = false;
        EvaluateVisibility();
        base.OnPointerExited(e);
    }

    private void OnPetMoved(object? payload)
    {
        try
        {
            var anchor = ParseAnchor(payload);
            if (!anchor.HasValue)
            {
                return;
            }
            _hasAnchor = true;
            _anchorX = anchor.Value.x;
            _anchorY = anchor.Value.y;
            if (IsVisible)
            {
                ApplyTargetPosition();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MiniBar] PetMoved: {ex.Message}"); }
    }

    /// <summary>
    /// 应用目标位置：有锚点 → 条中心对齐锚点 X、条顶 = 锚点 Y + AnchorOffsetYPx（脚下下方，
    /// 头顶让给 BalloonWindow 气泡）；无 pet:moved 历史 → 主屏工作区中下兜底。均 clamp 进屏内。
    /// </summary>
    private void ApplyTargetPosition()
    {
        double x;
        double y;
        var screen = Screens.Primary;
        if (_hasAnchor)
        {
            x = _anchorX - Width / 2;
            y = _anchorY + AnchorOffsetYPx;
            // 优先用包含锚点的屏幕做 clamp（多显示器下避免被拉回主屏）
            try
            {
                var hit = Screens.ScreenFromPoint(new PixelPoint((int)_anchorX, (int)_anchorY));
                if (hit != null)
                {
                    screen = hit;
                }
            }
            catch (Exception) { /* ScreenFromPoint 不可用时退回 Primary */ }
        }
        else
        {
            if (screen == null)
            {
                return;
            }
            x = screen.WorkingArea.X + (screen.WorkingArea.Width - Width) / 2;
            y = screen.WorkingArea.Y + screen.WorkingArea.Height * FallbackScreenYRatio;
        }
        if (screen == null)
        {
            return;
        }
        // clamp 进屏（学 BackgroundWindow.ClampToScreen 口径）
        double maxX = screen.Bounds.X + screen.Bounds.Width - Width;
        double maxY = screen.Bounds.Y + screen.Bounds.Height - Height;
        if (maxX < screen.Bounds.X)
        {
            maxX = screen.Bounds.X;
        }
        if (maxY < screen.Bounds.Y)
        {
            maxY = screen.Bounds.Y;
        }
        if (x < screen.Bounds.X)
        {
            x = screen.Bounds.X;
        }
        if (x > maxX)
        {
            x = maxX;
        }
        if (y < screen.Bounds.Y)
        {
            y = screen.Bounds.Y;
        }
        if (y > maxY)
        {
            y = maxY;
        }
        Position = new PixelPoint((int)x, (int)y);
    }

    // ═══════════════════════════════════════════
    // pet:moved payload 解析（契约：ValueTuple<double,double>(X,Y) 桌面像素坐标；容错兼容其他形态）
    // ═══════════════════════════════════════════

    /// <summary>
    /// pet:moved payload 容错解析为 (x, y)：主路径 = 契约的 ValueTuple&lt;double,double&gt;(X,Y)；
    /// 兼容 PixelPoint / Point / "x,y" 字符串 / 带 X+Y 数值成员的任意对象；解析不出返回 null。
    /// </summary>
    private static (double x, double y)? ParseAnchor(object? payload)
    {
        try
        {
            if (payload is ValueTuple<double, double> vt)
            {
                return (vt.Item1, vt.Item2);
            }
            if (payload is PixelPoint pp)
            {
                return (pp.X, pp.Y);
            }
            if (payload is Point pt)
            {
                return (pt.X, pt.Y);
            }
            if (payload is string s)
            {
                var parts = s.Split(',');
                if (parts.Length >= 2
                    && double.TryParse(parts[0].Trim(), out double sx)
                    && double.TryParse(parts[1].Trim(), out double sy))
                {
                    return (sx, sy);
                }
                return null;
            }
            if (payload != null)
            {
                double ax = ReadNumericMember(payload, "X") ?? ReadNumericMember(payload, "Item1") ?? double.NaN;
                double ay = ReadNumericMember(payload, "Y") ?? ReadNumericMember(payload, "Item2") ?? double.NaN;
                if (!double.IsNaN(ax) && !double.IsNaN(ay))
                {
                    return (ax, ay);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MiniBar] ParseAnchor: {ex.Message}");
        }
        return null;
    }

    /// <summary>读对象上名为 propName 的数值成员（属性优先，字段兜底），支持 int/long/double/float；不存在返回 null。</summary>
    private static double? ReadNumericMember(object obj, string propName)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        object? raw = prop?.GetValue(obj);
        if (raw == null)
        {
            var field = type.GetField(propName, BindingFlags.Public | BindingFlags.Instance);
            raw = field?.GetValue(obj);
        }
        if (raw is int i)
        {
            return i;
        }
        if (raw is long l)
        {
            return l;
        }
        if (raw is float f)
        {
            return f;
        }
        if (raw is double d)
        {
            return d;
        }
        return null;
    }
}
