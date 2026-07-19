using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using MiniPet.Services;

namespace MiniPet.Views;

public class PetWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AnimService _animService;
    private readonly SpriteService _spriteService;
    private readonly Image _spriteImage;
    private readonly TextBlock _infoText;
    private Point _dragStart;
    private bool _isDragging;
    private PixelPoint _windowStartPos;
    private readonly DispatcherTimer _timer;
    private int _tickCount;

    public PetWindow(ConfigService configService, AnimService animService, SpriteService spriteService)
    {
        _configService = configService;
        _animService = animService;
        _spriteService = spriteService;

        Title = "🐾 MiniPet";
        Width = 320;
        Height = 240;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Content = grid;

        // Row 0: 标题
        _infoText = new TextBlock
        {
            Text = "🐾 MiniPet 桌宠",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8)
        };
        Grid.SetRow(_infoText, 0);
        grid.Children.Add(_infoText);

        // Row 1: 精灵图
        _spriteImage = new Image { Width = 200, Height = 120, Stretch = Stretch.Uniform };
        Grid.SetRow(_spriteImage, 1);
        grid.Children.Add(_spriteImage);

        // Row 2: 状态
        var statusBar = new TextBlock
        {
            Text = "右键菜单",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            TextAlignment = TextAlignment.Center
        };
        Grid.SetRow(statusBar, 2);
        grid.Children.Add(statusBar);

        // 右键菜单
        ContextMenu = BuildContextMenu();

        // 指针事件
        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed)
            {
                ContextMenu?.Open(this);
                return;
            }
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            _windowStartPos = Position;
        };
        PointerMoved += (_, e) =>
        {
            if (!_isDragging) return;
            var pos = e.GetPosition(this);
            Position = new PixelPoint(
                (int)(_windowStartPos.X + pos.X - _dragStart.X),
                (int)(_windowStartPos.Y + pos.Y - _dragStart.Y));
        };
        PointerReleased += (_, _) => _isDragging = false;

        // 帧循环
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            _tickCount++;
            var (fd, bmp) = _animService.Tick(500);
            if (bmp != null)
            {
                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                _spriteImage.Source = new Avalonia.Media.Imaging.Bitmap(ms);
            }
            _infoText.Text = $"🐾 Tick #{_tickCount}";
        };

        _animService.Play("stand", PlayMode.Loop);
        _timer.Start();
    }

    private ContextMenu BuildContextMenu()
    {
        var cm = new ContextMenu();
        var animItem = new MenuItem { Header = "切换动画" };
        var standItem = new MenuItem { Header = "stand" };
        standItem.Click += (_, _) => _animService.Play("stand", PlayMode.Loop);
        animItem.Items.Add(standItem);
        var flyItem = new MenuItem { Header = "fly" };
        flyItem.Click += (_, _) => _animService.Play("fly", PlayMode.Loop);
        animItem.Items.Add(flyItem);
        cm.Items.Add(animItem);
        cm.Items.Add(new Separator());
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Close();
        cm.Items.Add(exitItem);
        return cm;
    }
}