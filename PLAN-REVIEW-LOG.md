# Plan Review Log

## Iteration 5 — Hermes 客户端 surface（api_server SSE 发起式，桌宠即入口）
Act 1 (grill-with-docs) complete — plan locked，ADR-0006 改名 `0006-hermes-api-server-client-surface.md`。基于 `claude-prompt-20260810-05.md`（04 作废）。MAX_ROUNDS=5。

Act 1 决策轨迹（7 项）：
1. **MiniPet = Hermes 客户端 surface（发起者）**，非旁观者。用户拍板「跟官方桌宠一样走」：要 session 归属→要 session→要当发起者→**api_server（8642）是官方唯一外部通道**（Open WebUI 同款）。闭环。
2. **数据通道 = api_server SSE**：`POST /api/sessions` 建自己的 session + `POST /api/sessions/{id}/chat/stream` 发起内容收 SSE。事件按发起者路由，自己的 session 天然收得到（Codex R1 F3 证明 WS 无被动订阅，发起式是正解）。
3. **SSE 事件集核实**（api_server.py）：`run.started / message.started / tool.progress(_thinking=reasoning) / tool.started / tool.completed / tool.failed / assistant.completed / run.completed / error / done`。Review 可用 `tool.progress(_thinking)` 映射；**Jump/Waiting 无 SSE 信号**。
4. **MiniPet 变迷你聊天客户端**：点桌宠弹迷你对话框（输入 + 回复显示），发送 → SSE 驱动动画 + 回复滚动。
5. **会话复用**：创建一次 session 多次 chat/stream（连续上下文）。
6. **state.db 兜底保留**：api_server 不可达时回退上轮 SQLite 启发式（含 14 白盒测试）。
7. **AgentState→动画映射配置化**：7 态用户自选动画（设置页），Jump/Waiting 用配置的其他动作实现（SSE 无信号）。

运维前提（接受）：config.yaml 启用 `platforms.api_server` + 设 `API_SERVER_KEY` + 重启 Hermes（本机 8642 未监听）。

## Round 1 — Codex（thread=019ffab1-7255-7430-a2d5-48242118773e，model=glm-5.2 via cc-switch）
VERDICT: **REVISE** — 11 findings（全部源码级核实 api_server.py），Claude 全采纳：
1. **假取消路径**：`chat/stream` 的 `run_id` 不注册到 `_active_run_agents/_active_run_tasks` → `POST /v1/runs/{run_id}/stop` 404。→ 取消 = HTTP 请求中止（CancellationToken），删 stop 声称。
2. **SSE 解析**：`_sse_frame` 事件名在 `event:`、payload 在 `data:`——只读 `data:` 丢事件类型。→ 同时解析两字段。
3. **`assistant.delta` 缺失**：真实流对全部 assistant 文本 emit；只显示 completed 丢中间文本。→ 累积 delta + 对账 `run.completed.messages`。
4. **Session 409**：`POST /api/sessions` 对已存在 id 返回 409。→ 建一次，409/存在即复用。
5. **`effective_session_id` 轮转**：压缩时事件返回新 session_id，用旧 id resume 到 stale parent 重触发压缩。→ 持久化事件里的 session_id 变化。
6. **multiplex**：`multiplex_profiles` 次 profile 走 `/p/{profile}/` + profile-scoped key。→ 非空 profile 用 `/p/{profile}` base + 该 profile key。
7. **长回合衰减 Idle**：Aggregator StalenessSeconds 清 Run/Review/Failed，但 SSE 回合可静默（30s keepalive）。→ SSE 流活跃期间抑制 staleness 或心跳。
8. **并发发送**：active turn 时再 POST 同 session 竞态历史写+动画。→ 发送禁用直到 done/error。
9. **明文 API key 安全回归**：settings.json 可被同步/备份/本地进程读。→ macOS Keychain/Windows Credential Manager 存，settings 存引用。
10. **设置变更不生效**：adapter 启动一次构造，SaveSettings 不重配。→ OnSettingsChanged 重启或文档要求重启。
11. **`run.started` 映射遗漏**：POST 接受与 message.started 之间可落 Idle。→ map run.started→Run（首个 busy）。

### Claude's response（Round 1 → Rev 2）
11/11 采纳（final arbiter），修订 PLAN.md（Rev 2）+ ADR 已更新：SSE 解析 event:+data:、取消=HTTP 中止、assistant.delta 累积、409 复用、effective_session_id 轮转、/p/{profile}、长回合防 Idle、并发发送保护、Keychain 存 key（不可行则明文+文档标注）、设置保存提示重启、run.started→Run。

## Round 2 — Codex（resume 同 thread，model=glm-5.2）
VERDICT: **REVISE** — 4 findings（实现细节），Claude 全采纳：
1. **HTTP abort 不可靠取消**：`_run_agent` 在 executor 跑、无 agent_ref 中断支持 → abort 可能留 turn 继续跑。→ 文档诚实标注"取消为尽力而为"。
2. **delta + completed 重复**：累积 delta 又显示 completed.content 会重复（completed 是权威终稿）。→ delta 实时渲染 + 完成时用 completed 内容**替换** buffer。
3. **staleness 抑制需 finally 重置**：SSE error/abort/timeout 时抑制 flag 若没清，宠物钉 Run/Review。→ finally 清 flag。
4. **delta 需按 message_id 作用域**：多轮回放/乱序流污染。→ delta buffer 按事件 message_id 键控 + message.started 重置。

### Claude's response（Round 2 → Rev 3）
4/4 采纳，修订 PLAN.md（Rev 3）：取消诚实标注尽力而为、回复显示 delta 实时+completed 替换防重复、staleness 抑制 flag finally 清、delta buffer 按 message_id 键控。

## Round 3 — Codex（resume 同 thread，model=glm-5.2）
VERDICT: **APPROVED** — Rev 3 覆盖 Round-2 全部发现，无新阻断缺陷。残留意（纳入实现）：HTTP-abort 可能留 executor 继续跑，UI 须等上个 run 确认停止才允许新 turn（否则并发保护可绕过）。

**Resolution (converged)** — 两 act 完成，plan 锁定（PLAN.md Rev 3），有效审查 3 轮（R1 REVISE×11、R2 REVISE×4、R3 APPROVED）。等待用户实现方式裁决。

---

## Iteration 4 — Hermes gateway 事件流接入（WS surface，全 7 态 + 显式降级链）
Act 1 (grill-with-docs) complete — plan locked，新增 [ADR-0006](docs/adr/0006-hermes-gateway-event-subscription.md)，CONTEXT.md 增「Agent 状态 (AgentState)」术语。MAX_ROUNDS=5。基于 `claude-prompt-20260810-04.md`。

Act 1 决策轨迹（9 项，均经源码核实）：
1. **砍远程（A）** — Hermes 非 loopback 强制 OAuth gate（`?token=` 无条件拒、`--insecure` 已失效于 MCP-persistence 安全修复），第三方 WS 远程直连被设计阻断；远程走 `ssh -L` 隧道，MiniPet 锁 127.0.0.1。删 `HermesHost`。
2. **token 本地必需** — grill 初判"loopback 免 auth"**错**（只看 HTTP `gated_auth_middleware` 层，漏 WS `_ws_auth_reason` 层）；用户纠正 + 源码核实：loopback WS 也强制 `?token=<_SESSION_TOKEN>`，无 token → `no_credential`。来源 `os.environ.get("HERMES_DASHBOARD_SESSION_TOKEN") or secrets.token_urlsafe(32)`（`web_server.py:346`）→ 不设 env 每次重启随机，MiniPet 失联。
3. **WS 值得做** — 全 7 态（hit/mouse/say）是核心卖点，state.db 3 态撑不起；不为省 token 运维放弃。
4. **显式降级 + 测试连接** — token 失败红字「仅 SQLite 3 态」绝不静默（避免上一轮 CC adapter「管道对、末端哑」翻版）+ 设置页「测试连接」按钮主动可见实际链路。
5. **REST token 合并** — 删 `HermesRestToken`（= `HermesToken`，同一 `_SESSION_TOKEN`；REST 用 `Bearer`、WS 用 `?token=`）。
6. **WS 映射补 turn.\*** — `turn.end`→Wave / `turn.error`→Failed / `turn.start`→Run（回合级，比 `message.*` 准；镜像 `derive_pet_state` 优先级）。
7. **探活端点修正** — `/api/sessions`（需 token，plan 原不带 = 401）→ `/api/health`（`PUBLIC_API_PATHS` 免 token，专为 liveness）。
8. **profile 显式化** — plan/现有 state.db 版隐含"默认 profile"，实测本机 3 serve 并存（默认/work-java/desktop-pet）+ 各自 state.db/端口；加 `HermesProfile`，state.db 路径 + 端口按 profile 解析，端口手填优先 + 自动发现仅端口=0 fallback 且按 `--profile` 名过滤。
9. **ADR-0006 + CONTEXT** — 推翻刚验证 OK 的 state.db 改 WS surface 符合 ADR 三标准；CONTEXT 补 AgentState 域术语。

## Round 1 — Codex（thread=019ffa4a-f43d-7162-94f8-8f103f0f51f9，model=glm-5.2）
VERDICT: **REVISE** — 6 findings（Codex 读了 Hermes 源码 + MiniPet 现有 adapter，逐条验证 plan 事实基线）：
- **F1（致命）**：`turn.start/turn.end/turn.error` 是 **compute-host 内部帧，不是 WS 客户端事件**。WS client 真实收到的事件集 = `approval.request / clarify.request / secret.request / sudo.request / error / message.start / message.delta / message.complete / message.interim / reasoning.available / reasoning.delta / thinking.delta / tool.start / tool.complete / tool.generating / tool.progress / status.update / browser.progress / subagent.*`（`gateway-events.ts` 权威集）。`server.py:1739` 把 compute-host `turn.error` 转成 `message.complete(status=error)` 发给客户端。→ **plan §2d 的 turn.\* 映射是错的，回退 message.\* + 内容判定**。
- **F2（重要）**：`message.complete` → `celebrate: true`（JUMP 闪现），非 Wave；`message.complete(status=error)` → Failed。官方 desktop（`pet.ts`/`gateway-events.ts`）：`celebrate/justCompleted/error` 是**瞬时闪现拍（1.6s 衰减 timer）**，`toolRunning/reasoning/awaitingInput/busy` 才是稳态 flag。→ **plan 无 JUMP 事件，补 celebrate 触发（todos 全完成），Wave/Failed 按闪现拍语义**。
- **F3（致命）**：`/api/ws` 是 JSON-RPC，事件按 **sid 路由**（`write_json` 只发给该 session 绑定的 transport）；**无被动订阅 RPC**（无 `session.subscribe`），只有 `session.resume(sid)`（副作用：重挂 detached WS / 取消孤儿回收）。→ **plan 的"连上被动听"架构不成立**；除非 resume 拿会话（= 半参与，违背被动观察）。实测：无 token 裸连 62986 `/api/ws` → **HTTP 403**（鉴权层拒）。
- **F4（高）**：REST `/api/sessions` 的 `is_active = ended_at IS NULL AND now-last_active<300` → **REST 也只能 Run/Idle**，给不了 Waiting/Failed/Review/Wave/Jump。→ plan 把 WS→REST→SQLite 当"优雅降级"，实际 REST 与 SQLite 同级（2-3 态）。
- **F5（中）**：`message.complete` / resolving `tool.complete` 才 `clearAllPrompts` 清 `awaitingInput`（Waiting）。→ plan Waiting 映射无清除语义。
- **F6（低）**：serve/dashboard 默认端口 **9119**（`subcommands/dashboard.py:27`），plan/ADR 用 8642 当默认是任意值（作文档示例可，需注明默认 9119）。

### Claude's response（Round 1 → 修订）
F1/F2/F3 为阻断性发现，改 plan 事实基线（§0）+ 映射（§2d）：删除 turn.\* 映射，改用 client-facing 事件集；补 `message.complete`→celebrate/JUMP + `status=error`→Failed 闪现语义；明确"无被动订阅"——WS 裸连只能收 global broadcast，agent turn 事件需绑定会话（方向待用户裁决，见下）。F4→REST 回退降级说明改为"与 SQLite 同级 2-3 态"。F5→Waiting 由 message.complete/tool.complete 清除。F6→文档默认端口改 9119。

**用户裁决（B 方向，采纳）**：放弃 WS `/api/ws` 主线，回 state.db 扩展。理由：事件按 session id 路由、MiniPet 观察者无 session 归属、裸连收不到 agent 事件；C 缩为广播无意义、A resume 干扰 Hermes surface 管理。裁决细节（全部纳入 Rev 2）：
- 去 WS（不连 /api/ws）；监控链 = state.db 查询（保留 QuerySnapshot/MapState/PollOnce，仅本地）+ REST /api/sessions 轮询补充（近期活跃→Run、刚结束→Wave，失败静默跳过）；两路都经现有 PublishState → AgentStateAggregator，不改接口。
- 7 态走 state.db 启发式：新增 HermesProfile（默认空=默认 profile，state.db 路径按 ~/.hermes/[profiles/<p>/]state.db）；messages 表 role='assistant' 最近 N 条内容无关启发——tool_calls 非空且无完成→Run、reasoning_content 非空→Review、最近 finish_reason='error'→Failed、last_activity_at 近 10s 无 tool→Review（Waiting 降级 Review）；只取列名/计数，绝不 SELECT content。
- 配置修正：删 HermesToken（?token= 作废）、删 HermesMode 的 ws 分支；保留 HermesPort（手填优先，0=自动发现）、HermesRestPollSeconds；新增 HermesApiServerKey（API_SERVER_KEY，明文=本地凭证）。
- 设置页：Hermes 区改「端口 + profile + API Key + REST 轮询周期 + staleness」，删 WS 连接状态，改「最近同步时间 + 当前链路(state.db/rest)」。
- HERMES_INTEGRATION.md：api_server REST（8642 + API_SERVER_KEY）与 state.db 被动监控；未来内容发起走 POST /api/sessions/{id}/chat/stream（SSE）——**标注未证实（源码未找到）**。

### Claude's response（裁决 → Rev 2）
全采纳，修订 PLAN.md（Rev 2）+ ADR-0006（改名 `0006-hermes-state-monitoring-7state.md`，WS 方向废弃、state.db 启发式 + REST 补充）。
**裁决后事实核实**：①messages 表 schema 实测含 role/tool_calls/tool_name/finish_reason/reasoning_content/reasoning_details/codex_reasoning_items——启发式列全真实存在；②api_server 平台 + API_SERVER_KEY + 默认端口 8642（config_defaults.py:4361/4370）核实；③`POST /api/sessions/{id}/chat/stream` SSE 端点 **web_server.py 宽查无命中 → 未证实**，HERMES_INTEGRATION.md 标注"实现前须核实"。

## Round 2 — Codex（resume `019ffa4a`，model=glm-5.2）
VERDICT: **REVISE** — 11 findings（F1–F11），全部源码核实：
- **F1**：`hermes serve --port 8642` 起的是 **dashboard**（/api/sessions 要 `Bearer _SESSION_TOKEN` 非 API_SERVER_KEY）；api_server 是独立 gateway 平台（`gateway/platforms/api_server.py`，`hermes gateway start` 启动）→ **plan REST 目标服务器错**。
- **F2**：api_server `/api/sessions` 返回 `last_active/ended_at`（`_session_response` api_server.py:3280-3287），**无 `is_active`**（那是 dashboard router 加的）。
- **F3（致命）**：`finish_reason='error'` **从不出现**——源码只赋 stop/tool_calls/incomplete/content_filter/length/verification_required（conversation_loop.py:3074-3101）；**DB 实测零 error**（tool_calls 3843/stop 967/verification_required 22/length 2）→ **Failed 启发式是死的**。
- **F4**：SSE `POST /api/sessions/{id}/chat/stream` **真实存在**（api_server.py:2072/:3776，emit run.started/assistant.completed/run.completed/tool.*/error），但**非被动**（POST 触发）——plan 的"未证实"事实错误。
- **F5**：api_server 有 `/p/<profile>/...` 多路路由，REST 无 profile 感知。
- **F6**：启发式优先级未镜像 derive_pet_state——busy/近期活动应→**Run**（非 Review）；应先 Failed。
- **F7**：实际只 5-6 态（Failed 今天是坏的），别号称 7 态。
- **F8**：tool_calls 未完成关联需解析 tool_calls JSON（含 function args）→ 触碰"绝不 SELECT content"红线。
- **F9**：Aggregator 排名 Failed≈Run>Review>Waiting>Wave>Idle + 只衰减 Failed/Run/Review → REST 发的 Wave 会输给 state.db Run/Review。
- **F10**：api_server health = `/health`/`/v1/health`（:2056-2058），非 `/api/health`（那是 dashboard 的）。
- **F11**：启发式作用域未定义（active=1、未结束会话、时间窗），compacted/历史行可让 Review/Failed 常驻。

**Claude 核实**：F1/F2/F3/F4/F10 全部源码 + DB 坐实（api_server.py 存在、_session_response 字段、finish_reason 零 error、SSE 端点、/health）。

### 用户裁决（Round 2 → Rev 3）—— 砍 REST，纯 state.db
选 **a：砍 REST，纯 state.db**。理由（按事实链）：REST 读的是同一 state.db（serve 起 dashboard），字段仅 last_activity_at/ended_at（无 is_active），绕 REST 一圈 = 同一数据源 + token 鉴权负担，**零增量信息**；b（api_server）是未来发起阶段依赖（桌面端默认不跑 gateway 平台），被动监控不值得多养服务；c（dashboard Bearer）把上一轮已否决的 token 运维请回来。最终监控架构：**单数据源 state.db**——现有 3 态（已验证）+ messages 启发式扩展 7 态，无 REST、无 WS、无 token；未来发起阶段独立引入 api_server SSE。
按此改：HermesAdapter 去 REST 轮询（保留现有 SQLite）；删 `HermesRestPollSeconds`/`HermesApiServerKey`；设置页 Hermes 区只留「profile + staleness + 同步时间显示」。

### Claude's response（裁决 → Rev 3）
全采纳，修订 PLAN.md（Rev 3）+ ADR-0006（单数据源 state.db 启发式）：删 REST/WS/token、删 HermesRestPollSeconds/HermesApiServerKey、Failed 按 F3 事实诚实标注弱信号（incomplete/content_filter/length）、Waiting 降级、优先级镜像 derive_pet_state（busy→Run）、启发式作用域限定 active=1 + 时间窗、SSE 归未来发起阶段（事实修正：已证实存在但非被动）。

## Round 3 — Codex（resume `019ffa4a`，model=glm-5.2）
VERDICT: **REVISE** — 6 findings（F1–F6，全在启发式本身），全部源码+DB 核实：
- **F1**：`finish_reason='length'/'incomplete'` 是**续传信号非终端失败**（conversation_loop.py:3205-3218 归一化续传；codex_responses_adapter.py:1334 重试 3 次）；DB 2 行 length 均 active=1 未结束。→ Failed 仅 content_filter 或 ended 标记。**实测 active=1 下零 content_filter → Failed 本轮无实际覆盖**。
- **F2**：`tool_calls` 非空不是"进行中"信号（完成行永远非空，Run 压 Review，staleness 不修复）。→ Run 需最新行 role='assistant'+tool_calls+**无后续 role='tool' 行**。**实测 messages.role 有 tool(4547) 行**——判定可行。
- **F3**：plan 基线用全表（tool_calls 3843/stop 967），active=1 过滤后为 tool_calls(3433)/stop(836)。→ 启发式基线用 active=1 同 scope。
- **F4**：profile 路径忽略 HERMES_HOME（hermes_constants.py:63-75）。→ state.db 根尊重 HERMES_HOME，不硬编码 ~/.hermes。
- **F5**：现有 AppConfig.cs 字段是 `StalenessSeconds`（:74），非 HermesStalenessSeconds。→ 复用现有字段，不改名。
- **F6**：标题"7 态"过誉（实际 Idle/Run/Wave/Review + 弱 Failed，无 Waiting/Jump）。→ 标题改 5 态诚实范围。

**Claude 核实**：F1/F2/F3/F5 全部 DB 坐实（role 分布、length active、active=1 分布、AppConfig 字段）；F4 现有代码硬编码 ~/.hermes（:343-345）确认。

### Claude's response（Round 3 → Rev 4）
6/6 采纳，修订 PLAN.md（Rev 4）+ ADR-0006：Failed 仅 content_filter/ended 标记（length/incomplete 当续传，明确 DB 零 content_filter → Failed 无实际覆盖）、Run 判定加"无后续 role='tool' 行"（tool 行实测存在）、finish_reason 基线 active=1 scope、state.db 根尊重 HERMES_HOME、复用现有 StalenessSeconds、标题改 5 态诚实范围。

## Round 4 — Codex（resume `019ffa4a`，model=glm-5.2）
VERDICT: **REVISE** — 3 findings（窄修正），全部核实：
- **F1**：Failed 的 "ended 会话标记" 未定义会误报——`end_reason` 实测仅 `cron_complete(361)/(null)(21)/ws_orphan_reap(4)`，无失败特定值；若实现为"任何 ended"则每个正常完成会话都变 Failed 且 Failed 压 Wave 吞掉 Wave。→ **删 ended 分支，Failed 仅 content_filter**。
- **F2**：ADR consequences 与 Rev 4 不同步（仍写 7-state、length/incomplete 当 Failed 近似）。→ 同步 5 态。
- **F3**：role 基线全表（tool 4547）与 finish_reason active=1 不一致。→ 统一 active scope（active=1 下 tool 4055）。

**Claude 核实**：F1 end_reason 实测 3 值无失败；F3 active=1 role 分布 assistant(4344)/tool(4055)/user(972) 确认。

### Claude's response（Round 4 → Rev 5）
3/3 采纳，修订 PLAN.md（Rev 5）+ ADR-0006：Failed 仅 content_filter（删 ended 标记，end_reason 无失败值）、ADR consequences 同步 5 态 + Failed=content_filter-only/有效缺失 + Run=latest-unresolved-tool、role 基线统一 active=1 scope。

## Round 5 — Codex（resume `019ffa4a`，model=glm-5.2）**MAX_ROUNDS=5 封顶**
VERDICT: **REVISE** — 2 findings：
- **F1（重要）**：优先级 `Failed>Wave>Run>Review>Idle` + busy→Run 兜底让 **Review 永远不可达**（活跃 reasoning 会话也有近期活动，Run 总赢）；canonical 顺序 tool_running→reasoning→busy。→ 明确求值顺序 `Failed → Wave → unresolved-tool Run → reasoning Review → busy Run → Idle`。
- **F2（低）**：元数据过期（PLAN header 仍 Rev 4、Run bullet 4547 vs active 4055、ADR context 仍 7-state 措辞）。

### Claude's response（Round 5 → Rev 6，final arbiter）
2/2 采纳，修订 PLAN.md（Rev 6）+ ADR-0006（改名 `0006-hermes-state-monitoring.md` 去 7state 误导）：求值顺序明确化（Review 先于 busy→Run）、计数统一 active=1（tool 4055）、ADR 措辞 5-state。

**Resolution（cap hit 但实质收敛）** — 两 act 完成，5 轮 Codex 审查（R1 6 / R2 11 / R3 6 / R4 3 / R5 2，共 28 条意见，全采纳），max cap 无 APPROVED 但 Codex 自评"substantive architecture is now sound and source-verified"，剩余仅文档/求值顺序打磨。等待用户签收 + 实现方式裁决。

---

## Iteration 3 — 纸娃娃模块重新设计（实时合成 + 统一素材选择库）
Act 1 (grill-with-docs) complete — plan locked，CONTEXT.md 增「部件/耳朵类型/表情/皮肤/染发/遮挡锁/搭配/实时合成」术语，新增 [ADR-0005](docs/adr/0005-paperdoll-real-time-compositing.md)（取代 ADR-0002）。MAX_ROUNDS=5。
11 项决策：①实时合成弃图条；②AnimService 帧提供者接口(加法式)；③耳朵按 WZ piece 名分组(人类靠 head 圆耳)；④统一素材选择库重做(保留 Mob/NPC)；⑤眨眼表情+外部驱动接口；⑥全部动作+武器映射+拖拽随机 fly/swing；⑦zmap 遮挡 locks；⑧搭配持久化+离线缓存；⑨皮肤+染发；⑩草稿模式；⑪WriteableBitmap 直拷。

## Round 1 — Codex（thread=019fe6a8-7a0e-7471-81eb-b8c06f58da83，model=glm-5.2）
VERDICT: **REVISE** — 9 findings：
- **F1（高）**：哨兵调用点未迁移——空闲随机(AnimService.cs:308)、拖拽(PetWindow.axaml.cs:211)、动画菜单(PetWindow.Menu.cs:230)、气泡锚点仍经 `SpriteService`/`GetCachedActionList` 解析哨兵动作。→ 全部改经 `IEntityFrameProvider`。
- **F2（高）**：player 动作破坏现有状态机——纸娃娃动作 `stand1`/`walk1`/`swingOF` 不在 `_animDefs`；OneShot 完成硬编码回 `stand`(AnimService.cs:199)。→ 加 player 动作定义/别名(`stand→stand1`)、OneShot 回退按实体解析。
- **F3（高）**：locks 算法未明确——`buildLock` 按 item ID 升序+走 zmap 锁 vslot；`refreshLock` 双查 self/layer-vslot + `mailArm`/`mailChest`/`pants`/`backPants`/`backHead`/cap/ear-fix 分支。→ 移植精确算法，非仅"smap+islot/vslot"。
- **F4（高）**：layer resolution 不完整——`ResolveLayer` 缺数值 z、`Below/Under/Over/Above`、`ZmapIndex.fixLayers`。→ 移植 `resolveUseablePieceName` + `ZmapIndex`。
- **F5（高/修正采纳）**：female body/skin 映射——Codex 称 MapleSalon2 用 female 12000/head 22000；**核实为误读**（store.ts:47 的 12000 是默认 Head id）。采核心：皮肤=Body/Head 槽 id(2000–2999 / +10000)，拒绝错误数值。
- **F6（高）**：Earring/Overall 可选不可渲染——`EnumerateRoots` 缺 Earring、105xxxx 无字段。→ 加字段与前缀映射。
- **F7（高）**：共享合成器状态竞态——预览/帧循环并发改 `_current`。→ `RenderFrame` 用不可变外观快照+代际 state。
- **F8（中）**：离线缓存与重写矛盾——弃图条又复用哨兵图条，无 provider fallback/outfit-keyed 失效。→ 按外观哈希 key + 搭配变化失效。
- **F9（中）**：素材枚举未统一——`GetEquipList` 枚举不同节点+cap 200。→ 单一 `_Canvas` 枚举器 + `String.wz` 名称 + 分页。

### Claude's response
9/9 采纳（F5 修正采纳——核心采纳、错误数值拒绝，已核实 MapleSalon2 itemId.ts 皮肤=body/head 槽 id）。已修订 PLAN.md（Rev 1）：F1→§2 哨兵调用点全量迁移；F2→§2 player 动作定义/别名+OneShot 回退；F3→§1 locks 精确分支移植；F4→§1 layer resolution 精确移植；F5→§1 皮肤=Body/Head 槽 id + 诊断验证，拒绝 female=12000/22000；F6→§1 `EnumerateRoots` 增 Earring/Longcoat；F7→§1 `RenderFrame` 不可变快照+代际；F8→§5 离线缓存外观哈希 key+失效；F9→§4 单一枚举器+`String.wz`+分页。无拒绝。

## Round 2 — Codex（resume 同 session，model=glm-5.2）
VERDICT: **APPROVED** — F1–F9 全部确认解决，无新阻塞缺陷。两条非阻塞实施约束，均已纳入 PLAN：①F1 清扫须显式含 `AnimService.Play`/`HasSpriteForAction`/`LoadStripWithFallback` + `PetWindow.StartAnimation`（启动 `Play("stand")` 仍走旧图条门 AnimService.cs:84 / PetWindow.axaml.cs:342）；②blink 为多帧表情时 `RenderFrame`/`IEntityFrameProvider` 需 face-expression 帧索引或内部表情时间线（对齐 characterFaceFrame.ts:99）。

**Resolution (converged)** — 两 act 完成，计划锁定（PLAN.md Rev 1），等待实现方式裁决。有效评审 2 轮（R1 REVISE×9、R2 APPROVED）；另有 1 次 Round 1 代理故障重跑（cc-switch 未启动，用户启动后重跑成功）。



## Iteration 2 — 背景调整修复（渲染对齐 + 菜单历史 + 选图器并入设置中心）
Act 1 (grill-with-docs) complete — plan locked，CONTEXT.md 增「裁剪视口」术语，新增 ADR-0004。MAX_ROUNDS=5。
四问题方案：①整图渲染(相机=地图中心)→按像素裁 crop + 移植后端 back 公式；②菜单「开启/关闭背景」toggle；③「最近使用×4」；④删 MapBrowserWindow，完整选图器并入设置中心背景 tab，crop 框高对比。

## Round 1 — Codex（thread=019fbbeb-fafc-71b0-9a7b-11d7befb4adf，model=glm-5.2）
VERDICT: **REVISE** — 12 findings（首轮空 prompt / 误读 iter1 日志两次失败已重跑，本轮为有效评审）：
- **F1（阻断/编译）**：删 `MapBrowserWindow.cs` 会连带删 `MapSelection`/`MapEntry`（仅定义于此文件:20,23），致 PetWindow/SettingsWindow/新选图器 3 处编译断。→ 删文件前先搬 record 到 Models。
- **F2（阻断/数据丢失）**：`MapHistory` schema `List<string>`→`List<MapHistoryEntry>` 使旧配置反序列化抛 `JsonException`，`ConfigService.LoadSettings` 捕获→`CreateDefaultSettings`→`SaveSettings` 覆写（ConfigService.cs:54-65），用户丢全部配置。→ 必须加 `JsonConverter<MapHistoryEntry>`（string/object 兼容）。
- **F3（高/内存）**：`SKBitmap` 子集是别名非深拷贝；缓存一换 crop 读野指针。→ crop 用 `SKCanvas(dst).DrawBitmap(full,src,dst)` 深拷贝到新 `SKBitmap(cw,ch)`。
- **F4（高/正确性）**：`RenderMapFull` 读 WZ 须 `WithWzLock`（`ExtractPng`/`GetOrigin` 不自锁）；Monitor 可重入不死锁。锁外做发布/拷贝。
- **F5（中）**：crop 越界规则自相矛盾（clamp vs 留透明）+ ApplyMapSelection/LoadDefaultPet 不一致。→ 统一：固定 cropW×cropH 画布、清透明、源矩形求交 blit。
- **F6（中/关键）**：现 `DrawBack` **未减 origin**；PLAN 符号写反（`+oriX`）。→ 绘制时 `floor(px)-MinX-oriX`，取 `_wz.GetOrigin`。
- **F7（中）**：`MapBack` 无 `ScreenMode` 字段。→ 加属性 + ParseBacks 读取 + `!=0` 跳过。
- **F8（低）**：`bgItem` 局部、`Opening` 不重建；空历史 4 槽塌成 1 占位（非固定 6 项）。→ 持 `_bgMenu` 引用、Opening 重建。
- **F9（低）**：toggle-open 同步 `LoadMap` 阻 UI；null/未加载未回退设置 tab。→ 后台 Task + 失败回退 `OpenBackgroundTab`。
- **F10（低）**：`MapHistory.RemoveAll(x=>x==sel.Id)` 改 schema 后不编译；最近项应用从 entry 自带 crop。→ `x.Id==sel.Id`、`Insert(MapHistoryEntry{...})`。
- **F11（低）**：缓存 SKBitmap 须切图/退出 Dispose；并发 last-wins。→ Dispose 旧 + `_fullRenderGen`。
- **F12（低）**：开机重启用与 F5 互动；clamping 交 SetMap，后台线程渲染。

### Claude's response
12/12 全部采纳，已修订 PLAN.md（Rev 2）：F1→§0 类型搬迁前置；F2→§4 `JsonConverter<MapHistoryEntry>` 注册 + fixture；F3→§1 crop 深拷贝；F4→§1 `WithWzLock` 包 WZ 段；F5→§1 `CropFull` 统一越界规则；F6→§1 back 绘制减 origin、修正符号；F7→§1 `MapBack.ScreenMode`；F8→§2 持 `_bgMenu`+Opening 重建、措辞改"toggle+0~4+调整"；F9→§2 后台 Task+回退；F10→§4 `MapHistoryEntry`+调用方改写；F11→§1 缓存 Dispose+`_fullRenderGen`；F12→§1 开机 clamping 交 SetMap。无拒绝。

## Round 2 — Codex（resume 同 session，model=glm-5.2）
VERDICT: **REVISE** — F1–F12 全部确认解决；新增 4 条：
- **N1（必然静默失败）**：迁移产生 `Crop*=0` 历史项→`SetMap` 命中 `cropW<=0` 短路（`_image.Source=null`）静默不显示；PLAN 称"无 crop=整图"却无归一化点，且删了原两个默认来源（MapBrowser 默认 / LoadDefaultPet zero→full）。
- **N2（flip 符号 bug）**：back flip 现绕 `(bx-oriX+w/2)` 中心，Java 绕放置点 `bx`（`translate(bx,by);scale(-1,1);draw(-ox,-oy)`）；非中心锚点错位。可算符号错，非"目视验收"。
- **N3（归属歧义）**：`OpenBackgroundTab()` 在 PetWindow/SettingsWindow 各定义一半；`ShowSettingsWindow` 已可见早返回会跳过切 tab。
- **N4（低/native 泄漏）**：并发缓存未命中，输者 SKBitmap 不 Dispose（F3 已防 corruption，仅内存）。

### Claude's response
4/4 全部采纳，已修订 PLAN Rev 3：N1→§1 `CropFull` 入口 `cw<=0→worldW/worldH`（"无 crop=整图"唯一归一化点，全链路统一；`SetMap` 移除短路）；N2→§1 back flip 改 `Save/Translate(bx,by)/Scale(-1,1)/DrawBitmap(-ori)` 绕放置点，`DrTiled` 每 tile 同规则；N3→§3 `SettingsWindow.OpenBackgroundTab()` 独占 `SwitchSubTab(2)+Show/Activate`，PetWindow 仅确保实例不早返回；N4→§1 发布 CAS：`_fullRenderGen` 过期则 Dispose 自己的 bitmap 不发布。tile/obj 的 `DrawSprite` flip 同类问题标注为 out-of-scope（pre-existing，非本轮 back 范围）。无拒绝。

## Round 3 — Codex（resume 同 session，model=glm-5.2）
VERDICT: **APPROVED** — N1–N4 全部具体解决，F1–F12 复核仍在。两条非阻塞备注（均已涵盖）：tile/obj `DrawSprite` flip 的 out-of-scope 划分正确；选图器单击不拖 crop 也会落到 N1 的整图默认（与"无 crop=整图"契约一致，不仅迁移路径可达）。无新阻塞缺陷。渲染数学现已与 Java 参考可算等价（非目视），schema 迁移安全，缓存/线程模型无 corruption 或泄漏路径。

**Resolution (converged)** — 两 act 完成，计划锁定（PLAN.md Rev 3），等待实施方式裁决。有效评审共 3 轮（R1 REVISE×12、R2 REVISE×4、R3 APPROVED）；另有 2 次 R1 失败重跑（空 prompt / 误读 iter1 日志）。

## 实现（Claude，2026-08-01，按 PLAN Rev 3）
8 步全部落地：
- F1：新增 `MiniPet/Models/MapSelection.cs`（搬 `MapSelection`/`MapEntry`），删 `MapBrowserWindow.cs`。
- F2/F10：`AppConfig` 加 `MapHistoryEntry`，`PetSettings.MapHistory`→`List<MapHistoryEntry>`；`ConfigService` 加 `MapHistoryEntryConverter`（兼容旧 `List<string>`）注册到 `GetJsonOptions`。
- F7：`MapBack.ScreenMode` 字段 + `ParseBacks` 读取 + `!=0` 跳过。
- 渲染（F3/F4/F5/F6/N1/N2/N4/F11）：`MapService` 新增 `RenderMapFull`(WithWzLock+缓存+Dispose)、`RenderCore`、`CropFull`(归一化+求交+深拷贝)、改写 `DrawBack`/`DrTiled`/`DrawBackOnce`(减 origin + flip 绕放置点)、`GetBackTileMode`；`CompositeMapThumbnail` 消费 `RenderMapFull`；删 `CompositeMapFrame`。
- `BackgroundWindow.SetMap` 改 `CropFull`、移除 cropW<=0 短路。
- PetWindow（F8/F9/F10/F12/N3）：`_bgMenu` + `RebuildBgMenu`(Opening 重建：toggle+最近×4+调整地图)、`ToggleBackground`(后台 LoadMap+回退)、`OpenBackgroundTab`；删 `OpenMapBrowser`/顶层打开地图浏览器；`ApplyMapSelection` 改 `MapHistoryEntry`；`LoadDefaultPet` 重开自动加载。
- SettingsWindow（§3/N3）：`OpenBackgroundTab()` public；`BuildBackgroundTab` 重写为完整选图器(搜索/列表/分页/大预览/拖crop，亮黄高对比框)；删 `OpenMapBrowserFromSettings`/`RefreshMapSummary`/`UpdateMapPreviewThumbnail`/`ApplyBackgroundToMain`；死方法 `UpdateMapPreview` 的 `CompositeMapFrame` 引用改 `CompositeMapThumbnail`。
- 行为细化：选图器未拖 crop → 默认中心 800×600 视口（与图1期望一致）；`CropFull` 的 cw<=0→整图 仅作用于迁移/零 crop 历史项。
- **编译自检未做**：本 shell 无 `dotnet`（用户环境构建）。MapService 渲染段已目视复核结构连贯。

---

## Iteration 1 — 背景地图系统重构（已完成 Act 3 构建）
Act 1 (grill) complete — plan locked with the user. MAX_ROUNDS=5（后上调至 7）。

## Round 1 — Codex (2026-08-01, model=deepseek-v4-flash, thread=019fb921-4d88-79e3-aa9c-a5f8f8a01411)

VERDICT: **REVISE** — 8 findings:

1. **macOS 穿透**：`IsHitTestVisible` 只影响 Avalonia 应用内命中测试，`Background=Transparent` 的原生 NSWindow 仍捕获点击。需 ObjC interop 设 `NSWindow.ignoresMouseEvents`。
2. **`_wzLock` 非完整操作锁**：`FindNodeByPath`/`ResolveToCanvas` 锁内返回节点后，调用方在锁外遍历 `.Nodes`/`TryExtract`（WzService.cs:413、696）。并发地图枚举+名称缓存+后台预览会与现有操作竞态。
3. **`CompositeMapFrame` 非全图缩略**：预览 w/h 渲染的是以地图内容中心为相机中心的视口，不是整图缩略（MapService.cs:241）。
4. **`PlacePetOnMapGround` 忽略 crop origin**：仍用 `map.MinX`/`map.MinY`，crop 起点偏离地图左上角时宠物错位（PetWindow.axaml.cs:865）。
5. **`SetMap` 无保护异步渲染**：快速切图/切 crop 时旧 `Task.Run` 覆盖新图（BackgroundWindow.cs:61）。
6. **crop 字段缺失 + 无校验**：PetSettings 无 crop 字段；全零/非法值会产生零尺寸窗口。
7. **设置无背景应用事件**：`SettingsWindow` 只有 `OnPetApplied`/`OnPaperdollApplied`，缺地图应用事件与统一持久化入口。
8. **菜单勾选态过期 + 手动拖动脆弱**：菜单构建一次，`Opening` 只刷新动画菜单；应持 MenuItem 引用刷新勾选态，拖动用现有 `BeginMoveDrag` 模式。

### Claude's response
8/8 全部采纳并核实到具体代码行（BackgroundWindow.cs、MapService.cs:241、PetWindow.axaml.cs:865、WzService.cs 多处锁外访问、SettingsWindow.axaml.cs 事件、AppConfig.cs PetSettings 无 crop 字段）。均修订进 PLAN.md，见 Approach 对应条目。

## Round 2 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 5 findings:

1. **WZ 锁修复仍不完整**：现有 `ExportSpriteStrip`/`BuildSpriteStripPng`/`GetActionList`/`GetBalloonIdNode` 仍在 `_wzLock` 外调 `TryExtract()`（WzService.cs:413、551），后台地图预览仍会与现有预加载路径并发损坏节点树。
2. **`MapService.LoadMap` 元数据遍历不在完整锁内**：`FindNodeByPath` 后锁外枚举返回节点（如 foothold），两个并发 LoadMap 或 LoadMap + 精灵提取仍竞态。
3. **启动名称缓存构建是 UI 线程 + 陈旧风险**：上千条 `id→名` 字典启动同步构建会卡 UI；WZ 重建后缓存陈旧。
4. **交互模式无焦点/激活处理**：`BackgroundWindow` 是 `ShowActivated=false`，进入交互模式后 Esc 可能仍发给 PetWindow、`BeginMoveDrag` 收不到可靠输入。
5. **全图缩略缓存无界**：浏览上千张地图时缓存每张 2000×1500 位图会耗尽内存。

### Claude's response
5/5 全部采纳。1 → Approach 1 锁范围扩大到 4 个现有提取方法；2 → `LoadMap` 元数据经 WzService 持锁入口；3 → 目录缓存后台构建 + `LoadWz` 成功后重建；4 → `SetInteractive` 时焦点/Esc 处理；5 → 缩略图只缓存当前选中地图。

## Round 3 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 4 findings:

1. **WZ 节点遍历序列化仍不完整**：`GetDirectoryChildren`/`GetFramePieceNames`/`GetPieceMap`/`GetZmapOrder`（WzService.cs:696）及 `PaperdollService`（GetActionFrameCount:414/GetActionList:428/GetEquipList:446）仍锁外枚举 `Wz_Node.Nodes`。只锁部分方法，地图预览与纸娃娃/精灵工作仍可竞态。
2. **`MapBrowserWindow` 预览无代际保护**：只给 `BackgroundWindow.SetMap` 加了 `_mapRenderGen`，浏览器快速切地图时旧缩略图任务可最后完成覆盖当前预览/crop。
3. **目录重建与 WZ 重载竞态**：每次 `LoadWz` 后重建目录无版本化，用户改 WZ 路径时旧构建任务可能发布陈旧名称。
4. **克隆 `MaterialBrowserWindow` 带入同步 WZ 加载**：WZ 未加载时打开浏览器，其构造函数同步 `LoadWz`+同步枚举全量目录，阻塞 UI 线程。

### Claude's response
4/4 全部采纳。1 → WzService 4 个枚举方法整体持锁 + 暴露 `WithWzLock`，PaperdollService 锁外枚举包进同一锁；2 → `MapBrowserWindow` 加 `_previewGen`；3 → 目录构建版本号，仅发布最新 `LoadWz` 结果；4 → `MapBrowserWindow` 构造只显示加载态，目录后台就绪后填充。

## Round 4 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 3 findings:

1. **`MapBrowserWindow` 无 WZ 加载启动路径**：有加载态但没说谁启动后台 `LoadWz`，打开时 WZ 未就绪可能永久停在加载态。
2. **交互模式在背景隐藏时不重置**：`HideMap()`/`Hide()` 留下 `Interactive=true`+`ignoresMouseEvents=false`，下次 `Show()` 变可点击而非默认穿透。
3. **交互菜单在无背景/无 map 时被调用**：`TryGetPlatformHandle()` 为 null，原生穿透切换静默 no-op。

### Claude's response
3/3 全部采纳。1 → Approach 2 明确后台 `LoadWz`→构建目录→填充列表的启动路径（spinner）；2 → Approach 3 `HideMap()`/`Hide()` 时 `SetInteractive(false)`，每次显示回到穿透态；3 → Approach 3/4 `SetInteractive` 句柄不可用时安全返回，菜单项无背景时 `IsEnabled=false`。

## Round 5 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 2 findings:

1. **"所有 WZ 节点访问持锁"声明仍不完整**：`PaperdollService.CollectPieces`（:143）锁外枚举 `faceRootNode.Nodes`；`WzService.GetFrameCount`/`AutoDiscoverMappings` 锁外遍历子节点。逐个点名总会有遗漏。
2. **多 `MapBrowserWindow` 实例重复启动后台 LoadWz/目录构建**：无共享 in-flight 任务，`WzService.LoadWz` 未防并发调用。

### Claude's response
2/2 全部采纳。1 → Approach 1 改为实施时对**全部 Wz_Node 消费者**做锁审计统一包装（含 CollectPieces:143、GetFrameCount、AutoDiscoverMappings），不逐个点名；2 → Approach 1/2 目录构建**单飞** + `LoadWz` 并发守卫，多窗口 await 同一就绪任务。

**MAX_ROUNDS=5 已到，未获 APPROVED → Resolution (deadlock)。Claude 最终立场：5 轮 22 条意见全部采纳修订，无拒绝；计划已收敛，剩余为实施时的锁审计 checklist 而非结构性缺陷。交用户裁决。**
→ 用户裁决：**提高 MAX_ROUNDS 至 7 再跑**，复审最后两条修订。

## Round 6 — Codex（resume 同 session，2026-08-01）

VERDICT: **APPROVED** — 先前意见全部解决：全部 `Wz_Node` 消费者锁审计、`LoadWz` 并发守卫 + 目录构建单飞、多窗口 await 同一就绪任务、交互模式/持久化/渲染代际/macOS 穿透修复均保留。无新实质缺陷。剩余风险（ObjC `ignoresMouseEvents` 互操作写法、crop 预览到世界坐标换算）为实施细节而非计划阻塞。

**Resolution (converged)** — 两 act 完成，计划锁定，等待实施方式裁决。

## Act 3 — Build（2026-08-01，Claude 实现）

实现 PLAN 全部 7 个 Approach（+ 设置中心卡顿修复）：
- **卡顿修复**：SettingsWindow 预览动画去磁盘 IO（缓存源 Bitmap + CroppedBitmap）、打开异步化（LoadPreviewActionAsync 代际保护）。
- **Approach 1**：WzService ListMapIds(21230)/BuildMapNameCache(21206)/EnsureMapCatalog(单飞+版本化后台)/WithWzLock；4 个 TryExtract 方法整体 `_wzLock`；LoadWz 并发守卫；MapService.LoadMap→LoadMapCore 经 WithWzLock；PaperdollService 3 个锁外枚举方法包 WithWzLock。
- **Approach 2**：MapBrowserWindow（三栏+搜索+分页+crop 拖矩形选择器，后台 CompositeMapThumbnail+代际，await 目录缓存）；MapService.CompositeMapThumbnail（整图缩略，WithWzLock）。
- **Approach 3**：BackgroundWindow 交互模式+原生 NSWindow.ignoresMouseEvents(ObjC interop)+crop 渲染+_renderGen 代际+BeginMoveDrag+Esc+HideMap 重置。
- **Approach 4**：PetWindow 菜单接线（浏览地图/交互模式勾选项 Opening 刷新 IsChecked+IsEnabled）、PlacePetOnMapGround(map,cropX,cropY)、ApplyMapSelection 持久化。
- **Approach 5**：SettingsWindow 背景 tab 重构（移除硬编码 _mapPresets，改浏览器入口+摘要+应用）、OnBackgroundMapApplied 事件、AppConfig CropX/Y/W/H。
- **Approach 6+7**：开机自动展示（LoadDefaultPet crop 校验 clamp 边界）+ 运行验证。

### 验证
- **编译**：0 错误。
- **运行**：WZ 加载(Mob 4387/Map 21230)、地图目录 21206、纸娃娃合成 2085 字节（锁审计未破坏）、无死锁/Exception、进程稳定。
- **手动关键点审查**：_wzLock 重入安全（Monitor 可重入，WithWzLock→FindNodeByPath/LoadMapCore→ParseTiles 均同线程重入）；crop 坐标换算正确（_thumbScale=worldW/thumbW、viewX=cropX-MinX、cropX+bgW/2）；代际保护正确（_previewGen/_renderGen 丢弃过期）。
- **Codex 验证**：API 返回 404（bigmodel.cn coding/paas/v4/responses endpoint 故障，ACT2 时正常），重试多次均失败，无法执行 Codex 审查。改用上述手动审查 + 编译/运行验证代替。
- **待用户交互测试确认**：macOS 原生 ignoresMouseEvents interop 的 objc_msgSend arm64 ABI（bool 参数传 IntPtr 1/0）、crop 拖矩形交互、交互模式穿透↔拖动切换。
