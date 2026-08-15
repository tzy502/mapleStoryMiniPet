using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Adapters;

namespace MiniPet.Views;

/// <summary>
/// SettingsWindow Agent 接入 tab（partial 拆分）：BuildAgentTab。
/// PLAN-agent-integration §7 —— 各 adapter 开关 + 轮询/静默/staleness 阈值 + WZ 庆祝节点路径。
/// 绑定 PetSettings.AgentIntegration，手动读写（与 Balloon/Background tab 同风格）。
/// </summary>
public partial class SettingsWindow
{
    // ── Agent 接入 UI 引用 ──
    private CheckBox? _agentHermesCheck;
    private CheckBox? _agentCCCheck;
    private CheckBox? _agentCodexCheck;
    private CheckBox? _agentEffectCheck;
    private NumericUpDown? _agentHermesInterval;
    private NumericUpDown? _agentCCSilence;
    private NumericUpDown? _agentJumpDisplay;
    private NumericUpDown? _agentStaleness;
    private TextBox? _agentWzNodeInput;
    private TextBox? _agentHermesProfileInput;
    private TextBlock? _agentHermesStatusText;
    private NumericUpDown? _agentHermesPort;
    private TextBox? _agentHermesApiKey;
    private readonly Dictionary<AgentState, TextBox> _agentStateActionInputs = new();
    private readonly Dictionary<AgentState, TextBox> _agentStateBalloonInputs = new();

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
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            // ── 标题 ──
            root.Children.Add(new TextBlock
            {
                Text = "🤖 Agent 接入",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = BrushTxtPrimary,
            });
            root.Children.Add(new TextBlock
            {
                Text = "订阅 Hermes / Claude Code / Codex 的实时活动状态，用桌宠反映 agent 干活过程。\n优先级 Hermes > Claude Code > Codex。",
                FontSize = 11,
                Foreground = BrushTxtDim,
                TextWrapping = TextWrapping.Wrap,
            });

            // ── adapter 开关区 ──
            root.Children.Add(BuildAgentSectionHeader("Adapter 开关"));
            var switchPanel = new StackPanel { Spacing = 8 };
            _agentHermesCheck = MakeAgentToggle("Hermes（state.db 只读轮询）", ai.HermesEnabled);
            _agentCCCheck = MakeAgentToggle("Claude Code（开发中）", ai.CCEnabled);
            _agentCodexCheck = MakeAgentToggle("Codex（开发中）", ai.CodexEnabled);
            _agentEffectCheck = MakeAgentToggle("通用特效层（Jump 庆祝 / Failed 弹窗）", ai.EffectEnabled);
            switchPanel.Children.Add(_agentHermesCheck);
            switchPanel.Children.Add(_agentCCCheck);
            switchPanel.Children.Add(_agentCodexCheck);
            switchPanel.Children.Add(_agentEffectCheck);
            root.Children.Add(switchPanel);

            // ── 阈值区 ──
            root.Children.Add(BuildAgentSectionHeader("阈值（秒）"));
            var threshGrid = new Grid();
            for (int i = 0; i < 4; i++)
            {
                threshGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
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
                Text = ai.HermesApiServerKey ?? string.Empty,
                Background = BrushInputBg,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Width = 320,
            };
            keyWrap.Children.Add(_agentHermesApiKey);
            connPanel.Children.Add(keyWrap);
            root.Children.Add(connPanel);

            // ── Hermes Profile（profile 名 + 同步状态显示） ──
            root.Children.Add(BuildAgentSectionHeader("Hermes Profile"));
            var profileRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _agentHermesProfileInput = new TextBox
            {
                Watermark = "默认空 = 默认 profile",
                FontSize = 12,
                Text = ai.HermesProfile ?? string.Empty,
                Background = BrushInputBg,
                BorderBrush = new SolidColorBrush(InputBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Width = 240,
            };
            _agentHermesStatusText = new TextBlock
            {
                Text = "⚪ 未同步",
                FontSize = 11,
                Foreground = BrushTxtDim,
                VerticalAlignment = VerticalAlignment.Center,
            };
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

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                row.Children.Add(new TextBlock
                {
                    Text = StateDisplayName(st),
                    FontSize = 12,
                    Width = 110,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = BrushTxtPrimary,
                });
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
                Background = BrushInputBg,
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
            saveBtn.Click += (_, _) => SaveAgentIntegration();
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);

            scroll.Content = root;
            return new Border { Child = scroll };
        }
        catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] BuildAgentTab: {ex.Message}"); return new Border(); }
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
        nud.Width = 140;
        nud.Foreground = BrushTxtPrimary;
        nud.Background = BrushInputBg;

        var wrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 14, 0) };
        wrap.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = BrushTxtDim });
        wrap.Children.Add(nud);
        return wrap;
    }

    /// <summary>构造一个 7 态映射用的输入框（Action / Balloon 共用）。</summary>
    private static TextBox MakeAgentStateTextBox(string watermark, string text)
    {
        return new TextBox
        {
            Watermark = watermark,
            FontSize = 12,
            Text = text,
            Background = BrushInputBg,
            BorderBrush = new SolidColorBrush(InputBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Width = 220,
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
}
