using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using MiniPet.Services;

namespace MiniPet.Services.Adapters
{
    /// <summary>
    /// Claude Code agent 状态适配器。
    ///
    /// 数据源：~/.claude/projects/&lt;编码路径&gt;/&lt;sid&gt;.jsonl（嵌套 &lt;sid&gt;/subagents/agent-*.jsonl）。
    /// 监听策略：FileSystemWatcher 仅作唤醒源，poll 为 source of truth——按 mtime 滚动窗(5min)
    /// 判定活跃 session，用 (size cursor) 读取自上次游标之后的新字节，按 '\n' 切行；
    /// 末行若无 '\n' 缓冲到下次解析。
    ///
    /// 状态映射（PLAN §5）：
    ///   末尾 tool_use 未配 tool_result → Run
    ///   思考/读取(read_file 等) → Review
    ///   is_error → Failed
    ///   末尾 assistant 文本 + 静默 20s → Wave（Jump 需额外检测 todo 全完成标记，无则只 Wave）
    ///   末尾停在 user/awaiting → Waiting
    ///
    /// 隐私：只发 AgentState + 非内容 Detail，绝不 log/persist content/reasoning/密钥。
    /// </summary>
    public sealed class ClaudeCodeAdapter : IAgentStateAdapter
    {
        private static readonly AgentId s_id = AgentId.ClaudeCode;

        // 根目录：~/.claude/projects
        private static readonly string s_rootDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        // 滚动窗：5min 内 mtime 判活跃
        private static readonly TimeSpan s_activeWindow = TimeSpan.FromMinutes(5);

        // 静默 debounce：默认 20s 无新 append 即视为完成态
        private static readonly TimeSpan s_silenceDebounce = TimeSpan.FromSeconds(20);

        // 轮询周期：2s（FSW 唤醒后也走同一 poll）
        private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);

        private readonly EventBus _eventBus;
        private readonly DispatcherTimer _pollTimer;
        private FileSystemWatcher? _watcher;

        // 文件游标：路径 → (lastSize, lineBuffer)
        // lineBuffer：末行无 '\n' 时暂存，下次拼到新数据前再切行。
        private readonly Dictionary<string, FileCursor> _cursors = new(StringComparer.Ordinal);

        // 每 session 的状态机：(debounceTimer, lastPublishedState, currentDerivedState)
        private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

        private readonly object _lock = new();
        private bool _started;
        private bool _disposed;

        public AgentId Id => s_id;

        public ClaudeCodeAdapter(EventBus eventBus)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = s_pollInterval
            };
            _pollTimer.Tick += OnPollTick;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started) { return; }
                _started = true;
            }

            // 即使根目录不存在也不抛——用户可能尚未安装 Claude Code。
            if (!Directory.Exists(s_rootDir))
            {
                // 仅启 poll，目录出现后自愈。
                _pollTimer.Start();
                return;
            }

            TryStartWatcher();
            _pollTimer.Start();
        }

        public void Stop()
        {
            lock (_lock)
            {
                _started = false;
            }

            _pollTimer.Stop();

            if (_watcher is not null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnWatcherChanged;
                    _watcher.Created -= OnWatcherChanged;
                    _watcher.Renamed -= OnWatcherChanged;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ClaudeCodeAdapter] Stop watcher: {ex.Message}");
                }
                _watcher = null;
            }

            // 停所有静默 timer
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    try { kv.Value.DebounceTimer?.Stop(); } catch { }
                }
            }
        }

        // ---------- FSW 唤醒 ----------

        private void TryStartWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(s_rootDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    Filter = "*.jsonl"
                };
                _watcher.Changed += OnWatcherChanged;
                _watcher.Created += OnWatcherChanged;
                _watcher.Renamed += OnWatcherChanged;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // FSW 起不来不致命：poll 是 source of truth。
                Console.Error.WriteLine($"[ClaudeCodeAdapter] FileSystemWatcher init: {ex.Message}");
                _watcher = null;
            }
        }

        private void OnWatcherChanged(object sender, FileSystemEventArgs e)
        {
            // FSW 仅唤醒——立即跑一次 poll（在 UI 线程）。
            // 不在此直接读文件，避免与 poll 竞态。
            Dispatcher.UIThread.Post(DoPoll);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // FSW 内部缓冲区溢出或失效：重建。poll 仍兜底。
            Console.Error.WriteLine($"[ClaudeCodeAdapter] FSW error: {e.GetException()?.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_watcher is not null)
                    {
                        _watcher.EnableRaisingEvents = false;
                        _watcher.Dispose();
                    }
                }
                catch { }
                _watcher = null;
                if (Directory.Exists(s_rootDir))
                {
                    TryStartWatcher();
                }
            });
        }

        // ---------- Poll ----------

        private void OnPollTick(object? sender, EventArgs e) => DoPoll();

        private void DoPoll()
        {
            if (!Directory.Exists(s_rootDir))
            {
                return;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(s_rootDir, "*.jsonl", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeCodeAdapter] EnumerateFiles: {ex.Message}");
                return;
            }

            var now = DateTime.UtcNow;
            var activeCutoff = now - s_activeWindow;

            foreach (var path in files)
            {
                DateTime mtime;
                try
                {
                    mtime = File.GetLastWriteTimeUtc(path);
                }
                catch { continue; }

                if (mtime < activeCutoff)
                {
                    // 非活跃 session：停其静默 timer，下次 poll 跳过。
                    // 不主动清游标，便于再次活跃时续读增量。
                    SessionState? ss;
                    lock (_lock)
                    {
                        _sessions.TryGetValue(path, out ss);
                    }
                    if (ss is not null)
                    {
                        try { ss.DebounceTimer?.Stop(); } catch { }
                    }
                    continue;
                }

                ProcessFile(path, now);
            }
        }

        private void ProcessFile(string path, DateTime nowUtc)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch { return; }

            FileCursor cursor;
            lock (_lock)
            {
                if (!_cursors.TryGetValue(path, out cursor))
                {
                    cursor = new FileCursor(lastSize: 0, lineBuffer: string.Empty);
                    _cursors[path] = cursor;
                }
            }

            // 文件被截断/轮转：重置游标。
            if (size < cursor.LastSize)
            {
                cursor = new FileCursor(0, string.Empty);
                lock (_lock) { _cursors[path] = cursor; }
            }

            if (size == cursor.LastSize)
            {
                // 无新数据，仅推进静默 timer 判定（DoPoll 已处理）。
                TickSession(path, hadNewData: false, lastEntries: null, nowUtc);
                return;
            }

            // 读自游标之后的新字节。
            string appended;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(cursor.LastSize, SeekOrigin.Begin);
                var remaining = (int)(size - cursor.LastSize);
                var buffer = new byte[remaining];
                var read = fs.Read(buffer, 0, remaining);
                appended = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeCodeAdapter] read {path}: {ex.Message}");
                return;
            }

            // 拼上次的末行缓冲，按 '\n' 切。
            var combined = cursor.LineBuffer + appended;
            var newBuffer = string.Empty;
            List<JsonElement>? parsedEntries = null;

            int lineStart = 0;
            for (int i = 0; i < combined.Length; i++)
            {
                if (combined[i] == '\n')
                {
                    var line = combined.AsSpan(lineStart, i - lineStart).Trim().ToString();
                    lineStart = i + 1;
                    if (line.Length == 0) { continue; }
                    var entry = TryParseLine(line);
                    if (entry is not null)
                    {
                        (parsedEntries ??= new List<JsonElement>()).Add(entry.Value);
                    }
                }
            }

            if (lineStart < combined.Length)
            {
                // 末行无 '\n'：缓冲到下次。
                newBuffer = combined.Substring(lineStart);
            }

            // 更新游标
            lock (_lock)
            {
                _cursors[path] = new FileCursor(size, newBuffer);
            }

            TickSession(path, hadNewData: true, lastEntries: parsedEntries, nowUtc);
        }

        // ---------- 单 session 状态机 ----------

        private void TickSession(string path, bool hadNewData, List<JsonElement>? lastEntries, DateTime nowUtc)
        {
            SessionState ss;
            bool newSession;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(path, out ss))
                {
                    ss = new SessionState
                    {
                        DebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
                        {
                            Interval = s_silenceDebounce
                        }
                    };
                    ss.DebounceTimer.Tick += (s, e) => OnSilenceElapsed(path);
                    _sessions[path] = ss;
                    newSession = true;
                }
                else
                {
                    newSession = false;
                }
            }

            // 派生当前态（仅依据末尾内容，不存内容本体）
            AgentState? derived = null;
            if (hadNewData && lastEntries is not null && lastEntries.Count > 0)
            {
                derived = DeriveStateFromTail(lastEntries, out _);
                // 记录最近一次派生态（非内容）
                lock (_lock)
                {
                    ss.LastDerivedState = derived;
                    ss.LastActivityUtc = nowUtc;
                }
                // 重置静默 debounce timer（每次该文件有新 append → 重置）
                try
                {
                    ss.DebounceTimer!.Stop();
                    ss.DebounceTimer.Start();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ClaudeCodeAdapter] reset debounce: {ex.Message}");
                }

                // 有新数据时按 derived 态实时发布（Run/Review/Failed/Waiting 等活性态）
                PublishState(path, derived.Value);
            }
            else if (newSession)
            {
                // 首次发现：不主动发 Idle，等真正派生出态再发。
            }
        }

        private void OnSilenceElapsed(string path)
        {
            SessionState? ss;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(path, out ss) || ss is null) { return; }
            }

            // 末尾 assistant 文本 + 静默超 20s → Wave
            // Jump 需 todo 全完成标记——本轮无可靠标记，只发 Wave。
            // 若 LastDerivedState 是 Run/Review/Waiting 等活性态，静默后降 Wave（视为该 session 已完成一回合）。
            // 若 LastDerivedState 已是 Failed/Wave/Idle，不覆盖。
            var last = ss.LastDerivedState;
            if (last == AgentState.Failed) { return; }

            PublishState(path, AgentState.Wave);

            // 停 timer（Wave 一次性，由 staleness/aggregator 衰减回 Idle）
            try { ss.DebounceTimer?.Stop(); } catch { }
        }

        private void PublishState(string path, AgentState state)
        {
            SessionState? ss;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(path, out ss) || ss is null) { return; }
                // 去重：同 session 同态不重复发。
                if (ss.LastPublishedState == state) { return; }
                ss.LastPublishedState = state;
            }

            // Detail 仅承载非内容摘要（路径 + 态名），绝不放 content/reasoning。
            var detail = $"{Path.GetFileName(path)}:{state}";
            _eventBus.PublishOnUIThread(EventType.AgentStateChanged,
                new AgentStateEvent(s_id, state, detail, DateTime.Now));
        }

        // ---------- 解析 ----------

        private static JsonElement? TryParseLine(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                // 拷贝为可独立持有的元素（JsonDocument.Dispose 后仍可用）。
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 依据一组新 append 行（按出现顺序）的末尾内容派生态。
        /// 规则（PLAN §5）：
        ///   - 末尾 tool_use 未配 tool_result → Run
        ///   - 思考/读取类（read_file/glob/grep 等） → Review
        ///   - 任意 is_error=true 的 tool_result → Failed
        ///   - 末尾 user/awaiting input → Waiting
        ///   - 末尾 assistant 文本（非 tool_use）→ 视为思考段，先不在此判定 Wave（Wave 由静默 timer 推）
        /// </summary>
        private static AgentState DeriveStateFromTail(List<JsonElement> entries, out string _detail)
        {
            _detail = string.Empty;
            if (entries.Count == 0) { return AgentState.Idle; }

            // 全扫描找 is_error（任何 error → Failed 优先）
            bool anyError = false;
            string? lastToolUseId = null;
            bool tailIsToolUse = false;     // 末尾行是 tool_use
            bool tailIsUser = false;        // 末尾行是 user（含 tool_result 或 awaiting input）
            bool tailIsAssistantText = false; // 末尾行是 assistant 纯文本
            bool tailIsReadLike = false;    // 末尾 tool_use 是 read/grep/glob 等读取类

            for (int i = 0; i < entries.Count; i++)
            {
                var el = entries[i];
                var role = TryGetString(el, "role");
                var isError = TryGetBool(el, "is_error");

                // content 可能是数组（content blocks）或字符串。
                if (role == "user")
                {
                    // user 行通常含 tool_result
                    if (isError == true) { anyError = true; }
                    lastToolUseId = null; // 配平
                    tailIsToolUse = false;
                    tailIsUser = true;
                    tailIsAssistantText = false;
                    tailIsReadLike = false;
                }
                else if (role == "assistant")
                {
                    var (hasToolUse, isReadLike) = InspectAssistantContent(el);
                    if (hasToolUse)
                    {
                        lastToolUseId = "pending";
                        tailIsToolUse = true;
                        tailIsReadLike = isReadLike;
                    }
                    else
                    {
                        lastToolUseId = null;
                        tailIsToolUse = false;
                        tailIsReadLike = false;
                    }
                    tailIsUser = false;
                    tailIsAssistantText = !hasToolUse;
                }
                // 其他 role（如 "summary"/"system"）忽略
            }

            if (anyError) { return AgentState.Failed; }

            // 末尾停在 tool_use（未配 tool_result）→ Run（读取类降 Review）
            if (tailIsToolUse)
            {
                return tailIsReadLike ? AgentState.Review : AgentState.Run;
            }

            // 末尾停在 user（往往是 tool_result 回包或 awaiting user input）
            if (tailIsUser)
            {
                // 若末尾 user 行无 tool_result content block → awaiting input
                if (LastUserIsAwaiting(entries[entries.Count - 1]))
                {
                    return AgentState.Waiting;
                }
                // tool_result 已回 → agent 继续干活，按 Run（静默后会推 Wave）
                return AgentState.Run;
            }

            // 末尾 assistant 文本 → 视为思考段（Review），静默后推 Wave
            if (tailIsAssistantText)
            {
                return AgentState.Review;
            }

            return AgentState.Idle;
        }

        private static (bool hasToolUse, bool isReadLike) InspectAssistantContent(JsonElement el)
        {
            if (!el.TryGetProperty("content", out var content)) { return (false, false); }

            bool hasToolUse = false;
            bool isReadLike = false;

            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var type = TryGetString(block, "type");
                    if (type == "tool_use")
                    {
                        hasToolUse = true;
                        var name = TryGetString(block, "name");
                        if (IsReadLikeTool(name)) { isReadLike = true; }
                    }
                    else if (type == "thinking")
                    {
                        // 思考块 → 视作 Review 倾向（不在此 set hasToolUse）
                        isReadLike = true;
                    }
                }
            }
            else if (content.ValueKind == JsonValueKind.String)
            {
                // 纯文本 assistant 消息
            }

            return (hasToolUse, isReadLike);
        }

        private static bool LastUserIsAwaiting(JsonElement userEl)
        {
            // 末尾 user 行：若 content 不含 tool_result block（如纯文本 awaiting input）→ Waiting
            if (!userEl.TryGetProperty("content", out var content)) { return true; }

            if (content.ValueKind == JsonValueKind.String) { return true; }

            if (content.ValueKind == JsonValueKind.Array)
            {
                bool hasToolResult = false;
                foreach (var block in content.EnumerateArray())
                {
                    var type = TryGetString(block, "type");
                    if (type == "tool_result") { hasToolResult = true; }
                }
                return !hasToolResult;
            }

            return true;
        }

        private static bool IsReadLikeTool(string? name)
        {
            return name switch
            {
                "read_file" or "Read" or "grep" or "glob" or "list_files" or "list_dir"
                    or "search" or "find" => true,
                _ => false
            };
        }

        private static string? TryGetString(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
            return null;
        }

        private static bool? TryGetBool(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out p) && p.ValueKind == JsonValueKind.False)
            {
                return false;
            }
            return null;
        }

        // ---------- 内部数据结构 ----------

        private sealed class FileCursor
        {
            public long LastSize { get; }
            public string LineBuffer { get; }
            public FileCursor(long lastSize, string lineBuffer)
            {
                LastSize = lastSize;
                LineBuffer = lineBuffer;
            }
        }

        private sealed class SessionState
        {
            public DispatcherTimer? DebounceTimer { get; set; }
            public AgentState? LastDerivedState { get; set; }
            public AgentState? LastPublishedState { get; set; }
            public DateTime LastActivityUtc { get; set; }
        }
    }
}
