using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MiniPet.Services;
using WzComparerR2.WzLib;

/// <summary>
/// 反向索引扫描基准（2026-09-15）：
///   --bgmscan [degree]  串行基线（复刻 ScanReverseCore 语义，NRE 已修）vs 并行（每任务独立 Wz_Structure 按组加载），
///                       双方各扫一遍，对比耗时 + 结果一致性 + FloralLife 选图。
/// </summary>
static class ScanDiag
{
    sealed class Ent { public string MapId; public int Town; public string Ret; }

    public static void Run(WzService wz, int degree)
    {
        // ---- 共享树：组 → img 名字（只拿名字，不解压）----
        var swNames = Stopwatch.StartNew();
        Wz_Node mapMap = wz.FindNodeByPath("Map/Map");
        if (mapMap == null) { Console.WriteLine("[scan] Map/Map 不存在"); return; }
        var groups = new Dictionary<int, List<string>>();
        for (int g = 0; g <= 9; g++)
        {
            var names = new List<string>();
            var gn = mapMap.FindNodeByPath("Map" + g);
            if (gn != null)
            {
                foreach (Wz_Node n in gn.Nodes)
                {
                    if (n.Text.EndsWith(".img", StringComparison.Ordinal)) { names.Add(n.Text.Substring(0, n.Text.Length - 4)); }
                }
            }
            groups[g] = names;
        }
        int total = groups.Values.Sum(x => x.Count);
        Console.WriteLine($"[scan] 名字枚举 {swNames.Elapsed.TotalSeconds:F1}s，img 总数 {total}，degree={degree}");

        // ---- 探一个 img：找它真实所属 Wz_File 路径（为并行 worker 定位分组文件夹）----
        string mapDir = FindMapDir(wz, groups);
        Console.WriteLine($"[scan] 并行 worker 用 Map 目录: {mapDir ?? "(未找到→并行跳过)"}");

        // ---- 串行基线（复刻 ScanReverseCore：共享树 + 名字查询）----
        var sw0 = Stopwatch.StartNew();
        var serial = ScanSerial(wz, groups);
        Console.WriteLine($"[scan][串行] 总耗时 {sw0.Elapsed.TotalSeconds:F1}s  曲目 {serial.Count}  条目 {serial.Values.Sum(x => x.Count)}");
        ReportFloral("串行", serial);

        GC.Collect();

        // ---- 并行（每任务独立 Wz_Structure 按组加载，零 WzLock 竞争，逐图 Unextract）----
        if (mapDir != null)
        {
            var sw1 = Stopwatch.StartNew();
            var par = ScanParallel(wz, groups, mapDir, degree);
            Console.WriteLine($"[scan][并行 x{degree}] 总耗时 {sw1.Elapsed.TotalSeconds:F1}s  曲目 {par.Count}  条目 {par.Values.Sum(x => x.Count)}");
            ReportFloral("并行", par);
            Compare(serial, par);
        }
    }

    static string FindMapDir(WzService wz, Dictionary<int, List<string>> groups)
    {
        var kv = groups.First(x => x.Value.Count > 0);
        string id = kv.Value[0];
        try
        {
            var imgNode = wz.FindNodeByPath($"Map/Map/Map{kv.Key}/{id}.img");
            var img = imgNode?.GetValue<Wz_Image>();
            if (img == null) { return null; }
            img.TryExtract();
            var f1 = img.WzFile;
            var f2 = img.Node != null ? img.Node.GetNodeWzFile() : null;
            string p1 = FilePathOf(f1), p2 = FilePathOf(f2);
            Console.WriteLine($"[scan] 样本 {kv.Key}/{id}: WzImage.WzFile={(p1 ?? "null")} GetNodeWzFile={(p2 ?? "null")}");
            // 样本 img 的物理 WzFile 形如 .../Map/Map/Map0/Map0_000.wz → 上两级 = 各分组 Map0..9 的父目录
            string dir = p1 != null ? Path.GetDirectoryName(p1) : null;
            if (dir == null && p2 != null) { dir = Path.GetDirectoryName(p2); }
            return dir != null ? Path.GetDirectoryName(dir) : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[scan] FindMapDir 失败: {ex.Message}");
            return null;
        }
    }

    static string FilePathOf(IMapleStoryFile f)
    {
        try { return ((f as Wz_File)?.FileStream as FileStream)?.Name; }
        catch { return null; }
    }

    static Dictionary<string, List<Ent>> ScanSerial(WzService wz, Dictionary<int, List<string>> groups)
    {
        var result = new Dictionary<string, List<Ent>>(StringComparer.Ordinal);
        for (int g = 0; g <= 9; g++)
        {
            var gsw = Stopwatch.StartNew();
            int hit = 0;
            foreach (var id in groups[g])
            {
                KeyValuePair<string, Ent>? found = null;
                try
                {
                    var node = wz.FindNodeByPath($"Map/Map/Map{g}/{id}.img");
                    var img = node?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) { continue; }
                    var info = img.Node.FindNodeByPath("info");
                    var bgm = MusicDecisions.NormalizeBgm(info?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                    if (bgm != null)
                    {
                        int town = info?.FindNodeByPath("town")?.GetValueEx<int>(0) ?? 0;
                        string ret = info?.FindNodeByPath("returnMap")?.GetValueEx<string>(null) ?? "";
                        found = new KeyValuePair<string, Ent>(
                            bgm.Img + "/" + bgm.Track,
                            new Ent { MapId = id, Town = town, Ret = ret });
                        hit++;
                    }
                }
                catch { }
                if (found.HasValue)
                {
                    if (!result.TryGetValue(found.Value.Key, out var list))
                    {
                        list = new List<Ent>();
                        result[found.Value.Key] = list;
                    }
                    list.Add(found.Value.Value);
                }
            }
            Console.WriteLine($"[scan][串行] Map{g}: {groups[g].Count} imgs hit={hit} {gsw.Elapsed.TotalSeconds:F1}s");
        }
        return result;
    }

    static Dictionary<string, List<Ent>> ScanParallel(WzService wz, Dictionary<int, List<string>> groups, string mapDir, int degree)
    {
        var result = new ConcurrentDictionary<string, List<Ent>>(StringComparer.Ordinal);
        int firstKey = groups.First(x => x.Value.Count > 0).Key;
        Parallel.ForEach(
            groups.Where(x => x.Value.Count > 0),
            new ParallelOptions { MaxDegreeOfParallelism = degree },
            kv =>
            {
                var gsw = Stopwatch.StartNew();
                try
                {
                    Wz_Structure.DefaultAutoDetectExtFiles = true;
                    Wz_Structure.DefaultImgCheckDisabled = true;
                    var wzs = new Wz_Structure();
                    Wz_Node root = null;
                    wzs.LoadWzFolder(Path.Combine(mapDir, "Map" + kv.Key), ref root, false);
                    if (root == null) { Console.WriteLine($"[scan][par] Map{kv.Key}: 根 null"); return; }
                    if (kv.Key == firstKey)
                    {
                        var kids = new List<string>();
                        foreach (Wz_Node c in root.Nodes) { kids.Add(c.Text); if (kids.Count >= 5) { break; } }
                        Console.WriteLine($"[scan][par] 组根 '{root.Text}' 子节点 {root.Nodes.Count} 首5: [{string.Join(",", kids)}]");
                    }
                    int hit = 0;
                    foreach (var id in kv.Value)
                    {
                        try
                        {
                            var node = root.FindNodeByPath(id + ".img");
                            var img = node?.GetValue<Wz_Image>();
                            if (img == null || !img.TryExtract()) { continue; }
                            var info = img.Node.FindNodeByPath("info");
                            var bgm = MusicDecisions.NormalizeBgm(info?.FindNodeByPath("bgm")?.GetValueEx<string>(null));
                            if (bgm != null)
                            {
                                int town = info?.FindNodeByPath("town")?.GetValueEx<int>(0) ?? 0;
                                string ret = info?.FindNodeByPath("returnMap")?.GetValueEx<string>(null) ?? "";
                                var key = bgm.Img + "/" + bgm.Track;
                                var list = result.GetOrAdd(key, _ => new List<Ent>());
                                lock (list) { list.Add(new Ent { MapId = id, Town = town, Ret = ret }); }
                                hit++;
                            }
                            img.Unextract();
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[scan][par] {kv.Key}/{id}: {ex.Message}"); }
                    }
                    Console.WriteLine($"[scan][par] Map{kv.Key}: {kv.Value.Count} imgs hit={hit} {gsw.Elapsed.TotalSeconds:F1}s");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[scan][par] Map{kv.Key} 组失败: {ex.Message}");
                }
            });
        return result.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    static void ReportFloral(string tag, Dictionary<string, List<Ent>> idx)
    {
        if (idx.TryGetValue("Bgm00.img/FloralLife", out var l))
        {
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in l)
            {
                if (!string.IsNullOrEmpty(e.Ret)) { votes[e.Ret] = votes.TryGetValue(e.Ret, out var c) ? c + 1 : 1; }
            }
            Console.WriteLine($"[scan][{tag}] Bgm00/FloralLife → {l.Count} 张图  returnMap 众数第一图 = {MusicDecisions.FirstMapByReturnMap(votes) ?? "(null)"}");
        }
        else
        {
            Console.WriteLine($"[scan][{tag}] Bgm00/FloralLife 无记录!");
        }
    }

    static void Compare(Dictionary<string, List<Ent>> a, Dictionary<string, List<Ent>> b)
    {
        if (a.Count == 0) { Console.WriteLine("[scan][对比] 串行结果为空，跳过"); return; }
        int diffTracks = 0, diffEntries = 0;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var lb)) { diffTracks++; continue; }
            var sa = kv.Value.Select(e => e.MapId).OrderBy(x => x, StringComparer.Ordinal);
            var sb = lb.Select(e => e.MapId).OrderBy(x => x, StringComparer.Ordinal);
            if (!sa.SequenceEqual(sb)) { diffEntries++; }
        }
        Console.WriteLine($"[scan][对比] 曲目差 {diffTracks}  组内条目差 {diffEntries}（0/0 = 完全一致）");
    }
}
