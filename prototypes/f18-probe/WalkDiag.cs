using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

/// <summary>
/// 人物行走路径（照 sdlMS 的 foothold 链 + 爬绳）：
///   ① 起点/终点 = 地图上**相距最远的一对传送门**（传送门本身不渲染）
///   ② 解析 ladderRope（梯子/绳子），把「绳两端附近的 foothold 段」连成图的一条边
///   ③ Dijkstra：节点 = foothold 段；边 = prev/next 相邻（代价=段长）+ 绳/梯（代价=绳长）
///   ④ 出图：整图 + 路径（走路黄线 / 爬绳青线）+ 沿路纸娃娃（走路 walk 帧、爬绳 ladder/rope 帧）
/// 运行：--walk &lt;mapId&gt;
/// </summary>
static class WalkDiag
{
    sealed record Rope(int X, int YTop, int YBot, bool IsLadder);

    public static void Run(WzService wz, string outDir, string mapId, bool reverse = false)
    {
        Directory.CreateDirectory(outDir);
        var svc = new MapService(wz, new CacheManager());
        var mi = svc.LoadMap(mapId);
        if (mi == null) { Console.WriteLine($"[walk] {mapId} 加载失败"); return; }

        var fhs = new Dictionary<int, MapFoothold>();
        foreach (var f in mi.Footholds) { if (f.Id != 0) { fhs[f.Id] = f; } }

        // ---------- 1. 相距最远的一对传送门 ----------
        // 只取「能通往下一个地图」的传送门：WZ portal/*/tm（目标地图）非空且非 999999999
        var portalInfo = ParsePortals(wz, mapId);
        var allPs = mi.Portals.ToList();
        bool HasTarget(MapPortal p) =>
            portalInfo.TryGetValue(p.Id, out var inf)
            && !string.IsNullOrEmpty(inf.Tm)
            && inf.Tm != "999999999" && inf.Tm != "-1";
        var ps = allPs.Where(HasTarget).OrderBy(p => p.X).ToList();
        Console.WriteLine($"[walk] portal 总数 {allPs.Count}；有目标地图(tm)的候选 {ps.Count} 个：");
        foreach (var p in ps)
        {
            var inf = portalInfo[p.Id];
            Console.WriteLine($"[walk]   #{p.Id,-3} type={inf.Pt,-4} tm={inf.Tm,-12} tn='{inf.Tn}' @({p.X},{p.Y}) pn='{inf.Pn}'");
        }
        foreach (var p in allPs.Where(p => !HasTarget(p)))
        {
            var tm = portalInfo.TryGetValue(p.Id, out var inf) ? inf.Tm : "";
            Console.WriteLine($"[walk]   （跳过 #{p.Id} type={p.Type} tm='{tm}'）");
        }
        if (ps.Count < 2) { Console.WriteLine("[walk] 有目标地图的传送门不足 2 个"); return; }
        (MapPortal A, MapPortal B, double D) far = (ps[0], ps[1], -1);
        for (int i = 0; i < ps.Count; i++)
        {
            for (int j = i + 1; j < ps.Count; j++)
            {
                double d = Math.Sqrt(Math.Pow(ps[i].X - ps[j].X, 2) + Math.Pow(ps[i].Y - ps[j].Y, 2));
                if (d > far.D) { far = (ps[i], ps[j], d); }
            }
        }
        // 默认：x 小者当起点；reverse=true 时交换（用于「从低处往高处走」，会用到爬绳）
        var startP = far.A.X <= far.B.X ? far.A : far.B;
        var endP = ReferenceEquals(startP, far.A) ? far.B : far.A;
        if (reverse) { (startP, endP) = (endP, startP); }
        Console.WriteLine($"[walk] {mapId} portal {ps.Count} 个；最远一对相距 {far.D:F0}px：");
        Console.WriteLine($"[walk]   起点 portal#{startP.Id} @({startP.X},{startP.Y})  终点 portal#{endP.Id} @({endP.X},{endP.Y})");

        // ---------- 2. ladderRope ----------
        var ropes = ParseRopes(wz, mapId);
        Console.WriteLine($"[walk] ladderRope {ropes.Count} 根（梯子 {ropes.Count(r => r.IsLadder)} / 绳 {ropes.Count(r => !r.IsLadder)}）");

        // ---------- 3. 图 + Dijkstra ----------
        int startFh = NearestFh(fhs, startP.X, startP.Y);
        int endFh = NearestFh(fhs, endP.X, endP.Y);
        Console.WriteLine($"[walk] 吸附：起点 → fh#{startFh}  终点 → fh#{endFh}");
        var adj = BuildGraph(fhs, ropes);
        Console.WriteLine("[diag] 绳样例: " + string.Join(" | ", ropes.Take(6).Select(r => $"x={r.X} y={r.YTop}->{r.YBot}{(r.IsLadder ? "(梯)" : "(绳)")}")));
        Console.WriteLine($"[diag] 段边数：起点 fh#{startFh}={adj.GetValueOrDefault(startFh)?.Count ?? 0} 条，终点 fh#{endFh}={adj.GetValueOrDefault(endFh)?.Count ?? 0} 条");
        var comp = Component(adj, startFh);
        Console.WriteLine($"[diag] 起点连通分量：{comp.Count} 段 / 全图 {fhs.Count} 段；含终点？{comp.Contains(endFh)}");
        if (comp.Count <= 6) { Console.WriteLine("[diag] 起点分量成员: " + string.Join(",", comp.Select(id => $"#{id}({fhs[id].X1},{fhs[id].Y1})-({fhs[id].X2},{fhs[id].Y2})"))); }
        var (pathFh, pathRope) = Dijkstra(fhs, adj, startFh, endFh);
        if (pathFh == null || pathFh.Count == 0) { Console.WriteLine("[walk] 两点之间不可达（段链 + 绳不连通）"); return; }
        int climbCnt = pathRope.Count(r => r >= 0);
        double ropeLen = pathRope.Where(r => r >= 0).Sum(i => RopeLen(ropes[i]));
        Console.WriteLine($"[walk] 路径：{pathFh.Count} 段 + 攀爬 {climbCnt} 次（绳长 {ropeLen:F0}px）");

        // ---------- 4. 几何路径点 ----------
        var pts = new List<(float X, float Y, bool Climb)> { (startP.X, startP.Y, false) };
        float curX = startP.X, curY = startP.Y;
        for (int i = 0; i < pathFh.Count; i++)
        {
            var fh = fhs[pathFh[i]];
            double d1 = Math.Sqrt(Math.Pow(curX - fh.X1, 2) + Math.Pow(curY - fh.Y1, 2));
            double d2 = Math.Sqrt(Math.Pow(curX - fh.X2, 2) + Math.Pow(curY - fh.Y2, 2));
            bool fwd = d1 <= d2;
            var (ax, ay) = fwd ? (fh.X1, fh.Y1) : (fh.X2, fh.Y2);
            var (bx, by) = fwd ? (fh.X2, fh.Y2) : (fh.X1, fh.Y1);
            pts.Add((ax, ay, false));
            pts.Add((bx, by, false));
            curX = bx; curY = by;
            int rIdx = pathRope[i];
            if (rIdx >= 0)
            {
                var rope = ropes[rIdx];
                bool fromTop = Math.Abs(curY - rope.YTop) <= Math.Abs(curY - rope.YBot);
                float ty = fromTop ? rope.YTop : rope.YBot;
                float oy = fromTop ? rope.YBot : rope.YTop;
                pts.Add((rope.X, ty, true));
                pts.Add((rope.X, oy, true));
                curX = rope.X; curY = oy;
            }
        }
        pts.Add((endP.X, endP.Y, false));
        Console.WriteLine($"[walk] 路径点 {pts.Count} 个");

        // ---------- 5. 渲染 ----------
        svc.MapShowPortal = true;   // 本图用于核对传送门位置：贴图 + 编号全画
        int w = mi.MaxX - mi.MinX, h = mi.MaxY - mi.MinY;
        var (cx, cy) = MapService.GetMapCenter(mi);
        using var full = svc.RenderViewport(mi, cx, cy, 1f, 0, w, h);
        if (full == null) { Console.WriteLine("[walk] 渲染失败"); return; }
        int offX = mi.MinX, offY = mi.MinY;

        var cache = new CacheManager();
        var sprite = new SpriteService(cache);
        var pd = new PaperdollService(wz, cache, sprite);
        pd.SetAppearance(new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
        });
        var frameCache = new Dictionary<string, (SKBitmap? bmp, int ox, int oy)>();
        (SKBitmap? bmp, int ox, int oy) GetFrame(string act)
        {
            if (!frameCache.TryGetValue(act, out var v))
            {
                var (f, ox, oy, fw, fh) = pd.RenderFrame(act, 0);
                v = (f, ox, oy);
                frameCache[act] = v;
            }
            return v;
        }
        var walkA = GetFrame("walk1");
        var walkB = GetFrame("walk2");
        var climbA = GetFrame("ladder");
        var climbB = GetFrame("rope");
        Console.WriteLine($"[walk] 帧：walk1={walkA.bmp != null} walk2={walkB.bmp != null} ladder={climbA.bmp != null} rope={climbB.bmp != null}");

        double totalLen = 0;
        for (int i = 1; i < pts.Count; i++) { totalLen += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2)); }
        Console.WriteLine($"[walk] 路径总长（含跳跃/下落位移）{totalLen:F0}px；终点吸附误差 {Math.Sqrt(Math.Pow(pts[pts.Count - 1].X - endP.X, 2) + Math.Pow(pts[pts.Count - 1].Y - endP.Y, 2)):F0}px");
        // 调试：导出用到的动作帧，便于核对姿势与锚点
        foreach (var act in new[] { "walk1", "ladder", "rope", "stand1" })
        {
            var fr = GetFrame(act);
            if (fr.bmp != null)
            {
                string ff = Path.Combine(outDir, $"frame_{act}.png");
                using var fi = SKImage.FromBitmap(fr.bmp);
                using var fd = fi.Encode(SKEncodedImageFormat.Png, 100);
                using var ffs = File.OpenWrite(ff); ffs.SetLength(0); fd.SaveTo(ffs);
                Console.WriteLine($"[walk] 帧 {act}: {fr.bmp.Width}x{fr.bmp.Height} origin=({fr.ox},{fr.oy}) → frame_{act}.png");
            }
        }
        var samples = Sample(pts, 90, 240);
        Console.WriteLine($"[walk] 采样落脚点 {samples.Count} 个");

        using var bmp = new SKBitmap(full.Width, full.Height);
        using (var cv = new SKCanvas(bmp))
        {
            cv.DrawBitmap(full, 0, 0);
            using var walkLine = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true, Color = new SKColor(255, 220, 0), StrokeCap = SKStrokeCap.Round };
            using var climbLine = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true, Color = new SKColor(0, 230, 255), StrokeCap = SKStrokeCap.Round };
            using var dotS = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 220, 0) };
            using var dotE = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(255, 30, 30) };
            using var text = new SKPaint { IsAntialias = true, TextSize = 28, Color = SKColors.White };
            using var textBg = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, 180) };

            // ① 走路路径（黄线，人物之下）
            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i - 1].Climb || pts[i].Climb) { continue; }
                cv.DrawLine(pts[i - 1].X - offX, pts[i - 1].Y - offY, pts[i].X - offX, pts[i].Y - offY, walkLine);
            }
            // ② 爬绳人物（先画，随后用绳子盖住 = 绳在人物前面，符合 MapleStory）
            var climbFr = climbA.bmp != null ? climbA : climbB;
            foreach (var (sx, sy, climb) in samples)
            {
                if (!climb || climbFr.bmp == null) { continue; }
                cv.DrawBitmap(climbFr.bmp, sx - offX - climbFr.ox, sy - offY - climbFr.oy);
            }
            // ③ 爬绳路径（青线，盖在爬绳人物上）
            for (int i = 1; i < pts.Count; i++)
            {
                if (!(pts[i - 1].Climb || pts[i].Climb)) { continue; }
                cv.DrawLine(pts[i - 1].X - offX, pts[i - 1].Y - offY, pts[i].X - offX, pts[i].Y - offY, climbLine);
            }
            // ④ 走路人物（最上层）
            int si = 0;
            foreach (var (sx, sy, climb) in samples)
            {
                if (climb) { continue; }
                var fr = si++ % 2 == 0 ? walkA : walkB;
                if (fr.bmp != null) { cv.DrawBitmap(fr.bmp, sx - offX - fr.ox, sy - offY - fr.oy); }
            }
            // 所有传送门（核对用）：白点 + #id
            using (var pDot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(255, 255, 255, 235) })
            using (var pRing = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true, Color = new SKColor(0, 0, 0, 210) })
            using (var pText = new SKPaint { IsAntialias = true, TextSize = 26, Color = new SKColor(255, 255, 0) })
            using (var pTextBg = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, 170) })
            {
                foreach (var p in mi.Portals)
                {
                    float px = p.X - offX, py = p.Y - offY;
                    cv.DrawCircle(px, py, 9, pDot);
                    cv.DrawCircle(px, py, 9, pRing);
                    bool has = portalInfo.TryGetValue(p.Id, out var inf2);
                    string tm = has ? inf2.Tm : "";
                    bool valid = has && !string.IsNullOrEmpty(tm) && tm != "999999999" && tm != "-1";
                    string t = valid ? $"#{p.Id} ->{tm}" : $"#{p.Id} t{(has ? inf2.Pt : p.Type)}";
                    cv.DrawRect(new SKRect(px + 10, py - 26, px + 10 + t.Length * 15, py - 2), pTextBg);
                    cv.DrawText(t, px + 14, py - 6, pText);
                }
            }
            cv.DrawCircle(startP.X - offX, startP.Y - offY, 12, dotS);
            cv.DrawCircle(endP.X - offX, endP.Y - offY, 12, dotE);
            using (var mark = new SKPaint { IsAntialias = true, TextSize = 40, Color = SKColors.White })
            using (var markBg = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 150, 0, 220) })
            using (var markBg2 = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(200, 0, 0, 220) })
            {
                cv.DrawRect(new SKRect(startP.X - offX - 16, startP.Y - offY - 66, startP.X - offX + 16, startP.Y - offY - 24), markBg);
                cv.DrawText("S", startP.X - offX - 12, startP.Y - offY - 32, mark);
                cv.DrawRect(new SKRect(endP.X - offX - 16, endP.Y - offY - 66, endP.X - offX + 16, endP.Y - offY - 24), markBg2);
                cv.DrawText("E", endP.X - offX - 12, endP.Y - offY - 32, mark);
            }
            string label = $"START #{startP.Id}({startP.X},{startP.Y}) -> END #{endP.Id}({endP.X},{endP.Y})   straight={far.D:F0}px  path={totalLen:F0}px  {pathFh.Count} segs + climb {climbCnt}";
            cv.DrawRect(new SKRect(10, 10, 10 + label.Length * 15 + 20, 50), textBg);
            cv.DrawText(label, 20, 40, text);
        }
        string outFile = Path.Combine(outDir, $"walk_{mapId}{(reverse ? "_反向" : "")}_传送门核对.png");
        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.OpenWrite(outFile)) { fs.SetLength(0); data.SaveTo(fs); }
        Console.WriteLine();

        string txt = Path.Combine(outDir, $"walk_{mapId}{(reverse ? "_反向" : "")}_路径.txt");
        using (var sw = new StreamWriter(txt))
        {
            sw.WriteLine($"# {mapId} 起点 portal#{startP.Id}@({startP.X},{startP.Y}) → 终点 portal#{endP.Id}@({endP.X},{endP.Y})");
            sw.WriteLine($"# 传送门最远一对直线 {far.D:F0}px；路径 {totalLen:F0}px（{pathFh.Count} 段 + 攀爬 {climbCnt} 次）");
            sw.WriteLine("# idx  类型    段/绳                     长度");
            for (int i = 0; i < pathFh.Count; i++)
            {
                var fh = fhs[pathFh[i]];
                sw.WriteLine($"{i,4}  walk    fh#{fh.Id} L{fh.Layer} ({fh.X1},{fh.Y1})->({fh.X2},{fh.Y2})   {SegLen(fh):F0}");
                if (pathRope[i] >= 0)
                {
                    var r = ropes[pathRope[i]];
                    sw.WriteLine($"      climb   {(r.IsLadder ? "梯子" : "绳")} x={r.X} y={r.YTop}->{r.YBot}   {RopeLen(r):F0}");
                }
            }
        }
        Console.WriteLine($"  → walk_{mapId}{(reverse ? "_反向" : "")}_路径.txt");
    }

    sealed record PortalInfo(int Pt, string Pn, string Tn, string Tm);

    static Dictionary<int, PortalInfo> ParsePortals(WzService wz, string mapId)
    {
        var map = new Dictionary<int, PortalInfo>();
        var root = wz.FindNodeByPath($"Map/Map/Map{mapId[0]}/{mapId}.img/portal");
        if (root == null) { return map; }
        foreach (Wz_Node n in root.Nodes)
        {
            if (!int.TryParse(n.Text, out var id)) { continue; }
            int pt = n.FindNodeByPath("pt")?.GetValue<int>() ?? 0;
            string pn = n.FindNodeByPath("pn")?.GetValue<string>() ?? "";
            string tn = n.FindNodeByPath("tn")?.GetValue<string>() ?? "";
            string tm = "";
            try { tm = n.FindNodeByPath("tm")?.GetValue<string>() ?? ""; } catch { tm = ""; }
            if (string.IsNullOrEmpty(tm))
            {
                try { tm = (n.FindNodeByPath("tm")?.GetValue<int>() ?? 0).ToString(); } catch { tm = ""; }
            }
            map[id] = new PortalInfo(pt, pn, tn, tm);
        }
        return map;
    }

    // ---------- ladderRope 解析 ----------
    static List<Rope> ParseRopes(WzService wz, string mapId)
    {
        var list = new List<Rope>();
        var root = wz.FindNodeByPath($"Map/Map/Map{mapId[0]}/{mapId}.img/ladderRope");
        if (root == null) { return list; }
        foreach (Wz_Node n in root.Nodes)
        {
            int x = n.FindNodeByPath("x")?.GetValue<int>() ?? 0;
            int y1 = n.FindNodeByPath("y1")?.GetValue<int>() ?? 0;
            int y2 = n.FindNodeByPath("y2")?.GetValue<int>() ?? 0;
            int l = n.FindNodeByPath("l")?.GetValue<int>() ?? 0;
            list.Add(new Rope(x, Math.Min(y1, y2), Math.Max(y1, y2), l != 0));
        }
        return list;
    }

    // ---------- 图 ----------
    static Dictionary<int, List<(int to, double cost, int rope)>> BuildGraph(Dictionary<int, MapFoothold> fhs, List<Rope> ropes)
    {
        var adj = new Dictionary<int, List<(int to, double cost, int rope)>>();
        void Add(int a, int b, double cost, int rope)
        {
            if (!adj.TryGetValue(a, out var l)) { l = new List<(int, double, int)>(); adj[a] = l; }
            l.Add((b, cost, rope));
        }
        foreach (var fh in fhs.Values)
        {
            if (fh.Next != 0 && fhs.ContainsKey(fh.Next)) { Add(fh.Id, fh.Next, SegLen(fhs[fh.Next]), -1); }
            if (fh.Prev != 0 && fhs.ContainsKey(fh.Prev)) { Add(fh.Id, fh.Prev, SegLen(fhs[fh.Prev]), -1); }
        }
        // 绳/梯：两端各吸附最近段（限 60px，超距视为悬空不连），代价 = 绳长 + 两端吸附距离
        for (int ri = 0; ri < ropes.Count; ri++)
        {
            var r = ropes[ri];
            var (topId, topD) = NearestFhWithDist(fhs, r.X, r.YTop);
            var (botId, botD) = NearestFhWithDist(fhs, r.X, r.YBot);
            if (topId == 0 || botId == 0 || topId == botId) { continue; }
            if (topD > 150 || botD > 150) { continue; }
            double cost = RopeLen(r) + topD + botD;
            Add(topId, botId, cost, ri);
            Add(botId, topId, cost, ri);
        }
        // 跳跃边：水平 <=110px 且**向上 <=77px**（sdlMS：起跳 -555 / 重力 2000 → 最大跃升 555²/(2*2000) ≈ 77px）→ 双向
        const double JumpUp = 77, JumpDx = 110;
        var ids = fhs.Keys.OrderBy(v => v).ToList();
        for (int i = 0; i < ids.Count; i++)
        {
            for (int j = i + 1; j < ids.Count; j++)
            {
                var a = fhs[ids[i]];
                var b = fhs[ids[j]];
                double dx = Math.Min(Math.Abs(a.X1 - b.X1), Math.Abs(a.X1 - b.X2));
                dx = Math.Min(dx, Math.Min(Math.Abs(a.X2 - b.X1), Math.Abs(a.X2 - b.X2)));
                double ay = Math.Min(a.Y1, a.Y2), by = Math.Min(b.Y1, b.Y2);
                double up = ay - by;                       // >0 = b 在 a 上方
                if (dx <= JumpDx && up <= JumpUp && up >= -JumpUp)
                {
                    double cost = dx * 2 + 20;
                    Add(a.Id, b.Id, cost, -1);
                    Add(b.Id, a.Id, cost, -1);
                }
            }
        }
        // 下落边（单向 A→下方最近段）：从一段的 x 位置可以直接掉到下方最近的段（sdlMS 扫掠落地）
        foreach (var a in fhs.Values)
        {
            float mx = (a.X1 + a.X2) / 2f;
            double ay = Math.Max(a.Y1, a.Y2);
            int bestB = 0; double bestDy = double.MaxValue;
            foreach (var b in fhs.Values)
            {
                if (b.Id == a.Id) { continue; }
                int lo = Math.Min(b.X1, b.X2), hi = Math.Max(b.X1, b.X2);
                if (mx < lo || mx > hi) { continue; }
                double by = (b.Y1 + b.Y2) / 2.0;
                double dy = by - ay;
                if (dy > 5 && dy < bestDy) { bestDy = dy; bestB = b.Id; }
            }
            if (bestB != 0) { Add(a.Id, bestB, bestDy * 1.2, -1); }
        }
        return adj;
    }

    /// <summary>Dijkstra：返回段序列 + 每段之后使用的绳索引（-1=无）。</summary>
    static (List<int>? fhs, List<int> ropes) Dijkstra(
        Dictionary<int, MapFoothold> fhs, Dictionary<int, List<(int to, double cost, int rope)>> adj, int start, int goal)
    {
        if (!fhs.ContainsKey(start) || !fhs.ContainsKey(goal)) { return (null, new List<int>()); }
        var dist = new Dictionary<int, double> { [start] = 0 };
        var prev = new Dictionary<int, (int from, int rope)>();
        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(start, 0);
        while (pq.TryDequeue(out var cur, out var d))
        {
            if (d > dist.GetValueOrDefault(cur, double.MaxValue)) { continue; }
            if (cur == goal) { break; }
            if (!adj.TryGetValue(cur, out var edges)) { continue; }
            foreach (var (to, cost, rope) in edges)
            {
                double nd = d + cost;
                if (nd < dist.GetValueOrDefault(to, double.MaxValue))
                {
                    dist[to] = nd;
                    prev[to] = (cur, rope);
                    pq.Enqueue(to, nd);
                }
            }
        }
        if (!dist.ContainsKey(goal)) { return (null, new List<int>()); }
        var path = new List<int>();
        var ropeAt = new List<int>();
        int node = goal;
        while (node != start)
        {
            path.Add(node);
            var (from, rope) = prev[node];
            ropeAt.Add(rope);
            node = from;
        }
        path.Add(start);
        path.Reverse();
        ropeAt.Reverse();
        var ropesOut = new List<int>();
        for (int i = 0; i < path.Count; i++) { ropesOut.Add(i < ropeAt.Count ? ropeAt[i] : -1); }
        return (path, ropesOut);
    }

    static HashSet<int> Component(Dictionary<int, List<(int to, double cost, int rope)>> adj, int start)
    {
        var seen = new HashSet<int> { start };
        var q = new Queue<int>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (!adj.TryGetValue(cur, out var edges)) { continue; }
            foreach (var (to, _, _) in edges)
            {
                if (seen.Add(to)) { q.Enqueue(to); }
            }
        }
        return seen;
    }

    // ---------- 几何小工具 ----------
    static double SegLen(MapFoothold fh) => Math.Sqrt(Math.Pow(fh.X2 - fh.X1, 2) + Math.Pow(fh.Y2 - fh.Y1, 2));
    static double RopeLen(Rope r) => Math.Abs(r.YBot - r.YTop);

    static (int id, double dist) NearestFhWithDist(Dictionary<int, MapFoothold> fhs, float x, float y)
    {
        double best = double.MaxValue; int bestId = 0;
        foreach (var fh in fhs.Values)
        {
            double d = DistToSeg(x, y, fh.X1, fh.Y1, fh.X2, fh.Y2);
            if (d < best) { best = d; bestId = fh.Id; }
        }
        return (bestId, best);
    }

    /// <summary>两段的四个端点之间的最小距离（判断是否可跨缺口/跳过去）。</summary>
    static double MinEndpointDist(MapFoothold a, MapFoothold b)
    {
        double best = double.MaxValue;
        foreach (var (ax, ay) in new[] { (a.X1, a.Y1), (a.X2, a.Y2) })
        {
            foreach (var (bx, by) in new[] { (b.X1, b.Y1), (b.X2, b.Y2) })
            {
                double d = Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));
                if (d < best) { best = d; }
            }
        }
        return best;
    }

    static int NearestFh(Dictionary<int, MapFoothold> fhs, float x, float y)
    {
        double best = double.MaxValue; int bestId = 0;
        foreach (var fh in fhs.Values)
        {
            double d = DistToSeg(x, y, fh.X1, fh.Y1, fh.X2, fh.Y2);
            if (d < best) { best = d; bestId = fh.Id; }
        }
        return bestId;
    }

    static double DistToSeg(float px, float py, float x1, float y1, float x2, float y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double len2 = dx * dx + dy * dy;
        if (len2 <= 0) { return Math.Sqrt(Math.Pow(px - x1, 2) + Math.Pow(py - y1, 2)); }
        double t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(px - (x1 + t * dx), 2) + Math.Pow(py - (y1 + t * dy), 2));
    }

    static List<(float X, float Y, bool Climb)> Sample(List<(float X, float Y, bool Climb)> pts, double step, int max)
    {
        var outp = new List<(float, float, bool)>();
        double acc = 0, nextAt = 0;
        for (int i = 1; i < pts.Count && outp.Count < max; i++)
        {
            double seg = Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
            if (seg <= 0.001) { continue; }
            double t = nextAt - acc;
            while (t <= seg && outp.Count < max)
            {
                float r = (float)(t / seg);
                outp.Add((pts[i - 1].X + (pts[i].X - pts[i - 1].X) * r,
                          pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * r,
                          pts[i - 1].Climb || pts[i].Climb));
                nextAt += step;
                t = nextAt - acc;
            }
            acc += seg;
        }
        return outp;
    }
}
