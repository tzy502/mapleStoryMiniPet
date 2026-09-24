using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;

namespace MiniPet.Services.Walk
{
    /// <summary>
    /// 行走规划（F18）：
    ///   ① 方向选择 —— 从入口段沿链向两端干跑，比「向下方向」与「向右方向」的**累计长度**，取更长者；
    ///   ② 下一个可传送的传送门筛选 —— 排除入口门，要求 tm 有效且**目标地图的 BGM == 当前曲目**；
    ///      没有 → 返回 null（调用方走「走到尽头一半 → 瞬移回起点 → 反向」的折返分支）。
    /// </summary>
    public static class WalkPathPlanner
    {
        public enum WalkAxis { Down, Right }

        /// <summary>
        /// 方向选择。返回 (Forward, Axis, Length)：Forward=true 表示走 `next` 链的方向。
        /// 「向下」= 净 y 增量更大的一端；「向右」= 净 x 增量更大的一端；两者取累计长度更长者（同长取向下）。
        /// </summary>
        public static (bool Forward, WalkAxis Axis, double Length) PickDirection(MapInfo map, int startFootholdId)
        {
            var a = MapWalker.Trace(map, startFootholdId, true);
            var b = MapWalker.Trace(map, startFootholdId, false);
            // 取累计长度更长的一边；轴向由该边的主要延伸方向决定（|Δx| ≥ |Δy| → 横向=「从左往右」）
            bool forward = a.Len >= b.Len;
            var pick = forward ? a : b;
            var axis = Math.Abs(pick.Dx) >= Math.Abs(pick.Dy) ? WalkAxis.Right : WalkAxis.Down;
            return (forward, axis, pick.Len);
        }

        /// <summary>
        /// 选下一个可传送的传送门。
        /// </summary>
        /// <param name="map">当前地图</param>
        /// <param name="entrancePortalId">入口传送门 id（本次进来的那个，排除）</param>
        /// <param name="currentTrackKey">当前曲目 key，形如 "Bgm00/FloralLife"</param>
        /// <param name="mapBgmOf">mapId → 该地图的 info/bgm 原始值（注入，避免 Walk 层依赖 WZ）</param>
        /// <param name="visited">
        /// 贪心遍历（胶水 2026-09-15）：本次行走**已走过的图**集合。非 null 时优先选「目标图还没去过」的
        /// 候选门（存储顺序内第一个）；所有候选目标都去过 → 返回 null（调用方按 noNext 走死胡同退图链路，
        /// 回初始地图后清空集合重新遍历）。null = 不做贪心（老行为：存储顺序第一个匹配门）。
        /// </param>
        /// <param name="currentX">
        /// 人物当前 x（贪心「从远到近」，胶水 2026-09-15）：多个未访问候选时取**水平距离最远**的门；
        /// NaN = 无坐标，退化为存储顺序第一个未访问。
        /// </param>
        public static MapPortal? ChooseNextPortal(MapInfo map, int entrancePortalId, string currentTrackKey,
            Func<string, string> mapBgmOf, HashSet<string>? visited = null, float currentX = float.NaN)
        {
            if (map == null || string.IsNullOrEmpty(currentTrackKey)) { return null; }
            MapPortal? firstMatch = null;
            MapPortal? farthestUnvisited = null;
            foreach (var p in map.Portals)
            {
                if (p.Id == entrancePortalId) { continue; }
                if (!p.HasTarget) { continue; }
                // 同图跳层门（tm=自己，如 100000203 爬塔图的 up01~up07/h001/h002）**不是出路**：
                // 它的 BGM 必然与当前曲一致（同一张图），会抢占候选位，但同图不切规则永远挡住切图
                // → 人物被图死（实测 100000203 被困爬塔 90s 出不来；out00 才是真正的出口）。
                if (string.Equals(p.Tm, map.Id, StringComparison.Ordinal)) { continue; }
                string? key = TrackKeyOf(mapBgmOf(p.Tm));
                if (key == null || !string.Equals(key, currentTrackKey, StringComparison.OrdinalIgnoreCase)) { continue; }

                if (firstMatch == null) { firstMatch = p; }
                if (visited != null && !visited.Contains(p.Tm))
                {
                    // 贪心「从远到近」：未访问候选里取离当前 x 最远的门
                    if (farthestUnvisited == null
                        || (Math.Abs(p.X - currentX) > Math.Abs(farthestUnvisited.X - currentX)))
                    {
                        farthestUnvisited = p;
                    }
                }
            }
            if (visited != null && farthestUnvisited != null)
            {
                return farthestUnvisited;
            }
            if (visited != null && firstMatch != null)
            {
                // 匹配门存在但目标全去过 → 视作「没有下一个可去的门」（返回 null 触发退图回初始地图）
                return null;
            }
            return firstMatch;
        }

        /// <summary>
        /// 贪心候选序列：全部「目标 BGM == 当前曲」的门（排除入口/同图），排序 =
        /// 未访问按「离 currentX 最远」优先，其次已访问按存储序；visited=null 退化为存储序。
        /// 供 SetupLeg 逐个尝试 Plan——排头的不可达（如 100000000 的 in04 高台门落差 390px 无过渡段）
        /// 时换下一个，而不是整图放弃。
        /// </summary>
        public static List<MapPortal> OrderCandidates(MapInfo map, int entrancePortalId, string currentTrackKey,
            Func<string, string> mapBgmOf, HashSet<string>? visited = null, float currentX = float.NaN)
        {
            var list = new List<MapPortal>();
            if (map == null || string.IsNullOrEmpty(currentTrackKey)) { return list; }
            var all = new List<MapPortal>();
            foreach (var p in map.Portals)
            {
                if (p.Id == entrancePortalId) { continue; }
                if (!p.HasTarget) { continue; }
                if (string.Equals(p.Tm, map.Id, StringComparison.Ordinal)) { continue; }
                string? key = TrackKeyOf(mapBgmOf(p.Tm));
                if (key == null || !string.Equals(key, currentTrackKey, StringComparison.OrdinalIgnoreCase)) { continue; }
                all.Add(p);
            }
            // 入口门排除放宽（100000001 实测）：唯一合格门恰是入口门时，排除后候选=0 → 死循环不出去。
            // 此时保留入口门作为候选（走回它 = 原路返回，合法遍历动作）。
            list = all;
            if (list.Count == 0)
            {
                foreach (var p in map.Portals)
                {
                    if (!p.HasTarget) { continue; }
                    if (string.Equals(p.Tm, map.Id, StringComparison.Ordinal)) { continue; }
                    string? key = TrackKeyOf(mapBgmOf(p.Tm));
                    if (key != null && string.Equals(key, currentTrackKey, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(p);
                    }
                }
            }
            if (visited != null)
            {
                // 「从远到近」（胶水 2026-09-16 更正口径）：未访问候选按**人物与门的距离**（|门x − 人物x|）
                // 降序——最远的门优先，跟着传送门走。currentX 由调用方传人物当前 x。
                float cx = float.IsNaN(currentX) ? 0f : currentX;
                list.Sort((a, b) =>
                {
                    bool aNew = !visited.Contains(a.Tm), bNew = !visited.Contains(b.Tm);
                    if (aNew != bNew) { return aNew ? -1 : 1; }
                    if (aNew && bNew)
                    {
                        float da = Math.Abs(a.X - cx), db = Math.Abs(b.X - cx);
                        if (Math.Abs(da - db) > 0.5f) { return db > da ? 1 : -1; }   // 最远的门优先
                    }
                    return 0;   // 已访问保持存储序
                });
            }
            return list;
        }

        /// <summary>
        /// 该地图是否「没有下一个可传送的传送门」（胶水定义）：
        ///   全图只有一个传送门，或除入口门外没有任何可跨图的门 / 没有一个目标 BGM 与当前一致 /
        ///   （贪心模式，visited 非 null）所有匹配门的目标图本次都已走过。
        /// </summary>
        public static bool HasNoNextPortal(MapInfo map, int entrancePortalId, string currentTrackKey,
            Func<string, string> mapBgmOf, HashSet<string>? visited = null, float currentX = float.NaN)
        {
            if (map == null) { return true; }
            if (map.Portals.Count <= 1) { return true; }
            return ChooseNextPortal(map, entrancePortalId, currentTrackKey, mapBgmOf, visited, currentX) == null;
        }

        /// <summary>BGM 原始值 → 曲目 key（"Bgm00/FloralLife"，去 .img；静音轨返回 null）。</summary>
        public static string? TrackKeyOf(string? bgm)
        {
            var r = MusicDecisions.NormalizeBgm(bgm);
            if (r == null) { return null; }
            string img = r.Img.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? r.Img[..^4] : r.Img;
            return $"{img}/{r.Track}";
        }
    }
}
