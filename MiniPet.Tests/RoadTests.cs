using Xunit;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using MiniPet.Services;
using MiniPet.Services.Road;

namespace MiniPet.Tests;

/// <summary>
/// F18 无限之路单元测试（v13 定稿口径——docs/ai/algorithm-infinite-road-FINAL.md 开发唯一依据）。
/// 分四类：
///  1. RoadConstants 与算法参数总表一致性（真测——契约已落盘）；
///  2. RoadModels 契约类型行为（真测——record 值等价 / 枚举相位语义 / 计算属性）；
///  3. InfiniteRoadGenerator v13 块池洗牌/双向带/无限平地（真测——已落盘，静态纯函数标注「保留供测试」）；
///  4. 提取期组构建/三证据评分、主题池、战斗时间线（[Fact(Skip)] 占位——按落盘进度逐个解除；
///     Skip 原因逐用例注明：函数已删的旧算法用例不恢复，v13 新语义落盘后改写为对应真测）。
/// 约定同 MusicModuleTests：真实 WZ / 文件 IO / 渲染硬件路径不入自动测试（白盒出图走
/// prototypes/road-whitebox，WZ 实图对拍走 MiniPet.CrashSelfTest.RoadV5Probe）。
/// </summary>
public class RoadTests
{
    // ====== 1. RoadConstants ↔ 算法参数总表一致性 ======

    [Fact]
    public void Constants_Geometry_MatchAlgorithmSection9()
    {
        // §9：COL_W=90 / SCROLL_SPEED=100 / PREGEN_COLS=12 / SURF_LERP_W=30
        Assert.Equal(90, RoadConstants.ColW);
        Assert.Equal(100.0, RoadConstants.ScrollSpeed);
        Assert.Equal(12, RoadConstants.PregenCols);
        Assert.Equal(30, RoadConstants.SurfLerpW);
        // §3.5 回收余量 200px；§4.1 柱位图固定源分辨率 90×900 且源宽必须 = 柱宽
        Assert.Equal(200, RoadConstants.RecycleMargin);
        Assert.Equal(90, RoadConstants.ColumnBmpW);
        Assert.Equal(900, RoadConstants.ColumnBmpH);
        Assert.Equal(RoadConstants.ColW, RoadConstants.ColumnBmpW);
        // §3.2 初始填充 6~10 根平地柱（范围自洽）
        Assert.Equal(6, RoadConstants.InitFillMin);
        Assert.Equal(10, RoadConstants.InitFillMax);
        Assert.True(RoadConstants.InitFillMin < RoadConstants.InitFillMax);
        Assert.Equal(800, RoadConstants.FallbackWindowW);
    }

    [Fact]
    public void Constants_StepGeneration_MatchAlgorithmSection9()
    {
        // §9：P_STEP=0.12 / RUN_LIMIT=3 / BAND_MARGIN=120 / FLAT_TOL=4 / RECENT_N=8
        //（v13：RECENT_N=8 语义变为 recent N 公式上限 max(1, min(8, ⌊块数/2⌋))——FINAL §四）
        Assert.Equal(0.12, RoadConstants.PStep);
        Assert.Equal(3, RoadConstants.RunLimit);
        Assert.Equal(120, RoadConstants.BandMargin);
        Assert.Equal(4, RoadConstants.FlatTol); // 2026-08-28 回退：±8 致邻段凸起草块错位观感
        Assert.Equal(8, RoadConstants.RecentN);
        // STEP_MIN/MAX=40/200，且 §7 台阶判定阈值与模板下界同为 40（>40 触发 jump）
        Assert.Equal(40, RoadConstants.StepThresh);
        Assert.Equal(40, RoadConstants.StepMin);
        Assert.Equal(200, RoadConstants.StepMax);
        Assert.Equal(RoadConstants.StepThresh, RoadConstants.StepMin);
        Assert.True(RoadConstants.StepMin < RoadConstants.StepMax);
    }

    [Fact]
    public void Constants_ObjClassification_MatchAlgorithmSection2_4AndSection9()
    {
        // §2.4/§9：站地容差 60 / 装饰带高 250 / 锚定半径 45 / 后景阈值 200 与宽 300
        Assert.Equal(60, RoadConstants.ObjGroundTol);
        Assert.Equal(250, RoadConstants.ObjDecorBand);
        Assert.Equal(45, RoadConstants.ObjDecorHalfW);
        Assert.Equal(200, RoadConstants.ObjFarThresh);
        Assert.Equal(300, RoadConstants.ObjFarMinW);
    }

    [Fact]
    public void Constants_EntitySpawnAndCombat_MatchAlgorithmSection5AndSection9()
    {
        // §5.1/§9：NPC 1/10、上限 3、冷却 6；怪 1/6、上限 2、冷却 8；防冲突半径 60
        Assert.Equal(1.0 / 10, RoadConstants.NpcP, 10);
        Assert.Equal(3, RoadConstants.NpcMaxScreen);
        Assert.Equal(6, RoadConstants.NpcCooldown);
        Assert.Equal(1.0 / 6, RoadConstants.MobP, 10);
        Assert.Equal(2, RoadConstants.MobMaxScreen);
        Assert.Equal(8, RoadConstants.MobCooldown);
        Assert.Equal(60, RoadConstants.EntityClearRadius);
        // §5.3/§9：ATTACK_RANGE=70 / HIT_FRAME_RATIO=0.5
        Assert.Equal(70, RoadConstants.AttackRange);
        Assert.Equal(0.5, RoadConstants.HitFrameRatio);
    }

    [Fact]
    public void Constants_TimelineDurations_MatchAlgorithmSection5_3AndSection9()
    {
        // §5.3/§9：die1 停帧 500 → 淡出 300；hit1 兜底 300；jump 300；crossfade 500（ms）
        Assert.Equal(500, RoadConstants.DieHoldMs);
        Assert.Equal(300, RoadConstants.DieFadeMs);
        Assert.Equal(300, RoadConstants.HitFallbackMs);
        Assert.Equal(300, RoadConstants.JumpMs);
        Assert.Equal(500, RoadConstants.CrossfadeMs);
    }

    [Fact]
    public void Constants_CacheAndPool_MatchSection9AndPlan()
    {
        // §9：BSC_CACHE_MAX=128；PLAN D1 段：条带缓存 LRU 64
        Assert.Equal(128, RoadConstants.BscCacheMax);
        Assert.Equal(64, RoadConstants.StripCacheMax);
        // 池下限双常量（v13 FINAL §0）：
        // MinPoolColumns=5 = Usable 门（图级/单图是否可入主题池）；
        // NormalPoolColumns=20 = 正常档合并池下限（≥20 recent 开启；<20 降级档 recent 关闭）
        Assert.Equal(5, RoadConstants.MinPoolColumns);
        Assert.Equal(20, RoadConstants.NormalPoolColumns);
        Assert.True(RoadConstants.MinPoolColumns < RoadConstants.NormalPoolColumns);
    }

    [Fact]
    public void Constants_TownFallbackMaps_FiveVerifiedTownMapIds()
    {
        // §8：首曲未命中 → 五城镇随机（100000000/101000000/102000000/103000000/105000000，town=1 已终验）
        Assert.Equal(
            new[] { "100000000", "101000000", "102000000", "103000000", "105000000" },
            RoadConstants.TownFallbackMaps);
        // 长度恰 5 且互异（随机回退不允许重复入口）
        Assert.Equal(5, RoadConstants.TownFallbackMaps.Length);
        Assert.Equal(5, RoadConstants.TownFallbackMaps.Distinct().Count());
    }

    // ====== 2. RoadModels 契约类型行为 ======

    [Fact]
    public void PaperdollLifeId_IsSentinel_ForEntityPipelineRouting()
    {
        // 人物实体化：LifeId="__paperdoll__" 哨兵——渲染器 DrawEntities 经 EntityFrameResolver
        // 的 paperdoll 分支走 PaperdollService 合成帧（与 mob 条带路径分流）。常量须与渲染侧
        // 分支判断同源（RoadModeController.BindEntityResolver / BuildPaperdollEntityState）。
        Assert.Equal("__paperdoll__", RoadConstants.PaperdollLifeId);
    }

    [Fact]
    public void PetFollower_ApplyPlacementFalse_KeepsStateButSkipsWindowPlacement()
    {
        // Mode3 人物实体化：Follower 只推进贴地/跳跃/行走动画状态（供实体快照取 FeetY/Action/Frame），
        // applyPlacement=false 时不得再 setPetTopLeft 挪宠物窗口（避免与路窗实体双画）。
        var anim = new AnimService(new SpriteService(new CacheManager()));
        var follower = new PetFollower(anim);
        var road = new StubRoadSurface();
        int placementCalls = 0;
        // 实体渲染模式：不落位
        follower.Tick(16.6, road, 100, 200, 800, 600, 300, 100, 150, (_, _) => placementCalls++, applyPlacement: false);
        Assert.Equal(0, placementCalls);
        Assert.True(follower.HasFeet);
        Assert.Equal(300, follower.CurrentFeetY, 3);
        // 旧行为回归：applyPlacement 默认 true 仍落位（非纸娃娃宠物/Mode3 外路径不受影响）
        follower.Tick(16.6, road, 100, 200, 800, 600, 300, 100, 150, (_, _) => placementCalls++);
        Assert.Equal(1, placementCalls);
        Assert.Equal(300, follower.CurrentFeetY, 3);
    }

    private sealed class StubRoadSurface : IRoadSurfaceProvider
    {
        public double ScrollX => 0;
        public double? SurfaceY(double worldX) => 300;
    }

    [Fact]
    public void RoadPhase_HasExactlyFivePhases_NoPausedValue()
    {        // PLAN Rev 5：暂停不是相位——建模为 IsPaused 标志 + RoadClockScale∈{0,1} 叠加在
        // 生命周期相位之上（Building/Entering/Running 任一相位暂停后可正确恢复），
        // 故枚举绝不允许出现 Paused 值（否则暂停中的相位丢失、恢复错乱）。
        var names = Enum.GetNames<RoadPhase>();
        Assert.Equal(new[] { "Inactive", "Building", "Entering", "Running", "Exiting" }, names);
        Assert.DoesNotContain("Paused", names);
        // 底层值 0..4 单调（生命周期推进 = 数值递增的单向序）
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Enum.GetValues<RoadPhase>().Select(v => (int)v).ToArray());
    }

    [Fact]
    public void RoadBack_RecordValueEquality()
    {
        // back 视差不可变记录：等参构造的两个实例值等价（发布快照深拷贝比较的基础）
        var a = new RoadBack(1, 2, 3, 4, 5, 6, 0, "BS", "0", "1", 7, 8, Front: false, Alpha: 255, ScreenMode: false, SpineAni: 0, Fh: 0);
        var b = new RoadBack(1, 2, 3, 4, 5, 6, 0, "BS", "0", "1", 7, 8, Front: false, Alpha: 255, ScreenMode: false, SpineAni: 0, Fh: 0);
        Assert.Equal(a, b);
        Assert.True(a == b);
        // 任一字段差异（前置层标记）即不等——BacksFront 分层依赖此判别
        var front = a with { Front = true };
        Assert.NotEqual(a, front);
    }

    [Fact]
    public void RoadRenderRoad_RecordValueEquality()
    {
        // record 等价逐字段比较：标量字段（代际/tS/scroll 原点/垂直锚）值语义，
        // 集合字段（Columns/Entities/Decors…）引用语义——独立构造的两个等值路因列表引用不同而不等。
        // 此契约行为固定于此：渲染侧判重/判换代必须比较 BuildGeneration 等标量，不可依赖整体 Equals。
        var r1 = MakeRoad(gen: 1);
        var r2 = MakeRoad(gen: 1);
        Assert.NotEqual(r1, r2);
        // 自反等价 + 标量字段差异可判别：
        // 素材构建代际不同即不等（WZ 重载后新旧包不可混淆）
        Assert.Equal(r1, r1);
        Assert.NotEqual(r1, r1 with { BuildGeneration = 2 });
        // 垂直映射基准（62% 锚）参与等价
        Assert.NotEqual(r1, r1 with { InitialSurfaceY = r1.InitialSurfaceY + 1 });
        // crossfade 双原点：ScrollOriginX 参与等价
        Assert.NotEqual(r1, r1 with { ScrollOriginX = r1.ScrollOriginX + 1.0 });
        // 贴回 Decor（非主干组背景装饰）随载荷参与等价
        var decor = new RoadDecor(10, 20, IsObj: true, Ts: null, U: 0, No: 0, OS: "oS", L0: "l0", L1: "l1", L2: "l2");
        var withDecor = MakeRoad(gen: 1) with { };
        Assert.NotEqual(r1, r1 with { Decors = new[] { decor } });
        // 源图坐标锚（back/远obj 归一基准）参与等价——锚不一致即 crossfade 背景错位
        Assert.NotEqual(r1, r1 with { SourceAnchorX = r1.SourceAnchorX + 1 });
    }

    [Fact]
    public void RoadFrame_RecordValueEquality_RoadClockMsPresent()
    {
        // RoadClockMs/AnimDeltaMs 字段存在于构造签名 = 编译期契约已验证；
        // 运行期断言：自反射等价、路时钟差异即不等（渲染侧一切流逝型视觉只取该绝对值，禁止自累加）
        var f1 = new RoadFrame(RoadPhase.Running, false, null, MakeRoad(1), null, 0, 0.0, 1000.0, 16.6);
        Assert.Equal(f1, f1);
        Assert.Equal(1000.0, f1.RoadClockMs);
        var later = f1 with { RoadClockMs = 1016.6 };
        Assert.NotEqual(f1, later);
        // 暂停叠加标志独立于相位可组合（Running + IsPaused=true 合法——暂停不是相位）
        var paused = f1 with { IsPaused = true };
        Assert.True(paused.IsPaused);
        Assert.Equal(RoadPhase.Running, paused.Phase);
        // crossfade 进度字段参与等价（alpha 0=纯 Current，1=已切 Incoming）
        Assert.NotEqual(f1, f1 with { CrossfadeAlpha = 0.5 });
        // Sky（固定天空层）参与等价：Sky 不参与 crossfade 但随帧快照发布（null = Building/未激活）
        var sky = new RoadSky(Array.Empty<RoadBack>(), Array.Empty<RoadBack>(), 0, 0, 300, 0.0);
        Assert.Null(f1.Sky);
        Assert.NotEqual(f1, f1 with { Sky = sky });
    }

    [Fact]
    public void SmallRecords_ValueEquality()
    {
        // 契约层小 record 值等价汇总：tile/装饰/远obj/life 候选/实体渲染态/crossfade 进度
        Assert.Equal(new RoadTile(0, 1, 2, 3), new RoadTile(0, 1, 2, 3));
        Assert.NotEqual(new RoadTile(0, 1, 2, 3), new RoadTile(0, 1, 2, 4));

        Assert.Equal(new RoadObj(1, 2, "a", "b", "c", "d"), new RoadObj(1, 2, "a", "b", "c", "d"));
        Assert.NotEqual(new RoadObj(1, 2, "a", "b", "c", "d"), new RoadObj(9, 2, "a", "b", "c", "d"));

        Assert.Equal(new FarObjEntry(1, 2, "oS", "l0", "l1", "l2", 350), new FarObjEntry(1, 2, "oS", "l0", "l1", "l2", 350));
        Assert.NotEqual(new FarObjEntry(1, 2, "oS", "l0", "l1", "l2", 350), new FarObjEntry(1, 2, "oS", "l0", "l1", "l2", 299));

        Assert.Equal(new LifeCandidate(true, "9300000", 0), new LifeCandidate(true, "9300000", 0));
        Assert.NotEqual(new LifeCandidate(false, "9300000", 0), new LifeCandidate(true, "9300000", 0));

        var e1 = new RoadEntityState(true, "9300000", "100000000", 100.0, 300, "hit1", 2, 0.8, true);
        var e2 = new RoadEntityState(true, "9300000", "100000000", 100.0, 300, "hit1", 2, 0.8, true);
        Assert.Equal(e1, e2);
        Assert.NotEqual(e1, e1 with { Alpha = 1.0 });

        Assert.Equal(new RoadCrossfadeState(0.5, true), new RoadCrossfadeState(0.5, true));
        Assert.NotEqual(new RoadCrossfadeState(0.5, true), new RoadCrossfadeState(0.6, true));

        // Decor（非主干组贴回）：IsObj 分支（tile 装饰 vs obj 装饰）参与等价
        Assert.Equal(
            new RoadDecor(1, 2, true, null, 0, 0, "oS", "l0", "l1", "l2"),
            new RoadDecor(1, 2, true, null, 0, 0, "oS", "l0", "l1", "l2"));
        Assert.NotEqual(
            new RoadDecor(1, 2, true, null, 0, 0, "oS", "l0", "l1", "l2"),
            new RoadDecor(1, 2, false, "tS.img", 1, 2, null, null, null, null));
    }

    [Fact]
    public void ColumnTemplate_Weight_DecoratedColumnsBoosted()
    {
        // §3.3 抽柱加权：绑定 obj 的柱权重 ×1.5（整数化为 10/15）——v13 块池加权随机同口径
        var plain = new ColumnTemplate { Id = 1, SurfaceH = 100 };
        var decorated = new ColumnTemplate { Id = 2, SurfaceH = 100 };
        decorated.Objs.Add(new RoadObj(0, 0, "oS", "l0", "l1", "l2"));
        Assert.Equal(10, plain.Weight);
        Assert.Equal(15, decorated.Weight);
    }

    [Fact]
    public void RoadMaterialPack_Usable_RequiresMinimumFlatPool()
    {
        // Usable 唯一口径（FINAL §0）：主干柱总数（Σ max(1,WidthCols)，缺口不计——红线⑫）
        // ≥ NormalPoolColumns(20) = 正常档；<20 = 降级档仍可激活（素材照进——仲裁裁决链），
        // 柱=0 才不可用；MeetsMergeMin(≥5 柱) = 入合并池质量下限（每图贡献门）
        var pack = new RoadMaterialPack { MapId = "100000000", MainSurfaceH = 300 };
        pack.FlatColumns.Add(new ColumnTemplate { Id = 0, SurfaceH = 300, WidthCols = RoadConstants.NormalPoolColumns - 1 });
        Assert.False(pack.Usable);        // 19 柱 < 20（宽块口径：单块 19 柱）
        Assert.True(pack.MeetsMergeMin);  // ≥5 柱——入合并池（降级档素材照进的载体）
        Assert.Equal(RoadConstants.NormalPoolColumns - 1, pack.MainColumnTotal);
        pack.FlatColumns.Add(new ColumnTemplate { Id = 99, SurfaceH = 300, WidthCols = 1 });
        Assert.True(pack.Usable);         // 20 柱恰界 = 正常档
        // 缺口不计柱宽（红线⑫）：WidthCols=0 按 1 计
        var gapPack = new RoadMaterialPack { MapId = "x", MainSurfaceH = 300 };
        gapPack.FlatColumns.Add(new ColumnTemplate { Id = 0, SurfaceH = 300, WidthCols = 0 });
        Assert.Equal(1, gapPack.MainColumnTotal);
    }

    // ====== 3. InfiniteRoadGenerator v13（块池洗牌/双向带/无限平地——已落盘真测） ======
    // 期望锚定 FINAL §四（双向带/recent N/降级档）与 §0 术语表（Usable/缺口不计柱宽）。
    // 无限平地口径：所有列 SurfaceY 恒定 = pack.MainSurfaceH + offset（offset = startSurfaceY − MainSurfaceH）。

    [Fact]
    public void Generator_InitialBand_SurfaceUniformAndContiguous()
    {
        // FINAL §四：初始铺带 = 全平地（无限平地口径）——旧「路刚开始强制平地柱」语义的 v13 等价形态：
        // 带体所有列 SurfaceY 统一、WorldX 严格连续（前块右缘 = 后块左缘——地面连续零断裂红线）
        var pack = MakePack(nBlocks: 10, colsPerBlock: 2, mainSurfH: 500);
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 300, new Random(7));

        Assert.Equal(300, gen.InitialSurfaceY);
        Assert.Equal(300, gen.CurrentSurfaceY);
        var cols = gen.ActiveColumns;
        Assert.NotEmpty(cols);
        foreach (var c in cols)
        {
            Assert.Equal(300, c.SurfaceY); // 500 + offset(−200) = 300（同池块相接平齐）
            Assert.Contains(c.Template, pack.FlatColumns); // 块模板必来自池（无凭空柱）
        }
        for (int i = 1; i < cols.Count; i++)
        {
            double prevRight = cols[i - 1].WorldX + cols[i - 1].WidthCols * RoadConstants.ColW;
            Assert.Equal(prevRight, cols[i].WorldX); // WorldX 连续累加（无缺口无重叠）
        }
    }

    [Fact]
    public void Generator_SurfaceYAt_FlatDirectReturn_AndWideBlock()
    {
        // FINAL §四 静态表面函数（「保留供测试」公开纯函数）：无限平地口径——平段直返所在列
        // SurfaceY，无台阶插值/斜坡分支（旧 IsStepLerp/SlantDy 分支已删，SlantDy 数据保真不参与洗牌路）
        var cols = new List<RoadColumn>
        {
            new() { Template = new ColumnTemplate { Id = 1, SurfaceH = 0 }, WorldX = 0, SurfaceY = 300 },
            new() { Template = new ColumnTemplate { Id = 2, SurfaceH = 0 }, WorldX = 90, SurfaceY = 302 },
        };
        // 柱内任意点取所在柱值（45 ∈ [0,90) → 300；135 ∈ [90,180) → 302）
        Assert.Equal(300.0, GeneratorSurfaceYAt(cols, 45.0)!.Value, 1);
        Assert.Equal(302.0, GeneratorSurfaceYAt(cols, 135.0)!.Value, 1);
        // 右缘开区间语义：x=90 恰落第二柱左缘
        Assert.Equal(302.0, GeneratorSurfaceYAt(cols, 90.0)!.Value, 1);

        // 宽块（WidthCols=3，覆盖 [0,270)）：块内全程同表面
        var wide = new List<RoadColumn>
        {
            new() { Template = new ColumnTemplate { Id = 3, SurfaceH = 0, WidthCols = 3 }, WorldX = 0, WidthCols = 3, SurfaceY = 280 },
        };
        Assert.Equal(280.0, GeneratorSurfaceYAt(wide, 0.0)!.Value, 1);
        Assert.Equal(280.0, GeneratorSurfaceYAt(wide, 269.9)!.Value, 1);
        Assert.Null(GeneratorSurfaceYAt(wide, 270.0)); // 覆盖宽 = ColW×WidthCols，右缘开
    }

    [Fact]
    public void Generator_SurfaceYAt_NoCoverNull_AndUnorderedInput()
    {
        // 无覆盖（带外/空带）→ null，调用方按「保持上一帧」硬约束兜底（§7）；
        // 线性扫描实现不要求列序（乱序输入同结果——注释明示「按 WorldX 升序或乱序均可」）
        var unordered = new List<RoadColumn>
        {
            new() { Template = new ColumnTemplate { Id = 2, SurfaceH = 0 }, WorldX = 180, SurfaceY = 310 },
            new() { Template = new ColumnTemplate { Id = 1, SurfaceH = 0 }, WorldX = 0, SurfaceY = 300 },
        };
        Assert.Equal(300.0, GeneratorSurfaceYAt(unordered, 45.0)!.Value, 1);
        Assert.Equal(310.0, GeneratorSurfaceYAt(unordered, 200.0)!.Value, 1);
        Assert.Null(GeneratorSurfaceYAt(unordered, 90.0)); // [90,180) 无列（缺口如实断开不插值）
        Assert.Null(GeneratorSurfaceYAt(new List<RoadColumn>(), 100.0));
    }

    [Fact]
    public void Generator_RecentN_Formula_NormalPoolHalfClamp()
    {
        // FINAL §四/红线⑨：recent N = max(1, min(8, ⌊块数/2⌋))（正常档）；实现字段 _recentN 反射读取。
        // 正常档前提 = 合并池主干柱总数 ≥20（<20 即降级档 N=0）——合成池按 colsPerBlock 凑足 20 柱
        // 块数 5×4 柱 → N=2；块数 4×5 柱 → N=2；块数 32 → N=8（上限 clamp）；块数 16×2 → N=8；
        // 块数 1×20 柱 → N=max(1, min(8,0))=1（下限 1 恒达成）
        Assert.Equal(2, ReadRecentN(MakePack(5, 4)));
        Assert.Equal(2, ReadRecentN(MakePack(4, 5)));
        Assert.Equal(8, ReadRecentN(MakePack(32, 1)));
        Assert.Equal(8, ReadRecentN(MakePack(16, 2)));
        Assert.Equal(1, ReadRecentN(MakePack(1, 20)));

        // 柱宽口径（红线⑫ 缺口不计柱宽的实现 = Σ Max(1, WidthCols)）：7 块 × 3 柱 = 21 柱 ≥ 20
        // → 正常档（若误按块数 7 < 20 判降级即回归）；N = ⌊7/2⌋ = 3
        Assert.Equal(3, ReadRecentN(MakePack(7, 3)));
        // 边界：10 块 × 2 柱 = 20 柱恰达正常档下限 → N = ⌊10/2⌋ = 5
        Assert.Equal(5, ReadRecentN(MakePack(10, 2)));
    }

    [Fact]
    public void Generator_RecentN_Formula_DegradedPoolClosed()
    {
        // FINAL §四/§0 Usable 唯一口径：合并池主干柱总数 < 20 → 降级档 recent 关闭（N=0，
        // 精确语义 = 随机取块不查 recent 队列——「重复就重复」拍板）
        Assert.Equal(0, ReadRecentN(MakePack(2, 1)));  // 2 柱 < 20
        Assert.Equal(0, ReadRecentN(MakePack(19, 1))); // 19 柱 < 20（边界下侧）
        // 9 块 × 2 柱 = 18 柱 < 20 → 降级（柱宽求和口径，非块数）
        Assert.Equal(0, ReadRecentN(MakePack(9, 2)));
    }

    [Fact]
    public void Generator_RecentNoRepeat_ConsecutiveBlocksWithinWindow()
    {
        // FINAL §四 防重行为：正常档取块避开最近 N 块模板 → 带序列任意连续 N+1 块模板互不相同。
        // 池 8 块（32 柱）→ N = 4：连续 5 块必互异。窗口放大铺长带（单向右移 = 纯 append——
        // 空间序 = 取块时间序，全带断言成立）；初始小窗口切片含 ~6 块同样可断
        var pack = MakePack(8, 4, mainSurfH: 300);
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 300, new Random(42));
        var initial = gen.ActiveColumns.Select(c => c.Template).ToList();
        Assert.True(initial.Count >= 5);
        AssertNoRepeatWithinWindow(initial, window: 5);

        // Advance 大幅右移（窗口 12000px）→ 新铺 ~33 块全进视图，整带防重断言
        gen.WindowWidth = 12000;
        gen.Advance(gen.ScrollX + 12000 + 12000);
        var band = gen.ActiveColumns.Select(c => c.Template).ToList();
        Assert.True(band.Count >= 20, "铺带长度不足（期望 ≥20 块）");
        AssertNoRepeatWithinWindow(band, window: 5);
    }

    [Fact]
    public void Generator_DegradedPool_AllowsRepeat_RoadNeverBreaks()
    {
        // 降级档（池 < 20 柱）：recent 关闭 → 单模板池反复出现属预期（不报错不断路）；
        // SurfaceY 全程有值（路可用——FINAL §六 降级档「路可用、循环感明示」）
        var pack = MakePack(1, 4, mainSurfH: 300); // 4 柱 < 20
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 300, new Random(7));
        double edge = gen.ScrollX + gen.WindowWidth;
        for (int i = 0; i < 4; i++)
        {
            edge += gen.WindowWidth;
            gen.Advance(edge);
        }
        var cols = gen.ActiveColumns;
        // 视图条数受切片宽限制（1 块 = 360px > 窗 1/3）——断言带持续扩展用覆盖宽度口径：
        // 4 次推进后视图覆盖 ≥ 3 屏（历史带不删 + 右向扩展）
        double covered = cols.Sum(c => c.WidthCols * RoadConstants.ColW);
        Assert.True(covered >= gen.WindowWidth * 3, $"视图覆盖不足：{covered}px");
        Assert.True(cols.Count >= 3);
        foreach (var c in cols)
        {
            Assert.Same(pack.FlatColumns[0], c.Template); // 重复 = 降级档预期
            Assert.Equal(300, c.SurfaceY);
        }
    }

    [Fact]
    public void Generator_Bidirectional_LeftAdvancePrepends_RightReturnReplaysHistory()
    {
        // FINAL §四 双向带：向左滚动 Prepend 新内容（左走出新内容）；向右回走历史保留零生成
        //（历史列 WorldX/Template 原样回放——往返重复 = 设计取舍明示）；回收仅背离方向远端退出视图
        var pack = MakePack(10, 2, mainSurfH: 300);
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 300, new Random(11));

        double edge = gen.ScrollX + gen.WindowWidth;
        // 先向右推进 2 屏建立历史
        for (int i = 0; i < 2; i++)
        {
            edge += gen.WindowWidth;
            gen.Advance(edge);
        }
        var history = gen.ActiveColumns.Select(c => (c.WorldX, c.SurfaceY, c.Template)).ToList();
        double minHistX = history.Min(c => c.WorldX);

        // 向左推进 2 屏：左缘出新内容（Prepend——新列 WorldX 递减延伸）
        for (int i = 0; i < 2; i++)
        {
            edge -= gen.WindowWidth;
            gen.Advance(edge);
        }
        var afterLeft = gen.ActiveColumns;
        Assert.True(afterLeft.Min(c => c.WorldX) < minHistX - gen.WindowWidth); // 左侧确有新列

        // 向右回走原位：历史列（WorldX/SurfaceY/Template）逐条恢复原样
        double backEdge = gen.ScrollX + gen.WindowWidth;
        for (int i = 0; i < 2; i++)
        {
            backEdge += gen.WindowWidth;
            gen.Advance(backEdge);
        }
        var returned = gen.ActiveColumns.Select(c => (c.WorldX, c.SurfaceY, c.Template)).ToList();
        foreach (var h in history)
        {
            var match = returned.FirstOrDefault(r => r.WorldX == h.WorldX);
            Assert.Equal(h.SurfaceY, match.SurfaceY);
            Assert.Same(h.Template, match.Template);
        }
    }

    [Fact]
    public void Generator_SurfaceY_InstanceCoveredAcrossBand()
    {
        // 实例表面函数（带体历史不删——双向回走区域仍有效）：带覆盖范围内逐点采样恒有值
        // 且 = 统一带表面（无台阶零高差——「地面连续零台阶」基线项的生成器侧断言）
        var pack = MakePack(10, 2, mainSurfH: 300);
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 320, new Random(3));
        double edge = gen.ScrollX + gen.WindowWidth;
        for (int i = 0; i < 3; i++)
        {
            edge += gen.WindowWidth;
            gen.Advance(edge);
        }
        var cols = gen.ActiveColumns;
        double left = cols.Min(c => c.WorldX);
        double right = cols.Max(c => c.WorldX + c.WidthCols * RoadConstants.ColW);
        for (double x = left + 1; x < right - 1; x += 37.0)
        {
            double? y = gen.SurfaceY(x);
            Assert.NotNull(y);
            Assert.Equal(320.0, y.Value, 1); // MainSurfaceH 300 + offset 20 = 320
        }
        // 带外 → null
        Assert.Null(gen.SurfaceY(left - 5000));
        Assert.Null(gen.SurfaceY(right + 5000));
    }

    [Fact]
    public void Generator_EmptyPool_DefensiveNoCrash()
    {
        // 空池防御（Usable=false 误激活）：不扩展、不抛异常、SurfaceY 恒 null（调用方保上帧兜底）
        var pack = MakePack(0, 1);
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 300, new Random(1));
        Assert.Empty(gen.ActiveColumns);
        Assert.Null(gen.SurfaceY(100.0));
        gen.Advance(2000); // 空池 Advance 不抛
        Assert.Empty(gen.ActiveColumns);
        Assert.Equal(1200, gen.ScrollX); // 2000 − WindowWidth(800)
    }

    // ====== 4. RoadMapResolver 候选选择（已落盘真测） ======

    [Fact]
    public void RoadMapResolver_TownFirstNoMixedPool_AndDeterministicFallback()
    {
        // §8 第 3/4 条（纯函数 PickMapFromCandidates / PickTownFallback）：
        // 多图命中优先 town=1 随机、绝不混池；无城镇才全池随机；空候选 null（走回退链）
        var mixed = new List<(string MapId, bool IsTown)>
        {
            ("100000000", true),
            ("200000000", false),
            ("101000000", true),
        };
        var towns = new HashSet<string> { "100000000", "101000000" };
        for (int i = 0; i < 20; i++)
        {
            var pick = InvokeStatic(new[] { "RoadMapResolver" }, new[] { "PickMapFromCandidates" }, new object?[] { mixed, new Random(i) });
            Assert.NotNull(pick);
            Assert.Contains((string)pick!, towns);          // 有 town 绝不混入非 town
        }

        // 无城镇 → 全池随机；固定种子两次调用结果一致（可复现调试）
        var noTown = new List<(string MapId, bool IsTown)> { ("200000000", false), ("910000000", false) };
        var a1 = InvokeStatic(new[] { "RoadMapResolver" }, new[] { "PickMapFromCandidates" }, new object?[] { noTown, new Random(99) });
        var a2 = InvokeStatic(new[] { "RoadMapResolver" }, new[] { "PickMapFromCandidates" }, new object?[] { noTown, new Random(99) });
        Assert.NotNull(a1);
        Assert.Equal(a1, a2);
        Assert.Contains((string)a1!, new HashSet<string> { "200000000", "910000000" });

        // 空候选 → null（调用方走「沿用上一首/首曲五城镇」回退链——该步为实例态逻辑，集成 harness 覆盖）
        var empty = InvokeStatic(new[] { "RoadMapResolver" }, new[] { "PickMapFromCandidates" }, new object?[] { new List<(string, bool)>(), new Random(1) });
        Assert.Null(empty);

        // 首曲五城镇回退：固定种子确定性 + 结果必属 TownFallbackMaps
        var f1 = InvokeStatic(new[] { "RoadMapResolver" }, new[] { "PickTownFallback" }, new object?[] { new Random(123) });
        var f2 = InvokeStatic(new[] { "RoadMapResolver" }, new[] { "PickTownFallback" }, new object?[] { new Random(123) });
        Assert.NotNull(f1);
        Assert.Equal(f1, f2);
        Assert.Contains((string)f1!, RoadConstants.TownFallbackMaps);
    }

    // ====== 5. v13 提取期组构建 / 三证据评分（RoadMaterialExtractor 已落盘——private 纯函数反射真测） ======
    // 期望锚定 FINAL §一（colTop 链聚组 + 三证据评分）与常量（ChainGapMin/Max=88/92、ChainGapFillMax=272、
    // ChainTopTol=8、GroupJoinTol=8、LifeFhTol=20、FootholdMinLen=270、MainContinuityMin=0.6）。

    [Fact(Skip = "v13 无逐柱掷骰（PStep 掷骰/台阶分支随逐柱生成删除）——块池加权随机为 private TakeNextBlock，防重行为已由 Generator_RecentNoRepeat 真测覆盖；加权随机不可确定性断言")]
    public void PickNextColumn_DiceBoundary_NeverHitFlat_AlwaysHitStep()
    {
        // 原 §3.3 掷骰语义（rng.NextDouble() < PStep 才进台阶分支）——v13 块池洗牌无此决策点
    }

    [Fact(Skip = "v13 无限平地口径：BandMargin 中带守卫/游程限制随台阶机制整体删除（FINAL §四 洗牌 = 随机取块，无方向分支）")]
    public void PickNextColumn_BandMarginAndRunLimitDirectionGuards()
    {
        // 原 §3.3 方向守卫（BandMargin 禁行/RunLimit 反向）——v13 无台阶无方向
    }

    [Fact]
    public void Chains_ContinuousSameSurfaceColumns_MergeIntoOneChain()
    {
        // FINAL §一：链 = 相邻列 x 差 ∈[88,92] 且 colTop 差 ≤8 的连续列串；链表面 = 链内 colTop 众数
        var cols = MakeSourceCols(new[] { 0, 90, 180, 270 }, 300);
        var list = Wrap(InvokeExtractor("BuildChains", cols));
        Assert.Single(list);
        Assert.Equal(300, ReadField(list[0]!, "Surface"));
        Assert.Equal(0, ReadField(list[0]!, "MinX"));
        var chainCols = Wrap(ReadField(list[0]!, "Cols"));
        Assert.Equal(4, chainCols.Count);
    }

    [Fact]
    public void Chains_TopDiffTolerance8_InclusiveBoundary()
    {
        // colTop 差 ≤8 同链（ChainTopTol=8 闭）：差 8 → 1 链；差 9 → 断 2 链
        var joined = Wrap(InvokeExtractor("BuildChains", MakeSourceCols(new[] { 0, 90 }, 300, 308)));
        Assert.Single(joined);
        var broken = Wrap(InvokeExtractor("BuildChains", MakeSourceCols(new[] { 0, 90 }, 300, 309)));
        Assert.Equal(2, broken.Count);
    }

    [Fact]
    public void Chains_GapUpToTwoColumns_CloneFilledFromPrev()
    {
        // 缺口 ≤2 柱（x 差 ∈(92,272]）同链 + 缺口列克隆前柱整栈补齐（F18-14 原理）：
        // dx=180（1 柱缺口）→ 链 3 柱（克隆 x=90）；dx=270（2 柱缺口）→ 链 4 柱（克隆 x=90/180）
        var src = MakeSourceCols(new[] { 0, 180 }, 300, 300);
        SetSourceColTiles(src[0], (10, -20, 80, 40)); // 源柱塞一块 tile 验证克隆整栈平移
        var one = Wrap(InvokeExtractor("BuildChains", src));
        Assert.Single(one);
        var oneCols = Wrap(ReadField(one[0]!, "Cols"));
        Assert.Equal(3, oneCols.Count);
        Assert.Equal(90, ReadField(oneCols[1]!, "X")); // 克隆柱 x = 前柱+90
        Assert.Equal(300, ReadField(oneCols[1]!, "ColTop"));

        var two = Wrap(InvokeExtractor("BuildChains", MakeSourceCols(new[] { 0, 270 }, 300, 300)));
        Assert.Single(two);
        var twoCols = Wrap(ReadField(two[0]!, "Cols"));
        Assert.Equal(4, twoCols.Count);

        // 克隆柱 tile 栈 = 前柱整栈 x 平移（VisLeft + shift）
        var clonedTiles = Wrap(ReadField(oneCols[1]!, "Tiles"));
        Assert.Single(clonedTiles);
        Assert.Equal(100, ReadField(clonedTiles[0]!, "VisLeft")); // 10 + (90−0)
    }

    [Fact]
    public void Chains_GapOverTwoColumns_AlwaysFill_FloorGuaranteed()
    {
        // 2026-09-01 甲方拍板「人物脚下地板必须始终存在」：缺口一律克隆补齐（撤销"透明断开"）：
        // dx=273（>272 仍克隆——3 柱）；dx=363（4 柱缺口——5 柱含 4 克隆）
        var justOver = Wrap(InvokeExtractor("BuildChains", MakeSourceCols(new[] { 0, 273 }, 300, 300)));
        Assert.Single(justOver);
        Assert.Equal(5, Wrap(ReadField(justOver[0]!, "Cols")).Count); // 源 2 + 克隆 3（(273-1)/90）
        var wide = Wrap(InvokeExtractor("BuildChains", MakeSourceCols(new[] { 0, 363 }, 300, 300)));
        Assert.Single(wide);
        Assert.Equal(6, Wrap(ReadField(wide[0]!, "Cols")).Count); // 源 2 + 克隆 4
    }

    [Fact]
    public void Groups_RepSingleLayerJudgment_NoChainDrift()
    {
        // FINAL §一/红线⑮：组并入 = 与组代表差 ≤8（单层判定，无链式传递）——
        // 链 300 → 308（差 8 并入，代表仍 300）→ 316（与代表差 16 ≠ 与前链差——不得链式并入）
        var chains = MakeChains((minX: 0, surface: 300), (minX: 500, surface: 308), (minX: 1000, surface: 316));
        var groups = Wrap(InvokeExtractor("BuildGroups", chains));
        Assert.Equal(2, groups.Count); // 链式传递会错并成 1 组（漂移雪球回归哨兵）
        Assert.Equal(300, ReadField(groups[0]!, "RepSurface"));
        Assert.Equal(316, ReadField(groups[1]!, "RepSurface"));
    }

    [Fact]
    public void Groups_RepIsSmallestXChainSurface()
    {
        // 组代表 = 组内源图 x 最小的链的表面（首条并入链锁定，漂移上限 8px）
        var chains = MakeChains((minX: 0, surface: 305), (minX: 100, surface: 300));
        var groups = Wrap(InvokeExtractor("BuildGroups", chains));
        Assert.Single(groups);
        Assert.Equal(305, ReadField(groups[0]!, "RepSurface")); // x 最小链 305 锁定，300 后并入不改代表
        // ColCount = 组内全部链柱数之和
        Assert.Equal(2, ReadField(groups[0]!, "ColCount"));
    }

    [Fact]
    public void Scoring_FootholdPerRootAccumulate_NoCap()
    {
        // FINAL §一/红线⑬⑭：foothold 项 = 0.5 × Σ(lenᵢ/270) / 3 逐根累加——无单根 cap、无总和 cap。
        // 组 8 柱 rep=300 + 3 根各 300px @ y=300 → Σ=3.3333 → score = 8×(1+0.5×3.3333/3) = 12.444；
        // 旧 cap 口径（min(1,Σ/810)）会给 1.5 → 12.0——差值即回归哨兵
        var groups = MakeGroupsWithIdx(8, 300);
        var mi = new MiniPet.Models.MapInfo();
        foreach (var x in new[] { 0, 400, 800 })
        {
            mi.Footholds.Add(new MiniPet.Models.MapFoothold { X1 = x, Y1 = 300, X2 = x + 300, Y2 = 300 });
        }
        InvokeExtractor("ScoreGroups", groups, mi, MakeLifeRefs());
        double score3 = Convert.ToDouble(ReadField(groups[0]!, "Score"));
        Assert.Equal(12.444, score3, 3);

        // 8 根 → Σ=8.889 → score = 8×(1+0.5×8.889/3) = 19.852（cap 口径恒 12——无 cap 铁证）
        var mi8 = new MiniPet.Models.MapInfo();
        for (int i = 0; i < 8; i++)
        {
            mi8.Footholds.Add(new MiniPet.Models.MapFoothold { X1 = i * 400, Y1 = 300, X2 = i * 400 + 300, Y2 = 300 });
        }
        var groups8 = MakeGroupsWithIdx(8, 300);
        InvokeExtractor("ScoreGroups", groups8, mi8, MakeLifeRefs());
        Assert.Equal(19.852, Convert.ToDouble(ReadField(groups8[0]!, "Score")), 3);
    }

    [Fact]
    public void Scoring_FootholdQualification_SurfaceTolLengthMinHorizontal()
    {
        // foothold 资格三条件（FINAL §一）：水平（y1==y2）+ 长 ≥270 + 组表面 ±20 内
        var groups = MakeGroupsWithIdx(4, 300);
        var mi = new MiniPet.Models.MapInfo
        {
            // ①长 269（<270 不计）@y=300
            Footholds = { new MiniPet.Models.MapFoothold { X1 = 0, Y1 = 300, X2 = 269, Y2 = 300 } },
            // ②长 300 但 y=组表面+21（>±20 不计）
            // ③长 300 且 y=组表面−20（恰界计入）
            // ④非水平（Y1≠Y2 不计）
        };
        mi.Footholds.Add(new MiniPet.Models.MapFoothold { X1 = 500, Y1 = 321, X2 = 800, Y2 = 321 });
        mi.Footholds.Add(new MiniPet.Models.MapFoothold { X1 = 1000, Y1 = 280, X2 = 1300, Y2 = 280 });
        mi.Footholds.Add(new MiniPet.Models.MapFoothold { X1 = 2000, Y1 = 330, X2 = 2400, Y2 = 300 });
        InvokeExtractor("ScoreGroups", groups, mi, MakeLifeRefs());
        Assert.Equal(300L, Convert.ToInt64(ReadField(groups[0]!, "FootholdLen"))); // 仅 ③ 计入（Σ = 300/270×270 = 300）
        double score = Convert.ToDouble(ReadField(groups[0]!, "Score"));
        Assert.Equal(4 * (1 + 0.5 * (300.0 / 270) / 3), score, 6);
    }

    [Fact]
    public void Scoring_LifeCount_FhAuthorityTol20_HideFiltered()
    {
        // life 项（FINAL §0/§一）：Hide==false 且 Fh 权威层 == 组表面（±20）才计数；
        // life 倍率 = 1 + 0.25 × min(1, life数/3)（W6 实测口径：life 8 → ×1.25）
        var groups = MakeGroupsWithIdx(9, 300);
        var lifes = MakeLifeRefs(
            (hide: false, fhY: 315, feetY: 0),   // Fh 差 15 ≤20 → 计
            (hide: false, fhY: 325, feetY: 0),   // Fh 差 25 >20 → 不计（无 Fh 几何 feet=0 距 300 差 300 也 >60）
            (hide: true, fhY: 300, feetY: 0),    // Hide → 不计
            (hide: false, fhY: 300, feetY: 0),   // Fh 恰界 → 计
            (hide: false, fhY: int.MinValue, feetY: 360)); // 无 Fh 几何回退差 60（恰界）→ 计
        InvokeExtractor("AssignLifes", lifes, groups);
        var mi = new MiniPet.Models.MapInfo();
        InvokeExtractor("ScoreGroups", groups, mi, lifes);
        Assert.Equal(3, ReadField(groups[0]!, "LifeCount"));
        Assert.Equal(9 * (1 + 0.25 * Math.Min(1.0, 3 / 3.0)), Convert.ToDouble(ReadField(groups[0]!, "Score")), 6); // life 3 → ×1.25
    }

    [Fact]
    public void AssignLifes_FhPriorityOverGeometry_NearestOnDoubleHit()
    {
        // life 归属（FINAL §0）：Fh 权威 ±20 优先；无 Fh 几何 feet ±60 回退；双命中取 |层−组表面| 最小者
        var groups = MakeGroupsWithIdx(0, 300, (idx: 1, rep: 320)); // 组0 rep=300、组1 rep=320
        var lifes = MakeLifeRefs(
            (hide: false, fhY: 305, feetY: 380)); // Fh=305：组0 差5（≤20）、组1 差15（≤20）双命中 → 取更近组0；
                                                  // 几何 feet=380：组1 差 60 也命中——Fh 权威优先不受几何干扰
        InvokeExtractor("AssignLifes", lifes, groups);
        Assert.Equal(0, ReadField(lifes[0]!, "GroupIdx"));

        // 无 Fh：几何回退（tol=60）——feet=350 距组1(320) 差 30 命中组1（组0 差 50 也命中 → 取更近组1）
        var lifes2 = MakeLifeRefs((hide: false, fhY: int.MinValue, feetY: 350));
        InvokeExtractor("AssignLifes", lifes2, groups);
        Assert.Equal(1, ReadField(lifes2[0]!, "GroupIdx"));

        // 全不中 → -1（不计分——Fh 众数层 ±20 无任何组命中回退语义的对照组）
        var lifes3 = MakeLifeRefs((hide: false, fhY: 500, feetY: 500));
        InvokeExtractor("AssignLifes", lifes3, groups);
        Assert.Equal(-1, ReadField(lifes3[0]!, "GroupIdx"));
    }

    [Fact]
    public void MainContinuity_MainGroupScope_GateAt06()
    {
        // 连续率（FINAL §0）：作用域 = 主干组柱集，相邻柱对 x 差 ∈[88,92] 占比；<0.6 整图 Usable 拒绝
        var continuous = MakeGroupsWithIdx(4, 300); // 柱 x=0/90/180/270
        SetGroupCols(continuous[0]!, MakeSourceCols(new[] { 0, 90, 180, 270 }, 300));
        Assert.Equal(1.0, Convert.ToDouble(InvokeExtractor("MainContinuity", continuous[0])), 6);

        var sparse = MakeGroupsWithIdx(3, 300); // 柱 x=0/90/400（1/2 对连续 = 0.5 < 0.6）
        SetGroupCols(sparse[0]!, MakeSourceCols(new[] { 0, 90, 400 }, 300));
        Assert.Equal(0.5, Convert.ToDouble(InvokeExtractor("MainContinuity", sparse[0])), 6);
    }

    [Fact]
    public void PickMainGroup_HighestScoreWins()
    {
        // 主干组 = score 最高（柱数单证据修死：8 柱无证据 < 9 柱无证据，但 8 柱带 foothold+life 证据可翻盘）
        var weak = MakeGroupsWithIdx(8, 100);
        ReadFieldSet(weak[0]!, "Score", 10.0);
        var strong = MakeGroupsWithIdx(0, 300, (idx: 1, rep: 200));
        ReadFieldSet(strong[1]!, "Score", 11.25);
        var main = InvokeExtractor("PickMainGroup", new List<object?>(strong).Concat(weak).ToList());
        Assert.Equal(200, ReadField(main!, "RepSurface"));
    }

    [Fact]
    public void ThemePool_ResolveMulti_MergesQualifiedPacks()
    {
        // FINAL §三 主题池合并（MergePacks 纯函数真测）：跨图重定基 bandY = 首包表面、
        // 块 Id 全池重编号、Sky/锚点取首包（多图背景不混池）、PoolMapIds 记贡献图、
        // 柱数拼接；合格性门（每图 ≥5 柱 MeetsMergeMin + PackHasPixels）在 ResolveMultiAsync
        // 调用方（WZ 依赖），MeetsMergeMin 语义已由 RoadMaterialPack_Usable 真测钉死
        var packA = MakePack(3, 8, mainSurfH: 300, mapId: "100000000");   // 24 柱——首包（bandY 基准）
        var packB = MakePack(2, 3, mainSurfH: 500, mapId: "200000000");   // 6 柱——贡献小但 ≥5 入池
        var merged = RoadMapResolver.MergePacks(new[] { packA, packB });

        // 贡献记录 + Sky/锚点属主 = 首包
        Assert.Equal(new[] { "100000000", "200000000" }, merged.PoolMapIds);
        Assert.Equal("100000000", merged.MapId);
        // bandY 重定基：合并池 MainSurfaceH = 首包表面；B 包块 SurfaceH = 500 + (300−500) = 300
        Assert.Equal(300, merged.MainSurfaceH);
        Assert.All(merged.FlatColumns, t => Assert.Equal(300, t.SurfaceH));
        // 柱数拼接（24 + 6 = 30 ≥20 = 正常档）与 Id 全池唯一
        Assert.Equal(30, merged.MainColumnTotal);
        Assert.True(merged.Usable);
        Assert.Equal(merged.FlatColumns.Count, merged.FlatColumns.Select(t => t.Id).Distinct().Count());
        // 首包/次包块数拼接：A 3 块（宽 8 柱）+ B 2 块（宽 3 柱，重定基后 SurfaceH=300）
        Assert.Equal(3, merged.FlatColumns.Count(t => t.WidthCols == 8));
        Assert.Equal(2, merged.FlatColumns.Count(t => t.WidthCols == 3 && t.SurfaceH == 300));
    }

    [Fact]
    public void ThemePool_MergePacks_EmptyInput_YieldsEmptyPool()
    {
        // 空输入防御：不抛异常、空池（MainColumnTotal=0 → Usable=false → 调用方走兜底链）
        var merged = RoadMapResolver.MergePacks(Array.Empty<RoadMaterialPack>());
        Assert.Equal(0, merged.MainColumnTotal);
        Assert.False(merged.Usable);
        Assert.Empty(merged.PoolMapIds);
    }

    [Fact(Skip = "待 Extractor v13 块切分/底边绑定与 Renderer/Controller 定稿后按真实签名评估：SplitBlocks/BindObj 纯函数若可反射合成则改写真测（obj 保守区间双缘修正 + 8 柱软上限 + 真实底边 ±60）；否则保持 Skip 并注明")]
    public void BlockSplit_AndObjBottomBinding_PendingFinalSignature()
    {
        // v13 改写方向（FINAL §二 + W10 实测基准）：
        // ▽ 块切分：锚点 o.X 落块区间归该块；区间冲突 → 块边界右移至 obj 右缘+10 连锁收敛；禁硬切 obj（红线⑧）
        // ▽ 底边绑定：真实底边 = o.Y−oy+H 贴主干层表面 ±60（大门 o=3907,-47 oy=208 H=417 → 底边 162 ≈152±10 绑定；
        //   锚点口径差 199 须不再为判据——红线⑦回归）
    }

    [Fact(Skip = "RoadCombatDirector 已落盘但 _rng 不可注入（生成 roll 1/10、1/6 不可控）且 encounter 入口依赖 AnimService 纸娃娃租约——确定性时钟断言需依赖注入重构；AttackRange/HitFrameRatio/DieHoldMs/DieFadeMs 已由常量真测钉死")]
    public void CombatTimeline_ClockDrivenPhaseAssertions()
    {
        // 占位规格（解除时按真实 API 改写；算法 §5.3 战斗状态机）：
        //  1. approach：怪世界 x 距人物世界 x <= ATTACK_RANGE(70) 触发；
        //  2. 触发即向 AnimService 取 CombatLease，租约被破坏 → 作废该次战斗；
        //  3. 攻击动作进度达 HitFrameRatio=0.5 → 怪播 hit1（兜底 300ms）；
        //  4. hit1 播完 → die1 → 停帧 500 → 淡出 300 → 销毁；
        //  5. 全程路不停（时钟推进下 scrollX 持续递增）。
    }

    // ====== private ======

    /// <summary>构造最小合法 RoadRenderRoad（gen 可变——素材代际差异断言用；v13 契约含 Decors/源图锚）。</summary>
    private static RoadRenderRoad MakeRoad(int gen)
    {
        return new RoadRenderRoad(
            gen, "100000000", "woodMarble.img",
            new[] { new RoadColumn { Template = new ColumnTemplate { Id = 1, SurfaceH = 0 }, WorldX = 0, SurfaceY = 300 } },
            Array.Empty<RoadEntityState>(), Array.Empty<FarObjEntry>(), Array.Empty<RoadDecor>(),
            0.0, 300, 0, 300);
    }

    /// <summary>构造合成块池 pack：nBlocks 个块模板 × 每块 colsPerBlock 柱，SurfaceH 统一 = mainSurfH。</summary>
    private static RoadMaterialPack MakePack(int nBlocks, int colsPerBlock, int mainSurfH = 300, string mapId = "100000000")
    {
        var pack = new RoadMaterialPack
        {
            MapId = mapId,
            TileSetName = "woodMarble.img",
            MainSurfaceH = mainSurfH,
        };
        for (int i = 0; i < nBlocks; i++)
        {
            pack.FlatColumns.Add(new ColumnTemplate { Id = i, SurfaceH = mainSurfH, WidthCols = Math.Max(1, colsPerBlock) });
        }
        return pack;
    }

    // ── v13 提取器 private 纯函数反射设施（BuildChains/BuildGroups/ScoreGroups 等合成输入直测） ──

    /// <summary>取 RoadMaterialExtractor 私有嵌套中间态类型（SourceCol/Chain/Group/LifeRef/ColTile）。</summary>
    private static Type NestedType(string name)
    {
        return typeof(RoadMaterialExtractor).GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new NotSupportedException("嵌套类型未落盘：" + name + "——Extractor v13 中间态结构变更后适配本测试");
    }

    /// <summary>
    /// 反射调用 RoadMaterialExtractor private static 纯函数（按方法名+参数个数定位）；
    /// List&lt;object?&gt; 实参自动转换为形参的强类型 List&lt;T&gt;（合成中间态集合传参）。方法未落盘 → 抛适配提示。
    /// </summary>
    private static object? InvokeExtractor(string method, params object?[] args)
    {
        var m = typeof(RoadMaterialExtractor)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(mi => mi.Name == method && mi.GetParameters().Length == args.Length)
            ?? throw new NotSupportedException(
                "方法未落盘或参数个数不符：" + method + "(" + args.Length + "参) @ RoadMaterialExtractor——按最终签名适配本测试");
        var bound = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var pt = m.GetParameters()[i].ParameterType;
            if (args[i] is List<object?> loose && pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(List<>))
            {
                var real = Activator.CreateInstance(pt)!;
                var add = pt.GetMethod("Add")!;
                foreach (var e in loose)
                {
                    add.Invoke(real, new[] { e });
                }
                bound[i] = real;
            }
            else
            {
                bound[i] = args[i];
            }
        }
        return m.Invoke(null, bound);
    }

    /// <summary>结果集合包装（List&lt;Chain&gt;/List&lt;Group&gt; 等非泛型枚举 → List&lt;object?&gt; 便于断言索引）。</summary>
    private static List<object?> Wrap(object? result)
    {
        if (result == null) { return new List<object?>(); }
        if (result is IEnumerable<object?> loose) { return loose.ToList(); }
        if (result is System.Collections.IEnumerable en && result is not string) { return en.Cast<object?>().ToList(); }
        throw new InvalidOperationException("结果非集合：" + result.GetType().Name);
    }

    /// <summary>构造合成源图列集（SourceCol.X/ColTop；tops 单值 = 全列同顶，逐值 = 逐列指定）。</summary>
    private static List<object?> MakeSourceCols(int[] xs, params int[] tops)
    {
        var t = NestedType("SourceCol");
        var result = new List<object?>();
        for (int i = 0; i < xs.Length; i++)
        {
            var col = Activator.CreateInstance(t, nonPublic: true)!;
            SetMember(col, "X", xs[i]);
            SetMember(col, "ColTop", tops.Length == 1 ? tops[0] : tops[i]);
            result.Add(col);
        }
        return result;
    }

    /// <summary>给合成源柱塞 tile（ColTile record：VisLeft/VisTop/W/H/U/No/Ts/IsSurface/Z/Id——克隆平移断言用；
    /// 合成 tile 的 Z/Id 取 0——测试只断言几何，不涉柱内 (Z,Id) 混排）。</summary>
    private static void SetSourceColTiles(object col, params (int VisLeft, int VisTop, int W, int H)[] tiles)
    {
        var t = NestedType("ColTile");
        var tilesField = col.GetType().GetField("Tiles", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (System.Collections.IList?)tilesField.GetValue(col) ?? throw new InvalidOperationException("SourceCol.Tiles null");
        foreach (var (vl, vt, w, h) in tiles)
        {
            list.Add(Activator.CreateInstance(t, vl, vt, w, h, "bsc", 0, "testTs.img", true, 0, 0)!);
        }
    }

    /// <summary>构造合成链集（Chain.Cols 各塞 1 个 x=minX 的列——BuildGroups 取 Cols[0]/[^1].X 需非空）。</summary>
    private static List<object?> MakeChains(params (int MinX, int Surface)[] chains)
    {
        var t = NestedType("Chain");
        var result = new List<object?>();
        foreach (var (minX, surface) in chains)
        {
            var chain = Activator.CreateInstance(t, nonPublic: true)!;
            var cols = MakeSourceCols(new[] { minX }, surface);
            SetMember(chain, "Cols", ToTypedList(cols, NestedType("SourceCol")));
            SetMember(chain, "Surface", surface);
            SetMember(chain, "MinX", minX);
            result.Add(chain);
        }
        return result;
    }

    /// <summary>
    /// 构造合成组集（Group）：组0 = {Idx=0, RepSurface=rep, ColCount=colCount}；
    /// extra 追加组（ColCount 默认 1）。Chains/Cols 留空——评分/归属只用 Idx/RepSurface/ColCount。
    /// </summary>
    private static List<object?> MakeGroupsWithIdx(int colCount, int rep, params (int Idx, int Rep)[] extra)
    {
        var t = NestedType("Group");
        var result = new List<object?> { MakeGroup(t, 0, rep, colCount) };
        foreach (var (idx, r) in extra)
        {
            result.Add(MakeGroup(t, idx, r, 1));
        }
        return result;
    }

    /// <summary>构造单个合成 Group 实例。</summary>
    private static object MakeGroup(Type t, int idx, int rep, int colCount)
    {
        var g = Activator.CreateInstance(t, nonPublic: true)!;
        SetMember(g, "Idx", idx);
        SetMember(g, "RepSurface", rep);
        SetMember(g, "ColCount", colCount);
        return g;
    }

    /// <summary>设置合成组的柱集（MainContinuity 断言用——转换为目标 List&lt;SourceCol&gt; 类型）。</summary>
    private static void SetGroupCols(object group, List<object?> cols)
    {
        SetMember(group, "Cols", ToTypedList(cols, NestedType("SourceCol")));
    }

    /// <summary>构造合成 life 集（LifeRef：Hide/FhY/Y；feetY 写入 Y——FeetY 派生属性 Cy=0 时取 Y）。</summary>
    private static List<object?> MakeLifeRefs(params (bool Hide, int FhY, int FeetY)[] lifes)
    {
        var t = NestedType("LifeRef");
        var result = new List<object?>();
        foreach (var (hide, fhY, feetY) in lifes)
        {
            var l = Activator.CreateInstance(t, nonPublic: true)!;
            SetMember(l, "LifeId", "900000" + result.Count);
            SetMember(l, "Hide", hide);
            SetMember(l, "FhY", fhY);
            SetMember(l, "Y", feetY);
            result.Add(l);
        }
        return result;
    }

    /// <summary>反射读实例字段/属性（嵌套中间态 public field 为主）。</summary>
    private static object? ReadField(object target, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var f = target.GetType().GetField(name, F);
        if (f != null) { return f.GetValue(target); }
        return target.GetType().GetProperty(name, F)?.GetValue(target);
    }

    /// <summary>反射写字段值（测试预置——Score 等实现内赋值字段的直接设定）。</summary>
    private static void ReadFieldSet(object target, string name, object value)
    {
        SetMember(target, name, value);
    }

    /// <summary>反射写字段（field 优先、property 回退）。</summary>
    private static void SetMember(object target, string name, object? value)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var f = target.GetType().GetField(name, F);
        if (f != null) { f.SetValue(target, value); return; }
        target.GetType().GetProperty(name, F)!.SetValue(target, value);
    }

    /// <summary>把 List&lt;object?&gt; 转为目标元素类型的强类型 List（反射构造 + 逐个 Add）。</summary>
    private static object ToTypedList(List<object?> items, Type elementType)
    {
        var typed = Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        var add = typed.GetType().GetMethod("Add")!;
        foreach (var e in items)
        {
            add.Invoke(typed, new[] { e });
        }
        return typed;
    }

    /// <summary>调用 InfiniteRoadGenerator.SurfaceYAt 静态纯函数（v13「保留供测试」公开入口）。</summary>
    private static double? GeneratorSurfaceYAt(IReadOnlyList<RoadColumn> columns, double worldX)
    {
        return (double?)InvokeStatic(
            new[] { "InfiniteRoadGenerator" },
            new[] { "SurfaceYAt" },
            new object?[] { columns, worldX });
    }

    /// <summary>反射读 InfiniteRoadGenerator._recentN（recent 容量公式断言——private readonly 字段）。</summary>
    private static int ReadRecentN(RoadMaterialPack pack)
    {
        var gen = new InfiniteRoadGenerator(pack, startWorldX: 800, startSurfaceY: 300, new Random(1));
        var field = typeof(InfiniteRoadGenerator).GetField("_recentN", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Convert.ToInt32(field!.GetValue(gen));
    }

    /// <summary>
    /// 防重断言：块模板序列中任意连续 window 个块互不相同（recent N = window−1 的行为等价形式）。
    /// </summary>
    private static void AssertNoRepeatWithinWindow(List<ColumnTemplate> blocks, int window)
    {
        Assert.True(blocks.Count > 0, "待断言块序列为空");
        for (int i = 0; i + window <= blocks.Count; i++)
        {
            var slice = blocks.Skip(i).Take(window).ToList();
            Assert.Equal(window, slice.Distinct().Count());
        }
    }

    /// <summary>
    /// 在 MiniPet 程序集 MiniPet.Services.Road 命名空间下按「类型候选 × 方法候选 × 参数个数」
    /// 定位公开静态纯函数并调用。类型/方法未落盘 → 抛带适配提示异常（占位测试 Skip 解除后才会执行到这里）。
    /// </summary>
    private static object? InvokeStatic(string[] typeNames, string[] methodNames, object?[] args)
    {
        foreach (var tn in typeNames)
        {
            var t = Type.GetType("MiniPet.Services.Road." + tn + ", MiniPet");
            if (t == null)
            {
                continue;
            }
            foreach (var mn in methodNames)
            {
                var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(mi => mi.Name == mn && mi.GetParameters().Length == args.Length);
                if (m != null)
                {
                    return m.Invoke(null, args);
                }
            }
        }
        throw new NotSupportedException(
            "待落盘或签名不符：" + string.Join("/", methodNames) + " @ MiniPet.Services.Road." + string.Join("/", typeNames)
            + "——占位测试按 FINAL+算法文档假设签名调用，集成者按真实签名适配后解除 Skip");
    }
}
