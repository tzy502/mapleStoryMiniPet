using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniPet.Models;
using WzComparerR2.WzLib;

namespace MiniPet.Services
{
    /// <summary>每首曲目收录的有名字关联地图上限（用户口径：凑满 5 个有名字的就不再加，其余下略）。</summary>
    internal static partial class MusicCatalogLimits
    {
        public const int MaxNamedEntriesPerTrack = 5;
    }
    /// <summary>
    /// 音乐目录服务（设计 §11.2 + PLAN 步骤 4 并发纪律 + V0.2.1 本地源/曲目地图反向索引）：
    /// 纯数据服务，不含播放逻辑。
    /// - 目录：遍历 Sound 下 Bgm*.img 懒建曲目缓存（首次约 2.2 秒，UI 需 loading 提示）；
    /// - 本地音频库（V0.2.1）：ScanLocalLibraryAsync 递归扫本地 mp3/flac/wav 入独立缓存，
    ///   与 WZ 目录并列；GetCatalogAsync/Search 返回 WZ + 本地合并视图；
    /// - 曲目↔地图反向索引（V0.2.1）：GetTrackMapsAsync 后台单飞遍历 Map/Map/Map{0..9} 全部
    ///   地图 img 聚合 info/bgm + info/town，供 UI 做反向查询；
    /// - 搜索/分类：内存过滤（OrdinalIgnoreCase）；分类走数字感知自然排序；
    /// - 场景映射（F17）：mapId → bgm 按需单查（绝不预遍历全部地图）；
    /// - 并发纪律：目录枚举是同步 WzLib 锁内工作，整体包在 Task.Run 的**单飞任务**里执行
    ///   （缓存 Task 字段而非 bool 标志，并发调用复用同一在途任务）+ **代际（版本号）guard**——
    ///   任务启动记代际，完成后比对不发布过期缓存；await 侧拿到结果后再复核，不符抛
    ///   InvalidOperationException（WZ 已重载，目录过期），绝不返回脏数据；
    ///   本地扫描的 File IO 同样 Task.Run 单飞但**绝不持 WZ 锁**（与 WzLib 无关），独立代际。
    /// </summary>
    public class MusicCatalogService
    {
        /// <summary>本地音频曲目的 Img 占位标记（区别于 WZ 分类 "BgmXX.img"；播放端据此走文件读取分支）。</summary>
        public const string LocalImgTag = "__LOCAL__";

        /// <summary>「📁 本地」分类名：存在本地曲目时由 GetCategories 固定追加在列表最末。</summary>
        public const string LocalCategoryName = "📁 本地";

        private readonly WzService _wz;
        private readonly ConfigService? _config; // 反向索引扫描的独立 WZ 路径来源（可选）
        // 目录单飞与发布锁：保护 _catalogTask/_catalogCache/_catalogGen 三个字段
        private readonly object _catalogLock = new();
        // 单飞任务：缓存 Task 本身（而非 _loading bool 标志）——并发调用直接复用同一在途任务
        private Task<List<MusicTrack>>? _catalogTask;
        // 在途任务启动时的代际（供 await 侧复核）
        private int _catalogTaskGen;
        // 已发布的目录结果（Search/GetCategories/GetCatalogSnapshot 同步只读消费）；失效时置 null
        private List<MusicTrack>? _catalogCache;
        // 目录代际：InvalidateCatalog / WzReloaded 时自增；任务启动记代际，完成后比对，不符不发布
        private int _catalogGen;
        // 分类自然排序比较器（V0.2.1：数字感知，口径集中在 MusicDecisions.CompareNatural）
        private static readonly IComparer<string> NaturalComparer =
            Comparer<string>.Create(MusicDecisions.CompareNatural);

        // ====== 本地音乐库（V0.2.1） ======
        // 发布锁：保护 _localCache/_localTask/_localGen/_localRoot 四字段（与 WZ 的 _catalogLock 并列独立）
        private readonly object _localLock = new();
        // 已发布的本地曲目缓存（null = 未启用本地源）；与 WZ 缓存并列，WZ 重载不清本地
        private List<MusicTrack>? _localCache;
        // 本地扫描单飞任务（口径同目录构建：锁内缓存 Task 本身，并发调用复用在途）
        private Task<List<MusicTrack>>? _localTask;
        // 本地源代际：SetLocalRootAsync 设源/清除时自增；扫描完成比对不符不发布
        private int _localGen;
        // 当前生效的本地根目录（播放端提取分支读取）；扫描成功更新 / 清除或失败置 null
        private string? _localRoot;

        // ====== 曲目↔地图反向索引（V0.2.1，2026-08-27 重写：按节点懒查 + 逐图短锁 + 磁盘缓存） ======
        // 发布锁：保护 _reverseStore/_reversePendingImgs/_reverseScanTask/_reverseGen 四字段
        private readonly object _reverseLock = new();
        // 反向索引存储（trackKey → ≤5 条有名字地图切片 + 其余计数）；WZ 数据基本不变，跨会话磁盘缓存
        private Dictionary<string, TrackMapSlice> _reverseStore = new(StringComparer.Ordinal);
        // 待扫描的 BgmXX.img 集合（用户点开分类时逐个入队；防重复排队）
        private readonly HashSet<string> _reversePendingImgs = new(StringComparer.Ordinal);
        // 全量增量扫描任务（单飞：首个待查节点触发，一次遍历顺带补齐所有节点）
        private Task? _reverseScanTask;
        // 扫描代际：WZ 重载时自增使在途扫描结果作废
        private int _reverseGen;

        // ====== 原始全候选索引（V0.2.1 F18 第二产物，2026-08-28：与 UI 切片同源同扫） ======
        // 键 = 带 .img 的 trackKey（"Bgm58.img/曲名"，与 MusicTrack.Key 同形态）；
        // 值 = 该曲全部候选地图（不截断、不做 5 名封顶）——无限之路源图解析用；
        // 磁盘缓存独立文件 music-raw-bgm-index.json（schema 版本化，仅整轮完成时落盘）
        private Dictionary<string, List<RawBgmEntry>> _rawBgmIndex = new(StringComparer.Ordinal);
        // 原始索引是否已覆盖全库（整轮扫描成功收尾 / 完整缓存载入；WZ 重载/新轮启动时置 false）
        private bool _rawBgmComplete;

        public MusicCatalogService(WzService wz, ConfigService? config = null)
        {
            _wz = wz;
            _config = config;
            // WZ 重载 → 失效缓存与在途任务（WzService 不能反向依赖目录服务避免 DI 成环，故由此订阅；
            // 参数 = 新 WZ 代际号，本服务用自己的 _catalogGen 记代际，事件仅作失效触发）
            _wz.WzReloaded += OnWzReloaded;
            // 磁盘缓存随构造同步载入（几 MB 内小文件，毫秒级；WZ 数据基本不变故长期有效，
            // 仅 WZ 重载/手动刷新目录时删除重算——见 ResetReverseIndex）
            LoadReverseCacheFromDisk();
            // 第二产物（F18 原始全候选索引）独立文件独立 schema，一并构造期载入
            LoadRawBgmCacheFromDisk();
        }

        /// <summary>
        /// 获取全部曲目 = WZ 目录 + 本地缓存 合成视图（V0.2.1；本地未扫描则只 WZ）。
        /// WZ 部分首次调用后台遍历 Sound/Bgm*.img 建缓存，并发调用复用同一单飞任务。
        /// WZ 未加载 → InvalidOperationException（UI 据此显示「WZ 未加载，无法读取音乐」灰占位）；
        /// 构建期间发生 WZ 重载 → 目录过期，同样抛 InvalidOperationException（不返回脏数据）。
        /// 返回列表为拷贝，调用方可安全持有。
        /// </summary>
        public async Task<List<MusicTrack>> GetCatalogAsync(CancellationToken ct = default)
        {
            List<MusicTrack> wzList = await GetWzCatalogCoreAsync(ct).ConfigureAwait(false);
            // 合并本地缓存（锁外快照读，惯例同 Search/GetCategories：最坏拿到 null 或稍旧引用皆安全）
            var local = _localCache;
            if (local == null || local.Count == 0)
            {
                return wzList;
            }
            var merged = new List<MusicTrack>(wzList.Count + local.Count);
            merged.AddRange(wzList);
            merged.AddRange(local);
            return merged;
        }

        /// <summary>
        /// 全库只读快照（V0.2.1 新增）= WZ 缓存 + 本地缓存 合成拷贝：
        /// 供播放端随机跨节点抽取 / TryFindTrack 覆盖本地曲目等同步消费；
        /// WZ 缓存未就绪时若本地已扫描则仅返回本地；两者都未就绪 → null。
        /// </summary>
        public List<MusicTrack>? GetLibrarySnapshot()
        {
            var wz = _catalogCache;
            var local = _localCache;
            if (wz == null)
            {
                return local == null ? null : new List<MusicTrack>(local);
            }
            if (local == null || local.Count == 0)
            {
                return new List<MusicTrack>(wz);
            }
            var merged = new List<MusicTrack>(wz.Count + local.Count);
            merged.AddRange(wz);
            merged.AddRange(local);
            return merged;
        }

        /// <summary>
        /// 内存过滤搜索：曲名（Track）+ 分类名（Img）OrdinalIgnoreCase 包含匹配。
        /// V0.2.1 起覆盖 WZ + 本地合并视图（本地曲目的 Track 含子目录前缀，搜目录名同样命中）。
        /// 目录与本地均未就绪 → 空列表；关键字空白 → 全量拷贝（UI「输入即过滤」的空关键字态）。
        /// </summary>
        public List<MusicTrack> Search(string keyword)
        {
            var snapshot = GetLibrarySnapshot();
            if (snapshot == null)
            {
                return new List<MusicTrack>();
            }
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return snapshot;
            }
            return snapshot
                .Where(t => t.Track.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || t.Img.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// 分类名列表："Bgm00"…"Bgm93"——**去 .img 后缀**供 UI 分类栏直接显示（曲目的 Img 字段保留
        /// 完整 "Bgm00.img" 作为 Key 组成部分，二者由 UI 自行对应）。
        /// V0.2.1 排序口径：数字感知自然排序（MusicDecisions.CompareNatural——Bgm01&lt;Bgm02&lt;…
        /// &lt;Bgm09&lt;Bgm10&lt;…&lt;Bgm012，数值比较，修正 Ordinal 的 "Bgm012" 插进 "Bgm01"/"Bgm02" 缺陷）。
        /// 存在本地曲目时**固定追加「📁 本地」于列表最末**：人为聚合类目不参与自然排序口径
        /// （UI 选中该分类时按 MusicCatalogService.LocalImgTag 匹配曲目 Img 字段）。
        /// 目录未就绪 → 仅可能返回「📁 本地」（无则空列表）。
        /// </summary>
        public List<string> GetCategories()
        {
            var cache = _catalogCache;
            var local = _localCache;
            var categories = new List<string>();
            if (cache != null)
            {
                categories.AddRange(
                    cache.Select(t => t.Img)
                         .Where(img => img.EndsWith(".img", StringComparison.Ordinal))
                         .Select(img => img[..^4])
                         .Distinct()
                         .OrderBy(c => c, NaturalComparer));
            }
            if (local == null || local.Count == 0)
            {
                return categories;
            }
            categories.Add(LocalCategoryName);
            return categories;
        }

        /// <summary>
        /// 清空目录缓存并废弃在途任务（WZ 重载自动触发 / 设置页手动「刷新目录」调用）。
        /// 在途任务完成时因代际不符不会发布缓存；后续 GetCatalogAsync 重新起单飞构建。
        /// </summary>
        public void InvalidateCatalog()
        {
            lock (_catalogLock)
            {
                _catalogGen++;
                _catalogCache = null;
                _catalogTask = null;
            }
        }

        /// <summary>
        /// 查地图的场景曲目（F17）：读 WZ `Map/Map/Map{首字符}/{9位补零}.img/info/bgm` 字符串属性
        /// （路径构造与 MapService.GetMapWzPath 同惯例，此处额外做 9 位补零防短 id 落空）→
        /// MusicDecisions.NormalizeBgm 归一化 → 与目录里真实存在的曲目比对。
        /// 按需单查，绝不预遍历全部地图。查不到 / WZ 未加载 → null。
        /// 目录已就绪时 bgm 指向不存在的曲目也返回 null；目录未就绪返回归一化引用
        /// （下游 ExtractSound 自会校验存在性，失败保持当前播放不断曲）。
        /// </summary>
        public Task<MusicTrackRef?> GetSceneTrackAsync(string mapId)
        {
            if (string.IsNullOrWhiteSpace(mapId))
            {
                return Task.FromResult<MusicTrackRef?>(null);
            }
            if (!_wz.IsLoaded || _wz.WzRoot == null)
            {
                return Task.FromResult<MusicTrackRef?>(null);
            }
            MusicTrackRef? trackRef;
            try
            {
                var padded = mapId.Trim().PadLeft(9, '0');
                // GetStringProperty 内部走 FindNodeByPath（持 WzService 锁 + extractImage），与 MapService 读 info 字段同模式
                string? bgm = _wz.GetStringProperty($"Map/Map/Map{padded[0]}/{padded}.img/info/bgm");
                trackRef = MusicDecisions.NormalizeBgm(bgm);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] GetSceneTrackAsync({mapId}): {ex.Message}");
                trackRef = null;
            }
            if (trackRef == null)
            {
                return Task.FromResult<MusicTrackRef?>(null);
            }
            // 与目录里真实存在的曲目比对（Ordinal 精确，同 Key 语义）
            var cache = _catalogCache;
            if (cache != null)
            {
                bool exists = cache.Any(t => string.Equals(t.Img, trackRef.Img, StringComparison.Ordinal)
                                             && string.Equals(t.Track, trackRef.Track, StringComparison.Ordinal));
                return Task.FromResult<MusicTrackRef?>(exists ? trackRef : null);
            }
            return Task.FromResult<MusicTrackRef?>(trackRef);
        }

        /// <summary>
        /// 当前已发布的 WZ 目录快照（未就绪为 null；**仅 WZ 曲目**，不含本地，场景联动按需单查比对用）。
        /// 供按 Key 精确查找等同步只读消费，调用方不得修改。含本地的合成视图请用 GetLibrarySnapshot。
        /// </summary>
        public List<MusicTrack>? GetCatalogSnapshot()
        {
            return _catalogCache;
        }

        // ====== 本地音乐库 API（V0.2.1） ======

        /// <summary>当前生效的本地音乐根目录；未启用 / 扫描失败 / 已清除时为 null。</summary>
        public string? LocalRoot
        {
            get { lock (_localLock) { return _localRoot; } }
        }

        /// <summary>
        /// 扫描本地音频库：递归收集 root 下 *.mp3/*.flac/*.wav（大小写不敏感），生成
        /// MusicTrack(Img=__LOCAL__, Track=相对路径去扩展名, Ms=0, Channels=0, Frequency=0)。
        /// 子文件夹相对路径拼进 Track（如 "subdir/song"，'/' 分隔统一口径）避免跨文件夹重名冲突。
        /// 并发纪律：File IO 全程 Task.Run 内执行、**绝不持 WZ 锁**（与 WzLib 节点树无关）；
        /// 单飞 + 代际 guard 口径与 WZ 目录构建一致（并发复用同一在途扫描，完成后比对代际不符不发布）。
        /// 根目录不存在 → DirectoryNotFoundException；结果经 GetLibrarySnapshot/GetCatalogAsync 合成可见。
        /// </summary>
        public Task<List<MusicTrack>> ScanLocalLibraryAsync(string dir, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                throw new ArgumentException("本地音乐根目录不能为空", nameof(dir));
            }
            lock (_localLock)
            {
                if (_localTask != null && !_localTask.IsCompleted)
                {
                    // 单飞：并发调用复用同一在途扫描（后来者 ct 不并入，口径同 WZ 目录构建）
                    return GuardLocalStaleAsync(_localTask, _localGen);
                }
                _localGen++;
                int gen = _localGen;
                var build = BuildLocalAsync(dir.Trim(), gen, ct);
                _localTask = build;
                return GuardLocalStaleAsync(build, gen);
            }
        }

        /// <summary>
        /// 设置/清除本地音乐源入口（V0.2.1）：
        /// - path 为 null/空白 → 清除本地缓存（在途扫描按代际废弃），播放端本地分支随之失效；
        /// - 否则扫描入独立缓存并 InvalidateCatalog 重建「WZ + 本地」合成视图（下次取目录即重新拉起）。
        /// 扫描失败（目录不存在等）视作未启用本地源：stderr 记录、不留半份缓存，不向 UI 抛出。
        /// LocalMusicPath 的持久化由 UI/上层写入 MusicConfig（数据层只存，不在本服务引入配置依赖）。
        /// </summary>
        public async Task SetLocalRootAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ClearLocalState();
                InvalidateCatalog();
                return;
            }
            try
            {
                await ScanLocalLibraryAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 本地库扫描失败({path}): {ex.Message}");
                ClearLocalState();
            }
            InvalidateCatalog();
        }

        // ====== 曲目↔地图反向索引 API（V0.2.1，2026-08-27 重写） ======
        //
        // 设计口径（用户 2026-08-27 拍板）：
        //   ① 只按需查当前选中的 BgmXX 节点——打开音乐 tab 不触发任何扫描（修复全量预扫
        //      独占 WZ 锁 ~30s 导致的明显卡死）；
        //   ② 扫描逐图小段拿 _wz.WzLock（拿一张、放锁、再拿下一张），桌宠其它 WZ 操作随时插队；
        //   ③ 结果磁盘缓存（WZ 数据基本不变），第二次起秒出；WZ 重载/手动刷新目录才删除重算；
        //   ④ 每曲目最多收录 5 条**有名字**的关联地图（非隐藏优先：无名地图视为隐藏级，
        //      永不占名额只累计数），超出部分以 moreCount 计，UI 用「下略」代替展示。

        /// <summary>反向索引有增量发布后触发（构建线程任意线程触发，订阅方自行 Dispatcher.UIThread.Post）。</summary>
        public event Action? ReverseIndexChanged;

        /// <summary>
        /// 按节点懒查询：确保指定 BgmXX.img（如 "Bgm00.img"）的关联地图数据可用——
        /// 磁盘缓存/内存已命中则立即返回；否则入队并启动后台增量扫描（单飞，一次遍历顺带补齐所有节点）。
        /// fire-and-forget 调用即可，进度经 ReverseIndexChanged 事件推送。
        /// </summary>
        public void KickTrackMapsForImg(string? imgName)
        {
            if (string.IsNullOrEmpty(imgName) || !_wz.IsLoaded)
            {
                return;
            }
            lock (_reverseLock)
            {
                if (StoreHasImgLocked(imgName) || !_reversePendingImgs.Add(imgName))
                {
                    return;
                }
            }
            EnsureScanTask();
        }

        /// <summary>
        /// 同步读取某曲目的关联地图切片（内存直读不等待）：items ≤5 条有名字地图（城镇优先）；
        /// moreCount = 未收录的其余地图张数。无任何记录时返回 false（UI 副行整行隐藏）。
        /// </summary>
        public bool TryGetTrackMaps(string? trackKey, out IReadOnlyList<TrackMapRef> items, out int moreCount)
        {
            if (!string.IsNullOrEmpty(trackKey))
            {
                lock (_reverseLock)
                {
                    if (_reverseStore.TryGetValue(trackKey, out var slice))
                    {
                        items = slice.Items;
                        moreCount = slice.MoreCount;
                        return true;
                    }
                }
            }
            items = Array.Empty<TrackMapRef>();
            moreCount = 0;
            return false;
        }

        /// <summary>存储里是否已有该 img（"BgmXX.img"）名下的任意记录（用于去重入队判断，调用方持 _reverseLock）。</summary>
        private bool StoreHasImgLocked(string imgName)
        {
            string prefix = imgName + "/";
            foreach (var key in _reverseStore.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 原始全候选查询（F18 无限之路源图解析，V0.2.1）：返回 trackKey（带 .img，
        /// 如 "Bgm58.img/FloralLife"，与 MusicTrack.Key 同形态）被哪些地图用作 info/bgm——
        /// **全部候选，不截断**（区别于 TryGetTrackMaps 的 5 名 UI 切片）。
        /// - 索引已完整（FullComplete=true）：同步返回（可能空列表 = 全库确无地图用此曲）；
        /// - 索引未就绪：kick 共享扫描（复用 EnsureScanTask 单飞机制，扫描跑内部代际）并等待
        ///   本轮收场——**ct 只用于放弃调用方等待（Task.WhenAny 包装），绝不传进共享扫描**；
        ///   放弃时返回当前不完整快照（FullComplete=false，调用方自行走回退链）；
        /// - WZ 未加载：直接 FullComplete=false 空候选（不 kick）。
        /// </summary>
        public async Task<RawBgmResult> GetRawBgmCandidatesAsync(string trackKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(trackKey) || !_wz.IsLoaded)
            {
                return new RawBgmResult(false, Array.Empty<(string, bool)>());
            }
            // 等待轮数上限：防「扫描立即收场但代际反复作废」死循环（WZ 未加载/反复重载时快速放弃）
            const int MaxWaitRounds = 6;
            for (int round = 0; round < MaxWaitRounds; round++)
            {
                List<(string MapId, bool IsTown)> snapshot = SnapshotRawBgmCandidates(trackKey, out bool complete);
                if (complete)
                {
                    return new RawBgmResult(true, snapshot);
                }
                // 未就绪：kick 扫描（可能复用在途任务）→ WhenAny 等待本轮收场或调用方放弃
                Task scan = EnsureScanTask();
                Task giveUp = Task.Delay(Timeout.Infinite, ct);
                Task finished = await Task.WhenAny(scan, giveUp).ConfigureAwait(false);
                if (finished == giveUp)
                {
                    // 调用方放弃等待：返回当前不完整快照（扫描本身继续跑完，绝不被取消）
                    return new RawBgmResult(false, snapshot);
                }
                // 本轮扫描收场 → 回到快照复核（期间若代际作废由上限兜底退出）
            }
            return new RawBgmResult(false, SnapshotRawBgmCandidates(trackKey, out _));
        }

        /// <summary>锁内快照某曲目原始候选（值拷贝，调用方可安全持有；complete = 索引是否已覆盖全库）。</summary>
        private List<(string MapId, bool IsTown)> SnapshotRawBgmCandidates(string trackKey, out bool complete)
        {
            lock (_reverseLock)
            {
                complete = _rawBgmComplete;
                if (_rawBgmIndex.TryGetValue(trackKey, out List<RawBgmEntry>? list))
                {
                    var result = new List<(string, bool)>(list.Count);
                    foreach (RawBgmEntry e in list)
                    {
                        result.Add((e.MapId, e.IsTown));
                    }
                    return result;
                }
                return new List<(string, bool)>();
            }
        }

        /// <summary>
        /// 保证增量扫描任务在跑（单飞；已结束的任务不复用，重新起一轮覆盖新入队节点）。
        /// 返回在途/新起任务的句柄（GetRawBgmCandidatesAsync 用 Task.WhenAny 等待其收场——
        /// ct 绝不传进共享扫描，扫描只受内部代际约束）。
        /// </summary>
        private Task EnsureScanTask()
        {
            Task started;
            lock (_reverseLock)
            {
                if (_reverseScanTask != null && !_reverseScanTask.IsCompleted)
                {
                    return _reverseScanTask;
                }
                _reverseGen++;
                int gen = _reverseGen;
                // 新一轮重扫期间原始索引视为不完整（消费方走回退链等待本轮收场）
                _rawBgmComplete = false;
                var pending = new List<string>(_reversePendingImgs);
                _reversePendingImgs.Clear();
                started = Task.Run(() => ScanReverseCore(gen, pending));
                _reverseScanTask = started;
            }
            // 异步观察故障防 UnobservedTaskException；实际结果经事件推送
            started.ContinueWith(
                t => Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描收场: {t.Exception?.GetBaseException().Message ?? "完成"}"),
                TaskScheduler.Default);
            return started;
        }

        // ====== private ======

        /// <summary>
        /// WZ 目录获取核心（原 GetCatalogAsync 主体）：锁内只做任务句柄分发，await 一律发生在锁外
        /// （V0.2.1 因外层需要追加本地合并而拆出；单飞 + 代际 guard 口径与原实现完全一致）。
        /// </summary>
        private Task<List<MusicTrack>> GetWzCatalogCoreAsync(CancellationToken ct)
        {
            lock (_catalogLock)
            {
                if (_catalogCache != null)
                {
                    return Task.FromResult(new List<MusicTrack>(_catalogCache));
                }
                if (_catalogTask != null && !_catalogTask.IsCompleted)
                {
                    // 单飞：复用在途任务（后来者的 ct 不并入——单次构建只受首发者取消约束）
                    return GuardStaleAsync(_catalogTask, _catalogTaskGen);
                }
                // 已完成（成功已发布/失败/取消）的任务不复用——重新起一次构建
                _catalogGen++;
                int gen = _catalogGen;
                _catalogTaskGen = gen;
                var build = BuildCatalogAsync(gen, ct);
                _catalogTask = build;
                return GuardStaleAsync(build, gen);
            }
        }

        /// <summary>WZ 重载事件（参数 = 新 WZ 代际号）：目录失效重建 + 本地反向索引失效重建。</summary>
        private void OnWzReloaded(int generation)
        {
            InvalidateCatalog();
            // V0.2.1：反向索引基于同一棵 WZ 树，一并失效（下次 GetTrackMapsAsync 单飞重算）
            ResetReverseIndex();
        }

        /// <summary>清除本地源内存态：bump 代际废弃在途扫描 + 清空缓存与根目录（持 _localLock，原子）。</summary>
        private void ClearLocalState()
        {
            lock (_localLock)
            {
                _localGen++;
                _localCache = null;
                _localTask = null;
                _localRoot = null;
            }
        }

        /// <summary>
        /// 本地扫描单飞任务体：Task.Run 内做 File IO（绝不持 WZ 锁），完成后锁内比对启动代际——
        /// 不符（期间清除/换源）则不发布缓存，抛 InvalidOperationException 由 await 侧接收。
        /// </summary>
        private async Task<List<MusicTrack>> BuildLocalAsync(string rootDir, int gen, CancellationToken ct)
        {
            List<MusicTrack> built = await Task.Run(() => BuildLocalCore(rootDir, ct), ct).ConfigureAwait(false);
            lock (_localLock)
            {
                if (gen != _localGen)
                {
                    throw new InvalidOperationException("本地源已变更，扫描结果过期");
                }
                _localCache = built;
                _localRoot = rootDir;
            }
            return built;
        }

        /// <summary>
        /// await 侧代际复核包装（口径同 GuardStaleAsync）：拿到结果后再复核一次当前本地源代际，
        /// 不符抛 InvalidOperationException。
        /// </summary>
        private async Task<List<MusicTrack>> GuardLocalStaleAsync(Task<List<MusicTrack>> task, int gen)
        {
            List<MusicTrack> list = await task.ConfigureAwait(false);
            lock (_localLock)
            {
                if (gen != _localGen)
                {
                    throw new InvalidOperationException("本地源已变更，扫描结果过期");
                }
            }
            return new List<MusicTrack>(list);
        }

        /// <summary>
        /// 本地扫描核心：递归枚举（IgnoreInaccessible 容错不可访问子目录）+ 扩展名大小写不敏感过滤，
        /// 逐文件取消检查点；Track 名 = ToLocalTrackName（去扩展名 + '\' 归一 '/'）。
        /// 不校验音频可解码性（BASS 打开失败走播放端既有失败链路），文件名即曲名事实来源。
        /// </summary>
        private static List<MusicTrack> BuildLocalCore(string rootDir, CancellationToken ct)
        {
            if (!Directory.Exists(rootDir))
            {
                throw new DirectoryNotFoundException($"本地音乐目录不存在: {rootDir}");
            }
            var result = new List<MusicTrack>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };
            foreach (string file in Directory.EnumerateFiles(rootDir, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!MusicDecisions.IsSupportedAudioExtension(file))
                {
                    continue;
                }
                string rel = Path.GetRelativePath(rootDir, file);
                // 子文件夹相对路径拼进 Track（如 "subdir/song"），跨文件夹重名不冲突
                result.Add(new MusicTrack(LocalImgTag, MusicDecisions.ToLocalTrackName(rel), 0, 0, 0));
            }
            return result;
        }

        /// <summary>
        /// 增量扫描核心（后台线程执行）：遍历 Map0..Map9 全部地图 img，**逐图小段拿 _wz.WzLock**
        /// （拿一张读 info/bgm+town 就放锁，桌宠其它 WZ 操作随时插队——修复旧实现整段独占
        /// ~30 秒导致的卡死）；聚合遵守 5 名封顶口径（见 AccumulateTrackMap），每个分组收尾
        /// 增量发布 + 触发事件，整轮结束后落盘缓存。代际不符（WZ 已重载）静默废弃本轮。
        /// </summary>
        private void ScanReverseCore(int gen, List<string> pendingImgs)
        {
            if (!_wz.IsLoaded || _wz.WzRoot == null)
            {
                return;
            }
            var working = new Dictionary<string, TrackMapSlice>(_reverseStore, StringComparer.Ordinal);
            // 第二产物（F18）：原始全候选索引 working 副本（键同 trackKey 带 .img；全部候选不截断）
            var rawWorking = new Dictionary<string, List<RawBgmEntry>>(_rawBgmIndex, StringComparer.Ordinal);
            WzService? scanWz = null;
            try
            {
                // ★ 终极根因修复（白盒实证 2026-08-28）：全库扫描绝不可在主 WZ 实例上跑——
                // 21254 张 map img TryExtract 常驻会打坏进程内后续一切 LoadMap/ExtractPng（模式3「一个图都
                // 找不到」的真因）。改用**独立临时 WzService**（自 Load 自整体释放），主实例零污染。
                // 主实例仅用于 IsLoaded 前置判定。
                scanWz = new WzService();
                // 路径来源 = WzConfig（~/.minipet/wz-config.json，应用真实加载口径 App.axaml.cs:204 同源）——
                // 2026-08-29 白盒实证：此前误读 PetSettings.WzLibPath（恒空）→ 扫描静默放弃
                // → raw 索引永不建成 → 所有 BGM「无候选」全走回退随机镇
                var wzCfg = _config?.LoadWzConfig();
                string wzLib = wzCfg?.WzLibPath ?? "";
                string baseWz = wzCfg?.BaseWzPath ?? "";
                if (string.IsNullOrEmpty(wzLib) || string.IsNullOrEmpty(baseWz))
                {
                    Console.Error.WriteLine("[MusicCatalog] 反向索引扫描跳过：wz-config.json 路径未配置");
                    return;
                }
                var (scanOk, scanErr) = scanWz.LoadWz(wzLib, baseWz);
                if (!scanOk)
                {
                    Console.Error.WriteLine($"[MusicCatalog] 扫描 WZ 加载失败: {scanErr}");
                    return;
                }
                Wz_Node? mapMapNode;
                lock (scanWz.WzLock)
                {
                    mapMapNode = scanWz.WzRoot.FindNodeByPath(true, "Map", "Map");
                }
                if (mapMapNode == null)
                {
                    return;
                }
                for (int g = 0; g <= 9; g++)
                {
                    lock (_reverseLock)
                    {
                        if (gen != _reverseGen)
                        {
                            return; // WZ 已重载：整轮作废（新扫描任务会由重载后的 kick 重新拉起）
                        }
                    }
                    // 独立实例：无跨线程共享，直接遍历（WzLib 单线程约束在本扫描线程内自洽）
                    List<Wz_Node> imgNodes = new();
                    {
                        var groupNode = mapMapNode.FindNodeByPath($"Map{g}");
                        if (groupNode != null)
                        {
                            foreach (Wz_Node n in groupNode.Nodes)
                            {
                                if (n.Text.EndsWith(".img", StringComparison.Ordinal))
                                {
                                    imgNodes.Add(n);
                                }
                            }
                        }
                    }
                    bool changed = false;
                    bool rawChanged = false;
                    foreach (var imgNode in imgNodes)
                    {
                        // 短锁三：单张 img 的提取与字段读取（TryExtract 非线程安全必须持锁，
                        // 但只锁一张的耗时量级，锁外等待方最多等几毫秒）
                        List<KeyValuePair<string, TrackMapRef>> found = new();
                        lock (scanWz.WzLock)
                        {
                            try
                            {
                                var img = imgNode.GetValue<Wz_Image>();
                                if (img == null || !img.TryExtract())
                                {
                                    continue;
                                }
                                var info = img.Node.FindNodeByPath("info");
                                MusicTrackRef? trackRef =
                                    MusicDecisions.NormalizeBgm(info?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                                if (trackRef == null)
                                {
                                    continue;
                                }
                                int town = info?.FindNodeByPath("town")?.GetValueEx<int>(0) ?? 0;
                                string mapId = imgNode.Text[..^4];
                                var entry = new TrackMapRef(mapId, LookupMapName(mapId), town == 1);
                                found.Add(new KeyValuePair<string, TrackMapRef>($"{trackRef.Img}/{trackRef.Track}", entry));
                            }
                            catch (Exception ex)
                            {
                                // 单个地图 img 解析失败跳过，不中断整体构建
                                Console.Error.WriteLine($"[MusicCatalog] 反向索引解析 {imgNode.Text} 失败: {ex.Message}");
                            }
                        }
                        foreach (var kv in found)
                        {
                            changed |= AccumulateTrackMap(working, kv.Key, kv.Value);
                            // 同一聚合点写第二产物：全部候选不截断（同 img 单轮内只扫一次，天然无重复 mapId）；
                            // 无名地图不进 UI 切片（AccumulateTrackMap 返回 false）但必须进原始索引
                            if (!rawWorking.TryGetValue(kv.Key, out List<RawBgmEntry>? rawList))
                            {
                                rawList = new List<RawBgmEntry>();
                                rawWorking[kv.Key] = rawList;
                                rawChanged = true;
                            }
                            rawList.Add(new RawBgmEntry(kv.Value.MapId, kv.Value.IsTown));
                        }
                    }
                    // 分组收尾：增量发布（UI 即便中途进来也能看到已完成分区的数据）+ 触发刷新；
                    // UI 切片三件套（store/落盘/事件）只在 changed 时执行——与原实现逐字节等价，行为零改动；
                    // 原始索引独立发布（读方可见部分数据，但 _rawBgmComplete 仍为 false——消费方按
                    // FullComplete 语义走回退链，不拿半成品做最终决策）
                    if (changed || rawChanged)
                    {
                        lock (_reverseLock)
                        {
                            if (changed)
                            {
                                _reverseStore = working;
                                MergeSlicesFromPendingImgNames(pendingImgs, working);
                            }
                            if (rawChanged)
                            {
                                _rawBgmIndex = rawWorking;
                            }
                        }
                        if (changed)
                        {
                            PersistReverseCacheToDisk(working);
                            ReverseIndexChanged?.Invoke();
                        }
                    }
                }
                // 整轮收尾：原始索引标记全库完整 + 落盘（代际不符则整轮作废，不发布不落盘）；
                // UI 切片缓存沿用「每分组落盘」旧节奏，行为零改动
                bool commit;
                lock (_reverseLock)
                {
                    commit = gen == _reverseGen;
                    if (commit)
                    {
                        _rawBgmIndex = rawWorking;
                        _rawBgmComplete = true;
                    }
                }
                if (commit)
                {
                    PersistRawBgmCacheToDisk(rawWorking);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描失败: {ex.Message}");
            }
            finally
            {
                // 临时实例整体弃用（独立实例+局部引用，GC 回收其全部 img 原生资源）——主 WZ 实例零污染
                scanWz = null;
            }
        }

        /// <summary>
        /// 聚合一条「曲目 ← 地图」记录（5 名封顶口径）：有名字的地图（name != null）才有资格占
        /// ≤5 个展示名额，槽满后（含本条更好也不收）一律只累加 MoreCount；无名地图视为隐藏级
        /// 永不占名额。返回存储是否发生变化。
        /// </summary>
        private static bool AccumulateTrackMap(
            Dictionary<string, TrackMapSlice> store, string key, TrackMapRef entry)
        {
            store.TryGetValue(key, out var slice);
            // 首次入店 slice 为 null（default(TrackMapSlice)）：空集起步，勿触 slice.Items
            var items = slice?.Items as List<TrackMapRef> ?? new List<TrackMapRef>();
            int more = slice?.MoreCount ?? 0;
            bool isFull = items.Count >= MusicCatalogLimits.MaxNamedEntriesPerTrack;
            if (entry.MapName != null && !ContainsEntry(items, entry))
            {
                if (!isFull)
                {
                    items.Add(entry);
                    // 展示序：城镇优先，组内名称序（OrdinalIgnoreCase）——每次插入后就地重排保持不变式
                    items.Sort((a, b) =>
                    {
                        int byTown = b.IsTown.CompareTo(a.IsTown);
                        if (byTown != 0)
                        {
                            return byTown;
                        }
                        return string.Compare(a.MapName, b.MapName, StringComparison.OrdinalIgnoreCase);
                    });
                }
                else
                {
                    more++;
                }
                store[key] = new TrackMapSlice(items, more);
                return true;
            }
            return false;
        }

        /// <summary>切片去重（同 mapId 视为同图）。</summary>
        private static bool ContainsEntry(List<TrackMapRef> items, TrackMapRef entry)
        {
            foreach (var it in items)
            {
                if (string.Equals(it.MapId, entry.MapId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把本轮待查节点名合并进扫过的记录里（Kick 的去重判断基于存储前缀匹配；此处仅语义占位：
        /// 扫描覆盖全部节点，pending 天然被吸收）。
        /// </summary>
        private void MergeSlicesFromPendingImgNames(List<string> pendingImgs, Dictionary<string, TrackMapSlice> working)
        {
            pendingImgs.Clear(); // 单飞一轮即覆盖全库节点，无需按名裁剪
        }

        // ====== 反向索引磁盘缓存（.minipet 口径目录下 music-track-maps.json） ======

        /// <summary>缓存目录（.minipet 口径；Windows 优先 exe 同目录绿色便携，回退 ApplicationData）。</summary>
        private static string ReverseCacheDirectory()
        {
            if (OperatingSystem.IsMacOS() || !OperatingSystem.IsWindows())
            {
                var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dir = Path.Combine(home, ".minipet");
                Directory.CreateDirectory(dir);
                return dir;
            }
            // Windows：优先 exe 同目录（绿色便携口径，同 ConfigService.GetConfigBaseDir）
            try
            {
                var dir = AppContext.BaseDirectory;
                var probe = Path.Combine(dir, ".minipet-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "MiniPet");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string ReverseCacheFilePath()
        {
            return Path.Combine(ReverseCacheDirectory(), "music-track-maps.json");
        }

        /// <summary>原始全候选索引缓存文件（独立于 UI 切片缓存；schema 版本化，不符作废重建）。</summary>
        private static string RawBgmCacheFilePath()
        {
            return Path.Combine(ReverseCacheDirectory(), "music-raw-bgm-index.json");
        }

        /// <summary>构造时同步载入磁盘缓存（文件损坏静默放弃走重建，绝不影响启动）。</summary>
        private void LoadReverseCacheFromDisk()
        {
            try
            {
                string path = ReverseCacheFilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TrackMapSlice>>(
                    File.ReadAllText(path), ReverseCacheJsonOptions());
                if (loaded != null && loaded.Count > 0)
                {
                    lock (_reverseLock)
                    {
                        _reverseStore = new Dictionary<string, TrackMapSlice>(loaded, StringComparer.Ordinal);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 反向索引缓存载入失败: {ex.Message}");
            }
        }

        /// <summary>原子落盘（tmp + move）；写失败仅记日志不影响内存态。</summary>
        private void PersistReverseCacheToDisk(Dictionary<string, TrackMapSlice> store)
        {
            try
            {
                string path = ReverseCacheFilePath();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(store, ReverseCacheJsonOptions()));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 反向索引缓存落盘失败: {ex.Message}");
            }
        }

        /// <summary>反向索引缓存 JSON 选项（camelCase + 大小写不敏感读回，对齐 ConfigService 风格）。</summary>
        private static JsonSerializerOptions ReverseCacheJsonOptions() => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        // ====== 原始全候选索引磁盘缓存（music-raw-bgm-index.json，schema 版本化） ======

        /// <summary>缓存 schema 版本（头部字段；不符即整文件作废重建）。</summary>
        private const int RawBgmCacheSchema = 1;

        /// <summary>
        /// 构造时同步载入原始索引缓存：文件仅在整轮扫描成功后落盘（见 ScanReverseCore 整轮收尾），
        /// 故文件存在且 schema 相符 ⟹ 上轮全库完整 → 直接置 _rawBgmComplete；
        /// schema 不符/文件损坏 → 静默作废（删除文件走重建，绝不影响启动与 UI 切片缓存）。
        /// </summary>
        private void LoadRawBgmCacheFromDisk()
        {
            try
            {
                string path = RawBgmCacheFilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                var file = JsonSerializer.Deserialize<RawBgmCacheFile>(
                    File.ReadAllText(path), ReverseCacheJsonOptions());
                if (file == null || file.Schema != RawBgmCacheSchema)
                {
                    // 版本不符：作废重建（下次 GetRawBgmCandidatesAsync kick 全量扫描）
                    TryDeleteCacheFile(path);
                    return;
                }
                lock (_reverseLock)
                {
                    _rawBgmIndex = file.Entries != null
                        ? new Dictionary<string, List<RawBgmEntry>>(file.Entries, StringComparer.Ordinal)
                        : new Dictionary<string, List<RawBgmEntry>>(StringComparer.Ordinal);
                    _rawBgmComplete = true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 原始索引缓存载入失败: {ex.Message}");
            }
        }

        /// <summary>原始索引原子落盘（tmp + move，头部 {"schema":1}）；只在整轮扫描成功后调用一次。</summary>
        private void PersistRawBgmCacheToDisk(Dictionary<string, List<RawBgmEntry>> index)
        {
            try
            {
                string path = RawBgmCacheFilePath();
                string tmp = path + ".tmp";
                var file = new RawBgmCacheFile(RawBgmCacheSchema, index);
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, ReverseCacheJsonOptions()));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 原始索引缓存落盘失败: {ex.Message}");
            }
        }

        /// <summary>尽力删除缓存文件（失败仅记日志）。</summary>
        private static void TryDeleteCacheFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 缓存文件删除失败({path}): {ex.Message}");
            }
        }

        /// <summary>WZ 重载时清空反向索引 + 删除磁盘缓存 + 废弃在途扫描（bump 代际使旧结果不可发布）。</summary>
        private void ResetReverseIndex()
        {
            lock (_reverseLock)
            {
                _reverseGen++;
                _reverseStore = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
                _reversePendingImgs.Clear();
                // 第二产物（F18 原始索引）一并失效：内存清空 + 标记不完整（新轮 kick 后重建）
                _rawBgmIndex = new Dictionary<string, List<RawBgmEntry>>(StringComparer.Ordinal);
                _rawBgmComplete = false;
            }
            try
            {
                string path = ReverseCacheFilePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 反向索引缓存删除失败: {ex.Message}");
            }
            try
            {
                string rawPath = RawBgmCacheFilePath();
                if (File.Exists(rawPath))
                {
                    File.Delete(rawPath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 原始索引缓存删除失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 地图名尽量从已有地图名缓存取（转发 WzService.GetMapName → EnsureMapCatalog 后台产物；
        /// 后台未完成则全部 miss）。GetMapName 的 miss 哨兵形态是回退串 "map_{id}"，
        /// 在此归一为 null（UI 自行显示 MapId），绝不为无名地图编造名称。
        /// </summary>
        private string? LookupMapName(string mapId)
        {
            string name = _wz.GetMapName(mapId);
            return name == $"map_{mapId}" ? null : name;
        }

        /// <summary>
        /// 单飞构建任务：Task.Run 内持 WzService 锁同步遍历（Wz_Image.TryExtract 非线程安全必须串行化），
        /// 完成后在锁内比对启动代际——不符（WZ 已重载）则**不发布缓存**。
        /// </summary>
        private async Task<List<MusicTrack>> BuildCatalogAsync(int gen, CancellationToken ct)
        {
            List<MusicTrack> built = await Task.Run(() => BuildCatalogCore(ct), ct).ConfigureAwait(false);
            lock (_catalogLock)
            {
                if (gen != _catalogGen)
                {
                    // WZ 已重载，目录过期：不发布缓存（抛错由 await 侧接收）
                    throw new InvalidOperationException("WZ 已重载，目录过期");
                }
                _catalogCache = built;
            }
            return built;
        }

        /// <summary>
        /// await 侧代际复核包装：拿到结果后再比对一次当前代际，不符抛 InvalidOperationException
        /// （覆盖「构建发布成功后、await 恢复执行前」窗口内发生的 WZ 重载）。
        /// </summary>
        private async Task<List<MusicTrack>> GuardStaleAsync(Task<List<MusicTrack>> task, int gen)
        {
            List<MusicTrack> list = await task.ConfigureAwait(false);
            lock (_catalogLock)
            {
                if (gen != _catalogGen)
                {
                    throw new InvalidOperationException("WZ 已重载，目录过期");
                }
            }
            return new List<MusicTrack>(list);
        }

        /// <summary>
        /// 锁内遍历 Sound 下 Bgm*.img → Wz_Image.TryExtract → **img.Node.Nodes**（⚠️ 探针实证：
        /// 外层 img 节点的 Nodes 恒为 0，必须读 img.Node.Nodes）→ 每个 Wz_Sound 子节点生成一首
        /// MusicTrack（Ms/Channels/Frequency 直接读 Wz_Sound 属性）。
        /// 整体持 _wzLock（与 MapCatalogService.BuildMapNameCache 同模式：一次长临界区完成全量遍历）。
        /// WZ 未加载抛 InvalidOperationException（不发布空目录，否则缓存短路导致 WZ 加载后仍返回空）。
        /// </summary>
        private List<MusicTrack> BuildCatalogCore(CancellationToken ct)
        {
            if (!_wz.IsLoaded || _wz.WzRoot == null)
            {
                throw new InvalidOperationException("WZ 未加载，无法读取音乐目录");
            }
            var result = new List<MusicTrack>();
            lock (_wz.WzLock)
            {
                try
                {
                    var soundDir = _wz.WzRoot.FindNodeByPath(true, "Sound");
                    if (soundDir == null)
                    {
                        return result;
                    }
                    foreach (Wz_Node imgNode in soundDir.Nodes)
                    {
                        // 只收 Bgm*.img（Sound 下另有 NpcSound/UITransSnd 等非 BGM 分类，不进目录）
                        if (!imgNode.Text.StartsWith("Bgm", StringComparison.OrdinalIgnoreCase)
                            || !imgNode.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var img = imgNode.GetValue<Wz_Image>();
                            if (img == null || !img.TryExtract())
                            {
                                continue;
                            }
                            // ⚠️ 外层 imgNode.Nodes 恒 0，必须读 img.Node.Nodes
                            foreach (Wz_Node trackNode in img.Node.Nodes)
                            {
                                var sound = trackNode.GetValue<Wz_Sound>();
                                if (sound == null)
                                {
                                    continue;
                                }
                                result.Add(new MusicTrack(imgNode.Text, trackNode.Text, sound.Ms, sound.Channels, sound.Frequency));
                            }
                        }
                        catch (Exception ex)
                        {
                            // 单个 img 解析失败跳过，不中断整体构建
                            Console.Error.WriteLine($"[MusicCatalog] 解析 {imgNode.Text} 失败: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MusicCatalog] 目录构建失败: {ex.Message}");
                }
            }
            return result;
        }
    }

    /// <summary>
    /// 曲目 ↔ 地图 反向索引条目（V0.2.1）：某曲目被哪些地图用作 info/bgm。
    /// （命名 TrackMapRef 与 SettingsWindow.Music 的消费契约对齐；record 值等价可比较。）
    /// </summary>
    /// <param name="MapId">地图 id（WZ 文件名去 .img 后缀，9 位补零形态）。</param>
    /// <param name="MapName">地图显示名（来自已有地图名缓存；缓存未就绪或无名时 null，UI 回退显示 MapId）。</param>
    /// <param name="IsTown">是否村庄地图（同 img 的 info/town 整型属性 == 1；探针实证取值仅 0/1）。</param>
    public sealed record TrackMapRef(string MapId, string? MapName, bool IsTown);

    /// <summary>
    /// 单首曲目的关联地图切片：Items ≤5 条**有名字**的地图（城镇优先，组内名称序）；
    /// MoreCount = 未收录的其余地图张数（无名/超额），UI 以「…下略」代替展示。
    /// </summary>
    public sealed record TrackMapSlice(IReadOnlyList<TrackMapRef> Items, int MoreCount);

    /// <summary>
    /// 原始索引单条候选（F18）：某曲目被某地图用作 info/bgm 的记录（mapId + 是否城镇）。
    /// 区别于 TrackMapRef：无名称字段、不做 5 名封顶——全部候选保真供无限之路选源图。
    /// </summary>
    /// <param name="MapId">地图 id（WZ 文件名去 .img 后缀，9 位补零形态）。</param>
    /// <param name="IsTown">是否村庄地图（info/town == 1）。</param>
    public sealed record RawBgmEntry(string MapId, bool IsTown);

    /// <summary>
    /// GetRawBgmCandidatesAsync 返回（F18）：
    /// FullComplete = 索引是否已覆盖全库（true 时 Candidates 为最终事实，空 = 确无地图用此曲）；
    /// Candidates = 全部候选（不截断；FullComplete=false 时为当前部分快照，只作参考不作最终决策）。
    /// </summary>
    public sealed record RawBgmResult(bool FullComplete, IReadOnlyList<(string MapId, bool IsTown)> Candidates);

    /// <summary>原始索引缓存文件载荷（music-raw-bgm-index.json）：头部 schema 版本化，不符整文件作废重建。</summary>
    internal sealed record RawBgmCacheFile(int Schema, Dictionary<string, List<RawBgmEntry>>? Entries);
}
