using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Tests
{
    /// <summary>
    /// 曲目↔地图反向索引回归（2026-09-15 P0 修复）：
    /// - 根因一：AccumulateTrackMap 对**首条**记录（store miss → slice == null）直接解引用
    ///   slice.Items → NullReferenceException，扫描任务开局即崩、从未产出任何数据
    ///   （实测表现：--bgmdiag 90s 全 (null)、磁盘缓存从未落盘）。
    /// - 根因二：存储 key 带 .img（"Bgm00.img/FloralLife"）而查询 key 不带（"Bgm00/FloralLife"，
    ///   WalkPathPlanner.TrackKeyOf 口径）→ 即使扫完也 100% miss。
    /// 本文件锁死这两个坑 + 5 名封顶/票数聚合口径。
    /// </summary>
    public class MusicReverseIndexTests
    {
        private static TrackMapRef Entry(string mapId, string? name = null, bool town = false, string? ret = null)
            => new TrackMapRef(mapId, name, town, ret);

        // ---------- 根因一回归：首条记录不得 NRE ----------

        [Fact]
        public void AccumulateTrackMap_EmptyStore_FirstEntry_DoesNotThrowAndIsStored()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            var entry = Entry("100000200", "射手公园", town: true);

            var ex = Record.Exception(() =>
                MusicCatalogService.AccumulateTrackMap(store, "Bgm00/FloralLife", entry, "706020100"));

            Assert.Null(ex);
            Assert.True(store.ContainsKey("Bgm00/FloralLife"));
            Assert.Single(store["Bgm00/FloralLife"].Items);
            Assert.Equal("100000200", store["Bgm00/FloralLife"].Items[0].MapId);
        }

        [Fact]
        public void AccumulateTrackMap_SecondTrackFirstEntry_AlsoFine()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            MusicCatalogService.AccumulateTrackMap(store, "Bgm00/A", Entry("000000001", "n1"), returnMap: null);
            var ex = Record.Exception(() =>
                MusicCatalogService.AccumulateTrackMap(store, "Bgm00/B", Entry("000000002", "n2"), returnMap: null));

            Assert.Null(ex);
            Assert.Equal(2, store.Count);
        }

        // ---------- 5 名封顶 / moreCount / 票数 / 去重 口径 ----------

        [Fact]
        public void AccumulateTrackMap_MoreThanFiveNamed_CapsAtFiveAndCountsMore()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            for (int i = 0; i < 8; i++)
            {
                MusicCatalogService.AccumulateTrackMap(
                    store, "Bgm00/X", Entry($"00000000{i}", $"n{i}"), returnMap: null);
            }
            var slice = store["Bgm00/X"];
            Assert.Equal(5, slice.Items.Count);
            Assert.Equal(3, slice.MoreCount);
        }

        [Fact]
        public void AccumulateTrackMap_DuplicateMapId_IsIgnoredAndDoesNotInflateMoreCount()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            for (int i = 0; i < 3; i++)
            {
                MusicCatalogService.AccumulateTrackMap(
                    store, "Bgm00/X", Entry("000000001", "same"), returnMap: null);
            }
            var slice = store["Bgm00/X"];
            Assert.Single(slice.Items);
            Assert.Equal(0, slice.MoreCount);
        }

        [Fact]
        public void AccumulateTrackMap_ReturnMapVotes_AccumulatePerMap_BeyondDisplayCap()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            // 5 张图 returnMap=900000000 + 2 张（超展示封顶）returnMap=900000001
            for (int i = 0; i < 5; i++)
            {
                MusicCatalogService.AccumulateTrackMap(
                    store, "Bgm00/X", Entry($"00000000{i}", $"n{i}"), returnMap: "900000000");
            }
            for (int i = 0; i < 2; i++)
            {
                MusicCatalogService.AccumulateTrackMap(
                    store, "Bgm00/X", Entry($"00000010{i}", $"m{i}"), returnMap: "900000001");
            }
            var slice = store["Bgm00/X"];
            Assert.NotNull(slice.ReturnMapVotes);
            Assert.Equal(5, slice.ReturnMapVotes!["900000000"]);
            Assert.Equal(2, slice.ReturnMapVotes["900000001"]);
            Assert.Equal("900000000", MusicDecisions.FirstMapByReturnMap(slice.ReturnMapVotes));
        }

        [Fact]
        public void AccumulateTrackMap_UnnamedMaps_NeverTakeDisplaySlots()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000001", name: null), returnMap: null);
            MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000002", "named"), returnMap: null);

            var slice = store["Bgm00/X"];
            Assert.Single(slice.Items);
            Assert.Equal("000000002", slice.Items[0].MapId);
        }

        [Fact]
        public void AccumulateTrackMap_TownFirst_ThenNameOrdinalIgnoreCase()
        {
            var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
            MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000001", "bbb"), returnMap: null);
            MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000002", "aaa"), returnMap: null);
            MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000003", "ccc", town: true), returnMap: null);

            var ids = store["Bgm00/X"].Items.Select(x => x.MapId).ToList();
            Assert.Equal(new[] { "000000003", "000000002", "000000001" }, ids);
        }

        // ---------- 根因二回归：key 归一化口径 ----------

        [Theory]
        [InlineData("Bgm00.img/FloralLife", "Bgm00/FloralLife")]
        [InlineData("Bgm00/FloralLife", "Bgm00/FloralLife")]
        [InlineData("bgm00.img/florallife", "bgm00/florallife")]
        [InlineData(" Bgm10.img/Timeless ", "Bgm10/Timeless")]
        [InlineData("BgmEvent/weddingDance", "BgmEvent/weddingDance")]
        public void NormalizeTrackKey_ImgSuffixIsStripped(string input, string expected)
        {
            Assert.Equal(expected, MusicDecisions.NormalizeTrackKey(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("NoSlash")]
        [InlineData("/LeadingSlash")]
        [InlineData("TrailingSlash/")]
        public void NormalizeTrackKey_InvalidInputs_ReturnNull(string? input)
        {
            Assert.Null(MusicDecisions.NormalizeTrackKey(input));
        }

        [Theory]
        [InlineData("Bgm00.img", "Bgm00")]
        [InlineData("Bgm00", "Bgm00")]
        [InlineData("bgm00.IMG", "bgm00")]
        [InlineData(" Bgm00.img ", "Bgm00")]
        public void NormalizeImgCategory_SuffixIsStripped(string input, string expected)
        {
            Assert.Equal(expected, MusicDecisions.NormalizeImgCategory(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".img")]
        public void NormalizeImgCategory_InvalidInputs_ReturnNull(string? input)
        {
            Assert.Null(MusicDecisions.NormalizeImgCategory(input));
        }

        /// <summary>口径契约：存储 key（无 .img）必须与 WalkPathPlanner.TrackKeyOf 的产出形态一致。</summary>
        [Fact]
        public void NormalizeTrackKey_MatchesWalkPathPlannerTrackKeyOf_Output()
        {
            // TrackKeyOf("Bgm00/FloralLife") 走 NormalizeBgm → 去 .img → "Bgm00/FloralLife"
            var fromWalk = TrackKeyOfShim("Bgm00/FloralLife");
            Assert.Equal(fromWalk, MusicDecisions.NormalizeTrackKey("Bgm00.img/FloralLife"));
        }

    /// <summary>复刻 WalkPathPlanner.TrackKeyOf 的形态（Walk/ 不引用，避免反向依赖）。</summary>
    private static string? TrackKeyOfShim(string? bgm)
    {
        var r = MusicDecisions.NormalizeBgm(bgm);
        if (r == null)
        {
            return null;
        }
        string img = r.Img.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? r.Img[..^4] : r.Img;
        return $"{img}/{r.Track}";
    }

    /// <summary>
    /// 第二处 NRE 回归（2026-09-15）：AccumulateTrackMap 必须对已有切片的 Items **无条件拷贝**——
    /// 旧写法 `as List&lt;T&gt;` 复用已发布切片的同一 List 实例，重扫会不持锁就地 Add/Sort
    /// UI 正在枚举的列表（并发读写的经典 NRE 前奏）。
    /// </summary>
    [Fact]
    public void AccumulateTrackMap_ExistingSlice_NeverMutatesPreviouslyPublishedList()
    {
        var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
        MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000001", "a"), returnMap: null);
        IReadOnlyList<TrackMapRef> published = store["Bgm00/X"].Items; // 模拟 UI 已持有的旧切片引用

        MusicCatalogService.AccumulateTrackMap(store, "Bgm00/X", Entry("000000002", "b"), returnMap: null);

        Assert.Equal(1, published.Count); // 旧切片（已发布数据）绝不被后续聚合就地改动
        Assert.Equal("000000001", published[0].MapId);
        Assert.Equal(2, store["Bgm00/X"].Items.Count);
    }
}

/// <summary>
/// 「曲目 → 有序候选图」口径锁（胶水 2026-09-15 规则 2 + 补充）：
/// town 优先（mapId 升序）+ town 合格校验（returnMap 指向图 BGM ≠ 当前曲 → 降级混排）
/// + 非 town mapId 升序；GetFirstMapByReturnMap = 排序后第一个；规则 5 输入条件（无候选 → null）。
/// </summary>
public class MusicWalkMapOrderTests
{
    private static TrackMapRef Entry(string mapId, string? name = null, bool town = false, string? ret = null)
        => new TrackMapRef(mapId, name, town, ret);

    // ---------- 纯函数口径：OrderTrackMaps ----------

    [Fact]
    public void Order_TownFirst_ThenOthers_BothMapIdAscending()
    {
        var items = new List<TrackMapRef>
        {
            Entry("000000005", "n5"),
            Entry("000000009", "town9", town: true, ret: "000000005"), // returnMap 在候选集内 → 合格免查询
            Entry("000000002", "n2"),
            Entry("000000007", "town7", town: true, ret: "000000005"),
        };
        var ordered = MusicDecisions.OrderTrackMaps(items, "Bgm00/FloralLife", _ => null);
        Assert.Equal(new[] { "000000007", "000000009", "000000002", "000000005" }, ordered);
    }

    [Fact]
    public void Order_UnqualifiedTown_DemotedIntoOthersByMapId()
    {
        var items = new List<TrackMapRef>
        {
            Entry("000000004", "n4"),
            Entry("000000006", "badTown", town: true, ret: "777777777"), // 指向图 BGM ≠ 当前曲 → 降级
            Entry("000000002", "n2"),
        };
        var ordered = MusicDecisions.OrderTrackMaps(items, "Bgm00/FloralLife", id => id == "777777777" ? "Bgm01/Other" : null);
        Assert.Equal(new[] { "000000002", "000000004", "000000006" }, ordered);
    }

    [Fact]
    public void Order_QualifiedTown_ViaLookupBgmMatch()
    {
        var items = new List<TrackMapRef> { Entry("000000006", "goodTown", town: true, ret: "555555555") };
        var ordered = MusicDecisions.OrderTrackMaps(
            items, "Bgm00/FloralLife", id => id == "555555555" ? "Bgm00/FloralLife" : null);
        Assert.Equal(new[] { "000000006" }, ordered);
    }

    [Fact]
    public void Order_QualifiedTown_ReturnMapTargetInCandidates_NeverCallsLookup()
    {
        var items = new List<TrackMapRef>
        {
            Entry("000000006", "town", town: true, ret: "000000001"),
            Entry("000000001", "n1"),
        };
        var called = false;
        var ordered = MusicDecisions.OrderTrackMaps(items, "Bgm00/FloralLife", _ => { called = true; return null; });
        Assert.Equal(new[] { "000000006", "000000001" }, ordered);
        Assert.False(called); // 捷径命中：指向图在候选集内必然同曲，免 WZ 查询
    }

    [Fact]
    public void Order_TownWithoutReturnMap_WithCurrentBgm_IsDemoted()
    {
        var items = new List<TrackMapRef> { Entry("000000006", "town", town: true, ret: null) };
        var ordered = MusicDecisions.OrderTrackMaps(items, "Bgm00/FloralLife", _ => "Bgm00/FloralLife");
        Assert.Equal(new[] { "000000006" }, ordered); // 无法确认合格 → 保守降级（仍是候选）
    }

    [Fact]
    public void Order_NullCurrentBgm_SkipsValidation_TownsKeepPriority()
    {
        var items = new List<TrackMapRef>
        {
            Entry("000000002", "n"),
            Entry("000000009", "town", town: true, ret: null),
        };
        var ordered = MusicDecisions.OrderTrackMaps(items, null, _ => null);
        Assert.Equal(new[] { "000000009", "000000002" }, ordered);
    }

    [Fact]
    public void Order_CurrentBgmWithImgSuffix_NormalizesBeforeCompare()
    {
        var items = new List<TrackMapRef> { Entry("000000006", "town", town: true, ret: "555555555") };
        var ordered = MusicDecisions.OrderTrackMaps(
            items, "Bgm00.img/FloralLife", _ => "Bgm00/FloralLife"); // 查询侧无 .img，当前曲带 .img
        Assert.Equal(new[] { "000000006" }, ordered);
    }

    [Fact]
    public void Order_DuplicateMapIds_Deduped()
    {
        var items = new List<TrackMapRef>
        {
            Entry("000000002", "n"),
            Entry("000000002", "n-dup"),
            Entry("000000001", "town", town: true),
        };
        var ordered = MusicDecisions.OrderTrackMaps(items, "Bgm00/X", _ => null);
        Assert.Equal(new[] { "000000001", "000000002" }, ordered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Order_EmptyOrNullItems_ReturnsEmpty(int mode)
    {
        var items = mode == 0 ? null : new List<TrackMapRef>();
        Assert.Empty(MusicDecisions.OrderTrackMaps(items, "Bgm00/X", _ => null));
    }

    // ---------- 端到端：MusicCatalogService（注入存储 + 注入 BGM 查询） ----------

    private static MusicCatalogService NewCatalogWithStore(params TrackMapRef[] floralMaps)
    {
        var store = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
        if (floralMaps.Length > 0)
        {
            store["Bgm00/FloralLife"] = new TrackMapSlice(new List<TrackMapRef>(floralMaps), 0, null);
        }
        var catalog = new MusicCatalogService(new WzService());
        catalog.ReplaceReverseStoreForTests(store); // bump 代际 + 整体替换，绕过磁盘缓存
        return catalog;
    }

    [Fact]
    public void GetFirstMapByReturnMap_QualifiedTownWins_OverSmallerNonTownMapId()
    {
        var catalog = NewCatalogWithStore(
            Entry("000000004", "n4"),
            Entry("000000006", "town", town: true, ret: "000000004")); // 指向图在候选集 → 合格
        Assert.Equal("000000006", catalog.GetFirstMapByReturnMap("Bgm00/FloralLife"));
        // key 兼容（带 .img 的 MusicTrack.Key 口径命中同一存储）
        Assert.Equal("000000006", catalog.GetFirstMapByReturnMap("Bgm00.img/FloralLife"));
    }

    [Fact]
    public void GetTrackMapsOrdered_CurrentBgmMismatch_DemotesTown_TwoKeysCachedIndependently()
    {
        var catalog = NewCatalogWithStore(
            Entry("000000004", "n4"),
            Entry("000000006", "town", town: true, ret: "777777777"));
        catalog.ReturnMapBgmLookup = id => id == "777777777" ? "Bgm00/FloralLife" : null;

        // 当前曲 = 指向图 BGM → town 合格排前
        Assert.Equal(new List<string> { "000000006", "000000004" },
            catalog.GetTrackMapsOrdered("Bgm00/FloralLife", "Bgm00/FloralLife"));
        // 当前曲 ≠ 指向图 BGM → 同一 town 降级，mapId 升序混排
        Assert.Equal(new List<string> { "000000004", "000000006" },
            catalog.GetTrackMapsOrdered("Bgm00/FloralLife", "Bgm02/CCC"));
    }

    [Fact]
    public void GetFirstMapByReturnMap_LookupNotInjected_TownStillViaCandidateShortcut()
    {
        // 生产默认 lookup = WZ 读；测试进程 WZ 未加载 → 查询必 null。
        // returnMap 指向图在候选集内 → 捷径免查询，town 仍合格（无需注入即可验真）。
        var catalog = NewCatalogWithStore(
            Entry("000000004", "n4"),
            Entry("000000006", "town", town: true, ret: "000000004"));
        Assert.Equal("000000006", catalog.GetFirstMapByReturnMap("Bgm00/FloralLife"));
    }

    [Fact]
    public void GetFirstMapByReturnMap_NoCandidates_ReturnsNull_Rule5FallbackInput()
    {
        var catalog = NewCatalogWithStore(); // 空 store：索引未就绪 / 真无候选
        Assert.Null(catalog.GetFirstMapByReturnMap("Bgm00/FloralLife"));
        Assert.Null(catalog.GetFirstMapByReturnMap(null));
        Assert.Null(catalog.GetFirstMapByReturnMap("NotATrackKey"));
    }

    [Fact]
    public void GetFirstMapByReturnMap_TrackGone_AfterStoreReplaced_GenerationInvalidatesCache()
    {
        var catalog = NewCatalogWithStore(
            Entry("000000004", "n4"),
            Entry("000000006", "town", town: true, ret: "000000004"));
        Assert.Equal("000000006", catalog.GetFirstMapByReturnMap("Bgm00/FloralLife")); // 已进缓存

        catalog.ReplaceReverseStoreForTests(new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal));
        Assert.Null(catalog.GetFirstMapByReturnMap("Bgm00/FloralLife")); // 代际失效 → 不得再吐旧缓存
    }
}
}
