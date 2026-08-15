using System;

namespace MiniPet.Services
{
    /// <summary>
    /// Agent 活动状态（对齐 Hermes 官方 Pets 7 态语义：借语义不借 petdex spritesheet 格式）。
    /// 顺序仅作优先级参考（活动级排序见 AgentStateAggregator）。
    /// </summary>
    public enum AgentState
    {
        Idle,
        Run,
        Review,
        Wave,
        Jump,
        Failed,
        Waiting
    }

    /// <summary>
    /// Agent 标识。Key 大小写敏感，作为 Aggregator 各源流的 key。
    /// 提供静态常量便于 adapter 与 Aggregator 引用（tiebreak DSH > Hermes > ClaudeCode > Codex，
    /// 2026-08-15 起 DSH 最优先：本机主 harness，状态信号最权威，见 RESEARCH-dsh-http-bridge.md §5）。
    /// </summary>
    public readonly record struct AgentId(string Key)
    {
        public static readonly AgentId Dsh = new("dsh");
        public static readonly AgentId Hermes = new("hermes");
        public static readonly AgentId ClaudeCode = new("claude-code");
        public static readonly AgentId Codex = new("codex");
    }

    /// <summary>
    /// Agent 状态事件：adapter 解析数据源后，经 EventBus.PublishOnUIThread(EventType.AgentStateChanged, ...) 发布。
    /// Detail 仅承载非内容摘要（绝不持久化/log/上报 content/reasoning/密钥）。
    /// </summary>
    public sealed record AgentStateEvent(AgentId Source, AgentState State, string? Detail, DateTime Ts);
}
