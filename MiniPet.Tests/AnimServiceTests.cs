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

    // ── A1：OneShot 期间 move/fly 切换的 pending 语义（原实现条件写反 → 静默丢弃）──

    [Fact]
    public void SwitchTo_MoveDuringOneShot_WithEmptyPending_SetsPendingAndKeepsAction()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say");                       // 进入 OneShot 表情
        Assert.Equal(TestPlayMode.OneShot, state.CurrentMode);
        state.PendingAction = string.Empty;           // 干净状态（菜单触发等场景）
        SwitchTo(state, "move");                      // A1：原实现静默丢弃，修正后应记 pending
        Assert.Equal("move", state.PendingAction);
        Assert.Equal("say", state.CurrentAction);     // 不打断当前表情
    }

    [Fact]
    public void SwitchTo_MoveDuringOneShot_PendingStand_OverridesToMove()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say");
        state.PendingAction = "stand";                // 空闲随机预置的默认回退
        SwitchTo(state, "move");
        Assert.Equal("move", state.PendingAction);    // 用户请求覆盖默认回退
    }

    [Fact]
    public void SwitchTo_MoveDuringOneShot_ExistingUserPending_KeepsFirst()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say");
        state.PendingAction = "fly";                  // 已有用户请求
        SwitchTo(state, "move");
        Assert.Equal("fly", state.PendingAction);     // 保留首次请求，不被后续覆盖
    }

    [Fact]
    public void SwitchTo_StandDuringOneShot_InterruptsImmediately()
    {
        var state = new TestAnimState();
        SwitchTo(state, "say");
        SwitchTo(state, "stand");                     // stand 不受 OneShot 保护
        Assert.Equal("stand", state.CurrentAction);
        Assert.Equal(TestPlayMode.Loop, state.CurrentMode);
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

    // --- R3: 纸娃娃离线兜底动作映射（stand→stand1 等） ---

    [Theory]
    [InlineData("stand", "stand1")]
    [InlineData("walk", "walk1")]
    [InlineData("move", "walk1")]
    [InlineData("fly", "jump")]
    [InlineData("stand1", "stand1")]
    [InlineData("walk2", "walk2")]
    [InlineData("jump", "jump")]
    [InlineData("swingOF", "swingOF")]
    public void MapOfflineAction_AlignsWithOfflineStripNames(string input, string expected)
    {
        Assert.Equal(expected, MapOfflineAction(input));
    }

    [Fact]
    public void IsOfflineActionName_OnlyStripActions()
    {
        Assert.True(IsOfflineActionName("stand1"));
        Assert.True(IsOfflineActionName("walk2"));
        Assert.True(IsOfflineActionName("jump"));
        Assert.False(IsOfflineActionName("stand"));
        Assert.False(IsOfflineActionName("move"));
        Assert.False(IsOfflineActionName("swingOF"));
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
        // A1 语义（与 AnimService.SwitchTo 对齐）：OneShot 表情播放中收到 fly/move →
        // 仅在无 pending 或 pending 为默认回退 "stand" 时记录为 pending，不打断当前表情动画。
        // （原实现 `if (!string.IsNullOrEmpty(pending))` 写反：干净状态 pending 为空时 move/fly 被静默丢弃）
        if (state.CurrentMode == TestPlayMode.OneShot
            && !state.CurrentAction.StartsWith("stand")
            && !state.CurrentAction.StartsWith("fly")
            && !state.CurrentAction.StartsWith("move")
            && (action.StartsWith("fly") || action.StartsWith("move")))
        {
            if (state.PendingAction.Length == 0 || state.PendingAction == "stand")
            {
                state.PendingAction = action;
            }
            return;
        }

        var resolved = action;
        if (!AnimDefs.ContainsKey(action))
        {
            resolved = ResolveFallback(action, new Dictionary<string, string>());
            if (resolved == null) return;
        }

        // 对齐 AnimService.Play：直接播放（非 OneShot 保护分支）即作废残留 pending
        state.PendingAction = string.Empty;
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

    // --- R3: 与 AnimService 离线兜底段一致的最小逻辑副本 ---

    private static bool IsOfflineActionName(string action) =>
        action == "stand1" || action == "stand2" || action == "walk1" || action == "walk2" || action == "jump";

    private static string MapOfflineAction(string action) => action switch
    {
        "stand" => "stand1",
        "walk" or "move" => "walk1",
        "fly" => "jump",
        _ => action
    };

    // ── A15：空闲随机展示池（武器匹配 + 无帧过滤 + OneShot 播完回 stand）──

    [Theory]
    [InlineData("1210000", "swingO")]   // 单手剑
    [InlineData("1220000", "swingO")]   // 单手斧
    [InlineData("1230000", "swingO")]   // 单手钝器
    [InlineData("1240000", "stab")]     // 匕首
    [InlineData("1250000", "stab")]     // 拳套
    [InlineData("1300000", "swingO")]   // 本 WZ 实测 130-132 为 swingO 帧集（非常规双手编号）
    [InlineData("1320000", "swingO")]
    [InlineData("1330000", "swingP")]   // 枪（swingPF 帧）
    [InlineData("1340000", "swingP")]   // 矛
    [InlineData("1370000", "shoot")]    // 弓（shoot1/shootF 帧）
    [InlineData("1380000", "shoot")]    // 弩
    [InlineData("1400000", "swingT")]   // 本 WZ 实测 140-146 为 swingT 帧集
    [InlineData("1410000", "swingT")]   // 对齐 IsTwoHandWeapon
    [InlineData("1460000", "swingT")]
    [InlineData("1530000", "swingT")]   // 本 WZ 实测 153 系 swingT/swingP 帧
    [InlineData("1700000", "swingP")]   // 本 WZ 170 系无展示帧 → 池空兜底
    [InlineData("1800000", "")]         // 未知类型 → 无首选族（走兜底池）
    [InlineData("", "")]
    [InlineData("not_a_number", "")]
    public void WeaponActionFamily_ClassifiesByPart(string weaponId, string expected)
    {
        Assert.Equal(expected, WeaponActionFamily(weaponId));
    }

    [Fact]
    public void WeaponActionFamily_NullWeapon_ReturnsEmpty()
    {
        Assert.Equal("", WeaponActionFamily(null));
    }

    [Theory]
    [InlineData("swingO1", true)]
    [InlineData("swingOF", true)]
    [InlineData("swingT3", true)]
    [InlineData("swingPF", true)]
    [InlineData("shoot1", true)]
    [InlineData("shootF", true)]
    [InlineData("stabO1", true)]
    [InlineData("stabTF", true)]
    [InlineData("stand1", false)]
    [InlineData("walk1", false)]
    [InlineData("alert", false)]
    [InlineData("proneStab", false)]   // 非武器展示动作（趴下攻击）
    public void IsWeaponDisplayAction_OnlyWeaponDisplayNames(string action, bool expected)
    {
        Assert.Equal(expected, IsWeaponDisplayAction(action));
    }

    [Theory]
    [InlineData("swingO1", "swingO", true)]
    [InlineData("swingOF", "swingO", true)]
    [InlineData("swingO1", "swingT", false)]
    [InlineData("swingT3", "swingT", true)]
    [InlineData("swingP1", "swingP", true)]
    [InlineData("shoot1", "shoot", true)]
    [InlineData("stabO1", "stab", true)]
    [InlineData("stabTF", "stab", true)]
    [InlineData("stabTF", "swingT", false)]   // stabT 不属 swingT 族
    public void IsFamilyMatch_MatchesFamilyOnly(string action, string family, bool expected)
    {
        Assert.Equal(expected, IsFamilyMatch(action, family));
    }

    [Fact]
    public void BuildIdlePools_WithSword_FamilyMatchGoesPreferred()
    {
        var available = new List<string>
        {
            "stand1", "walk1", "jump",           // Loop
            "swingO1", "swingO2", "swingO3", "swingOF",
            "stabO1", "shoot1"                   // 武器有帧的其它展示动作 → 兜底池
        };
        // 模拟武器：单手剑（族 swingO），swingO2 该武器无帧（该动画不会画出武器 → 跳过）
        var (loop, preferred, alt) = BuildIdlePools(available, WeaponActionFamily("1230000"),
            a => a != "swingO2");

        Assert.Contains("stand1", loop);
        Assert.DoesNotContain("walk1", loop);   // 2026-08-17：空闲随机不用 walk/fly
        Assert.DoesNotContain("jump", loop);    // jump 也不用——只要站姿 + 攻击展示
        Assert.Contains("swingO1", preferred);
        Assert.Contains("swingO3", preferred);
        Assert.Contains("swingOF", preferred);
        Assert.DoesNotContain("swingO2", preferred);      // 武器无帧 → 跳过换另一个
        Assert.DoesNotContain("swingO1", loop);           // 展示动作不进 Loop 池
        Assert.Contains("stabO1", alt);                   // 非本族但武器有帧 → 兜底池
        Assert.Contains("shoot1", alt);
    }

    [Fact]
    public void BuildIdlePools_NoWeapon_BothDisplayPoolsEmpty()
    {
        var available = new List<string> { "stand1", "walk1", "swingO1", "swingOF" };
        // 无武器 → WeaponHasAction 恒 false（无武器不选武器展示动画）
        var (loop, preferred, alt) = BuildIdlePools(available, WeaponActionFamily(null), _ => false);

        Assert.Contains("stand1", loop);
        Assert.Empty(preferred);   // 无武器 → 首选池空
        Assert.Empty(alt);         // 无武器 → 兜底池空（只 Loop）
    }

    [Fact]
    public void BuildIdlePools_UnknownWeapon_AltPoolTakesOver()
    {
        var available = new List<string> { "stand1", "swingO1", "swingOF", "shoot1" };
        // 未知武器类型（族为空）：首选池空，武器有帧的展示动作全进兜底池（仍有展示）
        var (loop, preferred, alt) = BuildIdlePools(available, WeaponActionFamily("1800000"), _ => true);

        Assert.Contains("stand1", loop);
        Assert.Empty(preferred);
        Assert.Contains("swingO1", alt);
        Assert.Contains("shoot1", alt);
    }

    [Fact]
    public void BuildIdlePools_WeaponAllActionsNoFrames_BothDisplayPoolsEmpty()
    {
        var available = new List<string> { "stand1", "swingO1", "swingO2" };
        // 武器在全部展示动作下都无帧（该动画都不展示武器）→ 展示池空，退化为 Loop
        var (loop, preferred, alt) = BuildIdlePools(available, "swingO", _ => false);

        Assert.Contains("stand1", loop);
        Assert.Empty(preferred);
        Assert.Empty(alt);
    }

    [Fact]
    public void PickIdleAction_DisplayAction_IsOneShot()
    {
        var loop = new List<string> { "stand1", "walk1" };
        var display = new List<string> { "swingO1", "swingOF" };
        var (candidates, chosen, oneShot) = PickIdleAction(loop, display, new List<string>(), "stand1", 1);

        Assert.Equal(3, candidates.Count);           // 排除当前 stand1：walk1 + swingO1 + swingOF
        Assert.Equal("swingO1", chosen);
        Assert.True(oneShot);                        // 展示动作 → OneShot
    }

    [Fact]
    public void PickIdleAction_LoopAction_IsLoop()
    {
        var loop = new List<string> { "stand1", "walk1" };
        var display = new List<string> { "swingO1" };
        var (_, chosen, oneShot) = PickIdleAction(loop, display, new List<string>(), "swingO1", 0);

        Assert.Equal("stand1", chosen);              // 排除当前 swingO1 后第一个候选
        Assert.False(oneShot);                       // Loop 动作 → Loop
    }

    [Fact]
    public void PickIdleAction_PreferredEmpty_UsesAltPool()
    {
        var loop = new List<string> { "stand1" };
        // 首选池空（武器类型未知），兜底池有武器有帧的展示动作 → 用兜底池
        var (candidates, chosen, oneShot) = PickIdleAction(loop, new List<string>(), new List<string> { "swingOF" }, "stand1", 0);

        Assert.Equal(1, candidates.Count);   // 当前 stand1 被排除，只剩兜底 swingOF
        Assert.Equal("swingOF", chosen);
        Assert.True(oneShot);
    }

    [Fact]
    public void PickIdleAction_DisplayPoolEmpty_AlwaysLoop()
    {
        var loop = new List<string> { "stand1", "walk1", "jump" };
        var (candidates, chosen, oneShot) = PickIdleAction(loop, new List<string>(), new List<string>(), "stand1", 0);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("walk1", chosen);
        Assert.False(oneShot);
    }

    [Fact]
    public void PickIdleAction_AllPoolsEmpty_ReturnsNull()
    {
        var (candidates, chosen, _) = PickIdleAction(new List<string>(), new List<string>(), new List<string>(), "stand1", 0);

        Assert.Empty(candidates);
        Assert.Null(chosen);
    }

    [Fact]
    public void OneShotCompletion_EmptyPending_ReturnsProviderStand()
    {
        // TickProvider OneShot 完成：无 pending → ResolveActionForProvider("stand") → stand1/stand2
        Assert.Equal("stand1", ResolveOneShotNext("", "stand1"));
    }

    [Fact]
    public void OneShotCompletion_PendingMove_RunsPendingFirst()
    {
        // OneShot 期间收到 move（SwitchTo 记入 pending）→ 播完先切 pending，不回 stand
        Assert.Equal("walk1", ResolveOneShotNext("walk1", "stand1"));
    }

    [Fact]
    public void IdlePoolKey_IncludesAppearanceHash()
    {
        // A15：纸娃娃 mobId 恒为哨兵，缓存键必须含外观指纹（换武器/外观自动重建展示池）
        Assert.NotEqual(IdlePoolKey("__paperdoll__", "hashA"), IdlePoolKey("__paperdoll__", "hashB"));
        Assert.Equal(IdlePoolKey("100", ""), IdlePoolKey("100", ""));   // mob 路径键稳定
    }

    // --- A15 最小逻辑副本（与 AnimService 对应段一致） ---

    /// <summary>与 AnimService.IsLoopAction 一致：_animDefs 显式 Loop 或 stand*/walk*/fly*/jump* 前缀。</summary>
    private static bool IsLoopAction(string action)
    {
        // 2026-08-17 同步 AnimService：空闲随机不用 walk/fly/move（更多攻击动作）
        if (AnimDefs.TryGetValue(action, out var def) && def.IsLoop)
        {
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

    /// <summary>与 AnimService.GetWeaponActionFamily 一致（本 WZ 实测校正）。</summary>
    private static string WeaponActionFamily(string? weaponId)
    {
        if (string.IsNullOrEmpty(weaponId) || !int.TryParse(weaponId, out var n))
        {
            return "";
        }
        int part = n / 10000;
        if (part >= 121 && part <= 123) return "swingO";   // 单手剑/斧/钝器
        if (part >= 124 && part <= 125) return "stab";     // 匕首/拳套
        if (part >= 130 && part <= 132) return "swingO";   // 本 WZ 实测 swingO 帧集
        if (part >= 133 && part <= 134) return "swingP";   // 枪/矛
        if (part >= 137 && part <= 138) return "shoot";    // 弓/弩
        if (part >= 140 && part <= 146) return "swingT";   // 本 WZ 实测 swingT 帧集
        if (part == 153) return "swingT";                  // 本 WZ 实测 swingT/swingP 帧
        if (part == 170) return "swingP";                  // 本 WZ 170 系无展示帧
        return "";
    }

    private static bool IsWeaponDisplayAction(string action)
    {
        return action.StartsWith("swingO", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("swingT", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("swingP", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("shoot", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("stabO", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("stabT", StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>与 AnimService.BuildIdlePools 一致：族匹配 → 首选池；武器有帧的其它展示 → 兜底池。</summary>
    private static (List<string> loop, List<string> preferred, List<string> alt) BuildIdlePools(
        List<string> available, string family, Func<string, bool> weaponHasAction)
    {
        var loop = new List<string>();
        var preferred = new List<string>();
        var alt = new List<string>();
        foreach (var a in available)
        {
            if (a.StartsWith("die", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.StartsWith("hit", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLoopAction(a))
            {
                loop.Add(a);
                continue;
            }
            if (!IsWeaponDisplayAction(a) || !weaponHasAction(a))
            {
                continue;
            }
            if (family.Length > 0 && IsFamilyMatch(a, family))
            {
                preferred.Add(a);
            }
            else
            {
                alt.Add(a);
            }
        }
        return (loop, preferred, alt);
    }

    /// <summary>与 AnimService.GetRandomIdleAction 一致：首选池非空用首选，否则用兜底池。</summary>
    private static (List<string> candidates, string? chosen, bool oneShot) PickIdleAction(
        List<string> loopPool, List<string> preferred, List<string> alt, string currentAction, int pickIndex)
    {
        var effectiveDisplay = preferred.Count > 0 ? preferred : alt;
        var candidates = new List<string>(loopPool.Count + effectiveDisplay.Count);
        foreach (var a in loopPool)
        {
            if (a != currentAction) candidates.Add(a);
        }
        foreach (var a in effectiveDisplay)
        {
            if (a != currentAction) candidates.Add(a);
        }
        if (candidates.Count == 0)
        {
            return (candidates, null, false);
        }
        var chosen = candidates[Math.Min(pickIndex, candidates.Count - 1)];
        return (candidates, chosen, effectiveDisplay.Contains(chosen));
    }

    private static string ResolveOneShotNext(string pending, string providerStand) =>
        !string.IsNullOrEmpty(pending) ? pending : providerStand;

    private static string IdlePoolKey(string mobId, string hash) => $"{mobId}|{hash}";
}
