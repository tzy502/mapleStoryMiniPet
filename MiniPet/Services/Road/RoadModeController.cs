using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Views;
using MiniPet.Services;
using SkiaSharp;
using Microsoft.Extensions.DependencyInjection;

namespace MiniPet.Services.Road
{
    /// <summary>D2 生成器句柄（反射创建后按契约接口 IRoadSurfaceProvider/IRoadColumnSource 消费）。</summary>
    internal sealed class RoadGeneratorHandle
    {
        public InfiniteRoadGenerator Obj = null!;
        public IRoadSurfaceProvider Road = null!;
        public IRoadColumnSource? Columns;
        public int InitialSurfaceY;
    }

    /// <summary>背景/宠物进入前快照（退出恢复用；PetWindow Attach 时提供取/还委托）。</summary>
    public sealed record RoadBackgroundSnapshot(bool BgVisible, bool VpVisible, int MapMode, Avalonia.PixelPoint PetPos);

    /// <summary>UI 命令种类（三路事件合并去重用——同 kind 未执行命令被新命令覆盖）。</summary>
    internal enum RoadCommandKind { State, Current, Wz }

    /// <summary>
    /// RoadModeController —— 模式3「无限之路」唯一编排者（PLAN Rev 5 运行时架构节全量落实）：
    /// - 输入合并：MusicPlayerService.CurrentChanged/StateChanged + WzService.WzReloaded 三路 →
    ///   代际标记命令队列 → Dispatcher.UIThread.Post 串行处理（Queue + 单飞行标志；同 kind 合并）；
    /// - RoadClock 唯一时钟：滚动/实体/战斗/crossfade/宠物动画 delta 全由它驱动（暂停 = 一切静止，
    ///   IsPaused 标志 + RoadClockScale∈{0,1} 叠加在相位之上，任何相位可暂停可恢复）；
    /// - 两段式 tick：BeginTick(dt) 在 AnimService.Tick 前（暂停=0 / 非路模式=null 透传原 delta）；
    ///   EndTick(dt) 在 origin 补偿后、pet:moved 哨兵前（推进时钟→补柱→Follower→Director→
    ///   发布不可变 RoadFrame 快照并 kick 渲染——Follower 定位在 origin 补偿之后，最终生效）；
    /// - 生命周期：Playing→Building（旧背景不动）→后台建包（在途 ct 取旧以新）→包就绪→隐藏旧窗
    ///   （记录旧模式/两窗可见态/宠物进入前屏幕位置）→ShowRoad 淡入 500ms→Running；
    ///   Stopped→Exiting 淡出 500ms→恢复旧背景与宠物复位→Inactive；失败→立即恢复旧背景+
    ///   RoadState.LastError 记因（音乐继续）；
    /// - 背景操作统一闸门 GateBackgroundAction：SemaphoreSlim 串行；路激活→先完整退出（await）再执行
    ///   原语义；退出场景携带地图命令抑制代际（ApplyMapSelection 尾部 OnMapChanged 被抑制一轮，
    ///   防 F17 自动重进模式3）；
    /// - 换歌 crossfade：新解析≠当前→后台建新包→Incoming 生成器（重定基：锚 scrollX+W/2、
    ///   initialSurfaceY 对齐旧路当前表面）→500ms alpha→交换（旧包条带缓存整代淘汰）；
    ///   更新的换歌取消上一条未完成 Incoming（代际）；
    /// - F17 互斥：MusicPlayerService.RoadActiveGate 静态门（本控制器构造时设置）——路激活期间
    ///   OnMapChanged 直接 return；
    /// - MapMode=3 纯运行时态：永不持久化（AppConfig.MapMode 只存恢复后的 1/2）；
    /// - 并行契约：D1（RoadMaterialExtractor/条带缓存）/D2（RoadMapResolver/InfiniteRoadGenerator）/
    ///   D3（InfiniteRoadWindow）经 RoadParallelGateway 反射按点名类型+方法签名绑定——落盘即接通，
    ///   未落盘时路激活失败并 LastError 注记（不影响其余功能）。
    /// 线程模型：状态机全部 UI 线程（命令队列派发 + PetWindow 定时器 EndTick）；后台仅素材构建。
    /// </summary>
    public sealed class RoadModeController
    {
        private readonly MusicPlayerService _music;
        private readonly WzService _wz;
        private readonly MusicCatalogService _catalog;
        private readonly ConfigService _config;
        private readonly RoadCombatDirector _director;
        private readonly AnimService _anim;              // 人物动画状态（纸娃娃实体快照 Action/FrameIndex 源）
        private readonly PaperdollService _paperdoll; // 面向控制（进路朝右与滚动方向一致，退出还原用户设置）
        private long _lastLogicTickMs; // 最近一帧 BeginTick 时刻（渲染外推基准；BeginTick 每帧无条件刷新，含暂停/Building/Exiting）
        private double? _lastExtrapScrollX; // 上一发布帧 ScrollOriginX（渲染外推方向按其帧间实际位移取号，与生成器推进方向耦合）
        private readonly PetFollower _follower;
        /// <summary>源图解析器（F18，持有回退链状态；候选迭代 ResolveWithPackAsync 供激活/换歌直取 Usable 包）。</summary>
        private readonly RoadMapResolver _resolver;

        /// <summary>模式3 运行态（UI 状态栏/测试断言锚）。</summary>
        public RoadState State { get; } = new();

        // ── UI 命令队列（代际标记 + 单飞行 + 同 kind 合并）──
        private readonly Queue<(RoadCommandKind Kind, Func<Task> Run)> _commands = new();
        private bool _commandFlying;

        // ── 路核心态（全部 UI 线程访问）──
        private RoadMaterialPack? _currentPack;
        private RoadGeneratorHandle? _currentGen;
        private int _currentBuildGen;
        private int _currentInitialSurfaceY;
        // 暂停标志（PLAN：IsPaused + RoadClockScale∈{0,1} 叠加在相位之上——任何相位可暂停可恢复；RoadState 契约未含此字段，控制器私有态，经 RoadFrame.IsPaused 对外发布）
        private bool _roadPaused;
        private List<RoadColumn>? _currentColSnapshot;   // 发布柱深拷贝缓存（列数/末柱变化才重建）
        private (int Count, double LastX) _currentColStamp;
        private IReadOnlyList<RoadBack>? _currentBacksNonFront;
        private IReadOnlyList<RoadBack>? _currentBacksFront;
        private RoadSky? _roadSky;        // 固定天空层（FINAL 拍板 #3：首次激活冻结 back 副本+自有锚点/相机；换歌不变，仅 CleanupRoadState 清空）
        private double _skyLastScrollX;   // Sky CamX delta 传播基准（上一帧 Current 路 scroll 原点——换歌更换原点时 delta 连续，天空视差相位不跳）
        private bool _roadWinZHooked;     // 路窗 z 序钩子已订阅（一次性）
        private bool _prevFlip;           // 进入路前用户的面朝设置（退出还原——Flip 语义见 PaperdollService.Flip）
        private string _roadTrackKey = ""; // 当前路归属曲目键（Img/Track——同曲 CurrentChanged 守卫：单曲循环/自然续播同曲不重解析不重铺带）
        private RoadMaterialPack? _incomingPack;
        private RoadGeneratorHandle? _incomingGen;
        private int _incomingGenTag;                      // 换歌代际（更新的取消上一条 Incoming）
        private CancellationTokenSource? _buildCts;
        private int _packSeq;                             // 素材构建代际发号器（柱位图缓存键组成）
        private double _roadClockMs;
        private long _frameLogSeq = 0;
        private bool _enterTiming;
        private double _enterStartMs;
        private bool _exitTiming;
        private double _exitStartMs;
        private TaskCompletionSource<bool>? _exitTcs;
        private bool _crossTiming;
        private double _crossStartMs;
        private double _crossfadeAlpha;
        private int _crossGen;

        // ── 进入前快照（退出恢复）──
        private RoadBackgroundSnapshot? _prevSnapshot;

        // ── 闸门（背景操作统一串行化）──
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _suppressMapCmdOnce;

        // ── PetWindow 接入（Attach 时提供）──
        private Window? _petWindow;
        private Func<RoadBackgroundSnapshot>? _snapshotBackground;
        private Action<RoadBackgroundSnapshot>? _restoreBackground;
        private Action? _hideBackground;

        /// <summary>路状态通知（进入/失败/不可用；UI 线程命令链内触发——订阅方直接弹气泡）。</summary>
        public event Action<string>? RoadNotice;

        /// <summary>实体帧位图 resolver（并行契约：D1 条带缓存 GetStrip(mapId,lifeId,action) 直连；未落盘为 null）。summary 见类注释。</summary>
        public Func<RoadEntityState, SKBitmap?>? EntityFrameResolver { get; set; }

        /// <summary>路是否激活（Building/Entering/Running/Exiting 任一相位）——F17 互斥门/背景闸门判定用。</summary>
        public bool IsRoadActive => State.Phase != RoadPhase.Inactive;

        /// <summary>路窗人物锚（Mode3 气泡用——人物在路窗中央、0.62h 表面上方；未激活返回 null）。</summary>
        public (int X, int Y)? GetRoadBalloonAnchor()
        {
            try
            {
                if (State.Phase == RoadPhase.Inactive)
                {
                    return null;
                }
                var win = RoadParallelGateway.EnsureRoadWindow();
                if (win == null || !win.IsVisible)
                {
                    return null;
                }
                return ((int)(win.Position.X + win.Bounds.Width / 2), (int)(win.Position.Y + win.Bounds.Height * 0.62 - 140));
            }
            catch { return null; }
        }

        public RoadModeController(MusicPlayerService music, WzService wz, MusicCatalogService catalog, ConfigService config, RoadCombatDirector director, AnimService anim, PaperdollService paperdoll)
        {
            _music = music;
            _wz = wz;
            _catalog = catalog;
            _config = config;
            _director = director;
            _anim = anim;
            _paperdoll = paperdoll;
            _follower = new PetFollower(anim);
            _resolver = new RoadMapResolver(catalog, wz);
            // F17 互斥门（静态可达——MusicPlayerService 构造无法注入本控制器，会形成 DI 环）
            MusicPlayerService.RoadActiveGate = () => IsRoadActive;
            // 三路输入 → 代际标记命令队列（全部经 Dispatcher.UIThread.Post 派发串行）
            _music.CurrentChanged += track => EnqueueCommand(RoadCommandKind.Current, () => HandleCurrentChangedAsync(track));
            _music.StateChanged += state => EnqueueCommand(RoadCommandKind.State, () => HandleStateChangedAsync(state));
            Console.Error.WriteLine($"[Road] Controller 就绪（CurrentChanged/StateChanged 已订阅；当前曲={_music.Current?.Img ?? "null"}/{_music.Current?.Track ?? "null"}）");
            Console.Error.Flush();
            _wz.WzReloaded += gen => EnqueueCommand(RoadCommandKind.Wz, () => HandleWzReloadedAsync(gen));
        }

        /// <summary>
        /// PetWindow 接入（构造注入后调用）：喂宠物窗口引用（Position 复位/尺寸读取）+
        /// 背景「取快照/隐藏/恢复」三委托（模式1/2 两窗口归 PetWindow 所有，控制器不直接触碰）。
        /// </summary>
        public void AttachPetWindow(Window petWindow, Func<RoadBackgroundSnapshot> snapshotBackground, Action<RoadBackgroundSnapshot> restoreBackground, Action hideBackground)
        {
            _petWindow = petWindow;
            _snapshotBackground = snapshotBackground;
            _restoreBackground = restoreBackground;
            _hideBackground = hideBackground;
        }

        // ═══════════════ 两段式 tick（PetWindow.OnTimerTick 调用） ═══════════════

        /// <summary>
        /// 帧前半段（AnimService.Tick 之前调用）：返回本帧宠物动画 delta——
        /// 非路模式（Inactive）返回 null（PetWindow 以原 delta 喂 Tick，行为与旧版一致）；
        /// 暂停返回 0（宠物动画随路全停）；其余相位原样透传。
        /// </summary>
        public double? BeginTick(double deltaMs)
        {
            // 「最近逻辑时刻」每帧无条件刷新（Inactive/Building/Exiting/暂停都刷，此前只在
            // EndTick 渲染路径更新——恢复瞬间基准陈旧会被外推 clamp 100ms 造成 60px/s×0.1s=6px 跳变）
            _lastLogicTickMs = Environment.TickCount64;
            if (State.Phase == RoadPhase.Inactive)
            {
                return null;
            }
            if (_roadPaused)
            {
                return 0;
            }
            return deltaMs;
        }

        /// <summary>
        /// 帧后半段（origin 补偿之后、pet:moved 哨兵之前调用）：推进 RoadClock（只加 dt×Scale）→
        /// 生成器补柱 → PetFollower 定位（覆盖 origin 补偿的窗口位移——宠物归路所有）→
        /// CombatDirector + 攻击观察 → 组装不可变 RoadFrame 快照 → 推 InfiniteRoadWindow 渲染。
        /// </summary>
        public void EndTick(double deltaMs)
        {
            try
            {
                if (State.Phase == RoadPhase.Inactive)
                {
                    return;
                }
                double dt = _roadPaused ? 0 : deltaMs;
                _roadClockMs += dt;
                // 帧状态日志（每 60 帧一行——人物位置列覆盖检查；"脚下地板"可观测锚）
                if (++_frameLogSeq % 60 == 0 && _currentGen != null)
                {
                    try
                    {
                        double petX = _currentGen.Road.ScrollX + (RoadConstants.FallbackWindowW / 2.0);
                        var colsAll = _currentGen.Columns?.ActiveColumns ?? (IReadOnlyList<RoadColumn>)Array.Empty<RoadColumn>();
                        var under = colsAll.FirstOrDefault(c => petX >= c.WorldX && petX < c.WorldX + c.WidthCols * RoadConstants.ColW);
                        double petFeet = _follower?.CurrentFeetY ?? 0;
                        double? surfY = _currentGen.Road?.SurfaceY(petX);
                        Console.Error.WriteLine($"[Road] Frame#{_frameLogSeq} cols={colsAll.Count} 人物x={petX:F0} 脚下列={(under != null ? $"有(宽{under.WidthCols})" : "无!")} tiles={under?.Template.Tiles.Count ?? -1} objs={under?.Template.Objs.Count ?? -1} FeetY=(pet){petFeet:F0}/(surfY){surfY?.ToString("F0") ?? "null"} 差={petFeet - (surfY ?? petFeet):F0}");
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[Road] FrameLog: {ex.Message}"); }
                }

                // 相位计时（Entering 淡入 → Running；Exiting 淡出完成在 FinishExit 收口）
                if (State.Phase == RoadPhase.Entering && _enterTiming && _roadClockMs - _enterStartMs >= RoadConstants.CrossfadeMs)
                {
                    State.Phase = RoadPhase.Running;
                    _enterTiming = false;
                }
                if (State.Phase == RoadPhase.Exiting && _exitTiming && _roadClockMs - _exitStartMs >= RoadConstants.CrossfadeMs)
                {
                    FinishExit();
                    return;
                }

                // 滚动推进（Current + Incoming 双原点各自推进；D2 未落盘时 Advance 失败路静止）
                if (_currentGen != null)
                {
                    RoadParallelGateway.AdvanceGenerator(_currentGen.Obj, dt);
                }
                if (_incomingGen != null)
                {
                    RoadParallelGateway.AdvanceGenerator(_incomingGen.Obj, dt);
                }

                // 换歌 crossfade alpha 推进 → 满即交换
                if (_crossTiming)
                {
                    _crossfadeAlpha = Math.Min(1.0, (_roadClockMs - _crossStartMs) / RoadConstants.CrossfadeMs);
                    if (_crossfadeAlpha >= 1.0)
                    {
                        SwapIncoming();
                    }
                }

                var win = RoadParallelGateway.EnsureRoadWindow();
            if (win != null && win.PetMenu == null)
            {
                win.PetMenu = _petWindow?.ContextMenu; // 右键入口（人物实体化后）
            }
                if (win == null || _currentGen == null)
                {
                    return; // Building 未完成/窗口缺位：本帧无渲染载荷
                }
                double w = win.Width > 0 ? win.Width : RoadConstants.FallbackWindowW;
                double h = win.Height > 0 ? win.Height : 600;
                // 窗口尺寸同步（2026-08-29 修复①：AdvanceGenerator 的位移提取/右缘回收与 BandMargin
                // 全按 gen.WindowWidth/WindowHeight 计算——路窗交互缩放后不同步，右缘回收按旧宽判定
                // 会把仍在屏内的柱挖走 = 路面空档）。每帧写入（默认 800×600 为 no-op，缩放后即生效）
                if (_currentGen != null)
                {
                    _currentGen.Obj.WindowWidth = w;
                    _currentGen.Obj.WindowHeight = h;
                }
                if (_incomingGen != null)
                {
                    _incomingGen.Obj.WindowWidth = w;
                    _incomingGen.Obj.WindowHeight = h;
                }

                // PetFollower（归 Current 路所有；交接到 Incoming 在 Swap 后自然切换）
                // 人物实体化（2026-09）：纸娃娃人物已并入实体管线（BuildRenderRoad 追加 __paperdoll__ 实体）——
                // Follower 只推进贴地/跳跃/行走动画状态（供实体快照取 Action/FrameIndex/FeetY），
                // 不再 setPetTopLeft 挪宠物窗口（避免与路窗实体双画）；拖拽期间仍整段跳过（保留拖动行为）。
                // Mob/NPC 宠物（非纸娃娃）不进实体管线：保持旧 Follower 窗口落位行为不变。
                                var dragHold = _petWindow is PetWindow pw && pw.IsDragHold;
                if (_petWindow != null && _setPetTopLeftCached != null && !dragHold)
                {
                    _follower.Tick(dt, _currentGen.Road, win.Position.X, win.Position.Y, w, h,
                        _currentInitialSurfaceY, _petWindow.Width, (_petWindow as PetWindow)?.PetFeetOffset ?? _petWindow.Height,
                        _setPetTopLeftCached, applyPlacement: !_anim.IsProviderActive);
                }

                // CombatDirector + 攻击时间线观察
                var cols = _currentGen.Columns?.ActiveColumns ?? (IReadOnlyList<RoadColumn>)Array.Empty<RoadColumn>();
                var entities = _director.Tick(dt, cols, _currentGen.Road.ScrollX, w, State);
                _director.ObserveAttack(State);
                State.ColumnCount = cols.Count;

                // 组装 RoadFrame（不可变快照）→ 推渲染（合并式 kick）
                var currentRender = BuildRenderRoad(entities);
                if (currentRender == null)
                {
                    return;
                }
                RoadRenderRoad? incomingRender = null;
                if (_crossTiming && _incomingGen != null)
                {
                    incomingRender = BuildIncomingRenderRoad();
                }
                // Sky CamX delta 传播（FINAL 拍板 #3）：天空水平相机按 Current 路 scroll 帧间增量累加；
                // 换歌更换 scroll 原点后 delta 仍连续 → 天空视差相位无跳变；_roadSky 为 null（未激活）时跳过
                if (_roadSky != null)
                {
                    double curScroll = _currentGen.Road.ScrollX;
                    _roadSky = _roadSky with { CamX = _roadSky.CamX + (curScroll - _skyLastScrollX) };
                    _skyLastScrollX = curScroll;
                }
                var frame = new RoadFrame(State.Phase, _roadPaused, _roadSky, currentRender, incomingRender,
                    _crossGen, _crossTiming ? _crossfadeAlpha : 0, _roadClockMs, dt);
                // 渲染外推（修「像素点卡过去」；并修「滚动一段后突然从头开始」的渲染层回跳）：
                // ⚠️ 外推方向与生成器推进方向耦合（改方向时此处自动跟随，无需另行同步）——
                // 不写死正负号，按本帧 ScrollOriginX 相对上一发布帧的实际帧间位移取号。
                // 当前 AdvanceGenerator 实现（gen.Advance(右缘 + 速度×dt)，gen 内部无取反）下
                // ScrollOriginX 递增 → 取号 +1 → 外推 += shift；若生成器反转（ScrollOriginX 递减）
                // 则自动变为 -=。此前写死 `-=` 与推进反向：每个渲染帧先把整路拉回 → 视觉回跳/从头。
                // 基准：_lastLogicTickMs 已移至 BeginTick 每帧无条件刷新（见 BeginTick 注释），
                // 暂停/恢复瞬间不会 clamp 出 6px 跳变；暂停时位移为 0 → 取号 0 → 外推自然冻结。
                double extrapolateMs = Math.Min(100, Environment.TickCount64 - _lastLogicTickMs);
                double shift = RoadConstants.ScrollSpeed * extrapolateMs / 1000.0;
                double extrapDir = 0;
                if (_lastExtrapScrollX.HasValue)
                {
                    extrapDir = Math.Sign(frame.Current.ScrollOriginX - _lastExtrapScrollX.Value);
                }
                _lastExtrapScrollX = frame.Current.ScrollOriginX;
                var currentEx = frame.Current with { ScrollOriginX = frame.Current.ScrollOriginX + extrapDir * shift };
                var incomingEx = frame.Incoming == null
                    ? null
                    : frame.Incoming with { ScrollOriginX = frame.Incoming.ScrollOriginX + extrapDir * shift };
                var renderFrame = frame with { Current = currentEx, Incoming = incomingEx };
                RoadParallelGateway.WindowRender(renderFrame, w, h);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Road] EndTick: {ex.Message}");
            }
        }

        private Action<double, double>? _setPetTopLeftCached;

        // ═══════════════ 背景操作统一闸门 ═══════════════

        /// <summary>
        /// 背景操作统一闸门：SemaphoreSlim 串行化；路激活（含退出中）→ 先完整走「退出路」
        /// （淡出→恢复→宠物复位→抑制代际收尾）await 完毕后执行原语义。
        /// 被 PetWindow 五个背景变更命令（ToggleBackground/SwitchToStatic/SwitchToCamera/
        /// ApplyMapSelection/QuickHideBackground）调用——原 void 方法重构为 awaitable 内核后经此闸门。
        /// </summary>
        public async Task GateBackgroundAction(Func<Task> original)
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (IsRoadActive)
                {
                    // 先完整退出路；本次 gate 触发的地图命令尾部 OnMapChanged 抑制一轮（防 F17 自动重进）
                    _suppressMapCmdOnce = true;
                    await ExitRoadAsync().ConfigureAwait(true);
                }
                if (original != null)
                {
                    await original().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Road] GateBackgroundAction: {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 地图命令抑制代际消费：ApplyMapSelection 尾部的 OnMapChanged 调用前查询——
        /// 本次背景命令因路退出而让路（true=抑制本轮，清标志）则跳过场景联动；正常路径 false 照旧。
        /// </summary>
        public bool ConsumeMapCommandSuppression()
        {
            if (_suppressMapCmdOnce)
            {
                _suppressMapCmdOnce = false;
                return true;
            }
            return false;
        }

        /// <summary>设置宠物落位回调（PetWindow Attach 内部使用；屏幕坐标 = 宠物窗口左上）。</summary>
        public void SetPetPositionSink(Action<double, double> sink)
        {
            _setPetTopLeftCached = sink;
        }

        // ═══════════════ 命令处理（UI 线程串行） ═══════════════

        private void EnqueueCommand(RoadCommandKind kind, Func<Task> run)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // 合并：同 kind 未执行命令被本命令覆盖（最后语义为准）
                    if (_commands.Count > 0)
                    {
                        var keep = new List<(RoadCommandKind, Func<Task>)>();
                        while (_commands.Count > 0)
                        {
                            var old = _commands.Dequeue();
                            if (old.Item1 != kind)
                            {
                                keep.Add(old);
                            }
                        }
                        foreach (var k in keep)
                        {
                            _commands.Enqueue(k);
                        }
                    }
                    _commands.Enqueue((kind, run));
                    PumpCommands();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Road] Enqueue({kind}): {ex.Message}");
                }
            });
        }

        private async void PumpCommands()
        {
            if (_commandFlying)
            {
                return;
            }
            _commandFlying = true;
            try
            {
                while (_commands.Count > 0)
                {
                    var (_, run) = _commands.Dequeue();
                    try
                    {
                        await run().ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Road] 命令处理异常: {ex.Message}");
                    }
                }
            }
            finally
            {
                _commandFlying = false;
            }
        }

        /// <summary>播放状态命令：Playing→激活（已激活则解除暂停）；Paused→IsPaused=true（RoadClockScale=0）；Stopped→退出。</summary>
        private async Task HandleStateChangedAsync(PlaybackState state)
        {
            switch (state)
            {
                case PlaybackState.Playing:
                    if (State.Phase == RoadPhase.Inactive)
                    {
                        await ActivateAsync().ConfigureAwait(true);
                    }
                    else if (_roadPaused)
                    {
                        _roadPaused = false; // Resume 续走（RoadClockScale 恢复 1）
                    }
                    break;
                case PlaybackState.Paused:
                    if (IsRoadActive)
                    {
                        _roadPaused = true; // 暂停 = 一切静止（滚动/实体/战斗/淡入淡出/宠物动画全停）
                    }
                    break;
                case PlaybackState.Stopped:
                    if (IsRoadActive)
                    {
                        await ExitRoadAsync().ConfigureAwait(true);
                    }
                    break;
            }
        }

        /// <summary>换歌命令：路激活时新解析≠当前 → crossfade 换素材；未激活交给 State 命令（Playing 重放 Current）。</summary>
        private async Task HandleCurrentChangedAsync(MusicTrack? track)
        {
            // [Road] 换歌埋点（2026-08-29）：验证 CurrentChanged → 换歌分支是否触发（修后保留观测）
            Console.Error.WriteLine($"[Road] CurrentChanged: track={track?.Img}/{track?.Track} phase={State.Phase} active={IsRoadActive}");
            if (!IsRoadActive || State.Phase == RoadPhase.Exiting)
            {
                return;
            }
            if (track == null)
            {
                return;
            }
            // Silence 静音曲隐藏（甲方拍板 2026-09-01）：Silence = 默认静音占位（WZ 挂 1321 图），
            // 不参与无限之路——直接保持当前路（不解析不换带）
            if (string.Equals(track.Track, "Silence", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[Road] CurrentChanged: Silence 静音曲隐藏（不换路）");
                return;
            }
            // 同曲守卫（2026-08-30 用户实测「单曲循环滚动到一半直接从头开始」）：自然播完/循环续播
            // 也会触发 CurrentChanged——同曲重解析的候选序是洗牌的，可能选中不同图 → crossfade 换带
            // = 路从新带起点重来。曲键未变（Img/Track 口径同 MusicTrack.Key）→ 保持当前路不动
            string newKey = TrackKeyOf(track);
            if (!string.IsNullOrEmpty(_roadTrackKey) && string.Equals(newKey, _roadTrackKey, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[Road] CurrentChanged 同曲守卫生效 key={newKey}（不重解析）");
                return;
            }
            await SwitchSongAsync(track).ConfigureAwait(true);
        }

        /// <summary>曲目稳定键（Img/Track——与 MusicCatalogService 解析链 trackKey 同口径）。</summary>
        private static string TrackKeyOf(MusicTrack track)
        {
            return track.Img + "/" + track.Track;
        }

        /// <summary>WZ 重载命令：取消在途构建 → 清条带缓存 → 路退出（旧背景由 WZ 重载流程恢复渲染）→ 仍 Playing 则重建重进。</summary>
        private async Task HandleWzReloadedAsync(int wzGen)
        {
            _buildCts?.Cancel();
            _buildCts = null;
            RoadParallelGateway.EvictAllStrips();
            if (IsRoadActive)
            {
                await ExitRoadAsync().ConfigureAwait(true);
            }
            if (_music.State == PlaybackState.Playing && _music.Current != null)
            {
                await ActivateAsync().ConfigureAwait(true);
            }
        }

        // ═══════════════ 激活 / 退出 / 换歌 ═══════════════

        /// <summary>
        /// 激活序：Building（旧背景不动）→ 后台建包（在途 ct 取旧以新）→ 包就绪 → 记录旧背景快照 →
        /// 隐藏旧窗 → ShowRoad 淡入 500ms（Entering）→ Running（EndTick 相位计时切换）。
        /// 任一失败 → 立即恢复旧背景 + RoadState.LastError 记因（音乐继续）。
        /// </summary>
        private async Task ActivateAsync()
        {
            if (State.Phase != RoadPhase.Inactive)
            {
                return;
            }
            var track = _music.Current;
            if (track == null)
            {
                return;
            }
            State.Phase = RoadPhase.Building;
            State.LastError = "";
            State.FallbackReason = "";
            _roadPaused = false;
            _roadClockMs = 0;
            try
            {
                if (!_wz.IsLoaded)
                {
                    FailActivate("WZ 未加载");
                    return;
                }
                // 第一步：候选迭代「解析+提取」（城镇优先洗牌逐个提取，首个 Usable 包胜出，至多 6 试；
                // 在途构建 CancellationToken 取旧以新为准——解析与提取一并可被新流程取消）
                _buildCts?.Cancel();
                var cts = new CancellationTokenSource();
                _buildCts = cts;
                var resolved = await _resolver.ResolveMultiAsync(track, (mid, ct2) => RoadParallelGateway.ExtractPackDirectAsync(mid, ct2), State, cts.Token).ConfigureAwait(true);
                if (cts.IsCancellationRequested)
                {
                    // 被更新的构建/WZ 重载覆盖：本次作废（新流程接管）
                    if (State.Phase == RoadPhase.Building)
                    {
                        State.Phase = RoadPhase.Inactive;
                    }
                    return;
                }
                string? mapId = resolved?.MapId;
                RoadMaterialPack? pack = resolved?.Pack;
                Console.Error.WriteLine($"[Road] Activate track={track?.Img}/{track?.Track} 选定 map={resolved?.MapId ?? "null"} 块={(pack?.FlatColumns.Count ?? -1)} 柱={(pack?.MainColumnTotal ?? -1)} tier={(pack != null ? (pack.MainColumnTotal >= RoadConstants.NormalPoolColumns ? "NORMAL" : pack.MainColumnTotal > 0 ? "DEGRADED" : "EMPTY") : "NULL")} 池成员={pack?.PoolMapIds.Count ?? 0}");
                if (resolved == null)
                {
                    // 回退链：候选迭代未命中 → ResolveAsync 旧回退（沿用上一首/五城镇兜底）+ 单次提取
                    mapId = await _resolver.ResolveAsync(track, CancellationToken.None).ConfigureAwait(true);
                    if (cts.IsCancellationRequested)
                    {
                        if (State.Phase == RoadPhase.Building)
                        {
                            State.Phase = RoadPhase.Inactive;
                        }
                        return;
                    }
                    if (string.IsNullOrEmpty(mapId))
                    {
                        FailActivate("本地曲无源图（回退链走到头）");
                        return;
                    }
                    State.FallbackReason = "候选迭代未命中，回退链兜底";
                Console.Error.WriteLine($"[Road] Activate track={track?.Img}/{track?.Track} 回退链 map={mapId}({State.FallbackReason})");
                    pack = await RoadParallelGateway.ExtractPackDirectAsync(mapId, cts.Token).ConfigureAwait(true);
                    if (cts.IsCancellationRequested)
                    {
                        if (State.Phase == RoadPhase.Building)
                        {
                            State.Phase = RoadPhase.Inactive;
                        }
                        return;
                    }
                    if (pack == null)
                    {
                        FailActivate("素材提取不可用（D1 RoadMaterialExtractor 未落盘）");
                        return;
                    }
                    // Usable 唯一口径（FINAL §0）：主干柱总数 ≥20 = 正常档；<20 = 降级档素材照进
                    //（仲裁裁决链——"重复就重复"拍板），柱=0 才激活失败；DegradedPool 记观测（W17）
                    State.DegradedPool = pack.MainColumnTotal < RoadConstants.NormalPoolColumns;
                    if (State.DegradedPool)
                    {
                        State.FallbackReason = $"合并池 {pack.MainColumnTotal} 柱 <{RoadConstants.NormalPoolColumns} → 降级档（防重关）";
                    }
                    if (pack.MainColumnTotal <= 0)
                    {
                        FailActivate("柱池为空（无可用主干素材）");
                        return;
                    }
                }
                State.SelectedMapId = mapId;

                // 第二步：生成器 + 路窗口（D2/D3 契约绑定；缺位即失败恢复）
                var gen = RoadParallelGateway.CreateGenerator(pack, RoadConstants.FallbackWindowW / 2.0, pack.MainSurfaceH);
                if (gen == null)
                {
                    FailActivate("路生成器不可用（D2 InfiniteRoadGenerator 未落盘）");
                    return;
                }
                var win = RoadParallelGateway.EnsureRoadWindow();
            if (win != null && win.PetMenu == null)
            {
                win.PetMenu = _petWindow?.ContextMenu; // 右键入口（人物实体化后）
            }
                if (win == null)
                {
                    FailActivate("路窗口创建失败（见 stderr [RoadGateway] 日志）");
                    return;
                }
                HookRoadWindowZOrder(win);

                // 第三步：记录旧背景 → 隐藏旧窗 → 建路 → ShowRoad 淡入
                if (_snapshotBackground != null)
                {
                    _prevSnapshot = _snapshotBackground();
                }
                _hideBackground?.Invoke();
                int buildGen = ++_packSeq;
                State.BuildGeneration = buildGen;
                InstallCurrentRoad(pack, gen, buildGen);
                _director.Reset(State);
                _director.BindLifePool(pack.LifeCandidates, pack.MapId);
                _follower.Reset();
                BindEntityResolver(buildGen, pack);
                RoadParallelGateway.WindowShowRoad();
                RaisePetAboveRoad("路激活"); // 路窗 Show() 会盖过同级 Topmost 宠物窗（后 Show 者居上）
                // 面向与滚动方向一致（用户实测）：路向右走 → 朝右（Flip=false）；退出时还原用户设置
                _prevFlip = _paperdoll.Flip;
                _paperdoll.Flip = true; // 427f 诊断：WZ 精灵原图朝左 → Flip=true（镜像）= 朝右 = 行进方向（右移语义）
                _roadTrackKey = TrackKeyOf(track); // 激活曲键（同曲守卫基准）
                State.Phase = RoadPhase.Entering;
                _enterStartMs = _roadClockMs;
                _enterTiming = true;
                RoadNotice?.Invoke("🛣 进入无限之路");
            }
            catch (Exception ex)
            {
                FailActivate($"激活异常: {ex.Message}");
            }
        }

        /// <summary>退出序：淡出 500ms（RoadClock 驱动，EndTick 检测完成）→ 恢复旧背景模式与窗口可见态 + 宠物复位 → Inactive。awaitable（闸门/命令复用）。</summary>
        private async Task ExitRoadAsync()
        {
            if (State.Phase == RoadPhase.Inactive || _exitTcs != null)
            {
                return;
            }
            _roadPaused = false; // 退出淡出必须流动
            State.Phase = RoadPhase.Exiting;
            _exitStartMs = _roadClockMs;
            _exitTiming = true;
            // 取消在途构建/未完成 Incoming（退出压倒一切）
            _buildCts?.Cancel();
            _incomingGenTag++;
            _crossTiming = false;
            _incomingPack = null;
            _incomingGen = null;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _exitTcs = tcs;
            // 兜底超时（EndTick 不跑的异常路径也要解锁闸门，2s = 4×CrossfadeMs）
            _ = Task.Delay(RoadConstants.CrossfadeMs * 4).ContinueWith(_ => tcs.TrySetResult(false), TaskScheduler.Default);
            await tcs.Task.ConfigureAwait(true);
        }

        /// <summary>退出收口（EndTick 淡出计时到点调用）：恢复旧背景 + 宠物复位 + 清路态 → Inactive。</summary>
        private void FinishExit()
        {
            try
            {
                RoadParallelGateway.WindowHideRoad();
                RestorePreviousBackground();
            }
            finally
            {
                CleanupRoadState();
                State.Phase = RoadPhase.Inactive;
                _roadPaused = false;
                _exitTiming = false;
                var tcs = _exitTcs;
                _exitTcs = null;
                if (tcs != null)
                {
                    tcs.TrySetResult(true);
                }
            }
        }

        /// <summary>换歌 crossfade：新解析≠当前 → 后台建新包 → Incoming 生成器（重定基）→ 500ms alpha → 交换（EndTick 检测）。更新的换歌取消上一条未完成 Incoming（代际）。</summary>
        private async Task SwitchSongAsync(MusicTrack track)
        {
            // 守卫洞修复（f5e 报告）：入口即记曲键——此前只在 :778 实际换带路径更新，
            // 同源早退/解析失败路径不更新 → 同曲二次 CurrentChanged 绕过守卫 → 洗牌解析漂移换带（"从头再来"根因）
            _roadTrackKey = TrackKeyOf(track);
            int myTag = ++_incomingGenTag; // 取消上一条未完成 Incoming（换歌开始即生效）
            _buildCts?.Cancel();
            var cts = new CancellationTokenSource();
            _buildCts = cts;
            // 第一步：候选迭代「解析+提取」（城镇优先洗牌逐个提取，首个 Usable 包胜出，至多 6 试）
            var resolved = await _resolver.ResolveMultiAsync(track, (mid, ct2) => RoadParallelGateway.ExtractPackDirectAsync(mid, ct2), State, cts.Token).ConfigureAwait(true);
            // [Road] 换歌埋点（2026-08-29）：解析结果与同源图短路判定（修后保留观测）
            Console.Error.WriteLine($"[Road] SwitchSong: track={track.Img}/{track.Track} resolved={resolved?.MapId ?? "null"} selected={State.SelectedMapId} phase={State.Phase}");
            if (cts.IsCancellationRequested || myTag != _incomingGenTag)
            {
                return; // 被更新的换歌/WZ 重载/退出覆盖
            }
            string? mapId = resolved?.MapId;
            RoadMaterialPack? pack = resolved?.Pack;
            if (resolved == null)
            {
                // 第二步：回退链——ResolveAsync 旧回退（沿用上一首/五城镇兜底）+ 单次提取，仍失败才进失败契约
                mapId = await _resolver.ResolveAsync(track, CancellationToken.None).ConfigureAwait(true);
                if (string.IsNullOrEmpty(mapId) || string.Equals(mapId, State.SelectedMapId, StringComparison.Ordinal))
                {
                    return; // 回退链同源图（或解析失败沿用现状）→ 不换
                }
                if (cts.IsCancellationRequested || myTag != _incomingGenTag)
                {
                    return;
                }
                pack = await RoadParallelGateway.ExtractPackDirectAsync(mapId, cts.Token).ConfigureAwait(true);
                if (cts.IsCancellationRequested || myTag != _incomingGenTag)
                {
                    return;
                }
                // 换歌 Usable 口径与首激活一致（FINAL §0）：柱=0 才拒；<20 降级档照换（素材照进）
                if (pack == null || pack.MainColumnTotal <= 0)
                {
                    State.LastError = $"换歌建包失败: {mapId}";
                    RoadNotice?.Invoke("🛣 换歌源图不可用，保持当前路");
                    return;
                }
            }
            else if (string.IsNullOrEmpty(mapId) || string.Equals(mapId, State.SelectedMapId, StringComparison.Ordinal))
            {
                return; // 同源图 → 不换（新 API 已提取的包直接丢弃）
            }
            if (_currentGen == null)
            {
                return;
            }
            // Incoming 生成器重定基（换歌视口连续性，缺陷修复 2026-08-28）：锚 = 旧路当前
            // ScrollOrigin（左缘世界 x）+ 生成器自身半窗宽。必须用 FallbackWindowW（CreateGenerator
            // 给 gen 固定此窗宽，EndTick 不同步实际窗宽）而非窗口实际宽 w：生成器 ctor 内
            // ScrollX = 锚 − FallbackWindowW/2，代入后新 gen 的 ScrollX 初始值 == 旧路当前
            // ScrollOrigin（按构造连续，与 w 无关）。此前传 w/2，实际窗宽 ≠ FallbackWindowW 时
            // 新 gen ScrollX 起步偏移 (w−FallbackWindowW)/2 → crossfade 起视口横向跳变。
            // （激活路径 ActivateAsync 传 FallbackWindowW/2 → ScrollX 从 0 起步为设计行为：
            // 全新激活无旧路可连续，非缺陷。）
            var win = RoadParallelGateway.EnsureRoadWindow();
            if (win != null && win.PetMenu == null)
            {
                win.PetMenu = _petWindow?.ContextMenu; // 右键入口（人物实体化后）
            }
            double w = win != null && win.Width > 0 ? win.Width : RoadConstants.FallbackWindowW;
            double anchorX = _currentGen.Road.ScrollX + RoadConstants.FallbackWindowW / 2;
            double? oldSurface = _currentGen.Road.SurfaceY(anchorX);
            int initialSurfaceY = oldSurface.HasValue ? (int)oldSurface.Value : pack.MainSurfaceH;
            var gen = RoadParallelGateway.CreateGenerator(pack, anchorX, initialSurfaceY);
            if (gen == null)
            {
                State.LastError = "换歌生成器创建失败";
                return;
            }
            _incomingPack = pack;
            _incomingGen = gen;
            _crossGen++;
            _crossTiming = true;
            _crossStartMs = _roadClockMs;
            _crossfadeAlpha = 0;
            State.SelectedMapId = mapId;
            _roadTrackKey = TrackKeyOf(track); // 换歌曲键即归新曲（crossfade 期间重复 CurrentChanged 被同曲守卫拦截）
            // [Road] 换歌埋点（2026-08-29）：Incoming 就绪（修后保留观测）
            Console.Error.WriteLine($"[Road] SwitchSong: incoming ready map={mapId} anchor={anchorX:F0} surf={initialSurfaceY} crossfade 开始");
        }

        /// <summary>
        /// crossfade 交换：Current=Incoming；PetFollower 归新路（表面已对齐连续）。
        /// 实体不断代（2026-08-30 用户实测「换歌/循环时 NPC/怪瞬间消失」）：旧实体保留——其 WorldX 在
        /// 重定基坐标系下与新路连续（Incoming 锚=旧路 scroll），按原 LifeId 继续走完死亡链/走出右缘
        /// 后自然回收，新柱生成改用新图 life 池补员（「等移除屏幕了再销毁再补充」）。
        /// 为支撑旧实体帧解析：RoadEntityState 携带 MapId（resolver 按 ent.MapId 取帧），
        /// 旧包条带**不整代淘汰**（交由 StripCacheMax LRU 自然老化——整代淘汰会抽掉旧实体脚下的帧）。
        /// </summary>
        private void SwapIncoming()
        {
            if (_incomingGen == null || _incomingPack == null)
            {
                _crossTiming = false;
                return;
            }
            InstallCurrentRoad(_incomingPack, _incomingGen, ++_packSeq);
            _incomingPack = null;
            _incomingGen = null;
            _crossTiming = false;
            _crossfadeAlpha = 0;
            _director.BindLifePool(_currentPack != null ? _currentPack.LifeCandidates : null, _currentPack?.MapId ?? "");
            BindEntityResolver(_currentBuildGen, _currentPack!);
            // [Road] 换歌埋点（2026-08-29）：crossfade 交换完成（修后保留观测）
            Console.Error.WriteLine($"[Road] SwapIncoming: current={_currentPack?.MapId} buildGen={_currentBuildGen}");
        }

        /// <summary>装配 Current 路（含 back 表转换缓存与列快照失效）。</summary>
        private void InstallCurrentRoad(RoadMaterialPack pack, RoadGeneratorHandle gen, int buildGen)
        {
            _currentPack = pack;
            _currentGen = gen;
            _lastExtrapScrollX = null; // 换 gen 后帧间位移基准作废（新 gen ScrollX 世界位与旧路无关，防取错外推方向号）
            _currentBuildGen = buildGen;
            _currentInitialSurfaceY = gen.InitialSurfaceY;
            State.BuildGeneration = buildGen;
            State.SelectedMapId = pack.MapId;
            // back 全字段克隆（MapBack 简化模型 → RoadBack 契约 record；BS/No/Fh 等简化模型缺失字段以近似值填充，D1 提供更全数据时以其为准）
            var nonFront = new List<RoadBack>();
            var front = new List<RoadBack>();
            if (pack.Backs != null)
            {
                foreach (var b in pack.Backs)
                {
                    var rb = RoadMaterialExtractor.ToRoadBack(b); // 提取器统一口径（BS/No/TileMode/可见性 ==0||==1）——行内近似转换废弃（天空全杀根因）
                    if (b.Front)
                    {
                        front.Add(rb);
                    }
                    else
                    {
                        nonFront.Add(rb);
                    }
                }
            }
            _currentBacksNonFront = nonFront;
            _currentBacksFront = front;
            // 天空层（2026-09-01 甲方反馈修订：天空必须对应当前地图——「天空不是地图对应的」= 冻结首次激活被否定）：
            // 每次装路重建 = 当前 pack 的 back + 锚点 + 本路垂直锚；CamX 从当前路 ScrollX 起步（换歌零横跳，
            // 与垂直锚 InitialSurfaceY=旧路表面（重定基）共同保证换歌天空零跳变——只换内容不换相机基准
            _skyLastScrollX = gen.Road.ScrollX;
            _roadSky = new RoadSky(nonFront, front, pack.AnchorX, pack.AnchorY, gen.InitialSurfaceY, 0);
            _currentColSnapshot = null; // 迫使列快照重建
            _currentColStamp = default;
            // 跨 fade 预构建（f5e 诊断：Incoming 柱位图首建慢 → crossfade 空白/丢帧——换歌后立即预热首屏列）
            try
            {
                var win = RoadParallelGateway.EnsureRoadWindow();
            if (win != null && win.PetMenu == null)
            {
                win.PetMenu = _petWindow?.ContextMenu; // 右键入口（人物实体化后）
            }
                var renderRoad = BuildRenderRoad(Array.Empty<RoadEntityState>());
                if (win != null && renderRoad != null)
                {
                    win.Renderer.PrewarmColumns(renderRoad, renderRoad.Columns);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Road] Prewarm: {ex.Message}"); }
        }

        /// <summary>
        /// 实体帧 resolver 接线（D1 条带缓存直连：GetEntityStrip(mapId,lifeId,action,isMob,state)→RoadStripEntry(Png=整条拼合条带字节, Strip=元数据))。
        /// mapId 取实体自带的 ent.MapId（2026-08-30 换歌不断代：旧图实体在新路里继续按旧图取帧，直至走出屏幕）。
        /// 人物实体化（2026-09）：LifeId="__paperdoll__" 走 PaperdollService 合成帧（纸娃娃帧源），
        /// NPC/mob 路径不变；并接线到路窗渲染器 EntityBitmapResolver（D4 接线缺位修复——此前实体不渲染）。
        /// </summary>
        private void BindEntityResolver(int buildGen, RoadMaterialPack pack)
        {
            EntityFrameResolver = ent =>
            {
                try
                {
                    if (string.Equals(ent.LifeId, RoadConstants.PaperdollLifeId, StringComparison.Ordinal))
                    {
                        return GetPaperdollFrame(ent.Action, ent.FrameIndex);
                    }
                    return RoadEntityFrameCache.GetFrame(ent.MapId, ent.LifeId, ent.Action, ent.FrameIndex, ent.IsMob, State);
                }
                catch
                {
                    return null;
                }
            };
            // D4 接线：控制器 resolver → 路窗渲染器（DrawEntities 经 EntityBitmapResolver 取帧）。
            // 此前只设 EntityFrameResolver 未接渲染器 → DrawEntities 恒 null 早退 = 实体（含人物）不渲染。
            var win = RoadParallelGateway.EnsureRoadWindow();
            if (win != null && win.PetMenu == null)
            {
                win.PetMenu = _petWindow?.ContextMenu; // 右键入口（人物实体化后）
            }
            if (win != null)
            {
                win.Renderer.EntityBitmapResolver = EntityFrameResolver;
            }
            _director.BindStripProbe((lifeId, action) =>
            {
                try
                {
                    // isMob 未知时两态皆试（LRU 命中零成本；仅 miss 时第二次构建探测）；mapId = 当前池图
                    string probeMapId = pack.MapId;
                    return RoadMaterialExtractor.GetEntityStrip(probeMapId, lifeId, action, true, State) != null
                        || RoadMaterialExtractor.GetEntityStrip(probeMapId, lifeId, action, false, State) != null;
                }
                catch
                {
                    return false;
                }
            });
        }

        // ── 纸娃娃实体帧缓存（渲染线程经 resolver 调用；键=外观hash|action|frame——换装自动失效；LRU 上限小） ──
        private const int PaperdollFrameCacheMax = 24;
        private readonly object _paperdollFrameLock = new();
        private readonly Dictionary<string, SKBitmap> _paperdollFrames = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _paperdollOrder = new();

        /// <summary>取/建纸娃娃实体帧（PaperdollService.RenderFrame 实时合成；LRU 淘汰 Dispose 释放句柄）。</summary>
        /// <summary>纸娃娃帧 → 统一画布（200×174：origin 行贴底、水平居中——所有动作帧脚底同位）。</summary>
        private static SKBitmap? ComposePaperdollFrame(SKBitmap src, int ox, int oy, int fw, int fh)
        {
            const int CW = 200, CH = 174;
            var dst = new SkiaSharp.SKBitmap(CW, CH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            using (var c = new SkiaSharp.SKCanvas(dst))
            {
                float x = (CW - fw) / 2f;
                float yTop = CH - oy; // origin 行（脚底）贴画布底
                c.DrawBitmap(src, x, yTop);
            }
            return dst;
        }

        private SKBitmap? GetPaperdollFrame(string action, int frameIndex)
        {
            string key = $"{_paperdoll.CurrentHash}|{action}|{frameIndex}";
            lock (_paperdollFrameLock)
            {
                if (_paperdollFrames.TryGetValue(key, out var hit))
                {
                    _paperdollOrder.Remove(key);
                    _paperdollOrder.AddLast(key);
                    return hit;
                }
            }
            var (bmp, ox, oy, fw, fh) = _paperdoll.RenderFrame(action, frameIndex);
            if (bmp == null)
            {
                return null;
            }
            // 统一画布（动作位置稳定）：所有帧合成到定长画布，origin 行（脚底）贴画布底、水平居中——
            // 渲染器"底对齐+居中"对每帧一致 → 站立/行走/跳跃切换不漂移（用户"每次动作位置不同"修复）
            bmp = ComposePaperdollFrame(bmp, ox, oy, fw, fh);
            if (bmp == null) { return null; }
            lock (_paperdollFrameLock)
            {
                _paperdollFrames[key] = bmp;
                _paperdollOrder.AddLast(key);
                while (_paperdollOrder.Count > PaperdollFrameCacheMax)
                {
                    var oldest = _paperdollOrder.First;
                    if (oldest == null)
                    {
                        break;
                    }
                    _paperdollOrder.RemoveFirst();
                    if (_paperdollFrames.Remove(oldest.Value, out var dead))
                    {
                        // 崩溃根因（2026-09-01 闪退 0x18/SIGSEGV/sk_bitmap_get_info）：
                        // 渲染线程（Task.Run）可能仍在使用返回的位图 → 淘汰时 Dispose 即"释放后用"崩溃
                        // → 淘汰仅移出缓存，位图交 GC（帧缓存量小，无内存压力）
                        _ = dead;
                    }
                }
            }
            return bmp;
        }

        /// <summary>清纸娃娃实体帧缓存（路退出/WZ 重载时释放原生句柄）。</summary>
        private void ClearPaperdollFrames()
        {
            lock (_paperdollFrameLock)
            {
                foreach (var b in _paperdollFrames.Values)
                {
                    b.Dispose();
                }
                _paperdollFrames.Clear();
                _paperdollOrder.Clear();
            }
        }

        /// <summary>组装 Current 路渲染载荷（不可变；柱深拷贝缓存——列数/末柱变化才重建）。
        /// 人物实体化（2026-09）：快照末尾并入纸娃娃实体（LifeId="__paperdoll__" 哨兵）——
        /// 渲染器 DrawEntities 经 EntityFrameResolver 的 paperdoll 分支取 PaperdollService 合成帧，
        /// 与 NPC/mob 同一基准（高度/贴地/层序），不再走 PetFollower 独立窗口落位。</summary>
        private RoadRenderRoad? BuildRenderRoad(IReadOnlyList<RoadEntityState> entities)
        {
            if (_currentPack == null || _currentGen == null)
            {
                return null;
            }
            var cols = _currentGen.Columns?.ActiveColumns ?? (IReadOnlyList<RoadColumn>)Array.Empty<RoadColumn>();
            double lastX = cols.Count > 0 ? cols[cols.Count - 1].WorldX : 0;
            if (_currentColSnapshot == null || _currentColStamp.Count != cols.Count || Math.Abs(_currentColStamp.LastX - lastX) > 0.001)
            {
                var list = new List<RoadColumn>(cols.Count);
                foreach (var c in cols)
                {
                    list.Add(new RoadColumn
                    {
                        Template = c.Template,
                        WorldX = c.WorldX,
                        SurfaceY = c.SurfaceY,
                        IsStepLerp = c.IsStepLerp,
                        StepFromY = c.StepFromY,
                        StepToY = c.StepToY,
                    });
                }
                _currentColSnapshot = list;
                _currentColStamp = (cols.Count, lastX);
            }
            // 人物（纸娃娃）并入实体列表：仅纸娃娃 provider 在播且有动画信息时追加
            var entityList = entities;
            var paperdoll = BuildPaperdollEntityState();
            if (paperdoll != null)
            {
                var withPet = new List<RoadEntityState>(entityList.Count + 1);
                withPet.AddRange(entityList);
                withPet.Add(paperdoll);
                entityList = withPet;
            }
            return new RoadRenderRoad(
                _currentBuildGen,
                _currentPack.MapId,
                _currentPack.TileSetName,
                _currentColSnapshot,
                entityList,
                _currentPack.FarObjs,
                (IReadOnlyList<RoadDecor>)Array.Empty<RoadDecor>(), // 装饰带：生成器当前无 Decor 产出（块级激活换算待接入），空 = 无装饰
                _currentGen.Road.ScrollX,
                _currentInitialSurfaceY,
                _currentPack.AnchorX, // 源图坐标锚（back/远obj 归一基准；此前误传 scrollX 致 back 画出屏=黑天）
                _currentPack.AnchorY);
        }

        /// <summary>人物纸娃娃实体态（LifeId="__paperdoll__" 哨兵；渲染器经 resolver paperdoll 分支取帧）。
        /// WorldX = 锁窗心（ScrollX + 窗宽/2，与 PetFollower 同源）；FeetY = Follower 当前脚底
        /// （含跳跃弧线；GroundCalibPx 语义保留——旧窗口管线把校准加在落位偏移上，实体化后校准
        /// 并入 FeetY：渲染器 sy = 0.62h + (FeetY − InitialSurfaceY)，见 RoadConstants.GroundCalibPx）。
        /// Action/FrameIndex = AnimService 当前动画（walk1/stand1/…）；FacingLeft=false = 朝右
        /// （Flip 镜像后——WZ 原图朝左，Flip=true 镜像成朝右行进方向）。</summary>
        private RoadEntityState? BuildPaperdollEntityState()
        {
            if (_currentPack == null || _currentGen == null || !_anim.IsProviderActive)
            {
                return null;
            }
            // 拖拽保持期（IsDragHold）：人物随鼠标走由 PetWindow 画（保留拖动行为）——路侧跳过实体防双画
            if (_petWindow is PetWindow petWin && petWin.IsDragHold)
            {
                return null;
            }
            var info = _anim.CurrentAnimInfo;
            if (info == null)
            {
                return null;
            }
            var win = RoadParallelGateway.EnsureRoadWindow();
            if (win != null && win.PetMenu == null)
            {
                win.PetMenu = _petWindow?.ContextMenu; // 右键入口（人物实体化后）
            }
            double w = win != null && win.Width > 0 ? win.Width : RoadConstants.FallbackWindowW;
            double petWorldX = _currentGen.Road.ScrollX + w / 2;
            double? surfaceY = _currentGen.Road.SurfaceY(petWorldX);
            double feetY = _follower.HasFeet ? _follower.CurrentFeetY : (surfaceY ?? _currentInitialSurfaceY);
            return new RoadEntityState(
                IsMob: false,
                LifeId: RoadConstants.PaperdollLifeId,
                MapId: _currentPack.MapId,
                WorldX: petWorldX,
                FeetY: (int)Math.Round(feetY) + RoadConstants.GroundCalibPx,
                Action: info.Value.Action,
                FrameIndex: info.Value.FrameIndex,
                Alpha: 1.0,
                FacingLeft: false);
        }

        /// <summary>组装 Incoming 路渲染载荷（crossfade 进场；实体列表为空——怪仍走 Current）。</summary>
        private RoadRenderRoad? BuildIncomingRenderRoad()
        {
            if (_incomingPack == null || _incomingGen == null)
            {
                return null;
            }
            var cols = _incomingGen.Columns?.ActiveColumns ?? (IReadOnlyList<RoadColumn>)Array.Empty<RoadColumn>();
            var list = new List<RoadColumn>(cols.Count);
            foreach (var c in cols)
            {
                list.Add(new RoadColumn
                {
                    Template = c.Template,
                    WorldX = c.WorldX,
                    SurfaceY = c.SurfaceY,
                    IsStepLerp = c.IsStepLerp,
                    StepFromY = c.StepFromY,
                    StepToY = c.StepToY,
                });
            }
            // back（nonFront/front）已归 RoadSky 固定天空层（首次激活冻结、换歌不变）——Incoming 路不再携带
            return new RoadRenderRoad(
                _currentBuildGen + 1,
                _incomingPack.MapId,
                _incomingPack.TileSetName,
                list,
                Array.Empty<RoadEntityState>(),
                _incomingPack.FarObjs,
                (IReadOnlyList<RoadDecor>)Array.Empty<RoadDecor>(), // 装饰带：同 Current（生成器暂无产出）
                _incomingGen.Road.ScrollX,
                _incomingGen.InitialSurfaceY,
                _incomingPack.AnchorX, // 源图坐标锚（back/远obj 归一基准）——与 BuildRenderRoad 同语义；此前误传 scrollX，换歌 crossfade 起背景错位（视口跳感）
                _incomingPack.AnchorY); // 2026-08-29 统一：此前误传 MainSurfaceH，AnchorY≠MainSurfaceH 的图 crossfade 期间背景垂直跳变
        }

        // ====== private ======

        /// <summary>激活失败：恢复旧背景（若已隐藏）+ Inactive + LastError 记因 + 气泡提示（音乐继续）。</summary>
        private void FailActivate(string reason)
        {
            if (_prevSnapshot != null)
            {
                RestorePreviousBackground();
            }
            CleanupRoadState();
            State.Phase = RoadPhase.Inactive;
            State.LastError = reason;
            RoadNotice?.Invoke($"🛣 无限之路不可用: {reason}");
        }

        /// <summary>
        /// 路窗 z 序钩子（一次性订阅）：路窗与宠物窗同为 Topmost——后 Show/makeKeyAndOrderFront 者居上，
        /// 路窗任何提升都会盖住人物。交互态退出（InteractiveChanged）时重申宠物窗在最前
        /// （交互态进入时路窗在前属有意设计——用户正在与路窗交互）。
        /// </summary>
        private void HookRoadWindowZOrder(Window win)
        {
            if (_roadWinZHooked || win is not InfiniteRoadWindow irw)
            {
                return;
            }
            _roadWinZHooked = true;
            irw.InteractiveChanged += interactive =>
            {
                if (!interactive)
                {
                    // 退出交互态：路窗 makeKeyAndOrderFront 曾把自己提到宠物窗之上——重申人物在最前
                    RaisePetAboveRoad("交互退出");
                }
            };
        }

        /// <summary>把宠物窗重申到路窗之上（照 RaisePet 既有机制：Topmost 同级 Activate 提前）。</summary>
        private void RaisePetAboveRoad(string reason)
        {
            try
            {
                _petWindow?.Activate();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Road] 宠物窗置顶失败（{reason}）: {ex.Message}");
            }
        }

        /// <summary>恢复进入前背景：两窗口可见态 + 旧模式持久化（1/2——MapMode=3 永不落盘）+ 宠物复位。</summary>
        private void RestorePreviousBackground()
        {
            try
            {
                if (_prevSnapshot != null)
                {
                    _restoreBackground?.Invoke(_prevSnapshot);
                    try
                    {
                        _config.Update(s => { s.MapMode = _prevSnapshot.MapMode; });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Road] MapMode 恢复持久化失败: {ex.Message}");
                    }
                    if (_petWindow != null)
                    {
                        _petWindow.Position = _prevSnapshot.PetPos; // 宠物复位（进入前屏幕位置）
                    }
                    _paperdoll.Flip = _prevFlip; // 面向还原用户进路前设置（激活时记录）
                    _roadTrackKey = "";          // 曲键清空（下次激活重新锚定——同曲守卫不跨路生效）
                    _prevSnapshot = null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Road] RestoreBackground: {ex.Message}");
            }
        }

        /// <summary>清路核心态（生成器/包/Incoming/时钟相关——不动 State 观测字段）。</summary>
        private void CleanupRoadState()
        {
            _currentPack = null;
            _currentGen = null;
            _currentColSnapshot = null;
            _currentBacksNonFront = null;
            _currentBacksFront = null;
            _roadSky = null; // 固定天空层随路退出清空（下次激活以新图首激活重建——拍板 #3 生命周期）
            _incomingPack = null;
            _incomingGen = null;
            _crossTiming = false;
            _crossfadeAlpha = 0;
            _enterTiming = false;
            _buildCts = null;
            _director.Reset(State);
            _follower.Reset();
            ClearPaperdollFrames(); // 纸娃娃实体帧缓存随路退出释放（原生句柄）
        }
    }

    /// <summary>
    /// RoadParallelGateway —— D1/D2/D3 并行反射网关（并行契约）：
    /// 按 PLAN 点名类型 + 方法签名在 MiniPet 程序集内反射绑定；落盘即自动接通，未落盘返回 null/false
    /// （控制器按失败降级：路激活失败 + LastError 注记）。绑定结果全部缓存（MethodInfo/实例），
    /// 每 tick 仅一次 Invoke 开销（30fps 可忽略）。D3 窗口按契约签名：
    /// LatestFrame / Render(RoadFrame,w,h) / ShowRoad / HideRoad / SetInteractive。
    /// </summary>
    internal static class RoadParallelGateway
    {
        // ── 强类型直连（2026-08-28 重写：删除全部反射绑定——组件已全部落盘，反射探测是纯负债） ──
        private static InfiniteRoadWindow? _win;
        private static bool _winResolved;

        /// <summary>解析/创建路窗口（D3 单例从 DI 容器取；ctor 依赖 WzService 由容器注入）。</summary>
        public static InfiniteRoadWindow? EnsureRoadWindow()
        {
            if (_winResolved)
            {
                return _win;
            }
            _winResolved = true;
            try
            {
                var sp = App.SP;
                _win = sp != null
                    ? sp.GetRequiredService<InfiniteRoadWindow>()
                    : new InfiniteRoadWindow();
                return _win;
            }
            catch (Exception ex)
            {
                // DI 解析失败 → 直接构造（ctor 的 WzService 为可选参）
                try
                {
                    _win = new InfiniteRoadWindow();
                    Console.Error.WriteLine($"[RoadGateway] DI 取路窗失败改直构: {ex.Message}");
                    return _win;
                }
                catch (Exception ex2)
                {
                    Console.Error.WriteLine($"[RoadGateway] 路窗直构也失败: {ex2.Message}");
                    _win = null;
                    return null;
                }
            }
        }

        /// <summary>推帧渲染（D3 窗口内部 volatile 快照 + 合并 kick）。</summary>
        public static void WindowRender(RoadFrame frame, double w, double h)
        {
            try
            {
                (_win as InfiniteRoadWindow)?.Render(frame, w, h);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadGateway] Render: {ex.Message}");
            }
        }

        public static void WindowShowRoad()
        {
            try
            {
                (_win as InfiniteRoadWindow)?.ShowRoad();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadGateway] ShowRoad: {ex.Message}");
            }
        }

        public static void WindowHideRoad()
        {
            try
            {
                (_win as InfiniteRoadWindow)?.HideRoad();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadGateway] HideRoad: {ex.Message}");
            }
        }

        /// <summary>
        /// 滚动推进（正确语义：新右缘 = 当前右缘 + 速度×dt；此前反射把 dtMs 当右缘传入，从未真正滚动）。
        /// 窗宽由调用方（EndTick）每帧写入 gen.WindowWidth。
        /// </summary>
        public static bool AdvanceGenerator(InfiniteRoadGenerator gen, double dtMs)
        {
            try
            {
                double rightEdge = gen.ScrollX + gen.WindowWidth;
                gen.Advance(rightEdge + RoadConstants.ScrollSpeed * dtMs / 1000.0);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadGateway] Advance: {ex.Message}");
                return false;
            }
        }

        /// <summary>D1 素材提取强类型直调（首次自动 Configure；此前反射 3 参调 3 参签名不匹配恒 null）。</summary>
        public static async Task<RoadMaterialPack?> ExtractPackDirectAsync(string mapId, CancellationToken ct)
        {
            EnsureExtractorConfigured();
            return await Task.Run(() => RoadMaterialExtractor.Extract(mapId, ct, _configuredState), ct).ConfigureAwait(true);
        }

        private static RoadState _configuredState = new();
        private static bool _extractorConfigured;

        private static void EnsureExtractorConfigured()
        {
            if (_extractorConfigured)
            {
                return;
            }
            try
            {
                var sp = App.SP;
                if (sp != null)
                {
                    RoadMaterialExtractor.Configure(sp.GetRequiredService<MapService>(), sp.GetRequiredService<WzService>());
                    _extractorConfigured = true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Road] Extractor Configure 失败: {ex.Message}");
            }
        }

        /// <summary>D2 生成器强类型构造（此前反射 3 参调 4 参 ctor——尾可选参不自动补——恒 null）。</summary>
        public static RoadGeneratorHandle? CreateGenerator(RoadMaterialPack pack, double anchorWorldX, int initialSurfaceY)
        {
            try
            {
                var gen = new InfiniteRoadGenerator(pack, anchorWorldX, initialSurfaceY);
                gen.WindowWidth = RoadConstants.FallbackWindowW;
                gen.WindowHeight = 600;
                return new RoadGeneratorHandle { Obj = gen, Road = gen, Columns = gen, InitialSurfaceY = initialSurfaceY };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadGateway] CreateGenerator: {ex.Message}");
                return null;
            }
        }

        /// <summary>旧代际实体条带整包淘汰（转发提取器 LRU）。</summary>
        public static void EvictStrips(int generation)
        {
            // RoadMaterialExtractor.EvictMapStrips(mapId) 按 mapId 维度——代际淘汰由换图路径以
            // EvictAllStrips 兜底（同进程素材池一次性重置），此处保持空实现避免误删新包。
        }

        public static void EvictAllStrips()
        {
            // 提取器 LRU 按 mapId 维度淘汰（EvictMapStrips）；WZ 重载时整实例重建，无需全清
        }
    }

namespace MiniPet.Services.Road
{
    /// <summary>
    /// 实体帧位图有界缓存（集成者加）：条带 PNG 解码一次 → 按帧号裁剪 → LRU（上限 128 帧）。
    /// 键 = mapId|lifeId|action|frameIndex；淘汰 Dispose 释放原生句柄。
    /// </summary>
    internal static class RoadEntityFrameCache
    {
        private const int MaxFrames = 128;
        private static readonly object _lock = new();
        private static readonly Dictionary<string, SKBitmap> _frames = new(StringComparer.Ordinal);
        private static readonly LinkedList<string> _order = new();
        // 条带整图解码缓存（key=mapId|lifeId|action → 已解码条带位图；与帧缓存共用淘汰预算的独立小表）
        private static readonly Dictionary<string, SKBitmap> _strips = new(StringComparer.Ordinal);

        public static SKBitmap? GetFrame(string mapId, string lifeId, string action, int frameIndex, bool isMob, RoadState state)
        {
            string fkey = $"{mapId}|{lifeId}|{action}|{frameIndex}";
            lock (_lock)
            {
                if (_frames.TryGetValue(fkey, out var hit))
                {
                    _order.Remove(fkey);
                    _order.AddLast(fkey);
                    return hit;
                }
            }
            var entry = RoadMaterialExtractor.GetEntityStrip(mapId, lifeId, action, isMob, state)
                        ?? RoadMaterialExtractor.GetEntityStrip(mapId, lifeId, action, !isMob, state);
            if (entry?.Strip == null || entry.Png == null || entry.Png.Length == 0)
            {
                return null;
            }
            string skey = $"{mapId}|{lifeId}|{action}";
            SKBitmap? strip = null;
            lock (_lock)
            {
                _strips.TryGetValue(skey, out strip);
            }
            if (strip == null)
            {
                strip = SKBitmap.Decode(entry.Png);
                if (strip == null)
                {
                    return null;
                }
                lock (_lock)
                {
                    _strips[skey] = strip;
                }
            }
            var meta = entry.Strip;
            // 帧几何以整条图实测为准（白盒实证 2026-08-28：meta.FrameHeight 偏小致「只画上半」、
            // FrameCount 不符致整条平铺）：fh=条带高；fw=条带宽/帧数（元数据仅在条带宽不可整除时兜底）
            int fh = strip.Height;
            int fc = meta.FrameCount > 0 ? meta.FrameCount : 1;
            int fw = (strip.Width % fc == 0) ? strip.Width / fc : (meta.FrameWidth > 0 ? meta.FrameWidth : strip.Width);
            if (fw <= 0) { fw = strip.Width; }
            int idx = ((frameIndex % fc) + fc) % fc;
            int left = idx * fw;
            if (left + fw > strip.Width)
            {
                fw = strip.Width - left;
                if (fw <= 0)
                {
                    return null;
                }
            }
            var frame = new SKBitmap(new SKImageInfo(fw, fh));
            using (var c = new SKCanvas(frame))
            {
                c.DrawBitmap(strip, new SKRect(left, 0, left + fw, fh), new SKRect(0, 0, fw, fh));
            }
            lock (_lock)
            {
                _frames[fkey] = frame;
                _order.AddLast(fkey);
                while (_order.Count > MaxFrames)
                {
                    var oldest = _order.First;
                    if (oldest == null)
                    {
                        break;
                    }
                    _order.RemoveFirst();
                    if (_frames.Remove(oldest.Value, out var dead))
                    {
                        _ = dead; // 释放后用修复（同纸娃娃 LRU）：渲染线程可能仍在使用 → 交 GC
                    }
                }
            }
            return frame;
        }

        /// <summary>换源图整代淘汰（crossfade 交换后调用；Dispose 全部帧与条带）。</summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                foreach (var b in _frames.Values)
                {
                    b.Dispose();
                }
                foreach (var b in _strips.Values)
                {
                    b.Dispose();
                }
                _frames.Clear();
                _strips.Clear();
                _order.Clear();
            }
        }
    }
}
}

namespace MiniPet.Services.Road
{
    /// <summary>
    /// 实体帧位图有界缓存：条带 PNG 解码一次 → 按帧号裁剪 → LRU（上限 128 帧）。
    /// 键 = mapId|lifeId|action|frameIndex；淘汰 Dispose 释放原生句柄。
    /// </summary>
    internal static class RoadEntityFrameCache
    {
        private const int MaxFrames = 128;
        private static readonly object _lock = new();
        private static readonly Dictionary<string, SKBitmap> _frames = new(StringComparer.Ordinal);
        private static readonly LinkedList<string> _order = new();
        private static readonly Dictionary<string, SKBitmap> _strips = new(StringComparer.Ordinal);

        public static SKBitmap? GetFrame(string mapId, string lifeId, string action, int frameIndex, bool isMob, RoadState state)
        {
            string fkey = $"{mapId}|{lifeId}|{action}|{frameIndex}";
            lock (_lock)
            {
                if (_frames.TryGetValue(fkey, out var hit))
                {
                    _order.Remove(fkey);
                    _order.AddLast(fkey);
                    return hit;
                }
            }
            var entry = RoadMaterialExtractor.GetEntityStrip(mapId, lifeId, action, isMob, state)
                        ?? RoadMaterialExtractor.GetEntityStrip(mapId, lifeId, action, !isMob, state);
            if (entry?.Strip == null || entry.Png == null || entry.Png.Length == 0)
            {
                return null;
            }
            string skey = $"{mapId}|{lifeId}|{action}";
            SKBitmap? strip;
            lock (_lock)
            {
                _strips.TryGetValue(skey, out strip);
            }
            if (strip == null)
            {
                strip = SKBitmap.Decode(entry.Png);
                if (strip == null)
                {
                    return null;
                }
                lock (_lock)
                {
                    _strips[skey] = strip;
                }
            }
            var meta = entry.Strip;
            // 帧几何以整条图实测为准（白盒实证 2026-08-28：meta.FrameHeight 偏小致「只画上半」、
            // FrameCount 不符致整条平铺）：fh=条带高；fw=条带宽/帧数（元数据仅在条带宽不可整除时兜底）
            int fh = strip.Height;
            int fc = meta.FrameCount > 0 ? meta.FrameCount : 1;
            int fw = (strip.Width % fc == 0) ? strip.Width / fc : (meta.FrameWidth > 0 ? meta.FrameWidth : strip.Width);
            if (fw <= 0) { fw = strip.Width; }
            int idx = ((frameIndex % fc) + fc) % fc;
            int left = idx * fw;
            if (left + fw > strip.Width)
            {
                fw = strip.Width - left;
                if (fw <= 0)
                {
                    return null;
                }
            }
            var frame = new SKBitmap(new SKImageInfo(fw, fh));
            using (var c = new SKCanvas(frame))
            {
                c.DrawBitmap(strip, new SKRect(left, 0, left + fw, fh), new SKRect(0, 0, fw, fh));
            }
            lock (_lock)
            {
                _frames[fkey] = frame;
                _order.AddLast(fkey);
                while (_order.Count > MaxFrames)
                {
                    var oldest = _order.First;
                    if (oldest == null)
                    {
                        break;
                    }
                    _order.RemoveFirst();
                    if (_frames.Remove(oldest.Value, out var dead))
                    {
                        _ = dead; // 释放后用修复（同纸娃娃 LRU）：渲染线程可能仍在使用 → 交 GC
                    }
                }
            }
            return frame;
        }

        public static void ClearAll()
        {
            lock (_lock)
            {
                foreach (var b in _frames.Values)
                {
                    b.Dispose();
                }
                foreach (var b in _strips.Values)
                {
                    b.Dispose();
                }
                _frames.Clear();
                _strips.Clear();
                _order.Clear();
            }
        }
    }
}
