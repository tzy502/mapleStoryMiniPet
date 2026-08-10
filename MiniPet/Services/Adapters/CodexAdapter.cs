using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;

namespace MiniPet.Services.Adapters
{
    /// <summary>
    /// Codex CLI agent 状态适配器（阶段1）。
    ///
    /// 数据源：~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl（每行一个 RolloutLine）。
    ///   - FileSystemWatcher 盯根目录 sessions/，IncludeSubdirectories=true（抓 YYYY/MM/DD 嵌套）。
    ///   - poll 为 source of truth（mtime+size cursor），FSW 仅唤醒；末行无尾换行时 buffer 到下次（C10）。
    ///
    /// 状态映射（C2 精确事件，非静默推断）—— 解析 event_msg.payload.type：
    ///   - task_started            → Run
    ///   - task_complete（无 error）→ Wave（turn 完成；last_agent_message 无法可靠区分 plan 全完成，回退 Wave）
    ///   - task_complete（有 error）→ Failed
    ///   - turn_aborted            → Failed（interrupted）
    ///   - agent_message / agent_reasoning（运行中片段）→ Run
    ///
    /// 隐私：绝不 log/persist content/reasoning。Detail 只放非内容摘要（如「task_started」「task_complete」）。
    /// </summary>
    public sealed class CodexAdapter : IAgentStateAdapter
    {
        // ===== 常量 =====
        private const int PollIntervalMs = 5000;           // poll 周期 5s（FSW 唤醒 + 定时兜底，可配，阶段1 常量化）
        private const int StalenessMultiplier = 2;         // 活跃会话新鲜度 = 2 * 轮询周期（与 HermesAdapter 对齐）
        private const int MaxBackoffMs = 60_000;           // 退避封顶 60s
        private const int InitialBackoffMs = 1_000;        // 初始退避 1s
        private const int ConsecutiveErrorThreshold = 5;   // 连续异常阈值 → 触发熔断暂停
        private const int CircuitBreakPauseMs = 60_000;    // 熔断暂停时长 60s
        private const int ActiveSessionWindowMinutes = 5;  // mtime 滚动窗：5min 内修改的 rollout 视为活跃

        // ===== 依赖 =====
        private readonly EventBus _eventBus;
        private readonly string _sessionsRoot;

        // ===== 轮询 / FSW 状态 =====
        private DispatcherTimer? _timer;
        private FileSystemWatcher? _fsw;
        private bool _running;

        // poll 游标：source of truth（key = 全路径，value = (mtime, size)）。
        private readonly Dictionary<string, FileCursor> _cursors = new();

        // 末行缓冲：无尾换行的行 buffer 到下次读取（key = 全径，value = 未完成尾行）。
        private readonly Dictionary<string, string> _tailBuffers = new();

        // 上次发布的状态（去抖：同状态不重复发）
        private AgentState _lastPublished = AgentState.Idle;

        // 熔断 / 退避
        private int _consecutiveErrors;
        private int _currentBackoffMs = InitialBackoffMs;
        private DateTime _circuitBreakUntil = DateTime.MinValue;

        /// <summary>用默认 sessions 路径（~/.codex/sessions）构造。</summary>
        public CodexAdapter(EventBus eventBus)
            : this(eventBus, BuildDefaultSessionsRoot())
        {
        }

        /// <summary>测试/配置注入用：显式 sessions 根目录。</summary>
        public CodexAdapter(EventBus eventBus, string sessionsRoot)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _sessionsRoot = sessionsRoot ?? throw new ArgumentNullException(nameof(sessionsRoot));
        }

        /// <inheritdoc />
        public AgentId Id => AgentId.Codex;

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

            // FSW 仅唤醒 poll（IncludeSubdirectories=true 抓 YYYY/MM/DD 嵌套）。
            // FSW 失败不影响 poll 兜底（目录不存在/权限等 → 静默降级到纯 poll）。
            TryStartWatcher();

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

            if (_fsw != null) {
                _fsw.Changed -= OnFsChanged;
                _fsw.Created -= OnFsChanged;
                _fsw.Renamed -= OnFsChanged;
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
                _fsw = null;
            }
        }

        // ============================================================
        // FSW（仅唤醒）
        // ============================================================

        private void TryStartWatcher()
        {
            try
            {
                if (!Directory.Exists(_sessionsRoot)) {
                    return; // 纯 poll 兜底。
                }

                _fsw = new FileSystemWatcher(_sessionsRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _fsw.Changed += OnFsChanged;
                _fsw.Created += OnFsChanged;
                _fsw.Renamed += OnFsChanged;
            }
            catch
            {
                // FSW 初始化失败不致命：poll 为 source of truth 兜底。
                _fsw = null;
            }
        }

        private void OnFsChanged(object? sender, FileSystemEventArgs e)
        {
            // FSW 事件可能在 IO 线程触发；直接调度一次 poll（DispatcherTimer 已在 UI 线程，Post 回 UI 线程跑）。
            // FSW 唤醒后不立即读（文件可能正被 Codex 写、读不到完整行）——交给 poll 的 mtime+size cursor 判定。
            Dispatcher.UIThread.Post(() => PollOnce());
        }

        // ============================================================
        // poll（source of truth）
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

            // 熔断中：跳过本次。
            if (DateTime.Now < _circuitBreakUntil) {
                return;
            }

            try
            {
                var events = ScanChangedFiles();
                var mapped = ReduceToState(events);

                // 同状态不重复发（去抖）。
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

        /// <summary>
        /// 扫描所有活跃 rollout（mtime 在窗口内），按 cursor 增量解析新增行。
        /// poll 游标为 source of truth：仅当 mtime/size 变化才读，且从上次 size 偏移续读。
        /// </summary>
        private List<RolloutEvent> ScanChangedFiles()
        {
            var result = new List<RolloutEvent>();

            if (!Directory.Exists(_sessionsRoot)) {
                return result; // 无数据，不发 Failed，交 Aggregator staleness 降 Idle。
            }

            var cutoff = DateTime.Now - TimeSpan.FromMinutes(ActiveSessionWindowMinutes);

            // 枚举 YYYY/MM/DD/**/rollout-*.jsonl。SearchOption.AllDirectories 抓嵌套日期目录。
            var files = Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories);
            foreach (var path in files)
            {
                FileInfo fi;
                try {
                    fi = new FileInfo(path);
                } catch {
                    continue;
                }

                // 跳过窗口外的旧文件（非活跃会话）。
                if (fi.LastWriteTime < cutoff) {
                    continue;
                }

                // cursor 判定：无变化则跳过。
                long prevSize = 0;
                if (_cursors.TryGetValue(path, out var prev)) {
                    prevSize = prev.Size;
                    // mtime/size 未变 → 跳过（content 改变必伴随 size/mtime 改变）。
                    if (fi.Length == prev.Size && fi.LastWriteTime == prev.Mtime) {
                        continue;
                    }
                    // 文件被截断/轮转（size 变小）→ 重置游标从头读。
                    if (fi.Length < prevSize) {
                        prevSize = 0;
                        _tailBuffers.Remove(path);
                    }
                }

                var newEvents = ReadNewLines(path, prevSize, fi.Length);
                result.AddRange(newEvents);

                _cursors[path] = new FileCursor(fi.LastWriteTime, fi.Length);
            }

            return result;
        }

        /// <summary>
        /// 从 offset 续读至文件末尾，逐行解析。
        /// 末行无尾换行时 buffer 到 _tailBuffers，下次再尝试（C10：不解析半行）。
        /// </summary>
        private List<RolloutEvent> ReadNewLines(string path, long offset, long length)
        {
            var events = new List<RolloutEvent>();
            if (length <= offset) {
                return events;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Position = offset;

            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // ReadLine 已消费换行符；若最后一行读出但读到 EOF 时无法确认其后是否有换行，
                // 此时 Position == length → 这行视为「已完成」（有换行或 EOF 终止）。
                // 但若刚读到 EOF 且无换行，下次文件再追加时同一行可能被重复解析。
                // 保守做法：最后一行（读取后正好 EOF）buffer 到下次，确保不重复、不解析半行。
                if (reader.EndOfStream)
                {
                    // 末行 buffer：本次不解析，留待下次 size 增长后再读（届时 ReadLine 会因新内容返回完整行）。
                    _tailBuffers[path] = line;
                    // 回退游标：让下次从这行起始 offset 重读（buffer 内容仅作占位，重读会覆盖）。
                    // 实现简化：保持上次 cursor size 不前进到行尾，下次从同一 offset 重读即可。
                    // 但调用方已用 fi.Length 更新 cursor —— 为避免重复，这里 buffer 已存内容，
                    // 下次 ReadNewLines 续读时会先把新追加字节拼到此行后。
                    // 因此此处不更新 cursor 的 size（由调用方统一更新），仅靠 buffer 保证语义。
                    break;
                }

                // 正常完整行（带换行）→ 先拼上次的 buffer（若有），再解析。
                if (_tailBuffers.TryGetValue(path, out var buffered)) {
                    _tailBuffers.Remove(path);
                    line = buffered + line;
                }

                var ev = TryParseRolloutLine(line);
                if (ev != null) {
                    events.Add(ev.Value);
                }
            }

            return events;
        }

        // ============================================================
        // RolloutLine 解析（System.Text.Json，CaseInsensitive）
        // ============================================================

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// 解析单行 RolloutLine JSON：
        /// { "timestamp": "...", "type": "session_meta"|"event_msg"|..., "payload": { ... } }
        /// 只关心 type == "event_msg"，按 payload.type 映射状态。
        /// 隐私：绝不读取/持久化 payload 中的 content/reasoning/message 文本。
        /// </summary>
        private RolloutEvent? TryParseRolloutLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("type", out var typeEl)) {
                    return null;
                }
                if (typeEl.GetString() != "event_msg") {
                    return null;
                }
                if (!doc.RootElement.TryGetProperty("payload", out var payload)) {
                    return null;
                }
                if (!payload.TryGetProperty("type", out var payloadTypeEl)) {
                    return null;
                }

                string payloadType = payloadTypeEl.GetString() ?? "";
                bool hasError = payload.TryGetProperty("error", out var errEl)
                                && errEl.ValueKind == JsonValueKind.Object;

                // 时间戳（event_msg 顶层 timestamp，ISO8601 Z）。
                DateTime ts = DateTime.Now;
                if (doc.RootElement.TryGetProperty("timestamp", out var tsEl)
                    && tsEl.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(tsEl.GetString(), out var dto)) {
                        ts = dto.LocalDateTime;
                    }
                }

                return new RolloutEvent(payloadType, hasError, ts);
            }
            catch
            {
                // 坏行/半行 → 跳过（poll 游标已保证只读完整行，此处兜底）。
                return null;
            }
        }

        // ============================================================
        // 状态映射
        // ============================================================

        /// <summary>
        /// 取本批事件的「最强」状态（按活动级 Failed ≈ Run > ... 的近似）。
        /// Codex 每个事件即精确状态，无需静默推断（C2）。
        /// 同批多事件按优先级取一个发布。
        /// </summary>
        private (AgentState State, string Detail) ReduceToState(IReadOnlyList<RolloutEvent> events)
        {
            if (events.Count == 0) {
                // 无新事件：不发 Idle（交 Aggregator staleness 统一降 Idle，避免 adapter 抖动）。
                return (_lastPublished, "");
            }

            AgentState best = AgentState.Idle;
            string bestDetail = "";

            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                var (s, detail) = MapEvent(ev);
                if (Rank(s) > Rank(best)) {
                    best = s;
                    bestDetail = detail;
                }
            }

            return (best, bestDetail);
        }

        private static (AgentState State, string Detail) MapEvent(RolloutEvent ev)
        {
            // 隐私：Detail 只放 payload.type（非内容）。
            switch (ev.PayloadType)
            {
                case "task_started":
                    return (AgentState.Run, "task_started");
                case "task_complete":
                    // 有 error → Failed；否则 turn 完成 → Wave。
                    // last_agent_message 无法可靠区分 plan 全完成（R3 实测仅一句话回复也带 last_agent_message），
                    // 故不臆造 Jump，统一 Wave。
                    return ev.HasError
                        ? (AgentState.Failed, "task_complete (error)")
                        : (AgentState.Wave, "task_complete");
                case "turn_aborted":
                    return (AgentState.Failed, "turn_aborted");
                case "agent_message":
                case "agent_reasoning":
                    // 运行中片段 → 视为活跃 Run（精确 task_started 通常紧随，这里兜底）。
                    return (AgentState.Run, ev.PayloadType);
                default:
                    // token_count / user_message / web_search_* / thread_settings_* 等：不影响状态。
                    return (AgentState.Idle, "");
            }
        }

        /// <summary>活动级排序（数值大者优先）。Failed 与 Run 同级 Failed 胜。</summary>
        private static int Rank(AgentState s) => s switch
        {
            AgentState.Failed => 6,
            AgentState.Run => 5,
            AgentState.Review => 4,
            AgentState.Waiting => 3,
            AgentState.Wave => 2,
            AgentState.Jump => 1,
            _ => 0,
        };

        // ============================================================
        // 发布
        // ============================================================

        private void PublishState(AgentState state, string detail)
        {
            _eventBus.PublishOnUIThread(
                EventType.AgentStateChanged,
                new AgentStateEvent(AgentId.Codex, state, detail, DateTime.Now));
        }

        // ============================================================
        // 错误处理：backoff + circuit-breaker
        // ============================================================

        private void HandlePollError(Exception ex)
        {
            // 隐私：只记计数，不打 ex.Message（可能含路径/敏感）。
            _consecutiveErrors++;

            if (_consecutiveErrors >= ConsecutiveErrorThreshold)
            {
                _circuitBreakUntil = DateTime.Now.AddMilliseconds(CircuitBreakPauseMs);
                _currentBackoffMs = InitialBackoffMs;
                return;
            }

            int backoff = _currentBackoffMs;
            _currentBackoffMs = Math.Min(_currentBackoffMs * 2, MaxBackoffMs);

            if (_timer != null) {
                _timer.Interval = TimeSpan.FromMilliseconds(backoff);
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

        private static string BuildDefaultSessionsRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "sessions");
        }

        // ============================================================
        // 内部数据结构
        // ============================================================

        private readonly struct FileCursor
        {
            public DateTime Mtime { get; }
            public long Size { get; }

            public FileCursor(DateTime mtime, long size)
            {
                Mtime = mtime;
                Size = size;
            }
        }

        private readonly struct RolloutEvent
        {
            public string PayloadType { get; }
            public bool HasError { get; }
            public DateTime Ts { get; }

            public RolloutEvent(string payloadType, bool hasError, DateTime ts)
            {
                PayloadType = payloadType;
                HasError = hasError;
                Ts = ts;
            }
        }
    }
}
