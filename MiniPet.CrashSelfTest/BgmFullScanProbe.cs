using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Services;
using MiniPet.Models;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// BGM → 地图 全量数据校验探针（只读，不碰生产代码）：
/// 全量 21254 图 info/bgm 读取统计（空值分布 / 格式异常 / bgmKey 挂图分布 / 兜底源头）
/// + 目录交叉校验（bgm 指向不存在的曲目）+ 10 张疑似异常样本。
/// 读取全部走公开 API：ListMapIds + GetStringProperty/GetIntProperty（与 GetSceneTrackAsync
/// 生产读取路径同字符串同实现）+ BuildMapNameCache + MusicCatalogService.GetCatalogAsync。
/// </summary>
public static class BgmFullScanProbe
{
    private sealed class MapRec
    {
        public string Id = "";
        public char Group;          // Map0..Map9
        public string RawBgm = "";  // 原始 info/bgm 字符串
        public string? NormKey;     // NormalizeBgm 后 "Img/Track"（null = 无法归一化）
        public int Town;
        public bool EmptyBgm;       // raw 为空
        public bool BadFmt;         // raw 非空但归一化失败
        public bool TrackMissing;   // 归一化成功但目录无此曲
        public string Name = "";    // 地图显示名（String/Map.img；无则 map_{id}）
    }

    public static int Main(string[] args)
    {
        try
        {
            Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"probe error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static void Run()
    {
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        Console.WriteLine($"WZ lib  = {wzConfig.WzLibPath}");
        Console.WriteLine($"WZ base = {wzConfig.BaseWzPath}");
        if (string.IsNullOrEmpty(wzConfig.BaseWzPath) || !System.IO.Directory.Exists(wzConfig.BaseWzPath))
        {
            Console.WriteLine("WZ 路径无效，无法扫描");
            return;
        }
        var wz = new WzService();
        var (ok, err) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
        if (!ok)
        {
            Console.WriteLine($"WZ 加载失败: {err}");
            return;
        }
        Console.WriteLine("WZ 加载成功");

        // ── 1. 目录（Bgm*.img → 曲名集合），走生产路径 MusicCatalogService.GetCatalogAsync ──
        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var mcs = new MusicCatalogService(wz, config);
        var cat = mcs.GetCatalogAsync().GetAwaiter().GetResult();
        foreach (var t in cat)
        {
            if (!catalog.TryGetValue(t.Img, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                catalog[t.Img] = set;
            }
            set.Add(t.Track);
        }
        int catalogTracks = catalog.Values.Sum(v => v.Count);
        Console.WriteLine($"目录: Bgm 分类 {catalog.Count} 个, 曲目 {catalogTracks} 首");
        foreach (var kv in catalog.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value.Count} 首");
        }

        // ── 2. 地图名缓存（String/Map.img），走生产路径 BuildMapNameCache ──
        var names = wz.BuildMapNameCache();
        Console.WriteLine($"地图名缓存: {names.Count} 条");

        // ── 3. 全量地图 info/bgm 扫描（生产读取路径 GetStringProperty/GetIntProperty）──
        var ids = wz.ListMapIds();
        Console.WriteLine($"地图总数(ListMapIds): {ids.Count}");
        var recs = new List<MapRec>(ids.Count);
        long t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int readFail = 0;
        foreach (var mid in ids)
        {
            string path = $"Map/Map/Map{mid[0]}/{mid}.img/info/bgm";
            string? bgmVal = null;
            try { bgmVal = wz.GetStringProperty(path); }
            catch (Exception) { readFail++; }
            int town = 0;
            try { town = wz.GetIntProperty($"Map/Map/Map{mid[0]}/{mid}.img/info/town"); }
            catch (Exception) { }
            var rec = new MapRec
            {
                Id = mid,
                Group = mid[0],
                RawBgm = bgmVal ?? "",
                Town = town,
                Name = names.TryGetValue(mid, out var nm) ? nm : $"map_{mid}",
            };
            if (string.IsNullOrWhiteSpace(bgmVal))
            {
                rec.EmptyBgm = true;
            }
            else
            {
                var tr = MusicDecisions.NormalizeBgm(bgmVal);
                if (tr == null)
                {
                    rec.BadFmt = true;
                }
                else
                {
                    rec.NormKey = tr.Img + "/" + tr.Track;
                    rec.TrackMissing = !catalog.TryGetValue(tr.Img, out var set) || !set.Contains(tr.Track);
                }
            }
            recs.Add(rec);
        }
        long t1 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Console.WriteLine($"\n=== 全量扫描完成: {recs.Count} 图, 读取失败 {readFail}, 耗时 {t1 - t0}s ===");

        // ── 4. 四类统计 ──
        Console.WriteLine("\n【① bgm 为空/缺失】");
        var empties = recs.Where(r => r.EmptyBgm).ToList();
        Console.WriteLine($"总数: {empties.Count} / {recs.Count} ({100.0 * empties.Count / recs.Count:F1}%)");
        Console.WriteLine("按地图编号段分布:");
        foreach (var grp in recs.GroupBy(r => r.Group).OrderBy(g => g.Key))
        {
            int emptyInGroup = grp.Count(r => r.EmptyBgm);
            Console.WriteLine($"  Map{grp.Key}: {grp.Count()} 图, 空 bgm {emptyInGroup} ({100.0 * emptyInGroup / grp.Count():F1}%)");
        }
        Console.WriteLine("样本（有名字优先，前 12）:");
        foreach (var r in empties.OrderByDescending(r => r.Name.StartsWith("map_") ? 0 : 1).ThenBy(r => r.Id).Take(12))
        {
            Console.WriteLine($"  {r.Id} | {r.Name} | town={r.Town} | bgm=空");
        }

        Console.WriteLine("\n【② bgm 非 'Bgmxx/xxx' 常规格式】");
        var badFmt = recs.Where(r => r.BadFmt).ToList();
        Console.WriteLine($"格式异常（非空但无法拆成 分类/曲名）: {badFmt.Count} 图");
        foreach (var r in badFmt.Take(15))
        {
            Console.WriteLine($"  {r.Id} | {r.Name} | town={r.Town} | bgm=[{r.RawBgm}]");
        }
        var missing = recs.Where(r => r.TrackMissing).ToList();
        Console.WriteLine($"归一化成功但目录无此曲: {missing.Count} 图");
        foreach (var r in missing.Take(15))
        {
            Console.WriteLine($"  {r.Id} | {r.Name} | town={r.Town} | bgm=[{r.RawBgm}]");
        }
        Console.WriteLine("目录缺失 key 分组（key → 图数）:");
        foreach (var g in missing.GroupBy(r => r.NormKey!, StringComparer.Ordinal)
                                 .OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {g.Key}: {g.Count()} 图 | 样例 bgm=[{g.First().RawBgm}] 图={g.First().Id}");
        }

        Console.WriteLine("\n【③ 同一 bgmKey 挂多少图】");
        var keyGroups = recs.Where(r => r.NormKey != null)
                            .GroupBy(r => r.NormKey!, StringComparer.Ordinal)
                            .Select(g => new { Key = g.Key, Count = g.Count(), Towns = g.Count(r => r.Town == 1) })
                            .OrderByDescending(x => x.Count)
                            .ToList();
        Console.WriteLine($"有效 bgmKey 数: {keyGroups.Count}（目录 {catalogTracks} 首曲中 {keyGroups.Count} 首有图挂载, {catalogTracks - keyGroups.Count} 首无任何地图使用）");
        Console.WriteLine("挂图最多 TOP 15:");
        foreach (var x in keyGroups.Take(15))
        {
            Console.WriteLine($"  {x.Key}: {x.Count} 图（城镇 {x.Towns}）");
        }
        int single = keyGroups.Count(x => x.Count == 1);
        Console.WriteLine($"只挂 1 张图的 bgmKey 数: {single}");
        var lens = keyGroups.Select(x => x.Count).OrderBy(c => c).ToList();
        Console.WriteLine($"候选数 min/median/max: {lens.First()}/{lens[lens.Count / 2]}/{lens.Last()}");

        Console.WriteLine("\n【④ 有图但 bgm 为空 → 走不到逆推（兜底源头）】");
        Console.WriteLine($"共 {empties.Count} 图（同①），这些图在无限之路中永远不会成为候选源图；");
        Console.WriteLine($"全部候选 bgmKey 覆盖图数: {recs.Count(r => r.NormKey != null)} / {recs.Count}");

        // 目录曲目候选覆盖（无限之路影响面）
        Console.WriteLine("\n【无限之路影响面】");
        Console.WriteLine($"目录 {catalogTracks} 首曲: 有候选 {keyGroups.Count}（{100.0 * keyGroups.Count / catalogTracks:F1}%）, 无候选走末级兜底 {catalogTracks - keyGroups.Count}（{100.0 * (catalogTracks - keyGroups.Count) / catalogTracks:F1}%）");
        Console.WriteLine("无候选曲目样本（前 25）:");
        var catKeys = new HashSet<string>(catalog.SelectMany(kv => kv.Value.Select(t => kv.Key + "/" + t)), StringComparer.Ordinal);
        foreach (var k in catKeys.Except(keyGroups.Select(x => x.Key), StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(25))
        {
            Console.WriteLine($"  {k}");
        }

        // ── 5. 抽样 10 张「bgm 疑似异常」图 ──
        Console.WriteLine("\n【抽样 10 张 bgm 疑似异常图（图id|图名|bgm 值）】");
        var sample = new List<MapRec>();
        sample.AddRange(empties.Where(r => !r.Name.StartsWith("map_")).OrderBy(r => r.Id).Take(3));
        sample.AddRange(badFmt.OrderBy(r => r.Id).Take(3));
        sample.AddRange(missing.OrderBy(r => r.Id).Take(2));
        var silence = recs.Where(r => r.NormKey != null
                                      && r.NormKey.EndsWith("/Silence", StringComparison.OrdinalIgnoreCase)
                                      && !r.Name.StartsWith("map_")).OrderBy(r => r.Id).Take(1).ToList();
        sample.AddRange(silence);
        var zeroMap = recs.Where(r => r.Id.StartsWith("000")).Take(1).ToList();
        sample.AddRange(zeroMap);
        foreach (var r in sample.Take(10))
        {
            Console.WriteLine($"  {r.Id} | {r.Name} | town={r.Town} | bgm=[{r.RawBgm}]");
        }
    }
}
