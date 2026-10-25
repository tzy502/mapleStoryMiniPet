using System;
using System.Diagnostics;
using MiniPet.Services;

/// <summary>诊断：某个曲目能否反查到地图（F18 §2「播哪首走哪张图」）。</summary>
static class BgmDiag
{
    public static void Run(WzService wz, string trackKey)
    {
        var catalog = new MusicCatalogService(wz);
        Console.WriteLine($"[bgmdiag] 查询 {trackKey}");
        var sw = Stopwatch.StartNew();
        var first = catalog.GetFirstMapByReturnMap(trackKey);
        if (!string.IsNullOrEmpty(first))
        {
            // 磁盘缓存命中：构造时载入，无需扫描
            Console.WriteLine($"[bgmdiag] GetFirstMapByReturnMap → {first}  （磁盘缓存命中 {sw.Elapsed.TotalSeconds:F1}s）");
            return;
        }
        Console.WriteLine($"[bgmdiag] GetFirstMapByReturnMap → (null)，Kick 触发后台扫描");
        int slash = trackKey.IndexOf('/');
        string img = slash > 0 ? trackKey[..slash] + ".img" : trackKey;
        Console.WriteLine($"[bgmdiag] Kick {img}");
        catalog.KickTrackMapsForImg(img);
        for (int i = 0; i < 90; i++)
        {
            System.Threading.Thread.Sleep(1000);
            var v = catalog.GetFirstMapByReturnMap(trackKey);
            if ((i + 1) % 2 == 0)
            {
                Console.WriteLine($"[bgmdiag] 第 {i + 1}s → {(string.IsNullOrEmpty(v) ? "(null)" : v)}");
            }
            if (!string.IsNullOrEmpty(v))
            {
                Console.WriteLine($"[bgmdiag] 命中! {trackKey} → {v}，总耗时 {sw.Elapsed.TotalSeconds:F1}s");
                return;
            }
        }
        Console.WriteLine($"[bgmdiag] 90s 仍未命中（失败）");
    }
}
