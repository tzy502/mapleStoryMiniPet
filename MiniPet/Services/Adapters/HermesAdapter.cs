// TODO: csproj 需加 <PackageReference Include="Microsoft.Data.Sqlite"/>（Config 模块统一加）
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;

namespace MiniPet.Services.Adapters
{
    /// <summary>
    /// Hermes agent 状态适配器（阶段1）。
    ///
    /// 数据源：~/.hermes/state.db 只读 SQLite 轮询（唯一安全源）。
    ///   - 严禁 hermes status（仅环境/API-key 健康，零 activity 字段 — N1）。
    ///   - 严禁 hermes sessions list（CLI 以写模式开库、争用 gateway 写锁、实测报错 — N4）。
    ///
    /// 状态映射（R2/R3 实测诚实版）：
    ///   - 存在 last_activity_at 近期(<=2*轮询周期) 且 active → Run。
    ///   - last_activity_at 为 NULL 或过期 → Idle（僵尸会话，N5）。
    ///   - ended_at 在最近轮询窗口内刚被填 → Wave（无法判断 plan 完成则 Wave，不臆造 Jump）。
    ///   - Waiting/Failed/Review：state.db 无可靠信号（handoff_state 全空 — N2）→ 不发，交 Aggregator staleness 降 Idle。
    ///
    /// 隐私：绝不 log/persist content/reasoning/密钥。Detail 只放非内容摘要（如「active sessions: N」）。
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

        // ===== 依赖 =====
        private readonly EventBus _eventBus;
        private readonly string _connStr;
        private readonly int _queryLimit = 8;

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
        /// 用默认 state.db 路径（~/.hermes/state.db）构造。
        /// </summary>
        public HermesAdapter(EventBus eventBus)
            : this(eventBus, BuildDefaultConnStr())
        {
        }

        /// <summary>
        /// 测试/配置注入用：显式连接串。
        /// </summary>
        public HermesAdapter(EventBus eventBus, string connStr)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _connStr = connStr ?? throw new ArgumentNullException(nameof(connStr));
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

            if (_timer != null) {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }
        }

        // ============================================================
        // 轮询
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

                // 同状态不重复发（去抖动）。
                if (mapped.State != _lastPublished) {
                    _lastPublished = mapped.State;
                    PublishState(mapped.State, mapped.Detail);
                }

                // 成功：重置异常计数与退避。
                _consecutiveErrors = 0;
                _currentBackoffMs = InitialBackoffMs;
            }
            catch (Exception ex)
            {
                HandlePollError(ex);
            }
        }

        // ============================================================
        // state.db 只读查询
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

            return new HermesSnapshot(active, recentEnded);
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
        // 状态映射（R2/R3 诚实版）
        // ============================================================

        private (AgentState State, string Detail) MapState(HermesSnapshot snapshot)
        {
            var now = DateTime.Now;
            var freshnessThreshold = TimeSpan.FromMilliseconds(PollIntervalMs * StalenessMultiplier);
            var recentEndedWindow = TimeSpan.FromMilliseconds(PollIntervalMs * RecentEndedWindowMultiplier);

            // 1) ended_at 在最近窗口内刚被填 → Wave。
            //    end_reason/上下文无法可靠示 plan 完成（R2 实测仅 cron_complete/ws_orphan_reap）→ 不臆造 Jump，统一 Wave。
            //    （未来 messages 表启发式或 plan 完成标记落地后，再考虑 Jump。）
            for (int i = 0; i < snapshot.RecentEnded.Count; i++)
            {
                var row = snapshot.RecentEnded[i];
                if (now - row.EndedAt <= recentEndedWindow) {
                    return (AgentState.Wave, BuildDetail(0));
                }
            }

            // 2) 存在近期 active 会话 → Run。
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

            if (foundRecentActive) {
                return (AgentState.Run, BuildDetail(activeCount));
            }

            // 3) 无近期活动（全 NULL/过期/无 active 会话）→ Idle。
            //    Waiting/Failed/Review 不发（state.db 无信号，N2），交 Aggregator staleness 降 Idle。
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

        // ============================================================
        // 错误处理：backoff + circuit-breaker
        // ============================================================

        private void HandlePollError(Exception ex)
        {
            // 隐私：绝不 log 异常细节里的 content；只记计数。这里 Console 不打 ex.Message（可能含路径/敏感）。
            _consecutiveErrors++;

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
        // 辅助
        // ============================================================

        private static string BuildDefaultConnStr()
        {
            string dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".hermes",
                "state.db");
            return "Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=false";
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

        private readonly struct ActiveSessionRow
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

        private readonly struct EndedSessionRow
        {
            public DateTime EndedAt { get; }
            public string? EndReason { get; }

            public EndedSessionRow(DateTime endedAt, string? endReason)
            {
                EndedAt = endedAt;
                EndReason = endReason;
            }
        }

        private readonly struct HermesSnapshot
        {
            public IReadOnlyList<ActiveSessionRow> Active { get; }
            public IReadOnlyList<EndedSessionRow> RecentEnded { get; }

            public HermesSnapshot(IReadOnlyList<ActiveSessionRow> active, IReadOnlyList<EndedSessionRow> recentEnded)
            {
                Active = active;
                RecentEnded = recentEnded;
            }
        }
    }
}
