using System;
using System.IO;
using System.Threading.Tasks;
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
}

public class TrayService
{
    private TrayIcon? _trayIcon;
    private Window? _mainWindow;
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
            _mainWindow = window;
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
            // 菜单已存在（注入晚于 ShowTrayIcon 的时序兜底）：就地重建补入音乐分组
            if (_trayIcon != null)
            {
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
            if (_mainWindow == null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_mainWindow.IsVisible)
                    _mainWindow.Hide();
                else
                    _mainWindow.Show();
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
            var showItem = CreateMenuItem("显示主窗口", () =>
            {
                try
                {
                    if (_mainWindow == null) return;
                    Dispatcher.UIThread.Post(() => _mainWindow.Show());
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
                    if (_mainWindow == null) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _eventBus.Publish(EventType.OpenSettings);
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Settings: {ex.Message}"); }
            });
            menu.Items.Add(settingsItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            // 重置到桌面中央
            var centerItem = CreateMenuItem("重置到桌面中央", () =>
            {
                try
                {
                    if (_mainWindow == null) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_mainWindow is Window win)
                        {
                            var screens = win.Screens;
                            if (screens != null && screens.ScreenCount > 0)
                            {
                                var screen = screens.Primary ?? screens.All[0];
                                var bounds = screen.Bounds;
                                win.Position = new PixelPoint(
                                    (int)(bounds.X + (bounds.Width - win.Width) / 2),
                                    (int)(bounds.Y + (bounds.Height - win.Height) / 2));
                            }
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
