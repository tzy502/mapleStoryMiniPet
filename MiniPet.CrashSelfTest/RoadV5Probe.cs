using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Road;
using SkiaSharp;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// v5 机制实测（2026-09-01 对抗评审用）：
/// L1 全量：foothold 资格层（水平 |x2-x1|≥270、同 y±4 合并、总长≥810）通过率 + scan 近似分类（tile.Y 口径）
/// L2 细推演：6 图（塔楼×4 + 双子层×2）foothold 层明细 / 全层合一列顶归属（解码 PNG）/ obj 真实底边（o.Y−oy+H）vs 锚点
/// 运行：MiniPet.CrashSelfTest.dll l1|l2
/// </summary>
public static class RoadV5Probe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var mode = args.Length > 0 ? args[0] : "l2";
                if (mode == "l1") { L1(); }
                else { L2(); }
                exitCode = 0;
            }
            catch (Exception ex) { Console.WriteLine($"probe error: {ex.Message}"); exitCode = 1; }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static (WzService wz, MapService mapSvc) Init()
    {
        var basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) { Environment.Exit(2); }
        return (wz, new MapService(wz, new CacheManager()));
    }

    /// <summary>foothold 水平长段 → 层候选（同 y±4 合并；len 累加、span 并、数累加）。</summary>
    private sealed record FhLayer(int Y, long Len, int Lo, int Hi, int Cnt);

    private static List<FhLayer> BuildFhLayers(List<MapFoothold> fhs)
    {
        var layers = new List<FhLayer>();
        foreach (var f in fhs)
        {
            if (f.Y1 != f.Y2) { continue; }
            int len = Math.Abs(f.X2 - f.X1);
            if (len < 270) { continue; }
            int lo = Math.Min(f.X1, f.X2), hi = Math.Max(f.X1, f.X2);
            int idx = -1;
            for (int i = 0; i < layers.Count; i++)
            {
                if (Math.Abs(layers[i].Y - f.Y1) <= 4) { idx = i; break; }
            }
            if (idx >= 0)
            {
                var old = layers[idx];
                layers[idx] = old with { Len = old.Len + len, Lo = Math.Min(old.Lo, lo), Hi = Math.Max(old.Hi, hi), Cnt = old.Cnt + 1 };
            }
            else
            {
                layers.Add(new FhLayer(f.Y1, len, lo, hi, 1));
            }
        }
        return layers;
    }

    private static List<List<int>> ClusterYs(List<int> ys)
    {
        var result = new List<List<int>>();
        if (ys.Count == 0) { return result; }
        var sorted = ys.OrderBy(x => x).ToList();
        var cur = new List<int> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i - 1] <= 8) { cur.Add(sorted[i]); }
            else { result.Add(cur); cur = new List<int> { sorted[i] }; }
        }
        result.Add(cur);
        return result;
    }

    private static void L1()
    {
        var (wz, mapSvc) = Init();
        var ids = wz.ListMapIds();

        object uLock = new object();
        var uDist = new Dictionary<string, HashSet<string>>();
        var uGlobal = new Dictionary<string, int>();
        var surfUsSet = new HashSet<string> { "bsc", "enH0", "enH1", "enV0", "enV1", "edU", "edD", "slLU", "slRU", "slLD", "slRD" };
        int total = 0, hasTile = 0, fhCand = 0, fhQual = 0, noQual = 0, fh3 = 0;
        long chainMeasured = 0, chainShort = 0, poolMeasured = 0, poolShort = 0, lifeChanged = 0, vsCurrentDiff = 0;
        var diffSamples = new List<string>();
        var keyBgmAll = new Dictionary<string, (int maps, long cols)>();
        var keyBgmOk = new Dictionary<string, (int maps, long cols, List<string> Names)>();
        var keyBgmVal = new Dictionary<string, (int maps, long cols)>();
        var badNames = new List<string>(); int invalidCnt = 0;
        int poolLt5 = 0, pool5to19 = 0, poolGe20 = 0;
        int multi = 0, twin = 0;
        long multiFail = 0, twinFail = 0, normalRow = 0;
        // 有候选层但 0 资格（810 门槛挡住）→ 按 maxLen 收集
        var blockedList = new Dictionary<string, (int Cand, long MaxLen)>();
        // 0 候选层（連 270px 水平段都没有）→ 按有无 tile 收集
        int noFhAny = 0;
        var multiTag = new Dictionary<string, string>();

        foreach (var mid in ids.OrderBy(x => x))
        {
            MapInfo? mi;
            try { mi = mapSvc.LoadMap(mid); }
            catch { mi = null; }
            if (mi == null) { continue; }
            total++;

            var colXs = new SortedSet<int>();
            var ysAll = new List<int>();
            var usAll = new HashSet<string>();
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var t in lay.Tiles) { colXs.Add(t.X); ysAll.Add(t.Y); }
                foreach (var t in lay.Tiles)
                {
                    var ss = (t.Resource.ResourceUrl ?? "").Split('/');
                    if (ss.Length >= 4) { usAll.Add(ss[3]); }
                }
            }
            // u 统计（全量累积）
            lock (uLock) { uDist[mid] = usAll; foreach (var u in usAll) { uGlobal[u] = uGlobal.GetValueOrDefault(u) + 1; } }
            if (colXs.Count == 0) { continue; }
            hasTile++;

            var clusters = ClusterYs(ysAll);
            int mainCount = clusters.Count > 0 ? clusters.Max(c => c.Count) : 0;
            int mainShare = mainCount > 0 ? mainCount * 100 / Math.Max(1, ysAll.Count) : 0;
            int surfClusters = clusters.Count;
            bool isMulti = surfClusters >= 5 && mainShare < 30;
            bool isTwin = surfClusters >= 2 && surfClusters <= 4 && mainShare >= 30 && mainShare <= 60;

            // ===== v9 近似指标（TODO-1 链长 / TODO-8 分歧率；近似口径：colTop≈列内 min t.Y，未解码 oy）=====
            var colTop = new Dictionary<int, int>();
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var t in lay.Tiles)
                {
                    colTop[t.X] = Math.Min(colTop.GetValueOrDefault(t.X, int.MaxValue), t.Y);
                }
            }
            var scols = colTop.OrderBy(k => k.Key).ToList();
            long bestChain = 0; int curC = 1;
            for (int i = 1; i < scols.Count; i++)
            {
                int dx = scols[i].Key - scols[i - 1].Key;
                int dy = Math.Abs(scols[i].Value - scols[i - 1].Value);
                if (dy <= 8 && (dx is >= 88 and <= 92 || dx is > 92 and <= 272)) { curC++; }
                else { if (curC > bestChain) { bestChain = curC; } curC = 1; }
            }
            if (curC > bestChain) { bestChain = curC; }
            if (scols.Count > 0) { chainMeasured++; if (bestChain < 20) { chainShort++; } }
            var chainY = new List<int>();
            for (int i = 0; i < scols.Count;)
            {
                int startTop = scols[i].Value; int j = i + 1;
                for (; j < scols.Count; j++)
                {
                    int dx = scols[j].Key - scols[j - 1].Key;
                    int dy = Math.Abs(scols[j].Value - scols[j - 1].Value);
                    if (dy <= 8 && (dx is >= 88 and <= 92 || dx is > 92 and <= 272)) { } else { break; }
                }
                chainY.Add(startTop);
                i = j;
            }
            var groups9 = new List<(int Y, int Cols)>();
            foreach (var cy in chainY)
            {
                int idx = -1;
                for (int i = 0; i < groups9.Count; i++) { if (Math.Abs(groups9[i].Y - cy) <= 8) { idx = i; break; } }
                if (idx >= 0) { groups9[idx] = (groups9[idx].Y, groups9[idx].Cols + 1); }
                else { groups9.Add((cy, 1)); }
            }
            if (groups9.Count > 0)
            {
                poolMeasured++;
                var scored9 = new List<(int Y, int Cols, double S, double SNoLife)>();
                foreach (var g in groups9)
                {
                    long fhT = 0;
                    foreach (var f2 in mi.Footholds)
                    {
                        if (f2.Y1 == f2.Y2 && Math.Abs(f2.X2 - f2.X1) >= 270 && Math.Abs(f2.Y1 - g.Y) <= 60) { fhT += Math.Abs(f2.X2 - f2.X1); }
                    }
                    int lifeN = mi.Lifes.Count(l => !l.Hide && Math.Abs((l.Cy != 0 ? l.Cy : l.Y) - g.Y) <= 60);
                    double f = 1 + 0.5 * Math.Min(1.0, fhT / 810.0);
                    double lf = 1 + 0.25 * Math.Min(1.0, lifeN / 3.0);
                    scored9.Add((g.Y, g.Cols, g.Cols * f * lf, g.Cols * f));
                }
                var pick = scored9.OrderByDescending(x => x.S).ThenByDescending(x => x.Cols).ThenBy(x => x.Y).First();
                var pickNoLife = scored9.OrderByDescending(x => x.SNoLife).ThenByDescending(x => x.Cols).ThenBy(x => x.Y).First();
                if (pick.Cols < 20) { poolShort++; }
                if (pick.Y != pickNoLife.Y) { lifeChanged++; }
                var curCluster = clusters.Count > 0 ? clusters.OrderByDescending(c => c.Count).ThenByDescending(c => c[0]).First() : new List<int>();
                if (curCluster.Count > 0 && Math.Abs(pick.Y - curCluster[0]) > 8)
                {
                    vsCurrentDiff++;
                    if (diffSamples.Count < 40) { diffSamples.Add($"{mid}({pick.Y}vs{curCluster[0]},{pick.S:F1})"); }
                }
                // ===== 主题池达成率（有效口径）：per bgmKey 聚合每图池柱数 + ulDate/offLimits 有效性 =====
                string bgmKey = wz.GetStringProperty($"{MapService.GetMapWzPath(mid)}/info/bgm") ?? "";
                var mapPath = MapService.GetMapWzPath(mid);
                int noMapCmd = wz.GetIntProperty($"{mapPath}/info/noMapCmd");
                int hideMinimap = wz.GetIntProperty($"{mapPath}/info/hideMinimap");
                int returnMap = wz.GetIntProperty($"{mapPath}/info/returnMap");
                bool validMap = noMapCmd == 0 && hideMinimap == 0 && returnMap != 0;
                if (bgmKey != "")
                {
                    var pb = pick.Cols;
                    keyBgmAll[bgmKey] = (keyBgmAll.GetValueOrDefault(bgmKey).maps + 1, keyBgmAll.GetValueOrDefault(bgmKey).cols);
                    if (validMap)
                    {
                        keyBgmVal[bgmKey] = (keyBgmVal.GetValueOrDefault(bgmKey).maps + 1, keyBgmVal.GetValueOrDefault(bgmKey).cols + pb);
                    }
                    if (pb >= 5)
                    {
                        var oldv = keyBgmOk.TryGetValue(bgmKey, out var v0) ? v0 : (0, 0, new List<string>());
                        var names = oldv.Names;
                        names.Add($"{mid}|{wz.GetMapName(mid) ?? "?"}|{pb}柱|noCmd={noMapCmd}|hid={hideMinimap}");
                        keyBgmOk[bgmKey] = (oldv.maps + 1, oldv.cols + pb, names);
                    }
                    if (pb < 5) { poolLt5++; }
                    else if (pb < 20) { pool5to19++; }
                    else { poolGe20++; }
                    if (!validMap) { invalidCnt++; }
                }
            }
            int rawFh = mi.Footholds.Count(f => f.Y1 == f.Y2 && Math.Abs(f.X2 - f.X1) >= 270);
            if (rawFh >= 3) { fh3++; }
            var layers = BuildFhLayers(mi.Footholds);
            if (layers.Count > 0) { fhCand++; } else { noFhAny++; }
            int qual = layers.Count(l => l.Len >= 810);
            if (qual > 0) { fhQual++; }
            else
            {
                noQual++;
                long maxLen = layers.Count > 0 ? layers.Max(l => l.Len) : 0;
                if (layers.Count > 0 && blockedList.Count < 200 && !blockedList.ContainsKey(mid))
                {
                    blockedList[mid] = (layers.Count, maxLen);
                }
                if (isMulti) { multiFail++; }
                if (isTwin) { twinFail++; }
            }
            if (isMulti) { multi++; if (multiTag.Count < 40 && !multiTag.ContainsKey(mid)) { multiTag[mid] = mid; } }
            if (isTwin) { twin++; }
            if (isMulti || isTwin || qual == 0)
            {
                Console.WriteLine($"FH|{mid}|{wz.GetMapName(mid) ?? ""}|tile={colXs.Count}|簇={surfClusters}|主={mainCount}({mainShare}%)|fh层={layers.Count}|资格={qual}|maxlen={(layers.Count > 0 ? layers.Max(l => l.Len) : 0)}|{(isMulti ? "多层 " : "")}{(isTwin ? "双子 " : "")}{(qual == 0 ? "无资格层" : "")}");
            }
        }

        Console.WriteLine($"\n=== v9 全量指标（近似口径：colTop≈列内 min t.Y，未解码 oy——链长偏保守）===");
        Console.WriteLine($"最长链 <20 柱的图 = {chainShort} / {chainMeasured}（{(chainMeasured > 0 ? chainShort * 100.0 / chainMeasured : 0):F1}%）[TODO-1]");
        Console.WriteLine($"v9 池（评分最高组）<20 柱（落主题池）= {poolShort} / {poolMeasured}（{(poolMeasured > 0 ? poolShort * 100.0 / poolMeasured : 0):F1}%）");
        Console.WriteLine($"life 加分改变选层结果 = {lifeChanged} / {poolMeasured}（{(poolMeasured > 0 ? lifeChanged * 100.0 / poolMeasured : 0):F1}%）[TODO-8 生命影响力]");
        Console.WriteLine($"v9 选层 vs 现状近似(主簇 tile.Y 众数) 异层 = {vsCurrentDiff} / {poolMeasured}（{(poolMeasured > 0 ? vsCurrentDiff * 100.0 / poolMeasured : 0):F1}%）[分歧率]");
        Console.WriteLine($"分歧样本（异层且两评分接近，top15）: " + string.Join(" / ", diffSamples.Take(15)));

        int valKeys = keyBgmVal.Count;
        var valReachKey = keyBgmOk.Where(k => k.Value.Names.Count > 0 && k.Value.cols >= 20).ToList();
        Console.WriteLine($"图级池柱数分布: <5 = {poolLt5}（不入池） 5~19 = {pool5to19}（主题池源） ≥20 = {poolGe20}（单图自足）");
        Console.WriteLine($"[近似有效口径（noMapCmd==0 且 hideMinimap==0 且 returnMap!=0）] 有效图bgmKey = {valKeys}；≥20达标 = {valReachKey.Count}（{(keyBgmOk.Count > 0 ? valReachKey.Count * 100.0 / keyBgmOk.Count : 0):F1}% / 全部581的{valReachKey.Count * 100.0 / 581:F1}%）");
        Console.WriteLine($"[不达标（合格图但Σ<20）的图名单（前 30，含 noCmd/hid 标志）:");
        var bad = keyBgmOk.Where(k => k.Value.cols < 20).OrderByDescending(k => k.Value.cols).SelectMany(k => k.Value.Names);
        foreach (var bn in bad.Take(30)) { Console.WriteLine($"  {bn}"); }
        Console.WriteLine($"不达标 bgmKey 样本（合格图多但 Σ<20，top10）: " + string.Join(" / ", keyBgmOk.Where(k => k.Value.cols < 20).OrderByDescending(k => k.Value.cols).Take(10).Select(k => $"{k.Key}(图{k.Value.maps},Σ{k.Value.cols})")));
        Console.WriteLine($"\n=== 有候选层但被 810 门槛挡下（0 资格），maxlen Top 40 ===");
        Console.WriteLine($"foothold 候选层(≥1 层) 图数 = {fhCand}；其中 资格层(≥810) 达标 = {fhQual}；0 资格层 = {noQual}（走 直方图谷→频次单峰 备用链）");
        Console.WriteLine($"0 候选层（连 1 条 ≥270px 水平段都没有）图数 = {noFhAny}（直入备用链）");
        Console.WriteLine($"[v8 加分覆盖] 原始 ≥3 根 ≥270px 水平段（不合并）的图数 = {fh3} / {hasTile}（{(hasTile > 0 ? fh3 * 100.0 / hasTile : 0):F1}%——v8 foothold 加分仅在≤{fh3} 图上可能生效）");
        Console.WriteLine($"多层高危(簇≥5 且主<30%) = {multi}；其中 0 资格层 = {multiFail}（占比 {(multi > 0 ? multiFail * 100.0 / multi : 0):F1}%）");
        Console.WriteLine($"双子层(簇2~4 且主 30~60%) = {twin}；其中 0 资格层 = {twinFail}（占比 {(twin > 0 ? twinFail * 100.0 / twin : 0):F1}%）");
        var surfOnly = uDist.Count(kv => kv.Value.All(u => surfUsSet.Contains(u)));
        var anyNonSurf = uDist.Count(kv => kv.Value.Any(u => !surfUsSet.Contains(u)));
        Console.WriteLine($"\n=== u 全量分布（{uDist.Count} 图有 tile 的图）：全 u ∈ 表面类集合的图 = {surfOnly}；存在非表面类 u 的图 = {anyNonSurf}");
        foreach (var kv in uGlobal.OrderByDescending(k => k.Value).Take(30))
        {
            Console.WriteLine($"  u={kv.Key} 图数={kv.Value}");
        }
        var nonSurfUs = uGlobal.Keys.Where(u => !surfUsSet.Contains(u)).OrderByDescending(u => uGlobal[u]).Take(20).ToList();
        Console.WriteLine($"非表面类 u Top20: {string.Join(",", nonSurfUs)}");
        int keysTotal = keyBgmAll.Count, keysReach = keyBgmOk.Count(k => k.Value.cols >= 20);
        Console.WriteLine($"\n=== 主题池达成率（per bgmKey；每图贡献=评分最高组柱数）===");
        Console.WriteLine($"bgmKey 总数 = {keysTotal}；有≥5柱合格图的 bgmKey = {keyBgmOk.Count}；其中合并池≥20 达标 = {keysReach}（{(keyBgmOk.Count > 0 ? keysReach * 100.0 / keyBgmOk.Count : 0):F1}%）");
        
        Console.WriteLine($"图级池柱数分布: <5 = {poolLt5}（不入池） 5~19 = {pool5to19}（主题池源） ≥20 = {poolGe20}（单图自足）");

        Console.WriteLine($"\n=== 有候选层但被 810 门槛挡下（0 资格），maxlen Top 40 ===");
        foreach (var kv in blockedList.OrderByDescending(k => k.Value.MaxLen).Take(40))
        {
            Console.WriteLine($"  {kv.Key} fh层={kv.Value.Cand} maxlen={kv.Value.MaxLen}");
        }
    }

    private static void L2()
    {
        var (wz, mapSvc) = Init();
        var targets = new[] { "221000000", "221000002", "221030300", "109010108", "102040301", "106030000", "000000000", "993000000", "100000000" };
        foreach (var mid in targets)
        {
            var mi = mapSvc.LoadMap(mid);
            if (mi == null) { Console.WriteLine($"\n===== {mid} LoadMap null"); continue; }
            Console.WriteLine($"\n===== {mid} {wz.GetMapName(mid) ?? ""} =====");

            var layers = BuildFhLayers(mi.Footholds);
            Console.WriteLine($"foothold 候选层（水平≥270、同y±4合并）:");
            foreach (var l in layers.OrderBy(l => l.Y))
            {
                Console.WriteLine($"  层y={l.Y} span=[{l.Lo}..{l.Hi}] 总长={l.Len} 段数={l.Cnt} {(l.Len >= 810 ? "【资格】" : "(<810)")}");
            }

            // 全层合一列顶（v5 §一 柱集口径）：每列 topY = min(可见顶 = t.Y − oy)；u = url 第4段
            var groups = new Dictionary<int, List<(int VisTop, string U)>>();
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var t in lay.Tiles)
                {
                    var url = t.Resource.ResourceUrl ?? "";
                    if (!groups.TryGetValue(t.X, out var list)) { list = new(); groups[t.X] = list; }
                    try
                    {
                        var (ox, oy) = wz.GetOrigin(url);
                        var png = wz.ExtractPng(url);
                        if (png == null || png.Length <= 4) { continue; }
                        using var bmp = SKBitmap.Decode(png);
                        if (bmp == null) { continue; }
                        var seg = url.Split('/');
                        string u = seg.Length >= 4 ? seg[3] : "?";
                        list.Add((t.Y - oy, u));
                    }
                    catch { }
                }
            }
            var cols = groups.Where(g => g.Value.Count > 0)
                .Select(g => new { X = g.Key, TopY = g.Value.Min(v => v.VisTop) })
                .OrderBy(c => c.X).ToList();
            Console.WriteLine($"全层合一列数={cols.Count}（skip 空列）; 列顶 topY 分布:");
            var topGroups = cols.GroupBy(c => c.TopY).OrderByDescending(g => g.Count()).Take(12);
            foreach (var tg in topGroups)
            {
                Console.WriteLine($"  topY={tg.Key} 列数={tg.Count()}");
            }

            // 柱归属（距层 y 最近：≤60 归；≤120 且无更近竞争归；否则丢）
            if (layers.Count > 0)
            {
                var assign = new Dictionary<int, List<int>>();
                foreach (var l in layers) { assign[l.Y] = new List<int>(); }
                int dropped = 0;
                var droppedTops = new Dictionary<int, int>();
                foreach (var c in cols)
                {
                    var nearest = layers.OrderBy(l => Math.Abs(l.Y - c.TopY)).First();
                    int d = Math.Abs(nearest.Y - c.TopY);
                    if (d <= 60) { assign[nearest.Y].Add(c.X); }
                    else if (d <= 120 && layers.Count(l => l.Y != nearest.Y && Math.Abs(l.Y - c.TopY) < d) == 0) { assign[nearest.Y].Add(c.X); }
                    else
                    {
                        dropped++;
                        droppedTops[c.TopY] = droppedTops.GetValueOrDefault(c.TopY) + 1;
                    }
                }
                Console.WriteLine($"柱归属: 丢弃={dropped}（>120px 或更近竞争）");
                foreach (var kv in assign.OrderBy(k => k.Key))
                {
                    Console.WriteLine($"  层y={kv.Key} 归柱={kv.Value.Count}");
                }
                // v5 主干层 = 资格层中柱数最多
                var qualLayers = layers.Where(l => l.Len >= 810).ToList();
                if (qualLayers.Count > 0)
                {
                    var winner = qualLayers.OrderByDescending(l => assign[l.Y].Count).ThenByDescending(l => l.Len).ThenBy(l => l.Y).First();
                    Console.WriteLine($"v5 主干层 = {winner.Y}（资格层柱数最多） 归柱={assign[winner.Y].Count}");
                }
                else
                {
                    Console.WriteLine($"v5 主干层 = 无资格层 → 走备用判据链（直方图谷→频次单峰）");
                    Console.WriteLine($"  直方图谷启发: 柱顶 topY 众数 = {cols.GroupBy(c => c.TopY).OrderByDescending(g => g.Count()).First().Key}（频次单峰兜底会选此层）");
                }
            }

            // v6 最长连续链（边 = x 差 ∈[88,92] 且 |colTop差| ≤ 8）
            Console.WriteLine($"v6 最长链计算（节点 {cols.Count} 列）:");
            var chains = new List<(int StartX, int Len, int YMode)>();
            int curLen = 1, curStart = 0;
            long curYSum = cols[0].TopY;
            var curYs = new List<int> { cols[0].TopY };
            for (int i = 1; i < cols.Count; i++)
            {
                bool edge = (cols[i].X - cols[i - 1].X) is >= 88 and <= 92 && Math.Abs(cols[i].TopY - cols[i - 1].TopY) <= 8;
                if (edge)
                {
                    curLen++; curYs.Add(cols[i].TopY);
                }
                else
                {
                    chains.Add((cols[curStart].X, curLen, curYs.GroupBy(y => y).OrderByDescending(g => g.Count()).First().Key));
                    curLen = 1; curStart = i; curYs = new List<int> { cols[i].TopY };
                }
            }
            chains.Add((cols[curStart].X, curLen, curYs.GroupBy(y => y).OrderByDescending(g => g.Count()).First().Key));
            foreach (var c in chains.OrderByDescending(c => c.Len))
            {
                Console.WriteLine($"  链: 起点x={c.StartX} 长={c.Len}柱 表面y≈{c.YMode}{(c.Len == chains.Max(x => x.Len) ? "  <== 最长" : "")}");
            }
            var best = chains.OrderByDescending(c => c.Len).First();
            Console.WriteLine($"  v6 池 = 最长链: 长={best.Len}柱 表面y≈{best.YMode} ({(best.Len >= 20 ? "≥20 达标" : "<20 → 回退链/主题池")})");

            // v7 口径：表面类 tile（u ∈ 集合）→ 列 colTop（表面类 min VisTop）→ 链（x 差 88~92 边 / (92,272] 缺口补齐 / 其他边界）
            var surfUs = new HashSet<string> { "bsc", "enH0", "enH1", "enV0", "enV1", "edU", "edD", "slLU", "slRU", "slLD", "slRD" };
            Console.WriteLine($"v7 表面口径计算:");
            var surfCols = new List<(int X, int TopY, List<string> Us)>();
            foreach (var g in groups)
            {
                var surf = g.Value.Where(v => surfUs.Contains(v.U)).ToList();
                if (surf.Count == 0) { continue; } // 非表面列（装饰类 only）→ 跳过
                surfCols.Add((g.Key, surf.Min(v => v.VisTop), surf.Select(v => v.U).Distinct().ToList()));
            }
            surfCols.Sort((a, b) => a.X.CompareTo(b.X));
            Console.WriteLine($"  表面列={surfCols.Count} / 全列={groups.Count}（非表面列={groups.Count - surfCols.Count}）");
            // 各组 u 统计（@ 表面列 y）
            foreach (var ug in surfCols.GroupBy(c => c.TopY).OrderByDescending(g => g.Count()).Take(10))
            {
                var us = ug.SelectMany(c => c.Us).Distinct().OrderBy(x => x);
                Console.WriteLine($"    表面y={ug.Key} 列={ug.Count()} u={string.Join(",", us)}");
            }
            // v7 链（含缺口补齐统计）
            var chains7 = new List<(int StartX, int Len, int YMode, int Fill)>();
            int curLen7 = 1, curStart7 = 0, curFill7 = 0;
            var curYs7 = new List<int> { surfCols[0].TopY };
            for (int i = 1; i < surfCols.Count; i++)
            {
                int dx = surfCols[i].X - surfCols[i - 1].X;
                bool same = Math.Abs(surfCols[i].TopY - surfCols[i - 1].TopY) <= 8;
                if (same && dx is >= 88 and <= 92) { curLen7++; curYs7.Add(surfCols[i].TopY); }
                else if (same && dx is > 92 and <= 272) { curLen7++; curYs7.Add(surfCols[i].TopY); curFill7 += (dx - 90 + 89) / 90; } // 缺口补齐列数（克隆）
                else
                {
                    chains7.Add((surfCols[curStart7].X, curLen7, curYs7.GroupBy(y => y).OrderByDescending(g => g.Count()).First().Key, curFill7));
                    curLen7 = 1; curStart7 = i; curFill7 = 0; curYs7 = new List<int> { surfCols[i].TopY };
                }
            }
            chains7.Add((surfCols[curStart7].X, curLen7, curYs7.GroupBy(y => y).OrderByDescending(g => g.Count()).First().Key, curFill7));
            Console.WriteLine($"  v7 链（x差∈(92,272]且colTop≤8 = 同链缺口补齐）:");
            foreach (var c in chains7.OrderByDescending(c => c.Len))
            {
                Console.WriteLine($"    链: x={c.StartX} 长={c.Len}柱 表面y≈{c.YMode} 缺口补齐={c.Fill}柱");
            }
            // 同表面组（链表面众数 ≤8 合并）
            var groups7 = new List<(int Y, int Cols, int Chains)>();
            foreach (var c in chains7.OrderByDescending(c => c.Len))
            {
                int idx = -1;
                for (int i = 0; i < groups7.Count; i++) { if (Math.Abs(groups7[i].Y - c.YMode) <= 8) { idx = i; break; } }
                if (idx >= 0) { groups7[idx] = (groups7[idx].Y, groups7[idx].Cols + c.Len, groups7[idx].Chains + 1); }
                else { groups7.Add((c.YMode, c.Len, 1)); }
            }
            Console.WriteLine($"  v7 同表面组:");
            foreach (var g7 in groups7.OrderByDescending(g => g.Cols))
            {
                Console.WriteLine($"    组: 表面y≈{g7.Y} 柱数={g7.Cols} 链数={g7.Chains}");
            }
            var best7 = groups7.OrderByDescending(g => g.Cols).First();
            Console.WriteLine($"  v7 池 = {best7.Y}（柱数最多组 {best7.Cols} 柱）{(best7.Cols >= 20 ? "≥20 达标" : "<20 → 回退/主题池")}（含缺口补齐列，非纯真实列）");

            // life.Fh 权威层分布（仲裁均值 vs 众数核验）
            var fhById = mi.Footholds.Where(f => f.Id != 0).ToDictionary(f => f.Id, f => f);
            var fhLayers = new List<(int FhId, int Y)>();
            foreach (var l in mi.Lifes)
            {
                if (l.Fh != 0 && fhById.TryGetValue(l.Fh, out var fh)) { fhLayers.Add((l.Fh, fh.Y1)); }
            }
            if (fhLayers.Count > 0)
            {
                Console.WriteLine($"  life.Fh 权威层(总数={fhLayers.Count}): 众数层={fhLayers.GroupBy(x => x.Y).OrderByDescending(g => g.Count()).First().Key}({fhLayers.GroupBy(x => x.Y).OrderByDescending(g => g.Count()).First().Count()}) 均值层={(int)fhLayers.Average(x => x.Y)}");
                foreach (var g in fhLayers.GroupBy(x => x.Y).OrderByDescending(g => g.Count())) { Console.WriteLine($"    fh层y={g.Key} life={g.Count()}"); }
                Console.WriteLine($"  life 无Fh/无效 = {mi.Lifes.Count - fhLayers.Count}");
            }
            // info 字段清单（有效地图判别字段寻找）
            if (mid is "221000000" or "000000000" or "993000000")
            {
                var infoNode = wz.FindNodeByPath($"{MapService.GetMapWzPath(mid)}/info");
                if (infoNode != null)
                {
                    var names = infoNode.Nodes.Select(n => n.Text).OrderBy(x => x).ToList();
                    string[] probes = { "town", "fieldLimit", "mobRate", "returnMap", "mapMark", "fly", "swim", "cloud", "noMapCmd", "hideMinimap" };
                    var bits = new List<string>();
                    foreach (var nm in probes) { object v = wz.GetIntProperty($"{MapService.GetMapWzPath(mid)}/info/{nm}"); bits.Add($"{nm}={(v is int i ? i.ToString() : "?")}"); }
                    Console.WriteLine($"  [{mid}] info 字段({names.Count}): {string.Join(",", names)} | {string.Join(" ", bits)}");
                }
                else { Console.WriteLine($"  [{mid}] info null"); }
            }
            // v8 三证据评分（foothold ≥3 根×1.5 / life ≥3 ×1.25；组表面 ±60 内）
            Console.WriteLine($"  v8 评分（各组：柱数 × foothold加分 × life加分）:");
            foreach (var g7 in groups7.OrderByDescending(g => g.Cols))
            {
                var fhSegs = mi.Footholds.Where(f => f.Y1 == f.Y2 && Math.Abs(f.X2 - f.X1) >= 270 && Math.Abs(f.Y1 - g7.Y) <= 60).ToList();
                int fhN = fhSegs.Count;
                long fhTotal = fhSegs.Sum(f => (long)Math.Abs(f.X2 - f.X1));
                int lifeN = mi.Lifes.Count(l =>
                {
                    int feet = l.Cy != 0 ? l.Cy : l.Y;
                    return Math.Abs(feet - g7.Y) <= 60;
                });
                double sc = g7.Cols * (1 + 0.5 * Math.Min(1.0, fhTotal / 810.0)) * (1 + 0.25 * Math.Min(1.0, lifeN / 3.0));
                Console.WriteLine($"    组y={g7.Y} 柱={g7.Cols} foothold段={fhN}(总长={fhTotal}, {string.Join("+", fhSegs.Select(f => Math.Abs(f.X2 - f.X1)))}) life={lifeN} v9score={sc:F2}");
            }
            var winner8 = groups7.OrderByDescending(g =>
                g.Cols * (mi.Footholds.Count(f => f.Y1 == f.Y2 && Math.Abs(f.X2 - f.X1) >= 270 && Math.Abs(f.Y1 - g.Y) <= 60) >= 3 ? 1.5 : 1.0) *
                (mi.Lifes.Count(l => { int feet = l.Cy != 0 ? l.Cy : l.Y; return Math.Abs(feet - g.Y) <= 60; }) >= 3 ? 1.25 : 1.0)).First();
            Console.WriteLine($"  v8 主干组 = {winner8.Y} score最高（柱数最多组={groups7.OrderByDescending(g => g.Cols).First().Y}）");

            // 现状选层（RoadMaterialExtractor 现行 WZ 层评分）
            try
            {
                var state = new RoadState();
                RoadMaterialExtractor.Configure(new MapService(wz, new CacheManager()), wz);
                var pack = RoadMaterialExtractor.Extract(mid, CancellationToken.None, state);
                if (pack != null)
                {
                    Console.WriteLine($"现状(Extract): 平地柱={pack.FlatColumns.Count} 坡={pack.SlantColumns.Count} 台阶={pack.Steps.Count} mainSurf={pack.MainSurfaceH} 连续率={pack.ContinuityRatio:F2} usable={pack.Usable}");
                }
                else { Console.WriteLine($"现状(Extract): null — {state.LastError}"); }
            }
            catch (Exception ex) { Console.WriteLine($"现状(Extract) 异常: {ex.Message}"); }

            // obj 真实底边 vs 锚点（取样 12 个）
            Console.WriteLine($"obj 样本（锚点 o.Y vs 真实底边 o.Y−oy+H vs 层 y）:");
            int shown = 0;
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var o in lay.Objs)
                {
                    if (shown >= 12) { break; }
                    var url = o.Resource != null && o.Resource.Frames.Count > 0 ? o.Resource.Frames[0].ResourceUrl ?? "" : "";
                    if (url == "") { continue; }
                    try
                    {
                        var (ox, oy) = wz.GetOrigin(url);
                        var png = wz.ExtractPng(url);
                        if (png == null || png.Length <= 4) { continue; }
                        using var bmp = SKBitmap.Decode(png);
                        if (bmp == null) { continue; }
                        int bot = o.Y - oy + bmp.Height;
                        int visTop = o.Y - oy;
                        string probe = url.Split('/').Skip(2).FirstOrDefault() ?? "";
                        Console.WriteLine($"  o={o.X},{o.Y} oy={oy} H={bmp.Height} 可见顶={visTop} 底边={bot} 锚底差={(bot == o.Y ? 0 : bot - o.Y)}");
                        shown++;
                    }
                    catch { }
                }
                if (shown >= 12) { break; }
            }
        }
    }
}
