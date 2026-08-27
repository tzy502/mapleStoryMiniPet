using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniPet.Models;
using WzComparerR2.WzLib;

namespace MiniPet.Services
{
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

        // ====== 曲目↔地图反向索引（V0.2.1） ======
        // 发布锁：保护 _reverseIndex/_reverseTask/_reverseGen 三字段
        private readonly object _reverseLock = new();
        // 已发布的反向索引（trackKey → 引用该曲目的地图列表）；基于 WZ 树，重载时失效重建
        private Dictionary<string, List<TrackMapRef>>? _reverseIndex;
        // 单飞构建任务（口径同目录构建）
        private Task<Dictionary<string, List<TrackMapRef>>>? _reverseTask;
        // 构建代际：WZ 重载时自增，完成后比对不符不发布
        private int _reverseGen;

        public MusicCatalogService(WzService wz)
        {
            _wz = wz;
            // WZ 重载 → 失效缓存与在途任务（WzService 不能反向依赖目录服务避免 DI 成环，故由此订阅；
            // 参数 = 新 WZ 代际号，本服务用自己的 _catalogGen 记代际，事件仅作失效触发）
            _wz.WzReloaded += OnWzReloaded;
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

        // ====== 曲目↔地图反向索引 API（V0.2.1） ======

        /// <summary>
        /// 反向索引是否已就绪可直接取用（true 时 GetTrackMapsAsync 同步命中）；
        /// WZ 重载失效期间为 false，直到重建完成。供 UI 决定立即读取还是挂 loading 态。
        /// </summary>
        public bool IsReverseIndexReady
        {
            get { lock (_reverseLock) { return _reverseIndex != null; } }
        }

        /// <summary>
        /// 曲目 ↔ 地图 反向索引懒加载（V0.2.1）：首次调用触发后台单飞构建并 await 其完成；
        /// 已就绪同步返回拷贝；WZ 未加载/构建期间 WZ 重载 → InvalidOperationException（不返回脏数据）。
        /// ⚠️ 首次构建耗时约 30 秒量级（见 BuildReverseIndexCore 注释），长耗时的 kick 应由 UI 以
        /// fire-and-forget 发起、经 IsReverseIndexReady 轮询就绪后消费。
        /// </summary>
        public Task<Dictionary<string, List<TrackMapRef>>> GetTrackMapsAsync(CancellationToken ct = default)
        {
            lock (_reverseLock)
            {
                if (_reverseIndex != null)
                {
                    return Task.FromResult(CopyReverseIndex(_reverseIndex));
                }
                if (_reverseTask != null && !_reverseTask.IsCompleted)
                {
                    return _reverseTask;
                }
                _reverseGen++;
                int gen = _reverseGen;
                var build = BuildReverseIndexGuardedAsync(gen, ct);
                _reverseTask = build;
                return build;
            }
        }

        /// <summary>
        /// 按 trackKey（"BgmXX.img/曲名"）单查该曲目的关联地图列表（V0.2.1 便捷重载）：
        /// 索引未建时自动 kick 后台单飞构建并等待完成后查询；该曲目无关联地图
        /// （key 不在索引中）→ 空列表。批量预取建议直接用字典版一次拿全。
        /// </summary>
        public async Task<IReadOnlyList<TrackMapRef>> GetTrackMapsAsync(
            string trackKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(trackKey))
            {
                return Array.Empty<TrackMapRef>();
            }
            Dictionary<string, List<TrackMapRef>> index =
                await GetTrackMapsAsync(ct).ConfigureAwait(false);
            return index.TryGetValue(trackKey, out var list) ? list : Array.Empty<TrackMapRef>();
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
        /// 反向索引单飞任务体：Task.Run 内持 WZ 锁全量构建，完成后锁内比对启动代际——
        /// 不符（WZ 已重载）不发布缓存并抛 InvalidOperationException 由 await 侧接收；
        /// 发布后返回拷贝（调用方与内部状态隔离）。
        /// </summary>
        private async Task<Dictionary<string, List<TrackMapRef>>> BuildReverseIndexGuardedAsync(
            int gen, CancellationToken ct)
        {
            Dictionary<string, List<TrackMapRef>> built =
                await Task.Run(() => BuildReverseIndexCore(ct), ct).ConfigureAwait(false);
            lock (_reverseLock)
            {
                if (gen != _reverseGen)
                {
                    throw new InvalidOperationException("WZ 已重载，反向索引过期");
                }
                _reverseIndex = built;
            }
            return CopyReverseIndex(built);
        }

        /// <summary>反向索引深一层拷贝（外壳 + 每条 List 均拷贝；几百 key × 几项，开销可忽略）。</summary>
        private static Dictionary<string, List<TrackMapRef>> CopyReverseIndex(
            Dictionary<string, List<TrackMapRef>> source)
        {
            var copy = new Dictionary<string, List<TrackMapRef>>(source.Count, StringComparer.Ordinal);
            foreach (var kv in source)
            {
                copy[kv.Key] = new List<TrackMapRef>(kv.Value);
            }
            return copy;
        }

        /// <summary>WZ 重载时清空反向索引 + 废弃在途构建（bump 代际使旧结果不可发布）。</summary>
        private void ResetReverseIndex()
        {
            lock (_reverseLock)
            {
                _reverseGen++;
                _reverseIndex = null;
                _reverseTask = null;
            }
        }

        /// <summary>
        /// 反向索引构建核心（Task.Run 后台线程内执行；整体持 _wz.WzLock 长临界区约 30 秒量级，
        /// 与 MapCatalogService.BuildMapNameCache 同模式——WzLib 遍历必须串行化）：
        /// 显式遍历 Map0..Map9 组（跳过 AreaCode.img 等同层杂项节点）→ 每 img TryExtract 读
        /// info/bgm 归一化聚合 + info/town 判村庄 → 地图名尽量取已有地图名缓存（miss 记 null）。
        /// 单个 img 解析失败跳过不中断；OperationCanceledException 直接上抛。
        /// </summary>
        private Dictionary<string, List<TrackMapRef>> BuildReverseIndexCore(CancellationToken ct)
        {
            var result = new Dictionary<string, List<TrackMapRef>>(StringComparer.Ordinal);
            if (!_wz.IsLoaded || _wz.WzRoot == null)
            {
                throw new InvalidOperationException("WZ 未加载，无法构建曲目地图索引");
            }
            lock (_wz.WzLock)
            {
                try
                {
                    var mapMapNode = _wz.WzRoot.FindNodeByPath(true, "Map", "Map");
                    if (mapMapNode == null)
                    {
                        return result;
                    }
                    for (int g = 0; g <= 9; g++)
                    {
                        var groupNode = mapMapNode.FindNodeByPath($"Map{g}");
                        if (groupNode == null)
                        {
                            continue;
                        }
                        foreach (Wz_Node imgNode in groupNode.Nodes)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!imgNode.Text.EndsWith(".img", StringComparison.Ordinal))
                            {
                                continue;
                            }
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
                                string key = $"{trackRef.Img}/{trackRef.Track}";
                                if (!result.TryGetValue(key, out var list))
                                {
                                    list = new List<TrackMapRef>();
                                    result[key] = list;
                                }
                                list.Add(entry);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                // 单个地图 img 解析失败跳过，不中断整体构建
                                Console.Error.WriteLine($"[MusicCatalog] 反向索引解析 {imgNode.Text} 失败: {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MusicCatalog] 反向索引构建失败: {ex.Message}");
                }
            }
            return result;
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
}
