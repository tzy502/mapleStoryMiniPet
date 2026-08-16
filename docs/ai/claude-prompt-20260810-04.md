# MiniPet — Hermes 接入整合版：WS 长连接 + 远程支持 + 配置页 + Hermes 接入文档

> 提示语版本：claude-prompt-20260810-04（整合版，单 Agent 串行）
> 日期：2026-08-10
> 对应技术方案：docs/adr/0006-hermes-gateway-event-subscription.md
> 整合：02（WS 长连接）+ 03（配置页/文档）+ 远程支持（host/token 可配，非硬编码 127.0.0.1）
> 前序版本保留：claude-prompt-20260810-02.md、claude-prompt-20260810-03.md

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 编译：`"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore`（0 错误才交付）

## 背景

MiniPet 桌宠需要实时感知 Hermes Agent 活动（工具执行/思考/等待/出错/完成 → 切换动画）。

**场景**：Hermes gateway 可能跑在：
- 本机（127.0.0.1，默认）
- **远程**：Windows 或 NAS 上（需手填 IP/域名 + token）

**方案**：WS 长连接订阅 `/api/ws` 事件流（实时全 7 态），失败回退 REST 轮询，再失败回退 SQLite。

## 权威参考（源码核实）

### WS 端点与鉴权

| 项 | 值 | 源码 |
|----|-----|------|
| 端点 | `ws://{host}:{port}/api/ws` | `tui_gateway/ws.py:19`、`hermes_cli/web_server.py:16241` |
| 协议 | newline-delimited JSON-RPC | `tui_gateway/ws.py` 头注释 |
| 本地鉴权 | `?token=<_SESSION_TOKEN>`（loopback 模式） | `web_server.py:15074` `_ws_auth_reason` |
| 远程鉴权 | `?ticket=<single-use>` 或 `?internal=`（gated 模式） | 同上 |
| 首帧 | 服务端推 `gateway.ready` | `tui_gateway/ws.py:314-326` |
| 事件帧 | `{"jsonrpc":"2.0","method":"event","params":{"type":"tool.start",...}}` | 同上 |

**注意**：
- 远程连接需要鉴权。MiniPet 配置 `HermesToken` 作为 `?token=` 参数（loopback/insecure 模式可用）
- 桌面端 Electron 通过 `getGatewayWsUrl()` 拿 token+URL，MiniPet 没有这个 IPC，只能自己配置
- `tui_gateway/ws.py` 裸 handler 无鉴权，但实际部署时被 `web_server.py` 的鉴权包装包裹

### 事件 → AgentState 映射

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

**文件**：`MiniPet/Models/AppConfig.cs`（`AgentIntegrationConfig` 类 L58-78 末尾追加）

```csharp
// ── Hermes 连接配置（方案 A：WS 长连接，支持本地/远程）──

/// <summary>Hermes gateway 主机。默认 127.0.0.1；远程（Windows/NAS）填 IP 或域名。</summary>
public string HermesHost { get; set; } = "127.0.0.1";

/// <summary>Hermes gateway 端口。0 = 本地自动发现（扫描 hermes_cli serve 进程监听端口）；远程必填。</summary>
public int HermesPort { get; set; } = 0;

/// <summary>WS 鉴权 token（?token= 参数）。本地无鉴权可留空；远程 loopback/insecure 模式需要。</summary>
public string HermesToken { get; set; } = string.Empty;

/// <summary>连接方式：auto（WS 优先→REST→SQLite）/ ws（仅 WS）/ rest（仅 REST 轮询）。默认 auto。</summary>
public string HermesMode { get; set; } = "auto";

/// <summary>WS 连接超时（秒，默认 5）。</summary>
public int HermesWsConnectTimeoutSeconds { get; set; } = 5;

/// <summary>WS 断线重连间隔（秒，默认 3）。</summary>
public int HermesWsReconnectSeconds { get; set; } = 3;

/// <summary>WS 事件 staleness 阈值（秒，超此无事件→Idle，默认 10）。</summary>
public int HermesWsStalenessSeconds { get; set; } = 10;

/// <summary>REST 轮询周期（秒，WS 不可用时，默认 5）。</summary>
public int HermesRestPollSeconds { get; set; } = 5;

/// <summary>REST Bearer token（API_SERVER_KEY，api_server 平台启用时需要）。可选。</summary>
public string HermesRestToken { get; set; } = string.Empty;
```

### 任务 2 — HermesAdapter.cs WS 长连接改造（核心）

**文件**：`MiniPet/Services/Adapters/HermesAdapter.cs`

**2a. 构造注入配置**（改造现有构造函数）：

```csharp
// 新增字段
private readonly string _host;
private readonly int _port;
private readonly string _token;
private readonly string _mode;
private int _wsConnectTimeoutMs;
private int _wsReconnectMs;
private int _wsStalenessSec;

// 新增构造函数（保留原有 EventBus-only 构造兼容，用默认配置）
public HermesAdapter(EventBus eventBus)
    : this(eventBus, BuildDefaultConnStr(), new AgentIntegrationConfig()) { }

public HermesAdapter(EventBus eventBus, string connStr)
    : this(eventBus, connStr, new AgentIntegrationConfig()) { }

public HermesAdapter(EventBus eventBus, string connStr, AgentIntegrationConfig ai)
{
    _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    _connStr = connStr ?? throw new ArgumentNullException(nameof(connStr));
    _host = string.IsNullOrEmpty(ai.HermesHost) ? "127.0.0.1" : ai.HermesHost;
    _port = ai.HermesPort;
    _token = ai.HermesToken ?? string.Empty;
    _mode = string.IsNullOrEmpty(ai.HermesMode) ? "auto" : ai.HermesMode.ToLowerInvariant();
    _wsConnectTimeoutMs = Math.Max(1, ai.HermesWsConnectTimeoutSeconds) * 1000;
    _wsReconnectMs = Math.Max(1, ai.HermesWsReconnectSeconds) * 1000;
    _wsStalenessSec = Math.Max(3, ai.HermesWsStalenessSeconds);
}
```

**2b. 端口发现（仅本地 host 时启用）**：

```csharp
/// <summary>发现 gateway 端口。host 非 loopback 或已配置端口时直接返回配置值。</summary>
private async Task<int> ResolvePortAsync()
{
    try
    {
        // 手填端口优先
        if (_port > 0) return _port;

        // 仅对本地 host 自动发现
        if (!IsLoopbackHost(_host)) return 8642; // 远程无端口信息时用默认

        // 扫描 hermes_cli serve 进程监听端口（pgrep + lsof，macOS/Linux）
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"pgrep -f 'hermes_cli.main.*serve' | while read pid; do lsof -i -P -n -p $pid 2>/dev/null | grep LISTEN | grep 127.0.0.1 | awk '{print $9}' | sed 's/.*://' ; done\"",
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null)
        {
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim(), out var p) && p > 0)
                {
                    if (await IsGatewayAliveAsync(_host, p)) return p;
                }
            }
        }
        return 8642; // 默认端口兜底
    }
    catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] ResolvePort: {ex.Message}"); return 8642; }
}

private static bool IsLoopbackHost(string host)
{
    var h = host.Trim().ToLowerInvariant();
    return h is "127.0.0.1" or "localhost" or "::1";
}

/// <summary>验证 gateway：GET /api/sessions 返回 JSON。</summary>
private static async Task<bool> IsGatewayAliveAsync(string host, int port)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var resp = await client.GetAsync($"http://{host}:{port}/api/sessions");
        if (!resp.IsSuccessStatusCode) return false;
        var ct = await resp.Content.ReadAsStringAsync();
        return ct.Contains("session") || ct.StartsWith("[") || ct.StartsWith("{");
    }
    catch { return false; }
}
```

**2c. WS 连接 + 事件接收**：

```csharp
private System.Net.WebSockets.ClientWebSocket? _ws;
private System.Threading.CancellationTokenSource? _wsCts;
private Task? _wsTask;
private bool _wsConnected;
private DateTime _lastEventTime = DateTime.MinValue;

/// <summary>启动 WS 长连接订阅（带重连循环）。</summary>
private async Task StartWsAsync(int port, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            using var client = new System.Net.WebSockets.ClientWebSocket();
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            // 构造 URL：ws://{host}:{port}/api/ws[?token=xxx]
            var url = $"ws://{_host}:{port}/api/ws";
            if (!string.IsNullOrEmpty(_token))
                url += $"?token={Uri.EscapeDataString(_token)}";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_wsConnectTimeoutMs);
            await client.ConnectAsync(new Uri(url), cts.Token);

            _ws = client;
            _wsConnected = true;
            _lastEventTime = DateTime.Now;
            Console.WriteLine($"[HermesAdapter] WS 已连接 {url}");
            NotifyConnStatus($"已连接 {_host}:{port}", true);

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

/// <summary>解析事件帧 → AgentState → 发布。</summary>
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
        _ => null,
    };
}
```

**2d. Staleness 衰减**（WS 空闲回 Idle）：

```csharp
private DispatcherTimer? _stalenessTimer;

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

**2e. REST 轮询回退**（WS 不可用时）：

```csharp
private DispatcherTimer? _restTimer;

private void StartRestPolling(int port)
{
    _restTimer?.Stop();
    _restTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, HermesRestPollSeconds) * 1000) };
    _restTimer.Tick += async (_, _) =>
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://{_host}:{port}/api/sessions");
            if (!string.IsNullOrEmpty(HermesRestToken))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HermesRestToken);
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync();
            // 简化：存在活跃会话且近期有活动 → Run，否则 Idle（阶段1）
            var hasRecent = json.Contains("last_activity_at") && !json.Contains("\"ended_at\": null");
            var state = hasRecent ? AgentState.Run : AgentState.Idle;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PublishState(state, "rest poll"));
        }
        catch { }
    };
    _restTimer.Start();
}
```

**2f. Start()/Stop() 主流程**（按 mode 选择链路）：

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
            var useWs = _mode is "auto" or "ws";
            var useRest = _mode is "auto" or "rest";

            if (useWs)
            {
                _wsCts?.Cancel();
                _wsCts = new CancellationTokenSource();
                _wsTask = StartWsAsync(port, _wsCts.Token);
                StartStalenessCheck();
                // 等 2 秒看 WS 是否连上
                await Task.Delay(2000);
                if (_wsConnected)
                {
                    Console.WriteLine($"[HermesAdapter] WS 模式运行中 ({_host}:{port})");
                    return;
                }
                Console.WriteLine("[HermesAdapter] WS 未连上，尝试 REST/SQLite");
            }

            if (useRest && await IsGatewayAliveAsync(_host, port))
            {
                Console.WriteLine($"[HermesAdapter] REST 轮询模式 ({_host}:{port})");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartRestPolling(port));
                return;
            }

            // SQLite 回退（仅本地）
            if (IsLoopbackHost(_host))
            {
                Console.WriteLine("[HermesAdapter] 回退 SQLite 轮询");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
                    _timer.Tick += OnTick;
                    _timer.Start();
                    PollOnce();
                });
            }
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
    _restTimer?.Stop();
    if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
    try { _ws?.Dispose(); } catch { }
}
```

**2g. 连接状态通知**（设置页 UI 回显）：

```csharp
// 委托字段（由 App 装配时挂 SettingsWindow.UpdateHermesConnStatus）
public Action<string, bool>? ConnStatusCallback { get; set; }

private void NotifyConnStatus(string text, bool connected)
{
    try { ConnStatusCallback?.Invoke(text, connected); }
    catch { }
}
```

### 任务 3 — App.axaml.cs 装配（传配置 + 挂状态回调）

**文件**：`MiniPet/App.axaml.cs`

在现有 HermesAdapter 创建处（搜 `HermesAdapter`）改造：

```csharp
// 原：var hermesAdapter = new HermesAdapter(eventBus);
// 改：
var ai = configService.LoadSettings().AgentIntegration ?? new AgentIntegrationConfig();
var hermesAdapter = new HermesAdapter(eventBus, HermesAdapter.BuildDefaultConnStr(), ai);
// 挂连接状态回调（如果设置窗口已创建）
hermesAdapter.ConnStatusCallback = (text, connected) =>
{
    try { petWindow?.UpdateHermesConnStatus(text, connected); }
    catch { }
};
```

> ⚠️ 若当前 App.axaml.cs 中 HermesAdapter 创建方式不同（如 DI 注入），按现有代码适配。HermesAdapter.BuildDefaultConnStr() 需改为 public（当前是 private）。

### 任务 4 — SettingsWindow.Agent.cs 配置页

**文件**：`MiniPet/Views/SettingsWindow.Agent.cs`

**4a. 新增 UI 引用字段**：

```csharp
// ── Hermes 连接配置 UI ──
private TextBox? _agentHermesHostInput;
private NumericUpDown? _agentHermesPort;
private TextBox? _agentHermesTokenInput;
private ComboBox? _agentHermesModeBox;
private TextBlock? _agentHermesConnStatus;
```

**4b. 在 switchPanel 之后插入 Hermes 连接配置区块**（端口/超时/重连/staleness 已有 03 版设计，整合加 host/token/mode）：

```csharp
// ── Hermes 连接配置（核心：支持本地/远程）──
root.Children.Add(BuildAgentSectionHeader("🔗 Hermes 连接（WebSocket 长连接）"));

var connHint = new TextBlock
{
    Text = "桌宠订阅 Hermes gateway 事件流，实时反映 agent 状态。\n本地：host=127.0.0.1 + 端口 0（自动发现）。远程（Windows/NAS）：填 IP/域名 + 端口 + token。",
    FontSize = 10, Foreground = BrushTxtDim, TextWrapping = TextWrapping.Wrap,
};
root.Children.Add(connHint);

// 连接状态（HermesAdapter 运行时回写）
_agentHermesConnStatus = new TextBlock { Text = "⚪ 未连接", FontSize = 11, Foreground = BrushTxtDim, Margin = new Thickness(0, 4, 0, 0) };
root.Children.Add(_agentHermesConnStatus);

// Host + 端口行
var hostRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
_agentHermesHostInput = new TextBox
{
    Text = ai.HermesHost, FontSize = 12, Width = 220,
    Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder),
    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
};
_agentHermesPort = new NumericUpDown { Value = ai.HermesPort, Minimum = 0, Maximum = 65535, FormatString = "0", FontSize = 12, Width = 120, Foreground = BrushTxtPrimary, Background = BrushInputBg };
var hostLabel = new TextBlock { Text = "Host（0=自动发现端口）", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
hostRow.Children.Add(hostLabel);
hostRow.Children.Add(_agentHermesHostInput);
hostRow.Children.Add(_agentHermesPort);
root.Children.Add(hostRow);

// Token + Mode 行
var tokenRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
_agentHermesTokenInput = new TextBox
{
    Text = ai.HermesToken, FontSize = 12, Width = 220,
    Background = BrushInputBg, BorderBrush = new SolidColorBrush(InputBorder),
    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
    Watermark = "WS token（本地可留空）",
};
_agentHermesModeBox = new ComboBox
{
    FontSize = 12, Width = 140,
    ItemsSource = new[] { "auto", "ws", "rest" },
    SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "auto", "ws", "rest" }, ai.HermesMode)),
};
var tokenLabel = new TextBlock { Text = "Token（?token= 参数）", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
tokenRow.Children.Add(tokenLabel);
tokenRow.Children.Add(_agentHermesTokenInput);
var modeLabel = new TextBlock { Text = "模式", FontSize = 10, Foreground = BrushTxtDim, VerticalAlignment = VerticalAlignment.Center };
tokenRow.Children.Add(modeLabel);
tokenRow.Children.Add(_agentHermesModeBox);
root.Children.Add(tokenRow);

// 超时/重连/staleness/轮询 2×2 网格（沿用 03 版 MakeAgentNumericBox）
var wsGrid = new Grid();
for (int i = 0; i < 2; i++) wsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
for (int i = 0; i < 2; i++) wsGrid.RowDefinitions.Add(new RowDefinition());
_agentHermesWsTimeout = new NumericUpDown();
_agentHermesReconnect = new NumericUpDown();
_agentHermesStaleness = new NumericUpDown();
_agentHermesRestPoll = new NumericUpDown();
var t0 = MakeAgentNumericBox("连接超时（秒）", ai.HermesWsConnectTimeoutSeconds, 1, 60, _agentHermesWsTimeout);
var t1 = MakeAgentNumericBox("重连间隔（秒）", ai.HermesWsReconnectSeconds, 1, 120, _agentHermesReconnect);
var t2 = MakeAgentNumericBox("Staleness（秒）", ai.HermesWsStalenessSeconds, 3, 120, _agentHermesStaleness);
var t3 = MakeAgentNumericBox("REST 轮询（秒）", ai.HermesRestPollSeconds, 1, 120, _agentHermesRestPoll);
Grid.SetColumn(t0, 0); Grid.SetRow(t0, 0);
Grid.SetColumn(t1, 1); Grid.SetRow(t1, 0);
Grid.SetColumn(t2, 0); Grid.SetRow(t2, 1);
Grid.SetColumn(t3, 1); Grid.SetRow(t3, 1);
wsGrid.Children.Add(t0); wsGrid.Children.Add(t1); wsGrid.Children.Add(t2); wsGrid.Children.Add(t3);
root.Children.Add(wsGrid);
```

**4c. CC/Codex 开关标注「开发中」**：

```csharp
_agentCCCheck = MakeAgentToggle("Claude Code（🔧 开发中）", ai.CCEnabled);
_agentCodexCheck = MakeAgentToggle("Codex（🔧 开发中）", ai.CodexEnabled);
```

**4d. SaveAgentIntegration() 补充保存**（在 L213 后追加）：

```csharp
if (_agentHermesHostInput != null) { ai.HermesHost = string.IsNullOrWhiteSpace(_agentHermesHostInput.Text) ? "127.0.0.1" : _agentHermesHostInput.Text.Trim(); }
if (_agentHermesPort != null && _agentHermesPort.Value.HasValue) { ai.HermesPort = (int)Math.Max(0, _agentHermesPort.Value.Value); }
if (_agentHermesTokenInput != null) { ai.HermesToken = _agentHermesTokenInput.Text?.Trim() ?? string.Empty; }
if (_agentHermesModeBox != null && _agentHermesModeBox.SelectedIndex >= 0) { ai.HermesMode = (string)_agentHermesModeBox.SelectedItem!; }
if (_agentHermesWsTimeout != null && _agentHermesWsTimeout.Value.HasValue) { ai.HermesWsConnectTimeoutSeconds = (int)Math.Max(1, _agentHermesWsTimeout.Value.Value); }
if (_agentHermesReconnect != null && _agentHermesReconnect.Value.HasValue) { ai.HermesWsReconnectSeconds = (int)Math.Max(1, _agentHermesReconnect.Value.Value); }
if (_agentHermesStaleness != null && _agentHermesStaleness.Value.HasValue) { ai.HermesWsStalenessSeconds = (int)Math.Max(3, _agentHermesStaleness.Value.Value); }
if (_agentHermesRestPoll != null && _agentHermesRestPoll.Value.HasValue) { ai.HermesRestPollSeconds = (int)Math.Max(1, _agentHermesRestPoll.Value.Value); }
```

**4e. UpdateHermesConnStatus 方法**（public，供 HermesAdapter 调用）：

```csharp
/// <summary>更新 Hermes 连接状态显示（HermesAdapter 运行时调用）。UI 线程安全。</summary>
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

**4f. 新增字段声明**（顶部）：

```csharp
private NumericUpDown? _agentHermesWsTimeout;
private NumericUpDown? _agentHermesReconnect;
private NumericUpDown? _agentHermesStaleness;
private NumericUpDown? _agentHermesRestPoll;
```

### 任务 5 — Hermes 接入文档

**文件（新建）**：`docs/HERMES_INTEGRATION.md`

```markdown
# Hermes → MiniPet 桌宠接入指南

本文档供 **Hermes Agent** 读取，说明 MiniPet 桌宠如何连接 Hermes gateway 实时事件流。

## 一句话

MiniPet 通过 `ws://{host}:{port}/api/ws` 长连接订阅 Hermes gateway 的 JSON-RPC 事件流，实时反映 agent 活动。

## 连接配置（MiniPet 设置中心 → 🤖 Agent 接入）

| 配置项 | 说明 | 默认 |
|--------|------|------|
| Host | gateway 主机：本地 `127.0.0.1`；远程（Windows/NAS）填 IP/域名 | 127.0.0.1 |
| 端口 | `0` = 本地自动发现（扫描 hermes_cli serve 进程）；远程必填 | 0 |
| Token | WS 鉴权 `?token=` 参数（loopback/insecure 模式）；本地无鉴权可留空 | 空 |
| 模式 | auto（WS→REST→SQLite）/ ws / rest | auto |
| 连接超时 | WS 握手超时（秒） | 5 |
| 重连间隔 | 断线重连等待（秒） | 3 |
| Staleness | N 秒无事件→Idle（秒） | 10 |
| REST 轮询 | WS 不可用时的轮询周期（秒） | 5 |
| REST Token | api_server 的 API_SERVER_KEY（Bearer），可选 | 空 |

## Hermes 侧要求

1. **gateway 在跑**：`hermes serve`（TUI/dashboard）或 gateway 平台在运行
2. **端口可达**：本地自动发现；远程需防火墙放行 + 手填端口
3. **鉴权**：本地 loopback 一般无需 token；远程若 `--insecure` 或 loopback 模式，配置 `?token=<_SESSION_TOKEN>`；gated 模式需 ticket（当前版本 MiniPet 不支持 ticket，需 `--insecure` 或本地）

## 事件 → 动画映射

| 事件 type | 动画 | 冒险岛动作 |
|-----------|------|-----------|
| `tool.start` | 工具执行中 | move |
| `reasoning.available` | 思考中 | say |
| `clarify.request`/`approval.request` | 等待用户 | mouse |
| `message.complete` | 回合完成 | hand |
| `error` | 出错 | hit |
| `message.delta`/`message.start` | busy | move |
| 10s 无事件 | 空闲 | stand |

## 协议（源码）

- WS 端点：`tui_gateway/ws.py:19` / `hermes_cli/web_server.py:16241`
- newline-delimited JSON-RPC；首帧 `gateway.ready`；事件帧 `{"jsonrpc":"2.0","method":"event","params":{"type":"..."}}`

## 故障排查

| 症状 | 排查 |
|------|------|
| 一直 Idle | gateway 在跑？端口配置对？Hermes 当前有活动会话？ |
| 连不上远程 | 防火墙放行端口？token 对不对？模式选 auto/ws？ |
| 频繁断开 | 加大重连间隔；检查网络稳定性 |

## 未来

- 双向通信（桌宠→gateway 发消息）
- ticket 鉴权支持、多 profile
```

## 编码约定

1. **所有方法 try-catch**，catch 里 `Console.Error.WriteLine`
2. **不删除现有 SQLite 查询代码**（保留 fallback）
3. **不改 IAgentStateAdapter / AgentStateAggregator / 其他 adapter**
4. **不引入新 NuGet 包**（ClientWebSocket / HttpClient / System.Text.Json 都是 .NET 9 内置）
5. **if 后面必须加 {}**
6. **改文件清单**：`AppConfig.cs`、`HermesAdapter.cs`、`SettingsWindow.Agent.cs`、`App.axaml.cs`、新建 `docs/HERMES_INTEGRATION.md`

## 验证清单

1. 编译 0 错误
2. grep 确认：`HermesHost`（AppConfig）、`StartWsAsync`（HermesAdapter）、`ResolvePortAsync`、`UpdateHermesConnStatus`（SettingsWindow.Agent）、`HERMES_INTEGRATION.md` 存在
3. 运行验证（本地）：桌宠启动 → 日志 `WS 已连接 ws://127.0.0.1:xxxx/api/ws` 或回退提示；设置页 Agent tab 显示 🟢 连接状态
4. 远程验证（可选）：配 NAS IP + 端口 → 连接成功日志

## 禁止事项

- 禁止改 AgentStateAggregator / EffectLayerService / AnimService / 其他 adapter
- 禁止删 SQLite 查询代码
- 禁止引入新 NuGet 包
- 禁止改 WZ/地图/气泡模块
- 禁止 git 提交
