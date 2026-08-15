// TODO: csproj 需加 <PackageReference Include="Microsoft.Data.Sqlite"/>（Config 模块统一加）
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using MiniPet.Models;

namespace MiniPet.Services.Adapters
{
    /// <summary>
    /// Hermes agent 状态适配器（PLAN Rev3：MiniPet = Hermes 客户端 surface，api_server SSE 发起式）。
    ///
    /// 主链路：api_server SSE 客户端（Hermes 官方唯一外部客户端通道）。
    ///   - 建会话：POST /api/sessions（Bearer API_SERVER_KEY；**建一次持久化 session id，已存在/409 → 复用**）。
    ///   - 对话：POST /api/sessions/{id}/chat/stream（body {"message": msg}），SSE 逐行解析 **event: + data:** 两字段。
    ///   - 事件→AgentState：run/tool/message.started→Run、tool.progress(_thinking)→Review、tool.failed/error→Failed、
    ///     assistant.delta→DeltaReceived、assistant.completed/run.completed(无 error)→Jump（本地合成）再衰减 Wave、done→Idle。
    ///   - 取消：CancellationToken 中止 HTTP 请求（**不用 /v1/runs/stop，404 假路径**）；尽力而为，Hermes 端 turn 可能继续跑。
    ///
    /// 兜底：api_server 不可达（构造/发送异常）→ 回退现有 state.db 只读 SQLite 轮询启发式（下述原逻辑保留不动）。
    ///   - 严禁 hermes status / hermes sessions list（CLI 以写模式开库、争用 gateway 写锁 — N1/N4）。
    ///   - 状态映射（5 态启发式，求值顺序 Failed → Wave → unresolved-tool Run → reasoning Review → busy Run → Idle）。
    ///
    /// 隐私红线：Detail 只放非内容摘要；绝不 SELECT content；token/API key 绝不 log/persist。
    /// </summary>
    public sealed class HermesAdapter : IAgentStateAdapter
    {
        // ===== 常量 =====
        private const int PollIntervalMs = 5000;           // 轮询周期 5s（可配，阶段1 常量化）
        private const int BusyTimeoutMs = 3000;            // PRAGMA busy_timeout
        private const int StalenessMultiplier = 2;         // last_activity_at 新鲜度阈值 = 2 * 轮询周期
        private const int RecentEndedWindowMultiplier = 2; // ended_at「刚被填」窗口 = 2 * 轮询周期
        private const int MaxBackoffMs = 60_000;           // 退避封顶 60s
        private const int InitialBackoffMs = 1_000;        // 初始退避 1s
        private const int ConsecutiveErrorThreshold = 5;   // 连续异常阈值 → 触发熔断暂停
        private const int CircuitBreakPauseMs = 60_000;    // 熔断暂停时长 60s

        // ===== api_server 常量 =====
        private const string DefaultHost = "127.0.0.1";     // 锁死本机（PLAN：远程直连 out of scope）
        private const int DefaultPort = 8642;               // Hermes gateway/api_server 默认端口
        private const int HttpTimeoutMinutes = 30;          // 长回合：SendAsync 超时封顶 30min（SSE 头读取极快，不影响流读）

        // 查询：取最近 N 个活跃会话（ended_at IS NULL）。白盒验证修正：
        //   - 去掉 LEFT JOIN messages：messages.active 是 per-message 标志，JOIN 致每个 session 膨胀 N 行，
        //     LIMIT 8 可能只覆盖同一 session 的多条 message，语义错乱。
        //   - session-existence 改用 sessions.message_count > 0（session 有内容）。
        // sessions 表实测列（R2 + 白盒）：last_activity_at(REAL epoch秒) / ended_at(REAL) / end_reason / message_count。
        // 隐私：只取时间戳/计数，绝不 SELECT content/reasoning。
        private const string Sql = @"
            SELECT s.last_activity_at, s.ended_at, s.end_reason, s.message_count
            FROM sessions AS s
            WHERE s.ended_at IS NULL
            ORDER BY s.last_activity_at DESC
            LIMIT @n;";

        private const string SqlRecentEnded = @"
            SELECT s.ended_at, s.end_reason
            FROM sessions AS s
            WHERE s.ended_at IS NOT NULL
            ORDER BY s.ended_at DESC
            LIMIT @n;";

        // messages 表启发式（PLAN Rev6）：
        //   - 作用域：active=1 未结束会话 + 新近消息时间窗（timestamp > @windowStart），排除 compacted/历史行。
        //   - 隐私红线：绝不 SELECT content。tool_calls/reasoning_content 由 SQL 判空（IS NOT NULL AND LENGTH>0）返回 0/1，
        //     应用侧只读布尔列，不触碰值本身、不解析 args JSON。
        //   - has_followup_tool：该 assistant 行之后是否存在 role='tool' 行（tool 是否已响应）。
        //   - 只取最新 N 条 assistant 行（timestamp 倒序）供 Failed/Review/unresolved-tool Run 判定。
        private const string SqlRecentMessages = @"
            SELECT (m.tool_calls IS NOT NULL AND LENGTH(m.tool_calls) > 0) AS has_tool_calls,
                   (m.reasoning_content IS NOT NULL AND LENGTH(m.reasoning_content) > 0) AS has_reasoning,
                   m.finish_reason,
                   EXISTS(
                       SELECT 1
                       FROM messages AS t
                       WHERE t.session_id = m.session_id
                         AND t.role = 'tool'
                         AND t.active = 1
                         AND t.timestamp > m.timestamp
                   ) AS has_followup_tool
            FROM messages AS m
            JOIN sessions AS s ON s.id = m.session_id
            WHERE m.active = 1
              AND m.compacted = 0
              AND s.ended_at IS NULL
              AND m.role = 'assistant'
              AND m.timestamp > @windowStart
            ORDER BY m.timestamp DESC
            LIMIT @n;";

        // ===== 依赖 =====
        private readonly EventBus _eventBus;
        private readonly string _connStr;
        private readonly string _profile;   // Hermes profile 名；空 = 默认 profile（决定 state.db 路径 + 状态文案 + /p/{profile} base path）
        private readonly int _queryLimit = 8;

        // ===== api_server 依赖 =====
        private readonly string _apiKey;                        // API_SERVER_KEY（Bearer）；空 = 未配置 api_server，走 state.db 兜底
        private readonly int _port = DefaultPort;               // HermesPort（0 = 自动发现默认 8642）
        private readonly string _basePath;                      // profile 非空 → "/p/{profile}"；空 → ""
        private readonly int _stalenessSeconds = 30;            // Aggregator staleness（心跳抑制用）
        private readonly int _jumpDisplaySeconds = 3;           // Jump 一次性庆祝时长 → 衰减 Wave
        private string? _sessionId;                             // 已建会话 id（建一次；事件 effective_session_id 轮转更新）

        // 共享 HttpClient（连接池复用；Auth 按请求设置，因 key per profile）
        private static readonly HttpClient s_http = CreateHttpClient();

        // ===== SSE 回合状态 =====
        private bool _turnActive;                               // SSE turn 活跃标志（staleness 抑制 + IsBusy）
        private DateTime _lastPublishUtc = DateTime.MinValue;   // 回合内最近一次发布/增量时刻（心跳判定静默）
        private AgentState _lastTurnState = AgentState.Idle;    // 回合内最近发布态（心跳重复它防翻转）
        private bool _sawDone;                                  // 收到 done 事件
        private readonly List<string> _completedParts = new();  // 各 assistant.completed 的终稿（按序）
        private readonly Dictionary<string, string> _deltaBuffers = new(StringComparer.Ordinal); // message_id → delta 累积
        private string _fallbackDelta = string.Empty;           // 无 message_id 时的 delta 兜底累积
        private DispatcherTimer? _stalenessHeartbeat;           // 长回合静默心跳（刷新 Aggregator staleness）
        private DispatcherTimer? _waveDecayTimer;               // Jump → Wave 衰减定时器

        // ===== 轮询状态 =====
        private DispatcherTimer? _timer;
        private bool _running;

        // 上次发布的状态（避免抖动：同状态不重复发）
        private AgentState _lastPublished = AgentState.Idle;

        // 熔断 / 退避
        private int _consecutiveErrors;
        private int _currentBackoffMs = InitialBackoffMs;
        private DateTime _circuitBreakUntil = DateTime.MinValue;

        /// <summary>
        /// 回复中间文本增量（assistant.delta），UI 实时滚动。
        /// 回调在 SSE 读取线程触发（非 UI 线程），订阅方需自行 marshal 到 UI。
        /// </summary>
        public event Action<string>? DeltaReceived;

        /// <summary>
        /// 当前是否忙（SSE turn 未结束），UI 并发发送保护。
        /// turn 开始 true、done/error/finally 结束 false。
        /// </summary>
        public bool IsBusy { get; private set; }

        /// <summary>
        /// 已建 Hermes api_server 会话 id（建一次；事件轮转 effective_session_id 后更新）。空 = 未建。
        /// </summary>
        public string? HermesSessionId => _sessionId;

        /// <summary>
        /// 连接状态回调（每次 PollOnce 成功/失败或 api_server 调用成功/失败触发一次，UI 线程 Post）。
        /// 参数：(状态文案, 是否连通)。文案仅含 profile 名 + 来源 + 时间，绝不携带 content/reasoning/key。
        /// 设置页据此显示「🟢 state.db ✓ / 🟢 api_server ✓」。
        /// </summary>
        public Action<string, bool>? ConnStatusCallback { get; set; }

        /// <summary>
        /// 当前活跃实例（App 生命周期内单例；Start 设 / Stop 清）。
        /// 设置页打开时据此挂 ConnStatusCallback 回显同步状态（App 无法直接拿 SettingsWindow 实例）。
        /// </summary>
        public static HermesAdapter? Current { get; private set; }

        /// <summary>
        /// 用默认 state.db 路径（~/.hermes/state.db，profile 空）构造（state.db 兜底模式）。
        /// </summary>
        public HermesAdapter(EventBus eventBus)
            : this(eventBus, BuildDefaultConnStr(null))
        {
        }

        /// <summary>
        /// 按 AgentIntegrationConfig 构造（api_server SSE 主链路 + state.db 兜底）：
        ///   - HermesProfile → state.db 路径 + /p/{profile} base path（multiplex）。
        ///   - HermesPort → api_server 端口（0=默认 8642）；HermesApiServerKey → Bearer key（空 = 未配置，走兜底）。
        ///   - HermesPollIntervalSeconds / StalenessSeconds / JumpDisplaySeconds。
        /// </summary>
        public HermesAdapter(EventBus eventBus, AgentIntegrationConfig ai)
            : this(eventBus, BuildDefaultConnStr(ai?.HermesProfile))
        {
            _profile = ai?.HermesProfile ?? string.Empty;
            _apiKey = ai?.HermesApiServerKey ?? string.Empty;
            // HermesPort=0（默认）= 自动发现 → 用默认 8642；非零 → 显式端口。
            _port = (ai?.HermesPort ?? 0) == 0 ? DefaultPort : ai!.HermesPort;
            _stalenessSeconds = ai?.StalenessSeconds ?? 30;
            _jumpDisplaySeconds = ai?.JumpDisplaySeconds ?? 3;
            _basePath = string.IsNullOrEmpty(_profile)
                ? string.Empty
                : "/p/" + Uri.EscapeDataString(_profile);

            // 长回合 staleness 抑制：turn 活跃期间静默超过阈值 → 心跳重复最近活性态（刷新 Aggregator staleness）。
            _stalenessHeartbeat = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(_stalenessSeconds / 2.0, 5, 30))
            };
            _stalenessHeartbeat.Tick += OnStalenessHeartbeat;
        }

        /// <summary>
        /// 测试/配置注入用：显式连接串（profile 空，不影响路径解析）。
        /// </summary>
        public HermesAdapter(EventBus eventBus, string connStr)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _connStr = connStr ?? throw new ArgumentNullException(nameof(connStr));
            _profile = string.Empty;
            _apiKey = string.Empty;
            _basePath = string.Empty;
        }

        /// <inheritdoc />
        public AgentId Id => AgentId.Hermes;

        // ============================================================
        // 生命周期
        // ============================================================

        /// <inheritdoc />
        public void Start()
        {
            if (_running) {
                return;
            }
            _running = true;
            Current = this;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
            _timer.Tick += OnTick;
            _timer.Start();

            // 启动立即探一次（不等首个 tick）。
            PollOnce();
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!_running) {
                return;
            }
            _running = false;
            if (Current == this) {
                Current = null;
            }

            // 停 SSE 回合相关定时器 + 复位回合状态。
            _stalenessHeartbeat?.Stop();
            _waveDecayTimer?.Stop();
            _turnActive = false;
            IsBusy = false;

            if (_timer != null) {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }
        }

        // ============================================================
        // 轮询（state.db 兜底；SSE 回合活跃期间不发布，避免与 SSE 状态打架）
        // ============================================================

        private void OnTick(object? sender, EventArgs e)
        {
            PollOnce();
        }

        private void PollOnce()
        {
            if (!_running) {
                return;
            }

            // 熔断中：跳过本次，等暂停窗口结束。
            if (DateTime.Now < _circuitBreakUntil) {
                return;
            }

            try
            {
                var snapshot = QuerySnapshot();
                var mapped = MapState(snapshot);

                // SSE 回合活跃期间：不发布 SQLite 启发式态（SSE 事件是权威源），仅刷新连接状态。
                if (!_turnActive) {
                    // 同状态不重复发（去抖动）。
                    if (mapped.State != _lastPublished) {
                        _lastPublished = mapped.State;
                        PublishState(mapped.State, mapped.Detail);
                    }
                }

                // 成功：重置异常计数与退避。
                _consecutiveErrors = 0;
                _currentBackoffMs = InitialBackoffMs;

                // 连接状态回调（UI 线程，含当前时间）。来源固定为 state.db（兜底健康）。
                RaiseConnStatus(true, "state.db");
            }
            catch (Exception ex)
            {
                HandlePollError(ex);
            }
        }

        // ============================================================
        // state.db 只读查询（兜底，保留不动）
        // ============================================================

        /// <summary>
        /// 查询当前快照。每次 using 新连接，不跨生命周期持连接。
        /// ReadOnly + Pooling=false + busy_timeout=3000 + query_only=1（C11）。
        /// </summary>
        private HermesSnapshot QuerySnapshot()
        {
            using var conn = OpenReadOnly();
            var active = new List<ActiveSessionRow>(8);
            var recentEnded = new List<EndedSessionRow>(4);

            // messages 表启发式（Failed / Review / unresolved-tool Run）。
            var messages = ReadMessageSignals(conn);

            // 活跃会话（ended_at IS NULL）。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = Sql;
                cmd.Parameters.AddWithValue("@n", _queryLimit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime? lastActivity = GetNullableDateTime(reader, 0);
                    DateTime? endedAt = GetNullableDateTime(reader, 1);
                    string? endReason = reader.IsDBNull(2) ? null : reader.GetString(2);
                    bool messagesActive = !reader.IsDBNull(3) && reader.GetInt32(3) != 0;

                    active.Add(new ActiveSessionRow(lastActivity, endedAt, endReason, messagesActive));
                }
            }

            // 最近结束的会话（检测「ended_at 刚被填」→ Wave）。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SqlRecentEnded;
                cmd.Parameters.AddWithValue("@n", _queryLimit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    DateTime? endedAt = GetNullableDateTime(reader, 0);
                    string? endReason = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (endedAt.HasValue) {
                        recentEnded.Add(new EndedSessionRow(endedAt.Value, endReason));
                    }
                }
            }

            return new HermesSnapshot(active, recentEnded, messages);
        }

        /// <summary>
        /// 读取 messages 表启发式信号（Failed / Review / unresolved-tool Run）。
        /// 隐私红线：应用侧只读 0/1 布尔列与 finish_reason 枚举，绝不读取 content；tool_calls/reasoning_content 值不落地。
        /// </summary>
        private MessageSignals ReadMessageSignals(SqliteConnection conn)
        {
            // 消息时间窗 = 与 busy Run 相同的 staleness 阈值（2 * 轮询周期），信号与活跃判定同频衰减。
            double windowStartEpoch = ToEpochSeconds(DateTime.Now.AddMilliseconds(-(PollIntervalMs * StalenessMultiplier)));

            bool hasContentFilter = false;
            bool hasUnresolvedTool = false;
            bool hasCompleted = false;
            int reviewCount = 0;
            bool isFirst = true;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SqlRecentMessages;
                cmd.Parameters.AddWithValue("@windowStart", windowStartEpoch);
                cmd.Parameters.AddWithValue("@n", _queryLimit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // 列序：0=has_tool_calls, 1=has_reasoning, 2=finish_reason, 3=has_followup_tool。
                    bool hasToolCalls = !reader.IsDBNull(0) && reader.GetInt32(0) != 0;
                    bool hasReasoning = !reader.IsDBNull(1) && reader.GetInt32(1) != 0;
                    string? finishReason = reader.IsDBNull(2) ? null : reader.GetString(2);
                    bool hasFollowupTool = !reader.IsDBNull(3) && reader.GetInt32(3) != 0;

                    // Failed：仅 finish_reason='content_filter'（length/incomplete 是续传信号，不当 Failed）。
                    // 诚实标注：DB 实测零 content_filter 行 → 本轮基本不出。
                    if (finishReason == "content_filter") {
                        hasContentFilter = true;
                    }

                    // Review：reasoning_content 非空（纯推理信号）。
                    if (hasReasoning) {
                        reviewCount++;
                    }

                    // Run（unresolved-tool）/ Completed（finish_reason='stop'）：仅最新 assistant 行（timestamp 倒序首行）。
                    // Completed = 最新行已结束输出且无挂起工具（回合完成信号 → Wave「做完了 撒花」），
                    // 不依赖 session ended_at（Hermes 会话常驻，回合级完成只能从 messages 表拿）。
                    if (isFirst) {
                        isFirst = false;
                        if (hasToolCalls && !hasFollowupTool) {
                            hasUnresolvedTool = true;
                        }
                        if (finishReason == "stop" && !hasToolCalls) {
                            hasCompleted = true;
                        }
                    }
                }
            }

            return new MessageSignals(hasContentFilter, hasUnresolvedTool, hasCompleted, reviewCount);
        }

        private SqliteConnection OpenReadOnly()
        {
            // 异常则由调用方做退避/重连（不在此吞）。
            var conn = new SqliteConnection(_connStr);
            conn.Open();

            // query_only=1 双保险（连接串已 ReadOnly）。
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA query_only=1;";
                pragma.ExecuteNonQuery();
            }

            return conn;
        }

        // ============================================================
        // 状态映射（R2/R3 诚实版，保留不动）
        // ============================================================

        internal static (AgentState State, string Detail) MapState(HermesSnapshot snapshot)
        {
            var now = DateTime.Now;
            var freshnessThreshold = TimeSpan.FromMilliseconds(PollIntervalMs * StalenessMultiplier);
            var recentEndedWindow = TimeSpan.FromMilliseconds(PollIntervalMs * RecentEndedWindowMultiplier);

            // 先统计活跃会话数与近期活跃标志（供 Detail 与 busy Run 兜底复用）。
            int activeCount = 0;
            bool foundRecentActive = false;
            for (int i = 0; i < snapshot.Active.Count; i++)
            {
                var row = snapshot.Active[i];
                if (!row.MessagesActive) {
                    continue; // messages.active 仅作 session-existence filter。
                }
                activeCount++;

                // last_activity_at 新鲜度判定（N5：NULL=僵尸会话，过期=Idle 兜底）。
                if (row.LastActivity.HasValue
                    && (now - row.LastActivity.Value) <= freshnessThreshold)
                {
                    foundRecentActive = true;
                }
            }

            // 求值顺序（镜像 Hermes derive_pet_state：tool_running → reasoning → busy）：
            //   Failed → Wave → unresolved-tool Run → reasoning Review → busy Run → Idle。
            // Review 必须在 busy→Run 兜底之前判定，否则活跃 reasoning 会话会被 busy→Run 吞掉、Review 永远不可达。

            // 1) Failed：活跃未结束会话新近窗内 assistant 行 finish_reason='content_filter'。
            //    诚实标注：DB 实测零 content_filter → 本轮基本不出（不虚构失败）。
            if (snapshot.Messages.HasContentFilter) {
                return (AgentState.Failed, "failed: 1");
            }

            // 2) Wave（回合完成）：最新 assistant 行 finish_reason='stop' 且无挂起工具。
            //    不依赖 session ended_at（Hermes 会话常驻，ended_at 永远 NULL → 旧判定永不触发）。
            //    → 桌宠显示「做完了 撒花 🎉」。
            if (snapshot.Messages.HasCompleted) {
                return (AgentState.Wave, "completed: stop");
            }

            // 3) Wave（会话级）：ended_at 在最近窗口内刚被填（cron 等真结束的会话）。
            //    end_reason/上下文无法可靠示 plan 完成（R2 实测仅 cron_complete/ws_orphan_reap）→ 不臆造 Jump，统一 Wave。
            for (int i = 0; i < snapshot.RecentEnded.Count; i++)
            {
                var row = snapshot.RecentEnded[i];
                if (now - row.EndedAt <= recentEndedWindow) {
                    return (AgentState.Wave, BuildDetail(0));
                }
            }

            // 3) Run（unresolved-tool）：最新 assistant 行 tool_calls 非空 + 无后续 role='tool' 行（tool 未完成）。
            if (snapshot.Messages.HasUnresolvedTool) {
                return (AgentState.Run, BuildDetail(activeCount));
            }

            // 4) Review：新近窗内 assistant 行 reasoning_content 非空（纯推理信号）。
            if (snapshot.Messages.ReviewCount > 0) {
                return (AgentState.Review, $"review: {snapshot.Messages.ReviewCount}");
            }

            // 5) busy Run 兜底：存在近期 active 会话。
            if (foundRecentActive) {
                return (AgentState.Run, BuildDetail(activeCount));
            }

            // 6) Idle：无上述信号（全 NULL/过期/无 active 会话）。
            return (AgentState.Idle, BuildDetail(activeCount));
        }

        private static string BuildDetail(int activeSessionCount)
        {
            // 隐私：Detail 只放非内容摘要。
            return $"active sessions: {activeSessionCount}";
        }

        // ============================================================
        // 发布
        // ============================================================

        private void PublishState(AgentState state, string detail)
        {
            _eventBus.PublishOnUIThread(
                EventType.AgentStateChanged,
                new AgentStateEvent(AgentId.Hermes, state, detail, DateTime.Now));
        }

        /// <summary>
        /// SSE 事件发布（更新回合内最近发布时刻/状态，供心跳与 done 判定）。
        /// </summary>
        private void PublishSseState(AgentState state, string detail)
        {
            PublishState(state, detail);
            _lastPublishUtc = DateTime.UtcNow;
            _lastTurnState = state;
        }

        /// <summary>
        /// 触发连接状态回调（UI 线程 Post）。文案只含 profile 名 + 来源 + 时间，绝不携带 content/reasoning/key。
        /// </summary>
        private void RaiseConnStatus(bool ok, string? source = null)
        {
            var callback = ConnStatusCallback;
            if (callback == null) {
                return;
            }

            string kind = source ?? ConnSourceLabel;
            string name = string.IsNullOrEmpty(_profile) ? kind : $"{_profile} {kind}";
            string text = ok ? $"{name} ✓ {DateTime.Now:HH:mm:ss}" : $"{name} ✗";

            Dispatcher.UIThread.Post(() => callback(text, ok));
        }

        /// <summary>
        /// 状态来源标签：配置了 api_server key → api_server（主链路），否则 state.db（兜底）。
        /// </summary>
        private string ConnSourceLabel => string.IsNullOrEmpty(_apiKey) ? "state.db" : "api_server";

        // ============================================================
        // 错误处理：backoff + circuit-breaker
        // ============================================================

        private void HandlePollError(Exception ex)
        {
            // 隐私：绝不 log 异常细节里的 content；只记计数。这里 Console 不打 ex.Message（可能含路径/敏感）。
            _consecutiveErrors++;

            // 连接状态回调：失败（UI 线程）。
            RaiseConnStatus(false, "state.db");

            if (_consecutiveErrors >= ConsecutiveErrorThreshold)
            {
                // 熔断：暂停一段后自动恢复探测。
                _circuitBreakUntil = DateTime.Now.AddMilliseconds(CircuitBreakPauseMs);
                _currentBackoffMs = InitialBackoffMs;
                return;
            }

            // 指数退避（封顶），通过推迟下一次 tick 等效「等待」。
            // DispatcherTimer 固定周期，此处仅更新退避值；如需真正推迟，可临时调 Interval。
            int backoff = _currentBackoffMs;
            _currentBackoffMs = Math.Min(_currentBackoffMs * 2, MaxBackoffMs);

            if (_timer != null) {
                _timer.Interval = TimeSpan.FromMilliseconds(backoff);
                // 下次 tick 后恢复标准周期。
                Dispatcher.UIThread.Post(() =>
                {
                    if (_timer != null && _running) {
                        _timer.Interval = TimeSpan.FromMilliseconds(PollIntervalMs);
                    }
                });
            }
        }

        // ============================================================
        // api_server SSE 客户端（主链路，PLAN Rev3 §2）
        // ============================================================

        /// <summary>
        /// 发起一轮对话（POST chat/stream），SSE 事件驱动动画，返回终稿回复文本。
        /// 返回为累积 delta + assistant.completed 终稿替换后的文本（多消息以换行拼接）。
        /// 取消为尽力而为：CancellationToken 中止本机 HTTP 请求；Hermes 端 turn 可能继续跑（不用 /v1/runs/stop，404 假路径）。
        /// api_server 不可达/未配置 → 回退现有 state.db 轮询（Start 已在跑）并返回诚实提示文本。
        /// </summary>
        /// <param name="message">用户输入消息。</param>
        /// <param name="ct">取消令牌（中止 HTTP 请求）。</param>
        /// <returns>终稿回复文本（可能为空字符串；失败时含错误提示）。</returns>
        /// <exception cref="InvalidOperationException">已有 turn 在进行（UI 应经 IsBusy 防并发）。</exception>
        public async Task<string> SendMessageAsync(string message, CancellationToken ct = default)
        {
            if (_turnActive) {
                throw new InvalidOperationException("Hermes 回合已在进行（IsBusy 防并发发送）");
            }

            _turnActive = true;
            IsBusy = true;
            _sawDone = false;
            _completedParts.Clear();
            _deltaBuffers.Clear();
            _fallbackDelta = string.Empty;
            _lastTurnState = AgentState.Idle;
            _stalenessHeartbeat?.Start();

            try
            {
                PublishSseState(AgentState.Run, "turn started");

                // 未配置 api_server → 走兜底：诚实返回提示（state.db 轮询已在反映状态）。
                if (string.IsNullOrEmpty(_apiKey)) {
                    PublishSseState(AgentState.Failed, "api_server 未配置");
                    RaiseConnStatus(false, "api_server");
                    return "[Hermes api_server 未配置（HermesApiServerKey 为空），无可用回复]";
                }

                // 建 session（建一次；已存在/409 → 复用）。
                var sessionId = await EnsureSessionAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sessionId)) {
                    throw new InvalidOperationException("Hermes 会话创建失败");
                }

                var reply = await StreamChatAsync(sessionId, message, ct).ConfigureAwait(false);
                return reply;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户取消：尽力而为（Hermes 端 turn 可能继续跑）。诚实降回 Idle。
                PublishSseState(AgentState.Idle, "cancelled");
                throw;
            }
            catch (Exception ex)
            {
                // api_server 不可达 / HTTP 错误：回退现有 SQLite 轮询（Start 已运行），并诚实标注。
                PublishSseState(AgentState.Failed, "api_server 不可达");
                RaiseConnStatus(false, "api_server");
                var partial = GetReplyText();
                if (!string.IsNullOrEmpty(partial)) {
                    return partial;
                }
                return $"[Hermes api_server 不可达，已回退本地状态轮询：{SafeExcerpt(ex)}]";
            }
            finally
            {
                // finally 必清：done/error/abort/timeout 任何路径都释放回合态 + 心跳（防宠物钉死在 Run/Review）。
                _turnActive = false;
                IsBusy = false;
                _lastPublishUtc = DateTime.MinValue;
                _stalenessHeartbeat?.Stop();
            }
        }

        /// <summary>
        /// 建会话：POST /api/sessions（Bearer key）。建一次持久化 _sessionId；已存在 id → 409 复用（连续上下文）。
        /// 请求 id 按 profile+machine 确定性生成，跨重启复用同一 session。
        /// </summary>
        private async Task<string> EnsureSessionAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_sessionId)) {
                return _sessionId;
            }

            var requested = BuildRequestedSessionId();
            var url = BuildUrl("/api/sessions");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent("{\"id\":\"" + JsonEscape(requested) + "\"}", Encoding.UTF8, "application/json");

            using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Conflict) {
                // 409：session 已存在（上次运行创建）→ 复用 requested id。
                _sessionId = requested;
                return _sessionId;
            }

            if (!resp.IsSuccessStatusCode) {
                throw new HttpRequestException($"session create HTTP {(int)resp.StatusCode}");
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var id = ParseSessionIdFromBody(body);
            _sessionId = string.IsNullOrEmpty(id) ? requested : id;
            return _sessionId;
        }

        /// <summary>
        /// POST chat/stream + SSE 逐行解析（event: + data: 两字段）。
        /// </summary>
        private async Task<string> StreamChatAsync(string sessionId, string message, CancellationToken ct)
        {
            var url = BuildUrl($"/api/sessions/{Uri.EscapeDataString(sessionId)}/chat/stream");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { message }),
                Encoding.UTF8,
                "application/json");

            using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                throw new HttpRequestException($"chat/stream HTTP {(int)resp.StatusCode}");
            }

            RaiseConnStatus(true, "api_server");

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? currentEvent = null;
            var dataLines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrEmpty(line)) {
                    // 空行 = 帧结束：event: 已收、data: 已收 → 派发。
                    if (dataLines.Count > 0) {
                        HandleSseFrame(currentEvent, string.Join("\n", dataLines));
                        dataLines.Clear();
                        currentEvent = null;
                        if (_sawDone) {
                            break;
                        }
                    }
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal)) {
                    currentEvent = line.Substring("event:".Length).Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal)) {
                    dataLines.Add(line.Substring("data:".Length).TrimStart());
                }
                // 其它行（注释/空 SSE 行）忽略。
            }

            // 流提前结束（未收到 done）：诚实降回 Idle（Failed 保留，交由 staleness 衰减）。
            if (!_sawDone && _lastTurnState != AgentState.Failed) {
                PublishSseState(AgentState.Idle, "stream ended");
            }

            return GetReplyText();
        }

        /// <summary>
        /// 单帧派发：事件名（event:）+ payload（data:）→ AgentState + 回复文本累积。
        /// </summary>
        private void HandleSseFrame(string? eventName, string data)
        {
            string? evt = eventName;
            using var doc = ParseJson(data);
            var root = doc == null ? default : doc.RootElement;

            // event: 缺失时（服务端只发 data: 且带 type 字段）从 payload 推断事件类型。
            if (string.IsNullOrEmpty(evt) && doc != null
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String)
            {
                evt = typeProp.GetString();
            }

            switch (evt)
            {
                case "run.started":
                case "tool.started":
                    // 首个 busy 信号（POST 接受与 message.started 之间不落 Idle）。
                    PublishSseState(AgentState.Run, evt);
                    break;

                case "message.started":
                    {
                        var mid = GetString(root, "message_id");
                        if (string.IsNullOrEmpty(mid)) {
                            mid = GetString(root, "id");
                        }
                        if (!string.IsNullOrEmpty(mid)) {
                            _deltaBuffers[mid] = string.Empty; // message.started 重置该消息 delta 缓冲
                        }
                        PublishSseState(AgentState.Run, evt);
                        break;
                    }

                case "tool.progress":
                    // 仅 _thinking（推理）→ Review。
                    if (GetString(root, "tool_name") == "_thinking") {
                        PublishSseState(AgentState.Review, "thinking");
                    }
                    break;

                case "tool.failed":
                case "error":
                    PublishSseState(AgentState.Failed, evt);
                    break;

                case "assistant.delta":
                    {
                        var dMid = GetString(root, "message_id");
                        var delta = ExtractText(root);
                        if (!string.IsNullOrEmpty(delta)) {
                            if (!string.IsNullOrEmpty(dMid)) {
                                _deltaBuffers[dMid] = (_deltaBuffers.TryGetValue(dMid, out var cur) ? cur : string.Empty) + delta;
                            } else {
                                _fallbackDelta += delta;
                            }
                            // 增量实时触发 UI 滚动（回调在 SSE 读取线程）。
                            DeltaReceived?.Invoke(delta);
                            _lastPublishUtc = DateTime.UtcNow; // delta 也算回合内活跃
                        }
                        break;
                    }

                case "assistant.completed":
                    {
                        var final = ExtractText(root);
                        if (!string.IsNullOrEmpty(final)) {
                            _completedParts.Add(final); // 终稿替换 delta（防重复）
                            var cMid = GetString(root, "message_id");
                            if (!string.IsNullOrEmpty(cMid)) {
                                _deltaBuffers.Remove(cMid);
                            }
                        }
                        PersistEffectiveSession(root);
                        // 低优先级 Wave（回合内可能被后续 Run/Review 覆盖，无害）。
                        PublishSseState(AgentState.Wave, "assistant completed");
                        break;
                    }

                case "run.completed":
                    {
                        if (HasError(root)) {
                            PublishSseState(AgentState.Failed, "run error");
                        } else {
                            // 终稿兜底：若尚未收到 assistant.completed 终稿，则不再臆造（delta 已累积在缓冲）。
                            PersistEffectiveSession(root);
                            // 本地合成 Jump（跳一下庆祝），再衰减 Wave。
                            PublishSseState(AgentState.Jump, "run completed");
                            ScheduleWaveDecay();
                        }
                        break;
                    }

                case "done":
                    _sawDone = true;
                    PublishSseState(AgentState.Idle, "done");
                    break;

                default:
                    // 其它事件（tool.completed 等）不映射状态。
                    break;
            }
        }

        /// <summary>
        /// 长回合 staleness 抑制：SSE turn 活跃期间静默超过半阈值 → 心跳重复最近活性态，刷新 Aggregator staleness。
        /// finally 会停此心跳（error/abort/timeout 也停）。
        /// </summary>
        private void OnStalenessHeartbeat(object? sender, EventArgs e)
        {
            if (!_turnActive) {
                _stalenessHeartbeat?.Stop();
                return;
            }

            var silence = Math.Max(5.0, _stalenessSeconds / 2.0);
            if ((DateTime.UtcNow - _lastPublishUtc).TotalSeconds >= silence)
            {
                // 重复最近活性态（Run/Review/Failed），避免翻转；其余态用 Run 兜底。
                var s = _lastTurnState;
                if (s != AgentState.Failed && s != AgentState.Run && s != AgentState.Review) {
                    s = AgentState.Run;
                }
                PublishSseState(s, "keepalive");
            }
        }

        /// <summary>
        /// Jump → Wave 衰减（本地定时器兜底；Aggregator 自身 JumpDisplaySeconds 到期也会降 Wave，二者幂等）。
        /// done 之后 _turnActive=false，衰减不再覆盖 Idle。
        /// </summary>
        private void ScheduleWaveDecay()
        {
            if (_waveDecayTimer == null) {
                _waveDecayTimer = new DispatcherTimer();
                _waveDecayTimer.Tick += OnWaveDecayTick;
            } else {
                _waveDecayTimer.Stop();
            }
            _waveDecayTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _jumpDisplaySeconds));
            _waveDecayTimer.Start();
        }

        private void OnWaveDecayTick(object? sender, EventArgs e)
        {
            _waveDecayTimer?.Stop();
            if (!_turnActive) {
                return; // done 已发布 Idle，不覆盖
            }
            PublishSseState(AgentState.Wave, "jump decay");
        }

        /// <summary>
        /// 终稿文本：已完成消息的 completed 终稿按序拼接 + 尾部未完成消息的 delta 累积。
        /// </summary>
        private string GetReplyText()
        {
            var parts = new List<string>();
            foreach (var p in _completedParts) {
                if (!string.IsNullOrEmpty(p)) {
                    parts.Add(p);
                }
            }
            foreach (var kv in _deltaBuffers) {
                if (!string.IsNullOrEmpty(kv.Value)) {
                    parts.Add(kv.Value);
                }
            }
            if (!string.IsNullOrEmpty(_fallbackDelta)) {
                parts.Add(_fallbackDelta);
            }
            return string.Join("\n", parts);
        }

        /// <summary>
        /// 会话轮转：assistant.completed / run.completed 携带 effective_session_id 或 session_id 时持久化，
        /// 避免 resume 到 stale parent 重触发压缩。
        /// </summary>
        private void PersistEffectiveSession(JsonElement root)
        {
            var eff = GetString(root, "effective_session_id");
            if (!string.IsNullOrEmpty(eff)) {
                _sessionId = eff;
                return;
            }
            var sid = GetString(root, "session_id");
            if (!string.IsNullOrEmpty(sid)) {
                _sessionId = sid;
            }
        }

        // ---------- api_server 辅助 ----------

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(HttpTimeoutMinutes) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return client;
        }

        /// <summary>
        /// profile 非空 → base path /p/{profile}（multiplex）。
        /// </summary>
        private string BuildUrl(string path)
        {
            return $"http://{DefaultHost}:{_port}{_basePath}{path}";
        }

        /// <summary>
        /// 确定性会话 id：minipet_{profile|default}_{machine}——跨重启复用同一 session（连续上下文），409/存在即复用。
        /// </summary>
        private static string BuildRequestedSessionId()
        {
            var machine = Sanitize(Environment.MachineName);
            return $"minipet_{machine}";
        }

        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            var s = sb.ToString();
            return string.IsNullOrEmpty(s) ? "default" : s;
        }

        private static string JsonEscape(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        private static string ParseSessionIdFromBody(string body)
        {
            if (string.IsNullOrEmpty(body)) {
                return string.Empty;
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                return GetString(doc.RootElement, "id");
            }
            catch
            {
                return string.Empty;
            }
        }

        private static JsonDocument? ParseJson(string data)
        {
            if (string.IsNullOrEmpty(data)) {
                return null;
            }
            try
            {
                return JsonDocument.Parse(data);
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) {
                return string.Empty;
            }
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) {
                return p.GetString() ?? string.Empty;
            }
            return string.Empty;
        }

        /// <summary>
        /// 提取回复文本（assistant.delta 增量 / assistant.completed 终稿共用）：
        /// 依次取 content / text / delta 顶层字符串，回退嵌套对象/数组块，防御结构多形态（实现时确认）。
        /// </summary>
        private static string ExtractText(JsonElement root)
        {
            foreach (var key in new[] { "content", "text", "delta" })
            {
                var s = GetString(root, key);
                if (!string.IsNullOrEmpty(s)) {
                    return s;
                }
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // 嵌套对象：content / delta / message 子对象。
                foreach (var key in new[] { "content", "delta", "message" })
                {
                    if (root.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.Object) {
                        var s = ExtractText(child);
                        if (!string.IsNullOrEmpty(s)) {
                            return s;
                        }
                    }
                }

                // content 数组：拼接字符串块 / {text} 块。
                if (root.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var block in arr.EnumerateArray())
                    {
                        if (block.ValueKind == JsonValueKind.String) {
                            sb.Append(block.GetString());
                        } else if (block.ValueKind == JsonValueKind.Object) {
                            var t = GetString(block, "text");
                            if (!string.IsNullOrEmpty(t)) {
                                sb.Append(t);
                            }
                        }
                    }
                    return sb.ToString();
                }
            }

            return string.Empty;
        }

        private static bool HasError(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) {
                return false;
            }
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.False) {
                    return false;
                }
                if (e.ValueKind == JsonValueKind.String) {
                    return !string.IsNullOrEmpty(e.GetString());
                }
                return true; // 对象/数组/true
            }
            return false;
        }

        /// <summary>
        /// 异常摘录（隐私：不打完整 ex.Message，可能含路径/敏感；取类型名）。
        /// </summary>
        private static string SafeExcerpt(Exception ex)
        {
            return ex?.GetType().Name ?? "unknown";
        }

        // ============================================================
        // 辅助
        // ============================================================

        internal static string BuildDefaultConnStr(string? profile)
        {
            // 根：HERMES_HOME 环境变量优先，否则默认 ~/.hermes（Codex R3 F4：现有代码未处理环境变量，此处补齐）。
            string homeEnv = Environment.GetEnvironmentVariable("HERMES_HOME");
            string root = string.IsNullOrEmpty(homeEnv)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes")
                : homeEnv;

            // profile 非空 → <根>/profiles/<profile>/state.db；否则 <根>/state.db。
            string dbPath = string.IsNullOrEmpty(profile)
                ? Path.Combine(root, "state.db")
                : Path.Combine(root, "profiles", profile, "state.db");

            return "Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=false";
        }

        private static double ToEpochSeconds(DateTime dt)
        {
            return (dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static DateTime? GetNullableDateTime(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) {
                return null;
            }
            // Hermes state.db 时间戳格式未在契约中固化（可能是 ISO8601 字符串或 epoch 秒/毫秒）。
            // 优先按字符串解析，失败回退整数 epoch（秒/毫秒），全失败返回 null（按 Idle 兜底）。
            var raw = reader.GetValue(ordinal);
            return ParseDateTime(raw);
        }

        private static DateTime? ParseDateTime(object raw)
        {
            if (raw == null) {
                return null;
            }

            switch (raw)
            {
                case DateTime dt:
                    return dt;
                case string s:
                    if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)) {
                        return parsed.ToLocalTime();
                    }
                    return null;
                case double d:
                    // 白盒验证：Hermes state.db 时间戳 = REAL epoch 秒（浮点，实测 started_at=1786324006.48）。
                    // 秒/毫秒启发式同 long：> 9999-12-31 秒边界(253402300799) 视为毫秒。
                    if (d > 253402300799.0) {
                        return DateTimeOffset.FromUnixTimeMilliseconds((long)d).LocalDateTime;
                    }
                    return DateTimeOffset.FromUnixTimeSeconds((long)d).LocalDateTime;
                case long l:
                    // epoch 秒 / 毫秒 启发式：10 位(秒) / 13 位(毫秒)。
                    if (l > 253402300799L) { // 9999-12-31 秒边界
                        return DateTimeOffset.FromUnixTimeMilliseconds(l).LocalDateTime;
                    }
                    return DateTimeOffset.FromUnixTimeSeconds(l).LocalDateTime;
                case int i:
                    return DateTimeOffset.FromUnixTimeSeconds(i).LocalDateTime;
                default:
                    return null;
            }
        }

        // ============================================================
        // 内部数据结构
        // ============================================================

        internal readonly struct ActiveSessionRow
        {
            public DateTime? LastActivity { get; }
            public DateTime? EndedAt { get; }
            public string? EndReason { get; }
            public bool MessagesActive { get; }

            public ActiveSessionRow(DateTime? lastActivity, DateTime? endedAt, string? endReason, bool messagesActive)
            {
                LastActivity = lastActivity;
                EndedAt = endedAt;
                EndReason = endReason;
                MessagesActive = messagesActive;
            }
        }

        internal readonly struct EndedSessionRow
        {
            public DateTime EndedAt { get; }
            public string? EndReason { get; }

            public EndedSessionRow(DateTime endedAt, string? endReason)
            {
                EndedAt = endedAt;
                EndReason = endReason;
            }
        }

        internal readonly struct MessageSignals
        {
            public bool HasContentFilter { get; }
            public bool HasUnresolvedTool { get; }
            public bool HasCompleted { get; }
            public int ReviewCount { get; }

            public MessageSignals(bool hasContentFilter, bool hasUnresolvedTool, bool hasCompleted, int reviewCount)
            {
                HasContentFilter = hasContentFilter;
                HasUnresolvedTool = hasUnresolvedTool;
                HasCompleted = hasCompleted;
                ReviewCount = reviewCount;
            }
        }

        internal readonly struct HermesSnapshot
        {
            public IReadOnlyList<ActiveSessionRow> Active { get; }
            public IReadOnlyList<EndedSessionRow> RecentEnded { get; }
            public MessageSignals Messages { get; }

            public HermesSnapshot(
                IReadOnlyList<ActiveSessionRow> active,
                IReadOnlyList<EndedSessionRow> recentEnded,
                MessageSignals messages)
            {
                Active = active;
                RecentEnded = recentEnded;
                Messages = messages;
            }
        }
    }
}
