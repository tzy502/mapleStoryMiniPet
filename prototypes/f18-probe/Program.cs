using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

/// <summary>
/// F18 探针（常驻版，放仓库内避免 /tmp 被清）：
///   --full &lt;mapId...&gt;   渲染指定地图整图 → out/full_{mapId}.png（与模式1/2 同一 RenderViewport）
///   --bgm  [topN]         全量反查 BGM → 地图（info/bgm + info/town）→ out/bgm_map_index.csv
/// </summary>
static class F18Probe
{
    const string DataDir = "/Volumes/SSD/mxd/mxd/Data";
    static WzService Wz;
    static string OutDir;

    static void Main(string[] args)
    {
        OutDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "out"));
        Directory.CreateDirectory(OutDir);
        Wz = new WzService();
        Wz.LoadWz(null, DataDir);

        if (args.Length == 0)
        {
            Console.WriteLine("用法: --full <mapId...> | --bgm [topN]");
            return;
        }
        if (args[0] == "--full") { Full(args.Skip(1).ToArray()); return; }
        if (args[0] == "--bgm") { Bgm(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 15); return; }
        if (args[0] == "--bands") { BandsDiag.Run(Wz, OutDir, args.Skip(1).ToArray()); return; }
        if (args[0] == "--maps") { Maps(args.Skip(1).ToArray()); return; }
        if (args[0] == "--tree") { Tree(args.Length > 1 ? args[1] : "Map/Obj", args.Length > 2 ? int.Parse(args[2]) : 2); return; }
        if (args[0] == "--find") { Find(args.Length > 1 ? args[1] : "Map/Obj", args.Length > 2 ? args[2] : "arrow"); return; }
        if (args[0] == "--objlist") { ObjList(args.Skip(1).ToArray()); return; }
        if (args[0] == "--int")
        {
            var v = Wz.FindNodeByPath(args[1])?.GetValue<int>() ?? -999;
            Console.WriteLine($"{args[1]} = {v}");
            return;
        }
        if (args[0] == "--doll") { DollDiag.Run(Wz, OutDir); return; }
        if (args[0] == "--bgmdiag") { BgmDiag.Run(Wz, args.Length > 1 ? args[1] : "Bgm10/Timeless"); return; }
        if (args[0] == "--objraw")
        {
            string mid = args.Length > 1 ? args[1] : "100000200";
            double x0 = args.Length > 2 && double.TryParse(args[2], out var v0) ? v0 : 950;
            double x1 = args.Length > 3 && double.TryParse(args[3], out var v1) ? v1 : 1700;
            var svc = new MapService(Wz, new CacheManager());
            var mi = svc.LoadMap(mid);
            if (mi == null) { Console.WriteLine("加载失败"); return; }
            Console.WriteLine($"[objraw] {mid} 层0 obj 总数={mi.Layers[0].Objs.Count}");
            foreach (var o in mi.Layers[0].Objs.Where(o => o.X >= x0 && o.X <= x1).OrderBy(o => o.Z).ThenBy(o => o.Id))
            {
                Console.WriteLine($"  id={o.Id,3} z={o.Z,3} ({o.X,5},{o.Y,5}) {o.Resource.Frames[0].ResourceUrl}");
            }
            return;
        }
        if (args[0] == "--ropes")
        {
            string mid = args.Length > 1 ? args[1] : "101000000";
            var svc = new MapService(Wz, new CacheManager());
            var mi = svc.LoadMap(mid);
            var node = Wz.FindNodeByPath($"Map/Map/Map{mid[0]}/{mid}.img/ladderRope");
            Console.WriteLine($"[ropes] MapInfo.Ropes={mi?.Ropes.Count}  FindNodeByPath(ladderRope)={(node == null ? "null" : node.Nodes.Count + " 子节点")}");
            return;
        }
        if (args[0] == "--chains") { ChainDiag.Run(Wz, args.Length > 1 ? args[1] : "101000000"); return; }
        if (args[0] == "--walkmode")
        {
            WalkModeProbe.Run(Wz, args.Length > 1 ? args[1] : "Bgm00/FloralLife",
                              args.Length > 2 && int.TryParse(args[2], out var sec) ? sec : 20,
                              args.Length > 3 ? args[3] : "101000000");
            return;
        }
        if (args[0] == "--walk")
        {
            WalkDiag.Run(Wz, OutDir, args.Length > 1 ? args[1] : "101000000",
                         args.Length > 2 && args[2] == "rev");
            return;
        }
        if (args[0] == "--png")
        {
            var bytes = Wz.ExtractPng(args[1]);
            if (bytes == null) { Console.WriteLine($"  {args[1]} 提取失败"); return; }
            string nm = args.Length > 2 ? args[2] : "png_out.png";
            File.WriteAllBytes(Path.Combine(OutDir, nm), bytes);
            Console.WriteLine($"  → {nm} ({bytes.Length} bytes)");
            return;
        }
        Console.WriteLine($"未知参数 {args[0]}");
    }

    // ---------- 整图导出 ----------
    static void Full(string[] mapIds)
    {
        if (mapIds.Length == 0) { mapIds = new[] { "100000000" }; }
        foreach (var id in mapIds)
        {
            var svc = new MapService(Wz, new CacheManager());
            var mi = svc.LoadMap(id);
            if (mi == null) { Console.WriteLine($"  {id} 加载失败"); continue; }
            int w = mi.MaxX - mi.MinX, h = mi.MaxY - mi.MinY;
            var (cx, cy) = MapService.GetMapCenter(mi);
            using var bmp = svc.RenderViewport(mi, cx, cy, 1f, 0, w, h);
            if (bmp == null) { Console.WriteLine($"  {id} 渲染失败"); continue; }
            string f = Path.Combine(OutDir, $"full_{id}.png");
            SavePng(bmp, f);
            Console.WriteLine($"  → full_{id}.png ({bmp.Width}x{bmp.Height}) world=[{mi.MinX},{mi.MinY}]~[{mi.MaxX},{mi.MaxY}]");
        }
    }

    // ---------- BGM 反查 ----------
    static void Bgm(int topN)
    {
        var mapRoot = Wz.FindNodeByPath("Map/Map");
        if (mapRoot == null) { Console.WriteLine("[bgm] 找不到 Map/Map"); return; }
        var ids = new List<string>();
        foreach (var sub in mapRoot.Nodes)
        {
            foreach (var img in sub.Nodes)
            {
                if (img.Text.EndsWith(".img")) { ids.Add(img.Text.Substring(0, img.Text.Length - 4)); }
            }
        }
        ids.Sort();
        Console.WriteLine($"[bgm] 地图 img 总数 = {ids.Count}");

        var csv = new List<string> { "mapId,bgm,img,track,town,mapName,tS,lowestFH,returnMap" };
        var byBgm = new Dictionary<string, List<string>>();
        var townOf = new Dictionary<string, int>();
        var retOf = new Dictionary<string, string>();
        int withBgm = 0, noBgm = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;
        foreach (var id in ids)
        {
            done++;
            if (done % 4000 == 0) { Console.WriteLine($"[bgm] {done}/{ids.Count} {sw.Elapsed.TotalSeconds:F0}s 有BGM={withBgm}"); }
            Wz_Node root = null;
            try
            {
                var img = Wz.FindNodeByPath($"Map/Map/Map{id[0]}/{id}.img")?.GetValue<Wz_Image>();
                img?.TryExtract();
                root = img?.Node;
            }
            catch { }
            if (root == null) { continue; }
            var info = root.Nodes.FirstOrDefault(n => n.Text == "info");
            string bgm = info?.Nodes.FirstOrDefault(n => n.Text == "bgm")?.GetValue<string>() ?? "";
            int town = 0;
            var townNode = info?.Nodes.FirstOrDefault(n => n.Text == "town");
            if (townNode != null) { int.TryParse(townNode.Value?.ToString() ?? "0", out town); }
            string mapName = info?.Nodes.FirstOrDefault(n => n.Text == "mapName")?.GetValue<string>() ?? "";
            string returnMap = "";
            var rmNode = info?.Nodes.FirstOrDefault(n => n.Text == "returnMap");
            if (rmNode != null)
            {
                try { returnMap = rmNode.GetValue<string>() ?? ""; } catch { returnMap = ""; }
                if (string.IsNullOrEmpty(returnMap))
                {
                    try { returnMap = rmNode.GetValue<int>().ToString(); } catch { returnMap = ""; }
                }
            }
            townOf[id] = town;
            retOf[id] = returnMap;
            // 地面 tS（第一个含 enH 砖的层的 tS）+ 最低水平 foothold
            string groundTs = "";
            for (int li = 0; li < 8 && groundTs == ""; li++)
            {
                var ln = root.Nodes.FirstOrDefault(n => n.Text == li.ToString());
                var tileN = ln?.Nodes.FirstOrDefault(n => n.Text == "tile");
                if (tileN == null) { continue; }
                string ts = ln.Nodes.FirstOrDefault(n => n.Text == "info")?.Nodes.FirstOrDefault(n => n.Text == "tS")?.GetValue<string>() ?? "";
                foreach (var t in tileN.Nodes)
                {
                    string u = t.Nodes.FirstOrDefault(n => n.Text == "u")?.GetValue<string>() ?? "";
                    if (u.StartsWith("enH")) { groundTs = ts; break; }
                }
            }
            int lowestFh = int.MinValue;
            var fhRoot = root.Nodes.FirstOrDefault(n => n.Text == "foothold");
            if (fhRoot != null)
            {
                foreach (var l0 in fhRoot.Nodes)
                {
                    foreach (var l1 in l0.Nodes)
                    {
                        foreach (var l2 in l1.Nodes)
                        {
                            int y1 = l2.Nodes.FirstOrDefault(n => n.Text == "y1")?.GetValue<int>() ?? 0;
                            int y2 = l2.Nodes.FirstOrDefault(n => n.Text == "y2")?.GetValue<int>() ?? 0;
                            if (y1 == y2 && y1 > lowestFh) { lowestFh = y1; }
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(bgm)) { noBgm++; csv.Add($"{id},,,,{town},{mapName},{groundTs},{lowestFh},{returnMap}"); continue; }
            withBgm++;
            if (!byBgm.TryGetValue(bgm, out var l)) { l = new List<string>(); byBgm[bgm] = l; }
            l.Add(id);
            csv.Add($"{id},{bgm},{bgm},,{town},{mapName},{groundTs},{lowestFh},{returnMap}");
        }

        File.WriteAllLines(Path.Combine(OutDir, "bgm_map_index.csv"), csv);
        var lines = new List<string>();
        void P(string s) { Console.WriteLine(s); lines.Add(s); }
        P($"===== BGM 反查（扫描 {ids.Count}，有 BGM {withBgm}，无 BGM {noBgm}）=====");
        P($"不同 BGM 曲目 = {byBgm.Count}");
        P($"地图数最多的 BGM Top{topN}（默认地图 = 该 BGM 所有相关地图的 returnMap 众数）：");
        foreach (var kv in byBgm.OrderByDescending(k => k.Value.Count).Take(topN))
        {
            int townCnt = kv.Value.Count(m => townOf.TryGetValue(m, out var t) && t == 1);
            var rmGroups = kv.Value.Where(m => retOf.TryGetValue(m, out var r) && !string.IsNullOrEmpty(r))
                                   .GroupBy(m => retOf[m]).OrderByDescending(g => g.Count()).ToList();
            string def = rmGroups.Count > 0 ? $"{rmGroups[0].Key}（{rmGroups[0].Count()}/{kv.Value.Count}）" : "(无 returnMap)";
            P($"   {kv.Key,-42} 地图 {kv.Value.Count,4} 张（town=1 {townCnt,3}）  默认地图 {def}");
        }
        P($"耗时 {sw.Elapsed.TotalSeconds:F0}s");
        File.WriteAllLines(Path.Combine(OutDir, "bgm_汇总.txt"), lines);
        Console.WriteLine($"  → {Path.Combine(OutDir, "bgm_map_index.csv")}");
        Console.WriteLine($"  → {Path.Combine(OutDir, "bgm_汇总.txt")}");
    }

    // ---------- 整张地图裁片（基准线 = 该图最低水平 foothold） ----------
    static void Maps(string[] ids)
    {
        foreach (var id in ids)
        {
            var svc = new MapService(Wz, new CacheManager());
            var mi = svc.LoadMap(id);
            if (mi == null) { Console.WriteLine($"  {id} 加载失败"); continue; }
            int lowFh = int.MinValue;
            foreach (var fh in mi.Footholds) { if (fh.Y1 == fh.Y2 && fh.Y1 > lowFh) { lowFh = fh.Y1; } }
            if (lowFh == int.MinValue) { Console.WriteLine($"  {id} 无水平 foothold"); continue; }
            // 参考 Java renderMapObj：只画 obj + tile；life 保留；portal / back 不渲染（back 留到视差阶段）
            svc.MapShowPortal = false;
            svc.MapShowLife = true;
            svc.MapShowBack = false;
            // 过滤「箭头」类 obj：guide.img/maple/board/1/* 是指向右的发光箭头（board/0 是告示牌，保留）
            int removed = 0;
            foreach (var layer in mi.Layers)
            {
                removed += layer.Objs.RemoveAll(o => (o.Resource?.Frames != null) &&
                    o.Resource.Frames.Any(f => IsArrow(f.ResourceUrl)));
            }
            if (removed > 0) { Console.WriteLine($"     （过滤箭头 obj {removed} 个）"); }
            int w = mi.MaxX - mi.MinX, h = mi.MaxY - mi.MinY;
            var (cx, cy) = MapService.GetMapCenter(mi);
            using var full = svc.RenderViewport(mi, cx, cy, 1f, 0, w, h);
            if (full == null) { Console.WriteLine($"  {id} 渲染失败"); continue; }
            int gy = lowFh - mi.MinY;          // 基准线（图内 y）
            int top = gy - 400;
            var crop = new SKBitmap(w, 560);
            using (var cv = new SKCanvas(crop))
            {
                cv.Clear(SKColors.Transparent);
                int sy0 = Math.Max(0, top), sy1 = Math.Min(full.Height, top + 560);
                int dy0 = sy0 - top;
                if (sy1 > sy0)
                {
                    cv.DrawBitmap(full, new SKRect(0, sy0, w, sy1), new SKRect(0, dy0, w, dy0 + (sy1 - sy0)));
                }
            }
            SavePng(crop, Path.Combine(OutDir, $"map_{id}.png"));
            Console.WriteLine($"  → map_{id}.png 整宽={w} 最低fh.y={lowFh}");
        }
    }

    static void Find(string root, string kw)
    {
        var n = Wz.FindNodeByPath(root);
        if (n == null) { Console.WriteLine($"{root} 不存在"); return; }
        int hits = 0;
        Walk(n, root);
        Console.WriteLine($"命中 {hits} 条（keyword={kw}）");
        void Walk(WzComparerR2.WzLib.Wz_Node node, string path)
        {
            foreach (var c in node.Nodes)
            {
                string p = path + "/" + c.Text;
                if (c.Nodes.Count == 0 && p.ToLower().Contains(kw.ToLower())) { Console.WriteLine("  " + p); hits++; }
                Walk(c, p);
            }
        }
    }

    /// <summary>箭头类 obj：signboard.img/**/arrow/* 与 guide.img/maple/board/1/*（指路箭头）。</summary>
    static bool IsArrow(string url)
    {
        if (string.IsNullOrEmpty(url)) { return false; }
        string u = url.ToLowerInvariant();
        return u.Contains("/arrow/") || u.Contains("guide.img/maple/board/1") || u.Contains("guide.img/milepost/");
    }

    static void ObjList(string[] ids)
    {
        foreach (var id in ids)
        {
            var svc = new MapService(Wz, new CacheManager());
            var mi = svc.LoadMap(id);
            if (mi == null) { Console.WriteLine($"  {id} 加载失败"); continue; }
            Console.WriteLine($"===== {id} =====");
            var agg = new Dictionary<string, (int cnt, int z, int w, int h)>();
            for (int li = 0; li < mi.Layers.Length; li++)
            {
                foreach (var o in mi.Layers[li].Objs)
                {
                    var f0 = o.Resource?.Frames?.FirstOrDefault();
                    if (f0 == null || string.IsNullOrEmpty(f0.ResourceUrl)) { continue; }
                    var sz = Wz.GetPngSize(f0.ResourceUrl);
                    if (!agg.TryGetValue(f0.ResourceUrl, out var v)) { v = (0, f0.Z, sz?.W ?? 0, sz?.H ?? 0); }
                    agg[f0.ResourceUrl] = (v.cnt + 1, f0.Z, v.w, v.h);
                }
            }
            foreach (var kv in agg.OrderByDescending(k => k.Value.cnt))
            {
                Console.WriteLine($"   x{kv.Value.cnt,-3} z={kv.Value.z,-3} {kv.Value.w}x{kv.Value.h,-4} {kv.Key}");
            }
        }
    }

    static void Tree(string path, int maxDepth)
    {
        var n = Wz.FindNodeByPath(path);
        if (n == null) { Console.WriteLine($"{path} 不存在"); return; }
        Console.WriteLine($"{path}:");
        Walk(n, 0);
        void Walk(WzComparerR2.WzLib.Wz_Node node, int depth)
        {
            if (depth >= maxDepth) { return; }
            foreach (var c in node.Nodes)
            {
                Console.WriteLine($"{new string(' ', depth * 2)}{c.Text}");
                Walk(c, depth + 1);
            }
        }
    }

    static void SavePng(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(path);
        fs.SetLength(0);
        data.SaveTo(fs);
    }
}
