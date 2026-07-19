using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

public partial class WzConfigDialog : Window
{
    private readonly ConfigService _configService;
    private WzConfig _wzConfig;

    public WzConfigDialog(ConfigService configService)
    {
        _configService = configService;
        _wzConfig = configService.LoadWzConfig();
        DataContext = _wzConfig;

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 浏览 wzlib DLL
    /// </summary>
    private async void OnBrowseWzLibClick(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 WzComparerR2.WzLib.dll",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DLL 文件") { Patterns = new[] { "*.dll" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            _wzConfig.WzLibPath = path;
            var input = this.FindControl<TextBox>("WzLibPathInput");
            if (input != null)
                input.Text = path;
        }
    }

    /// <summary>
    /// 浏览 BaseWZ 目录
    /// </summary>
    private async void OnBrowseBaseWzClick(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Data 目录",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            _wzConfig.BaseWzPath = path;
            var input = this.FindControl<TextBox>("BaseWzPathInput");
            if (input != null)
                input.Text = path;
        }
    }

    /// <summary>
    /// 验证配置
    /// </summary>
    private async void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        var statusText = this.FindControl<TextBlock>("StatusText");
        if (statusText != null)
            statusText.Text = "验证中…";

        // 异步验证
        var isValid = await ValidateWzConfig();

        if (statusText != null)
            statusText.Text = isValid ? "✅ 验证通过" : "❌ 配置无效";
    }

    /// <summary>
    /// 取消
    /// </summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 保存
    /// </summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _configService.SaveWzConfig(_wzConfig);
        Close();
    }

    /// <summary>
    /// 验证 WZ 配置有效性
    /// </summary>
    private async Task<bool> ValidateWzConfig()
    {
        // 占位验证逻辑
        await Task.Delay(500);
        return !string.IsNullOrEmpty(_wzConfig.WzLibPath) &&
               !string.IsNullOrEmpty(_wzConfig.BaseWzPath);
    }
}