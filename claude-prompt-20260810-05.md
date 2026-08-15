# MiniPet — HermesAdapter 升级：跟官方桌宠一样走 Gateway WS 事件订阅（最终版）

> 提示语版本：claude-prompt-20260810-05（最终裁决版，单 Agent 串行）
> 日期：2026-08-13
> 对应技术方案：MiniPet/docs/adr/0006-hermes-gateway-event-subscription.md（需同步更新）
> 前序版本：claude-prompt-20260810-04.md（作废——远程支持/多链路降级被否）
> 裁决来源：胶水拍板「跟官方桌宠一样走」——MiniPet 复刻官方桌面端 pet 状态链，WS 事件订阅为主链路

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 主项目：`MiniPet/MiniPet.csproj`
- .NET SDK（dotnet 是**可执行文件**，必须引号调用）：
  ```bash
  export DOTNET_ROOT="/Volumes/SSD/C#"
  export PATH="$DOTNET_ROOT:$PATH"
  "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore
  ```
- 编译必须 0 错误才交付

## 背景与最终决策

MiniPet 桌宠需实时感知 Hermes Agent 活动（工具执行/思考/等待/出错/完成 → 切换冒险岛动画）。

**最终决策（胶水 2026-08-13 拍板，不可再议）**：**跟官方桌宠一样走**——MiniPet 作为 Hermes 客户端，连 gateway WebSocket 收事件流，复刻官方桌面端 `apps/desktop/src/store/pet.ts` 的 `setPetActivity → derivePetState` 状态链。

**否决的路线**（不再讨论）：
- ❌ state.db 主链路（官方不用，只作临时兜底）
- ❌ REST 轮询（不引入）
- ❌ 远程直连（Hermes 非 loopback 强制 OAuth gate，`--insecure` 已失效，锁死本地）

## 权威参考（源码核实）

### 官方桌宠状态链（照抄对象）

`apps/desktop/src/store/pet.ts`：
```
gateway 事件帧（method:"event"）→ setPetActivity({toolRunning, reasoning, error, ...}) → derivePetState(activity) → PetState
```
优先级：error > celebrate > justCompleted > awaitingInput > toolRunning > reasoning > busy > idle

### WS 端点与协议

| 项 | 值 | 源码 |
|----|-----|------|
| 端点 | `ws://127.0.0.1:{port}/api/ws?token={token}` | `hermes_cli/web_server.py:16241`（鉴权包装）+ `tui_gateway/ws.py`（handler） |
| 协议 | newline-delimited JSON-RPC（双向） | `tui_gateway/ws.py` 头注释 |
| 首帧 | 服务端推 `gateway.ready` | `tui_gateway/ws.py:314-326` |
| 事件帧 | `{"jsonrpc":"2.0","method":"event","params":{"type":"tool.start",...}}` | 同上 |
| 本地鉴权 | **必须 `?token=`**（loopback 无 token → `no_credential` 拒绝） | `hermes_cli/web_server.py` `_ws_auth_reason` |

### Token 获取（关键运维点）

- `_SESSION_TOKEN` = `HERMES_DASHBOARD_SESSION_TOKEN` 环境变量，未设则每次启动 `secrets.token_urlsafe(32)` 随机生成 → **重启即失联**
- **必须写进文档让用户固定设置 env**，MiniPet 配置里填同一值

### 事件 → AgentState 映射（对齐官方 pet.ts 优先级）

| 事件 type | AgentState | 说明 |
|-----------|-----------|------|
| `error` | Failed | 最高优先 |
| `clarify.request` / `approval.request` / `secret.request` / `sudo.request` | Waiting | 等待用户 |
| `tool.start` | Run | 工具执行 |
| `reasoning.available` | Review | 思考中 |
| `message.start` / `message.delta` | Run | busy |
| `message.complete`（无 error） | Wave | 回合完成 |
| 其余（gateway.ready/status.update/delta 流） | 不映射 | — |

## 本次任务（单 Agent 串行）

### 任务 1 — AgentIntegrationConfig 扩展（AppConfig.cs）

**文件**：`MiniPet/Models/AppConfig.cs`（`AgentIntegrationConfig` 类，现 L58-78 末尾追加）

```csharp
// ── Hermes Gateway WS 连接配置（最终版：跟官方桌宠一样走）──

/// <summary>Hermes gateway 端口。0 = 自动发现（扫描 hermes_cli serve 进程，按 profile 过滤）；手填优先。</summary>
public int HermesPort { get; set; } = 0;

/// <summary>WS 鉴权 token（?token= 参数）。= HERMES_DASHBOARD_SESSION_TOKEN 环境变量值，必须固定设置否则重启失联。</summary>
public string HermesToken { get; set; } = string.Empty;

/// <summary>监控的 Hermes profile 名（默认空 = 默认 profile；用于 state.db 路径解析与端口发现过滤）。</summary>
public string HermesProfile { get; set; } = string.Empty;

/// <summary>WS 连接超时（秒，默认 5）。</summary>
public int HermesWsConnectTimeoutSeconds { get; set; } = 5;

/// <summary>WS 断线重连间隔（秒，默认 3）。</summary>
public int HermesWsReconnectSeconds { get; set; } = 3;

/// <summary>WS 事件 staleness 阈值（秒，超此无事件→Idle，默认 10）。</summary>
public int HermesWsStalenessSeconds { get; set; } = 10;
```

### 任务 2 — HermesAdapter.cs WS 客户端改造（核心）

**文件**：`MiniPet/Services/Adapters/HermesAdapter.cs`

**2a. 新增字段 + 构造注入配置**（保留原有构造函数兼容）：

```csharp
// ===== WS 状态 =====
private System.Net.WebSockets.ClientWebSocket? _ws;
private System.Threading.CancellationTokenSource? _wsCts;
private Task? _wsTask;
private bool _wsConnected;
private DateTime _lastEventTime = DateTime.MinValue;
private DispatcherTimer? _stalenessTimer;

// ===== 配置 =====
private readonly int _port;
private readonly string _token;
private readonly string _profile;
private int _wsConnectTimeoutMs;
private int _wsReconnectMs;
private int _wsStalenessSec;

// 新增构造函数（保留 EventBus-only / connStr 构造兼容）
public HermesAdapter(EventBus eventBus) : this(eventBus, BuildDefaultConnStr(), new AgentIntegrationConfig()) { }
public HermesAdapter(EventBus eventBus, string connStr) : this(eventBus, connStr, new AgentIntegrationConfig()) { }

public HermesAdapter(EventBus eventBus, string connStr, AgentIntegrationConfig ai)
{
    _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    _connStr = connStr ?? throw new ArgumentNullException(nameof(connStr));
    _port = ai.HermesPort;
    _token = ai.HermesToken ?? string.Empty;
    _profile = ai.HermesProfile ?? string.Empty;
    _wsConnectTimeoutMs = Math.Max(1, ai.HermesWsConnectTimeoutSeconds) * 1000;
    _wsReconnectMs = Math.Max(1, ai.HermesWsReconnectSeconds) * 1000;
    _wsStalenessSec = Math.Max(3, ai.HermesWsStalenessSeconds);
}
```

**2b. WS 连接 + 接收循环（带重连）**：

```csharp
/// <summary>启动 WS 长连接订阅（带重连循环）。主链路。</summary>
private async Task StartWsAsync(int port, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            using var client = new System.Net.WebSockets.ClientWebSocket();
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            var url = $"ws://127.0.0.1:{port}/api/ws";
            if (!string.IsNullOrEmpty(_token))
                url += $"?token={Uri.EscapeDataString(_token)}";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_wsConnectTimeoutMs);
            await client.ConnectAsync(new Uri(url), cts.Token);

            _ws = client;
            _wsConnected = true;
            _lastEventTime = DateTime.Now;
            Console.WriteLine($"[HermesAdapter] WS 已连接 {url}");
            NotifyConnStatus($"已连接 :{port}", true);

            var buf = new byte[65536];
            while (!token.IsCancellationRequested && client.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await client.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                var text = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    HandleWsFrame(line.Trim());
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HermesAdapter] WS 异常: {ex.Message}");
        }
        finally
        {
            _wsConnected = false;
            _ws = null;
            NotifyConnStatus($"断开，{_wsReconnectMs / 1000}s 后重连", false);
        }

        try { await Task.Delay(_wsReconnectMs, token); }
        catch (TaskCanceledException) { break; }
    }
}
```

**2c. 事件帧解析 → AgentState 映射**：

```csharp
private void HandleWsFrame(string json)
{
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var m) || m.GetString() != "event") return;
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("type", out var t)) return;
        var eventType = t.GetString() ?? "";
        _lastEventTime = DateTime.Now;

        var state = MapEventTypeToAgentState(eventType);
        if (state.HasValue)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                PublishState(state.Value, $"ws: {eventType}"));
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] HandleWsFrame: {ex.Message}"); }
}

/// <summary>Gateway 事件 type → AgentState（7 态，对齐官方 pet.ts 优先级）。</summary>
private static AgentState? MapEventTypeToAgentState(string eventType)
{
    return eventType switch
    {
        "error" => AgentState.Failed,
        "clarify.request" or "approval.request" or "secret.request" or "sudo.request" => AgentState.Waiting,
        "tool.start" => AgentState.Run,
        "reasoning.available" => AgentState.Review,
        "message.start" or "message.delta" => AgentState.Run,
        "message.complete" => AgentState.Wave,
        _ => null,   // gateway.ready / status.update / *.delta 流 / tool.complete 不映射
    };
}
```

**2d. Staleness 衰减**：

```csharp
private void StartStalenessCheck()
{
    _stalenessTimer?.Stop();
    _stalenessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    _stalenessTimer.Tick += (_, _) =>
    {
        try
        {
            if (_wsConnected && _lastEventTime != DateTime.MinValue
                && (DateTime.Now - _lastEventTime).TotalSeconds > _wsStalenessSec)
            {
                PublishState(AgentState.Idle, "ws staleness");
                _lastEventTime = DateTime.MinValue;
            }
        }
        catch { }
    };
    _stalenessTimer.Start();
}
```

**2e. Start()/Stop()：WS 主链路 + SQLite 临时兜底**：

```csharp
public void Start()
{
    if (_running) return;
    _running = true;

    _ = Task.Run(async () =>
    {
        try
        {
            var port = await ResolvePortAsync();
            _wsCts?.Cancel();
            _wsCts = new CancellationTokenSource();
            _wsTask = StartWsAsync(port, _wsCts.Token);
            StartStalenessCheck();

            // 等 3 秒看 WS 是否连上（主链路）
            await Task.Delay(3000);
            if (_wsConnected)
            {
                Console.WriteLine($"[HermesAdapter] WS 主链路运行中 (:{port})");
                return;
            }

            // WS 连不上 → SQLite 临时兜底（仅本地）
            Console.WriteLine("[HermesAdapter] WS 未连上，临时回退 SQLite 轮询");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
                _timer.Tick += OnTick;
                _timer.Start();
                PollOnce();
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] Start: {ex.Message}"); }
    });
}

public void Stop()
{
    if (!_running) return;
    _running = false;

    _wsCts?.Cancel();
    try { _wsTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    _stalenessTimer?.Stop();
    if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
    try { _ws?.Dispose(); } catch { }
}
```

**2f. 端口解析**（手填优先，0=自动发现按 profile 过滤）：

```csharp
private async Task<int> ResolvePortAsync()
{
    try
    {
        if (_port > 0) return _port;   // 手填优先

        // 自动发现：pgrep hermes_cli serve + lsof，按 profile 过滤
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = string.IsNullOrEmpty(_profile)
                ? "-c \"pgrep -f 'hermes_cli.main.*serve' | while read pid; do lsof -i -P -n -p $pid 2>/dev/null | grep LISTEN | grep 127.0.0.1 | awk '{print $9}' | sed 's/.*://' ; done\""
                : $"-c \"pgrep -f 'hermes_cli.main.*serve.*--profile {_profile}' | while read pid; do lsof -i -P -n -p $pid 2>/dev/null | grep LISTEN | grep 127.0.0.1 | awk '{{print $9}}' | sed 's/.*://' ; done\"",
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null)
        {
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim(), out var p) && p > 0) return p;
            }
        }
        return 8642;   // 默认端口兜底
    }
    catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] ResolvePort: {ex.Message}"); return 8642; }
}
```

**2g. 连接状态通知**：

```csharp
public Action<string, bool>? ConnStatusCallback { get; set; }

private void NotifyConnStatus(string text, bool connected)
{
    try { ConnStatusCallback?.Invoke(text, connected); }
    catch { }
}
```

### 任务 3 — App.axaml.cs 装配（传配置 + 挂状态回调）

**文件**：`MiniPet/App.axaml.cs`

找到现有 HermesAdapter 创建处，改造为：

```csharp
var ai = configService.LoadSettings().AgentIntegration ?? new AgentIntegrationConfig();
var hermesAdapter = new HermesAdapter(eventBus, HermesAdapter.BuildDefaultConnStr(), ai);
hermesAdapter.ConnStatusCallback = (text, connected) =>
{
    try { petWindow?.UpdateHermesConnStatus(text, connected); }
    catch { }
};
```

> ⚠️ `BuildDefaultConnStr()` 当前是 private，需改为 public。若现有装配方式不同（DI 注入），按现有代码适配，保证 HermesAdapter 能拿到 AgentIntegrationConfig。

### 任务 4 — SettingsWindow.Agent.cs 配置页

**文件**：`MiniPet/Views/SettingsWindow.Agent.cs`

**4a. 新增 UI 引用字段**：

```csharp
// ── Hermes Gateway WS 配置 UI ──
private NumericUpDown? _agentHermesPort;
private TextBox? _agentHermesTokenInput;
private TextBox? _agentHermesProfileInput;
private TextBlock? _agentHermesConnStatus;
private NumericUpDown? _agentHermesWsTimeout;
private NumericUpDown? _agentHermesReconnect;
private NumericUpDown? _agentHermesStaleness;
```

**4b. 在 switchPanel 之后插入「Hermes Gateway 连接」区块**：

```csharp
// ── Hermes Gateway 连接（跟官方桌宠一样走 WS 事件订阅）──
root.Children.Add(BuildAgentSectionHeader("🔗 Hermes Gateway 连接（WS 事件订阅）"));

var connHint = new TextBlock
{
    Text = "桌宠连 Hermes gateway WebSocket（ws://127.0.0.1:{端口}/api/ws）订阅实时事件，与官方桌宠同一条链路。\nToken = HERMES_DASHBOARD_SESSION_TOKEN 环境变量值（必须固定设置，否则 Hermes 重启后 token 变化导致失联）。",
    FontSize = 10, Foreground = BrushTxtDim, TextWrapping = TextWrapping.Wrap,
};
root.Children.Add(connHint);

// 连接状态（HermesAdapter 运行时回写）
_agentHermesConnStatus = new TextBlock { Text = "⚪ 未连接", FontSize = 11, Foreground = BrushTxtDim, Margin = new Thickness(0, 4, 0, 0) };
root.Children.Add(_agentHermesConnStatus);

// 端口 + Token 行
var portRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
_agentHermesPort = new NumericUpDown { Value = ai.HermesPort, Minimum = 0, Maximum = 65535, FormatString = "0", FontSize = 12, Width = 120, Foreground = BrushTxtPrimary, Background = BrushInputBg };
var portLabel = new TextBlock { Text = "端口（0=自动发现）", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
portRow.Children.Add(portLabel);
portRow.Children.Add(_agentHermesPort);
root.Children.Add(portRow);

var tokenRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
_agentHermesTokenInput = new TextBox
{
    Text = ai.HermesToken, FontSize = 12, Width = 320,
    Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder),
    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
    Watermark = "HERMES_DASHBOARD_SESSION_TOKEN 值",
};
var tokenLabel = new TextBlock { Text = "Token", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
tokenRow.Children.Add(tokenLabel);
tokenRow.Children.Add(_agentHermesTokenInput);
root.Children.Add(tokenRow);

// Profile 行
var profileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
_agentHermesProfileInput = new TextBox
{
    Text = ai.HermesProfile, FontSize = 12, Width = 200,
    Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder),
    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
    Watermark = "留空 = 默认 profile",
};
var profileLabel = new TextBlock { Text = "Profile", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
profileRow.Children.Add(profileLabel);
profileRow.Children.Add(_agentHermesProfileInput);
root.Children.Add(profileRow);

// 超时/重连/staleness 一行三列
var wsGrid = new Grid();
for (int i = 0; i < 3; i++) wsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
_agentHermesWsTimeout = new NumericUpDown();
_agentHermesReconnect = new NumericUpDown();
_agentHermesStaleness = new NumericUpDown();
var t0 = MakeAgentNumericBox("连接超时（秒）", ai.HermesWsConnectTimeoutSeconds, 1, 60, _agentHermesWsTimeout);
var t1 = MakeAgentNumericBox("重连间隔（秒）", ai.HermesWsReconnectSeconds, 1, 120, _agentHermesReconnect);
var t2 = MakeAgentNumericBox("Staleness（秒）", ai.HermesWsStalenessSeconds, 3, 120, _agentHermesStaleness);
Grid.SetColumn(t0, 0); Grid.SetColumn(t1, 1); Grid.SetColumn(t2, 2);
wsGrid.Children.Add(t0); wsGrid.Children.Add(t1); wsGrid.Children.Add(t2);
root.Children.Add(wsGrid);
```

**4c. CC/Codex 标注「开发中」**：

```csharp
_agentCCCheck = MakeAgentToggle("Claude Code（🔧 开发中）", ai.CCEnabled);
_agentCodexCheck = MakeAgentToggle("Codex（🔧 开发中）", ai.CodexEnabled);
```

**4d. SaveAgentIntegration() 补充保存**：

```csharp
if (_agentHermesPort != null && _agentHermesPort.Value.HasValue) { ai.HermesPort = (int)Math.Max(0, _agentHermesPort.Value.Value); }
if (_agentHermesTokenInput != null) { ai.HermesToken = _agentHermesTokenInput.Text?.Trim() ?? string.Empty; }
if (_agentHermesProfileInput != null) { ai.HermesProfile = _agentHermesProfileInput.Text?.Trim() ?? string.Empty; }
if (_agentHermesWsTimeout != null && _agentHermesWsTimeout.Value.HasValue) { ai.HermesWsConnectTimeoutSeconds = (int)Math.Max(1, _agentHermesWsTimeout.Value.Value); }
if (_agentHermesReconnect != null && _agentHermesReconnect.Value.HasValue) { ai.HermesWsReconnectSeconds = (int)Math.Max(1, _agentHermesReconnect.Value.Value); }
if (_agentHermesStaleness != null && _agentHermesStaleness.Value.HasValue) { ai.HermesWsStalenessSeconds = (int)Math.Max(3, _agentHermesStaleness.Value.Value); }
```

**4e. UpdateHermesConnStatus 方法**（public，供 HermesAdapter 调用）：

```csharp
public void UpdateHermesConnStatus(string text, bool connected)
{
    try
    {
        if (Dispatcher.UIThread.CheckAccess())
            DoUpdateConnStatus(text, connected);
        else
            Dispatcher.UIThread.Post(() => DoUpdateConnStatus(text, connected));
    }
    catch { }
}

private void DoUpdateConnStatus(string text, bool connected)
{
    if (_agentHermesConnStatus != null)
    {
        _agentHermesConnStatus.Text = (connected ? "🟢 " : "🔴 ") + text;
        _agentHermesConnStatus.Foreground = connected
            ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 120))
            : new SolidColorBrush(Color.FromArgb(255, 220, 100, 100));
    }
}
```

### 任务 5 — Hermes 接入文档

**文件（新建）**：`MiniPet/docs/HERMES_INTEGRATION.md`（放 MiniPet 项目内，随编译分发——csproj 已有 `docs/**/*.md` Content 配置）

```markdown
# Hermes → MiniPet 桌宠接入指南

本文档供 **Hermes Agent / 用户** 读取，说明 MiniPet 桌宠如何连接 Hermes gateway 实时事件流（与官方桌宠同一链路）。

## 一句话

MiniPet 通过 `ws://127.0.0.1:{port}/api/ws?token={token}` WebSocket 长连接订阅 Hermes gateway 的 JSON-RPC 事件流，实时反映 agent 活动（工具执行/思考/等待/出错/完成），与官方桌面端 pet 状态链一致。

## 必须配置（一次性，否则连接失败）

### 1. 固定设置 token

Hermes 的 WS 鉴权 token 来自 `HERMES_DASHBOARD_SESSION_TOKEN` 环境变量。**不设置则每次启动随机生成，MiniPet 立即失联**。

启动 Hermes 前固定设置：

```bash
export HERMES_DASHBOARD_SESSION_TOKEN=$(openssl rand -hex 16)
# 记下这个值，填到 MiniPet 设置中心 → 🤖 Agent 接入 → Token
hermes serve --port 8642
```

### 2. 固定端口

`hermes serve` 默认随机端口（--port 0）。建议固定 `--port 8642`，MiniPet 设置中心填 8642；端口填 0 时 MiniPet 自动发现（按 profile 过滤）。

### 3. 验证

```bash
curl "http://127.0.0.1:8642/health"
```

## MiniPet 侧配置

| 配置项 | 说明 | 默认 |
|--------|------|------|
| 端口 | 0 = 自动发现；手填固定端口更稳 | 0 |
| Token | HERMES_DASHBOARD_SESSION_TOKEN 值（必填） | 空 |
| Profile | 留空 = 默认 profile | 空 |
| 连接超时 | WS 握手超时（秒） | 5 |
| 重连间隔 | 断线重连等待（秒） | 3 |
| Staleness | N 秒无事件→Idle（秒） | 10 |

## 事件 → 动画映射

| 事件 type | 动画 | 冒险岛动作 |
|-----------|------|-----------|
| `tool.start` | 工具执行中 | move |
| `reasoning.available` | 思考中 | say |
| `clarify.request` / `approval.request` | 等待用户 | mouse |
| `message.complete` | 回合完成 | hand |
| `error` | 出错 | hit |
| `message.delta` / `message.start` | busy | move |
| staleness 超时 | 空闲 | stand |

## 协议（源码参考）

- WS 端点：`hermes_cli/web_server.py:16241`（鉴权包装）+ `tui_gateway/ws.py`（handler）
- 协议：newline-delimited JSON-RPC；首帧 `gateway.ready`；事件帧 `{"jsonrpc":"2.0","method":"event","params":{"type":"..."}}`
- 状态链对齐：`apps/desktop/src/store/pet.ts`（setPetActivity → derivePetState）

## 安全说明

- Token 明文存 MiniPet 配置（~/.minipet/settings.json）= 本地 gateway 凭证，仅本机使用
- 不持久化/log 事件内容（reasoning/content），只映射状态

## 故障排查

| 症状 | 排查 |
|------|------|
| 一直 Idle | gateway 在跑？端口对？token 是否设了 env 并填进 MiniPet？ |
| 连接频繁断开 | 检查 HERMES_DASHBOARD_SESSION_TOKEN 是否固定；加大重连间隔 |
| 桌宠动画不动 | Hermes 当前是否有活跃会话（发一条消息看事件推送） |

## 未来计划

- 双向通信（桌宠 → gateway 发起消息，走 api_server REST `POST /api/sessions/{id}/chat/stream` SSE）
- 多 profile 支持
```

### 任务 6 — 更新 ADR-0006

**文件**：`MiniPet/docs/adr/0006-hermes-gateway-event-subscription.md`

把文档改为最终决策版：主链路 = WS 事件订阅（跟官方桌宠一样，`pet.ts` 状态链复刻）；state.db 仅临时兜底；REST/远程直连已否决；token 固定设置运维要求。

## 编码约定

1. **所有方法 try-catch**，catch 里 `Console.Error.WriteLine`
2. **保留现有 SQLite 查询代码**（QuerySnapshot/MapState/PollOnce 全保留，作 WS 失败临时兜底）
3. **不改 IAgentStateAdapter / AgentStateAggregator / 其他 adapter**
4. **不引入新 NuGet 包**（ClientWebSocket / HttpClient / System.Text.Json 均内置）
5. **if 后面必须加 {}**
6. **改文件清单**：`AppConfig.cs`、`HermesAdapter.cs`、`SettingsWindow.Agent.cs`、`App.axaml.cs`、新建 `MiniPet/docs/HERMES_INTEGRATION.md`、更新 `MiniPet/docs/adr/0006-hermes-gateway-event-subscription.md`

## 验证清单

1. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore` → 0 错误
2. grep 确认：
   - `grep -n "StartWsAsync" MiniPet/Services/Adapters/HermesAdapter.cs`
   - `grep -n "ClientWebSocket" MiniPet/Services/Adapters/HermesAdapter.cs`
   - `grep -n "MapEventTypeToAgentState" MiniPet/Services/Adapters/HermesAdapter.cs`
   - `grep -n "HermesPort" MiniPet/Models/AppConfig.cs`
   - `grep -n "UpdateHermesConnStatus" MiniPet/Views/SettingsWindow.Agent.cs`
   - `[ -f MiniPet/docs/HERMES_INTEGRATION.md ] && echo ok`
3. 编译产物携带文档：`MiniPet/bin/Debug/net9.0/docs/HERMES_INTEGRATION.md` 存在
4. 运行验证（需 Hermes 在跑 + token 已配）：
   - 启动桌宠 → 日志 `[HermesAdapter] WS 已连接 ws://127.0.0.1:xxxx/api/ws`
   - 设置页 Agent tab 显示 🟢 连接状态
   - 在 Hermes 桌面端发消息干活 → 桌宠动画随 tool.start/reasoning/error 切换

## 禁止事项

- 禁止改 AgentStateAggregator / EffectLayerService / AnimService / 其他 adapter
- 禁止删 SQLite 查询代码（临时兜底保留）
- 禁止实现远程直连（锁死 127.0.0.1）
- 禁止引入 REST 轮询（已否决）
- 禁止引入新 NuGet 包
- 禁止改 WZ/地图/气泡模块
- 禁止 git 提交
