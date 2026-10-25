using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using MiniPet.Services;

/// <summary>诊断：foothold 的 prev/next「链」连通分量（行走模式只沿单链走，链碎了就走不出去）。</summary>
static class ChainDiag
{
    public static void Run(WzService wz, string mapId)
    {
        var svc = new MapService(wz, new CacheManager());
        var mi = svc.LoadMap(mapId);
        if (mi == null) { Console.WriteLine($"{mapId} 加载失败"); return; }
        var fhs = new Dictionary<int, MapFoothold>();
        foreach (var f in mi.Footholds) { if (f.Id != 0 && !fhs.ContainsKey(f.Id)) { fhs[f.Id] = f; } }

        var parent = fhs.Keys.ToDictionary(k => k, k => k);
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { if (fhs.ContainsKey(b)) { parent[Find(a)] = Find(b); } }
        foreach (var f in fhs.Values) { Union(f.Id, f.Next); Union(f.Id, f.Prev); }

        var comps = fhs.Keys.GroupBy(Find)
            .Select(g => (Ids: g.ToList(), Len: g.Sum(id => Math.Sqrt(Math.Pow(fhs[id].X2 - fhs[id].X1, 2) + Math.Pow(fhs[id].Y2 - fhs[id].Y1, 2)))))
            .OrderByDescending(c => c.Len).ToList();

        // 入口传送门（最左）吸附段所在分量
        var portal = mi.Portals.OrderBy(p => p.X).FirstOrDefault();
        int entry = 0; double best = double.MaxValue;
        if (portal != null)
        {
            foreach (var f in fhs.Values)
            {
                double d = Dist(portal.X, portal.Y, f);
                if (d < best) { best = d; entry = f.Id; }
            }
        }
        var entryComp = comps.FirstOrDefault(c => c.Ids.Contains(entry));

        Console.WriteLine($"=== {mapId} foothold {fhs.Count} 段 / 纯 prev-next 链 {comps.Count} 条；绳 {mi.Ropes.Count} 根 ===");
        Console.WriteLine($"  最大链：{string.Join(" | ", comps.Take(5).Select(c => $"{c.Ids.Count}段/{c.Len:F0}px"))}");
        Console.WriteLine($"  入口门#{portal?.Id}({portal?.X},{portal?.Y}) 吸附 fh#{entry} → 所在链只有 {entryComp.Ids.Count} 段 / {entryComp.Len:F0}px");
    }

    static double Dist(float px, float py, MapFoothold f)
    {
        double dx = f.X2 - f.X1, dy = f.Y2 - f.Y1, len2 = dx * dx + dy * dy;
        if (len2 <= 0) { return Math.Sqrt(Math.Pow(px - f.X1, 2) + Math.Pow(py - f.Y1, 2)); }
        double t = Math.Clamp(((px - f.X1) * dx + (py - f.Y1) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(px - (f.X1 + t * dx), 2) + Math.Pow(py - (f.Y1 + t * dy), 2));
    }
}
