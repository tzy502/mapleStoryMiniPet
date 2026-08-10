using System;
using Avalonia.Threading;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// Agent 状态特效层（PLAN-agent-integration.md §2 通用特效层）。
    ///
    /// 订阅 EventBus.PetReflectStateChanged（AgentStateAggregator 计算后的桌宠反映态），
    /// 把 AgentState 翻译成可视反馈，按 PLAN §2 表分类：
    ///   - Jump    → WZ 庆祝动画（节点未定，TODO 占位：暂用「搞定!」气泡）
    ///   - Failed  → emoji 弹窗（冒汗/晕），复用 UI/Basic 符号（路径未定，TODO 占位：暂用「出错了…」气泡）
    ///   - Wave    → 气泡「完成」
    ///   - Review  → 气泡「思考中…」
    ///   - Waiting → 气泡「等你」
    ///   - Run     → 调 PaperdollService 预留 expression gateway 触发实体忙碌动作（walk1/move）；gateway 未实现则 TODO
    ///   - Idle    → 不动（回默认表情 default/blink）
    ///
    /// 设计取舍（选简单可行的）：
    /// overlay 只负责 Jump/Failed 这种「实体之上的叠加」与「气泡固定文本」；
    /// Run/Idle 这种「实体现有动作切换」交给 PaperdollService.ExpressionDriver，本服务只翻状态不直接画帧。
    /// 不替换实体帧、不切实体（Mob/NPC/纸娃娃通用）。
    /// 当前 overlay 落地为「调 BalloonService 显示固定文本气泡」——BalloonService 已是独立透明置顶窗口
    /// （见 BalloonWindow：Topmost + Transparent + NSWindow.ignoresMouseEvents 点击穿透），
    /// 复用它即等于在桌宠实体上方叠了一层 overlay，无需再造第二个透明窗口。
    /// 未来若需 WZ 庆祝动画/emoji 图像叠加（非文字气泡），再在此类内挂独立 EffectOverlayWindow（参考 BalloonWindow 做法）。
    /// </summary>
    public class EffectLayerService
    {
        private readonly EventBus _eventBus;
        private readonly BalloonService _balloonService;
        private readonly ConfigService _configService;
        private readonly PaperdollService? _paperdoll;

        // 当前生效态，避免重复触发同一反馈（例如 Run 期间连续 OnReflect 只触发一次 busy 表情）
        private AgentState _currentReflect = AgentState.Idle;

        /// <summary>
        /// 实体动作驱动（Agent Run 切忙碌动作；PLAN §2 expression gateway 扩展）。
        /// 不进构造（装配 agent 并行按现有 public 构造签名装配，避免冲突），
        /// 由装配方（PetWindow / App）在构造后注入 AnimService 实例。null 时 Run 仅记日志不抛。
        /// </summary>
        internal IActionDriver? ActionDriver { get; set; }

        /// <summary>
        /// 构造注入：EventBus 必需；BalloonService/ConfigService 必需（Jump/Failed/Wave/Review/Waiting 走气泡）；
        /// PaperdollService 可选（Run/Idle 走 expression gateway，未注入则 Run 仅记 TODO 不抛）。
        /// </summary>
        public EffectLayerService(
            EventBus eventBus,
            BalloonService balloonService,
            ConfigService configService,
            PaperdollService? paperdoll = null)
        {
            _eventBus = eventBus;
            _balloonService = balloonService;
            _configService = configService;
            _paperdoll = paperdoll;

            _eventBus.Subscribe(EventType.PetReflectStateChanged, OnReflect);
        }

        /// <summary>
        /// PetReflectStateChanged 处理。
        /// Aggregator 已在 UI 线程 Publish（DispatcherTimer 内调用），但 adapter 若直接 Publish 此事件
        /// 或未来调用方变更，仍需线程安全：非 UI 线程则 Post 到 UI 线程再处理（所有反馈副作用都在 UI 线程跑）。
        /// </summary>
        private void OnReflect(object? data)
        {
            if (data is not AgentState state)
            {
                return;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnReflect(data));
                return;
            }

            if (state == _currentReflect)
            {
                // 同态不重复触发（避免 Run 期间重复刷气泡 / 重复设表情）
                return;
            }

            var prev = _currentReflect;
            _currentReflect = state;
            ApplyEffect(state, prev);
        }

        /// <summary>
        /// 状态→反馈映射（PLAN §2 表）。每分支独立 try/catch，单态失败不影响其它态。
        /// </summary>
        private void ApplyEffect(AgentState state, AgentState prev)
        {
            try
            {
                switch (state)
                {
                    case AgentState.Jump:
                        // TODO(WZ 庆祝动画)：用户确认 WZ 内有现成「庆祝成功」动画。
                        // 实现时用 WzService 定位候选节点：
                        //   Effect/、UI/Basic（emotion/符号）、Item/ 等，FindNodeByPath 后 ExtractPng 取帧序列。
                        // 找不到则降级 UI/Basic emotion + 气泡。当前先用占位：显「搞定!」气泡。
                        ShowBalloon("搞定!");
                        break;

                    case AgentState.Failed:
                        // TODO(WZ emoji 弹窗)：复用 UI/Basic 符号（冒汗/晕/流泪）做 emoji 弹窗。
                        // 路径同 Jump 待确认（UI/Basic/...）。当前先用占位：显「出错了…」气泡。
                        ShowBalloon("出错了…");
                        break;

                    case AgentState.Wave:
                        ShowBalloon("完成");
                        break;

                    case AgentState.Review:
                        ShowBalloon("思考中…");
                        break;

                    case AgentState.Waiting:
                        // + 问号：当前用文本「等你?」承载；完整问号 emoji 待 UI/Basic 符号落地。
                        ShowBalloon("等你?");
                        break;

                    case AgentState.Run:
                        // Run → 实体切忙碌动作（PLAN §2）+「干活中」气泡。
                        // 退化兜底（极端：实体只有一个动作）：NPC/Mob 仅 stand 无 move/walk1 时，
                        //   PlayAction 经 AnimService fallback 链回 stand（动作不变）→ 气泡「干活中」是唯一可视反馈。
                        //   原则：动作是增强、气泡是兜底，任何情况都至少有气泡反馈（零动画也不丢状态提示）。
                        // IActionDriver.PlayAction 内部按当前实体类型路由：
                        //   纸娃娃 → walk1（AnimService 别名 move→walk1）；Mob → move；NPC → move 或 fallback stand。
                        // 统一传语义动作 "move"，实体差异在 driver 内消化。
                        // TODO(飞行怪): Mob 仅有 fly 无 move 时应 PlayAction("fly")（AnimService fallback 链待加 move→fly）。
                        if (ActionDriver != null)
                        {
                            ActionDriver.PlayAction("move");
                        }
                        else
                        {
                            Console.WriteLine("[EffectLayerService] Run → ActionDriver 未注入，实体动作未切");
                        }
                        // 气泡兜底：无论实体动作是否切成功，Run 都显一次「干活中」
                        // （同态去重 _currentReflect，持续期间只触发一次；气泡自带淡出）
                        ShowBalloon("干活中");
                        TryDriveBusyExpression();
                        break;

                    case AgentState.Idle:
                        // Run→Idle：实体动作回 stand（纸娃娃解析为 stand1，Mob/NPC 回 stand 图条）。
                        // 仅当上一态是 Run 才需要 RestoreDefault（避免从 Jump/Wave 回 Idle 误切走 say 等表情）
                        // —— 实际上 RestoreDefault 只切 stand，对任何前置态都安全（OneShot 播完本就回 stand）。
                        if (ActionDriver != null && prev == AgentState.Run)
                        {
                            ActionDriver.RestoreDefault();
                        }
                        // 不主动清气泡（BalloonService 自带淡出计时）。
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EffectLayerService] ApplyEffect({state}): {ex.Message}");
            }
        }

        /// <summary>
        /// 走 BalloonService 显示固定文本气泡（overlay 主体）。
        /// balloonId 取用户配置，缺省回退 "560"（与 PetWindow.ShowBalloonNow 一致）。
        /// BalloonService.Show 内部后台渲染 → OnBalloonReady 回调由上层（PetWindow）在 UI 线程显示。
        /// </summary>
        private void ShowBalloon(string text)
        {
            try
            {
                var settings = _configService.LoadSettings();
                var balloonId = settings.BalloonId;
                if (string.IsNullOrEmpty(balloonId))
                {
                    balloonId = "560";
                }
                var data = _balloonService.LoadBalloon(balloonId);
                if (data == null)
                {
                    // 缓存未就绪（后台种子中），本轮静默；下一轮命中再显示
                    return;
                }
                // fire-and-forget：Show 内部已切后台线程渲染，OnBalloonReady 回调到 UI 线程
                _ = _balloonService.Show(text, data);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EffectLayerService] ShowBalloon({text}): {ex.Message}");
            }
        }

        /// <summary>
        /// 尝试驱动实体忙碌表情（Run 态）。
        /// PaperdollService.ExpressionDriver（IExpressionDriver?）是预留 gateway（PaperdollService.cs:45 / :908 ResolveExpression）。
        /// 当前 EffectLayerService 不直接设置 ExpressionDriver（避免与 PetWindow 现有 driver 冲突），
        /// 仅在 PaperdollService 注入时记日志便于联调；真正驱动实体动作待 gateway 扩展（见 ApplyEffect Run 分支 TODO）。
        /// </summary>
        private void TryDriveBusyExpression()
        {
            if (_paperdoll == null)
            {
                // 未注入 PaperdollService（Mob/NPC 实体无纸娃娃 gateway）→ 跳过，无副作用
                return;
            }
            // TODO(gateway 接入)：注入/构造一个 IExpressionDriver，Run 时返回忙碌表情（如 "default" 配 walk1），
            //   Idle 时回 "default"/"blink"。当前仅日志，不实际改 driver 以免破坏现有表情逻辑。
            Console.WriteLine("[EffectLayerService] Run → busy expression gateway (TODO: wire IExpressionDriver for walk1/move)");
        }

        /// <summary>当前生效态（供外部观测/调试）。</summary>
        public AgentState CurrentReflect => _currentReflect;
    }
}
