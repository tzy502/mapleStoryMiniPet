# MiniPet — Hermes 接入配置页 + WS 连接状态 + Hermes 接入文档

> 提示语版本：claude-prompt-20260810-03（Mode B 增量改造，单 Agent 串行）
> 日期：2026-08-10
> 对应技术方案：docs/adr/0006-hermes-gateway-event-subscription.md
> 前序：claude-prompt-20260810-02.md（WS 长连接 HermesAdapter 改造）
> 本版新增：配置页 UI 完善 + AgentIntegrationConfig 扩展 + Hermes 端接入文档（让 Hermes 自己接桌宠）

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 编译：`"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore`（0 错误才交付）

## 背景

现有设置中心已有「🤖 Agent 接入」tab（`SettingsWindow.Agent.cs`），含 Hermes/Claude Code/Codex 开关 + 阈值配置。但：
1. Hermes 配置项没有 WS 连接相关字段（端口覆盖、连接状态显示）
2. CC/Codex 开关可以保留但标注"开发中"（功能未完成）
3. 没有 Hermes 端的接入文档（让 Hermes 主动连桌宠）

本任务完善配置页 + 扩展配置模型 + 创建 Hermes 端接入文档。

## 本次任务（单 Agent 串行）

### 任务 1 — AgentIntegrationConfig 扩展：新增 Hermes WS 配置字段

**文件**：`MiniPet/Models/AppConfig.cs`（改现有 `AgentIntegrationConfig` 类，L58-78）

在现有字段末尾（L77 `WzCelebrationNodePath` 之后）新增：

```csharp
// ── Hermes WebSocket 连接配置（方案 A：WS 长连接事件订阅）──

/// <summary>Hermes gateway 端口覆盖（0 = 自动发现，扫描 hermes_cli serve 进程监听端口）。</summary>
public int HermesGatewayPort { get; set; } = 0;

/// <summary>Hermes WS 连接超时（秒，默认 5）。</summary>
public int HermesWsConnectTimeoutSeconds { get; set; } = 5;

/// <summary>Hermes WS 断线重连间隔（秒，默认 3）。</summary>
public int HermesWsReconnectSeconds { get; set; } = 3;

/// <summary>Hermes WS 事件 staleness 阈值（秒，超此无事件→Idle，默认 10）。</summary>
public int HermesWsStalenessSeconds { get; set; } = 10;
```

### 任务 2 — SettingsWindow.Agent.cs 配置页改造

**文件**：`MiniPet/Views/SettingsWindow.Agent.cs`（只改这一个文件 + AppConfig.cs）

**2a. 新增 UI 引用字段**（在 L19-27 现有字段后追加）：

```csharp
// ── Hermes WS 配置 UI ──
private NumericUpDown? _agentHermesPort;
private NumericUpDown? _agentHermesWsTimeout;
private NumericUpDown? _agentHermesReconnect;
private NumericUpDown? _agentHermesStaleness;
private TextBlock? _agentHermesConnStatus;   // 连接状态显示
```

**2b. 在 BuildAgentTab 的「Hermes 开关」下方插入「Hermes 连接配置」区块**（在现有 switchPanel 之后、阈值区之前插入）：

```csharp
// ── Hermes 连接配置（仅 HermesEnabled 时有意义）──
root.Children.Add(BuildAgentSectionHeader("🔗 Hermes 连接配置（WebSocket 长连接）"));

var hermesConnHint = new TextBlock
{
    Text = "桌宠通过 ws://127.0.0.1:{端口}/api/ws 长连接订阅 Hermes 实时事件（tool.start / reasoning / clarify / error 等）。\n端口填 0 = 自动发现（扫描 hermes_cli serve 进程）。",
    FontSize = 10,
    Foreground = BrushTxtDim,
    TextWrapping = TextWrapping.Wrap,
};
root.Children.Add(hermesConnHint);

// 连接状态（只读显示，由 HermesAdapter 运行时回写）
_agentHermesConnStatus = new TextBlock
{
    Text = "⚪ 未连接",
    FontSize = 11,
    Foreground = BrushTxtDim,
    Margin = new Thickness(0, 4, 0, 0),
};
root.Children.Add(_agentHermesConnStatus);

// 端口 + 超时 + 重连 + staleness（2×2 网格）
var wsGrid = new Grid();
for (int i = 0; i < 2; i++) { wsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); }
for (int i = 0; i < 2; i++) { wsGrid.RowDefinitions.Add(new RowDefinition()); }

_agentHermesPort = new NumericUpDown();
_agentHermesWsTimeout = new NumericUpDown();
_agentHermesReconnect = new NumericUpDown();
_agentHermesStaleness = new NumericUpDown();

var wsC0 = MakeAgentNumericBox("Gateway 端口（0=自动）", ai.HermesGatewayPort, 0, 65535, _agentHermesPort);
var wsC1 = MakeAgentNumericBox("连接超时（秒）", ai.HermesWsConnectTimeoutSeconds, 1, 60, _agentHermesWsTimeout);
var wsC2 = MakeAgentNumericBox("重连间隔（秒）", ai.HermesWsReconnectSeconds, 1, 120, _agentHermesReconnect);
var wsC3 = MakeAgentNumericBox("Staleness 阈值（秒）", ai.HermesWsStalenessSeconds, 3, 120, _agentHermesStaleness);

Grid.SetColumn(wsC0, 0); Grid.SetRow(wsC0, 0);
Grid.SetColumn(wsC1, 1); Grid.SetRow(wsC1, 0);
Grid.SetColumn(wsC2, 0); Grid.SetRow(wsC2, 1);
Grid.SetColumn(wsC3, 1); Grid.SetRow(wsC3, 1);
wsGrid.Children.Add(wsC0); wsGrid.Children.Add(wsC1);
wsGrid.Children.Add(wsC2); wsGrid.Children.Add(wsC3);
root.Children.Add(wsGrid);
```

**2c. CC/Codex 开关标注「开发中」**：把现有 switchPanel 里的标签改掉：

```csharp
// 原来：
// _agentCCCheck = MakeAgentToggle("Claude Code（FSW + 静默推断）", ai.CCEnabled);
// _agentCodexCheck = MakeAgentToggle("Codex（task_complete 精确事件）", ai.CodexEnabled);

// 改为：
_agentCCCheck = MakeAgentToggle("Claude Code（🔧 开发中）", ai.CCEnabled);
_agentCodexCheck = MakeAgentToggle("Codex（🔧 开发中）", ai.CodexEnabled);
```

**2d. SaveAgentIntegration() 补充保存新字段**（在 L213 `WzCelebrationNodePath` 保存之后追加）：

```csharp
if (_agentHermesPort != null && _agentHermesPort.Value.HasValue) { ai.HermesGatewayPort = (int)Math.Max(0, _agentHermesPort.Value.Value); }
if (_agentHermesWsTimeout != null && _agentHermesWsTimeout.Value.HasValue) { ai.HermesWsConnectTimeoutSeconds = (int)Math.Max(1, _agentHermesWsTimeout.Value.Value); }
if (_agentHermesReconnect != null && _agentHermesReconnect.Value.HasValue) { ai.HermesWsReconnectSeconds = (int)Math.Max(1, _agentHermesReconnect.Value.Value); }
if (_agentHermesStaleness != null && _agentHermesStaleness.Value.HasValue) { ai.HermesWsStalenessSeconds = (int)Math.Max(3, _agentHermesStaleness.Value.Value); }
```

**2e. 连接状态显示更新方法**（新增 public 方法，供 HermesAdapter 运行时调用）：

```csharp
/// <summary>更新 Hermes 连接状态显示（由 HermesAdapter 运行时调用）。UI 线程安全。</summary>
public void UpdateHermesConnStatus(string text, bool connected)
{
    try
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            DoUpdateConnStatus(text, connected);
        }
        else
        {
            Dispatcher.UIThread.Post(() => DoUpdateConnStatus(text, connected));
        }
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

### 任务 3 — 创建 Hermes 接入文档（让 Hermes 自己接桌宠）

**文件（新建）**：`docs/HERMES_INTEGRATION.md`（项目根 docs 下）

这是给 **Hermes Agent** 读的文档——Hermes 用这个文档理解怎么配置才能让桌宠连上它的 gateway。内容如下（**完整照抄**）：

```markdown
# Hermes → MiniPet 桌宠接入指南

本文档供 **Hermes Agent** 读取，说明 MiniPet 桌宠如何连接 Hermes gateway 的 WebSocket 事件流。

## 一句话

MiniPet 桌宠通过 `ws://127.0.0.1:{gateway_port}/api/ws` 长连接订阅 Hermes gateway 的 JSON-RPC 事件流，实时反映 agent 活动（工具执行/思考/等待/出错/完成）。

## MiniPet 侧配置

MiniPet 设置中心 → 🤖 Agent 接入 → 🔗 Hermes 连接配置：

| 配置项 | 说明 | 默认 |
|--------|------|------|
| Hermes 开关 | 开启后桌宠订阅 Hermes 事件 | 开 |
| Gateway 端口 | `0` = 自动发现（扫描 hermes_cli serve 进程） | 0 |
| 连接超时 | WS 握手超时（秒） | 5 |
| 重连间隔 | 断线后重连等待（秒） | 3 |
| Staleness | N 秒无事件→Idle（秒） | 10 |

## Hermes 侧配置（确保 gateway 对 MiniPet 可达）

### 1. Gateway 必须在跑

MiniPet 连本地 gateway 的 `/api/ws` WebSocket 端点。确保 Hermes 桌面端或 CLI 在运行：

\`\`\`bash
# 检查 gateway 进程
pgrep -f "hermes_cli.*serve" || pgrep -f "gateway run"
\`\`\`

### 2. 端口发现

MiniPet 自动扫描 `hermes_cli serve` 进程的 `127.0.0.1:{port}` LISTEN 端口。
如果自动发现失败，在 MiniPet 设置里手填端口。

### 3. 无需认证（本地）

MiniPet 连的是 `127.0.0.1` 本地端口，gateway 默认不要求 API_SERVER_KEY。
如果 gateway 配置了 `API_SERVER_KEY`，MiniPet 也需要配置（当前版本暂不支持鉴权，留 TODO）。

## 事件 → 桌宠动画映射

MiniPet 订阅 gateway 推送的事件帧，按 `params.type` 映射到冒险岛动画：

| Hermes 事件 type | 桌宠动画语义 | 冒险岛动作 |
|------------------|-------------|-----------|
| `tool.start` | 工具执行中 | move（走路） |
| `reasoning.available` | 模型思考中 | say（说话） |
| `clarify.request` / `approval.request` | 等待用户输入 | mouse（发呆） |
| `message.complete`（无 error） | 回合完成 | hand（挥手） |
| `error` | 出错了 | hit（受击） |
| `message.delta` / `message.start` | busy（输出中） | move（走路） |
| 10 秒无事件 | 空闲 | stand（站立） |

## 协议细节（源码参考）

- **WS 端点**：`tui_gateway/ws.py:19` → `@app.websocket("/api/ws")`
- **帧格式**：newline-delimited JSON-RPC
- **服务端推送帧**：`{"jsonrpc":"2.0","method":"event","params":{"type":"tool.start",...}}`
- **首帧**：连接后服务端推 `gateway.ready`（含 skin + change_events 标志）

## 故障排查

| 症状 | 排查 |
|------|------|
| 桌宠一直 Idle | 检查 gateway 是否在跑（`pgrep -f hermes_cli`），检查 MiniPet 设置里端口 |
| 桌宠动画不动 | 检查 Hermes 当前是否有活跃会话（发一条消息看事件是否推送） |
| 连接频繁断开 | 增大「重连间隔」和「Staleness 阈值」 |

## 未来计划

- MiniPet → Hermes 双向通信（桌宠右键菜单发消息到 gateway）
- 鉴权支持（API_SERVER_KEY）
- 多 profile 支持（MiniPet 可选连哪个 Hermes profile 的 gateway）
```

## 编码约定

1. **所有方法 try-catch**，catch 里 `Console.Error.WriteLine`
2. **不改 IAgentStateAdapter 接口 / HermesAdapter.cs**（本任务只改 UI + 模型 + 文档）
3. **不引入新 NuGet 包**
4. **if 后面必须加 {}**
5. **只改 3 个文件**：`AppConfig.cs`（模型）+ `SettingsWindow.Agent.cs`（UI）+ 新建 `docs/HERMES_INTEGRATION.md`

## 验证清单

1. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore` → 0 错误
2. grep 确认：
   - `grep -n "HermesGatewayPort" MiniPet/Models/AppConfig.cs`
   - `grep -n "_agentHermesPort" MiniPet/Views/SettingsWindow.Agent.cs`
   - `grep -n "UpdateHermesConnStatus" MiniPet/Views/SettingsWindow.Agent.cs`
   - `grep -n "开发中" MiniPet/Views/SettingsWindow.Agent.cs`
3. 运行验证：打开设置中心 → Agent 接入 tab → 看到 Hermes WS 配置区 + CC/Codex 标注"开发中"

## 禁止事项

- 禁止改 HermesAdapter.cs / IAgentStateAdapter / AgentStateAggregator / 其他 adapter
- 禁止改 WZ / 地图 / 气泡等无关模块
- 禁止引入新 NuGet 包
- 禁止 git 提交
