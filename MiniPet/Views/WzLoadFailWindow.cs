using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MiniPet.Views;

/// <summary>
/// WZ 素材读取失败弹窗（纯代码窗口，无 .axaml）。
/// 启动加载失败 / 异常分支由 App 弹出，给出错误原因 + 下载引导。
/// </summary>
public class WzLoadFailWindow : Window
{
    private const string DownloadUrl = "https://github.com/Kagamia/WzComparerR2/releases";

    public WzLoadFailWindow(string errorText, string? wzLibWarning)
    {
        try
        {
            Title = "WZ 素材读取失败";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
            Background = new SolidColorBrush(Color.FromArgb(242, 38, 38, 44));

            var primary = new SolidColorBrush(Color.FromArgb(255, 240, 240, 244));
            var body = new SolidColorBrush(Color.FromArgb(255, 226, 226, 232));
            var dim = new SolidColorBrush(Color.FromArgb(255, 176, 176, 190));
            var warn = new SolidColorBrush(Color.FromArgb(255, 230, 196, 106));
            var linkColor = new SolidColorBrush(Color.FromArgb(255, 94, 156, 255));

            var stack = new StackPanel { Margin = new Thickness(28), Spacing = 14 };

            stack.Children.Add(new TextBlock
            {
                Text = "WZ 素材读取失败",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = primary
            });

            stack.Children.Add(new TextBlock
            {
                Text = errorText,
                FontSize = 13,
                Foreground = body,
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrEmpty(wzLibWarning))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = wzLibWarning,
                    FontSize = 12,
                    Foreground = warn,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = "可前往 GitHub 下载 WzComparerR2（含 WzComparerR2.WzLib.dll）",
                FontSize = 12,
                Foreground = dim,
                TextWrapping = TextWrapping.Wrap
            });

            var link = new TextBlock
            {
                Text = DownloadUrl,
                FontSize = 12,
                Foreground = linkColor,
                TextDecorations = TextDecorations.Underline,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            link.PointerPressed += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = DownloadUrl, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WzLoadFailWindow] 打开链接: {ex.Message}");
                }
            };
            stack.Children.Add(link);

            var okBtn = new Button
            {
                Content = "知道了",
                FontSize = 12,
                Padding = new Thickness(24, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = linkColor,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            UiStyle.ApplyHover(okBtn);
            okBtn.Click += (_, _) =>
            {
                try { Close(); }
                catch (Exception ex) { Console.Error.WriteLine($"[WzLoadFailWindow] Close: {ex.Message}"); }
            };
            stack.Children.Add(okBtn);

            Content = stack;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WzLoadFailWindow] 构造: {ex.Message}");
        }
    }
}
