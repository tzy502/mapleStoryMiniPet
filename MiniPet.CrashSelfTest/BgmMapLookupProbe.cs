using System;
using System.Linq;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// BGM → 地图 查询探针：给定一首 bgm（"Bgm58/FloralLife" 或带 .img "Bgm58.img/FloralLife"），
/// 输出它属于哪些地图（id | 是否城镇 | 图名）。
/// 复用生产查询路径 MusicCatalogService.GetRawBgmCandidatesAsync：
/// 磁盘缓存命中秒出（FullComplete=true）；缓存缺失才现场全量扫描。
/// 用法：BgmMapLookupProbe "Bgm00/GoPicnic" [更多 bgm...]
/// </summary>
public static class BgmMapLookupProbe
{
    public static int Main(string[] args)
    {
        try
        {
            var queries = args.Length > 0 ? args : new[] { "Bgm00/GoPicnic" };
            var config = new ConfigService();
            var wzConfig = config.LoadWzConfig();
            var wz = new WzService();
            var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
            if (!ok)
            {
                Console.WriteLine("WZ 加载失败");
                return 1;
            }
            var mcs = new MusicCatalogService(wz, config);
            var names = wz.BuildMapNameCache();
            foreach (var q in queries)
            {
                var tr = MusicDecisions.NormalizeBgm(q);
                if (tr == null)
                {
                    Console.WriteLine($"无法解析 bgm: {q}");
                    continue;
                }
                string key = tr.Img + "/" + tr.Track;
                var result = mcs.GetRawBgmCandidatesAsync(key).GetAwaiter().GetResult();
                Console.WriteLine($"\n=== {key} 属于 {result.Candidates.Count} 张地图 (FullComplete={result.FullComplete}) ===");
                int town = result.Candidates.Count(c => c.IsTown);
                Console.WriteLine($"    城镇 {town} 张 / 普通 {result.Candidates.Count - town} 张");
                foreach (var (mapId, isTown) in result.Candidates.OrderBy(c => c.MapId))
                {
                    string name = names.TryGetValue(mapId, out var n) ? n : "map_" + mapId;
                    Console.WriteLine($"  {mapId} | {(isTown ? "城镇" : "普通")} | {name}");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"probe error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
