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
    }

    /// <summary>路径的一跳：走到 FootholdId 这一段，用的是 Link 方式（Rope 时带绳 id）。</summary>
    public sealed record WalkHop(int FootholdId, WalkLink Link, int RopeId, double Cost);

    /// <summary>
    /// F18 行走图（对齐探针 `WalkDiag` 已验证的建图口径）：
    ///   节点 = foothold 段；边 = ① prev/next 相邻 ② 绳/梯两端 ③ 跳跃（水平 ≤110 / 升降 ≤77）
    ///   ④ 下落（正下方最近的段，单向）。
    /// 魔法密林实证：纯 prev/next 只能到 10/336 段，加完这些边后 **336/336 全连通**。
    /// 图按 MapInfo 引用缓存（同一张图只建一次）。
    /// </summary>
    public static class WalkGraph
    {
        public const double JumpMaxDx = 110;   // 跳跃水平极限
        public const double JumpMaxUp = 77;    // 起跳 -555 / 重力 2000 → 555²/(2·2000) ≈ 77px
        public const double RopeAttachMax = 150;  // 绳端吸附上限

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
        public static List<WalkHop>? Plan(MapInfo map, int startFootholdId, int goalFootholdId)
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
                    double nd = d + e.Cost;
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

        // ================= 建图 =================

        private static Graph Build(MapInfo map)
        {
            var g = new Graph();
            foreach (var f in map.Footholds)
            {
                if (f.Id != 0 && !g.Fhs.ContainsKey(f.Id)) { g.Fhs[f.Id] = f; }
            }
            foreach (var id in g.Fhs.Keys) { g.Adj[id] = new List<Edge>(); }

            void Add(int from, int to, WalkLink link, int ropeId, double cost)
            {
                if (g.Adj.TryGetValue(from, out var list)) { list.Add(new Edge(to, link, ropeId, cost)); }
            }

            // ① 相邻（prev / next）
            foreach (var f in g.Fhs.Values)
            {
                if (g.Fhs.TryGetValue(f.Next, out var n)) { Add(f.Id, n.Id, WalkLink.Adjacent, 0, SegLen(n)); }
                if (g.Fhs.TryGetValue(f.Prev, out var p)) { Add(f.Id, p.Id, WalkLink.Adjacent, 0, SegLen(p)); }
            }

            // ② 绳 / 梯：两端各吸附最近段（≤150px），双向边，代价 = 绳长 + 两端吸附距离
            foreach (var r in map.Ropes ?? new List<LadderRope>())
            {
                var (topId, topD) = Nearest(g.Fhs, r.X, r.Top);
                var (botId, botD) = Nearest(g.Fhs, r.X, r.Bottom);
                if (topId == 0 || botId == 0 || topId == botId) { continue; }
                if (topD > RopeAttachMax || botD > RopeAttachMax) { continue; }
                double cost = Math.Abs(r.Bottom - r.Top) + topD + botD;
                Add(topId, botId, WalkLink.Rope, r.Id, cost);
                Add(botId, topId, WalkLink.Rope, r.Id, cost);
            }

            // ③ 跳跃：端点水平 ≤110 且 升降 ≤77，双向（代价含惩罚，让 Dijkstra 优先走/爬）
            var ids = g.Fhs.Keys.OrderBy(v => v).ToList();
            for (int i = 0; i < ids.Count; i++)
            {
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var a = g.Fhs[ids[i]];
                    var b = g.Fhs[ids[j]];
                    double dx = EndpointDx(a, b);
                    double dy = EndpointDy(a, b);
                    if (dx <= JumpMaxDx && Math.Abs(dy) <= JumpMaxUp)
                    {
                        double cost = dx * 2 + 20;
                        Add(a.Id, b.Id, WalkLink.Jump, 0, cost);
                        Add(b.Id, a.Id, WalkLink.Jump, 0, cost);
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

        private static double SegLen(MapFoothold f) =>
            Math.Sqrt(Math.Pow(f.X2 - f.X1, 2) + Math.Pow(f.Y2 - f.Y1, 2));

        private static (int Id, double Dist) Nearest(Dictionary<int, MapFoothold> fhs, float x, float y)
        {
            double best = double.MaxValue;
            int bestId = 0;
            foreach (var f in fhs.Values)
            {
                double d = DistToSeg(x, y, f);
                if (d < best) { best = d; bestId = f.Id; }
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
