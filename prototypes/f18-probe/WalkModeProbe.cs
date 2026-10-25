using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Walk;

/// <summary>F18 行走模式端到端探针：真 WZ 地图驱动 WalkModeService，30fps 干跑 N 秒，打印切图与轨迹。</summary>
static class WalkModeProbe
{
    public static void Run(WzService wz, string trackKey, int seconds, string startMapId, double speed = 1.0, TextWriter? writer = null)
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
        RunWith(wz, mapSvc, pd, trackKey, seconds, startMapId, speed, writer);
    }

    /// <summary>读图 info/bgm 原始值（与 WalkModeService.ReadMapBgmFromWz 同路径规则）。</summary>
    private static string ReadMapBgm(WzService wz, string mapId)
    {
        try
        {
            var s = mapId.Trim();
            var padded = s.Length >= 9 || !s.All(char.IsDigit) ? s : s.PadLeft(9, '0');
            return wz.WithWzLock(() => wz.GetStringProperty($"{MapService.GetMapWzPath(padded)}/info/bgm") ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    /// <summary>共享 WZ/地图/角色服务的单图执行（批量模式复用，省去每图重建开销）。</summary>
    public static void RunWith(WzService wz, MapService mapSvc, PaperdollService pd, string trackKey, int seconds, string startMapId, double speed, TextWriter? writer)
    {
        var output = writer ?? Console.Out;

        var maps = new List<string>();
        var walk = new WalkModeService(wz, mapSvc, pd, null,
            mapPickerOf: _ => startMapId);
        walk.MapChanged += id => { maps.Add(id); output.WriteLine($"[walkmode] 切图 → {id}"); };

        walk.Start(trackKey);
        output.WriteLine($"[walkmode] start track={trackKey} active={walk.IsActive} map={walk.CurrentMapId} phase={walk.Phase} facing={walk.Facing} speed={speed}");

        // 诊断「向后跳/来回锯齿」：每次 hop 推进打印该跳的目标段与 link
        var loadedMaps = new Dictionary<string, MapInfo>();
        MapInfo MapOf(string id)
        {
            if (!loadedMaps.TryGetValue(id, out var m)) { loadedMaps[id] = m = mapSvc.LoadMap(id)!; }
            return m!;
        }
        int lastHop = -1;
        string hopMap = walk.CurrentMapId;

        // 倍速下每 tick 推进 speed/30 仿真秒，跑 seconds 仿真秒需要 seconds*30/speed 个 tick
        int steps = Math.Max(1, (int)Math.Round(seconds * 30.0 / speed));
        string lastMap = walk.CurrentMapId;
        int climbs = 0;
        double simT = 0.0;          // 仿真时间（秒）
        double dt = speed / 30.0;   // 每 tick 推进的仿真秒（Tick 内部按 1/60 拆子步，上限 1.0s）
        double lastSample = -1e9;
        for (int i = 0; i < steps && walk.IsActive; i++)
        {
            walk.Tick(dt);
            simT += dt;
            if (walk.Phase == WalkPhase.Climbing) { climbs++; }
            if (walk.CurrentHopIndex != lastHop || !string.Equals(walk.CurrentMapId, hopMap, StringComparison.Ordinal))
            {
                lastHop = walk.CurrentHopIndex;
                hopMap = walk.CurrentMapId;
                var hops = walk.PlannedHops;
                if (lastHop >= 0 && lastHop < hops.Count)
                {
                    var h = hops[lastHop];
                    var fh = MapOf(hopMap).Footholds.FirstOrDefault(f => f.Id == h.FootholdId);
                    output.WriteLine($"[hop] t={simT,6:F1}s hop={lastHop}/{hops.Count} link={h.Link} → fh#{h.FootholdId} ({fh?.X1},{fh?.Y1})-({fh?.X2},{fh?.Y2}) from=({walk.X:F0},{walk.Y:F0})");
                }
            }
            if (simT - lastSample >= 1.0 - 1e-9)
            {
                lastSample = simT;
                var hops = walk.PlannedHops;
                string hopDesc = "";
                int hi = walk.CurrentHopIndex;
                if (hi >= 0 && hi < hops.Count)
                {
                    var h = hops[hi];
                    var mi = MapOf(walk.CurrentMapId);
                    var fh = mi.Footholds.FirstOrDefault(f => f.Id == h.FootholdId);
                    var rope = h.RopeId > 0 ? mi.Ropes.FirstOrDefault(r => r.Id == h.RopeId) : null;
                    hopDesc = $" hop[{hi}]=link={h.Link} →fh#{h.FootholdId}({fh?.X1},{fh?.Y1})-({fh?.X2},{fh?.Y2}) rope={(rope != null ? $"x={rope.X},{rope.Top}~{rope.Bottom}" : h.RopeId.ToString())}";
                }
                output.WriteLine($"[walkmode] t={simT,6:F1}s map={walk.CurrentMapId} ({walk.X,7:F0},{walk.Y,7:F0}) act={walk.CurrentAction,-7} phase={walk.Phase} facing={walk.Facing} fh={walk.CurrentFootholdId}{hopDesc}");
            }
            if (!string.Equals(walk.CurrentMapId, lastMap, StringComparison.Ordinal))
            {
                lastMap = walk.CurrentMapId;
            }
        }
        output.WriteLine($"[walkmode] 结束 active={walk.IsActive} phase={walk.Phase} 最终 map={walk.CurrentMapId} ({walk.X:F0},{walk.Y:F0}) simT={simT:F1}s");
        output.WriteLine($"[walkmode] 切图次数={maps.Count} 地图序列={(maps.Count == 0 ? walk.CurrentMapId : string.Join(" → ", maps))} 爬绳帧数={climbs}");
    }

    /// <summary>批量模式：listFile 每行一个 mapId，逐图跑 N 秒仿真（speed 倍速），每图日志独立文件，进程内复用 WZ 与角色服务。</summary>
    public static void BatchRun(WzService wz, string listFile, string outDir, int seconds, double speed)
    {
        Directory.CreateDirectory(outDir);
        var ids = new List<(string mapId, string track)>();
        foreach (var raw in File.ReadAllLines(listFile))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("#", StringComparison.Ordinal)) { continue; }
            var parts = l.Split('\t');
            if (parts.Length >= 2) { ids.Add((parts[^1].Trim(), parts[0].Trim())); }
            else
            {
                // 无 track 关联的图：用图自身 info/bgm 的 TrackKey 作为遍历上下文（近似真实候选集）
                string bgm = ReadMapBgm(wz, l);
                string? tk = WalkPathPlanner.TrackKeyOf(bgm);
                ids.Add((l, tk ?? ""));
            }
        }
        Console.WriteLine($"[batch] 共 {ids.Count} 张图, speed={speed}, seconds={seconds}, out={outDir}");
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
        int done = 0, fail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (id, track) in ids)
        {
            var logPath = Path.Combine(outDir, $"walk_{id}.log");
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            try
            {
                using (var w = new StreamWriter(logPath) { AutoFlush = false })
                {
                    // WalkModeService 的 [setup]/死胡同 行走 Console.Out/Error，逐图重定向才能归档到该图日志
                    Console.SetOut(w);
                    Console.SetError(w);
                    w.WriteLine($"==== MAP {id} track={track} speed={speed} ====");
                    RunWith(wz, mapSvc, pd, track, seconds, id, speed, w);
                }
            }
            catch (Exception ex)
            {
                fail++;
                File.WriteAllText(logPath, $"==== MAP {id} ====\n[batch] EXCEPTION {ex.GetType().Name}: {ex.Message}\n");
                Console.Out.WriteLine($"[batch] {id} 异常: {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
            done++;
            if (done % 25 == 0)
            {
                Console.WriteLine($"[batch] 进度 {done}/{ids.Count} 失败={fail} 用时={sw.Elapsed.TotalMinutes:F1}min");
            }
        }
        Console.WriteLine($"[batch] 完成 {done}/{ids.Count} 失败={fail} 总用时={sw.Elapsed.TotalMinutes:F1}min");
    }
}
