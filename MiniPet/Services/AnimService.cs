using System;
using System.Collections.Generic;
using System.Drawing;
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

        private int _currentMobId;
        private string _currentAction = "stand";
        private string _pendingAction = string.Empty;
        private int _currentFrameIndex;
        private double _accumulatedDelay;
        private PlayMode _currentMode = PlayMode.Loop;
        private bool _isPlaying;
        private bool _isPaused;

        // 空闲超时
        private int _idleTimeoutMinMs = 15000;
        private int _idleTimeoutMaxMs = 30000;
        private bool _randomIdleEnabled = true;
        private double _idleAccumulated;

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
        /// </summary>
        public void SwitchTo(string action)
        {
            var mode = IsLooping(action) ? PlayMode.Loop : PlayMode.OneShot;
            Play(action, mode);
        }

        /// <summary>
        /// 每帧调用，返回当前帧数据
        /// </summary>
        public (FrameData? data, Bitmap? image) Tick(double deltaMs)
        {
            if (!_isPlaying || _isPaused)
                return (null, null);

            var (strip, pngData) = _currentStrip;
            if (strip == null || pngData == null)
                return (null, null);

            _accumulatedDelay += deltaMs;

            // 空闲超时累积
            if (_randomIdleEnabled && _currentMode == PlayMode.Loop)
            {
                _idleAccumulated += deltaMs;
                if (_idleAccumulated >= _idleTimeoutMinMs)
                {
                    var randomAction = GetRandomAction();
                    if (randomAction != null && randomAction != _currentAction)
                    {
                        _pendingAction = "stand";
                        SwitchTo(randomAction);
                        return Tick(0);
                    }
                }
            }

            // 帧切换
            var frameDelay = GetFrameDelay(strip, _currentFrameIndex);
            if (_accumulatedDelay >= frameDelay)
            {
                _accumulatedDelay -= frameDelay;
                _currentFrameIndex++;

                // 是否播完
                if (_currentFrameIndex >= strip.FrameCount)
                {
                    if (_currentMode == PlayMode.OneShot)
                    {
                        EmitEvent(AnimEventType.ActionCompleted, _currentAction);
                        var onComplete = _onComplete;
                        _onComplete = null;
                        onComplete?.Invoke();

                        // 切回 pending 动作或 stand
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

                _currentStrip = _spriteService.GetCachedStrip(_currentMobId, _currentAction);
                if (_currentStrip is (null, _))
                {
                    _currentStrip = LoadStripWithFallback(_currentAction);
                }

                EmitEvent(AnimEventType.FrameChanged, _currentAction);
            }

            // 裁剪当前帧
            var frameData = new FrameData
            {
                Index = _currentFrameIndex,
                Delay = frameDelay,
                A0 = 255,
                A1 = 255
            };

            var (cs, cd) = _currentStrip;
            var bitmap = _spriteService.GetFrame(cs, cd, _currentFrameIndex);
            return (frameData, bitmap);
        }

        /// <summary>
        /// 获取当前帧
        /// </summary>
        public (FrameData? data, Bitmap? image) GetCurrentFrame()
        {
            var (strip, pngData) = _currentStrip;
            if (strip == null || pngData == null) return (null, null);

            var frameData = new FrameData
            {
                Index = _currentFrameIndex,
                Delay = GetFrameDelay(strip, _currentFrameIndex),
                A0 = 255,
                A1 = 255
            };

            var bitmap = _spriteService.GetFrame(strip, pngData, _currentFrameIndex);
            return (frameData, bitmap);
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
        }

        /// <summary>
        /// 获取随机空闲动作
        /// </summary>
        public string? GetRandomAction()
        {
            var idleActions = new[] { "say", "mouse", "laugh", "eye" };
            var available = idleActions.Where(a => HasSpriteForAction(a)).ToArray();
            if (available.Length == 0) return null;

            var rng = Random.Shared;
            return available[rng.Next(available.Length)];
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
        public void SetCurrentMobId(int mobId)
        {
            _currentMobId = mobId;
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
            // 默认帧延迟 240ms，实际应从 FrameData 中读取
            return 240;
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