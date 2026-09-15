using System;
using System.Collections.Generic;
using System.IO;

using System.Threading.Tasks;

using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services;

/// <summary>
/// V0.2.1 音乐增量跨窗口运行时契约事件（agent-B3 定义）：
/// 现有 EventBus 以 EventType 枚举为通道（枚举本体不在本增量修改范围），此处以未占用数值段
/// （现有成员至 11，从 100 起）强转扩展，与协调者下发的字符串契约 key 一一对应：
/// - "pet:moved"             → PetMoved       payload = ValueTuple&lt;double,double&gt;(X,Y) 桌面像素坐标（10Hz 节流）
/// - "music:minibar-changed" → MiniBarChanged payload = bool（MiniBarEnabled 最新值）
/// </summary>
public static class MiniPetEvents
{
    /// <summary>宠物桌面位置变化（payload 见类注释；发布方已节流，订阅方自行做 UI 线程派发）。</summary>
    public const EventType PetMoved = (EventType)100;

    /// <summary>迷你控制条开关变化（payload = bool 开关最新值）。</summary>
    public const EventType MiniBarChanged = (EventType)101;

    /// <summary>"pet:mouseenter"：鼠标进入桌宠窗口（V0.2.1 迷你控制条悬停显隐用，无 payload 语义）。</summary>
    public const EventType PetMouseEnter = (EventType)102;

    /// <summary>"pet:mouseleave"：鼠标离开桌宠窗口。</summary>
    public const EventType PetMouseLeave = (EventType)103;
}

public class TrayService
{
    private TrayIcon? _trayIcon;
    // V0.1.3 多桌宠：登记全部桌宠窗口，托盘动作（显示/隐藏/居中）作用于全部
    // （旧版单槽 _mainWindow 只认最后创建的那只 → 第 2 只出现后"显示主窗口"永远只动一只）。
    private readonly List<Window> _pets = new();
    /// <summary>音乐分组是否已就地重建过（V0.1.3 多桌宠：每只桌宠构造都会调 SetMusicService，
    /// 反复重建托盘菜单会触发 Avalonia「The menu being updated does not match」）。</summary>
    private bool _musicMenuRebuilt;
    private readonly object _petsLock = new();
    private readonly EventBus _eventBus;
    private string _tooltip = "MiniPet 桌宠";
    // 音乐模块 V0.2.1：托盘音乐快捷分组依赖的播放编排服务（由 PetWindow 构造经 SetMusicService 注入，
    // PetWindow 同时持有 TrayService 与 MusicPlayerService 引用，TrayService 构造签名保持不变，无需动 App 注册）
    private MusicPlayerService? _musicService;
    // CurrentChanged/StateChanged 订阅只挂一次（SetMusicService 幂等重建菜单时不得重复订阅）
    private bool _musicEventsWired;
    // 托盘音乐分组的动态项：头部曲名展示（禁用态）+ 播放/暂停（文案随状态切换），RefreshMusicMenuItems 刷新
    private NativeMenuItem? _musicHeaderItem;
    private NativeMenuItem? _musicPlayPauseItem;

    public TrayService(EventBus eventBus)
    {
        try
        {
            _eventBus = eventBus;
            Console.WriteLine("[TrayService] 初始化完成，托盘功能就绪");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] 构造函数: {ex.Message}");
        }
    }

    public void SetMainWindow(Window window)
    {
        try
        {
            RegisterPet(window);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] SetMainWindow: {ex.Message}");
        }
    }


    /// <summary>
    /// 注入音乐播放服务（V0.2.1，PetWindow 构造调用——它同时持有本服务与 MusicPlayerService 引用；
    /// 本方法不改变 TrayService 构造签名，App 注册无需同步）：记录引用并订阅换曲/状态事件驱动
    /// 托盘音乐分组文案刷新；若托盘菜单已先于此创建则就地重建补入音乐分组（幂等）。
    /// </summary>
    public void SetMusicService(MusicPlayerService musicService)
    {
        try
        {
            if (musicService == null)
            {
                return;
            }
            _musicService = musicService;
            if (!_musicEventsWired)
            {
                _musicEventsWired = true;
                // CurrentChanged/StateChanged 可能从后台线程触发 → 统一 Post 到 UI 线程再改 NativeMenuItem 文案
                musicService.CurrentChanged += _ => Dispatcher.UIThread.Post(RefreshMusicMenuItems);
                musicService.StateChanged += _ => Dispatcher.UIThread.Post(RefreshMusicMenuItems);
            }
            // 菜单已存在（注入晚于 ShowTrayIcon 的时序兜底）：就地重建补入音乐分组。
            // V0.1.3 多桌宠：只在首次兜底时重建一次——N 只桌宠构造都会调本方法，反复重建托盘菜单会抛
            // 「The menu being updated does not match」（日志实测），且首建菜单时音乐分组已在。
            if (_trayIcon != null && !_musicMenuRebuilt)
            {
                _musicMenuRebuilt = true;
                _trayIcon.Menu = BuildTrayMenu();
                RefreshMusicMenuItems();
            }
            Console.WriteLine("[TrayService] 音乐快捷分组已接入");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] SetMusicService: {ex.Message}");
        }
    }

    /// <summary>登记一只桌宠窗口（V0.1.3 多桌宠）。</summary>
    public void RegisterPet(Window window)
    {
        try
        {
            if (window == null) { return; }
            lock (_petsLock)
            {
                if (!_pets.Contains(window)) { _pets.Add(window); }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] RegisterPet: {ex.Message}");
        }
    }

    /// <summary>注销桌宠窗口（窗口销毁时）；全部注销后撤掉托盘图标。</summary>
    public void UnregisterPet(Window window)
    {
        try
        {
            if (window == null) { return; }
            bool empty;
            lock (_petsLock)
            {
                _pets.Remove(window);
                empty = _pets.Count == 0;
            }
            if (empty) { HideTrayIcon(); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] UnregisterPet: {ex.Message}");
        }
    }

    private List<Window> PetSnapshot()
    {
        lock (_petsLock) { return new List<Window>(_pets); }
    }

    /// <summary>全部桌宠是否都处于隐藏状态（托盘点击 = 全显示 / 全隐藏切换）。</summary>
    private bool AllPetsHidden()
    {
        var pets = PetSnapshot();
        return pets.Count > 0 && pets.All(p => !p.IsVisible);
    }

    public void ShowTrayIcon()
    {
        try
        {
            Console.WriteLine("[TrayService] ShowTrayIcon() 被调用");

            if (_trayIcon != null)
            {
                Console.WriteLine("[TrayService] 托盘图标已存在，跳过创建");
                return;
            }

            var icon = CreateTrayIcon();
            Console.WriteLine($"[TrayService] 图标创建结果: {(icon != null ? "成功" : "失败")}");

            _trayIcon = new TrayIcon
            {
                ToolTipText = _tooltip,
                Icon = icon,
            };

            _trayIcon.Menu = BuildTrayMenu();
            _trayIcon.Clicked += OnTrayIconClicked;
            _trayIcon.IsVisible = true;
            Console.WriteLine("[TrayService] TrayIcon 已创建并设置为可见");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] ShowTrayIcon: {ex.Message}");
        }
    }

    public void HideTrayIcon()
    {
        try
        {
            if (_trayIcon == null) return;
            _trayIcon.IsVisible = false;
            _trayIcon.Clicked -= OnTrayIconClicked;
            _trayIcon = null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] HideTrayIcon: {ex.Message}");
        }
    }

    public void SetTooltip(string text)
    {
        try
        {
            _tooltip = text;
            if (_trayIcon != null)
                _trayIcon.ToolTipText = text;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] SetTooltip: {ex.Message}");
        }
    }

    public void ShowNotification(string title, string text)
    {
        try
        {
            if (_trayIcon == null) return;
            var previous = _trayIcon.ToolTipText;
            _trayIcon.ToolTipText = $"{title}: {text}";
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            timer.Tick += (_, _) =>
            {
                try
                {
                    _trayIcon.ToolTipText = previous;
                    timer.Stop();
                }
                catch { }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] ShowNotification: {ex.Message}");
        }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        try
        {
            if (PetSnapshot().Count == 0) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                // V0.1.3：全部可见 → 全部隐藏；有隐藏 → 全部显示（多桌宠一起进退）
                var show = AllPetsHidden();
                foreach (var win in PetSnapshot())
                {
                    try
                    {
                        if (show) { win.Show(); }
                        else { win.Hide(); }
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[TrayService] TogglePet: {ex.Message}"); }
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] OnTrayIconClicked: {ex.Message}");
        }
    }

    private NativeMenu BuildTrayMenu()
    {
        try
        {
            var menu = new NativeMenu();
            var showItem = CreateMenuItem("显示全部桌宠", () =>
            {
                try
                {
                    if (PetSnapshot().Count == 0) { return; }
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var win in PetSnapshot())
                        {
                            try { win.Show(); }
                            catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Show: {ex.Message}"); }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TrayService] Show: {ex.Message}");
                }
            });
            menu.Items.Add(showItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            // 设置中心
            var settingsItem = CreateMenuItem("设置中心", () =>
            {
                try
                {
                    if (PetSnapshot().Count == 0) { return; }
                    Dispatcher.UIThread.Post(() =>
                    {
                        // V0.1.3：定向到主宠（PetId 空 = 主宠），不再让 N 只宠物各弹一个设置窗
                        _eventBus.Publish(EventType.OpenSettings, new Models.OpenSettingsEvent(null));
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Settings: {ex.Message}"); }
            });
            menu.Items.Add(settingsItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            // 重置到桌面中央（V0.1.3：全部桌宠一起居中并错开，位置写回各自配置）
            var centerItem = CreateMenuItem("重置到桌面中央", () =>
            {
                try
                {
                    if (PetSnapshot().Count == 0) { return; }
                    Dispatcher.UIThread.Post(() =>
                    {
                        var pets = PetSnapshot();
                        for (int i = 0; i < pets.Count; i++)
                        {
                            var win = pets[i];
                            try
                            {
                                var screens = win.Screens;
                                if (screens == null || screens.ScreenCount == 0) { continue; }
                                var screen = screens.Primary ?? screens.All[0];
                                var bounds = screen.WorkingArea;
                                int x = (int)(bounds.X + (bounds.Width - win.Width) / 2) + i * 150;
                                int y = (int)(bounds.Y + (bounds.Height - win.Height) / 2) + i * 110;
                                if (x + win.Width > bounds.X + bounds.Width) { x = (int)(bounds.X + bounds.Width - win.Width); }
                                if (y + win.Height > bounds.Y + bounds.Height) { y = (int)(bounds.Y + bounds.Height - win.Height); }
                                win.Position = new PixelPoint(x, y);
                                if (win is Views.PetWindow pw) { pw.SavePositionNow(); }
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Center: {ex.Message}"); }
                        }
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Center: {ex.Message}"); }
            });
            menu.Items.Add(centerItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            // ── 🎵 音乐快捷分组（V0.2.1）：头部曲名展示 + 播放/暂停 + 上/下一首；未注入服务时不占位 ──
            if (_musicService != null)
            {
                // 头部：纯展示当前曲名（禁用防误点；播放前显示占位文案）
                _musicHeaderItem = new NativeMenuItem { Header = "🎵 未在播放", IsEnabled = false };
                menu.Items.Add(_musicHeaderItem);

                // 播放/暂停二态切换：Playing → PauseAsync；Paused → ResumeAsync；Stopped 禁用
                _musicPlayPauseItem = CreateMenuItem("▶️ 播放", () =>
                {
                    var svc = _musicService;
                    if (svc == null)
                    {
                        return;
                    }
                    var state = svc.State;
                    if (state == PlaybackState.Playing)
                    {
                        RunMusicAction(svc.PauseAsync, "Pause");
                    }
                    else if (state == PlaybackState.Paused)
                    {
                        RunMusicAction(svc.ResumeAsync, "Resume");
                    }
                });
                menu.Items.Add(_musicPlayPauseItem);

                // 上一首 / 下一首：直调服务推进方法（无队列时服务内部 no-op，安全）
                menu.Items.Add(CreateMenuItem("⏮ 上一首", () =>
                {
                    if (_musicService == null)
                    {
                        return;
                    }
                    RunMusicAction(_musicService.PrevAsync, "Prev");
                }));
                menu.Items.Add(CreateMenuItem("⏭ 下一首", () =>
                {
                    if (_musicService == null)
                    {
                        return;
                    }
                    RunMusicAction(_musicService.NextAsync, "Next");
                }));

                menu.Items.Add(new NativeMenuItemSeparator());

                // 构建完成立即按当前播放状态刷一次初始文案（头部曲名 + ⏯ 态）
                RefreshMusicMenuItems();
            }

            var exitItem = CreateMenuItem("退出", () =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() => _eventBus.Publish(EventType.AppExit));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TrayService] Exit: {ex.Message}");
                }
            });
            menu.Items.Add(exitItem);

            return menu;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] BuildTrayMenu: {ex.Message}");
            return new NativeMenu();
        }
    }

    /// <summary>
    /// 刷新托盘音乐分组文案（V0.2.1，CurrentChanged/StateChanged 事件与菜单构建时调用）：
    /// 头部展示当前曲名（Stopped 态恢复占位「🎵 未在播放」，右键菜单 RefreshMusicMenuItem 同口径以
    /// Track 名展示、超长截断）；⏯ 文案随 Playing/Paused 切换，Stopped 无活动流禁用防误触空转。
    /// 调用方保证已在 UI 线程（事件路径经 Dispatcher.UIThread.Post 进入）。
    /// </summary>
    private void RefreshMusicMenuItems()
    {
        try
        {
            if (_musicHeaderItem == null || _musicPlayPauseItem == null || _musicService == null)
            {
                return;
            }
            var track = _musicService.Current;
            var state = _musicService.State;
            string headerText;
            if (track != null && state != PlaybackState.Stopped)
            {
                // 曲名超长截断（macOS NSMenuItem 过长由系统裁切，自行控制在 24 字符内观感一致）
                var name = track.Track;
                headerText = name.Length > 24 ? $"🎵 {name[..24]}…" : $"🎵 {name}";
            }
            else
            {
                headerText = "🎵 未在播放";
            }
            _musicHeaderItem.Header = headerText;
            _musicPlayPauseItem.Header = state == PlaybackState.Playing ? "⏸ 暂停" : "▶️ 播放";
            _musicPlayPauseItem.IsEnabled = state == PlaybackState.Playing || state == PlaybackState.Paused;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] RefreshMusicMenuItems: {ex.Message}");
        }
    }

    /// <summary>托盘音乐操作 fire-and-forget：服务方法均返回 Task，失败只记 stderr 不影响托盘交互。</summary>
    private static void RunMusicAction(Func<Task> action, string name)
    {
        try
        {
            var task = action();
            if (task == null)
            {
                return;
            }
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Console.Error.WriteLine($"[TrayService] 音乐操作 {name}: {t.Exception?.GetBaseException().Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] 音乐操作 {name}: {ex.Message}");
        }
    }

    private static NativeMenuItem CreateMenuItem(string header, Action action)
    {
        var item = new NativeMenuItem { Header = header };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Console.Error.WriteLine($"[TrayService] MenuItem '{header}': {ex.Message}"); }
        };
        return item;
    }

    private static WindowIcon CreateTrayIcon()
    {
        try
        {
            using var bitmap = new SKBitmap(32, 32);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            // 身体（蓝紫色圆形）
            using var bodyPaint = new SKPaint { Color = new SKColor(94, 156, 255), IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawOval(new SKRect(2, 4, 30, 28), bodyPaint);

            // 眼白
            using var eyePaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawOval(new SKRect(8, 10, 15, 17), eyePaint);
            canvas.DrawOval(new SKRect(17, 10, 24, 17), eyePaint);

            // 瞳孔
            using var pupilPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawOval(new SKRect(10, 12, 13, 15), pupilPaint);
            canvas.DrawOval(new SKRect(19, 12, 22, 15), pupilPaint);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            return new WindowIcon(new Bitmap(ms));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TrayService] CreateTrayIcon: {ex.Message}");
            // 回退：最小 PNG（1x1 透明）
            var fallbackPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137, 0, 0, 0, 13, 73, 68, 65, 84, 120, 156, 98, 96, 96, 96, 0, 0, 0, 4, 0, 1, 39, 221, 9, 92, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
            return new WindowIcon(new Bitmap(new MemoryStream(fallbackPng)));
        }
    }
}
