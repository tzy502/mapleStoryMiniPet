# ADR-0006: MiniPet ← Hermes Gateway WebSocket 事件订阅（方案 A）

> 状态：已确认（2026-08-10）
> 关联需求：MiniPet 桌宠实时感知 Hermes Agent 活动
> 关联提示语：claude-prompt-20260810-02.md

## 背景

MiniPet 已有 Agent 状态感知框架（`AgentStateAggregator` + `IAgentStateAdapter` + `HermesAdapter` / `ClaudeCodeAdapter` / `CodexAdapter`），桌宠根据 Agent 状态切换动画。

现有 `HermesAdapter` 采用 **方案 B（SQLite 轮询）**：只读 `~/.hermes/state.db`，5 秒轮询一次。

**问题**：SQLite 轮询只能推导出 **3 态**（Idle / Run / Wave），无法获取：
- **Waiting**（clarify/approval 等用户输入）—— state.db 无信号
- **Failed**（出错）—— state.db 无信号
- **Review**（模型思考中）—— state.db 无信号
- **Jump**（庆祝）—— state.db 无信号

Hermes 官方桌宠（petdex）通过 **Gateway WebSocket 长连接** 获取全 7 态信号。

## 决策

采用 **方案 A（Gateway WebSocket `/api/ws` JSON-RPC 长连接）**，升级 HermesAdapter。

### 接入点（源码核实）

**端点**：`ws://127.0.0.1:{port}/api/ws`

**注册位置**（源码实锤）：
- `tui_gateway/ws.py:19` — `@app.websocket("/api/ws")`（TUI/Dashboard/桌面端共用）
- `hermes_cli/web_server.py:16241` — `@app.websocket("/api/ws")`（dashboard 服务）

**协议**：newline-delimited JSON-RPC（双向），与桌面端完全相同的信号源。

**端口发现**：
- `hermes_cli serve` 启动时 `--port 0`（随机端口）
- Electron 主进程通过 IPC（`getGatewayWsUrl()`）把端口传给渲染进程
- MiniPet 无法走 Electron IPC，需要自己发现端口：
  - **方式1（推荐）**：读 `~/.hermes/state.db` 或 gateway 写入的端口文件
  - **方式2**：扫描 localhost 常见端口范围
  - **方式3（阶段1降级）**：如果端口发现困难，先用 REST `/api/sessions` 轮询

### 连接建立流程

```
1. MiniPet HermesAdapter.Start()
2. 发现 gateway 端口（读 state.db / 端口文件 / 扫描）
3. 连 ws://127.0.0.1:{port}/api/ws
4. 收到 gateway.ready 事件（服务端首帧，含 skin + change_events 标志）
5. 持续接收 event 帧：
   {"jsonrpc":"2.0", "method":"event", "params":{"type":"tool.start", ...}}
6. 按 type 映射 → AgentState → EventBus.Publish(AgentStateChanged)
```

### 事件帧格式（源码 `tui_gateway/ws.py:316-326` 核实）

服务端推送（无 id，`method: "event"`）：
```json
{"jsonrpc":"2.0", "method":"event", "params":{"type":"tool.start", "tool_name":"terminal", ...}}
```

客户端请求（有 id，标准 JSON-RPC 请求/响应）：
```json
{"jsonrpc":"2.0", "id":"r1", "method":"session.list", "params":{}}
```

### 事件类型（源码 `gateway-events.ts:20-39` + `ws.py:53-57` 核实）

| 事件 type | 说明 | 流式（高频，被 coalesce） |
|-----------|------|--------------------------|
| `message.start` | 模型开始输出 | 否 |
| `message.delta` | 模型逐 token 输出 | **是**（coalesce 30fps） |
| `message.complete` | 回合完成 | 否 |
| `message.interim` | 中间结果 | 否 |
| `tool.start` | 工具开始执行 | 否 |
| `tool.complete` | 工具执行完成 | 否 |
| `tool.progress` | 工具进度 | 否 |
| `reasoning.available` | 推理开始 | 否 |
| `reasoning.delta` | 推理逐 token | **是**（coalesce） |
| `thinking.delta` | 思考逐 token | **是**（coalesce） |
| `clarify.request` | 等待用户澄清 | 否 |
| `approval.request` | 等待用户审批 | 否 |
| `secret.request` | 等待密钥输入 | 否 |
| `sudo.request` | 等待 sudo | 否 |
| `error` | 出错 | 否 |
| `status.update` | 状态更新 | 否 |
| `gateway.ready` | 连接就绪（首帧） | 否 |
| `pet.changed` | petdex 配置变更 | 否 |
| `sessions.changed` | 会话列表变更 | 否 |

### 事件 → AgentState 映射（对齐 `agent/pet/state.py` derive_pet_state）

| Gateway 事件 | AgentState | 优先级 |
|-------------|------------|--------|
| `error` | **Failed** | 1（最高） |
| `message.complete`（无 error） | **Wave** | 3 |
| `clarify.request` / `approval.request` / `secret.request` / `sudo.request` | **Waiting** | 4 |
| `tool.start` | **Run** | 5 |
| `reasoning.available` / `reasoning.delta` | **Review** | 6 |
| `message.start` / `message.delta` | **Run**（busy） | 7 |
| 无活动 / 会话空闲 | **Idle** | 8（最低） |

优先级链与 `AgentStateAggregator` 完全一致：
```
error(Failed) > celebrate(Jump) > justCompleted(Wave) > awaitingInput(Waiting) > toolRunning(Run) > reasoning(Review) > busy(Run) > Idle
```

### 不变项

- `IAgentStateAdapter` 接口不变（`Start()/Stop()/Id`）
- `AgentStateAggregator` 不变
- `ClaudeCodeAdapter` / `CodexAdapter` 不变
- SQLite 查询代码保留作为 fallback（Gateway 不可用时自动切换）

## 配置与接入文档

### MiniPet 侧配置（UI）

设置中心 → 🤖 Agent 接入：
- **Hermes**：WS 长连接（方案 A）—— Host（默认 127.0.0.1，**远程 Windows/NAS 可填 IP/域名**）/ 端口(0=自动)/ Token / 模式(auto/ws/rest) / 超时 / 重连 / staleness / REST 轮询，连接状态实时显示
- **Claude Code**：开关保留，标注「开发中」
- **Codex**：开关保留，标注「开发中」
- **特效层**：Jump 庆祝 / Failed 弹窗开关

配置持久化到 `~/.minipet/settings.json` 的 `AgentIntegration` 字段。

### Hermes 侧接入文档

`docs/HERMES_INTEGRATION.md` —— 供 Hermes Agent 读取的自助接入指南。Hermes 读这份文档即可理解：
1. MiniPet 连的是 `ws://{host}:{port}/api/ws`
2. 本地自动发现端口；远程手填 host:port（+token）
3. 事件 → 动画映射表
4. 故障排查指引

## 连接链路（本地 / 远程）

```
Hermes gateway (hermes serve / gateway)
  │
  ├── WS /api/ws（JSON-RPC 事件流，首选）
  │     ├── 本地：自动发现端口（pgrep+lsof 扫描），无鉴权或 ?token=
  │     └── 远程：手填 host:port + ?token=（--insecure/loopback 模式）
  │
  ├── REST /api/sessions（回退1，轮询）
  │     ├── 本地：自动发现
  │     └── 远程：手填 + Bearer API_SERVER_KEY（可选）
  │
  └── SQLite state.db（回退2，仅本地，现有代码保留）
        │
        ▼
MiniPet HermesAdapter → AgentStateAggregator → EffectLayerService → AnimService → 桌宠动画
```

## 实现阶段

| 阶段 | 内容 | 提示语 | 状态 |
|------|------|--------|------|
| 1 | WS 长连接 + 远程支持 + 配置页 + 接入文档（整合版） | `claude-prompt-20260810-04.md` | 待执行 |
| 2 | 断线重连指数退避 + 心跳 + gated ticket 鉴权 | — | 后续 |
| 3 | 双向交互（MiniPet → gateway 发消息）+ 多 profile | — | 后续 |
