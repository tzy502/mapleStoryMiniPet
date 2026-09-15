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
    public void TrackKeyOf_NormalizesImgSuffixAndIgnoresSilence()
    {
        Assert.Equal("Bgm58/The Beginnig of The Adventure", WalkPathPlanner.TrackKeyOf("Bgm58/The Beginnig of The Adventure"));
        Assert.Equal("Bgm00/FloralLife", WalkPathPlanner.TrackKeyOf("Bgm00.img/FloralLife"));
        Assert.Null(WalkPathPlanner.TrackKeyOf("Bgm00/Silence"));   // 静音轨硬编码忽略
    }
}
