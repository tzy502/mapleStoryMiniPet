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
    private readonly TextBox _nameBox;
    private Point _dragStart;
    private bool _isDragging;
    private PixelPoint _windowStartPos;
    private readonly DispatcherTimer _timer;

    public PetWindow(ConfigService configService, AnimService animService, SpriteService spriteService)
    {
        _configService = configService;
        _animService = animService;
        _spriteService = spriteService;

        // Window setup
        // WindowStyle removed for Avalonia 12.x
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        Width = 200;
        Height = 250;
        // SystemDecorations.None

        // Root panel
        var panel = new Canvas();
        Content = panel;

        // Sprite image
        _spriteImage = new Image { Width = 140, Height = 160 };
        Canvas.SetLeft(_spriteImage, 30);
        Canvas.SetTop(_spriteImage, 10);
        panel.Children.Add(_spriteImage);

        // Name text
        _nameBox = new TextBox { Width = 140 };
        Canvas.SetLeft(_nameBox, 30);
        Canvas.SetTop(_nameBox, 175);
        panel.Children.Add(_nameBox);

        // Status text
        _infoText = new TextBlock { Foreground = Brushes.White, FontSize = 10 };
        Canvas.SetLeft(_infoText, 30);
        Canvas.SetTop(_infoText, 200);
        panel.Children.Add(_infoText);

        // Events
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        // Context menu
        ContextMenu = BuildContextMenu();

        // Frame timer
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += OnTick;

        // Start
        LoadDefaultPet();
        _timer.Start();
    }

    private void LoadDefaultPet()
    {
        var settings = _configService.LoadSettings();
        _animService.Play("stand", PlayMode.Loop);
        _infoText.Text = $"🐾 {settings.CurrentMobId}";
        _nameBox.Text = "";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var (frameData, bmp) = _animService.Tick(120);
        if (bmp != null)
        {
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            _spriteImage.Source = new Avalonia.Media.Imaging.Bitmap(ms);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging = true;
        _dragStart = e.GetPosition(this);
        _windowStartPos = Position;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;
        Position = new PixelPoint((int)(_windowStartPos.X + dx), (int)(_windowStartPos.Y + dy));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var switchMob = new MenuItem { Header = "切换人物" };
        switchMob.Items.Add(new MenuItem { Header = "怪物列表（WZ未加载）", IsEnabled = false });
        switchMob.Items.Add(new MenuItem { Header = "NPC列表（WZ未加载）", IsEnabled = false });
        menu.Items.Add(switchMob);

        var switchAnim = new MenuItem { Header = "切换动画" };
        switchAnim.Items.Add(new MenuItem { Header = "stand" });
        switchAnim.Items.Add(new MenuItem { Header = "fly" });
        switchAnim.Items.Add(new MenuItem { Header = "say" });
        menu.Items.Add(switchAnim);

        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "设置中心" };
        settings.Click += (s, e) => new SettingsWindow(_configService).Show();
        menu.Items.Add(settings);

        var hermes = new MenuItem { Header = "Hermes 终端", IsEnabled = false };
        menu.Items.Add(hermes);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (s, e) => Close();
        menu.Items.Add(exit);

        return menu;
    }
}
