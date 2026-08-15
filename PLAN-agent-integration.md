# Plan: MiniPet 接入主流 Agent 状态（Hermes / Claude Code / Codex）
_Locked via grill-me-codex — by Claude + 唐子煜_
_Rev 4 — Codex R3 VERDICT: APPROVED（C1–C14 + N1–N3 全 resolved）；采纳 R3 残留 N4/N5 小修。_

## Goal
让 MiniPet 从"纯冒险岛桌宠"升级为"agent 状态桌宠"：订阅 Hermes / Claude Code / Codex 的实时活动状态，用桌宠当前实体 + **通用特效层**（复用 WZ 庆祝动画）反映 agent 干活过程。**统一状态模型采用 Hermes 官方 Pets 7 状态语义**（借语义，不借 petdex spritesheet 格式）。优先级 **Hermes > Claude Code > Codex**。本轮：地基（7 状态模型 + 特效层）+ **Hermes adapter（state.db 轮询，优先）** + **CC adapter（FSW+poll）** + Codex 接口定义。WorkBuddy/媒体后续。

## Approach

### 0. 事实基线（Codex R1+R2 实地验证 `~/.hermes` / `~/.claude` / `~/.codex`）
- **Hermes**：activity 数据在 `~/.hermes/state.db`（**SQLite WAL**，非 JSONL）。权威信号 = `sessions` 表 `last_activity_at` / `last_activity_description` / `last_activity_provenance` / `handoff_state` / `ended_at` / `end_reason` / `messages.active`。
  - **Codex R2 实测**：`hermes status` 输出**仅环境/API-key 健康**（Python 版本/model/API keys），**零 activity 字段**——不可作状态源。`state.db` 中 **`handoff_state` 对 266 session 全空**、`last_activity_provenance` 空/`unknown`、`end_reason` 仅 `cron_complete`/`ws_orphan_reap`。→ **Hermes 无可靠的 Waiting/Failed/Review 信号**。
  - 数据源：**`state.db` 只读直读为唯一安全源**（WAL 并发读，schema 已验证真实）；`hermes status`（仅环境健康）与 `hermes sessions list`（CLI 以写模式开库、争用 gateway 写锁、实测报错 `state.db is not writable`）均移出 activity 路径（N4）。
- **Claude Code**：`~/.claude/projects/<编码路径>/<sid>.jsonl`，嵌套 `<sid>/subagents/agent-*.jsonl`（11 project 目录实测）。**无 task_complete 事件**（完成靠推断）。不开 HTTP。
- **Codex CLI**：`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`，每行 `RolloutLine`。**有 `event_msg`：`task_started`/`task_complete`（带 `completed_at`/`duration_ms`/`last_agent_message`）**。不开 HTTP。
- **EventBus**（`EventBus.cs:55`）：`Publish` 同步在发布者线程调 handler。现有 `UserDragStart/End`（`PetWindow.axaml.cs:532/558`）**已同步发布 + 订阅者自 marshal**（:222/:248）——不可破坏其同步语义。
- **已有 seam**：`AppConfig.cs:99` `HermesConfig`；`PaperdollService.cs:1129` 预留 expression gateway。

### 1. 统一状态模型（`AgentState.cs` + EventBus + Aggregator）
- `enum AgentState { Idle, Run, Review, Wave, Jump, Failed, Waiting }`（Hermes 7 态）。
- `record AgentStateEvent(AgentId Source, AgentState State, string? Detail, DateTime Ts)`。
- **EventBus 线程安全（C3+N3）**：marshal **只 scope 到新 `AgentStateChanged`**，用 `if (Dispatcher.UIThread.CheckAccess()) handler(data) else Dispatcher.UIThread.Post(() => handler(data))`——on-thread 同步、off-thread Post；**不改现有 8 个 EventType 的同步语义**（避免 double-queue / 重排）。实现上可新增 `PublishOnUIThread(EventType, data)` 仅 adapter 调用。
- **AgentStateAggregator（C4/C5/C6/C8）**：
  - 输入：每个 agent 的**每个活跃 session 各自一条状态流**。
  - 优先级：活动级 `Failed ≈ Run > Review > Waiting > Wave > Idle`，**同级 tiebreak Hermes > CC > Codex**；`Failed` 与 `Run` 同级 `Failed` 胜。文档化。
  - **Staleness 衰减（C5）**：每 agent 流 T 秒（默认 30s）无事件 → 降 `Idle` + 清 `Failed`/`Run`/`Review`。
  - **`Jump` 一次性（C6）**：固定时长（默认 3s）庆祝，播完衰减 `Wave`→`Idle`；不进 last-state-wins。
  - **adapter 失败保护（R2 minor）**：重复异常（DB 锁/CLI 崩）加 backoff/circuit-breaker（staleness 管 stuck 不管 exception storm）。

### 2. 通用特效层 — overlay 与 expression gateway 分工（C9/C13）
- **复用 `PaperdollService` 预留 expression gateway（`:1129`）驱动每实体动画**（`Run`→纸娃娃 `walk1`/Mob `move`/NPC 现有）。
- **新增 `EffectLayerService`（overlay）只管 `Jump`/`Failed`**：WZ 庆祝动画（`Jump`）+ emoji 弹窗（`Failed`）。全实体通用，不替换实体帧，不切实体。
- **WZ 庆祝节点（C9）**：实现前用 WzService 定位确认（用户称存在；候选 `Effect/`、`UI/Basic` emotion、`Item/`）。找不到则 `Jump` 降级 `UI/Basic` emotion + 气泡。
- 气泡（`Wave`/`Review`/`Waiting`）走现有 `BalloonService`，固定字符串。
- 表情先用纸娃娃 `default`/`blink`（完整表情集 out of scope）。

### 3. Adapter 抽象（`Adapters/IAgentStateAdapter.cs`）+ 隐私（C12）
- `interface IAgentStateAdapter { AgentId Id; void Start(); void Stop(); }`。
- **隐私**：adapter **只发 `AgentState` + 非内容 `Detail`**，**绝不持久化/log/上报 `content`/`reasoning`/密钥**。state.db/CC jsonl/Codex rollout 含完整对话与密钥——只读、不外泄。

### 4. Hermes adapter（优先，`Adapters/HermesAdapter.cs`）—— R2 实测后诚实版
- **数据源 = `state.db` 只读直读轮询**（唯一安全源）：连接串 `Mode=ReadOnly;Pooling=false` + `PRAGMA busy_timeout=3000;query_only=1`（C11），出错重连。`hermes status`（仅环境健康）与 `hermes sessions list`（CLI 写模式开库争用写锁、实测报错）均不用（N1+N4）。
- **状态映射（R2 实测，诚实版，不映射虚构列）**：
  - 近期 `last_activity_at` + `messages.active` → `Run`
  - 过期（超 staleness）→ `Idle`
  - `ended_at` 刚填 → `Wave`（或 `Jump` 若上下文示 plan 完成）
  - **`Waiting`/`Failed`/`Review`：本轮 Hermes 无可靠信号（`handoff_state` 全空）→ 降级 `Idle`**，留接口未来用 `messages` 表 role/content-free 启发式探测真实 approval/error（N2）。
  - ⚠️ **`Run` 可靠性（N5）**：依赖 `last_activity_at` 新鲜度（实测 8 个 open session 中 5 个该字段 NULL=僵尸会话）；`messages.active`（88% 消息 set）仅作 session-existence filter 不作 liveness。**实现时首先验证 `last_activity_at` 是否实时更新**；staleness timer 兜底降 Idle——不会显示错误状态（最多该 Run 时显示 Idle）。
- 轮询周期默认 5s（可配）。右键菜单「Hermes 终端」（`PetWindow.Menu.cs:148`）落地为状态面板 / 快速唤起 `hermes`。

### 5. Claude Code adapter（第二，`Adapters/ClaudeCodeAdapter.cs`）—— FSW+poll + debounce（C4/C7/C10）
- FileSystemWatcher 盯 `~/.claude/projects/`，**`IncludeSubdirectories=true`**（抓 `subagents/`）。
- **poll 为 source of truth**（mtime+size cursor），FSW 仅唤醒；只解析**完整行**（无尾换行的末行 buffer 到下次）。
- 多活跃 session 各自一条流（mtime 滚动窗 5min）。
- **完成态（C7）**：append **debounce-reset** 静默 timer；最小静默窗 20s（可配）；末尾 assistant 文本 + 静默 → `Wave`；**`Jump` 需 todo/plan 全完成 或 Stop hook**，否则只 `Wave`。
- 映射：末尾 `tool_use` 未配 `tool_result`→`Run`，思考/读取→`Review`，`is_error`→`Failed`，awaiting input→`Waiting`。

### 6. Codex adapter（第三，接口定义；因精确事件可提前）—— 精确事件（C2）
- 盯 `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`（FSW+poll 同 CC）。
- **直接映射 `event_msg`**：`task_started`→`Run`，`task_complete`→`Jump`（plan）/`Wave`（turn，按 `last_agent_message`）；error→`Failed`。不用静默。

### 7. 设置（`AppConfig.cs` 复用扩展 `HermesConfig`，C13）
- `AgentIntegration`：adapter 开关、轮询/静默/staleness 阈值（Hermes 5s / CC 20s / Jump 3s / staleness 30s）、特效开关、WZ 庆祝节点路径（可手填覆盖）。设置中心新增「Agent 接入」tab。

### 8. App Sandbox 注（C14）
- 当前未沙盒，读 `~/.hermes`/`~/.claude`/`~/.codex` 作同用户 OK。未来 notarize + App Sandbox 需 `com.apple.security.temporary-exception.files.absolute-path.read-only` 读三目录。

## Key decisions & tradeoffs
- 采用 Hermes 官方 7 状态语义（借语义不借 petdex 格式）。
- MiniPet 观察者角色（不起子进程、不抢控制权）。
- **Hermes 走 state.db 只读轮询**（非 FSW，规避 SQLite-tail 不可能 + FSW 可靠性）。
- overlay（`Jump`/`Failed`）+ 复用 PaperdollService expression gateway 驱动实体动作（不新造 seam）。
- 不切实体。
- Codex 用精确 `task_complete`（非静默）；CC 靠 debounce 推断。
- **Hermes 状态覆盖诚实话**：本轮仅 `Run`/`Idle`/`Wave`/`Jump` 可靠（state.db 无 Waiting/Failed/Review 信号）；CC/Codex 覆盖更全。不映射虚构值。

## Risks / open questions
- **Hermes Waiting/Failed/Review 信号缺失**：未来需探 `messages` 表启发式（role/content-free）找真实 approval/error 态。
- **N4**：`hermes sessions list` CLI 以写模式开 state.db、争用 gateway 写锁且实测报错——已从 fallback 移除，`state.db` 只读为唯一源。
- **N5**：Hermes `Run` 依赖 `last_activity_at` 新鲜度（实测多 NULL 僵尸会话）——实现时首验；staleness 兜底降 Idle。
- **`hermes sessions list` 输出**：可选 fallback，实现时 verify 是否含 activity。
- **WZ 庆祝节点**：锁定前必须确认，否则 `Jump` 降级 UI/Basic emotion + 气泡。
- **CC `Jump` 检测**：todo/plan 全完成标记未必所有 session 有；无则只 `Wave`。
- **macOS FSW + poll 双通道**：poll source of truth、不漏不重，需单测。
- **adapter exception storm**：需 backoff/circuit-breaker（R2 minor）。
- **Jump→Wave→Idle 时序 vs staleness timer**：实现时定清（R2 minor）。
- **WorkBuddy 接入方式未知**（后续）。

## Out of scope
- WorkBuddy、QQ 音乐/弹幕、petdex 格式、纸娃娃完整表情集、起子进程驱动 agent、第三方 HermesPet app 集成、Hermes/Codex HTTP API、CC Stop hook（留接口）。
