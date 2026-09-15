using System;
using System.Collections.Generic;
using System.IO;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Walk;

/// <summary>F18 行走模式端到端探针：真 WZ 地图驱动 WalkModeService，30fps 干跑 N 秒，打印切图与轨迹。</summary>
static class WalkModeProbe
{
    public static void Run(WzService wz, string trackKey, int seconds, string startMapId)
    {
        var cache = new CacheManager();
        var mapSvc = new MapService(wz, cache);
        var sprite = new SpriteService(cache);
        var pd = new PaperdollService(wz, cache, sprite);
        pd.SetAppearance(new CharacterAppearance
        {
            Gender = 0, BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
        });

        var maps = new List<string>();
        var walk = new WalkModeService(wz, mapSvc, pd, null,
            mapPickerOf: _ => startMapId);
        walk.MapChanged += id => { maps.Add(id); Console.WriteLine($"[walkmode] 切图 → {id}"); };

        walk.Start(trackKey);
        Console.WriteLine($"[walkmode] start track={trackKey} active={walk.IsActive} map={walk.CurrentMapId} phase={walk.Phase} facing={walk.Facing}");

        int steps = Math.Max(1, seconds * 30);
        string lastMap = walk.CurrentMapId;
        int climbs = 0;
        for (int i = 0; i < steps && walk.IsActive; i++)
        {
            walk.Tick(1.0 / 30);
            if (walk.Phase == WalkPhase.Climbing) { climbs++; }
            if (i % 150 == 0)
            {
                Console.WriteLine($"[walkmode] t={i / 30.0,5:F1}s map={walk.CurrentMapId} ({walk.X,7:F0},{walk.Y,7:F0}) act={walk.CurrentAction,-7} phase={walk.Phase} facing={walk.Facing}");
            }
            if (!string.Equals(walk.CurrentMapId, lastMap, StringComparison.Ordinal))
            {
                lastMap = walk.CurrentMapId;
            }
        }
        Console.WriteLine($"[walkmode] 结束 active={walk.IsActive} phase={walk.Phase} 最终 map={walk.CurrentMapId} ({walk.X:F0},{walk.Y:F0})");
        Console.WriteLine($"[walkmode] 切图次数={maps.Count} 地图序列={(maps.Count == 0 ? walk.CurrentMapId : string.Join(" → ", maps))} 爬绳帧数={climbs}");
    }
}
