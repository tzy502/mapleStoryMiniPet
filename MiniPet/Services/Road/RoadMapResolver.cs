using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniPet.Models;

namespace MiniPet.Services.Road
{
    /// <summary>
    /// 曲源图解析器（F18，算法 §8 修订版——town 先行分层已取消，消费共享原始索引）：
    /// 当前曲 MusicTrack → 绑定源图 mapId。
    /// - 查询链：trackKey = track.Img + "/" + track.Track（带 .img，与 MusicTrack.Key 同形态）
    ///   → MusicCatalogService.GetRawBgmCandidatesAsync（共享全量扫描 + 磁盘缓存）；
    ///   有候选：优先 town=1 随机一张，否则随机一张（**不混池**，F18-15）；
    ///   无候选（FullComplete 前提下）→ 解析结果 null 走回退链；
    /// - 回退链（持有状态）：null → 沿用 _lastResolvedMapId（上一首源图）→ 仍无（首曲）
    ///   → RoadConstants.TownFallbackMaps 五城镇随机（F18-2）；
    /// - FullComplete=false 期间：**立即**走回退链返回旧图/五城镇（不阻塞激活），同时后台等
    ///   扫描完成后同曲补解析一次入缓存——同曲不重复解析；
    /// - 本地曲（Img == LocalImgTag）/空曲 → null 走回退链；
    /// - WZ 重载：清 _lastResolvedMapId 与同曲缓存（索引重建后结果可能变化）。
    /// 线程纪律：ResolveAsync 由 RoadModeController 在 UI 命令队列内调用；后台补解析在线程池，
    /// 与 ResolveAsync 经 _stateLock 串行化状态读写。
    /// </summary>
    public sealed class RoadMapResolver
    {
        private readonly MusicCatalogService _catalog;
        private readonly WzService _wz;
        private readonly Random _rng;
        // rng 非线程安全：ResolveAsync（调用方线程）与后台补解析（线程池）并发抽取时串行化
        private readonly object _rngLock = new();
        // 状态锁：保护 _lastResolvedMapId/_cacheTrackKey/_cacheMapId/_pendingTrackKey/_bgResolveGen
        private readonly object _stateLock = new();
        // 最近一次成功解析的源图（回退链第一站）
        private string? _lastResolvedMapId;
        // 同曲解析缓存（含负缓存：FullComplete=true 且无候选 → MapId=null 也缓存，同曲不重复解析）
        private string? _cacheTrackKey;
        private string? _cacheMapId;
        // 后台补解析在途曲目（同曲已有在途等待时不重复起）
        private string? _pendingTrackKey;
        private Task? _pendingTask;
        // 后台补解析代际：换曲/WZ 重载后旧后台结果作废
        private int _bgResolveGen;

        public RoadMapResolver(MusicCatalogService catalog, WzService wz, Random? rng = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _wz = wz ?? throw new ArgumentNullException(nameof(wz));
            // 种子可配（复现调试，口径同算法 §3.1 rng）
            _rng = rng ?? new Random();
            _wz.WzReloaded += OnWzReloaded;
        }

        /// <summary>
        /// 解析当前曲的源图 mapId（含回退链，返回值几乎恒非 null——五城镇兜底）。
        /// ct 语义随 GetRawBgmCandidatesAsync：只放弃调用方等待，绝不取消共享扫描。
        /// </summary>
        /// <summary>
        /// 带素材可用性的解析（白盒实证 2026-08-28：随机单候选可落在无柱图/不可用图）：
        /// 候选按「城镇优先洗牌 → 其余洗牌」排序，逐个交 tryExtract 提取，第一个 Usable 包即胜出
        /// （最多试 MaxUsableTries=6 个）；无可用候选/无候选 → null（调用方走回退链）。
        /// 返回 (mapId, pack)；pack 供直接使用避免二次提取。
        /// </summary>
        public async Task<(string MapId, RoadMaterialPack? Pack)?> ResolveWithPackAsync(
            MusicTrack? track,
            Func<string, CancellationToken, Task<RoadMaterialPack?>> tryExtract,
            CancellationToken ct = default)
        {
            if (track == null || string.Equals(track.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal))
            {
                return null;
            }
            string trackKey = track.Img + "/" + track.Track;
            RawBgmResult result = await _catalog.GetRawBgmCandidatesAsync(trackKey, ct).ConfigureAwait(false);
            if (result.Candidates.Count == 0)
            {
                // 无候选（未使用曲）：直接走末级兜底（沿用上一首 → 五城镇），不再返回 null
                // （白盒实证：返回 null 后回退链单候选随机仍会落到 0 柱图）
                var lastResort0 = await LastResortAsync(tryExtract, ct).ConfigureAwait(false);
                if (lastResort0 != null) { return lastResort0; }
                return null;
            }
            // 尝试序（白盒实证 2026-08-29：Nightmare 候选 70 个全是 910 系活动图，best-of-N
            // 选出的也是活动图——玩家期望「游戏内真实地图」）。优先级：城镇 → 普通地图
            //（mapId < 900000000）→ 其余（910 系活动图殿后）；同组内洗牌
            List<(string MapId, bool IsTown)> towns, normals, others;
            lock (_rngLock)
            {
                towns = result.Candidates.Where(c => c.IsTown).OrderBy(_ => _rng.Next()).ToList();
                normals = result.Candidates
                    .Where(c => !c.IsTown && long.TryParse(c.MapId, out var id) && id < 900000000)
                    .OrderBy(_ => _rng.Next()).ToList();
                others = result.Candidates
                    .Where(c => !c.IsTown && !(long.TryParse(c.MapId, out var id2) && id2 < 900000000))
                    .OrderBy(_ => _rng.Next()).ToList();
            }
            // 试提取全部候选（≤6），按信息量得分（平地柱×装饰）择优——白盒实证：首个可用即停
            // 会随机落在「孤树/单桥」型贫瘠图，与源图对比观感不合格
            int tried = 0;
            (string MapId, RoadMaterialPack Pack, int Score)? best = null;
            foreach (var cand in towns.Concat(normals).Concat(others))
            {
                if (tried >= 40 || ct.IsCancellationRequested)
                {
                    break;
                }
                tried++;
                RoadMaterialPack? pack = null;
                try
                {
                    pack = await tryExtract(cand.MapId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RoadResolver] 候选 {cand.MapId} 提取异常: {ex.Message}");
                }
                // 入池门（FINAL §三）：每图贡献 ≥5 主干柱（MeetsMergeMin）+ 有像素；
                // ≥20 与否只分档（正常/降级）不拒绝——柱=0 已被柱门拦截，连续率<0.6 在提取期清池
                if (pack == null || !pack.MeetsMergeMin || !RoadMaterialExtractor.PackHasPixels(pack))
                {
                    continue;
                }
                int score = pack.FlatColumns.Count * (1 + pack.FlatColumns.Sum(c => c.Objs.Count));
                if (best == null || score > best.Value.Score)
                {
                    best = (cand.MapId, pack, score);
                }
            }
            if (best != null)
            {
                lock (_stateLock)
                {
                    _cacheTrackKey = trackKey;
                    _cacheMapId = best.Value.MapId;
                    _lastResolvedMapId = best.Value.MapId;
                }
                return (best.Value.MapId, best.Value.Pack);
            }
            return await LastResortAsync(tryExtract, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 主题池多图合并解析（FINAL §三 唯一正式路径）：
        /// 候选序（城镇 → 普通图 &lt;900000000 → 其余，组内洗牌）逐个 tryExtract（≤40 试），
        /// **收集全部合格包非首个即停**（合格 = 每图贡献 ≥5 柱 + 有像素；≥20 与否只分档不拒绝）；
        /// 合并池 = MergePacks（跨图重定基 bandY=首包表面，块相对关系保真）；
        /// 合格包为空 → 末级兜底（沿用上一首 → 五城镇随机——与单候选路径同语义）。
        /// state 可空：传入时回写 ScanProgress/ScanComplete（共享扫描进度可观测——W17 验收锚）。
        /// </summary>
        public async Task<(string MapId, RoadMaterialPack? Pack)?> ResolveMultiAsync(
            MusicTrack? track,
            Func<string, CancellationToken, Task<RoadMaterialPack?>> tryExtract,
            RoadState? state = null,
            CancellationToken ct = default)
        {
            if (track == null || string.Equals(track.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal))
            {
                return null;
            }
            string trackKey = track.Img + "/" + track.Track;
            RawBgmResult result = await _catalog.GetRawBgmCandidatesAsync(trackKey, ct).ConfigureAwait(false);
            if (result.Candidates.Count == 0)
            {
                // 无候选（未使用曲）：直接末级兜底（FINAL §三——与单候选路径同语义）
                return await LastResortAsync(tryExtract, ct).ConfigureAwait(false);
            }
            // 候选序（同 ResolveWithPackAsync 口径）：城镇 → 普通 → 其余，组内洗牌
            List<(string MapId, bool IsTown)> towns, normals, others;
            lock (_rngLock)
            {
                towns = result.Candidates.Where(c => c.IsTown).OrderBy(_ => _rng.Next()).ToList();
                normals = result.Candidates
                    .Where(c => !c.IsTown && long.TryParse(c.MapId, out var id) && id < 900000000)
                    .OrderBy(_ => _rng.Next()).ToList();
                others = result.Candidates
                    .Where(c => !c.IsTown && !(long.TryParse(c.MapId, out var id2) && id2 < 900000000))
                    .OrderBy(_ => _rng.Next()).ToList();
            }
            var qualified = new List<RoadMaterialPack>();
            (string MapId, RoadMaterialPack Pack, int Score)? bestSingle = null; // FINAL §六 正常档：单图 ≥20 柱优先（不入合并池）
            int tried = 0;
            foreach (var cand in towns.Concat(normals).Concat(others))
            {
                if (tried >= RoadConstants.MaxResolveTries || ct.IsCancellationRequested)
                {
                    break;
                }
                tried++;
                if (state != null)
                {
                    state.ScanProgress = Math.Min(1.0, (double)tried / RoadConstants.MaxResolveTries);
                }
                RoadMaterialPack? pack = null;
                try
                {
                    pack = await tryExtract(cand.MapId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RoadResolver] 主题池候选 {cand.MapId} 提取异常: {ex.Message}");
                }
                // 每图贡献 ≥5 柱（MeetsMergeMin）+ 有像素才入合并池（质量下限，FINAL §三）
                if (pack == null || !pack.MeetsMergeMin || !RoadMaterialExtractor.PackHasPixels(pack))
                {
                    continue;
                }
                if (pack.MainColumnTotal >= RoadConstants.NormalPoolColumns)
                {
                    // 单图达标（≥20 柱）：记录得分最高的一个（与 ResolveWithPackAsync 同口径信息量得分）
                    int score = pack.FlatColumns.Count * (1 + pack.FlatColumns.Sum(c => c.Objs.Count));
                    if (bestSingle == null || score > bestSingle.Value.Score) { bestSingle = (cand.MapId, pack, score); }
                    continue; // 达标单图不入合并池——正常档优先（FINAL §六）
                }
                qualified.Add(pack);
            }
            if (state != null)
            {
                state.ScanProgress = 1.0;
                state.ScanComplete = true;
            }
            if (bestSingle != null)
            {
                lock (_stateLock) { _lastResolvedMapId = bestSingle.Value.MapId; }
                return (bestSingle.Value.MapId, bestSingle.Value.Pack);
            }
            if (qualified.Count == 0)
            {
                return await LastResortAsync(tryExtract, ct).ConfigureAwait(false);
            }
            RoadMaterialPack merged = MergePacks(qualified);
            lock (_stateLock)
            {
                _lastResolvedMapId = merged.MapId;
            }
            return (merged.MapId, merged);
        }

        /// <summary>
        /// 合并池构建（FINAL §三，纯函数可测）：调用方已按「每图贡献 ≥5 柱 + 有像素」过滤合格包，
        /// 本函数只做合并——以首包 MainSurfaceH 为 bandY 基准，各包块 SurfaceH（及台阶起终点）
        /// 平移 delta = bandY − 包.MainSurfaceH（同包内相对关系 100% 保真）；块 Id 全池重编号
        /// （防跨图同号冲突）；主干列/斜坡列/台阶拼接；Sky/锚点/远景取首包（背景冻结语义——
        /// RoadSky 只冻结一次，多图背景不混池）；life 候选拼接（RoadEntityState.MapId 已支持逐实体按图取帧）。
        /// </summary>
        public static RoadMaterialPack MergePacks(IReadOnlyList<RoadMaterialPack> packs)
        {
            var first = packs.Count > 0 ? packs[0] : new RoadMaterialPack();
            int bandY = first.MainSurfaceH;
            var merged = new RoadMaterialPack
            {
                MapId = first.MapId,
                PoolMapIds = packs.Select(p => p.MapId).ToList(),
                TileSetName = first.TileSetName,
                MainSurfaceH = bandY,
                AnchorX = first.AnchorX,
                AnchorY = first.AnchorY,
                Backs = first.Backs,
                FarObjs = first.FarObjs,
                ContinuityRatio = packs.Count > 0 ? packs.Min(p => p.ContinuityRatio) : 0,
            };
            int idSeq = 0;
            foreach (var p in packs)
            {
                int delta = bandY - p.MainSurfaceH;
                foreach (var t in p.FlatColumns)
                {
                    merged.FlatColumns.Add(RebaseBlock(t, delta, idSeq++));
                }
                foreach (var t in p.SlantColumns)
                {
                    merged.SlantColumns.Add(RebaseBlock(t, delta, idSeq++));
                }
                foreach (var s in p.Steps)
                {
                    merged.Steps.Add(new StepTemplate
                    {
                        Id = idSeq++,
                        Up = s.Up,
                        StartSurfaceH = s.StartSurfaceH + delta,
                        EndSurfaceH = s.EndSurfaceH + delta,
                        Columns = s.Columns.Select(c => RebaseBlock(c, delta, idSeq++)).ToList(),
                    });
                }
                merged.LifeCandidates.AddRange(p.LifeCandidates);
            }
            return merged;
        }

        /// <summary>块跨图重定基：SurfaceH 平移 delta（并入统一 bandY 基准）+ Id 重编号；tile/obj 相对偏移不动（相对块表面语义不变）。</summary>
        private static ColumnTemplate RebaseBlock(ColumnTemplate t, int delta, int newId)
        {
            return new ColumnTemplate
            {
                Id = newId,
                SourceMapId = t.SourceMapId,
                SourceSurfaceY = t.SourceSurfaceY,
                WidthCols = t.WidthCols,
                SurfaceH = t.SurfaceH + delta,
                Tiles = t.Tiles,
                Objs = t.Objs,
                BscPad = t.BscPad,
                SlantDy = t.SlantDy,
            };
        }

        /// <summary>末级兜底：沿用上一首 → 白盒已验证镇优先 → 其余五城镇，各试提取至可用（合计 ≤6 试）。</summary>
        private async Task<(string MapId, RoadMaterialPack? Pack)?> LastResortAsync(
            Func<string, CancellationToken, Task<RoadMaterialPack?>> tryExtract, CancellationToken ct)
        {
            string? last;
            lock (_stateLock) { last = _lastResolvedMapId; }
            var lastResort = new List<string>();
            if (!string.IsNullOrEmpty(last)) { lastResort.Add(last!); }
            // 2026-08-29 用户实测「全部都是废都」根因：103/102 硬编码首两位 → 所有解析失败曲
            // 全落废都。改回五城镇全随机（各歌落不同镇）
            lock (_rngLock)
            {
                lastResort.AddRange(RoadConstants.TownFallbackMaps.OrderBy(_ => _rng.Next()));
            }
            foreach (var mid in lastResort)
            {
                if (ct.IsCancellationRequested) { break; }
                RoadMaterialPack? fb = null;
                try
                {
                    fb = await tryExtract(mid, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[RoadResolver] 兜底 {mid} 提取异常: {ex.Message}"); }
                if (fb != null && fb.Usable && RoadMaterialExtractor.PackHasPixels(fb))
                {
                    lock (_stateLock) { _lastResolvedMapId = mid; }
                    return (mid, fb);
                }
            }
            return null;
        }

        public async Task<string?> ResolveAsync(MusicTrack? track, CancellationToken ct = default)
        {
            // 第一步：WZ 曲解析（本地曲/空曲 → resolved 保持 null 直接走回退链）
            string? resolved = null;
            if (track != null && !string.Equals(track.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal))
            {
                // 键形统一带 .img（"Bgm58.img/FloralLife"），与原始索引键一致
                string trackKey = track.Img + "/" + track.Track;
                bool cached = false;
                lock (_stateLock)
                {
                    // 同曲缓存命中（含负缓存）：扫描完成后同曲不重复解析
                    if (string.Equals(_cacheTrackKey, trackKey, StringComparison.Ordinal))
                    {
                        resolved = _cacheMapId;
                        cached = true;
                    }
                }
                if (!cached)
                {
                    RawBgmResult result = await _catalog.GetRawBgmCandidatesAsync(trackKey, ct).ConfigureAwait(false);
                    if (result.FullComplete)
                    {
                        // 第二步：多图选图——城镇优先随机、不混池（纯函数，供 D5 测试）
                        lock (_rngLock)
                        {
                            resolved = PickMapFromCandidates(result.Candidates, _rng);
                        }
                        lock (_stateLock)
                        {
                            _cacheTrackKey = trackKey;
                            _cacheMapId = resolved;
                        }
                    }
                    else
                    {
                        // 索引未完整：立即走回退链（下方第三步），同时后台等扫描完成补缓存
                        KickBackgroundResolve(trackKey);
                    }
                }
            }
            // 第三步：回退链——有解析结果直接采用；null → 沿用上一首；仍无（首曲）→ 五城镇随机
            if (resolved != null)
            {
                lock (_stateLock)
                {
                    _lastResolvedMapId = resolved;
                }
                return resolved;
            }
            lock (_stateLock)
            {
                if (_lastResolvedMapId != null)
                {
                    return _lastResolvedMapId;
                }
            }
            lock (_rngLock)
            {
                return PickTownFallback(_rng);
            }
        }

        /// <summary>
        /// 多候选选图（纯函数，D5 测试用）：优先 town=1 子池随机一张，无城镇才全池随机——
        /// 两种池不混（F18-15「多图优先 town」）；空候选 → null（调用方走回退链）。
        /// </summary>
        public static string? PickMapFromCandidates(IReadOnlyList<(string MapId, bool IsTown)> candidates, Random rng)
        {
            if (candidates.Count == 0)
            {
                return null;
            }
            // 第一步：数城镇候选（有则只从城镇子池抽，绝不混入非城镇）
            int townCount = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].IsTown)
                {
                    townCount++;
                }
            }
            if (townCount > 0)
            {
                int pick = rng.Next(townCount);
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].IsTown)
                    {
                        if (pick == 0)
                        {
                            return candidates[i].MapId;
                        }
                        pick--;
                    }
                }
            }
            // 第二步：无城镇 → 全池随机
            return candidates[rng.Next(candidates.Count)].MapId;
        }

        /// <summary>首曲回退：五城镇随机一张（纯函数，D5 测试用；town=1 已终验）。</summary>
        public static string PickTownFallback(Random rng)
        {
            return RoadConstants.TownFallbackMaps[rng.Next(RoadConstants.TownFallbackMaps.Length)];
        }

        // ====== private ======

        /// <summary>
        /// 后台等扫描完成后同曲补解析（FullComplete=false 期间 ResolveAsync 立即回退时调用）：
        /// 同曲已有在途等待则不重复起；完成后结果入同曲缓存（下次 ResolveAsync 直接命中，
        /// 不重复解析），并刷新 _lastResolvedMapId 供后续回退更准。
        /// </summary>
        private void KickBackgroundResolve(string trackKey)
        {
            lock (_stateLock)
            {
                if (string.Equals(_pendingTrackKey, trackKey, StringComparison.Ordinal)
                    && _pendingTask != null && !_pendingTask.IsCompleted)
                {
                    return;
                }
                _bgResolveGen++;
                int gen = _bgResolveGen;
                _pendingTrackKey = trackKey;
                _pendingTask = Task.Run(() => BackgroundResolveCore(trackKey, gen));
            }
        }

        /// <summary>
        /// 后台补解析任务体：阻塞等共享索引完整（CancellationToken.None——共享扫描只受内部代际
        /// 约束），完成后选图入缓存；期间换曲/WZ 重载（gen 不符）则结果作废静默退出。
        /// </summary>
        private async Task BackgroundResolveCore(string trackKey, int gen)
        {
            try
            {
                RawBgmResult result = await _catalog.GetRawBgmCandidatesAsync(trackKey, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!result.FullComplete)
                {
                    return;
                }
                string? mapId;
                lock (_rngLock)
                {
                    mapId = PickMapFromCandidates(result.Candidates, _rng);
                }
                lock (_stateLock)
                {
                    if (gen != _bgResolveGen)
                    {
                        return; // 期间换曲/WZ 重载：作废
                    }
                    _cacheTrackKey = trackKey;
                    _cacheMapId = mapId;
                    if (mapId != null)
                    {
                        _lastResolvedMapId = mapId;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RoadMapResolver] 后台补解析({trackKey})失败: {ex.Message}");
            }
        }

        /// <summary>WZ 重载：清上一首源图与同曲缓存（索引重建后同曲可能解析出不同结果）。</summary>
        private void OnWzReloaded(int generation)
        {
            lock (_stateLock)
            {
                _bgResolveGen++;
                _lastResolvedMapId = null;
                _cacheTrackKey = null;
                _cacheMapId = null;
                _pendingTrackKey = null; // 在途后台任务由代际作废
            }
        }
    }
}
