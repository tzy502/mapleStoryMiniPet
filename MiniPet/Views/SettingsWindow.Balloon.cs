using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using System.IO;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow 气泡 tab 与其余（partial 拆分）：BuildBalloonTab 至文件尾。原实现从 SettingsWindow.axaml.cs 平移，行为完全一致。
/// </summary>
public partial class SettingsWindow
{
    private Border BuildBalloonTab()
    {
        try
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var sidebar = new StackPanel { Margin = new Thickness(10), Spacing = 8 };
            sidebar.Children.Add(new TextBlock { Text = "💬 气泡样式", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushTxtDim, Margin = new Thickness(4, 0, 0, 0) });

            var balloonListBox = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(-10, 0, -10, 0) };
            balloonListBox.ItemsSource = _balloonPresets;
            balloonListBox.ItemTemplate = new FuncDataTemplate<(int Id, string Name, Color Color, Color BgColor)>((data, _) =>
                new Border
                {
                    Padding = new Thickness(10, 6),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Border { Width = 28, Height = 20, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(data.BgColor), BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1) }, new TextBlock { Text = data.Name, FontSize = 12, Foreground = BrushTxtSecondary, VerticalAlignment = VerticalAlignment.Center } } }
                });
            balloonListBox.SelectedIndex = 0;
            balloonListBox.SelectionChanged += (_, _) =>
            {
                try { if (balloonListBox.SelectedItem is ValueTuple<int, string, Color, Color> item) { _selectedBalloonId = item.Item1; UpdateBalloonPreview(item.Item1, item.Item2, item.Item4, item.Item3); } }
                catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BalloonSelect: {ex.Message}"); }
            };
            sidebar.Children.Add(balloonListBox);
            Grid.SetColumn(sidebar, 0);
            grid.Children.Add(sidebar);

            var divider = new Border { Width = 1, Background = BrushBorder, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            _balloonPreviewCanvas = new Border { CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 94, 156, 255)), BorderThickness = new Thickness(1), Background = GetCheckerboardBrush(), Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Stretch };
            var first = _balloonPresets[0];
            UpdateBalloonPreview(first.Id, first.Name, first.BgColor, first.Color);
            Grid.SetColumn(_balloonPreviewCanvas, 2);
            grid.Children.Add(_balloonPreviewCanvas);
            return new Border { Child = grid };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildBalloonTab: {ex.Message}"); return new Border(); }
    }

    private void UpdateBalloonPreview(int id, string name, Color bgColor, Color textColor)
    {
        try
        {
            if (_balloonPreviewCanvas == null) return;
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 16 };

            var balloonContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            balloonContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            balloonContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var balloonBody = new Border
            {
                CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(bgColor), BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(28, 18),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Color = Color.FromArgb(80, 0, 0, 0), OffsetX = 0, OffsetY = 4 })
            };
            var balloonContent = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            balloonContent.Children.Add(new TextBlock { Text = name, FontSize = 10, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center });
            _balloonPreviewText = new TextBlock { Text = "你好！我是 MiniPet 桌宠 🐾", FontSize = 14, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(textColor), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            balloonContent.Children.Add(_balloonPreviewText);
            balloonBody.Child = balloonContent;
            Grid.SetRow(balloonBody, 0);
            balloonContainer.Children.Add(balloonBody);

            var arrow = new Polygon { Points = new List<Point> { new(0, 0), new(12, 10), new(24, 0) }, Fill = new SolidColorBrush(bgColor), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(arrow, 1);
            balloonContainer.Children.Add(arrow);

            panel.Children.Add(balloonContainer);
            panel.Children.Add(new TextBlock { Text = "🐾", FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { new TextBlock { Text = $"样式: {name}", FontSize = 10, Foreground = BrushTxtDim, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = $"ID: {id}", FontSize = 9, Foreground = BrushTxtDim, FontFamily = new FontFamily("SF Mono, monospace"), HorizontalAlignment = HorizontalAlignment.Center } } });
            _balloonPreviewCanvas.Child = panel;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdateBalloonPreview: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════
    private static Button MakeSmallBtn(string text, Action onClick)
    {
        var btn = new Button { Content = text, FontSize = 10, Padding = new Thickness(10, 4), Background = new SolidColorBrush(Color.FromArgb(128, 60, 60, 70)), Foreground = BrushTxtSecondary, BorderBrush = new SolidColorBrush(InputBorder), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        btn.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] Btn {text}: {ex.Message}"); } };
        return btn;
    }

    // ═══════════════════════════════════════════
    // 素材浏览集成
    // ═══════════════════════════════════════════
    private void OpenMaterialBrowser()
    {
        try
        {
            var browser = new MaterialBrowserWindow(_wzService, _config);
            browser.OnMaterialSelected += (id, name) =>
            {
                Console.WriteLine($"[SettingsWindow] OnMaterialSelected received: Id={id}, Name={name}");
                Dispatcher.UIThread.Post(() => SelectPet(name, id));
            };
            browser.Show(this);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenMaterialBrowser: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════
    // 预览动画
    // ═══════════════════════════════════════════
    private void StartPreviewAnimation()
    {
        try
        {
            _previewAnimTimer.Stop();

            if (_previewPngData == null || _previewStrip == null)
                return;

            _previewFrameIndex = 0;

            var delay = 240;
            if (_previewStrip.FrameData != null && _previewStrip.FrameData.Count > 0)
                delay = _previewStrip.FrameData[0].Delay;
            _previewAnimTimer.Interval = TimeSpan.FromMilliseconds(delay);
            _previewAnimTimer.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] StartPreviewAnimation: {ex}"); }
    }

    private void OnPreviewAnimTick(object? sender, EventArgs e)
    {
        try
        {
            if (_previewCanvas == null || _previewStrip == null || _previewSourceBitmap == null) return;
            if (_previewCanvas.Child is StackPanel stack)
            {
                foreach (var child in stack.Children)
                {
                    if (child is Border figure2 && figure2.Child is Image img2 && img2.Tag?.ToString() == "previewSprite")
                    {
                        var frameWidth = _previewStrip.FrameWidth;
                        var frameHeight = _previewStrip.FrameHeight;

                        var frameRect = new PixelRect(frameWidth * _previewFrameIndex, 0, frameWidth, frameHeight);
                        try { img2.Source = new CroppedBitmap(_previewSourceBitmap, frameRect); }
                        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] TickFrame: {ex.Message}"); }
                        _previewFrameIndex++;
                        if (_previewFrameIndex >= _previewStrip.FrameCount)
                            _previewFrameIndex = 0;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OnPreviewAnimTick: {ex}"); }
    }

    // ═══════════════════════════════════════════
    // WZ 宠物列表加载
    // ═══════════════════════════════════════════
    private void LoadPetListFromWz()
    {
        try
        {
            // 只刷新 UI 列表显示，不修改用户数据
            Dispatcher.UIThread.Post(() =>
            {
                RefreshFavoriteList();
                RefreshRecentList();
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] LoadPetListFromWz: {ex.Message}"); }
    }
}
