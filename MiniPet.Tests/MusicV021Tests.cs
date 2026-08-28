using Xunit;
using System;
using System.Linq;
using System.Collections.Generic;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Tests;

/// <summary>
/// 音乐模块 V0.2.1 增量单元测试：自然排序（CompareNatural）/ 随机跨节点抽取
/// （PickRandomAcross）/ 本地曲目 Track 相对路径规则（ToLocalTrackName + IsSupportedAudioExtension）/
/// TrackMapRef record 值等价 / MusicConfig 新字段默认值与 JSON 往返。
/// 凡涉 WZ/File IO 的部分只测纯逻辑（约定同 MusicModuleTests——音频硬件路径与真实 WZ 不入自动测试）。
/// </summary>
public class MusicV021Tests
{
    // ====== 1. MusicDecisions.CompareNumericNatural（分类自然排序） ======

    [Fact]
    public void CompareNatural_BgmMixedWidthSequence_SortsByEmbeddedNumber()
    {
        // V0.2.1 口径：数字段按数值比较——Bgm01<Bgm02<…<Bgm09<…Bgm09 数值 9 &lt; Bgm012 数值 12
        // → Bgm012 在 Bgm09 之后；本序列含任务书点名的 Bgm012/Bgm02/Bgm10 混合宽度组合
        var input = new List<string> { "Bgm012", "Bgm02", "Bgm10", "Bgm01", "Bgm09", "Bgm00" };
        var sorted = input.OrderBy(c => c, Comparer<string>.Create(MusicDecisions.CompareNatural)).ToList();

        Assert.Equal(
            new[] { "Bgm00", "Bgm01", "Bgm02", "Bgm09", "Bgm10", "Bgm012" },
            sorted);
    }

    [Fact]
    public void CompareNatural_FixesOrdinalDefect_Bgm012NoLongerBetweenBgm01AndBgm02()
    {
        // 缺陷回归锚：Ordinal 会把 "Bgm012" 插进 "Bgm01"/"Bgm02" 之间；自然排序必须不这样
        var ordinalOrder = new[] { "Bgm01", "Bgm02" }
            .Append("Bgm012")
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        Assert.Equal("Bgm012", ordinalOrder[1]); // 先证明 Ordinal 确实存在该缺陷

        Assert.True(MusicDecisions.CompareNatural("Bgm02", "Bgm012") < 0);
        Assert.True(MusicDecisions.CompareNatural("Bgm09", "Bgm012") < 0);
        Assert.True(MusicDecisions.CompareNatural("Bgm012", "Bgm10") > 0);
    }

    [Theory]
    [InlineData("Bgm02", 2, "Bgm09", 9)]
    [InlineData("Bgm9", 9, "Bgm10", 10)]
    public void CompareNatural_WiderNumbersBeatsLexicographic(string a, int aNum, string b, int bNum)
    {
        int expected = aNum.CompareTo(bNum);
        Assert.Equal(expected < 0, MusicDecisions.CompareNatural(a, b) < 0);
        Assert.Equal(expected > 0, MusicDecisions.CompareNatural(a, b) > 0);
    }

    [Fact]
    public void CompareNatural_LeadingZeros_EqualStems_FallBackToWholeStringOrdinal()
    {
        // 前导零不影响数值："Bgm007" 与 "Bgm7" 数值相等 → 回退全串 Ordinal 保稳定确定性
        int result = MusicDecisions.CompareNatural("Bgm07", "Bgm7");

        Assert.Equal(
            Math.Sign(string.CompareOrdinal("Bgm07", "Bgm7")),
            Math.Sign(result));
        // 完全一致恒为 0
        Assert.Equal(0, MusicDecisions.CompareNatural("Bgm07", "Bgm07"));
    }

    [Fact]
    public void CompareNatural_MultiNumberSegments_ComparesSegmentBySegment()
    {
        // 多段数字逐段比较：前段已分出胜负即返回
        Assert.True(MusicDecisions.CompareNatural("v2/chapter10", "v2/chapter9") > 0);
        Assert.True(MusicDecisions.CompareNatural("v2/chapter9", "v10/chapter1") < 0);
    }

    [Fact]
    public void CompareNatural_PrefixRelationship_ShorterFirst()
    {
        Assert.True(MusicDecisions.CompareNatural("Bgm01", "Bgm01.img") < 0);
        Assert.True(MusicDecisions.CompareNatural("Bgm01.img", "Bgm01") > 0);
    }

    [Fact]
    public void CompareNatural_NullAndIdentity_HaveDocumentedConventions()
    {
        Assert.Equal(0, MusicDecisions.CompareNatural(null, null));
        Assert.Equal(0, MusicDecisions.CompareNatural("Bgm00", "Bgm00"));
        Assert.True(MusicDecisions.CompareNatural(null, "Bgm00") < 0);
        Assert.True(MusicDecisions.CompareNatural("Bgm00", null) > 0);
    }

    // ====== 2. MusicDecisions.PickRandomAcross（随机跨节点抽取） ======

    [Fact]
    public void PickRandomAcross_TwoHundredDraws_NeverHitsExcludedIndexAndStaysInRange()
    {
        const int count = 10;
        const int exclude = 4;
        var rng = new Random(2026);

        for (int i = 0; i < 200; i++)
        {
            int pick = MusicDecisions.PickRandomAcross(count, exclude, rng);
            Assert.InRange(pick, 0, count - 1);
            Assert.NotEqual(exclude, pick);
        }
    }

    [Fact]
    public void PickRandomAcross_ExcludeAtBoundaries_IsRespected()
    {
        var rng = new Random(7);

        for (int i = 0; i < 100; i++)
        {
            Assert.NotEqual(0, MusicDecisions.PickRandomAcross(5, 0, rng));
            Assert.NotEqual(4, MusicDecisions.PickRandomAcross(5, 4, rng));
        }
    }

    [Fact]
    public void PickRandomAcross_SpreadsOverAllNonExcludedPositions()
    {
        // 弱均匀性断言：200 次应覆盖除 exclude 外的全部合法位置（count=5, exclude=2）
        var rng = new Random(42);
        var seen = new HashSet<int>();

        for (int i = 0; i < 200; i++)
        {
            seen.Add(MusicDecisions.PickRandomAcross(5, 2, rng));
        }

        Assert.Contains(0, seen);
        Assert.Contains(1, seen);
        Assert.Contains(3, seen);
        Assert.Contains(4, seen);
        Assert.DoesNotContain(2, seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PickRandomAcross_NotPositiveCount_ReturnsMinusOne(int count)
    {
        Assert.Equal(-1, MusicDecisions.PickRandomAcross(count, 0));
    }

    [Fact]
    public void PickRandomAcross_SingleCandidate_ReturnsZeroEvenIfItIsTheExclude()
    {
        // count==1 无处可避让：语义定为返回唯一候选 0（exclude 仅在有多候选时才可满足）
        Assert.Equal(0, MusicDecisions.PickRandomAcross(1, 0));
        Assert.Equal(0, MusicDecisions.PickRandomAcross(1, -1, new Random(1)));
    }

    [Fact]
    public void PickRandomAcross_OutOfRangeExclude_TreatedAsNoExclusion()
    {
        var rng = new Random(99);

        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(MusicDecisions.PickRandomAcross(6, -1, rng), 0, 5);
            Assert.InRange(MusicDecisions.PickRandomAcross(6, 6, rng), 0, 5);
        }
    }

    // ====== 3. 本地曲目 Track 相对路径规则（纯字符串口径） ======

    [Theory]
    [InlineData("song.mp3", "song")]
    [InlineData("subdir/song.flac", "subdir/song")]
    [InlineData("deep/nested/dir/song.wav", "deep/nested/dir/song")]
    public void ToLocalTrackName_RelativePathWithExt_StripsExtensionKeepsForwardSlashes(string rel, string expected)
    {
        Assert.Equal(expected, MusicDecisions.ToLocalTrackName(rel));
    }

    [Fact]
    public void ToLocalTrackName_BackslashSeparators_AreNormalizedToForward()
    {
        // Windows 的 Path.GetRelativePath 产 '\'，入库必须归一 '/'（播放端反向 Replace 还原平台分隔符）
        Assert.Equal("subdir/song", MusicDecisions.ToLocalTrackName(@"subdir\song.mp3"));
        Assert.Equal("a/b/c", MusicDecisions.ToLocalTrackName(@"a\b\c.flac"));
    }

    [Fact]
    public void ToLocalTrackName_DotsInsideName_OnlyTrailingExtensionIsStripped()
    {
        // 曲名本身带点：只剥最后一段扩展名
        Assert.Equal("Mr.Dot & Friends", MusicDecisions.ToLocalTrackName("Mr.Dot & Friends.mp3"));
        Assert.Equal("archived.v2/song", MusicDecisions.ToLocalTrackName("archived.v2/song.wav"));
    }

    [Fact]
    public void ToLocalTrackName_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MusicDecisions.ToLocalTrackName(""));
    }

    [Theory]
    [InlineData(".mp3", true)]
    [InlineData(".MP3", true)]
    [InlineData(".Flac", true)]
    [InlineData(".WAV", true)]
    [InlineData(".ogg", false)]
    [InlineData(".txt", false)]
    [InlineData("", false)]
    public void IsSupportedAudioExtension_MatchesOnlyMp3FlacWavCaseInsensitive(string ext, bool expected)
    {
        Assert.Equal(expected, MusicDecisions.IsSupportedAudioExtension(ext));
    }

    [Theory]
    [InlineData("Song.MP3", true)]
    [InlineData("track.final.flac", true)]
    [InlineData("voice.OGG", false)]
    [InlineData("notes", false)]
    public void IsSupportedAudioExtension_FileNames_TakeLastExtensionCaseInsensitive(string fileName, bool expected)
    {
        Assert.Equal(expected, MusicDecisions.IsSupportedAudioExtension(fileName));
    }

    // ====== 4. 本地源标记常量（跨服务契约锚点） ======

    [Fact]
    public void LocalSource_ContractConstants_AreStableForPlayerAndUi()
    {
        // Img 占位标记与固定末位分类名是 MusicCatalogService/MusicPlayerService/UI 三方契约：
        // 播放端 TryExtractBytes 以 Img==__LOCAL__ 分派本地分支；UI 分类栏末位渲染「📁 本地」
        Assert.Equal("__LOCAL__", MusicCatalogService.LocalImgTag);
        Assert.Equal("📁 本地", MusicCatalogService.LocalCategoryName);
    }

    // ====== 5. TrackMapRef（record 值等价 + 探针实证语义默认值形态） ======

    [Fact]
    public void TrackMapRef_SameValues_AreEqualByValue()
    {
        var a = new TrackMapRef("100000000", "Henesys", true);
        var b = new TrackMapRef("100000000", "Henesys", true);

        Assert.True(a == b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TrackMapRef_NullMapName_IsAllowedForUnknownNamedMaps()
    {
        // 地图名缓存 miss 时 MapName=null（UI 回退显示 MapId），record 接受 null 且参与值等价
        var noName = new TrackMapRef("910000000", null, false);
        var named = new TrackMapRef("910000000", "Free Market", false);

        Assert.Null(noName.MapName);
        Assert.False(noName.IsTown);
        Assert.NotEqual(named, noName);
    }

    // ====== 6. MusicConfig 新字段（V0.2.1 默认值 + JSON 往返兼容旧配置） ======

    [Fact]
    public void MusicConfig_V021Defaults_LocalLibraryDisabledAndMiniBarOn()
    {
        var config = new MusicConfig();

        // 空串 = 未启用本地源；迷你悬浮条默认开
        Assert.Equal(string.Empty, config.LocalMusicPath);
        Assert.True(config.MiniBarEnabled);
    }

    [Fact]
    public void MusicConfig_V021Fields_JsonRoundTripWithCamelCaseNaming()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var original = new MusicConfig
        {
            LocalMusicPath = "/Users/demo/Music",
            MiniBarEnabled = false
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, options);

        // 命名策略跟随主工程 PetSettings 的 CamelCase（music 子树 camelCase 落盘）
        Assert.Contains("\"localMusicPath\"", json);
        Assert.Contains("\"miniBarEnabled\"", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize<MusicConfig>(json, options);

        Assert.NotNull(restored);
        Assert.Equal("/Users/demo/Music", restored!.LocalMusicPath);
        Assert.False(restored.MiniBarEnabled);
    }

    [Fact]
    public void PetSettings_LegacyJsonWithoutV021Fields_GetsSafeDefaults()
    {
        // V0.2.0 的旧配置无 localMusicPath/miniBarEnabled 字段：反序列化落属性初始化器默认值；
        // 生产 GetJsonOptions 未注册枚举转换器，枚举按数字落盘（Random = 2）
        var legacyJson = """
            {
              "music": {
                "volume": 55,
                "playbackMode": 2
              }
            }
            """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        var settings = System.Text.Json.JsonSerializer.Deserialize<MiniPet.Models.PetSettings>(legacyJson, options);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.Music);
        Assert.Equal(string.Empty, settings.Music.LocalMusicPath);
        Assert.True(settings.Music.MiniBarEnabled);
        Assert.Equal(PlaybackMode.Random, settings.Music.PlaybackMode);
    }
}
