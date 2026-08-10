namespace MiniPet.Services.Adapters
{
    /// <summary>
    /// Agent 状态适配器接口。
    /// 实现类负责把数据源（Hermes state.db / CC jsonl / Codex rollout 等）解析为 AgentState，
    /// 经 EventBus.PublishOnUIThread(EventType.AgentStateChanged, AgentStateEvent) 发布到 UI 线程。
    /// 隐私：只发 AgentState + 非内容 Detail，绝不持久化/log/上报 content/reasoning/密钥。
    /// </summary>
    public interface IAgentStateAdapter
    {
        /// <summary>本 adapter 的 Agent 标识（用于 Aggregator tiebreak Hermes > ClaudeCode > Codex）。</summary>
        AgentId Id { get; }

        /// <summary>启动数据源监听（轮询/FSW）。幂等。</summary>
        void Start();

        /// <summary>停止数据源监听并释放资源。幂等。</summary>
        void Stop();
    }
}
