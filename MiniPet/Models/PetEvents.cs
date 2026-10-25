namespace MiniPet.Models;

/// <summary>
/// PetChanged 事件载荷（V0.1.3 多桌宠定向）：<see cref="PetId"/> 标识发起切换的那只宠物，
/// 只有它自己更新托盘提示 / 最近使用记录（旧版无源广播会让所有宠物互相污染）。
/// </summary>
public sealed record PetChangedEvent(string PetId, string MobId, string Name);

/// <summary>拖拽开始 / 结束事件载荷：只有 <see cref="PetId"/> 对应的那只切换拖拽动画。</summary>
public sealed record PetDragEvent(string PetId);

/// <summary>打开设置中心事件载荷：<see cref="PetId"/> 为空 = 主宠（托盘入口）。</summary>
public sealed record OpenSettingsEvent(string? PetId);

/// <summary>
/// 纸娃娃外观应用事件载荷（设置在设置中心点「应用到桌宠」时发布）：
/// 只有 <see cref="PetId"/> 对应的那只宠物窗口执行换装——设置中心的编辑目标可以不是
/// 创建该窗口的那只宠物，旧链路（OnPaperdollApplied 直接回调创建者）会把创建者也一起改掉。
/// </summary>
public sealed record PetPaperdollEvent(string PetId, CharacterAppearance? Appearance);

/// <summary>
/// 宠物命令载荷（添加 / 移除 / 设为主宠）：由 <see cref="Services.PetManager"/> 消费。
/// <see cref="PetId"/> 为空时语义由命令决定（AddPet = 复制主宠；RemovePet/SetPrimaryPet = 忽略）。
/// </summary>
public sealed record PetCommandEvent(string? PetId);

/// <summary>
/// 实体切换事件载荷（设置中心「📦 桌宠设置」页点「确定应用」时发布）：
/// 只有 <see cref="PetId"/> 对应的那只宠物窗口切实体。与换装（PetPaperdollEvent）同构——
/// 设置中心的编辑目标可以不是「创建该设置窗口的那只宠物」，旧链路直接回调创建者会把两只一起改掉。
/// </summary>
public sealed record PetEntityAppliedEvent(string PetId, string MobId, string Name, string EntityType);
