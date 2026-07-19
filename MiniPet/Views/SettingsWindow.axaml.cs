using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MiniPet.Services;

namespace MiniPet.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private bool _isDarkTheme = true;

    public SettingsWindow(ConfigService configService)
    {
        _configService = configService;
        InitializeComponent();
// #if DEBUG
        this.// AttachDevTools();
// #endif
        LoadSettings();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 从 ConfigService 加载当前设置
    /// </summary>
    private void LoadSettings()
    {
        var settings = _configService.LoadSettings();
        // 主题
        _isDarkTheme = settings.Theme != "light";
        var themeToggle = this.FindControl<ToggleSwitch>("ThemeToggle");
        if (themeToggle != null)
            themeToggle.IsChecked = _isDarkTheme;

        var themeLabel = this.FindControl<TextBlock>("ThemeLabel");
        if (themeLabel != null)
            themeLabel.Text = _isDarkTheme ? "暗黑" : "白天";
    }

    /// <summary>
    /// 保存设置回 ConfigService
    /// </summary>
    private void SaveSettings()
    {
        var settings = _configService.LoadSettings();
        settings.Theme = _isDarkTheme ? "dark" : "light";
        _configService.SaveSettings(settings);
    }

    /// <summary>
    /// 主Tab切换
    /// </summary>
    private void OnMainTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        var tabControl = (TabControl)sender!;
        var selectedIndex = tabControl.SelectedIndex;

        var petSettingsContent = this.FindControl<Grid>("PetSettingsContent");
        var aiSettingsContent = this.FindControl<Grid>("AISettingsContent");
        var chatLogContent = this.FindControl<Grid>("ChatLogContent");

        if (petSettingsContent != null)
            petSettingsContent.IsVisible = selectedIndex == 0;
        if (aiSettingsContent != null)
            aiSettingsContent.IsVisible = selectedIndex == 1;
        if (chatLogContent != null)
            chatLogContent.IsVisible = selectedIndex == 2;
    }

    /// <summary>
    /// 子Tab切换
    /// </summary>
    private void OnSubTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        var tabControl = (TabControl)sender!;
        var selectedIndex = tabControl.SelectedIndex;

        var materialContent = this.FindControl<Grid>("MaterialContent");
        var paperdollContent = this.FindControl<Grid>("PaperdollContent");
        var backgroundContent = this.FindControl<Grid>("BackgroundContent");
        var balloonContent = this.FindControl<Grid>("BalloonContent");

        if (materialContent != null)
            materialContent.IsVisible = selectedIndex == 0;
        if (paperdollContent != null)
            paperdollContent.IsVisible = selectedIndex == 1;
        if (backgroundContent != null)
            backgroundContent.IsVisible = selectedIndex == 2;
        if (balloonContent != null)
            balloonContent.IsVisible = selectedIndex == 3;
    }

    /// <summary>
    /// 暗黑模式切换
    /// </summary>
    private void OnThemeToggleChanged(object? sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender!;
        _isDarkTheme = toggle.IsChecked ?? false;

        var themeLabel = this.FindControl<TextBlock>("ThemeLabel");
        if (themeLabel != null)
            themeLabel.Text = _isDarkTheme ? "暗黑" : "白天";

        SaveSettings();
    }

    /// <summary>
    /// WZ 配置按钮点击
    /// </summary>
    private void OnWzConfigClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new WzConfigDialog(_configService)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// 素材分段切换
    /// </summary>
    private void OnSegmentClick(object? sender, RoutedEventArgs e)
    {
        var border = (Border)sender!;
        var mobSegment = this.FindControl<Border>("MobSegment");
        var npcSegment = this.FindControl<Border>("NpcSegment");

        if (border == mobSegment)
        {
            mobSegment.Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#5E9CFF26"));
            var tb = (TextBlock)mobSegment.Child!;
            tb.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#5E9CFF"));

            npcSegment.Background = new SolidColorBrush(Avalonia.Media.Colors.Transparent);
            var npcTb = (TextBlock)npcSegment.Child!;
            npcTb.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF4D"));
        }
        else
        {
            npcSegment.Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#5E9CFF26"));
            var npcTb = (TextBlock)npcSegment.Child!;
            npcTb.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#5E9CFF"));

            mobSegment.Background = new SolidColorBrush(Avalonia.Media.Colors.Transparent);
            var tb = (TextBlock)mobSegment.Child!;
            tb.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF4D"));
        }
    }

    /// <summary>
    /// 浏览素材库
    /// </summary>
    private void OnBrowseMaterialClick(object? sender, RoutedEventArgs e)
    {
        // 占位：后续打开 MaterialBrowserWindow
    }

    /// <summary>
    /// 素材项悬停
    /// </summary>
    private void OnMaterialItemHover(object? sender, PointerEventArgs e)
    {
        var border = (Border)sender!;
        var bg = border.Background as SolidColorBrush;
        if (bg == null || bg.Color == Avalonia.Media.Colors.Transparent)
            border.Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#5E9CFF1F"));
    }

    /// <summary>
    /// 素材项离开
    /// </summary>
    private void OnMaterialItemLeave(object? sender, PointerEventArgs e)
    {
        var border = (Border)sender!;
        var bg = border.Background as SolidColorBrush;
        if (bg != null && bg.Color == Avalonia.Media.Color.Parse("#5E9CFF1F"))
            border.Background = new SolidColorBrush(Avalonia.Media.Colors.Transparent);
    }

    /// <summary>
    /// 素材项点击
    /// </summary>
    private void OnMaterialItemClick(object? sender, PointerPressedEventArgs e)
    {
        // 占位：切换到对应素材
    }

    /// <summary>
    /// 收藏点击
    /// </summary>
    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        // 占位：收藏切换
    }

    /// <summary>
    /// 播放点击
    /// </summary>
    private void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        // 占位：播放预览
    }

    /// <summary>
    /// 复制为 OBJ
    /// </summary>
    private void OnCopyObjClick(object? sender, RoutedEventArgs e)
    {
        // 占位：复制到背景 OBJ 列表
    }

    /// <summary>
    /// 应用素材
    /// </summary>
    private void OnApplyMaterialClick(object? sender, RoutedEventArgs e)
    {
        // 占位：应用到宠物窗口
    }

    /// <summary>
    /// 背景色点击
    /// </summary>
    private void OnBackgroundClick(object? sender, RoutedEventArgs e)
    {
        var border = (Border)sender!;
        // 重置所有背景色卡
        var bgNames = new[] { "BgTransparent", "BgGrid", "BgHeneseys", "BgMarket", "BgForest" };
        foreach (var name in bgNames)
        {
            var b = this.FindControl<Border>(name);
            if (b != null)
                b.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#50505A99"));
        }
        // 选中当前
        border.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#5E9CFF"));
    }

    /// <summary>
    /// 浏览发型
    /// </summary>
    private void OnBrowseHairClick(object? sender, RoutedEventArgs e)
    {
        // 占位：打开素材浏览器
    }

    /// <summary>
    /// 浏览脸型
    /// </summary>
    private void OnBrowseFaceClick(object? sender, RoutedEventArgs e)
    {
        // 占位
    }

    /// <summary>
    /// 浏览皮肤
    /// </summary>
    private void OnBrowseSkinClick(object? sender, RoutedEventArgs e)
    {
        // 占位
    }

    /// <summary>
    /// 应用纸娃娃
    /// </summary>
    private void OnApplyPaperdollClick(object? sender, RoutedEventArgs e)
    {
        // 占位
    }

    /// <summary>
    /// 应用气泡
    /// </summary>
    private void OnApplyBalloonClick(object? sender, RoutedEventArgs e)
    {
        // 占位
    }
}