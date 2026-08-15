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
    /// DSH 迷你聊天对话框（RESEARCH-dsh-http-bridge.md §4.3）：桌宠右键菜单「对话」弹出（待接线）。
    /// HermesChatWindow 改造，仅换 adapter：
    ///   输入 → DshAdapter.SendMessageAsync（session.create 固定 minipet_dsh 幂等复用 + session.prompt mode=queue）；
    ///   DeltaReceived 实时滚动（WS mux 按 sessionId 过滤本会话 assistant/chunk text-delta 增量）；
    ///   完成时用权威终稿（assistant/message）替换 delta 过程稿（防重复显示）；
    ///   取消 → 取消按钮 → DshAdapter.CancelAsync（session.cancel RPC，服务端真取消）。
    /// 样式对齐 HermesChatWindow：Topmost / 透明背景 / 紧凑尺寸 ~420×360。
    /// </summary>
    public class DshChatWindow : Window
    {
        private static DshChatWindow? _instance;

        /// <summary>单例入口：已开则 Activate/聚焦，未开则新建显示。</summary>
        public static void OpenOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }
            var win = new DshChatWindow();
            _instance = win;
            win.Show();
        }

        private readonly TextBlock _statusBlock;
        private readonly TextBlock _outputBlock;
        private readonly ScrollViewer _outputScroll;
        private readonly TextBox _inputBox;
        private readonly Button _sendButton;
        private readonly Button _cancelButton;

        /// <summary>构造时订阅 DeltaReceived 的 adapter 实例（关闭时按实例精确退订，防 Current 变化失效）。</summary>
        private readonly DshAdapter? _subscribedAdapter;

        private bool _sending;              // 本地发送中 flag（IsBusy 双保险）
        private string _deltaBuffer = string.Empty; // delta 过程稿累积缓冲

        public DshChatWindow()
        {
            Title = "DSH 对话";
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

            // ── 输入行（多行输入 + 发送 + 取消按钮）──
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

            _cancelButton = new Button
            {
                Content = "取消",
                FontSize = 12,
                Padding = new Thickness(12, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false, // 仅在发送中可用（session.cancel RPC 真取消）
            };
            UiStyle.ApplyHover(_cancelButton);
            _cancelButton.Click += async (_, _) =>
            {
                await CancelAsync();
            };

            var inputRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(_inputBox, 0);
            Grid.SetColumn(_sendButton, 1);
            Grid.SetColumn(_cancelButton, 2);
            inputRow.Children.Add(_inputBox);
            inputRow.Children.Add(_sendButton);
            inputRow.Children.Add(_cancelButton);

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
            _subscribedAdapter = DshAdapter.Current;
            SetStatus(_subscribedAdapter != null ? "🟢 DSH 已连接" : "🔴 DSH 未连接", _subscribedAdapter == null);
            if (_subscribedAdapter != null)
            {
                _subscribedAdapter.DeltaReceived += OnDeltaReceived;
            }
        }

        private async Task SendAsync()
        {
            try
            {
                var adapter = DshAdapter.Current;
                if (adapter == null)
                {
                    SetStatus("🔴 DSH 未连接", true);
                    AppendOutput("DSH 适配器未连接，无法发送。");
                    return;
                }
                // 双保险：本地 _sending + adapter.IsBusy（防同 session 并发 prompt 竞态）
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
                _cancelButton.IsEnabled = true;
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
                catch (OperationCanceledException)
                {
                    SetStatus("🟡 已取消", false);
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
                    _cancelButton.IsEnabled = false;
                    ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DshChat] SendAsync: {ex.Message}");
                _sending = false;
                _sendButton.IsEnabled = true;
                _cancelButton.IsEnabled = false;
            }
        }

        /// <summary>取消当前回合：session.cancel RPC（服务端真取消；回合态由 turn/end 收敛）。</summary>
        private async Task CancelAsync()
        {
            try
            {
                var adapter = DshAdapter.Current;
                if (adapter == null)
                {
                    return;
                }
                SetStatus("🟡 取消中…", false);
                await adapter.CancelAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DshChat] CancelAsync: {ex.Message}");
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
                    Console.Error.WriteLine($"[DshChat] OnDelta: {ex.Message}");
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
                    Console.Error.WriteLine($"[DshChat] ScrollToEnd: {ex.Message}");
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
