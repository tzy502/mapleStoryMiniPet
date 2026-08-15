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

        // Review（思考）周期提示定时器：同态去重后进入 Review 只触发一次动画不重复，
        // 用定时器每 3~5 秒补一次随机动作（skill/attack 优先），离开 Review 停止。
        private DispatcherTimer? _reviewTimer;

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

            // Review（思考）周期提示：进入 Review 启动定时器（每 3~5 秒随机动作），离开停止
            if (state == AgentState.Review)
            {
                StartReviewTimer();
            }
            else
            {
                StopReviewTimer();
            }

            ApplyEffect(state, prev);
        }

        /// <summary>
        /// 状态→反馈映射（PLAN.md §4：读 AgentIntegrationConfig.AgentStateFeedback 配置，非硬编码 switch）。
        /// 每态：Action 非空 → PlayAction(fb.Action)；Balloon 非空 → ShowBalloon(fb.Balloon)；都空 → 不反馈。
        /// Idle 特例：映射 Action 空时 RestoreDefault() 回 stand（Run→Idle 语义，对任何前置态安全）。
        /// 同态去重（_currentReflect）由 OnReflect 处理。
        /// </summary>
        private void ApplyEffect(AgentState state, AgentState prev)
        {
            try
            {
                var settings = _configService.LoadSettings();
                var feedbackMap = settings.AgentIntegration?.AgentStateFeedback ?? AgentIntegrationConfig.DefaultAgentStateFeedback();
                feedbackMap.TryGetValue(state, out var fb);

                // Idle：映射 Action 空 → RestoreDefault() 回 stand（Run→Idle 实体动作回 stand；
                // RestoreDefault 只切 stand，对任何前置态都安全，OneShot 播完本就回 stand）
                if (state == AgentState.Idle && prev == AgentState.Run && ActionDriver != null && string.IsNullOrEmpty(fb?.Action))
                {
                    ActionDriver.RestoreDefault();
                }

                // Action 非空 → 切实体动作（Run 默认 "move"：纸娃娃→walk1，Mob→move，NPC→fallback stand）。
                // Action 空且非 Idle → 随机动作（优先 skill/attack，AnimService.GetRandomAction；空=无可用动作则静默）。
                // 实体差异在 driver 内消化；未注入 ActionDriver 时静默跳过。
                if (!string.IsNullOrEmpty(fb?.Action))
                {
                    ActionDriver?.PlayAction(fb.Action);
                }
                else if (state != AgentState.Idle && ActionDriver is AnimService animSvc)
                {
                    var randomAction = animSvc.GetRandomAction();
                    if (!string.IsNullOrEmpty(randomAction))
                    {
                        ActionDriver.PlayAction(randomAction);
                        Console.WriteLine($"[EffectLayerService] {state} → 随机动作 {randomAction}");
                    }
                }

                // Balloon 非空 → 气泡；空 → 状态默认话语（DefaultAgentStateFeedback 兜底，如 Review=思考中…）。
                // Wave（做完了 撒花 🎉）显示 5s 后自动关闭；其余状态气泡常驻等替换。
                var balloonText = fb?.Balloon;
                if (string.IsNullOrEmpty(balloonText))
                {
                    var defaults = AgentIntegrationConfig.DefaultAgentStateFeedback();
                    if (defaults.TryGetValue(state, out var defFb) && !string.IsNullOrEmpty(defFb.Balloon))
                    {
                        balloonText = defFb.Balloon;
                    }
                }
                if (!string.IsNullOrEmpty(balloonText))
                {
                    int? durationMs = state == AgentState.Wave ? 5000 : (int?)null;
                    // 加粗仅 Review（思考中…）气泡（胶水 2026-08-15：只针对思考时加粗）
                    bool bold = state == AgentState.Review;
                    ShowBalloon(balloonText, durationMs, bold);
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
        private void ShowBalloon(string text, int? durationMs = null, bool bold = false)
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
                _ = _balloonService.Show(text, data, durationMs, bold);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EffectLayerService] ShowBalloon({text}): {ex.Message}");
            }
        }

        /// <summary>
        /// 启动 Review（思考）周期提示定时器：每 3~5 秒随机播一次动作（AnimService.GetRandomAction，
        /// skill/attack 优先）。进入 Review 时调用；同态去重保证只启动一次，重复进入（已启动）直接跳过。
        /// </summary>
        private void StartReviewTimer()
        {
            try
            {
                if (_reviewTimer != null)
                {
                    return;
                }
                _reviewTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(Random.Shared.Next(3, 6))
                };
                _reviewTimer.Tick += OnReviewTimerTick;
                _reviewTimer.Start();
                Console.WriteLine("[EffectLayerService] Review → 启动思考提示定时器（每 3~5 秒随机动作）");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EffectLayerService] StartReviewTimer: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止 Review 周期提示定时器。离开 Review 时调用（任何非 Review 状态都会停）。
        /// </summary>
        private void StopReviewTimer()
        {
            try
            {
                if (_reviewTimer == null)
                {
                    return;
                }
                _reviewTimer.Stop();
                _reviewTimer.Tick -= OnReviewTimerTick;
                _reviewTimer = null;
                Console.WriteLine("[EffectLayerService] 离开 Review → 停止思考提示定时器");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EffectLayerService] StopReviewTimer: {ex.Message}");
            }
        }

        /// <summary>
        /// Review 定时器 tick：随机动作一次（复用 ApplyEffect 的 AnimService.GetRandomAction 分支逻辑）。
        /// ActionDriver 未注入或不是 AnimService（无可选动作）时静默跳过，不打扰。
        /// </summary>
        private void OnReviewTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (ActionDriver is not AnimService animSvc)
                {
                    return;
                }
                var randomAction = animSvc.GetRandomAction();
                if (string.IsNullOrEmpty(randomAction))
                {
                    return;
                }
                ActionDriver.PlayAction(randomAction);
                Console.WriteLine($"[EffectLayerService] Review 思考提示 → 随机动作 {randomAction}");
                if (_reviewTimer != null)
                {
                    // 每次播完重设随机间隔（3~5 秒），节奏自然
                    _reviewTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(3, 6));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EffectLayerService] OnReviewTimerTick: {ex.Message}");
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
