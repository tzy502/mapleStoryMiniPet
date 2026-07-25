using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace MiniPet.Services;

public class TrayService
{
    private TrayIcon? _trayIcon;
    private Window? _mainWindow;
    private readonly EventBus _eventBus;
    private string _tooltip = "MiniPet 桌宠";

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
