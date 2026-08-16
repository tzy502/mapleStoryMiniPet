# MiniPet — HermesAdapter 升级：SQLite 轮询 → Gateway WebSocket 长连接事件订阅

> 提示语版本：claude-prompt-20260810-02（Mode B 增量改造，单 Agent 串行）
> 日期：2026-08-10
> 对应技术方案：docs/adr/0006-hermes-gateway-event-subscription.md
> 前序版本：claude-prompt-20260810-01.md（REST 轮询方案，已废弃——SSE/HTTP 长连接改用 WS）

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

## 背景

当前 `HermesAdapter.cs` 轮询 `~/.hermes/state.db`，只能推导 3 态（Idle/Run/Wave），Waiting/Failed/Review/Jump 推不出来。

Hermes Gateway 提供 **WebSocket 长连接**（`/api/ws` JSON-RPC），能实时收到全部事件信号。本任务把 HermesAdapter 升级为 WS 事件订阅。

## 权威参考：Gateway WS 接口（源码核实）

### WS 端点
- URL：`ws://127.0.0.1:{port}/api/ws`
- 注册位置：`tui_gateway/ws.py:19` `@app.websocket("/api/ws")`
- 协议：newline-delimited JSON-RPC（双向）

### 连接流程
1. WS 握手成功后，服务端推送首帧 `gateway.ready`：
   ```json
   {"jsonrpc":"2.0","method":"event","params":{"type":"gateway.ready","payload":{"skin":{...},"change_events":true}}}
   ```
2. 之后持续推送事件帧（method 恒为 `"event"`，事件类型在 `params.type`）：
   ```json
   {"jsonrpc":"2.0","method":"event","params":{"type":"tool.start","tool_name":"terminal",...}}
   ```
3. 客户端也可以发 JSON-RPC 请求（有 id），服务端返回响应。MiniPet 阶段1 不需要发请求，只订阅事件。

### 事件 → AgentState 映射表

| 事件 type | AgentState |
|-----------|------------|
| `error` | Failed |
| `clarify.request` / `approval.request` / `secret.request` / `sudo.request` | Waiting |
| `tool.start` | Run |
| `reasoning.available` | Review |
| `message.start` / `message.delta` | Run（busy） |
| `message.complete`（无 error） | Wave |
| `gateway.ready` | 不映射（连接就绪信号） |

优先级（与 AgentStateAggregator 一致）：
```
Failed > Jump > Wave > Waiting > Run(tool) > Review > Run(busy) > Idle
```

### 端口发现（关键挑战）

`hermes_cli serve` 启动 `--port 0`（随机端口）。发现方式（按优先级）：

1. **读 gateway 写入的端口文件**：检查 `~/.hermes/` 下是否有端口文件（如 `gateway_port` / `serve.pid` 等）
2. **lsof 扫描**：找 `hermes_cli.main.*serve` 进程监听的 `127.0.0.1:{port}` 端口
3. **REST 健康检查**：逐个试候选端口，`GET /api/sessions` 返回 JSON = 找到

## 本次任务（单 Agent 串行）

### 任务 1 — GatewayPortDiscovery：发现 gateway 端口

**文件**：`MiniPet/Services/Adapters/HermesAdapter.cs`（只改这一个文件）

新增私有方法：

```csharp
/// <summary>
/// 发现 Hermes gateway 的本地监听端口。
/// 策略：启动 hermes_cli serve 进程列表 → lsof 找 127.0.0.1:{port} LISTEN → 验证 /api/sessions。
/// </summary>
private async Task<int?> DiscoverGatewayPortAsync()
{
    try
    {
        // 方式1：读 ~/.hermes/ 下的端口文件（如果有）
        var hermesHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");
        // 检查常见端口文件名
        foreach (var portFile in new[] { "gateway_port", "serve.port", ".gateway_port" })
        {
            var fp = Path.Combine(hermesHome, portFile);
            if (File.Exists(fp) && int.TryParse(File.ReadAllText(fp).Trim(), out var p))
                if (await IsGatewayPortValidAsync(p)) return p;
        }

        // 方式2：扫描 hermes_cli serve 进程的监听端口
        var ports = GetHermesServePorts();
        foreach (var p in ports)
        {
            if (await IsGatewayPortValidAsync(p)) return p;
        }

        return null;
    }
    catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] DiscoverPort: {ex.Message}"); return null; }
}

/// <summary>验证端口：GET /api/sessions 返回 JSON。</summary>
private async Task<bool> IsGatewayPortValidAsync(int port)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var resp = await client.GetAsync($"http://127.0.0.1:{port}/api/sessions");
        if (!resp.IsSuccessStatusCode) return false;
        var ct = await resp.Content.ReadAsStringAsync();
        return ct.Contains("session") || ct.StartsWith("[") || ct.StartsWith("{");
    }
    catch { return false; }
}

/// <summary>用 pgrep/lsof 找 hermes_cli serve 进程的监听端口（macOS/Linux）。</summary>
private static List<int> GetHermesServePorts()
{
    var ports = new List<int>();
    try
    {
        // macOS: lsof -i -P -n | grep python | grep LISTEN
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
                    ports.Add(p);
            }
        }
    }
    catch { }
    return ports;
}
```

### 任务 2 — HermesWsClient：WebSocket 长连接 + 事件订阅

在 HermesAdapter 类内新增 WS 客户端字段和方法：

```csharp
// WS 长连接
private System.Net.WebSockets.WebSocket? _ws;
private System.Threading.CancellationTokenSource? _wsCts;
private Task? _wsReceiveTask;
private int _gwPort;
private bool _wsConnected;

// 当前活动状态（由 WS 事件驱动，供 staleness 衰减参考）
private DateTime _lastEventTime = DateTime.MinValue;

/// <summary>
/// 启动 WS 长连接事件订阅。
/// </summary>
private async Task StartWsEventLoopAsync(int port)
{
    _gwPort = port;
    _wsCts?.Cancel();
    _wsCts = new System.Threading.CancellationTokenSource();
    var token = _wsCts.Token;

    _wsReceiveTask = Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HermesAdapter] WS loop: {ex.Message}");
            }

            // 断线重连：等 3 秒（阶段1 固定间隔；阶段2 改指数退避）
            try { await Task.Delay(3000, token); }
            catch (TaskCanceledException) { break; }
        }
    }, token);
}

/// <summary>
/// 连接 WS + 接收循环。连接断开时退出，由外层重连。
/// </summary>
private async Task ConnectAndReceiveAsync(CancellationToken token)
{
    using var client = new System.Net.WebSockets.ClientWebSocket();
    await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_gwPort}/api/ws"), token);
    _ws = client;
    _wsConnected = true;
    Console.WriteLine($"[HermesAdapter] WS 已连接 ws://127.0.0.1:{_gwPort}/api/ws");

    var buf = new byte[65536];
    while (!token.IsCancellationRequested && client.State == System.Net.WebSockets.WebSocketState.Open)
    {
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buf), token);
        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            break;

        var text = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
        // WS 帧可能是多条 newline-delimited JSON
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            HandleWsFrame(line.Trim());
        }
    }

    _wsConnected = false;
    Console.WriteLine($"[HermesAdapter] WS 断开，将重连");
}

/// <summary>
/// 解析单个 JSON-RPC 帧，按 params.type 映射 → AgentState → 发布。
/// </summary>
private void HandleWsFrame(string json)
{
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 只处理 method=="event" 的推送帧
        if (!root.TryGetProperty("method", out var methodProp)) return;
        if (methodProp.GetString() != "event") return;

        if (!root.TryGetProperty("params", out var paramsProp)) return;
        if (!paramsProp.TryGetProperty("type", out var typeProp)) return;

        var eventType = typeProp.GetString() ?? "";
        _lastEventTime = DateTime.Now;

        var state = MapEventTypeToAgentState(eventType);
        if (state.HasValue)
        {
            // 发布到 UI 线程
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                PublishState(state.Value, $"ws event: {eventType}"));
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] HandleWsFrame: {ex.Message}"); }
}

/// <summary>
/// Gateway 事件 type → AgentState（7 态全映射）。
/// 返回 null = 不映射（如 gateway.ready / status.update）。
/// </summary>
private static AgentState? MapEventTypeToAgentState(string eventType)
{
    return eventType switch
    {
        "error" => AgentState.Failed,
        "clarify.request" => AgentState.Waiting,
        "approval.request" => AgentState.Waiting,
        "secret.request" => AgentState.Waiting,
        "sudo.request" => AgentState.Waiting,
        "tool.start" => AgentState.Run,
        "reasoning.available" => AgentState.Review,
        "message.start" => AgentState.Run,
        "message.delta" => AgentState.Run,   // busy（高频，会被 coalesce）
        "message.complete" => AgentState.Wave,
        // 不映射
        "gateway.ready" => null,
        "status.update" => null,
        "reasoning.delta" => null,           // 高频流式，不逐帧映射（Review 由 reasoning.available 触发）
        "thinking.delta" => null,
        "tool.complete" => null,             // 工具完成≠回合完成；下一个 tool.start 或 message.complete 覆盖
        "tool.progress" => null,
        "message.interim" => null,
        "pet.changed" => null,
        "sessions.changed" => null,
        _ => null,
    };
}
```

### 任务 3 — Start()/Stop() 改造：WS 优先，SQLite 回退

```csharp
public void Start()
{
    if (_running) return;
    _running = true;

    // 异步启动 WS（端口发现 + 连接）
    _ = Task.Run(async () =>
    {
        try
        {
            var port = await DiscoverGatewayPortAsync();
            if (port.HasValue)
            {
                Console.WriteLine($"[HermesAdapter] 发现 gateway 端口 {port.Value}，启动 WS 订阅");
                await StartWsEventLoopAsync(port.Value);
                return; // WS 成功启动，不启动 SQLite 定时器
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[HermesAdapter] WS 启动失败: {ex.Message}"); }

        // WS 不可用 → 回退 SQLite 轮询（保留原有逻辑）
        Console.WriteLine("[HermesAdapter] Gateway WS 不可用，回退 SQLite 轮询");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
            _timer.Tick += OnTick;
            _timer.Start();
            PollOnce();
        });
    });
}

public new void Stop()
{
    if (!_running) return;
    _running = false;

    _wsCts?.Cancel();
    try { _wsReceiveTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

    if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }

    try { _ws?.Dispose(); } catch { }
}
```

### 任务 4 — Staleness 衰减（WS 空闲时回 Idle）

新增 DispatcherTimer（1 秒周期），检查 `_lastEventTime` 超过阈值（如 10 秒）无事件 → 发 Idle：

```csharp
private DispatcherTimer? _stalenessTimer;
private const int WsStalenessSeconds = 10;

// 在 StartWsEventLoopAsync 连接成功后启动
private void StartStalenessCheck()
{
    _stalenessTimer?.Stop();
    _stalenessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    _stalenessTimer.Tick += (_, _) =>
    {
        if (_wsConnected && _lastEventTime != DateTime.MinValue
            && (DateTime.Now - _lastEventTime).TotalSeconds > WsStalenessSeconds)
        {
            PublishState(AgentState.Idle, "ws staleness");
            _lastEventTime = DateTime.MinValue; // 避免重复发
        }
    };
    _stalenessTimer.Start();
}
```

## 编码约定（硬性规则）

1. **所有方法 try-catch**，catch 里 `Console.Error.WriteLine("[HermesAdapter] ...")`
2. **不删除现有 SQLite 查询代码**（QuerySnapshot/MapState/PollOnce 全保留作为 fallback）
3. **不改 IAgentStateAdapter 接口**、**不改 AgentStateAggregator**、**不改其他 adapter**
4. **不引入新 NuGet 包**——`System.Net.WebSockets.ClientWebSocket` 和 `System.Text.Json` 是 .NET 9 内置
5. **if 后面必须加 {}**
6. **只改 HermesAdapter.cs 一个文件**
7. **Stop() 需正确清理 WS + CancellationTokenSource + stalenessTimer**

## 验证清单

1. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore` → 0 错误
2. grep 确认：
   - `grep -n "DiscoverGatewayPortAsync" MiniPet/Services/Adapters/HermesAdapter.cs`
   - `grep -n "ClientWebSocket" MiniPet/Services/Adapters/HermesAdapter.cs`
   - `grep -n "MapEventTypeToAgentState" MiniPet/Services/Adapters/HermesAdapter.cs`
   - `grep -n "StartWsEventLoopAsync" MiniPet/Services/Adapters/HermesAdapter.cs`
3. 运行验证：
   - 启动桌宠 → 日志出现 `[HermesAdapter] 发现 gateway 端口 XXXX` 或 `WS 不可用，回退 SQLite`
   - 如果 WS 连上：桌宠在你用 Hermes 时切换动画（tool.start → Run 动画）

## 禁止事项

- 禁止改 IAgentStateAdapter 接口 / AgentStateAggregator / 其他 adapter
- 禁止删 SQLite 查询代码（作为 fallback）
- 禁止引入新 NuGet 包
- 禁止改其他文件（只改 HermesAdapter.cs）
- 禁止 git 提交
