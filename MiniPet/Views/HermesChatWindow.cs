using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Services.Adapters;

namespace MiniPet.Views
{
    /// <summary>
    /// Hermes 迷你聊天对话框（PLAN §3）：桌宠右键菜单「对话」弹出。
    /// 输入 → HermesAdapter.SendMessageAsync（api_server SSE）；DeltaReceived 实时滚动回复，
    /// 完成时用权威终稿替换 delta 过程稿（防重复显示）。
    /// 样式对齐 BalloonWindow：Topmost / 透明背景 / 紧凑尺寸 ~420×360。
    /// 注意：HermesAdapter 的 SendMessageAsync / DeltaReceived / IsBusy 由 api_server 客户端重构（任务 #7）提供，
    /// 当前文件仅按契约调用，不触碰 HermesAdapter.cs。
    /// </summary>
    public class HermesChatWindow : Window
    {
        private static HermesChatWindow? _instance;

        /// <summary>单例入口：已开则 Activate/聚焦，未开则新建显示。</summary>
        public static void OpenOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }
            var win = new HermesChatWindow();
            _instance = win;
            win.Show();
        }

        private readonly TextBlock _statusBlock;
        private readonly TextBlock _outputBlock;
        private readonly ScrollViewer _outputScroll;
        private readonly TextBox _inputBox;
        private readonly Button _sendButton;

        /// <summary>构造时订阅 DeltaReceived 的 adapter 实例（关闭时按实例精确退订，防 Current 变化失效）。</summary>
        private readonly HermesAdapter? _subscribedAdapter;

        private bool _sending;              // 本地发送中 flag（IsBusy 双保险）
        private string _deltaBuffer = string.Empty; // delta 过程稿累积缓冲

        public HermesChatWindow()
        {
            Title = "Hermes 对话";
            Width = 420;
            Height = 360;
            // 自适应：允许缩放 + 最小尺寸（内部 Grid 已为 Star 结构，直接受益）
            CanResize = true;
            MinWidth = 320;
            MinHeight = 240;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // 注意：不设 IsHitTestVisible=false —— 聊天窗口必须可交互（BalloonWindow 点击穿透只适用于气泡）

            // ── 状态行（🟢/🔴 连接状态 + IsBusy/发送状态）──
            _statusBlock = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 180, 195)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            };

            // ── 回复显示区（ScrollViewer 内 TextBlock，自动换行）──
            _outputBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 235, 235, 240)),
            };
            _outputScroll = new ScrollViewer
            {
                Content = _outputBlock,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            // ── 输入行（多行输入 + 发送按钮）──
            _inputBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Height = 64,
                Background = Brushes.White,
                Foreground = Brushes.Black,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_inputBox, ScrollBarVisibility.Auto);
            _sendButton = new Button
            {
                Content = "发送",
                FontSize = 12,
                Padding = new Thickness(18, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, 0),
            };
            UiStyle.ApplyHover(_sendButton);
            _sendButton.Click += async (_, _) =>
            {
                await SendAsync();
            };

            var inputRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(_inputBox, 0);
            Grid.SetColumn(_sendButton, 1);
            inputRow.Children.Add(_inputBox);
            inputRow.Children.Add(_sendButton);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(_statusBlock, 0);
            Grid.SetRow(_outputScroll, 1);
            Grid.SetRow(inputRow, 2);
            root.Children.Add(_statusBlock);
            root.Children.Add(_outputScroll);
            root.Children.Add(inputRow);

            var frame = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 30, 30, 38)),
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 130, 130, 140)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(1),
                Child = root,
            };
            Content = frame;

            // ── 初始连接状态 + DeltaReceived 订阅 ──
            _subscribedAdapter = HermesAdapter.Current;
            SetStatus(_subscribedAdapter != null ? "🟢 Hermes 已连接" : "🔴 Hermes 未连接", _subscribedAdapter == null);
            if (_subscribedAdapter != null)
            {
                _subscribedAdapter.DeltaReceived += OnDeltaReceived;
            }
        }

        private async Task SendAsync()
        {
            try
            {
                var adapter = HermesAdapter.Current;
                if (adapter == null)
                {
                    SetStatus("🔴 Hermes 未连接", true);
                    AppendOutput("Hermes 适配器未连接，无法发送。");
                    return;
                }
                // 双保险：本地 _sending + adapter.IsBusy（防同 session 并发 chat/stream 竞态）
                if (_sending || adapter.IsBusy)
                {
                    SetStatus("⏳ 上一轮仍在进行，请稍候", false);
                    return;
                }

                var msg = _inputBox.Text?.Trim();
                if (string.IsNullOrEmpty(msg))
                {
                    return;
                }

                _sending = true;
                _sendButton.IsEnabled = false;
                _deltaBuffer = string.Empty;
                SetStatus("🟢 发送中…", false);

                try
                {
                    var reply = await adapter.SendMessageAsync(msg);
                    // 完成：用权威终稿替换 delta 过程稿（防重复显示）；adapter 未返回终稿时保留 delta 过程稿
                    _deltaBuffer = string.IsNullOrEmpty(reply) ? _deltaBuffer : reply;
                    _outputBlock.Text = _deltaBuffer;
                    _inputBox.Clear();
                    SetStatus("🟢 已完成", false);
                }
                catch (Exception ex)
                {
                    SetStatus("🔴 发送失败", true);
                    AppendOutput($"错误：{ex.Message}");
                }
                finally
                {
                    _sending = false;
                    _sendButton.IsEnabled = true;
                    ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HermesChat] SendAsync: {ex.Message}");
                _sending = false;
                _sendButton.IsEnabled = true;
            }
        }

        /// <summary>DeltaReceived 回调：追加 delta 过程稿实时滚动（任意线程 → UI 线程 Post）。</summary>
        private void OnDeltaReceived(string delta)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _deltaBuffer += delta;
                    _outputBlock.Text = _deltaBuffer;
                    ScrollToEnd();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HermesChat] OnDelta: {ex.Message}");
                }
            });
        }

        private void AppendOutput(string text)
        {
            _outputBlock.Text = string.IsNullOrEmpty(_outputBlock.Text) ? text : _outputBlock.Text + "\n" + text;
            ScrollToEnd();
        }

        private void SetStatus(string text, bool isError)
        {
            _statusBlock.Text = text;
            _statusBlock.Foreground = new SolidColorBrush(
                isError ? Color.FromArgb(255, 235, 120, 120) : Color.FromArgb(255, 180, 180, 195));
        }

        private void ScrollToEnd()
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _outputScroll.ScrollToEnd();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HermesChat] ScrollToEnd: {ex.Message}");
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_subscribedAdapter != null)
            {
                _subscribedAdapter.DeltaReceived -= OnDeltaReceived;
            }
            if (_instance == this)
            {
                _instance = null;
            }
            base.OnClosed(e);
        }
    }
}
