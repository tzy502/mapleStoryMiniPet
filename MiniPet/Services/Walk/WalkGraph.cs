using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;

namespace MiniPet.Services.Walk
{
    /// <summary>一条边上「怎么过去」的转移方式。</summary>
    public enum WalkLink
    {
        /// <summary>相邻段（prev/next），直接走过去。</summary>
        Adjacent,
        /// <summary>跳过去（水平 ≤110px、上升 ≤77px）。</summary>
        Jump,
        /// <summary>爬绳/梯（绳两端吸附的段之间）。</summary>
        Rope,
        /// <summary>掉下去（正下方最近的段）。</summary>
        Fall,
        /// <summary>同图传送门（tm=自己、tn 互指，SleepyWood 树图 up00/down00 型）：走到门口即传送到目标门位置。</summary>
        Warp,
    }

    /// <summary>路径的一跳：走到 FootholdId 这一段，用的是 Link 方式（Rope 时带绳 id）。</summary>
    public sealed record WalkHop(int FootholdId, WalkLink Link, int RopeId, double Cost, int FromFootholdId = 0);

    /// <summary>
    /// F18 行走图（对齐探针 `WalkDiag` 已验证的建图口径）：
    ///   节点 = foothold 段；边 = ① prev/next 相邻 ② 绳/梯两端 ③ 跳跃（水平 ≤110 / 升降 ≤77）
    ///   ④ 下落（正下方最近的段，单向）。
    /// 魔法密林实证：纯 prev/next 只能到 10/336 段，加完这些边后 **336/336 全连通**。
    /// 图按 MapInfo 引用缓存（同一张图只建一次）。
    /// </summary>
    public static class WalkGraph
    {
        public const double JumpMaxDx = 110;   // 跳跃水平极限（按 JumpReach 公式动态判定，此常量仅作历史上限参考）
        public const double JumpMaxUp = 77;    // 起跳 -555 / 重力 2000 → 555²/(2·2000) ≈ 77px

        /// <summary>
        /// sdlMS 物理跳距（胶水 2026-09-16「跳跃按 sdlMS 逻辑取走多少」）：满速水平 125px/s，
        /// 滞空 = (555 + √(555² + 2g·落差)) / g，落差 = 落点相对起点的下降量（0=同高 ≈69px，越低越远）。
        /// </summary>
        public static double JumpReach(double drop)
        {
            double d = Math.Max(drop, 0);
            double t = (555.0 + Math.Sqrt(555.0 * 555.0 + 2 * 2000.0 * d)) / 2000.0;
            return 125.0 * t;
        }

        /// <summary>sdlMS 下落（走出掉落 vy=0）水平位移：125 × √(2·落差/g)。</summary>
        public static double WalkOffReach(double drop)
        {
            double d = Math.Max(drop, 0);
            return 125.0 * Math.Sqrt(2 * d / 2000.0);
        }

        private sealed record Edge(int To, WalkLink Link, int RopeId, double Cost);

        private sealed class Graph
        {
            public Dictionary<int, MapFoothold> Fhs = new();
            public Dictionary<int, List<Edge>> Adj = new();
        }

        private static readonly Dictionary<MapInfo, Graph> _cache = new();
        private static readonly object _cacheLock = new();

        /// <summary>取（或建）该地图的行走图。</summary>
        private static Graph GetGraph(MapInfo map)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(map, out var g)) { return g; }
                g = Build(map);
                // 缓存上限：地图对象通常只有当前一张，超出就整体清空（避免长期驻留）
                if (_cache.Count > 8) { _cache.Clear(); }
                _cache[map] = g;
                return g;
            }
        }

        /// <summary>
        /// 规划：从 startFootholdId 到 goalFootholdId 的最短路。
        /// 返回值**不含起点**，每项 = 「下一段 + 怎么过去」；不可达返回 null。
        /// </summary>
        public static List<WalkHop>? Plan(MapInfo map, int startFootholdId, int goalFootholdId, Func<int, double>? segmentPenalty = null)
        {
            if (map == null) { return null; }
            var g = GetGraph(map);
            if (!g.Fhs.ContainsKey(startFootholdId) || !g.Fhs.ContainsKey(goalFootholdId)) { return null; }
            if (startFootholdId == goalFootholdId) { return new List<WalkHop>(); }

            var dist = new Dictionary<int, double> { [startFootholdId] = 0 };
            var prev = new Dictionary<int, Edge>();
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(startFootholdId, 0);
            while (pq.TryDequeue(out var cur, out var d))
            {
                if (d > dist.GetValueOrDefault(cur, double.MaxValue)) { continue; }
                if (cur == goalFootholdId) { break; }
                if (!g.Adj.TryGetValue(cur, out var edges)) { continue; }
                foreach (var e in edges)
                {
                    // 贪心「每条路尽量走一次」（胶水 2026-09-15）：已走过的段加罚，路径优先探索新段
                    double nd = d + e.Cost + (segmentPenalty?.Invoke(e.To) ?? 0);
                    if (nd < dist.GetValueOrDefault(e.To, double.MaxValue))
                    {
                        dist[e.To] = nd;
                        prev[e.To] = new Edge(cur, e.Link, e.RopeId, e.Cost);
                        pq.Enqueue(e.To, nd);
                    }
                }
            }
            if (!dist.ContainsKey(goalFootholdId)) { return null; }

            var hops = new List<WalkHop>();
            int node = goalFootholdId;
            while (node != startFootholdId && prev.TryGetValue(node, out var pe))
            {
                hops.Add(new WalkHop(node, pe.Link, pe.RopeId, pe.Cost));
                node = pe.To;
            }
            if (node != startFootholdId) { return null; }
            hops.Reverse();
            // 起跳段回填：每跳的 FromFootholdId = 上一跳的目标段（首跳 = 起点）——
            // 执行层据此「先沿链走到起跳段再跳」，杜绝在半路原地起跳够不到目标段
            int from = startFootholdId;
            for (int i = 0; i < hops.Count; i++)
            {
                hops[i] = hops[i] with { FromFootholdId = from };
                from = hops[i].FootholdId;
            }
            return hops;
        }

        /// <summary>从 startFootholdId 出发的可达段集合（含自身）。</summary>
        public static HashSet<int> Reachable(MapInfo map, int startFootholdId)
        {
            var seen = new HashSet<int>();
            if (map == null) { return seen; }
            var g = GetGraph(map);
            if (!g.Fhs.ContainsKey(startFootholdId)) { return seen; }
            var q = new Queue<int>();
            q.Enqueue(startFootholdId);
            seen.Add(startFootholdId);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!g.Adj.TryGetValue(cur, out var edges)) { continue; }
                foreach (var e in edges)
                {
                    if (seen.Add(e.To)) { q.Enqueue(e.To); }
                }
            }
            return seen;
        }

        /// <summary>可达集里「未走过段」中 Dijkstra 距离**最近**者（覆盖模式卡死自救的重规划目标：
        /// 卡住后的远目标路线往往原样复现同一批执行不了的跳，换近目标给执行一次不同的机会）。</summary>
        public static (int FootholdId, double Len)? NearestUnvisited(MapInfo map, int startFootholdId, HashSet<int> visited)
        {
            if (map == null || visited == null) { return null; }
            var g = GetGraph(map);
            if (!g.Fhs.ContainsKey(startFootholdId)) { return null; }
            var dist = new Dictionary<int, double> { [startFootholdId] = 0 };
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(startFootholdId, 0);
            while (pq.TryDequeue(out var cur, out var d))
            {
                if (d > dist.GetValueOrDefault(cur, double.MaxValue)) { continue; }
                if (!g.Adj.TryGetValue(cur, out var edges)) { continue; }
                foreach (var e in edges)
                {
                    double nd = d + e.Cost;
                    if (nd < dist.GetValueOrDefault(e.To, double.MaxValue))
                    {
                        dist[e.To] = nd;
                        pq.Enqueue(e.To, nd);
                    }
                }
            }
            (int Id, double Dist)? best = null;
            foreach (var kv in dist)
            {
                if (kv.Key == startFootholdId || visited.Contains(kv.Key)) { continue; }
                if (best == null || kv.Value < best.Value.Dist) { best = (kv.Key, kv.Value); }
            }
            return best != null ? (best.Value.Id, best.Value.Dist) : null;
        }

        /// <summary>段是否在图里（垂直段等建图时被排除的段不在；物理行走链可以走到它们上面）。</summary>
        public static bool InGraph(MapInfo map, int footholdId)
        {
            if (map == null) { return false; }
            return GetGraph(map).Fhs.ContainsKey(footholdId);
        }

        /// <summary>
        /// 点到图内段的吸附（运行时规划锚点用）：返回距 (px,py) 最近的图内段（y 差优先、水平差次之）。
        /// walker 站在图外段（垂直段/墙）时，Plan/Reachable/FarthestUnvisited 会全部静默失败——
        /// 规划前必须先把锚点吸附回图内（706030400 底层平台链含竖直小段实测）。
        /// </summary>
        public static int NearestInGraph(MapInfo map, float px, float py)
        {
            if (map == null) { return 0; }
            int best = 0;
            double bestD = double.MaxValue;
            foreach (var f in GetGraph(map).Fhs.Values)
            {
                float lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
                float cx = Math.Clamp(px, lo, hi);
                double d = Math.Abs(py - MapWalker.YOn(f, cx)) + Math.Abs(px - cx) * 0.5;
                if (d < bestD) { bestD = d; best = f.Id; }
            }
            return best;
        }

        /// <summary>
        /// 可达集里「累计路径最长」的段（折返分支的目标）——用 0-1 最短路不行（边权不同），
        /// 这里按 Dijkstra 的最短距离取最大者作为「最远的一段」。
        /// </summary>
        public static (int FootholdId, double Len)? Farthest(MapInfo map, int startFootholdId)
        {
            if (map == null) { return null; }
            var g = GetGraph(map);
            if (!g.Fhs.ContainsKey(startFootholdId)) { return null; }
            var dist = new Dictionary<int, double> { [startFootholdId] = 0 };
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(startFootholdId, 0);
            while (pq.TryDequeue(out var cur, out var d))
            {
                if (d > dist.GetValueOrDefault(cur, double.MaxValue)) { continue; }
                if (!g.Adj.TryGetValue(cur, out var edges)) { continue; }
                foreach (var e in edges)
                {
                    double nd = d + e.Cost;
                    if (nd < dist.GetValueOrDefault(e.To, double.MaxValue))
                    {
                        dist[e.To] = nd;
                        pq.Enqueue(e.To, nd);
                    }
                }
            }
            int bestId = startFootholdId;
            double bestLen = 0;
            foreach (var kv in dist)
            {
                if (kv.Value > bestLen) { bestLen = kv.Value; bestId = kv.Key; }
            }
            return (bestId, bestLen);
        }

        /// <summary>
        /// 「走到尽头」（§6 半程折返的**全长**口径）：从起点段出发，可达集内沿 <paramref name="facing"/>
        /// 方向水平延伸最远的段视为「可行走路径的尽头」（水平并列时取路由代价更大者，纯垂直图由此仍能选到端头），
        /// 规划到那里的路径写入 <paramref name="route"/>（不含起点），返回**全程长度**。
        ///
        /// 全长 = max(水平跨度, 路径实际位移累计 <see cref="TraverseLength"/>)：
        ///   - 水平跨度 = 尽头段中心 x 与起点段中心 x 的带符号差（碎片化平坦图的主口径，
        ///     104000000 实测 ≈2200px → 半程 ≈1100px）；
        ///   - 位移累计含跳跃/下落/爬绳的几何距离（垂直图的兜底口径）。
        /// ⚠️ 不能用 Dijkstra 边代价当全长：同链相邻段之间恒有代价 ~20 的跳跃捷径边，
        /// 会把全长压得远小于实际步数（104000000 实测半程被压到 402~502px → 约每 5 秒瞬移一次）。
        /// 规划不出非空路径（尽头 = 起点 / 不可达）→ 返回 0 且 route 清空。
        /// </summary>
        public static double RouteToEnd(MapInfo map, int startFootholdId, int facing, List<WalkHop> route, Func<int, double>? segmentPenalty = null)
        {
            route?.Clear();
            if (map == null || route == null) { return 0; }
            var g = GetGraph(map);
            if (!g.Fhs.TryGetValue(startFootholdId, out var startFh)) { return 0; }

            // Dijkstra 全量：可达距离（尽头并列时的次级排序）+ 全量代价表
            var dist = new Dictionary<int, double> { [startFootholdId] = 0 };
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(startFootholdId, 0);
            while (pq.TryDequeue(out var cur, out var d))
            {
                if (d > dist.GetValueOrDefault(cur, double.MaxValue)) { continue; }
                if (!g.Adj.TryGetValue(cur, out var edges)) { continue; }
                foreach (var e in edges)
                {
                    double nd = d + e.Cost;
                    if (nd < dist.GetValueOrDefault(e.To, double.MaxValue))
                    {
                        dist[e.To] = nd;
                        pq.Enqueue(e.To, nd);
                    }
                }
            }

            int dir = facing >= 0 ? 1 : -1;
            int endId = startFootholdId;
            double bestExtent = CenterX(startFh) * dir;
            double bestDist = 0;
            foreach (var kv in dist)
            {
                double extent = CenterX(g.Fhs[kv.Key]) * dir;
                if (extent > bestExtent || (extent == bestExtent && kv.Value > bestDist))
                {
                    bestExtent = extent;
                    bestDist = kv.Value;
                    endId = kv.Key;
                }
            }
            if (endId == startFootholdId) { return 0; }

            var path = Plan(map, startFootholdId, endId, segmentPenalty);
            if (path == null || path.Count == 0) { return 0; }
            route.AddRange(path);
            double span = (CenterX(g.Fhs[endId]) - CenterX(startFh)) * dir;
            return Math.Max(span, TraverseLength(map, startFootholdId, path));
        }

        /// <summary>
        /// 路径的「实际位移累计长度」（胶水的「模拟行走长度」= 累计长度含跳跃/爬绳）：
        /// 走 = 目标段段长；跳/落 = 两段最近端点间距；爬绳 = 绳长（缺绳退化端点间距）。
        /// 与路由代价（带惩罚的 Dijkstra 权重）无关。
        /// </summary>
        public static double TraverseLength(MapInfo map, int startFootholdId, IReadOnlyList<WalkHop> path)
        {
            if (map == null || path == null || path.Count == 0) { return 0; }
            var fhs = new Dictionary<int, MapFoothold>();
            foreach (var f in map.Footholds)
            {
                if (f.Id != 0 && !fhs.ContainsKey(f.Id)) { fhs[f.Id] = f; }
            }
            double sum = 0;
            int fromId = startFootholdId;
            for (int i = 0; i < path.Count; i++)
            {
                var hop = path[i];
                fhs.TryGetValue(hop.FootholdId, out var to);
                fhs.TryGetValue(fromId, out var from);
                if (to != null)
                {
                    if (hop.Link == WalkLink.Adjacent) { sum += SegLen(to); }
                    else if (hop.Link == WalkLink.Rope)
                    {
                        LadderRope? rope = null;
                        foreach (var r in map.Ropes ?? new List<LadderRope>())
                        {
                            if (r.Id == hop.RopeId) { rope = r; break; }
                        }
                        sum += rope != null ? Math.Abs(rope.Bottom - rope.Top) : EndpointDist(from, to);
                    }
                    else
                    {
                        // 与运行时 ResolveLink 同口径：同链相邻段之间 Dijkstra 常取代价 ~20 的跳跃捷径边，
                        // 但实际执行会归一化成「走」→ 按目标段段长计；真跳/落的位移才是端点间距
                        bool chained = from != null && (from.Next == to.Id || from.Prev == to.Id);
                        sum += chained ? SegLen(to) : EndpointDist(from, to);
                    }
                }
                fromId = hop.FootholdId;
            }
            return sum;
        }

        /// <summary>
        /// 可达集里「未走过段」中 Dijkstra 距离最远者（覆盖遍历的腿目标；距离口径与 <see cref="Farthest"/> 一致）。
        /// 距离**并列**时取水平延伸更远者（胶水「尽头 = 物理可达集内水平最延伸段」）：
        /// 碎片化图的「缺口段」与「缺口后大地面」到起点的图距同为一次跳跃边，字典序随机裁决
        /// 会把腿目标停在缺口段、全程少算一半（101000050 实测 fh3=960 vs fh4=1960）。
        /// 全部可达段都已走过 → null（调用方回退 extent 尽头口径）。分层树图（706030400 型）靠它把
        /// 传送边连着的上下层纳入覆盖腿——纯水平 extent 口径永远选中宽底层、上层只在进图时被路过。
        /// </summary>
        public static (int FootholdId, double Len)? FarthestUnvisited(MapInfo map, int startFootholdId, HashSet<int> visited)
        {
            if (map == null || visited == null) { return null; }
            var g = GetGraph(map);
            if (!g.Fhs.ContainsKey(startFootholdId)) { return null; }
            var dist = new Dictionary<int, double> { [startFootholdId] = 0 };
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(startFootholdId, 0);
            while (pq.TryDequeue(out var cur, out var d))
            {
                if (d > dist.GetValueOrDefault(cur, double.MaxValue)) { continue; }
                if (!g.Adj.TryGetValue(cur, out var edges)) { continue; }
                foreach (var e in edges)
                {
                    double nd = d + e.Cost;
                    if (nd < dist.GetValueOrDefault(e.To, double.MaxValue))
                    {
                        dist[e.To] = nd;
                        pq.Enqueue(e.To, nd);
                    }
                }
            }
            int bestId = 0;
            double bestLen = 0;
            double bestExtent = double.NegativeInfinity;
            float startX = CenterX(g.Fhs[startFootholdId]);
            foreach (var kv in dist)
            {
                if (kv.Key == startFootholdId || visited.Contains(kv.Key)) { continue; }
                double extent = Math.Abs(CenterX(g.Fhs[kv.Key]) - startX);
                if (kv.Value > bestLen
                    || (Math.Abs(kv.Value - bestLen) <= 1e-6 && extent > bestExtent))
                {
                    bestLen = Math.Max(bestLen, kv.Value);
                    bestExtent = extent;
                    bestId = kv.Key;
                }
            }
            return bestId != 0 ? (bestId, bestLen) : null;
        }

        private static float CenterX(MapFoothold f) => (f.X1 + f.X2) / 2f;

        /// <summary>两段四个端点之间的最小欧氏距离（跳/落一跳的实际位移近似）。</summary>
        private static double EndpointDist(MapFoothold? a, MapFoothold? b)
        {
            if (a == null || b == null) { return 0; }
            double best = double.MaxValue;
            foreach (var (ax, ay) in new[] { (a.X1, a.Y1), (a.X2, a.Y2) })
            {
                foreach (var (bx, by) in new[] { (b.X1, b.Y1), (b.X2, b.Y2) })
                {
                    double d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                    if (d < best) { best = d; }
                }
            }
            return best;
        }

        // ================= 建图 =================

        private static Graph Build(MapInfo map)
        {
            var g = new Graph();
            foreach (var f in map.Footholds)
            {
                if (f.Id == 0 || g.Fhs.ContainsKey(f.Id)) { continue; }
                // 垂直段（x1≈x2，墙/柱）排除出图：不能站立行走，WalkStep 沿它 x 恒定走不动
                //（100000000 fh#50 (3247,210)-(3247,150) 实测卡死，贪心去高台门的路径停在 26/47 跳）。
                // 它们的绳/梯语义由 MapInfo.Ropes（ladderRope）另行承载。
                if (Math.Abs(f.X1 - f.X2) < 0.5) { continue; }
                g.Fhs[f.Id] = f;
            }
            foreach (var id in g.Fhs.Keys) { g.Adj[id] = new List<Edge>(); }

            void Add(int from, int to, WalkLink link, int ropeId, double cost)
            {
                if (g.Adj.TryGetValue(from, out var list)) { list.Add(new Edge(to, link, ropeId, cost)); }
            }

            // ① 相邻（prev / next）。跨高度链邻按方向与落差分类（台阶 jump 联动，胶水规则；
            // SleepyWood 706030xxx 实测：链邻下台阶按「走」卡死 → 图被误判死胡同 → 全链没有全路径走）：
            //   · |Δy| ≤ 40 → Adjacent（走，贴线含小台阶）
            //   · 目标在上方 40 < Δy ≤ 77 → Jump（物理跳上去）
            //   · 目标在上方 Δy > 77 → 不建（真墙/断档，不可达）
            //   · 目标在下方任意落差 → Fall（走出边缘掉落，物理下落不限落差）
            foreach (var f in g.Fhs.Values)
            {
                void AddLink(int fromId, MapFoothold from, MapFoothold to)
                {
                    double dy = (to.Y1 + to.Y2) / 2.0 - (from.Y1 + from.Y2) / 2.0;   // 正 = 目标在下方
                    double ady = Math.Abs(dy);
                    if (ady <= 40) { Add(fromId, to.Id, WalkLink.Adjacent, 0, SegLen(to)); return; }
                    // ⚠️ 链邻跨高度必须加**物理可达约束**：WZ 链存在「跨区域传送链」（105010300 实测：
                    //    fh#349 next 直指 2400px 外、1380px 下方的出口段），无约束会建出物理不可能的
                    //    Fall 边 → 执行超时兜底瞬移（胶水日志「瞬间挪到出口、没走全路径」的元凶）。
                    //    约束口径（2026-09-16 改物理公式）：水平差 ≤ sdlMS 下落/跳跃可达位移。
                    double dx = Math.Min(Math.Min(Math.Abs(from.X1 - to.X1), Math.Abs(from.X1 - to.X2)),
                                         Math.Min(Math.Abs(from.X2 - to.X1), Math.Abs(from.X2 - to.X2)));
                    if (dy < 0)
                    {
                        // 目标在上方：跳得上（≤77）且水平 ≤ 跳距
                        if (ady <= JumpMaxUp && dx <= JumpReach(ady))
                        { Add(fromId, to.Id, WalkLink.Jump, 0, SegLen(to)); }
                        return;   // 上方超 77 = 墙不建；水平超跳距 = 跳不上不建
                    }
                    // 目标在下方：走出掉落水平位移 = WalkOffReach(dy)
                    if (dx <= WalkOffReach(dy) + 8)
                    { Add(fromId, to.Id, WalkLink.Fall, 0, ady * 1.2); }
                    return;
                    // else：上方超限 → 墙，不建
                }
                if (g.Fhs.TryGetValue(f.Next, out var n)) { AddLink(f.Id, f, n); }
                if (g.Fhs.TryGetValue(f.Prev, out var pv)) { AddLink(f.Id, f, pv); }
            }

            // ② 绳 / 梯：两端各吸附一段，双向边，代价 = 绳长 + 两端吸附距离。
            // F1a：吸附口径与运行时 MapWalker.RopeNearby 的判定包络**完全一致**（水平 ±15、竖直 ±20），
            // 保证规划出的绳跳一定执行得起来。旧口径「点到段距离 ≤150」远大于运行时窗口，
            // 会规划出永远抓不到的绳（102000000 rope7 绳底悬空差 11px、101000000 rope8 间隙 54.7px
            // → 人物在绳底 ±6~10px 来回徘徊、永不推进）。
            foreach (var r in map.Ropes ?? new List<LadderRope>())
            {
                var (topId, topD) = NearestAttach(g.Fhs, r.X, r.Top);
                var (botId, botD) = NearestAttach(g.Fhs, r.X, r.Bottom);
                if (topId == 0 || botId == 0 || topId == botId) { continue; }
                double cost = Math.Abs(r.Bottom - r.Top) + topD + botD;
                Add(topId, botId, WalkLink.Rope, r.Id, cost);
                Add(botId, topId, WalkLink.Rope, r.Id, cost);
            }

            // ②a 同图传送门（SleepyWood 706030400 树图型：up00/down00 tm=自己、tn 互指目标门）：
            // 建传送边（门吸附段 → 目标门吸附段，Jump 型）——分层树图的上下层靠它覆盖。
            // 之前一刀切排除 tm=自己 → 树图被判死胡同、上层/绳区永不覆盖（706030400 实测）。
            foreach (var p in map.Portals)
            {
                if (!p.HasTarget || !string.Equals(p.Tm, map.Id, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(p.Tn)) { continue; }
                MapPortal? dest = null;
                foreach (var q in map.Portals)
                {
                    if (string.Equals(q.PName, p.Tn, StringComparison.OrdinalIgnoreCase)) { dest = q; break; }
                }
                if (dest == null) { continue; }
                var segFrom = NearestFootholdNode(g.Fhs, p.X, p.Y);
                var segTo = NearestFootholdNode(g.Fhs, dest.X, dest.Y);
                if (segFrom == null || segTo == null || segFrom.Id == segTo.Id) { continue; }
                // ropeId=-portalId 标记传送门（负数不与真绳冲突）。⚠️ 方向语义 =「站在 ropeId 那扇门」：
                // 正向边站在 p 传到 dest；反向边站在 dest（经 dest.Tn）传回 p——两边必须各标各的门，
                // 否则反向执行会解析回脚下那扇门原地空传、还把整对门锁进已用集合（up00↔down00 单向断头）。
                Add(segFrom.Id, segTo.Id, WalkLink.Warp, -p.Id, 60);
                Add(segTo.Id, segFrom.Id, WalkLink.Warp, -dest.Id, 60);
            }

            // ②b 垂直立柱梯（MapService 把垂直 foothold 转成 id≥100000 的 LadderRope）：
            // 绳边 = prev/next **链邻段直连**。立柱两端常悬空 >20px（100000000 fh#39/50 底端距主街 64px），
            // F1a 吸附窗口对它不适用；链邻段是 WZ 数据给定的真实连通。双向（柱可上可下）。
            foreach (var f in map.Footholds)
            {
                if (Math.Abs(f.X1 - f.X2) >= 0.5f) { continue; }   // 只看垂直段
                int ropeId = 100000 + f.Id;
                if (map.Ropes == null || !map.Ropes.Any(r => r.Id == ropeId)) { continue; }
                if (!g.Fhs.TryGetValue(f.Prev, out var pNode) || !g.Fhs.TryGetValue(f.Next, out var nNode)) { continue; }
                double cost = Math.Abs((double)f.Y2 - f.Y1) + 1;
                Add(f.Prev, f.Next, WalkLink.Rope, ropeId, cost);
                Add(f.Next, f.Prev, WalkLink.Rope, ropeId, cost);
            }

            // ③ 跳跃：端点水平 ≤110 且 升降 ≤77，**单向**（从「后」端点段指向「前」端点段）——
            // 人只能往行进方向跳。旧双向口径会让 Dijkstra 规划出「向后跳」的 hop
            // （100000000 实测：向右走到 x=1170 却跳回 990，再走回 1352 又跳回 1170，来回锯齿）。
            // x 区间重叠的段（同层平行段/微台阶）之间**不建**跳跃边——它们之间「跳」没有几何意义，
            // 且重叠段的「最近端点对」左右关系不定，方向判定失效会建出双向小跳边
            // （100000000 y=334 平台实测：1.4s 内在 x=623~636 连跳 3 次）。
            var ids = g.Fhs.Keys.OrderBy(v => v).ToList();
            for (int i = 0; i < ids.Count; i++)
            {
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var a = g.Fhs[ids[i]];
                    var b = g.Fhs[ids[j]];
                    double dx = EndpointDx(a, b);
                    double dy = EndpointDy(a, b);
                    double aLo = Math.Min(a.X1, a.X2), aHi = Math.Max(a.X1, a.X2);
                    double bLo = Math.Min(b.X1, b.X2), bHi = Math.Max(b.X1, b.X2);
                    bool xOverlap = aLo <= bHi && bLo <= aHi;
                    // sdlMS 物理可达：dy（正=目标低）→ 跳距 = JumpReach(dy)；目标高于起跳 ≤77 时落差为负
                    double dropC = (Math.Min(b.Y1, b.Y2) + Math.Max(b.Y1, b.Y2)) / 2.0
                                 - (Math.Min(a.Y1, a.Y2) + Math.Max(a.Y1, a.Y2)) / 2.0;
                    double reach = JumpReach(dropC);
                    if (!xOverlap && dx <= reach && dropC >= -JumpMaxUp)
                    {
                        double cost = dx * 2 + 20;
                        // 方向 = 最近端点对的水平朝向：靠右的段是「前」，从靠左的段跳向靠右的段
                        double ax = NearestEndpointX(a, b.X1, b.X2);
                        double bx = NearestEndpointX(b, a.X1, a.X2);
                        var (from, to) = bx >= ax ? (a, b) : (b, a);
                        Add(from.Id, to.Id, WalkLink.Jump, 0, cost);
                    }
                    // ③b 跳+落差 Fall（胶水「能跳跃到的地方不算死胡同」）：目标在下方任意落差、
                    // 水平 ≤180（跳出边缘 vx=200 × 落差延长滞空）→ 单向 Fall（跳出掉落，物理落点扫掠兜底）。
                    // 非链邻（链邻下跨已在 ①）、x 不重叠（重叠=同层）。
                    if (!xOverlap && dx > JumpMaxDx && dx <= 180)
                    {
                        double drop = (Math.Min(b.Y1, b.Y2) + Math.Max(b.Y1, b.Y2)) / 2.0
                                    - (Math.Min(a.Y1, a.Y2) + Math.Max(a.Y1, a.Y2)) / 2.0;
                        var (nearA, farA) = (NearestEndpointX(a, b.X1, b.X2), 0.0);
                        // 方向同上：靠左段起跳跳向靠右段（下落方向自然向前）
                        double ax2 = NearestEndpointX(a, b.X1, b.X2);
                        double bx2 = NearestEndpointX(b, a.X1, a.X2);
                        if (drop > 40)
                        {
                            var (from2, to2) = bx2 >= ax2 ? (a, b) : (b, a);
                            Add(from2.Id, to2.Id, WalkLink.Fall, 0, dx * 1.5 + drop);
                        }
                    }
                }
            }

            // ④ 下落：从段的 x 中点垂直向下，落到正下方最近的一段（单向）
            foreach (var a in g.Fhs.Values)
            {
                float mx = (a.X1 + a.X2) / 2f;
                double ay = Math.Max(a.Y1, a.Y2);
                int bestB = 0;
                double bestDy = double.MaxValue;
                foreach (var b in g.Fhs.Values)
                {
                    if (b.Id == a.Id) { continue; }
                    int lo = Math.Min(b.X1, b.X2), hi = Math.Max(b.X1, b.X2);
                    if (mx < lo || mx > hi) { continue; }
                    double by = (b.Y1 + b.Y2) / 2.0;
                    double drop = by - ay;
                    if (drop > 5 && drop < bestDy) { bestDy = drop; bestB = b.Id; }
                }
                if (bestB != 0) { Add(a.Id, bestB, WalkLink.Fall, 0, bestDy * 1.2); }
            }

            return g;
        }

        /// <summary>点到段吸附（建图用）：返回距离最近（y 差优先）的段节点；无 → null。</summary>
        private static MapFoothold? NearestFootholdNode(Dictionary<int, MapFoothold> fhs, float px, float py)
        {
            MapFoothold? best = null;
            double bestD = double.MaxValue;
            foreach (var f in fhs.Values)
            {
                if (Math.Abs(f.X1 - f.X2) < 0.5f) { continue; }   // 垂直段不可站
                float lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
                if (px < lo || px > hi) { continue; }
                float segY = MapWalker.YOn(f, px);
                double d = Math.Abs(segY - py);
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }

        private static double SegLen(MapFoothold f) =>
            Math.Sqrt(Math.Pow(f.X2 - f.X1, 2) + Math.Pow(f.Y2 - f.Y1, 2));

        /// <summary>
        /// 绳端吸附（F1a）：与运行时 <see cref="MapWalker.RopeNearby"/> 同包络——绳端 x ±15 内站得上、
        /// 段面在绳端 y ±20 内才算「够得着」；命中段里取点到段距离最小者。无可执行吸附 → (0, 0)。
        /// 斜坡段上 |YOn(x) − ry| 沿 x 线性 → 检查水平窗两端即可；竖直段（梯）按 y 跨度判。
        /// </summary>
        private static (int Id, double Dist) NearestAttach(Dictionary<int, MapFoothold> fhs, float rx, float ry)
        {
            double best = double.MaxValue;
            int bestId = 0;
            foreach (var f in fhs.Values)
            {
                float lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
                float wl = Math.Max(lo, rx - MapWalker.RopeNearMaxX);
                float wh = Math.Min(hi, rx + MapWalker.RopeNearMaxX);
                if (wl > wh) { continue; }
                bool reachable;
                if (f.X1 == f.X2)
                {
                    // 竖直段（梯面）：x 对齐即可，y 看段跨度
                    float yLo = Math.Min(f.Y1, f.Y2), yHi = Math.Max(f.Y1, f.Y2);
                    reachable = ry >= yLo - MapWalker.RopeNearMaxY && ry <= yHi + MapWalker.RopeNearMaxY;
                }
                else
                {
                    reachable = Math.Abs(MapWalker.YOn(f, wl) - ry) <= MapWalker.RopeNearMaxY
                             || Math.Abs(MapWalker.YOn(f, wh) - ry) <= MapWalker.RopeNearMaxY;
                }
                if (!reachable) { continue; }
                double d = DistToSeg(rx, ry, f);
                if (d < best)
                {
                    best = d;
                    bestId = f.Id;
                }
            }
            return (bestId, best);
        }

        private static double DistToSeg(float px, float py, MapFoothold f)
        {
            double dx = f.X2 - f.X1, dy = f.Y2 - f.Y1, len2 = dx * dx + dy * dy;
            if (len2 <= 0) { return Math.Sqrt(Math.Pow(px - f.X1, 2) + Math.Pow(py - f.Y1, 2)); }
            double t = Math.Clamp(((px - f.X1) * dx + (py - f.Y1) * dy) / len2, 0, 1);
            return Math.Sqrt(Math.Pow(px - (f.X1 + t * dx), 2) + Math.Pow(py - (f.Y1 + t * dy), 2));
        }

        /// <summary>两段四个端点之间的最小水平距离。</summary>
        private static double EndpointDx(MapFoothold a, MapFoothold b)
        {
            double best = double.MaxValue;
            foreach (var ax in new[] { a.X1, a.X2 })
            {
                foreach (var bx in new[] { b.X1, b.X2 })
                {
                    double d = Math.Abs(ax - bx);
                    if (d < best) { best = d; }
                }
            }
            return best;
        }

        /// <summary>段 a 的两个端点中，离 (bx1/bx2) 最近端点对里 a 侧端点的 x（用于判定跳跃方向）。</summary>
        private static double NearestEndpointX(MapFoothold a, float bx1, float bx2)
        {
            double best = double.MaxValue;
            double bestX = a.X1;
            foreach (var ax in new[] { a.X1, a.X2 })
            {
                foreach (var bx in new[] { bx1, bx2 })
                {
                    double d = Math.Abs(ax - bx);
                    if (d < best) { best = d; bestX = ax; }
                }
            }
            return bestX;
        }

        /// <summary>两段四个端点之间的垂直差（取绝对差最小的一对；正 = b 在上方）。</summary>
        private static double EndpointDy(MapFoothold a, MapFoothold b)
        {
            double best = double.MaxValue;
            double signed = 0;
            foreach (var ay in new[] { a.Y1, a.Y2 })
            {
                foreach (var by in new[] { b.Y1, b.Y2 })
                {
                    double d = Math.Abs(ay - by);
                    if (d < best) { best = d; signed = ay - by; }
                }
            }
            return signed;
        }
    }
}
