using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerV2 = WzComparerR2.WzLib;

/// <summary>
/// 取「最长可走路面带」并裁片：规则与 foothold 筛选一致
/// （水平段 + 有平行 enH 表面砖 → 合并成带 → 只留 >=180px），每张图导出最长带的裁片
/// out/band_{mapId}.png（高 560，地面线距顶 400）+ 清单 out/band_清单.txt
/// 运行：--bands &lt;mapId...&gt;
/// </summary>
static class BandsDiag
{
    const int ABOVE = 400, BELOW = 160, H = ABOVE + BELOW;

    public static void Run(WzService wz, string outDir, string[] mapIds)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        foreach (var id in mapIds)
        {
            var svc = new MapService(wz, new CacheManager());
            var mi = svc.LoadMap(id);
            if (mi == null) { Console.WriteLine($"  {id} 加载失败"); report.Add($"{id}\tFAIL\tload"); continue; }

            // enH* 表面砖
            var tiles = new List<(int L, int R, int Top)>();
            for (int l = 0; l < mi.Layers.Length; l++)
            {
                foreach (var t in mi.Layers[l].Tiles)
                {
                    string url = t.Resource?.ResourceUrl ?? "";
                    if (string.IsNullOrEmpty(url)) { continue; }
                    var parts = url.Split('/');
                    string u = parts.Length >= 2 ? parts[parts.Length - 2] : "";
                    if (!u.StartsWith("enH")) { continue; }
                    var sz = wz.GetPngSize(url);
                    if (sz == null) { continue; }
                    var (ox, oy) = wz.GetOrigin(url);
                    tiles.Add((t.X - ox, t.X - ox + sz.Value.W, t.Y - oy));
                }
            }

            // 可走路面段
            var keep = new List<(int Layer, int Y, int X1, int X2)>();
            foreach (var fh in mi.Footholds)
            {
                if (fh.Y1 != fh.Y2) { continue; }
                int x1 = Math.Min(fh.X1, fh.X2), x2 = Math.Max(fh.X1, fh.X2);
                bool has = false;
                foreach (var t in tiles)
                {
                    if (t.R < x1 || t.L > x2) { continue; }
                    if (Math.Abs(t.Top - fh.Y1) > 20) { continue; }
                    has = true; break;
                }
                if (has) { keep.Add((fh.Layer, fh.Y1, x1, x2)); }
            }

            // 合并带（同层同 y，首尾相接 gap<=2）
            var bands = new List<(int Layer, int Y, int X1, int X2, int Len)>();
            foreach (var g in keep.GroupBy(k => (k.Layer, k.Y)))
            {
                var segs = g.OrderBy(k => k.X1).ToList();
                int cx1 = segs[0].X1, cx2 = segs[0].X2;
                for (int i = 1; i < segs.Count; i++)
                {
                    if (segs[i].X1 <= cx2 + 2) { cx2 = Math.Max(cx2, segs[i].X2); }
                    else { bands.Add((g.Key.Layer, g.Key.Y, cx1, cx2, cx2 - cx1)); cx1 = segs[i].X1; cx2 = segs[i].X2; }
                }
                bands.Add((g.Key.Layer, g.Key.Y, cx1, cx2, cx2 - cx1));
            }
            var best = bands.Where(b => b.Len >= 180).OrderByDescending(b => b.Len).FirstOrDefault();
            if (best.Len == 0) { Console.WriteLine($"  {id} 无 >=180 的带（带数 {bands.Count}）"); report.Add($"{id}\tNOBAND\tbands={bands.Count}"); continue; }

            int w = mi.MaxX - mi.MinX, h = mi.MaxY - mi.MinY;
            var (cx, cy) = MapService.GetMapCenter(mi);
            using var full = svc.RenderViewport(mi, cx, cy, 1f, 0, w, h);
            if (full == null) { report.Add($"{id}\tFAIL\trender"); continue; }

            int gx = best.X1 - mi.MinX;                 // 带左端（图内）
            int gy = best.Y - mi.MinY;                  // 地面线（图内）
            int cropW = best.Len;
            int top = gy - ABOVE;
            using var crop = new SKBitmap(cropW, H);
            using (var cv = new SKCanvas(crop))
            {
                cv.Clear(SKColors.Transparent);
                int sy0 = Math.Max(0, top), sy1 = Math.Min(full.Height, top + H);
                int dy0 = sy0 - top;
                if (sy1 > sy0)
                {
                    cv.DrawBitmap(full, new SKRect(gx, sy0, gx + cropW, sy1), new SKRect(0, dy0, cropW, dy0 + (sy1 - sy0)));
                }
            }
            string f = Path.Combine(outDir, $"band_{id}.png");
            using (var img = SKImage.FromBitmap(crop))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(f)) { fs.SetLength(0); data.SaveTo(fs); }
            Console.WriteLine($"  → band_{id}.png 带 L{best.Layer} y={best.Y} x=[{best.X1},{best.X2}] 长={best.Len} 图={w}x{h}");
            report.Add($"{id}\tOK\tL{best.Layer}\ty={best.Y}\tx1={best.X1}\tx2={best.X2}\tlen={best.Len}\tbands={bands.Count}\tmapw={w}\tmapname={MapName(mi)}");
        }
        File.WriteAllLines(Path.Combine(outDir, "band_清单.txt"), report);
        Console.WriteLine($"  → {Path.Combine(outDir, "band_清单.txt")}");
    }

    static string MapName(MapInfo mi) => mi.Id;
}
