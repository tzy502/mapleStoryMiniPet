using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Adapters;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow Agent 接入 tab（partial 拆分）：BuildAgentTab（Hermes，含 DSH 监控区块）+
/// BuildDshChatTab（DeepSeek Harness 对话入口页）。
/// PLAN-agent-integration §7 —— 各 adapter 开关 + 轮询/静默/staleness 阈值 + WZ 庆祝节点路径；
/// DSH 监控 2026-08-16 从「常规」子 tab 撤回 Hermes tab 原位置（与 Hermes 设置同区）。
/// 绑定 PetSettings.AgentIntegration，手动读写（与 Balloon/Background tab 同风格）。
/// </summary>
public partial class SettingsWindow
{
    // ── Agent 接入 UI 引用 ──
    private CheckBox? _agentHermesCheck;
    private CheckBox? _agentCCCheck;
    private CheckBox? _agentCodexCheck;
    private CheckBox? _agentEffectCheck;
    private CheckBox? _agentDshCheck;
    private NumericUpDown? _agentHermesInterval;
    private NumericUpDown? _agentCCSilence;
    private NumericUpDown? _agentJumpDisplay;
    private NumericUpDown? _agentStaleness;
    private TextBox? _agentWzNodeInput;
    private TextBox? _agentHermesProfileInput;
    private TextBlock? _agentHermesStatusText;
    private NumericUpDown? _agentHermesPort;
    private TextBox? _agentHermesApiKey;
    private TextBox? _agentDshHostInput;
    private NumericUpDown? _agentDshPort;
    private TextBox? _agentDshWorkspaceInput;
    private TextBlock? _agentDshStatusText;
    // ── DeepSeek Harness 对话子 tab UI 引用 ──
    private TextBlock? _dshChatStatusText;
    private Button? _dshChatOpenBtn;
    private TextBlock? _dshChatHint;
    private readonly Dictionary<AgentState, TextBox> _agentStateActionInputs = new();
    private readonly Dictionary<AgentState, TextBox> _agentStateBalloonInputs = new();

    /// <summary>
    /// 通用设置子 tab（2026-08-16：从 Hermes tab 抽离的通用配置——Adapter 开关 / 阈值 / 7 态映射 / WZ 节点，
    /// 排在 Hermes 之前；Hermes tab 只留连接与 Profile，Deepseek harness tab 只留 DSH 内容）。
    /// </summary>
    private Border BuildGeneralTab()
    {
        try
        {
            var ai = _settings.AgentIntegration ?? new AgentIntegrationConfig();
            // 容错：旧配置缺字段时回填默认
            if (_settings.AgentIntegration == null)
            {
                _settings.AgentIntegration = ai;
            }

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0),
            };

            var root = new StackPanel
            {
                Margin = new Thickness(20, 16, 20, 16),
                Spacing = 18,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // ── 标题 ──
            root.Children.Add(new TextBlock
            {
                Text = "⚙️ 通用设置",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = BrushTxtPrimary,
            });
            root.Children.Add(new TextBlock
            {
                Text = "Agent 接入通用配置（所有 adapter 共用）：开关、阈值、7 态反馈映射与庆祝节点。",
                FontSize = 11,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });

            // ── Adapter 开关 ──
            root.Children.Add(BuildAgentSectionHeader("Adapter 开关"));
            var switchRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
            };
            _agentHermesCheck = MakeAgentToggle("Hermes", ai.HermesEnabled);
            _agentCCCheck = MakeAgentToggle("Claude Code", ai.CCEnabled);
            _agentCodexCheck = MakeAgentToggle("Codex", ai.CodexEnabled);
            _agentEffectCheck = MakeAgentToggle("特效层", ai.EffectEnabled);
            switchRow.Children.Add(_agentHermesCheck);
            switchRow.Children.Add(_agentCCCheck);
            switchRow.Children.Add(_agentCodexCheck);
            switchRow.Children.Add(_agentEffectCheck);
            root.Children.Add(switchRow);

            // ── 阈值区 ──
            root.Children.Add(BuildAgentSectionHeader("阈值（秒）"));
            var threshGrid = new Grid();
            // 4 列等分（Star）：阈值输入自适应占满整行宽度
            for (int i = 0; i < 4; i++)
            {
                threshGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            }
            _agentHermesInterval = new NumericUpDown();
            _agentCCSilence = new NumericUpDown();
            _agentJumpDisplay = new NumericUpDown();
            _agentStaleness = new NumericUpDown();
            var c0 = MakeAgentNumericBox("Hermes 轮询周期", ai.HermesPollIntervalSeconds, 1, 3600, _agentHermesInterval);
            var c1 = MakeAgentNumericBox("CC 静默窗", ai.CCSilenceSeconds, 1, 600, _agentCCSilence);
            var c2 = MakeAgentNumericBox("Jump 庆祝时长", ai.JumpDisplaySeconds, 1, 60, _agentJumpDisplay);
            var c3 = MakeAgentNumericBox("Staleness 衰减", ai.StalenessSeconds, 1, 600, _agentStaleness);
            Grid.SetColumn(c0, 0);
            Grid.SetColumn(c1, 1);
            Grid.SetColumn(c2, 2);
            Grid.SetColumn(c3, 3);
            threshGrid.Children.Add(c0);
            threshGrid.Children.Add(c1);
            threshGrid.Children.Add(c2);
            threshGrid.Children.Add(c3);
            root.Children.Add(threshGrid);

            // ── 7 态映射配置区 ──
            root.Children.Add(BuildAgentSectionHeader("7 态 状态→反馈 映射"));
            root.Children.Add(new TextBlock
            {
                Text = "每态可配 Action（实体动作名，空=不切动作）+ Balloon（气泡文本，空=不弹）。默认对齐原 EffectLayerService。",
                FontSize = 10,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });
            var feedbackMap = ai.AgentStateFeedback ?? AgentIntegrationConfig.DefaultAgentStateFeedback();
            foreach (var st in new[] { AgentState.Run, AgentState.Review, AgentState.Wave, AgentState.Jump, AgentState.Failed, AgentState.Waiting, AgentState.Idle })
            {
                feedbackMap.TryGetValue(st, out var fb);
                var actionTb = MakeAgentStateTextBox("动作名，空=不切", fb?.Action ?? string.Empty);
                var balloonTb = MakeAgentStateTextBox("气泡文本，空=不弹", fb?.Balloon ?? string.Empty);
                _agentStateActionInputs[st] = actionTb;
                _agentStateBalloonInputs[st] = balloonTb;

                var row = new Grid
                {
                    ColumnSpacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 状态名
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // Action（占一半剩余宽度）
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // Balloon（占一半剩余宽度）
                var stateLabel = new TextBlock
                {
                    Text = StateDisplayName(st),
                    FontSize = 12,
                    Width = 110,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = BrushTxtPrimary,
                };
                Grid.SetColumn(stateLabel, 0);
                Grid.SetColumn(actionTb, 1);
                Grid.SetColumn(balloonTb, 2);
                row.Children.Add(stateLabel);
                row.Children.Add(actionTb);
                row.Children.Add(balloonTb);
                root.Children.Add(row);
            }

            // ── WZ 庆祝节点路径 ──
            root.Children.Add(BuildAgentSectionHeader("WZ 庆祝节点路径（可选）"));
            var wzHint = new TextBlock
            {
                Text = "手填覆盖 Jump 庆祝动画的 WZ 节点路径。留空 → 自动探测（Effect/、UI/Basic emotion、Item/）。",
                FontSize = 10,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(wzHint);
            _agentWzNodeInput = new TextBox
            {
                Watermark = "例：Effect/celebrate",
                FontSize = 12,
                Text = ai.WzCelebrationNodePath ?? string.Empty,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
            };
            root.Children.Add(_agentWzNodeInput);

            // ── 保存按钮 ──
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var saveBtn = new Button
            {
                Content = "💾 保存",
                FontSize = 11,
                Padding = new Thickness(20, 8),
                Background = BrushAccent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
            };
            UiStyle.ApplyHover(saveBtn);
            saveBtn.Click += (_, _) => SaveAgentIntegration();
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);

            scroll.Content = root;
            return new Border { Child = scroll };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildGeneralTab: {ex.Message}"); return new Border(); }
    }

    private Border BuildAgentTab()
    {
        try
        {
            var ai = _settings.AgentIntegration ?? new AgentIntegrationConfig();
            // 容错：旧配置缺字段时回填默认
            if (_settings.AgentIntegration == null)
            {
                _settings.AgentIntegration = ai;
            }

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0),
            };

            var root = new StackPanel
            {
                Margin = new Thickness(20, 16, 20, 16),
                Spacing = 18,
                // 自适应：去掉 MaxWidth=720 上限，内容随窗口宽度伸缩（阈值行/映射行均 Star 等分，不受影响）
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // ── 标题 ──
            root.Children.Add(new TextBlock
            {
                Text = "🔗 Hermes 接入",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = BrushTxtPrimary,
            });
            root.Children.Add(new TextBlock
            {
                Text = "Hermes（api_server）连接与 Profile 配置；通用行为（Adapter 开关 / 阈值 / 7 态映射）在「通用」tab。",
                FontSize = 11,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });

            // ── Hermes 连接（api_server） ──
            root.Children.Add(BuildAgentSectionHeader("Hermes 连接（api_server）"));
            var connPanel = new StackPanel { Spacing = 10 };
            _agentHermesPort = new NumericUpDown();
            connPanel.Children.Add(MakeAgentNumericBox("端口（0 = 自动发现，默认 8642）", ai.HermesPort, 0, 65535, _agentHermesPort));
            var keyWrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            keyWrap.Children.Add(new TextBlock { Text = "API Server Key（api_server Bearer 本地凭证，仅本机勿备份同步）", FontSize = 10, Foreground = BrushTxtDim });
            _agentHermesApiKey = new TextBox
            {
                Watermark = "Hermes config.yaml 的 api_server key",
                FontSize = 12,
                TextAlignment = TextAlignment.Left,
                Text = ai.HermesApiServerKey ?? string.Empty,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
            };
            keyWrap.Children.Add(_agentHermesApiKey);
            connPanel.Children.Add(keyWrap);
            root.Children.Add(connPanel);

            // ── Hermes Profile（profile 名 + 同步状态显示） ──
            root.Children.Add(BuildAgentSectionHeader("Hermes Profile"));
            var profileRow = new Grid
            {
                ColumnSpacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            profileRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // profile 名输入（占满剩余宽度）
            profileRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 同步状态
            _agentHermesProfileInput = new TextBox
            {
                Watermark = "默认空 = 默认 profile",
                FontSize = 12,
                Text = ai.HermesProfile ?? string.Empty,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
            };
            _agentHermesStatusText = new TextBlock
            {
                Text = "⚪ 未同步",
                FontSize = 11,
                Foreground = BrushTxtDim,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_agentHermesProfileInput, 0);
            Grid.SetColumn(_agentHermesStatusText, 1);
            profileRow.Children.Add(_agentHermesProfileInput);
            profileRow.Children.Add(_agentHermesStatusText);
            root.Children.Add(profileRow);

            // 挂 HermesAdapter 同步状态回调（adapter 生命周期跟随 App，静态 Current 单例）。
            if (HermesAdapter.Current is { } hermesAdapter)
            {
                hermesAdapter.ConnStatusCallback += UpdateHermesConnStatus;
            }
            root.Children.Add(new TextBlock
            {
                Text = "默认空 = 默认 profile；state.db 路径按 ~/.hermes/[profiles/<p>/]state.db 解析（根尊重 HERMES_HOME）。",
                FontSize = 10,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });

            // ── 保存按钮 ──
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var saveBtn = new Button
            {
                Content = "💾 保存",
                FontSize = 11,
                Padding = new Thickness(20, 8),
                Background = BrushAccent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
            };
            UiStyle.ApplyHover(saveBtn);
            saveBtn.Click += (_, _) => SaveAgentIntegration();
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);

            scroll.Content = root;
            return new Border { Child = scroll };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildAgentTab: {ex.Message}"); return new Border(); }
    }

    /// <summary>
    /// DeepSeek Harness 对话子 tab（2026-08-16 由占位改为实际功能页）：
    /// 与桌宠右键菜单「🤖 DSH 对话」同链路——DshChatWindow.OpenOrActivate（单例，已开则聚焦）。
    /// DshAdapter 未装配（DshEnabled 关或 App 启动未装配）时按钮降级为说明文字：
    /// 设置窗口无 EventBus 依赖无法就地自动装配，提示用户先经桌宠右键菜单装配或开启 DSH 监控后重启。
    /// </summary>
    private Border BuildDshChatTab()
    {
        try
        {
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0),
            };

            var root = new StackPanel
            {
                Margin = new Thickness(20, 16, 20, 16),
                Spacing = 18,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // ── 标题 ──
            root.Children.Add(new TextBlock
            {
                Text = "DeepSeek Harness 对话",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = BrushTxtPrimary,
            });
            root.Children.Add(new TextBlock
            {
                Text = "连接本机 DeepSeek Harness（DeepSeek Harness Web GUI 后端，loopback 127.0.0.1:3080，免鉴权），打开迷你对话窗口即可与 DSH 会话对话（session.create 固定 minipet_dsh 复用）。\n对话链路独立于「DSH 监控」观察开关；仅当本机 DSH 未运行时窗口显示未连接。",
                FontSize = 11,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });

            // ── DSH 监控（DeepSeek Harness，本机 127.0.0.1:3080；2026-08-16 从 Hermes tab 移入本 tab） ──
            root.Children.Add(BuildAgentSectionHeader("DSH 监控（DeepSeek Harness，本机 127.0.0.1:3080）"));
            var ai = _settings.AgentIntegration ?? new AgentIntegrationConfig();
            var dshPanel = new StackPanel { Spacing = 10 };
            _agentDshCheck = MakeAgentToggle("DSH 监控（默认开启，DeepSeek Harness 执行状态反映；仅本机 loopback；DSH 未运行时不打扰，桌宠保持静默）", ai.DshEnabled);
            dshPanel.Children.Add(_agentDshCheck);
            var dshHostRow = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            dshHostRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // Host 输入（占满剩余宽度）
            dshHostRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // 同步状态
            _agentDshHostInput = new TextBox
            {
                Watermark = "127.0.0.1",
                FontSize = 12,
                Text = ai.DshHost ?? "127.0.0.1",
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
            };
            _agentDshStatusText = new TextBlock
            {
                Text = "⚪ 未同步（未开启或 DSH 未运行）",
                FontSize = 11,
                Foreground = BrushTxtDim,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_agentDshHostInput, 0);
            Grid.SetColumn(_agentDshStatusText, 1);
            dshHostRow.Children.Add(_agentDshHostInput);
            dshHostRow.Children.Add(_agentDshStatusText);
            dshPanel.Children.Add(dshHostRow);
            _agentDshPort = new NumericUpDown();
            dshPanel.Children.Add(MakeAgentNumericBox("端口（默认 3080，dsh --profile web）", ai.DshPort == 0 ? 3080 : ai.DshPort, 1, 65535, _agentDshPort));
            var wsWrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            wsWrap.Children.Add(new TextBlock { Text = "会话工作区 cwd（可选，留空 = MiniPet 进程当前目录）", FontSize = 10, Foreground = BrushTxtDim });
            _agentDshWorkspaceInput = new TextBox
            {
                Watermark = "例：/Users/.../mapleStoryMiniPet/.worktrees/dsh",
                FontSize = 12,
                Text = ai.DshWorkspace ?? string.Empty,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
            };
            wsWrap.Children.Add(_agentDshWorkspaceInput);
            dshPanel.Children.Add(wsWrap);
            root.Children.Add(dshPanel);
            root.Children.Add(new TextBlock
            {
                Text = "观察链路：WS events.mux 订阅（session/event 驱动 7 态），隐私只消费事件视图，绝不拉取 content/密钥。\n保存后重启生效；「🤖 DSH 对话」无需开启观察即可用——点击时自动装配，仅当 DSH 未运行时窗口显示未连接。",
                FontSize = 10,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });

            // 挂 DshAdapter 同步状态回调（adapter 生命周期跟随 App，静态 Current 单例）。
            if (DshAdapter.Current is { } dshAdapter2)
            {
                dshAdapter2.ConnStatusCallback += UpdateDshConnStatus;
            }

            // ── 连接状态提示 ──
            var dsh = DshAdapter.Current;
            _dshChatStatusText = new TextBlock
            {
                Text = dsh != null && dsh.IsConnected
                    ? "🟢 DSH 已连接（127.0.0.1:3080）"
                    : "🔴 DSH 未连接（适配器未装配或 DSH 未运行）",
                FontSize = 12,
                Foreground = BrushTxtSecondary,
            };
            root.Children.Add(_dshChatStatusText);

            // ── 打开对话窗口按钮 ──
            _dshChatOpenBtn = new Button
            {
                Content = "💬 打开 DSH 对话窗口",
                FontSize = 12,
                Padding = new Thickness(22, 10),
                Background = BrushAccent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            UiStyle.ApplyHover(_dshChatOpenBtn);
            _dshChatOpenBtn.Click += (_, _) => OpenDshChatWindow();
            // 缺少必要依赖（DshAdapter 未装配，设置窗口无 EventBus 无法就地装配）→ 按钮禁用，降级为说明文字
            _dshChatOpenBtn.IsEnabled = dsh != null;
            root.Children.Add(_dshChatOpenBtn);

            _dshChatHint = new TextBlock
            {
                Text = dsh != null
                    ? "点击后打开 DSH 对话窗口（单例，已开则聚焦）；连接状态在窗口状态行实时显示。"
                    : "⚠️ DSH 适配器未装配（设置窗口无 EventBus，无法就地自动装配）。\n请先在桌宠右键菜单点一次「🤖 DSH 对话」自动装配，或开启本页「Hermes」tab 的 DSH 监控后重启 MiniPet。",
                FontSize = 10,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(_dshChatHint);

            scroll.Content = root;
            return new Border { Child = scroll };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildDshChatTab: {ex.Message}"); return new Border(); }
    }

    /// <summary>打开 DSH 对话窗口（与桌宠右键菜单「🤖 DSH 对话」同链路：DshChatWindow.OpenOrActivate 单例）。</summary>
    private void OpenDshChatWindow()
    {
        try
        {
            if (DshAdapter.Current == null)
            {
                // 降级：按钮已禁用，双保险——点击也只会提示
                if (_dshChatHint != null)
                {
                    _dshChatHint.Text = "⚠️ DSH 适配器未装配。请先在桌宠右键菜单点一次「🤖 DSH 对话」自动装配，或开启 DSH 监控后重启 MiniPet。";
                }
                return;
            }
            DshChatWindow.OpenOrActivate();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] OpenDshChatWindow: {ex.Message}"); }
    }

    private static TextBlock BuildAgentSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushTxtSecondary,
            Margin = new Thickness(0, 2, 0, 0),
        };
    }

    private static CheckBox MakeAgentToggle(string label, bool initial)
    {
        return new CheckBox
        {
            Content = label,
            IsChecked = initial,
            FontSize = 12,
            Foreground = BrushTxtPrimary,
        };
    }

    /// <summary>构造一个带标签的数值输入框（标签在上、NumericUpDown 在下），并把传入的 nud 填好值返回。</summary>
    private static StackPanel MakeAgentNumericBox(string label, int initial, int min, int max, NumericUpDown nud)
    {
        nud.Value = initial;
        nud.Minimum = min;
        nud.Maximum = max;
        nud.FormatString = "0";
        nud.Increment = 1;
        nud.FontSize = 12;
        nud.HorizontalContentAlignment = HorizontalAlignment.Left;
        nud.TextAlignment = TextAlignment.Left;
        nud.Foreground = Brushes.Black;
        nud.Background = Brushes.White;
        // Avalonia NumericUpDown.TextAlignment 属性不保证传递到模板内 PART_TextBox（实测无效），
        // 必须等模板应用后直接操作内部 TextBox 强制左对齐（与普通 TextBox 样式一致，胶水 2026-08-15）。
        nud.Loaded += (_, _) =>
        {
            try
            {
                var tb = FindTemplateTextBox(nud);
                if (tb != null)
                {
                    tb.TextAlignment = TextAlignment.Left;
                    tb.HorizontalContentAlignment = HorizontalAlignment.Left;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SettingsWindow.Agent] MakeAgentNumericBox 左对齐失败: {ex.Message}");
            }
        };

        var wrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 14, 0) };
        wrap.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtDim });
        wrap.Children.Add(nud);
        return wrap;
    }

    /// <summary>在控件模板视觉树中查找第一个 TextBox（NumericUpDown 内部 PART_TextBox 用）。</summary>
    private static TextBox? FindTemplateTextBox(Control root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is TextBox tb) return tb;
            if (child is Control c)
            {
                var found = FindTemplateTextBox(c);
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>构造一个 7 态映射用的输入框（Action / Balloon 共用）。</summary>
    private static TextBox MakeAgentStateTextBox(string watermark, string text)
    {
        return new TextBox
        {
            Watermark = watermark,
            FontSize = 12,
            Text = text,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
        };
    }

    /// <summary>AgentState 显示名（英文枚举 + 中文说明）。</summary>
    private static string StateDisplayName(AgentState state)
    {
        switch (state)
        {
            case AgentState.Run: return "Run 干活";
            case AgentState.Review: return "Review 思考";
            case AgentState.Wave: return "Wave 完成";
            case AgentState.Jump: return "Jump 庆祝";
            case AgentState.Failed: return "Failed 失败";
            case AgentState.Waiting: return "Waiting 等待";
            case AgentState.Idle: return "Idle 空闲";
            default: return state.ToString();
        }
    }

    private void SaveAgentIntegration()
    {
        try
        {
            var ai = _settings.AgentIntegration ?? new AgentIntegrationConfig();
            if (_agentHermesCheck != null) { ai.HermesEnabled = _agentHermesCheck.IsChecked ?? false; }
            if (_agentCCCheck != null) { ai.CCEnabled = _agentCCCheck.IsChecked ?? false; }
            if (_agentCodexCheck != null) { ai.CodexEnabled = _agentCodexCheck.IsChecked ?? false; }
            if (_agentEffectCheck != null) { ai.EffectEnabled = _agentEffectCheck.IsChecked ?? false; }
            if (_agentHermesInterval != null && _agentHermesInterval.Value.HasValue) { ai.HermesPollIntervalSeconds = (int)Math.Max(1, _agentHermesInterval.Value.Value); }
            if (_agentCCSilence != null && _agentCCSilence.Value.HasValue) { ai.CCSilenceSeconds = (int)Math.Max(1, _agentCCSilence.Value.Value); }
            if (_agentJumpDisplay != null && _agentJumpDisplay.Value.HasValue) { ai.JumpDisplaySeconds = (int)Math.Max(1, _agentJumpDisplay.Value.Value); }
            if (_agentStaleness != null && _agentStaleness.Value.HasValue) { ai.StalenessSeconds = (int)Math.Max(1, _agentStaleness.Value.Value); }
            if (_agentWzNodeInput != null)
            {
                var p = _agentWzNodeInput.Text?.Trim();
                ai.WzCelebrationNodePath = string.IsNullOrEmpty(p) ? null : p;
            }
            if (_agentHermesProfileInput != null)
            {
                var p = _agentHermesProfileInput.Text?.Trim();
                ai.HermesProfile = string.IsNullOrEmpty(p) ? null : p;
            }
            if (_agentHermesPort != null && _agentHermesPort.Value.HasValue)
            {
                ai.HermesPort = (int)Math.Max(0, _agentHermesPort.Value.Value);
            }
            if (_agentHermesApiKey != null)
            {
                var k = _agentHermesApiKey.Text?.Trim();
                ai.HermesApiServerKey = k ?? string.Empty;
            }
            // DSH 接入（开关 / Host / 端口 / 工作区）
            if (_agentDshCheck != null) { ai.DshEnabled = _agentDshCheck.IsChecked ?? false; }
            if (_agentDshHostInput != null)
            {
                var h = _agentDshHostInput.Text?.Trim();
                ai.DshHost = string.IsNullOrEmpty(h) ? "127.0.0.1" : h;
            }
            if (_agentDshPort != null && _agentDshPort.Value.HasValue)
            {
                ai.DshPort = (int)Math.Max(1, _agentDshPort.Value.Value);
            }
            if (_agentDshWorkspaceInput != null)
            {
                var w = _agentDshWorkspaceInput.Text?.Trim();
                ai.DshWorkspace = w ?? string.Empty;
            }
            // 7 态映射：读现有 AgentStateFeedback 或默认，改后存回
            var map = ai.AgentStateFeedback ?? AgentIntegrationConfig.DefaultAgentStateFeedback();
            foreach (var kv in _agentStateActionInputs)
            {
                var st = kv.Key;
                var actionText = kv.Value.Text?.Trim();
                var balloonText = _agentStateBalloonInputs.TryGetValue(st, out var balloonTb) ? balloonTb.Text?.Trim() : null;
                if (!map.TryGetValue(st, out var fb))
                {
                    fb = new AgentStateFeedback();
                    map[st] = fb;
                }
                fb.Action = string.IsNullOrEmpty(actionText) ? null : actionText;
                fb.Balloon = string.IsNullOrEmpty(balloonText) ? null : balloonText;
            }
            ai.AgentStateFeedback = map;
            _settings.AgentIntegration = ai;
            _config.SaveSettings(_settings);
            if (_statusBarText != null) { _statusBarText.Text = "Agent 接入设置已保存"; }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] SaveAgentIntegration: {ex.Message}"); }
    }

    /// <summary>更新 Hermes 同步状态显示（HermesAdapter 回调）。UI 线程安全。</summary>
    public void UpdateHermesConnStatus(string text, bool connected)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateHermesConnStatus(text, connected));
            return;
        }
        if (_agentHermesStatusText != null)
        {
            _agentHermesStatusText.Text = (connected ? "🟢 " : "🔴 ") + text;
        }
    }

    /// <summary>更新 DSH 同步状态显示（DshAdapter 回调）。UI 线程安全。</summary>
    public void UpdateDshConnStatus(string text, bool connected)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateDshConnStatus(text, connected));
            return;
        }
        if (_agentDshStatusText != null)
        {
            _agentDshStatusText.Text = (connected ? "🟢 " : "🔴 ") + text;
        }
    }
}
