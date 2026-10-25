using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>按 bgm 关键词查地图 + back/tile/obj 结构（森林条带诊断）。</summary>
public static class BgmMapProbe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        Dispatcher.UIThread.Post(() =>
        {
            try { Run(args.Length > 0 ? args[0] : "WarmRegard"); exitCode = 0; }
            catch (Exception ex) { Console.WriteLine($"probe error: {ex.Message}"); exitCode = 1; }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static void Run(string needle)
    {
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
        if (!ok) { Console.WriteLine("WZ 失败"); return; }
        var mapSvc = new MapService(wz, new CacheManager());
        if (needle.Length == 9 && needle.All(char.IsDigit))
        {
            var miX = mapSvc.LoadMap(needle);
            if (miX != null && needle == "101000000")
            {
                int gl = miX.Footholds.Count > 0 ? miX.Footholds.Min(f => f.Layer) : 0;
                var segs = miX.Footholds.Where(f => f.Layer == gl && f.Y1 == f.Y2).Select(f => (f.Y1, Math.Min(f.X1, f.X2), Math.Max(f.X1, f.X2))).ToList();
                Console.WriteLine($"[MATCH] 地面层={gl} 水平段={segs.Count} 段y众数={segs.GroupBy(s => s.Y1).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key}");
                int hit = 0, miss = 0;
                foreach (var layer in miX.Layers) { if (layer == null) continue; foreach (var o in layer.Objs) { if (Math.Abs(o.Y - segs.Min(s => s.Y1)) <= 60) hit++; else miss++; } }
                Console.WriteLine($"[MATCH] obj 锚点距最浅段y≤60: 命中={hit} 未命中={miss}（总={hit+miss}）");
                foreach (var sg in segs.Take(3)) { Console.WriteLine($"   seg y={sg.Y1} x=[{sg.Item2}..{sg.Item3}]"); }
                return;
            }
        }
        if (needle.Length == 9 && needle.All(char.IsDigit))
        {
            var mi0 = mapSvc.LoadMap(needle);
            if (mi0 == null) { Console.WriteLine("LoadMap null"); return; }
            var us = new HashSet<string>();
            foreach (var lay in mi0.Layers) { if (lay == null) continue; foreach (var t in lay.Tiles) { var ss = (t.Resource.ResourceUrl ?? "").Split('/'); if (ss.Length >= 4) us.Add(ss[3]); } }
            int fh0 = mi0.Footholds.Count(f => f.Layer == 0 && f.Y1 == f.Y2);
            Console.WriteLine($"== {needle} {wz.GetMapName(needle) ?? "?"} tile={mi0.Layers.Sum(l => l?.Tiles?.Count ?? 0)} obj={mi0.Layers.Sum(l => l?.Objs?.Count ?? 0)} back={mi0.Backs.Count} fh={mi0.Footholds.Count} fhL0水平={fh0} u={string.Join(",", us.OrderBy(x => x))}");
            foreach (var g in mi0.Footholds.GroupBy(f => f.Layer).OrderBy(g => g.Key)) { Console.WriteLine($"   fh Layer {g.Key}: {g.Count()} 条（水平 {g.Count(f => f.Y1 == f.Y2)}）"); }
            foreach (var f in mi0.Footholds.Where(f => f.Y1 == f.Y2 && Math.Abs(f.X2 - f.X1) >= 270).Take(4)) { Console.WriteLine($"   fhL{f.Layer} 水平 {f.X1},{f.Y1}→{f.X2},{f.Y2}"); }
            return;
        }
        foreach (var mid in wz.ListMapIds().OrderBy(x => x))
        {
            string bgm = wz.GetStringProperty($"{MapService.GetMapWzPath(mid)}/info/bgm") ?? "";
            if (!bgm.Contains(needle, StringComparison.OrdinalIgnoreCase)) { continue; }
            var mi = mapSvc.LoadMap(mid);
            if (mi == null) { continue; }
            int objs = mi.Layers.Sum(l => l?.Objs?.Count ?? 0);
            int tiles = mi.Layers.Sum(l => l?.Tiles?.Count ?? 0);
            Console.WriteLine($"== {mid} {wz.GetMapName(mid) ?? "?"} bgm={bgm} tile={tiles} obj={objs} back={mi.Backs.Count}");
            foreach (var b in mi.Backs.Take(10))
            {
                Console.WriteLine($"   back[{b.Id}] {b.Resource.ResourceUrl} type={b.Type} ani={b.Ani} x={b.X} y={b.Y} c={b.Cx}x{b.Cy} rx={b.Rx} ry={b.Ry} front={b.Front} screen={b.ScreenMode} flip={b.FlipX}");
            }
        }
    }
}
