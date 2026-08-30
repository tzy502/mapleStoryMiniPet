using System;
using MiniPet.Services;

namespace MiniPet.Services.Road
{
    /// <summary>
    /// PetFollower —— 人物（桌宠）锁路心跟随器（算法文档 §7 / PLAN Rev 5 D4 段）：
    /// petWorldX = scrollX + 窗宽/2；groundY = 表面函数值；
    /// |dy| ≤ StepThresh(40) 直接贴地；&gt; 40 强制 jump（AnimService jump，JumpMs=300ms 抛物线，
    /// 租约外普通 Play——战斗租约若被打断即作废该次遭遇，与 PLAN「用户/移动优先」一致）；
    /// surfaceY 返回 null 保持上一帧（绝不回退窗口底，F18 硬约束）。
    /// 垂直屏幕映射对齐 D3 渲染契约：screenY = 窗高 62% 锚 + (feetY − initialSurfaceY)。
    /// 窗口移动/缩放每 tick 全量重算（锚定路窗水平中心），无需失效通知。
    /// 线程模型：仅 UI 线程逻辑 tick（EndTick）内调用。
    /// </summary>
    public sealed class PetFollower
    {
        private readonly AnimService _anim;
        private double _feetY;             // 世界表面 y（路坐标系）
        private bool _hasFeet;             // 首帧前无基准（首帧直接贴地不判 jump）
        private bool _jumping;
        private double _jumpT;             // 已跳时长 ms（RoadClock 驱动——路暂停即冻结）
        private double _jumpFromY;
        private double _jumpToY;
        private const double JumpArcHeight = 30.0; // 抛物线上拱峰值 px（视觉弧线）

        public PetFollower(AnimService anim)
        {
            _anim = anim;
        }

        /// <summary>路切换（激活/换歌交换）后重置基准：下一帧直接贴新路表面（不触发 jump）。</summary>
        public void Reset()
        {
            _hasFeet = false;
            _jumping = false;
            _jumpT = 0;
        }

        /// <summary>当前人物脚底世界 y（路坐标系；含跳跃弧线——实体管线 FeetY 同源）。</summary>
        public double CurrentFeetY => _feetY;

        /// <summary>是否已有贴地基准（首帧前 false——实体管线据此回退 SurfaceY 直取）。</summary>
        public bool HasFeet => _hasFeet;

        /// <summary>
        /// 单帧推进（EndTick 内调用）。
        /// </summary>
        /// <param name="dtRoadMs">路时钟 delta（暂停=0：人物动画与跳跃全部冻结）</param>
        /// <param name="road">当前路表面函数提供者（交换后归新路所有）</param>
        /// <param name="winScreenX">路窗口屏幕左上 x</param>
        /// <param name="winScreenY">路窗口屏幕左上 y</param>
        /// <param name="winW">路窗口宽（宠物锁其水平中心）</param>
        /// <param name="winH">路窗口高（62% 垂直锚）</param>
        /// <param name="initialSurfaceY">本路垂直映射基准（RoadRenderRoad.InitialSurfaceY 同源）</param>
        /// <param name="petW">宠物窗口宽（SetPetTopLeft 前取）</param>
        /// <param name="petH">宠物窗口高</param>
        /// <param name="setPetTopLeft">落位回调（屏幕坐标 = 宠物窗口左上；学 PlacePetOnMapGround 的绝对定位写法）</param>
        /// <param name="applyPlacement">是否落位宠物窗口（Mode3 实体渲染 = false：只推进状态不挪窗——人物归路实体管线画）</param>
        public void Tick(
            double dtRoadMs,
            IRoadSurfaceProvider road,
            double winScreenX,
            double winScreenY,
            double winW,
            double winH,
            int initialSurfaceY,
            double petW,
            double petH,
            Action<double, double> setPetTopLeft,
            bool applyPlacement = true)
        {
            if (road == null || setPetTopLeft == null)
            {
                return;
            }
            // 第一步：世界坐标定位（锁路窗水平中心）
            double petWorldX = road.ScrollX + winW / 2;
            double? groundY = road.SurfaceY(petWorldX);

            // 第二步：贴地 / 跳跃状态机（groundY null = 表面无覆盖，保上帧）
            double feetY = _feetY;
            if (_jumping)
            {
                // 跳跃中不贴地：RoadClock 驱动抛物线（暂停 dt=0 → 冻结在弧线上）
                _jumpT += dtRoadMs;
                if (_jumpT >= RoadConstants.JumpMs)
                {
                    _jumping = false;
                    _hasFeet = true;
                    _feetY = _jumpToY;
                    feetY = _jumpToY;
                }
                else
                {
                    double t = _jumpT / RoadConstants.JumpMs;
                    double lerp = _jumpFromY + (_jumpToY - _jumpFromY) * t;
                    // 抛物线上拱（sin 半周期）：上跳/下跳都带弧线，视觉自然
                    feetY = lerp - Math.Sin(Math.PI * t) * JumpArcHeight;
                }
            }
            else if (groundY.HasValue)
            {
                if (!_hasFeet)
                {
                    // 首帧/重置后：直接贴地（无 dy 可判，不触发 jump）
                    _hasFeet = true;
                    _feetY = groundY.Value;
                    feetY = groundY.Value;
                }
                else
                {
                    double dy = groundY.Value - _feetY;
                    if (Math.Abs(dy) <= RoadConstants.StepThresh)
                    {
                        // 平地/小起伏：直接贴
                        _feetY = groundY.Value;
                        feetY = groundY.Value;
                    }
                    else
                    {
                        // 台阶：强制 jump（AnimService jump 租约外普通 Play；300ms 抛物线）
                        _jumping = true;
                        _jumpT = 0;
                        _jumpFromY = _feetY;
                        _jumpToY = groundY.Value;
                        feetY = _jumpFromY;
                        TryPlayJump();
                    }
                }
            }
            // groundY null 且未跳跃：feetY 保持上一帧（_feetY 未动）

            // 第二步（b）：默认 move 行走态（用户 2026-08-28：路在滚=人在走）——
            // 非跳跃且当前动作是 stand* 时切 walk1（纸娃娃）/move（mob）Loop
            if (!_jumping)
            {
                try
                {
                    var cur = _anim.CurrentAnimInfo;
                    string curAction = cur?.Action ?? "";
                    if (curAction.StartsWith("stand", StringComparison.OrdinalIgnoreCase) || curAction.Length == 0)
                    {
                        _anim.Play(IsProviderPaperdoll() ? "walk1" : "move", PlayMode.Loop);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetFollower] walk: {ex.Message}"); }
            }

            // 第三步：世界 → 屏幕映射（62% 锚，对齐 D3 渲染垂直映射）+ 锁窗心落位
            // Mode3 实体渲染（applyPlacement=false）：人物由路实体管线（DrawEntities）绘制——
            // 本方法只推进贴地/跳跃/行走动画状态（供实体快照取 Action/FrameIndex/FeetY），不挪宠物窗口。
            if (!applyPlacement)
            {
                return;
            }
            double feetScreenY = winScreenY + winH * 0.62 + (feetY - initialSurfaceY);
            double petScreenX = winScreenX + winW / 2 - petW / 2;
                        // 脚底对齐：petH 参数 = 窗口顶到纸娃娃可见脚底的偏移（topLeft + petH = 脚底屏幕 y）
            // GroundCalibPx：贴地校准（用户视觉反馈微调用；正=人物上移，负=下移）
            setPetTopLeft(petScreenX, feetScreenY - petH + RoadConstants.GroundCalibPx);
        }

        // ====== private ======

        private bool IsProviderPaperdoll()
        {
            try { return _anim.IsProviderActive; }
            catch { return false; }
        }

        /// <summary>触发 jump 动画（普通 Play：租约外——若战斗租约在途会被打断，战斗方收 OnBroken 作废遭遇）。</summary>
        private void TryPlayJump()
        {
            try
            {
                _anim.Play("jump", PlayMode.OneShot);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PetFollower] JumpPlay: {ex.Message}");
            }
        }
    }
}
