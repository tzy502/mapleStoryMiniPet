using MiniPet.Models;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 全图扫描校验（2026-09-01）：遍历全部地图，轻量统计（不解码 PNG）——
/// tile 列数 / 表面簇数（tile.Y 近似聚类）/ 主簇占比 / 列缺口 gap>92。
/// 判定：池<5（Usable 拒）、5~19（小池）、层≥5 且主簇&lt;30%（选错层高危）、gap&gt;0（克隆补位）。
/// 输出问题行到 stdout；汇总统计结尾。
/// </summary>
public static class RoadScanAllProbe
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

        var mapSvc = new MapService(wz, new CacheManager());
        var ids = wz.ListMapIds();
        Console.WriteLine($"遍历 {ids.Count} 张地图…");

        int total = 0, usable0 = 0, smallPool = 0, multiLayer = 0, gapMap = 0, okMap = 0, noBack = 0;
        // 每类别收集问题图（key=id, value=严重度数值），终局取每类最严重的 ≤10
        var smallList = new Dictionary<string, int>();    // 池<5：主簇数越小越严重
        var poolList = new Dictionary<string, int>();     // 小池 5-19：主簇数越小越严重
        var multiList = new Dictionary<string, int>();    // 多层：主簇占比越小越严重
        var gapList = new Dictionary<string, int>();      // 缺口：gap 越大越严重
        var backList = new List<string>();                // 天空缺失：取样板
        var objList = new List<string>();                 // 无 obj 装饰：取样板
        var twinList = new Dictionary<string, int>();     // 双子层：占比接近 50 者严重
        int noObj = 0, twin = 0;
        foreach (var mid in ids.OrderBy(x => x))
        {
            MapInfo? mi = null;
            try { mi = mapSvc.LoadMap(mid); }
            catch { }
            if (mi == null) { continue; }
            total++;

            // tile 列与表面簇（appx：tile.Y 整数聚类）
            var colXs = new SortedSet<int>();
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var t in lay.Tiles) { colXs.Add(t.X); }
            }
            if (colXs.Count == 0)
            {
                usable0++;
                continue;
            }
            // 表面簇（tile.Y 众数 ±8 聚簇近似）
            var ys = new List<int>();
            foreach (var lay in mi.Layers)
            {
                if (lay == null) { continue; }
                foreach (var t in lay.Tiles) { ys.Add(t.Y); }
            }
            var clusters = ClusterYs(ys);
            int mainCount = clusters.Count > 0 ? clusters.Max(c => c.Count) : 0;
            int mainShare = mainCount > 0 ? mainCount * 100 / Math.Max(1, ys.Count) : 0;
            int surfClusters = clusters.Count;

            // 缺口
            var xsArr = colXs.ToList();
            int gaps = 0;
            for (int i = 1; i < xsArr.Count; i++)
            {
                if (xsArr[i] - xsArr[i - 1] > 92) { gaps++; }
            }

            var name = wz.GetMapName(mid) ?? "";
            bool isSmall = mainCount < 5;
            bool isSmallPool = mainCount >= 5 && mainCount < 20;
            bool isMulti = surfClusters >= 5 && mainShare < 30;
            bool isGap = gaps > 0;
            bool isNoBack = (mi.Backs == null || mi.Backs.Count == 0);
            bool isNoObj = mi.Layers.Sum(l => l?.Objs?.Count ?? 0) == 0;
            // 双子层：簇=2~4 且主簇占比 30~60%——无法单一分层、投票易摇摆
            bool isTwin = surfClusters >= 2 && surfClusters <= 4 && mainShare >= 30 && mainShare <= 60;

            string tags = (isSmall ? "池<5 " : isSmallPool ? "小池 " : "") + (isMulti ? "多层选错层 " : "") + (isGap ? "缺口 " : "") + (isNoBack ? "缺失天空back " : "") + (isNoObj ? "无obj装饰 " : "") + (isTwin ? "双子层" : "");
            if (tags.Trim().Length > 0)
            {
                Console.WriteLine($"TILE|{mid}|{name}|列={colXs.Count}|簇={surfClusters}|主={mainCount}({mainShare}%)|gap={gaps}|back={mi.Backs?.Count ?? 0}|{tags.Trim()}");
                if (isSmall) { usable0++; smallList[mid] = mainCount; }
                else if (isSmallPool) { smallPool++; poolList[mid] = mainCount; }
                if (isMulti) { multiLayer++; multiList[mid] = mainShare; }
                if (isGap) { gapMap++; gapList[mid] = gaps; }
                if (isNoBack) { noBack++; if (backList.Count < 10) { backList.Add(mid); } }
                if (isNoObj) { noObj++; if (objList.Count < 10) { objList.Add(mid); } }
                if (isTwin) { twin++; twinList[mid] = mainShare; }
            }
            else
            {
                okMap++;
            }
        }

        Console.WriteLine($"\n汇总：共 {total} 张有 tile，池<5={usable0} 小池={smallPool} 多层高危={multiLayer} 缺口={gapMap} 天空back缺失={noBack} 无obj装饰={noObj} 双子层={twin} 正常={okMap}");
        Console.WriteLine($"\n=== 问题清单（每类最多 10 个）===");
        Console.WriteLine("多层选错层高危系列  " + string.Join(" / ", multiList.OrderBy(kv => kv.Value).Take(10).Select(kv => kv.Key)));
        Console.WriteLine("小池系列  " + string.Join(" / ", poolList.OrderBy(kv => kv.Value).Take(10).Select(kv => kv.Key)));
        Console.WriteLine("池<5系列  " + string.Join(" / ", smallList.OrderBy(kv => kv.Value).Take(10).Select(kv => kv.Key)));
        Console.WriteLine("缺口系列  " + string.Join(" / ", gapList.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key)));
        Console.WriteLine("缺失天空back系列  " + string.Join(" / ", backList));
        Console.WriteLine("无obj装饰系列  " + string.Join(" / ", objList));
        Console.WriteLine("双子层系列  " + string.Join(" / ", twinList.OrderBy(kv => Math.Abs(kv.Value - 50)).Take(10).Select(kv => kv.Key)));
    }

    /// <summary>tile.Y 聚簇（±8 容差有序聚类，返回簇列表）。</summary>
    private static List<List<int>> ClusterYs(List<int> ys)
    {
        var result = new List<List<int>>();
        if (ys.Count == 0) { return result; }
        var sorted = ys.OrderBy(x => x).ToList();
        var cur = new List<int> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i - 1] <= 8)
            {
                cur.Add(sorted[i]);
            }
            else
            {
                result.Add(cur);
                cur = new List<int> { sorted[i] };
            }
        }
        result.Add(cur);
        return result;
    }
}
