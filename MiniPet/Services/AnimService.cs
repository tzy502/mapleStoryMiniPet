using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;

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
    /// 动画服务 - 动画状态机、帧循环、动作切换
    /// </summary>
    public class AnimService
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
            // 如果当前是 OneShot（非 fly/move/stand），
            // 新动作是 fly/move → 不覆盖，等播完再切
            if (_currentMode == PlayMode.OneShot
                && !_currentAction.StartsWith("stand")
                && !_currentAction.StartsWith("fly")
                && !_currentAction.StartsWith("move")
                && (action.StartsWith("fly") || action.StartsWith("move")))
            {
                // 标记 pending，等 OneShot 播完自动切
                if (!string.IsNullOrEmpty(_pendingAction))
                    _pendingAction = action;
                return;
            }

            var mode = IsLooping(action) ? PlayMode.Loop : PlayMode.OneShot;
            Play(action, mode);
        }

        /// <summary>
        /// 每帧调用，返回当前帧数据
        /// </summary>
        public (FrameData? data, byte[]? pngData) Tick(double deltaMs)
        {
            if (!_isPlaying || _isPaused)
                return (null, null);

            var (strip, pngData) = _currentStrip;
            if (strip == null || pngData == null)
                return (null, null);

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
                        _pendingAction = "stand";
                        SwitchTo(randomAction);
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
            return (frameData, framePng);
        }

        /// <summary>
        /// 获取当前帧
        /// </summary>
        public (FrameData? data, byte[]? image) GetCurrentFrame()
        {
            var (strip, pngData) = _currentStrip;
            if (strip == null || pngData == null) return (null, null);

            var frameData = GetFrameMeta(strip, _currentFrameIndex);

            var framePng = _spriteService.GetFramePng(_currentMobId, strip!, _currentFrameIndex, pngData);
            return (frameData, framePng);
        }

        /// <summary>
        /// 获取当前动作名
        /// </summary>
        public string GetCurrentAction() => _currentAction;

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
            return _animDefs.TryGetValue(action, out var def) && def.IsLoop;
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
        /// 获取随机空闲动作：从 mob 真实可用动作中选，排除 move/fly/die*/hit*，优先 skill*/attack*。
        /// </summary>
        public string? GetRandomAction()
        {
            var all = _spriteService.GetAvailableActions(_currentMobId)
                .Where(a => a != _currentAction
                    && !a.StartsWith("die", StringComparison.OrdinalIgnoreCase)
                    && !a.StartsWith("hit", StringComparison.OrdinalIgnoreCase)
                    && !a.Equals("move", StringComparison.OrdinalIgnoreCase)
                    && !a.Equals("fly", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (all.Count == 0) return null;
            var preferred = all.Where(a => a.StartsWith("skill", StringComparison.OrdinalIgnoreCase)
                                        || a.StartsWith("attack", StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = preferred.Count > 0 ? preferred : all;
            return pool[Random.Shared.Next(pool.Count)];
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
            var result = _spriteService.LoadSpriteStrip(_currentMobId, action);
            if (result != null)
            {
                var pngKey = $"{_currentMobId}_{action}";
                var cached = _spriteService.GetCachedStrip(_currentMobId, action);
                return cached;
            }

            // fallback 链
            var fallback = ResolveFallback(action);
            if (fallback != null && fallback != action)
            {
                return LoadStripWithFallback(fallback);
            }

            return (null, null);
        }

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
}