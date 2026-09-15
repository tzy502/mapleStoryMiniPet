using System;
using MiniPet.Services;

/// <summary>诊断：某个曲目能否反查到地图（F18 §2「播哪首走哪张图」）。</summary>
static class BgmDiag
{
    public static void Run(WzService wz, string trackKey)
    {
        var catalog = new MusicCatalogService(wz);
        Console.WriteLine($"[bgmdiag] 查询 {trackKey}");
        var first = catalog.GetFirstMapByReturnMap(trackKey);
        Console.WriteLine($"[bgmdiag] GetFirstMapByReturnMap → {(string.IsNullOrEmpty(first) ? "(null)" : first)}");
        // 触发扫描后再查
        int slash = trackKey.IndexOf('/');
        string img = slash > 0 ? trackKey[..slash] + ".img" : trackKey;
        Console.WriteLine($"[bgmdiag] Kick {img}");
        catalog.KickTrackMapsForImg(img);
        for (int i = 0; i < 90; i++)
        {
            System.Threading.Thread.Sleep(1000);
            var v = catalog.GetFirstMapByReturnMap(trackKey);
            if ((i + 1) % 10 == 0) { Console.WriteLine($"[bgmdiag] 第 {i + 1}s → {(string.IsNullOrEmpty(v) ? "(null)" : v)}"); }
            if (!string.IsNullOrEmpty(v)) { break; }
        }
    }
}
