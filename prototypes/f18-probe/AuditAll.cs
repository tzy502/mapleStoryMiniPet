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
using MiniPet.Services;
using MiniPet.Services.Walk;
using WzComparerR2.WzLib;

/// <summary>
/// --auditall [limit] [degree]：全部 BGM 候选地图批量静态审计。
/// 候选图全集 = ~/.minipet/music-track-maps.json 反向索引 ∪ 全 WZ 扫描出的「info/bgm 非空」地图。
/// （⚠️ 磁盘缓存每曲目只收录 TOP5 有名图 + moreCount（MusicCatalogService 设计口径），
///   直接取会漏图——已知校验案例 100000001/100000200/100000203/706020100 都不在其中，
///   故并用 WZ 全扫兜底；JSON 候选单独计数便于对照。）
/// 每图指标（全部用 MiniPet 既有 API 口径）：
///   totalFh / verticalFh            总 foothold 段数 / 垂直段（x1==x2，墙）数
///   reachableRatio                  WalkGraph.Reachable 最大可达集 / 总非垂直段（低 = 图碎片化）
///   portals / usablePortals         总门数 / 有目标且非同图门数
///   unreachablePortals              门吸附段（WalkModeService.NearestFoothold 口径）不在最大可达集的可用门数
///   ropes / ropeEdgesAttached       真绳数 / 两端都过 F1a 吸附窗（±15/±20，同 WalkGraph 建图）且两端段不同的绳数
///   crossHeightChain                prev/next 链邻且 |Δy中点|>40 的对数（现归 Jump）
///   crossHeightUnjumpable           其中 |Δy|>77（超过 JumpMaxUp，物理不可跳）的对数
///   isolatedSegments                不在最大可达集的段数
/// 输出 /tmp/audit-all.csv + 控制台汇总（四类问题图计数 + 每类 TOP10 + 已知案例校验）。
/// </summary>
static class AuditAll
{
    const string CsvPath = "/tmp/audit-all.csv";

    sealed class Row
    {
        public string MapId = "";
        public int LoadOk;              // 1=成功 0=失败
        public int TotalFh, VerticalFh, NonVerticalFh, MaxReach;
        public double ReachableRatio;
        public int Portals, UsablePortals, UnreachablePortals;
        public int Ropes, RopeEdgesAttached;
        public int CrossHeightChain, CrossHeightUnjumpable;
        public int IsolatedSegments;
        public string Note = "";
    }

    public static void Run(WzService wz, int limit, int degree)
    {
        var sw = Stopwatch.StartNew();
        var jsonIds = LoadCandidateMapIds();
        var wzIds = ScanWzBgmMapIds(wz, degree);
        var mapIds = jsonIds.Union(wzIds).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"[audit] 候选图全集 {mapIds.Count} 张 = 反向索引缓存 {jsonIds.Count}（每曲目 TOP5 截断）∪ WZ 有BGM {wzIds.Count}");
        if (limit > 0 && limit < mapIds.Count) { mapIds = mapIds.GetRange(0, limit); Console.WriteLine($"[audit] limit={limit} 只跑前 {mapIds.Count} 张"); }

        var svc = new MapService(wz, new CacheManager());
        var rows = new ConcurrentBag<Row>();
        int done = 0;
        Parallel.ForEach(mapIds, new ParallelOptions { MaxDegreeOfParallelism = degree }, id =>
        {
            Row r;
            try { r = AuditOne(svc, id); }
            catch (Exception ex) { r = new Row { MapId = id, LoadOk = 0, Note = Sanitize(ex.Message) }; }
            rows.Add(r);
            int d = Interlocked.Increment(ref done);
            if (d % 200 == 0 || d == mapIds.Count) { Console.WriteLine($"[audit] {d}/{mapIds.Count} {sw.Elapsed.TotalSeconds:F0}s 失败={rows.Count(x => x.LoadOk == 0)}"); }
        });

        WriteCsv(rows.OrderBy(r => r.MapId, StringComparer.Ordinal).ToList());
        Report(rows, sw, mapIds.Count);
    }

    // ---------- 候选图全集 ----------
    static List<string> LoadCandidateMapIds()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minipet", "music-track-maps.json");
        var set = new SortedSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var track in doc.RootElement.EnumerateObject())
        {
            if (!track.Value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) { continue; }
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("mapId", out var mid)) { continue; }
                string id = mid.ValueKind == JsonValueKind.String ? mid.GetString() : mid.GetRawText();
                if (!string.IsNullOrEmpty(id)) { set.Add(id); }
            }
        }
        return set.ToList();
    }

    /// <summary>全 WZ 扫「info/bgm 非空」的地图 id（每组独立 Wz_Structure 并行加载，ScanDiag 同款做法，零 WZ 锁竞争）。</summary>
    static List<string> ScanWzBgmMapIds(WzService wz, int degree)
    {
        var mapRoot = wz.FindNodeByPath("Map/Map");
        if (mapRoot == null) { Console.WriteLine("[audit] Map/Map 不存在，WZ 兜底扫描跳过"); return new List<string>(); }
        var groups = new Dictionary<int, List<string>>();
        for (int g = 0; g <= 9; g++)
        {
            var names = new List<string>();
            var gn = mapRoot.FindNodeByPath("Map" + g);
            if (gn != null)
            {
                foreach (WzComparerR2.WzLib.Wz_Node n in gn.Nodes)
                {
                    if (n.Text.EndsWith(".img", StringComparison.Ordinal)) { names.Add(n.Text.Substring(0, n.Text.Length - 4)); }
                }
            }
            groups[g] = names;
        }
        string mapDir = FindMapDir(wz, groups);
        if (mapDir == null) { Console.WriteLine("[audit] 找不到 Map 分组目录，WZ 兜底扫描跳过"); return new List<string>(); }

        var found = new ConcurrentBag<string>();
        var swScan = Stopwatch.StartNew();
        Parallel.ForEach(groups.Where(x => x.Value.Count > 0),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) },
            kv =>
            {
                try
                {
                    WzComparerR2.WzLib.Wz_Structure.DefaultAutoDetectExtFiles = true;
                    WzComparerR2.WzLib.Wz_Structure.DefaultImgCheckDisabled = true;
                    var wzs = new WzComparerR2.WzLib.Wz_Structure();
                    WzComparerR2.WzLib.Wz_Node root = null;
                    wzs.LoadWzFolder(Path.Combine(mapDir, "Map" + kv.Key), ref root, false);
                    if (root == null) { return; }
                    foreach (var id in kv.Value)
                    {
                        try
                        {
                            var img = root.FindNodeByPath(id + ".img")?.GetValue<WzComparerR2.WzLib.Wz_Image>();
                            if (img == null || !img.TryExtract()) { continue; }
                            var bgm = MusicDecisions.NormalizeBgm(img.Node.FindNodeByPath("info")?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                            if (bgm != null) { found.Add(id); }
                            img.Unextract();
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[audit] WZ 扫描 Map{kv.Key} 组失败: {ex.Message}"); }
            });
        Console.WriteLine($"[audit] WZ 有BGM 扫描 {swScan.Elapsed.TotalSeconds:F0}s → {found.Count} 张");
        return found.ToList();
    }

    /// <summary>样本 img 的物理 WzFile 路径上两级 = 各分组 Map0..9 的父目录（ScanDiag 同款）。</summary>
    static string FindMapDir(WzService wz, Dictionary<int, List<string>> groups)
    {
        var kv = groups.First(x => x.Value.Count > 0);
        string id = kv.Value[0];
        try
        {
            var img = wz.FindNodeByPath($"Map/Map/Map{kv.Key}/{id}.img")?.GetValue<WzComparerR2.WzLib.Wz_Image>();
            if (img == null) { return null; }
            img.TryExtract();
            string p1 = ((img.WzFile as WzComparerR2.WzLib.Wz_File)?.FileStream as FileStream)?.Name;
            string dir = p1 != null ? Path.GetDirectoryName(p1) : null;
            return dir != null ? Path.GetDirectoryName(dir) : null;
        }
        catch { return null; }
    }

    // ---------- 单图审计 ----------
    static Row AuditOne(MapService svc, string mapId)
    {
        var row = new Row { MapId = mapId };
        var map = svc.LoadMap(mapId);
        if (map == null) { row.Note = "load_failed"; return row; }
        row.LoadOk = 1;
        row.TotalFh = map.Footholds.Count;

        // WalkGraph.Build 同口径的「立足段」：id≠0、去重、非垂直（x1≈x2 是墙，进不了图）
        var segs = new Dictionary<int, MapFoothold>();
        foreach (var f in map.Footholds)
        {
            if (f.Id == 0 || segs.ContainsKey(f.Id)) { continue; }
            if (Math.Abs(f.X1 - f.X2) < 0.5) { row.VerticalFh++; continue; }
            segs[f.Id] = f;
        }
        row.NonVerticalFh = segs.Count;

        // 最大可达集：Reachable 单调（s∈Reachable(t) ⇒ Reachable(s)⊆Reachable(t)），
        // 已被覆盖的段不可能给出更大集合 → 跳过，结果与「每段都跑一遍」完全一致
        var covered = new HashSet<int>();
        HashSet<int> maxSet = new();
        foreach (var id in segs.Keys)
        {
            if (covered.Contains(id)) { continue; }
            var r = WalkGraph.Reachable(map, id);
            covered.UnionWith(r);
            if (r.Count > maxSet.Count) { maxSet = r; }
        }
        row.MaxReach = maxSet.Count;
        row.ReachableRatio = segs.Count == 0 ? 0 : Math.Round((double)maxSet.Count / segs.Count, 4);
        row.IsolatedSegments = segs.Count - maxSet.Count;

        // 门：总数 / 可用（有目标且非同图）/ 可用中吸附段不在最大可达集的
        row.Portals = map.Portals.Count;
        string selfId = mapId.PadLeft(9, '0');
        foreach (var p in map.Portals)
        {
            if (!p.HasTarget) { continue; }
            if (string.Equals(p.Tm, map.Id, StringComparison.Ordinal) || p.Tm.PadLeft(9, '0') == selfId) { continue; }
            row.UsablePortals++;
            var snap = NearestFoothold(map, p.X, p.Y);
            if (snap == null || !segs.ContainsKey(snap.Id) || !maxSet.Contains(snap.Id)) { row.UnreachablePortals++; }
        }

        // 绳：真绳（MapService 已 RemoveAll Id>=100000，剩下的都是 ladderRope 真绳）；
        // 两端吸附复刻 WalkGraph.Build→NearestAttach 的 F1a 窗口（对非垂直段，±15/±20）
        var realRopes = map.Ropes.Where(r => r.Id < 100000).ToList();
        row.Ropes = realRopes.Count;
        foreach (var r in realRopes)
        {
            var (topId, _) = NearestAttach(segs, r.X, r.Top);
            var (botId, _) = NearestAttach(segs, r.X, r.Bottom);
            if (topId != 0 && botId != 0 && topId != botId) { row.RopeEdgesAttached++; }
        }

        // 跨高度链邻：prev/next 链邻对（去重）|Δy中点|>40；其中 >77 = JumpMaxUp 之上，物理不可跳
        var seenPair = new HashSet<long>();
        foreach (var f in segs.Values)
        {
            TryChainPair(segs, f.Id, f.Next, seenPair, row);
            TryChainPair(segs, f.Id, f.Prev, seenPair, row);
        }
        return row;
    }

    static void TryChainPair(Dictionary<int, MapFoothold> segs, int fromId, int toId, HashSet<long> seen, Row row)
    {
        if (!segs.TryGetValue(toId, out var to)) { return; }
        long key = fromId < toId ? ((long)fromId << 32) | (uint)toId : ((long)toId << 32) | (uint)fromId;
        if (!seen.Add(key)) { return; }
        var from = segs[fromId];
        double dy = Math.Abs((to.Y1 + to.Y2) / 2.0 - (from.Y1 + from.Y2) / 2.0);
        if (dy > 40)
        {
            row.CrossHeightChain++;
            if (dy > WalkGraph.JumpMaxUp) { row.CrossHeightUnjumpable++; }
        }
    }

    /// <summary>WalkModeService.NearestFoothold 同口径：id≠0 全部段（含垂直）里点到线段距离最近者。</summary>
    static MapFoothold NearestFoothold(MapInfo map, float px, float py)
    {
        MapFoothold best = null;
        double bestD = double.MaxValue;
        foreach (var f in map.Footholds)
        {
            if (f.Id == 0) { continue; }
            double d = DistToSeg(px, py, f);
            if (d < bestD) { bestD = d; best = f; }
        }
        return best;
    }

    /// <summary>WalkGraph.Build 绳端吸附（F1a）同口径：x±15 窗口内、段面 y±20 内的非垂直段，取距离最小者。</summary>
    static (int Id, double Dist) NearestAttach(Dictionary<int, MapFoothold> fhs, float rx, float ry)
    {
        double best = double.MaxValue;
        int bestId = 0;
        foreach (var f in fhs.Values)
        {
            float lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
            float wl = Math.Max(lo, rx - MapWalker.RopeNearMaxX);
            float wh = Math.Min(hi, rx + MapWalker.RopeNearMaxX);
            if (wl > wh) { continue; }
            bool ok = Math.Abs(MapWalker.YOn(f, wl) - ry) <= MapWalker.RopeNearMaxY
                   || Math.Abs(MapWalker.YOn(f, wh) - ry) <= MapWalker.RopeNearMaxY;
            if (!ok) { continue; }
            double d = DistToSeg(rx, ry, f);
            if (d < best) { best = d; bestId = f.Id; }
        }
        return (bestId, best);
    }

    static double DistToSeg(float px, float py, MapFoothold f)
    {
        double dx = f.X2 - f.X1, dy = f.Y2 - f.Y1, len2 = dx * dx + dy * dy;
        if (len2 <= 0) { return Math.Sqrt(Math.Pow(px - f.X1, 2) + Math.Pow(py - f.Y1, 2)); }
        double t = Math.Clamp(((px - f.X1) * dx + (py - f.Y1) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(px - (f.X1 + t * dx), 2) + Math.Pow(py - (f.Y1 + t * dy), 2));
    }

    // ---------- 输出 ----------
    static void WriteCsv(List<Row> rows)
    {
        var lines = new List<string> { "mapId,loadOk,totalFh,verticalFh,nonVerticalFh,maxReach,reachableRatio,portals,usablePortals,unreachablePortals,ropes,ropeEdgesAttached,crossHeightChain,crossHeightUnjumpable,isolatedSegments,note" };
        foreach (var r in rows)
        {
            lines.Add($"{r.MapId},{r.LoadOk},{r.TotalFh},{r.VerticalFh},{r.NonVerticalFh},{r.MaxReach},{r.ReachableRatio},{r.Portals},{r.UsablePortals},{r.UnreachablePortals},{r.Ropes},{r.RopeEdgesAttached},{r.CrossHeightChain},{r.CrossHeightUnjumpable},{r.IsolatedSegments},{r.Note}");
        }
        File.WriteAllLines(CsvPath, lines);
        Console.WriteLine($"[audit] → {CsvPath}（{rows.Count} 行）");
    }

    static void Report(ConcurrentBag<Row> rows, Stopwatch sw, int total)
    {
        var all = rows.ToList();
        var ok = all.Where(r => r.LoadOk == 1).ToList();
        int failed = all.Count - ok.Count;
        int noFh = ok.Count(r => r.NonVerticalFh == 0);
        int totalRopes = ok.Sum(r => r.Ropes);
        int attachedRopes = ok.Sum(r => r.RopeEdgesAttached);

        var catA = ok.Where(r => r.UnreachablePortals > 0).OrderByDescending(r => r.UnreachablePortals).ThenByDescending(r => r.UsablePortals).ToList();
        var catB = ok.Where(r => r.Ropes - r.RopeEdgesAttached > 0).OrderByDescending(r => r.Ropes - r.RopeEdgesAttached).ToList();
        var catC = ok.Where(r => r.NonVerticalFh > 0 && r.ReachableRatio < 0.8).OrderBy(r => r.ReachableRatio).ThenByDescending(r => r.IsolatedSegments).ToList();
        var catD = ok.Where(r => r.CrossHeightUnjumpable > 0).OrderByDescending(r => r.CrossHeightUnjumpable).ToList();

        Console.WriteLine();
        Console.WriteLine($"===== 全量静态审计汇总（候选 {total} / 成功 {ok.Count} / 加载失败 {failed} / 无立足段 {noFh}）耗时 {sw.Elapsed.TotalMinutes:F1}min =====");
        Console.WriteLine($"立足段合计 {ok.Sum(r => r.NonVerticalFh)}（垂直墙 {ok.Sum(r => r.VerticalFh)}），真绳 {totalRopes}（两端吸附成立 {attachedRopes}，吸附失败 {totalRopes - attachedRopes}）");
        Console.WriteLine();
        void Cat(string name, List<Row> list, Func<Row, string> unit)
        {
            Console.WriteLine($"【{name}】{list.Count} 张（占成功 {Pct(list.Count, ok.Count)}）");
            foreach (var r in list.Take(10))
            {
                Console.WriteLine($"   {r.MapId}  {unit(r)}");
            }
            Console.WriteLine();
        }
        Cat("不可达门 >0（吸附段不在最大连通分量 → 走不出去/进不来）", catA,
            r => $"门{r.Portals} 可用{r.UsablePortals} 不可达{r.UnreachablePortals} 段{r.NonVerticalFh} 连通比{r.ReachableRatio:F2}");
        Cat("吸附失败绳 >0（绳盲区：绳端不在 F1a 窗口内）", catB,
            r => $"绳{r.Ropes} 吸附成立{r.RopeEdgesAttached} 失败{r.Ropes - r.RopeEdgesAttached} 段{r.NonVerticalFh} 连通比{r.ReachableRatio:F2}");
        Cat("连通比 <80%（图碎片化，遍历覆盖不全）", catC,
            r => $"段{r.NonVerticalFh} 最大可达{r.MaxReach} 比率{r.ReachableRatio:F2} 孤立{r.IsolatedSegments} 门{r.Portals}/可用{r.UsablePortals}");
        Cat("跨高度不可跳 >0（链邻 |Δy|>77 归走但物理跳不上去）", catD,
            r => $"链邻高差对{r.CrossHeightChain} 其中不可跳{r.CrossHeightUnjumpable} 段{r.NonVerticalFh} 连通比{r.ReachableRatio:F2}");

        // 已知案例校验
        Console.WriteLine("===== 已知案例校验 =====");
        foreach (var id in new[] { "100000200", "100000203", "100000001", "100000002", "100000003", "706020100", "910000000" })
        {
            var r = all.FirstOrDefault(x => x.MapId == id);
            if (r == null || r.LoadOk == 0) { Console.WriteLine($"   {id}: （未跑或加载失败）"); continue; }
            Console.WriteLine($"   {id}: 段{r.NonVerticalFh}(垂直{r.VerticalFh}) 连通比{r.ReachableRatio:F2} 门{r.Portals}/可用{r.UsablePortals}/不可达{r.UnreachablePortals} 绳{r.Ropes}/吸附{r.RopeEdgesAttached} 高差链{r.CrossHeightChain}/不可跳{r.CrossHeightUnjumpable} 孤立{r.IsolatedSegments}");
        }
    }

    static string Pct(int a, int b) => b == 0 ? "0%" : $"{100.0 * a / b:F1}%";

    static string Sanitize(string s) => (s ?? "").Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');
}
