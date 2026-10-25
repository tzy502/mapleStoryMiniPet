using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        // 「曲目→有序候选图」缓存（胶水 2026-09-15 规则 2，排序 + town/returnMap 的 WZ 读一次成型）：
        // cacheKey = trackKey 或 "trackKey|currentBgmKey" → 有序 mapId 列表；有效期绑 _reverseGen
        //（新扫描轮发布 / WZ 重载即整体失效，懒清理），避免每次选图重做带锁 WZ 读
        private readonly Dictionary<string, List<string>> _orderedMapsCache = new(StringComparer.Ordinal);
        private int _orderedMapsCacheGen = -1;

        /// <summary>
        /// returnMap 指向图的 BGM 查询注入点（town 合格校验用，规则 2 补充）：
        /// mapId → 归一化曲目 key（"BgmXX/track" 无 .img 口径，与 NormalizeTrackKey 产出可比）。
        /// null（默认）= 生产路径走 ReadMapBgmTrackKey（WZ 读）；测试注入桩免 WZ。
        /// </summary>
        internal Func<string, string?>? ReturnMapBgmLookup { get; set; }

        public MusicCatalogService(WzService wz)
        {
            _wz = wz;
            // WZ 重载 → 失效缓存与在途任务（WzService 不能反向依赖目录服务避免 DI 成环，故由此订阅；
            // 参数 = 新 WZ 代际号，本服务用自己的 _catalogGen 记代际，事件仅作失效触发）
            _wz.WzReloaded += OnWzReloaded;
            // 磁盘缓存随构造同步载入（几 MB 内小文件，毫秒级；WZ 数据基本不变故长期有效，
            // 仅 WZ 重载/手动刷新目录时删除重算——见 ResetReverseIndex）
            LoadReverseCacheFromDisk();
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
        /// <summary>
        /// 启动预热：后台**全量**扫描「曲目 ← 地图」反向索引（ScanReverseCore 会遍历 Map0..Map9 的全部 img，
        /// 两万张量级，耗时以分钟计）。结果落盘（PersistReverseCacheToDisk），下次启动由
        /// LoadReverseCacheFromDisk 秒读。不预热的话，用户首次播放时索引必然没就绪 → 全部走默认主城兜底。
        /// </summary>
        public void WarmUpReverseIndex()
        {
            if (!_wz.IsLoaded)
            {
                return;
            }
            lock (_reverseLock)
            {
                _reversePendingImgs.Add("__warmup__");
            }
            EnsureScanTask();
        }

        public void KickTrackMapsForImg(string? imgName)
        {
            if (string.IsNullOrEmpty(imgName) || !_wz.IsLoaded)
            {
                return;
            }
            // 归一化："Bgm00.img" / "Bgm00" 统一为无 .img 形态（与存储 key、WalkPathPlanner.TrackKeyOf 同口径）
            string? norm = MusicDecisions.NormalizeImgCategory(imgName);
            if (norm == null)
            {
                return;
            }
            lock (_reverseLock)
            {
                if (StoreHasImgLocked(norm) || !_reversePendingImgs.Add(norm))
                {
                    return;
                }
            }
            EnsureScanTask();
        }

        /// <summary>
        /// 同步读取某曲目的关联地图切片（内存直读不等待）：items ≤5 条有名字地图（城镇优先）；
        /// moreCount = 未收录的其余地图张数。无任何记录时返回 false（UI 副行整行隐藏）。
        /// trackKey 兼容 "Bgm00.img/xxx" 与 "Bgm00/xxx" 两种形态（内部归一化，2026-09-15）。
        /// </summary>
        public bool TryGetTrackMaps(string? trackKey, out IReadOnlyList<TrackMapRef> items, out int moreCount)
        {
            var key = MusicDecisions.NormalizeTrackKey(trackKey);
            if (key != null)
            {
                lock (_reverseLock)
                {
                    if (_reverseStore.TryGetValue(key, out var slice))
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

        /// <summary>存储里是否已有该分类（"Bgm00"，无 .img）名下的任意记录（用于去重入队判断，调用方持 _reverseLock）。</summary>
        private bool StoreHasImgLocked(string imgCategory)
        {
            string prefix = imgCategory + "/";
            foreach (var key in _reverseStore.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>保证增量扫描任务在跑（单飞；已结束的任务不复用，重新起一轮覆盖新入队节点）。</summary>
        private void EnsureScanTask()
        {
            Task? toAwait = null;
            lock (_reverseLock)
            {
                if (_reverseScanTask != null && !_reverseScanTask.IsCompleted)
                {
                    return;
                }
                _reverseGen++;
                int gen = _reverseGen;
                var pending = new List<string>(_reversePendingImgs);
                _reversePendingImgs.Clear();
                _reverseScanTask = Task.Run(() => ScanReverseCore(gen, pending));
                toAwait = _reverseScanTask;
            }
            // 异步观察故障防 UnobservedTaskException；实际结果经事件推送
            toAwait?.ContinueWith(
                t => Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描收场: {t.Exception?.GetBaseException()}"),
                TaskScheduler.Default);
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
        /// 全量扫描核心（后台单飞线程）：2026-09-15 重写。
        /// 旧实现的两个死因——① AccumulateTrackMap 对首条记录 NRE（扫描开局即崩，从未产出过数据）；
        /// ② 存储 key 带 .img 而查询 key 不带（即使扫完也 100% miss）。新实现：
        /// - 共享树只做**名字快照 + 定位 Map 数据目录**（短锁、零解压，不与渲染抢 WzLock）；
        /// - 解压/读 info 交给**每任务独立 Wz_Structure**（按 Map0..Map9 分组文件夹加载，互不共享
        ///   节点树 → 无需 WzLock，不阻塞桌宠其它 WZ 操作；逐图 Unextract 控内存）；
        /// - 大组（Map9 一万+ 张）按 3000 张切片消长尾；分组文件夹布局不符（非 mxd 结构）→ 回退
        ///   旧串行路径（共享树 + 逐图短锁，保底正确性）；
        /// - worker 只产出 (trackKey, TrackMapRef)，**聚合仍单线程**走 AccumulateTrackMap（5 名封顶
        ///   口径与既有语义完全一致），名字在聚合时补（MapCatalogService 缓存字典命中，纯内存）；
        /// - 结束统一：代际校验 → 发布快照 → 落盘 → ReverseIndexChanged。
        /// 探针实测（--bgmscan，2026-09-15，本机 mxd Data）：串行 30.5s / 并行x4 10.2s，双方
        /// 907 曲目 / 19871 条目完全一致。
        /// </summary>
        private void ScanReverseCore(int gen, List<string> pendingImgs)
        {
            if (!_wz.IsLoaded || _wz.WzRoot == null)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                // 短锁一：共享树名字快照 + 样本 img 定位 Map 数据目录（只读名字，不解压）
                var groups = new List<KeyValuePair<int, List<string>>>(10);
                string? mapDir = null;
                lock (_wz.WzLock)
                {
                    // ⚠️ WzRoot 可能在方法入口判空之后被 WZ 重载置空（_wzs 换引用不持此锁），
                    // 此处必须再 `?.` 兜底，否则 reload 竞态窗口内即 NRE（2026-09-15 加固）
                    var mapMapNode = _wz.WzRoot?.FindNodeByPath(true, "Map", "Map");
                    if (mapMapNode == null)
                    {
                        return;
                    }
                    Wz_Image? sample = null;
                    for (int g = 0; g <= 9; g++)
                    {
                        var ids = new List<string>();
                        var groupNode = mapMapNode.FindNodeByPath($"Map{g}");
                        if (groupNode != null)
                        {
                            foreach (Wz_Node n in groupNode.Nodes)
                            {
                                if (!n.Text.EndsWith(".img", StringComparison.Ordinal))
                                {
                                    continue;
                                }
                                ids.Add(n.Text[..^4]);
                                if (sample == null)
                                {
                                    sample = n.GetValue<Wz_Image>();
                                }
                            }
                        }
                        groups.Add(new KeyValuePair<int, List<string>>(g, ids));
                    }
                    mapDir = DiscoverMapDir(sample);
                }
                int totalImgs = groups.Sum(x => x.Value.Count);
                if (totalImgs == 0)
                {
                    return;
                }

                // 并行扫描（独立结构，零 WzLock）→ 失败/布局不符回退串行（共享树短锁）
                var sink = new ConcurrentQueue<(string Key, TrackMapRef Entry)>();
                bool parallel = mapDir != null && ScanParallel(gen, groups, mapDir, sink);
                if (!parallel)
                {
                    ScanSerialFallback(gen, groups, sink);
                }
                if (IsReverseStale(gen))
                {
                    Console.Error.WriteLine("[MusicCatalog] 反向索引扫描作废（WZ 已重载）");
                    return;
                }

                // 聚合（单线程消费者，口径同旧实现）+ 地图名补全（MapCatalogService 缓存字典，纯内存命中）
                var working = new Dictionary<string, TrackMapSlice>(_reverseStore, StringComparer.Ordinal);
                int drained = 0;
                while (sink.TryDequeue(out var item))
                {
                    var named = item.Entry.MapName != null
                        ? item.Entry
                        : item.Entry with { MapName = LookupMapName(item.Entry.MapId) };
                    AccumulateTrackMap(working, item.Key, named, named.ReturnMap);
                    drained++;
                }
                if (drained == 0)
                {
                    // 一无所获（异常数据）：不发布不落盘，避免清空已有缓存
                    Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描 0 命中（{totalImgs} imgs，{sw.Elapsed.TotalSeconds:F1}s），跳过发布");
                    return;
                }

                // 发布 + 落盘（快照替换，避免读者看到正在聚合的容器）
                lock (_reverseLock)
                {
                    if (gen != _reverseGen)
                    {
                        Console.Error.WriteLine("[MusicCatalog] 反向索引扫描作废（WZ 已重载）");
                        return;
                    }
                    _reverseStore = new Dictionary<string, TrackMapSlice>(working, StringComparer.Ordinal);
                    MergeSlicesFromPendingImgNames(pendingImgs, working);
                }
                PersistReverseCacheToDisk(working);
                ReverseIndexChanged?.Invoke();
                Console.Error.WriteLine(
                    $"[MusicCatalog] 反向索引扫描完成: {drained} 条目 / {_reverseStore.Count} 曲目，"
                    + $"耗时 {sw.Elapsed.TotalSeconds:F1}s（{(parallel ? "并行" : "串行回退")}）");
            }
            catch (Exception ex)
            {
                // 完整堆栈（2026-09-15）：此前只打 Message，NRE 无法定位行号，扫描静默死亡 90s 全 null
                Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描失败: {ex}");
            }
        }

        /// <summary>代际是否已过期（WZ 已重载，本轮结果应作废）。</summary>
        private bool IsReverseStale(int gen)
        {
            lock (_reverseLock)
            {
                return gen != _reverseGen;
            }
        }

        /// <summary>
        /// 从样本地图 img 反查其物理 WZ 文件路径，推导 Map0..Map9 分组文件夹的父目录。
        /// mxd 结构样本形如 &lt;…&gt;/Map/Map/Map0/Map0_000.wz → 上两级。拿不到 / 目录不存在 → null（回退串行）。
        /// </summary>
        private static string? DiscoverMapDir(Wz_Image? sample)
        {
            try
            {
                string? p = (sample?.WzFile as Wz_File)?.FileStream as FileStream switch
                {
                    FileStream fs => fs.Name,
                    _ => null
                };
                if (string.IsNullOrEmpty(p))
                {
                    return null;
                }
                string? groupDir = Path.GetDirectoryName(p);
                string? mapDir = groupDir == null ? null : Path.GetDirectoryName(groupDir);
                if (mapDir == null || !Directory.Exists(mapDir))
                {
                    return null;
                }
                return mapDir;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 并行扫描：Map0..Map9 各组分片（大组按 3000 张切片消长尾），每任务独立 Wz_Structure 加载
        /// 所在分组文件夹（mxd 结构：MapX.wz + MapX_000.wz 合并），互不共享节点树 → 全程不持 WzLock。
        /// 任一分片失败 → 清空 sink 返回 false（上层整体回退串行，避免半份重复数据）。
        /// </summary>
        private bool ScanParallel(
            int gen, List<KeyValuePair<int, List<string>>> groups, string mapDir,
            ConcurrentQueue<(string Key, TrackMapRef Entry)> sink)
        {
            // 分组文件夹布局预检（非 mxd 结构直接回退，不浪费加载）
            foreach (var kv in groups)
            {
                if (kv.Value.Count > 0
                    && !File.Exists(Path.Combine(mapDir, "Map" + kv.Key, "Map" + kv.Key + ".wz")))
                {
                    return false;
                }
            }
            int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            const int chunkSize = 3000;
            var chunks = new List<(string Folder, List<string> Ids, int From, int Count)>();
            foreach (var kv in groups)
            {
                if (kv.Value.Count == 0)
                {
                    continue;
                }
                string folder = Path.Combine(mapDir, "Map" + kv.Key);
                for (int from = 0; from < kv.Value.Count; from += chunkSize)
                {
                    chunks.Add((folder, kv.Value, from, Math.Min(chunkSize, kv.Value.Count - from)));
                }
            }
            Console.Error.WriteLine($"[MusicCatalog] 反向索引并行扫描: {chunks.Count} 分片 x {degree} 线程");
            var failures = new ConcurrentQueue<Exception>();
            Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = degree }, chunk =>
            {
                try
                {
                    ScanWorkerChunk(chunk.Folder, chunk.Ids, chunk.From, chunk.Count, sink, gen);
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });
            if (!failures.IsEmpty)
            {
                sink.Clear();
                string firstMsg = failures.TryPeek(out var firstFail) ? firstFail.Message : "(unknown)";
                Console.Error.WriteLine(
                    $"[MusicCatalog] 并行扫描分片失败 x{failures.Count}: {firstMsg} → 串行回退");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 并行分片体：独立 Wz_Structure 加载一个分组文件夹（目录级加载，毫秒级），逐 img
        /// TryExtract → 读 info/bgm + town + returnMap → **Unextract**（独立结构内无人共享，绝对安全，
        /// 内存不随扫描累积）。产出 (trackKey 无 .img 口径, TrackMapRef[名=待补]) 入队。
        /// </summary>
        private void ScanWorkerChunk(
            string folder, List<string> ids, int from, int count,
            ConcurrentQueue<(string Key, TrackMapRef Entry)> sink, int gen)
        {
            Wz_Structure.DefaultAutoDetectExtFiles = true;
            Wz_Structure.DefaultImgCheckDisabled = true;
            var wzs = new Wz_Structure();
            Wz_Node? root = null;
            wzs.LoadWzFolder(folder, ref root, false);
            if (root == null)
            {
                throw new InvalidOperationException($"分组目录加载失败: {folder}");
            }
            int end = Math.Min(from + count, ids.Count);
            for (int i = from; i < end; i++)
            {
                if ((i - from) % 256 == 0 && IsReverseStale(gen))
                {
                    return; // 代际过期：中止分片（上层发现代际不符后整轮作废，不会发布半份数据）
                }
                string id = ids[i];
                Wz_Node? node = null;
                try
                {
                    node = root.FindNodeByPath(id + ".img");
                }
                catch
                {
                    // 路径解析失败按无 BGM 处理
                }
                Wz_Image? img = node?.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract())
                {
                    continue;
                }
                try
                {
                    var info = img.Node?.FindNodeByPath("info");
                    var trackRef = MusicDecisions.NormalizeBgm(info?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                    if (trackRef == null)
                    {
                        continue;
                    }
                    int town = info?.FindNodeByPath("town")?.GetValueEx<int>(0) ?? 0;
                    string returnMap = info?.FindNodeByPath("returnMap")?.GetValueEx<string>(null) ?? "";
                    var entry = new TrackMapRef(id, null, town == 1, string.IsNullOrEmpty(returnMap) ? null : returnMap);
                    string key = MusicDecisions.NormalizeTrackKey($"{trackRef.Img}/{trackRef.Track}")
                        ?? $"{trackRef.Img}/{trackRef.Track}";
                    sink.Enqueue((key, entry));
                }
                finally
                {
                    img.Unextract();
                }
            }
        }

        /// <summary>
        /// 串行回退（分组文件夹布局不符 / 并行失败时）：旧路径——共享树 + 逐图短锁拿 _wz.WzLock，
        /// 解压读字段后即刻放锁，桌宠其它 WZ 操作随时插队。产出与并行同形态的 sink 条目
        /// （名字在此内联补全，与旧实现一致）。
        /// </summary>
        private void ScanSerialFallback(
            int gen, List<KeyValuePair<int, List<string>>> groups,
            ConcurrentQueue<(string Key, TrackMapRef Entry)> sink)
        {
            foreach (var kv in groups)
            {
                if (IsReverseStale(gen))
                {
                    return;
                }
                // 短锁：快照该组 img 节点引用（WzLib 遍历非线程安全，必须在锁内完成）
                var imgNodes = new List<Wz_Node>();
                lock (_wz.WzLock)
                {
                    var mapMapNode = _wz.WzRoot?.FindNodeByPath(true, "Map", "Map");
                    var groupNode = mapMapNode?.FindNodeByPath($"Map{kv.Key}");
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
                foreach (var imgNode in imgNodes)
                {
                    // 短锁：单张 img 的提取与字段读取
                    lock (_wz.WzLock)
                    {
                        try
                        {
                            var img = imgNode.GetValue<Wz_Image>();
                            if (img == null || !img.TryExtract())
                            {
                                continue;
                            }
                            var info = img.Node.FindNodeByPath("info");
                            var trackRef = MusicDecisions.NormalizeBgm(info?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                            if (trackRef == null)
                            {
                                continue;
                            }
                            int town = info?.FindNodeByPath("town")?.GetValueEx<int>(0) ?? 0;
                            string mapId = imgNode.Text[..^4];
                            string returnMap = info?.FindNodeByPath("returnMap")?.GetValueEx<string>(null) ?? "";
                            var entry = new TrackMapRef(mapId, LookupMapName(mapId), town == 1,
                                string.IsNullOrEmpty(returnMap) ? null : returnMap);
                            string key = MusicDecisions.NormalizeTrackKey($"{trackRef.Img}/{trackRef.Track}")
                                ?? $"{trackRef.Img}/{trackRef.Track}";
                            sink.Enqueue((key, entry));
                        }
                        catch (Exception ex)
                        {
                            // 单个地图 img 解析失败跳过，不中断整体构建
                            Console.Error.WriteLine($"[MusicCatalog] 反向索引解析 {imgNode.Text} 失败: {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 聚合一条「曲目 ← 地图」记录（5 名封顶口径）：有名字的地图（name != null）才有资格占
        /// ≤5 个展示名额，槽满后（含本条更好也不收）一律只累加 MoreCount；无名地图视为隐藏级
        /// 永不占名额。返回存储是否发生变化。
        /// ⚠️ 2026-09-15 修 NRE 一：store miss 时 slice 为 null，原实现直接解引用 slice.Items
        /// → 每首曲目**首次入库**即 NullReferenceException，整个扫描任务开局即死、
        /// 一次都成功不了（90s 全 null + 缓存从未落盘的第一根因）。
        /// ⚠️ 2026-09-15 修 NRE 二（并发共享列表）：items **无条件拷贝新 List**——旧写法
        /// `slice?.Items as List&lt;T&gt; ?? new List&lt;T&gt;(...)` 在 Items 本就是 List 时（磁盘缓存载入、
        /// 已发布切片全部如此）直接复用**同一实例**，重扫（WarmUp 每次启动全量重扫）会不持
        /// _reverseLock 向 UI 正在枚举的已发布列表就地 Add/Sort → 读端轻则枚举异常、重则集合
        /// 内部结构撕裂（HEAD 版扫描器按引用发布 working 字典时的并发读写的经典 NRE 表现）。
        /// 拷贝后本函数只动私有副本，发布一律走锁内整体替换，读写永不共享可变集合。
        /// </summary>
        internal static bool AccumulateTrackMap(
            Dictionary<string, TrackMapSlice> store, string key, TrackMapRef entry, string? returnMap)
        {
            store.TryGetValue(key, out var slice);
            var items = new List<TrackMapRef>(slice?.Items ?? Array.Empty<TrackMapRef>());
            int more = slice?.MoreCount ?? 0;
            // returnMap 票数：与该曲目相关的**每张**地图都计一票（不受 5 名展示封顶影响）
            Dictionary<string, int>? votes = slice?.ReturnMapVotes == null
                ? null
                : new Dictionary<string, int>(slice.ReturnMapVotes, StringComparer.Ordinal);
            bool votesChanged = false;
            if (!string.IsNullOrEmpty(returnMap))
            {
                votes ??= new Dictionary<string, int>(StringComparer.Ordinal);
                votes[returnMap] = votes.TryGetValue(returnMap, out var vc) ? vc + 1 : 1;
                votesChanged = true;
            }
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
                store[key] = new TrackMapSlice(items, more, votes);
                return true;
            }
            if (votesChanged)
            {
                store[key] = new TrackMapSlice(items, more, votes);
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
        /// returnMap 指向图的 BGM（town 合格校验用，规则 2 补充）：mapId → 归一化曲目 key
        /// （"BgmXX/track" 无 .img 口径，与 NormalizeTrackKey 产出可比）。路径构造与 GetSceneTrackAsync
        /// 同惯例（9 位补零防短 id 落空）；GetStringProperty 内部持 WzService 锁，此处不额外加锁。
        /// 查不到 / WZ 未加载 / 解析失败 → null（调用方视为「≠ 当前 BGM」→ 该 town 不合格）。
        /// </summary>
        private string? ReadMapBgmTrackKey(string mapId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mapId) || !_wz.IsLoaded || _wz.WzRoot == null)
                {
                    return null;
                }
                var padded = mapId.Trim().PadLeft(9, '0');
                string? bgm = _wz.GetStringProperty($"Map/Map/Map{padded[0]}/{padded}.img/info/bgm");
                var r = MusicDecisions.NormalizeBgm(bgm);
                if (r == null)
                {
                    return null;
                }
                string img = r.Img.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? r.Img[..^4] : r.Img;
                return img + "/" + r.Track;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] ReadMapBgmTrackKey({mapId}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 曲目候选图全列表（F18 遍历顺序用，胶水 2026-09-15 规则 2 + 补充）：
        /// 候选 = TryGetTrackMaps 收录的全部 mapId（去重）。排序口径 =
        /// **有 town 的图优先（town 内 mapId 升序）→ 其余按 mapId 升序**；town 合格校验：
        /// returnMap 指向图 BGM ≠ currentBgmKey 的 town **降级为普通候选**（混入 mapId 升序），
        /// 详见 MusicDecisions.OrderTrackMaps（纯函数，单测锁口径）。
        /// 排序 + town/returnMap 的 WZ 读按 (trackKey, currentBgmKey) 缓存一次成型，
        /// _reverseGen 变化（新扫描轮 / WZ 重载）即整体失效。计算在锁外（绝不持 _reverseLock 读 WZ），
        /// 并发重算幂等无害。无候选 / trackKey 为空 → 空列表（调用方走默认城镇兜底 = 规则 5 语义）。
        /// </summary>
        public List<string> GetTrackMapsOrdered(string? trackKey, string? currentBgmKey = null)
        {
            var key = MusicDecisions.NormalizeTrackKey(trackKey);
            if (key == null)
            {
                return new List<string>();
            }
            var current = MusicDecisions.NormalizeTrackKey(currentBgmKey);
            string cacheKey = current == null ? key : key + "|" + current;

            IReadOnlyList<TrackMapRef> items;
            int gen;
            lock (_reverseLock)
            {
                // 缓存随代际整体失效（懒清理：代际不符即清空重建）
                if (_orderedMapsCacheGen != _reverseGen)
                {
                    _orderedMapsCache.Clear();
                    _orderedMapsCacheGen = _reverseGen;
                }
                if (_orderedMapsCache.TryGetValue(cacheKey, out var hit))
                {
                    return new List<string>(hit);
                }
                if (!_reverseStore.TryGetValue(key, out var slice))
                {
                    return new List<string>();
                }
                items = slice.Items;
                gen = _reverseGen;
            }
            Func<string, string?> lookup = ReturnMapBgmLookup ?? ReadMapBgmTrackKey;
            var ordered = MusicDecisions.OrderTrackMaps(items, current, lookup);
            lock (_reverseLock)
            {
                // 计算期间代际变了（新扫描/WZ 重载）就放弃缓存本轮结果，下次按新数据重算
                if (gen == _reverseGen)
                {
                    _orderedMapsCache[cacheKey] = ordered;
                }
            }
            return new List<string>(ordered);
        }

        /// <summary>
        /// 「无限之路第一张地图」（遍历起点，胶水 2026-09-15 规则 2 新口径）：
        /// = GetTrackMapsOrdered(trackKey, trackKey) 的第一个——**有 town 优先（以本曲为基准做
        /// returnMap 合格校验）+ mapId 最小**。旧「returnMap 票数众数」口径
        /// （MusicDecisions.FirstMapByReturnMap）废弃不再走此处（函数保留，纯函数单测仍在）。
        /// trackKey 兼容 "Bgm00/FloralLife"（WalkPathPlanner.TrackKeyOf 口径）与
        /// "Bgm00.img/FloralLife"（MusicTrack.Key 口径）。
        /// 无候选 / 索引未就绪 → null（调用方走默认城镇兜底；索引就绪后 PetWindow.ReassignWalkMap
        /// 自动重开到正确图 = 规则 5 链路）。
        /// </summary>
        public string? GetFirstMapByReturnMap(string? trackKey)
        {
            var ordered = GetTrackMapsOrdered(trackKey, trackKey);
            return ordered.Count > 0 ? ordered[0] : null;
        }

        /// <summary>
        /// 测试注入（InternalsVisibleTo MiniPet.Tests）：整体替换反向索引存储并 bump 代际
        /// （顺带失效有序候选缓存），绕过磁盘缓存/后台扫描链路，单测直驱发布后的读取口径。
        /// </summary>
        internal void ReplaceReverseStoreForTests(Dictionary<string, TrackMapSlice> store)
        {
            lock (_reverseLock)
            {
                _reverseStore = store;
                _reverseGen++;
            }
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

        private static string ReverseCacheFilePath()
        {
            if (OperatingSystem.IsMacOS() || !OperatingSystem.IsWindows())
            {
                var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dir = Path.Combine(home, ".minipet");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "music-track-maps.json");
            }
            // Windows：优先 exe 同目录（绿色便携口径，同 ConfigService.GetConfigBaseDir）
            try
            {
                var dir = AppContext.BaseDirectory;
                var probe = Path.Combine(dir, ".minipet-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "music-track-maps.json");
            }
            catch
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "MiniPet");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "music-track-maps.json");
            }
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
                    // key 迁移：2026-09-15 起存储 key 无 .img（旧缓存可能带 .img），读入时统一归一化
                    var migrated = new Dictionary<string, TrackMapSlice>(loaded.Count, StringComparer.Ordinal);
                    foreach (var kv in loaded)
                    {
                        migrated[MusicDecisions.NormalizeTrackKey(kv.Key) ?? kv.Key] = kv.Value;
                    }
                    lock (_reverseLock)
                    {
                        _reverseStore = migrated;
                    }
                    Console.Error.WriteLine($"[MusicCatalog] 反向索引磁盘缓存载入: {migrated.Count} 曲目");
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

        /// <summary>WZ 重载时清空反向索引 + 删除磁盘缓存 + 废弃在途扫描（bump 代际使旧结果不可发布）。</summary>
        private void ResetReverseIndex()
        {
            lock (_reverseLock)
            {
                _reverseGen++;
                _reverseStore = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
                _reversePendingImgs.Clear();
                _orderedMapsCache.Clear(); // 排序缓存随代际整体失效（懒清理兜底，此处即时清）
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
    public sealed record TrackMapRef(string MapId, string? MapName, bool IsTown, string? ReturnMap = null);

    /// <summary>
    /// 单首曲目的关联地图切片：Items ≤5 条**有名字**的地图（城镇优先，组内名称序）；
    /// MoreCount = 未收录的其余地图张数（无名/超额），UI 以「…下略」代替展示。
    /// </summary>
    public sealed record TrackMapSlice(
        IReadOnlyList<TrackMapRef> Items,
        int MoreCount,
        IReadOnlyDictionary<string, int>? ReturnMapVotes = null);
}
