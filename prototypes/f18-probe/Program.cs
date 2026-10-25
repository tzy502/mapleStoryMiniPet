using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Walk;
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
            Console.WriteLine("用法: --full <mapId...> | --bgm [topN] | --auditall [limit] [degree]");
            return;
        }
        if (args[0] == "--full") { Full(args.Skip(1).ToArray()); return; }
        if (args[0] == "--fdoverlay") { FdOverlay(args.Length > 1 ? args[1] : "706020100"); return; }
        if (args[0] == "--portals")
        {
            // --portals <mapId> [trackKey]：列出全部门 + 目标图 BGM；给 trackKey 时标注哪个门会被 ChooseNextPortal 选中
            var cache = new CacheManager();
            var mapService = new MapService(Wz, cache);
            var map = mapService.LoadMap(args[1]);
            string? track = args.Length > 2 ? args[2] : null;
            foreach (var p in map!.Portals)
            {
                string padded = p.Tm.Length == 7 && p.HasTarget ? p.Tm : p.Tm.PadLeft(9, '0');
                string bgm = p.HasTarget
                    ? Wz.GetStringProperty($"{MapService.GetMapWzPath(p.Tm)}/info/bgm") ?? ""
                    : "";
                string? key = MiniPet.Services.Walk.WalkPathPlanner.TrackKeyOf(bgm);
                bool hit = track != null && string.Equals(key, track, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"[portal] id={p.Id} ({p.X},{p.Y}) {p.PName} → {p.Tm}{(p.HasTarget ? "" : " (无目标)")} key={key ?? "?"}{(hit ? "   ← BGM 匹配" : "")}");
            }
            return;
        }
        if (args[0] == "--bgm") { Bgm(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 15); return; }
        if (args[0] == "--auditall")
        {
            // --auditall [limit] [degree]：全部 BGM 候选图批量静态审计 → /tmp/audit-all.csv
            int limit = args.Length > 1 && int.TryParse(args[1], out var lim) ? lim : 0;
            int degree = args.Length > 2 && int.TryParse(args[2], out var deg) ? deg : 4;
            AuditAll.Run(Wz, limit, degree);
            return;
        }
        if (args[0] == "--bands") { BandsDiag.Run(Wz, OutDir, args.Skip(1).ToArray()); return; }
        if (args[0] == "--maps") { Maps(args.Skip(1).ToArray()); return; }
        if (args[0] == "--tree") { Tree(args.Length > 1 ? args[1] : "Map/Obj", args.Length > 2 ? int.Parse(args[2]) : 2); return; }
        if (args[0] == "--face") { Face(args.Length > 1 ? args[1] : "Character/Face/00020000.img"); return; }
        if (args[0] == "--faceacc") { FaceAcc(args.Length > 1 ? args[1] : null); return; }
        if (args[0] == "--find") { Find(args.Length > 1 ? args[1] : "Map/Obj", args.Length > 2 ? args[2] : "arrow"); return; }
        if (args[0] == "--objlist") { ObjList(args.Skip(1).ToArray()); return; }
        if (args[0] == "--int")
        {
            var v = Wz.FindNodeByPath(args[1])?.GetValue<int>() ?? -999;
            Console.WriteLine($"{args[1]} = {v}");
            return;
        }
        if (args[0] == "--route")
        {
            // --route <from> <to>：BFS 门图（portal.Tm 有效且非 999999999/自环），输出图序列路线
            var cache = new CacheManager();
            var mapService = new MapService(Wz, cache);
            string from = args[1], to = args[2];
            var prev = new Dictionary<string, string?> { [from] = null };
            var queue = new Queue<string>(new[] { from });
            int visited = 0;
            while (queue.Count > 0 && !prev.ContainsKey(to))
            {
                var cur = queue.Dequeue();
                visited++;
                if (visited > 3000) { break; }
                var mi = mapService.LoadMap(cur);
                if (mi == null) { continue; }
                foreach (var p in mi.Portals)
                {
                    if (!p.HasTarget || string.Equals(p.Tm, cur, StringComparison.Ordinal) || p.Tm == "999999999") { continue; }
                    if (prev.ContainsKey(p.Tm)) { continue; }
                    prev[p.Tm] = cur;
                    if (p.Tm == to) { break; }
                    queue.Enqueue(p.Tm);
                }
            }
            if (!prev.ContainsKey(to))
            {
                Console.WriteLine($"[route] {from} → {to} 不可达（扫描 {visited} 图）");
                return;
            }
            var path = new List<string>();
            for (var m = to; m != null; m = prev.ContainsKey(m) ? prev[m] : null) { path.Insert(0, m); }
            Console.WriteLine($"[route] {from} → {to}：{string.Join(" → ", path)}（扫描 {visited} 图）");
            return;
        }
        if (args[0] == "--thumb")
        {
            // --thumb <mapId>：复刻设置页 BgRenderPreview 的渲染路径（LoadMap + CompositeMapThumbnail）
            var cache = new CacheManager();
            var mapService = new MapService(Wz, cache);
            try
            {
                var map = mapService.LoadMap(args[1]);
                Console.WriteLine($"[thumb] LoadMap: {(map != null ? $"OK 段{map.Footholds.Count} back={map?.Backs?.Count ?? 0}" : "null")}");
                var png = map != null ? mapService.CompositeMapThumbnail(map, 800, 600) : null;
                Console.WriteLine($"[thumb] CompositeMapThumbnail: {(png != null ? $"OK {png.Length}B" : "null ← 渲染失败复现")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[thumb] 异常: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            return;
        }
        if (args[0] == "--findmap")
        {
            // --findmap <关键词>：按名称找地图 id（经 MapCatalogService）
            var cache = new CacheManager();
            var catalog = new MapCatalogService(Wz, cache);
            var found = catalog.FindMapsByName(args[1]);
            foreach (var (id, name) in found.Take(10))
            {
                Console.WriteLine($"[findmap] {id} : {name}");
            }
            Console.WriteLine($"[findmap] 共 {found.Count} 张");
            return;
        }
        if (args[0] == "--imgprobe")
        {
            // --imgprobe <mapId>：按不同补零/分片组合探测 img 节点（GetExtractedImgNode 同口径）
            var cache = new CacheManager();
            var mapSvc = new MapService(Wz, cache);
            var mapId = args[1];
            var candidates = new[]
            {
                $"Map/Map/Map{mapId[0]}/{mapId}.img",
                $"Map/Map/Map{mapId[0]}/{mapId.PadLeft(9, '0')}.img",
                $"Map/Map/Map{mapId.PadLeft(9, '0')[0]}/{mapId.PadLeft(9, '0')}.img",
            };
            foreach (var path in candidates.Distinct())
            {
                var node = Wz.FindNodeByPath(path);
                var img = node?.GetValue<Wz_Image>();
                bool ok = img != null && img.TryExtract();
                Console.WriteLine($"[imgprobe] {path} → 节点={node != null} 解压={ok}");
            }
            return;
        }
        if (args[0] == "--navel")
        {
            // --navel <bodyImg> <action>：读各帧 map.navel（WzService 同口径）
            var wz2 = Wz;
            var action = args[2];
            for (int fr = 0; fr < 5; fr++)
            {
                var nx = wz2.FindNodeByPath($"Character/{args[1]}/{action}/{fr}/map/navel")?.GetValueEx<WzComparerR2.WzLib.Wz_Vector>(null);
                if (nx == null) { Console.WriteLine($"frame {fr}: (无)"); continue; }
                Console.WriteLine($"frame {fr}: navel=({nx.X},{nx.Y})");
            }
            return;
        }
        if (args[0] == "--reach")
        {
            var cache = new CacheManager();
            var mapService = new MapService(Wz, cache);
            var map = mapService.LoadMap(args[1]);
            int sfh = args.Length > 2 ? int.Parse(args[2]) : 1;
            var reach = WalkGraph.Reachable(map, sfh);
            Console.WriteLine($"[reach] {args[1]} 从 fh#{sfh} 可达 {reach.Count} 段");
            foreach (var f in map!.Footholds.OrderBy(f => f.X1))
            {
                Console.WriteLine($"[reach] {(reach.Contains(f.Id) ? "IN " : "OUT")} fh#{f.Id} ({f.X1},{f.Y1})-({f.X2},{f.Y2})");
            }
            return;
        }
        if (args[0] == "--str")
        {
            var sv = Wz.GetStringProperty(args[1]) ?? "(null)";
            Console.WriteLine($"{args[1]} = {sv}");
            return;
        }
        if (args[0] == "--fhs")
        {
            // --fhs <mapId> <yMin> <yMax>：列出 y 在范围内的 foothold 段
            var cache = new CacheManager();
            var mapService = new MapService(Wz, cache);
            var map = mapService.LoadMap(args[1]);
            int yMin = args.Length > 2 ? int.Parse(args[2]) : -100000;
            int yMax = args.Length > 3 ? int.Parse(args[3]) : 100000;
            foreach (var f in map!.Footholds.OrderBy(f => f.Y1).ThenBy(f => f.X1))
            {
                int yMid = (f.Y1 + f.Y2) / 2;
                if (yMid < yMin || yMid > yMax) { continue; }
                Console.WriteLine($"[fhs] id={f.Id} ({f.X1},{f.Y1})-({f.X2},{f.Y2}) prev={f.Prev} next={f.Next}");
            }
            return;
        }
        if (args[0] == "--doll") { DollDiag.Run(Wz, OutDir); return; }
        if (args[0] == "--actions")
        {
            var cache = new CacheManager();
            var sprite = new SpriteService(cache);
            var pd = new PaperdollService(Wz, cache, sprite);
            pd.SetAppearance(new CharacterAppearance { Gender = 0, BodyId = 2000, Hair = new ItemInfo { Id = "30000" }, Face = new ItemInfo { Id = "20000" } });
            var acts = pd.GetAvailableActions();
            Console.WriteLine($"[actions] body 2000 可用动作({acts.Count}): {string.Join(", ", acts)}");
            foreach (var a in new[] { "stand1", "stand2", "walk1", "walk2", "jump", "ladder", "rope" })
            {
                Console.WriteLine($"[actions] HasAction({a}) = {pd.HasAction(a)}  frames={pd.GetFrameCount(a)}");
            }
            Console.WriteLine($"[actions] ResolveAction(walk2) → {pd.ResolveAction("walk2")}");
            Console.WriteLine($"[actions] ResolveAction(jump)  → {pd.ResolveAction("jump")}");
            return;
        }
        if (args[0] == "--mobactions")
        {
            string aid = args.Length > 1 ? args[1] : "9834755";
            string aty = args.Length > 2 ? args[2] : "Mob";
            var list = Wz.GetActionList(aid, aty);
            Console.WriteLine($"[actions] {aty}/{aid} → {list.Count} 个: {string.Join(", ", list)}");
            var cached = Wz.GetActionList(aid);
            Console.WriteLine($"[actions] 默认重载 → {cached.Count} 个");
            return;
        }
        if (args[0] == "--balloon")
        {
            string bid = args.Length > 1 ? args[1] : "520";
            var cache = new CacheManager();
            var tiles = Wz.ExtractBalloonTiles(bid);
            int clr = Wz.GetBalloonClr(bid);
            Console.WriteLine($"[balloon] id={bid} tiles={(tiles == null ? "null" : $"ok clr={clr}")}");
            if (tiles != null)
            {
                Console.WriteLine($"[balloon] NW={tiles.NW.Length} N={tiles.N.Length} NE={tiles.NE.Length} W={tiles.W.Length} C={tiles.C.Length} E={tiles.E.Length} SW={tiles.SW.Length} S={tiles.S.Length} SE={tiles.SE.Length} Arrow={tiles.Arrow.Length} Head={tiles.Head.Length}");
            }
            if (tiles == null) { Console.WriteLine("[balloon] → 无素材（ExtractBalloonTiles 返回 null）"); return; }
            var svc = new BalloonService(cache, Wz);
            string btext = args.Length > 2 ? args[2] : "你好！我是 MiniPet 桌宠";
            var png = svc.RenderBalloon(btext, new MiniPet.Models.BalloonData { Id = bid, Tiles = tiles, Clr = clr });
            Console.WriteLine($"[balloon] RenderBalloon → {(png == null ? "null" : png.Length + " bytes")}");
            if (png != null && png.Length > 0)
            {
                var f = System.IO.Path.Combine(OutDir, $"balloon_{bid}.png");
                System.IO.File.WriteAllBytes(f, png);
                // 另存一张合成到白底的（参考实现的预览背景是浅色，透明中央看起来才是"浅色底"）
                using var bmp = SkiaSharp.SKBitmap.Decode(png);
                if (bmp != null)
                {
                    using var onWhite = new SkiaSharp.SKBitmap(bmp.Width, bmp.Height);
                    using (var cv = new SkiaSharp.SKCanvas(onWhite))
                    {
                        cv.Clear(SkiaSharp.SKColors.White);
                        cv.DrawBitmap(bmp, 0, 0);
                    }
                    using var img2 = SkiaSharp.SKImage.FromBitmap(onWhite);
                    using var d2 = img2.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(OutDir, $"balloon_{bid}_white.png"), d2.ToArray());
                }
                Console.WriteLine($"  → balloon_{bid}.png (+_white)");
            }
            return;
        }
        if (args[0] == "--chairdiag") { ChairDiag.Run(Wz, OutDir, args.Length > 1 ? args[1] : "3015632"); return; }
        if (args[0] == "--chairrender") { ChairRenderDiag.Run(Wz, OutDir, args.Length > 1 ? args[1] : "3015632"); return; }
        if (args[0] == "--chairscan") { ChairScanDiag.Run(Wz, OutDir); return; }
        if (args[0] == "--namediag") { NameDiag.Run(Wz, OutDir); return; }
        if (args[0] == "--chairanim")
        {
            ChairAnimDiag.Run(Wz, OutDir,
                args.Length > 1 ? args[1] : "3019632",
                args.Length > 2 ? args[2] : "stand1",
                args.Length > 3 && int.TryParse(args[3], out var cf) ? cf : 0);
            return;
        }
        if (args[0] == "--expr") { ExprDiag.Run(Wz, OutDir); return; }
        if (args[0] == "--bgmdiag") { BgmDiag.Run(Wz, args.Length > 1 ? args[1] : "Bgm10/Timeless"); return; }
        if (args[0] == "--bgmscan")
        {
            int degree = args.Length > 1 && int.TryParse(args[1], out var d) ? d : Math.Min(4, Math.Max(1, Environment.ProcessorCount - 2));
            ScanDiag.Run(Wz, degree);
            return;
        }
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
            // 可选尾部 --speed N：仿真倍速（每 tick dt=N/30 仿真秒，Tick 内部仍按 1/60 拆子步）
            double speed = 1.0;
            for (int a = 1; a < args.Length - 1; a++)
            {
                if (args[a] == "--speed" && double.TryParse(args[a + 1], out var sp)) { speed = sp; }
            }
            WalkModeProbe.Run(Wz, args.Length > 1 ? args[1] : "Bgm00/FloralLife",
                              args.Length > 2 && int.TryParse(args[2], out var sec) ? sec : 20,
                              args.Length > 3 ? args[3] : "101000000", speed);
            return;
        }
        if (args[0] == "--walkmode-batch")
        {
            // --walkmode-batch <listFile> <outDir> [seconds] [speed]
            var listFile = args.Length > 1 ? args[1] : "";
            var outDir = args.Length > 2 ? args[2] : "out/walkbatch";
            var seconds = args.Length > 3 && int.TryParse(args[3], out var s2) ? s2 : 90;
            var speed = args.Length > 4 && double.TryParse(args[4], out var sp2) ? sp2 : 10.0;
            if (listFile.Length == 0) { Console.WriteLine("用法: --walkmode-batch <listFile> <outDir> [seconds] [speed]"); return; }
            WalkModeProbe.BatchRun(Wz, listFile, outDir, seconds, speed);
            return;
        }
        if (args[0] == "--cover-batch")
        {
            // --cover-batch <listFile> <outCsv> [seconds] [speed] [label]：每图路径覆盖率（见 CoverBatch.cs）
            var listFile = args.Length > 1 ? args[1] : "";
            var outCsv = args.Length > 2 ? args[2] : "out/cover.csv";
            var seconds = args.Length > 3 && int.TryParse(args[3], out var s4) ? s4 : 90;
            var speed = args.Length > 4 && double.TryParse(args[4], out var sp4) ? sp4 : 10.0;
            var label = args.Length > 5 ? args[5] : "";
            if (listFile.Length == 0) { Console.WriteLine("用法: --cover-batch <listFile> <outCsv> [seconds] [speed] [label]"); return; }
            CoverBatch.Run(Wz, listFile, outCsv, seconds, speed, label);
            return;
        }
        if (args[0] == "--listmaps")
        {
            // --listmaps <outFile>：全量地图 id（Map/Map/*/*.img，排序后每行一个），覆盖率批量清单用
            var outFile = args.Length > 1 ? args[1] : Path.Combine(OutDir, "mapids.txt");
            var mapRoot = Wz.FindNodeByPath("Map/Map");
            if (mapRoot == null) { Console.WriteLine("[listmaps] 找不到 Map/Map"); return; }
            var ids = new List<string>();
            foreach (var sub in mapRoot.Nodes)
            {
                foreach (var img in sub.Nodes)
                {
                    if (img.Text.EndsWith(".img")) { ids.Add(img.Text.Substring(0, img.Text.Length - 4)); }
                }
            }
            ids.Sort(StringComparer.Ordinal);
            File.WriteAllLines(outFile, ids);
            Console.WriteLine($"[listmaps] {ids.Count} 张图 → {outFile}");
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

    /// <summary>--fdoverlay &lt;mapId&gt;：整图渲染 + 红线画出全部 foothold + 黄点画出传送门，验证「人物吸附的 fd 是否在视觉地面上」。</summary>
    static void FdOverlay(string mapId)
    {
        var svc = new MapService(Wz, new CacheManager());
        var mi = svc.LoadMap(mapId);
        if (mi == null) { Console.WriteLine($"  {mapId} 加载失败"); return; }
        int w = mi.MaxX - mi.MinX, h = mi.MaxY - mi.MinY;
        var (cx, cy) = MapService.GetMapCenter(mi);
        using var bmp = svc.RenderViewport(mi, cx, cy, 1f, 0, w, h);
        if (bmp == null) { Console.WriteLine("  渲染失败"); return; }
        // RenderViewport 的视口中心 = (cx,cy) → 世界 → 像素：px = wx - (cx - w/2), py = wy - (cy - h/2)
        float ox = cx - w / 2f, oy = cy - h / 2f;
        using var cv = new SKCanvas(bmp);
        using var line = new SKPaint { Color = SKColors.Red, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var dot = new SKPaint { Color = SKColors.Yellow, IsAntialias = true };
        foreach (var f in mi.Footholds)
        {
            cv.DrawLine(f.X1 - ox, f.Y1 - oy, f.X2 - ox, f.Y2 - oy, line);
        }
        foreach (var p in mi.Portals)
        {
            cv.DrawCircle(p.X - ox, p.Y - oy, 5, dot);
        }
        string f0 = Path.Combine(OutDir, $"fdoverlay_{mapId}.png");
        SavePng(bmp, f0);
        Console.WriteLine($"  → fdoverlay_{mapId}.png ({bmp.Width}x{bmp.Height}) 原点=({ox:F0},{oy:F0})，fd {mi.Footholds.Count} 段 / 门 {mi.Portals.Count} 个");
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

    /// <summary>
    /// --face &lt;faceImgPath&gt;：表情功能调研诊断（一次性 dump 全部表情节点）：
    /// 每个表情 → 结构（数字帧/直接 piece）/帧数/每帧 delay/每帧部件名；末尾汇总表情清单。
    /// </summary>
    static void Face(string path)
    {
        var n = Wz.FindNodeByPath(path);
        if (n == null) { Console.WriteLine($"{path} 不存在"); return; }
        Console.WriteLine($"== {path} 表情结构 ==");
        var summary = new List<string>();
        foreach (var expr in n.Nodes)
        {
            if (expr.Text == "info") { continue; }
            int numericFrames = 0;
            foreach (var c in expr.Nodes) { if (int.TryParse(c.Text, out _)) numericFrames++; }
            if (numericFrames > 0)
            {
                var delays = new List<int>();
                var pieces = new List<string>();
                for (int f = 0; f < numericFrames; f++)
                {
                    var fn = expr.Nodes.First(c => c.Text == f.ToString());
                    int delay = fn.Nodes.Where(c => c.Text == "delay").Select(c => c.GetValue<int>()).FirstOrDefault();
                    delays.Add(delay);
                    pieces.Add(string.Join(",", fn.Nodes.Where(p => int.TryParse(p.Text, out _) == false && p.Text != "delay" && p.Text != "move").Select(p => p.Text)));
                }
                Console.WriteLine($"{expr.Text}: 数字帧 x{numericFrames}  delay=[{string.Join(",", delays)}]ms  pieces=[{string.Join(" | ", pieces)}]");
                summary.Add($"{expr.Text}({numericFrames}帧)");
            }
            else
            {
                var names = expr.Nodes.Where(p => p.Text != "delay" && p.Text != "move" && p.Text != "info").Select(p => p.Text).ToList();
                int delay = expr.Nodes.Where(c => c.Text == "delay").Select(c => c.GetValue<int>()).FirstOrDefault();
                Console.WriteLine($"{expr.Text}: 直接piece x{names.Count}  delay={delay}ms  pieces=[{string.Join(",", names)}]");
                summary.Add($"{expr.Text}(静态)");
            }
        }
        Console.WriteLine($"== 清单（{summary.Count} 个表情节点）==");
        Console.WriteLine(string.Join(", ", summary));
        var faceDir = Wz.FindNodeByPath("Character/Face");
        if (faceDir != null)
        {
            int faceImgs = faceDir.Nodes.Count(c => c.Text.EndsWith(".img") && c.Text.StartsWith("0002"));
            Console.WriteLine($"== Character/Face 脸型 img 总数: {faceImgs} ==");
        }
    }

    /// <summary>
    /// --faceacc [itemId]：101 面饰表情联动诊断。无参时扫描 Accessory 目录 0101xxxx 前缀的 img，
    /// 统计「表情结构 / 动作结构 / 其他」占比，并取首个表情结构样本 dump；有参时 dump 指定面饰。
    /// </summary>
    static void FaceAcc(string? itemId)
    {
        var accDir = Wz.FindNodeByPath("Character/Accessory");
        if (accDir == null) { Console.WriteLine("Character/Accessory 不存在"); return; }
        if (!string.IsNullOrEmpty(itemId))
        {
            DumpAcc($"Character/Accessory/{itemId.PadLeft(11, '0')}.img");
            return;
        }
        int exprStruct = 0, actionStruct = 0, other = 0;
        string? sample = null;
        var allExprNames = new HashSet<string>();
        int scanned = 0;
        foreach (var child in accDir.Nodes)
        {
            var name = child.Text.Replace(".img", "");
            if (name.Length != 8 || !name.StartsWith("0101") || !int.TryParse(name, out _)) { continue; }
            scanned++;
            var img = child.GetValue<WzComparerR2.WzLib.Wz_Image>();
            var inner = img?.Node;
            if (inner == null) { continue; }
            bool hasAction = false, hasExpr = false;
            var exprs = new List<string>();
            foreach (var top in inner.Nodes)
            {
                if (top.Text == "info" || top.Text == "default") { continue; }
                bool numeric = top.Nodes.Any(c => int.TryParse(c.Text, out _));
                if (new[] { "stand1", "stand2", "walk1", "walk2" }.Contains(top.Text)) { hasAction = true; }
                else if (numeric || top.Text == "blink" || top.Text == "smile" || top.Text == "angry") { hasExpr = true; exprs.Add(top.Text); }
            }
            foreach (var e in exprs) { allExprNames.Add(e); }
            if (hasExpr) { exprStruct++; sample ??= child.Text; }
            else if (hasAction) { actionStruct++; }
            else { other++; }
        }
        Console.WriteLine($"== 101 面饰扫描：{scanned} 个 img，表情结构 {exprStruct} / 动作结构 {actionStruct} / 其他 {other} ==");
        Console.WriteLine($"表情节点名并集: {string.Join(",", allExprNames.OrderBy(x => x))}");
        if (sample != null) { DumpAcc($"Character/Accessory/{sample}"); }
    }

    static void DumpAcc(string path)
    {
        var n = Wz.FindNodeByPath(path);
        if (n == null) { Console.WriteLine($"{path} 不存在"); return; }
        Console.WriteLine($"== {path} ==");
        foreach (var top in n.Nodes)
        {
            if (top.Text == "info")
            {
                var nm = top.Nodes.FirstOrDefault(c => c.Text == "name")?.GetValueEx<string>(null);
                Console.WriteLine($"info: name={nm}");
                continue;
            }
            bool numeric = top.Nodes.Any(c => int.TryParse(c.Text, out _));
            if (numeric)
            {
                var frames = top.Nodes.Where(c => int.TryParse(c.Text, out _)).Count();
                var frame0 = top.Nodes.First(c => int.TryParse(c.Text, out _));
                var pieces = string.Join(",", frame0.Nodes.Where(p => p.Text != "delay" && p.Text != "move").Select(p => p.Text));
                int delay = frame0.Nodes.Where(c => c.Text == "delay").Select(c => c.GetValue<int>()).FirstOrDefault();
                Console.WriteLine($"{top.Text}: 数字帧 x{frames} delay(帧0)={delay}ms pieces(帧0)=[{pieces}]");
            }
            else
            {
                var pieces = string.Join(",", top.Nodes.Select(p => p.Text));
                Console.WriteLine($"{top.Text}: 非数字帧 children=[{pieces}]");
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
