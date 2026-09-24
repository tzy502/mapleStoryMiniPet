using System;
using System.Collections.Generic;
using System.IO;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// 音乐模块纯函数决策集（无状态、无 IO，供单元测试直接测）。
    /// 与 MusicCatalogService / MusicPlayerService 的可变编排分离：
    /// 归一化 / 队列推进数学 / 显示格式 / 失败终态判定等口径逻辑全部集中在此。
    /// </summary>
    public static class MusicDecisions
    {
        /// <summary>
        /// 硬编码忽略的 BGM（2026-09-14 定）：Bgm00/Silence 是静音轨，项目内**完全忽略**——
        /// 不视为场景 BGM，场景联动（F17）与无限之路选图（F18）都不会命中它。
        /// </summary>
        public const string IgnoredSilenceBgm = "Bgm00/Silence";

    /// <summary>
    /// 「曲目 → 候选地图」排序口径（胶水 2026-09-15 规则 2 + 补充，F18 无限之路起始图/遍历序）：
    /// 1. **有 town 的图优先**，组内 mapId 升序（Ordinal——存储为 9 位补零形态，字符序即数值序）；
    /// 2. **town 合格校验**：town 图的 info/returnMap 指向图 BGM ≠ currentBgmKey → 该 town **不合格**，
    ///    降级为普通候选（按 mapId 升序混入非 town 队列）。校验捷径：returnMap 指向图若在本候选集内，
    ///    其 BGM 必然等于本曲目（候选集本身就是按 info/bgm == 本曲目扫出来的），免查询直接合格；
    ///    否则经 returnMapBgmOf 委托查指向图（生产 = WZ 读 info/bgm，测试 = 字典桩）。
    /// 3. 非 town 图按 mapId 升序。
    /// 边界口径：currentBgmKey 空 → 无比较基准，town 全部保持优先；returnMap 缺失 / 指向图查不到
    /// BGM → 视为「≠ 当前 BGM」→ 不合格降级（保守口径：宁可混排也不选错起点）。
    /// items 允许重复 mapId（去重保留首条）；null/空 → 空列表。纯函数无 IO，供单测直接锁口径。
    /// </summary>
    /// <param name="items">曲目候选图条目（TryGetTrackMaps 收录形态，IsTown/ReturnMap 由扫描填充）。</param>
    /// <param name="currentBgmKey">当前曲目 key（"Bgm00/FloralLife" 或 "Bgm00.img/FloralLife"，内部归一化）。</param>
    /// <param name="returnMapBgmOf">mapId → 指向图归一化 BGM key（查不到为 null）；仅 town 且指向图不在候选集时调用。</param>
    public static List<string> OrderTrackMaps(
        IReadOnlyList<TrackMapRef>? items,
        string? currentBgmKey,
        Func<string, string?>? returnMapBgmOf)
    {
        var result = new List<string>();
        if (items == null || items.Count == 0)
        {
            return result;
        }
        // 去重（同 mapId 视为同图，保留首条数据）
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<TrackMapRef>();
        foreach (var it in items)
        {
            if (it == null || string.IsNullOrEmpty(it.MapId) || !candidateIds.Add(it.MapId))
            {
                continue;
            }
            candidates.Add(it);
        }
        string? current = NormalizeTrackKey(currentBgmKey);
        var towns = new List<string>();
        var others = new List<string>();
        foreach (var c in candidates)
        {
            if (c.IsTown && IsTownQualifiedForTrack(c, current, candidateIds, returnMapBgmOf))
            {
                towns.Add(c.MapId);
            }
            else
            {
                others.Add(c.MapId);
            }
        }
        towns.Sort(StringComparer.Ordinal);
        others.Sort(StringComparer.Ordinal);
        result.AddRange(towns);
        result.AddRange(others);
        return result;
    }

    /// <summary>
    /// town 合格校验（规则 2 补充）：returnMap 指向图的 BGM == 当前曲目才算合格。
    /// - currentBgmKey 空 → 无从校验，视为合格（调用方没给比较基准，不动 town 优先级）；
    /// - returnMap 指向图在候选集内 → 其 BGM 必为当前曲目，免查询直接合格；
    /// - 其余经 returnMapBgmOf 查指向图（null 委托 / returnMap 缺失 / 查不到 / 值不等 → 不合格）。
    /// </summary>
    private static bool IsTownQualifiedForTrack(
        TrackMapRef town, string? currentBgmKey, HashSet<string> candidateIds, Func<string, string?>? returnMapBgmOf)
    {
        if (string.IsNullOrEmpty(currentBgmKey))
        {
            return true;
        }
        if (!string.IsNullOrEmpty(town.ReturnMap) && candidateIds.Contains(town.ReturnMap!))
        {
            return true;
        }
        if (returnMapBgmOf == null || string.IsNullOrEmpty(town.ReturnMap))
        {
            return false;
        }
        string? bgm = returnMapBgmOf(town.ReturnMap!);
        return !string.IsNullOrEmpty(bgm) && string.Equals(bgm, currentBgmKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// returnMap 票数 → 无限之路「第一张地图」（实时算，不查表）：
        /// 取票数最多者；并列取 mapId 小者（Ordinal）保证结果确定。无票 / null → null。
        /// </summary>
        public static string? FirstMapByReturnMap(IReadOnlyDictionary<string, int>? votes)
        {
            if (votes == null || votes.Count == 0)
            {
                return null;
            }
            string? best = null;
            int bestVotes = 0;
            foreach (var kv in votes)
            {
                if (kv.Value > bestVotes
                    || (kv.Value == bestVotes && best != null && string.CompareOrdinal(kv.Key, best) < 0))
                {
                    best = kv.Key;
                    bestVotes = kv.Value;
                }
            }
            return best;
        }

        /// <summary>是否为硬编码忽略的 BGM（去首尾空白、大小写不敏感）。</summary>
        public static bool IsIgnoredBgm(string? bgm)
        {
            return !string.IsNullOrWhiteSpace(bgm)
                && bgm.Trim().Equals(IgnoredSilenceBgm, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 归一化 BGM 分类名（"Bgm00.img" / "Bgm00" 统一为无 .img 形态，与地图 info/bgm、
        /// WalkPathPlanner.TrackKeyOf 同口径）。空白 / 剥后缀后为空 → null。
        /// </summary>
        public static string? NormalizeImgCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return null;
            }
            var s = category.Trim();
            if (s.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^4];
            }
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// 归一化「曲目↔地图」反向索引的 trackKey："Bgm00.img/FloralLife" 与 "Bgm00/FloralLife"
        /// 统一为无 .img 形态（2026-09-15 定：存储 key 一律无 .img——此前存储带 .img、查询不带，
        /// 两边永远对不上，扫描即使完成也 100% miss，是「播放全兜底默认城镇」的第一根因）。
        /// 无斜杠 / 空段 / 空白 → null。
        /// </summary>
        public static string? NormalizeTrackKey(string? trackKey)
        {
            if (string.IsNullOrWhiteSpace(trackKey))
            {
                return null;
            }
            var s = trackKey.Trim();
            int slash = s.IndexOf('/');
            if (slash <= 0 || slash >= s.Length - 1)
            {
                return null;
            }
            string? img = NormalizeImgCategory(s[..slash]);
            if (img == null)
            {
                return null;
            }
            return img + "/" + s[(slash + 1)..].Trim();
        }

        /// <summary>
        /// 归一化地图 info/bgm 字段值为曲目引用。
        /// ⚠️ 探针实证：WZ 的 bgm 值形如 "Bgm58/The Beginnig of The Adventure"——
        /// 分类部分**无 .img 后缀**，必须补 ".img" 才能匹配 Sound 目录（"Bgm58" → "Bgm58.img"）。
        /// 无斜杠 / 空串 / null 一律返回 null（无法定位分类，视为无场景 BGM）。
        /// 硬编码忽略的 BGM（Bgm00/Silence）同样返回 null。
        /// 例："Bgm58/xxx" → MusicTrackRef(Img="Bgm58.img", Track="xxx")。
        /// </summary>
        public static MusicTrackRef? NormalizeBgm(string? bgm)
        {
            if (string.IsNullOrWhiteSpace(bgm))
            {
                return null;
            }
            // 硬编码忽略：Bgm00/Silence 是静音轨，项目内完全忽略（不视为场景 BGM）
            if (IsIgnoredBgm(bgm))
            {
                return null;
            }
            var s = bgm.Trim();
            // 用 IndexOf 而非 Split：曲目名理论上不含斜杠，但按「首斜杠前=分类、其余=曲目名」切割更稳
            int slash = s.IndexOf('/');
            if (slash <= 0 || slash >= s.Length - 1)
            {
                // 无斜杠 / 分类为空 / 曲目为空 → 无法归一化
                return null;
            }
            string img = s[..slash];
            string track = s[(slash + 1)..];
            if (!img.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                // bgm 值无 .img 后缀需归一化；已带后缀（防御）则不重复补
                img += ".img";
            }
            return new MusicTrackRef(img, track);
        }

        /// <summary>
        /// 下一首索引（队列推进数学）：
        /// - Sequential：current+1，越界回卷到 0（v0.2.0 显式口径——队列尾回卷队首持续循环播放，
        ///   是对 CONTEXT.md「队列」条目耗尽行为的本版本 override，非遗漏）；
        /// - RepeatOne：原值（单曲循环重播当前）；
        /// - Random：在 [0,count) 内均匀抽取，count&gt;1 时尽量不与 current 相同
        ///   （实现为「在其余 count-1 首上均匀取」，等价于抽中当前曲即重抽的均匀化）；
        /// - count&lt;=0 → -1（无队列可推进）。
        /// </summary>
        /// <param name="mode">播放模式</param>
        /// <param name="count">队列长度</param>
        /// <param name="current">当前索引（越界值先归一为 0）</param>
        /// <param name="rng">可注入随机源（默认 Random.Shared，测试可传固定种子）</param>
        public static int NextIndex(PlaybackMode mode, int count, int current, Random? rng = null)
        {
            if (count <= 0)
            {
                return -1;
            }
            if (current < 0 || current >= count)
            {
                // 非法当前索引（如初始 -1）归一为队首
                current = 0;
            }
            switch (mode)
            {
                case PlaybackMode.RepeatOne:
                {
                    return current;
                }
                case PlaybackMode.Random:
                {
                    return PickRandomOther(count, current, rng);
                }
                case PlaybackMode.Sequential:
                default:
                {
                    // 顺序模式 +1 越界回卷到 0
                    return (current + 1) % count;
                }
            }
        }

        /// <summary>
        /// 上一首索引（与 NextIndex 对称）：
        /// - Sequential：current-1，越界（&lt;0）回卷到队尾 count-1；
        /// - RepeatOne：原值；
        /// - Random：就换一首随机（与 NextIndex 同抽取逻辑，不回退历史）；
        /// - count&lt;=0 → -1。
        /// </summary>
        public static int PrevIndex(PlaybackMode mode, int count, int current, Random? rng = null)
        {
            if (count <= 0)
            {
                return -1;
            }
            if (current < 0 || current >= count)
            {
                current = 0;
            }
            switch (mode)
            {
                case PlaybackMode.RepeatOne:
                {
                    return current;
                }
                case PlaybackMode.Random:
                {
                    return PickRandomOther(count, current, rng);
                }
                case PlaybackMode.Sequential:
                default:
                {
                    // 顺序模式 -1 越界回卷到队尾
                    return current - 1 < 0 ? count - 1 : current - 1;
                }
            }
        }

        /// <summary>
        /// 曲目显示文案：ms&lt;=0（元数据缺失，如场景曲目 Ms 填 0）只显示曲名；
        /// 否则 "track (mm:ss)"——格式与 MusicTrack.Display 一致（TimeSpan 的分钟段 mm:ss）。
        /// </summary>
        public static string FormatDisplay(string track, int ms)
        {
            if (ms <= 0)
            {
                return track;
            }
            return $"{track} ({TimeSpan.FromMilliseconds(ms):mm\\:ss})";
        }

        /// <summary>
        /// 播放链路失败后的状态决策（自然播完 / 提交失败共用，无重试循环）：
        /// - oldStreamAlive=true（准备阶段失败：WZ 提取为 null / Prepare 抛错——活动流分毫未动）
        ///   → 保持 currentState 不变，仅上抛 PlaybackError（用户触发的失败保持原播放不动）；
        /// - oldStreamAlive=false（自然播完后继曲目准备失败 / RepeatOne 重备失败 / 提交段失败
        ///   ——旧流已终结或已确定性释放，「保持当前播放」不再可行）
        ///   → Stopped 终态 + 上抛 PlaybackError（无重试循环，本版本显式口径）。
        /// 返回 (下一播放状态, 是否上抛错误)。
        /// </summary>
        /// <param name="oldStreamAlive">失败发生时原活动流是否仍在正常播（false = 已终结/已释放）</param>
        /// <param name="currentState">失败发生时的播放状态（保持原状分支的返回值）</param>
        public static (PlaybackState NextState, bool RaiseError) DecideFailureOutcome(bool oldStreamAlive, PlaybackState currentState)
        {
            if (oldStreamAlive)
            {
                // 活动流未动：保持原播放，只报错
                return (currentState, true);
            }
            // 旧流已终结：终态 Stopped + 上抛（自然播完后的失败语义，RepeatOne 重备失败同口径）
            return (PlaybackState.Stopped, true);
        }

        /// <summary>
        /// V0.2.1 分类自然排序比较器：把字符串切成「非数字段 / 数字段」交替序列逐段比较——
        /// 数字段按**数值**比（"Bgm09" 数值 9 &lt; "Bgm012" 数值 12，修掉 Ordinal 会把
        /// "Bgm012" 插进 "Bgm01"/"Bgm02" 之间的缺陷），非数字段按字符序；
        /// 前导零不影响数值（"Bgm007" == "Bgm7" 时回退全串 Ordinal 保稳定确定性）。
        /// 纯函数无状态，GetCategories 与单测共用同一口径。
        /// </summary>
        public static int CompareNatural(string? a, string? b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }
            if (a is null)
            {
                return -1;
            }
            if (b is null)
            {
                return 1;
            }
            int ia = 0;
            int ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
                {
                    // 数字段：整段读出后剥前导零，先比长度再逐位比（免 long/BigInteger 也承载超长数字）
                    int startA = ia;
                    while (ia < a.Length && char.IsDigit(a[ia]))
                    {
                        ia++;
                    }
                    int startB = ib;
                    while (ib < b.Length && char.IsDigit(b[ib]))
                    {
                        ib++;
                    }
                    string stemA = a[startA..ia].TrimStart('0');
                    string stemB = b[startB..ib].TrimStart('0');
                    int byLen = stemA.Length.CompareTo(stemB.Length);
                    if (byLen != 0)
                    {
                        return byLen;
                    }
                    int byVal = string.CompareOrdinal(stemA, stemB);
                    if (byVal != 0)
                    {
                        return byVal;
                    }
                }
                else
                {
                    // 非数字段（或两侧类型不同）：逐字符 Ordinal 比较
                    int byChar = a[ia].CompareTo(b[ib]);
                    if (byChar != 0)
                    {
                        return byChar;
                    }
                    ia++;
                    ib++;
                }
            }
            // 公共前缀相等：短者在前；完全一致时回退全串 Ordinal 兜底（如 "Bgm07" vs "Bgm7" 保持稳定次序）
            if (ia < a.Length)
            {
                return 1;
            }
            if (ib < b.Length)
            {
                return -1;
            }
            return string.CompareOrdinal(a, b);
        }

        /// <summary>
        /// V0.2.1 随机跨节点抽取：从**全库范围** [0,count) 均匀抽一个索引且不命中 excludeIndex。
        /// - count&lt;=0 → -1（无可用曲目）；count==1 → 0（唯一曲目无可避让）；
        /// - excludeIndex 越界（&lt;0 或 &gt;=count，如当前曲不在全库中传 -1）→ 视为不排除直接均匀取；
        /// - 正常路径 = 在其余 count-1 个位置上均匀取（pick &gt;= excludeIndex 时右移一位跳过），
        ///   与 PickRandomOther 同数学、语义面向全库而非队列。
        /// </summary>
        /// <param name="count">候选总数（全库曲目数）</param>
        /// <param name="excludeIndex">需避开的候选索引；不排除任何候选传负值</param>
        /// <param name="rng">可注入随机源（默认 Random.Shared，测试可传固定种子）</param>
        public static int PickRandomAcross(int count, int excludeIndex, Random? rng = null)
        {
            var r = rng ?? Random.Shared;
            if (count <= 0)
            {
                return -1;
            }
            if (count == 1)
            {
                return 0;
            }
            if (excludeIndex < 0 || excludeIndex >= count)
            {
                return r.Next(count);
            }
            int pick = r.Next(count - 1);
            if (pick >= excludeIndex)
            {
                pick++;
            }
            return pick;
        }

        /// <summary>
        /// V0.2.1 本地曲目 Track 名规则：输入相对根目录的音频文件路径（含扩展名），输出入库 Track 名 =
        /// 去扩展名 + 分隔符统一为 '/'。Windows 的 Path.GetRelativePath 产 '\\' 必须归一
        /// （播放端 TryExtractBytes 以 '/' 反向 Replace 还原平台分隔符），子文件夹前缀保留
        /// （如 "subdir/song"）避免跨文件夹重名冲突。纯字符串运算无 IO，BuildLocalCore 与单测共用。
        /// </summary>
        public static string ToLocalTrackName(string relativePathWithExt)
        {
            if (string.IsNullOrEmpty(relativePathWithExt))
            {
                return string.Empty;
            }
            string ext = Path.GetExtension(relativePathWithExt);
            string stem = relativePathWithExt[..^ext.Length];
            return stem.Replace('\\', '/');
        }

        /// <summary>
        /// V0.2.1 支持的本地音频扩展名判定：*.mp3 / *.flac / *.wav，大小写不敏感。
        /// 入参可带前导点（".MP3"）或完整文件名（"x.Flac"，自动取最后一段扩展名）。
        /// </summary>
        public static bool IsSupportedAudioExtension(string fileNameOrExtension)
        {
            if (string.IsNullOrEmpty(fileNameOrExtension))
            {
                return false;
            }
            string ext = fileNameOrExtension.StartsWith('.')
                ? fileNameOrExtension
                : Path.GetExtension(fileNameOrExtension);
            return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        }

        // ====== private ======

        /// <summary>
        /// 随机抽取一个尽量不等于 current 的索引：在其余 count-1 个位置上均匀取
        /// （pick &gt;= current 时右移一位跳过 current）。count&lt;=1 时无处可避，返回 0。
        /// </summary>
        private static int PickRandomOther(int count, int current, Random? rng)
        {
            var r = rng ?? Random.Shared;
            if (count <= 1)
            {
                return 0;
            }
            int pick = r.Next(count - 1);
            if (pick >= current)
            {
                pick++;
            }
            return pick;
        }
    }
}
