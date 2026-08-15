using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace MiniPet.Services
{
    /// <summary>
    /// Agent 状态聚合器：订阅各 adapter 的 AgentStateChanged，按优先级规则计算桌宠反映态，
    /// 经 EventBus.Publish(EventType.PetReflectStateChanged, reflectState) 输出。
    ///
    /// 反映态规则：
    ///   活动级 Failed ≈ Run > Review > Waiting > Wave > Idle
    ///   （Failed 与 Run 同级时 Failed 胜；同级 tiebreak DSH > Hermes > ClaudeCode > Codex）
    ///   Jump 为一次性庆祝（不进 last-state-wins），固定时长后衰减 Wave→Idle
    ///
    /// staleness：DispatcherTimer 周期检查，某 AgentId 超 StalenessSeconds 无事件
    ///   → 该源降 Idle 并清 Failed/Run/Review，重算反映态
    ///
    /// 注：本类内部 DispatcherTimer 已在 UI 线程跑，直接用 Publish 即可。
    /// </summary>
    public class AgentStateAggregator
    {
        private readonly EventBus _eventBus;

        // 各源最新态（Jump 不进此表，单独管理）
        private readonly Dictionary<AgentId, (AgentState State, DateTime LastSeen)> _latest = new();

        // Jump 庆祝中：源 → 庆祝到期时刻（UTC）。到期后该源衰减 Wave→Idle
        private readonly Dictionary<AgentId, DateTime> _jumpingUntil = new();

        private AgentState _currentReflect = AgentState.Idle;
        private readonly DispatcherTimer _timer;

        // 阈值（可配）
        /// <summary>源超此秒数无事件 → 降 Idle 并清 Failed/Run/Review（默认 30）。</summary>
        public int StalenessSeconds { get; set; } = 30;

        /// <summary>Jump 一次性庆祝时长（秒，默认 3）；到期后该源衰减 Wave→Idle。</summary>
        public int JumpDisplaySeconds { get; set; } = 3;

        // staleness 检查周期（ms）
        private const int TickIntervalMs = 1000;

        public AgentStateAggregator(EventBus eventBus)
        {
            _eventBus = eventBus;
            _eventBus.Subscribe(EventType.AgentStateChanged, OnAgentState);

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(TickIntervalMs), DispatcherPriority.Normal, OnTick);
        }

        /// <summary>启动周期 staleness/Jump 衰减检查。幂等。</summary>
        public void Start()
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        /// <summary>停止检查。幂等。</summary>
        public void Stop()
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        /// <summary>当前反映态（供 UI 直接读取，亦经 PetReflectStateChanged 事件推送）。</summary>
        public AgentState CurrentReflect => _currentReflect;

        private void OnAgentState(object? data)
        {
            if (data is not AgentStateEvent ev)
            {
                return;
            }

            // Jump 一次性：标记该源"庆祝中"，不进 last-state-wins
            if (ev.State == AgentState.Jump)
            {
                _jumpingUntil[ev.Source] = DateTime.UtcNow.AddSeconds(Math.Max(1, JumpDisplaySeconds));
                // Jump 不更新 _latest（避免覆盖原 Run/Review 等真实态）
                RecomputeAndPublish();
                return;
            }

            // 非 Jump：更新该源最新态
            _latest[ev.Source] = (ev.State, DateTime.UtcNow);
            RecomputeAndPublish();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            bool changed = false;
            var now = DateTime.UtcNow;

            // 1) Jump 衰减：到期移除（移除后该源若无 _latest 则视为 Idle）
            if (_jumpingUntil.Count > 0)
            {
                List<AgentId>? expired = null;
                foreach (var kv in _jumpingUntil)
                {
                    if (kv.Value <= now)
                    {
                        expired ??= new List<AgentId>();
                        expired.Add(kv.Key);
                    }
                }
                if (expired != null)
                {
                    foreach (var id in expired)
                    {
                        _jumpingUntil.Remove(id);
                        // 衰减到 Wave（一次性庆祝播完 → 回 Wave；下次重算若无活动则继续落 Idle）
                        _latest[id] = (AgentState.Wave, now);
                    }
                    changed = true;
                }
            }

            // 2) staleness 衰减：超时源降 Idle + 清 Failed/Run/Review
            var cutoff = now.AddSeconds(-StalenessSeconds);
            foreach (var kv in _latest)
            {
                if (kv.Value.LastSeen < cutoff)
                {
                    var s = kv.Value.State;
                    if (s == AgentState.Failed || s == AgentState.Run || s == AgentState.Review)
                    {
                        _latest[kv.Key] = (AgentState.Idle, kv.Value.LastSeen);
                        changed = true;
                    }
                }
            }

            // 3) Jump 中但 _latest 已被覆盖/衰减：保持 changed（让反映态随 Jump 续命，直到 _jumpingUntil 到期）
            if (_jumpingUntil.Count > 0)
            {
                changed = true;
            }

            if (changed)
            {
                RecomputeAndPublish();
            }
        }

        /// <summary>
        /// 重算反映态并发布（仅变化时发）：
        /// 活动级 Failed ≈ Run > Review > Waiting > Wave > Idle；同级 tiebreak DSH > Hermes > CC > Codex；
        /// Jump 优先（任何源庆祝中 → 反映 Jump）。
        /// </summary>
        private void RecomputeAndPublish()
        {
            AgentState next;

            // Jump 例外：任一源庆祝中 → 反映 Jump（一次性，最高优先）
            if (_jumpingUntil.Count > 0)
            {
                next = AgentState.Jump;
            }
            else
            {
                next = ComputeReflectFromLatest();
            }

            if (next != _currentReflect)
            {
                _currentReflect = next;
                _eventBus.Publish(EventType.PetReflectStateChanged, _currentReflect);
            }
        }

        private AgentState ComputeReflectFromLatest()
        {
            AgentState best = AgentState.Idle;
            AgentId bestSource = default;
            bool hasBest = false;

            foreach (var kv in _latest)
            {
                var s = kv.Value.State;
                if (!hasBest)
                {
                    best = s;
                    bestSource = kv.Key;
                    hasBest = true;
                    continue;
                }

                int cmp = CompareActivity(s, kv.Key, best, bestSource);
                if (cmp > 0)
                {
                    best = s;
                    bestSource = kv.Key;
                }
            }

            return best;
        }

        /// <summary>
        /// 比较 (sA, idA) 是否优于 (sB, idB)：
        /// 活动级 Failed ≈ Run > Review > Waiting > Wave > Idle；Failed 与 Run 同级 Failed 胜；
        /// 同级 tiebreak DSH > Hermes > ClaudeCode > Codex。
        /// 返回 &gt;0 表示 A 优，&lt;0 表示 B 优，0 相等。
        /// </summary>
        private static int CompareActivity(AgentState sA, AgentId idA, AgentState sB, AgentId idB)
        {
            int ra = ActivityRank(sA);
            int rb = ActivityRank(sB);
            if (ra != rb)
            {
                return ra.CompareTo(rb);
            }
            // 同级（且均为 Failed/Run 同档）时 Failed 胜
            if (sA != sB && (IsFailedRun(sA) && IsFailedRun(sB)))
            {
                if (sA == AgentState.Failed && sB != AgentState.Failed) return 1;
                if (sB == AgentState.Failed && sA != AgentState.Failed) return -1;
            }
            return SourceTiebreak(idA).CompareTo(SourceTiebreak(idB));
        }

        // 活动级排序（数值越大越优先）：Failed ≈ Run > Review > Waiting > Wave > Idle
        private static int ActivityRank(AgentState s)
        {
            return s switch
            {
                AgentState.Idle => 0,
                AgentState.Wave => 1,
                AgentState.Waiting => 2,
                AgentState.Review => 3,
                AgentState.Run => 4,
                AgentState.Failed => 4, // 与 Run 同级
                _ => 0
            };
        }

        private static bool IsFailedRun(AgentState s) => s == AgentState.Failed || s == AgentState.Run;

        // tiebreak：DSH > Hermes > ClaudeCode > Codex（其它按字符串稳定回退）
        private static int SourceTiebreak(AgentId id)
        {
            if (id == AgentId.Dsh) return 4;
            if (id == AgentId.Hermes) return 3;
            if (id == AgentId.ClaudeCode) return 2;
            if (id == AgentId.Codex) return 1;
            return 0;
        }
    }
}
