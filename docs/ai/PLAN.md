# Plan: Hermes 客户端 surface（api_server SSE 发起式，桌宠即入口）
_Locked via grill-with-docs — by Claude + 502. Terms per CONTEXT.md._
_Rev 2 — Codex R1 审查 11 条全采纳（_sse_frame event: 解析 / 409 / assistant.delta / effective_session_id / /p/{profile} / 假取消路径 / 长回合衰减 / 并发发送 / Keychain / 设置生效 / run.started）。_

## Goal
把 MiniPet 从"旁观反映 Hermes 活动"升级为 **Hermes 客户端 surface**：用户点桌宠弹出迷你对话框 → 输入发给 Hermes agent（api_server SSE）→ agent 干活，SSE 事件流实时驱动桌宠 7 态动画 + 回复显示。**AgentState→动画映射做成设置页可配置**（7 态用户自选动画）。api_server 是 Hermes 官方唯一外部客户端通道（Open WebUI 同款）。

## Approach

### 0. 事实基线（源码核实）
- **api_server 端点**：`POST /api/sessions`（可传 `id` 或自动 `api_{time}_{uuid}`；**已存在 id → 409**，需"建一次、409/存在即复用"）；`POST /api/sessions/{id}/chat/stream`（body：`message`/`input` + 可选 `model`/`system_message`，SSE）。
- **SSE 帧格式**（Codex R1）：`_sse_frame` 把**事件名放 `event:` 字段、JSON payload 放 `data:`**——必须同时解析两者（只读 `data:` 丢事件类型）。
- **SSE 事件集**：`run.started / message.started / tool.progress（tool_name="_thinking"=reasoning）/ tool.started / tool.completed / tool.failed / assistant.delta / assistant.completed / run.completed / error / done`。
- **`assistant.delta`**：真实流对**所有 assistant 文本** emit——回复显示需累积 delta，与 `run.completed.messages` 对账。
- **会话轮转**：压缩（compaction）可能在 `assistant.completed`/`run.completed` 返回 `effective_session_id`——须持久化事件里的 `session_id` 变化，否则 resume 到 stale parent 重触发压缩。
- **multiplex**：`multiplex_profiles` 下次要 profile 走 `/p/{profile}/` + profile-scoped key。
- **取消**：`chat/stream` 的 `run_id` 不注册到 `_active_run_agents/_active_run_tasks` → **`POST /v1/runs/{run_id}/stop` 返回 404（假路径）**。取消 = 中止 HTTP 请求（CancellationToken）。
- **鉴权**：`Authorization: Bearer <API_SERVER_KEY>`；无 key 拒启。
- 官方桌面端 agent 事件来自**自己绑定的 session 消息流**——MiniPet 作为发起者建自己的 session 天然收得到。

### 1. AgentIntegrationConfig（AppConfig.cs）
- `HermesPort`（默认 0=自动发现 8642）、`HermesApiServerKey`（API_SERVER_KEY）、`HermesProfile`（默认空）。
- **API key 存储（Codex R1 安全）**：优先存 macOS Keychain / Windows Credential Manager，settings 只存引用；若实现复杂则明文存 + 文档明确"本地凭证仅本机、勿备份同步"。
- **新增 AgentState→动画映射配置**：`Dictionary<AgentState, AgentStateFeedback>`（Action 动作名 + Balloon 气泡文本），默认值 = 现有 EffectLayerService switch。

### 2. HermesAdapter 重构（api_server 客户端，主链路）
- **建会话**：`POST /api/sessions` per profile（**建一次持久化 `HermesSessionId`；已存在/409 → 复用**）。
- **profile base path**：非空 `HermesProfile` → base = `/p/{profile}` + 该 profile 的 key（multiplex）。
- **chat/stream SSE 客户端**：`HttpClient` + `StreamReader` 逐行解析 **`event:` + `data:`** 两字段；请求头 Bearer。
- **事件→AgentState 映射**（补齐 Codex R1）：
  - `run.started` → **Run**（首个 busy 信号，POST 接受与 message.started 之间不落 Idle）
  - `tool.started` / `message.started` → Run
  - `tool.progress`（`_thinking`）→ Review
  - `tool.failed` / `error` → Failed
  - `assistant.delta` → 累积（回复显示中间文本）；不单独映射状态
  - `assistant.completed` / `run.completed`（无 error）→ Wave
  - `done` → Idle
  - **Jump**：`run.completed` 成功时本地合成 Jump 再衰减 Wave
  - **Waiting**：SSE 无 clarify/approval → 配置映射到其他动作
- **取消**：HTTP 请求 CancellationToken 中止（**不用 `/v1/runs/stop`，404 假路径**）；`_run_agent` 在 executor 跑、无 agent_ref 中断支持——**abort 可能留下 Hermes turn 继续跑，诚实文档标注"取消为尽力而为"**（Codex R2）。
- **长回合防 Idle（Codex R1/R2）**：SSE 流活跃期间**抑制 Aggregator staleness**（回合内静默但 keepalive 也算活跃）；**抑制 flag 须在 `finally` 清（SSE error/abort/timeout 也清），否则宠物钉死在 Run/Review**。
- **state.db 兜底保留**：api_server 不可达时回退现有 SQLite 启发式。
- 保留 `ConnStatusCallback` / 静态 `Current` / 退避熔断（适配 HTTP）。

### 3. MiniPet 迷你对话框 UI（点桌宠弹出）
- 双击/右键「对话」→ 弹出迷你对话框（输入 + 回复显示 + 发送 + 连接状态）。
- **并发发送保护（Codex R1）**：active turn 未 `done`/error 前禁用发送（防同 session 并发 chat/stream 竞态）。
- **回复显示（Codex R2）**：`assistant.delta` **实时渲染** + 完成时用 `assistant.completed`/`run.completed` 内容**替换** buffer（delta 是过程稿、completed 是权威终稿，**避免重复显示**）；**delta buffer 按事件 `message_id` 键控、`message.started` 时重置**（防乱序/多轮污染）；多轮复用 session（连续上下文）。
- **设置生效（Codex R1）**：保存 Agent 设置后**提示重启生效**（adapter 启动时一次构造），或挂 `OnSettingsChanged` 重启 adapter（实现时二选一，默认提示重启）。

### 4. EffectLayerService 配置化
- 状态→反馈映射从硬编码 switch 改为读 `AgentIntegrationConfig` 映射配置（Action 名 + 气泡文本）。
- 默认值 = 现有 switch（Run→`PlayAction("move")`、Jump→「搞定!」、Failed→「出错了…」、Wave→「完成」、Review→「思考中…」、Waiting→「等你?」、Idle→stand）。

### 5. SettingsWindow.Agent.cs
- Hermes 连接区：端口 / API Server Key / profile。
- **7 态映射配置区**：每态「动作名下拉 + 气泡文本输入」。

### 6. docs/HERMES_INTEGRATION.md（更新）
- api_server 启用步骤（config.yaml `platforms.api_server` + `API_SERVER_KEY` + `hermes gateway` 重启）+ MiniPet 配置 + 事件→动画 + 故障排查。

### 7. ADR-0006（[0006-hermes-api-server-client-surface.md](docs/adr/0006-hermes-api-server-client-surface.md)）

## Key decisions & tradeoffs
- **MiniPet = Hermes 客户端 surface（发起者）**：要 session→要当发起者→api_server 官方唯一外部通道。闭环。
- **SSE 每回合一流**（请求-响应，空闲无常驻连接）。
- **AgentState→动画配置化**（7 态用户自选；Jump/Waiting 无 SSE 信号 → 配置实现）。
- **复用 session**（连续上下文）+ **state.db 兜底保留**。
- Codex R1 采纳：SSE 解析 event:+data:、取消=HTTP 中止、长回合防 Idle、并发发送保护、Keychain 存 key、409 复用、effective_session_id 轮转、/p/{profile}。

## Risks / open questions
- **api_server 启用**：本机未启用（8642 未监听），需 config + 重启（影响正在跑的桌面端，需用户安排）。
- **SSE 无 Jump/Waiting 信号**：Jump 本地合成，Waiting 靠配置。
- **Keychain 存储实现复杂度**（Avalonia 跨平台）：不可行则明文 + 文档风险标注。
- **model 选择**：chat/stream 用什么 model 干活（body.model 或 session-persisted，实现时确认）。
- **回复文本字段**：`assistant.delta`/`run.completed.messages` 结构实现时确认。

## Out of scope
- WS `/api/ws`、REST 轮询、远程直连（锁死 127.0.0.1）。
- 完整聊天历史管理（本轮弹框内显示）。
- 多 profile 切换 UI（默认 profile）。
- CC/Codex adapter 改动。
