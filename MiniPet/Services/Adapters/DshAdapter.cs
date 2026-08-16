using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MiniPet.Models;

namespace MiniPet.Services.Adapters
{
    /// <summary>
    /// DSH（DeepSeek Harness，本机 Web GUI 后端，端口 3080）agent 状态适配器
    /// （RESEARCH-dsh-http-bridge.md：纯观察 + 对话两种形态）。
    ///
    /// 主链路：ClientWebSocket 连 ws://127.0.0.1:3080/api/events.mux（全会话 mux 帧），
    /// 订阅 session/event / session/projection / question/requested / approval/requested / stream/error。
    /// 断线指数退避重连（复用 HermesAdapter 骨架）。
    ///
    /// 事件 → AgentState（映射表见 RESEARCH §4.4，实测校准 2026-08-15）：
    ///   - turn/start / step/start / tool/call / user/message / text-delta → Run
    ///   - assistant/chunk(reasoning-delta) → Review
    ///   - tool/result(isError) / turn/end(reason.error) / stream/error → Failed
    ///   - question/requested / approval/requested / approval/asked → Waiting
    ///   - turn/end(reason.completed / max-tokens) → 本地合成 Jump（最高活性会话）→ 衰减 Wave → Idle
    ///   - turn/end(reason.aborted / blocked / interrupted) → Idle
    ///   - 静默 > staleness → Aggregator 降 Idle（心跳防误衰减）
    ///
    /// 多会话聚合：mux 是全会话流，本 adapter 作为 Aggregator 的单一源（AgentId.Dsh），
    /// 内部按会话跟踪最近态，取活性级最高者发布（同 Hermes 多 profile 聚合语义）。
    ///
    /// 对话（DshChatWindow）：session.create（cwd=MiniPet 工作区、sessionId 固定 minipet_dsh，
    /// 幂等复用）→ session.prompt（mode=queue）→ WS mux 按 sessionId 过滤本会话的
    /// assistant/chunk(text-delta) 增量 → DeltaReceived 滚动；取消用 session.cancel RPC（真取消）。
    ///
    /// 鉴权：loopback（127.0.0.1）免 token（browser-trust fence），session.* 不在特权方法列表。
    /// 隐私红线：只消费事件视图结构字段与非内容 Detail，绝不拉取 session.history 全文/content/密钥；
    /// 对话增量文本仅经 DeltaReceived 交 UI，绝不落盘/log。
    /// </summary>
    public sealed class DshAdapter : IAgentStateAdapter
    {
        // ===== 常量 =====
        private const string DefaultHost = "127.0.0.1";     // 锁死本机 loopback（跨机 out of scope）
        private const int DefaultPort = 3080;               // dsh --profile web 默认端口
        private const int InitialBackoffMs = 1_000;         // 初始退避 1s
        private const int MaxBackoffMs = 60_000;            // 退避封顶 60s
        private const string FixedSessionId = "minipet_dsh"; // 对话会话固定 id（确定性复用，跨重启连续上下文）
        private const int MaxSessions = 64;                 // 会话跟踪表上限（防内存膨胀）

        // ===== 依赖 =====
        private readonly EventBus _eventBus;
        private readonly string _host;
        private readonly int _port;
        private readonly string _workspace;                 // session.create 的 cwd
        private readonly int _stalenessSeconds;
        private readonly int _jumpDisplaySeconds;

        // ===== WS 生命周期 =====
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _lifetimeCts;
        private bool _running;
        private int _currentBackoffMs = InitialBackoffMs;

        // ===== 观察状态（全会话聚合） =====
        private readonly Dictionary<string, SessionTrack> _sessions = new(StringComparer.Ordinal);
        private AgentState _lastPublished = AgentState.Idle;
        private DateTime _lastPublishUtc = DateTime.MinValue;
        private DispatcherTimer? _stalenessHeartbeat;       // 长回合静默心跳（刷新 Aggregator staleness）
        private DispatcherTimer? _idleDecayTimer;           // Jump 庆祝后 → Idle 衰减

        // ===== 对话状态 =====
        private string? _sessionId;                         // 已建会话 id（minipet_dsh）
        private int _lastSeenTurn = -1;                     // 本会话最近一次 turn/start 回合号（发送时基线用）
        private int _chatBaseTurn = -1;                     // 发送时正在进行的回合号（排除旧回合 turn/end 误结算）
        private int? _chatTurn;                             // 本轮回合号（turn/end 配对）
        private readonly StringBuilder _chatDelta = new();  // text-delta 增量累积（流式过程稿）
        private readonly List<string> _chatFinals = new();  // assistant/message 终稿（按序）
        private TaskCompletionSource<string>? _chatTcs;     // 回合结束结算
        private bool _chatError;                            // 本回合出错（reason.error）
        private bool _chatCancelled;                        // 本回合被取消（aborted/blocked）

        // 共享 HttpClient（RPC 用；loopback 免鉴权，无 header）
        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// 回复中间文本增量（assistant/chunk text-delta，仅本 adapter 拥有的会话 minipet_dsh），
        /// UI 实时滚动。回调在 WS 读取线程触发（非 UI 线程），订阅方需自行 marshal 到 UI。
        /// </summary>
        public event Action<string>? DeltaReceived;

        /// <summary>当前是否忙（对话回合未结束），UI 并发发送保护。</summary>
        public bool IsBusy { get; private set; }

        /// <summary>已建 DSH 会话 id（minipet_dsh）。空 = 未建。</summary>
        public string? DshSessionId => _sessionId;

        /// <summary>
        /// 连接状态回调（每次 WS 连接成功/失败触发一次，UI 线程 Post）。
        /// 文案只含来源 + 时间，绝不携带 content/reasoning/key。
        /// </summary>
        public Action<string, bool>? ConnStatusCallback { get; set; }

        /// <summary>当前活跃实例（App 生命周期内单例；Start 设 / Stop 清）。</summary>
        public static DshAdapter? Current { get; private set; }

        /// <summary>
        /// 收到 mux 帧时触发（参数 = 帧类型，如 session/subscribed / session/event / session/projection）。
        /// 仅帧类型字符串，绝不携带帧内容（隐私红线：不落 content/reasoning/key）。
        /// 供 DshProbe 验证 / 未来可观测性使用；回调在 WS 读取线程触发。
        /// </summary>
        public event Action<string>? MuxFrameReceived;

        /// <summary>按 AgentIntegrationConfig 构造（DshHost/DshPort/DshWorkspace 生效）。</summary>
        public DshAdapter(EventBus eventBus, AgentIntegrationConfig ai)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _host = string.IsNullOrWhiteSpace(ai?.DshHost) ? DefaultHost : ai!.DshHost.Trim();
            _port = (ai?.DshPort ?? 0) == 0 ? DefaultPort : ai!.DshPort;
            _workspace = string.IsNullOrWhiteSpace(ai?.DshWorkspace) ? Environment.CurrentDirectory : ai!.DshWorkspace.Trim();
            _stalenessSeconds = Math.Max(5, ai?.StalenessSeconds ?? 30);
            _jumpDisplaySeconds = Math.Max(1, ai?.JumpDisplaySeconds ?? 3);

            // 长回合 staleness 抑制：活跃期间静默超过半阈值 → 心跳重复最近活性态（刷新 Aggregator staleness）。
            _stalenessHeartbeat = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(_stalenessSeconds / 2.0, 5, 30))
            };
            _stalenessHeartbeat.Tick += OnStalenessHeartbeat;
        }

        /// <inheritdoc />
        public AgentId Id => AgentId.Dsh;

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
            _lifetimeCts = new CancellationTokenSource();
            _stalenessHeartbeat?.Start();
            _ = RunAsync(_lifetimeCts.Token);
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!_running) {
                return;
            }
            _running = false;
            _stalenessHeartbeat?.Stop();
            _idleDecayTimer?.Stop();
            _lifetimeCts?.Cancel();
            try {
                _ws?.Dispose();
            }
            catch {
                // 释放竞态忽略
            }
            _ws = null;
            if (Current == this) {
                Current = null;
            }
        }

        // ============================================================
        // WS 主循环（连接 + 指数退避重连）
        // ============================================================

        /// <summary>连接 → 收帧循环；断开后指数退避重连，直到 Stop。</summary>
        private async Task RunAsync(CancellationToken ct)
        {
            while (_running)
            {
                ClientWebSocket? ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    var uri = new Uri($"ws://{_host}:{_port}/api/events.mux");
                    await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                    _currentBackoffMs = InitialBackoffMs;
                    _ws = ws;
                    RaiseConnStatus(true);
                    await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                    ws = null; // ReceiveLoop 内已处理关闭/释放
                }
                catch (OperationCanceledException)
                {
                    break; // Stop / 生命周期取消
                }
                catch (Exception)
                {
                    // DSH 未运行 / 连接被拒 → 退避重连（不崩）
                    RaiseConnStatus(false);
                }
                finally
                {
                    try {
                        ws?.Dispose();
                    }
                    catch {
                        // 忽略
                    }
                    _ws = null;
                }

                if (!_running) {
                    break;
                }
                int delay = _currentBackoffMs;
                _currentBackoffMs = Math.Min(_currentBackoffMs * 2, MaxBackoffMs);
                try {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    break;
                }
            }
        }

        /// <summary>逐帧读取 mux（一帧一条 WS 消息，JSON 信封 {type,rpcId,method,payload}）。</summary>
        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (_running && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch {
                            // 服务端已断，忽略
                        }
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var text = sb.ToString();
                if (!string.IsNullOrWhiteSpace(text)) {
                    HandleMuxFrame(text);
                }
            }
        }

        // ============================================================
        // mux 帧解析（观察链路主入口）
        // ============================================================

        private void HandleMuxFrame(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) {
                    return;
                }
                // 信封 payload 槽 = mux 帧（type 判别）。
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) {
                    return;
                }
                // 帧类型外发（可观测性：DshProbe 验证收帧；只发类型不发内容）。
                var frameType = GetString(payload, "type");
                if (!string.IsNullOrEmpty(frameType)) {
                    MuxFrameReceived?.Invoke(frameType);
                }
                HandleMuxPayload(payload);
            }
            catch (JsonException)
            {
                // 坏帧忽略（不崩接收循环）
            }
        }

        private void HandleMuxPayload(JsonElement payload)
        {
            var type = GetString(payload, "type");
            switch (type)
            {
                case "session/event":
                    HandleSessionEvent(payload);
                    break;

                case "session/projection":
                    HandleProjection(payload);
                    break;

                case "question/requested":
                    SetSessionState(GetString(payload, "sessionId"), AgentState.Waiting, "question");
                    break;

                case "approval/requested":
                    SetSessionState(GetString(payload, "sessionId"), AgentState.Waiting, "approval");
                    break;

                case "question/resolved":
                case "approval/resolved":
                    HandleResolved(payload);
                    break;

                case "session/queue":
                    HandleQueue(payload);
                    break;

                case "stream/error":
                    PublishState(AgentState.Failed, "stream/error");
                    break;

                default:
                    // session/subscribed / session/jobs / 未知帧：不映射状态。
                    break;
            }
        }

        /// <summary>session/event：事件驱动 7 态（主状态源，映射见类注释）。</summary>
        private void HandleSessionEvent(JsonElement payload)
        {
            var sessionId = GetString(payload, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) {
                return;
            }
            if (!payload.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) {
                return;
            }
            var etype = GetString(ev, "type");
            var data = ev.TryGetProperty("data", out var d) ? d : default;

            switch (etype)
            {
                case "assistant/chunk":
                    // 对话流式增量：仅发送中的本 adapter 会话（minipet_dsh）发 DeltaReceived
                    // （防 GUI 侧同会话 prompt 的增量串入聊天窗口）。
                    if (sessionId == _sessionId && _chatTcs != null)
                    {
                        var delta = ExtractChunkText(data);
                        if (!string.IsNullOrEmpty(delta))
                        {
                            _chatDelta.Append(delta);
                            DeltaReceived?.Invoke(delta);
                        }
                    }
                    var chunkState = MapChunkState(data);
                    if (chunkState.HasValue) {
                        SetSessionState(sessionId, chunkState.Value, "assistant/chunk");
                    }
                    break;

                case "assistant/message":
                    // 终稿收敛（对话）：仅发送中累积 text 块（reasoning 块不进回复）。
                    if (sessionId == _sessionId && _chatTcs != null)
                    {
                        var final = ExtractAssistantText(data);
                        if (!string.IsNullOrEmpty(final)) {
                            _chatFinals.Add(final);
                        }
                    }
                    break;

                case "turn/start":
                    SetSessionState(sessionId, AgentState.Run, "turn/start");
                    if (sessionId == _sessionId)
                    {
                        var turnNo = GetInt(data, "turn");
                        if (turnNo >= 0) {
                            _lastSeenTurn = turnNo;
                        }
                        // 发送中：记录本轮回合号（发送后首个新回合 = 我们的回合，排除发送前已在进行的老回合）。
                        if (_chatTcs != null && !_chatTurn.HasValue && turnNo > _chatBaseTurn) {
                            _chatTurn = turnNo;
                        }
                    }
                    break;

                case "turn/end":
                    HandleTurnEnd(sessionId, data);
                    break;

                default:
                    var st = MapSessionEventState(etype, data);
                    if (st.HasValue) {
                        SetSessionState(sessionId, st.Value, etype);
                    }
                    break;
            }
        }

        /// <summary>turn/end：按 reason.kind 收敛会话态 + 结算对话回合 + 完成庆祝。</summary>
        private void HandleTurnEnd(string sessionId, JsonElement data)
        {
            var kind = MapTurnEndKind(data);
            if (kind == "error")
            {
                _sessions[sessionId] = new SessionTrack(AgentState.Failed, DateTime.UtcNow);
            }
            else if (kind == "completed" || kind == "max-tokens")
            {
                _sessions[sessionId] = new SessionTrack(AgentState.Wave, DateTime.UtcNow);
            }
            else
            {
                // aborted / blocked / interrupted / 未知 → 取消或静默
                _sessions[sessionId] = new SessionTrack(AgentState.Idle, DateTime.UtcNow);
            }
            _lastPublishUtc = DateTime.UtcNow;

            // 对话回合结算：本会话 + 回合号配对（turn > 发送时基线 = 本回合；未记录回合号则按会话收敛）。
            if (_chatTcs != null && sessionId == _sessionId)
            {
                var turnNo = GetInt(data, "turn");
                if (turnNo > _chatBaseTurn && (!_chatTurn.HasValue || turnNo == _chatTurn.Value))
                {
                    var tcs = _chatTcs;
                    _chatTcs = null;
                    if (kind == "error") {
                        _chatError = true;
                    }
                    else if (kind != "completed" && kind != "max-tokens") {
                        _chatCancelled = true;
                    }
                    tcs.TrySetResult(GetChatReplyText());
                }
            }

            if (kind == "completed" || kind == "max-tokens")
            {
                // 完成庆祝：仅当本会话为当前最高活性会话时合成 Jump（多会话并行时由活性级决定）。
                var reflect = ComputeBestState();
                if (reflect == AgentState.Wave || reflect == AgentState.Idle)
                {
                    _lastPublished = reflect;
                    PublishState(AgentState.Jump, "turn completed"); // 一次性庆祝（Aggregator 到期衰减 Wave）
                    ScheduleIdleDecay();
                }
                else
                {
                    PublishReflect();
                }
            }
            else
            {
                PublishReflect();
            }
        }

        /// <summary>session/projection：subagentTiming.active = 强活跃佐证 → Run；其余投影仅刷新活性。</summary>
        private void HandleProjection(JsonElement payload)
        {
            var sessionId = GetString(payload, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) {
                return;
            }
            if (!payload.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) {
                return;
            }
            var key = GetString(payload, "key");
            bool activeSubagent = key == "subagentTiming"
                && value.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.Object;
            if (activeSubagent)
            {
                SetSessionState(sessionId, AgentState.Run, "subagent running");
                return;
            }
            // 其余投影（sessionStats/title 等）：仅刷新已活跃会话的活性，不强制 Run（防回合结束的尾投影误报）。
            if (_sessions.TryGetValue(sessionId, out var tr) && tr.State != AgentState.Idle)
            {
                _sessions[sessionId] = new SessionTrack(tr.State, DateTime.UtcNow);
                _lastPublishUtc = DateTime.UtcNow;
            }
        }

        /// <summary>question/approval resolved：仅 Waiting 中的会话回 Run（工作恢复）。</summary>
        private void HandleResolved(JsonElement payload)
        {
            var sessionId = GetString(payload, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) {
                return;
            }
            if (_sessions.TryGetValue(sessionId, out var tr) && tr.State == AgentState.Waiting)
            {
                SetSessionState(sessionId, AgentState.Run, "resolved");
            }
        }

        /// <summary>session/queue：有排队消息 → 该会话有活要干 → Run。</summary>
        private void HandleQueue(JsonElement payload)
        {
            var sessionId = GetString(payload, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) {
                return;
            }
            if (payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                SetSessionState(sessionId, AgentState.Run, "queue");
            }
        }

        // ============================================================
        // 会话态聚合 + 发布
        // ============================================================

        private void SetSessionState(string sessionId, AgentState state, string detail)
        {
            if (string.IsNullOrEmpty(sessionId)) {
                return;
            }
            _sessions[sessionId] = new SessionTrack(state, DateTime.UtcNow);
            _lastPublishUtc = DateTime.UtcNow;
            PublishReflect();
        }

        /// <summary>发布当前聚合态（多会话取活性级最高；变化才发，防抖动）。</summary>
        private void PublishReflect()
        {
            var best = ComputeBestState();
            if (best == _lastPublished) {
                return;
            }
            _lastPublished = best;
            PublishState(best, BuildDetail());
        }

        /// <summary>聚合态：各会话最近态按活性级取最高（Run/Failed &gt; Review &gt; Waiting &gt; Wave &gt; Idle）。</summary>
        private AgentState ComputeBestState()
        {
            var best = AgentState.Idle;
            foreach (var kv in _sessions)
            {
                var s = kv.Value.State;
                if (s == AgentState.Idle) {
                    continue;
                }
                if (ActivityRank(s) > ActivityRank(best)) {
                    best = s;
                }
            }
            return best;
        }

        private bool HasActiveSession()
        {
            foreach (var kv in _sessions)
            {
                var s = kv.Value.State;
                if (s == AgentState.Run || s == AgentState.Review || s == AgentState.Failed || s == AgentState.Waiting) {
                    return true;
                }
            }
            return false;
        }

        private static int ActivityRank(AgentState s)
        {
            return s switch
            {
                AgentState.Failed => 4,
                AgentState.Run => 4,
                AgentState.Review => 3,
                AgentState.Waiting => 2,
                AgentState.Wave => 1,
                _ => 0, // Idle
            };
        }

        private void PublishState(AgentState state, string detail)
        {
            _eventBus.PublishOnUIThread(
                EventType.AgentStateChanged,
                new AgentStateEvent(AgentId.Dsh, state, detail, DateTime.Now));
            _lastPublishUtc = DateTime.UtcNow;
        }

        /// <summary>Detail 只放非内容摘要（活跃会话数）。</summary>
        private string BuildDetail()
        {
            int active = 0;
            foreach (var kv in _sessions)
            {
                if (kv.Value.State != AgentState.Idle) {
                    active++;
                }
            }
            return $"active sessions: {active}";
        }

        /// <summary>长回合 staleness 抑制：活跃期间静默超半阈值 → 心跳重复最近活性态（刷新 Aggregator staleness）。</summary>
        private void OnStalenessHeartbeat(object? sender, EventArgs e)
        {
            if (!_running) {
                _stalenessHeartbeat?.Stop();
                return;
            }
            if (!HasActiveSession()) {
                CleanupStaleSessions();
                return;
            }
            double silence = Math.Max(5.0, _stalenessSeconds / 2.0);
            if ((DateTime.UtcNow - _lastPublishUtc).TotalSeconds < silence) {
                return;
            }
            var best = ComputeBestState();
            if (best != AgentState.Idle) {
                PublishState(best, "keepalive");
            }
        }

        /// <summary>会话跟踪表清理：Idle 且超 staleness 的旧会话移除（防长期运行内存膨胀）。</summary>
        private void CleanupStaleSessions()
        {
            if (_sessions.Count <= MaxSessions) {
                return;
            }
            var cutoff = DateTime.UtcNow.AddSeconds(-_stalenessSeconds);
            List<string>? stale = null;
            foreach (var kv in _sessions)
            {
                if (kv.Value.State == AgentState.Idle && kv.Value.LastSeen < cutoff)
                {
                    stale ??= new List<string>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
            {
                foreach (var sid in stale) {
                    _sessions.Remove(sid);
                }
            }
        }

        /// <summary>Jump 庆祝后 → Idle 衰减（Aggregator 的 Jump 到期衰减 Wave 后，此处再收敛 Idle）。</summary>
        private void ScheduleIdleDecay()
        {
            if (_idleDecayTimer == null)
            {
                _idleDecayTimer = new DispatcherTimer();
                _idleDecayTimer.Tick += OnIdleDecayTick;
            }
            else {
                _idleDecayTimer.Stop();
            }
            _idleDecayTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _jumpDisplaySeconds + 2));
            _idleDecayTimer.Start();
        }

        private void OnIdleDecayTick(object? sender, EventArgs e)
        {
            _idleDecayTimer?.Stop();
            if (!_running) {
                return;
            }
            // 无活跃会话（Run/Review/Failed/Waiting 皆无）→ 发布 Idle 放松（庆祝播完）。
            if (!HasActiveSession() && _lastPublished != AgentState.Idle)
            {
                _lastPublished = AgentState.Idle;
                PublishState(AgentState.Idle, "idle");
            }
        }

        // ============================================================
        // 对话形态（RPC + 回合结算）
        // ============================================================

        /// <summary>
        /// 发一轮对话：session.create（幂等复用 minipet_dsh）→ session.prompt（mode=queue，accepted 即返回）
        /// → 等 WS turn/end 结算（text-delta 增量已实时 DeltaReceived）。
        /// 返回终稿回复文本（assistant/message 终稿优先，无终稿则 delta 过程稿累积）。
        /// 取消：ct 触发 → 尽力 RPC session.cancel（服务端真取消，turn/end aborted 补发）。
        /// </summary>
        public async Task<string> SendMessageAsync(string message, CancellationToken ct = default)
        {
            if (IsBusy) {
                throw new InvalidOperationException("DSH 回合已在进行（IsBusy 防并发发送）");
            }

            IsBusy = true;
            _chatDelta.Clear();
            _chatFinals.Clear();
            _chatTurn = null;
            _chatBaseTurn = _lastSeenTurn; // 发送时正在进行的回合号（旧回合 turn/end 不结算本回合）
            _chatError = false;
            _chatCancelled = false;
            _chatTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var sessionId = await EnsureSessionAsync(ct).ConfigureAwait(false);
                var (ok, err) = await PostRpcAsync("session.prompt", new
                {
                    sessionId,
                    mode = "queue",
                    content = new object[] { new { type = "text", text = message } },
                }, ct).ConfigureAwait(false);
                if (!ok) {
                    throw new InvalidOperationException("session.prompt 被拒绝" + (string.IsNullOrEmpty(err) ? string.Empty : "：" + err));
                }

                var reply = await _chatTcs.Task.WaitAsync(ct).ConfigureAwait(false);
                if (_chatCancelled) {
                    throw new OperationCanceledException("用户取消", ct);
                }
                if (_chatError) {
                    return string.IsNullOrEmpty(reply) ? "[DSH 回合出错]" : reply;
                }
                return reply;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户取消：真取消 RPC（服务端 turn 会结束，回合态由 turn/end 收敛）。
                try {
                    await CancelAsync().ConfigureAwait(false);
                }
                catch {
                    // 取消失败不掩盖原始取消
                }
                throw;
            }
            finally
            {
                IsBusy = false;
                _chatTcs = null;
            }
        }

        /// <summary>取消当前回合：session.cancel RPC（服务端真取消，比断连可靠）。幂等。</summary>
        public async Task CancelAsync()
        {
            if (string.IsNullOrEmpty(_sessionId)) {
                return;
            }
            await PostRpcAsync("session.cancel", new { sessionId = _sessionId }, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>建会话：session.create（sessionId 固定 minipet_dsh，服务端幂等复用，无需 409 特判）。</summary>
        private async Task<string> EnsureSessionAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_sessionId)) {
                return _sessionId;
            }
            var (ok, err) = await PostRpcAsync("session.create", new { cwd = _workspace, sessionId = FixedSessionId }, ct).ConfigureAwait(false);
            if (!ok) {
                throw new InvalidOperationException("session.create 失败" + (string.IsNullOrEmpty(err) ? string.Empty : "：" + err));
            }
            _sessionId = FixedSessionId;
            return _sessionId;
        }

        /// <summary>
        /// 终稿回复文本：assistant/message 终稿按序拼接（权威）；无终稿（中途取消/思考中）→ delta 累积。
        /// </summary>
        private string GetChatReplyText()
        {
            if (_chatFinals.Count > 0) {
                return string.Join("\n", _chatFinals);
            }
            return _chatDelta.ToString();
        }

        /// <summary>RPC 信封：POST /api/{method}，body {type:client-request, rpcId, method, payload}；回包 result.ok。</summary>
        private async Task<(bool Ok, string? Error)> PostRpcAsync(string method, object payload, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                type = "client-request",
                rpcId = "minipet-" + Guid.NewGuid().ToString("N"),
                method,
                payload,
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await s_http.PostAsync($"http://{_host}:{_port}/api/{method}", content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                {
                    bool ok = result.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    string? err = null;
                    if (!ok && result.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                    {
                        err = GetString(errEl, "message");
                        if (string.IsNullOrEmpty(err)) {
                            err = "rpc failed";
                        }
                    }
                    return (ok, err);
                }
            }
            catch (JsonException)
            {
                // 回包不可解析 → 按失败
            }
            return (false, "bad response");
        }

        /// <summary>触发连接状态回调（UI 线程 Post）。文案只含来源 + 时间。</summary>
        private void RaiseConnStatus(bool ok)
        {
            var callback = ConnStatusCallback;
            if (callback == null) {
                return;
            }
            string text = ok ? $"DSH ✓ {DateTime.Now:HH:mm:ss}" : "DSH ✗";
            Dispatcher.UIThread.Post(() => callback(text, ok));
        }

        // ============================================================
        // 纯函数映射（白盒单测面，HermesAdapterTests 同款）
        // ============================================================

        /// <summary>session/event 事件 → 会话态（null = 不改变态）。实测校准 2026-08-15。</summary>
        internal static AgentState? MapSessionEventState(string eventType, JsonElement data)
        {
            switch (eventType)
            {
                case "turn/start":
                case "step/start":
                case "tool/call":
                case "user/message":
                    return AgentState.Run;

                case "assistant/chunk":
                    return MapChunkState(data);

                case "tool/result":
                    return ToolResultIsError(data) ? AgentState.Failed : AgentState.Run;

                case "turn/end":
                    return MapTurnEndState(data);

                case "approval/asked":
                    return AgentState.Waiting;

                case "approval/decided":
                    return AgentState.Run;

                default:
                    // assistant/message / step/end / todo/write / request/* / 未知 → 不映射
                    return null;
            }
        }

        /// <summary>assistant/chunk → 态：reasoning-delta → Review（思考中）；text-delta → Run；其余 chunk 不映射。</summary>
        internal static AgentState? MapChunkState(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) {
                return null;
            }
            if (!data.TryGetProperty("chunk", out var chunk) || chunk.ValueKind != JsonValueKind.Object) {
                return null;
            }
            var ctype = GetString(chunk, "type");
            if (ctype == "reasoning-delta") {
                return AgentState.Review;
            }
            if (ctype == "text-delta") {
                return AgentState.Run;
            }
            return null; // block-start / block-end / usage / finish
        }

        /// <summary>turn/end → 态：completed/max-tokens → Wave（适配器再合成 Jump）；error → Failed；其余 → Idle。</summary>
        internal static AgentState? MapTurnEndState(JsonElement data)
        {
            var kind = MapTurnEndKind(data);
            switch (kind)
            {
                case "completed":
                case "max-tokens":
                    return AgentState.Wave;
                case "error":
                    return AgentState.Failed;
                default:
                    return AgentState.Idle; // aborted / blocked / interrupted / 未知
            }
        }

        /// <summary>turn/end 的 reason.kind（completed / error / aborted / blocked / interrupted / max-tokens）。</summary>
        internal static string MapTurnEndKind(JsonElement data)
        {
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                return GetString(r, "kind");
            }
            return string.Empty;
        }

        /// <summary>tool/result 是否出错：data.error 存在 或 message.content[0].isError=true。</summary>
        internal static bool ToolResultIsError(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) {
                return false;
            }
            if (data.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object) {
                return true;
            }
            if (data.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
            {
                var first = content[0];
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>assistant/chunk 的 text-delta 增量文本（对话流式用）。</summary>
        internal static string ExtractChunkText(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) {
                return string.Empty;
            }
            if (!data.TryGetProperty("chunk", out var chunk) || chunk.ValueKind != JsonValueKind.Object) {
                return string.Empty;
            }
            if (GetString(chunk, "type") != "text-delta") {
                return string.Empty;
            }
            return GetString(chunk, "text");
        }

        /// <summary>assistant/message 的 text 块拼接（reasoning 块排除；终稿收敛用）。</summary>
        internal static string ExtractAssistantText(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) {
                return string.Empty;
            }
            if (!data.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) {
                return string.Empty;
            }
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) {
                return string.Empty;
            }
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) {
                    continue;
                }
                if (GetString(block, "type") != "text") {
                    continue;
                }
                var t = GetString(block, "text");
                if (!string.IsNullOrEmpty(t))
                {
                    if (sb.Length > 0) {
                        sb.Append('\n');
                    }
                    sb.Append(t);
                }
            }
            return sb.ToString();
        }

        // ============================================================
        // JSON 辅助
        // ============================================================

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

        private static int GetInt(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) {
                return -1;
            }
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) {
                return v;
            }
            return -1;
        }

        // ============================================================
        // 内部数据结构
        // ============================================================

        internal readonly struct SessionTrack
        {
            public AgentState State { get; }
            public DateTime LastSeen { get; }

            public SessionTrack(AgentState state, DateTime lastSeen)
            {
                State = state;
                LastSeen = lastSeen;
            }
        }
    }
}
