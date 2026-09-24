using System;
using System.Linq;
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

        /// <summary>
        /// 行走动作（胶水 2026-09-15 拍板）：**默认 walk1**；只有当前武器在 walk1 下无法展示
        /// （WeaponHasAction=false）而 walk2 可以时才用 walk2。选定后整个行走期间**固定**，
        /// 不做 walk1/walk2 周期交替（原 0.4s 交替观感是「动作莫名其妙切换」，已废弃）。
        /// </summary>
        private string _walkAction = ActionWalk1;

        /// <summary>要求 2：Jump / Fall 跳的视觉过渡时长（不做抛物线物理，纯位移插值）。</summary>
        /// <summary>
        /// 跳跃/下坠物理（2026-09-15 重修，胶水：对齐 sdlMS「按住方向键跳跃」）：
        /// 起跳 vspeed=-555、重力 2000、落速上限 +670；空中水平保持惯性 vx=±125（sdlMS 空中变向仅 0.1·dt，
        /// 近似为不变）；**飞到哪落到哪**（扫掠线段与段面求交，取最先穿越者），不再预选落点插值——
        /// 旧实现「人先前移、跳跃又插回身后落点」的倒退观感即源于此。
        /// </summary>
        private const double WalkGravity = 2000.0;              // 重力（sdlMS）
        private const float JumpInitVy = -555f;                 // 起跳初速（sdlMS vspeed_min*0.1）
        private const float FallTerminalVy = 670f;              // 下落终速（sdlMS vspeed_max）
        /// <summary>空中水平速度：sdlMS 行走上限 125 → 同高跳距仅 69px，图内 70~110px 的设计跳口全部
        /// 跳不过、连通性塌（104000000→104010000 门路径实测断裂）。取 200 → 同高跳距 111px 覆盖全部
        /// 110px 跳口；「按住方向键 + 惯性 + 飞到哪落到哪」的 sdlMS 行为模式不变。</summary>
        private const float AirHspeed = 200f;
        private const double AirMaxSec = 2.0;                   // 空中安全上限（防飞出地图不落地）

        /// <summary>物理子步长：固定 1/60s 积分（对齐 sdlMS 固定步进），避免大 dt 一帧穿过传送门。</summary>
        private const double SimStepSec = 1.0 / 60.0;

        /// <summary>单次 Tick 最多推进的仿真时长（挂起恢复后不追帧失控）。</summary>
        private const double MaxSimSecPerTick = 1.0;

        /// <summary>§5 到门判定：|Δx| ≤ 12 且 |Δy| ≤ 40。</summary>
        private const double PortalBandX = 12.0;
        private const double PortalBandY = 40.0;

        /// <summary>脱离绳后的同一条绳冷却（秒）：避免「刚离开又立刻上绳」原地循环。</summary>
        private const double RopeCooldownSec = 1.0;

        /// <summary>F4：兜底顺手上绳的同绳冷却（1s 太短 → 同一根绳上下摆钟，101000000 rope11 实测）。</summary>
        private const double RopeFallbackCooldownSec = 3.0;

        /// <summary>F4：兜底上绳后离开绳 x 超过这个距离才解除「不准再上同一根」的闩。</summary>
        private const float RopeRegrabMinDist = 40f;

        /// <summary>F1b：站得上绳的 x 却够不着绳的 y 时，超过这个时长就降级跳过该绳跳。</summary>
        private const double RopeStuckTimeoutSec = 2.0;

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
        /// <summary>曲目候选图有序列表（规则 4 顺序退图用；缺省走 catalog.GetTrackMapsOrdered，可注入替身）。</summary>
        private readonly Func<string, List<string>?> _trackMapsOrdered;

        // ===== 状态 =====
        private bool _active;
        private bool _paused;
        private MapInfo? _map;
        private MapWalker? _walker;
        private string _mapId = string.Empty;
        private string _trackKey = string.Empty;
        private MapPortal? _candidate;
        private bool _noNextPortal;

        /// <summary>
        /// 死胡同覆盖规则（胶水 2026-09-15 规则 1/3/4，2026-09-15 连环快切修复）：
        /// 无门图（noNext）进入「覆盖模式」——来回走两遍是**正常覆盖行为**（规则 1），但每趟必须完整：
        /// 一次「完整单程」= 真尽头 blocked 且与上次计入 blocked 的水平位移 ≥
        /// max(本腿规划全长/3, <see cref="DeadEndMinLegDist"/>)；梯子端点、小缺口边这类位移不足的
        /// blocked 只掉头重置方向、**不计趟**（922010000 实测：hop 走完 1s 内爬梯到端点 blocked →
        /// 另一端 blocked → turns 秒到 2 → 退图，5 秒连切 4 张图、每张只走一部分，即此病灶）。
        /// 退出条件 = **两个方向各一次完整单程**（<see cref="_deadEndTurns"/> ≥ 2）
        /// **且本图总停留 ≥ <see cref="DeadEndMinStaySec"/>**（防超小图秒退 + MapChanged 狂闪）。
        /// 物理依据（单向性）：跳跃落差可下跳、上跳受 <see cref="WalkGraph.JumpMaxUp"/>(77px) 限——
        /// 天然产生「从左到右走不了但从右到左可以」的单向缺口（地球防御总部），两方向各走一遍才互补覆盖全图。
        /// <see cref="DeadEndReturnSec"/> 仅作「30s 无完整单程进展」的卡死兜底。
        /// </summary>
        private const double DeadEndReturnSec = 30.0;
        /// <summary>完整单程的最小水平位移（胶水：全长/3 与 300px 取大者——常量即 300px 下限）。</summary>
        private const double DeadEndMinLegDist = 300.0;
        /// <summary>死胡同图最短停留：两趟走完也要待够才退图（防秒退快切 + 淡入淡出狂闪）。</summary>
        private const double DeadEndMinStaySec = 20.0;
        private double _deadEndSec;
        /// <summary>本图累计停留（只累计，BeginMap 清零；退图判定用，不受进度清零影响）。</summary>
        private double _deadEndStaySec;
        private int _deadEndTurns;
        /// <summary>上次「计入的 blocked」位置 x（完整单程位移判定的参照点；BeginMap 时 = 入口 x）。</summary>
        private float _lastTurnX;
        private string _sessionStartMapId = string.Empty;
        /// <summary>来源图（穿门进入时的上一张图，退图回退链用）。</summary>
        private string _cameFromMapId = string.Empty;

        /// <summary>
        /// 本图内已用过的同图传送门（防 up00↔down00 乒乓；进图清空）。只作「本图访问用过哪些门」的
        /// 记录；执行闸门是 <see cref="_selfWarpCooldownUntil"/> 的 10s 冷却（永久封禁会把
        /// 「传下去后回上层」也堵死，706030400 实测：进图从树顶传到底部后上层永不再覆盖）。
        /// </summary>
        private readonly HashSet<int> _usedSelfPortals = new();

        /// <summary>
        /// 同图传送边冷却：门 id → 冷却到期时刻（<see cref="_simClock"/> 口径）。传送成功时**双向都标**
        /// （源门 + 目标门）——落地就在目标门口，不标反向会立刻被重规划选回、原地弹回（A→B→A 乒乓）。
        /// 同一条边 10s 内禁用、到期自动恢复（上层走完下得来）；其它传送边不受影响。
        /// </summary>
        private const double SelfWarpCooldownSec = 10.0;
        private readonly Dictionary<int, double> _selfWarpCooldownUntil = new();
        /// <summary>会话累计仿真时钟（秒，StepOnce 逐子步累加）：传送冷却的计时基准（随仿真推进，非墙钟）。</summary>
        private double _simClock;

        /// <summary>
        /// 贪心遍历（胶水 2026-09-15）：本次行走已走过的图。选门时优先「没去过的图」；
        /// 当前图所有可去的图都去过 → 视作死胡同退图回初始地图并**清空**（死胡同图除外），开始新一轮遍历。
        /// </summary>
        private readonly HashSet<string> _visitedMaps = new(StringComparer.Ordinal);

        /// <summary>
        /// 确认过的死胡同图（触发过死胡回退图的）：跨轮保留在 _visitedMaps 里不再优先去——
        /// 否则每轮清空后「最远的死胡同图」永远第一站（100000001 实测：000↔001 卡死循环）。
        /// 新会话（换曲目）清空：noNext 判定依赖目标图 BGM，换曲后同一张图可能不再死。
        /// </summary>
        private readonly HashSet<string> _deadEndMaps = new(StringComparer.Ordinal);

        /// <summary>当前图内已走过的段（贪心「每条路尽量走一次」：规划时对已走段加罚，优先探索新段）。</summary>
        private readonly HashSet<int> _walkedFh = new();
        /// <summary>
        /// 已走段的规划罚金 + 冷却中传送门的门口段罚金（<see cref="WarpCooldownPenalty"/>）：
        /// 后者让 Dijkstra 在传送冷却期内不再把这条传送边当最短路——否则「传送落地 → 重规划又选中
        /// 同一条边（cost 60 最划算）→ 走回门口」反复空转，是全量低覆盖的第一大杀手（326000000 等
        /// 覆盖率 0.002 实测）。罚金高额但有限：传送边是唯一通路时仍会被选中，执行层届时按冷却跳过，
        /// 冷却到期自然恢复通行。
        /// </summary>
        private double WalkedPenalty(int fhId) => (_walkedFh.Contains(fhId) ? 400 : 0) + WarpCooldownPenalty(fhId);

        /// <summary>冷却中传送门的门口段罚金：门口段在冷却期内规划绕行（+5000，见 <see cref="WalkedPenalty"/>）。</summary>
        private const double WarpCooldownPenaltyValue = 5000;

        private double WarpCooldownPenalty(int fhId)
        {
            if (_map == null || _selfWarpCooldownUntil.Count == 0) { return 0; }
            foreach (var p in _map.Portals)
            {
                if (!_selfWarpCooldownUntil.TryGetValue(p.Id, out var until) || _simClock >= until) { continue; }
                var seg = NearestFoothold(_map, p.X, p.Y);
                if (seg != null && seg.Id == fhId) { return WarpCooldownPenaltyValue; }
            }
            return 0;
        }

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

        /// <summary>
        /// 路径失配自救（2026-09-15 魔法密林实测）：hop 长时间不推进（人物来回走）→ 从当前位置
        /// 重新 Plan 到候选门。旧实现会沿失配路径无限掉头走回。
        /// 死胡同覆盖模式（_noNextPortal）下**禁用**：自救重规划会改写来回路径、破坏两遍完整覆盖；
        /// 真失配时掉头机制本身能兜底（胶水 2026-09-15）。
        /// </summary>
        private const double HopStallResec = 8.0;
        /// <summary>覆盖模式（noNext）的 hop 卡死窗口：比候选模式短——覆盖图大、每秒都是覆盖预算，
        /// 遇到执行不了的跳要尽快换目标（盒式净位移判定保证了不会误伤正常长腿）。</summary>
        private const double HopStallCoverResec = 5.0;
        private double _hopStallSec;
        /// <summary>覆盖模式卡死重规划的目标交替标记：近/最远未访段轮换，避免反复复现同一条死路。</summary>
        private bool _coverReplanAlt;
        private int _lastHopIndex = -1;
        /// <summary>上次 hop 推进时的位置：hop 卡死自救的**净位移**参照（原地震荡不算进展）。</summary>
        private float _hopStallAnchorX;
        private float _hopStallAnchorY;
        private double _posStallSec;
        private float _lastStallX = float.NaN;
        private float _lastStallY = float.NaN;
        /// <summary>同一 hop 的起跳尝试计数（B3：连续 3 次跳完 hop 不推进 → 放弃候选门）。</summary>
        private int _airFailStreak;
        private int _airLastHopIndex = -1;
        /// <summary>跳抓：空中意图抓取的绳 id（0=无）；落地未抓 → _ropeCatchFails++。</summary>
        private int _airCatchRope;
        private int _ropeCatchFails;

        /// <summary>撞墙转身停顿（胶水「撞墙回头很突兀」）：blocked 后先站住 0.4s 再掉头。</summary>
        private const double TurnPauseSec = 0.4;
        private double _turnPauseSec;
        private bool _pendingTurnDeadEnd;

        /// <summary>
        /// 死胡同「中途 blocked」的原位重规划冷却（秒）：图上前方仍有可达段但物理走不过去时，
        /// 原位重规划找回计划内过缺口的方式；冷却防止 Rope 类无法执行的跳每子步反复 Dijkstra。
        /// </summary>
        private const double MidBlockedReplanCdSec = 2.0;
        private double _midBlockedReplanCd;

        /// <summary>空中状态（跳跃/下坠物理积分中，期间不接受新的 WalkStep）。</summary>
        private bool _transitioning;
        private double _airElapsed;
        private float _airVy;
        private float _airVx;
        private float _airFromX;
        private float _airFromY;
        private int _airFromFh;
        private int _airHopIndex = -1;
        private int _transitionTargetFh;
        /// <summary>空中时对外汇报的物理位置（MapWalker 只能在段上定位，空中位移由本类接管）。</summary>
        private float _transitionX;
        private float _transitionY;

        private int _facing = 1;
        private int _climbDir = -1;
        private int _lastRopeId;
        private float _lastRopeX;
        private bool _ropeRegrabArmed;
        private bool _ropeFromPlanned = true;
        private double _ropeStuckSec;
        private double _ropeCooldown;
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
        /// <param name="trackMapsOrderedOf">可选：trackKey → 候选图有序列表（规则 4 顺序退图），缺省走 catalog.GetTrackMapsOrdered</param>
        public WalkModeService(WzService wz, MapService mapService, PaperdollService paperdoll, MusicCatalogService catalog,
            Func<string, MapInfo?>? mapLoader = null,
            Func<string, string>? mapBgmOf = null,
            Func<string, string?>? mapPickerOf = null,
            Func<string, List<string>?>? trackMapsOrderedOf = null)
        {
            _wz = wz;
            _mapService = mapService;
            _paperdoll = paperdoll;
            _catalog = catalog;
            _mapLoader = mapLoader ?? new Func<string, MapInfo?>(id => _mapService?.LoadMap(id));
            _mapBgmOf = mapBgmOf ?? new Func<string, string>(ReadMapBgmFromWz);
            _mapPicker = mapPickerOf ?? new Func<string, string?>(key => _catalog?.GetFirstMapByReturnMap(key));
            _trackMapsOrdered = trackMapsOrderedOf ?? new Func<string, List<string>?>(key => _catalog?.GetTrackMapsOrdered(key));
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

        /// <summary>诊断：当前所在 foothold 段 id。</summary>
        public int CurrentFootholdId => _walker?.FootholdId ?? 0;

        /// <summary>设置来源图（死胡同「原路返回」目标）。用于非穿门的换图（如反向索引就绪后的自动重开）。</summary>
        public void SetCameFrom(string mapId) { _cameFromMapId = mapId; }

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
        /// <summary>当前行走绑定的曲目 key（Start 传入；反向索引延迟就绪后由调用方核对用）。</summary>
        public string CurrentTrackKey => _trackKey;

        /// <summary>单测白盒：当前执行到第几跳。</summary>
        internal int HopIndex => _hopIndex;

        /// <summary>
        /// 开始播放音乐 → 进入行走模式（§2 选图 → §3 选方向 → 开走）。
        /// 选不到起始地图 → 触发 Exited 并保持 Idle（§2 回退链由调用方处理）。
        /// </summary>
        public void Start(string trackKey)
        {
            _trackKey = trackKey ?? string.Empty;
            // 新会话：初始地图重新记录，死胡同计时/圈数清零，贪心遍历重新开始
            //（_deadEndMaps 也清：noNext 依赖目标图 BGM，换曲后同一张图可能不再死）
            _sessionStartMapId = string.Empty;
            _deadEndSec = 0;
            _deadEndTurns = 0;
            _visitedMaps.Clear();
            _deadEndMaps.Clear();
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

            // 死胡同覆盖模式（胶水 2026-09-15 规则 1/3/4 + 连环快切修复）：两方向各一次完整单程
            // 且总停留 ≥ DeadEndMinStaySec 才退图；30s 无完整单程进展仅作卡死兜底。
            if (_noNextPortal)
            {
                double add = Math.Min(dt, MaxSimSecPerTick);
                _deadEndSec += add;
                _deadEndStaySec += add;
                if (_deadEndSec >= DeadEndReturnSec)
                {
                    Console.Error.WriteLine($"[WalkMode] 死胡同 {_mapId} 已 {_deadEndSec:F0}s 无完整单程进展 → 尝试退图");
                    _deadEndMaps.Add(_mapId);
                    ReturnToSessionStartMap();
                    return;
                }
            }
            else
            {
                _deadEndSec = 0;
                _deadEndTurns = 0;
            }

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
            string prevMapId = _mapId;   // 进入前所在的图（穿门时的来源图）
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
            // 会话初始地图 = Start（arrival==null）时选中的第一张图；死胡同超时切回的目标。
            // 只在会话尚未记录时写入（死胡同切回也走 arrival==null 的 BeginMap，不能覆盖）。
            if (string.IsNullOrEmpty(_sessionStartMapId)) { _sessionStartMapId = mapId; }
            // 来源图（胶水「原路返回」）：穿门进入时 = **进入前所在的图**（BeginMap 前的 _mapId）
            if (arrival != null) { _cameFromMapId = prevMapId; }
            _deadEndSec = 0;
            _deadEndStaySec = 0;
            _deadEndTurns = 0;
            _visitedMaps.Add(mapId);
            _walkedFh.Clear();
            _usedSelfPortals.Clear();
            _selfWarpCooldownUntil.Clear();   // 门 id 各图复用，冷却必须随图切换清空
            // 进图最短停留（B1 乒乓秒切修复）：有候选门的图对 A↔B 互指时，进图 <1s 即切回
            // → 90s 内 10+ 次淡入淡出狂闪（919 图实测）。进图冷却 2.5s 内不判定到门。
            _portalCooldown = Math.Max(_portalCooldown, 2.5);

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
        ///   ③ 无候选门 / 不可达 → 方向按「走到尽头的累计长度（含跳跃/爬绳）更长的一边」重选（§3 图口径），
        ///      目标 = 朝向方向「可行走路径尽头」（<see cref="WalkGraph.RouteToEnd"/>），
        ///      _halfLen = 全程的一半（§6 折返阈值；旧口径被单链长/2 与跳跃捷径边压到几百 px → 频繁瞬移）。
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
            // 入口段必须锚定在图内（垂直段被建图排除）：站上去 WalkStep 走不动、Plan 全部静默失败
            if (!WalkGraph.InGraph(map, best.Id))
            {
                int anchorId = WalkGraph.NearestInGraph(map, px, py);
                var anchorFh = FindFoothold(map, anchorId);
                if (anchorFh == null)
                {
                    Console.Error.WriteLine($"[WalkMode] 地图 {map.Id} 没有可用图内 foothold，退出行走");
                    Reset(exited: true);
                    return false;
                }
                best = anchorFh;
            }

            _startFootholdId = best.Id;
            _startX = Math.Clamp(px, Math.Min(best.X1, best.X2), Math.Max(best.X1, best.X2));
            // 完整单程位移判定的参照点 = 入口 x（第一次真尽头 blocked 的位移从进图位置起算）
            _lastTurnX = _startX;

            _walker = new MapWalker(map, best.Id);
            _walker.PlaceOn(best.Id, _startX);

            var (forward, _, _) = WalkPathPlanner.PickDirection(map, best.Id);
            _facing = FacingOf(map, best.Id, forward);
            // 贪心遍历（胶水 2026-09-15）：候选门按「未访问优先 + 从远到近」排序，**逐个尝试 Plan**——
            // 排头的不可达（100000000 的 in04 在 -56 高台、与主街落差 390px 无过渡段，实测 Plan 失败）
            // 就换下一个；全部不可达才 noNext=true（复用死胡同退图链路：走一圈回初始地图重新遍历）。
            _candidate = null;
            var candidates = WalkPathPlanner.OrderCandidates(map, entrancePortalId, _trackKey, _mapBgmOf, _visitedMaps, _walker.X);
            // 规则 4（胶水「死胡同了就按顺序走第二个图 以此类推」）：已知死胡同图的门**非必要不去**；
            // 全部候选都是死胡同图时（无别的路），再允许走死胡同图（覆盖两圈走既定流程）。
            foreach (var pass in new[] { true, false })
            {
                foreach (var cand in candidates)
                {
                    if (pass && _deadEndMaps.Contains(cand.Tm)) { continue; }   // 第一遍跳过已知死胡同图
                    int goalFhId = GoalAnchorFh(cand.X, cand.Y);
                    var planned = goalFhId != 0 ? WalkGraph.Plan(map, best.Id, goalFhId, WalkedPenalty) : null;
                    if (planned == null)
                    {
                        Console.WriteLine($"[setup] 候选 {cand.PName}@{cand.X},{cand.Y}→{cand.Tm} 不可达，换下一个");
                        continue;
                    }
                    _candidate = cand;
                    _path.AddRange(planned);
                    break;
                }
                if (_candidate != null) { break; }
            }
            _noNextPortal = _candidate == null;

            Console.WriteLine($"[setup] 入口段={best.Id}({best.X1},{best.Y1})-({best.X2},{best.Y2}) candidate={(_candidate == null ? "null" : _candidate.PName + "@" + _candidate.X + "," + _candidate.Y + "→" + _candidate.Tm)} noNext={_noNextPortal} 候选数={candidates.Count} path={_path.Count}");

            if (_candidate == null)
            {
                // §3 方向选择（图口径，胶水：累计路径长度含跳跃/爬绳更长的一边）；图上两个方向都
                // 规划不出路（极小图/孤立段）→ 保持单链口径的朝向。
                if (TryPickFacingByRoute(map, best.Id, out int routeFacing)) { _facing = routeFacing; }
                // §6 无候选门 / 不可达：目标 = 面向方向「可行走路径尽头」（WalkGraph 口径，含跳跃/爬绳），
                // 掉头阈值 = 全程（走到尽头才掉头；旧「半程瞬移」的 /2 口径已废弃）。
                // 旧口径（单链长/2 与 Dijkstra 代价/2 取较大者）在碎片化地图上被「跳跃捷径边（代价~20）」
                // 压到 402~502px（104000000 实测）→ 约每 5 秒瞬移一次；新口径全程 ≈2000+px。
                // 死胡同覆盖模式禁用贪心段罚（_walkedFh 刚清空此处本就为空，用 SegmentPenalty 统一口径）。
                if (!PlanLegToTarget(map, best.Id, _facing, _path, out _halfLen, SegmentPenalty))
                {
                    // 尽头 = 起点（无图路径）→ 退回单链口径：沿链走到头的一半
                    _halfLen = HalfLegLength(map, best.Id, _facing);
                }
            }

            _transitioning = false;
            _airElapsed = 0;
            _legTraveled = 0;
            _ropeCooldown = 0;
            _midBlockedReplanCd = 0;
            _lastRopeId = 0;
            _lastRopeX = 0;
            _ropeRegrabArmed = false;
            _ropeFromPlanned = true;
            _ropeStuckSec = 0;
            _paused = false;
            ResolveActions();
            // 新腿开走即处于行走动作（胶水 2026-09-15：切图/换腿后人物不能站着顿一下再走——
            // BeginMap 设的 stand1 会一直保留到下一次动作变化，被 33ms 采样到就是「走到一半变 stand」）。
            _currentAction = _walkAction;

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
            // 同图传送门（SleepyWood 706030400 树图型：up00/down00 tm=自己、tn 互指目标门）：
            // 图内**传送**（PlaceOn 到目标门位置 + 重规划继续遍历），不切图不淡出。
            // Warp hop（StepOnce）是规划主路径；这里只是「路上路过门口」的兜底，两者共用
            // <see cref="ExecuteSelfWarp"/>（含乒乓防护 _usedSelfPortals，统一口径防漂移）。
            if (string.Equals(portal.Tm, _mapId, StringComparison.Ordinal))
            {
                if (!ExecuteSelfWarp(portal)) { _portalCooldown = PortalCooldownSec; }
                return;
            }
            Console.WriteLine($"[WalkMode] 走到传送门 {portal.Id}({portal.PName}) → 切图 {portal.Tm}");
            _portalCooldown = PortalCooldownSec;
            BeginMap(portal.Tm, portal);
        }

        /// <summary>
        /// 执行同图传送（源门 → 其 Tn 指向的目标门）：PlaceOn 到目标门吸附段 + 重规划本腿
        /// （有候选门 → 重规划到候选门；否则按当前朝向重规划「可行走路径尽头」——传送落地的新区域
        /// 由重规划自然纳入覆盖）。返回 false = 门对冷却中（乒乓防护）/ 目标异常，调用方自行降级。
        /// 防护 = 同一条传送边 10s 冷却（<see cref="_selfWarpCooldownUntil"/>，双向同标）：
        /// 冷却期内不传（重规划也由 <see cref="WarpCooldownPenalty"/> 绕开该边）；到期恢复 —— 上层
        /// 走完可以再传回来（永久封禁会让分层图只覆盖单侧，706030400 实测）。
        /// </summary>
        private bool ExecuteSelfWarp(MapPortal portal)
        {
            if (_map == null || _walker == null || string.IsNullOrEmpty(portal.Tn)
                || (_selfWarpCooldownUntil.TryGetValue(portal.Id, out var coolUntil) && _simClock < coolUntil))
            {
                return false;
            }
            MapPortal? dest = null;
            foreach (var q in _map.Portals)
            {
                if (string.Equals(q.PName, portal.Tn, StringComparison.OrdinalIgnoreCase)) { dest = q; break; }
            }
            // 目标门吸附段锚定图内：落到垂直段上 WalkStep 走不动、后续规划全瞎
            var destSegId = dest != null ? GoalAnchorFh(dest.X, dest.Y) : 0;
            var destSeg = destSegId != 0 ? FindFoothold(_map, destSegId) : null;
            if (dest == null || destSeg == null) { return false; }
            Console.WriteLine($"[WalkMode] 同图传送 {portal.PName}({portal.X},{portal.Y}) → {dest.PName}({dest.X},{dest.Y})");
            // 双向同标：落地就在目标门口，只标源门的话重规划立刻选回反向边 → 原地弹回乒乓
            _selfWarpCooldownUntil[portal.Id] = _simClock + SelfWarpCooldownSec;
            _selfWarpCooldownUntil[dest.Id] = _simClock + SelfWarpCooldownSec;
            _usedSelfPortals.Add(portal.Id);
            _usedSelfPortals.Add(dest.Id);
            _walker.PlaceOn(destSeg.Id, Math.Clamp(dest.X, Math.Min(destSeg.X1, destSeg.X2), Math.Max(destSeg.X1, destSeg.X2)));
            _transitionX = _walker.X;
            _transitionY = _walker.Y;
            _legTraveled += 100;
            _path.Clear();
            _hopIndex = 0;
            _hopStallSec = 0;
            _posStallSec = 0;
            if (_candidate != null)
            {
                var goalFhId = GoalAnchorFh(_candidate.X, _candidate.Y);
                var replanned = goalFhId != 0 ? WalkGraph.Plan(_map, PlanAnchorFh(), goalFhId, WalkedPenalty) : null;
                if (replanned != null) { _path.AddRange(replanned); }
            }
            if (_path.Count == 0 && !PlanLegToTarget(_map, PlanAnchorFh(), _facing, _path, out _halfLen, WalkedPenalty))
            {
                _halfLen = HalfLegLength(_map, _walker.FootholdId, _facing);
            }
            _hopIndex = 0;
            _portalCooldown = PortalCooldownSec;
            return true;
        }

        // ===== 内部：单步 =====

        /// <summary>推进一个物理子步。</summary>
        /// <returns>false = 本次 Tick 的剩余子步不再推进（上绳 / 过渡完成 / 切图 / 折返 / 退出）。</returns>
        private bool StepOnce(double step)
        {
            if (_map == null || _walker == null) { return false; }
            _simClock += step;
            if (_ropeCooldown > 0) { _ropeCooldown -= step; }
            if (_portalCooldown > 0) { _portalCooldown -= step; }
            if (_midBlockedReplanCd > 0) { _midBlockedReplanCd -= step; }
            // F4：兜底「不准再上同一根绳」的闩——离开绳 x 足够远才解除
            if (_ropeRegrabArmed && Math.Abs(_walker.X - _lastRopeX) > RopeRegrabMinDist) { _ropeRegrabArmed = false; }

            // 要求 2：Jump / Fall 视觉过渡期间不接受新的 WalkStep，只推进插值
            if (_transitioning) { return AdvanceAirborne(step); }

            if (Phase == WalkPhase.Climbing)
            {
                int ropeId = _walker.RopeId;
                float climbFrom = _walker.Y;
                _walker.ClimbStep((float)step, _climbDir);
                // 「模拟行走长度」含爬绳：攀爬距离计入已走距离（口径：累计长度含跳跃/爬绳）
                _legTraveled += Math.Abs(_walker.Y - climbFrom);
                // 端头物理脱离（胶水 2026-09-16「绳子到头下来不是 jump」）：不瞬移吸附最近段
                //（旧 LeaveRope 把 Y 直接改成段面 = 大位移瞬移），转下落态 jump 落地到端头地面。
                if (_walker.JustReachedEnd)
                {
                    _walker.LeaveRope();   // 清绳状态（位置保持端头，不吸附）
                    _transitionTargetFh = _walker.FootholdId;
                    _airHopIndex = _hopIndex;
                    _airVy = 0f;
                    _airVx = 0f;
                    _airFromFh = 0;
                    _airFromX = _walker.X;
                    _airFromY = _walker.Y;
                    _transitionX = _walker.X;
                    _transitionY = _walker.Y;
                    _transitioning = true;
                    _ropeCooldown = _ropeFromPlanned ? RopeCooldownSec : RopeFallbackCooldownSec;
                    _lastRopeId = ropeId;
                    _lastRopeX = _walker.X;
                    _ropeRegrabArmed = !_ropeFromPlanned;
                    _currentAction = ActionJump;
                    return true;
                }
                if (_walker.OnRope) { return true; }
                // 防御：外部脱离（非端头）→ 旧行为回行走
                Phase = WalkPhase.Walking;
                _ropeCooldown = _ropeFromPlanned ? RopeCooldownSec : RopeFallbackCooldownSec;
                _lastRopeId = ropeId;
                _lastRopeX = _walker.X;
                _ropeRegrabArmed = !_ropeFromPlanned;
                _currentAction = _walkAction;
                if (_hopIndex < _path.Count && _path[_hopIndex].Link == WalkLink.Rope) { _hopIndex++; }
                SyncHops();
                return false;
            }

            if (Phase != WalkPhase.Walking) { return false; }

            var hop = CurrentHop();
            WalkLink link = hop == null ? WalkLink.Adjacent : ResolveLink(_map, _walker.FootholdId, hop);

            // 同图传送 hop（Warp）：必须先沿链走到传送门口段（FromFootholdId），到位即触发图内传送——
            // 绝不当物理跳（传送目标靠门对互指，物理跳不可达，SleepyWood 706030400 实测）。
            // ropeId=-portalId 标记「站在哪扇门」（WalkGraph ②a：正/反向边各标各自的源门）。
            if (hop != null && link == WalkLink.Warp)
            {
                var srcPortal = _map.Portals.FirstOrDefault(p => p.Id == -hop.RopeId);
                if (srcPortal == null)
                {
                    // 门数据缺失（异常）→ 跳过该跳，避免永久卡死
                    _hopIndex++;
                    SyncHops();
                    return false;
                }
                // 「到门口」= 站上门吸附段且走到门的 x（吸附段可能很长，门口在段中）；或已落在
                // 门口判定带（PortalBand 同口径，兼容斜坡/重叠等价段上段 id 失配的情形）。
                bool onDoorSeg = _walker.FootholdId == hop.FromFootholdId;
                bool nearDoor = Math.Abs(_walker.X - srcPortal.X) <= PortalBandX
                    && Math.Abs(_walker.Y - srcPortal.Y) <= PortalBandY;
                if (!(onDoorSeg && nearDoor))
                {
                    // 未到门口：朝门口 x 沿链走（同 Jump/Fall 的「不在起跳点就不起跳」——普通行走引导，
                    // 走到后上面的条件自然成立；由 WalkGraph 规划保证门口段可达）
                    int toward = Math.Sign(srcPortal.X - _walker.X);
                    if (toward != 0) { _facing = toward; }
                }
                else if (ExecuteSelfWarp(srcPortal))
                {
                    return false;   // 传送完成：PlaceOn 到目标门 + 本腿重规划已就绪，本子步到此
                }
                else
                {
                    // 已站门口但传送不可行：门对已用过（乒乓防护，_usedSelfPortals）或目标异常
                    // → 跳过该跳继续走后面的路，绝不再传（同门二次不传）
                    _hopIndex++;
                    SyncHops();
                    return false;
                }
            }

            if (hop != null && link == WalkLink.Rope)
            {
                // §4 Rope 跳：真实攀爬——先沿链走到绳附近，几何包络命中后 EnterRope(ropeId)，再 ClimbStep 到端头
                var rope = FindRope(_map, hop.RopeId);
                if (rope == null)
                {
                    // 绳数据缺失（异常）→ 跳过该跳，避免永久卡死
                    _hopIndex++;
                    SyncHops();
                    return false;
                }
                // F2：直接对目标绳做几何判定，不经过 RopeNearby（多绳 x 重叠时它可能返回别的绳把目标绳挡住）
                bool inX = Math.Abs(_walker.X - rope.X) <= MapWalker.RopeNearMaxX;
                bool inY = _walker.Y >= rope.Top - MapWalker.RopeNearMaxY && _walker.Y <= rope.Bottom + MapWalker.RopeNearMaxY;
                bool justLeftThisRope = rope.Id == _lastRopeId && _ropeCooldown > 0;
                // 移动跳抓（胶水 2026-09-16「移动过程中可以跳抓」）：跳跃高度与普通跳相同（sdlMS
                // vspeed 统一 -555 → 77px），优势在**空中横移**——抓取触发窗放宽到 RopeNear + 半个
                // 空中横移量（125×0.5s≈62px → 窗口 ~87px），起跳后 vx 朝绳、空中每子步对齐即抓。
                float catchReachX = MapWalker.RopeNearMaxX + AirHspeed * 0.5f;
                bool inCatchX = Math.Abs(_walker.X - rope.X) <= catchReachX;
                if (inX && inY && !justLeftThisRope)
                {
                    int climbDir = ClimbDirection(_walker.Y, rope);
                    if (_walker.EnterRope(rope.Id))
                    {
                        _climbDir = climbDir;
                        Phase = WalkPhase.Climbing;
                        _ropeFromPlanned = true;
                        _ropeStuckSec = 0;
                        _currentAction = rope.IsLadder ? _ladderAction : _ropeAction;
                        return false;
                    }
                }
                if (!inCatchX)
                {
                    // 还没到绳：朝绳的 x 走（必要时转身）
                    int toward = Math.Sign(rope.X - _walker.X);
                    if (toward != 0) { _facing = toward; }
                }
                else if (inX && inY && justLeftThisRope)
                {
                    // 刚离开本绳且还在冷却：路过不动作
                }
                else if (inCatchX && !inY)
                {
                    // 跳抓（2026-09-16，配合绳窗口扩到 ±25/±40）：站得上 x 却够不着 y（绳端悬空）→
                    // **原地起跳**，空中 y 进入绳范围即 EnterRope（物理跳高 77px + 空中抓取窗）。
                    // 跳 2 次仍抓不到 → F1b 降级跳过（避免无限跳）。
                    if (_ropeCatchFails < 2 && !_transitioning)
                    {
                        int dir = Math.Sign(rope.X - _walker.X);
                        _airCatchRope = rope.Id;
                        StartAirborne(hop, jumpTowards: dir == 0 ? _facing : dir, vy: -555f);
                        _currentAction = ActionJump;
                        return true;
                    }
                    // F1b：站得上绳的 x 却够不着绳的 y（悬空绳端等）→ **累计**计时（来回经过也累计），
                    // 超时降级：跳过该绳继续走后面的跳，避免绳底徘徊死锁
                    _ropeStuckSec += step;
                    if (_ropeStuckSec >= RopeStuckTimeoutSec)
                    {
                        Console.Error.WriteLine(
                            $"[WalkMode] F1b 绳跳够不到，降级跳过：rope#{hop.RopeId} walker=({_walker.X:F0},{_walker.Y:F0}) rope x={rope.X} top={rope.Top} bottom={rope.Bottom}");
                        _ropeStuckSec = 0;
                        _hopIndex++;
                        SyncHops();
                        return false;
                    }
                }
            }
            else if (hop == null)
            {
                // §4 传统「走到绳附近自动上绳」只在没有图路径的兜底行走时保留（有路径时由 Rope 跳驱动，不乱上绳）
                var rope = _walker.RopeNearby();
                // F4：同绳「不准再上」= 冷却中 或 闩未解（离开绳 x >40px 才解除）；且绳底在脚下 ±20 内
                // 或绳身穿过脚下平台才可顺手抓（101000000 rope11：瞬移回起点后仍在吸附窗 → 同绳上下摆钟）
                bool justLeftThisRope = rope != null && rope.Id == _lastRopeId
                    && (_ropeCooldown > 0 || _ropeRegrabArmed);
                bool grabbable = rope != null && FallbackRopeGrabbable(rope);
                if (grabbable && !justLeftThisRope && FacingTowardRope(rope))
                {
                    int climbDir = ClimbDirection(_walker.Y, rope);
                    if (_walker.EnterRope(rope.Id))
                    {
                        _climbDir = climbDir;
                        Phase = WalkPhase.Climbing;
                        _ropeFromPlanned = false;
                        _currentAction = rope.IsLadder ? _ladderAction : _ropeAction;
                        return false;
                    }
                }
            }

            // 跳的行走引导：相邻跳朝目标段中心、跳/落跳朝**起跳段**中心——锚点在身后就先转身走过去。
            // 没有这条引导，中途重规划/传送落地后首跳指向反方向时（RouteToEnd 并列裁决可能把「只能
            // 经传送/坠落到达的同 x 上层段」选成面向方向的尽头），人物会面壁走到 2s 冷却重规划、
            // 重规划又规划同一条路 → 原地钉死（706030500 树图传送落地复现：钉墙 15s+）。
            // 已站在锚点段上则不干预朝向（相邻=等同步推进；跳/落=起跳方向交给既有 gate/blocked 逻辑）。
            // Rope/Warp 有各自的朝向引导，不在此列。
            if (hop != null && (link == WalkLink.Adjacent || link == WalkLink.Jump || link == WalkLink.Fall))
            {
                int anchorId = link == WalkLink.Adjacent ? hop.FootholdId : hop.FromFootholdId;
                if (_walker.FootholdId != anchorId)
                {
                    var anchorFh = FindFoothold(_map, anchorId);
                    if (anchorFh != null)
                    {
                        int toward = Math.Sign((anchorFh.X1 + anchorFh.X2) / 2f - _walker.X);
                        if (toward != 0) { _facing = toward; }
                    }
                }
            }

            // 要求 2（2026-09-15 物理跳跃）：Jump / Fall **必须站在起跳段（FromFootholdId）上才起跳**——
            // 不在起跳段就先沿链走过去（落到 §3 行走），杜绝在半路原地起跳够不到目标段
            //（魔法密林实测：人在斜坡链上反复起跳落回原地，hop 永不推进）。
            if (hop != null && (link == WalkLink.Jump || link == WalkLink.Fall))
            {
                bool onTakeoff = hop.FromFootholdId == 0 || _walker.FootholdId == hop.FromFootholdId;
                if (onTakeoff)
                {
                    return StartAirborne(hop);
                }
                // 未到起跳段：按普通行走继续（走向起跳段；走到后上面的条件自然成立）
            }

            // §3 行走：dirInput = Facing（MapWalker 内部做加速/摩擦/贴线/切段）
            float prevX = _walker.X;
            bool blocked = _walker.WalkStep((float)step, _facing);
            _legTraveled += Math.Abs(_walker.X - prevX);
            _walkedFh.Add(_walker.FootholdId);
            // 走过的段若就是后面某一跳的目标 → 推进 _hopIndex（连推，处理沿链一次跨多段）
            SyncHops();

            // 行走动作固定（见 _walkAction 注释）：不再按时间交替
            _currentAction = _walkAction;

            // 位置停滞自救（B2 修复：入口段为垂直段等场景 path=0，hop 从未开始 → 旧 stall 不触发；
            // 252 图实测 1Hz 上下振荡 30s）。Walking 且 8s 位置几乎不动（含 hop 不推进）→ 重规划/放弃。
            {
                bool posMoved = Math.Abs(_walker.X - _lastStallX) > 5f || Math.Abs(_walker.Y - _lastStallY) > 5f;
                if (posMoved)
                {
                    _lastStallX = _walker.X;
                    _lastStallY = _walker.Y;
                    _posStallSec = 0;
                }
                else
                {
                    _posStallSec += step;
                    if (_posStallSec >= HopStallResec && _candidate != null)
                    {
                        var goalFh3 = GoalAnchorFh(_candidate.X, _candidate.Y);
                        var replanned3 = goalFh3 != 0 ? WalkGraph.Plan(_map, PlanAnchorFh(), goalFh3, WalkedPenalty) : null;
                        Console.WriteLine($"[setup] 位置停滞 {_posStallSec:F0}s at ({_walker.X:F0},{_walker.Y:F0}) → " + (replanned3 != null ? $"重规划（{replanned3.Count} 跳）" : "放弃候选"));
                        _posStallSec = 0;
                        if (replanned3 != null)
                        {
                            _path = replanned3;
                            _hopIndex = 0;
                            _legTraveled = 0;
                        }
                        else
                        {
                            _candidate = null;
                            _noNextPortal = true;
                        }
                    }
                }
            }

            // 路径失配自救：hop 长时间无推进且**净位移**很小（路径与实际失配 / 执行不了的跳，人物
            // 原地小震荡）→ 从当前位置重新规划。有候选门 → 重 Plan 到候选门，不可达则放弃该候选；
            // 死胡同覆盖模式 → 换一个「未访段」目标重规划（不走放弃逻辑）。净位移门槛保护正常的
            // 长腿行走（朝远处 hop 目标走时 hop 迟迟不推进但位移大，不算卡）。覆盖模式原本完全
            // 禁用自救（怕破坏两遍覆盖，胶水 2026-09-15）——但遇到执行不了的跳（326000000：
            // 高差 780px 的 Jump 跳）会原地无限震荡 50s+，覆盖率归零，故按净位移条件恢复。
            if (_hopIndex == _lastHopIndex)
            {
                _hopStallSec += step;
                // 移出 100px 盒子 = 还在图上真正移动（朝远处 hop 目标的长途行走）→ 不算卡：重锚重来。
                // 锚定在「hop 推进时刻」不够——推进后仍可能沿链滑下坡走出一大段再开始卡。
                if (_walker != null && (Math.Abs(_walker.X - _hopStallAnchorX) >= 100f || Math.Abs(_walker.Y - _hopStallAnchorY) >= 100f))
                {
                    _hopStallSec = 0;
                    _hopStallAnchorX = _walker.X;
                    _hopStallAnchorY = _walker.Y;
                }
            }
            else
            {
                _hopStallSec = 0;
                _lastHopIndex = _hopIndex;
                _hopStallAnchorX = _walker?.X ?? 0f;
                _hopStallAnchorY = _walker?.Y ?? 0f;
            }
            bool hopNetStuck = _hopStallSec >= (_noNextPortal ? HopStallCoverResec : HopStallResec) && _walker != null
                && Math.Abs(_walker.X - _hopStallAnchorX) < 100f
                && Math.Abs(_walker.Y - _hopStallAnchorY) < 100f;
            if (hopNetStuck && _candidate != null && !_noNextPortal)
            {
                var goalFh2 = GoalAnchorFh(_candidate.X, _candidate.Y);
                var replanned = goalFh2 != 0 ? WalkGraph.Plan(_map, PlanAnchorFh(), goalFh2, WalkedPenalty) : null;
                if (replanned != null)
                {
                    Console.WriteLine($"[setup] hop 卡住 {_hopStallSec:F0}s → 从当前位置重新规划（{replanned.Count} 跳）");
                    _path = replanned;
                    _hopIndex = 0;
                    _legTraveled = 0;
                }
                else
                {
                    Console.WriteLine($"[setup] hop 卡住 {_hopStallSec:F0}s 且候选 {_candidate.PName} 不可达 → 放弃");
                    _candidate = null;
                    _noNextPortal = true;
                }
                _hopStallSec = 0;
            }
            else if (hopNetStuck && _noNextPortal && _map != null)
            {
                // 覆盖模式：卡住后换目标重规划——交替用「最近/最远未访段」：远目标路线若原样复现
                // 同一批执行不了的跳（910510100 实测 5s 一轮死循环），近目标给执行一次不同的机会。
                _coverReplanAlt = !_coverReplanAlt;
                int anchorFh = PlanAnchorFh();
                var alt = _coverReplanAlt ? WalkGraph.NearestUnvisited(_map, anchorFh, _walkedFh)
                                          : WalkGraph.FarthestUnvisited(_map, anchorFh, _walkedFh);
                bool replanned = false;
                if (alt.HasValue)
                {
                    var leg = WalkGraph.Plan(_map, anchorFh, alt.Value.FootholdId, SegmentPenalty);
                    if (leg is { Count: > 0 })
                    {
                        _path = leg;
                        _hopIndex = 0;
                        _legTraveled = 0;
                        replanned = true;
                    }
                }
                if (!replanned && PlanLegToTarget(_map, anchorFh, _facing, _path, out _halfLen, SegmentPenalty))
                {
                    _hopIndex = 0;
                    _legTraveled = 0;
                    replanned = true;
                }
                if (replanned)
                {
                    Console.WriteLine($"[setup] 覆盖腿 hop 卡住 {_hopStallSec:F0}s → 换{(_coverReplanAlt ? "近" : "远")}未访目标重新规划");
                }
                else
                {
                    _hopIndex++;
                    SyncHops();
                }
                _hopStallSec = 0;
            }

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

            // §6 无门折返（胶水 2026-09-15 拍板改为「掉头走回」）：**走到该方向真正尽头**（贴边 blocked）
            // 才原地掉头反向走。不用位移阈值触发：RouteToEnd 的全程是「段中心跨度」估算，会低估实际位移
            // （101000057 实测：估算 1950 在 x=2370 就触发，而尽头在 x=3400 → 走到一半就掉头）；
            // blocked 才是「尽头」的精确信号。废弃旧「瞬移回起点」（小图 4~5 秒一闪现，观感破碎）。

            // 兜底：前方无路（贴边停住）——三种情况：
            //   ① 当前跳的目标段就在前方贴邻（未链接的台阶/接缝段：ResolveLink 按「走」处理，
            //      但 MapWalker 没法跨无链段）→ 退化成跳/落过渡直接落到目标段，
            //      否则会在接缝两侧来回摩擦死锁（104000000 实测：卡在 y=347 平台 5px/s 龟速爬行）；
            //      死胡同覆盖模式下计划中的 Jump/Fall 也算——单向缺口（落差可下跳、上跳受 77px 限）
            //      的「下跳」正是该方向覆盖的必经过法（规则 3）；
            //   ② 死胡同图走到「真尽头」→ 掉头（TurnAround 内做完整单程判定）；
            //   ③ 有候选门图 → 简单反向。
            if (blocked)
            {
                var hopB = CurrentHop();
                WalkLink linkB = WalkLink.Adjacent;
                if (hopB != null && _map != null)
                {
                    linkB = ResolveLink(_map, _walker.FootholdId, hopB);
                    bool gapHop = linkB == WalkLink.Adjacent
                        || (_noNextPortal && (linkB == WalkLink.Jump || linkB == WalkLink.Fall));
                    if (gapHop && TryHopOverGap(hopB))
                    {
                        return true;
                    }
                }
                if (_noNextPortal)
                {
                    if (_map == null) { return false; }
                    // 规则 3（死胡同完整覆盖，胶水 2026-09-15）：只有「真尽头」（图口径：面向方向
                    // 再无可达段）的 blocked 才进入掉头/计趟；中途 blocked（缺口过不去但图上前方
                    // 还有路）不计遍数，尽量把覆盖走下去 —— 防止「走到中途就 turns>=2 → 只走了
                    // 一部分就退图」（地球防御总部单向缺口 / 922010000 梯子端点实测病灶）。
                    if (IsWalkableEndInDirection(_map, PlanAnchorFh(), _facing))
                    {
                        TurnAround();
                        return false;
                    }
                    // 中途 blocked：① 深落差（>120px，TryHopOverGap 够不着）的计划内 Fall 直接起跳
                    // 交给重力（扫掠落地 + AirMaxSec 超时兜底）；② 其余原位重规划（不带贪心已走罚、
                    // 但带传送冷却罚——别把路再规划过冷却中的门口）找回过缺口的跳序列，冷却期内不重复。
                    // 保持当前朝向，遍数不动。
                    if (hopB != null && linkB == WalkLink.Fall
                        && hopB.FootholdId != _walker.FootholdId
                        && StartAirborne(hopB))
                    {
                        return true;
                    }
                    if (_midBlockedReplanCd <= 0
                        && PlanLegToTarget(_map, PlanAnchorFh(), _facing, _path, out _halfLen, WarpCooldownPenalty))
                    {
                        _hopIndex = 0;
                        _midBlockedReplanCd = MidBlockedReplanCdSec;
                    }
                    return false;
                }
                _facing = -_facing;
                _legTraveled = 0;
            }
            return true;
        }

        /// <summary>
        /// 真尽头掉头：原地掉头 + 从**当前段**重新规划新朝向的路径与掉头阈值（与 SetupLeg 同口径：
        /// 新朝向的「可行走路径尽头」全程）。旧实现「PlaceOn 瞬移回起点段」已废弃。
        /// 死胡同覆盖模式的计趟在**调用前**已由真尽头判定把关（StepOnce 的
        /// <see cref="IsWalkableEndInDirection"/>），此处再做「完整单程」位移判定：
        /// 与上次计入 blocked 的水平位移 ≥ max(本腿全长/3, <see cref="DeadEndMinLegDist"/>) 才算一趟
        /// ——梯子端点、小缺口边的短途 blocked 只掉头重置方向不计趟（922010000 连环快切片段）。
        /// 退图条件 = 两方向各一次完整单程（_deadEndTurns ≥ 2）且总停留 ≥ <see cref="DeadEndMinStaySec"/>；
        /// 退图目标走规则 4 的有序列表（见 <see cref="ReturnToSessionStartMap"/>）。
        /// </summary>
        private void TurnAround()
        {
            _facing = -_facing;
            _legTraveled = 0;
            _transitioning = false;
            _ropeCooldown = RopeCooldownSec;
            _midBlockedReplanCd = 0;
            // 掉头后必须**从当前所在段重新规划**本腿路径与掉头阈值；阈值保持全长口径不变
            //（只作完整单程位移分母与诊断，不参与触发）。死胡同覆盖模式禁用贪心段罚——
            // 第二遍就该原路完整重走一遍，罚金会让路径畸形绕行（胶水 2026-09-15）。
            int fromFh = PlanAnchorFh();
            if (fromFh == 0) { fromFh = _walker?.FootholdId ?? _startFootholdId; }
            if (_map != null && !PlanLegToTarget(_map, fromFh, _facing, _path, out _halfLen, SegmentPenalty))
            {
                _halfLen = HalfLegLength(_map, fromFh, _facing);
            }
            _hopIndex = 0;
            // F1b 的「够不到绳」累计计时**不清零**：同一根够不到的绳不会因为掉头就变得够得到

            if (_noNextPortal)
            {
                float x = _walker?.X ?? 0f;
                double minLeg = Math.Max(_halfLen / 3.0, DeadEndMinLegDist);
                double moved = Math.Abs(x - _lastTurnX);
                _lastTurnX = x;
                if (moved >= minLeg)
                {
                    // 一趟完整单程完成：重置卡死兜底计时（有进展），下一趟从掉头点重新起算
                    _deadEndSec = 0;
                    _deadEndTurns++;
                    Console.Error.WriteLine(
                        $"[WalkMode] 死胡同 {_mapId} 完整单程 ×{_deadEndTurns}（位移 {moved:F0}px ≥ {minLeg:F0}）");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[WalkMode] 死胡同 {_mapId} 短途 blocked（位移 {moved:F0}px < {minLeg:F0}）→ 只掉头不计趟");
                }
                if (_deadEndTurns >= 2 && _deadEndStaySec >= DeadEndMinStaySec)
                {
                    Console.Error.WriteLine(
                        $"[WalkMode] 死胡同 {_mapId} 两方向各完整单程 + 停留 {_deadEndStaySec:F0}s → 退图");
                    _deadEndMaps.Add(_mapId);
                    ReturnToSessionStartMap();
                }
            }
        }

        /// <summary>
        /// 死胡同「真尽头」判定（图口径）：从当前段出发，面向方向是否还有水平延伸更远的可达段。
        /// <see cref="WalkGraph.RouteToEnd"/> 规划不出前方路径（尽头 = 起点）→ 已是真尽头。
        /// 中途 blocked（缺口过不去但图上前方仍有路）由此与真尽头区分开，不计遍数。
        /// </summary>
        private static bool IsWalkableEndInDirection(MapInfo map, int fromFootholdId, int facing)
        {
            var tmp = new List<WalkHop>();
            double full = WalkGraph.RouteToEnd(map, fromFootholdId, facing, tmp);
            return full <= 1 || tmp.Count == 0;
        }

        /// <summary>
        /// 规划段罚：死胡同覆盖模式下**只用传送冷却罚**（贪心「已走段 +400」会破坏两遍完整覆盖，
        /// 胶水 2026-09-15；但冷却中的传送门口段仍要绕开——两遍覆盖不等于反复空转门口）；
        /// 有候选门的图维持贪心探索（含冷却罚）。
        /// </summary>
        private Func<int, double>? SegmentPenalty => _noNextPortal ? WarpCooldownPenalty : WalkedPenalty;

        /// <summary>
        /// 退出死胡同（胶水 2026-09-15 规则 4「顺序退图」）：退图目标 =
        /// <see cref="MusicCatalogService.GetTrackMapsOrdered"/> 排序列表中**当前图之后的第一个**
        /// 非死胡同、非当前图的 mapId；列表到尾 → 回绕到列表第一个合格者，以此走完整张列表。
        /// 列表为空 / 全死胡同 / catalog 为空（探针、测试注入 null）→ 回退既有逻辑：
        /// ① 来源图；② 会话初始图；③ 当前曲目其他候选图；全都没有/都指向当前图 → 留在原地继续覆盖。
        /// </summary>
        private void ReturnToSessionStartMap()
        {
            _deadEndSec = 0;
            _deadEndTurns = 0;
            string? target = PickNextMapInOrderedTrackMaps();
            if (string.IsNullOrEmpty(target))
            {
                target = !string.IsNullOrEmpty(_cameFromMapId) ? _cameFromMapId : _sessionStartMapId;
                if (string.IsNullOrEmpty(target) || string.Equals(target, _mapId, StringComparison.Ordinal))
                {
                    // 当前图就是唯一已知目标 → 从曲目的候选图里挑一张「非死胡同且非当前」的换图
                    target = null;
                    if (_catalog != null && _catalog.TryGetTrackMaps(_trackKey, out var items, out _))
                    {
                        foreach (var it in items)
                        {
                            if (!string.Equals(it.MapId, _mapId, StringComparison.Ordinal)
                                && !_deadEndMaps.Contains(it.MapId))
                            {
                                target = it.MapId;
                                break;
                            }
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(target) || string.Equals(target, _mapId, StringComparison.Ordinal))
            {
                return;   // 无处可退：继续当前图两遍覆盖
            }
            // 新一轮贪心遍历：已访问只保留死胡同图（不再优先去），其余清空 → 次远的图成为下一轮首选
            _visitedMaps.RemoveWhere(id => !_deadEndMaps.Contains(id));
            BeginMap(target, arrival: null);
        }

        /// <summary>
        /// 规则 4 的「有序列表下一个」：当前图在列表中的位置之后第一个非死胡同、非当前图的 mapId；
        /// 当前图不在列表 → 从列表头找；到尾 → 回绕到列表头继续找。取不到（空列表/全死胡同）→ null。
        /// </summary>
        /// <summary>
        /// 退图目标（胶水 2026-09-16「优先走最远的地图」）：候选里**mapId 距当前图数值最远**的合格图；
        /// 并列（对称）取列表序靠前者。无合格（全死胡同/全当前图）→ null（回退来源图/原地）。
        /// </summary>
        private string? PickNextMapInOrderedTrackMaps()
        {
            if (string.IsNullOrEmpty(_trackKey)) { return null; }
            List<string>? ordered = _trackMapsOrdered(_trackKey);
            if (ordered == null || ordered.Count == 0) { return null; }
            long curId = long.TryParse(_mapId, out var c) ? c : 0;
            string? best = null;
            long bestDist = -1;
            int bestIdx = int.MaxValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                string m = ordered[i];
                if (!IsEligibleExitTarget(m)) { continue; }
                long id = long.TryParse(m, out var v) ? v : 0;
                long dist = Math.Abs(id - curId);
                if (dist > bestDist || (dist == bestDist && i < bestIdx))
                {
                    bestDist = dist;
                    best = m;
                    bestIdx = i;
                }
            }
            return best;
        }

        /// <summary>规则 4 的合格退图目标：非当前图、非死胡同。</summary>
        private bool IsEligibleExitTarget(string mapId)
        {
            return !string.Equals(mapId, _mapId, StringComparison.Ordinal)
                && !_deadEndMaps.Contains(mapId);
        }

        // ===== 内部：路径推进 / Jump-Fall 过渡（要求 2、4）=====

        /// <summary>当前执行到的那一跳（路径走完 / 无路径 → null，走兜底沿链行走）。</summary>
        private WalkHop? CurrentHop()
        {
            return _hopIndex >= 0 && _hopIndex < _path.Count ? _path[_hopIndex] : null;
        }

        /// <summary>
        /// 按当前位置推进 _hopIndex：先按段 id（<see cref="NextHopIndex"/>），失配再用**几何兜底**——
        /// 魔法密林等图的斜坡有多条不同 id 的重叠等价段（同 x 范围、同坡面），人物实际走在等价段上时
        /// id 永远失配 → hop 不推进 → 来回走。几何口径 = 人物在目标段 x 范围 ±5 且距段面 ≤10px 即视为到达。
        /// </summary>
        private void SyncHops()
        {
            int idx = NextHopIndex(_path, _hopIndex, _walker?.FootholdId ?? 0);
            if (idx == _hopIndex && _map != null && _walker != null)
            {
                for (int j = _path.Count - 1; j >= _hopIndex && j >= 0; j--)
                {
                    var f = FindFoothold(_map, _path[j].FootholdId);
                    if (f == null) { continue; }
                    float lo = Math.Min(f.X1, f.X2) - 5f, hi = Math.Max(f.X1, f.X2) + 5f;
                    if (_walker.X < lo || _walker.X > hi) { continue; }
                    float segY = MapWalker.YOn(f, _walker.X);
                    if (Math.Abs(segY - _walker.Y) <= 10f) { idx = j + 1; break; }
                }
            }
            _hopIndex = idx;
        }

        /// <summary>
        /// 推进下标（要求 4 的纯函数边界）。
        /// 「顺路完成」连推：当前段若出现在 index 之后的**某个** hop（含 index 本身），则它前面的跳全部视为
        /// 已完成（返回该 hop 的下一个下标）——实际行走沿链会穿过多个未列入路径的中间段（Dijkstra 的跳跃
        /// 捷径边跨段建路，执行层沿链走完这些段后位置已越过中间 hop）；不跳过就会「回头执行」旧 hop，
        /// 表现为向后方目标段跳（100000000 实测：沿链走到 x=991 却回头跳向 fh#154 的 848 端）。
        /// 当前段不在路径后方 → 保持原 index（等 blocked/兜底处理）。
        /// </summary>
        internal static int NextHopIndex(IReadOnlyList<WalkHop> hops, int index, int currentFootholdId)
        {
            for (int j = hops.Count - 1; j >= index; j--)
            {
                if (hops[j].FootholdId == currentFootholdId) { return j + 1; }
            }
            return index;
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
                // 台阶归走须同时满足「水平近 + 高度差 ≤20」——魔法密林 fh#123→fh#48 高差 75px 也被归走，
                // 人物走到下方却上不去（走 ≠ 跳），hop 永不推进 → 来回走
                float fromY = (from.Y1 + from.Y2) / 2f;
                float targetY = (target.Y1 + target.Y2) / 2f;
                if (dx <= 90 && Math.Abs(targetY - fromY) <= 20) { return WalkLink.Adjacent; }
            }
            return hop.Link;
        }

        /// <summary>
        /// 开始空中阶段（跳跃/下坠，sdlMS 口径）：跳跃 vspeed=-555，下坠 vspeed=0；
        /// 水平「按住方向键」= vx = facing·125；**不预选落点**——重力积分到哪段面就落到哪段。
        /// </summary>
        private bool StartAirborne(WalkHop hop) => StartAirborne(hop, jumpTowards: null, vy: null);

        private bool StartAirborne(WalkHop hop, int? jumpTowards, float? vy)
        {
            if (_map == null || _walker == null) { return false; }
            _transitionTargetFh = hop.FootholdId;
            _airHopIndex = _hopIndex;
            bool fall = hop.Link == WalkLink.Fall;
            _airVy = vy ?? (fall ? 0f : -555f);
            _airVx = jumpTowards.HasValue ? jumpTowards.Value * AirHspeed : _facing * AirHspeed;
            _airElapsed = 0;
            _airFromX = _walker.X;
            _airFromY = _walker.Y;
            _airFromFh = _walker.FootholdId;
            _transitionX = _walker.X;
            _transitionY = _walker.Y;
            _transitioning = true;
            _currentAction = ActionJump;
            return true;
        }

        /// <summary>
        /// 推进空中物理（半隐式欧拉，1/60s 子步）：重力积分 → 位移 → 扫掠落地检测
        /// （位移线段 vs 全部段面，取最先穿越的最高面；起跳段自身豁免，等价 sdlMS 落地保护）。
        /// 落段由重力决定、不保证是规划目标段 → 落地后把起跳的 hop 跳过（SyncHops 顺路完成对齐）。
        /// </summary>
        private bool AdvanceAirborne(double step)
        {
            if (_map == null || _walker == null)
            {
                _transitioning = false;
                return false;
            }
            _airElapsed += step;
            float prevY = _transitionY;
            _airVy = Math.Min(_airVy + (float)(WalkGravity * step), FallTerminalVy);
            float nx = _transitionX + _airVx * (float)step;
            float ny = prevY + _airVy * (float)step;

            // 横向撞墙（sdlMS fall_collide_wall）：位移线段穿越垂直段（x1==x2，无 k 即墙）
            // → x 钳到墙边 ±0.1、水平速度清零，继续下落（不穿墙）
            float? wallX = FindWallDuringSweep(_transitionX, nx, Math.Min(prevY, ny), Math.Max(prevY, ny));
            if (wallX.HasValue)
            {
                nx = _airVx > 0 ? wallX.Value - 0.1f : wallX.Value + 0.1f;
                _airVx = 0;
            }

            // 空中抓绳（跳抓）：起跳意图抓绳时，每子步检查绳包络（x±25、y 身区间）→ 命中即抓
            if (_airCatchRope > 0)
            {
                var catchRope = FindRope(_map, _airCatchRope);
                if (catchRope != null && Math.Abs(nx - catchRope.X) <= MapWalker.RopeNearMaxX
                    && ny >= catchRope.Top - 10f && ny <= catchRope.Bottom + 10f
                    && _walker.EnterRope(catchRope.Id))
                {
                    _transitioning = false;
                    _airCatchRope = 0;
                    Phase = WalkPhase.Climbing;
                    _climbDir = ny >= catchRope.Bottom - 5 ? +1 : -1;
                    _ropeFromPlanned = true;
                    _ropeStuckSec = 0;
                    _ropeCooldown = 0;
                    _currentAction = catchRope.IsLadder ? _ladderAction : _ropeAction;
                    return false;
                }
            }

            var land = FindLandingDuringSweep(nx, prevY, ny);
            bool timeout = _airElapsed >= AirMaxSec || ny > _map.MaxY + 300;
            if (land != null || timeout)
            {
                _transitioning = false;
                Phase = WalkPhase.Walking;   // 绳端物理脱离落地：必须回 Walking（否则停在 Climbing 永不走）
                int fh = land?.FhId ?? _transitionTargetFh;
                float lx = land.HasValue ? nx : _transitionX;
                _walker.PlaceOn(fh, lx);
                _legTraveled += Distance(_airFromX, _airFromY, _walker.X, _walker.Y);
                _transitionX = _walker.X;
                _transitionY = _walker.Y;
                _currentAction = _walkAction;
                if (_hopIndex == _airHopIndex && _hopIndex < _path.Count) { _hopIndex++; }
                // B3：同一起跳段连续 3 次空中落地后 hop 仍未推进 → 该跳物理不可达，放弃候选门换下一个
                if (_airCatchRope > 0) { _airCatchRope = 0; _ropeCatchFails++; _ropeStuckSec = 0; }
                if (_hopIndex == _airHopIndex)
                {
                    _airFailStreak++;
                    if (_airFailStreak >= 3 && _candidate != null)
                    {
                        Console.WriteLine($"[setup] 同一跳连续 {_airFailStreak} 次未推进 → 放弃候选 {_candidate.PName}→{_candidate.Tm}");
                        _candidate = null;
                        _noNextPortal = true;
                        _airFailStreak = 0;
                    }
                }
                else { _airFailStreak = 0; }
                SyncHops();
                return false;
            }
            _transitionX = nx;
            _transitionY = ny;
            _currentAction = ActionJump;
            return true;
        }

        /// <summary>落地检测：位移线段 (prevX≈x, prevY)→(x, newY) 穿越的段面（x 覆盖、y 向下为正），取最先碰到的最高面；起跳段豁免。</summary>
        private (int FhId, float SegY)? FindLandingDuringSweep(float x, float prevY, float newY)
        {
            if (newY <= prevY) { return null; }   // 上升段不会落地
            (int, float)? best = null;
            foreach (var f in _map!.Footholds)
            {
                if (f.Id == _airFromFh) { continue; }
                if (Math.Abs(f.X1 - f.X2) < 0.5f) { continue; }   // 垂直段是墙，不是立足面
                float lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
                if (x < lo || x > hi) { continue; }
                float segY = MapWalker.YOn(f, x);
                if (prevY <= segY && segY <= newY)
                {
                    if (best == null || segY < best.Value.Item2) { best = (f.Id, segY); }
                }
            }
            return best == null ? null : (best.Value.Item1, best.Value.Item2);
        }

        /// <summary>横向撞墙检测：位移线段（水平为主）穿越的垂直段（x1≈x2、y 范围与位移 y 区间相交）→ 返回墙的 x。</summary>
        private float? FindWallDuringSweep(float xFrom, float xTo, float yLo, float yHi)
        {
            if (Math.Abs(xTo - xFrom) < 1e-4f) { return null; }
            if (yHi - yLo < 1f) { yHi = yLo + 1f; }   // 水平飞跃时给最小判定高度
            float? best = null;
            foreach (var f in _map!.Footholds)
            {
                if (Math.Abs(f.X1 - f.X2) >= 0.5f) { continue; }   // 只看垂直段
                float wx = f.X1;
                float lo = Math.Min(f.Y1, f.Y2), hi = Math.Max(f.Y1, f.Y2);
                if (hi - lo < 1f) { continue; }
                bool crossed = (xFrom < wx && xTo >= wx) || (xFrom > wx && xTo <= wx);
                bool yOverlap = yLo <= hi && yHi >= lo;
                if (!crossed || !yOverlap) { continue; }
                if (best == null || Math.Abs(wx - xFrom) < Math.Abs(best.Value - xFrom)) { best = wx; }
            }
            return best;
        }

        /// <summary>
        /// sdlMS 行走墙壁逻辑（physic::walk，fall=true）：段尽头无相邻段时**走出去掉落**（current_fh=0 进下落态），
        /// 而不是贴墙定住。端点外侧正下方存在落脚面 → 以空中下坠继续（水平惯性保留）；返回 false = 真无路 → 掉头。
        /// </summary>
        private bool TryWalkOffEdge(int facing)
        {
            if (_map == null || _walker == null) { return false; }
            var fh = FindFoothold(_map, _walker.FootholdId);
            if (fh == null) { return false; }
            float edgeX = facing > 0 ? Math.Max(fh.X1, fh.X2) + 2f : Math.Min(fh.X1, fh.X2) - 2f;
            foreach (var f in _map.Footholds)
            {
                if (Math.Abs(f.X1 - f.X2) < 0.5f) { continue; }
                float lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
                if (edgeX < lo || edgeX > hi) { continue; }
                float segY = MapWalker.YOn(f, edgeX);
                if (segY > _walker.Y + 4f) { return true; }   // 正下方有落脚面（低 4px 以上）→ 走出去掉落
            }
            return false;
        }

        /// <summary>从段尽头走出去进入下坠（水平惯性 = facing·AirHspeed，vspeed=0）。</summary>
        private bool StartWalkOffFall(int facing)
        {
            if (_walker == null) { return false; }
            _airHopIndex = -1;
            _airVy = 0f;
            _airVx = facing * AirHspeed;
            _airElapsed = 0;
            _airFromX = _walker.X;
            _airFromY = _walker.Y;
            _airFromFh = _walker.FootholdId;
            _transitionTargetFh = _walker.FootholdId;
            _transitionX = _walker.X;
            _transitionY = _walker.Y;
            _transitioning = true;
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
        /// 门猎手（覆盖模式的盆地脱困）：遍历本图同图传送门，找一扇「落点（Tn 目标门）侧可达集中
        /// 还有未走过段」的门，返回**落点段**作为腿目标——规划出的路由会自然经过门口传送边，
        /// 走到门即传（冷却中则等冷却/绕行），落地后从对面继续覆盖。
        /// </summary>
        private int FindUnvisitedDoorTarget(MapInfo map)
        {
            if (map == null) { return 0; }
            foreach (var p in map.Portals)
            {
                if (!p.HasTarget || !string.Equals(p.Tm, map.Id, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(p.Tn)) { continue; }
                MapPortal? dest = null;
                foreach (var q in map.Portals)
                {
                    if (string.Equals(q.PName, p.Tn, StringComparison.OrdinalIgnoreCase)) { dest = q; break; }
                }
                if (dest == null) { continue; }
                int destSeg = GoalAnchorFh(dest.X, dest.Y);
                if (destSeg == 0) { continue; }
                var beyond = WalkGraph.Reachable(map, destSeg);
                beyond.ExceptWith(_walkedFh);
                if (beyond.Count > 0) { return destSeg; }
            }
            return 0;
        }

        /// <summary>规划锚点：walker 当前段不在图里（垂直段/墙，物理链会走到）时吸附到最近图内段——
        /// 否则 Plan/Reachable/FarthestUnvisited 以该段为起点全部静默失败（706030400 底层平台链含
        /// 竖直小段实测：走到那儿之后覆盖腿目标选择整体瘫痪、只剩单链口径来回走）。</summary>
        private int PlanAnchorFh()
        {
            if (_map == null || _walker == null) { return 0; }
            int fh = _walker.FootholdId;
            return WalkGraph.InGraph(_map, fh) ? fh : WalkGraph.NearestInGraph(_map, _walker.X, _walker.Y);
        }

        /// <summary>目标锚点：目标点吸附段不在图里时吸附到最近图内段（同 <see cref="PlanAnchorFh"/> 口径）。</summary>
        private int GoalAnchorFh(float x, float y)
        {
            if (_map == null) { return 0; }
            var seg = NearestFoothold(_map, x, y);
            if (seg != null && WalkGraph.InGraph(_map, seg.Id)) { return seg.Id; }
            return WalkGraph.NearestInGraph(_map, x, y);
        }

        /// <summary>
        /// 建一条腿的路径与掉头阈值。死胡同覆盖模式（<see cref="_noNextPortal"/>）目标 = 可达集里
        /// 「未走过段」中 Dijkstra 距离最远者（<see cref="WalkGraph.FarthestUnvisited"/>）——纯水平
        /// extent 口径在「宽底层 + 窄上层」的分层图永远选中底层尽头，传送边连着的上层只在进图那一下
        /// 被路过、之后不再规划回去（706030400 实测：传到底层后 300s 不再上去，上层覆盖 ≈0）。
        /// 按最远未访段选目标后，传送边/绳成为覆盖路径的组成部分；全部走过 → 回退 extent 尽头口径
        /// （两遍覆盖退图流程不变，胶水规则 3）。有候选门的图维持 extent 尽头（贪心探索走 penalty）。
        /// </summary>
        private bool PlanLegToTarget(MapInfo map, int fromFootholdId, int facing, List<WalkHop> into, out double halfLen, Func<int, double>? penalty)
        {
            if (_noNextPortal)
            {
                into.Clear();
                halfLen = 0;
                var target = WalkGraph.FarthestUnvisited(map, fromFootholdId, _walkedFh);
                if (target == null)
                {
                    // 当前位置（按有向边）已无未访段：被单向落差/跳边困在盆地里（910510100 实测：
                    // 300s 覆盖零增长、反复横跳）。找一扇「落点侧还有未访段」的同图门，以落点段为
                    // 腿目标——Dijkstra 会自然选门口传送边；冷却中则罚金绕行/到门等冷却，均有限。
                    int doorTarget = FindUnvisitedDoorTarget(map);
                    if (doorTarget != 0)
                    {
                        var viaDoor = WalkGraph.Plan(map, fromFootholdId, doorTarget, penalty);
                        if (viaDoor is { Count: > 0 })
                        {
                            var startFh0 = FindFoothold(map, fromFootholdId);
                            var endFh0 = FindFoothold(map, doorTarget);
                            double traverse0 = WalkGraph.TraverseLength(map, fromFootholdId, viaDoor);
                            double span0 = startFh0 != null && endFh0 != null
                                ? Math.Abs((endFh0.X1 + endFh0.X2) / 2.0 - (startFh0.X1 + startFh0.X2) / 2.0)
                                : 0;
                            into.AddRange(viaDoor);
                            halfLen = Math.Max(span0, traverse0);
                            return true;
                        }
                    }
                }
                if (target.HasValue)
                {
                    var planned = WalkGraph.Plan(map, fromFootholdId, target.Value.FootholdId, penalty);
                    if (planned is { Count: > 0 })
                    {
                        var startFh = FindFoothold(map, fromFootholdId);
                        var endFh = FindFoothold(map, target.Value.FootholdId);
                        double traverse = WalkGraph.TraverseLength(map, fromFootholdId, planned);
                        double span = startFh != null && endFh != null
                            ? Math.Abs((endFh.X1 + endFh.X2) / 2.0 - (startFh.X1 + startFh.X2) / 2.0)
                            : 0;
                        double full = Math.Max(span, traverse);
                        if (full > 1)
                        {
                            into.AddRange(planned);
                            halfLen = full;
                            return true;
                        }
                    }
                }
            }
            return PlanHalfRoute(map, fromFootholdId, facing, into, out halfLen, penalty);
        }

        /// <summary>
        /// 掉头腿规划（§6 图口径，2026-09-15「掉头走回」版）：目标 = 朝向方向「可行走路径尽头」
        /// （<see cref="WalkGraph.RouteToEnd"/>），路径写入 <paramref name="into"/>，
        /// **掉头阈值 = 全程**（走到尽头才掉头；旧「半程瞬移」的 /2 口径已废弃）。
        /// 规划不出非空路径 → false（调用方退回单链口径）。
        /// </summary>
        private static bool PlanHalfRoute(MapInfo map, int startFootholdId, int facing, List<WalkHop> into, out double halfLen, Func<int, double>? segmentPenalty = null)
        {
            into.Clear();
            halfLen = 0;
            double full = WalkGraph.RouteToEnd(map, startFootholdId, facing, into, segmentPenalty);
            if (full <= 1 || into.Count == 0)
            {
                into.Clear();
                return false;
            }
            halfLen = full;
            return true;
        }

        /// <summary>单链兜底的掉头阈值：该朝向的整条链长（不再 /2）。</summary>
        private static double HalfLegLength(MapInfo map, int footholdId, int facing)
        {
            var a = MapWalker.Trace(map, footholdId, true);
            var b = MapWalker.Trace(map, footholdId, false);
            bool useForward = PickChainForwardForFacing(a, b, facing);
            return useForward ? a.Len : b.Len;
        }

        /// <summary>
        /// §3 方向选择（图口径）：比较两个方向「走到尽头」的全程（累计长度含跳跃/爬绳），取更长者；
        /// 两个方向都规划不出路 → false（调用方保持单链口径 <see cref="WalkPathPlanner.PickDirection"/> 的朝向）。
        /// </summary>
        private static bool TryPickFacingByRoute(MapInfo map, int startFootholdId, out int facing)
        {
            facing = 0;
            var tmp = new List<WalkHop>();
            double right = WalkGraph.RouteToEnd(map, startFootholdId, 1, tmp);
            double left = WalkGraph.RouteToEnd(map, startFootholdId, -1, tmp);
            if (right <= 1 && left <= 1) { return false; }
            facing = right >= left ? 1 : -1;
            return true;
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

        /// <summary>
        /// F4 兜底上绳收窄：绳底就在脚下（±20）或绳身穿过脚下平台（绳 x 落在平台范围内、
        /// 平台面 y 在绳的竖直包络内）才允许「顺手上绳」；防止把悬在半空/平台外侧的绳也抓上来。
        /// </summary>
        private bool FallbackRopeGrabbable(LadderRope rope)
        {
            if (_map == null || _walker == null) { return false; }
            if (Math.Abs(_walker.Y - rope.Bottom) <= MapWalker.RopeNearMaxY) { return true; }
            var foot = FindFoothold(_map, _walker.FootholdId);
            if (foot == null) { return false; }
            float l = Math.Min(foot.X1, foot.X2), r = Math.Max(foot.X1, foot.X2);
            if (rope.X < l || rope.X > r) { return false; }
            float platY = MapWalker.YOn(foot, rope.X);
            return platY >= rope.Top - MapWalker.RopeNearMaxY && platY <= rope.Bottom + MapWalker.RopeNearMaxY;
        }

        /// <summary>
        /// blocked 时的「贴邻跨跳」：当前跳的目标段最近端点 ≤120px（ResolveLink 归「走」的贴邻段
        /// 最大 dx 90 + 跳跃边最大 dy 77 的组合上限）→ 起跳过渡直接落到目标段。
        /// 目标常在正下方/斜下方（台阶、悬崖沿），不要求在行进方向前方。返回 false = 条件不满足（调用方掉头）。
        /// </summary>
        private bool TryHopOverGap(WalkHop hop)
        {
            if (_map == null || _walker == null || hop.FootholdId == _walker.FootholdId) { return false; }
            var target = FindFoothold(_map, hop.FootholdId);
            if (target == null) { return false; }
            double d = Math.Min(Distance(_walker.X, _walker.Y, target.X1, target.Y1),
                                Distance(_walker.X, _walker.Y, target.X2, target.Y2));
            if (d > 120) { return false; }
            return StartAirborne(hop);
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
            // Tn 匹配失败 → 本图里「同样指向来源图」的门中，坐标离 source 最近者
            //（⚠️ 绝不能拿 source 坐标直接在本图 NearestPortal：两张图坐标系不同，
            //  001 回 000 实测落错入口段 → 规划判 noNext → 人物在主街无限掉头走回）
            MapPortal? best = null;
            double bestD = double.MaxValue;
            foreach (var q in map.Portals)
            {
                if (!string.Equals(q.Tm, source.Tm, StringComparison.OrdinalIgnoreCase)) { continue; }
                double d = (q.X - source.X) * (q.X - source.X) + (q.Y - source.Y) * (q.Y - source.Y);
                if (d < bestD) { bestD = d; best = q; }
            }
            return best ?? PickEntryPortal(map);
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
        /// ladder / rope / walk 动作名兜底（对齐 PaperdollService.ResolveAction 口径）：
        /// 外观没有该动作时退回 stand1 等真实存在的动作，避免窗口层拿到空动作。
        /// walk（胶水 2026-09-15）：默认 walk1；当前武器在 walk1 下无法展示（无帧）而 walk2 可以时才用 walk2。
        /// </summary>
        private void ResolveActions()
        {
            var pd = _paperdoll;
            if (pd == null) { return; }
            try
            {
                _ladderAction = pd.HasAction(ActionLadder) ? ActionLadder : pd.ResolveAction(ActionLadder);
                _ropeAction = pd.HasAction(ActionRope) ? ActionRope : pd.ResolveAction(ActionRope);
                // walk 动作选择：武器能展示 walk1 就 walk1；不能而能展示 walk2 才 walk2；
                // 无武器 / 武器两个动作都无帧（武器不参与绘制）→ 按身体帧选，默认 walk1。
                bool bodyWalk2 = pd.HasAction(ActionWalk2);
                if (pd.WeaponHasAction(ActionWalk1))
                {
                    _walkAction = ActionWalk1;
                }
                else if (pd.WeaponHasAction(ActionWalk2) && bodyWalk2)
                {
                    _walkAction = ActionWalk2;
                }
                else
                {
                    _walkAction = pd.HasAction(ActionWalk1) ? ActionWalk1 : (bodyWalk2 ? ActionWalk2 : pd.ResolveAction(ActionWalk1));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMode] ResolveActions: {ex.Message}");
                _ladderAction = ActionLadder;
                _ropeAction = ActionRope;
                _walkAction = ActionWalk1;
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
            _airElapsed = 0;
            _ropeCooldown = 0;
            _lastRopeId = 0;
            _lastRopeX = 0;
            _ropeRegrabArmed = false;
            _ropeFromPlanned = true;
            _ropeStuckSec = 0;
            _midBlockedReplanCd = 0;
            _currentAction = ActionStand;
            _pausedAction = ActionStand;
            if (exited) { Exited?.Invoke(); }
        }
    }
}
