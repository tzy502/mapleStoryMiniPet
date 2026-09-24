using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MiniPet.Models;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace MiniPet.Services;

/// <summary>
/// 纸娃娃实时合成（ADR-0005，取代 ADR-0002 合成图条）。
/// 按 action/frame 逐帧合成 body/head/hair/face/equip 部件：耳朵按 head WZ 里以 "ear" 结尾的
/// canvas 名分组、按 earType 直接索引（人类靠 head 自带圆耳、空组不画）；表情 default/blink；
/// zmap 分层 + 基于 vslot 的遮挡锁。经 IEntityFrameProvider 接入 AnimService，
/// WZ 读取全部缓存（roots/bounds/meta/bmp），帧循环热路径只做组装与绘制。
/// </summary>
public class PaperdollService : IEntityFrameProvider
{
    /// <summary>纸娃娃在 AnimService 中的哨兵 mobId。</summary>
    public const string SentinelMobId = "__paperdoll__";

    // 耳朵类型 = head WZ canvas 名（对齐 MapleSalon2 CharacterEarType：src/const/ears.ts）
    public const string EarHuman = "humanEar";
    public const string EarElf = "ear";
    public const string EarLef = "lefEar";
    public const string EarHighLef = "highlefEar";
    public static readonly string[] AllEarTypes = { EarHuman, EarElf, EarLef, EarHighLef };

    // 表情
    public const string ExpressionDefault = "default";
    public const string ExpressionBlink = "blink";

    private readonly WzService _wz;
    private readonly CacheManager _cache;
    private readonly SpriteService _sprite;

    // 当前活动外观（主窗口渲染；IEntityFrameProvider 消费）。切换外观清空依赖外观的缓存。
    private CharacterAppearance? _current;
    private string _currentHash = "";

    // 表情驱动（预留 gateway：未来 Hermes/Claude/Codex 按 agent 状态注入）。
    public IExpressionDriver? ExpressionDriver { get; set; }

    // ═══════════════════════════════════════════
    // 表情（V0.2.x M1）：手动/自动表情状态
    //   PaperdollService 是 per-pet Transient（App.axaml.cs）→ 状态天然多宠隔离
    // ═══════════════════════════════════════════

    /// <summary>当前手动/自动播放中的表情（default = 无）。volatile：Tick 线程写、渲染线程读。</summary>
    private volatile string _manualExpression = ExpressionDefault;
    /// <summary>播放截止时刻（durationMs ≤ 0 时为 DateTime.MaxValue = 常驻）。</summary>
    private DateTime _manualExpireUtc = DateTime.MinValue;

    /// <summary>
    /// WZ 实测的 25 个表情（default + 24，见 docs/ai/PLAN-表情功能.md §2）。
    /// Friendly = 可进「随机表情」池；负面表情只能从菜单手动播。
    /// </summary>
    /// 顺序照抄 MapleSalon2 `const/emotions.ts:31` 的 CharacterExpressionsOrder（qBlue 是 GMS 特有，附在末尾）。
    /// Friendly = 进「随机表情」池（PLAN §4.4：只放友好表情，shine 不在池内）。
    public static readonly (string Key, string Cn, bool Friendly)[] KnownExpressions =
    {
        ("blink", "眨眼", false), ("hit", "受击", false), ("smile", "微笑", true),
        ("troubled", "烦恼", false), ("cry", "哭", false), ("angry", "生气", false),
        ("bewildered", "慌张", false), ("stunned", "发晕", false), ("vomit", "呕吐", false),
        ("oops", "哎呀", true), ("cheers", "欢呼", true), ("chu", "亲亲", true),
        ("wink", "眨眼（俏皮）", true), ("pain", "痛苦", false), ("glitter", "闪亮", true),
        ("despair", "绝望", false), ("love", "爱心", true), ("shine", "闪耀", false),
        ("blaze", "怒火", false), ("hum", "哼歌", true), ("bowing", "鞠躬", false),
        ("hot", "热", false), ("dam", "郁闷", false), ("default", "默认", false),
        ("qBlue", "蓝色问号", false),
    };

    /// <summary>
    /// 播放表情，durationMs 后自动回默认（≤0 = 常驻直到 <see cref="CancelExpression"/>）。
    /// 表情只换**脸图层内容**，不打断 body 动作（不进 AnimService 状态机）。
    /// 帧源缓存 key 已含 expression（CollectPiecesForFrame），换表情零额外失效成本。
    /// </summary>
    public void PlayExpression(string expr, int durationMs = 3000)
    {
        _manualExpression = string.IsNullOrEmpty(expr) ? ExpressionDefault : expr;
        _manualExpireUtc = durationMs > 0 ? DateTime.UtcNow.AddMilliseconds(durationMs) : DateTime.MaxValue;
    }

    /// <summary>立即回默认表情。</summary>
    public void CancelExpression()
    {
        _manualExpression = ExpressionDefault;
        _manualExpireUtc = DateTime.MinValue;
    }

    /// <summary>当前生效的表情（诊断/测试用）。</summary>
    public string CurrentExpression => ResolveExpression();

    /// <summary>手动表情是否在生效中。</summary>
    public bool IsExpressionActive =>
        !string.Equals(_manualExpression, ExpressionDefault, StringComparison.Ordinal)
        && DateTime.UtcNow < _manualExpireUtc;

    // ── M2 自动表情时钟（眨眼 / 随机表情）──
    private DateTime _nextBlinkUtc = DateTime.MinValue;
    private DateTime _nextRandomUtc = DateTime.MinValue;
    private readonly Random _exprRng = new();

    /// <summary>自动眨眼（待机每 3~6s 眨一次，≈180ms）。菜单可关。</summary>
    public bool AutoBlinkEnabled { get; set; } = true;
    /// <summary>随机表情（待机每 20~45s 播一个友好表情，3s）。菜单可开，默认关。</summary>
    public bool RandomExpressionEnabled { get; set; }
    /// <summary>抑制随机表情（行走/攀爬、拖拽、Agent 忙碌时由外部置位；眨眼不受影响）。</summary>
    public bool SuppressRandomExpression { get; set; }

    /// <summary>
    /// 表情时钟：由外部每帧驱动（PetWindow 33ms 定时器）。
    /// 手动表情播放期间不打扰；眨眼与随机表情各自独立计时。
    /// </summary>
    public void TickExpressionClock()
    {
        try
        {
            if (IsExpressionActive)
            {
                return;
            }
            var now = DateTime.UtcNow;
            if (AutoBlinkEnabled && now >= _nextBlinkUtc)
            {
                _nextBlinkUtc = now.AddMilliseconds(_exprRng.Next(3000, 6000));
                PlayExpression(ExpressionBlink, 180);
                return;
            }
            if (RandomExpressionEnabled && !SuppressRandomExpression)
            {
                if (_nextRandomUtc == DateTime.MinValue)
                {
                    // 首次开启也要等 20~45s（不能 immediate 播一个，验收 §4 指出过）
                    _nextRandomUtc = now.AddMilliseconds(_exprRng.Next(20000, 45000));
                }
                else if (now >= _nextRandomUtc)
                {
                    _nextRandomUtc = now.AddMilliseconds(_exprRng.Next(20000, 45000));
                    var pool = KnownExpressions.Where(e => e.Friendly).ToArray();
                    if (pool.Length > 0)
                    {
                        PlayExpression(pool[_exprRng.Next(pool.Length)].Key, 3000);
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Paperdoll] TickExpressionClock: {ex.Message}"); }
    }

    /// <summary>面向（true=朝左，水平翻转渲染；默认朝右）。</summary>
    public bool Flip { get; set; }

    // zmap
    private List<string>? _zmapCache;
    // 胶水 2026-08-17 修复「猩红利刃等武器渲染失败」：_zmapIndex 原为普通 Dictionary，
    // ResolvePrepLayer 在渲染线程动态写新层（L1618）与后台预热/离线构建线程的 GetZmapTopToBottom
    // 重建索引（L1935）并发 → 字典状态损坏 → RenderFrame 抛「non-concurrent collections」→ 部件渲染中断。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _zmapIndex = new();

    // 缓存（ConcurrentDictionary：后台预热/离线构建与 UI 帧合成并发安全，防抖动/竞态）
    private readonly ConcurrentDictionary<string, List<(string Category, string Root)>> _rootCache = new();
    private readonly ConcurrentDictionary<string, Bounds> _boundsCache = new();
    private readonly ConcurrentDictionary<string, List<PieceSource>> _frameSourcesCache = new();
    private readonly ConcurrentDictionary<string, PieceMeta> _metaCache = new();
    private readonly ConcurrentDictionary<string, SKBitmap> _bmpCache = new();
    // 2026-08-16 轻量尺寸缓存（冰凌披风特效卡顿优化）：GetBounds 只需要宽高，走 GetPngSize
    // （不编码不解码），避免特效 155x140 大图在动作边界计算时全量 ExtractPng + SKBitmap.Decode
    private readonly ConcurrentDictionary<string, (int W, int H)> _sizeCache = new();
    private readonly ConcurrentDictionary<string, SKBitmap> _dyeBmpCache = new();
    private readonly ConcurrentDictionary<string, int> _delayCache = new();
    // 2026-08-16 帧位移 move 缓存（神之子头冠卡顿修复）：动画帧 RenderFrame 原每帧 FindNodeByPath 抢锁，
    // 后台预热特效帧首次解码（4.2s 持锁）时动画被阻塞 → move 查询结果缓存，动画热帧零锁
    private readonly ConcurrentDictionary<string, Wz_Vector?> _moveCache = new();
    // 坐骑/椅子动作帧数缓存（R1：CollectPiecesForFrame 每帧查一次，避免重复 WZ 遍历）
    private readonly ConcurrentDictionary<string, int> _rideFrameCountCache = new();
    // 坐骑 characterAction 映射缓存（R18：坐骑动作名 → 角色动作名，MapleSalon2 tamingMob.ts 同源；
    // 反向映射供 ResolveMountAction 选动作，hideBody 供隐藏角色身体；坐骑数据不随外观变，WZ 重载后随 SetAppearance 清）
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _mountCharActionCache = new();
    // 坐骑 characterAction 反向映射缓存（R18：角色动作名 → 坐骑动作名；见 GetMountCharActionReverse）
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _mountCharActionReverseCache = new();
    // 诊断去重（R5/R6：body 未锁定 / locked.Count==0 只打一次，防 30fps 刷屏）
    private readonly ConcurrentDictionary<string, byte> _loggedFrameFailures = new();
    // 椅子渲染缓存（R-chair：坐姿动作/特效层帧数/pos/z/tamingMob 只读一次，动画热帧零锁）
    private readonly ConcurrentDictionary<string, string> _chairSitActionCache = new();
    private readonly ConcurrentDictionary<string, int> _chairEffectFrameCountCache = new();
    private readonly ConcurrentDictionary<string, int> _chairEffectPosCache = new();
    private readonly ConcurrentDictionary<string, int> _chairEffectZCache = new();
    private readonly ConcurrentDictionary<string, bool> _chairTamingMobCache = new();
    // 装备真实 vslot/islot 读取缓存（itemId → 槽串；null=WZ 无数据，回退硬编码默认）。
    // 2026-08-17 修复：遮挡锁原用硬编码 vslot（帽 CpH1H5 等），cap 1003953 真实 vslot=CpH5
    // （只占 Cp+H5，不锁前发 H1）却被硬编码锁 H1 → 前发 hairOverHead 被错误隐藏（用户反馈
    // 「查看现在用的搭配发型展示有问题」）。SetAppearance 清空（WZ 重载后经 SetAppearance 重建）。
    private readonly ConcurrentDictionary<string, string?> _vslotCache = new();
    private readonly ConcurrentDictionary<string, string?> _islotCache = new();

    /// <summary>位图缓存容量上限（R11）：超过即释放并清一半，防无界常驻（简化 LRU，无访问序维护）。</summary>
    /// <summary>位图缓存容量上限（2026-08-16 512→2048：WarmupFor 换装预热预解码新外观全部位图
    /// （含特效 18 帧大图）时，512 上限触发无序遍历逐出一半——旧外观动画位图被逐出 → 动画每帧
    /// 重新 ExtractPng 撞预热锁卡数秒；2048 容纳两套外观 + 特效余量，实测特效帧 87KB×18 ≈ 1.5MB）。</summary>
    private const int MaxBmpCache = 2048;

    // zmap 不可用时回退（顶→底）
    // R13：按本 WZ 真实 zmap（183 层）修正披风相关层——身前披风层（capeOverHead/capeOverArm/capeOverFace/cape）
    // 在中上部、身后披风层（capeBelowWeapon/capeBelowBody/backHairOverCape/backCape）在 body 之下；
    // 原 fallback 用 "capeBack"（真实层名是 backCape）且 "effect" 在最后=最底（MapleSalon2 是置顶）→ 已修正。
    private static readonly string[] FallbackZmapTopToBottom =
    {
        "effect", "mobEquipFront", "capeOverHead", "capeOverArm", "capeOverFace",
        "cap", "hairOverHead", "weaponOverHair", "cape", "shield", "weapon", "gloveOver", "glove",
        "coat", "pants", "shoe", "hand", "arm", "capeBelowWeapon", "capeBelowBody",
        "backHairOverCape", "backCape", "tail", "face", "ear",
        "head", "body", "hairBelowHead", "back"
    };

    public PaperdollService(WzService wz, CacheManager cache, SpriteService sprite)
    {
        _wz = wz;
        _cache = cache;
        _sprite = sprite;
    }

    public CharacterAppearance? Current => _current;
    public string CurrentHash => _currentHash;

    /// <summary>
    /// 离线条带磁盘缓存 mobId（R4）：哨兵 + 外观 hash 后缀——换外观即换键自动失效，
    /// 旧外观的离线条永远不会被新外观播放（修复「换外观后离线仍播旧外观」）。
    /// AnimService 离线兜底经 provider 取此键加载（见 AnimService.LoadStripWithFallback）。
    /// </summary>
    public string OfflineCacheMobId => string.IsNullOrEmpty(_currentHash) ? SentinelMobId : $"{SentinelMobId}_{_currentHash}";

    /// <summary>设置当前外观（主窗口渲染目标）。切换外观清空依赖外观的缓存。</summary>
    public void SetAppearance(CharacterAppearance appearance)
    {
        CancelExpression();   // 换外观回到默认表情（避免旧外观的表情残留）
        _current = appearance;
        _currentHash = HashAppearance(appearance);
        _zmapCache = null;
        _boundsCache.Clear();
        _frameSourcesCache.Clear();
        _rootCache.Clear();
        _delayCache.Clear();
        _rideFrameCountCache.Clear();
        _mountCharActionCache.Clear();
        _mountCharActionReverseCache.Clear();
        _chairSitActionCache.Clear();
        _chairEffectFrameCountCache.Clear();
        _chairEffectPosCache.Clear();
        _chairEffectZCache.Clear();
        _chairTamingMobCache.Clear();
        _vslotCache.Clear();
        _islotCache.Clear();
        _loggedFrameFailures.Clear();
        _validActionsCache = null;
        _actionValidCache.Clear();
    }

    public void ClearBitmapCache()
    {
        foreach (var b in _bmpCache.Values) b.Dispose();
        _bmpCache.Clear();
        _metaCache.Clear();
    }

    /// <summary>
    /// 层解析诊断（CapeProbe 用）：跑完整 CollectPieces→ResolveAnchors→AssignLayers→ApplyLocks 管线，
    /// 返回每部件落层结果（真实代码路径，与渲染一致；不合成位图）。修复前后对照由同一方法给出。
    /// </summary>
    public List<LayerProbeItem> ProbeLayerResolution(CharacterAppearance a, string action, int frame)
    {
        var result = new List<LayerProbeItem>();
        if (a == null) return result;
        try
        {
            var hash = HashAppearance(a);
            var pieces = MaterializePieces(CollectPiecesForFrame(hash, a, action, frame, ExpressionDefault), action, frame);
            ResolveAnchors(pieces);
            AssignLayers(pieces);
            HideFrontFaceLayersOnBackAction(pieces, a, action, frame);
            ApplyLocks(hash, a, pieces);
            foreach (var p in pieces)
            {
                result.Add(new LayerProbeItem
                {
                    Category = p.Category,
                    PieceName = p.PieceName,
                    ZField = p.ZField,
                    ResolvedLayer = p.ResolvedLayer,
                    ZIndex = p.ZIndex,
                    Locked = p.Locked,
                    Hidden = p.Hidden,
                    HasMap = p.Map.Count > 0
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] ProbeLayerResolution: {ex.Message}"); }
        return result;
    }

    // ═══════════════════════════════════════════
    // IEntityFrameProvider（AnimService 消费）
    // ═══════════════════════════════════════════

    public bool HasAction(string action) => _current != null && GetActionFrameCount(BodyRoot(_current), action) > 0;

    public int GetFrameCount(string action) => _current == null ? 0 : GetActionFrameCount(BodyRoot(_current), action);

    public int GetFrameDelay(string action, int frame)
    {
        if (_current == null) return 100;
        string key = $"{action}|{frame}";
        if (_delayCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            int delay = _wz.GetDelay($"{BodyRoot(_current)}/{action}/{frame}");
            if (delay <= 0) delay = 100;
            // 限速上限 300ms（WZ 极慢动作如 500ms 呼吸太像静止；正常 walk/swing 100ms 不变）
            if (delay > 300) delay = 300;
            _delayCache[key] = delay;
            return delay;
        }
        catch { return 100; }
    }

    public List<string> GetAvailableActions()
    {
        // 只加载 MapleSalon2 定义的动作集（actions.ts 的 26 个），不遍历 body 全部动作
        // （避免几百个无用/技能节点被加载；有帧的才加入）
        if (_validActionsCache != null) return _validActionsCache;
        var valid = new List<string>();
        foreach (var a in ActionNames.Keys)
        {
            // 缓存键含外观 hash（R12）：换外观自动失效，且并发预览（不同外观）不串台
            string key = $"{_currentHash}|{a}";
            bool ok;
            if (!_actionValidCache.TryGetValue(key, out ok)) { ok = IsActionValid(a); _actionValidCache[key] = ok; }
            if (ok) valid.Add(a);
        }
        _validActionsCache = valid;
        return valid;
    }

    private bool IsActionValid(string action)
    {
        try
        {
            if (_current == null) return false;
            if (GetActionFrameCount(BodyRoot(_current), action) <= 0) return false;
            return GetBounds(_currentHash, _current, action).W > 0;
        }
        catch { return false; }
    }

    /// <summary>某动作的 union 尺寸与公共 origin（窗口/锚点基准用；WZ 不可用返回 0）。</summary>
    public (int w, int h, int originX, int originY) GetActionBounds(string action)
    {
        if (_current == null) return (0, 0, 0, 0);
        var b = GetBounds(_currentHash, _current, action);
        return (b.W, b.H, -b.Left, -b.Top);
    }

    /// <summary>动作解析：stand/walk 武器变体（单手 stand1/walk1、双手 stand2/walk2），move→walk1，fly→jump。</summary>
    public string ResolveAction(string requested)
    {
        if (string.IsNullOrEmpty(requested)) return requested;
        if (HasAction(requested)) return requested;
        if (requested == "stand" || requested == "walk")
        {
            bool twoHand = IsTwoHandWeapon(_current?.Weapon?.Id);
            string primary = (requested == "stand" ? "stand" : "walk") + (twoHand ? "2" : "1");
            if (HasAction(primary)) return primary;
            string alt = (requested == "stand" ? "stand" : "walk") + (twoHand ? "1" : "2");
            if (HasAction(alt)) return alt;
        }
        if (requested == "move" && HasAction("walk1")) return "walk1";
        if (requested == "fly" && HasAction("jump")) return "jump";
        var actions = GetAvailableActions();
        if (actions.Count > 0) return actions.Contains("stand1") ? "stand1" : actions[0];
        return requested;
    }

    /// <summary>逻辑动作名 → 实际动作（按传入外观 a 的武器单/双手选 stand1/2、walk1/2；不依赖 _current，
    /// 预览草稿外观与主窗口当前外观解耦）。胶水 2026-08-18：预览姿势默认 stand 自动解析。</summary>
    private string ResolveLogicalAction(CharacterAppearance a, string requested)
    {
        if (string.IsNullOrEmpty(requested)) { return requested; }
        string bodyRoot = BodyRoot(a);
        bool Has(string act) => GetActionFrameCount(bodyRoot, act) > 0;
        if (Has(requested)) { return requested; }
        if (requested == "stand" || requested == "walk")
        {
            bool twoHand = IsTwoHandWeapon(a.Weapon?.Id);
            string primary = (requested == "stand" ? "stand" : "walk") + (twoHand ? "2" : "1");
            if (Has(primary)) { return primary; }
            string alt = (requested == "stand" ? "stand" : "walk") + (twoHand ? "1" : "2");
            if (Has(alt)) { return alt; }
        }
        return requested;
    }

    /// <summary>双手武器（141-146 双手剑/斧/钝器、170 枪）→ 用 stand2/walk2 变体。</summary>
    private static bool IsTwoHandWeapon(string? weaponId)
    {
        if (string.IsNullOrEmpty(weaponId) || !int.TryParse(weaponId, out var n)) return false;
        int part = n / 10000;
        return (part >= 141 && part <= 146) || part == 170;
    }

    /// <summary>
    /// 当前武器在该动作下是否有帧（weapon 根节点下 action 含数字帧；无武器/无效 id → false）。
    /// 与渲染路径同条件：CollectPiecesForFrame 对装备检查 {root}/{action}/{frame} 存在才收部件
    /// （不存在则该动作不会画出这把武器）——用于「该动画会不会展示这个武器」的可用性判断
    /// （AnimService 空闲随机展示动作过滤；无武器时不选武器展示动画）。
    /// </summary>
    public bool WeaponHasAction(string action)
    {
        if (_current?.Weapon == null || string.IsNullOrEmpty(_current.Weapon.Id)) return false;
        string root = ItemRoot(_current.Weapon.Id);
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            return _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath($"{root}/{action}");
                if (node == null) return false;
                foreach (Wz_Node child in node.Nodes)
                {
                    if (int.TryParse(child.Text, out _)) return true;
                }
                return false;
            });
        }
        catch { return false; }
    }

    public (SKBitmap? Frame, int OriginX, int OriginY, int FrameW, int FrameH) RenderFrame(string action, int frame)
    {
        if (_current == null) return (null, 0, 0, 0, 0);
        return RenderFrame(_current, action, frame, ResolveExpression());
    }

    // ═══════════════════════════════════════════
    // 实时合成
    // ═══════════════════════════════════════════

    /// <summary>
    /// 合成单帧到 union canvas（该 action 各帧共用联合画布 + 公共 origin，窗口不抖）。
    /// appearance 用不可变快照（草稿/主窗口并发安全，F7）。
    /// </summary>
    public (SKBitmap? Frame, int OriginX, int OriginY, int FrameW, int FrameH) RenderFrame(
        CharacterAppearance a, string action, int frame, string expression)
    {
        if (a == null) return (null, 0, 0, 0, 0);
        try
        {
            var hash = HashAppearance(a);
            var bounds = GetBounds(hash, a, action);
            if (bounds.W <= 0 || bounds.H <= 0)
            {
                Console.Error.WriteLine($"[Paperdoll] RenderFrame FAIL: action={action} frame={frame} bounds={bounds.W}x{bounds.H} hash={hash}");
                return (null, 0, 0, 0, 0);
            }

            var pieces = MaterializePieces(CollectPiecesForFrame(hash, a, action, frame, expression), action, frame);
            ResolveAnchors(pieces);
            // R5 诊断：锚点解算后 body 未锁定（数据缺失/断件）——只打一次，防 30fps 刷屏
            var bodyDiagnostic = pieces.FirstOrDefault(p => p.Category == "body");
            if ((bodyDiagnostic == null || !bodyDiagnostic.Locked)
                && _loggedFrameFailures.TryAdd($"{hash}|body-unlocked", 0))
            {
                Console.Error.WriteLine($"[Paperdoll] ResolveAnchors: body 未锁定（body 数据缺失）hash={hash} action={action} frame={frame} pieces={pieces.Count}");
            }
            AssignLayers(pieces);
            HideFrontFaceLayersOnBackAction(pieces, a, action, frame);
            // 遮挡锁：帽子遮发 / 套服遮裤等覆盖关系（对齐 MapleSalon2 buildLock/refreshLock）
            ApplyLocks(hash, a, pieces);

            var locked = pieces.Where(p => p.Locked && !p.Hidden).ToList();
            if (locked.Count == 0)
            {
                // R6 诊断：locked.Count==0 静默空帧 → 打日志（含 hash/action/frame），只打一次防刷屏
                if (_loggedFrameFailures.TryAdd($"{hash}|{action}|{frame}", 0))
                {
                    Console.Error.WriteLine($"[Paperdoll] RenderFrame FAIL: locked.Count==0 hash={hash} action={action} frame={frame} pieces={pieces.Count}");
                }
                return (null, 0, 0, 0, 0);
            }

            // 帧位移 move（对齐 MapleSalon2 instruction.move）：WZ 每帧有 move 位移数据，
            // 角色位置由 move 决定（平滑）；忽略它则位置纯靠锚点（body 每帧 navel 变 → 呼吸整体摆 → 抖动）
            // 2026-08-16：move 查询走缓存（神之子头冠卡顿修复）——动画帧 RenderFrame 原每帧
            // FindNodeByPath 抢 _wzLock，后台预热特效帧（首次 ExtractPng 4.2s 持锁）时动画被阻塞。
            int mvX = 0, mvY = 0;
            var mv = GetFrameMove(hash, a, action, frame);
            if (mv != null) { mvX = mv.X; mvY = mv.Y; }

            // 画布原点 = body 锚点（照抄 MapleSalon2 updateCharacterPivotByBodyPiece：bodyFrame.pivot = bodyFrame.ancher）
            var bodyP = locked.FirstOrDefault(p => p.Category == "body");
            int bx = bodyP?.AnchorX ?? 0;
            int by = bodyP?.AnchorY ?? 0;

            var bmp = new SKBitmap(bounds.W, bounds.H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            // 面向：水平翻转（绕画布中心镜像）
            if (Flip)
            {
                canvas.Save();
                canvas.Translate(bounds.W / 2f, 0);
                canvas.Scale(-1, 1);
                canvas.Translate(-bounds.W / 2f, 0);
            }
            // z 大（底）先画，z 小（顶）后画；piece 相对 body 锚点平移到画布 + 帧位移 move
            foreach (var p in locked.OrderByDescending(p => p.ZIndex))
            {
                var pb = GetBmp(p.PiecePath);
                if (pb == null) continue;
                var dyeBmp = GetDyeBmp(a, p.Category, p.PiecePath);
                if (dyeBmp != null)
                    canvas.DrawBitmap(dyeBmp, p.FinalX - bx - bounds.Left + mvX, p.FinalY - by - bounds.Top + mvY);
                else
                    canvas.DrawBitmap(pb, p.FinalX - bx - bounds.Left + mvX, p.FinalY - by - bounds.Top + mvY);
            }
            if (Flip) canvas.Restore();
            // 翻转后 origin 镜像（水平）
            int outOriginX = Flip ? bounds.W + bounds.Left : -bounds.Left;
            return (bmp, outOriginX, -bounds.Top, bounds.W, bounds.H);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PaperdollService] RenderFrame({action}/{frame}): {ex.Message}");
            return (null, 0, 0, 0, 0);
        }
    }

    /// <summary>合成单帧输出 PNG（设置中心预览用；透明背景）。</summary>
    public byte[]? CompositeFrame(CharacterAppearance a, string action, int frame, string expression = ExpressionDefault)
    {
        if (a == null) return null;
        var (bmp, _, _, _, _) = RenderFrame(a, action, frame, expression);
        if (bmp == null) return null;
        try
        {
            using var image = SKImage.FromBitmap(bmp);
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] CompositeFrame: {ex.Message}"); return null; }
        finally { bmp.Dispose(); }
    }

    // ═══════════════════════════════════════════
    // 部件收集（WZ 读取在此，结果缓存 per hash|action|frame|expression）
    // ═══════════════════════════════════════════

    private List<PieceSource> CollectPiecesForFrame(string hash, CharacterAppearance a, string action, int frame, string expression)
    {
        string key = $"{hash}|{action}|{frame}|{expression}";
        if (_frameSourcesCache.TryGetValue(key, out var cached)) return cached;

        // R-chair：带椅子时角色渲染为坐姿（对齐 MapleSalon2 sitCharacter：groupData.action = info.sitAction || Sit）。
        // 角色部件用坐姿帧（body sit 只有 1 帧，帧号 clamp）；椅子特效帧仍用原始帧号驱动
        // （probe 实测 53% 椅子 effect 多帧，随角色动画循环，不冻结在 0 帧）。
        // R20 骑乘：坐骑 characterAction 映射叠加在坐姿之上——01930001 等飞行坐骑角色应播映射动作
        // （stand1→fly），坐骑部件由 AddMountPieces 按原角色动作独立解析（保持 MapleSalon2 语义）。
        var (renderAction, renderFrame) = ResolveRenderAction(a, action, frame);
        // 背面动作判定（对齐 sdlMS character_render_system.cpp:129 `bone_data[action][frame].face` 与
        // MapleSalon2 CharacterFaceFrame.isBackAction）：body 帧 face=0（爬绳 ladder/rope 等）→ 不渲染
        // 脸型与 101 面饰，否则后脑勺上会贴一张正脸（「背部有眼睛」）。
        // isHideFace（MapleSalon2 characterFaceFrame.ts:106-109）：body 动作为 blink / hide 时也不画脸
        bool faceVisible = IsBodyFaceVisible(a, renderAction, renderFrame)
                           && action != "blink" && action != "hide";
        // invisibleFace（MapleSalon2 item.ts:311）：穿戴物 info.invisibleFace == 1（面罩类）→ 覆盖脸型
        bool overrideFace = IsFaceOverriddenByEquipment(hash, a);

        var sources = new List<PieceSource>();
        // R18 hideBody：坐骑动作映射 hideBody（characterAction[坐骑动作] == "hideBody"，坐骑完全覆盖角色）
        // → 只收集坐骑部件，跳过 body/head/hair/face 等角色部件（对齐 MapleSalon2 isHideBodyAction 处理）。
        bool mountHideBody = IsMountHideBodyForAction(a, action);
        foreach (var (category, root) in GetRoots(hash, a))
        {
            if (mountHideBody && category != "mount") continue;
            // R1 坐骑：TamingMob/{id}.img/{mountAction}/{mountFrame}/0 —— 结构同角色部件（origin/map.navel/z），
            // 走同一 Piece 管线；动作缺省时回退 stand1/stand2（骑乘姿态），帧号 clamp 到坐骑帧数（最小联动）。
            if (category == "mount")
            {
                AddMountPieces(sources, a, root, action, frame);
                continue;
            }
            // R1 椅子：Item/Install|Cash/{img}.img/{id}/effect|effect2|effect3/{frame} —— effect 帧节点本身是单 canvas（Wz_Png），
            // 无 map 锚点（ResolveAnchors 兜底锚到身体 navel）；帧号 clamp 到特效帧数。
            // R-chair：坐姿渲染时椅子特效帧用原始帧号（多帧特效随动画循环）。
            if (category == "chair")
            {
                AddChairPieces(sources, a, root, frame);
                continue;
            }
            // R14 脸型：表情结构（对齐 MapleSalon2 CharacterFaceItem）——default 无数字帧（直接 piece），
            // blink/angry 等为数字帧（blink/0/face…）；帧号取模循环（对齐 MapleSalon2 frame % frames.length）。
            if (category == "face")
            {
                if (!faceVisible || overrideFace) { continue; }
                AddExpressionPieces(sources, a, category, root, ResolveFaceAction(root, expression), frame);
                continue;
            }
            // R14 饰品三分类（101 面饰 / 102 眼饰 / 103 耳环，对齐 MapleSalon2 拆分，可同时穿戴）：
            // 动作结构优先，动作缺失回退 default 静态帧；101 面饰与脸型同构（表情结构 default/blink…），走表情收集。
            if (category == "faceaccessory")
            {
                // 101 面饰与脸型同层：背面动作时随脸一起隐藏
                // （对齐 MapleSalon2 item.ts isFaceAccessory = isFace || isFaceAccessory）
                if (!faceVisible) { continue; }
                AddAccessoryPieces(sources, a, category, root, renderAction, expression, renderFrame, faceVisible);
                continue;
            }
            if (category is "eyeaccessory" or "earring")
            {
                // 102 眼饰 / 103 耳环不属于 faceAccessory：背面动作保留（与 MapleSalon2 一致）；
                // 但静态回退时要走 backDefault（103 耳环自带背面粉）
                AddAccessoryPieces(sources, a, category, root, renderAction, expression, renderFrame, faceVisible);
                continue;
            }

            string framePath = $"{root}/{renderAction}/{renderFrame}";
            if (!_wz.WithWzLock(() => _wz.FindNodeByPath(framePath) != null))
            {
                // 胶水 2026-08-18 修复「摄魂之剑渲染失败」：多皮肤武器 img 顶层是编号目录（如 01703683.img
                // 下挂 30/49 皮肤目录，动作在其中：30/stand1/0/weapon），直接查 {root}/{action} 落空 →
                // 整个武器被跳过（不画）。下钻：img 下第一个含该动作的编号目录（30 优先 49，皮肤序）。
                string? skinDir = null;
                foreach (var sub in _wz.GetFramePieceNames(root))
                {
                    if (!int.TryParse(sub, out _)) { continue; }
                    if (_wz.WithWzLock(() => _wz.FindNodeByPath($"{root}/{sub}/{renderAction}") != null))
                    {
                        skinDir = sub;
                        break;
                    }
                }
                if (skinDir == null) { continue; }
                framePath = $"{root}/{skinDir}/{renderAction}/{renderFrame}";
                if (!_wz.WithWzLock(() => _wz.FindNodeByPath(framePath) != null)) { continue; }
            }

            string itemId = GetCategoryItemId(a, category);
            foreach (var pieceName in _wz.GetFramePieceNames(framePath))
            {
                // 耳朵互斥：head 中 key 以 "ear" 结尾的 canvas 按 earType 取组（对齐 MapleSalon2 EarReg /ear$/i）
                if (category == "head" && IsEarPiece(pieceName) && pieceName != (a.Ear ?? EarHuman)) continue;
                // R13 装备特效开关：帧级 "effect"/"effectN" 部件视为特效帧（对齐 MapleSalon2 isKindOfEffect），关闭时跳过
                if (IsEffectPieceName(pieceName) && !a.EnableEffect) continue;
                string piecePath = $"{framePath}/{pieceName}";
                // 2026-08-16 发型 canvas 列表解析：部分发型部件（hairShade 等）帧内 UOL 目标是
                // 「canvas 列表」容器（如 default/hairShade 下挂 {0,1,2,...} 数字子节点），
                // 直接按容器路径取 PNG/origin/map/z 为空 → 部件被收集但永不绘制（该显示不显示）。
                if (category == "hair")
                {
                    // R-chair：坐姿渲染时槽位取坐姿帧（sit 只有 1 帧）
                    piecePath = ResolveHairPieceCanvasPath(piecePath, renderFrame.ToString());
                }
                sources.Add(new PieceSource(category, pieceName, piecePath, itemId));
            }
        }

        // R13 节点级装备特效：{root}/effect（真实客户端披风/武器特效结构，如 110249；本 WZ 披风仅帧级 effect）。
        // 按 EnableEffect 开关收集，effect 帧 clamp 到特效自身帧数（最小可用联动，同椅子 effect 的处理）。
        // R18：hideBody 时角色部件全隐藏，装备特效同样不收集（避免悬空显示）。
        if (a.EnableEffect && !mountHideBody)
        {
            foreach (var (category, root) in GetRoots(hash, a))
            {
                if (category is "body" or "head" or "hair" or "face" or "mount" or "chair") continue;
                // R-chair：带椅子坐姿渲染时节点级特效用坐姿动作（无坐姿帧则回退数字帧结构）
                AddEquipEffectPieces(sources, a, category, root, renderAction, frame);
                AddItemEffEffectPieces(sources, a, category, frame);
            }
        }

        _frameSourcesCache[key] = sources;
        return sources;
    }

    /// <summary>
    /// 表情结构部件收集（脸型 + 101 面饰，对齐 MapleSalon2 CharacterFaceItem）：
    /// 结构 A：{root}/{expr}/{frame}/{piece} —— blink/angry 等数字帧（blink/0/face…），帧号取模循环
    /// （对齐 MapleSalon2 getPiecesByFrame 的 frame % frames.length）；本 WZ probe 实测：face blink 有 0/1/2 三帧。
    /// 结构 B：{root}/{expr}/{piece} —— default 无数字帧，piece 直接挂在表情节点下（default/face）。
    /// </summary>
    private void AddExpressionPieces(List<PieceSource> sources, CharacterAppearance a, string category, string root, string expression, int frame)
    {
        string itemId = GetCategoryItemId(a, category);
        string exprPath = $"{root}/{expression}";
        if (!_wz.WithWzLock(() => _wz.FindNodeByPath(exprPath) != null)) return;

        // 结构 A：数字帧
        int numericFrames = CountNumericChildren(exprPath);
        if (numericFrames > 0)
        {
            int f = frame % numericFrames;
            string framePath = $"{exprPath}/{f}";
            if (!_wz.WithWzLock(() => _wz.FindNodeByPath(framePath) != null)) return;
            foreach (var pieceName in _wz.GetFramePieceNames(framePath))
            {
                if (pieceName == "delay" || pieceName == "move") continue;
                if (IsEffectPieceName(pieceName) && !a.EnableEffect) continue;
                sources.Add(new PieceSource(category, pieceName, $"{framePath}/{pieceName}", itemId));
            }
            return;
        }
        // 结构 B：直接 piece（default/face）
        foreach (var pieceName in _wz.GetFramePieceNames(exprPath))
        {
            if (pieceName == "delay" || pieceName == "move" || pieceName == "info") continue;
            if (IsEffectPieceName(pieceName) && !a.EnableEffect) continue;
            sources.Add(new PieceSource(category, pieceName, $"{exprPath}/{pieceName}", itemId));
        }
    }

    /// <summary>
    /// 饰品部件收集（101 面饰 / 102 眼饰 / 103 耳环，对齐 MapleSalon2 分类，category 传三路各自分类名）：
    /// ① 动作结构（102/103 实测：{root}/stand1/0/default，piece 为 uol 指向静态帧）——动作节点存在即用；
    /// ② 动作节点存在但帧缺失（少见）→ 回退 {root}/default 静态帧（piece 名同节点名 "default"）；
    /// ③ 无动作结构（101 面饰实测：仅 default/blink/hit… 表情结构，与脸型同构）→ 走表情收集 AddExpressionPieces。
    /// 修复前 101 面饰被当动作结构走 {root}/stand1/0 → 不存在 → 永不渲染（用户反馈「面饰不显示」）。
    /// </summary>
    private void AddAccessoryPieces(List<PieceSource> sources, CharacterAppearance a, string category, string root, string action, string expression, int frame, bool faceVisible = true)
    {
        string itemId = GetCategoryItemId(a, category);
        // ① 动作结构
        if (_wz.WithWzLock(() => _wz.FindNodeByPath($"{root}/{action}") != null))
        {
            string framePath = $"{root}/{action}/{frame}";
            if (_wz.WithWzLock(() => _wz.FindNodeByPath(framePath) != null))
            {
                foreach (var pieceName in _wz.GetFramePieceNames(framePath))
                {
                    if (pieceName == "delay" || pieceName == "move") continue;
                    if (IsEffectPieceName(pieceName) && !a.EnableEffect) continue;
                    sources.Add(new PieceSource(category, pieceName, $"{framePath}/{pieceName}", itemId));
                }
                return;
            }
            // ② 帧缺失 → 静态 default 回退（背面动作优先用物品自带的 backDefault 背面粉）
            AddDefaultStaticPiece(sources, a, category, root, itemId, backFace: !faceVisible);
            return;
        }
        // ③ 表情结构（101 面饰）：default 直接 piece / blink 数字帧
        AddExpressionPieces(sources, a, category, root, ResolveFaceAction(root, expression), frame);
    }

    /// <summary>静态默认帧收集：{root}/default 直接 piece（102/103 动作缺失时的回退；piece 名同节点名 "default"，z 指向对应 accessory 层）。</summary>
    private void AddDefaultStaticPiece(List<PieceSource> sources, CharacterAppearance a, string category, string root, string itemId, bool backFace = false)
    {
        // 背面动作（爬绳/趴下等 face=0）：优先用物品的 backDefault 背面粉（WZ 里 103 耳环等有该节点）；
        // 没有 backDefault 的物品退回 default（对齐 MapleSalon2 的背面处理缺口，本项目自行补齐）。
        string defaultPath = $"{root}/default";
        if (backFace && _wz.WithWzLock(() => _wz.FindNodeByPath($"{root}/backDefault") != null))
        {
            defaultPath = $"{root}/backDefault";
        }
        if (!_wz.WithWzLock(() => _wz.FindNodeByPath(defaultPath) != null)) return;
        foreach (var pieceName in _wz.GetFramePieceNames(defaultPath))
        {
            if (pieceName == "delay" || pieceName == "move" || pieceName == "info") continue;
            if (IsEffectPieceName(pieceName) && !a.EnableEffect) continue;
            sources.Add(new PieceSource(category, pieceName, $"{defaultPath}/{pieceName}", itemId));
        }
    }

    /// <summary>部件名是否为特效帧：effect / effect1 / effect2 …（对齐 MapleSalon2 chair effectReg /effect[0-9]?/）。</summary>
    private static bool IsEffectPieceName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("effect", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length > 6 && name.StartsWith("effect", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(name.Substring(6), out _);
        }
        return false;
    }

    /// <summary>
    /// R13 节点级装备特效部件收集：{root}/effect。
    /// 结构 ①（MapleSalon2 loadEffect）：{root}/effect/{action}/{frame} —— action 键动画，帧号 clamp 到特效帧数；
    /// 结构 ②（椅子同构）：{root}/effect/{frame} —— 数字帧，帧号 clamp。
    /// 部件以 category="effect" 进管线：无 map 锚点时兜底锚到身体 navel（ResolveAnchors），层恒为 effect（顶层）。
    /// </summary>
    private void AddEquipEffectPieces(List<PieceSource> sources, CharacterAppearance a, string category, string root, string action, int frame)
    {
        string effectRoot = $"{root}/effect";
        if (!_wz.WithWzLock(() => _wz.FindNodeByPath(effectRoot) != null)) return;
        string itemId = GetCategoryItemId(a, category);

        // ① action 键结构：{root}/effect/{action}/{frame}
        if (_wz.WithWzLock(() => _wz.FindNodeByPath($"{effectRoot}/{action}") != null))
        {
            int effFrames = GetActionFrameCount(effectRoot, action);
            if (effFrames > 0)
            {
                int effFrame = Math.Min(frame, effFrames - 1);
                string framePath = $"{effectRoot}/{action}/{effFrame}";
                if (_wz.WithWzLock(() => _wz.FindNodeByPath(framePath) != null))
                {
                    foreach (var pieceName in _wz.GetFramePieceNames(framePath))
                    {
                        if (pieceName == "delay" || pieceName == "move") continue;
                        sources.Add(new PieceSource("effect", pieceName, $"{framePath}/{pieceName}", itemId));
                    }
                }
                return;
            }
        }
        // ② 数字帧结构：{root}/effect/{frame}
        int frames = CountNumericChildren(effectRoot);
        if (frames > 0)
        {
            int effFrame = Math.Min(frame, frames - 1);
            string piecePath = $"{effectRoot}/{effFrame}";
            if (_wz.WithWzLock(() => _wz.FindNodeByPath(piecePath) != null))
            {
                sources.Add(new PieceSource("effect", effFrame.ToString(), piecePath, itemId));
            }
        }
    }

    /// <summary>
    /// R15 全局装备特效表收集：Effect/ItemEff.img/{id}/effect（MapleSalon2 getPieceEffectWz 同源）。
    /// 本 WZ 实测（1102149 冰凌披风）：特效动画帧在 default/{0..17}（18 帧循环），无 stand1 等动作帧；
    /// 帧号取模循环（对齐 MapleSalon2 特效动画循环语义）；z 为 effect/default 的 z（=-2，特效层）。
    /// 修复前只在 Character/Cape.img 内找 effect——本 WZ 披风 img 无 effect 节点，特效数据一直在
    /// Effect/ItemEff.img 却被忽略 → 冰凌披风特效缺失（用户反馈「冰凌披风渲染依旧有问题」）。
    /// </summary>
    private void AddItemEffEffectPieces(List<PieceSource> sources, CharacterAppearance a, string category, int frame)
    {
        string itemId = GetCategoryItemId(a, category);
        if (string.IsNullOrEmpty(itemId)) { return; }
        // MapleSalon2 用原样 id（如 1102149），部分数据也可能补零（01102149）——两种都试
        string effectRoot = $"Effect/ItemEff.img/{itemId}/effect";
        if (!_wz.WithWzLock(() => _wz.FindNodeByPath(effectRoot) != null))
        {
            effectRoot = $"Effect/ItemEff.img/{itemId.PadLeft(8, '0')}/effect";
            if (!_wz.WithWzLock(() => _wz.FindNodeByPath(effectRoot) != null)) { return; }
        }
        int frames = CountNumericChildren($"{effectRoot}/default");
        if (frames <= 0) { return; }
        int effFrame = frame % frames;
        string piecePath = $"{effectRoot}/default/{effFrame}";
        if (!_wz.WithWzLock(() => _wz.FindNodeByPath(piecePath) != null)) { return; }
        sources.Add(new PieceSource("effect", effFrame.ToString(), piecePath, itemId));
    }

    /// <summary>
    /// R1 坐骑部件收集：TamingMob/{id}.img/{mountAction}/{mountFrame}/0。
    /// 坐骑动作优先跟随当前角色动作（characterAction 反向映射优先，其次同名动作直接联动），
    /// 缺省回退 stand1/stand2（骑乘姿态）；帧号 clamp 到坐骑动作帧数（最小可用联动，不做 MapleSalon2 的逐帧 instruction 同步）。
    /// R18 hideBody：坐骑动作映射 hideBody（characterAction[坐骑动作] == "hideBody"）时坐骑完全覆盖角色，
    /// 角色身体部件由 CollectPiecesForFrame 跳过（见 IsMountHideBodyForAction），此处只收坐骑部件。
    /// </summary>
    private void AddMountPieces(List<PieceSource> sources, CharacterAppearance a, string mountRoot, string action, int frame)
    {
        string mountAction = ResolveMountAction(mountRoot, action);
        if (mountAction.Length == 0) return;
        int mountFrames = GetRideActionFrameCount(mountRoot, mountAction);
        if (mountFrames <= 0) return;
        int mountFrame = Math.Min(frame, mountFrames - 1);
        string framePath = $"{mountRoot}/{mountAction}/{mountFrame}";
        if (!_wz.WithWzLock(() => _wz.FindNodeByPath(framePath) != null)) return;
        string itemId = a.Mount?.Id ?? "";
        foreach (var pieceName in _wz.GetFramePieceNames(framePath))
        {
            // 只收 canvas 件（"delay" 等标量节点无 map → 不会锁定/绘制，但仍过滤掉避免白占 meta 缓存）
            if (pieceName == "delay" || pieceName == "move" || pieceName == "a0" || pieceName == "a1") continue;
            // R13 装备特效开关：坐骑帧内 "effect" 部件（坐骑光环等）同样受开关控制
            if (IsEffectPieceName(pieceName) && !a.EnableEffect) continue;
            sources.Add(new PieceSource("mount", pieceName, $"{framePath}/{pieceName}", itemId));
        }
    }

    /// <summary>
    /// R1 椅子部件收集：Item/Install|Cash/{img}.img/{id:D8}/effect/{frame}。
    /// effect 帧节点本身是单 canvas（Wz_Png，带 origin），无 map → ResolveAnchors 兜底锚到身体 navel。
    /// 帧号 clamp 到 effect 帧数（多数椅子 effect 只有 1 帧）。
    /// R-chair（对齐 MapleSalon2 createItems 的 effectReg /effect[0-9]?/）：
    /// ① 收集 effect/effect2/effect3 全部特效层——probe 实测 30.8% 椅子带 effect2（778/2530），
    ///    原实现只收集 effect → 特效层整体缺失；
    /// ② pos==1 椅子整体上移 50px（带 tamingMob 且 id&lt;3011000 上移 30px，对齐 ChairEffectPart）；
    /// ③ effect 节点 z&gt;0 的特效层置顶（对齐 MapleSalon2 getOrCreatEffectLayer：z&gt;=1 → zIndex+200 置顶）。
    /// </summary>
    private void AddChairPieces(List<PieceSource> sources, CharacterAppearance a, string chairRoot, int frame)
    {
        string itemId = a.Chair?.Id ?? "";
        foreach (var effName in new[] { "effect", "effect2", "effect3" })
        {
            string effectRoot = $"{chairRoot}/{effName}";
            int effectFrames = GetChairEffectFrameCount(effectRoot);
            if (effectFrames <= 0) continue;
            int effectFrame = Math.Min(frame, effectFrames - 1);
            string piecePath = $"{effectRoot}/{effectFrame}";
            if (!_wz.WithWzLock(() => _wz.FindNodeByPath(piecePath) != null)) continue;
            int pos = GetChairEffectPos(effectRoot);
            int z = GetChairEffectZ(effectRoot);
            int offsetY = 0;
            if (pos == 1)
            {
                bool hasTamingMob = HasChairTamingMob(chairRoot);
                int idNum = int.TryParse(itemId, out var iv) ? iv : 0;
                offsetY = (hasTamingMob && idNum / 1000 < 3011) ? -30 : -50;
            }
            // z>0 特效层（effect2 实测 z=3）置顶渲染；z<=0（椅子本体）走默认身后层
            string layerOverride = z > 0 ? "effect" : "";
            sources.Add(new PieceSource("chair", $"{effName}/{effectFrame}", piecePath, itemId)
            {
                OffsetY = offsetY,
                ZFieldOverride = layerOverride,
            });
        }
    }

    /// <summary>椅子坐姿动作（对齐 MapleSalon2 getFirstCharacterGroupData：info/sitAction 优先，缺省 Sit）。
    /// "hide" 特例 → sit；动作不存在时回退 sit（probe 实测 body sit=1 帧齐全）。缓存 per 椅子 id。</summary>
    private string ResolveChairAction(CharacterAppearance a)
    {
        if (string.IsNullOrEmpty(a.Chair?.Id)) return "";
        string id = a.Chair!.Id;
        if (_chairSitActionCache.TryGetValue(id, out var cached)) return cached;
        string resolved = "";
        string root = ChairRoot(id);
        if (root.Length > 0)
        {
            string? sitAction = _wz.GetStringProperty($"{root}/info/sitAction");
            if (!string.IsNullOrEmpty(sitAction))
            {
                resolved = sitAction.Equals("hide", StringComparison.OrdinalIgnoreCase) ? "sit" : sitAction;
            }
            else
            {
                resolved = "sit";
            }
            if (GetActionFrameCount(BodyRoot(a), resolved) <= 0)
            {
                resolved = GetActionFrameCount(BodyRoot(a), "sit") > 0 ? "sit" : "";
            }
        }
        _chairSitActionCache[id] = resolved;
        return resolved;
    }

    /// <summary>带椅子时渲染动作解析为坐姿；无椅子/坐姿不可用时返回原动作。帧号 clamp 到坐姿帧数（sit 只有 1 帧）。</summary>
    private (string Action, int Frame) ResolveChairRender(CharacterAppearance a, string action, int frame)
    {
        string sitAction = ResolveChairAction(a);
        if (sitAction.Length == 0) return (action, frame);
        int fc = GetActionFrameCount(BodyRoot(a), sitAction);
        if (fc <= 0) return (action, frame);
        return (sitAction, Math.Min(frame, fc - 1));
    }

    /// <summary>
    /// R20 渲染动作解析（椅子坐姿 + 骑乘映射叠加）：先按椅子坐姿，再按坐骑 characterAction 映射
    /// 切换角色动作（如 01930001 飞行坐骑 stand1→fly）；帧号 clamp 到最终动作帧数（角色/坐骑同帧一致）。
    /// 无坐骑/无映射时行为与 ResolveChairRender 完全一致（普通坐骑无回归）。
    /// </summary>
    private (string Action, int Frame) ResolveRenderAction(CharacterAppearance a, string action, int frame)
    {
        // 胶水 2026-08-18：逻辑动作名解析（stand/walk → 单/双手 stand1/stand2/walk1/walk2）——
        // 预览姿势默认传 stand（用户要求不硬编码 stand1），由武器类型自动选姿势。
        // 注意用传入外观 a 的武器判定（_current 可能是另一外观，预览草稿 vs 主窗口当前）。
        action = ResolveLogicalAction(a, action);
        var (rAct, rFrame) = ResolveChairRender(a, action, frame);
        string rideAct = ResolveRideAction(a, rAct);
        if (rideAct != rAct)
        {
            rAct = rideAct;
            int fc = GetActionFrameCount(BodyRoot(a), rAct);
            if (fc > 0) rFrame = Math.Min(rFrame, fc - 1);
        }
        return (rAct, rFrame);
    }

    /// <summary>椅子特效层帧数缓存（CountNumericChildren 每帧调用，热路径避免重复 WZ 遍历）。</summary>
    private int GetChairEffectFrameCount(string effectRoot)
    {
        if (_chairEffectFrameCountCache.TryGetValue(effectRoot, out var c)) return c;
        int c2 = CountNumericChildren(effectRoot);
        _chairEffectFrameCountCache[effectRoot] = c2;
        return c2;
    }

    /// <summary>椅子特效层 pos（缓存；effect 节点上的 pos 决定椅子相对角色锚点的偏移）。</summary>
    private int GetChairEffectPos(string effectRoot)
    {
        if (_chairEffectPosCache.TryGetValue(effectRoot, out var c)) return c;
        int c2 = _wz.GetIntProperty($"{effectRoot}/pos");
        _chairEffectPosCache[effectRoot] = c2;
        return c2;
    }

    /// <summary>椅子特效层 z（缓存；z>0 特效层置顶渲染）。</summary>
    private int GetChairEffectZ(string effectRoot)
    {
        if (_chairEffectZCache.TryGetValue(effectRoot, out var c)) return c;
        int c2 = _wz.GetIntProperty($"{effectRoot}/z");
        _chairEffectZCache[effectRoot] = c2;
        return c2;
    }

    /// <summary>椅子是否带 tamingMob（pos==1 偏移量选择用；缓存）。</summary>
    private bool HasChairTamingMob(string chairRoot)
    {
        if (_chairTamingMobCache.TryGetValue(chairRoot, out var c)) return c;
        bool b = _wz.WithWzLock(() => _wz.FindNodeByPath($"{chairRoot}/info/tamingMob") != null);
        _chairTamingMobCache[chairRoot] = b;
        return b;
    }

    /// <summary>坐骑 characterAction 正向映射（缓存）：坐骑动作名 → 角色动作名（MapleSalon2 tamingMob.ts 同源）。</summary>
    private Dictionary<string, string> GetMountCharActionMap(string mountRoot)
    {
        if (_mountCharActionCache.TryGetValue(mountRoot, out var cached)) return cached;
        var map = _wz.WithWzLock(() =>
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            var imgNode = _wz.FindNodeByPath(mountRoot);
            if (imgNode == null) return m;
            var img = imgNode.GetValue<Wz_Image>();
            Wz_Node? inner = img != null ? img.Node : imgNode;
            if (inner == null) return m;
            var ca = inner.FindNodeByPath("characterAction");
            if (ca == null) return m;
            foreach (Wz_Node kv in ca.Nodes)
            {
                var v = kv.GetValueEx<string>(null);
                if (!string.IsNullOrEmpty(v)) m[kv.Text] = v;
            }
            return m;
        });
        _mountCharActionCache[mountRoot] = map;
        return map;
    }

    /// <summary>
    /// 坐骑 characterAction 反向映射（缓存）：角色动作名 → 坐骑动作名。
    /// characterAction 节点 = {坐骑动作名: 角色动作名}（如 01930001 飞行坐骑：walk1→fly、stand1→fly；
    /// 01932294 全覆盖坐骑：stand1→hideBody 等）。反向映射供 ResolveMountAction 按角色当前动作名选坐骑动作：
    /// ① 隐式同名（坐骑动作名 == 角色动作名，如 stand1→stand1）优先；
    /// ② 显式 characterAction 反向（角色动作名 → 映射到它的坐骑动作名，如 fly → walk1）。
    /// hideBody 值不参与反向（它不是角色真实动作名）。
    /// </summary>
    private Dictionary<string, string> GetMountCharActionReverse(string mountRoot)
    {
        if (_mountCharActionReverseCache.TryGetValue(mountRoot, out var cached)) return cached;
        var reverse = _wz.WithWzLock(() =>
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            var imgNode = _wz.FindNodeByPath(mountRoot);
            if (imgNode == null) return r;
            var img = imgNode.GetValue<Wz_Image>();
            Wz_Node? inner = img != null ? img.Node : imgNode;
            if (inner == null) return r;
            // ① 隐式同名：坐骑动作名 → 同名角色动作
            foreach (Wz_Node top in inner.Nodes)
            {
                if (top.Text == "info" || top.Text == "characterAction" || top.Text == "forcingItem") continue;
                if (!r.ContainsKey(top.Text)) r[top.Text] = top.Text;
            }
            // ② 显式 characterAction：坐骑动作 A → 角色动作 X；X 无条目时取 A，同名条目（A == X）优先覆盖
            var ca = inner.FindNodeByPath("characterAction");
            if (ca != null)
            {
                foreach (Wz_Node kv in ca.Nodes)
                {
                    var x = kv.GetValueEx<string>(null);
                    if (string.IsNullOrEmpty(x) || x.Equals("hideBody", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!r.TryGetValue(x, out var existing))
                    {
                        r[x] = kv.Text;
                    }
                    else if (kv.Text == x && existing != x)
                    {
                        r[x] = x;
                    }
                }
            }
            return r;
        });
        _mountCharActionReverseCache[mountRoot] = reverse;
        return reverse;
    }

    /// <summary>坐骑动作是否映射 hideBody（characterAction[坐骑动作] == "hideBody" → 坐骑完全覆盖角色，角色身体隐藏）。</summary>
    private bool IsMountActionHideBody(string mountRoot, string mountAction)
    {
        if (string.IsNullOrEmpty(mountAction)) return false;
        return GetMountCharActionMap(mountRoot).TryGetValue(mountAction, out var v)
            && v.Equals("hideBody", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前外观 + 当前角色动作下，坐骑动作是否映射 hideBody（R18：坐骑完全覆盖角色，只渲染坐骑）。</summary>
    private bool IsMountHideBodyForAction(CharacterAppearance a, string action)
    {
        if (string.IsNullOrEmpty(a.Mount?.Id)) return false;
        var mountRoot = MountRoot(a.Mount!.Id);
        if (mountRoot.Length == 0) return false;
        string mountAction = ResolveMountAction(mountRoot, action);
        if (mountAction.Length == 0) return false;
        return IsMountActionHideBody(mountRoot, mountAction);
    }

    /// <summary>
    /// R18 坐骑动作解析（对齐 MapleSalon2 tamingMob.ts characterAction 映射）：
    /// ① characterAction 反向映射：坐骑 img 有 characterAction 节点时，按「坐骑动作 → 角色动作」反向
    /// （角色动作名 → 坐骑动作名）优先选择坐骑动作（隐式同名映射内置，stand1/walk1 等直接命中同名坐骑动作）；
    /// ② 无映射时保留原回退链：同名字动作 → stand1/stand2（骑乘姿态）→ walk1/walk2/jump/fly/tired/prone 枚举。
    /// </summary>
    private string ResolveMountAction(string mountRoot, string requested)
    {
        // ① characterAction 反向映射：角色当前动作名 → 坐骑动作名（含隐式同名）
        if (!string.IsNullOrEmpty(requested))
        {
            if (GetMountCharActionReverse(mountRoot).TryGetValue(requested, out var mapped)
                && GetRideActionFrameCount(mountRoot, mapped) > 0)
            {
                return mapped;
            }
        }
        // ② 原回退链（保留）：同名 → stand1/stand2（骑乘姿态）→ 枚举
        if (!string.IsNullOrEmpty(requested) && GetRideActionFrameCount(mountRoot, requested) > 0) return requested;
        if (GetRideActionFrameCount(mountRoot, "stand1") > 0) return "stand1";
        if (GetRideActionFrameCount(mountRoot, "stand2") > 0) return "stand2";
        foreach (var act in new[] { "walk1", "walk2", "jump", "fly", "tired", "prone" })
        {
            if (GetRideActionFrameCount(mountRoot, act) > 0) return act;
        }
        return "";
    }

    /// <summary>
    /// R20 骑乘角色动作解析（对齐 MapleSalon2 sitCharacter：characterAction = 坐骑 characterAction 映射的角色动作）：
    /// 链：角色请求动作 → ResolveMountAction（反向映射出坐骑动作）→ characterAction[坐骑动作] → 角色应播动作。
    /// 例：01930001 飞行坐骑 characterAction={stand1→fly, walk1→fly,...} → 角色 stand1 → 坐骑 stand1 → 角色应播 fly
    /// （飞行坐骑骑乘时角色播 fly 帧，坐骑部件照常锚到角色 navel，不再浮在站立角色头顶）。
    /// 映射目标不可播（body 无该动作帧，如 01992031 的 StabT2）或值为 hideBody 时回退原动作；
    /// 无坐骑 / 坐骑无 characterAction 映射时原样返回（普通坐骑行为不变）。
    /// </summary>
    private string ResolveRideAction(CharacterAppearance a, string requested)
    {
        if (string.IsNullOrEmpty(a.Mount?.Id)) return requested;
        var mountRoot = MountRoot(a.Mount!.Id);
        if (mountRoot.Length == 0) return requested;
        // 角色请求动作 → 坐骑动作（复用 R18 反向映射链）
        string mountAction = ResolveMountAction(mountRoot, requested);
        if (mountAction.Length == 0) return requested;
        // 坐骑动作 → characterAction 映射的角色动作（如 stand1 → fly）
        var map = GetMountCharActionMap(mountRoot);
        if (map.TryGetValue(mountAction, out var mapped)
            && !string.IsNullOrEmpty(mapped)
            && !mapped.Equals("hideBody", StringComparison.OrdinalIgnoreCase)
            && GetActionFrameCount(BodyRoot(a), mapped) > 0)
        {
            return mapped;
        }
        return requested;
    }

    /// <summary>坐骑/椅子动作帧数缓存（避免 CollectPiecesForFrame 每帧重复 WZ 遍历）。</summary>
    private int GetRideActionFrameCount(string root, string action)
    {
        string key = $"{root}|{action}";
        if (_rideFrameCountCache.TryGetValue(key, out var c)) return c;
        int c2 = GetActionFrameCount(root, action);
        _rideFrameCountCache[key] = c2;
        return c2;
    }

    /// <summary>统计某节点下数字子节点数（椅子 effect 帧数）。</summary>
    private int CountNumericChildren(string path)
    {
        try
        {
            return _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath(path);
                if (node == null) return 0;
                int count = 0;
                foreach (Wz_Node child in node.Nodes)
                    if (int.TryParse(child.Text, out _)) count++;
                return count;
            });
        }
        catch { return 0; }
    }

    private static string GetCategoryItemId(CharacterAppearance a, string category) => category switch
    {
        "hair" => a.Hair?.Id ?? "", "face" => a.Face?.Id ?? "",
        "cap" => a.Cap?.Id ?? "", "cape" => a.Cape?.Id ?? "",
        "coat" => a.Coat?.Id ?? "", "overall" => a.Overall?.Id ?? "",
        "pants" => a.Pants?.Id ?? "", "shoe" => a.Shoes?.Id ?? "",
        "glove" => a.Glove?.Id ?? "", "shield" => a.Shield?.Id ?? "",
        "weapon" => a.Weapon?.Id ?? "", "earring" => a.Earring?.Id ?? "",
        "faceaccessory" => a.FaceAccessory?.Id ?? "", "eyeaccessory" => a.EyeAccessory?.Id ?? "",
        "mount" => a.Mount?.Id ?? "", "chair" => a.Chair?.Id ?? "",
        _ => ""
    };

    /// <summary>
    /// 发型部件路径 canvas 化（2026-08-16，probe 证实：HairProbe 扫描 17337 个发型，
    /// hairShade 部件 16319 个的帧内 UOL 目标是「canvas 列表」容器）。
    /// 结构：stand1/0/hairShade（Wz_Uol）→ default/hairShade（容器节点，value=null）→
    /// default/hairShade/{0,1,2,...}（数字子节点，PNG + origin/map/z 在子节点上；各槽位 UOL 均指向 0）。
    /// 直接按容器路径 ExtractPng/GetOrigin/GetPieceMap 全为空 → 部件被收集但永不绘制。
    /// 本方法把路径落到具体子节点（优先当前帧号槽位，回退 0，再回退首个数字子节点）。
    /// </summary>
    private string ResolveHairPieceCanvasPath(string piecePath, string frame)
    {
        return _wz.WithWzLock(() =>
        {
            var node = _wz.FindNodeByPath(piecePath);
            if (node == null) { return piecePath; }
            var resolved = node.ResolveUol() ?? node;
            // 已是 canvas（hair/hairOverHead/hairBelowBody 等常规部件）：无需调整
            if (resolved.GetValue<Wz_Png>() != null) { return piecePath; }
            string? chosen = null;
            var frameNode = resolved.FindNodeByPath(frame);
            if (frameNode != null && (frameNode.ResolveUol() ?? frameNode).GetValue<Wz_Png>() != null)
            {
                chosen = frame;
            }
            if (chosen == null)
            {
                foreach (Wz_Node child in resolved.Nodes)
                {
                    if (!int.TryParse(child.Text, out _)) { continue; }
                    if ((child.ResolveUol() ?? child).GetValue<Wz_Png>() != null)
                    {
                        chosen = child.Text;
                        break;
                    }
                }
            }
            if (chosen == null) { return piecePath; }
            return $"{resolved.FullPathToFile.Replace('\\', '/')}/{chosen}";
        });
    }

    /// <summary>
    /// 帧位移 move 读取（带缓存）：key = hash|action|frame。首次查 WZ（锁内），之后命中缓存零锁——
    /// 保证动画帧 RenderFrame 热帧不抢 _wzLock（后台预热持锁期间动画不阻塞）。
    /// </summary>
    private Wz_Vector? GetFrameMove(string hash, CharacterAppearance a, string action, int frame)
    {
        string key = $"{hash}|{action}|{frame}";
        if (_moveCache.TryGetValue(key, out var cached)) return cached;
        Wz_Vector? mv = null;
        try
        {
            // R-chair：带椅子时帧位移也用坐姿帧（坐姿 move 与坐姿 body 一致，避免站立 move 造成抖动）
            // R20 骑乘：帧位移同样用骑乘映射后的动作帧（01930001 角色播 fly 时 move 也取 fly，保持动作一致）
            var (rAct, rFrame) = ResolveRenderAction(a, action, frame);
            mv = _wz.WithWzLock(() => _wz.FindNodeByPath($"{BodyRoot(a)}/{rAct}/{rFrame}/move")?.GetValueEx<Wz_Vector>(null));
        }
        catch { }
        _moveCache[key] = mv;
        return mv;
    }

    private List<Piece> MaterializePieces(List<PieceSource> sources, string action = "", int frame = 0)
    {
        var pieces = new List<Piece>();
        foreach (var s in sources)
        {
            var meta = GetMeta(s.PiecePath);
            if (meta == null) continue;
            var piece = new Piece
            {
                Category = s.Category, PieceName = s.PieceName, PiecePath = s.PiecePath,
                ItemId = s.ItemId, Ox = meta.Ox, Oy = meta.Oy, Map = meta.Map,
                // R-chair：椅子特效层层覆盖（z>0 置顶）与 pos 偏移从 PieceSource 透传
                ZField = s.ZFieldOverride.Length > 0 ? s.ZFieldOverride : meta.ZField,
                OffsetX = s.OffsetX, OffsetY = s.OffsetY,
            };
            // alert / heal 动作：动作型装备提供 handMove 手部锚点（对齐 MapleSalon2
            // CharacterActionItem.ancherSetup —— 这两个动作抬手/施放，武器等要挂到手部锚点；
            // 部件若自带 handMove 则不覆盖）
            if ((action == "alert" || action == "heal")
                && IsActionItemCategory(piece.Category)
                && !piece.Map.ContainsKey("handMove"))
            {
                piece.Map["handMove"] = HandMoveDefaultAnchor(frame);
            }
            pieces.Add(piece);
        }
        return pieces;
    }

    /// <summary>
    /// 背面动作（body 帧 face=0 / blink / hide）时，脸层容器整体隐藏
    /// （对齐 MapleSalon2 characterFaceFrame.ts:185-196 `layer.visible = !isBackAction`）。
    /// </summary>
    private void HideFrontFaceLayersOnBackAction(List<Piece> pieces, CharacterAppearance a, string action, int frame)
    {
        var (renderAction, renderFrame) = ResolveRenderAction(a, action, frame);
        bool back = !IsBodyFaceVisible(a, renderAction, renderFrame)
                    || action == "blink" || action == "hide";
        if (!back) { return; }
        foreach (var p in pieces)
        {
            if (!string.IsNullOrEmpty(p.ResolvedLayer) && FrontFaceLayers.Contains(p.ResolvedLayer))
            {
                p.Hidden = true;
            }
        }
    }

    /// <summary>
    /// 脸层容器（对齐 MapleSalon2 characterFaceFrame.ts:185-196 的 FrontFaceLayers）：
    /// 背面动作时这些层的部件整体不渲染 —— 不区分 category（102 眼饰若落在 accessoryEyeBelowFace 同样隐藏）。
    /// </summary>
    private static readonly HashSet<string> FrontFaceLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessoryFace", "capAccessoryBelowAccFace", "accessoryFaceOverFaceBelowCap",
        "face", "accessoryEyeBelowFace", "accessoryFaceBelowFace",
    };

    /// <summary>
    /// 是否有穿戴物声明 info.invisibleFace == 1（面罩类，遮住脸型）。
    /// 对齐 MapleSalon2 item.ts:311 `isOverrideFace = wz?.info?.invisibleFace === 1`。
    /// </summary>
    private bool IsFaceOverriddenByEquipment(string hash, CharacterAppearance a)
    {
        try
        {
            foreach (var (category, root) in GetRoots(hash, a))
            {
                if (category is not ("face" or "faceaccessory" or "cap" or "capaccessory")) { continue; }
                if (_wz.WithWzLock(() => _wz.GetIntProperty($"{root}/info/invisibleFace")) == 1)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>是否为「动作型装备」部件（非 body/head/hair/face/mount/chair/effect 的穿戴物）。</summary>
    private static bool IsActionItemCategory(string category) =>
        category is not ("body" or "head" or "hair" or "face" or "mount" or "chair" or "effect" or "ear");

    /// <summary>alert / heal 动作的手部默认锚点（MapleSalon2 const/ancher.ts handMoveDefaultAnchers；
    /// 帧号越界取第 0 个）。</summary>
    private static (int x, int y) HandMoveDefaultAnchor(int frame) => frame switch
    {
        1 => (-10, 0),
        2 => (-12, 3),
        _ => (-8, -2),
    };

    /// <summary>head 的耳朵 canvas：以 "ear" 结尾（排除 capeArm 等含 ear 子串的 key）。</summary>
    private static bool IsEarPiece(string name) => name.EndsWith("ear", StringComparison.OrdinalIgnoreCase);

    private string ResolveFaceAction(string root, string expression)
    {
        if (!string.IsNullOrEmpty(expression) && _wz.FindNodeByPath($"{root}/{expression}") != null) return expression;
        if (_wz.FindNodeByPath($"{root}/{ExpressionBlink}") != null) return ExpressionBlink;
        return ExpressionDefault;
    }

    // ═══════════════════════════════════════════
    // 锚点解算（对齐 MapleSalon2 itemPiece.buildAncher / characterBodyFrame）
    // ═══════════════════════════════════════════

    private void ResolveAnchors(List<Piece> pieces)
    {
        var anchors = new Dictionary<string, (int x, int y)> { ["navel"] = (0, 0) };
        // R19 坐骑锚点对齐 MapleSalon2：坐骑全部部件以「坐骑帧主 navel」为基准摆位（-origin 相对布局），
        // 不锚到各部件自身 map.navel——多 navel 坐骑（如 01902002 三部件 navel 各不相同）若锚自身
        // navel，部件会整体错位（probe 实测 Δ=(25,-44)/(3,-22)）；主 navel = 帧内第一个含 map.navel 的
        // 部件（MapleSalon2 getFrameNavel = 第一个 map 含 navel 的 layer），全无则 (0,0)。
        // 无 map 的坐骑部件也照常锁定渲染（MapleSalon2 无视 map，position 恒 -origin）。
        bool mountFrameNavelFound = false;
        (int x, int y) mountFrameNavel = (0, 0);
        foreach (var p in pieces)
        {
            if (p.Category != "mount" || mountFrameNavelFound) continue;
            if (p.Map.TryGetValue("navel", out var nv)) { mountFrameNavel = nv; mountFrameNavelFound = true; }
        }
        foreach (var p in pieces)
        {
            if (p.Category != "mount" || p.Locked) continue;
            p.AnchorX = -mountFrameNavel.x;
            p.AnchorY = -mountFrameNavel.y;
            p.BaseAnchor = "navel";
            p.Locked = true;
        }

        int maxIter = pieces.Count * 4 + 8;
        for (int iter = 0; iter < maxIter; iter++)
        {
            bool changed = false;
            foreach (var p in pieces)
            {
                if (p.Locked || p.Map.Count == 0) continue;
                string? baseName = null;
                foreach (var k in p.Map.Keys)
                {
                    if (anchors.ContainsKey(k)) { baseName = k; break; }
                }
                if (baseName == null) continue;

                var A = anchors[baseName];
                var M = p.Map[baseName];
                p.AnchorX = A.x - M.x;
                p.AnchorY = A.y - M.y;
                p.BaseAnchor = baseName;
                foreach (var (other, off) in p.Map)
                {
                    if (other != baseName && !anchors.ContainsKey(other))
                        anchors[other] = (p.AnchorX + off.x, p.AnchorY + off.y);
                }
                p.Locked = true;
                changed = true;
            }
            if (!changed) break;
        }

        // R1 椅子部件无 map 锚点（WZ 只有 origin）：兜底锚到 navel 根（身体髋部）。
        // 最小可用版：不读 info/bodyRelMove（本 WZ 无坐椅数据可校准），原点对齐身体 navel。
        // R13 节点级特效部件同样可能无 map（独立 effect 帧）：同椅子兜底锚到 navel。
        // R15 全局特效（Effect/ItemEff.img）无 map 时对齐 MapleSalon2：锚 brow（眉毛，head 部件 map 提供）
        // + getEffectPos 默认偏移 {10,50} 取负（resolveEffectFrames 的 {brow:{-10,-50}}）；brow 不在锚表时回退 navel。
        // R-chair：椅子 pos==1 的 OffsetY（-50/-30）在此叠加（对齐 MapleSalon2 ChairEffectPart position 修正）。
        foreach (var p in pieces)
        {
            if (p.Locked || (p.Category != "chair" && p.Category != "effect")) continue;
            if (p.Category == "effect" && anchors.TryGetValue("brow", out var browAbs))
            {
                // MapleSalon2 map = {brow: {x: -10, y: -50}} → Anchor = browAbs - (-10, -50)
                p.AnchorX = browAbs.x + 10;
                p.AnchorY = browAbs.y + 50;
                p.BaseAnchor = "brow";
            }
            else
            {
                p.AnchorX = 0;
                p.AnchorY = 0;
                p.BaseAnchor = "navel";
            }
            p.AnchorX += p.OffsetX;
            p.AnchorY += p.OffsetY;
            p.Locked = true;
        }

        foreach (var p in pieces)
        {
            if (!p.Locked) continue;
            p.FinalX = p.AnchorX - p.Ox;
            p.FinalY = p.AnchorY - p.Oy;
        }
    }

    // ═══════════════════════════════════════════
    // 分层 + 遮挡锁（vslot 驱动 + zmap 顺序，对齐 MapleSalon2 buildLock/refreshLock 思想）
    // ═══════════════════════════════════════════

    private void AssignLayers(List<Piece> pieces)
    {
        var zmap = GetZmapTopToBottom();
        foreach (var p in pieces)
        {
            p.ResolvedLayer = ResolveLayer(p);
            p.ZIndex = _zmapIndex.TryGetValue(p.ResolvedLayer, out var zi) ? zi : int.MaxValue;
        }
    }

    /// <summary>
    /// 双条件遮挡判定（对齐 MapleSalon2 refreshLock）：部件可见当且仅当 hasSelfLock || hasLayerLock。
    /// hasSelfLock = 自身 vslot 每个槽在全局锁表中无主或主==自己；
    /// hasLayerLock = 该层 requireLocks 每个槽无主或主==自己（本服务不读 smap.img，
    /// 用该层所有实际穿戴部件的 vslot 槽码并集近似）。帽子（cap）强制只用 vslot 判定（MapleSalon2 force Cap using vslot）。
    /// </summary>
    private void ApplyLocks(string hash, CharacterAppearance a, List<Piece> pieces)
    {
        var locks = BuildLayerLocks(hash, a);
        if (locks == null || locks.Count == 0) return;

        // 层 requireLocks 近似：该层所有实际穿戴部件的 vslot 槽码并集
        var layerSlots = new Dictionary<string, HashSet<string>>();
        foreach (var p in pieces)
        {
            if (string.IsNullOrEmpty(p.ResolvedLayer)) continue;
            if (!layerSlots.TryGetValue(p.ResolvedLayer, out var set))
            {
                set = new HashSet<string>();
                layerSlots[p.ResolvedLayer] = set;
            }
            foreach (var slot in VslotCodes(GetVslotForCategory(p.Category, p.ItemId)))
                set.Add(slot);
        }

        foreach (var p in pieces)
        {
            if (p.Hidden) continue;
            var vslot = GetVslotForCategory(p.Category, p.ItemId);
            if (string.IsNullOrEmpty(vslot)) continue;
            var slots = VslotCodes(vslot).ToList();

            bool selfOk = SlotsOk(locks, p.ItemId, slots);
            bool layerOk;
            if (p.Category == "cap")
            {
                // MapleSalon2 force Cap using vslot：帽子的 hasLayerLock 与 hasSelfLock 等价，只判自身 vslot
                layerOk = false;
            }
            else if (p.Category == "hair")
            {
                // 2026-08-16 发型层 requireLocks 用 smap.img 真实槽码（hair→H2、hairOverHead→H1、
                // hairShade→Hs、hairBelowBody→Hb…，probe 实证 smap.img 183 层）。
                // 修复：旧实现用「本层穿戴部件 vslot 并集」近似，发型层并集 = 头发自身 vslot
                // （H1H2H3H4H5H6HfHsHb），帽子 vslot（CpH1H5）锁 H1/H5 → 整层头发被隐藏
                // （probe 实证：cap=1000000/1002149 时 hair 件 0 可见）。smap 单码（H2 等）
                // 不被帽子锁定 → 后发保持可见，仅前发（H1）被帽子遮挡，对齐 MapleSalon2 smap 语义。
                // 2026-08-17 修复：smap **无该层条目**时 layerOk 恒真——对齐 MapleSalon2 refreshLock
                // （requireLocks = smap[name] ?? []，空 requireLocks 的 hasAllLocks 恒 true → 部件可见）。
                // 原回退「本层穿戴部件 vslot 并集 / 自身 vslot」会被帽子锁定的槽误伤：hairBelowHead
                // 部件（zmap 有 hairBelowHead 层，但 smap 无该层——smap 只有 backHairBelowHead=Hf）
                // 在 cap=1003953（vslot CpH5 锁 H5）下 selfOk 失败且回退 vslot 也被锁 → 整部件被
                // 错误隐藏（probe 实测 8 个发型受影响，用户反馈「发型部分内容被错误隐藏」）。
                var smapCodes = GetSmapLayerCodes(p.ResolvedLayer);
                if (smapCodes.Count > 0)
                {
                    layerOk = SlotsOk(locks, p.ItemId, smapCodes);
                }
                else
                {
                    layerOk = true;
                }
            }
            else if (layerSlots.TryGetValue(p.ResolvedLayer, out var ls))
            {
                layerOk = SlotsOk(locks, p.ItemId, ls);
            }
            else
            {
                layerOk = SlotsOk(locks, p.ItemId, slots);
            }
            if (!selfOk && !layerOk) p.Hidden = true;
        }
    }

    // smap.img 层 → requireLocks 槽码（MapleSalon2 用 smap 而非本层穿戴部件 vslot 并集；懒加载缓存，全局只读）
    private Dictionary<string, string>? _smapLayerCache;
    private readonly object _smapCacheLock = new();

    /// <summary>smap.img 中某层的 requireLocks 槽码（如 hair→H2、hairOverHead→H1、hairShade→Hs）。无该层/空值返回空列表。</summary>
    private List<string> GetSmapLayerCodes(string layer)
    {
        if (string.IsNullOrEmpty(layer)) { return new List<string>(); }
        var cache = _smapLayerCache;
        if (cache == null)
        {
            lock (_smapCacheLock)
            {
                if (_smapLayerCache == null)
                {
                    var built = new Dictionary<string, string>(StringComparer.Ordinal);
                    _wz.WithWzLock(() =>
                    {
                        var smap = _wz.FindNodeByPath("smap.img");
                        if (smap == null) { return true; }
                        foreach (Wz_Node c in smap.Nodes)
                        {
                            var val = c.GetValueEx<string>(null);
                            if (!string.IsNullOrEmpty(val)) { built[c.Text] = val; }
                        }
                        return true;
                    });
                    _smapLayerCache = built;
                }
                cache = _smapLayerCache;
            }
        }
        if (cache.TryGetValue(layer, out var codes))
        {
            return VslotCodes(codes).ToList();
        }
        return new List<string>();
    }

    /// <summary>
    /// 全局遮挡锁表：slot 2 字符码 → 占槽部件 id（后写覆盖，对齐 MapleSalon2 buildLock）。
    /// 建表顺序：每个穿戴类别按 islot 槽位层在 zmap 中的位置（zmap 顶→底 → 倒序处理，后写者占槽），
    /// 最终槽位归「视觉上层（islot 层 index 小）+ 同层 item id 大」的部件。
    /// 注意：不能用按名分层的 CategoryInLayer 旧思路——本 WZ 的 hairOverHead 层在 cap 层之上，
    /// 按名分层会让后发覆盖帽子的槽位、帽子反被隐藏；MapleSalon2 用 item.islot.includes(layer) 定位，
    /// 每个部件只落在其 islot 槽位层（cap→Cp、hair→Hr 等 2 字符层），顺序才与之一致。
    /// </summary>
    private Dictionary<string, string>? BuildLayerLocks(string hash, CharacterAppearance a)
    {
        var zmap = GetZmapTopToBottom();
        // 只对实际穿戴的部位建立锁
        var idMap = new Dictionary<string, string>();
        foreach (var (category, root) in GetRoots(hash, a))
        {
            var id = GetCategoryItemId(a, category);
            if (!string.IsNullOrEmpty(id)) idMap[category] = id;
        }
        if (idMap.Count == 0) return null;

        // 每个穿戴类别定位到其 islot 槽位层（如 cap→Cp、hair→Hr、cape→Sr；zmap 顶层含这些 2 字符层）。
        // islot 有多个码（套服 MaPn）时取最靠上（index 最小）的层——其建表顺序等价 MapleSalon2 的多次压入。
        var placements = new List<(int Index, string Category)>();
        foreach (var cat in idMap.Keys)
        {
            int best = int.MaxValue;
            bool found = false;
            foreach (var code in VslotCodes(GetIslotForCategory(cat, idMap[cat])))
            {
                if (_zmapIndex.TryGetValue(code, out var idx) && idx < best)
                {
                    best = idx;
                    found = true;
                }
            }
            if (found) placements.Add((best, cat));
        }

        var locks = new Dictionary<string, string>();
        // 底→顶（index 大先处理）后写覆盖 → 视觉上层（index 小）占槽；同层按 item id 升序 → id 大者占槽
        foreach (var (_, cat) in placements
            .OrderByDescending(p => p.Index)
            .ThenBy(p => idMap[p.Category], StringComparer.Ordinal))
        {
            var id = idMap[cat];
            foreach (var slot in VslotCodes(GetVslotForCategory(cat, id)))
                locks[slot] = id;
        }
        return locks;
    }

    /// <summary>槽位是否全部无主或主==自己（对齐 MapleSalon2 hasAllLocks）。</summary>
    private static bool SlotsOk(Dictionary<string, string> locks, string itemId, IEnumerable<string> slots)
    {
        foreach (var s in slots)
        {
            if (locks.TryGetValue(s, out var owner) && owner != itemId) return false;
        }
        return true;
    }

    /// <summary>vslot/islot 串按 2 字符切块成槽码（对齐 MapleSalon2 info/vslot.match(/.{1,2}/g)）。</summary>
    private static IEnumerable<string> VslotCodes(string codes)
    {
        for (int i = 0; i + 1 < codes.Length; i += 2)
        {
            yield return codes.Substring(i, 2);
        }
    }

    /// <summary>
    /// 类别 → islot 槽位层码（优先 WZ 真实数据 {img}/info/islot——MapleSalon2 用 item.islot；
    /// 本 WZ probe 实证：cap 1003953 islot=Cp、weapon 1572011 islot=WpSi、hair islot=Hr、acc 101/102/103
    /// islot=Af/Ay/Ae，硬编码默认值只作 WZ 缺失时的回退）。body/head/坐骑/椅子无装备槽数据，返回 ""。
    /// </summary>
    private string GetIslotForCategory(string cat, string itemId = "")
    {
        if (cat is "hair" or "face" or "cap" or "cape" or "coat" or "overall"
            or "pants" or "shoe" or "glove" or "shield" or "weapon"
            or "earring" or "faceaccessory" or "eyeaccessory")
        {
            var fromWz = GetSlotFromWz(itemId, vslot: false);
            if (fromWz != null) { return fromWz; }
        }
        return cat switch
        {
            "cap" => "Cp", "hair" => "Hr", "head" => "Hd", "face" => "Fc",
            "earring" => EarringSlots(itemId),
            "faceaccessory" => EarringSlots(itemId),
            "eyeaccessory" => EarringSlots(itemId),
            "coat" => "Ma", "overall" => "MaPn", "pants" => "Pn", "shoe" => "So",
            "glove" => "Gv", "shield" => "Si", "weapon" => "Wp", "cape" => "Sr",
            "body" => "Bd", _ => ""
        };
    }

    /// <summary>
    /// 类别 → vslot 槽串（优先 WZ 真实数据 {img}/info/vslot——MapleSalon2 用 item.vslot；
    /// 硬编码默认值在个别装备上错误：实证 cap 1003953 真实 vslot=CpH5（只占 Cp+H5，不锁前发 H1），
    /// 硬编码 CpH1H5 锁 H1 → 前发 hairOverHead 被错误隐藏（用户反馈「查看现在用的搭配发型展示有问题」；
    /// MapleSalon2 refreshLock 为 hasSelfLock||hasLayerLock，hairOverHead 层 smap=H1，帽子不占 H1 时
    /// 前发应保持可见）。WZ 无数据回退硬编码；坐骑/椅子（Tm/无）不参与遮挡锁，返回 ""。
    /// </summary>
    private string GetVslotForCategory(string cat, string itemId = "")
    {
        if (cat is "hair" or "face" or "cap" or "cape" or "coat" or "overall"
            or "pants" or "shoe" or "glove" or "shield" or "weapon"
            or "earring" or "faceaccessory" or "eyeaccessory")
        {
            var fromWz = GetSlotFromWz(itemId, vslot: true);
            if (fromWz != null) { return fromWz; }
        }
        return cat switch
        {
            "cap" => "CpH1H5", "hair" => "H1H2H3H4H5H6HfHsHb", "head" => "Hd", "face" => "Fc",
            "earring" => EarringSlots(itemId),
            "faceaccessory" => EarringSlots(itemId),
            "eyeaccessory" => EarringSlots(itemId),
            "coat" => "Ma", "overall" => "MaPn", "pants" => "Pn", "shoe" => "So",
            "glove" => "GlGw", "shield" => "Si", "weapon" => "Wp", "cape" => "Sr",
            "body" => "Bd", _ => ""
        };
    }

    /// <summary>从 WZ 读取装备真实 vslot/islot（{img}/info/vslot|islot，缓存 per itemId；无数据返回 null 走硬编码回退）。</summary>
    private string? GetSlotFromWz(string itemId, bool vslot)
    {
        if (string.IsNullOrEmpty(itemId) || !int.TryParse(itemId, out _)) { return null; }
        var cache = vslot ? _vslotCache : _islotCache;
        if (cache.TryGetValue(itemId, out var cached)) { return cached; }
        var root = ItemRoot(itemId);
        string? val = null;
        if (root.Length > 0)
        {
            val = _wz.WithWzLock(() => _wz.GetStringProperty($"{root}/info/{(vslot ? "vslot" : "islot")}"));
        }
        if (string.IsNullOrEmpty(val)) { val = null; }
        cache[itemId] = val;
        return val;
    }

    /// <summary>饰品槽位按 id 前缀（R8 校准真实 WZ：101→Af 面饰、102→Ay 眼饰、103→Ae 耳环；其余回退 Ae）。</summary>
    private static string EarringSlots(string? itemId)
    {
        if (!string.IsNullOrEmpty(itemId) && int.TryParse(itemId, out var n))
        {
            int part = n / 10000;
            if (part == 101) return "Af";
            if (part == 102) return "Ay";
        }
        return "Ae";
    }

    // ═══════════════════════════════════════════
    // 层名解析
    // ═══════════════════════════════════════════

    /// <summary>
    /// 披风特殊层别名（R13，对照 MapleSalon2 zmapIndex.fixLayers，落到本 WZ zmap 实际存在的层）。
    /// 不同披风 ZField 各异（cape/capeArm/capeBelowHair/capeOverHead/backCape…），本 WZ zmap 无对应层时
    /// 按此表归位：身前部件（capeArm→cape、capeOverHead 等）在身前层，身后部件（capeBack/capeBelowHair→backCape/hairBelowBody）
    /// 在身后层。修复前 capeBelowHair 被 ResolvePrepLayer 误解析到 zmap 顶层（idx 0）→ 披风盖住身体正面。
    /// 注：capeOverArm 本 WZ zmap 已有（idx 29），表内兜底仍指向 gloveWrist（MapleSalon2 1102335），仅在不命中时生效。
    /// </summary>
    private static readonly Dictionary<string, string> CapeLayerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 披风系（本项目原有）——
        ["capeArm"] = "cape",
        ["capeBack"] = "backCape",
        ["backCapeBelowHead"] = "backMailChestOverPants",
        ["capeBelowHair"] = "hairBelowBody",
        ["capeBelowChest"] = "mailChest",
        ["capeBelowBodyOverPants"] = "pants",
        ["capeOverHeadOverCap"] = "capAccessory",
        ["capeUnderBody"] = "capeBelowBody",
        ["capeWeapon"] = "capeOverHead",
        ["capeOverBody"] = "weaponOverBody",
        ["capeBelowHead"] = "armBelowHead",
        ["capeOverArm"] = "gloveWrist",
        // —— MapleSalon2 zmapIndex.fixLayers 其余条目（WZ 里层名与真实层不一致的装备）——
        ["accessoryFaceOverCap"] = "accessoryEyeOverCap",   // 1012781
        ["backBelowBody"] = "backShieldBelowBody",          // 1402233
        ["backCapAcssesary"] = "backHairBelowCap",          // 1012418
        ["backCapBelowHair"] = "backCap",                   // 1003169
        ["backWeaponBelowGlove"] = "backWeaponOverGlove",   // 1472196
        ["capBelowHair"] = "hairBelowBody",                 // 1004506
        ["capBelowBody"] = "hairBelowBody",                 // 1004962
        ["capBackHair"] = "hairOverHead",                   // 1006737
        ["capBelowHead"] = "capAccessoryBelowBody",         // 1003975
        ["gloveBelowHair"] = "hair",                        // 1082738
        ["hairBelowHead"] = "mailArmBelowHead",             // 33525
        ["mailChestBelowBody"] = "gloveWristBelowBody",     // 1053813
        ["shieldBelowArm"] = "weaponBelowArm",              // 1092062
        ["weapnBelowBody"] = "weaponBelowBody",             // 1702891（拼写错误）
        ["weponBelowBody"] = "weaponBelowBody",             // 1402235（拼写错误）
        ["weaponBelowArmOverHead"] = "head",                // 1332168
        ["weaponBelowGlove"] = "gloveWristOverBody",        // 1703541
        ["weaponBelowHand"] = "weaponOverArmBelowHead",     // 1702012
        ["weaponBelowHandOverBody"] = "weaponOverArmBelowHead", // 1702288
        ["weaponBelowHead"] = "weaponOverArmBelowHead",     // Blaster 武器
        ["weaponBelowbody"] = "weaponBelowBody",            // 1703234（大小写）
        ["weaponBodyBelow"] = "weaponBelowBody",            // 1412004
        ["weaponOverArmBelowBody"] = "weaponOverArmBelowHead",  // 1342028
        ["weaponOverBelowArm"] = "weaponOverArmBelowHead",  // 1703029
        ["weaponOverGloveBelowMailArm"] = "glove",          // 1702443
        ["weaponOverHead"] = "hairOverHead",                // 1703236
        ["weaponWrist"] = "gloveWrist",                     // 1472152
        ["weaponback"] = "backWeapon",                      // 1702556
        ["weaponWristOverGloveOverArm"] = "weaponOverArm",  // v0.8.20
    };

    private static string? ResolveCapeLayerAlias(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return CapeLayerAliases.TryGetValue(name, out var target) ? target : null;
    }

    private string ResolveLayer(Piece p)
    {
        var zmap = GetZmapTopToBottom();
        if (!string.IsNullOrEmpty(p.ZField))
        {
            if (_zmapIndex.ContainsKey(p.ZField)) return p.ZField;
            if (_zmapIndex.ContainsKey(p.ZField.ToLowerInvariant())) return p.ZField.ToLowerInvariant();
            // 披风特殊层别名（如 capeBelowHair→hairBelowBody）：必须在 prep 解析前，避免 Below 误解析到顶层
            var zAlias = ResolveCapeLayerAlias(p.ZField);
            if (zAlias != null) return zAlias;
            // 数值 z：zmap[count - 10 - z]（对齐 MapleSalon2 resolveUseablePieceName）
            if (int.TryParse(p.ZField, out var zn))
            {
                int zi = zmap.Count - 10 - zn;
                if (zi >= 0 && zi < zmap.Count) return zmap[zi];
            }
            // Below/Under/Over/Above 层名（如 hairBelowBody）
            var prep = ResolvePrepLayer(p.ZField);
            if (prep != null) return prep;
        }
        if (_zmapIndex.ContainsKey(p.PieceName)) return p.PieceName;
        if (_zmapIndex.ContainsKey(p.PieceName.ToLowerInvariant())) return p.PieceName.ToLowerInvariant();
        var nameAlias = ResolveCapeLayerAlias(p.PieceName);
        if (nameAlias != null) return nameAlias;
        var prep2 = ResolvePrepLayer(p.PieceName);
        if (prep2 != null) return prep2;
        return p.Category switch
        {
            "body" => "body", "head" => "head", "face" => "face",
            // ⚠️ 2026-08-15 修复：发型有两个 canvas——hair（后发，画在 head 之下）与 hairOverHead（前发，画在 head 之上）。
            // 旧 fallback 一律 "hairOverHead" → 后发被画到最上层，盖住脸/帽子（用户反馈「头发渲染有问题」）。
            // 对齐 MapleSalon2 resolveUseablePieceName（fallback islot[0]='Hr' 头发专属层）：
            // 裸 hair / hairBelow* → hairBelowHead（头下），hairOver* → hairOverHead（头上）；zmap 有裸 hair 层则用。
            "hair" => ResolveHairLayer(p),
            "cap" => "cap", "coat" => "coat", "overall" => "coat",
            "pants" => "pants", "shoe" => "shoe", "glove" => "glove",
            "weapon" => "weapon", "shield" => "shield", "cape" => "cape",
            // 饰品三分类缺省层统一回退 accessory（WZ 真实 z 字段 accessoryFace/accessoryEyeOverCap/accessoryEar
            // 由 ResolveLayer 按 zmap 直接命中，此回退仅在部件无 z 时兜底）
            "earring" => "accessory", "faceaccessory" => "accessory", "eyeaccessory" => "accessory",
            // R13 节点级装备特效：恒落 effect 层（zmap 顶部注册，见 GetZmapTopToBottom）
            "effect" => "effect",
            // R1 坐骑/椅子：z 字段缺失时回退层——坐骑 tamingMobMid（角色身下）、椅子 tamingMobRear（角色身后）
            "mount" => "tamingMobMid", "chair" => "tamingMobRear",
            _ => p.PieceName
        };
    }

    /// <summary>发型部件层解析（2026-08-15，对齐 MapleSalon2）：前发 Over 系列 → hairOverHead；后发 → hairBelowHead；zmap 有裸 hair 层优先。</summary>
    private string ResolveHairLayer(Piece p)
    {
        var name = p.PieceName;
        if (_zmapIndex.ContainsKey(name)) return name;
        var lower = name.ToLowerInvariant();
        if (_zmapIndex.ContainsKey(lower)) return lower;
        if (name.Contains("Over", StringComparison.OrdinalIgnoreCase)) return "hairOverHead";
        if (name.Contains("Below", StringComparison.OrdinalIgnoreCase)) return "hairBelowHead";
        // 裸 hair：zmap 有 hair 层（桌宠 WZ 用 slot 缩写 Hr）→ 优先；否则后发默认 head 之下
        if (_zmapIndex.ContainsKey("hair")) return "hair";
        if (_zmapIndex.ContainsKey("Hr")) return "Hr";
        return "hairBelowHead";
    }

    /// <summary>
    /// Below/Under/Over/Above 层名解析：基于相邻层 index 推断并注册到 zmap 索引
    /// （对齐 MapleSalon2 isPossiblyResolvableLayer：below/under = target-1，over/above = target+1）。
    /// </summary>
    private string? ResolvePrepLayer(string name)
    {
        var m = Regex.Match(name, "(Below|Under|Over|Above)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var parts = Regex.Split(name, "(Below|Under|Over|Above)", RegexOptions.IgnoreCase);
        if (parts.Length < 3) return null;
        string baseName = parts[0];
        if (string.IsNullOrEmpty(baseName)) return null;
        if (!_zmapIndex.TryGetValue(baseName, out int index))
        {
            var bl = baseName.ToLowerInvariant();
            if (!_zmapIndex.TryGetValue(bl, out index)) index = 0;
        }
        for (int i = 1; i + 1 < parts.Length; i += 2)
        {
            string prep = parts[i];
            string target = parts[i + 1];
            int tIdx = _zmapIndex.TryGetValue(target, out var ti) ? ti : 0;
            if (prep.Equals("Below", StringComparison.OrdinalIgnoreCase) || prep.Equals("Under", StringComparison.OrdinalIgnoreCase))
                index = tIdx - 1;
            else
                index = tIdx + 1;
        }
        if (index < 0) index = 0;
        _zmapIndex[name] = index;
        _zmapIndex[name.ToLowerInvariant()] = index;
        return name;
    }

    // ═══════════════════════════════════════════
    // 部件根 / union bounds / 元数据缓存
    // ═══════════════════════════════════════════

    private static int BodyNumber(CharacterAppearance a)
    {
        if (a.BodyId >= 2000 && a.BodyId <= 2999) return a.BodyId;
        return a.Gender == 0 ? 2000 : 2001;
    }
    /// <summary>
    /// 该 body 动作帧是否显示面部：WZ `{body}/{action}/{frame}/face`（1=显示；0=背面动作，如 ladder/rope）。
    /// 字段缺失按「显示」处理（老版本 WZ / 特殊动作）。对齐 sdlMS 的 bone_data.face 与 MapleSalon2 isBackAction。
    /// </summary>
    private bool IsBodyFaceVisible(CharacterAppearance a, string action, int frame)
    {
        try
        {
            string path = $"{BodyRoot(a)}/{action}/{frame}/face";
            return _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath(path);
                if (node == null) { return true; }
                try { return node.GetValue<int>() != 0; } catch { return true; }
            });
        }
        catch { return true; }
    }

    private static string BodyRoot(CharacterAppearance a) => $"Character/{BodyNumber(a):D8}.img";
    private static string HeadRoot(CharacterAppearance a) => $"Character/{BodyNumber(a) + 10000:D8}.img";

    private static bool HasId(ItemInfo? it) => it != null && !string.IsNullOrEmpty(it.Id) && int.TryParse(it.Id, out _);

    /// <summary>装备 id → WZ img 根路径（目录映射参考 MapleSalon2 itemFolder.ts，统一 8 位补零）。</summary>
    private static string ItemRoot(string id)
    {
        int n = int.TryParse(id, out var v) ? v : 0;
        int part = n / 10000;
        string folder = part switch
        {
            2 or 5 => "Face/",
            3 or 4 or 6 or 7 => "Hair/",
            100 => "Cap/",
            101 or 102 or 103 => "Accessory/",
            104 => "Coat/",
            105 => "Longcoat/",
            106 => "Pants/",
            107 => "Shoes/",
            108 => "Glove/",
            109 => "Shield/",
            110 => "Cape/",
            _ when part >= 112 && part <= 119 => "Accessory/",
            _ when part >= 121 && part <= 170 => "Weapon/",
            _ when part >= 190 && part <= 199 => "TamingMob/",
            // 注：椅子（301 系）不走 ItemRoot——Install 目录组织与 Character 装备不同
            // （Item/Install/{id/1000 分组}.img/{id} 节点），由 ChairRoot 独立探测。
            _ => ""
        };
        return $"Character/{folder}{id.PadLeft(8, '0')}.img";
    }

    /// <summary>坐骑 img 根路径：Character/TamingMob/{id:D8}.img（190-199 系；结构同角色部件）。</summary>
    private static string MountRoot(string id)
    {
        if (!int.TryParse(id, out var n) || n <= 0) return "";
        return $"Character/TamingMob/{n:D8}.img";
    }

    /// <summary>
    /// 椅子 img 根路径探测：Item/Install（或 Cash）/{id/1000:D5}.img 或 {id/100:D6}.img 或 {id/10000:D4}.img 或旧版 0301.img，
    /// 节点 = id 补零 8 位。2026-08-16 probe 实证分组：3010xxx→03010.img（id/1000）、3015xxx→030150.img（id/100）、
    /// 3020xxx→0302.img（id/10000）；缺 id/100 分组时 3015000 系渲染找不到（原只试 id/1000 和 id/10000）。
    /// 未命中返回 ""（渲染跳过，不崩溃）。
    /// </summary>
    private string ChairRoot(string id)
    {
        if (!int.TryParse(id, out var n) || n <= 0) return "";
        string node = n.ToString("D8");
        string[] candidates =
        {
            $"Item/Install/{n / 1000:D5}.img/{node}",
            $"Item/Install/{n / 100:D6}.img/{node}",
            $"Item/Install/{n / 10000:D4}.img/{node}",
            $"Item/Install/0301.img/{node}",
            $"Item/Cash/{n / 1000:D5}.img/{node}",
            $"Item/Cash/{n / 100:D6}.img/{node}",
            $"Item/Cash/{n / 10000:D4}.img/{node}",
        };
        foreach (var c in candidates)
        {
            if (_wz.WithWzLock(() => _wz.FindNodeByPath(c) != null)) return c;
        }
        return "";
    }

    private List<(string Category, string Root)> GetRoots(string hash, CharacterAppearance a)
    {
        if (_rootCache.TryGetValue(hash, out var cached)) return cached;
        var list = new List<(string, string)>
        {
            ("body", BodyRoot(a)),
            ("head", HeadRoot(a))
        };
        if (HasId(a.Hair)) list.Add(("hair", ItemRoot(a.Hair!.Id)));
        if (HasId(a.Face)) list.Add(("face", ItemRoot(a.Face!.Id)));
        if (HasId(a.Cap)) list.Add(("cap", ItemRoot(a.Cap!.Id)));
        if (HasId(a.Cape)) list.Add(("cape", ItemRoot(a.Cape!.Id)));
        if (HasId(a.Coat)) list.Add(("coat", ItemRoot(a.Coat!.Id)));
        if (HasId(a.Overall)) list.Add(("overall", ItemRoot(a.Overall!.Id)));
        if (HasId(a.Pants)) list.Add(("pants", ItemRoot(a.Pants!.Id)));
        if (HasId(a.Shoes)) list.Add(("shoe", ItemRoot(a.Shoes!.Id)));
        if (HasId(a.Glove)) list.Add(("glove", ItemRoot(a.Glove!.Id)));
        if (HasId(a.Shield)) list.Add(("shield", ItemRoot(a.Shield!.Id)));
        if (HasId(a.Weapon)) list.Add(("weapon", ItemRoot(a.Weapon!.Id)));
        // 饰品三分类（对齐 MapleSalon2 101 面饰 / 102 眼饰 / 103 耳环，可同时穿戴，各自独立槽位）
        if (HasId(a.FaceAccessory)) list.Add(("faceaccessory", ItemRoot(a.FaceAccessory!.Id)));
        if (HasId(a.EyeAccessory)) list.Add(("eyeaccessory", ItemRoot(a.EyeAccessory!.Id)));
        if (HasId(a.Earring)) list.Add(("earring", ItemRoot(a.Earring!.Id)));
        // R1 坐骑/椅子：穿戴配置进渲染链路（TamingMob/Install；椅子路径探测命中才加）
        if (!string.IsNullOrEmpty(a.Mount?.Id))
        {
            var mountRoot = MountRoot(a.Mount!.Id);
            if (mountRoot.Length > 0) list.Add(("mount", mountRoot));
        }
        if (!string.IsNullOrEmpty(a.Chair?.Id))
        {
            var chairRoot = ChairRoot(a.Chair!.Id);
            if (chairRoot.Length > 0) list.Add(("chair", chairRoot));
        }
        _rootCache[hash] = list;
        return list;
    }

    /// <summary>该 action 全部帧的联合包围盒（union bounds，缓存）。</summary>
    private Bounds GetBounds(string hash, CharacterAppearance a, string action)
    {
        // 2026-08-16：去掉整体外层 WithWzLock——原整体持锁会让后台 Warmup（穿戴新外观首次
        // 冷加载 4.5s）垄断锁，UI 线程主窗口动画（DispatcherTimer 33ms）每帧 RenderFrame 抢锁
        // 被阻塞 → 穿戴「神之子头冠」等带特效装备时动画卡死数秒（用户反馈卡顿）。
        // 内部每步（CollectPiecesForFrame/GetMeta/GetBmp/GetPieceSize/GetActionFrameCount）已各自
        // WithWzLock 细粒度串行化 WZ 读取（Monitor 可重入），缓存全部 ConcurrentDictionary 线程安全。
        string key = $"{hash}|{action}";
        if (_boundsCache.TryGetValue(key, out var cached)) return cached;

        // 胶水 2026-08-18：逻辑动作名（stand/walk）先解析到实际动作（stand1/2）再查帧数——
        // 原直接 GetActionFrameCount(body, "stand") = 0 → bounds 0x0 → 渲染 FAIL（bounds 不走 ResolveRenderAction）。
        string actualAction = ResolveLogicalAction(a, action);
        int frameCount = GetActionFrameCount(BodyRoot(a), actualAction);
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        for (int f = 0; f < frameCount; f++)
        {
            var pieces = MaterializePieces(CollectPiecesForFrame(hash, a, action, f, ExpressionDefault));
            ResolveAnchors(pieces);
            // 该帧 body 锚点作为画布原点参考（照抄 MapleSalon2：bodyFrame.pivot = body ancher）
            var bodyP = pieces.FirstOrDefault(p => p.Category == "body" && p.Locked);
            int bx = bodyP?.AnchorX ?? 0;
            int by = bodyP?.AnchorY ?? 0;
            // 该帧 move（bounds 需容纳位移）
            int fmvX = 0, fmvY = 0;
            var fmv = GetFrameMove(hash, a, action, f);
            if (fmv != null) { fmvX = fmv.X; fmvY = fmv.Y; }
            foreach (var p in pieces.Where(x => x.Locked))
            {
                var (w, h) = GetPieceSize(p.PiecePath);
                minX = Math.Min(minX, p.FinalX - bx + fmvX); minY = Math.Min(minY, p.FinalY - by + fmvY);
                maxX = Math.Max(maxX, p.FinalX - bx + fmvX + w); maxY = Math.Max(maxY, p.FinalY - by + fmvY + h);
            }
        }
        var bounds = new Bounds(minX, minY, maxX - minX, maxY - minY);
        _boundsCache[key] = bounds;
        return bounds;
    }

    private PieceMeta? GetMeta(string piecePath)
    {
        if (_metaCache.TryGetValue(piecePath, out var m)) return m;
        // origin/map/z 复合读取一次 WithWzLock 串行化（与后台预热原子）
        var meta = _wz.WithWzLock(() => new PieceMeta
        {
            Ox = _wz.GetOrigin(piecePath).x,
            Oy = _wz.GetOrigin(piecePath).y,
            Map = _wz.GetPieceMap(piecePath),
            ZField = _wz.GetZField(piecePath) ?? ""
        });
        _metaCache[piecePath] = meta;
        return meta;
    }

    private SKBitmap? GetBmp(string piecePath)
    {
        if (_bmpCache.TryGetValue(piecePath, out var b)) return b;
        // ExtractPng 在锁内执行（与后台预热串行化）
        var png = _wz.WithWzLock(() => _wz.ExtractPng(piecePath));
        if (png == null || png.Length == 0) return null;
        var bmp = SKBitmap.Decode(png);
        if (bmp == null) return null;
        _bmpCache[piecePath] = bmp;
        EnforceBmpCap(_bmpCache);
        return bmp;
    }

    /// <summary>
    /// 轻量尺寸（2026-08-16 冰凌披风特效卡顿优化）：优先 _sizeCache；未命中走 GetPngSize
    /// （只读 Wz_Png 头，不编码不解码）；GetPngSize 失败回退 GetBmp（兜底）。GetBounds 全量使用。
    /// </summary>
    private (int W, int H) GetPieceSize(string piecePath)
    {
        if (_sizeCache.TryGetValue(piecePath, out var s)) return s;
        var sz = _wz.WithWzLock(() => _wz.GetPngSize(piecePath));
        if (sz == null)
        {
            var bmp = GetBmp(piecePath);
            return bmp != null ? (bmp.Width, bmp.Height) : (0, 0);
        }
        _sizeCache[piecePath] = sz.Value;
        return sz.Value;
    }

    /// <summary>
    /// 位图缓存容量上限（R11）：ConcurrentDictionary 无访问序，超出上限时移除一半条目（简化 LRU）。
    /// 上限 512 张覆盖整套外观的部件位图绰绰有余；只在外观批量切换/长会话中偶尔触发，防无界常驻。
    /// 注意：只移除引用不显式 Dispose——另一线程可能正在 GetBmp 后绘制该位图，显式释放原生句柄
    /// 会与在途渲染竞态（同 M1 ClearSpriteCache 结论）；原生内存由 GC 终结器回收。
    /// </summary>
    private static void EnforceBmpCap(ConcurrentDictionary<string, SKBitmap> cache)
    {
        if (cache.Count < MaxBmpCache) return;
        int evictTarget = MaxBmpCache / 2;
        int removed = 0;
        foreach (var kv in cache)
        {
            if (removed >= evictTarget) break;
            if (cache.TryRemove(kv.Key, out _)) removed++;
        }
    }

    /// <summary>染发位图：hair/face 部件带 Hue/Sat/Bright 时返回缓存染色位图（逐像素 HSV 调整），否则 null。</summary>
    private SKBitmap? GetDyeBmp(CharacterAppearance a, string category, string piecePath)
    {
        if (category != "hair" && category != "face") return null;
        var it = category == "hair" ? a.Hair : a.Face;
        if (it == null) return null;
        bool active = it.Dye != 0 || it.Hue != 0 || it.Saturation != 0 || it.Brightness != 0;
        if (!active) return null;
        string key = $"{it.Id}|{it.Hue}|{it.Saturation}|{it.Brightness}|{piecePath}";
        if (_dyeBmpCache.TryGetValue(key, out var b)) return b;
        var src = GetBmp(piecePath);
        if (src == null) return null;
        var dyed = ApplyHsv(src, it.Hue, it.Saturation / 100f, it.Brightness / 100f);
        if (dyed != null)
        {
            _dyeBmpCache[key] = dyed;
            EnforceBmpCap(_dyeBmpCache);
        }
        return dyed;
    }

    /// <summary>逐像素 HSV 调整（hue 旋转 + 饱和度 + 明度）。BGRA premul 输出，透明保留。</summary>
    private static SKBitmap? ApplyHsv(SKBitmap src, float hueDeg, float sat, float val)
    {
        try
        {
            var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(dst))
                canvas.DrawBitmap(src, 0, 0);
            using var pixmap = dst.PeekPixels();
            int count = src.Width * src.Height;
            unsafe
            {
                byte* px = (byte*)pixmap.GetPixels();
                for (int i = 0; i < count; i++)
                {
                    byte* p = px + i * 4;
                    if (p[3] == 0) continue;
                    // BGRA premul：先除 alpha 得直通色
                    float a = p[3] / 255f;
                    float r = a > 0 ? p[2] / 255f / a : 0;
                    float g = a > 0 ? p[1] / 255f / a : 0;
                    float b = a > 0 ? p[0] / 255f / a : 0;
                    float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    float delta = max - min;
                    float h = 0;
                    if (delta > 0)
                    {
                        if (max == r) h = 60f * (((g - b) / delta) % 6);
                        else if (max == g) h = 60f * (((b - r) / delta) + 2);
                        else h = 60f * (((r - g) / delta) + 4);
                        if (h < 0) h += 360;
                    }
                    float s = max > 0 ? delta / max : 0;
                    float v = max;
                    h = (h + hueDeg) % 360; if (h < 0) h += 360;
                    s = Math.Clamp(s * (1 + sat), 0, 1);
                    v = Math.Clamp(v * (1 + val), 0, 1);
                    float c2 = v * s;
                    float x = c2 * (1 - Math.Abs(((h / 60f) % 2) - 1));
                    float m2 = v - c2;
                    float nr, ng, nb;
                    if (h < 60) { nr = c2; ng = x; nb = 0; }
                    else if (h < 120) { nr = x; ng = c2; nb = 0; }
                    else if (h < 180) { nr = 0; ng = c2; nb = x; }
                    else if (h < 240) { nr = 0; ng = x; nb = c2; }
                    else if (h < 300) { nr = x; ng = 0; nb = c2; }
                    else { nr = c2; ng = 0; nb = x; }
                    p[2] = (byte)Math.Clamp((nr + m2) * a * 255, 0, 255);
                    p[1] = (byte)Math.Clamp((ng + m2) * a * 255, 0, 255);
                    p[0] = (byte)Math.Clamp((nb + m2) * a * 255, 0, 255);
                }
            }
            return dst;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] ApplyHsv: {ex.Message}"); return null; }
    }

    // ═══════════════════════════════════════════
    // zmap
    // ═══════════════════════════════════════════

    private List<string> GetZmapTopToBottom()
    {
        if (_zmapCache != null) return _zmapCache;
        var zmap = _wz.GetZmapOrder();
        if (zmap.Count == 0)
        {
            zmap = FallbackZmapTopToBottom.ToList();
        }
        else
        {
            // 桌宠 WZ zmap.img 是顶→底（mobEquipFront 顶 … Bd 底），直接用，不 reverse
            // （MapleSalon2 后端 zmap 是底→顶才 reverse，桌宠 WZ 不同源；reverse 会导致层级全反→披风错位）
        }
        _zmapIndex.Clear();
        for (int i = 0; i < zmap.Count; i++)
        {
            _zmapIndex[zmap[i]] = i;
            if (zmap[i].Length > 2) _zmapIndex[zmap[i].ToLowerInvariant()] = i;
        }
        // R13 特效层：真实 zmap（183 层）无 "effect" 层；MapleSalon2 zmapIndex 末尾 push('effect') 置顶
        // （Pixi 容器 zIndex 大=后画=顶层；本服务 zmap 顶→底 idx 0=顶层）→ 注册为最顶层，节点级特效落此层。
        if (!_zmapIndex.ContainsKey("effect"))
        {
            _zmapIndex["effect"] = 0;
        }
        _zmapCache = zmap;
        return zmap;
    }

    private int GetActionFrameCount(string bodyRoot, string action)
    {
        try
        {
            return _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath($"{bodyRoot}/{action}");
                if (node == null)
                {
                    return 0;
                }
                int count = 0;
                foreach (Wz_Node child in node.Nodes)
                    if (int.TryParse(child.Text, out _)) count++;
                return count;
            });
        }
        catch { return 0; }
    }

    /// <summary>纸娃娃动作简体中文名（对齐 MapleSalon2 zh-TW 转简体，actions.ts 动作集）。</summary>
    public static readonly Dictionary<string, string> ActionNames = new()
    {
        ["stand1"] = "站立", ["stand2"] = "站立(双手)",
        ["walk1"] = "行走", ["walk2"] = "行走(双手)",
        ["alert"] = "警戒", ["fly"] = "飞行", ["heal"] = "施放", ["jump"] = "跳跃",
        ["ladder"] = "攀爬(梯子)", ["rope"] = "攀爬(绳子)",
        ["prone"] = "趴下", ["proneStab"] = "趴下攻击", ["sit"] = "坐下",
        ["shoot1"] = "射击1", ["shoot2"] = "射击2", ["shootF"] = "射击F",
        ["stabO1"] = "刺击1", ["stabO2"] = "刺击2", ["stabOF"] = "刺击F",
        ["stabT1"] = "刺击T1", ["stabT2"] = "刺击T2", ["stabTF"] = "刺击TF",
        ["swingO1"] = "挥击1", ["swingO2"] = "挥击2", ["swingO3"] = "挥击3", ["swingOF"] = "挥击F",
        ["swingP1"] = "挥击P1", ["swingP2"] = "挥击P2", ["swingPF"] = "挥击PF",
        ["swingT1"] = "挥击T1", ["swingT2"] = "挥击T2", ["swingT3"] = "挥击T3", ["swingTF"] = "挥击TF",
    };

    public List<string> GetActionList()
    {
        try
        {
            List<string>? actions = _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath("Character/00002000.img");
                if (node == null) return null;
                var list = new List<string>();
                foreach (Wz_Node child in node.Nodes)
                    if (child.Text != "info") list.Add(child.Text);
                return list.Count > 0 ? list : null;
            });
            if (actions != null) return actions;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] GetActionList: {ex.Message}"); }
        return new() { "stand1", "stand2", "walk1", "walk2", "jump", "alert", "swingOF", "stabOF" };
    }

    // ═══════════════════════════════════════════
    // 外观哈希
    // ═══════════════════════════════════════════

    public static string HashAppearance(CharacterAppearance a)
    {
        var sb = new StringBuilder();
        sb.Append(a.Gender).Append('|').Append(a.Skin).Append('|').Append(a.BodyId).Append('|').Append(a.Ear ?? "").Append('|');
        AppendId(sb, a.Hair); AppendId(sb, a.Face); AppendId(sb, a.Cap); AppendId(sb, a.Cape);
        AppendId(sb, a.Coat); AppendId(sb, a.Overall); AppendId(sb, a.Pants); AppendId(sb, a.Shoes);
        AppendId(sb, a.Glove); AppendId(sb, a.Shield); AppendId(sb, a.Weapon);
        // 饰品三分类（101 面饰 / 102 眼饰 / 103 耳环）各自进指纹：换任一分类自动失效相关缓存 + 离线条缓存键
        AppendId(sb, a.FaceAccessory); AppendId(sb, a.EyeAccessory); AppendId(sb, a.Earring);
        // R1：坐骑/椅子 id 进指纹（换坐骑/椅子自动失效相关缓存 + 离线条缓存键）
        sb.Append(a.Mount?.Id ?? "").Append('|').Append(a.Chair?.Id ?? "").Append('|');
        // R13：装备特效开关进指纹（切换开关自动失效特效相关缓存 + 离线条缓存键）
        sb.Append(a.EnableEffect ? "1" : "0").Append('|');
        return sb.ToString();
    }

    private static void AppendId(StringBuilder sb, ItemInfo? it)
    {
        if (it == null || string.IsNullOrEmpty(it.Id)) { sb.Append("-|"); return; }
        sb.Append(it.Id).Append(':').Append(it.Dye).Append(':').Append(it.Hue).Append(':')
          .Append(it.Saturation).Append(':').Append(it.Brightness).Append('|');
    }

    private string ResolveExpression()
    {
        // 手动/自动表情优先（未过期）→ 否则交回 agent 驱动（IExpressionDriver，未来 Hermes 按状态注入）
        string manual = _manualExpression;
        if (!string.Equals(manual, ExpressionDefault, StringComparison.Ordinal) && DateTime.UtcNow < _manualExpireUtc)
        {
            return manual;
        }
        try { return ExpressionDriver?.GetExpression() ?? ExpressionDefault; }
        catch { return ExpressionDefault; }
    }

    private List<string>? _validActionsCache;
    private readonly ConcurrentDictionary<string, bool> _actionValidCache = new();

    // ═══════════════════════════════════════════
    // 离线缓存（WZ 断线兜底）：把当前外观合成精灵图条存 CacheManager 哨兵
    // ═══════════════════════════════════════════

    private static readonly string[] OfflineActions = { "stand1", "stand2", "walk1", "walk2", "jump" };

    // 拖拽/常用动作预热（减少运行时首次合成卡顿抖动）
    private static readonly string[] WarmupActions = { "stand1", "stand2", "swingO3", "swingOF", "stabOF", "fly", "walk1", "walk2", "jump", "alert" };

    /// <summary>预热拖拽/常用动作的 union bounds、move 与部件位图缓存。在后台调用（启动/换装后台 Task，
    /// 与主窗口合成线程错开）。2026-08-16：增加 move + 位图预解码——动画帧 RenderFrame 热帧零锁零解码
    /// （穿戴新外观时后台预热特效帧首次解码 4.2s 持锁，动画不被阻塞；否则动画每帧首次访问新帧位图
    /// ExtractPng 撞锁卡数秒）。</summary>
    public void Warmup()
    {
        if (_current == null || !_wz.IsLoaded) return;
        WarmupFor(_current);
    }

    /// <summary>
    /// 预热指定外观（2026-08-16 神之子头冠卡顿修复）：与 Warmup 不同——**不切换 _current、不清缓存**。
    /// 原 ApplyPaperdoll 流程：后台 SetAppearance(新外观) 清空全部缓存 → 预热持锁期间主窗口旧动画
    /// （UI 线程 DispatcherTimer）每帧 RenderFrame 缓存失效重新读 WZ 抢锁 → 动画卡死 4.5s（首次冷加载）。
    /// WarmupFor 只按传入外观的 hash 算 bounds/部件缓存（key 含新外观 hash，不污染旧外观缓存），
    /// 并预解码部件位图 + 缓存 move——预热完成后调用方再 SetAppearance 切换，切换即热（动画零锁零解码）。
    /// </summary>
    public void WarmupFor(CharacterAppearance a)
    {
        if (a == null || !_wz.IsLoaded) return;
        var hash = HashAppearance(a);
        foreach (var action in WarmupActions)
        {
            try
            {
                if (GetActionFrameCount(BodyRoot(a), action) <= 0) continue;
                GetBounds(hash, a, action);
                // 预解码该动作各帧部件位图 + move（特效帧首次 ExtractPng 4.2s 在后台完成，
                // 切换后动画首帧不再触发冷解码持锁）
                int fc = GetActionFrameCount(BodyRoot(a), action);
                for (int f = 0; f < fc; f++)
                {
                    GetFrameMove(hash, a, action, f);
                    var pieces = MaterializePieces(CollectPiecesForFrame(hash, a, action, f, ExpressionDefault));
                    foreach (var p in pieces)
                    {
                        if (p.Category == "body" || p.Category == "head" || p.Category == "hair" || p.Category == "face"
                            || p.Category == "cap" || p.Category == "cape" || p.Category == "coat" || p.Category == "pants"
                            || p.Category == "shoe" || p.Category == "glove" || p.Category == "weapon" || p.Category == "shield"
                            || p.Category == "earring" || p.Category == "faceaccessory" || p.Category == "eyeaccessory"
                            || p.Category == "effect")
                        {
                            GetBmp(p.PiecePath);
                        }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>为当前外观合成离线精灵图条（WZ 不可用时经 AnimService 图条路径播放兜底）。</summary>
    public void BuildOfflineStrips()
    {
        if (_current == null || !_wz.IsLoaded) return;
        var hash = _currentHash;
        // R4：缓存键含外观指纹（SentinelMobId + hash），换外观自动失效
        string offlineMobId = OfflineCacheMobId;
        foreach (var action in OfflineActions)
        {
            try
            {
                int frameCount = GetActionFrameCount(BodyRoot(_current), action);
                if (frameCount <= 0) continue;
                var bounds = GetBounds(hash, _current, action);
                if (bounds.W <= 0 || bounds.H <= 0) continue;

                int stripW = bounds.W * frameCount;
                using var strip = new SKBitmap(stripW, bounds.H, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(strip);
                canvas.Clear(SKColors.Transparent);
                var frameData = new List<FrameData>();
                for (int f = 0; f < frameCount; f++)
                {
                    int dx = f * bounds.W;
                    var pieces = MaterializePieces(CollectPiecesForFrame(hash, _current, action, f, ExpressionDefault));
                    ResolveAnchors(pieces);
                    AssignLayers(pieces);
                    // R2：离线图条与实时渲染一致应用遮挡锁（帽子遮发/套服遮裤等），且排除被锁隐藏的部件
                    ApplyLocks(hash, _current, pieces);
                    foreach (var p in pieces.Where(x => x.Locked && !x.Hidden).OrderByDescending(x => x.ZIndex))
                    {
                        var pb = GetBmp(p.PiecePath);
                        if (pb == null) continue;
                        var dyeBmp = GetDyeBmp(_current!, p.Category, p.PiecePath);
                        if (dyeBmp != null)
                            canvas.DrawBitmap(dyeBmp, dx + p.FinalX - bounds.Left, p.FinalY - bounds.Top);
                        else
                            canvas.DrawBitmap(pb, dx + p.FinalX - bounds.Left, p.FinalY - bounds.Top);
                    }
                    int delay = _wz.GetDelay($"{BodyRoot(_current)}/{action}/{f}");
                    frameData.Add(new FrameData { Index = f, Delay = delay > 0 ? delay : 100, A0 = 255, A1 = 255 });
                }
                using var image = SKImage.FromBitmap(strip);
                var png = image.Encode(SKEncodedImageFormat.Png, 90).ToArray();
                if (png == null || png.Length == 0) continue;

                var stripObj = new SpriteStrip
                {
                    Action = action,
                    FrameCount = frameCount,
                    FrameWidth = bounds.W,
                    FrameHeight = bounds.H,
                    TotalWidth = stripW,
                    OriginX = -bounds.Left,
                    OriginY = -bounds.Top,
                    FrameData = frameData,
                    DefaultDelay = 100
                };
                var config = new PetConfig
                {
                    Name = "paperdoll",
                    Type = "player",
                    Sprites = new Dictionary<string, SpriteStrip> { [action] = stripObj }
                };
                var configJson = System.Text.Json.JsonSerializer.Serialize(config);
                var configBytes = System.Text.Encoding.UTF8.GetBytes(configJson);
                _cache.SaveSpriteStrip(offlineMobId, action, png, configBytes);
                _sprite.CacheStrip(offlineMobId, action, stripObj, png);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] BuildOfflineStrip({action}): {ex.Message}"); }
        }
    }

    // ═══════════════════════════════════════════
    // 选择 UI 辅助（素材库 / 设置中心）
    // ═══════════════════════════════════════════

    public List<EquippedItem> GetEquipList(string category)
    {
        try
        {
            return _wz.WithWzLock(() =>
            {
                var result = new List<EquippedItem>();
                var dirNode = _wz.FindNodeByPath($"Character/{category}");
                if (dirNode == null) return result;
                foreach (Wz_Node child in dirNode.Nodes)
                {
                    var name = child.Text.Replace(".img", "");
                    if (int.TryParse(name, out _))
                    {
                        var nameNode = child.FindNodeByPath("info/name")?.GetValueEx<string>(null);
                        result.Add(new EquippedItem { Id = name, Name = nameNode ?? $"{category} {name}", Category = category });
                        if (result.Count >= 200) break;
                    }
                }
                return result;
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] GetEquipList: {ex.Message}"); return new(); }
    }

    public void SetEquip(string category, string itemId)
    {
        if (_current == null) return;
        var item = new ItemInfo { Id = itemId ?? string.Empty };
        switch (category)
        {
            case "Cap": _current.Cap = item; break;
            case "Cape": _current.Cape = item; break;
            case "Coat": _current.Coat = item; break;
            case "Overall": _current.Overall = item; break;
            case "Pants": _current.Pants = item; break;
            case "Shoes": _current.Shoes = item; break;
            case "Weapon": _current.Weapon = item; break;
            case "Shield": _current.Shield = item; break;
            case "Glove": _current.Glove = item; break;
            case "Earring": _current.Earring = item; break;
        }
    }

    public void RemoveEquip(string category)
    {
        if (_current == null) return;
        switch (category)
        {
            case "Cap": _current.Cap = null; break;
            case "Cape": _current.Cape = null; break;
            case "Coat": _current.Coat = null; break;
            case "Overall": _current.Overall = null; break;
            case "Pants": _current.Pants = null; break;
            case "Shoes": _current.Shoes = null; break;
            case "Weapon": _current.Weapon = null; break;
            case "Shield": _current.Shield = null; break;
            case "Glove": _current.Glove = null; break;
            case "Earring": _current.Earring = null; break;
        }
    }

    // ═══════════════════════════════════════════
    // 内部类型
    // ═══════════════════════════════════════════

    private sealed class Bounds
    {
        public int Left, Top, W, H;
        public Bounds(int left, int top, int w, int h) { Left = left; Top = top; W = w; H = h; }
    }

    private sealed class PieceSource
    {
        public string Category;
        public string PieceName;
        public string PiecePath;
        public string ItemId;
        // R-chair：椅子 pos 偏移（-50/-30）与特效层层覆盖（"effect" 置顶）
        public int OffsetX;
        public int OffsetY;
        public string ZFieldOverride = "";
        public PieceSource(string category, string pieceName, string piecePath, string itemId)
        { Category = category; PieceName = pieceName; PiecePath = piecePath; ItemId = itemId; }
    }

    private sealed class PieceMeta
    {
        public int Ox, Oy;
        public Dictionary<string, (int x, int y)> Map = new();
        public string ZField = "";
    }

    private sealed class Piece
    {
        public string Category = "";
        public string PieceName = "";
        public string PiecePath = "";
        public string ItemId = "";
        public int Ox, Oy;
        public Dictionary<string, (int x, int y)> Map = new();
        public string ZField = "";
        // R-chair：椅子 pos 偏移（在锚点上叠加）
        public int OffsetX, OffsetY;
        public string ResolvedLayer = "";
        public int ZIndex;
        public int AnchorX, AnchorY;
        public string? BaseAnchor;
        public bool Locked;
        public bool Hidden;
        public int FinalX, FinalY;
    }

    /// <summary>层解析诊断条目（CapeProbe 消费）：部件落层 + 遮挡结果快照。</summary>
    public sealed class LayerProbeItem
    {
        public string Category = "";
        public string PieceName = "";
        public string ZField = "";
        public string ResolvedLayer = "";
        public int ZIndex;
        public bool Locked;
        public bool Hidden;
        public bool HasMap;
    }
}

/// <summary>表情驱动接口（预留）：外部（未来 Hermes/Claude/Codex gateway）按 agent 状态返回当前表情名。</summary>
public interface IExpressionDriver
{
    string GetExpression();
}

public class EquippedItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}
