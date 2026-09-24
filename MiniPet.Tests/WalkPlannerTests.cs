using Xunit;
using System.Collections.Generic;
using MiniPet.Models;
using MiniPet.Services.Walk;

namespace MiniPet.Tests;

/// <summary>
/// F18 行走与地图切换 —— 规划层单测（`docs/ai/PLAN-F18-行走与地图切换.md`）：
/// 方向选择（累计长度更长者）与「下一个可传送传送门」筛选（目标地图 BGM == 当前曲目）。
/// </summary>
public class WalkPlannerTests
{
    private static MapInfo MapWith(params MapFoothold[] fhs)
    {
        var m = new MapInfo();
        m.Footholds.AddRange(fhs);
        return m;
    }

    [Fact]
    public void PickDirection_HorizontalChain_PicksRight()
    {
        var map = MapWith(
            new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, Next = 2 },
            new MapFoothold { Id = 2, X1 = 100, Y1 = 0, X2 = 300, Y2 = 0, Prev = 1 });

        var (forward, axis, len) = WalkPathPlanner.PickDirection(map, 1);

        Assert.True(forward);
        Assert.Equal(WalkPathPlanner.WalkAxis.Right, axis);
        Assert.Equal(300, len, 1);
    }

    [Fact]
    public void PickDirection_VerticalChain_PicksDown()
    {
        var map = MapWith(
            new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 0, Y2 = 50, Next = 2 },
            new MapFoothold { Id = 2, X1 = 0, Y1 = 50, X2 = 0, Y2 = 400, Prev = 1 });

        var (forward, axis, len) = WalkPathPlanner.PickDirection(map, 1);

        Assert.True(forward);
        Assert.Equal(WalkPathPlanner.WalkAxis.Down, axis);
        Assert.Equal(400, len, 1);
    }

    [Fact]
    public void PickDirection_PicksLongerSide()
    {
        // 右侧 100px、左侧 900px（沿 prev 链）→ 应选 prev 方向（forward=false）
        var map = MapWith(
            new MapFoothold { Id = 1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, Prev = 2 },
            new MapFoothold { Id = 2, X1 = -800, Y1 = 0, X2 = 0, Y2 = 0, Next = 1 });

        var (forward, _, len) = WalkPathPlanner.PickDirection(map, 1);

        Assert.False(forward);
        Assert.Equal(900, len, 1);
    }

    [Fact]
    public void ChooseNextPortal_RequiresTargetMapWithSameBgm()
    {
        var map = new MapInfo();
        map.Portals.Add(new MapPortal { Id = 0, Tm = "101000000", X = 0, Y = 0 });        // 入口
        map.Portals.Add(new MapPortal { Id = 1, Tm = "101000001", X = 100, Y = 0 });      // 同 BGM
        map.Portals.Add(new MapPortal { Id = 2, Tm = "999999999", X = 200, Y = 0 });      // 无目标
        var bgmOf = new Dictionary<string, string>
        {
            ["101000000"] = "Bgm09/DarkShadow",
            ["101000001"] = "Bgm00/FloralLife",
        };
        string Lookup(string id) => bgmOf.TryGetValue(id, out var v) ? v : "";

        var hit = WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup);
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.Id);

        // 目标地图 BGM 与当前曲目不一致 → 认定「没有下一个可传送的门」
        Assert.Null(WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm18/QueensGarden", Lookup));
        Assert.True(WalkPathPlanner.HasNoNextPortal(map, 0, "Bgm18/QueensGarden", Lookup));
    }

    [Fact]
    public void HasNoNextPortal_TrueWhenOnlyOnePortal()
    {
        var map = new MapInfo();
        map.Portals.Add(new MapPortal { Id = 0, Tm = "101000000", X = 0, Y = 0 });

        Assert.True(WalkPathPlanner.HasNoNextPortal(map, 0, "Bgm00/FloralLife", _ => "Bgm00/FloralLife"));
    }

    [Fact]
    public void HasNoNextPortal_FalseWhenSomeNeighborMapSharesBgm()
    {
        // 104000000 实景口径：入口门目标 bgm 不同 → 不算候选；只要有一个邻居与当前曲目同 bgm 就不算 noNext
        var map = new MapInfo();
        map.Portals.Add(new MapPortal { Id = 0, Tm = "104000001", X = 0, Y = 0 });
        map.Portals.Add(new MapPortal { Id = 2, Tm = "104010000", X = 100, Y = 0 });
        var bgmOf = new Dictionary<string, string>
        {
            ["104000001"] = "Bgm02/AboveTheTreetops",
            ["104010000"] = "Bgm00/FloralLife",
        };
        string Lookup(string id) => bgmOf.TryGetValue(id, out var v) ? v : "";

        Assert.False(WalkPathPlanner.HasNoNextPortal(map, 0, "Bgm00/FloralLife", Lookup));
        var hit = WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup);
        Assert.NotNull(hit);
        Assert.Equal(2, hit!.Id);
    }

    [Fact]
    public void GreedyVisits_PrefersUnvisitedMap_AllVisitedMeansNoNext()
    {
        // 胶水 2026-09-15：贪心遍历——优先去本次行走**没去过的图**；全部去过 → 无门可走（退图链路）
        var map = new MapInfo { Id = "100000000" };
        map.Portals.Add(new MapPortal { Id = 1, Tm = "100000200", X = 100, Y = 0 });  // 匹配，已访问
        map.Portals.Add(new MapPortal { Id = 2, Tm = "100000003", X = 200, Y = 0 });  // 匹配，未访问
        string Lookup(string id) => "Bgm00/FloralLife";

        var visited = new HashSet<string> { "100000000", "100000200" };

        // 未访问的 100000003 优先于存储顺序更靠前的 100000200
        var hit = WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup, visited);
        Assert.NotNull(hit);
        Assert.Equal("100000003", hit!.Tm);

        // 全部去过 → 返回 null（触发退图回初始地图），且 HasNoNextPortal 同口径
        visited.Add("100000003");
        Assert.Null(WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup, visited));
        Assert.True(WalkPathPlanner.HasNoNextPortal(map, 0, "Bgm00/FloralLife", Lookup, visited));

        // 不传 visited = 老行为（存储顺序第一个匹配门），保证兼容
        var legacy = WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup);
        Assert.NotNull(legacy);
        Assert.Equal("100000200", legacy!.Tm);
    }

    [Fact]
    public void GreedyVisits_FarthestUnvisitedFirst()
    {
        // 胶水 2026-09-15：多个未访问图 → 取**离当前 x 最远**的门（从远到近覆盖）
        var map = new MapInfo { Id = "100000000" };
        map.Portals.Add(new MapPortal { Id = 1, Tm = "100000200", X = 100, Y = 0 });  // 未访问，近
        map.Portals.Add(new MapPortal { Id = 2, Tm = "100000003", X = 200, Y = 0 });  // 未访问，远
        map.Portals.Add(new MapPortal { Id = 3, Tm = "100000001", X = 150, Y = 0 });  // 未访问，中
        string Lookup(string id) => "Bgm00/FloralLife";

        var visited = new HashSet<string> { "100000000" };

        // 人物在 x=0：最远的是 x=200
        var hit = WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup, visited, 0f);
        Assert.NotNull(hit);
        Assert.Equal("100000003", hit!.Tm);

        // 人物走到 x=190 后：最远的变成 x=100（距离 90 > 200-190=10 > 150-190=40）
        hit = WalkPathPlanner.ChooseNextPortal(map, 0, "Bgm00/FloralLife", Lookup, visited, 190f);
        Assert.NotNull(hit);
        Assert.Equal("100000200", hit!.Tm);
    }

    [Fact]
    public void SameMapPortals_AreNotExits()    {
        // 100000203 爬塔图实景：up01~up07/h001/h002 全部 tm=自己（跳层门），只有 out00 通向别的图。
        // 同图门 BGM 必然匹配（同一张图），若不算出路会让人物永远困在塔里（同图不切挡住切图）。
        var map = new MapInfo { Id = "100000203" };
        map.Portals.Add(new MapPortal { Id = 10, Tm = "100000203", X = 17, Y = 274 });   // 跳层门（自己）
        map.Portals.Add(new MapPortal { Id = 11, Tm = "100000203", X = 9, Y = 63 });     // 跳层门
        map.Portals.Add(new MapPortal { Id = 18, Tm = "100000200", X = 596, Y = 333 });  // 真正的出口

        string Lookup(string id) => "Bgm00/FloralLife";

        // 入口 = 门 10（从上一张图 Tn 匹配进来）→ 跳层门 11 被过滤，唯一候选是出口 18
        var hit = WalkPathPlanner.ChooseNextPortal(map, 10, "Bgm00/FloralLife", Lookup);
        Assert.NotNull(hit);
        Assert.Equal(18, hit!.Id);
        Assert.False(WalkPathPlanner.HasNoNextPortal(map, 10, "Bgm00/FloralLife", Lookup));

        // 全部都是同图门（把出口也改成自己）→ 等价死胡同
        map.Portals[^1] = new MapPortal { Id = 18, Tm = "100000203", X = 596, Y = 333 };
        Assert.True(WalkPathPlanner.HasNoNextPortal(map, 10, "Bgm00/FloralLife", Lookup));
        Assert.Null(WalkPathPlanner.ChooseNextPortal(map, 10, "Bgm00/FloralLife", Lookup));
    }

    [Fact]
    public void TrackKeyOf_NormalizesImgSuffixAndIgnoresSilence()
    {
        Assert.Equal("Bgm58/The Beginnig of The Adventure", WalkPathPlanner.TrackKeyOf("Bgm58/The Beginnig of The Adventure"));
        Assert.Equal("Bgm00/FloralLife", WalkPathPlanner.TrackKeyOf("Bgm00.img/FloralLife"));
        Assert.Null(WalkPathPlanner.TrackKeyOf("Bgm00/Silence"));   // 静音轨硬编码忽略
    }
}
