using Xunit;
using System;
using System.Collections.Generic;
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
        Func<string, string>? mapBgmOf = null)
    {
        return new WalkModeService(null!, null!, null!, null!,
            mapLoader: id => maps.TryGetValue(id, out var m) ? m : null,
            mapBgmOf: mapBgmOf ?? (_ => string.Empty),
            mapPickerOf: _ => startMapId);
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
    public void NoNextPortal_WalksHalfThenTeleportsBackAndFlipsFacing()
    {
        // §6：全图只有一条链（无传送门 → HasNoNextPortal=true），左链 600px → 半程 300px 后瞬移回起点并反向
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

        Assert.Equal(1, svc.Facing);      // 折返后朝向取反
        Assert.Equal(startX, svc.X, 1);   // 瞬移回起点（初始 foothold + 初始 x）
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
                Assert.Equal("rope", svc.CurrentAction);   // l=0 → rope
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
                Assert.Equal("ladder", svc.CurrentAction);   // l=1 → ladder 动画
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

        Assert.True(svc.HalfLen > 0, "不可达应退化到折返（半程阈值 > 0）");
        Assert.Empty(svc.PlannedHops);            // 可达集只有自己 → 无跳
        Assert.Equal(1, svc.Facing);

        // 折返行为：走到半程瞬移回起点并反向（§6，保留既有语义）
        int guard = 0;
        while (svc.Facing == 1 && guard++ < 60)
        {
            svc.Tick(0.5);
        }
        Assert.Equal(-1, svc.Facing);
        Assert.True(Math.Abs(svc.X - 0f) <= 1f, "应瞬移回起点附近");
    }

    [Fact]
    public void JumpHop_PlaysJumpActionDuringTransition_ThenLandsOnTargetFoothold()
    {
        // 要求 2：两条互不相连、水平距离 100（≤110）的段 → WalkGraph 规划 Jump 跳；
        // 过渡期间 CurrentAction == "jump"（不放 walk1/walk2），过渡结束 PlaceOn 到目标段最近端点。
        var map = GraphMap("101000030", 0, 300, 0, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 200, Y1 = 0, X2 = 300, Y2 = 0 });
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        Assert.Equal(100f, svc.X, 1);                 // 中点 (150,0) 吸附 fh1 最近端点 100
        Assert.Single(svc.PlannedHops);
        Assert.Equal(WalkLink.Jump, svc.PlannedHops[0].Link);
        Assert.Equal(2, svc.PlannedHops[0].FootholdId);

        svc.Tick(1.0 / 60);                           // 过渡刚开始
        Assert.Equal("jump", svc.CurrentAction);
        svc.Tick(0.1);                                // 推进插值（0.1s < 0.45s，仍未结束）
        Assert.Equal("jump", svc.CurrentAction);
        Assert.True(svc.X > 100f && svc.X < 200f, "过渡期间应在两点之间插值");
        Assert.Equal(0f, svc.Y, 1);

        svc.Tick(0.5);                                // 过渡结束（0.45s）
        Assert.Equal("walk1", svc.CurrentAction);
        Assert.Equal(200f, svc.X, 1);                 // 落到目标段离当前点最近的端点 (200,0)
        Assert.Equal(0f, svc.Y, 1);
        Assert.Equal(1, svc.HopIndex);
    }

    [Fact]
    public void FallHop_PlaysJumpActionThenLandsBelow_AndHopHelpersArePure()
    {
        // 要求 2：上下两条互不相连的段（垂直 300 > 跳跃上限 77）→ WalkGraph 规划 Fall（单向下落）；
        // 过渡（0.35s）期间 "jump"，结束落到下方段。
        var map = GraphMap("101000040", 0, 100, -300, 0);
        map.Footholds.Add(new MapFoothold { Id = 1, X1 = 0, Y1 = -300, X2 = 100, Y2 = -300 });  // 上层
        map.Footholds.Add(new MapFoothold { Id = 2, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 });         // 下层
        var svc = NewService(new Dictionary<string, MapInfo> { [map.Id] = map }, map.Id);

        svc.Start("Bgm00/FloralLife");
        Assert.Single(svc.PlannedHops);
        Assert.Equal(WalkLink.Fall, svc.PlannedHops[0].Link);
        Assert.Equal(2, svc.PlannedHops[0].FootholdId);

        svc.Tick(1.0 / 60);
        Assert.Equal("jump", svc.CurrentAction);
        svc.Tick(0.1);                                // 推进插值（0.1s < 0.35s，仍未结束）
        Assert.Equal("jump", svc.CurrentAction);
        Assert.True(svc.Y > -300f && svc.Y < 0f, "过渡期间应在两点之间插值");

        svc.Tick(0.5);
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
}
