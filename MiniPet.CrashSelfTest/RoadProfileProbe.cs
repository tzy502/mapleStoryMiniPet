using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using MiniPet.Services.Road;

namespace MiniPet.CrashSelfTest;

/// <summary>地图剖面分析：射手村/地球防御总部的段表面分布、坡、墙（竖直 tile）。</summary>
public static class RoadProfileProbe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Run();
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"probe error: {ex.Message}");
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static void Run()
    {
        var basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        var targets = new[] { "射手村" };
        var found = new Dictionary<string, string>();
        var globalMapIds = new List<string>();
        var mcs = new MapCatalogService(wz, new CacheManager());
        var cache = mcs.BuildMapNameCache();
        foreach (var (mid, name) in cache)
        {
            if (name != null && name.Contains("地球防御本部") && (mid.StartsWith("221") || mid.StartsWith("922")))
            {
                if (!globalMapIds.Contains(mid))
                {
                    globalMapIds.Add(mid);
                    Console.WriteLine($"  [相关图] {name} → {mid}");
                }
            }
        }
        globalMapIds.Add("100000000");   // 射手村
        globalMapIds.Add("103000000");   // 日志出现（GoPicnic 等）
        globalMapIds.Add("109010108");
        globalMapIds.Add("867112000");
        globalMapIds.Add("993212926");
        var maps = globalMapIds;

        foreach (var mapId in maps)
        {
            Console.WriteLine($"\n===== 地图 {mapId} =====");
            var state = new RoadState();
            RoadMaterialExtractor.Configure(new MapService(wz, new CacheManager()), wz);
            var pack = RoadMaterialExtractor.Extract(mapId, CancellationToken.None, state);
            if (pack == null)
            {
                Console.WriteLine($"  Extract null: {state.LastError}");
                continue;
            }
            Console.WriteLine($"  平地柱 {pack.FlatColumns.Count} 斜坡柱 {pack.SlantColumns.Count} 台阶 {pack.Steps.Count} back {pack.Backs.Count} mainSurf={pack.MainSurfaceH}");
            var allCols = pack.FlatColumns.Concat(pack.SlantColumns).ToList();
            var surfGroups = allCols.GroupBy(c => c.SurfaceH).OrderBy(g => g.Key).ToList();
            var mainCount = surfGroups.Count > 0 ? surfGroups.Max(g => g.Count()) : 0;
            var mainSurf = surfGroups.Count > 0 ? surfGroups.First(g => g.Count() == mainCount).Key : 0;
            var mainCols = allCols.Where(c => Math.Abs(c.SurfaceH - mainSurf) <= 8).ToList();
            // 缺口：主簇切片按 X 排序后相邻差 >92 的间隙数
            var xs = mainCols.Select(c => c.SurfaceH == mainSurf ? 0 : 0).ToList(); // 占位
            Console.WriteLine($"  柱={allCols.Count} 簇={surfGroups.Count} 主簇y={mainSurf}({mainCount}) 占比={(allCols.Count > 0 ? (mainCount * 100 / allCols.Count) : 0)}% 面域=[{surfGroups.FirstOrDefault()?.Key ?? 0}..{surfGroups.LastOrDefault()?.Key ?? 0}]");
            // 池大小 = 主簇 ±8 切片数
            int poolSize = mainCols.Count;
            Console.WriteLine($"  池切片={poolSize} ({(poolSize < 20 ? "不足20" : poolSize < 30 ? "偏小" : "达标")})");
            // 缺口统计（MapInfo 级）：任一图层 tile 列 X 排序后相邻 gap>92 的间隙数
            try
            {
                var mapSvc2 = new MapService(wz, new CacheManager());
                var mi = mapSvc2.LoadMap(mapId);
                if (mi != null)
                {
                    int gaps = 0, cols = 0;
                    var allXs = new SortedSet<int>();
                    foreach (var lay in mi.Layers)
                    {
                        if (lay == null) { continue; }
                        foreach (var t in lay.Tiles) { allXs.Add(t.X); cols++; }
                    }
                    var xsArr = allXs.ToList();
                    for (int i = 1; i < xsArr.Count; i++)
                    {
                        if (xsArr[i] - xsArr[i - 1] > 92) { gaps++; }
                    }
                    Console.WriteLine($"  tile列={xsArr.Count} 列gap>92={gaps} 处");
                }
            }
            catch (Exception ex) { Console.WriteLine($"  gap统计失败: {ex.Message}"); }
            foreach (var b in pack.Backs.Take(8))
            {
                Console.WriteLine($"  back: res={b.Resource.ResourceUrl} tm={b.Type} x={b.X} y={b.Y} c={b.Cx}x{b.Cy} rx={b.Rx} ry={b.Ry} front={b.Front} screen={b.ScreenMode} alpha={b.Alpha}");
            }
        }
    }
}
