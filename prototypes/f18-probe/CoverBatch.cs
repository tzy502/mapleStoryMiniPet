using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Walk;

/// <summary>
/// F18 路径覆盖率批量测量（--cover-batch &lt;listFile&gt; &lt;outCsv&gt; [sec] [speed] [label]）：
/// 每图一行 CSV：mapId,totalFh,reachableFh,walkedFh,coverage,homeWalkSec,leftHome,mapsVisited,entryFh,note
///   totalFh     = 图内 Id!=0 的 foothold 段数（WalkGraph 节点同口径，垂直段由建图另行排除）
///   reachableFh = WalkGraph.Reachable(map, 入口段).Count；入口段 = PickEntryPortal（可跨图门中最左，
///                 与 WalkModeService.Start(arrival:null) 同口径）/ 无门退地图中点 → NearestFoothold 吸附
///   walkedFh    = 仿真 sec 仿真秒内「初始图」上实际踩过的不同段数：逐 tick（1/30 仿真秒固定步长，
///                 保证短段不被整跳跨过）采样 CurrentFootholdId + 跳完成（hopIndex 前进）补记计划段；
///                 漫游到其它图期间的段归属其它图，不计入初始图
///   coverage    = walkedFh / reachableFh（reachableFh=0 → 空）
///   homeWalkSec = 仿真时间中停留在初始图上的秒数（漫游早退图用它在汇总里甄别）
/// 低覆盖图（reachFh&gt;5 且 coverage&lt;0.5）把该图仿真日志存 cover-logs/low_{id}.log 便于归因。
/// </summary>
static class CoverBatch
{
    /// <summary>封顶的内存日志：正常图丢弃，低覆盖图才落盘。</summary>
    private sealed class CappedWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly int _cap;
        public CappedWriter(int cap) { _cap = cap; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { if (_sb.Length < _cap) { _sb.Append(value); } }
        public override void Write(string? value) { if (value != null && _sb.Length < _cap) { _sb.Append(value); } }
        public override void WriteLine() { Write(Environment.NewLine); }
        public string Text => _sb.ToString();
    }

    public static void Run(WzService wz, string listFile, string outCsv, int seconds, double speed, string label = "")
    {
        string fullCsv = Path.GetFullPath(outCsv);
        Directory.CreateDirectory(Path.GetDirectoryName(fullCsv)!);
        string logsDir = Path.Combine(Path.GetDirectoryName(fullCsv)!, "cover-logs");
        var ids = new List<(string mapId, string track)>();
        foreach (var raw in File.ReadAllLines(listFile))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("#", StringComparison.Ordinal)) { continue; }
            var parts = l.Split('\t');
            if (parts.Length >= 2) { ids.Add((parts[^1].Trim(), parts[0].Trim())); }
            else
            {
                // 无 track 关联的图：用图自身 info/bgm 的 TrackKey 作为遍历上下文（与 --walkmode-batch 同口径）
                string bgm = ReadMapBgm(wz, l);
                string? tk = WalkPathPlanner.TrackKeyOf(bgm);
                ids.Add((l, tk ?? ""));
            }
        }
        Console.WriteLine($"[{label}cover] 共 {ids.Count} 张图, sec={seconds}, speed={speed}(采样步长固定 1/30s), out={fullCsv}");

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

        int done = 0, fail = 0, noFh = 0, loadFail = 0, lowCnt = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var csv = new StreamWriter(fullCsv, append: false);
        csv.WriteLine("mapId,totalFh,reachableFh,walkedFh,coverage,homeWalkSec,leftHome,mapsVisited,entryFh,rawWalked,note,ropesTotal,ropesClimbed");
        foreach (var (id, track) in ids)
        {
            string note = "ok";
            string csvLine;
            string? lowLog = null;
            var log = new CappedWriter(256 * 1024);
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            try
            {
                Console.SetOut(log);
                Console.SetError(log);
                var r = MeasureOne(wz, mapSvc, pd, id, track, seconds, speed);
                if (r == null)
                {
                    note = "load-fail";
                    loadFail++;
                    csvLine = $"{id},0,0,0,,0,0,0,0,0,load-fail";
                }
                else
                {
                    var st = r!;
                    if (st.TotalFh == 0) { note = "no-fh"; noFh++; }
                    double cov = st.ReachFh > 0 ? (double)st.WalkedFh / st.ReachFh : 0;
                    csvLine = $"{id},{st.TotalFh},{st.ReachFh},{st.WalkedFh},{cov:F4},{st.HomeSec:F1},{(st.LeftHome ? 1 : 0)},{st.MapsVis},{st.EntryFh},{st.RawWalked},{note},{st.RopesTotal},{st.RopesClimbed}";
                    if (st.ReachFh > 5 && st.WalkedFh < 0.5 * st.ReachFh)
                    {
                        lowCnt++;
                        lowLog = log.Text;
                    }
                }
            }
            catch (Exception ex)
            {
                fail++;
                csvLine = $"{id},0,0,0,,0,0,0,0,0,exception:{ex.GetType().Name}";
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
            csv.WriteLine(csvLine);
            if (lowLog != null)
            {
                Directory.CreateDirectory(logsDir);
                File.WriteAllText(Path.Combine(logsDir, $"low_{id}.log"), $"==== MAP {id} track={track} ====\n{lowLog}");
            }
            done++;
            if (done % 20 == 0 || done == ids.Count)
            {
                double etaMin = sw.Elapsed.TotalMinutes / done * (ids.Count - done);
                Console.WriteLine($"[{label}cover] 进度 {done}/{ids.Count} 失败={fail} 无段={noFh} 载入失败={loadFail} 低覆盖={lowCnt} 用时={sw.Elapsed.TotalMinutes:F1}min ETA={etaMin:F1}min");
            }
        }
        Console.WriteLine($"[{label}cover] 完成 {done}/{ids.Count} 失败={fail} 无段={noFh} 载入失败={loadFail} 低覆盖={lowCnt} 总用时={sw.Elapsed.TotalMinutes:F1}min");
    }

    private sealed record MapStat(int TotalFh, int ReachFh, int WalkedFh, double HomeSec, bool LeftHome, int MapsVis, int EntryFh, int RawWalked, int RopesTotal = 0, int RopesClimbed = 0);

    /// <summary>单图覆盖率测量：入口吸附 → 可达集 → 仿真采样。</summary>
    private static MapStat? MeasureOne(WzService wz, MapService mapSvc, PaperdollService pd, string mapId, string track, int seconds, double speed)
    {
        var map = mapSvc.LoadMap(mapId);
        if (map == null) { return null; }
        // 胶水口径（2026-09-16）：分母 = 地图实际路径 = **非垂直 fd 段 + 绳**（垂直段=墙，排除）；
        // 分子 = 踩过的 fd + 爬过的绳。剩下走不到的应该只占 <10% 且都是墙。
        int totalFh = 0;
        foreach (var f in map.Footholds) { if (f.Id != 0 && Math.Abs(f.X1 - f.X2) >= 0.5f) { totalFh++; } }
        int ropesTotal = map.Ropes?.Count(r => r.Id < 100000) ?? 0;
        if (totalFh == 0 && ropesTotal == 0) { return new MapStat(0, 0, 0, 0, false, 0, 0, 0, 0, 0); }

        // 入口段：与 WalkModeService.BeginMap(arrival:null) 同口径
        var portal = PickEntryPortal(map);
        float px, py;
        if (portal != null) { px = portal.X; py = portal.Y; }
        else { var (cx, cy) = MapService.GetMapCenter(map); px = cx; py = cy; }
        var entry = NearestFoothold(map, px, py);
        int entryFh = entry?.Id ?? 0;
        int reachFh = 0;
        var reachSet = entry != null ? WalkGraph.Reachable(map, entry.Id) : new HashSet<int>();
        reachFh = reachSet.Count;

        // 仿真 + 采样
        var walk = new WalkModeService(wz, mapSvc, pd, null, mapPickerOf: _ => mapId);
        var walked = new HashSet<int>();
        var climbedRopes = new HashSet<int>();
        int mapsVis = 0;
        walk.MapChanged += _ => { mapsVis++; };
        walk.Start(track);
        if (entryFh != 0) { walked.Add(entryFh); }
        int steps = Math.Max(1, seconds * 30);   // 30 tick / 仿真秒（dt=1/30 固定，采样粒度决定段保真度）
        double dt = 1.0 / 30.0;
        double homeSec = 0;
        bool leftHome = false;
        int lastHop = walk.CurrentHopIndex;
        int lastPathLen = walk.PathLength;
        for (int i = 0; i < steps && walk.IsActive; i++)
        {
            walk.Tick(dt);
            if (!string.Equals(walk.CurrentMapId, mapId, StringComparison.Ordinal))
            {
                leftHome = true;    // 漫游到其它图：本图统计冻结
                lastHop = walk.CurrentHopIndex;
                lastPathLen = walk.PathLength;
                continue;
            }
            homeSec += dt;
            int fh = walk.CurrentFootholdId;
            if (fh != 0) { walked.Add(fh); }
            // 爬绳记录：act 为 ladder/rope 时按位置匹配最近绳（±25）
            var curAct = walk.CurrentAction;
            if (curAct == "ladder" || curAct == "rope")
            {
                foreach (var r in map.Ropes ?? new List<LadderRope>())
                {
                    if (Math.Abs(walk.X - r.X) <= 25) { climbedRopes.Add(r.Id); break; }
                }
            }
            // 跳完成补记：hopIndex 前进说明 [lastHop, hi) 这些计划段确实走到了（防短段被整跳跨过）。
            // 计划长度变了（中途重规划/换腿）→ 索引语义失效，跳过补记只信位置采样。
            int hi = walk.CurrentHopIndex;
            int plen = walk.PathLength;
            if (hi > lastHop && plen == lastPathLen)
            {
                var hops = walk.PlannedHops;
                for (int j = Math.Max(0, lastHop); j < hi && j < hops.Count; j++)
                {
                    if (hops[j].FootholdId != 0) { walked.Add(hops[j].FootholdId); }
                }
            }
            lastHop = hi;
            lastPathLen = plen;
        }
        // 分子口径 = walked ∩ reachable：执行层能踩到图模型排除的段（如垂直墙段），
        // 不相交会算出 coverage>1；原始踩段数另存 rawWalked 列备查
        int rawWalked = walked.Count;
        walked.IntersectWith(reachSet);
        return new MapStat(totalFh, reachFh, walked.Count, homeSec, leftHome, mapsVis, entryFh, rawWalked, ropesTotal, climbedRopes.Count);
    }

    /// <summary>读图 info/bgm 原始值（与 WalkModeProbe.ReadMapBgm 同路径规则）。</summary>
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

    // ===== 入口吸附（语义与 WalkModeService 私有实现一致，勿改口径）=====

    private static MapPortal? PickEntryPortal(MapInfo map)
    {
        MapPortal? best = null;
        foreach (var p in map.Portals)
        {
            if (!p.HasTarget) { continue; }
            if (best == null || p.X < best.X || (p.X == best.X && p.Y < best.Y)) { best = p; }
        }
        if (best != null) { return best; }
        foreach (var p in map.Portals)
        {
            if (best == null || p.X < best.X) { best = p; }
        }
        return best;
    }

    private static double DistanceToSegment(float px, float py, MapFoothold f)
    {
        double x1 = f.X1, y1 = f.Y1, x2 = f.X2, y2 = f.Y2;
        double dx = x2 - x1, dy = y2 - y1;
        double len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : ((px - x1) * dx + (py - y1) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = x1 + t * dx, cy = y1 + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static MapFoothold? NearestFoothold(MapInfo map, float px, float py)
    {
        MapFoothold? best = null;
        double bestD = double.MaxValue;
        foreach (var f in map.Footholds)
        {
            if (f.Id == 0) { continue; }
            double d = DistanceToSegment(px, py, f);
            if (d < bestD)
            {
                bestD = d;
                best = f;
            }
        }
        return best;
    }
}
