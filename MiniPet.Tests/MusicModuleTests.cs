using Xunit;
using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Tests;

/// <summary>
/// 音乐模块 V0.2.0 单元测试（PLAN.md 步骤 9）：
/// 覆盖 MusicDecisions 纯函数集（bgm 归一化 / 队列推进数学 / 显示格式 / 失败终态判定）、
/// MusicTrack / MusicTrackRef / PlaybackMode 模型、MusicConfig JSON 往返与旧配置兼容。
/// 音频硬件路径与真实 WZ 文件不入自动测试（黑盒验收覆盖）。
/// </summary>
public class MusicModuleTests
{
    // --- 测试用 JSON 选项：对齐 ConfigService.GetJsonOptions 的核心三要素
    //     （WriteIndented / CamelCase / 大小写不敏感）；
    //     LenientStringConverter 与 MapHistoryEntryConverter 只作用于 string / MapHistoryEntry 字段，
    //     Music 子树全是基元类型 + 枚举 + List<string>，不受其影响 ---
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    // ====== 1. MusicDecisions.NormalizeBgm（bgm 值归一化） ======

    [Fact]
    public void NormalizeBgm_BgmValueWithoutImgSuffix_AppendsSuffix()
    {
        // WZ 探针实证样例：分类部分无 .img 后缀，必须补 ".img" 才能匹配 Sound 目录
        var result = MusicDecisions.NormalizeBgm("Bgm58/The Beginnig of The Adventure");

        Assert.NotNull(result);
        Assert.Equal(new MusicTrackRef("Bgm58.img", "The Beginnig of The Adventure"), result);
    }

    [Fact]
    public void NormalizeBgm_Silence_ReturnsNull()
    {
        // 硬编码忽略（2026-09-14 定）：Bgm00/Silence 是静音轨，项目内完全忽略 ——
        // 地图 bgm 指向它等价于「无 BGM」，场景联动 / 无限之路选图都不会命中。
        Assert.Null(MusicDecisions.NormalizeBgm("Bgm00/Silence"));
        Assert.True(MusicDecisions.IsIgnoredBgm(" Bgm00/Silence "));
        Assert.True(MusicDecisions.IsIgnoredBgm("bgm00/silence"));
        Assert.False(MusicDecisions.IsIgnoredBgm("Bgm00/FloralLife"));
        Assert.False(MusicDecisions.IsIgnoredBgm(null));
    }

    [Fact]
    public void FirstMapByReturnMap_TakesMode_AndBreaksTieBySmallerMapId()
    {
        // 实时算：returnMap 众数 = 无限之路第一张地图；并列取 mapId 小者
        var votes = new Dictionary<string, int> { ["302001000"] = 366, ["706020100"] = 100 };
        Assert.Equal("302001000", MusicDecisions.FirstMapByReturnMap(votes));

        var tie = new Dictionary<string, int> { ["910194100"] = 5, ["100000000"] = 5 };
        Assert.Equal("100000000", MusicDecisions.FirstMapByReturnMap(tie));

        Assert.Null(MusicDecisions.FirstMapByReturnMap(null));
        Assert.Null(MusicDecisions.FirstMapByReturnMap(new Dictionary<string, int>()));
    }

    [Fact]
    public void NormalizeBgm_AlreadyHasImgSuffix_DoesNotDuplicate()
    {
        var result = MusicDecisions.NormalizeBgm("Bgm58.img/SleepyWood");

        Assert.NotNull(result);
        Assert.Equal("Bgm58.img", result!.Img);
        Assert.Equal("SleepyWood", result.Track);
    }

    [Fact]
    public void NormalizeBgm_ImgSuffixCaseInsensitive_DoesNotDuplicate()
    {
        // 防御分支用 OrdinalIgnoreCase 判断后缀：已带 .IMG（任意大小写）不重复补，保持原大小写
        var result = MusicDecisions.NormalizeBgm("Bgm58.IMG/Track");

        Assert.NotNull(result);
        Assert.Equal("Bgm58.IMG", result!.Img);
        Assert.Equal("Track", result.Track);
    }

    [Fact]
    public void NormalizeBgm_NoSlash_ReturnsNull()
    {
        Assert.Null(MusicDecisions.NormalizeBgm("Bgm58"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeBgm_NullOrWhitespace_ReturnsNull(string? bgm)
    {
        Assert.Null(MusicDecisions.NormalizeBgm(bgm));
    }

    [Fact]
    public void NormalizeBgm_LeadingSlash_ReturnsNull()
    {
        // 前导斜杠 = 分类部分为空，无法定位分类
        Assert.Null(MusicDecisions.NormalizeBgm("/SleepyWood"));
    }

    [Fact]
    public void NormalizeBgm_TrailingSlash_ReturnsNull()
    {
        // 尾随斜杠 = 曲目部分为空
        Assert.Null(MusicDecisions.NormalizeBgm("Bgm58/"));
    }

    [Fact]
    public void NormalizeBgm_SurroundingWhitespace_IsTrimmed()
    {
        var result = MusicDecisions.NormalizeBgm("  Bgm58/SleepyWood  ");

        Assert.NotNull(result);
        Assert.Equal(new MusicTrackRef("Bgm58.img", "SleepyWood"), result);
    }

    [Fact]
    public void NormalizeBgm_MultipleSlashes_SplitsAtFirstSlash()
    {
        // 「首斜杠前=分类、其余=曲目名」切割：曲目名含斜杠时整体保留在 Track 段
        var result = MusicDecisions.NormalizeBgm("Bgm00/a/b");

        Assert.NotNull(result);
        Assert.Equal("Bgm00.img", result!.Img);
        Assert.Equal("a/b", result.Track);
    }

    // ====== 2. MusicDecisions.NextIndex（下一首索引数学） ======

    [Fact]
    public void NextIndex_Sequential_AtLastIndex_WrapsToZero()
    {
        // v0.2.0 显式口径：队列尾回卷到队首持续循环
        Assert.Equal(0, MusicDecisions.NextIndex(PlaybackMode.Sequential, 3, 2));
    }

    [Fact]
    public void NextIndex_Sequential_InMiddle_AdvancesByOne()
    {
        Assert.Equal(3, MusicDecisions.NextIndex(PlaybackMode.Sequential, 5, 2));
    }

    [Fact]
    public void NextIndex_Sequential_SingleItemTrack_WrapsToSelf()
    {
        // 场景联动单元素队列替换：count=1 时回卷即原位
        Assert.Equal(0, MusicDecisions.NextIndex(PlaybackMode.Sequential, 1, 0));
    }

    [Fact]
    public void NextIndex_RepeatOne_ReturnsCurrentIndex()
    {
        Assert.Equal(3, MusicDecisions.NextIndex(PlaybackMode.RepeatOne, 5, 3));
    }

    [Fact]
    public void NextIndex_Random_WithSeededRng_MatchesDocumentedComputation()
    {
        // 契约（XML doc 公开口径）：在其余 count-1 首上均匀取，pick >= current 时右移一位跳过 current
        int count = 5;
        int current = 2;
        var expectedRng = new Random(1234);
        int pick = expectedRng.Next(count - 1);
        if (pick >= current)
        {
            pick++;
        }

        var rng = new Random(1234);
        int actual = MusicDecisions.NextIndex(PlaybackMode.Random, count, current, rng);

        Assert.Equal(pick, actual);
        Assert.InRange(actual, 0, count - 1);
        Assert.NotEqual(current, actual);
    }

    [Fact]
    public void NextIndex_Random_TwoItemsFromFirst_AlwaysPicksSecond()
    {
        // count=2, current=0：Next(1) 恒 0 → 右移后恒 1，任何随机流下结果确定
        for (int seed = 0; seed < 50; seed++)
        {
            Assert.Equal(
                1,
                MusicDecisions.NextIndex(PlaybackMode.Random, 2, 0, new Random(seed)));
        }
    }

    [Fact]
    public void NextIndex_Random_NeverReturnsCurrentAndStaysInRange()
    {
        int count = 5;
        int current = 3;
        var rng = new Random(2026);

        for (int i = 0; i < 200; i++)
        {
            int next = MusicDecisions.NextIndex(PlaybackMode.Random, count, current, rng);
            Assert.InRange(next, 0, count - 1);
            Assert.NotEqual(current, next);
        }
    }

    [Fact]
    public void NextIndex_Random_CountOne_ReturnsZero()
    {
        // 无处可避的单元素队列：随机也只得回到 0
        Assert.Equal(0, MusicDecisions.NextIndex(PlaybackMode.Random, 1, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NextIndex_CountNotPositive_ReturnsMinusOne(int count)
    {
        Assert.Equal(-1, MusicDecisions.NextIndex(PlaybackMode.Sequential, count, 0));
        Assert.Equal(-1, MusicDecisions.NextIndex(PlaybackMode.RepeatOne, count, 0));
        Assert.Equal(-1, MusicDecisions.NextIndex(PlaybackMode.Random, count, 0));
    }

    [Fact]
    public void NextIndex_CurrentOutOfRange_NormalizesToZeroFirst()
    {
        // 越界 current（如初始 -1 / 超上界）先归一为队首再走各模式
        Assert.Equal(1, MusicDecisions.NextIndex(PlaybackMode.Sequential, 4, -1));
        Assert.Equal(1, MusicDecisions.NextIndex(PlaybackMode.Sequential, 4, 99));
        Assert.Equal(0, MusicDecisions.NextIndex(PlaybackMode.RepeatOne, 4, 99));
    }

    // ====== 3. MusicDecisions.PrevIndex（上一首索引数学，与 NextIndex 对称） ======

    [Fact]
    public void PrevIndex_Sequential_AtFirstIndex_WrapsToLast()
    {
        Assert.Equal(3, MusicDecisions.PrevIndex(PlaybackMode.Sequential, 4, 0));
    }

    [Fact]
    public void PrevIndex_Sequential_InMiddle_Decrements()
    {
        Assert.Equal(1, MusicDecisions.PrevIndex(PlaybackMode.Sequential, 5, 2));
    }

    [Fact]
    public void PrevIndex_RepeatOne_ReturnsCurrentIndex()
    {
        Assert.Equal(2, MusicDecisions.PrevIndex(PlaybackMode.RepeatOne, 5, 2));
    }

    [Fact]
    public void PrevIndex_Random_NeverReturnsCurrentAndStaysInRange()
    {
        // Random 的 Prev 与 Next 同抽取逻辑（不回退历史），只要求换一首
        int count = 6;
        int current = 0;
        var rng = new Random(4242);

        for (int i = 0; i < 200; i++)
        {
            int prev = MusicDecisions.PrevIndex(PlaybackMode.Random, count, current, rng);
            Assert.InRange(prev, 0, count - 1);
            Assert.NotEqual(current, prev);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PrevIndex_CountNotPositive_ReturnsMinusOne(int count)
    {
        Assert.Equal(-1, MusicDecisions.PrevIndex(PlaybackMode.Sequential, count, 0));
        Assert.Equal(-1, MusicDecisions.PrevIndex(PlaybackMode.Random, count, 0));
    }

    // ====== 4. MusicDecisions.FormatDisplay（显示文案格式） ======

    [Fact]
    public void FormatDisplay_PositiveMs_AppendsPaddedMinutesSeconds()
    {
        // 166000ms = 2 分 46 秒 → 补零为 "02:46"；与 TimeSpan 等价逻辑核对
        Assert.Equal("Track (02:46)", MusicDecisions.FormatDisplay("Track", 166_000));
        Assert.Equal("02:46", TimeSpan.FromMilliseconds(166_000).ToString(@"mm\:ss"));
    }

    [Fact]
    public void FormatDisplay_SubMinuteMs_PadsBothSegments()
    {
        Assert.Equal("Track (00:05)", MusicDecisions.FormatDisplay("Track", 5_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-166_000)]
    public void FormatDisplay_MsNotPositive_ReturnsTrackOnly(int ms)
    {
        // 元数据缺失（如场景曲目 Ms 填 0）：只显示曲名
        Assert.Equal("SleepyWood", MusicDecisions.FormatDisplay("SleepyWood", ms));
    }

    // ====== 5. MusicDecisions.DecideFailureOutcome（失败终态判定） ======

    [Fact]
    public void DecideFailureOutcome_OldStreamAlive_KeepsCurrentStateAndRaises()
    {
        // 准备阶段失败（WZ 提取 null / Prepare 抛错）：活动流分毫未动，保持原播放只上抛
        var playing = MusicDecisions.DecideFailureOutcome(true, PlaybackState.Playing);
        Assert.Equal((PlaybackState.Playing, true), playing);

        var paused = MusicDecisions.DecideFailureOutcome(true, PlaybackState.Paused);
        Assert.Equal((PlaybackState.Paused, true), paused);

        var stopped = MusicDecisions.DecideFailureOutcome(true, PlaybackState.Stopped);
        Assert.Equal((PlaybackState.Stopped, true), stopped);
    }

    [Fact]
    public void DecideFailureOutcome_OldStreamDead_StopsAndRaises()
    {
        // 旧流已终结（自然播完后继失败 / RepeatOne 重备失败 / 提交段失败）：Stopped 终态 + 上抛，无重试循环
        var outcome = MusicDecisions.DecideFailureOutcome(false, PlaybackState.Playing);
        Assert.Equal((PlaybackState.Stopped, true), outcome);
    }

    // ====== 6. MusicTrack（Key / Display） ======

    [Fact]
    public void MusicTrack_Key_CombinesImgAndTrack()
    {
        var track = new MusicTrack("Bgm00.img", "SleepyWood", 166_000, 2, 44_100);

        Assert.Equal("Bgm00.img/SleepyWood", track.Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MusicTrack_Display_MsNotPositive_OmitsDuration(int ms)
    {
        var track = new MusicTrack("Bgm00.img", "SleepyWood", ms, 2, 44_100);

        Assert.Equal("SleepyWood", track.Display);
        // 与 FormatDisplay 口径一致
        Assert.Equal(MusicDecisions.FormatDisplay(track.Track, track.Ms), track.Display);
    }

    [Fact]
    public void MusicTrack_Display_PositiveMs_IncludesDuration_AndMatchesFormatDisplay()
    {
        var track = new MusicTrack("Bgm00.img", "SleepyWood", 166_000, 2, 44_100);

        Assert.Equal("SleepyWood (02:46)", track.Display);
        Assert.Equal(MusicDecisions.FormatDisplay(track.Track, track.Ms), track.Display);
    }

    // ====== 7. PlaybackMode 枚举（与动画系统 PlayMode 刻意区分） ======

    [Fact]
    public void PlaybackMode_DefinesExactlySequentialRepeatOneRandom()
    {
        var names = Enum.GetNames<PlaybackMode>().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Random", "RepeatOne", "Sequential" }, names);

        Assert.Equal(0, (int)PlaybackMode.Sequential);
        Assert.Equal(1, (int)PlaybackMode.RepeatOne);
        Assert.Equal(2, (int)PlaybackMode.Random);
    }

    [Fact]
    public void PlaybackMode_IsDistinctTypeFromAnimPlayMode()
    {
        // 编译期即证：两个枚举互无隐式转换，本方法同时强类型引用两者（无需任何 cast 即可各自使用）。
        // 运行时再验一次类型身份：MiniPet.Models.PlaybackMode ≠ MiniPet.Services.PlayMode（Loop/OneShot）。
        PlaybackMode musicMode = PlaybackMode.Random;
        MiniPet.Services.PlayMode animMode = MiniPet.Services.PlayMode.Loop;

        Assert.NotSame(typeof(MiniPet.Services.PlayMode), musicMode.GetType());
        Assert.NotSame(typeof(PlaybackMode), animMode.GetType());
        Assert.NotEqual(typeof(MiniPet.Services.PlayMode), typeof(PlaybackMode));
        Assert.NotEqual(typeof(MiniPet.Services.PlayMode).FullName, typeof(PlaybackMode).FullName);

        // 值集也无交集：动画 PlayMode = Loop/OneShot，音乐 PlaybackMode = Sequential/RepeatOne/Random，
        // 语义无关不可互换（CONTEXT.md 术语表刻意区分）
        Assert.Equal(new[] { "Loop", "OneShot" }, Enum.GetNames<MiniPet.Services.PlayMode>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ====== 8. MusicConfig JSON 往返 + 旧配置兼容 ======

    [Fact]
    public void MusicConfig_JsonRoundTrip_PreservesAllFields_WithCamelCaseNaming()
    {
        var original = new MusicConfig
        {
            Volume = 55,
            PlaybackMode = PlaybackMode.Random,
            LastTrackKey = "Bgm00.img/SleepyWood",
            SceneLinkEnabled = true,
            PlayHistory = new List<string>
            {
                "Bgm00.img/SleepyWood",
                "Bgm58.img/The Beginnig of The Adventure"
            }
        };

        var json = JsonSerializer.Serialize(original, GetJsonOptions());

        // 命名策略跟随主工程 PetSettings 的 CamelCase（music 子树字段全部 camelCase 落盘）
        Assert.Contains("\"volume\"", json);
        Assert.Contains("\"playbackMode\"", json);
        Assert.Contains("\"lastTrackKey\"", json);
        Assert.Contains("\"sceneLinkEnabled\"", json);
        Assert.Contains("\"playHistory\"", json);

        var restored = JsonSerializer.Deserialize<MusicConfig>(json, GetJsonOptions());

        Assert.NotNull(restored);
        Assert.Equal(55, restored!.Volume);
        Assert.Equal(PlaybackMode.Random, restored.PlaybackMode);
        Assert.Equal("Bgm00.img/SleepyWood", restored.LastTrackKey);
        Assert.True(restored.SceneLinkEnabled);
        Assert.Equal(original.PlayHistory, restored.PlayHistory);
    }

    [Fact]
    public void MusicConfig_Defaults_MatchDocumentedSchema()
    {
        var config = new MusicConfig();

        Assert.Equal(70, config.Volume);
        Assert.Equal(PlaybackMode.Sequential, config.PlaybackMode);
        Assert.Null(config.LastTrackKey);
        Assert.False(config.SceneLinkEnabled);
        Assert.Empty(config.PlayHistory);
    }

    [Fact]
    public void PetSettings_LegacyJsonWithoutMusicField_GetsNonNullDefaultMusic()
    {
        // 旧配置文件无 music 字段：System.Text.Json 不触碰 JSON 里不存在的属性，
        // 反序列化落在属性初始化器默认值上，Music 非 null 且为文档默认
        var legacyJson = """
            {
              "wzLibPath": "/Volumes/SSD/mxd/mxd",
              "mapId": "100000000",
              "theme": "dark"
            }
            """;

        // 注意：MiniPet.Tests 命名空间下 ModelTests.cs 另有一个同名 PetSettings 副本，
        // 此处必须用全限定名指向主工程真实的 MiniPet.Models.PetSettings（含 Music 字段）
        var settings = JsonSerializer.Deserialize<MiniPet.Models.PetSettings>(legacyJson, GetJsonOptions());

        Assert.NotNull(settings);
        Assert.NotNull(settings!.Music);
        Assert.Equal(70, settings.Music.Volume);
        Assert.Equal(PlaybackMode.Sequential, settings.Music.PlaybackMode);
        Assert.Null(settings.Music.LastTrackKey);
        Assert.False(settings.Music.SceneLinkEnabled);
        Assert.Empty(settings.Music.PlayHistory);
        // 其余旧字段照常读入
        Assert.Equal("/Volumes/SSD/mxd/mxd", settings.WzLibPath);
    }

    // ====== 9. MusicTrackRef（record 值等价） ======

    [Fact]
    public void MusicTrackRef_SameValues_AreEqualByValue()
    {
        var a = new MusicTrackRef("Bgm58.img", "The Beginnig of The Adventure");
        var b = new MusicTrackRef("Bgm58.img", "The Beginnig of The Adventure");

        Assert.True(a == b);
        Assert.True(b == a);
        Assert.True(a.Equals(b));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MusicTrackRef_DifferentValues_AreNotEqual()
    {
        var a = new MusicTrackRef("Bgm00.img", "SleepyWood");
        var b = new MusicTrackRef("Bgm58.img", "SleepyWood");
        var c = new MusicTrackRef("Bgm00.img", "FlorinaBeach");

        Assert.True(a != b);
        Assert.True(a != c);
        Assert.True(b != c);
    }

    [Fact]
    public void MusicTrackRef_NormalizeBgmResult_EqualsDirectConstruction()
    {
        // 归一化产物与手工构造的同值引用可互换（场景联动比对曲目 Key 依赖值等价）
        var fromNormalize = MusicDecisions.NormalizeBgm("Bgm58/The Beginnig of The Adventure");
        var direct = new MusicTrackRef("Bgm58.img", "The Beginnig of The Adventure");

        Assert.NotNull(fromNormalize);
        Assert.Equal(direct, fromNormalize);
        Assert.True(direct == fromNormalize);
    }
}
