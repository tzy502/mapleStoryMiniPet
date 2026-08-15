using System;
using System.Collections.Generic;
using MiniPet.Services;
using MiniPet.Services.Adapters;

namespace MiniPet.Tests;

/// <summary>
/// HermesAdapter 白盒单元测试（PLAN Rev6 5 态启发式）。
/// 直接调 internal MapState / BuildDefaultConnStr，验证：
///   1) 求值顺序 Failed → Wave → reasoning Review → unresolved-tool Run → busy Run → Idle
///      （2026-08-15：Review 提前到 Run 前——Deepseek 思考行同时带 reasoning+tool_calls）
///   2) 各状态边界（Wave 窗口）
///   3) 隐私：Detail 只含非内容摘要
///   4) profile + HERMES_HOME 的 state.db 路径解析
/// 数据全部构造，不连真实 state.db。
/// </summary>
public class HermesAdapterTests
{
    private static HermesAdapter.HermesSnapshot Snapshot(
        bool contentFilter = false,
        bool unresolvedTool = false,
        int reviewCount = 0,
        int recentEndedCount = 0,
        TimeSpan? endedAge = null,
        bool recentActive = false)
    {
        var active = new List<HermesAdapter.ActiveSessionRow>();
        if (recentActive)
        {
            // last_activity 3 秒前：在 10s 新鲜窗口内 → busy Run 信号。
            active.Add(new HermesAdapter.ActiveSessionRow(DateTime.Now.AddSeconds(-3), null, null, true));
        }

        var ended = new List<HermesAdapter.EndedSessionRow>();
        for (int i = 0; i < recentEndedCount; i++)
        {
            var age = endedAge ?? TimeSpan.FromSeconds(2);
            ended.Add(new HermesAdapter.EndedSessionRow(DateTime.Now.Subtract(age), "cron_complete"));
        }

        var messages = new HermesAdapter.MessageSignals(contentFilter, unresolvedTool, false, reviewCount);
        return new HermesAdapter.HermesSnapshot(active, ended, messages);
    }

    // ── 求值顺序：Failed → Wave → unresolved-tool Run → reasoning Review → busy Run → Idle ──

    [Fact]
    public void ContentFilter_最高优先_Failed()
    {
        // 全信号都齐 → 仍 Failed（Failed 在链首）。
        var snap = Snapshot(contentFilter: true, unresolvedTool: true, reviewCount: 3, recentEndedCount: 1, recentActive: true);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Failed, state);
    }

    [Fact]
    public void Wave_优先于_Run_和_Review()
    {
        var snap = Snapshot(unresolvedTool: true, reviewCount: 2, recentEndedCount: 1, recentActive: true);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Wave, state);
    }

    [Fact]
    public void Review_优先于_UnresolvedTool_Run()
    {
        // 2026-08-15 修复：Deepseek 思考行同时带 reasoning_content + tool_calls（思考完直接调工具），
        // 旧顺序 unresolved-tool Run 优先 → Review 永不触发（用户反馈「思考时桌宠没反应」）。
        // 有 reasoning = 正在思考 → Review 优先于 Run（思考中气泡 + 随机 skill 动作）。
        var snap = Snapshot(unresolvedTool: true, reviewCount: 3);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Review, state);
    }

    [Fact]
    public void Review_先于_busy_Run()
    {
        // Codex R5 F1 修复验证：Review 必须在 busy→Run 兜底之前判定，
        // 否则活跃 reasoning 会话会被 busy→Run 吞掉、Review 永远不可达。
        var snap = Snapshot(reviewCount: 1, recentActive: true);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Review, state);
    }

    [Fact]
    public void BusyRun_最后兜底()
    {
        var snap = Snapshot(recentActive: true);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Run, state);
    }

    [Fact]
    public void 无任何信号_Idle()
    {
        var snap = Snapshot();
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Idle, state);
    }

    [Fact]
    public void Wave_窗口边界外_不触发()
    {
        // ended 超过 10s 窗口 → 不 Wave；无 recentActive → Idle。
        var snap = Snapshot(recentEndedCount: 1, endedAge: TimeSpan.FromSeconds(11));
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Idle, state);
    }

    [Fact]
    public void Completed_回合完成_Wave()
    {
        // 最新 assistant 行 finish_reason='stop' → 回合完成 → Wave（不依赖 session ended_at）。
        var messages = new HermesAdapter.MessageSignals(false, false, true, 0);
        var snap = new HermesAdapter.HermesSnapshot(new List<HermesAdapter.ActiveSessionRow>(), new List<HermesAdapter.EndedSessionRow>(), messages);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Wave, state);
    }

    [Fact]
    public void Completed_优先于_UnresolvedTool_Run()
    {
        // 完成信号与 unresolved-tool 同帧 → Wave 优先（完成是最终态）。
        var messages = new HermesAdapter.MessageSignals(false, true, true, 0);
        var snap = new HermesAdapter.HermesSnapshot(new List<HermesAdapter.ActiveSessionRow>(), new List<HermesAdapter.EndedSessionRow>(), messages);
        var (state, _) = HermesAdapter.MapState(snap);
        Assert.Equal(AgentState.Wave, state);
    }

    // ── 隐私：Detail 只含非内容摘要，绝不带 content/reasoning ──

    [Theory]
    [InlineData(true, false, 0, 0)]   // Failed → "failed: 1"
    [InlineData(false, true, 0, 0)]   // Run → "active sessions: 0"
    [InlineData(false, false, 2, 0)]  // Review → "review: 2"
    [InlineData(false, false, 0, 1)]  // Wave → "active sessions: 0"
    public void Detail_不含content与reasoning字段(bool cf, bool ut, int rc, int ra)
    {
        var snap = Snapshot(contentFilter: cf, unresolvedTool: ut, reviewCount: rc, recentEndedCount: ra);
        var (_, detail) = HermesAdapter.MapState(snap);
        Assert.DoesNotContain("content", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reasoning", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_calls", detail, StringComparison.OrdinalIgnoreCase);
    }

    // ── BuildDefaultConnStr：profile + HERMES_HOME 路径解析（Codex R3 F4）──

    [Fact]
    public void Profile空_默认根()
    {
        Environment.SetEnvironmentVariable("HERMES_HOME", null);
        var cs = HermesAdapter.BuildDefaultConnStr(null);
        Assert.Contains(".hermes/state.db", cs.Replace("\\", "/"));
        Assert.Contains("Mode=ReadOnly", cs);
        Assert.Contains("Pooling=false", cs);
    }

    [Fact]
    public void Profile非空_路径含profiles()
    {
        Environment.SetEnvironmentVariable("HERMES_HOME", null);
        var cs = HermesAdapter.BuildDefaultConnStr("work-java");
        Assert.Contains("profiles/work-java/state.db", cs.Replace("\\", "/"));
    }

    [Fact]
    public void HERMES_HOME_优先于默认根()
    {
        Environment.SetEnvironmentVariable("HERMES_HOME", "/tmp/hermes-test-home");
        try
        {
            var cs = HermesAdapter.BuildDefaultConnStr(null);
            Assert.Contains("/tmp/hermes-test-home/state.db", cs.Replace("\\", "/"));

            var csP = HermesAdapter.BuildDefaultConnStr("p1");
            Assert.Contains("/tmp/hermes-test-home/profiles/p1/state.db", csP.Replace("\\", "/"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
        }
    }
}
