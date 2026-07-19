using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// 宠物主窗口 - 透明无边框悬浮窗口，显示精灵图动画
/// </summary>
public partial class PetWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AnimService _animService;
    private readonly SpriteService _spriteService;

    // 拖拽状态
    private bool _isDragging;
    private PixelPoint _dragStartPoint;

    // 帧循环
    private DispatcherTimer? _frameTimer;
    private const int FrameIntervalMs = 120;

    public PetWindow(ConfigService configService, AnimService animService, SpriteService spriteService)
    {
        _configService = configService;
        _animService = animService;
        _spriteService = spriteService;

        InitializeComponent();

        // 初始化窗口位置
        LoadWindowPosition();

        // 设置拖拽事件
        SetupDrag();

        // 构建右键菜单
        SetupContextMenu();

        // 加载默认宠物
        LoadDefaultPet();

        // 启动帧循环
        StartFrameLoop();
    }

    private void LoadDefaultPet()
    {
        var settings = _configService.LoadSettings();
        if (settings.CurrentMobId > 0)
        {
            LoadPet(settings.CurrentMobId);
        }
    }

    private void LoadPet(int mobId)
    {
        _animService.SetCurrentMobId(mobId);

        // 预加载精灵图
        Task.Run(() =>
        {
            try
            {
                _spriteService.PreloadSprites(mobId);
            }
            catch
            {
                // 预加载失败不影响主流程
            }
        });

        // 播放默认动画
        var settings = _configService.LoadSettings();
        var defaultAnim = string.IsNullOrEmpty(settings.DefaultAnim) ? "stand" : settings.DefaultAnim;
        _animService.SwitchTo(defaultAnim);
    }

    private void SetupDrag()
    {
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            var pos = e.GetPosition(this);
            _dragStartPoint = new PixelPoint((int)pos.X, (int)pos.Y);
            e.Pointer.Capture(this);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;

        var currentPoint = e.GetPosition(this);
        var screenPoint = this.PointToScreen(currentPoint);

        Position = new PixelPoint(
            screenPoint.X - _dragStartPoint.X,
            screenPoint.Y - _dragStartPoint.Y
        );
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);

            // 保存窗口位置
            SaveWindowPosition();
        }
    }

    private void SetupContextMenu()
    {
        var contextMenu = new ContextMenu();

        // 切换人物
        var switchCharacterItem = new MenuItem { Header = "切换人物" };
        var npcItem = new MenuItem { Header = "NPC" };
        var mobItem = new MenuItem { Header = "MOB" };
        var playerItem = new MenuItem { Header = "PLAYER" };

        // WZ未加载时灰显
        var wzConfig = _configService.LoadWzConfig();
        var hasWz = !string.IsNullOrEmpty(wzConfig.BaseWzPath);
        npcItem.IsEnabled = hasWz;
        mobItem.IsEnabled = hasWz;
        playerItem.IsEnabled = hasWz;

        switchCharacterItem.Items.Add(npcItem);
        switchCharacterItem.Items.Add(mobItem);
        switchCharacterItem.Items.Add(playerItem);
        contextMenu.Items.Add(switchCharacterItem);

        // 切换动画
        var switchAnimItem = new MenuItem { Header = "切换动画" };
        RefreshAnimationMenu(switchAnimItem);
        contextMenu.Items.Add(switchAnimItem);

        // 切换背景
        var switchBgItem = new MenuItem { Header = "切换背景" };
        switchBgItem.IsEnabled = false; // 占位
        contextMenu.Items.Add(switchBgItem);

        // 切换气泡
        var switchBalloonItem = new MenuItem { Header = "切换气泡" };
        switchBalloonItem.IsEnabled = false; // 占位
        contextMenu.Items.Add(switchBalloonItem);

        // 切换椅子/骑宠
        var switchChairItem = new MenuItem { Header = "切换椅子/骑宠" };
        switchChairItem.IsEnabled = false; // 占位，仅PLAYER显示
        contextMenu.Items.Add(switchChairItem);

        // 分隔线
        contextMenu.Items.Add(new Separator());

        // Hermes终端
        var hermesItem = new MenuItem { Header = "Hermes 终端" };
        hermesItem.IsEnabled = false; // P2
        contextMenu.Items.Add(hermesItem);

        // 设置中心
        var settingsItem = new MenuItem { Header = "设置中心" };
        settingsItem.Click += (_, _) => OpenSettings();
        contextMenu.Items.Add(settingsItem);

        // 分隔线
        contextMenu.Items.Add(new Separator());

        // 退出
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitApp();
        contextMenu.Items.Add(exitItem);

        // 绑定到窗口
        ContextMenu = contextMenu;
    }

    private void RefreshAnimationMenu(MenuItem menuItem)
    {
        menuItem.Items.Clear();

        var animList = _animService.GetAnimationList();
        foreach (var anim in animList)
        {
            var item = new MenuItem { Header = anim };
            var capturedAnim = anim;
            item.Click += (_, _) =>
            {
                _animService.SwitchTo(capturedAnim);
            };
            menuItem.Items.Add(item);
        }
    }

    private void OpenSettings()
    {
        // 占位 - 后续实现 SettingsWindow
    }

    private void ExitApp()
    {
        // 先保存窗口位置
        SaveWindowPosition();

        // 尝试播放die动画
        try
        {
            _animService.SwitchTo("die");
            // 等一帧后退出
            DispatcherTimer.RunOnce(() => { Environment.Exit(0); }, TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    private void StartFrameLoop()
    {
        _frameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FrameIntervalMs)
        };

        _frameTimer.Tick += (_, _) =>
        {
            TickAnimation();
        };

        _frameTimer.Start();
    }

    private void TickAnimation()
    {
        try
        {
            var (frameData, bitmap) = _animService.Tick(FrameIntervalMs);
            if (bitmap != null && frameData != null)
            {
                // 从 SpriteStrip 获取 origin 信息
                var action = _animService.GetCurrentAction();
                var (strip, _) = _spriteService.GetCachedStrip(
                    _configService.LoadSettings().CurrentMobId,
                    action
                );

                var originX = strip?.OriginX ?? 0;
                var originY = strip?.OriginY ?? 0;

                SpriteCanvas.SetFrame(bitmap, originX, originY);
            }
        }
        catch
        {
            // 帧循环异常不崩溃
        }
    }

    private void LoadWindowPosition()
    {
        try
        {
            var settings = _configService.LoadSettings();
            if (settings.WindowX > 0 || settings.WindowY > 0)
            {
                Position = new PixelPoint(settings.WindowX, settings.WindowY);
            }
        }
        catch
        {
            // 位置加载失败使用默认
        }
    }

    private void SaveWindowPosition()
    {
        try
        {
            var settings = _configService.LoadSettings();
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
            _configService.SaveSettings(settings);
        }
        catch
        {
            // 位置保存失败忽略
        }
    }
}