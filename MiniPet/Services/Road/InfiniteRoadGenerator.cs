using System;
using System.Collections.Generic;

namespace MiniPet.Services.Road
{
    /// <summary>
    /// 无限路生成器（v13 定稿实现——docs/ai/algorithm-infinite-road-FINAL.md §四 运行期）。
    /// 等高块池 + 双向带模型：
    ///   - 块池 = pack.FlatColumns（主干组合并池全部块，提取期已重定基同表面）；
    ///   - 带体 _band = 块实例序列（一条 RoadColumn 承载一个块模板实例，覆盖宽 = ColW×WidthCols，
    ///     WorldX 连续累加、恒升序——SurfaceY 二分依赖）；
    ///   - 世界表面统一 = pack.MainSurfaceH + offset（无限平地口径：所有列 SurfaceY 恒定，
    ///     无台阶/斜坡分支——SlantDy 数据保真但不参与洗牌路）；
    ///   - 双向扩展：向左滚动 Prepend 新内容（Insert(0)、WorldX 递减），向右滚动 Append 新内容；
    ///     历史列保留不删（回收仅退出版图视图——双向回走零生成，往返重复 = FINAL 明示设计取舍）；
    ///   - 洗牌防重：随机取块避开最近 N 块模板，N = max(1, min(8, ⌊块数/2⌋))（红线 ⑨）；
    ///     降级档（合并池主干柱总数 &lt; 20）recent 关闭（FINAL §四：随机取块不查 recent 队列）。
    /// </summary>
    public sealed class InfiniteRoadGenerator : IRoadSurfaceProvider, IRoadColumnSource
    {
        private readonly RoadMaterialPack _pack;
        private readonly Random _rng;
        private readonly int _offset;

        /// <summary>带体：块实例序列（WorldX 升序；历史保留不删——双向回走供原始回放）。</summary>
        private readonly List<RoadColumn> _band = new();

        /// <summary>ActiveColumns 视图缓存（Advance 置脏，首次读取重建——控制器每 tick 读一次）。</summary>
        private readonly List<RoadColumn> _view = new();
        private bool _viewDirty = true;

        /// <summary>洗牌防重队列（按取块顺序；元素 = 模板引用，天然区分跨图同号模板）。</summary>
        private readonly List<ColumnTemplate> _recent = new();

        /// <summary>recent 容量：max(1, min(8, ⌊块数/2⌋))；0 = 降级档（不查 recent——FINAL §四）。</summary>
        private readonly int _recentN;

        /// <summary>块池非空标志（空池防御：Usable=false 误激活时不扩展、表面返回 null）。</summary>
        private readonly bool _hasPool;

        /// <summary>带左缘世界 x（首块左缘；空带时无意义）。</summary>
        private double _bandLeftX;

        /// <summary>带右缘世界 x（末块右缘；空带时 = 铺设起点游标）。</summary>
        private double _bandRightX;

        /// <summary>带世界表面统一值 = pack.MainSurfaceH + offset（同池块相接平齐，恒定）。</summary>
        private readonly int _bandSurfaceY;

        public double ScrollX { get; private set; }
        public double WindowWidth { get; set; } = RoadConstants.FallbackWindowW;
        public double WindowHeight { get; set; } = RoadConstants.FallbackWindowW;
        public int InitialSurfaceY { get; }
        public int CurrentSurfaceY { get; private set; }

        /// <summary>
        /// 激活柱只读视图 = 视图切片 [ScrollX − RecycleMargin, ScrollX + WindowWidth + 预生成余量]
        /// （与切片相交的列，含宽块伸入部分；历史带体不删——切片即「回收」，仅背离方向远端退出视图）。
        /// </summary>
        public IReadOnlyList<RoadColumn> ActiveColumns
        {
            get
            {
                if (_viewDirty)
                {
                    RebuildView();
                }
                return _view;
            }
        }

        public InfiniteRoadGenerator(RoadMaterialPack pack, double startWorldX, int startSurfaceY, Random? rng = null)
        {
            _pack = pack;
            _rng = rng ?? new Random();
            _offset = startSurfaceY - pack.MainSurfaceH;
            InitialSurfaceY = startSurfaceY;
            CurrentSurfaceY = startSurfaceY;
            _bandSurfaceY = pack.MainSurfaceH + _offset;
            ScrollX = startWorldX - WindowWidth / 2;

            // 第一步：洗牌档位判定——合并池主干柱总数（缺口不计柱宽，红线 ⑫）≥20 = 正常档
            //（recent 开启）；<20 = 降级档（recent 关闭，N=0——FINAL §0 Usable 唯一口径 / §四）
            int totalPoolCols = 0;
            foreach (var t in pack.FlatColumns)
            {
                totalPoolCols += Math.Max(1, t.WidthCols);
            }
            bool degradedPool = totalPoolCols < RoadConstants.NormalPoolColumns;
            if (degradedPool)
            {
                _recentN = 0;
            }
            else
            {
                _recentN = Math.Max(1, Math.Min(RoadConstants.RecentN, pack.FlatColumns.Count / 2));
            }
            _hasPool = pack.FlatColumns.Count > 0;

            // 第二步：初始铺带——以带右缘游标为铺设起点（窗口左缘再往左一个预生成余量），
            // 从左向右逐块 append，直至右余量达标；左余量由起点天然满足（EnsureLeft 空转兜底）
            _bandRightX = ScrollX - RoadConstants.PregenCols * RoadConstants.ColW;
            EnsureRight();
            EnsureLeft();
        }

        /// <summary>
        /// 滚动推进 + 双向带扩展（FINAL §四）。
        /// 第一步：重定窗口左缘 ScrollX = referenceEdge − WindowWidth（referenceEdge = 右缘，
        /// RoadModeController.AdvanceGenerator 语义）；
        /// 第二步：两个方向余量独立判定扩展——向右滚右余量不足即 Append（新内容在前方），
        /// 向左滚左余量不足即 Prepend（左走出新内容）；条件式内含方向语义，
        /// 无方向分支的双检查同时覆盖窗宽中途变化（controller 每帧写入）的兜底；
        /// 第三步：置视图脏（回收语义 = 视图切片随 ScrollX 平移，背离方向远端自然退出）。
        /// </summary>
        /// <param name="referenceEdge">参考缘世界 x（右缘，来源 RoadModeController.AdvanceGenerator）</param>
        public void Advance(double referenceEdge)
        {
            ScrollX = referenceEdge - WindowWidth;
            EnsureRight();
            EnsureLeft();
            _viewDirty = true;
        }

        /// <summary>
        /// 世界 x 处地面顶面 y（全带二分覆盖判定——带体历史不删，双向回走区域仍有效）；
        /// 无覆盖（带外/理论空洞）返回 null，调用方按「保持上一帧」硬约束兜底。
        /// </summary>
        public double? SurfaceY(double worldX)
        {
            if (_band.Count == 0)
            {
                return null;
            }
            // 第一步：二分找最后一条 WorldX ≤ worldX 的带列（_band 恒升序）
            int lo = 0;
            int hi = _band.Count - 1;
            int idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_band[mid].WorldX <= worldX)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (idx < 0)
            {
                return null;
            }
            // 第二步：宽块覆盖判定（覆盖宽 = ColW×WidthCols）——不覆盖即带外
            var col = _band[idx];
            if (worldX >= col.WorldX + Math.Max(1, col.WidthCols) * RoadConstants.ColW)
            {
                return null;
            }
            return col.SurfaceY;
        }

        /// <summary>
        /// 静态表面函数（保留供测试；无限平地口径——平段直返所在列 SurfaceY，
        /// 无台阶插值/斜坡分支；无覆盖返回 null）。线性扫描不要求列序。
        /// </summary>
        /// <param name="columns">候选列集（按 WorldX 升序或乱序均可）</param>
        /// <param name="worldX">世界 x</param>
        public static double? SurfaceYAt(IReadOnlyList<RoadColumn> columns, double worldX)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (worldX >= col.WorldX && worldX < col.WorldX + Math.Max(1, col.WidthCols) * RoadConstants.ColW)
                {
                    return col.SurfaceY;
                }
            }
            return null;
        }

        public void Dispose()
        {
        }

        // ====== private ======

        /// <summary>右余量不足（带右缘 &lt; 视口右缘 + 预生成余量）→ 持续 Append 直到达标。</summary>
        /// <returns>本次是否实际扩展过带体</returns>
        private bool EnsureRight()
        {
            double target = ScrollX + WindowWidth + RoadConstants.PregenCols * RoadConstants.ColW;
            bool grew = false;
            while (_bandRightX < target)
            {
                if (!AppendBlock())
                {
                    break;
                }
                grew = true;
            }
            return grew;
        }

        /// <summary>左余量不足（带左缘 &gt; ScrollX − 预生成余量）→ 持续 Prepend 直到达标。</summary>
        /// <returns>本次是否实际扩展过带体</returns>
        private bool EnsureLeft()
        {
            if (_band.Count == 0)
            {
                return false;
            }
            double target = ScrollX - RoadConstants.PregenCols * RoadConstants.ColW;
            bool grew = false;
            while (_bandLeftX > target)
            {
                if (!PrependBlock())
                {
                    break;
                }
                grew = true;
            }
            return grew;
        }

        /// <summary>
        /// 取一个块模板，Append 到带右端（WorldX 连续累加；带恒升序）。
        /// </summary>
        /// <returns>是否成功（块池空 = false，调用方终止扩展循环）</returns>
        private bool AppendBlock()
        {
            var tpl = TakeNextBlock();
            if (tpl == null)
            {
                return false;
            }
            int w = Math.Max(1, tpl.WidthCols);
            double x = _bandRightX;
            _band.Add(new RoadColumn { Template = tpl, WorldX = x, WidthCols = w, SurfaceY = _bandSurfaceY });
            _bandRightX = x + w * RoadConstants.ColW;
            if (_band.Count == 1)
            {
                _bandLeftX = x;
            }
            return true;
        }

        /// <summary>
        /// 取一个块模板，Prepend 到带左端（WorldX 递减、Insert(0)——升序保持，SurfaceY 二分依赖）。
        /// </summary>
        /// <returns>是否成功（空带/块池空 = false）</returns>
        private bool PrependBlock()
        {
            if (_band.Count == 0)
            {
                return false;
            }
            var tpl = TakeNextBlock();
            if (tpl == null)
            {
                return false;
            }
            int w = Math.Max(1, tpl.WidthCols);
            double x0 = _bandLeftX - w * RoadConstants.ColW;
            _band.Insert(0, new RoadColumn { Template = tpl, WorldX = x0, WidthCols = w, SurfaceY = _bandSurfaceY });
            _bandLeftX = x0;
            return true;
        }

        /// <summary>
        /// 洗牌取块（FINAL §四）：Weight 加权随机（绑定装饰 ×1.5 整数化——模板契约），
        /// 正常档先排除 recent 队列内模板（红线 ⑨：N = max(1, min(8, ⌊块数/2⌋))）；
        /// 候选为空（如单块池）→ 放开全池随机；降级档不查 recent（精确语义）。
        /// </summary>
        /// <returns>选中的块模板；块池空返回 null</returns>
        /// <summary>超马 1 式取块（2026-09-01 甲方拍板）：池块顺序循环平铺——不管重复、不洗牌、
        /// 不防重；地面必然连续（任意位置有块，人物脚下不悬空）。</summary>
        private int _pickIdx = -1;
        private ColumnTemplate? TakeNextBlock()
        {
            if (!_hasPool)
            {
                return null;
            }
            var pool = _pack.FlatColumns;
            _pickIdx = (_pickIdx + 1) % pool.Count;
            return pool[_pickIdx];
            // 第一步：组候选集（正常档滤 recent；滤后为空则放开全池）
            List<ColumnTemplate> candidates = new();
            if (_recentN > 0)
            {
                foreach (var t in pool)
                {
                    if (!_recent.Contains(t))
                    {
                        candidates.Add(t);
                    }
                }
            }
            if (candidates.Count == 0)
            {
                candidates.AddRange(pool);
            }
            // 第二步：Weight 加权随机
            int total = 0;
            foreach (var t in candidates)
            {
                total += t.Weight;
            }
            ColumnTemplate picked = candidates[candidates.Count - 1];
            if (total > 0)
            {
                int roll = _rng.Next(total);
                foreach (var t in candidates)
                {
                    if (roll < t.Weight)
                    {
                        picked = t;
                        break;
                    }
                    roll -= t.Weight;
                }
            }
            RecordRecent(picked);
            return picked;
        }

        /// <summary>取块后登记 recent（已在队内先移旧位再入队尾——LRU 语义；容量超限淘汰队头）。</summary>
        private void RecordRecent(ColumnTemplate tpl)
        {
            if (_recentN <= 0)
            {
                return;
            }
            _recent.Remove(tpl);
            _recent.Add(tpl);
            while (_recent.Count > _recentN)
            {
                _recent.RemoveAt(0);
            }
        }

        /// <summary>
        /// 重建 ActiveColumns 视图切片：与 [ScrollX − RecycleMargin, ScrollX + WindowWidth + 预生成余量]
        /// 相交的带列（宽块右缘伸入切片左界外沿也纳入）。
        /// </summary>
        private void RebuildView()
        {
            _view.Clear();
            if (_band.Count > 0)
            {
                double sliceStart = ScrollX - RoadConstants.RecycleMargin;
                double sliceEnd = ScrollX + WindowWidth + RoadConstants.PregenCols * RoadConstants.ColW;
                // 第一步：二分定位最后一条左缘 ≤ sliceStart 的带列（其宽块右缘可能伸入切片）
                int lo = 0;
                int hi = _band.Count - 1;
                int start = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_band[mid].WorldX <= sliceStart)
                    {
                        start = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                // 第二步：自起点向后线性收集至超出切片右界
                for (int i = start; i < _band.Count; i++)
                {
                    var col = _band[i];
                    if (col.WorldX >= sliceEnd)
                    {
                        break;
                    }
                    if (col.WorldX + Math.Max(1, col.WidthCols) * RoadConstants.ColW <= sliceStart)
                    {
                        continue;
                    }
                    _view.Add(col);
                }
            }
            _viewDirty = false;
        }
    }
}
