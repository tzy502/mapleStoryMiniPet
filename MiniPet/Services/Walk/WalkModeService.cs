using System;
using System.Collections.Generic;
using MiniPet.Models;

namespace MiniPet.Services.Walk
{
    /// <summary>
    /// 行走模式阶段（F18 §1）：Idle → Walking ⇄ Climbing → Crossing → Walking … → Idle。
    /// Crossing 只在一帧内瞬时存在（本类不做动画，窗口层据 <see cref="WalkModeService.MapChanged"/> 做 0.5s 淡入淡出）。
    /// </summary>
    public enum WalkPhase { Idle, Walking, Climbing, Crossing }

    /// <summary>
    /// 行走模式状态机（设计文档 `docs/ai/PLAN-F18-行走与地图切换.md` §1~§7）。
    /// 对齐 sdlMS：单张地图内的行走/攀爬交给 <see cref="MapWalker"/>（physic::walk / run_climb），
    /// 选方向交给 <see cref="WalkPathPlanner.PickDirection"/>（模拟行走取累计长度更长的一边），
    /// 选下一个门交给 <see cref="WalkPathPlanner.ChooseNextPortal"/>；本类只做「状态机 + 切图 + 半程折返」的调度。
    ///
    /// 线程模型（契约 §8）：Tick 由 UI 线程 30fps 驱动，事件同步触发，内部不开线程/定时器。
    /// 可测性：四个服务依赖照契约保留，但真正干活的三件事（载图 / 读地图 BGM / 选起始地图）
    /// 抽成可注入的委托（可选参数），单测无需真读 WZ。
    /// </summary>
    public sealed class WalkModeService
    {
        // ===== 常量 =====
        private const string ActionStand = "stand1";
        private const string ActionWalk1 = "walk1";
        private const string ActionWalk2 = "walk2";
        private const string ActionLadder = "ladder";
        private const string ActionRope = "rope";
        private const string ActionJump = "jump";

        /// <summary>walk1 / walk2 交替周期（§3 默认值：0.4s）。</summary>
        private const double WalkActionSwitchSec = 0.4;

        /// <summary>要求 2：Jump / Fall 跳的视觉过渡时长（不做抛物线物理，纯位移插值）。</summary>
        private const double JumpTransitionSec = 0.45;
        private const double FallTransitionSec = 0.35;

        /// <summary>物理子步长：固定 1/60s 积分（对齐 sdlMS 固定步进），避免大 dt 一帧穿过传送门。</summary>
        private const double SimStepSec = 1.0 / 60.0;

        /// <summary>单次 Tick 最多推进的仿真时长（挂起恢复后不追帧失控）。</summary>
        private const double MaxSimSecPerTick = 1.0;

        /// <summary>§5 到门判定：|Δx| ≤ 12 且 |Δy| ≤ 40。</summary>
        private const double PortalBandX = 12.0;
        private const double PortalBandY = 40.0;

        /// <summary>脱离绳后的同一条绳冷却（秒）：避免「刚离开又立刻上绳」原地循环。</summary>
        private const double RopeCooldownSec = 1.0;

        /// <summary>无入口门时的哨兵（真门 id 是 0 基下标，0 合法，故不能用 0）。</summary>
        private const int NonePortalId = -1;

        // ===== 依赖（契约构造函数保留；委托为可注入扩展点）=====
        private readonly WzService? _wz;
        private readonly MapService? _mapService;
        private readonly PaperdollService? _paperdoll;
        private readonly MusicCatalogService? _catalog;
        private readonly Func<string, MapInfo?> _mapLoader;
        private readonly Func<string, string> _mapBgmOf;
        private readonly Func<string, string?> _mapPicker;

        // ===== 状态 =====
        private bool _active;
        private bool _paused;
        private MapInfo? _map;
        private MapWalker? _walker;
        private string _mapId = string.Empty;
        private string _trackKey = string.Empty;
        private MapPortal? _candidate;
        private bool _noNextPortal;

        /// <summary>本次「腿」的起点（§6 折返的瞬移目标：初始段 + 初始 x）。</summary>
        private int _startFootholdId;
        private float _startX;

        /// <summary>半程折返阈值（当前朝向那条链的累计长度 / 2）与已走距离。</summary>
        private double _halfLen;
        private double _legTraveled;

        /// <summary>本次腿按 <see cref="WalkGraph"/> 规划出的跳序列（空 = 无图路径，退化为沿链行走）。</summary>
        private List<WalkHop> _path = new();

        /// <summary>当前执行到第几跳；_hopIndex >= _path.Count 表示路径走完。</summary>
        private int _hopIndex;

        /// <summary>要求 2：Jump / Fall 的视觉过渡状态（期间不接受新的 WalkStep）。</summary>
        private bool _transitioning;
        private double _transitionElapsed;
        private double _transitionDur;
        private int _transitionTargetFh;
        private float _transitionFromX;
        private float _transitionFromY;
        private float _transitionToX;
        private float _transitionToY;
        /// <summary>过渡期间对外汇报的插值位置（MapWalker 只能在段上定位，空中位移由本类接管）。</summary>
        private float _transitionX;
        private float _transitionY;

        private int _facing = 1;
        private int _climbDir = -1;
        private int _lastRopeId;
        private double _ropeCooldown;
        private double _walkAnimTime;
        private int _walkFrame;
        private string _currentAction = ActionStand;
        private string _pausedAction = ActionStand;
        private string _ladderAction = ActionLadder;
        private string _ropeAction = ActionRope;

        /// <summary>切图时触发（参数 = 新 mapId）；窗口层据此做 0.5s 淡入淡出。</summary>
        public event Action<string>? MapChanged;

        /// <summary>退出行走模式时触发（Stop / Start 选不到图 / 载图失败）。</summary>
        public event Action? Exited;

        /// <param name="wz">WZ 读取（默认实现用它读目标地图 info/bgm）</param>
        /// <param name="mapService">地图服务（默认用它 LoadMap；可空则必须注入 mapLoader）</param>
        /// <param name="paperdoll">纸娃娃（仅用于 ladder/rope 动作名兜底；可空）</param>
        /// <param name="catalog">音乐目录（默认用它 GetFirstMapByReturnMap 选起始图；可空则必须注入 mapPickerOf）</param>
        /// <param name="mapLoader">可选：mapId → MapInfo?，缺省走 mapService.LoadMap</param>
        /// <param name="mapBgmOf">可选：mapId → info/bgm 原始值，缺省走 WZ（`Map/Map/Map{id[0]}/{id}.img/info/bgm`）</param>
        /// <param name="mapPickerOf">可选：trackKey → 起始 mapId，缺省走 catalog.GetFirstMapByReturnMap</param>
        public WalkModeService(WzService wz, MapService mapService, PaperdollService paperdoll, MusicCatalogService catalog,
            Func<string, MapInfo?>? mapLoader = null,
            Func<string, string>? mapBgmOf = null,
            Func<string, string?>? mapPickerOf = null)
        {
            _wz = wz;
            _mapService = mapService;
            _paperdoll = paperdoll;
            _catalog = catalog;
            _mapLoader = mapLoader ?? new Func<string, MapInfo?>(id => _mapService?.LoadMap(id));
            _mapBgmOf = mapBgmOf ?? new Func<string, string>(ReadMapBgmFromWz);
            _mapPicker = mapPickerOf ?? new Func<string, string?>(key => _catalog?.GetFirstMapByReturnMap(key));
        }

        // ===== 公开 API（与契约签名一致）=====

        public bool IsActive => _active;

        public WalkPhase Phase { get; private set; } = WalkPhase.Idle;

        public string CurrentMapId => _mapId;

        /// <summary>世界坐标 x（Jump/Fall 过渡期间为插值位置；无地图时 0）。</summary>
        public float X => _transitioning ? _transitionX : (_walker?.X ?? 0f);

        /// <summary>世界坐标 y（Jump/Fall 过渡期间为插值位置；无地图时 0）。</summary>
        public float Y => _transitioning ? _transitionY : (_walker?.Y ?? 0f);

        /// <summary>+1 右 / -1 左。</summary>
        public int Facing => _facing;

        /// <summary>walk1 / walk2 / stand1 / ladder / rope。</summary>
        public string CurrentAction => _currentAction;

        /// <summary>单测白盒：本次腿规划出的跳序列（只读，要求 4）。</summary>
        internal IReadOnlyList<WalkHop> PlannedHops => _path;

        /// <summary>单测白盒：折返阈值（§6）。</summary>
        internal double HalfLen => _halfLen;
        /// <summary>诊断：当前执行到第几跳 / 路径总跳数。</summary>
        public int CurrentHopIndex => _hopIndex;
        public int PathLength => _path.Count;
        /// <summary>诊断：是否判定「没有下一个可传送的门」（走折返分支）。</summary>
        public bool NoNextPortal => _noNextPortal;

        /// <summary>单测白盒：当前执行到第几跳。</summary>
        internal int HopIndex => _hopIndex;

        /// <summary>
        /// 开始播放音乐 → 进入行走模式（§2 选图 → §3 选方向 → 开走）。
        /// 选不到起始地图 → 触发 Exited 并保持 Idle（§2 回退链由调用方处理）。
        /// </summary>
        public void Start(string trackKey)
        {
            _trackKey = trackKey ?? string.Empty;
            string? mapId = _mapPicker(_trackKey);
            if (string.IsNullOrEmpty(mapId))
            {
                // §2 回退链：曲目反查不到地图（**首次运行反向索引还没建好**时必然如此）→ 用默认城镇，
                // 否则播放音乐永远进不了行走模式（实测就是这个原因）。
                mapId = PickFallbackMap();
                Console.Error.WriteLine($"[WalkMode] 曲目 {_trackKey} 反查无地图 → 回退默认地图 {mapId}");
            }
            if (string.IsNullOrEmpty(mapId))
            {
                Console.Error.WriteLine($"[WalkMode] 曲目 {_trackKey} 连默认地图都取不到，退出行走");
                Reset(exited: true);
                return;
            }
            BeginMap(mapId!, arrival: null);
        }

        /// <summary>默认城镇五图（需求 §2 回退链）；用 trackKey 做稳定散列选一张，避免每次都同一张。</summary>
        private static readonly string[] FallbackMaps =
        {
            "100000000", // 射手村
            "103000000", // 废都（Kerning City）
            "102000000", // 勇士部落
            "101000000", // 魔法密林
            "104000000", // 彩虹岛（原写 "105040300" 不存在，probe 实测 FindNodeByPath=null → 会回退到空图）
        };

        private string PickFallbackMap()
        {
            if (string.IsNullOrEmpty(_trackKey)) { return FallbackMaps[0]; }
            int h = 0;
            foreach (char c in _trackKey) { h = (h * 31 + c) & 0x7fffffff; }
            return FallbackMaps[h % FallbackMaps.Length];
        }

        /// <summary>
        /// 30fps 外部驱动。大 dt 会拆成 1/60s 子步积分（每步都做门/绳判定，不会一帧穿门）。
        /// </summary>
        public void Tick(double dt)
        {
            if (!_active || _paused || _walker == null || _map == null) { return; }
            if (dt <= 0) { return; }
            if (Phase != WalkPhase.Walking && Phase != WalkPhase.Climbing) { return; }

            double remaining = Math.Min(dt, MaxSimSecPerTick);
            while (remaining > 1e-6 && _active)
            {
                if (Phase != WalkPhase.Walking && Phase != WalkPhase.Climbing) { break; }
                double step = Math.Min(remaining, SimStepSec);
                remaining -= step;
                if (!StepOnce(step)) { break; }
            }
        }

        /// <summary>停止播放（不含暂停）→ Idle + Exited（§1）。</summary>
        public void Stop()
        {
            Reset(exited: true);
        }

        /// <summary>暂停：保持不动（Phase 不变，Tick 空转）。</summary>
        public void Pause()
        {
            if (!_active) { return; }
            _paused = true;
            _pausedAction = _currentAction;
            _currentAction = ActionStand;
        }

        /// <summary>恢复暂停前的动作与阶段。</summary>
        public void Resume()
        {
            if (!_active) { return; }
            _paused = false;
            _currentAction = _pausedAction;
        }

        /// <summary>
        /// 歌曲播完（§7）：立即停（Phase 短暂 Idle）→ 置 Crossing，窗口层据此淡出；
        /// 「下一首」的选图由调用方随后 Start(trackKey) 触发（本方法拿不到下一首曲目）。
        /// </summary>
        public void OnTrackFinished()
        {
            if (!_active) { return; }
            Phase = WalkPhase.Idle;
            _walker = null;
            _paused = false;
            _transitioning = false;
            _path.Clear();
            _hopIndex = 0;
            _currentAction = ActionStand;
            Phase = WalkPhase.Crossing;
        }

        // ===== 内部：切图 / 建腿 =====

        /// <summary>
        /// 载入地图并进入 Walking（§5 固定顺序：Phase=Crossing → MapChanged(新图) → 载图 → 定入口门 → 选方向）。
        /// </summary>
        /// <param name="arrival">null = Start（起点取地图中点）；非 null = 从该门跨图过来的（入口门按 Tn 匹配）</param>
        private bool BeginMap(string mapId, MapPortal? arrival)
        {
            Phase = WalkPhase.Crossing;
            _walker = null;
            _currentAction = ActionStand;
            MapChanged?.Invoke(mapId);

            var map = _mapLoader(mapId);
            if (map == null)
            {
                Console.Error.WriteLine($"[WalkMode] 地图 {mapId} 载入失败，退出行走");
                Reset(exited: true);
                return false;
            }
            _map = map;
            _mapId = mapId;

            float px, py;
            int entranceId;
            if (arrival == null)
            {
                // §2：Start 的起点 = 本图的「入口传送门」（优先可跨图的门中最左者；没有则退回地图中点）。
                // 用入口门当起点才符合「从门进来 → 沿路走到下一个门」；用地图中点会吸附到无关的短链上。
                var entryPortal = PickEntryPortal(map);
                var (cx, cy) = entryPortal != null ? (entryPortal.X, entryPortal.Y) : MapService.GetMapCenter(map);
                px = cx;
                py = cy;
                entranceId = NearestPortal(map, cx, cy)?.Id ?? NonePortalId;
            }
            else
            {
                // §5：入口门 = 新图里 Tn 匹配 pname 的那个；找不到退化为坐标最近的
                var entrance = MatchEntrancePortal(map, arrival);
                px = entrance?.X ?? arrival.X;
                py = entrance?.Y ?? arrival.Y;
                entranceId = entrance?.Id ?? NonePortalId;
            }
            return SetupLeg(map, entranceId, px, py);
        }

        /// <summary>
        /// §3/§5 建「一条腿」（F18 图规划版）：
        ///   ① 入口段 = 入口传送门坐标吸附到的最近 foothold（点到线段距离最小，对齐 sdlMS 的落地吸附）；
        ///   ② 有候选门 → 目标段 = 门坐标吸附的最近段，<see cref="WalkGraph.Plan"/> 出跨链最短路（含爬绳 / 跳 / 落）；
        ///      Plan 返回 null（不可达）→ 当作没有候选门处理（要求 1）；
        ///   ③ 无候选门 / 不可达 → 目标 = <see cref="WalkGraph.Farthest"/>（可达集内最远段），
        ///      _halfLen = 该路径总 Cost 的一半（§6 折返阈值；图路径被 Jump 捷径压得过短时与旧链长口径取较大者）。
        /// </summary>
        private bool SetupLeg(MapInfo map, int entrancePortalId, float px, float py)
        {
            var best = NearestFoothold(map, px, py);
            if (best == null)
            {
                Console.Error.WriteLine($"[WalkMode] 地图 {map.Id} 没有可用 foothold，退出行走");
                Reset(exited: true);
                return false;
            }

            _startFootholdId = best.Id;
            _startX = Math.Clamp(px, Math.Min(best.X1, best.X2), Math.Max(best.X1, best.X2));

            _walker = new MapWalker(map, best.Id);
            _walker.PlaceOn(best.Id, _startX);

            var (forward, _, _) = WalkPathPlanner.PickDirection(map, best.Id);
            _facing = FacingOf(map, best.Id, forward);
            _candidate = WalkPathPlanner.ChooseNextPortal(map, entrancePortalId, _trackKey, _mapBgmOf);
            _noNextPortal = WalkPathPlanner.HasNoNextPortal(map, entrancePortalId, _trackKey, _mapBgmOf);

            _path.Clear();
            _hopIndex = 0;
            _halfLen = 0;

            if (_candidate != null)
            {
                // §5 有候选门：目标段 = 门坐标吸附到的最近段
                var goal = NearestFoothold(map, _candidate.X, _candidate.Y);
                var planned = goal != null ? WalkGraph.Plan(map, best.Id, goal.Id) : null;
                if (planned != null)
                {
                    _path.AddRange(planned);
                }
                else
                {
                    // 要求 1：Plan 返回 null（跨链不可达）→ 当作没有候选门，退回 §6 折返
                    _candidate = null;
                    _noNextPortal = true;
                }
            }

            if (_candidate == null)
            {
                // §6 无候选门 / 不可达：目标 = 可达集内最远段
                var far = WalkGraph.Farthest(map, best.Id);
                var planned = far != null ? WalkGraph.Plan(map, best.Id, far.Value.FootholdId) : null;
                if (planned != null) { _path.AddRange(planned); }
                // 折返阈值 = 路径总 Cost / 2。注意图里「同链相邻段」之间恒有极便宜的 Jump 边（代价 ~20，
                // 远小于整段长度），会把路径 Cost 压得很短，直接用会「刚迈步就瞬移折返」；
                // 故与旧口径（入口段所在链的可走长度 / 2）取较大者，保留 §6「走到一半」语义。
                _halfLen = Math.Max(PathCost(_path) / 2.0, HalfLegLength(map, best.Id, _facing));
            }

            _transitioning = false;
            _transitionElapsed = 0;
            _transitionDur = 0;
            _legTraveled = 0;
            _walkAnimTime = 0;
            _walkFrame = 0;
            _ropeCooldown = 0;
            _lastRopeId = 0;
            _paused = false;
            // 不要在这里重置成 stand：每次重规划（折返/换图）都会让动画「停一下再走」。
            // 保持当前行走动作，由逐帧的 walk1/walk2 交替继续驱动。
            ResolveActions();

            // 朝向按**图规划的首跳方向**重定：单链口径的 PickDirection 会把「本链最长」当成答案，
            // 而实际要走的是 WalkGraph 规划出的那条路（实测会出现「选了第二长」）。
            if (_path.Count > 0)
            {
                var first = FindFoothold(map, _path[0].FootholdId);
                if (first != null)
                {
                    float c = (first.X1 + first.X2) / 2f;
                    if (Math.Abs(c - _startX) > 1)
                    {
                        _facing = c > _startX ? 1 : -1;
                    }
                }
            }

            Phase = WalkPhase.Walking;
            _active = true;
            return true;
        }

        /// <summary>§5 走到候选门 → 淡入淡出切图（切图流程见 BeginMap）。</summary>
        /// <summary>切图冷却：防止「刚进新图，人物还贴着入口门」被立刻判定再次到门 → 同图切图风暴。</summary>
        private const double PortalCooldownSec = 1.5;
        private double _portalCooldown;

        private void CrossToPortal(MapPortal portal)
        {
            if (string.IsNullOrEmpty(portal.Tm)) { return; }
            // 同图不切（图数据里存在 tm 指向自己的门，如 101000000 的 ph00；否则会原地反复切）
            if (string.Equals(portal.Tm, _mapId, StringComparison.Ordinal))
            {
                _portalCooldown = PortalCooldownSec;
                return;
            }
            Console.WriteLine($"[WalkMode] 走到传送门 {portal.Id}({portal.PName}) → 切图 {portal.Tm}");
            _portalCooldown = PortalCooldownSec;
            BeginMap(portal.Tm, portal);
        }

        // ===== 内部：单步 =====

        /// <summary>推进一个物理子步。</summary>
        /// <returns>false = 本次 Tick 的剩余子步不再推进（上绳 / 过渡完成 / 切图 / 折返 / 退出）。</returns>
        private bool StepOnce(double step)
        {
            if (_map == null || _walker == null) { return false; }
            if (_ropeCooldown > 0) { _ropeCooldown -= step; }
            if (_portalCooldown > 0) { _portalCooldown -= step; }

            // 要求 2：Jump / Fall 视觉过渡期间不接受新的 WalkStep，只推进插值
            if (_transitioning) { return AdvanceTransition(step); }

            if (Phase == WalkPhase.Climbing)
            {
                int ropeId = _walker.RopeId;
                _walker.ClimbStep((float)step, _climbDir);
                if (_walker.OnRope) { return true; }
                // 到端头自动脱离（ClimbStep 内部已 LeaveRope）→ 回到行走；本 Tick 剩余子步丢弃
                Phase = WalkPhase.Walking;
                _ropeCooldown = RopeCooldownSec;
                _lastRopeId = ropeId;
                _legTraveled = 0;
                _walkAnimTime = 0;
                _currentAction = ActionWalk1;
                // 要求 2：Rope 跳爬到端头 LeaveRope 后 _hopIndex++
                if (_hopIndex < _path.Count && _path[_hopIndex].Link == WalkLink.Rope) { _hopIndex++; }
                SyncHops();
                return false;
            }

            if (Phase != WalkPhase.Walking) { return false; }

            var hop = CurrentHop();
            WalkLink link = hop == null ? WalkLink.Adjacent : ResolveLink(_map, _walker.FootholdId, hop);

            if (hop != null && link == WalkLink.Rope)
            {
                // §4 Rope 跳：真实攀爬——先沿链走到绳附近，RopeNearby + EnterRope(ropeId)，再 ClimbStep 到端头
                var rope = FindRope(_map, hop.RopeId);
                var near = _walker.RopeNearby();
                bool onTargetRope = rope != null && near != null && near.Id == rope.Id;
                bool justLeftThisRope = onTargetRope && rope!.Id == _lastRopeId && _ropeCooldown > 0;
                if (onTargetRope && !justLeftThisRope)
                {
                    int climbDir = ClimbDirection(_walker.Y, rope!);
                    if (_walker.EnterRope(rope!.Id))
                    {
                        _climbDir = climbDir;
                        Phase = WalkPhase.Climbing;
                        _currentAction = rope.IsLadder ? _ladderAction : _ropeAction;
                        return false;
                    }
                }
                if (rope != null)
                {
                    // 还没到绳：朝绳的 x 走（必要时转身）
                    int toward = Math.Sign(rope.X - _walker.X);
                    if (toward != 0) { _facing = toward; }
                }
            }
            else if (hop == null)
            {
                // §4 传统「走到绳附近自动上绳」只在没有图路径的兜底行走时保留（有路径时由 Rope 跳驱动，不乱上绳）
                var rope = _walker.RopeNearby();
                bool justLeftThisRope = rope != null && rope.Id == _lastRopeId && _ropeCooldown > 0;
                if (rope != null && !justLeftThisRope && FacingTowardRope(rope))
                {
                    int climbDir = ClimbDirection(_walker.Y, rope);
                    if (_walker.EnterRope(rope.Id))
                    {
                        _climbDir = climbDir;
                        Phase = WalkPhase.Climbing;
                        _currentAction = rope.IsLadder ? _ladderAction : _ropeAction;
                        return false;
                    }
                }
            }

            // 要求 2：Jump / Fall 不做真实抛物线，从当前位置到目标段「离当前点最近的端点」做视觉过渡
            if (hop != null && link == WalkLink.Jump) { return StartTransition(hop, JumpTransitionSec); }
            if (hop != null && link == WalkLink.Fall) { return StartTransition(hop, FallTransitionSec); }

            // §3 行走：dirInput = Facing（MapWalker 内部做加速/摩擦/贴线/切段）
            float prevX = _walker.X;
            bool blocked = _walker.WalkStep((float)step, _facing);
            _legTraveled += Math.Abs(_walker.X - prevX);
            // 走过的段若就是后面某一跳的目标 → 推进 _hopIndex（连推，处理沿链一次跨多段）
            SyncHops();

            // walk1 / walk2 按累计时间每 0.4s 交替
            _walkAnimTime += step;
            if (_walkAnimTime >= WalkActionSwitchSec)
            {
                _walkAnimTime -= WalkActionSwitchSec;
                _walkFrame ^= 1;
            }
            _currentAction = _walkFrame == 0 ? ActionWalk1 : ActionWalk2;

            // §5 到候选传送门 → 切图：
            //   ① 顺路经过门（坐标落进门周围的判定带）；
            //   ② 已走完 WalkGraph 规划出的整条路径（终点段 = 门吸附到的段，坐标可能与门差几十像素，
            //      光靠 ① 会永远触发不了 → 这里也算到达）。
            var cand = _candidate;
            bool reachedByBand = cand != null && _portalCooldown <= 0
                && Math.Abs(_walker.X - cand.X) <= PortalBandX
                && Math.Abs(_walker.Y - cand.Y) <= PortalBandY;
            // ⚠️ 必须要求 _path.Count > 0：空路径（起点段 == 目标段）时 0 >= 0 恒真 →
            // 每帧都会判定「到门」→ 切图风暴（实测 183 次 MapChanged / 8 秒）。
            bool reachedByPathEnd = cand != null && _portalCooldown <= 0 && !_transitioning
                && _path.Count > 0 && _hopIndex >= _path.Count;
            if (cand != null && (reachedByBand || reachedByPathEnd))
            {
                CrossToPortal(cand);
                return false;
            }

            // §6 路径走完仍没有下一个门 → 走到半程瞬移回起点 + 反向（循环）
            if (_noNextPortal && _halfLen > 1 && _legTraveled >= _halfLen)
            {
                _walker.PlaceOn(_startFootholdId, _startX);
                _facing = -_facing;
                _legTraveled = 0;
                _halfLen = GraphHalfLen(_map, _startFootholdId, _facing);
                // 折返后必须**重新规划本腿路径**：否则只剩「沿单链兜底走」，在碎片化地图上会原地小晃。
                // 目标仍取可达集内最远段（WalkGraph 口径），于是形成「沿整条可达路线往返」的观感。
                var farAfterTurn = WalkGraph.Farthest(_map, _startFootholdId);
                _path = farAfterTurn != null
                    ? (WalkGraph.Plan(_map, _startFootholdId, farAfterTurn.Value.FootholdId) ?? new List<WalkHop>())
                    : new List<WalkHop>();
                _hopIndex = 0;
                // 起点可能正落在绳/梯上：先冷却，避免「瞬移回来立刻上绳」原地打转
                _ropeCooldown = RopeCooldownSec;
                _transitioning = false;
                return false;
            }

            // 兜底：前方无路（贴边停住）→ 掉头，重新计半程
            if (blocked)
            {
                _facing = -_facing;
                _legTraveled = 0;
                if (_noNextPortal) { _halfLen = GraphHalfLen(_map, _startFootholdId, _facing); }
            }
            return true;
        }

        // ===== 内部：路径推进 / Jump-Fall 过渡（要求 2、4）=====

        /// <summary>当前执行到的那一跳（路径走完 / 无路径 → null，走兜底沿链行走）。</summary>
        private WalkHop? CurrentHop()
        {
            return _hopIndex >= 0 && _hopIndex < _path.Count ? _path[_hopIndex] : null;
        }

        /// <summary>按当前位置推进 _hopIndex（纯函数 <see cref="NextHopIndex"/> 的薄封装）。</summary>
        private void SyncHops()
        {
            _hopIndex = NextHopIndex(_path, _hopIndex, _walker?.FootholdId ?? 0);
        }

        /// <summary>
        /// 推进下标（要求 4 的纯函数边界）：当前段若正好是第 index 跳的目标，就推进到下一个未完成的跳。
        /// </summary>
        internal static int NextHopIndex(IReadOnlyList<WalkHop> hops, int index, int currentFootholdId)
        {
            int i = index;
            for (int guard = 0; guard < 64 && i < hops.Count; guard++)
            {
                if (hops[i].FootholdId != currentFootholdId) { break; }
                i++;
            }
            return i;
        }

        /// <summary>
        /// 归一化「本跳怎么过去」（要求 4 的纯函数边界）：
        /// 图里同链相邻段之间恒有代价 ~20 的 Jump 边（远小于整段长度），照做会变成「沿连续平台连跳」；
        /// 目标段就是当前段的 prev/next 时一律改走。Rope 保持攀爬（那是规划的真实过法）。
        /// </summary>
        internal static WalkLink ResolveLink(MapInfo map, int fromFootholdId, WalkHop hop)
        {
            if (hop.Link != WalkLink.Jump && hop.Link != WalkLink.Fall) { return hop.Link; }
            var from = FindFoothold(map, fromFootholdId);
            var target = FindFoothold(map, hop.FootholdId);
            if (from == null || target == null) { return hop.Link; }
            // 同链相邻 → 走
            if (from.Next == hop.FootholdId || from.Prev == hop.FootholdId) { return WalkLink.Adjacent; }
            // 斜坡 / 台阶（**只对 Jump**）：两段端点水平距离在一个瓦片（90px）以内 → 走（沿段线贴地移动）。
            // 胶水规则：走斜坡是「移动」不是「挑」；照 Jump 边做会变成沿斜坡连跳。
            // Fall 不受影响——下落就是下落（动作仍是 jump），把它归成走会丢掉掉落语义。
            if (hop.Link == WalkLink.Jump)
            {
                double dx = Math.Min(Math.Min(Math.Abs(from.X1 - target.X1), Math.Abs(from.X1 - target.X2)),
                                     Math.Min(Math.Abs(from.X2 - target.X1), Math.Abs(from.X2 - target.X2)));
                if (dx <= 90) { return WalkLink.Adjacent; }
            }
            return hop.Link;
        }

        /// <summary>
        /// 要求 2：开始 Jump / Fall 的视觉过渡——从当前位置插值到目标段上「离当前点最近的端点」，
        /// 期间 CurrentAction = "jump"；不做抛物线物理。
        /// </summary>
        private bool StartTransition(WalkHop hop, double dur)
        {
            if (_map == null || _walker == null) { return false; }
            var target = FindFoothold(_map, hop.FootholdId);
            if (target == null) { return false; }
            double d1 = Distance(_walker.X, _walker.Y, target.X1, target.Y1);
            double d2 = Distance(_walker.X, _walker.Y, target.X2, target.Y2);
            float landX = d1 <= d2 ? target.X1 : target.X2;

            _transitioning = true;
            _transitionElapsed = 0;
            _transitionDur = dur;
            _transitionTargetFh = target.Id;
            _transitionFromX = _walker.X;
            _transitionFromY = _walker.Y;
            _transitionToX = landX;
            _transitionToY = MapWalker.YOn(target, landX);
            _transitionX = _transitionFromX;
            _transitionY = _transitionFromY;
            _currentAction = ActionJump;
            return true;
        }

        /// <summary>推进视觉过渡；插值结束 PlaceOn(目标段, 落点x) 并 _hopIndex++（要求 2）。</summary>
        private bool AdvanceTransition(double step)
        {
            if (_map == null || _walker == null)
            {
                _transitioning = false;
                return false;
            }
            _transitionElapsed += step;
            if (_transitionElapsed >= _transitionDur)
            {
                _transitioning = false;
                _walker.PlaceOn(_transitionTargetFh, _transitionToX);
                _legTraveled += Distance(_transitionFromX, _transitionFromY, _walker.X, _walker.Y);
                _currentAction = ActionWalk1;
                if (_hopIndex < _path.Count) { _hopIndex++; }
                SyncHops();
                return false;
            }
            double t = _transitionElapsed / _transitionDur;
            _transitionX = (float)(_transitionFromX + (_transitionToX - _transitionFromX) * t);
            _transitionY = (float)(_transitionFromY + (_transitionToY - _transitionFromY) * t);
            _currentAction = ActionJump;
            return true;
        }

        // ===== 内部：几何 / 判定 =====

        /// <summary>链方向 → 世界朝向：按该方向的净位移 Δx 定符号；纯垂直链（Δx≈0）回退 forward ? +1 : -1。</summary>
        private static int FacingOf(MapInfo map, int footholdId, bool forward)
        {
            var t = MapWalker.Trace(map, footholdId, forward);
            if (t.Dx > 0.5) { return 1; }
            if (t.Dx < -0.5) { return -1; }
            return forward ? 1 : -1;
        }

        /// <summary>§6 半程阈值：当前朝向那条链的累计长度的一半。</summary>
        /// <summary>
        /// 本图的入口传送门：优先「可跨图（tm 有效）」的门里最左者（次选 y 更小者）；
        /// 没有可跨图的门则退化为任意最左的门；全无门 → null（调用方退回地图中点）。
        /// </summary>
        private static MapPortal? PickEntryPortal(MapInfo map)
        {
            MapPortal? best = null;
            foreach (var p in map.Portals)
            {
                if (!p.HasTarget) { continue; }
                if (best == null || p.X < best.X || (p.X == best.X && p.Y < best.Y)) { best = p; }
            }
            if (best != null) { return best; }
            foreach (var p in map.Portals)
            {
                if (best == null || p.X < best.X) { best = p; }
            }
            return best;
        }

        /// <summary>
        /// 折返阈值（图口径）：从起点段出发、沿 <see cref="WalkGraph"/> 能走到的最远段，其路径代价的一半。
        /// 单链口径 <see cref="HalfLegLength"/> 在碎片化地图（魔法密林入口段所在链仅 ~150px）会退化成原地小晃，
        /// 与「走到尽头一半再回头」的规则不符；这里取两者较大者兜底。
        /// </summary>
        private static double GraphHalfLen(MapInfo map, int footholdId, int facing)
        {
            double half = 0;
            var far = WalkGraph.Farthest(map, footholdId);
            if (far != null)
            {
                var path = WalkGraph.Plan(map, footholdId, far.Value.FootholdId);
                if (path != null)
                {
                    double cost = 0;
                    foreach (var h in path) { cost += h.Cost; }
                    half = cost / 2.0;
                }
            }
            return Math.Max(half, HalfLegLength(map, footholdId, facing));
        }

        private static double HalfLegLength(MapInfo map, int footholdId, int facing)
        {
            var a = MapWalker.Trace(map, footholdId, true);
            var b = MapWalker.Trace(map, footholdId, false);
            bool useForward = PickChainForwardForFacing(a, b, facing);
            return (useForward ? a.Len : b.Len) / 2.0;
        }

        /// <summary>朝向 → 链方向：优先取净位移符号与朝向一致的那条；都不一致（或一致）时取更长者。</summary>
        private static bool PickChainForwardForFacing(
            (double Len, double Dx, double Dy, int EndFh) a,
            (double Len, double Dx, double Dy, int EndFh) b,
            int facing)
        {
            bool aMatches = Math.Sign(a.Dx) == facing && Math.Sign(a.Dx) != 0;
            bool bMatches = Math.Sign(b.Dx) == facing && Math.Sign(b.Dx) != 0;
            if (aMatches != bMatches) { return aMatches; }
            return facing >= 0 ? a.Len >= b.Len : b.Len >= a.Len;
        }

        /// <summary>朝向绳：绳在行进方向一侧（|Δx| ≤ 2 视为已站在绳上，允许上绳）。</summary>
        private bool FacingTowardRope(LadderRope rope)
        {
            float dx = rope.X - (_walker?.X ?? 0f);
            if (Math.Abs(dx) <= 2f) { return true; }
            return Math.Sign(dx) == _facing;
        }

        /// <summary>爬向绳的远端（站在下端往上、站在上端往下）——对齐 sdlMS「爬到端头自然脱离」。</summary>
        private static int ClimbDirection(float y, LadderRope rope)
        {
            double toTop = Math.Abs(y - rope.Top);
            double toBottom = Math.Abs(y - rope.Bottom);
            return toTop >= toBottom ? -1 : 1; // -1 向上 / +1 向下
        }

        /// <summary>点到 foothold 线段的距离（§3 吸附口径：点到线段距离最小）。</summary>
        private static double DistanceToSegment(float px, float py, MapFoothold f)
        {
            double x1 = f.X1, y1 = f.Y1, x2 = f.X2, y2 = f.Y2;
            double dx = x2 - x1, dy = y2 - y1;
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : ((px - x1) * dx + (py - y1) * dy) / len2;
            t = Math.Clamp(t, 0, 1);
            double cx = x1 + t * dx, cy = y1 + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        /// <summary>§3 吸附：离 (px,py) 最近的 foothold 段（点到线段距离最小）；没有可用段 → null。</summary>
        private static MapFoothold? NearestFoothold(MapInfo map, float px, float py)
        {
            MapFoothold? best = null;
            double bestD = double.MaxValue;
            foreach (var f in map.Footholds)
            {
                if (f.Id == 0) { continue; }
                double d = DistanceToSegment(px, py, f);
                if (d < bestD)
                {
                    bestD = d;
                    best = f;
                }
            }
            return best;
        }

        /// <summary>按 id 取 foothold（WalkGraph 的 id 口径：Id != 0）。</summary>
        private static MapFoothold? FindFoothold(MapInfo map, int id)
        {
            foreach (var f in map.Footholds)
            {
                if (f.Id == id) { return f; }
            }
            return null;
        }

        /// <summary>按 id 取梯/绳。</summary>
        private static LadderRope? FindRope(MapInfo map, int id)
        {
            foreach (var r in map.Ropes)
            {
                if (r.Id == id) { return r; }
            }
            return null;
        }

        /// <summary>路径总代价（要求 1：折返阈值 = 它的一半）。</summary>
        private static double PathCost(IReadOnlyList<WalkHop> hops)
        {
            double sum = 0;
            for (int i = 0; i < hops.Count; i++) { sum += hops[i].Cost; }
            return sum;
        }

        /// <summary>两点距离（Jump/Fall 过渡落点选择与已走距离统计用）。</summary>
        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>离 (x,y) 最近的传送门（无门 → null）。</summary>
        private static MapPortal? NearestPortal(MapInfo map, float x, float y)
        {
            MapPortal? best = null;
            double bestD = double.MaxValue;
            foreach (var p in map.Portals)
            {
                double dx = p.X - x, dy = p.Y - y;
                double d = dx * dx + dy * dy;
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>§5 新图入口门：Tn 匹配 pname 的那个；找不到退化为离来源门坐标最近的门。</summary>
        private static MapPortal? MatchEntrancePortal(MapInfo map, MapPortal source)
        {
            if (!string.IsNullOrEmpty(source.Tn))
            {
                foreach (var q in map.Portals)
                {
                    if (string.Equals(q.PName, source.Tn, StringComparison.OrdinalIgnoreCase)) { return q; }
                }
            }
            return NearestPortal(map, source.X, source.Y);
        }

        /// <summary>
        /// 读地图 info/bgm（对齐 MusicCatalogService.GetSceneTrackAsync：9 位补零 + `Map/Map/Map{首字符}/…/info/bgm`，
        /// WithWzLock 串行化）；WZ 未加载 / 查不到 / 异常 → ""。
        /// </summary>
        private string ReadMapBgmFromWz(string mapId)
        {
            var wz = _wz;
            if (wz == null || string.IsNullOrWhiteSpace(mapId)) { return string.Empty; }
            try
            {
                string padded = NormalizeMapId(mapId);
                return wz.WithWzLock(() => wz.GetStringProperty($"{MapService.GetMapWzPath(padded)}/info/bgm") ?? string.Empty);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMode] ReadMapBgm({mapId}): {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>纯数字短 id 补零到 9 位（非数字 id 原样，避免拼出错误路径）。</summary>
        private static string NormalizeMapId(string mapId)
        {
            var s = mapId.Trim();
            if (s.Length >= 9) { return s; }
            foreach (char c in s)
            {
                if (!char.IsDigit(c)) { return s; }
            }
            return s.PadLeft(9, '0');
        }

        /// <summary>
        /// ladder / rope 动作名兜底（对齐 PaperdollService.ResolveAction 口径）：
        /// 外观没有该动作时退回 stand1 等真实存在的动作，避免窗口层拿到空动作。
        /// </summary>
        private void ResolveActions()
        {
            var pd = _paperdoll;
            if (pd == null) { return; }
            try
            {
                _ladderAction = pd.HasAction(ActionLadder) ? ActionLadder : pd.ResolveAction(ActionLadder);
                _ropeAction = pd.HasAction(ActionRope) ? ActionRope : pd.ResolveAction(ActionRope);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMode] ResolveActions: {ex.Message}");
                _ladderAction = ActionLadder;
                _ropeAction = ActionRope;
            }
        }

        /// <summary>回 Idle（exited=true 时同步触发 Exited）。</summary>
        private void Reset(bool exited)
        {
            _active = false;
            _paused = false;
            Phase = WalkPhase.Idle;
            _walker = null;
            _candidate = null;
            _noNextPortal = false;
            _portalCooldown = PortalCooldownSec;
            _legTraveled = 0;
            _halfLen = 0;
            _path.Clear();
            _hopIndex = 0;
            _transitioning = false;
            _transitionElapsed = 0;
            _transitionDur = 0;
            _walkAnimTime = 0;
            _walkFrame = 0;
            _ropeCooldown = 0;
            _lastRopeId = 0;
            _currentAction = ActionStand;
            _pausedAction = ActionStand;
            if (exited) { Exited?.Invoke(); }
        }
    }
}
