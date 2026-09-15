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
                t => Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描收场: {t.Exception?.GetBaseException().Message ?? "完成"}"),
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
            try
            {
                // 短锁一：拿 Map/Map 根节点引用（WZ 重载会换根，之后每组循环前再校验代际兜底）
                Wz_Node? mapMapNode;
                lock (_wz.WzLock)
                {
                    mapMapNode = _wz.WzRoot.FindNodeByPath(true, "Map", "Map");
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
                    // 短锁二：快照该组的 img 节点引用列表（WzLib 遍历非线程安全，必须在锁内完成）
                    List<Wz_Node> imgNodes = new();
                    lock (_wz.WzLock)
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
                    foreach (var imgNode in imgNodes)
                    {
                        // 短锁三：单张 img 的提取与字段读取（TryExtract 非线程安全必须持锁，
                        // 但只锁一张的耗时量级，锁外等待方最多等几毫秒）
                        List<KeyValuePair<string, TrackMapRef>> found = new();
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
                                MusicTrackRef? trackRef =
                                    MusicDecisions.NormalizeBgm(info?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                                if (trackRef == null)
                                {
                                    continue;
                                }
                                int town = info?.FindNodeByPath("town")?.GetValueEx<int>(0) ?? 0;
                                string mapId = imgNode.Text[..^4];
                                // returnMap：无限之路「第一张地图」的选图依据（运行时实时算众数，不落表）
                                string returnMap = info?.FindNodeByPath("returnMap")?.GetValueEx<string>(null) ?? "";
                                var entry = new TrackMapRef(mapId, LookupMapName(mapId), town == 1,
                                    string.IsNullOrEmpty(returnMap) ? null : returnMap);
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
                            changed |= AccumulateTrackMap(working, kv.Key, kv.Value, kv.Value.ReturnMap);
                        }
                    }
                    // 分组收尾：增量发布（UI 即便中途进来也能看到已完成分区的数据）+ 触发刷新
                    if (changed)
                    {
                        lock (_reverseLock)
                        {
                            _reverseStore = working;
                            MergeSlicesFromPendingImgNames(pendingImgs, working);
                        }
                        PersistReverseCacheToDisk(working);
                        ReverseIndexChanged?.Invoke();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MusicCatalog] 反向索引扫描失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 聚合一条「曲目 ← 地图」记录（5 名封顶口径）：有名字的地图（name != null）才有资格占
        /// ≤5 个展示名额，槽满后（含本条更好也不收）一律只累加 MoreCount；无名地图视为隐藏级
        /// 永不占名额。返回存储是否发生变化。
        /// </summary>
        private static bool AccumulateTrackMap(
            Dictionary<string, TrackMapSlice> store, string key, TrackMapRef entry, string? returnMap)
        {
            store.TryGetValue(key, out var slice);
            var items = slice.Items as List<TrackMapRef> ?? new List<TrackMapRef>(slice.Items);
            int more = slice.MoreCount;
            // returnMap 票数：与该曲目相关的**每张**地图都计一票（不受 5 名展示封顶影响）
            Dictionary<string, int>? votes = slice.ReturnMapVotes == null
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
        /// 实时算「无限之路第一张地图」（**不查表**）：该曲目所有相关地图里，`info/returnMap`
        /// 得票最多的那张（并列取 mapId 小者，保证确定性）。
        /// 数据来自反向索引一次性扫描时顺带累计的票数——调用是纯内存计算，切 BGM 时可随取随算。
        /// 曲目无索引 / 无 returnMap 票 / trackKey 为空 → null（调用方走回退链）。
        /// </summary>
        public string? GetFirstMapByReturnMap(string? trackKey)
        {
            if (string.IsNullOrEmpty(trackKey))
            {
                return null;
            }
            lock (_reverseLock)
            {
                if (!_reverseStore.TryGetValue(trackKey, out var slice))
                {
                    return null;
                }
                return MusicDecisions.FirstMapByReturnMap(slice.ReturnMapVotes);
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

        /// <summary>WZ 重载时清空反向索引 + 删除磁盘缓存 + 废弃在途扫描（bump 代际使旧结果不可发布）。</summary>
        private void ResetReverseIndex()
        {
            lock (_reverseLock)
            {
                _reverseGen++;
                _reverseStore = new Dictionary<string, TrackMapSlice>(StringComparer.Ordinal);
                _reversePendingImgs.Clear();
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
