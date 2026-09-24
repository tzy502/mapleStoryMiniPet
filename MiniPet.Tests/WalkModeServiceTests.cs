using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using MiniPet.Services.Walk;

namespace MiniPet.Tests;

/// <summary>
/// F18 行走与地图切换 —— 状态机单测（`docs/ai/PLAN-F18-行走与地图切换.md` §2~§7）。
/// 单测**不读 WZ**：载图（mapLoader）/ 读目标地图 BGM（mapBgmOf）/ 选起始地图（mapPickerOf）
/// 三个委托走 WalkModeService 的可注入可选参数，四个服务依赖传 null!。
/// </summary>
public class WalkModeServiceTests
{
    /// <summary>构造一张只有 bounds 的空图（MinY/MaxY=0，中点在 x 轴上，便于断言吸附结果）。</summary>
    private static MapInfo NewMap(string id, int minX, int maxX)
    {
        return new MapInfo { Id = id, MinX = minX, MaxX = maxX, MinY = 0, MaxY = 0 };
    }

    private static WalkModeService NewService(IReadOnlyDictionary<string, MapInfo> maps, string startMapId,
        Func<string, string>? mapBgmOf = null,
        Func<string, List<string>?>? trackMapsOrderedOf = null)
    {
        return new WalkModeService(null!, null!, null!, null!,
            mapLoader: id => maps.TryGetValue(id, out var m) ? m : null,
            mapBgmOf: mapBgmOf ?? (_ => string.Empty),
            mapPickerOf: _ => startMapId,
            trackMapsOrderedOf: trackMapsOrderedOf);
    }

    /// <summary>fh1(0..100) Prev=2 + fh2(-900..0) Next=1：左边更长（900）→ 应朝左走。</summary>
    private static MapInfo LeftLongerChain(string id)
    {
        var map = NewMap(id, 0, 100);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, Prev = 2 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = -900, Y1 = 0, X2 = 0, Y2 = 0, Next = 1 });
        return map;
    }

    [Fact]
    public void Start_PicksLongerSide_FacingMatchesPickDirection()
    {
        // §3 选方向：PickDirection 取累计长度更长的一边（此处 prev 链 1000 > next 链 100）→ Facing = -1（左）
        var map = LeftLongerChain("101000001");
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");

        var (forward, axis, _) = WalkPathPlanner.PickDirection(map, 1);
        Assert.False(forward);
        Assert.Equal(WalkPathPlanner.WalkAxis.Right, axis);

        Assert.True(svc.IsActive);
        Assert.Equal(WalkPhase.Walking, svc.Phase);
        Assert.Equal(map.Id, svc.CurrentMapId);
        Assert.Equal(new[] { map.Id }, changed);
        // 起点 = 地图中点 (50,0) 吸附到 fh1 的 x=50
        Assert.Equal(50f, svc.X, 1);
        Assert.Equal(0f, svc.Y, 1);
        Assert.Equal(-1, svc.Facing);

        // §3 行走：CurrentAction 在 walk1 / walk2 之间（0.4s 内先 walk1）
        svc.Tick(1.0 / 30);
        Assert.Equal("walk1", svc.CurrentAction);
        Assert.True(svc.X < 50f);
    }

    [Fact]
    public void NoNextPortal_WalksToEdgeThenTurnsAroundAndFlipsFacing()
    {
        // §6（2026-09-15 掉头走回）：全图只有一条链（无传送门 → HasNoNextPortal=true），
        // 左链 600px → 从 x=50 走到左端 x=-500 尽头 → **原地掉头**反向（不再瞬移回起点）
        var map = NewMap("101000002", 0, 100);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, Prev = 2 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = -500, Y1 = 0, X2 = 0, Y2 = 0, Next = 1 });
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        Assert.True(WalkPathPlanner.HasNoNextPortal(map, -1, "Bgm00/FloralLife", _ => string.Empty));
        svc.Start("Bgm00/FloralLife");
        float startX = svc.X;
        Assert.Equal(-1, svc.Facing);

        svc.Tick(0.5);
        Assert.True(svc.X < startX, "应先朝左走出去");

        int guard = 0;
        while (svc.Facing == -1 && guard++ < 200)
        {
            svc.Tick(0.5);
        }

        Assert.Equal(1, svc.Facing);            // 掉头后朝向取反
        Assert.True(svc.X < startX - 200, "掉头走回：应停在左端尽头附近，而不是瞬移回起点");
        Assert.Equal(0f, svc.Y, 1);
    }

    [Fact]
    public void Stop_SetsInactiveAndRaisesExited()
    {
        var map = LeftLongerChain("101000003");
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        int exited = 0;
        svc.Exited += () => exited++;

        svc.Start("Bgm00/FloralLife");
        Assert.True(svc.IsActive);
        Assert.Equal(0, exited);

        svc.Stop();

        Assert.False(svc.IsActive);
        Assert.Equal(WalkPhase.Idle, svc.Phase);
        Assert.Equal(1, exited);
        Assert.Equal("stand1", svc.CurrentAction);
    }

    [Fact]
    public void Start_NoMapForTrack_ExitsAndStaysIdle()
    {
        // §2：GetFirstMapByReturnMap 返回 null → Exited + Idle
        var svc = new WalkModeService(null!, null!, null!, null!,
            mapLoader: _ => null,
            mapBgmOf: _ => string.Empty,
            mapPickerOf: _ => null);
        int exited = 0;
        svc.Exited += () => exited++;

        svc.Start("Bgm00/NoSuchTrack");

        Assert.False(svc.IsActive);
        Assert.Equal(WalkPhase.Idle, svc.Phase);
        Assert.Equal(1, exited);
    }

    [Fact]
    public void WalkingIntoPortal_SwitchesMapAndRaisesMapChanged()
    {
        // §5：候选门 = 目标地图 info/bgm == 当前曲目 → 走到门前 (|Δx|≤12, |Δy|≤40) 触发切图
        var mapA = NewMap("101000001", 0, 200);
        mapA.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });
        mapA.Portals.Add(new MapPortal { Id = 0, X = 0, Y = 0, PName = "in", Tm = "101000009", Tn = "back" });
        mapA.Portals.Add(new MapPortal { Id = 1, X = 200, Y = 0, PName = "out", Tm = "101000002", Tn = "in" });

        var mapB = NewMap("101000002", 0, 200);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });
        mapB.Portals.Add(new MapPortal { Id = 7, X = 0, Y = 0, PName = "in", Tm = "", Tn = "" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        // 只有目标图 mapB 与当前曲目同 BGM（mapA 自己的门 p0 指向不存在的图，不参与）
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        Assert.Equal(mapA.Id, svc.CurrentMapId);
        Assert.Equal(1, svc.Facing);   // 入口段 fh1 两侧等长，取 forward（向右）→ 朝右走
        // §3 入口段 = 入口传送门吸附的最近段：PickEntryPortal 取最左可跨图门 p0(0,0) → 起点 x=0
        Assert.Equal(0f, svc.X, 1);

        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(0.5);
        }

        Assert.Equal(mapB.Id, svc.CurrentMapId);
        Assert.Equal(new[] { mapA.Id, mapB.Id }, changed);
        Assert.Equal(WalkPhase.Walking, svc.Phase);
        Assert.Equal(0f, svc.X, 1);    // 新图入口门 = Tn("in") 匹配的 q0(x=0)，吸附到 fh1
        Assert.Equal(1, svc.Facing);
    }

    [Fact]
    public void DeadEndFor30s_ReturnsToSessionStartMap()
    {
        // 胶水 2026-09-15：无门死胡同图累计 30s → 切回本次行走的**初始地图**
        var mapA = NewMap("101000061", 0, 200);
        mapA.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });
        mapA.Portals.Add(new MapPortal { Id = 0, X = 0, Y = 0, PName = "in", Tm = "101000099", Tn = "back" }); // 入口（目标不存在，不参与候选）
        mapA.Portals.Add(new MapPortal { Id = 1, X = 200, Y = 0, PName = "out", Tm = "101000062", Tn = "in" });

        // mapB 唯一门目标 101000063 的 BGM 与当前曲不一致 → noNext=True（死胡同）
        var mapB = NewMap("101000062", 0, 200);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });
        mapB.Portals.Add(new MapPortal { Id = 7, X = 0, Y = 0, PName = "in", Tm = "101000063", Tn = "x" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(0.5);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);   // 先穿门到死胡同图 B

        // 胶水 2026-09-15：死胡同**第一圈掉头走回完成**（第二次到尽头）即切回初始图（不等 30s）
        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 120)
        {
            svc.Tick(0.5);
        }
        Assert.Equal(mapA.Id, svc.CurrentMapId);
        Assert.Equal(new[] { mapA.Id, mapB.Id, mapA.Id }, changed);
        Assert.Equal(WalkPhase.Walking, svc.Phase);
    }

    [Fact]
    public void DeadEndTimer_ResetsWhenMapHasUsablePortal()
    {
        // 有门图（noNext=False）上死胡同计时不累计：同图走 40s 不触发回切
        var mapA = NewMap("101000064", 0, 200);
        mapA.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });
        // 门目标存在且 BGM 匹配 → noNext=False
        mapA.Portals.Add(new MapPortal { Id = 1, X = 200, Y = 0, PName = "out", Tm = "101000065", Tn = "in" });

        var mapB = NewMap("101000065", 0, 200);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, _ => "Bgm00/FloralLife");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        for (int i = 0; i < 120; i++)
        {
            svc.Tick(0.5);
        }
        // 全程 noNext=False（真走到门会切到 mapB；无论哪种都不应出现「死胡同回切」以外的第三张图）
        Assert.All(changed, id => Assert.Contains(id, new[] { mapA.Id, mapB.Id }));
    }

    [Fact]
    public void WalksIntoLadderRope_ClimbsToEndAndLandsOnUpperFoothold()
    {
        // §4：走到绳附近（±15px）且朝向绳 → 自动上绳 → 爬到端头脱离，落到上层平台
        var map = NewMap("101000005", 0, 400);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 400, Y2 = 0 });        // 下层
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 400, Y2 = -300 });  // 上层
        map.Ropes.Add(new LadderRope { Id = 1, X = 300, Y1 = -300, Y2 = 0, IsLadder = false });
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        Assert.Equal(200f, svc.X, 1);   // 中点起走，绳在 x=300
        Assert.Equal(1, svc.Facing);

        bool sawClimbing = false;
        int guard = 0;
        while ((svc.Phase != WalkPhase.Walking || svc.Y > -200f) && guard++ < 80)
        {
            svc.Tick(0.5);
            if (svc.Phase == WalkPhase.Climbing)
            {
                sawClimbing = true;
                Assert.True(svc.CurrentAction == "rope" || svc.CurrentAction == "jump",
                    $"Climbing 动作应为 rope（脱离过渡可为 jump），实际 {svc.CurrentAction}");   // l=0 → rope
            }
        }

        Assert.True(sawClimbing, "应自动上绳");
        Assert.Equal(-300f, svc.Y, 1);   // 到端头脱离，落在上层 foothold
        Assert.Equal(WalkPhase.Walking, svc.Phase);
    }

    [Fact]
    public void OnTrackFinished_StopsThenNextTrackStartResumesWalking()
    {
        // §7：歌曲播完 → 立即停（stand1 / 走位不动）→ Crossing 交给窗口层淡入淡出；
        // 下一首由调用方 Start(trackKey) 触发，人物在新图继续走
        var mapA = LeftLongerChain("101000006");
        var mapB = LeftLongerChain("101000007");
        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = new WalkModeService(null!, null!, null!, null!,
            mapLoader: id => maps.TryGetValue(id, out var m) ? m : null,
            mapBgmOf: _ => string.Empty,
            mapPickerOf: key => key == "Bgm01/Next" ? mapB.Id : mapA.Id);
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        svc.Tick(0.5);
        Assert.True(svc.IsActive);

        svc.OnTrackFinished();

        Assert.True(svc.IsActive);                     // 行走模式未退出（Exited 不触发）
        Assert.Equal(WalkPhase.Crossing, svc.Phase);
        Assert.Equal("stand1", svc.CurrentAction);
        svc.Tick(0.5);                                 // 等下一首期间不推进
        Assert.Equal(WalkPhase.Crossing, svc.Phase);

        svc.Start("Bgm01/Next");

        Assert.Equal(mapB.Id, svc.CurrentMapId);
        Assert.Equal(new[] { mapA.Id, mapB.Id }, changed);
        Assert.Equal(WalkPhase.Walking, svc.Phase);
        svc.Tick(0.5);
        Assert.True(svc.X < 50f);                      // 新图继续按方向走
    }

    [Fact]
    public void Pause_KeepsPosition_ResumeWalksAgain()
    {
        var map = NewMap("101000004", 0, 2000);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 2000, Y2 = 0 });
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        svc.Tick(0.5);
        float x = svc.X;
        Assert.True(x > 1000f);

        svc.Pause();
        Assert.Equal("stand1", svc.CurrentAction);
        svc.Tick(0.5);
        Assert.Equal(x, svc.X, 3);

        svc.Resume();
        svc.Tick(0.5);
        Assert.True(svc.X > x);
    }

    // ================= F18 图规划（WalkGraph）改造新增验收 =================

    /// <summary>带真实 bounds 的地图（Jump/Fall/Rope 规划要靠 Min/Max 定中点起点）。</summary>
    private static MapInfo GraphMap(string id, int minX, int maxX, int minY, int maxY)
    {
        return new MapInfo { Id = id, MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY };
    }

    [Fact]
    public void WalkGraph_PlansAcrossDisconnectedChains_ViaLadderRope()
    {
        // 要求 1/2：两条互不相连的链（fh1 在下、fh2 在上；无 prev/next，垂直 300 > 跳跃上限 77），
        // 只由一根梯子连接 —— 纯 prev/next 走法永远到不了。WalkGraph 应规划出 Rope 跳并真的爬上去切图。
        var mapA = GraphMap("101000010", 0, 100, -300, 0);
        mapA.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        mapA.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        mapA.Ropes.Add(new LadderRope { Id = 1, X = 50, Y1 = -300, Y2 = 0, IsLadder = true });
        mapA.Portals.Add(new MapPortal { Id = 0, X = 0, Y = 0, PName = "in", Tm = "101000019", Tn = "x" });       // 入口（bgm 不匹配）
        mapA.Portals.Add(new MapPortal { Id = 1, X = 100, Y = -300, PName = "out", Tm = "101000011", Tn = "in" }); // 候选门

        var mapB = GraphMap("101000011", 0, 100, -300, 0);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        mapB.Portals.Add(new MapPortal { Id = 7, X = 0, Y = -300, PName = "in", Tm = "", Tn = "" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");

        // 入口 = 最左可跨图门 p0(0,0) → 吸附 fh1；目标 = 候选门 p1 吸附的 fh2 → 图规划出一跳 Rope
        Assert.Equal(0f, svc.X, 1);
        Assert.Single(svc.PlannedHops);
        Assert.Equal(WalkLink.Rope, svc.PlannedHops[0].Link);
        Assert.Equal(2, svc.PlannedHops[0].FootholdId);
        Assert.Equal(1, svc.PlannedHops[0].RopeId);

        bool sawLadder = false;
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(0.5);
            if (svc.Phase == WalkPhase.Climbing)
            {
                sawLadder = true;
                Assert.True(svc.CurrentAction == "ladder" || svc.CurrentAction == "jump",
                    $"Climbing 动作应为 ladder（端头脱离过渡可为 jump），实际 {svc.CurrentAction}");
            }
        }

        Assert.True(sawLadder, "应真实攀爬梯子（Rope 跳）");
        Assert.Equal(mapB.Id, svc.CurrentMapId);             // 跨链走到门 → 切图
        Assert.Equal(new[] { mapA.Id, mapB.Id }, changed);
    }

    [Fact]
    public void UnreachableCandidatePortal_FallsBackToFoldbackWithPositiveHalfLen()
    {
        // 要求 1：候选门在另一条不可达的链上（无绳、垂直 300 超跳跃上限）→ Plan 返回 null →
        // 当作没有候选门 → 目标退化为 Farthest（可达集内最远段）、_halfLen > 0（§6 折返）。
        var map = GraphMap("101000020", 0, 100, -300, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        map.Portals.Add(new MapPortal { Id = 0, X = 0, Y = 0, PName = "in", Tm = "101000019", Tn = "x" });       // 入口（bgm 不匹配）
        map.Portals.Add(new MapPortal { Id = 1, X = 100, Y = -300, PName = "out", Tm = "101000021", Tn = "in" }); // 候选门（不可达）

        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id,
            id => id == "101000021" ? "Bgm00/FloralLife" : "Bgm03/Other");

        Assert.Null(WalkGraph.Plan(map, 1, 2));   // 前提：fh1 → fh2 图上不可达

        svc.Start("Bgm00/FloralLife");

        Assert.True(svc.HalfLen > 0, "不可达应退化到折返（掉头阈值 > 0）");
        Assert.Empty(svc.PlannedHops);            // 可达集只有自己 → 无跳
        Assert.Equal(1, svc.Facing);

        // 折返行为（2026-09-15 掉头走回）：走到该方向尽头（x=100 贴边）→ 原地掉头反向
        int guard = 0;
        while (svc.Facing == 1 && guard++ < 60)
        {
            svc.Tick(0.5);
        }
        Assert.Equal(-1, svc.Facing);
        Assert.True(svc.X > 90f, "掉头走回：应在尽头（x≈100）掉头，而不是瞬移回起点");
    }

    [Fact]
    public void JumpHop_PlaysJumpActionDuringTransition_ThenLandsOnTargetFoothold()
    {
        // 要求 2（2026-09-15 物理跳跃）：缺口 60（≤ sdlMS 同高跳距 125×0.555≈69）→ Jump 边；
        // 过渡期间 "jump"，真实抛物线（中途 Y < 0 升空），落到目标段（落点由物理决定，x≈169）。
        var map = GraphMap("101000030", 0, 200, 0, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 160, Y1 = 0, X2 = 260, Y2 = 0 });
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        Assert.Equal(100f, svc.X, 1);                 // 中点 (150,0) 吸附 fh1 最近端点 100
        Assert.Single(svc.PlannedHops);
        Assert.Equal(WalkLink.Jump, svc.PlannedHops[0].Link);
        Assert.Equal(2, svc.PlannedHops[0].FootholdId);

        svc.Tick(1.0 / 60);                           // 起跳
        Assert.Equal("jump", svc.CurrentAction);
        svc.Tick(0.1);                                // 空中（滞空 0.555s，未落地）
        Assert.Equal("jump", svc.CurrentAction);
        Assert.True(svc.X > 100f && svc.X < 270f, "空中应按 vx=125 前移");
        Assert.True(svc.Y < 0f, "物理跳跃中途应在起跳线之上（Y<0）");

        svc.Tick(0.5);                                // 累计 > 0.555s → 落地
        Assert.Equal("walk1", svc.CurrentAction);
        Assert.InRange(svc.X, 150f, 270f);            // 落在目标段上（物理落点 ≈169）
        Assert.Equal(0f, svc.Y, 1);
        Assert.Equal(1, svc.HopIndex);
    }

    [Fact]
    public void FallHop_PlaysJumpActionThenLandsBelow_AndHopHelpersArePure()
    {
        // 要求 2（物理下坠）：上下两段（垂直 300 > 77）→ Fall；重力加速 √(2·300/2000)≈0.55s 落到下层；
        // 空中水平保持 vx=125（按住方向键），下层段加宽覆盖落点。
        var map = GraphMap("101000040", 0, 100, -300, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });  // 上层
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = -100, Y1 = 0, X2 = 300, Y2 = 0 });     // 下层（加宽）
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        Assert.Single(svc.PlannedHops);
        Assert.Equal(WalkLink.Fall, svc.PlannedHops[0].Link);
        Assert.Equal(2, svc.PlannedHops[0].FootholdId);

        svc.Tick(1.0 / 60);
        Assert.Equal("jump", svc.CurrentAction);
        svc.Tick(0.1);                                // 空中加速下落（0.117s < 0.55s，未落地）
        Assert.Equal("jump", svc.CurrentAction);
        Assert.True(svc.Y > -300f && svc.Y < 0f, "下坠中途应在两层之间");

        svc.Tick(0.6);                                // 累计 > 0.55s → 落到下层
        Assert.Equal("walk1", svc.CurrentAction);
        Assert.Equal(0f, svc.Y, 1);                   // 落到下层段

        // 要求 4：纯函数边界可单独断言
        // ① 同链相邻段的 Jump 只是图的捷径 → 归一化成「走」（否则会沿连续平台连跳）
        var chain = GraphMap("101000041", 0, 200, 0, 0);
        chain.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, Next = 2 });
        chain.Footholds.Add(new MapFoothold { Id = 2, X1 = 100, Y1 = 0, X2 = 200, Y2 = 0, Prev = 1 });
        Assert.Equal(WalkLink.Adjacent, WalkModeService.ResolveLink(chain, 1, new WalkHop(2, WalkLink.Jump, 0, 20)));
        Assert.Equal(WalkLink.Fall, WalkModeService.ResolveLink(chain, 1, new WalkHop(9, WalkLink.Fall, 0, 30)));
        // ② 下标推进：当前段正好是第 index 跳的目标 → 推进到下一个未完成的跳
        var hops = new List<WalkHop> { new(2, WalkLink.Adjacent, 0, 100), new(3, WalkLink.Adjacent, 0, 100) };
        Assert.Equal(1, WalkModeService.NextHopIndex(hops, 0, 2));
        Assert.Equal(2, WalkModeService.NextHopIndex(hops, 1, 3));
        Assert.Equal(0, WalkModeService.NextHopIndex(hops, 0, 7));
    }

    // ================= P0 瞬移修复：半程口径（§6 全程 = 走到「可行走路径尽头」的累计长度） =================

    /// <summary>
    /// 复刻 104000000 的碎片化结构：起点链（prev/next，1000px）+ 100px 缺口（Jump 捷径边）+ 缺口段 +
    /// 只能下落的地面。旧口径 half = max(Dijkstra 代价/2≈358, 起点单链长/2=500) → 约 5 秒一瞬移；
    /// 新口径全程 = 起点到面向方向「尽头」的水平跨度 1950 → 半程 975。
    /// </summary>
    private static MapInfo FragmentedMap(string id)
    {
        var map = GraphMap(id, 0, 1000, 0, 80);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 600, Y2 = 0, Next = 2 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 600, Y1 = 0, X2 = 1000, Y2 = 0, Prev = 1 });
        map.Footholds.Add(new MapFoothold { Id = 3, X1 = 1060, Y1 = 0, X2 = 1460, Y2 = 0 });    // 缺口 60（≤物理跳距 69）→ Jump
        map.Footholds.Add(new MapFoothold { Id = 4, X1 = 1060, Y1 = 80, X2 = 3460, Y2 = 80 });  // 尽头所在地面 → Fall
        return map;
    }

    [Fact]
    public void NoNextPortal_HalfCoversFullWalkableSpan_NotChainOrGraphCost()
    {
        var map = FragmentedMap("101000050");

        // 纯函数口径：尽头 = fh4（center 2260）；全程 = max(水平跨度 1960, 位移累计 540) = 1960
        // （2026-09-16 物理跳距后：fh2→fh4 的 60px 缺口 + 80px 落差一跳可达（JumpReach(80)≈92px），
        //  Dijkstra 最短路变为 2 跳，不再经 fh3）
        var route = new List<WalkHop>();
        double full = WalkGraph.RouteToEnd(map, 1, 1, route);
        Assert.Equal(1960, full, 1);
        Assert.Equal(2, route.Count);
        // route[0]（fh1→fh2 同链相邻）：Dijkstra 可能取代价 ~20 的跳跃捷径边，运行时归一化成走，这里只认段
        Assert.Equal(2, route[0].FootholdId);
        Assert.Equal(4, route[1].FootholdId);
        // 60px 缺口 + 80px 落差：物理上是「跳下去」→ Jump（Fall 仅用于垂直走出边缘）
        Assert.Equal(WalkLink.Jump, route[1].Link);
        var planned = WalkGraph.Plan(map, 1, 4);
        Assert.NotNull(planned);
        Assert.Equal(500, WalkGraph.TraverseLength(map, 1, planned!), 1);   // 走 400 + 跳（端点距 √(60²+80²)=100）

        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        svc.Start("Bgm00/FloralLife");

        Assert.Equal(1, svc.Facing);            // 右侧全程 1960 > 左侧 0 → 朝右
        Assert.Equal(1960, svc.HalfLen, 1);     // 掉头阈值 = 全程（2026-09-15 掉头走回；旧半程口径 975 已废弃）
        // 腿目标 = 覆盖口径 FarthestUnvisited：fh3/fh4 图距并列（同为一次跳跃边 540）→ 按水平延伸裁决选 fh4
        //（2026-09-18 尽头自洽修复；旧字典序裁决随机停在 fh3 → HalfLen 少算一半成 960）
        Assert.Equal(2, svc.PlannedHops.Count);
        Assert.Equal(2, svc.PlannedHops[0].FootholdId);
        Assert.Equal(4, svc.PlannedHops[1].FootholdId);
        Assert.Equal(WalkLink.Jump, svc.PlannedHops[1].Link);
    }

    [Fact]
    public void NoNextPortal_WalksToEdgeThenTurnsAroundAndReplans()
    {
        var map = FragmentedMap("101000057");
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        svc.Start("Bgm00/FloralLife");
        float startX = svc.X;                   // 地图中心 (500,40) 吸附 fh1 → x=500

        int guard = 0;
        while (svc.Facing == 1 && guard++ < 400)
        {
            svc.Tick(0.5);
        }

        // 2026-09-15 掉头走回：走到右端尽头（fh4 右端 x=3460, y=80）才原地掉头，不再瞬移回起点
        Assert.Equal(-1, svc.Facing);
        Assert.True(svc.X > 3000f, $"掉头走回：应在尽头附近掉头（实际 x={svc.X}，起点 {startX}）");
        Assert.Equal(80f, svc.Y, 1);
    }

    [Fact]
    public void RouteToEnd_VerticalRopeMap_FullLengthFromTraverseNotSpan()
    {
        // 纯垂直图（水平跨度 0）：掉头阈值退化为路径位移累计（爬绳 300 全程，不再 /2）
        var map = GraphMap("101000051", 0, 100, -300, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        map.Ropes.Add(new LadderRope { Id = 1, X = 50, Y1 = -300, Y2 = 0 });

        var route = new List<WalkHop>();
        double full = WalkGraph.RouteToEnd(map, 1, 1, route);
        Assert.Equal(300, full, 1);
        Assert.Single(route);
        Assert.Equal(WalkLink.Rope, route[0].Link);

        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        svc.Start("Bgm00/FloralLife");
        Assert.Equal(300, svc.HalfLen, 1);
        Assert.Equal(WalkLink.Rope, svc.PlannedHops[0].Link);
    }

    // ================= 绳梯修复（F1a 吸附同包络 / F1b 超时降级 / F4 兜底收窄） =================

    [Fact]
    public void WalkGraph_RopeAttach_MatchesRuntimeEnvelope()
    {
        // 够得着（绳端 x±15 / y±20 内有段）→ 规划出绳跳
        var ok = GraphMap("101000052", 0, 100, -300, 0);
        ok.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        ok.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        ok.Ropes.Add(new LadderRope { Id = 1, X = 50, Y1 = -300, Y2 = 0 });
        var okHop = WalkGraph.Plan(ok, 1, 2);
        Assert.NotNull(okHop);
        Assert.Equal(WalkLink.Rope, okHop![0].Link);

        // 101000000 rope8 同病：绳底悬空 55px（>±20 窗）→ 不得建边（旧口径 ≤150px 会建 → 绳下徘徊死锁）
        var gap = GraphMap("101000053", 0, 100, -300, 0);
        gap.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        gap.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        gap.Ropes.Add(new LadderRope { Id = 1, X = 50, Y1 = -300, Y2 = -55 });
        Assert.Null(WalkGraph.Plan(gap, 1, 2));

        // 绳悬在平台外侧 30px（x 超出 ±15 窗；点到段距离仅 ~30，旧口径 150 会建）→ 不得建边
        var side = GraphMap("101000054", 0, 100, -300, 0);
        side.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        side.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });
        side.Ropes.Add(new LadderRope { Id = 1, X = 130, Y1 = -300, Y2 = 0 });
        Assert.Null(WalkGraph.Plan(side, 1, 2));
    }

    [Fact]
    public void RopeHop_StuckAtUnreachableY_TimesOutAndSkipsHop()
    {
        var map = GraphMap("101000055", 0, 200, -300, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = -300, X2 = 200, Y2 = -300 });
        map.Ropes.Add(new LadderRope { Id = 1, X = 100, Y1 = -300, Y2 = 0 });

        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        svc.Start("Bgm00/FloralLife");
        Assert.Single(svc.PlannedHops);     // 原绳可达 → 绳跳已规划

        // 运行期绳变了（绳底上收到 -200）：站立面 y=0 落在运行时窗口 [-320,-180] 之外 → 永远够不到
        map.Ropes[0].Y2 = -200;

        int guard = 0;
        while (svc.HopIndex < 1 && guard++ < 120)
        {
            svc.Tick(0.5);
        }

        Assert.True(svc.HopIndex >= 1, "F1b 超时后应降级跳过绳跳");
        Assert.Equal(WalkPhase.Walking, svc.Phase);   // 不该挂在绳上下不来
    }

    [Fact]
    public void FallbackRopeGrab_NeedsBottomNearFootOrRopeThroughPlatform()
    {
        // 绳悬在平台右外侧 10px、绳底比站立面低 50px：RopeNearby 的窗口命中，
        // 但绳底不在脚下 ±20 内、也不穿过脚下平台 → 兜底不得顺手抓（旧代码会抓上去）
        var map = GraphMap("101000056", 0, 100, -200, 50);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        map.Ropes.Add(new LadderRope { Id = 1, X = 110, Y1 = -200, Y2 = 50 });

        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);
        svc.Start("Bgm00/FloralLife");

        int guard = 0;
        while (guard++ < 60 && svc.Phase != WalkPhase.Climbing)
        {
            svc.Tick(0.5);
        }

        Assert.NotEqual(WalkPhase.Climbing, svc.Phase);
        Assert.True(svc.X > -1f && svc.X < 101f, "应仍在原平台上往返，没有被绳吸走");
    }

    // ================= 死胡同覆盖语义（胶水 2026-09-15 规则 1/3/4 + 连环快切修复） =================
    //
    // 规则 3：死胡同图退出条件 = **两个方向各一次「完整单程」**（真尽头 blocked 且与上次计入
    // blocked 的水平位移 ≥ max(本腿全长/3, 300px)）**且总停留 ≥ 20s**；单程位移不足的 blocked
    //（梯子端点、小缺口边）只掉头不计趟。物理依据：跳跃落差可下跳、上跳受 77px 限 →
    // 「从左到右走不了但从右到左可以」的单向缺口，两方向各走一遍才互补覆盖全图。
    // 规则 4：退图目标 = 曲目候选图排序列表中当前图之后的下一个；到尾回绕；不可用回退来源图。

    /// <summary>构造 Start 图 A + 指向 mapBId 的候选门（B 的 BGM 匹配曲目）；A 自身的 in 门指向不存在图。</summary>
    private static MapInfo PortalMap(string id, string mapBId, int width = 200)
    {
        var map = NewMap(id, 0, width);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = width, Y2 = 0 });
        map.Portals.Add(new MapPortal { Id = 0, X = 0, Y = 0, PName = "in", Tm = id + "9", Tn = "x" });
        map.Portals.Add(new MapPortal { Id = 1, X = width, Y = 0, PName = "out", Tm = mapBId, Tn = "in" });
        return map;
    }

    /// <summary>构造单段死胡同图（唯一门的 tm 指向不存在图 → BGM 不匹配 → noNext=true），入口在 entryX。</summary>
    private static MapInfo DeadEndMap(string id, int width, int entryX)
    {
        var map = NewMap(id, 0, width);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = width, Y2 = 0 });
        map.Portals.Add(new MapPortal { Id = 7, X = entryX, Y = 0, PName = "in", Tm = id + "8", Tn = "" });
        return map;
    }

    [Fact]
    public void DeadEnd_ExitNeedsTwoFullLegs_AndCoversBothEnds()
    {
        // 规则 3：两方向各一次完整单程 + 停留 ≥20s 才退图；退图前两个尽头都必须走到过。
        // B = 900px 链（-700..200），入口在中部 x=100：每趟位移 ≥300 阈值。
        var mapA = PortalMap("6001", "6002");
        var mapB = NewMap("6002", -700, 200);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = -700, Y1 = 0, X2 = 0, Y2 = 0, Next = 2 });
        mapB.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = 0, X2 = 200, Y2 = 0, Prev = 1 });
        mapB.Portals.Add(new MapPortal { Id = 7, X = 100, Y = 0, PName = "in", Tm = "60028", Tn = "" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);

        float minX = float.MaxValue, maxX = float.MinValue;
        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 80)
        {
            svc.Tick(1.0);
            minX = Math.Min(minX, svc.X);
            maxX = Math.Max(maxX, svc.X);
        }

        Assert.Equal(mapA.Id, svc.CurrentMapId);   // catalog=null → 回退来源图 A
        Assert.Equal(new[] { mapA.Id, mapB.Id, mapA.Id }, changed);
        Assert.True(minX <= -690f, $"第一遍应走到左尽头（minX={minX}）");
        Assert.True(maxX >= 190f, $"反方向一遍应走到右尽头（maxX={maxX}）");
    }

    [Fact]
    public void DeadEnd_SmallMap_ShortBlockedDoesNotQuickExit()
    {
        // 连环快切修复（胶水 2026-09-15 实跑诊断）：短图/梯子端点的连续 blocked（位移不足）
        // 只掉头不计趟 —— 300px 小图进入 10s 时必须仍在死胡同图（旧代码 ~5s 两次 blocked 就退回，
        // 5 秒连切 4 张图）；停留攒够 20s 后才退图回来源图。
        var mapA = PortalMap("6101", "6102");
        var mapB = DeadEndMap("6102", 300, 150);

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(0.5);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);

        for (int i = 0; i < 20; i++)   // 10s：不得秒退
        {
            svc.Tick(0.5);
            Assert.True(svc.CurrentMapId == mapB.Id, $"t≈{i * 0.5:F1}s 就退图 = 秒退回归");
        }

        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 200)
        {
            svc.Tick(0.5);
        }
        Assert.Equal(mapA.Id, svc.CurrentMapId);   // 停留足够后正常退图（不永久困住）
    }

    [Fact]
    public void DeadEnd_ExitTargetIsNextInOrderedList()
    {
        // 规则 4：退图目标 = GetTrackMapsOrdered 列表中当前图之后的下一个非死胡同非当前图。
        var mapA = PortalMap("6201", "6202");
        var mapB = DeadEndMap("6202", 600, 0);
        var mapC = DeadEndMap("6203", 200, 0);

        var maps = new Dictionary<string, MapInfo>
        {
            [mapA.Id] = mapA, [mapB.Id] = mapB, [mapC.Id] = mapC,
        };
        var svc = NewService(maps, mapA.Id,
            id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other",
            trackMapsOrderedOf: _ => new List<string> { "6202", "6203" });
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);

        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 80)
        {
            svc.Tick(1.0);
        }

        // B 走完两遍后退到排序列表的下一个 = C（而不是旧逻辑的来源图 A）
        Assert.Equal(mapC.Id, svc.CurrentMapId);
        Assert.Equal(new[] { mapA.Id, mapB.Id, mapC.Id }, changed);
    }

    [Fact]
    public void DeadEnd_OrderedListWraps_SkipsDeadEnded_FallsBackWhenExhausted()
    {
        // 规则 4 回绕：从列表尾的 C 退图 → 回绕找 B（已是死胡同，跳过）→ 无合格者
        // → 回退既有逻辑（来源图 A），不死锁。
        var mapA = PortalMap("6301", "6302");
        var mapB = DeadEndMap("6302", 600, 0);
        var mapC = DeadEndMap("6303", 600, 0);

        var maps = new Dictionary<string, MapInfo>
        {
            [mapA.Id] = mapA, [mapB.Id] = mapB, [mapC.Id] = mapC,
        };
        var svc = NewService(maps, mapA.Id,
            id => id == mapB.Id || id == mapC.Id ? "Bgm00/FloralLife" : "Bgm03/Other",
            trackMapsOrderedOf: _ => new List<string> { "6302", "6303" });
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);

        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 80)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapC.Id, svc.CurrentMapId);   // 规则 4：B 的下一个 = C

        guard = 0;
        while (svc.CurrentMapId == mapC.Id && guard++ < 80)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapA.Id, svc.CurrentMapId);   // 回绕无合格者 → 回退来源图 A
        Assert.Equal(new[] { mapA.Id, mapB.Id, mapC.Id, mapA.Id }, changed);
    }

    [Fact]
    public void DeadEnd_ExitFallsBackToCameFrom_WhenOrderedListUnavailable()
    {
        // catalog 为 null（本测试全套注入 null）/ 有序列表为空 → 回退既有逻辑：来源图优先。
        var mapA = PortalMap("6401", "6402");
        var mapB = DeadEndMap("6402", 600, 0);

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id,
            id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other",
            trackMapsOrderedOf: _ => new List<string>());
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);

        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 80)
        {
            svc.Tick(1.0);
        }

        Assert.Equal(mapA.Id, svc.CurrentMapId);
        Assert.Equal(new[] { mapA.Id, mapB.Id, mapA.Id }, changed);
    }

    [Fact]
    public void DeadEnd_OneWayGap_FallIsTaken_BothEndsCoveredBeforeExit()
    {
        // 规则 3 单向性（地球防御总部式）：落差 300px 可下跳、上跳不可能（>77px 限）→ 单向缺口。
        // 覆盖模式必须把计划内的 Fall 走掉、下跳后继续走到底层尽头；两方向各完整单程后才退图，
        // 不允许在缺口边中途 blocked 就计趟退图（「只走了一部分就退图」病灶）。
        var mapA = PortalMap("6501", "6502");
        var mapB = new MapInfo { Id = "6502", MinX = 100, MaxX = 1200, MinY = -300, MaxY = 400 };
        mapB.Footholds.Add(new MapFoothold { Id = 10, X1 = 0, Y1 = 0, X2 = 300, Y2 = 0 });      // 上层
        mapB.Footholds.Add(new MapFoothold { Id = 11, X1 = 100, Y1 = 300, X2 = 1200, Y2 = 300 }); // 下层（唯一回家路是单向落）
        mapB.Portals.Add(new MapPortal { Id = 7, X = 0, Y = 0, PName = "in", Tm = "65028", Tn = "" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        int guard = 0;
        while (svc.CurrentMapId == mapA.Id && guard++ < 40)
        {
            svc.Tick(1.0);
        }
        Assert.Equal(mapB.Id, svc.CurrentMapId);

        float maxX = float.MinValue;
        guard = 0;
        while (svc.CurrentMapId == mapB.Id && guard++ < 80)
        {
            svc.Tick(1.0);
            maxX = Math.Max(maxX, svc.X);
        }

        Assert.Equal(mapA.Id, svc.CurrentMapId);
        Assert.Equal(new[] { mapA.Id, mapB.Id, mapA.Id }, changed);
        Assert.True(maxX >= 1190f, $"下跳后应覆盖底层到右尽头（maxX={maxX}），而不是在缺口边中途退图");
    }

    // ================= 同图传送门（SleepyWood 706030400 树图型：Warp hop 执行） =================
    //
    // up00/down00 门 tm=自己、tn 互指（图内传送：树下↔树顶）。执行语义：
    //   沿链走到门口（门吸附段 + 门口 x 判定带）→ PlaceOn 到目标门吸附段 → 重规划本腿；
    //   绝不当物理跳（传送目标物理跳不可达）；_usedSelfPortals 防乒乓（每图访问每对门一次）。

    /// <summary>
    /// 复刻 706030400 的分层结构：底层 fh10(0..150)+fh11(150..350)、上层 fh20(0..150)+fh21(150..550)，
    /// 上下层无绳无跳边（落差 300 &gt; 77），唯一上行通道 = 同图门对 up00(300,0)↔down00(20,-300)。
    /// </summary>
    private static MapInfo TreeMap(string id, string? outTargetMapId = null)
    {
        var map = GraphMap(id, 0, 550, -300, 0);
        map.Footholds.Add(new MapFoothold { Id = 10, X1 = 0, Y1 = 0, X2 = 150, Y2 = 0, Next = 11 });
        map.Footholds.Add(new MapFoothold { Id = 11, X1 = 150, Y1 = 0, X2 = 350, Y2 = 0, Prev = 10 });
        map.Footholds.Add(new MapFoothold { Id = 20, X1 = 0, Y1 = -300, X2 = 150, Y2 = -300, Next = 21 });
        map.Footholds.Add(new MapFoothold { Id = 21, X1 = 150, Y1 = -300, X2 = 550, Y2 = -300, Prev = 20 });
        map.Portals.Add(new MapPortal { Id = 0, X = 0, Y = 0, PName = "in0", Tm = id + "9", Tn = "x" });   // 入口（目标不存在）
        map.Portals.Add(new MapPortal { Id = 1, X = 300, Y = 0, PName = "up00", Tm = id, Tn = "down00" });
        map.Portals.Add(new MapPortal { Id = 2, X = 20, Y = -300, PName = "down00", Tm = id, Tn = "up00" });
        if (outTargetMapId != null)
        {
            map.Portals.Add(new MapPortal { Id = 3, X = 530, Y = -300, PName = "out", Tm = outTargetMapId, Tn = "in" });
        }
        return map;
    }

    [Fact]
    public void WalkGraph_SelfWarpEdges_TagEachDirectionWithItsOwnSourceDoor()
    {
        // 反向边必须标「站在 dest 门」：旧 bug 两方向都标 -up00.Id → 反向执行解析回脚下那扇门
        // 原地空传、整对门被锁进已用集合（down00→up00 方向永不可用）。
        var map = TreeMap("706030700");

        var up = WalkGraph.Plan(map, 10, 20);      // 底层入口段 → 上层 down00 吸附段
        Assert.NotNull(up);
        Assert.Equal(2, up!.Count);
        Assert.Equal(WalkLink.Warp, up[1].Link);
        Assert.Equal(-1, up[1].RopeId);            // 站 up00 → 传去 down00
        Assert.Equal(11, up[1].FromFootholdId);    // 门口段 = up00 吸附段

        var down = WalkGraph.Plan(map, 20, 11);    // 反向：上层 → 底层 fh11
        Assert.NotNull(down);
        Assert.Single(down!);
        Assert.Equal(WalkLink.Warp, down[0].Link);
        Assert.Equal(-2, down[0].RopeId);          // 站 down00 → 传回 up00（旧 bug 于此标成 -1）
    }

    [Fact]
    public void SelfWarpHop_WalksToDoorThenTeleportsToTargetDoorSegment()
    {
        // 路径含 Warp 跳：执行必须「普通行走引导到门口段 + 走进门口判定带」才传送（PlaceOn 瞬移到
        // 目标门吸附段），绝不当物理跳、也不允许在门口段边缘（x=150 一进段就传）提前触发。
        var mapA = TreeMap("706030600", "706030601");
        var mapB = GraphMap("706030601", 0, 400, 0, 0);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 400, Y2 = 0 });
        mapB.Portals.Add(new MapPortal { Id = 7, X = 0, Y = 0, PName = "in", Tm = "", Tn = "" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id, id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other");
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");
        Assert.Equal(0f, svc.X, 1);                        // 入口 = in0(0,0) 吸附 fh10
        Assert.Equal(3, svc.PlannedHops.Count);            // 走到 fh11 → Warp 上层 → 走到 out 段
        Assert.Equal(WalkLink.Warp, svc.PlannedHops[1].Link);
        Assert.Equal(20, svc.PlannedHops[1].FootholdId);
        Assert.Equal(-1, svc.PlannedHops[1].RopeId);

        bool warped = false;
        int guard = 0;
        float prevX = svc.X, prevY = svc.Y;
        while (svc.CurrentMapId == mapA.Id && guard++ < 400)
        {
            svc.Tick(1.0 / 30);
            if (svc.Y < prevY - 200f)
            {
                warped = true;
                // 传送前一帧必须已走到门口判定带附近（|X-300|≤12），而不是门口段边缘 x=150
                Assert.True(prevX >= 280f, $"传送前应在门口附近（prevX={prevX}），而不是一进门口段就传");
                // PlaceOn 到目标门 down00(20,-300) 吸附段，且本子步即停（无漂移、非抛物线落点）
                Assert.Equal(20f, svc.X, 1);
                Assert.Equal(-300f, svc.Y, 1);
                Assert.Equal(WalkPhase.Walking, svc.Phase);
            }
            prevX = svc.X;
            prevY = svc.Y;
        }

        Assert.True(warped, "应发生同图传送（一帧内 Y 瞬变 >200，跳跃/坠落做不到）");
        Assert.Equal(mapB.Id, svc.CurrentMapId);           // 传送后沿上层走到候选门 out → 切图
        Assert.Equal(new[] { mapA.Id, mapB.Id }, changed);
    }

    [Fact]
    public void SelfWarp_PingPongGuard_CooldownBlocksRapidReuse_AllowsReturnAfterInterval()
    {
        // 无跨图候选 → 死胡同覆盖模式。防护语义 = 同一条传送边 10s 冷却（双向同标）：
        // ① 冷却期内同门/反向都不再传（无防护时「传送→重规划又选同边」会钉死门口连传）；
        // ② 冷却到期后允许再传（上层走完下得来，706030400 实测永久封禁只覆盖单侧）。
        // 26s 窗口内向上瞬传恰好 2 次、间隔 ≥10s（坠落/下落瞬移是向下，不计入口径）。
        var map = TreeMap("706030500");
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        Assert.True(svc.PlannedHops.Any(h => h.Link == WalkLink.Warp), "覆盖腿规划应含传送边");

        var warpTimes = new List<double>();
        float minY = 0f;
        float prevY = svc.Y;
        double t = 0;
        for (int i = 0; i < 26 * 30; i++)
        {
            svc.Tick(1.0 / 30);
            t += 1.0 / 30;
            if (svc.Y - prevY < -200f) { warpTimes.Add(t); }
            prevY = svc.Y;
            minY = Math.Min(minY, svc.Y);
        }

        Assert.True(minY <= -299f, "应已传送到上层（y=-300）");
        Assert.True(warpTimes.Count >= 2, "冷却到期后应允许再次传送（永久封禁会 =1，上层走完下不来）");
        for (int i = 1; i < warpTimes.Count; i++)
        {
            Assert.True(warpTimes[i] - warpTimes[i - 1] >= 9.5,
                $"第 {i} 次与第 {i + 1} 次传送间隔 {warpTimes[i] - warpTimes[i - 1]:F1}s 应 ≥ 冷却 10s（无同门连续传送）");
        }
    }

    [Fact]
    public void SelfWarp_UsedPortalsClearOnMapReentry_SecondVisitTeleportsAgain()
    {
        // 进图清空 _usedSelfPortals 与传送冷却表（门 id 各图复用）：A 传送上层→走到 out 切 B；
        // B 无候选 → 死胡同覆盖后回退来源图 A；A 第二次进图必须能再次传送（向上瞬传共 2 次）
        // ——不清空则第二次被上一张图的冷却/已用集合挡住（=1）。
        var mapA = TreeMap("706030600", "706030601");
        var mapB = GraphMap("706030601", 0, 400, 0, 0);
        mapB.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 400, Y2 = 0 });
        mapB.Portals.Add(new MapPortal { Id = 7, X = 0, Y = 0, PName = "in", Tm = "706030600", Tn = "in0" });

        var maps = new Dictionary<string, MapInfo> { [mapA.Id] = mapA, [mapB.Id] = mapB };
        var svc = NewService(maps, mapA.Id,
            id => id == mapB.Id ? "Bgm00/FloralLife" : "Bgm03/Other",
            trackMapsOrderedOf: _ => null);   // 有序列表不可用 → 退图回退来源图 A
        var changed = new List<string>();
        svc.MapChanged += id => changed.Add(id);

        svc.Start("Bgm00/FloralLife");

        int upWarpsOnA = 0;
        float prevY = svc.Y;
        int guard = 0;
        while (changed.Count < 4 && guard++ < 400)
        {
            svc.Tick(0.25);
            if (svc.CurrentMapId == mapA.Id && svc.Y - prevY < -200f) { upWarpsOnA++; }
            prevY = svc.Y;
        }

        Assert.Equal(new[] { mapA.Id, mapB.Id, mapA.Id, mapB.Id }, changed);
        Assert.Equal(2, upWarpsOnA);   // 每次进图各传送一次（第二次证明已用集合被进图清空）
    }
}
