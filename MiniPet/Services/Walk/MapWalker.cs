using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;

namespace MiniPet.Services.Walk
{
    /// <summary>
    /// 单张地图内的行走 —— 对齐 sdlMS `physic::walk` / `character_logic_system`：
    ///   沿 foothold 的 `prev`/`next` 链走，段内贴线（水平段 k=0，斜坡按两端线性插值），
    ///   x 走出段范围就切相邻段；切不过去则贴边停住；支持梯子/绳索攀爬。
    /// 物理常量取 sdlMS 实测值（水平上限 ±125、加速度 1400、摩擦 800、攀爬 100）。
    /// </summary>
    public sealed class MapWalker
    {
        public const float HSpeedMax = 125f;
        public const float HAccel = 1400f;
        public const float Friction = 800f;
        public const float ClimbSpeed = 100f;

        /// <summary>RopeNearby 的水平判定窗（WalkGraph 建图吸附口径必须与此一致，见 WalkGraph F1a）。</summary>
        /// <summary>绳吸附窗口（2026-09-16 扩大）：63.4% 的绳端悬空超旧 ±15/±20 窗（F1a 收窄防死锁的代价），
        /// 改为「够不到就跳抓」——起跳高度 77px 并入可达包络，窗口 y 向扩到 ±40 覆盖「站得上但要跳」的场景。</summary>
        public const float RopeNearMaxX = 25f;
        /// <summary>RopeNearby 的竖直判定窗。</summary>
        public const float RopeNearMaxY = 40f;

        private readonly MapInfo _map;
        private readonly Dictionary<int, MapFoothold> _fhs;
        private readonly List<LadderRope> _ropes;

        public int FootholdId { get; private set; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float HSpeed { get; private set; }
        /// <summary>当前攀爬的梯子/绳索 id（0 = 踩在地面上）。</summary>
        public int RopeId { get; private set; }
        public bool OnRope => RopeId != 0;
        /// <summary>攀爬方向：-1 向上、+1 向下。</summary>
        public int ClimbDir { get; private set; }

        public MapWalker(MapInfo map, int startFootholdId)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _fhs = new Dictionary<int, MapFoothold>();
            foreach (var f in map.Footholds)
            {
                if (f.Id != 0 && !_fhs.ContainsKey(f.Id)) { _fhs[f.Id] = f; }
            }
            _ropes = map.Ropes ?? new List<LadderRope>();
            if (!_fhs.ContainsKey(startFootholdId)) { throw new ArgumentException($"foothold {startFootholdId} 不存在", nameof(startFootholdId)); }
            FootholdId = startFootholdId;
            var fh = _fhs[startFootholdId];
            X = (fh.X1 + fh.X2) / 2f;
            Y = YOn(fh, X);
        }

        /// <summary>把角色放到指定 foothold 的指定 x（吸附落点）。</summary>
        public void PlaceOn(int footholdId, float x)
        {
            if (!_fhs.TryGetValue(footholdId, out var fh)) { return; }
            FootholdId = footholdId;
            X = Math.Clamp(x, Math.Min(fh.X1, fh.X2), Math.Max(fh.X1, fh.X2));
            Y = YOn(fh, X);
            HSpeed = 0;
            RopeId = 0;
            ClimbDir = 0;
        }

        /// <summary>
        /// 走一步（在地面上）：摩擦 → 加速 → 位移 → 切段/贴边。
        /// </summary>
        /// <param name="dt">秒</param>
        /// <param name="dirInput">-1 左 / 0 停 / +1 右</param>
        /// <returns>是否停在段边界（前方无路）</returns>
        public bool WalkStep(float dt, int dirInput)
        {
            if (OnRope) { return false; }
            // 摩擦（sdlMS physic::walk：反向加速度先减摩擦，再 clamp 到 -hspeed/dt 防反向加速）
            if (dirInput == 0)
            {
                float dec = Friction * dt;
                HSpeed = Math.Abs(HSpeed) <= dec ? 0 : HSpeed - Math.Sign(HSpeed) * dec;
            }
            else
            {
                HSpeed += dirInput * HAccel * dt;
                HSpeed = Math.Clamp(HSpeed, -HSpeedMax, HSpeedMax);
            }
            X += HSpeed * dt;

            // 切段：走出当前段就沿 prev/next 走
            for (int guard = 0; guard < 8; guard++)
            {
                var fh = _fhs[FootholdId];
                float l = Math.Min(fh.X1, fh.X2), r = Math.Max(fh.X1, fh.X2);
                if (X >= l && X <= r) { break; }
                int nextId = X < l ? fh.Prev : fh.Next;
                if (nextId == 0 || !_fhs.TryGetValue(nextId, out var nfh))
                {
                    // 前方无路：贴边停住（sdlMS fall=false 分支）
                    X = X < l ? l + 0.1f : r - 0.1f;
                    HSpeed = 0;
                    Y = YOn(fh, X);
                    return true;
                }
                FootholdId = nextId;
                // 相邻段若在另一侧，说明方向不对 → 贴边停住
                float nl = Math.Min(nfh.X1, nfh.X2), nr = Math.Max(nfh.X1, nfh.X2);
                if (X < nl || X > nr)
                {
                    X = Math.Clamp(X, nl, nr);
                    HSpeed = 0;
                    Y = YOn(nfh, X);
                    return true;
                }
            }
            Y = YOn(_fhs[FootholdId], X);
            return false;
        }

        /// <summary>
        /// 当前位置附近是否可上梯/绳（水平 ±15px 内、且 x 落在绳的 x 上）。
        /// 多根绳同时命中时取 |Δx| 最小者（F2：多绳 x 重叠时不被列表序挡住目标绳）。
        /// </summary>
        public LadderRope? RopeNearby()
        {
            LadderRope? best = null;
            float bestDx = float.MaxValue;
            foreach (var r in _ropes)
            {
                if (Math.Abs(X - r.X) > RopeNearMaxX) { continue; }
                if (Y < r.Top - RopeNearMaxY || Y > r.Bottom + RopeNearMaxY) { continue; }
                float dx = Math.Abs(X - r.X);
                if (dx < bestDx)
                {
                    bestDx = dx;
                    best = r;
                }
            }
            return best;
        }

        /// <summary>进入攀爬（对齐 sdlMS run_climb 的 x 范围 ±15 判定）。</summary>
        public bool EnterRope(int ropeId)
        {
            var rope = _ropes.FirstOrDefault(r => r.Id == ropeId);
            if (rope == null) { return false; }
            RopeId = rope.Id;
            X = rope.X;
            Y = Math.Clamp(Y, rope.Top, rope.Bottom);
            HSpeed = 0;
            ClimbDir = 0;
            return true;
        }

        /// <summary>到达绳端头（WalkModeService 据此转物理下落，替代旧「瞬移吸附最近段」）。</summary>
        public bool JustReachedEnd { get; private set; }

        /// <summary>攀爬一步：沿绳上下，越出端头只钳位并标记（脱离的物理下落由服务层接管）。</summary>
        public void ClimbStep(float dt, int dirInput)
        {
            if (!OnRope) { return; }
            var rope = _ropes.FirstOrDefault(r => r.Id == RopeId);
            if (rope == null) { RopeId = 0; return; }
            JustReachedEnd = false;
            ClimbDir = dirInput;
            Y += dirInput * ClimbSpeed * dt;
            if (Y <= rope.Top) { Y = rope.Top; JustReachedEnd = true; }
            else if (Y >= rope.Bottom) { Y = rope.Bottom; JustReachedEnd = true; }
        }

        /// <summary>脱离绳子：落到 x 最近的地面段上。</summary>
        public void LeaveRope()
        {
            RopeId = 0;
            ClimbDir = 0;
            int best = 0;
            double bestD = double.MaxValue;
            foreach (var fh in _fhs.Values)
            {
                float l = Math.Min(fh.X1, fh.X2), r = Math.Max(fh.X1, fh.X2);
                float px = Math.Clamp(X, l, r);
                double d = Math.Abs(Y - YOn(fh, px)) + Math.Abs(X - px) * 0.5;
                if (d < bestD) { bestD = d; best = fh.Id; }
            }
            if (best != 0)
            {
                var fh = _fhs[best];
                float l = Math.Min(fh.X1, fh.X2), r = Math.Max(fh.X1, fh.X2);
                FootholdId = best;
                X = Math.Clamp(X, l, r);
                Y = YOn(fh, X);
            }
        }

        /// <summary>
        /// 几何干跑（选方向用）：沿 prev/next 链一路走到头，返回累计长度与净位移。
        /// 对齐 sdlMS 的「沿链切段」语义，不含跳跃/爬绳（§3 的模拟行走口径）。
        /// </summary>
        public static (double Len, double Dx, double Dy, int EndFh) Trace(MapInfo map, int startFh, bool forward)
        {
            var fhs = new Dictionary<int, MapFoothold>();
            foreach (var f in map.Footholds) { if (f.Id != 0 && !fhs.ContainsKey(f.Id)) { fhs[f.Id] = f; } }
            if (!fhs.TryGetValue(startFh, out var cur)) { return (0, 0, 0, startFh); }
            var start = cur;
            // 起终点按行进方向取端点：next 方向 X1→X2，prev 方向 X2→X1
            float sx = forward ? start.X1 : start.X2;
            float sy = forward ? start.Y1 : start.Y2;
            double len = 0;
            int guard = 0;
            var seen = new HashSet<int>();
            while (guard++ < 5000 && seen.Add(cur.Id))
            {
                len += Math.Sqrt(Math.Pow(cur.X2 - cur.X1, 2) + Math.Pow(cur.Y2 - cur.Y1, 2));
                int nextId = forward ? cur.Next : cur.Prev;
                if (nextId == 0 || !fhs.TryGetValue(nextId, out var nx)) { break; }
                cur = nx;
            }
            float ex = forward ? cur.X2 : cur.X1;
            float ey = forward ? cur.Y2 : cur.Y1;
            return (len, ex - sx, ey - sy, cur.Id);
        }

        /// <summary>段内插值 y（水平段恒定，斜坡线性）。</summary>
        public static float YOn(MapFoothold fh, float x)
        {
            if (fh.X1 == fh.X2) { return fh.Y1; }
            float t = (x - fh.X1) / (float)(fh.X2 - fh.X1);
            return fh.Y1 + t * (fh.Y2 - fh.Y1);
        }
    }
}
