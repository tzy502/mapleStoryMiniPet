using System;
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
        public static MapPortal? ChooseNextPortal(MapInfo map, int entrancePortalId, string currentTrackKey,
            Func<string, string> mapBgmOf)
        {
            if (map == null || string.IsNullOrEmpty(currentTrackKey)) { return null; }
            foreach (var p in map.Portals)
            {
                if (p.Id == entrancePortalId) { continue; }
                if (!p.HasTarget) { continue; }
                string? key = TrackKeyOf(mapBgmOf(p.Tm));
                if (key != null && string.Equals(key, currentTrackKey, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>
        /// 该地图是否「没有下一个可传送的传送门」（胶水定义）：
        ///   全图只有一个传送门，或除入口门外没有任何可跨图的门 / 没有一个目标 BGM 与当前一致。
        /// </summary>
        public static bool HasNoNextPortal(MapInfo map, int entrancePortalId, string currentTrackKey,
            Func<string, string> mapBgmOf)
        {
            if (map == null) { return true; }
            if (map.Portals.Count <= 1) { return true; }
            return ChooseNextPortal(map, entrancePortalId, currentTrackKey, mapBgmOf) == null;
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
