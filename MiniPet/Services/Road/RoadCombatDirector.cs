using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Services;

namespace MiniPet.Services.Road
{
    /// <summary>遭遇相位（怪单向战斗状态机，算法 §5.3；NPC 无状态机不使用）。</summary>
    internal enum RoadEncounterPhase
    {
        Approach,   // 随路滚动接近（move 动画；世界 x 固定）
        Attacking,  // 纸娃娃租约攻击中（等命中帧/完成）
        Hit,        // 怪 hit1（HitFallbackMs 兜底时长）
        Die,        // 怪 die1（帧推进完停末帧）
        Hold,       // die1 停末帧展示（DieHoldMs）
        Fade,       // alpha 淡出（DieFadeMs）
        Dead        // 待销毁
    }

    /// <summary>路上实体运行时（可变；快照发布为不可变 RoadEntityState）。</summary>
    internal sealed class RoadEntityRuntime
    {
        public bool IsMob;
        public string LifeId = "";
        public string MapId = "";           // 所属源图（换歌不断代后旧实体按自己的图解析帧——跨图存续关键）
        public double WorldX;               // 世界 x 固定（滚动 = 视口移动）
        public int FeetY;                   // 生成柱表面 y（路坐标）
        public string Action = "stand";
        public int FrameIndex;
        public double Alpha = 1.0;
        public bool FacingLeft = false;     // 427f：原图已朝左 → 不镜像（FacingLeft=false）即面向左（朝向人物）；镜像原图=朝右（反）
        public RoadEncounterPhase Phase = RoadEncounterPhase.Approach;
        public double StateMs;              // 当前相位已历时（RoadClock）
        public bool HitTriggered;           // 攻击命中帧已触发（怪切 hit1 只切一次）
        public bool AttackCancelled;        // 租约被打断 → 作废遭遇（怪继续走过去）
    }

    /// <summary>
    /// RoadCombatDirector —— 路上实体与战斗导演（算法 §5 / PLAN Rev 5 D4 段）：
    /// - 实体生成共通机制：新柱入路时 roll（NPC 1/10 + 冷却≥6 柱 + 同屏&lt;3，仅 stand；
    ///   怪 1/6 + 冷却≥8 柱 + 同屏&lt;2，要求 move+hit1+die1 齐备）；±60px 防冲突；
    ///   生成位置偏向屏幕右缘外 1~2 柱（从右侧走入视野）；
    /// - 滚动接近：怪世界 x 固定，人物世界 x = scrollX+窗宽/2 递增；
    /// - 战斗闸门：AnimService.IsProviderActive（纸娃娃哨兵）为真才有战斗；Mob/NPC 宠物时
    ///   RoadState.CombatDisabled 注记、怪直接走过去（不还手、桌宠无敌）；
    /// - 攻击动作选择器：AnimService 现行武器族映射候选序 × PaperdollService.WeaponHasAction 过滤
    ///   （body 可用 + 武器该动作有帧——双帧红线）；有武器而全候选无帧 → 取消遭遇并
    ///   RoadState.WeaponActionUnavailable++（绝不播裸 body 攻击）；无武器 → swingO1 拳系；
    /// - 时间线：租约 → 攻击帧进度 ≥ HitFrameRatio(0.5) → 怪 hit1 → die1 停末帧 DieHoldMs →
    ///   alpha 淡出 DieFadeMs → 销毁——全部走 RoadClock（暂停即全停）；
    /// - 条带验证 probe（D1 并行契约）：BindStripProbe 注入 (mapId, lifeId, action)→有无帧；
    ///   未接（D1 未落盘）时候选验证降级放行（渲染帧由 D3 从条带缓存取，取不到画空不崩）。
    /// 线程模型：仅 UI 线程逻辑 tick（EndTick）内调用。
    /// </summary>
    public sealed class RoadCombatDirector
    {
        private readonly AnimService _anim;
        private readonly PaperdollService _paperdoll;
        private readonly Random _rng = new();
        private readonly List<RoadEntityRuntime> _entities = new();

        // 武器族 → 攻击动作候选序（族内按序取第一个双帧可用者；对齐 AnimService.GetWeaponActionFamily 映射）
        private static readonly Dictionary<string, string[]> FamilyAttackCandidates = new()
        {
            ["swingO"] = new[] { "swingO1", "swingO2", "swingO3" },
            ["swingT"] = new[] { "swingT1", "swingT2", "swingT3" },
            ["swingP"] = new[] { "swingP1", "swingP2" },
            ["stab"] = new[] { "stabO1", "stabO2" },
            // 弓弩（shoot）近身无射击帧 → 近战候选序（stabO 系优先，兜底 swingO1）
            ["shoot"] = new[] { "stabO1", "stabO2", "swingO1" },
        };

        // 生成冷却状态（柱数口径：距上次生成柱的 WorldX 距离）
        private double _lastNpcSpawnX = double.MinValue;
        private double _lastMobSpawnX = double.MinValue;
        // 已见最小柱 WorldX（镜像方向 F18：新柱自左侧预生成区进入、WorldX 递减——
        // 新柱检测按最小值递减判定；MaxValue = 尚未见过任何柱（首帧全量 roll））
        private double _lastSeenMaxColX = double.MaxValue;

        // 战斗租约（一次至多一个遭遇在攻击）
        private IDisposable? _lease;
        private RoadEntityRuntime? _attacking;
        private string _leaseAction = "";
        // D1 条带验证 probe（(lifeId, action) → 有无帧；控制器组装 RoadFrame 前接入，闭包当前 mapId）
        private Func<string, string, bool>? _stripProbe;
        // life 候选池（pack 级 RoadMaterialPack.LifeCandidates——控制器 InstallCurrentRoad 时绑定）
        private List<LifeCandidate> _lifePool = new();
        // 池所属源图 id（新实体 spawn 时烙进 MapId；换歌换池不洗旧实体——旧实体仍按自己的图取帧）
        private string _poolMapId = "";

        /// <summary>命中帧判定所需的可观测查询由 AnimService.CurrentAnimInfo 提供（构造方持有 _anim 直查）。</summary>
        public RoadCombatDirector(AnimService anim, PaperdollService paperdoll)
        {
            _anim = anim;
            _paperdoll = paperdoll;
        }

        /// <summary>条带验证 probe 接入（D1 并行契约：控制器在组装 RoadFrame 前调用绑定；null = D1 未落盘，候选验证降级放行）。</summary>
        public void BindStripProbe(Func<string, string, bool>? probe)
        {
            _stripProbe = probe;
        }

        /// <summary>life 候选池绑定（pack 级 RoadMaterialPack.LifeCandidates——控制器 InstallCurrentRoad 时注入；mapId 烙进其后生成的实体）。</summary>
        public void BindLifePool(List<LifeCandidate> candidates, string mapId)
        {
            _lifePool = candidates ?? new List<LifeCandidate>();
            _poolMapId = mapId ?? "";
        }

        /// <summary>换源图/重进路时清场（在途租约释放、实体全清、冷却重置）。</summary>
        public void Reset(RoadState state)
        {
            if (_lease != null)
            {
                try { _lease.Dispose(); } catch { /* 释放失败忽略 */ }
                _lease = null;
            }
            _attacking = null;
            _leaseAction = "";
            _entities.Clear();
            _lastNpcSpawnX = double.MinValue;
            _lastMobSpawnX = double.MinValue;
            _lastSeenMaxColX = double.MaxValue;
        }

        /// <summary>
        /// 单帧推进（EndTick 内调用）：
        /// 第一步 新柱生成 roll → 第二步 实体状态机推进 → 第三步 攻击触发/命中帧/租约收招 →
        /// 第四步 回收出屏与死亡实体 → 第五步 输出不可变快照列表。
        /// </summary>
        /// <param name="dtRoadMs">路时钟 delta（暂停=0：实体帧/时间线全冻结）</param>
        /// <param name="columns">激活柱只读视图（新柱检测 + 生成表面）</param>
        /// <param name="scrollX">视口左缘世界 x</param>
        /// <param name="windowW">路窗口宽</param>
        /// <param name="state">RoadState（CombatDisabled/WeaponActionUnavailable 可观测注记）</param>
        public List<RoadEntityState> Tick(double dtRoadMs, IReadOnlyList<RoadColumn> columns, double scrollX, double windowW, RoadState state)
        {
            // 第一步：新柱入路 roll 生成（镜像方向 F18 2026-08-29：新柱自屏幕左缘外侧预生成区
            // 进入、WorldX 递减——检测按「最小柱 WorldX 递减」判定；旧实现按最大值递增，
            // 镜像方向下最大值只减不增 → 除首帧外永不触发生成 = NPC/MOB 完全不展示的根因一）
            if (columns != null && columns.Count > 0)
            {
                double maxColX = double.MinValue;
                foreach (var c in columns)
                {
                    if (c.WorldX > maxColX)
                    {
                        maxColX = c.WorldX;
                    }
                }
                if (maxColX > _lastSeenMaxColX)
                {
                    foreach (var c in columns)
                    {
                        // 只处理本次新入路的柱（WorldX 大于上次已见最大值 = 右缘新补柱——右行语义）
                        if (c.WorldX <= _lastSeenMaxColX)
                        {
                            continue;
                        }
                        TrySpawnOnColumn(c, scrollX, windowW, state);
                    }
                    _lastSeenMaxColX = maxColX;
                }
            }

            // 第二步：实体状态机（RoadClock 驱动）
            double petWorldX = scrollX + windowW / 2;
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                AdvanceEntity(e, dtRoadMs, petWorldX, state);
            }

            // 第三步：回收（出左缘随柱销毁 / 死亡终态）
            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                var e = _entities[i];
                // 镜像方向 F18 2026-08-29：镜头左移 = 实体在屏幕上向右漂移、自「右缘」出屏——
                // 旧判定按左缘出屏（WorldX+ColW < scrollX-余量），镜像下新生成柱（左缘外预生成区）
                // 同帧即被判「出屏左」→ 实体生成当帧即被回收 = NPC/MOB 完全不展示的根因二
                bool offscreen = e.WorldX + RoadConstants.ColW < scrollX - RoadConstants.RecycleMargin; // 427f：右行语义 = 实体向左漂出屏 → 左缘回收
                if (e.Phase == RoadEncounterPhase.Dead || offscreen)
                {
                    if (ReferenceEquals(_attacking, e))
                    {
                        // 攻击中实体被回收（理论仅出屏，防御性释放租约）
                        CancelEncounter();
                    }
                    _entities.RemoveAt(i);
                }
            }

            // 第四步：快照输出（含帧号；位图由渲染侧从条带缓存按 LifeId/Action/FrameIndex 取）
            var snapshot = new List<RoadEntityState>(_entities.Count);
            foreach (var e in _entities)
            {
                snapshot.Add(new RoadEntityState(e.IsMob, e.LifeId, e.MapId, e.WorldX, e.FeetY, e.Action, e.FrameIndex, e.Alpha, e.FacingLeft));
            }
            return snapshot;
        }

        // ====== private ======

        /// <summary>新柱生成 roll：概率 + 冷却（柱数）+ 同屏上限 + ±60px 防冲突（镜像方向：右缘左侧任意新柱均有效）。</summary>
        private void TrySpawnOnColumn(RoadColumn col, double scrollX, double windowW, RoadState state)
        {
            double rightEdge = scrollX + windowW;
            // 生成位置约束（427f 修复，右行语义）：FINAL「运动方向前方 1~2 块生成」——右缘外 ≤4 柱缓冲允许
            if (col.WorldX > rightEdge + RoadConstants.ColW * 4)
            {
                return;
            }
            // 防冲突：柱 x±60px 内已有其他实体 → 跳过（站地 obj 由柱模板绑定约束近似：有绑定装饰的柱不生成）
            foreach (var e in _entities)
            {
                if (Math.Abs(e.WorldX + RoadConstants.ColW / 2 - (col.WorldX + RoadConstants.ColW / 2)) <= RoadConstants.EntityClearRadius)
                {
                    return;
                }
            }
            if (col.Template.Objs.Count > 0)
            {
                // 该柱已绑定站地/装饰 obj → 让位（±60px 防冲突的模板侧近似）
                return;
            }
            int aliveNpc = 0;
            int aliveMob = 0;
            foreach (var e in _entities)
            {
                if (e.Phase == RoadEncounterPhase.Dead)
                {
                    continue;
                }
                if (e.IsMob)
                {
                    aliveMob++;
                }
                else
                {
                    aliveNpc++;
                }
            }
            // NPC：1/10 + 冷却 6 柱 + 上限 3（仅 stand）
            if (aliveNpc < RoadConstants.NpcMaxScreen
                && col.WorldX - _lastNpcSpawnX >= RoadConstants.NpcCooldown * RoadConstants.ColW
                && _rng.NextDouble() < RoadConstants.NpcP)
            {
                var cand = PickLifeCandidate(isMob: false);
                if (cand != null)
                {
                    _entities.Add(new RoadEntityRuntime
                    {
                        IsMob = false,
                        LifeId = cand.Id,
                        MapId = _poolMapId,
                        WorldX = col.WorldX,
                        FeetY = col.SurfaceY + cand.Dy,
                        Action = "stand",
                        FacingLeft = false,
                    });
                    _lastNpcSpawnX = col.WorldX;
                    Console.Error.WriteLine($"[Road] NPC 生成 id={cand.Id} @x={col.WorldX:F0}（累计={_entities.Count}）");
                    return;
                }
            }
            // 怪：1/6 + 冷却 8 柱 + 上限 2（move+hit1+die1 齐备）
            if (aliveMob < RoadConstants.MobMaxScreen
                && col.WorldX - _lastMobSpawnX >= RoadConstants.MobCooldown * RoadConstants.ColW
                && _rng.NextDouble() < RoadConstants.MobP)
            {
                var mobCand = PickLifeCandidate(isMob: true);
                if (mobCand != null)
                {
                    _entities.Add(new RoadEntityRuntime
                    {
                        IsMob = true,
                        LifeId = mobCand.Id,
                        MapId = _poolMapId,
                        WorldX = col.WorldX,
                        FeetY = col.SurfaceY + mobCand.Dy,
                        Action = "move",
                        FacingLeft = false,
                    });
                    _lastMobSpawnX = col.WorldX;
                }
            }
        }

        /// <summary>life 候选过滤：NPC 仅 stand；怪要求 move+hit1+die1 齐备（probe 接通时严格，未接降级放行）。候选池为 pack 级。</summary>
        private LifeCandidate? PickLifeCandidate(bool isMob)
        {
            foreach (var lc in _lifePool)
            {
                if (lc.IsMob != isMob)
                {
                    continue;
                }
                if (_stripProbe == null)
                {
                    // D1 条带缓存未落盘：候选验证降级放行（渲染侧取不到帧画空不崩；D5 测试接 probe 后严格）
                    return lc;
                }
                if (!isMob)
                {
                    if (_stripProbe(lc.Id, "stand"))
                    {
                        return lc;
                    }
                    continue;
                }
                if (_stripProbe(lc.Id, "move") && _stripProbe(lc.Id, "hit1") && _stripProbe(lc.Id, "die1"))
                {
                    return lc;
                }
            }
            return null;
        }

        /// <summary>实体状态机推进（帧动画按固定帧时长近似——真实帧数由渲染侧条带 clamp；hit1/die1 停末帧）。</summary>
        private void AdvanceEntity(RoadEntityRuntime e, double dtRoadMs, double petWorldX, RoadState state)
        {
            e.StateMs += dtRoadMs;
            if (!e.IsMob)
            {
                // NPC：站柱 idle 循环（无状态机）
                e.FrameIndex = (int)(e.StateMs / DefaultFrameMs) % EstLoopFrames;
                return;
            }
            switch (e.Phase)
            {
                case RoadEncounterPhase.Approach:
                    e.Action = "move";
                    e.FrameIndex = (int)(e.StateMs / DefaultFrameMs) % EstLoopFrames;
                    if (!e.AttackCancelled && dtRoadMs > 0 && Math.Abs(e.WorldX - petWorldX) <= RoadConstants.AttackRange)
                    {
                        TryStartAttack(e, state);
                    }
                    break;
                case RoadEncounterPhase.Attacking:
                    // 站定等攻击完成（时间线由租约方 AnimService.CurrentAnimInfo/ActionCompleted 驱动，见 Tick 第三步）
                    e.Action = "move";
                    e.FrameIndex = (int)(e.StateMs / DefaultFrameMs) % EstLoopFrames;
                    break;
                case RoadEncounterPhase.Hit:
                    e.Action = "hit1";
                    e.FrameIndex = Math.Min((int)(e.StateMs / DefaultFrameMs), EstLoopFrames - 1);
                    if (e.StateMs >= RoadConstants.HitFallbackMs)
                    {
                        e.Phase = RoadEncounterPhase.Die;
                        e.StateMs = 0;
                    }
                    break;
                case RoadEncounterPhase.Die:
                    e.Action = "die1";
                    e.FrameIndex = Math.Min((int)(e.StateMs / DefaultFrameMs), EstLoopFrames - 1);
                    if (e.StateMs >= DieDurationMs)
                    {
                        e.Phase = RoadEncounterPhase.Hold;
                        e.StateMs = 0;
                        e.FrameIndex = EstLoopFrames - 1; // 停末帧
                    }
                    break;
                case RoadEncounterPhase.Hold:
                    e.Action = "die1";
                    e.FrameIndex = EstLoopFrames - 1; // 停末帧展示
                    if (e.StateMs >= RoadConstants.DieHoldMs)
                    {
                        e.Phase = RoadEncounterPhase.Fade;
                        e.StateMs = 0;
                    }
                    break;
                case RoadEncounterPhase.Fade:
                    e.Action = "die1";
                    e.FrameIndex = EstLoopFrames - 1;
                    e.Alpha = Math.Max(0, 1.0 - e.StateMs / RoadConstants.DieFadeMs);
                    if (e.Alpha <= 0)
                    {
                        e.Phase = RoadEncounterPhase.Dead;
                    }
                    break;
                case RoadEncounterPhase.Dead:
                    break;
            }
        }

        /// <summary>攻击触发：战斗闸门（IsProviderActive）+ 攻击动作选择器 + 租约 + Play（OneShot）。</summary>
        private void TryStartAttack(RoadEntityRuntime e, RoadState state)
        {
            // 战斗闸门：非纸娃娃（Mob/NPC 宠物）→ 无战斗，怪走过去（不还手）
            if (!_anim.IsProviderActive)
            {
                state.CombatDisabled = true;
                e.AttackCancelled = true;
                return;
            }
            state.CombatDisabled = false;
            string? action = PickAttackAction(out bool cancelledByWeapon);
            if (action == null)
            {
                // 有武器而全候选无帧 → 取消该次遭遇（绝不裸 body）；无武器且拳系无帧同样取消
                if (cancelledByWeapon)
                {
                    state.WeaponActionUnavailable++;
                }
                e.AttackCancelled = true;
                return;
            }
            // 先 Play 后 Acquire（Acquire 在前会被本次 Play 立即打断——同一 UI 线程同步序列无窗口期）
            _anim.Play(action, PlayMode.OneShot);
            _lease = _anim.AcquireCombatLease(action, OnLeaseBroken);
            if (_lease == null)
            {
                // 已有租约在途（理论不可达——本导演独占），防御性取消
                e.AttackCancelled = true;
                return;
            }
            _attacking = e;
            _leaseAction = action;
            e.Phase = RoadEncounterPhase.Attacking;
            e.StateMs = 0;
            e.HitTriggered = false;
        }

        /// <summary>
        /// 攻击动作选择器（武器红线）：AnimService.GetWeaponActionFamily 族候选序 ×
        /// body 可用（GetAvailableActions）× PaperdollService.WeaponHasAction 双帧过滤；
        /// 无武器 → swingO1 拳系（body 有帧才用）。
        /// </summary>
        /// <returns>选中的攻击动作；null = 取消遭遇（cancelledByWeapon 注明是否武器无帧所致）。</returns>
        private string? PickAttackAction(out bool cancelledByWeapon)
        {
            cancelledByWeapon = false;
            List<string> available;
            try
            {
                available = _paperdoll.GetAvailableActions();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadCombat] GetAvailableActions: {ex.Message}");
                return null;
            }
            string? weaponId = null;
            try
            {
                weaponId = _paperdoll.Current?.Weapon?.Id;
            }
            catch { /* 外观读取失败按无武器处理 */ }
            if (string.IsNullOrEmpty(weaponId))
            {
                // 无武器装备 → 拳系 swingO1（body 有帧才用）
                return available.Contains("swingO1") ? "swingO1" : null;
            }
            string family = AnimService.GetWeaponActionFamily(weaponId);
            string[] candidates;
            if (family.Length > 0 && FamilyAttackCandidates.TryGetValue(family, out var famCands))
            {
                candidates = famCands;
            }
            else
            {
                // 族未覆盖（如 170 系无展示帧）→ 全族浅尝一轮（武器有帧即用；全无则取消）
                candidates = FamilyAttackCandidates.Values.SelectMany(v => v).Distinct().ToArray();
            }
            foreach (var cand in candidates)
            {
                bool bodyOk = available.Contains(cand);
                bool weaponOk;
                try
                {
                    weaponOk = _paperdoll.WeaponHasAction(cand);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RoadCombat] WeaponHasAction({cand}): {ex.Message}");
                    weaponOk = false;
                }
                if (bodyOk && weaponOk)
                {
                    return cand;
                }
            }
            // 有武器而全候选无帧 → 取消遭遇（宁可不打不出裸体）
            cancelledByWeapon = true;
            return null;
        }

        /// <summary>租约打断回调（用户显式 Play / PetFollower jump）：作废该次战斗——怪继续走过去。</summary>
        private void OnLeaseBroken(string action)
        {
            _lease = null;
            if (_attacking != null)
            {
                _attacking.AttackCancelled = true;
                _attacking.Phase = RoadEncounterPhase.Approach;
                _attacking.StateMs = 0;
                _attacking = null;
            }
            _leaseAction = "";
        }

        /// <summary>取消在途遭遇（回收/重置用）：释放租约并让怪回归 Approach。</summary>
        private void CancelEncounter()
        {
            if (_lease != null)
            {
                try { _lease.Dispose(); } catch { /* 释放失败忽略 */ }
                _lease = null;
            }
            if (_attacking != null)
            {
                _attacking.AttackCancelled = true;
                _attacking.Phase = RoadEncounterPhase.Approach;
                _attacking = null;
            }
            _leaseAction = "";
        }

        // 实体帧推进近似常量（真实帧数由渲染侧条带 clamp；D1 probe 接通后可细化）
        private const double DefaultFrameMs = 240;   // 对齐 AnimService.GetFrameDelay 兜底
        private const int EstLoopFrames = 4;          // Maple 常见动作 2~4 帧
        private const double DieDurationMs = 720;     // die1 播放期（3 帧 × 240ms）

        /// <summary>
        /// 攻击时间线观察（控制器 EndTick 在 Director.Tick 后调用）：命中帧触发 + 完成/打断判定。
        /// - 命中帧：CurrentAnimInfo 的 FrameIndex/FrameCount ≥ HitFrameRatio(0.5) → 怪切 hit1；
        /// - 完成：攻击动作 ActionCompleted（AnimService 事件）后停末帧——本导演用
        ///   「租约仍在 + 当前帧已到末帧」近似收招完成（停末帧是租约期的确定行为）；
        /// - 打断：OnLeaseBroken 回调已把 _attacking 清空（此处无遭遇即无操作）。
        /// </summary>
        public void ObserveAttack(RoadState state)
        {
            var e = _attacking;
            if (e == null || _lease == null)
            {
                return;
            }
            var info = _anim.CurrentAnimInfo;
            if (info == null || !string.Equals(info.Value.Action, _leaseAction, StringComparison.Ordinal))
            {
                // 动作已被换走但租约未被打断（Stop 等边缘路径）→ 作废遭遇
                CancelEncounter();
                return;
            }
            var (action, frameIndex, frameCount) = info.Value;
            if (frameCount <= 0)
            {
                return;
            }
            if (!e.HitTriggered && (double)frameIndex / frameCount >= RoadConstants.HitFrameRatio)
            {
                // 命中帧：怪受击 hit1
                e.HitTriggered = true;
                e.Phase = RoadEncounterPhase.Hit;
                e.StateMs = 0;
            }
            if (e.HitTriggered && e.Phase == RoadEncounterPhase.Hit && frameIndex >= frameCount - 1)
            {
                // 攻击停末帧（租约期收招）→ 怪进入 hit1 时序（AdvanceEntity 推进到 die1）→ 释放租约恢复 stand
                FinishAttack();
            }
        }

        /// <summary>收招完成：释放租约（Rent 期结束），宠物回 stand（普通 SwitchTo——租约已释放不受抑制）。</summary>
        private void FinishAttack()
        {
            if (_lease != null)
            {
                try { _lease.Dispose(); } catch { /* 释放失败忽略 */ }
                _lease = null;
            }
            if (_attacking != null)
            {
                _attacking = null;
            }
            _leaseAction = "";
            try
            {
                _anim.SwitchTo("stand");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadCombat] StandRestore: {ex.Message}");
            }
        }
    }
}
