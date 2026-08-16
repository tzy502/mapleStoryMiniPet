# Plan Review Log: MiniPet 接入主流 Agent 状态（Hermes / Claude Code / Codex）
Act 1 (grill) complete — plan locked with 唐子煜. MAX_ROUNDS=5.

## Act 1 决策轨迹（供 Codex 审查上下文）

1. **角色定位** → 三层适配器汇入统一总线：Hermes adapter（官方语义，深）/ CC+Codex+WorkBuddy adapter（通用，直读本地）/ 媒体 adapter（QQ 音乐/弹幕，后续）。复用现有 EventBus + IExpressionDriver + BalloonService。

2. **数据通路** → 按能力分路：Hermes 走官方语义（无 session 进度 HTTP 端点，读本地）；CC/Codex 直读本地 jsonl（不开 HTTP 端口）；媒体后续定。

3. **Hermes 桌宠身份澄清（关键纠偏）** → grill 中纠正了错误样本（corpusiq 上 38 installs 的第三方 Node skill）。真正的两个候选：A) Nous 官方 Agent Pets（7 状态 + petdex），B) basionwang 第三方 HermesPet app（Swift 原生、聚合 7 agent 含 CC/Codex）。用户确认为 **A**，并采用其官方 7 状态语义。

4. **统一状态模型** → 直接采用 Hermes 官方 7 状态（failed/jump/wave/run/review/waiting/idle），消解"3 段还是 6 段"纠结。

5. **CC/Codex 边界** → 参考 basionwang 的 JSON→状态解析逻辑，但**不起子进程**（MiniPet 是观察者非驱动者），直读 jsonl，即开即用。

6. **反馈层** → 通用特效层（复用 WZ 现成庆祝动画 + emoji 弹窗 + 气泡），全实体通用，不切实体。表情受纸娃娃表情集限制，本轮 default/blink 顶。

7. **本轮范围 = C** → 地基（7 状态模型 + 特效层）+ Hermes adapter 优先。优先级 Hermes > CC > Codex（用户主力排序）。WorkBuddy/媒体后续。

## Act 2 — Codex 对抗审查

### Round 1 — Codex（thread `019feb3d…`，model glm-5.2）
Codex 实地读了 `~/.hermes`/`~/.claude`/`~/.codex` 真实数据布局 + MiniPet 代码库，非空谈。VERDICT: **REVISE**。

**Critical**
- C1 Hermes 活动数据是 SQLite（`state.db` 176MB WAL，`sessions` 表 `last_activity_at`/`handoff_state`/`ended_at`/`messages.active`），**非 JSONL**，FSW 无法 tail。fix：轮询只读连接 或 `hermes status`/`hermes sessions` CLI（均实测存在）。
- C2 Codex rollout 有 `event_msg`：`task_started`/`task_complete`（带 completed_at/duration_ms/last_agent_message），精确完成事件。fix：Codex 直接映射，不用静默推断。
- C3 `EventBus.Publish` 同步在发布者线程调 handler，adapter 从 FSW 线程池发布 → Avalonia UI 线程异常。fix：Publish 内 `Dispatcher.UIThread.Post`。

**High**
- C4 "当前活跃 session"未定义；实测 266 Hermes session、11 CC project（嵌套 `<sid>/subagents/`）。fix：枚举 ended_at IS NULL / mtime 滚动窗，CC `IncludeSubdirectories=true`。
- C5 过期/卡死状态不衰减（FSEvents 丢事件 / agent 死）。fix：每 agent staleness timer，T 秒无事件降 Idle + 清 Failed。
- C6 `Jump` 生命周期未定（last-state-wins 会卡住）。fix：一次性，固定时长衰减。
- C7 8s 静默对 agentic loop 太激进（mid-turn 间隙常>8s）。fix：debounce-reset + 更大窗口 + CC silence 不轻易升 Jump。
- C8 aggregator 活动级排序无 agent tiebreak，与"Hermes>CC>Codex"矛盾。fix：同级用 agent 优先级 tiebreak，文档化 Failed≈Run 选择。

**Medium**
- C9 WZ 庆祝节点未在代码库验证（grep celebrat/Effect/UI/Basic/Item 无结果）。fix：锁定前确认，否则降级 UI/Basic emotion。
- C10 macOS FileSystemWatcher（FSEvents）合并/丢事件；Created handler 读到不完整行。fix：配周期 poll（mtime+size cursor）作 source of truth，只解析完整行。
- C11 Hermes state.db 读并发需 WAL 安全。fix：`Mode=ReadOnly;Pooling=false` + `PRAGMA busy_timeout=3000`，出错重连。
- C12 隐私：state.db/CC jsonl/Codex rollout 含完整对话与密钥。fix：adapter 只发 AgentState+非内容 Detail，绝不 log/persist content。

**Low**
- C13 重复 seam：`AppConfig.cs:99` `HermesConfig` + `PaperdollService.cs:1129` 预留 expression gateway 已存在。fix：overlay 只管 Jump/Failed，实体动作复用预留 gateway；或标记 superseded。
- C14 App Sandbox：当前未沙盒 OK；未来 notarize+沙盒 需 temporary-exception 读三目录。

**Simpler alternative（Codex 建议 v1）**：只 Hermes via `hermes status`/`hermes sessions` 轮询，defer CC/Codex，跳过 FSW——风险最重都在 Hermes+FSW。

### Claude's response（Round 1 → Round 2）
**14/14 全采纳**（实地验证、具体、有 fix，非空谈）：
- C1→Hermes FSW 改轮询（hermes CLI 优先 / state.db 只读 WAL fallback）；C2→Codex 用 task_complete 精确事件；C3→Publish 内 UIThread marshal。
- C4→多 session 枚举+CC subagents；C5→staleness timer；C6→Jump 一次性定时衰减；C7→CC 静默 debounce+大窗+Jump 需强信号；C8→活动级优先+agent tiebreak 文档化。
- C9→WZ 节点锁定前确认/UI/Basic fallback；C10→FSW+poll（poll 为 source of truth）+只解析完整行；C11→SQLite 只读+busy_timeout；C12→隐私规则（只发非内容 Detail）。
- C13→复用 PaperdollService 预留 expression gateway 驱动实体动作，overlay 只 Jump/Failed（不新造 seam）；C14→App Sandbox temporary-exception 注。
- **simpler alternative 部分采纳**：Hermes 走 CLI 轮询（非 FSW）规避 SQLite-tail + FSW 风险；但 CC 仍做 FSW+poll（用户在用、第二优先、无 CLI status），Codex 接口定义。

PLAN 已修订为 Rev 2。Round 2 复审。

### Round 2 — Codex（resume `019feb3d…`，model glm-5.2）
**C1–C14 全 verified addressed**（plan-text 层面；机制修复是 Rev 2 最强部分）。VERDICT: **REVISE**——残留新问题：

- **N1**：Codex 实跑 `hermes status`——输出仅环境/API-key 健康（Python/model/API keys），**零 activity**。Rev 2 把它列为"优先数据源"是 regression。fix：`hermes status` 移出 activity 路径；`state.db` 只读直读为主，`hermes sessions list` 可选 verify-before-use fallback。
- **N2（最重）**：Codex 查 `state.db`——**`handoff_state` 对 266 session 全空**、`last_activity_provenance` 空/unknown、`end_reason` 仅 `cron_complete`/`ws_orphan_reap`。`handoff_state=awaiting_approval→Waiting` 是**虚构映射**。Hermes 仅能可靠推 `Run`/`Idle`/`Wave`/`Jump`。fix：诚实声明 `Waiting`/`Failed`/`Review` 本轮降级 Idle，留接口未来用 `messages` 表启发式探真实信号。
- **N3**：全局 `Publish` 搬 UI 线程会 double-queue（现有 `UserDragStart/End` 已同步+订阅者自 marshal）。fix：marshal 只 scope 到 `AgentStateChanged`，用 `CheckAccess()` 模式（on-thread 同步、off-thread Post）。
- **minor**：adapter 无 backoff/circuit-breaker（exception storm）；Jump→Wave→Idle 时序 vs staleness 欠定义。非 blocker。
- **pattern note**：Rev 2 修对了所有机制问题，但重犯 Rev 1 的错——Hermes 本地数据未验证假设（上移一层）。

### Claude's response（Round 2 → Round 3）
**N1/N2/N3 全采纳**（实地查证，不空谈）：
- N1→`hermes status` 移出，`state.db` 只读直读为主，`hermes sessions list` 可选 fallback。
- N2→Hermes 状态映射改诚实版：仅 `Run`/`Idle`/`Wave`/`Jump` 可靠；`Waiting`/`Failed`/`Review` 本轮降级 Idle，留接口未来用 `messages` 表启发式。**接受 Hermes 状态覆盖比 CC/Codex 薄的事实**，不映射虚构值。
- N3→marshal 只 scope `AgentStateChanged`，`CheckAccess()` 模式，不改现有 8 EventType 同步语义。
- minor→adapter backoff/circuit-breaker、Jump 时序实现时定。

PLAN 已修订为 Rev 3。Round 3 复审。

### Round 3 — Codex（resume `019feb3d…`）
**VERDICT: APPROVED**。N1/N2/N3 全 verified resolved。残留 N4/N5（非 blocker）：
- **N4**：Codex 实跑 `hermes sessions list` → `Could not open session database: state.db is not writable`。CLI 以写模式开库，争用 gateway 写锁。fix：从 fallback 移除，`state.db` 只读为唯一安全源。
- **N5**：Hermes `Run` 依赖 `last_activity_at` 新鲜度（实测 8 open 中 5 个 NULL=僵尸会话）；`messages.active`（88% set）仅 session-existence 不 liveness。fix：实现时首验 `last_activity_at` 实时性；staleness 兜底降 Idle，不会错状态（最多该 Run 时显 Idle）。
Codex 结论：plan 已收敛——机制（C1–C14）+ 数据假设（N1–N3）全解，Hermes 覆盖诚实声明，CC/Codex 承担完整 7 态。

### Claude's response（Round 3 → 收敛）
N4/N5 采纳（小修，纳入 Rev 4）。**Plan 收敛，APPROVED，进入用户签收。**
