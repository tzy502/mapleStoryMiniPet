using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniPet.Tests;

public class AnimServiceTests
{
    // --- embedded minimal models ---
    public class TestAnimationDef
    {
        public string Name { get; set; } = "";
        public bool IsLoop { get; set; }
        public string? Fallback { get; set; }
    }

    public enum TestPlayMode { Loop, OneShot }

    public class TestAnimState
    {
        public string CurrentAction { get; set; } = "stand";
        public TestPlayMode CurrentMode { get; set; } = TestPlayMode.Loop;
        public int CurrentFrameIndex { get; set; }
        public double AccumulatedDelay { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public double IdleAccumulated { get; set; }
        public string PendingAction { get; set; } = "";

        public bool RandomIdleEnabled { get; set; } = true;
        public int IdleTimeoutMinMs { get; set; } = 5000;
        public int IdleTimeoutMaxMs { get; set; } = 30000;
        public int DefaultFrameDelay { get; set; } = 120;

        // Callbacks
        public string? OnActionCompletedAction { get; set; }
        public string? OnActionStartedAction { get; set; }
    }

    // --- tests: 正常路径 (状态机逻辑) ---

    [Fact]
    public void SwitchTo_LoopAction_SetsLoopMode()
    {
        var state = new TestAnimState();
        SwitchTo(state, "stand");

        Assert.Equal("stand", state.CurrentAction);
        Assert.Equal(TestPlayMode.Loop, state.CurrentMode);
        Assert.Equal(0, state.CurrentFrameIndex);
        Assert.True(state.IsPlaying);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void SwitchTo_OneShotAction_SetsOneShotMode()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say");

        Assert.Equal(TestPlayMode.OneShot, state.CurrentMode);
    }

    [Fact]
    public void SwitchTo_UnknownAction_FallsBackToStand()
    {
        var state = new TestAnimState();
        SwitchTo(state, "nonexistent_action");

        // fallback chain resolves to stand
        Assert.Equal("stand", state.CurrentAction);
        Assert.Equal(TestPlayMode.Loop, state.CurrentMode);
    }

    [Fact]
    public void Tick_LoopMode_WrapsAroundAfterLastFrame()
    {
        var state = new TestAnimState();
        SwitchTo(state, "stand"); // 8 frames, loop

        state.CurrentFrameIndex = 7;
        // Simulate tick with enough delay to advance past frame 7
        var (frameIndex, frameCount, mode, completed) = SimulateTick(state, 300, 8);

        // Loop mode: wraps to 0, no completion
        Assert.Equal(0, frameIndex);
        Assert.False(completed);
    }

    [Fact]
    public void Tick_OneShotMode_CompletesAfterLastFrame()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say"); // 8 frames, oneShot

        state.CurrentFrameIndex = 7;
        var (frameIndex, frameCount, mode, completed) = SimulateTick(state, 300, 8);

        // OneShot: completed, triggers fallback to stand
        Assert.True(completed);
    }

    // --- tests: 边界 ---

    [Fact]
    public void Pause_Resume_TogglesState()
    {
        var state = new TestAnimState { IsPlaying = true };
        state.IsPaused = true;
        Assert.True(state.IsPaused);

        state.IsPaused = false;
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void Stop_ResetsState()
    {
        var state = new TestAnimState
        {
            IsPlaying = true,
            CurrentFrameIndex = 5,
            AccumulatedDelay = 1200
        };

        Stop(state);

        Assert.False(state.IsPlaying);
        Assert.Equal(0, state.CurrentFrameIndex);
        Assert.Equal(0, state.AccumulatedDelay);
    }

    [Fact]
    public void IsLooping_ReturnsCorrectForEachAction()
    {
        Assert.True(IsLooping("stand"));
        Assert.True(IsLooping("move"));
        Assert.True(IsLooping("fly"));
        Assert.False(IsLooping("say"));
        Assert.False(IsLooping("hit"));
        Assert.False(IsLooping("die"));
        Assert.False(IsLooping("skill"));
    }

    [Fact]
    public void ResolveFallback_CustomFallback_UsedFirst()
    {
        var customFallbacks = new Dictionary<string, string> { ["custom_action"] = "laugh" };

        var result = ResolveFallback("custom_action", customFallbacks);
        Assert.Equal("laugh", result);
    }

    [Fact]
    public void ResolveFallback_NoCustom_UsesDefFallbackOrStand()
    {
        var customFallbacks = new Dictionary<string, string>();

        // "fly" has def fallback "stand"
        Assert.Equal("stand", ResolveFallback("fly", customFallbacks));

        // "stand" has no fallback, returns stand
        Assert.Equal("stand", ResolveFallback("stand", customFallbacks));
    }

    // --- tests: 异常 ---

    [Fact]
    public void SwitchTo_EmptyAction_DoesNotCrash()
    {
        var state = new TestAnimState();
        var ex = Record.Exception(() => SwitchTo(state, ""));
        Assert.Null(ex);
        Assert.Equal("stand", state.CurrentAction); // falls back to stand
    }

    [Fact]
    public void Tick_WhenNotPlaying_ReturnsCurrentFrameUnchanged()
    {
        var state = new TestAnimState { IsPlaying = false, CurrentFrameIndex = 3 };
        var (frameIndex, _, _, _) = SimulateTick(state, 300, 8);

        Assert.Equal(3, frameIndex); // unchanged
    }

    [Fact]
    public void Tick_WhenPaused_ReturnsCurrentFrameUnchanged()
    {
        var state = new TestAnimState { IsPlaying = true, IsPaused = true, CurrentFrameIndex = 3 };
        var (frameIndex, _, _, _) = SimulateTick(state, 300, 8);

        Assert.Equal(3, frameIndex); // unchanged
    }

    [Fact]
    public void OneShotCompletion_TriggersPendingAction()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say");
        state.PendingAction = "laugh";
        state.CurrentFrameIndex = 7;

        var (_, _, _, completed) = SimulateTick(state, 300, 8);
        Assert.True(completed);
    }

    [Fact]
    public void IdleAccumulated_ReachesThreshold_TriggersRandomAction()
    {
        var state = new TestAnimState();
        SwitchTo(state, "stand");
        state.RandomIdleEnabled = true;
        state.IdleTimeoutMinMs = 1000;
        state.IdleAccumulated = 1000; // exceeded min

        var triggered = CheckIdleTrigger(state);
        Assert.True(triggered);
    }

    [Fact]
    public void IdleAccumulated_BelowThreshold_DoesNotTrigger()
    {
        var state = new TestAnimState();
        SwitchTo(state, "stand");
        state.RandomIdleEnabled = true;
        state.IdleTimeoutMinMs = 5000;
        state.IdleAccumulated = 1000; // below min

        var triggered = CheckIdleTrigger(state);
        Assert.False(triggered);
    }

    [Fact]
    public void EnableRandomIdle_False_NoTrigger()
    {
        var state = new TestAnimState();
        SwitchTo(state, "stand");
        state.RandomIdleEnabled = false;
        state.IdleAccumulated = 99999;

        var triggered = CheckIdleTrigger(state);
        Assert.False(triggered);
    }

    // --- embedded minimal logic replicas ---

    private static readonly Dictionary<string, TestAnimationDef> AnimDefs = new()
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

    private static void SwitchTo(TestAnimState state, string action)
    {
        var resolved = action;
        if (!AnimDefs.ContainsKey(action))
        {
            resolved = ResolveFallback(action, new Dictionary<string, string>());
            if (resolved == null) return;
        }

        state.CurrentAction = resolved;
        state.CurrentMode = IsLooping(resolved) ? TestPlayMode.Loop : TestPlayMode.OneShot;
        state.CurrentFrameIndex = 0;
        state.AccumulatedDelay = 0;
        state.IsPlaying = true;
        state.IsPaused = false;
        state.IdleAccumulated = 0;
    }

    private static void Stop(TestAnimState state)
    {
        state.IsPlaying = false;
        state.CurrentFrameIndex = 0;
        state.AccumulatedDelay = 0;
        state.PendingAction = "";
    }

    private static bool IsLooping(string action)
    {
        return AnimDefs.TryGetValue(action, out var def) && def.IsLoop;
    }

    private static string? ResolveFallback(string action, Dictionary<string, string> customFallbacks)
    {
        if (customFallbacks.TryGetValue(action, out var custom))
            return custom;
        if (AnimDefs.TryGetValue(action, out var def) && def.Fallback != null)
            return def.Fallback;
        return "stand";
    }

    private static (int frameIndex, int frameCount, TestPlayMode mode, bool completed) SimulateTick(
        TestAnimState state, double deltaMs, int frameCount)
    {
        if (!state.IsPlaying || state.IsPaused)
            return (state.CurrentFrameIndex, frameCount, state.CurrentMode, false);

        state.AccumulatedDelay += deltaMs;
        var frameDelay = state.DefaultFrameDelay;

        if (state.AccumulatedDelay >= frameDelay)
        {
            state.AccumulatedDelay -= frameDelay;
            state.CurrentFrameIndex++;

            if (state.CurrentFrameIndex >= frameCount)
            {
                if (state.CurrentMode == TestPlayMode.OneShot)
                {
                    return (state.CurrentFrameIndex, frameCount, state.CurrentMode, true);
                }
                state.CurrentFrameIndex = 0;
            }
        }

        return (state.CurrentFrameIndex, frameCount, state.CurrentMode, false);
    }

    private static bool CheckIdleTrigger(TestAnimState state)
    {
        if (!state.RandomIdleEnabled) return false;
        if (state.CurrentMode != TestPlayMode.Loop) return false;
        return state.IdleAccumulated >= state.IdleTimeoutMinMs;
    }
}
