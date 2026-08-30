using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services.Road
{
    /// <summary>路属实体条带缓存条目（Png = 整条条带图；Strip = 帧几何/延迟元数据）。</summary>
    public sealed record RoadStripEntry(byte[]? Png, SpriteStrip Strip);

    /// <summary>
    /// 非主干组贴回装饰集（路级，提取期随 Extract 产出、按 mapId LRU 缓存，红线⑥上限 DecorCacheMax）：
    /// Behind = Entities 之前画（人物遮挡 decor）；Front = Entities 之后画（前层 obj 遮挡人物——FINAL §二 Decor 分级）。
    /// 坐标口径（FINAL §二）：tile = 可见左上角相对主干锚（pack.AnchorX/AnchorY）；obj = 锚点相对主干锚；
    /// 均不含 origin——渲染层自减（权威画法 drawImage(im, x−ox, y−oy)）。禁烤入柱位图（红线⑤）。
    /// </summary>
    public sealed record RoadDecorLayer(IReadOnlyList<RoadDecor> Behind, IReadOnlyList<RoadDecor> Front);

    /// <summary>
    /// F18 无限之路素材提取器（v13 FINAL 定稿实现，权威 = docs/ai/algorithm-infinite-road-FINAL.md §一/§二）：
    /// colTop 链聚组（表面类 tile 列集 → 链 → 组）+ 三证据评分选主干组（柱数 × foothold 逐根累加 × life.Fh 权威归属）
    /// → 主干组块状切块（8 柱软上限 + obj 保守区间双缘修正）+ 真实底边绑定（±60）+ 非主干组贴回 Decor
    /// + ScreenMode 提取期归一化 + SlantDy 数据保真。.skel 一律排除并计数。
    /// 线程安全：全部 WZ 读取走 MapService.LoadMap / WzService 公开接口（各自内部持 _wzLock，
    /// Monitor 可重入），可在后台线程直接调用；条带/Decor 缓存为类内独立 LRU，不经 SpriteService。
    /// </summary>
    public static class RoadMaterialExtractor
    {
        // ═══ 依赖注入点（接线方在启动/路激活时 Configure；静态类无法构造注入） ═══
        private static MapService? _mapService;
        private static WzService? _wz;
        private static readonly object _cfgLock = new();

        // ═══ 路属条带 LRU（键 = mapId|lifeId|action，上限 StripCacheMax） ═══
        private static readonly object _stripLock = new();
        private static readonly Dictionary<string, LinkedListNode<(string Key, RoadStripEntry Entry)>> _stripMap = new();
        private static readonly LinkedList<(string Key, RoadStripEntry Entry)> _stripOrder = new();
        private const char KeySep = '|';

        // ═══ Decor 路级 LRU（键 = mapId，上限 DecorCacheMax——红线⑥禁无上限） ═══
        private static readonly object _decorLock = new();
        private static readonly Dictionary<string, LinkedListNode<(string Key, RoadDecorLayer Layer)>> _decorMap = new();
        private static readonly LinkedList<(string Key, RoadDecorLayer Layer)> _decorOrder = new();

        // ═══ WZ tile u 字符串 ↔ 契约 int U 的进程级双射 ═══
        // 契约 RoadTile.U 为 int，但 WZ tile u 是字符串（"bsc"/"slLU"…，MapService.ParseTiles 按字符串读）。
        // 提取期编码、渲染期 UToString 反查拼纹理路径（进程内稳定，同进程渲染闭环够用）。
        private static readonly object _uCodeLock = new();
        private static readonly Dictionary<string, int> _uCodes = new(StringComparer.Ordinal);
        private static readonly List<string> _uNames = new();

        // ═══ 表面类 tile u 集合（FINAL §0：全量 21254 图实测恰好这 11 种，无装饰类 tile） ═══
        private static readonly HashSet<string> SurfaceUs = new(StringComparer.Ordinal)
        {
            "bsc", "enH0", "enH1", "enV0", "enV1", "edU", "edD", "slLU", "slRU", "slLD", "slRD"
        };

        /// <summary>WZ tile u 字符串 → 契约 int U 编码（提取期调用；纯数字 u 原值编码避免歧义）。</summary>
        public static int UCode(string u)
        {
            lock (_uCodeLock)
            {
                if (_uCodes.TryGetValue(u, out var code)) { return code; }
                // 首次遇到：纯数字原值占用，非数字从 10000 起分配（正数域不与数字 u/WZ no 撞意义的区间）
                int next = int.TryParse(u, out var numeric) ? numeric : 10000 + _uNames.Count;
                _uCodes[u] = next;
                while (_uNames.Count <= next) { _uNames.Add(""); }
                _uNames[next] = u;
                return next;
            }
        }

        /// <summary>契约 int U → WZ tile u 字符串（渲染期拼 Map/Tile/{tS}.img/{u}/{no} 用；未知编码返回 "0"）。</summary>
        public static string UToString(int u)
        {
            lock (_uCodeLock)
            {
                if (u >= 0 && u < _uNames.Count && _uNames[u].Length > 0) { return _uNames[u]; }
                return u.ToString();
            }
        }

        /// <summary>注入依赖（App 启动或路激活时调用一次；重复调用会重置条带缓存）。</summary>
        public static void Configure(MapService mapService, WzService wz)
        {
            lock (_cfgLock)
            {
                _mapService = mapService;
                _wz = wz;
                lock (_stripLock)
                {
                    _stripMap.Clear();
                    _stripOrder.Clear();
                }
            }
        }

        /// <summary>
        /// 一次性提取源图素材包（后台线程调用）。返回 null = 不可用（未配置/WZ 未加载/源图解析失败/已取消），
        /// 原因写 state.LastError；主干组柱数不足时仍返回 pack（Usable=false 由契约属性判定，编排者决策）。
        /// </summary>
        public static RoadMaterialPack? Extract(string mapId, CancellationToken ct, RoadState state)
        {
            MapService? mapSvc;
            WzService? wz;
            lock (_cfgLock) { mapSvc = _mapService; wz = _wz; }
            if (mapSvc == null || wz == null || !wz.IsLoaded)
            {
                state.LastError = "素材提取器未配置或 WZ 未加载";
                return null;
            }
            if (ct.IsCancellationRequested) { return null; }

            // 第一步：源图解析（LoadMap 内部自持 _wzLock，后台线程安全，无需额外包锁）
            var mapInfo = mapSvc.LoadMap(mapId);
            if (mapInfo == null)
            {
                state.LastError = $"源图 {mapId} 解析失败（LoadMap null）";
                return null;
            }
            if (ct.IsCancellationRequested) { return null; }

            // 第二步：v13 §一 全层列集（colTop = 列内表面类 tile 的 min VisTop，非全部 tile——装饰无 tile 口径）
            var (allCols, tileSet) = BuildTileColumns(mapInfo, wz, ct);
            if (ct.IsCancellationRequested) { return null; }

            // 第三步：链构建（缺口 ≤2 柱克隆补齐）→ 链按表面聚组（组代表单层判定，无链式传递——红线⑮）
            var chains = BuildChains(allCols);
            var groups = BuildGroups(chains);
            if (groups.Count == 0)
            {
                // obj 地图分支（2026-09-01 甲方实测：魔法密林 101000000 等 tile=0 全 obj 地图——
                // 「无表面类列」应回退到 foothold 地面段 + 地面 obj 绑段，而非直接判不可用）
                var objPack = TryExtractObjGround(mapInfo, wz, state, ct);
                if (objPack != null) { return objPack; }
                state.LastError = $"源图 {mapId} 无表面类列（colTop 链聚组空）且无 obj 地面段";
                return null;
            }

            // 第四步：life 归属（Fh 权威 ±20 优先，无 Fh 几何 ±60 回退）+ 三证据评分 → 主干组
            var lifes = CollectLifeRefs(mapInfo);
            AssignLifes(lifes, groups);
            ScoreGroups(groups, mapInfo, lifes);
            var main = PickMainGroup(groups);
            state.FallbackReason = $"v13 主干组 y={main.RepSurface} 柱={main.ColCount} score={main.Score:F2}";

            // 第五步：连续率（作用域 = 主干组柱集）<0.6 → 整图 Usable 拒绝（清空列池，走主题池/兜底）
            double continuity = MainContinuity(main);
            var flat = new List<ColumnTemplate>();
            var slant = new List<ColumnTemplate>();
            var lifeCands = new List<LifeCandidate>();
            if (continuity >= RoadConstants.MainContinuityMin)
            {
                // 第六步：obj 解码（真实底边口径）→ 块状切块（obj 双缘修正 + 8 柱软上限 + 超宽 obj 冻结）
                var objs = CollectObjs(mapInfo, wz, state, ct);
                if (ct.IsCancellationRequested) { return null; }
                var (blocks, bound) = SplitBlocks(main, objs);
                BuildBlockTemplates(blocks, mapId, main.RepSurface, flat, slant);

                // 第七步：life 候选（Fh 归属主干组 + 众数层回退不静默 + 动作完备性验证）
                lifeCands = ExtractLifes(lifes, groups, main, wz, state);

                // 第八步：非主干组贴回 Decor（tile 可见口径 / obj 锚点口径 / 前层分级）
                PutDecors(mapId, CollectDecors(allCols, main, objs, bound));
            }
            else
            {
                state.FallbackReason += ";主干组连续率<0.6 整图拒绝（走主题池/兜底）";
            }

            // 第九步：组装包（back 深克隆防 MapInfo 后续突变穿透冻结契约；MainSurfaceH = 主干组表面）
            var pack = new RoadMaterialPack
            {
                MapId = mapId,
                TileSetName = tileSet,
                FlatColumns = flat,
                SlantColumns = slant,
                // v13 无台阶模板：组内 colTop 差 ≤8、块间表面统一（主干组），地面连续零台阶（FINAL §六基线）
                Steps = new List<StepTemplate>(),
                // 远景带废弃（FINAL §二两域消解：主干绑定 / Decor 贴回，不双轨）；契约字段保留（渲染侧空列表零成本跳过）
                FarObjs = new List<FarObjEntry>(),
                LifeCandidates = lifeCands,
                Backs = CloneBacks(mapInfo),
                AnchorX = main.MinX,
                AnchorY = main.RepSurface,
                // 主干组表面 = SurfaceY 唯一来源（FINAL §0：唯一可走层）
                MainSurfaceH = main.RepSurface,
                ContinuityRatio = continuity
            };
            state.ColumnCount = pack.FlatColumns.Count + pack.SlantColumns.Count;
            return pack;
        }

        /// <summary>
        /// 像素级可用性采样（白盒实证 2026-08-28：部分图柱池计数正常但贴图全解码失败 → 渲染空白）：
        /// 取首根平地柱的首块 tile 按 D3 渲染同款路径解码；失败/1x1 → 该图素材判不可用。
        /// </summary>
        /// <summary>obj 地图分支：tile=0 的图（魔法密林系/活动图）——地面由 obj 提供。
        /// 判据：foothold l0（地面层）水平段 → 段表面 y；地面 obj（锚点 o.Y 距段表面 ≤60）
        /// 绑段；每段 = 一个块模板（WidthCols = span/90 估算，Tiles 空、Objs 列表——渲染器画 obj 位图）。
        /// 主表面 = 段表面众数；池 = 全部段（无连续率门——obj 段天然连续）。</summary>
        private static RoadMaterialPack? TryExtractObjGround(MapInfo mi, WzService wz, RoadState state, CancellationToken ct)
        {
            // ① 全部 obj 解码（真实底边 = o.Y − oy + H；.skel/失败剔除）
            var infos = new List<(int X, int Bottom, string OS, string L0, string L1, string L2)>();
            foreach (var layer in mi.Layers)
            {
                if (layer == null) { continue; }
                foreach (var o in layer.Objs)
                {
                    if (ct.IsCancellationRequested) { return null; }
                    var quad = ParseObjUrl(o.Resource.Frames.Count > 0 ? o.Resource.Frames[0].ResourceUrl ?? "" : "");
                    if (quad == null) { continue; }
                    var (oS, l0, l1, l2) = quad.Value;
                    if (oS.Contains("skel", StringComparison.OrdinalIgnoreCase) || l2.Contains("skel", StringComparison.OrdinalIgnoreCase)) { state.SkelExcluded++; continue; }
                    int bottom = o.Y;
                    try
                    {
                        string opath = $"Map/Obj/{oS}.img/{l0}/{l1}/{l2}";
                        var png = wz.ExtractPng(opath);
                        if (png != null && png.Length > 4)
                        {
                            var (_, oy2) = wz.GetOrigin(opath);
                            using var bmp2 = SkiaSharp.SKBitmap.Decode(png);
                            if (bmp2 != null) { bottom = o.Y - oy2 + bmp2.Height; }
                        }
                    }
                    catch { }
                    infos.Add((o.X, bottom, oS, l0, l1, l2));
                }
            }
            if (infos.Count == 0) { return null; }
            // ② 平台面 = 底边 ±8 聚簇最大簇（obj 树冠平台/地面片——魔法密林类无 tile 无水平 foothold）
            var bottoms = infos.Select(x => x.Bottom).OrderBy(v => v).ToList();
            var best = new List<int>(); var cur = new List<int> { bottoms[0] };
            for (int i = 1; i < bottoms.Count; i++)
            {
                if (bottoms[i] - bottoms[i - 1] <= 8) { cur.Add(bottoms[i]); }
                else { if (cur.Count > best.Count) { best = cur; } cur = new List<int> { bottoms[i] }; }
            }
            if (cur.Count > best.Count) { best = cur; }
            int platformY = best[best.Count / 2];
            // ③ 段 = 平台面 ±60 的 obj 按 x 近似 90 网格列（列间 x 差 ≤272 同链——无限地板：全部同链）
            var cols = infos.Where(x => Math.Abs(x.Bottom - platformY) <= 60).OrderBy(x => x.X).ToList();
            if (cols.Count < 3) { return null; }
            var flat = new List<ColumnTemplate>();
            var segCols = new List<(int X, int Bottom, string OS, string L0, string L1, string L2)>();
            Action flushSeg = () =>
            {
                if (segCols.Count == 0) { return; }
                int lo = segCols.Min(c => c.X), hi = segCols.Max(c => c.X);
                int w = Math.Max(2, (hi - lo + RoadConstants.ColW) / RoadConstants.ColW);
                var tpl = new ColumnTemplate { Id = flat.Count, WidthCols = w, SurfaceH = platformY, BscPad = false, SlantDy = null, SourceSurfaceY = platformY };
                foreach (var c in segCols) { tpl.Objs.Add(new RoadObj(c.X - lo, c.Bottom - platformY, c.OS, c.L0, c.L1, c.L2, 0)); }
                flat.Add(tpl);
                segCols.Clear();
            };
            int prevX = int.MinValue;
            foreach (var c in cols)
            {
                if (prevX != int.MinValue && c.X - prevX > 272) { flushSeg(); }
                segCols.Add(c);
                prevX = c.X;
            }
            flushSeg();
            if (flat.Count == 0) { return null; }
            int widthSum = flat.Sum(t => t.WidthCols);
            state.FallbackReason = $"obj 平台面 y={platformY}（底边聚簇 {best.Count}/{infos.Count}）段={flat.Count} 柱宽={widthSum}";
            state.ColumnCount = widthSum;
            return new RoadMaterialPack
            {
                MapId = mi.Id,
                TileSetName = "",
                FlatColumns = flat,
                SlantColumns = new List<ColumnTemplate>(),
                Steps = new List<StepTemplate>(),
                FarObjs = new List<FarObjEntry>(),
                LifeCandidates = new List<LifeCandidate>(),
                Backs = CloneBacks(mi),
                AnchorX = 0,
                AnchorY = platformY,
                MainSurfaceH = platformY,
            };
        }

        public static bool PackHasPixels(RoadMaterialPack pack)
        {
            try
            {
                var col = pack.FlatColumns.Count > 0 ? pack.FlatColumns[0] : (pack.SlantColumns.Count > 0 ? pack.SlantColumns[0] : null);
                if (col == null || col.Tiles.Count == 0 || string.IsNullOrEmpty(pack.TileSetName)) { return false; }
                var t = col.Tiles[0];
                string ts = t.Ts ?? pack.TileSetName;
                // ⚠️ ts 已含 .img（ParseTileUrl 原样保留）——此处不得再追加（白盒实证双缀全 NULL）
                string path = $"Map/Tile/{ts}/{UToString(t.U)}/{t.No}";
                WzService? wz; lock (_cfgLock) { wz = _wz; }
                if (wz == null) { Console.Error.WriteLine($"[PackPix] wz null"); return false; }
                byte[]? png = wz.ExtractPng(path);
                Console.Error.WriteLine($"[PackPix] {path} len={(png?.Length.ToString() ?? "NULL")}");
                return png != null && png.Length > 100;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取路属实体条带（NPC/怪动作）：先查 LRU，未命中走 WzService.BuildSpriteStrip 构建
        /// （内部持 _wzLock 线程安全）。命中/未命中/淘汰计数写回 state.EntityCache*。
        /// </summary>
        public static RoadStripEntry? GetEntityStrip(string mapId, string lifeId, string action, bool isMob, RoadState state)
        {
            WzService? wz;
            lock (_cfgLock) { wz = _wz; }
            if (wz == null || !wz.IsLoaded || string.IsNullOrEmpty(lifeId)) { return null; }

            string key = $"{mapId}{KeySep}{lifeId}{KeySep}{action}";
            lock (_stripLock)
            {
                if (_stripMap.TryGetValue(key, out var node) && node.List != null)
                {
                    _stripOrder.Remove(node);
                    _stripOrder.AddLast(node);
                    state.EntityCacheHits++;
                    state.EntityCacheCount = _stripMap.Count;
                    return node.Value.Entry;
                }
            }

            // 构建在锁外（BuildSpriteStrip 自持 WZ 锁，整条 PNG 解码耗时，避免长持 LRU 锁）
            state.EntityCacheMisses++;
            var (png, strip, _) = wz.BuildSpriteStrip(lifeId, action, isMob ? "Mob" : "Npc");
            if (strip == null) { return null; }
            var entry = new RoadStripEntry(png, strip);

            lock (_stripLock)
            {
                if (_stripMap.TryGetValue(key, out var oldNode) && oldNode.List != null)
                {
                    // 并发构建同键：保留先入条目，仅提升热度（后者丢弃）
                    _stripOrder.Remove(oldNode);
                    _stripOrder.AddLast(oldNode);
                    return oldNode.Value.Entry;
                }
                _stripMap[key] = _stripOrder.AddLast((key, entry));
                while (_stripMap.Count > RoadConstants.StripCacheMax && _stripOrder.First != null)
                {
                    var oldest = _stripOrder.First;
                    _stripOrder.RemoveFirst();
                    _stripMap.Remove(oldest.Value.Key);
                    state.EntityCacheEvictions++;
                }
                state.EntityCacheCount = _stripMap.Count;
            }
            return entry;
        }

        /// <summary>整包淘汰某源图的全部条带与 Decor（crossfade 交换完成后清旧代际；计数写回 state）。</summary>
        public static void EvictMapStrips(string mapId, RoadState? state)
        {
            string prefix = mapId + KeySep;
            lock (_stripLock)
            {
                var victims = _stripOrder.Where(n => n.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                foreach (var v in victims)
                {
                    _stripOrder.Remove(v);
                    _stripMap.Remove(v.Key);
                    if (state != null) { state.EntityCacheEvictions++; }
                }
                if (state != null) { state.EntityCacheCount = _stripMap.Count; }
            }
            lock (_decorLock)
            {
                _decorMap.Remove(mapId);
            }
        }

        /// <summary>取源图贴回装饰集（Extract 时产出缓存；未提取过返回 null——渲染/组装方空跳过）。</summary>
        public static RoadDecorLayer? GetDecors(string mapId)
        {
            lock (_decorLock)
            {
                if (_decorMap.TryGetValue(mapId, out var node) && node.List != null)
                {
                    _decorOrder.Remove(node);
                    _decorOrder.AddLast(node);
                    return node.Value.Layer;
                }
                return null;
            }
        }

        /// <summary>
        /// MapBack → RoadBack 全字段克隆（渲染链构建 RoadRenderRoad 用）。
        /// BS/No 从 ResourceUrl 反解析（MapService.ParseBacks 编码）；TileMode 由 WZ type 换算
        /// （bit0=H, bit1=V, bit2=ScrollH, bit3=ScrollV，对齐 MapService.Render.cs GetBackTileMode）；
        /// ScreenMode 提取期归一化（FINAL §二一处归一化，CameraDisplayMode 拍死 0）：
        /// visible = (ScreenMode == 0 || == 1)——旧 `!= 0` 转换已删（天空全白 bug 根因修复，黑盒 B7）。
        /// MapBack 无对应字段的（Dx/Dy/Fh）填 0。
        /// </summary>
        public static RoadBack ToRoadBack(MapBack b)
        {
            var (bs, no) = ParseBackUrl(b.Resource?.ResourceUrl ?? "");
            bool visible = b.ScreenMode == 0 || b.ScreenMode == 1;
            return new RoadBack(
                b.X, b.Y, b.Rx, b.Ry, b.Cx, b.Cy, BackTileMode(b.Type),
                bs, b.Type.ToString(), no.ToString(), 0, 0,
                b.Front, b.Alpha, visible, b.SpineNo, 0);
        }

        // ====== private ======

        /// <summary>列内单块 tile（可见坐标口径：VisLeft/VisTop = (X−ox, Y−oy)；IsSurface = u ∈ 表面类集合；
        /// Z/Id = 源 MapTile 值原样保留（Z = 精灵帧 z，Id = 图层内序）——柱位图内 (Z,Id) 复合排序键）。</summary>
        private sealed record ColTile(int VisLeft, int VisTop, int W, int H, string U, int No, string Ts, bool IsSurface, int Z, int Id);

        /// <summary>源图列：同一 x 的全部 tile；ColTop = 表面类 tile 的 min VisTop（无表面类 = int.MaxValue，不进链）。</summary>
        private sealed class SourceCol
        {
            public int X;
            public List<ColTile> Tiles = new();
            public int ColTop = int.MaxValue;
        }

        /// <summary>链：相邻列 x 差 ∈[88,92] 且 colTop 差 ≤8 的连续列串（缺口 ≤2 柱克隆补齐）；Surface = 链内 colTop 众数。</summary>
        private sealed class Chain
        {
            public List<SourceCol> Cols = new();
            public int Surface;
            public int MinX;
        }

        /// <summary>组：链按表面聚组（并入 = 与组代表差 ≤8 单层判定）；RepSurface = 组内 x 最小链的表面（漂移上限 8px）。</summary>
        private sealed class Group
        {
            public int Idx;
            public int RepSurface;
            public List<Chain> Chains = new();
            public List<SourceCol> Cols = new();
            public int ColCount;
            public long FootholdLen;
            public int LifeCount;
            public double Score;
            public int MinX;
            public int MaxX;
        }

        /// <summary>obj 中间态：解码结果（W/H/oy）+ 四元组；Decoded=false = 解码失败（仅可作 Decor 锚点口径）。</summary>
        private sealed record ObjInfo(int X, int Y, int Z, string OS, string L0, string L1, string L2, int W, int H, int Oy, bool Decoded)
        {
            /// <summary>真实底边 = o.Y − oy + H（FINAL §0 绑定判据——锚点口径已证伪：221000000 417px 大门三例错杀）。</summary>
            public int Bottom => Y - Oy + H;
        }

        /// <summary>块：等高洗牌单元（主干组连续柱列 + obj 边界修正 + 缺口列保留块内透明）；Surface = 块内列 colTop 众数。</summary>
        private sealed class Block
        {
            public List<SourceCol> Cols = new();
            public List<ObjInfo> Objs = new();
            public int Surface;
            public bool AllSlant;
            public int MinX => Cols[0].X;
            public int RightEdge => Cols[^1].X + RoadConstants.ColW;
        }

        /// <summary>life 中间态：Fh 权威 y（foothold 反查；无效 = int.MinValue）+ 评分归属组下标。</summary>
        private sealed class LifeRef
        {
            public string LifeId = "";
            public bool IsMob;
            public bool Hide;
            public int Y;
            public int Cy;
            public int FhY = int.MinValue;
            public int GroupIdx = -1;
            /// <summary>脚底 y（Cy 缺失回退 Y；Cy 仅渲染定位用——归属一律 Fh 权威优先）。</summary>
            public int FeetY => Cy != 0 ? Cy : Y;
        }

        /// <summary>
        /// 第一步：全层 tile 收集 → 解码 → 列集（全部列含纯装饰列；链构建时过滤无表面类列）。
        /// 可见像素坐标换算对齐权威渲染 DrawWorldSprite（可见左上 = (X−ox, Y−oy)）；
        /// 解码失败/1x1 哨兵块直接剔除——杜绝「计数可用、渲染空白」。
        /// </summary>
        private static (List<SourceCol> Cols, string TileSet) BuildTileColumns(MapInfo mi, WzService wz, CancellationToken ct)
        {
            var byX = new SortedDictionary<int, SourceCol>();
            string tileSet = "";
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var t in lay.Tiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var parsed = ParseTileUrl(t.Resource.ResourceUrl);
                    if (parsed == null) { continue; }
                    var (tsImg, u, no) = parsed.Value;
                    string path = $"Map/Tile/{tsImg}/{u}/{no}";
                    byte[]? png;
                    int ox, oy;
                    try
                    {
                        png = wz.ExtractPng(path);
                        (ox, oy) = wz.GetOrigin(path);
                        if (png == null || png.Length <= 4) { continue; }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { continue; }
                    if (tileSet == "") { tileSet = tsImg; }
                    var size = DecodeSize(png);
                    if (size.Width <= 1 && size.Height <= 1) { continue; }
                    if (!byX.TryGetValue(t.X, out var col))
                    {
                        col = new SourceCol { X = t.X };
                        byX[t.X] = col;
                    }
                    col.Tiles.Add(new ColTile(t.X - ox, t.Y - oy, size.Width, size.Height, u, no, tsImg, SurfaceUs.Contains(u), t.Z, t.Id));
                }
            }
            // colTop = 列内**表面类 tile** 的 min VisTop（FINAL 检查点①：非全部 tile——墙体/装饰行不混入口径）
            foreach (var col in byX.Values)
            {
                var surfTops = col.Tiles.Where(t => t.IsSurface).Select(t => t.VisTop).ToList();
                if (surfTops.Count > 0) { col.ColTop = surfTops.Min(); }
            }
            return (byX.Values.ToList(), tileSet);
        }

        /// <summary>
        /// 第二步 a：链构建（FINAL §一）——相邻列 x 差 ∈[88,92] 且 colTop 差 ≤8 同链；
        /// 缺口（x 差 &gt;92 且 colTop 差 ≤8）：一律克隆前柱整栈补齐（2026-09-01 甲方拍板「脚下地板必须存在」——
        /// 无上限克隆；colTop 差 &gt;8 → 链边界。红线⑪"禁伪造"由拍板覆盖（地板优先）。
        /// </summary>
        private static List<Chain> BuildChains(List<SourceCol> allCols)
        {
            var result = new List<Chain>();
            var ordered = allCols.Where(c => c.ColTop != int.MaxValue).OrderBy(c => c.X).ToList();
            var cur = new List<SourceCol>();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (cur.Count == 0)
                {
                    cur.Add(ordered[i]);
                    continue;
                }
                var prev = cur[^1];
                int dx = ordered[i].X - prev.X;
                bool sameTop = Math.Abs(ordered[i].ColTop - prev.ColTop) <= RoadConstants.ChainTopTol;
                if (sameTop && dx is >= RoadConstants.ChainGapMin and <= RoadConstants.ChainGapMax)
                {
                    cur.Add(ordered[i]);
                }
                else if (sameTop && dx > RoadConstants.ChainGapMax)
                {
                    // 甲方拍板 2026-09-01「人物脚下地板必须始终存在」：缺口一律克隆补齐（撤销"透明断开"——
                    // 透明缺口列会让脚下露天/蓝（缺口缩略图 993000718 等），地板缺失不可接受）
                    int slots = Math.Max(1, (dx - 1) / RoadConstants.ColW);
                    for (int k = 1; k <= slots; k++)
                    {
                        cur.Add(CloneColumn(prev.X + k * RoadConstants.ColW, prev));
                    }
                    cur.Add(ordered[i]);
                }
                else
                {
                    result.Add(FinishChain(cur));
                    cur = new List<SourceCol> { ordered[i] };
                }
            }
            if (cur.Count > 0) { result.Add(FinishChain(cur)); }
            return result;
        }

        /// <summary>克隆前柱整栈（tile 全拷贝 x 平移；colTop 随源柱——链内柱顶一致口径）。</summary>
        private static SourceCol CloneColumn(int x, SourceCol src)
        {
            int shift = x - src.X;
            var col = new SourceCol { X = x, ColTop = src.ColTop };
            foreach (var t in src.Tiles) { col.Tiles.Add(t with { VisLeft = t.VisLeft + shift }); }
            return col;
        }

        /// <summary>收链：链表面 = 链内 colTop 众数（平手取更小 y）。</summary>
        private static Chain FinishChain(List<SourceCol> cols)
        {
            int mode = cols.Select(c => c.ColTop).GroupBy(t => t)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            return new Chain { Cols = cols, Surface = mode, MinX = cols[0].X };
        }

        /// <summary>
        /// 第二步 b：组构建（FINAL §一）——链按 x 升序遍历，并入 = 与组代表差 ≤8；
        /// 组代表 = 组内源图 x 最小链的表面（首条并入链即锁定，单层判定无链式传递——红线⑮禁漂移雪球）。
        /// </summary>
        private static List<Group> BuildGroups(List<Chain> chains)
        {
            var result = new List<Group>();
            foreach (var chain in chains.OrderBy(c => c.MinX))
            {
                var g = result.FirstOrDefault(g => Math.Abs(g.RepSurface - chain.Surface) <= RoadConstants.GroupJoinTol);
                if (g == null)
                {
                    g = new Group { RepSurface = chain.Surface };
                    result.Add(g);
                }
                g.Chains.Add(chain);
            }
            foreach (var g in result)
            {
                g.Cols = g.Chains.SelectMany(c => c.Cols).OrderBy(c => c.X).ToList();
                g.ColCount = g.Cols.Count;
                g.MinX = g.Cols[0].X;
                g.MaxX = g.Cols[^1].X;
            }
            for (int i = 0; i < result.Count; i++) { result[i].Idx = i; }
            return result;
        }

        /// <summary>
        /// life 归属（评分用，FINAL §0 life.Fh 权威归属）：Fh 权威 y ±20 优先（实测系统差 6~15px）；
        /// 无 Fh 回退几何 feet ±60；双命中/无命中 → 取 |层 − 组表面| 最小者；全不中 → -1（不计分）。
        /// </summary>
        private static void AssignLifes(List<LifeRef> lifes, List<Group> groups)
        {
            foreach (var l in lifes)
            {
                int authY = l.FhY != int.MinValue ? l.FhY : l.FeetY;
                int tol = l.FhY != int.MinValue ? RoadConstants.LifeFhTol : RoadConstants.ObjGroundTol;
                int best = -1;
                int bestDist = int.MaxValue;
                for (int i = 0; i < groups.Count; i++)
                {
                    int d = Math.Abs(groups[i].RepSurface - authY);
                    if (d <= tol && d < bestDist)
                    {
                        bestDist = d;
                        best = i;
                    }
                }
                l.GroupIdx = best;
            }
        }

        /// <summary>
        /// 三证据评分（FINAL §一，修「柱数单证据选台地」）：
        /// score = 柱数 × (1 + 0.5 × Σ(lenᵢ/270)/3) × (1 + 0.25 × min(1, life数/3))。
        /// foothold：组表面 ±20 内水平长段（≥270）逐根累加（无单根 cap、无总和 cap——红线⑬⑭）；
        /// life：Hide==false 且 Fh 权威归属 == 该组（±20，几何回退口径见 AssignLifes）。
        /// </summary>
        private static void ScoreGroups(List<Group> groups, MapInfo mi, List<LifeRef> lifes)
        {
            foreach (var g in groups)
            {
                double fhSum = 0;
                foreach (var f in mi.Footholds)
                {
                    if (f.Y1 != f.Y2) { continue; }
                    int len = Math.Abs(f.X2 - f.X1);
                    if (len < RoadConstants.FootholdMinLen) { continue; }
                    if (Math.Abs(f.Y1 - g.RepSurface) > RoadConstants.LifeFhTol) { continue; }
                    fhSum += len / (double)RoadConstants.FootholdMinLen;
                }
                int lifeN = lifes.Count(l => !l.Hide && l.GroupIdx == g.Idx);
                g.FootholdLen = (long)(fhSum * RoadConstants.FootholdMinLen);
                g.LifeCount = lifeN;
                g.Score = g.ColCount * (1 + 0.5 * fhSum / 3.0) * (1 + 0.25 * Math.Min(1.0, lifeN / 3.0));
            }
        }

        /// <summary>主干组 = score 最高；平手链：柱数 → foothold 总长 → life 数 → y 最大（主街在下——FINAL §一）。</summary>
        private static Group PickMainGroup(List<Group> groups)
        {
            return groups
                .OrderByDescending(g => g.Score)
                .ThenByDescending(g => g.ColCount)
                .ThenByDescending(g => g.FootholdLen)
                .ThenByDescending(g => g.LifeCount)
                .ThenByDescending(g => g.RepSurface)
                .First();
        }

        /// <summary>
        /// 连续率（FINAL §0，作用域 = 主干组柱集）：柱集 x 升序后相邻柱对中 x 差 ∈[88,92] 的对数 ÷ (柱数−1)；
        /// &lt;0.6 → 整图 Usable 拒绝（调用方清空列池走主题池/兜底）。
        /// </summary>
        private static double MainContinuity(Group main)
        {
            var xs = main.Cols.Select(c => c.X).OrderBy(x => x).ToList();
            if (xs.Count <= 1) { return xs.Count == 1 ? 1.0 : 0.0; }
            int adj = 0;
            for (int i = 1; i < xs.Count; i++)
            {
                if (xs[i] - xs[i - 1] is >= RoadConstants.ChainGapMin and <= RoadConstants.ChainGapMax) { adj++; }
            }
            return (double)adj / (xs.Count - 1);
        }

        /// <summary>
        /// obj 全量收集（解码 W/H/oy 供真实底边判定；解码失败件 Decoded=false 仅可作 Decor 锚点口径）。
        /// .skel obj（oS/l2 名含 skel）一律排除并计数（MapService 无骨骼渲染器）。
        /// </summary>
        private static List<ObjInfo> CollectObjs(MapInfo mi, WzService wz, RoadState state, CancellationToken ct)
        {
            var result = new List<ObjInfo>();
            foreach (var layer in mi.Layers)
            {
                if (layer == null) { continue; }
                foreach (var o in layer.Objs)
                {
                    if (ct.IsCancellationRequested) { return result; }
                    var quad = ParseObjUrl(o.Resource.Frames.Count > 0 ? o.Resource.Frames[0].ResourceUrl : "");
                    if (quad == null) { continue; }
                    var (oS, l0, l1, l2) = quad.Value;
                    if (oS.Contains("skel", StringComparison.OrdinalIgnoreCase)
                        || l2.Contains("skel", StringComparison.OrdinalIgnoreCase))
                    {
                        state.SkelExcluded++;
                        continue;
                    }
                    string path = $"Map/Obj/{oS}.img/{l0}/{l1}/{l2}/0";
                    int w = 0, h = 0, oy = 0;
                    bool decoded = false;
                    try
                    {
                        var size = wz.GetPngSize(path);
                        var org = wz.GetOrigin(path);
                        if (size != null) { w = size.Value.W; h = size.Value.H; }
                        oy = org.y;
                        decoded = w > 0 && h > 0;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { decoded = false; }
                    result.Add(new ObjInfo(o.X, o.Y, o.Z, oS, l0, l1, l2, w, h, oy, decoded));
                }
            }
            return result;
        }

        /// <summary>
        /// 块状切块（FINAL §二）：
        /// ①基础块 = 主干组柱列 x 升序连续游程（x 差 ≤92），按块宽 8 柱软上限切；
        /// ②obj 保守区间 [o.X−W, o.X+W] 双缘修正：锚点落块区间归该块，区间右缘越块界 →
        ///   块边界右移至 obj 右缘+10，连锁重查至收敛（右移单调、有限柱集必收敛）；
        /// ③超宽 obj（宽 &gt; 8 柱）视为不可拆对——整 obj 冻结锚点所在块（右缘不加余量；禁分家——红线⑧）；
        /// ④绑定 = 真实底边贴主干层表面 ±60（红线⑦禁锚点口径回归），Dx 相对块首柱、Dy 相对块表面。
        /// 缺口列（组内链间断口被 obj 修正粘合的槽位）保留块内（Tiles 无该槽 tile → 渲染透明）；
        /// 块宽 WidthCols = 实柱数（缺口列不计柱宽——红线⑫）。
        /// </summary>
        private static (List<Block> Blocks, HashSet<ObjInfo> Bound) SplitBlocks(Group main, List<ObjInfo> objs)
        {
            // ①主干绑定候选预筛：真实底边贴主干表面 ±60 且锚点落主干组域（域内筛选失败件由 Decor 消解为丢弃）
            int domLo = main.MinX;
            int domHi = main.MaxX + RoadConstants.ColW;
            var cands = objs
                .Where(o => o.Decoded && o.X >= domLo && o.X < domHi
                            && Math.Abs(o.Bottom - main.RepSurface) <= RoadConstants.ObjGroundTol)
                .OrderBy(o => o.X).ToList();

            // ②基础块：连续游程（x 差 ≤92）× 块宽 8 柱软上限
            var blocks = new List<Block>();
            var ordered = main.Cols.OrderBy(c => c.X).ToList();
            var run = new List<SourceCol>();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (run.Count > 0 && ordered[i].X - run[^1].X > RoadConstants.ChainGapMax)
                {
                    blocks.Add(new Block { Cols = run });
                    run = new List<SourceCol>();
                }
                run.Add(ordered[i]);
                if (run.Count >= RoadConstants.BlockSoftMaxCols)
                {
                    blocks.Add(new Block { Cols = run });
                    run = new List<SourceCol>();
                }
            }
            if (run.Count > 0) { blocks.Add(new Block { Cols = run }); }

            // ③obj 双缘修正（右移收敛）：块边界右移至 obj 右缘+10，连锁吞并后续块头部柱/槽位
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var o in cands)
                {
                    int idx = blocks.FindIndex(b => o.X >= b.MinX && o.X < b.RightEdge);
                    if (idx < 0) { continue; }
                    int right = o.X + o.W
                        + (o.W > RoadConstants.ColW * RoadConstants.BlockSoftMaxCols ? 0 : RoadConstants.BlockEdgeMargin);
                    while (blocks[idx].RightEdge < right && idx + 1 < blocks.Count)
                    {
                        var take = blocks[idx + 1].Cols.TakeWhile(c => c.X < right).ToList();
                        if (take.Count == 0) { break; }
                        blocks[idx].Cols.AddRange(take);
                        blocks[idx + 1].Cols.RemoveRange(0, take.Count);
                        if (blocks[idx + 1].Cols.Count == 0) { blocks.RemoveAt(idx + 1); }
                        moved = true;
                    }
                }
            }

            // ④块表面（colTop 众数）/ 斜坡判定 / 绑定（锚点落块区间 → 该块）
            var bound = new HashSet<ObjInfo>();
            foreach (var b in blocks)
            {
                b.Surface = b.Cols.Select(c => c.ColTop).GroupBy(t => t)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                b.AllSlant = b.Cols.All(c => c.Tiles.Any(t => t.IsSurface && t.U.StartsWith("sl", StringComparison.Ordinal)));
                foreach (var o in cands)
                {
                    if (bound.Contains(o)) { continue; }
                    if (o.X >= b.MinX && o.X < b.RightEdge)
                    {
                        b.Objs.Add(o);
                        bound.Add(o);
                    }
                }
            }
            return (blocks, bound);
        }

        /// <summary>
        /// 块 → ColumnTemplate：Dx/Dy 相对块首柱可见左缘/块表面（源图绝对表面进 SurfaceH，与 MainSurfaceH 同坐标系，
        /// 跨图重定基由 SourceSurfaceY 承担）；整块斜坡段 → SlantColumns + SlantDy 数据保真（W13：坡列非 (0,0)——
        /// DyLeft/DyRight = 块首/末列 colTop − 块表面），混合块平地口径进 FlatColumns（斜坡段不参与洗牌路——FINAL §二）。
        /// </summary>
        private static void BuildBlockTemplates(List<Block> blocks, string mapId, int mainSurface, List<ColumnTemplate> flat, List<ColumnTemplate> slant)
        {
            int id = 0;
            foreach (var b in blocks)
            {
                var tpl = new ColumnTemplate
                {
                    Id = id++,
                    SourceMapId = mapId,
                    SourceSurfaceY = mainSurface,
                    WidthCols = b.Cols.Count,
                    SurfaceH = b.Surface,
                    BscPad = true,
                    SlantDy = b.AllSlant
                        ? (b.Cols[0].ColTop - b.Surface, b.Cols[^1].ColTop - b.Surface)
                        : null
                };
                foreach (var c in b.Cols)
                {
                    foreach (var t in c.Tiles)
                    {
                        tpl.Tiles.Add(new RoadTile(UCode(t.U), t.No, t.VisLeft - b.MinX, t.VisTop - b.Surface, t.Ts, t.Z, t.Id));
                    }
                }
                foreach (var o in b.Objs)
                {
                    // 贴地吸附（2026-08-30 用户实测「告示牌/灌木悬空 15~40px」）：绑定门按底边 ±60 放行了
                    // 站在源图略高地块的 obj——其底边高于块表面即悬空。底边埋地（rel>0）保持自然压边；
                    // 悬空（rel<0）把锚点 Dy 下移 |rel|，底边精确吸附到块表面（路面站桩观感）
                    int rel = o.Bottom - b.Surface;
                    int dy = o.Y - b.Surface;
                    if (rel < 0)
                    {
                        dy -= rel;
                    }
                    tpl.Objs.Add(new RoadObj(o.X - b.MinX, dy, o.OS, o.L0, o.L1, o.L2, o.Z));
                }
                // obj 排序键 = 源 WZ z（Z 主序，对齐 MapService 同层混合 (Z0,Z1) 口径——渲染侧按 Z 混排 tile+obj）；
                // 同 Z 保持收集顺序（层序→层内序，WZ 迭代序）稳定兜底（RoadObj 契约无 Id，模板序即次键）。
                // 禁按 Dx 排序：按 x 排会翻转重叠 obj 的遮挡关系（2026-08-30 用户实测「同层内要按原序排序」）
                (b.AllSlant ? slant : flat).Add(tpl);
            }
        }

        /// <summary>
        /// life 候选（FINAL §二）：归属主干组（Fh 权威 ±20 / 无 Fh 几何 ±60）才提取；
        /// Fh 众数层 ±20 无任何组命中 → 回退主干组 + FallbackReason（不静默）；
        /// Hide==true 不收；NPC 验 stand / 怪验 move+hit1+die1（动作条 LRU 由 GetEntityStrip 承担）。
        /// Dy = feet − 主干组表面（主干组 = SurfaceY 唯一来源）。
        /// </summary>
        private static List<LifeCandidate> ExtractLifes(List<LifeRef> lifes, List<Group> groups, Group main, WzService wz, RoadState state)
        {
            var result = new List<LifeCandidate>();
            if (!wz.IsLoaded) { return result; }
            var fhLifes = lifes.Where(l => l.FhY != int.MinValue).ToList();
            bool fhFallback = false;
            if (fhLifes.Count > 0)
            {
                int modeY = fhLifes.GroupBy(l => l.FhY).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                fhFallback = !groups.Any(g => Math.Abs(g.RepSurface - modeY) <= RoadConstants.LifeFhTol);
                if (fhFallback) { state.FallbackReason += $";life.Fh 众数层 {modeY} ±20 无组命中，回退主干组"; }
            }
            foreach (var l in lifes)
            {
                if (l.Hide || string.IsNullOrEmpty(l.LifeId)) { continue; }
                bool mine = l.GroupIdx == main.Idx;
                if (!mine && fhFallback && l.FhY != int.MinValue) { mine = true; }
                if (!mine) { continue; }
                var actions = wz.GetActionList(l.LifeId, l.IsMob ? "Mob" : "Npc");
                bool ok = l.IsMob
                    ? actions.Contains("move") && actions.Contains("hit1") && actions.Contains("die1")
                    : actions.Contains("stand");
                if (!ok) { continue; }
                result.Add(new LifeCandidate(l.IsMob, l.LifeId, l.FeetY - main.RepSurface));
            }
            return result;
        }

        /// <summary>
        /// 非主干组贴回 Decor（FINAL §二两域消解，不双轨）：
        /// ①非主干组 tile → Decor（可见坐标口径相对主干锚；全库实证无非表面类 u，表面类 tile 一并保真）；
        /// ②未绑定 obj：主干域内 = 筛选失败（悬空/埋地/解码失败）→ 丢弃；域外 = Decor 不丢弃（锚点口径）；
        /// ③前层 obj（WZ z ≥ DecorFrontZ，源图前层语义）→ Front（Entities 之后画可遮挡人物）；其余 → Behind。
        /// </summary>
        private static RoadDecorLayer CollectDecors(List<SourceCol> allCols, Group main, List<ObjInfo> objs, HashSet<ObjInfo> bound)
        {
            int anchorX = main.MinX;
            int anchorY = main.RepSurface;
            int domLo = main.MinX;
            int domHi = main.MaxX + RoadConstants.ColW;
            var mainXs = main.Cols.Select(c => c.X).ToHashSet();
            var behind = new List<RoadDecor>();
            var front = new List<RoadDecor>();
            foreach (var c in allCols)
            {
                if (mainXs.Contains(c.X)) { continue; }
                foreach (var t in c.Tiles)
                {
                    behind.Add(new RoadDecor(t.VisLeft - anchorX, t.VisTop - anchorY, false, t.Ts, UCode(t.U), t.No, null, null, null, null));
                }
            }
            foreach (var o in objs)
            {
                if (bound.Contains(o)) { continue; }
                if (o.X >= domLo && o.X < domHi) { continue; }
                var d = new RoadDecor(o.X - anchorX, o.Y - anchorY, true, null, 0, 0, o.OS, o.L0, o.L1, o.L2);
                if (o.Z >= RoadConstants.DecorFrontZ) { front.Add(d); } else { behind.Add(d); }
            }
            return new RoadDecorLayer(behind, front);
        }

        /// <summary>life 中间态收集：Fh → foothold 反查权威 y（MapLife.Fh 字段；0/查不到 = 无 Fh）。</summary>
        private static List<LifeRef> CollectLifeRefs(MapInfo mi)
        {
            var fhById = new Dictionary<int, MapFoothold>();
            foreach (var f in mi.Footholds)
            {
                if (f.Id != 0 && !fhById.ContainsKey(f.Id)) { fhById[f.Id] = f; }
            }
            var result = new List<LifeRef>();
            foreach (var l in mi.Lifes)
            {
                if (string.IsNullOrEmpty(l.LifeId)) { continue; }
                var r = new LifeRef
                {
                    LifeId = l.LifeId,
                    IsMob = l.Type == "m",
                    Hide = l.Hide,
                    Y = l.Y,
                    Cy = l.Cy
                };
                if (l.Fh != 0 && fhById.TryGetValue(l.Fh, out var fh)) { r.FhY = fh.Y1; }
                result.Add(r);
            }
            return result;
        }

        /// <summary>Decor 路级缓存写入（同键替换置新；LRU 上限 DecorCacheMax——红线⑥）。</summary>
        private static void PutDecors(string mapId, RoadDecorLayer layer)
        {
            lock (_decorLock)
            {
                if (_decorMap.TryGetValue(mapId, out var old))
                {
                    _decorOrder.Remove(old);
                    _decorMap.Remove(mapId);
                }
                _decorMap[mapId] = _decorOrder.AddLast((mapId, layer));
                while (_decorMap.Count > RoadConstants.DecorCacheMax && _decorOrder.First != null)
                {
                    var oldest = _decorOrder.First;
                    _decorOrder.RemoveFirst();
                    _decorMap.Remove(oldest.Value.Key);
                }
            }
        }

        /// <summary>PNG → 解码尺寸（仅取宽高；失败返回 1x1 哨兵由调用方剔除）。</summary>
        private static SKSizeI DecodeSize(byte[] png)
        {
            try
            {
                using var bmp = SKBitmap.Decode(png);
                return bmp == null ? new SKSizeI(1, 1) : new SKSizeI(bmp.Width, bmp.Height);
            }
            catch { return new SKSizeI(1, 1); }
        }

        /// <summary>back 深克隆（新 MapBack + 新 Sprite，防 MapInfo 后续突变穿透冻结契约；ResourceUrl 原样携带）。</summary>
        private static List<MapBack> CloneBacks(MapInfo mi)
        {
            var list = new List<MapBack>(mi.Backs.Count);
            foreach (var b in mi.Backs)
            {
                list.Add(new MapBack
                {
                    Id = b.Id, X = b.X, Y = b.Y, Cx = b.Cx, Cy = b.Cy, Rx = b.Rx, Ry = b.Ry,
                    Alpha = b.Alpha, FlipX = b.FlipX, Front = b.Front, Ani = b.Ani,
                    SpineNo = b.SpineNo, Type = b.Type, ScreenMode = b.ScreenMode,
                    Resource = new Sprite { ResourceUrl = b.Resource?.ResourceUrl ?? "" }
                });
            }
            return list;
        }

        /// <summary>tile ResourceUrl（Map/Tile/{tS}.img/{u}/{no}）反解析；非该格式返回 null。</summary>
        private static (string TsImg, string U, int No)? ParseTileUrl(string url)
        {
            var p = url.Split('/');
            if (p.Length < 5 || !p[0].Equals("Map", StringComparison.Ordinal) || !p[1].Equals("Tile", StringComparison.Ordinal)) { return null; }
            if (!int.TryParse(p[4], out var no)) { return null; }
            return (p[2], p[3], no);
        }

        /// <summary>obj ResourceUrl（Map/Obj/{oS}.img/{l0}/{l1}/{l2}/0）反解析四元组（oS 去 .img）；非该格式返回 null。</summary>
        private static (string OS, string L0, string L1, string L2)? ParseObjUrl(string url)
        {
            var p = url.Split('/');
            if (p.Length < 6 || !p[0].Equals("Map", StringComparison.Ordinal) || !p[1].Equals("Obj", StringComparison.Ordinal)) { return null; }
            return (p[2].EndsWith(".img", StringComparison.Ordinal) ? p[2][..^4] : p[2], p[3], p[4], p[5]);
        }

        /// <summary>back ResourceUrl 反解析 (bS 含 .img, no)；非该格式返回 ("", 0)。</summary>
        private static (string Bs, int No) ParseBackUrl(string url)
        {
            var p = url.Split('/');
            // Map/Back/{bS}.img/ani/{no}/0 或 Map/Back/{bS}.img/back|spine/{no}
            if (p.Length < 5 || !p[0].Equals("Map", StringComparison.Ordinal) || !p[1].Equals("Back", StringComparison.Ordinal)) { return ("", 0); }
            return int.TryParse(p[4], out var no) ? (p[2], no) : (p[2], 0);
        }

        /// <summary>WZ back type 0-7 → TileMode 位（bit0=H, bit1=V, bit2=ScrollH, bit3=ScrollV；对齐 MapService.Render.cs:15 GetBackTileMode）。</summary>
        private static int BackTileMode(int type) => type switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 5,
            5 => 10,
            6 => 7,
            7 => 11,
            _ => 0
        };
    }
}
