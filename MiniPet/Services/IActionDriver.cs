namespace MiniPet.Services;

/// <summary>
/// 实体动作驱动接口（PLAN-agent-integration.md §2 expression gateway 扩展）。
///
/// <see cref="PaperdollService.IExpressionDriver"/> 只驱动「表情名」（default/blink），
/// 不切换实体动作（stand/walk1/move）。本接口补齐「动作切换」维度，供
/// <see cref="EffectLayerService"/> 在 Agent <see cref="AgentState.Run"/> 时把
/// 桌宠实体真正切到忙碌动作，Run 退出（→Idle）回 stand。
///
/// 实现方（<see cref="AnimService"/>）持有动作状态机与当前实体类型判定：
/// 纸娃娃（<see cref="PaperdollService.SentinelMobId"/> 哨兵 + provider）走别名解析
/// （move→walk1），Mob/NPC 走精灵图条（动作名透传）。
///
/// 调用方只需传语义动作名，实现内部按当前实体路由：
/// <list type="bullet">
/// <item><see cref="PlayAction"/>("move")：纸娃娃→walk1，Mob→move，NPC→move 或回退 stand</item>
/// <item><see cref="RestoreDefault"/>：回 stand（纸娃娃解析为 stand1）</item>
/// </list>
/// 对齐 <see cref="AnimService.SwitchTo"/>（自动判 Loop/OneShot、pending 合并），
/// 不破坏 OneShot 动作（say/laugh/skill 等）的现有播放流程。
/// </summary>
public interface IActionDriver
{
    /// <summary>
    /// 切到指定语义动作。实现内部按当前实体类型解析：
    /// 纸娃娃 → <see cref="AnimService"/> 的 provider 别名（move→walk1 等）；
    /// Mob/NPC → 透传动作名（move/fly），不可用时走 AnimService 现有 fallback 链回 stand。
    /// 若当前正在播 OneShot 动作（非 stand/fly/move），按 <see cref="AnimService.SwitchTo"/>
    /// 现有 pending 语义延迟到 OneShot 播完再切，不打断表情动画。
    /// </summary>
    /// <param name="action">语义动作名（如 "move" / "walk1" / "fly"）。</param>
    void PlayAction(string action);

    /// <summary>
    /// 恢复默认动作（stand）。Run 退出（→Idle）时调用。
    /// 纸娃娃解析为 stand1/stand2（武器变体由 provider 决定）。
    /// </summary>
    void RestoreDefault();
}
