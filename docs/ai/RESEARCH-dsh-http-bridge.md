# RESEARCH: MiniPet × DeepSeek Harness HTTP 桥接调研（纯观察 + 对话）

_调研日期：2026-08-15 · 状态：方案调研（未实施）_
_数据来源：DSH 0.1.0-rc.6 运行时源码（`~/.local/lib/node_modules/@deepseek-ai/dsh`）+ 本机 3080 端口 HTTP 实测_

## 1. 背景与目标

MiniPet 已完成 Hermes / Claude Code / Codex 三 agent 的状态接入（`PLAN-agent-integration.md`），
架构为**被动观察者**：`IAgentStateAdapter → EventBus → AgentStateAggregator → 桌宠 7 态反映`。

本调研回答：**DSH（DeepSeek Harness，本机 Web GUI 后端，端口 3080）是否可复用同一套 HTTP
逻辑接入？** 结论先行：**可以，且比 Hermes 更简单** —— DSH 原生暴露完整 session RPC + 事件流，
本机 loopback 免鉴权，无需任何 DSH 侧插件即可支撑「纯观察」与「对话」两种形态。

## 2. DSH 架构定位（一句话）

DSH 是 cordis 插件架构的 agent harness：Web GUI（3080）→ `dsh-host-webserver` →
`dsh-client-connection`（浏览器 trust fence + WS 事件下行）→ `dsh-host-apiproxy`（RPC 分发）→
`dsh-api-gateway`（typert gateway）→ session/agent 域服务。

## 3. DSH HTTP API 调研（实测验证）

### 3.1 传输与鉴权

- **Base**：`http://127.0.0.1:3080`（`dsh --profile web`，端口可配）。
- **鉴权**：`dsh-client-connection` 的 browser-trust fence —— **loopback（127.0.0.1/localhost）
  直接放行，无 token**；非 loopback 需 `--trusted-host` 声明；`PRIVILEGED_METHODS`
  （settings.* / credentials.* / host.openPath / host.pickDirectory / llm.discoverModels /
  agentPreset.copy 等）**永远锁 loopback**。MiniPet 用的 `session.*` 不在特权列表。
- **RPC 信封**（POST `/api/{method}`，`content-type: application/json`）：
  ```json
  // 请求
  {"type": "client-request", "rpcId": "<任意唯一串>", "method": "session.list", "payload": {}}
  // 响应（业务错误也是 200 + ServerResponse）
  {"type": "server-response", "rpcId": "...", "result": {"ok": true, "value": { ... }}}
  ```
- **实测证据**（2026-08-15，本机）：`POST /api/session.list` 无鉴权直接返回当前会话
  （含 `sessionId` / `cwd` / `running` / `agentPreset` / `projections`（sessionStats 的
  turns/steps/tokenUsage 等））。✅ 通道可用。

### 3.2 RPC 端点表（MiniPet 相关子集）

| 方法 | 请求 payload 要点 | 响应 value 要点 | 用途 |
|---|---|---|---|
| `session.create` | `{workspaceId 或 cwd（二选一）, sessionId?, agentPreset?}` | `{sessionId, agentPreset?}` | 建会话（对应 Hermes POST /api/sessions）；sessionId 可指定 → 确定性复用（同 Hermes `minipet_{machine}` 模式） |
| `session.prompt` | `{sessionId, mode: "queue"\|"steer", content: [{type:"text",text} 或 {type:"image",mediaType,data,name?}]}` | `{accepted: true, command?}` | **发消息**；**立即返回 accepted**，结果走事件流（非阻塞，同 Hermes chat/stream 的异步语义） |
| `session.cancel` | `{sessionId}` | — | 真取消回合（Hermes 只能断连接尽力而为） |
| `session.history` | `{sessionId, ...}` | 完整历史 | 补帧/恢复上下文 |
| `session.list` / `session.search` | — | 会话列表（含 projections） | 观察模式：发现会话 + 活动状态 |
| `session.models` / `session.selectModel` | — | 模型目录/选择 | 对话模式可选 |
| `session.rename` / `session.fork` | — | — | 管理 |
| `subagent.prompt` / `subagent.list` / `subagent.history` / `subagent.interrupt` | 同 session 族 | — | （可选）子代理对话 |
| `host.describe` / `workspace.list` 等 | — | — | 环境信息（可选） |

> 完整方法表见 `dsh-host-apiproxy` 的 `UNARY_ROUTES`（session/subagent/host/workspace/skill/
> agentPreset/goals/settings/credentials/llm/questions/approvals/jobs 等约 40+ 方法）。

### 3.3 事件流（核心）

- **传输**：**WebSocket**（非 SSE！`dsh-client-connection` 对普通 GET 返回 `426 upgrade required`
  并升级为 WS；服务端用 `ws` 库 noServer + handleUpgrade）。
  - `ws://127.0.0.1:3080/api/events.mux` —— 全会话 mux 帧（**观察 + 对话主用**）
  - `ws://127.0.0.1:3080/api/events.host` —— host 帧（workspace/插件生命周期等，暂不用）
- **mux 帧类型**（`payload.type`）：

| 帧类型 | 内容 | 7 态映射 |
|---|---|---|
| `session/subscribed` | 订阅确认 | — |
| `session/event` | `{sessionId, event, view?}`，内部事件见 3.4 | **主状态源** |
| `session/projection` | `{sessionStats: turns/steps/llmMs/toolMs/tokenUsage…, title, goal}` | Run 佐证 / Detail 展示 |
| `session/queue` / `session/jobs` | 队列/后台任务视图 | Run 佐证 |
| `question/requested` | `{sessionId, questions}` | **Waiting**（真实信号，Hermes 没有） |
| `approval/requested` | 审批请求 | **Waiting** |
| `approval/resolved` / `question/resolved` | 审批/提问解决 | 回 Run/Idle |
| `stream/error` | 流错误 | **Failed** |
| `text` / `image` | 文本/图片载荷 | 对话内容 |

### 3.4 `session/event` 内部事件类型（决定精确状态映射）

| event.type | 含义 | 7 态映射 |
|---|---|---|
| `turn/start` | 回合开始 | **Run** |
| `turn/end` | 回合结束（含结果） | **Jump → Wave**（成功） |
| `tool/call` | 工具调用开始（`view` 含 name/args） | **Run** |
| `tool/result` | 工具结果（`view` 含 is_error → Failed） | **Failed**（is_error）/ Run |
| `assistant/chunk` | 助手消息增量（`view` 含增量文本） | Run + **DeltaReceived**（对话流式） |
| `user/message` | 用户消息 | Run（自己发的确认） |
| `approval/asked` / `approval/decided` | 审批生命周期 | Waiting → Run |

> 事件类型与 view 投影由 `viewFor(ctx, event, …)` 生成（`dsh-host-apiproxy`），
> 具体字段在实现时按实际帧校准。

### 3.5 与 Hermes 链路对照

| 维度 | Hermes（现状） | DSH（本次调研） |
|---|---|---|
| Base | `http://127.0.0.1:8642` | `http://127.0.0.1:3080` |
| 鉴权 | Bearer API_SERVER_KEY | **loopback 免鉴权** |
| 建会话 | `POST /api/sessions`（409 复用） | `POST /api/session.create`（sessionId 可指定复用） |
| 发消息 | `POST /api/sessions/{id}/chat/stream`（SSE 单连接内全响应） | `POST /api/session.prompt`（accepted 即返回）+ **WS 事件流** |
| 取消 | 断连接（尽力而为） | `POST /api/session.cancel`（真取消） |
| 事件通道 | 同一 SSE 连接 | 独立 WS `events.mux`（多会话 mux） |
| 事件源 | 回合内事件驱动 | 回合内事件 + `session/projection` + 心跳 |
| Waiting 信号 | **无**（降级 Idle） | **question/approval/requested 真实可用** |
| Failed 信号 | content_filter（实测零命中） | `stream/error` / tool is_error |
| 历史 | 无 | `session.history` 完整历史 |
| 状态查询 | state.db SQLite 只读轮询（兜底） | `session.list` projections（权威） |

**结论：DSH 通道在状态丰富度上全面优于 Hermes，且免鉴权、无兜底轮询需求。**

## 4. 桌宠接入设计

### 4.1 通用架构（沿用现有链路，零渲染改动）

```
DshAdapter(WS events.mux + RPC) → EventBus(AgentStateChanged) → AgentStateAggregator
  → EventBus(PetReflectStateChanged) → EffectLayerService → 动作/气泡/表情
```

### 4.2 形态一：纯观察（DshAdapter，推荐先做）

- **连接**：`ClientWebSocket` 连 `ws://127.0.0.1:3080/api/events.mux`；断线指数退避重连（复用
  HermesAdapter 的退避/熔断骨架）。
- **数据**：只订阅，不发 prompt。`session.list`（或 mux 初始帧）发现活跃会话；`session/event` +
  `session/projection` 驱动 7 态；`question/requested` / `approval/requested` → Waiting。
- **隐私**：只消费事件视图（`view`），不拉取 `session.history` 全文；Detail 只放
  `{title, turns, steps}` 级摘要，绝不落 content/密钥。
- **优势**：事件驱动零轮询；Waiting/Failed 真实信号；多会话自动 mux。

### 4.3 形态二：对话（DshChatWindow，HermesChatWindow 改造）

- 建会话：`session.create {cwd: MiniPet 工作区, sessionId: "minipet_dsh"}`（409/存在即复用，
  同 HermesAdapter 的 EnsureSessionAsync 模式）。
- 发送：`session.prompt {sessionId, mode: "queue", content: [{type:"text", text}]}`。
- 接收：WS mux 上按 `sessionId` 过滤 `session/event`，`assistant/chunk` 的 view 增量 →
  `DeltaReceived` 实时滚动（HermesChatWindow 已有此回调契约）；`turn/end` 后以最终内容收敛。
- 取消：`session.cancel`（比 Hermes 断连可靠）。
- 窗口：复制 `HermesChatWindow.cs`（420×360 / Topmost / 透明），仅换 adapter。

### 4.4 事件 → AgentState 映射表（实现基线）

| DSH 信号 | AgentState | 备注 |
|---|---|---|
| `turn/start` / `tool/call` / `session/projection` 活跃 | Run | |
| `assistant/chunk`（reasoning 视图） | Review | 视 view 字段而定 |
| `question/requested` / `approval/requested` / `approval/asked` | Waiting | 唯一可靠 Waiting 源 |
| `stream/error` / `tool/result.is_error` | Failed | |
| `turn/end`（无错误） | Jump（本地合成）→ Wave | 复用 HermesAdapter 的 jump-decay 模式 |
| 静默 > staleness（30s） | Idle | Aggregator 已有 |

### 4.5 装配改动点（MiniPet 侧，全部）

1. `MiniPet/Services/Adapters/DshAdapter.cs`（新）：实现 `IAgentStateAdapter`，WS 事件订阅 +
   可选 RPC（对话模式）。
2. `MiniPet/Models/AppConfig.cs`：`AgentIntegration` 加 `DshEnabled` / `DshHost` / `DshPort` /
   `DshWorkspace`（可选）。
3. `MiniPet/Services/AgentState.cs`：`AgentId.Dsh = new("dsh")`。
4. `MiniPet/Services/AgentStateAggregator.cs`：`SourceTiebreak` 加 DSH（建议 DSH > Hermes，
   或按用户偏好）。
5. `MiniPet/App.axaml.cs`：`if (ai.DshEnabled) { … new DshAdapter(eventBus, ai); Start(); }`。
6. `MiniPet/Views/DshChatWindow.cs`（新，对话形态）：复制 HermesChatWindow 改 adapter。
7. `MiniPet/Views/SettingsWindow.Agent.cs`：Agent 接入 tab 加 DSH 开关/地址。
8. csproj：无新依赖（`System.Net.WebSockets.ClientWebSocket` 内置；JSON 用现有 System.Text.Json）。

### 4.6 隐私红线（沿用 PLAN-agent-integration.md §3）

- 只消费 `view` 投影与非内容 Detail；绝不拉取/持久化 content、reasoning、密钥。
- `session.history` 仅用于显式恢复（对话形态可选），不默认轮询。
- loopback 免鉴权意味着**任何本机进程**都可读 DSH 状态 —— 属既有事实，MiniPet 不扩大暴露面。

## 5. 关键决策

- **事件通道用 WS 而非 HTTP-SSE**：实测 `GET /api/events.mux` 返回 426 升级；服务端仅 WS 下行。
  C# `ClientWebSocket` 支持好，重连逻辑简单。
- **观察优先、对话随后**：纯观察零风险（只读订阅），先验证事件→7 态链路，再叠加 prompt 写操作。
- **不需要 DSH 侧插件**：官方 API 已覆盖全部需求（对比：注入器装的 dsh-super-injector 与
  本方案无关，属另一条线）。
- **AgentId tiebreak 建议 DSH 最优先**：DSH 是本机主 harness，状态信号最权威。

## 6. 风险与开放问题

- **WS 帧 schema 细节**：`session/event` 的 `view` 具体字段（chunk 文本路径、is_error 位置）
  未逐帧实测，实现时需抓包校准（写一个临时 WS 客户端 dump 一回合）。
- **多会话汇聚**：mux 是全会话流，观察模式需决定聚合策略（复用 Aggregator 每源一态，或
  DSH 内部再聚合为单源）—— 建议复用 Aggregator：DSH 作为一个源，内部取最高活动会话。
- **question/approval 的 resolved 事件**：映射回 Run/Idle 的时序需实测。
- **对话模式并发**：`session.prompt` 的 `queue`/`steer` 语义差异未深挖（queue=排队，steer=？
  需查证）—— 默认 queue。
- **WS 鉴权边界**：loopback 免鉴权仅限本机；若未来 MiniPet 跨机访问需 `--trusted-host` + 空
  trust list 特权方法会 403（session.* 不受影响）。
- **DSH 升级兼容**：RPC 信封/方法名随 0.1.x 演进可能变化，adapter 需版本探针（`host.describe`
  或 `session.list` 失败即降级 Idle，不崩）。

## 7. 实施计划（建议顺序）

| 阶段 | 内容 | 验收 |
|---|---|---|
| P0 | 临时 WS dump 工具：连 mux 抓一回合真实帧（turn/tool/chunk/end） | 帧 schema 定稿，映射表校准 |
| P1 | `DshAdapter`（纯观察）+ 装配 + Aggregator tiebreak | DSH 干活时桌宠 Run/Review/Waiting/Failed/Jump 全态正确 |
| P2 | 单测（参考 `HermesAdapterTests.cs`：帧解析/映射/重连） | `dotnet build` 0 错误 + 测试绿 |
| P3 | `DshChatWindow`（对话）+ 设置 tab | 桌宠对话 DSH，流式回复，cancel 生效 |
| P4 | 可选：双向控制（DSH 插件侧注册工具反向驱动桌宠） | 另行调研 |

## 8. 参考

- DSH 运行时源码（本机）：`~/.local/lib/node_modules/@deepseek-ai/dsh/node_modules/@deepseek-ai/`
  - `dsh-host-apiproxy/lib/index.js`（UNARY_ROUTES / events.mux / viewFor）
  - `dsh-client-connection/lib/index.js`（browser-trust fence / 426 升级 / WebSocketDownlinks）
  - `dsh-host-webserver/lib/index.js`（upgrade 路由）
- MiniPet 现有实现：`MiniPet/Services/Adapters/HermesAdapter.cs`（SSE 客户端骨架复用）、
  `MiniPet/Views/HermesChatWindow.cs`（对话窗口骨架复用）、`PLAN-agent-integration.md`
- 实测命令：
  ```bash
  curl -s -X POST http://127.0.0.1:3080/api/session.list \
    -H 'content-type: application/json' \
    -d '{"type":"client-request","rpcId":"probe-1","method":"session.list","payload":{}}'
  ```
