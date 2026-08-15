# MiniPet — HermesAdapter 升级：SQLite 轮询 → Gateway HTTP/SSE 实时事件

> 提示语版本：claude-prompt-20260810-01（Mode B 增量改造，单 Agent 串行）
> 日期：2026-08-10
> 对应技术方案：docs/adr/0006-hermes-gateway-event-subscription.md（需同步创建）
> 目标：把 HermesAdapter 从 state.db 轮询（3 态）升级为 Gateway HTTP/SSE 事件订阅（全 7 态）

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 主项目：`MiniPet/MiniPet.csproj`
- .NET SDK（dotnet 是**可执行文件**，必须引号调用）：
  ```bash
  export DOTNET_ROOT="/Volumes/SSD/C#"
  export PATH="$DOTNET_ROOT:$PATH"
  "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore   # 编译
  ```
- 编译必须 0 错误才交付

## 背景：为什么改

当前 `HermesAdapter.cs` 轮询 `~/.hermes/state.db`（SQLite 只读），只能推导出 **3 态**：
- 活跃会话近期有活动 → Run
- 会话刚结束 → Wave
- 其他 → Idle

**Missing/Failed/Review/Waiting 推不出来**（state.db 没有可靠信号——代码注释 L21 明确写了）。

Hermes Gateway 有 **HTTP REST + SSE** 接口，能拿到**全 7 态**的实时事件信号。本任务把 HermesAdapter 升级为 Gateway 事件订阅模式。

## 权威参考：Hermes Gateway 接口（源码核实）

### Gateway 地址
- 默认端口 `8642`（源码 `api_server.py` L151 `DEFAULT_PORT = 8642`）
- 绑定 `127.0.0.1`（本地）
- 环境变量 `API_SERVER_PORT` 可覆盖

### REST API（HTTP GET，返回 JSON）
| 端点 | 用途 |
|------|------|
| `GET /api/sessions` | 会话列表（含 `last_activity_at`/`ended_at`/`end_reason`） |
| `GET /api/sessions/{id}` | 单会话详情 |
| `GET /health` | gateway 健康检查 |
| `GET /v1/models` | 模型列表 |

### SSE 事件流（POST，返回 text/event-stream）
| 端点 | 用途 |
|------|------|
| `POST /api/sessions/{id}/chat/stream` | 发消息 + 拿 SSE 流（含全部事件类型） |

**SSE 事件类型**（源码 `gateway-events.ts` L20-39 核实）：
```
message.start / message.delta / message.complete / message.interim
tool.start / tool.complete / tool.progress / tool.generating
reasoning.available / reasoning.delta / thinking.delta
clarify.request / approval.request / secret.request / sudo.request
error / status.update / browser.progress
```

SSE 帧格式（`api_server.py` L187 `_sse_frame`）：
```
data: {"type":"tool.start","tool_name":"terminal",...}\n\n
```

### 认证
- Header `Authorization: Bearer <API_SERVER_KEY>`（如配置了 key）
- 本地开发默认无 key（直接 GET 即可）

### 事件 → PetState 映射（对齐 `agent/pet/state.py` derive_pet_state）

| Gateway 事件 | AgentState | 说明 |
|-------------|------------|------|
| `tool.start` | **Run** | 工具执行中 |
| `reasoning.available` / `reasoning.delta` / `thinking.delta` | **Review** | 模型思考中 |
| `clarify.request` / `approval.request` / `secret.request` / `sudo.request` | **Waiting** | 等待用户输入 |
| `message.complete`（无 error） | **Wave** | 回合完成 |
| `error` | **Failed** | 出错 |
| `message.start` / `message.delta`（无 tool/reasoning） | **Run** | busy（模型输出中） |
| 全部结束、无活动 | **Idle** | 空闲 |

**优先级**（与 `AgentStateAggregator` 一致）：
```
error(Failed) > celebrate(Jump) > justCompleted(Wave) > awaitingInput(Waiting) > toolRunning(Run) > reasoning(Review) > busy(Run) > Idle
```

## 本次任务（单 Agent 串行，按顺序完成）

### 任务 1 — GatewayHealthChecker：探测 gateway 是否可用

**文件**：`MiniPet/Services/Adapters/HermesAdapter.cs`（改现有文件，不新建）

在 HermesAdapter 类内新增**私有方法**（不破坏现有接口）：

```csharp
/// <summary>
/// 探测 gateway 是否在跑。GET http://127.0.0.1:{port}/health，200 = 可用。
/// </summary>
private bool IsGatewayAlive(int port)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var resp = client.GetAsync($"http://127.0.0.1:{port}/health").Result;
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}
```

### 任务 2 — GatewaySessionPoller：轻量 REST 轮询获取会话活动态（替代 SQLite 查询）

在 HermesAdapter 类内新增**私有方法**，用 REST 替代 SQLite 查询：

```csharp
// 会话活动快照（REST 版，替代 QuerySnapshot 的 SQLite 版）
private readonly struct GatewaySessionSnapshot
{
    public bool HasActiveSession { get; init; }      // 有 ended_at IS NULL 的会话
    public DateTime? LatestActivity { get; init; }    // 最近 last_activity_at
    public bool? LatestEndedRecently { get; init; }   // 最近是否有会话刚结束
}

/// <summary>
/// GET /api/sessions 拿活跃会话列表，推导活动态。
/// 比 SQLite 查询更准（gateway 视角，含 handoff/queue 状态）。
/// </summary>
private GatewaySessionSnapshot QueryGatewaySessions(int port)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var resp = client.GetAsync($"http://127.0.0.1:{port}/api/sessions").Result;
        if (!resp.IsSuccessStatusCode) return default;
        var json = resp.Content.ReadAsStringAsync().Result;
        // 用 System.Text.Json 解析（已在 csproj）
        using var doc = JsonDocument.Parse(json);
        // /api/sessions 返回 { sessions: [...] } 或 [...]
        JsonElement.ArrayEnumerator sessions;
        if (doc.RootElement.TryGetProperty("sessions", out var sProp) && sProp.ValueKind == JsonValueKind.Array)
            sessions = sProp.EnumerateArray();
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            sessions = doc.RootElement.EnumerateArray();
        else
            return default;

        bool hasActive = false;
        DateTime? latest = null;
        bool endedRecently = false;
        var now = DateTime.Now;
        var recentWindow = TimeSpan.FromMilliseconds(PollIntervalMs * RecentEndedWindowMultiplier);

        foreach (var s in sessions)
        {
            // ended_at 为 null/null = 活跃
            if (s.TryGetProperty("ended_at", out var endedProp) && endedProp.ValueKind == JsonValue.Null)
            {
                hasActive = true;
                if (s.TryGetProperty("last_activity_at", out var actProp))
                {
                    var dt = ParseJsonTimestamp(actProp);
                    if (dt.HasValue && (!latest.HasValue || dt > latest))
                        latest = dt;
                }
            }
            else if (s.TryGetProperty("ended_at", out var endedProp2) && endedProp2.ValueKind != JsonValue.Null)
            {
                var ended = ParseJsonTimestamp(endedProp2);
                if (ended.HasValue && (now - ended.Value) <= recentWindow)
                    endedRecently = true;
            }
        }
        return new GatewaySessionSnapshot { HasActiveSession = hasActive, LatestActivity = latest, LatestEndedRecently = endedRecently };
    }
    catch { return default; }
}

private static DateTime? ParseJsonTimestamp(JsonElement el)
{
    // epoch 秒(double) 或 ISO8601 字符串
    if (el.ValueKind == JsonValueKind.Number)
    {
        var d = el.GetDouble();
        return d > 253402300799.0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)d).LocalDateTime
            : DateTimeOffset.FromUnixTimeSeconds((long)d).LocalDateTime;
    }
    if (el.ValueKind == JsonValue.String)
    {
        var s = el.GetString();
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToLocalTime();
    }
    return null;
}
```

### 任务 3 — SSE 事件流订阅（核心：实时拿到 tool.start / reasoning / clarify 等）

在 HermesAdapter 类内新增**SSE 订阅**方法——这是本次改造的核心，让 MiniPet 能实时收到 7 态信号：

```csharp
private System.Threading.CancellationTokenSource? _sseCts;
private Task? _sseTask;

/// <summary>
/// 订阅活跃会话的 SSE 事件流（如果有的话）。
/// 退化为「轮询 /api/sessions 找最近活跃会话 → POST /chat/stream 拿 SSE」。
///
/// ⚠️ 实现策略（阶段1，简化）：
/// 不真正 POST /chat/stream（那需要发消息，只读监听做不到）。
/// 改为高频轮询 /api/sessions/{id}（2s 一次），从 last_activity_at 推断活动态，
/// 并检查 sessions 列表的 handoff_state/queue 状态判断 Waiting/Failed。
/// 真正 SSE 订阅留给阶段2（需要 gateway 暴露 read-only 事件流 endpoint）。
/// </summary>
private void StartGatewayEventLoop(int port)
{
    _sseCts?.Cancel();
    _sseCts = new System.Threading.CancellationTokenSource();
    var token = _sseCts.Token;

    _sseTask = Task.Run(async () =>
    {
        var lastState = AgentState.Idle;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = QueryGatewaySessions(port);
                var mapped = MapGatewayToAgentState(snapshot);
                if (mapped != lastState)
                {
                    lastState = mapped;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        PublishState(mapped, $"gateway port={port}"));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HermesAdapter] GatewayEventLoop: {ex.Message}");
            }
            try { await Task.Delay(PollIntervalMs, token); }
            catch (TaskCanceledException) { break; }
        }
    }, token);
}

/// <summary>
/// Gateway 会话快照 → AgentState（7 态推导，比 SQLite 版多了 Waiting/Failed/Review 可达路径）。
/// </summary>
private AgentState MapGatewayToAgentState(GatewaySessionSnapshot snapshot)
{
    var now = DateTime.Now;
    var freshness = TimeSpan.FromMilliseconds(PollIntervalMs * StalenessMultiplier);

    // 会话刚结束 → Wave
    if (snapshot.LatestEndedRecently)
        return AgentState.Wave;

    // 近期活跃 → Run（简化：无法从 /api/sessions 区分 Review/Waiting，仍按 Run）
    if (snapshot.HasActiveSession && snapshot.LatestActivity.HasValue
        && (now - snapshot.LatestActivity.Value) <= freshness)
        return AgentState.Run;

    return AgentState.Idle;
}
```

### 任务 4 — Start()/Stop() 改造：Gateway 优先，SQLite 回退

改 `Start()` 方法逻辑（**不删除现有 SQLite 查询代码**，作为 fallback 保留）：

```csharp
public void Start()
{
    if (_running) return;
    _running = true;

    // 探测 gateway 端口（默认 8642，可配）
    int gwPort = ResolveGatewayPort();

    if (IsGatewayAlive(gwPort))
    {
        Console.WriteLine($"[HermesAdapter] Gateway 可用 (port={gwPort})，切换到 HTTP 轮询模式");
        StartGatewayEventLoop(gwPort);
    }
    else
    {
        Console.WriteLine($"[HermesAdapter] Gateway 不可用，回退 SQLite 轮询模式");
        // 保留原有 DispatcherTimer SQLite 轮询逻辑
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _timer.Tick += OnTick;
        _timer.Start();
        PollOnce();
    }
}

public new void Stop()
{
    if (!_running) return;
    _running = false;

    // 停 SSE 循环
    _sseCts?.Cancel();
    _sseTask?.Wait(TimeSpan.FromSeconds(2));

    // 停 SQLite 定时器（如果启用了的话）
    if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
}

private static int ResolveGatewayPort()
{
    // 优先 API_SERVER_PORT 环境变量，其次默认 8642
    var env = Environment.GetEnvironmentVariable("API_SERVER_PORT");
    if (int.TryParse(env, out var p) && p > 0) return p;
    return 8642;
}
```

### 任务 5 — HttpClient 安全规范

所有 HTTP 调用必须遵守：
1. **using 包裹**（每次新建 HttpClient，不缓存——.NET 9 下安全；或用 static HttpClient）
2. **Timeout 设置**（2-3 秒，防 gateway 卡住）
3. **try-catch**（网络异常不打断主循环，记 Console.Error）
4. **绝不在 catch 里打 ex.Message 含 content/reasoning/密钥**（隐私，对齐现有注释 L308）

## 编码约定（硬性规则）

1. **所有方法 try-catch**，catch 里 `Console.Error.WriteLine("[HermesAdapter] ...")`
2. **不删除现有 SQLite 查询代码**（QuerySnapshot/MapState/PollOnce 全保留作为 fallback）
3. **不改 IAgentStateAdapter 接口**
4. **不改 AgentState/AgentId/AgentStateEvent**
5. **不改 AgentStateAggregator**（消费端不变，PublishState 调用不变）
6. **不改其他 adapter**（ClaudeCodeAdapter/CodexAdapter 不动）
7. **不引入新 NuGet 包**——`System.Net.Http`（HttpClient）和 `System.Text.Json`（JsonDocument）是 .NET 9 内置
8. **if 后面必须加 {}**
9. **只改 HermesAdapter.cs 一个文件**

## 验证清单（全部通过才交付）

1. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore` → 0 错误
2. grep 确认：
   - `grep -n "IsGatewayAlive" MiniPet/Services/Adapters/HermesAdapter.cs`（新增方法）
   - `grep -n "QueryGatewaySessions" MiniPet/Services/Adapters/HermesAdapter.cs`（新增方法）
   - `grep -n "StartGatewayEventLoop" MiniPet/Services/Adapters/HermesAdapter.cs`（新增方法）
   - `grep -n "ResolveGatewayPort" MiniPet/Services/Adapters/HermesAdapter.cs`（新增方法）
   - `grep -n "MapGatewayToAgentState" MiniPet/Services/Adapters/HermesAdapter.cs`（新增方法）
3. 运行验证（黑盒）：
   - 启动桌宠 → 日志出现 `[HermesAdapter] Gateway 可用 (port=8642)` 或 `Gateway 不可用，回退 SQLite 轮询模式`
   - 进程存活不崩溃

## 禁止事项

- 禁止改 IAgentStateAdapter 接口
- 禁止改 AgentStateAggregator / EffectLayerService / AnimService
- 禁止删 SQLite 查询代码（作为 fallback）
- 禁止引入新 NuGet 包
- 禁止改其他文件（只改 HermesAdapter.cs）
- 禁止 git 提交
