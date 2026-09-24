using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services
{
    /// <summary>
    /// 动画播放模式
    /// </summary>
    public enum PlayMode
    {
        Loop,
        OneShot
    }

    /// <summary>
    /// 动画事件类型（P2 预留）
    /// </summary>
    public enum AnimEventType
    {
        FrameChanged,
        ActionCompleted,
        ActionStarted,
        AllAnimationsCompleted
    }

    /// <summary>
    /// 动画服务 - 动画状态机、帧循环、动作切换。
    ///
    /// 实现 <see cref="IActionDriver"/>：Agent Run（<see cref="AgentState.Run"/>）时
    /// <see cref="EffectLayerService"/> 经 <see cref="PlayAction"/> 切忙碌动作，
    /// Run 退出经 <see cref="RestoreDefault"/> 回 stand。语义动作名在内部按当前实体类型路由：
    /// 纸娃娃（哨兵 + provider）走 <see cref="ResolveActionForProvider"/> 别名（move→walk1），
    /// Mob/NPC 透传动作名走精灵图条 fallback 链。
    /// </summary>
    public class AnimService : IActionDriver
    {
        private readonly SpriteService _spriteService;
        private readonly Dictionary<string, AnimationDef> _animDefs;
        private readonly Dictionary<string, string> _fallbacks;

        private string _currentMobId = string.Empty;
        private string _currentAction = "stand";
        private string _pendingAction = string.Empty;
        private int _currentFrameIndex;
        private double _accumulatedDelay;
        private PlayMode _currentMode = PlayMode.Loop;
        private bool _isPlaying;
        private bool _isPaused;

        // 空闲超时
        private int _idleTimeoutMinMs = 8000;
        private int _idleTimeoutMaxMs = 15000;
        private bool _randomIdleEnabled = true;
        private double _idleAccumulated;
        private double _currentIdleThreshold;

        // 回调
        private Action? _onComplete;
        private readonly Dictionary<AnimEventType, List<Action<object?>>> _eventHandlers;

        // 当前帧缓存
        private (SpriteStrip?, byte[]?) _currentStrip;

        // 实体帧提供者（纸娃娃实时合成；Mob/NPC 为 null 走图条）
        private IEntityFrameProvider? _frameProvider;

        // 空闲随机动作池缓存（A13）：避免每次空闲触发都枚举磁盘动作列表；缓存键含 mobId + 外观指纹，
        // mob/外观/武器变化自动失效重建（A15：纸娃娃换武器必须重建展示池，否则展示动作永不上池）
        private string _idlePoolKey = string.Empty;
        private List<string> _idleActionPool = new();        // Loop 池（stand/walk/fly/jump 等循环微动效）
        private List<string> _idleDisplayPool = new();       // 展示 OneShot 池-首选（武器类型匹配 + 武器有帧）
        private List<string> _idleDisplayAltPool = new();    // 展示 OneShot 池-兜底（武器有帧即可展示；族匹配空时用）

        public AnimService(SpriteService spriteService)
        {
            _spriteService = spriteService;
            _animDefs = new Dictionary<string, AnimationDef>
            {
                ["stand"] = new() { Name = "stand", IsLoop = true, Fallback = null },
                ["move"] = new() { Name = "move", IsLoop = true, Fallback = "stand" },
                ["fly"] = new() { Name = "fly", IsLoop = true, Fallback = "stand" },
                ["say"] = new() { Name = "say", IsLoop = false, Fallback = "stand" },
                ["mouse"] = new() { Name = "mouse", IsLoop = false, Fallback = "stand" },
                ["laugh"] = new() { Name = "laugh", IsLoop = false, Fallback = "stand" },
                ["eye"] = new() { Name = "eye", IsLoop = false, Fallback = "stand" },
                ["hit"] = new() { Name = "hit", IsLoop = false, Fallback = "stand" },
                ["skill"] = new() { Name = "skill", IsLoop = false, Fallback = "stand" },
                ["die"] = new() { Name = "die", IsLoop = false, Fallback = null }
            };
            _fallbacks = new Dictionary<string, string>();
            _eventHandlers = new Dictionary<AnimEventType, List<Action<object?>>>();
            ResetIdleThreshold();
        }

        /// <summary>
        /// 播放指定动作
        /// </summary>
        public void Play(string action, PlayMode mode)
        {
            // 直接 Play 会打断当前动作（含 OneShot）——「等 OneShot 播完再切」的契约破裂，
            // 残留的 pending（A1 拖拽/移动请求）应一并作废，否则会在下一次 OneShot 完成时误触发。
            _pendingAction = string.Empty;

            if (ActiveProvider != null)
            {
                // 纸娃娃：动作名解析到 provider 实际动作（stand→stand1 等），不查图条
                _currentAction = ResolveActionForProvider(action);
                _currentMode = mode;
                _currentFrameIndex = 0;
                _accumulatedDelay = 0;
                _isPlaying = true;
                _isPaused = false;
                _idleAccumulated = 0;
                ResetIdleThreshold();
                Console.WriteLine($"[Anim] Play: 请求={action} → {_currentAction} 帧数={(_frameProvider?.GetFrameCount(_currentAction) ?? 0)}");
                EmitEvent(AnimEventType.ActionStarted, _currentAction);
                return;
            }

            if (!_animDefs.ContainsKey(action) && !HasSpriteForAction(action))
            {
                // 未定义的动作，尝试 fallback
                var fallback = ResolveFallback(action);
                if (fallback == null) return;
                action = fallback;
            }

            _currentAction = action;
            _currentMode = mode;
            _currentFrameIndex = 0;
            _accumulatedDelay = 0;
            _isPlaying = true;
            _isPaused = false;
            _idleAccumulated = 0;
            ResetIdleThreshold();
            _currentStrip = _spriteService.GetCachedStrip(_currentMobId, action);

            if (_currentStrip is (null, _))
            {
                _currentStrip = LoadStripWithFallback(action);
            }

            EmitEvent(AnimEventType.ActionStarted, action);
        }

        /// <summary>
        /// 停止动画
        /// </summary>
        public void Stop()
        {
            _isPlaying = false;
            _currentFrameIndex = 0;
            _accumulatedDelay = 0;
            _pendingAction = string.Empty;
        }

        /// <summary>
        /// 切换动作（自动判断 Loop/OneShot）
        /// 如果当前正在播放 OneShot 动画（非 fly/move/stand），
        /// 且新动作是 fly/move，不覆盖当前动作（等播完回 stand 再处理）
        /// </summary>
        public void SwitchTo(string action)
        {
            // 当前在播 OneShot 表情（非 stand/fly/move）且新动作是 fly/move：
            // 不打断表情动画，把新动作记入 _pendingAction，等 OneShot 播完（Tick 内 SwitchTo(nextAction)）再切。
            // A1 修正：条件原为 `if (!string.IsNullOrEmpty(_pendingAction))`（写反）——OneShot 从干净状态
            // （菜单触发等，pending 为空）播放时，move/fly 请求被静默丢弃；且空闲随机预置 pending="stand" 时
            // 会无条件覆盖用户请求。修正语义：仅当没有 pending，或 pending 只是空闲随机预置的默认回退
            // "stand" 时记录；已有用户请求（move/fly）时保留首次请求，不被后续覆盖。
            if (_currentMode == PlayMode.OneShot
                && !_currentAction.StartsWith("stand")
                && !_currentAction.StartsWith("fly")
                && !_currentAction.StartsWith("move")
                && (action.StartsWith("fly") || action.StartsWith("move")))
            {
                if (_pendingAction.Length == 0 || _pendingAction == "stand")
                {
                    _pendingAction = action;
                }
                return;
            }

            var mode = IsLooping(action) ? PlayMode.Loop : PlayMode.OneShot;
            Play(action, mode);
        }

        /// <summary>
        /// 每帧调用，返回当前帧数据。纸娃娃（provider）走实时合成，Mob/NPC 走图条裁剪。
        /// </summary>
        public FrameResult Tick(double deltaMs)
        {
            if (!_isPlaying || _isPaused)
                return new FrameResult();

            var provider = ActiveProvider;
            if (provider != null)
                return TickProvider(provider, deltaMs);

            var (strip, pngData) = _currentStrip;
            if (strip == null || pngData == null)
            {
                // A9：在播但无条带（mob 无该动作/加载失败）→ 返回空帧标记，UI 清画布防旧帧冻结
                return new FrameResult { Empty = true };
            }

            _accumulatedDelay += deltaMs;

            // 无论当前是循环还是 OneShot，都按真实经过时间累积空闲计时。
            if (_randomIdleEnabled)
            {
                _idleAccumulated += deltaMs;
                if (_currentMode == PlayMode.Loop && _idleAccumulated >= _currentIdleThreshold)
                {
                    var randomAction = GetRandomAction();
                    if (randomAction != null && randomAction != _currentAction)
                    {
                        Console.WriteLine($"[AnimService] 随机动作触发: {randomAction}, 空闲 {_idleAccumulated:F0}ms, 阈值 {_currentIdleThreshold:F0}ms");
                        // A10：随机空闲只选 Loop 类动作 → 直接 Loop 播放，不预置 pending
                        // （Loop 无 OneShot 完成回调，原预置 "stand" 会悬挂：下次 OneShot 完成时误回 stand）
                        // 胶水 2026-08-17：mob 池现含 say/regen/attack1 等 OneShot 展示动作——
                        // 这类必须按 OneShot 播（播完自动回 stand），Loop 播会无限循环不回 stand
                        _pendingAction = string.Empty;
                        var idleMode = randomAction.StartsWith("stand", StringComparison.OrdinalIgnoreCase)
                            ? PlayMode.Loop
                            : PlayMode.OneShot;
                        Play(randomAction, idleMode);
                        return Tick(0);
                    }
                    // 没有可用动作，重置计时器
                    ResetIdleThreshold();
                }
            }

            // 帧切换 —— 一次性追帧（多帧/短延迟动画不再因每 tick 只进一帧而慢放/卡顿）
            int advances = 0;
            while (_accumulatedDelay >= GetFrameDelay(strip, _currentFrameIndex) && advances < 8)
            {
                _accumulatedDelay -= GetFrameDelay(strip, _currentFrameIndex);
                _currentFrameIndex++;
                advances++;

                // 是否播完
                if (_currentFrameIndex >= strip.FrameCount)
                {
                    if (_currentMode == PlayMode.OneShot)
                    {
                        EmitEvent(AnimEventType.ActionCompleted, _currentAction);
                        var onComplete = _onComplete;
                        _onComplete = null;
                        onComplete?.Invoke();

                        var nextAction = !string.IsNullOrEmpty(_pendingAction)
                            ? _pendingAction
                            : "stand";
                        _pendingAction = string.Empty;
                        SwitchTo(nextAction);
                        return Tick(0);
                    }

                    // Loop 模式回到开头
                    _currentFrameIndex = 0;
                }
            }

            if (advances > 0)
            {
                _currentStrip = _spriteService.GetCachedStrip(_currentMobId, _currentAction);
                if (_currentStrip is (null, _))
                {
                    _currentStrip = LoadStripWithFallback(_currentAction);
                }
                EmitEvent(AnimEventType.FrameChanged, _currentAction);
            }

            // 裁剪当前帧（从 SpriteStrip.FrameData 读取延迟和 Alpha）
            var frameData = GetFrameMeta(strip, _currentFrameIndex);

            var (cs, cd) = _currentStrip;
            var framePng = _spriteService.GetFramePng(_currentMobId, cs!, _currentFrameIndex, cd);
            if (framePng == null)
            {
                // A9：帧数据缺失（条带重建中/解码失败）→ 空帧标记，UI 清画布防旧帧冻结
                return new FrameResult { Data = frameData, Empty = true };
            }
            return new FrameResult { Data = frameData, Png = framePng };
        }

        /// <summary>纸娃娃 provider 路径：用 provider 的帧延迟推进，每帧实时合成位图。</summary>
        private FrameResult TickProvider(IEntityFrameProvider provider, double deltaMs)
        {
            _accumulatedDelay += deltaMs;

            if (_randomIdleEnabled)
            {
                _idleAccumulated += deltaMs;
                if (_currentMode == PlayMode.Loop && _idleAccumulated >= _currentIdleThreshold)
                {
                    var (randomAction, randomMode) = GetRandomIdleAction();
                    // 胶水 2026-08-17：选中与当前相同动作时重选一次（旧逻辑直接放弃 → 到点不切，观感「动画不切换」）
                    if (randomAction == _currentAction)
                    {
                        var (retryAction, retryMode) = GetRandomIdleAction();
                        if (retryAction != null && retryAction != _currentAction)
                        {
                            randomAction = retryAction;
                            randomMode = retryMode;
                        }
                    }
                    if (randomAction != null && randomAction != _currentAction)
                    {
                        Console.WriteLine($"[AnimService] 随机动作触发(纸娃娃): {randomAction} 模式={randomMode}");
                        // A15：空闲随机除 Loop 微动效外，还会选中武器展示 OneShot（swing*/stab*/shoot*）
                        // ——按 OneShot 播放，播完走本方法 OneShot 完成回调自动回 stand1/2（有 pending 则先执行）。
                        // 直接 Play 会清残留 pending（用户 move/fly 请求在 OneShot 播放期由 SwitchTo 记入 pending）。
                        _pendingAction = string.Empty;
                        Play(randomAction, randomMode);
                        return TickProvider(provider, 0);
                    }
                    ResetIdleThreshold();
                }
            }

            int frameCount = provider.GetFrameCount(_currentAction);
            if (frameCount <= 0)
            {
                var stand = ResolveActionForProvider("stand");
                if (_currentAction != stand)
                {
                    // 诊断「走一半变 stand」：正常 walk1/walk2/jump 都有帧，触发即说明动作解析出了问题
                    Console.Error.WriteLine($"[Anim] 动作无帧回退 stand: {_currentAction}");
                    _currentAction = stand;
                    _currentFrameIndex = 0;
                    _accumulatedDelay = 0;
                    return TickProvider(provider, 0);
                }
                // WZ 不可用/动作无帧：回退缓存精灵图条（离线兜底）
                return FallbackToOfflineStrip(_currentAction);
            }

            int advances = 0;
            while (_accumulatedDelay >= provider.GetFrameDelay(_currentAction, _currentFrameIndex) && advances < 8)
            {
                _accumulatedDelay -= provider.GetFrameDelay(_currentAction, _currentFrameIndex);
                _currentFrameIndex++;
                advances++;

                if (_currentFrameIndex >= frameCount)
                {
                    if (_currentMode == PlayMode.OneShot)
                    {
                        EmitEvent(AnimEventType.ActionCompleted, _currentAction);
                        var onComplete = _onComplete;
                        _onComplete = null;
                        onComplete?.Invoke();

                        var nextAction = !string.IsNullOrEmpty(_pendingAction)
                            ? _pendingAction
                            : ResolveActionForProvider("stand");
                        _pendingAction = string.Empty;
                        SwitchTo(nextAction);
                        return TickProvider(provider, 0);
                    }
                    _currentFrameIndex = 0;
                }
            }

            if (advances > 0) EmitEvent(AnimEventType.FrameChanged, _currentAction);

            var (bmp, ox, oy, fw, fh) = provider.RenderFrame(_currentAction, _currentFrameIndex);
            if (bmp == null) return FallbackToOfflineStrip(_currentAction);
            return new FrameResult
            {
                Bitmap = bmp,
                OriginX = ox,
                OriginY = oy,
                FrameW = fw,
                FrameH = fh,
                Data = FrameDataOf(provider, _currentFrameIndex)
            };
        }

        private FrameData FrameDataOf(IEntityFrameProvider provider, int frameIndex)
        {
            return new FrameData
            {
                Index = frameIndex,
                Delay = provider.GetFrameDelay(_currentAction, frameIndex),
                A0 = 255,
                A1 = 255
            };
        }

        /// <summary>WZ 不可用/合成失败时回退缓存精灵图条（离线兜底；由 PaperdollService.BuildOfflineStrips 预存）。</summary>
        private FrameResult FallbackToOfflineStrip(string action)
        {
            try
            {
                _currentStrip = LoadStripWithFallback(action);
                var (cs, cd) = _currentStrip;
                if (cs == null || cd == null)
                {
                    // A9：离线兜底也无可用条带 → 空帧标记，UI 清画布防旧帧冻结
                    return new FrameResult { Empty = true };
                }
                int idx = Math.Max(0, Math.Min(_currentFrameIndex, cs.FrameCount - 1));
                var framePng = _spriteService.GetFramePng(OfflineCacheMobId, cs, idx, cd);
                return new FrameResult { Data = GetFrameMeta(cs, idx), Png = framePng };
            }
            catch
            {
                return new FrameResult { Empty = true };
            }
        }

        /// <summary>
        /// 离线条带缓存 mobId（R4）：纸娃娃按外观 hash 后缀（PaperdollService.OfflineCacheMobId），
        /// 换外观自动失效；Mob/NPC 用原 mobId。
        /// </summary>
        private string OfflineCacheMobId =>
            IsProviderActive && _frameProvider is PaperdollService pd && !string.IsNullOrEmpty(pd.OfflineCacheMobId)
                ? pd.OfflineCacheMobId
                : _currentMobId;

        /// <summary>
        /// 获取当前帧
        /// </summary>
        public FrameResult GetCurrentFrame()
        {
            var provider = ActiveProvider;
            if (provider != null)
            {
                var (bmp, ox, oy, fw, fh) = provider.RenderFrame(_currentAction, _currentFrameIndex);
                return new FrameResult
                {
                    Bitmap = bmp, OriginX = ox, OriginY = oy, FrameW = fw, FrameH = fh,
                    Data = FrameDataOf(provider, _currentFrameIndex)
                };
            }

            var (strip, pngData) = _currentStrip;
            if (strip == null || pngData == null) return new FrameResult();

            var frameData = GetFrameMeta(strip, _currentFrameIndex);

            var framePng = _spriteService.GetFramePng(_currentMobId, strip!, _currentFrameIndex, pngData);
            return new FrameResult { Data = frameData, Png = framePng };
        }

        /// <summary>
        /// 获取当前动作名
        /// </summary>
        public string GetCurrentAction() => _currentAction;

        /// <summary>
        /// 当前播放图条引用（A13）：供 UI 帧循环直接读取，避免每帧二次 GetCachedStrip（锁 + LRU 链表维护）。
        /// 图条路径有效；纸娃娃 provider 路径为 null（UI 走 FrameResult.Bitmap）。
        /// </summary>
        public SpriteStrip? CurrentStrip => _currentStrip.Item1;

        /// <summary>
        /// 获取可用动作列表
        /// </summary>
        public List<string> GetAnimationList()
        {
            var configResult = _spriteService.GetCachedStrip(_currentMobId, "config");
            if (configResult is (_, null)) return _animDefs.Keys.ToList();

            // 从缓存配置中获取实际可用的动作
            return _animDefs.Keys.ToList();
        }

        /// <summary>
        /// 判断动作是否循环
        /// </summary>
        public bool IsLooping(string action)
        {
            if (_animDefs.TryGetValue(action, out var def) && def.IsLoop)
            {
                return true;
            }
            // 2026-08-17：纸娃娃实际动作名（stand1/stand2/walk1/fly/jump…）不在 _animDefs——
            // 原 IsLooping 返回 false → SwitchTo("stand") 把 stand1 按 OneShot 播放 → 播完
            // （~300ms）完成回调又回 stand1 → 死循环重置空闲累计 → 空闲随机永不触发（动画固定 stand1）。
            // 循环前缀判定对齐 IsLoopAction；swing*/stab*/shoot* 等展示动作不匹配 → 仍按 OneShot。
            if (action.StartsWith("stand", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (action.StartsWith("walk", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (action.StartsWith("fly", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (action.StartsWith("jump", StringComparison.OrdinalIgnoreCase)) { return true; }
            return false;
        }

        /// <summary>
        /// 设置动作缺失时的 fallback
        /// </summary>
        public void SetFallback(string action, string fallback)
        {
            _fallbacks[action] = fallback;
        }

        /// <summary>
        /// 设置空闲超时范围（毫秒）
        /// </summary>
        public void SetIdleTimeout(int minMs, int maxMs)
        {
            _idleTimeoutMinMs = minMs;
            _idleTimeoutMaxMs = maxMs;
        }

        /// <summary>
        /// 启用/禁用空闲随机动作
        /// </summary>
        public void EnableRandomIdle(bool enable)
        {
            _randomIdleEnabled = enable;
            _idleAccumulated = 0;
            ResetIdleThreshold();
        }

        /// <summary>
        /// 重置空闲阈值（在 min~max 之间随机）
        /// </summary>
        private void ResetIdleThreshold()
        {
            _idleAccumulated = 0;
            _currentIdleThreshold = _idleTimeoutMinMs + Random.Shared.NextDouble() * (_idleTimeoutMaxMs - _idleTimeoutMinMs);
        }

        /// <summary>
        /// 获取随机动作（Loop 池）：只从「Loop 类动作」池中选（A10 统一 Loop/OneShot 分裂；A13 池缓存免每触发磁盘枚举）。
        /// 旧逻辑问题：mob 路径会选中 walk1 等 OneShot（走一遍就停）、skill/attack 优先导致其他动作永轮不到、
        /// 纸娃娃路径会选中 fly（Loop 无限循环且 pending 悬挂）。统一后只播站/走/飞等循环微动效。
        /// 供 EffectLayerService（Agent 随机动作）与 mob 路径空闲随机使用；纸娃娃空闲随机走
        /// <see cref="GetRandomIdleAction"/>（Loop + 武器展示 OneShot）。
        /// </summary>
        public string? GetRandomAction()
        {
            var pool = GetIdleActionPool();
            if (pool.Count == 0)
            {
                return null;
            }

            // 池缓存不含「当前动作」过滤（动作随时变），取时排除
            var candidates = new List<string>(pool.Count);
            foreach (var a in pool)
            {
                if (a != _currentAction)
                {
                    candidates.Add(a);
                }
            }
            if (candidates.Count == 0)
            {
                return null;
            }
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        /// <summary>
        /// 空闲随机动作（A15）：Loop 池 + 武器展示 OneShot 池合并随机，返回 (动作, 播放模式)。
        /// 展示动作（swing*/stab*/shoot*）按 <see cref="PlayMode.OneShot"/> 播放——播完由 TickProvider
        /// 的 OneShot 完成回调自动回 stand1/2（「展示完回归 stand」）；无武器/该武器所有展示动作均无帧时
        /// 展示池为空，退化为纯 Loop（与旧行为一致）。
        /// </summary>
        private (string? Action, PlayMode Mode) GetRandomIdleAction()
        {
            var loopPool = GetIdleActionPool();
            var displayPool = GetIdleDisplayPool();
            var displayAltPool = GetIdleDisplayAltPool();
            // 首选族匹配展示池；族匹配为空（武器类型未知/映射未覆盖本 WZ）→ 用兜底池（武器有帧即展示）
            var effectiveDisplay = displayPool.Count > 0 ? displayPool : displayAltPool;

            // 合并候选（排除当前动作；两池无交集：Loop 池只含循环动作，展示池只含非循环武器动作）。
            // 胶水 2026-09-15：stand1/stand2/walk1/walk2 等**基础动作一律不参与空闲随机切换**
            // （待机就安定站在基准站姿上，切换只留给展示类动作）——不管当前是不是 stand 族，
            // stand* 候选直接剔除；walk*/fly*/jump* 在 IsLoopAction 已排除。
            var candidates = new List<string>(loopPool.Count + effectiveDisplay.Count);
            foreach (var a in loopPool)
            {
                if (a != _currentAction && !IsStandFamily(a))
                {
                    candidates.Add(a);
                }
            }
            foreach (var a in effectiveDisplay)
            {
                if (a != _currentAction && !IsStandFamily(a))
                {
                    candidates.Add(a);
                }
            }
            if (candidates.Count == 0)
            {
                return (null, PlayMode.Loop);
            }

            var pool = candidates;

            var chosen = pool[Random.Shared.Next(pool.Count)];
            var mode = effectiveDisplay.Contains(chosen) ? PlayMode.OneShot : PlayMode.Loop;
            return (chosen, mode);
        }

        /// <summary>空闲随机 Loop 池：仅 Loop 类动作（A13 缓存；与展示池同键同建）。</summary>
        /// <summary>是否 stand 族动作（stand / stand1 / stand2…）。</summary>
        private static bool IsStandFamily(string? action)
        {
            return !string.IsNullOrEmpty(action)
                   && action.StartsWith("stand", StringComparison.OrdinalIgnoreCase);
        }

        private List<string> GetIdleActionPool()
        {
            BuildIdlePools();
            return _idleActionPool;
        }

        /// <summary>
        /// 空闲随机展示池-首选（A15）：纸娃娃 + 有武器时，从「与武器类型匹配的展示动作」中选。
        /// 过滤规则：① 动作在 provider 可用列表（身体有帧 + 合成有界）；② 动作族前缀匹配武器类型
        /// （swingO=单手、swingT=双手、swingP=枪矛、shoot=弓弩、stab=匕首/拳套；映射按本 WZ 实测校正）；
        /// ③ 武器自身该动作有帧（WeaponHasAction——该动画会画出这把武器，否则跳过换另一个）。
        /// 无武器/武器类型未知 → 首选池为空（回退 <see cref="GetIdleDisplayAltPool"/>，再空则只 Loop）。
        /// </summary>
        private List<string> GetIdleDisplayPool()
        {
            BuildIdlePools();
            return _idleDisplayPool;
        }

        /// <summary>
        /// 空闲随机展示池-兜底（A15）：纸娃娃 + 有武器时，所有「武器自身有帧」的展示动作
        /// （该动画会画出这把武器），供武器类型映射未覆盖/不匹配时回退（避免本 WZ 编号异常段无展示）。
        /// </summary>
        private List<string> GetIdleDisplayAltPool()
        {
            BuildIdlePools();
            return _idleDisplayAltPool;
        }

        /// <summary>
        /// 构建空闲随机三池（Loop + 展示首选 + 展示兜底）。缓存键 = mobId + 外观指纹（纸娃娃 CurrentHash，
        /// 含武器 id；mob 为空串），外观/武器变化自动失效重建（A15：原 A13 只按 mobId 缓存，
        /// 纸娃娃 mobId 恒为哨兵 → 换武器后展示池永不刷新，展示动作永不上池）。
        /// 纸娃娃走 provider 动作列表；Mob/NPC 走精灵图条动作列表（内存 ∪ 磁盘）。
        /// </summary>
        private void BuildIdlePools()
        {
            string providerKey = string.Empty;
            var provider = ActiveProvider;
            if (provider is PaperdollService pd)
            {
                providerKey = pd.CurrentHash;
            }
            string key = $"{_currentMobId}|{providerKey}";
            if (_idlePoolKey == key && _idleActionPool.Count > 0)
            {
                return;
            }

            List<string>? available;
            if (provider != null)
            {
                available = provider.GetAvailableActions();
            }
            else
            {
                available = _spriteService.GetAvailableActions(_currentMobId);
            }

            // 纸娃娃 + 有武器 → 展示动作族（swingO/swingT/swingP/shoot/stab）；无武器/未知 → 空
            string family = string.Empty;
            var paperdoll = provider as PaperdollService;
            if (paperdoll != null)
            {
                family = GetWeaponActionFamily(paperdoll.Current?.Weapon?.Id);
            }

            var loopPool = new List<string>();
            var displayPool = new List<string>();
            var displayAltPool = new List<string>();
            if (available != null)
            {
                foreach (var a in available)
                {
                    if (a.StartsWith("die", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (a.StartsWith("hit", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // mob 路径（胶水 2026-08-17）：say/regen/attack1 等展示动作也进空闲池（OneShot 播完自动回 stand）
                    if (paperdoll == null && IsMobIdleShowAction(a) && !IsLoopAction(a))
                    {
                        loopPool.Add(a);
                        continue;
                    }
                    if (IsLoopAction(a))
                    {
                        loopPool.Add(a);
                        continue;
                    }
                    // 展示动作：武器自身该动作有帧（该动画会画出这把武器）才进池；
                    // 族匹配进首选池，其余（映射未覆盖的展示动作）进兜底池
                    if (paperdoll == null || !IsWeaponDisplayAction(a) || !paperdoll.WeaponHasAction(a))
                    {
                        continue;
                    }
                    if (family.Length > 0 && IsFamilyMatch(a, family))
                    {
                        displayPool.Add(a);
                    }
                    else
                    {
                        displayAltPool.Add(a);
                    }
                }
            }

            _idlePoolKey = key;
            _idleActionPool = loopPool;
            _idleDisplayPool = displayPool;
            _idleDisplayAltPool = displayAltPool;
        }

        /// <summary>
        /// 武器类型 → 展示动作族前缀（本 WZ 实测校正，WeaponActionProbe 实图验证）：
        /// 121-132 系为 swingO 帧集、133-134 枪矛 swingP、137-138 弓弩 shoot、140-146 双手 swingT
        /// （对齐 PaperdollService.IsTwoHandWeapon 的 141-146）、153 系 swingT/swingP、170 系无展示帧。
        /// 映射未覆盖/不匹配时由兜底池接管（武器有帧即展示），不会因编号异常而无展示。
        /// </summary>
        private static string GetWeaponActionFamily(string? weaponId)
        {
            if (string.IsNullOrEmpty(weaponId) || !int.TryParse(weaponId, out var n))
            {
                return string.Empty;
            }
            int part = n / 10000;
            if (part >= 121 && part <= 123)
            {
                return "swingO";   // 单手剑/斧/钝器
            }
            if (part >= 124 && part <= 125)
            {
                return "stab";     // 匕首/拳套（本 WZ 亦有 swingO 帧，stab 更贴匕首特征）
            }
            if (part >= 130 && part <= 132)
            {
                return "swingO";   // 本 WZ 实测 130-132 为 swingO 帧集（非常规双手编号）
            }
            if (part >= 133 && part <= 134)
            {
                return "swingP";   // 枪/矛（swingPF 帧）
            }
            if (part >= 137 && part <= 138)
            {
                return "shoot";    // 弓/弩（shoot1/shootF 帧）
            }
            if (part >= 140 && part <= 146)
            {
                return "swingT";   // 本 WZ 实测 140-146 为 swingT 帧集（对齐 IsTwoHandWeapon）
            }
            if (part == 153)
            {
                return "swingT";   // 本 WZ 实测 153 系 swingT/swingP 帧
            }
            if (part == 170)
            {
                return "swingP";   // 本 WZ 170 系无展示帧 → 两池皆空，兜底只 Loop
            }
            return string.Empty;
        }

        /// <summary>动作名是否为武器展示动作（swingO*/swingT*/swingP*/shoot*/stabO*/stabT*）。</summary>
        private static bool IsWeaponDisplayAction(string action)
        {
            return action.StartsWith("swingO", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("swingT", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("swingP", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("shoot", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("stabO", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("stabT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>动作名是否匹配武器动作族（stab 族含 stabO*/stabT*；其余按族前缀匹配）。</summary>
        private static bool IsFamilyMatch(string action, string family)
        {
            if (family.Length == 0)
            {
                return false;
            }
            if (family == "stab")
            {
                return action.StartsWith("stabO", StringComparison.OrdinalIgnoreCase)
                    || action.StartsWith("stabT", StringComparison.OrdinalIgnoreCase);
            }
            return action.StartsWith(family, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 空闲随机 Loop 池过滤（A10 + 2026-08-17 用户要求「walk/fly/jump 都不要，更多攻击动作」）：
        /// 只保留 stand* 站姿循环（纸娃娃与 mob/npc 一致）；walk*/fly*/move*/jump* 不再进空闲随机池
        /// ——空闲随机以攻击（展示）动作为主（swing*/stab*/shoot* OneShot），站姿为回归基准。
        /// 注意：与 IsLooping（OneShot 完成回调的 Loop/OneShot 判定）无关，勿混用。
        /// </summary>
        private bool IsLoopAction(string action)
        {
            if (_animDefs.TryGetValue(action, out var def) && def.IsLoop)
            {
                // _animDefs 里 move/fly 是 Loop——但空闲随机只要站姿
                if (action.StartsWith("walk", StringComparison.OrdinalIgnoreCase)
                    || action.StartsWith("fly", StringComparison.OrdinalIgnoreCase)
                    || action.StartsWith("move", StringComparison.OrdinalIgnoreCase)
                    || action.StartsWith("jump", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }
            return action.StartsWith("stand", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>mob 空闲随机候选（胶水 2026-08-17 修复 mob 永不自动切换）：
        /// mob 动作不在 _animDefs，IsLoopAction 对 regen/say/attack1 等全返回 false →
        /// 旧 mob 空闲池 = {stand}，排除当前动作后池空 → GetRandomAction 恒 null → 永不切换。
        /// mob 特化：stand* 之外的「安全 OneShot 展示动作」（say/regen/attack/skill/lift 等）
        /// 也进候选——播完由 Tick OneShot 完成回调自动回 stand，不会卡死。
        /// 排除 die*/hit*/jump*/fly*/move*/walk*（死亡/受击/位移类不进空闲随机）。</summary>
        private bool IsMobIdleShowAction(string action)
        {
            if (action.StartsWith("die", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("hit", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("jump", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("fly", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("move", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("walk", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            // stand* 是 Loop 回环主体；其余（say/regen/attack1/skill1/lift…）按 OneShot 展示
            return true;
        }

        /// <summary>
        /// 注册事件处理器（P2 预留）
        /// </summary>
        public void RegisterEvent(AnimEventType eventType, Action<object?> callback)
        {
            if (!_eventHandlers.ContainsKey(eventType))
            {
                _eventHandlers[eventType] = new List<Action<object?>>();
            }
            _eventHandlers[eventType].Add(callback);
        }

        /// <summary>
        /// 注册动画播放完成回调
        /// </summary>
        public void OnComplete(Action callback)
        {
            _onComplete = callback;
        }

        /// <summary>
        /// 设置当前 mob ID（内部使用）
        /// </summary>
        public string GetCurrentMobId() => _currentMobId;

        public void SetCurrentMobId(string mobId)
        {
            _currentMobId = mobId ?? string.Empty;
            // 随机动作池缓存失效（A13）：mob 变化后按新 mob 重建（A15：键含外观指纹，纸娃娃换外观/武器亦失效）
            _idlePoolKey = string.Empty;
        }

        // ─── IActionDriver（Agent Run 实体忙碌动作驱动；PLAN-agent-integration.md §2）───
        // PlayAction / RestoreDefault 复用 SwitchTo：它已处理
        //   1) provider 别名（纸娃娃 move→walk1，stand→stand1，由 ResolveActionForProvider）；
        //   2) Loop/OneShot 自动判定；
        //   3) 当前正在播 OneShot（非 stand/fly/move）时把 move/fly 缓存为 pending，不打断表情动画。
        // 调用方（EffectLayerService）只传语义动作名，实体类型差异在此处内部消化。

        /// <summary>
        /// IActionDriver.PlayAction：切到忙碌动作（Run 态）。
        /// Run 映射由 <see cref="EffectLayerService"/> 决定：
        /// 纸娃娃传 "walk1"（SwitchTo→provider 别名解析），
        /// Mob 传 "move"（或 "fly"，按 Mob 类型；透传到精灵图条），
        /// NPC 传其现有移动动作（无则回退 stand，由 SwitchTo fallback 链兜底）。
        /// </summary>
        void IActionDriver.PlayAction(string action)
        {
            if (string.IsNullOrEmpty(action))
            {
                return;
            }
            try
            {
                SwitchTo(action);
                Console.WriteLine($"[Anim] IActionDriver.PlayAction: {action} (实体={(IsProviderActive ? "paperdoll" : _currentMobId)}) → 当前={_currentAction}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Anim] IActionDriver.PlayAction({action}): {ex.Message}");
            }
        }

        /// <summary>
        /// IActionDriver.RestoreDefault：回 stand（Run→Idle 时调）。
        /// 纸娃娃经 SwitchTo→ResolveActionForProvider 解析为 stand1/stand2（武器变体由 provider 决定）；
        /// Mob/NPC 走精灵图条 stand（fallback 链兜底）。
        /// </summary>
        void IActionDriver.RestoreDefault()
        {
            try
            {
                SwitchTo("stand");
                Console.WriteLine($"[Anim] IActionDriver.RestoreDefault → stand (实体={(IsProviderActive ? "paperdoll" : _currentMobId)}) → 当前={_currentAction}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Anim] IActionDriver.RestoreDefault: {ex.Message}");
            }
        }

        /// <summary>注册帧提供者（纸娃娃实时合成）。Mob/NPC 不注册。</summary>
        public void RegisterFrameProvider(IEntityFrameProvider? provider)
        {
            _frameProvider = provider;
        }

        /// <summary>当前是否走帧提供者（纸娃娃哨兵 + 已注册）。</summary>
        public bool IsProviderActive => _frameProvider != null && _currentMobId == PaperdollService.SentinelMobId;

        private IEntityFrameProvider? ActiveProvider => IsProviderActive ? _frameProvider : null;

        // 标准动作 → 纸娃娃动作别名（对齐 MapleSalon2 updateActionByWeapon / stand 变体）
        private static readonly Dictionary<string, string> PlayerActionAliases = new()
        {
            ["stand"] = "stand1",
            ["move"] = "walk1",
            ["walk"] = "walk1",
            ["fly"] = "jump",
        };

        /// <summary>把请求动作解析为 provider 实际播放动作（武器变体/别名由 provider 决定）。</summary>
        private string ResolveActionForProvider(string action)
        {
            var p = ActiveProvider;
            if (p == null) return action;
            return p.ResolveAction(action);
        }

        /// <summary>
        /// 暂停动画
        /// </summary>
        public void Pause()
        {
            _isPaused = true;
        }

        /// <summary>
        /// 恢复动画
        /// </summary>
        public void Resume()
        {
            _isPaused = false;
        }

        private string? ResolveFallback(string action)
        {
            // 检查自定义 fallback
            if (_fallbacks.TryGetValue(action, out var customFallback))
                return customFallback;

            // 检查动画定义中的 fallback
            if (_animDefs.TryGetValue(action, out var def) && def.Fallback != null)
                return def.Fallback;

            // 全局 fallback 到 stand
            return "stand";
        }

        private (SpriteStrip?, byte[]?) LoadStripWithFallback(string action)
        {
            // R3 纸娃娃离线兜底：语义动作名对齐离线条动作（stand→stand1、walk/move→walk1、fly→jump），
            // 否则 "stand" 请求命不中 stand1 条带 → 空白宠物（A4 离线兜底不可达）。
            // 只映射一次（IsOfflineActionName 防止已映射名再递归 → stand1→stand 死循环）。
            string loadAction = action;
            if (IsProviderActive && !IsOfflineActionName(loadAction))
            {
                loadAction = MapOfflineAction(loadAction);
            }

            var result = _spriteService.LoadSpriteStrip(OfflineCacheMobId, loadAction);
            if (result != null)
            {
                var cached = _spriteService.GetCachedStrip(OfflineCacheMobId, loadAction);
                return cached;
            }

            // fallback 链（语义名按原动作走，纸娃娃别名已在上方解析完，不会递归回 stand→stand1）
            var fallback = ResolveFallback(action);
            if (fallback != null && fallback != action)
            {
                return LoadStripWithFallback(fallback);
            }

            // A4：纸娃娃离线兜底——离线精灵图条只含 provider 实际动作名（stand1/stand2/walk1/walk2/jump），
            // 语义名（stand/move/walk/fly）解析不到 → 按 provider 别名 + stand1 终极兜底，避免离线空白宠物
            if (_currentMobId == PaperdollService.SentinelMobId)
            {
                var alias = ResolveActionForProvider(action);
                if (!string.IsNullOrEmpty(alias) && alias != action)
                {
                    var aliasResult = _spriteService.LoadSpriteStrip(_currentMobId, alias);
                    if (aliasResult != null)
                    {
                        return _spriteService.GetCachedStrip(_currentMobId, alias);
                    }
                }
                var stand1 = _spriteService.LoadSpriteStrip(_currentMobId, "stand1");
                if (stand1 != null)
                {
                    return _spriteService.GetCachedStrip(_currentMobId, "stand1");
                }
            }

            // A9：fallback 终点（stand）仍无 → 依次尝试常用移动动作，mob 无 stand 时不至于空白
            foreach (var alt in new[] { "move", "fly", "walk" })
            {
                if (alt == action)
                {
                    continue;
                }
                var altResult = _spriteService.LoadSpriteStrip(_currentMobId, alt);
                if (altResult != null)
                {
                    var cached = _spriteService.GetCachedStrip(_currentMobId, alt);
                    return cached;
                }
            }

            return (null, null);
        }

        /// <summary>离线条动作名（对齐 PaperdollService.OfflineActions：stand1/stand2/walk1/walk2/jump）。</summary>
        private static bool IsOfflineActionName(string action) =>
            action == "stand1" || action == "stand2" || action == "walk1" || action == "walk2" || action == "jump";

        /// <summary>语义动作 → 离线条动作（R3：与 PaperdollService.ResolveAction 的 stand→stand1 语义对齐）。</summary>
        private static string MapOfflineAction(string action) => action switch
        {
            "stand" => "stand1",
            "walk" or "move" => "walk1",
            "fly" => "jump",
            _ => action
        };

        private bool HasSpriteForAction(string action)
        {
            var (strip, _) = _spriteService.GetCachedStrip(_currentMobId, action);
            if (strip != null) return true;

            var loaded = _spriteService.LoadSpriteStrip(_currentMobId, action);
            return loaded != null;
        }

        private static int GetFrameDelay(SpriteStrip strip, int frameIndex)
        {
            try
            {
                // FrameData 优先级最高
                if (strip.FrameData != null && frameIndex >= 0 && frameIndex < strip.FrameData.Count)
                    return strip.FrameData[frameIndex].Delay;
                if (strip.FrameDelays != null && frameIndex >= 0 && frameIndex < strip.FrameDelays.Count)
                    return strip.FrameDelays[frameIndex];
                return strip.DefaultDelay > 0 ? strip.DefaultDelay : 240;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnimService] GetFrameDelay: {ex.Message}");
                return 240;
            }
        }

        private static FrameData GetFrameMeta(SpriteStrip strip, int frameIndex)
        {
            try
            {
                if (strip.FrameData != null && frameIndex >= 0 && frameIndex < strip.FrameData.Count)
                    return strip.FrameData[frameIndex];
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnimService] GetFrameMeta: {ex.Message}");
            }
            return new FrameData
            {
                Index = frameIndex,
                Delay = GetFrameDelay(strip, frameIndex),
                A0 = 255,
                A1 = 255
            };
        }

        private void EmitEvent(AnimEventType eventType, object? data)
        {
            if (_eventHandlers.TryGetValue(eventType, out var handlers))
            {
                foreach (var handler in handlers)
                {
                    handler(data);
                }
            }
        }
    }

    /// <summary>
    /// 单帧结果：图条路径填 Png，纸娃娃 provider 路径填 Bitmap（WriteableBitmap 直拷）。
    /// OriginX/Y、FrameW/H 为 union canvas 公共锚点与尺寸（纸娃娃路径有效）。
    /// </summary>
    public class FrameResult
    {
        public FrameData? Data { get; set; }
        public byte[]? Png { get; set; }
        public SKBitmap? Bitmap { get; set; }
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        public int FrameW { get; set; }
        public int FrameH { get; set; }

        /// <summary>在播但无帧可画（条带缺失/解码失败/离线兜底空）→ UI 应清画布防旧帧冻结（A9）。</summary>
        public bool Empty { get; set; }
    }
}