# V0.1.3 多桌宠（Multi-Pet）实施计划

> 分支：`feature/0.1.3`（worktree `.worktrees/0.1.3`，基线 master `2b43d2d` V0.2.0）
> 需求：桌面可以同时存在多只桌宠——可以是不同的纸娃娃桌宠，也可以是不同的怪物 / NPC 桌宠。
> 交互决策（胶水确认）：**只有主宠响应 Agent 特效**；**只有主宠挂地图背景**；入口 **右键菜单 + 设置中心** 都要。

## 1. 目标与验收标准

| # | 验收点 | 判定方式 |
|---|--------|----------|
| A1 | 桌面上可同时存在 N（≥2）只桌宠，实体各不相同（纸娃娃 / Mob / NPC 任意组合） | 实机运行截图 |
| A2 | 每只独立动画：动作、帧、空闲随机互不串台 | 实机观察 + 单测 |
| A3 | 每只独立位置：拖拽各动各的，重启后回到原位 | 实机拖拽 + settings.json 检查 |
| A4 | 每只独立外观：切 Mob/纸娃娃只影响自己 | 实机 + settings.json 检查 |
| A5 | 添加 / 移除 / 设为主宠可用（右键菜单 + 设置中心），最后一只不可移除 | 实机 |
| A6 | 主宠唯一：挂地图背景 + 响应 Agent 特效；切换后旧主宠背景收起 | 实机 |
| A7 | 气泡定向：随机气泡只在自己窗口弹；Agent 状态气泡只在主宠弹 | 实机 |
| A8 | 旧配置（单宠 settings.json）自动迁移为 `pets[0]`，原宠物 / 位置 / 缩放不丢 | 单测 + 实机 |
| A9 | 编译 0 错误；既有 248 个单测全绿 | `dotnet build` / `dotnet test` |
| A10 | 退出：托盘退出 = 全部退出；移除一只不影响其余 | 实机 |

## 2. 架构

### 2.1 数据模型（`Models/AppConfig.cs`）

```csharp
class PetSettings {
    List<PetInstanceConfig> Pets;     // 新增：宠物实例列表（顺序 = 创建顺序，[0] 默认主宠）
    // 旧单值字段保留为「主宠的兼容镜像」：CurrentMobId / CurrentEntityType / CurrentPaperdoll / WindowScale
    string MapId; int MapMode; ...    // 地图背景仍为全局单份（只挂主宠）
}
class PetInstanceConfig {
    string Id;                 // "pet-1"…（稳定标识）
    string EntityType;         // Mob / Npc / player
    string MobId;              // 纸娃娃时为哨兵 "__paperdoll__"
    CharacterAppearance? Paperdoll;
    int WindowX, WindowY;      // int.MinValue = 未设置（启动按序号错开）
    double WindowScale;        // 每只独立缩放
    bool Primary;              // 主宠（唯一）
}
```

迁移：`ConfigService.EnsurePets()` 在 `LoadSettings` 内把旧单值字段迁移成 `pets[0]`（纯内存，首次保存落盘）。

### 2.2 组件与生命周期

- **`Services/PetManager.cs`（新，Singleton）**：持全部 `PetWindow`；`CreateAllFromConfig()` 启动建全部宠物；
  `AddPetLike()` / `RemovePet()` / `SetPrimary()`；`PetsChanged` / `PrimaryChanged` 事件；
  `ApplyPrimaryRole()` 把主宠身份派发给各窗口（背景归属）+ EffectLayer（Agent 特效驱动 + 气泡目标）。
- **方向约定**：PetManager → PetWindow。PetWindow **不反向持有** PetManager，命令经 EventBus 事件
  （`AddPet` / `RemovePet` / `SetPrimaryPet`，payload `PetCommandEvent`）。
- **DI 变化（`App.axaml.cs`）**：`AnimService`、`PaperdollService` 由 Singleton → **Transient**
  （各自持有单一实体状态：`_currentMobId` / `_current`+`Flip`，多只共用必然串台）；
  `PetManager` 新增 Singleton；`desktop.MainWindow = petManager.CreateAllFromConfig()`（主宠）。
  共享无害：`SpriteService`（按 mobId|action 键的纯缓存）、`MapService`（地图背景全局单份）、`WzService`、`CacheManager`。

### 2.3 事件与气泡定向

| 事件 | 旧 payload | 新 payload | 订阅方过滤 |
|------|-----------|-----------|-----------|
| `UserDragStart/End` | 无 | `PetDragEvent(petId)` | 只有该宠物切拖拽动画 |
| `PetChanged` | `(id,name)` | `PetChangedEvent(petId, mobId, name)` | 只有该宠物写最近使用；仅主宠改托盘提示 |
| `OpenSettings` | 无 | `OpenSettingsEvent(petId?)` | null = 主宠；否则只有目标宠物开设置窗 |
| `AddPet/RemovePet/SetPrimaryPet` | — | `PetCommandEvent(petId?)` | PetManager 消费 |

气泡：`BalloonService.Show(..., targetPetId)` + `OnBalloonReady(..., targetPetId)`；取消令牌由单槽改
**按 petId 分槽**（旧实现 B 弹气泡会掐掉 A 的）。`null` = 广播（兼容单宠语义）。

### 2.4 配置写入并发

`SaveSettings` 是整份覆盖写 → 多只宠物各自「读快照→改→写」会互相回滚。新增：

- `UpdateSettings(Action<PetSettings>)`：锁内 Load→mutate→Save（原子读改写）。
- `UpdatePet(petId, Action<PetInstanceConfig>)`：原子更新单只 + 主宠同步旧镜像字段。
- `SaveSettingsKeepingPets(local)`：旧快照调用方（设置中心各 tab）写全局设置时保留磁盘最新 Pets。

## 3. 改动清单（文件级）

| 文件 | 状态 | 内容 |
|------|------|------|
| `Models/AppConfig.cs` | ✅ | `PetInstanceConfig` + `PetSettings.Pets` |
| `Models/PetEvents.cs` | ✅ 新 | 4 个事件载荷 record |
| `Services/EventBus.cs` | ✅ | 3 个命令 EventType |
| `Services/ConfigService.cs` | ✅ | `EnsurePets` / `NewPetId` / `UpdateSettings` / `UpdatePet` / `AddPet` / `RemovePet` / `SetPrimaryPet` / `SaveSettingsKeepingPets` |
| `Services/PetManager.cs` | ✅ 新 | 宠物注册表 + 主宠派发 |
| `Services/BalloonService.cs` | ✅ | `targetPetId` 定向 + 分槽取消令牌 |
| `Services/TrayService.cs` | ✅ | `RegisterPet`/`UnregisterPet`；显示/隐藏/居中作用于全部 |
| `Services/EffectLayerService.cs` | ✅ | `TargetPetId`（Agent 气泡只给主宠） |
| `App.axaml.cs` | ✅ | DI（Transient Anim/Paperdoll + PetManager）、启动建全部宠物、特效绑主宠 |
| `Views/PetWindow.axaml.cs` | ✅ | 身份字段（`_petId`/`_cfg`）、per-pet 配置读写、位置持久化、事件过滤、气泡过滤、主宠角色、移除关闭 |
| `Views/PetWindow.Menu.cs` | ✅ | 「🐾 桌宠」菜单（添加/移除/设主宠）+ 非主宠背景菜单禁用 |
| `Views/PetWindow.Mode.cs` | ✅ | 地图写入改原子更新 |
| `Views/SettingsWindow.*` | 🔄 进行中 | 参数加 `PetManager` + 目标宠物；「桌宠」页；旧快照写入改 `SaveSettingsKeepingPets` |
| `MiniPet.Tests/*` | ⏳ | 多宠物单测（迁移 / 原子更新 / 主宠唯一性） |
| `docs/*` | ⏳ | README / ROADMAP / 本文件验收记录 |

## 4. 已知限制（有意为之）

1. 地图背景（静态图 / 摄像机）只挂主宠——背景窗口是全局单份语义（`MapService` 整图缓存单槽 + 全局层开关）。
2. Agent 特效只作用于主宠（胶水决策）；其余桌宠保持自身随机动画与随机气泡。
3. 每只宠物各有一份 `PaperdollService` 实例 → 纸娃娃位图缓存按实例重复（2-3 只量级可接受）。
4. `SpriteService.MaxMobCache = 5` 未调大：多只不同 Mob 时 LRU 会互相淘汰（性能损耗，非正确性问题）。
5. 进程级单实例锁仍不存在（多开进程会共用 `~/.minipet/settings.json`）——不在本版本范围。

## 5. 构建 / 验证命令

```bash
cd /Users/a502/IdeaProjects/mapleStoryMiniPet/.worktrees/0.1.3
# 编译（沙箱环境必须带 -p:UsedAvaloniaProducts= 跳过 Avalonia 构建遥测写 ~/Library）
"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug -p:UsedAvaloniaProducts=
# 单测
"/Volumes/SSD/C#/dotnet" test MiniPet.Tests/MiniPet.Tests.csproj -c Debug -p:UsedAvaloniaProducts=
# 运行（隔离 HOME，避免污染胶水的 ~/.minipet 配置）
HOME=/tmp/minipet-013-home caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
```

## 6. 实现记录

> 记录时间：2026-09-14｜分支 `feature/0.1.3`（worktree `.worktrees/0.1.3`）
> 本节记录文件级改动清单与验证状态；技术方案见上文 §1–§5。

### 6.1 改动文件清单（文件级）

| 文件 | 状态 | 内容 |
|------|------|------|
| `MiniPet/Models/AppConfig.cs` | 改 | `PetInstanceConfig` + `PetSettings.Pets` |
| `MiniPet/Models/PetEvents.cs` | **新增** | 4 个事件载荷 record |
| `MiniPet/Services/EventBus.cs` | 改 | 3 个命令 EventType |
| `MiniPet/Services/ConfigService.cs` | 改 | `EnsurePets` / `NewPetId` / `UpdateSettings` / `UpdatePet` / `AddPet` / `RemovePet` / `SetPrimaryPet` / `SaveSettingsKeepingPets` |
| `MiniPet/Services/PetManager.cs` | **新增** | 宠物注册表 + 主宠派发 |
| `MiniPet/Services/BalloonService.cs` | 改 | `targetPetId` 定向 + 分槽取消令牌 |
| `MiniPet/Services/TrayService.cs` | 改 | `RegisterPet`/`UnregisterPet`；显示/隐藏/居中作用于全部 |
| `MiniPet/Services/EffectLayerService.cs` | 改 | `TargetPetId`（Agent 气泡只给主宠） |
| `MiniPet/App.axaml.cs` | 改 | DI（Transient Anim/Paperdoll + PetManager）、启动建全部宠物、特效绑主宠 |
| `MiniPet/Views/PetWindow.axaml.cs` | 改 | 身份字段（`_petId`/`_cfg`）、per-pet 配置读写、位置持久化、事件过滤、气泡过滤、主宠角色、移除关闭 |
| `MiniPet/Views/PetWindow.Menu.cs` | 改 | 「🐾 桌宠」菜单（添加/移除/设主宠）+ 非主宠背景菜单禁用 |
| `MiniPet/Views/PetWindow.Mode.cs` | 改 | 地图写入改原子更新 |
| `MiniPet/Views/SettingsWindow.axaml.cs` | 改 | 构造加 `PetManager` + `EventBus` + 目标宠物；主 tab 加「🐾 桌宠」（索引 1）；`PetsChanged` 订阅；切页/配置变更刷新列表 |
| `MiniPet/Views/SettingsWindow.Pets.cs` | **新增** | 「🐾 桌宠」页：列表（实体/主宠/缩放/位置/显示态）、添加 / 移除 / 设主宠、逐只缩放滑块、切目标同步纸娃娃草稿 |
| `MiniPet/Views/SettingsWindow.Paperdoll.cs` | 改 | 保存预设改 `SaveSettingsKeepingPets`；「应用到桌宠」落目标宠物实例（`UpdatePet`）+ 发布定向换装事件 |
| `MiniPet/Views/SettingsWindow.Background.cs` / `.Balloon.cs` / `.Agent.cs` | 改 | 全局设置写入改 `SaveSettingsKeepingPets`（不再整份覆盖宠物列表） |
| `MiniPet.Tests/MultiPetConfigTests.cs` | **新增** | 16 条多宠物配置单测（迁移 / 主宠唯一 / 增删 / 镜像同步 / 不回滚） |
| `docs/ai/PLAN-0.1.3-multi-pet.md` | **新增** | 本实施计划 + 本实现记录 |
| `README.md` | 改 | 功能简介加「🐾 多桌宠」、操作提示加「多桌宠」小节 |
| `docs/ROADMAP.md` | 改 | 版本现状表 + V0.1.3 章节 + 变更记录 |

### 6.2 测试与验证状态

**编译与单测（实测）**
- `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug -p:UsedAvaloniaProducts=` → **0 Error(s)**
- `"/Volumes/SSD/C#/dotnet" test MiniPet.Tests/MiniPet.Tests.csproj -c Debug -p:UsedAvaloniaProducts=` → **Failed: 0, Passed: 264**（基线 248 + 本次新增 16）

**实机运行验收（隔离 HOME `/tmp/minipet-013-home`，未污染 `~/.minipet`）**

| # | 验收点 | 结果 | 实测证据 |
|---|--------|------|----------|
| A1 | 多只桌宠同屏、实体各异 | ✅ | 3 只同屏（纸娃娃 + Mob 8180000 + Mob 9300688），截图与 `CGWindowListCopyWindowInfo` 三窗口并存 |
| A2 | 每只独立动画 | ✅ | 日志各窗口独立 `StartAnimation(pet-N)`、独立 `EnsureBalloonAnchor`、独立随机动作 |
| A3 | 位置独立 + 重启恢复 | ✅ | 拖 pet-2 → `settings.json` 写入 `windowX/Y=652,315`；重启后窗口回到 (652,315)，其余两只位置不变 |
| A4 | 外观/实体独立 | ✅ | 重启后各自恢复（纸娃娃 hair=42540 / 8180000 / 9300688） |
| A5 | 添加 / 移除 / 设主宠 | ✅ | 右键菜单与设置中心两条入口均实测：`添加桌宠 pet-4（复制自 pet-2）`、`移除桌宠 pet-4，剩余 3 只`、`主宠切换为 pet-2`；复制纸娃娃（pet-1 → pet-5）实测：新宠物 `entityType=player` + `paperdoll.hair=42540`（深拷贝成功），同屏共 5 只（2 纸娃娃 + 3 Mob） |
| A6 | 主宠唯一 + 旧字段镜像 | ✅ | 切换后 `pets` 中仅 pet-2 `primary=true`，且镜像字段 `currentMobId=8180000 / currentEntityType=mob` 同步 |
| A7 | 逐只缩放 | ✅ | 设置中心滑块拖 pet-2：`windowScale 0.60→0.50`，pet-1/pet-3 不变，窗口尺寸随之变化 |
| A8 | 旧单宠配置迁移 | ✅ | 写入无 `pets` 的旧配置 → 启动生成 `pet-1`（保留 mobId 9300688 与 `windowScale 1.25`，坐标 (0,0) 视为未设置） |
| A9 | 编译 0 错误 + 单测全绿 | ✅ | 见上方实测输出 |
| A10 | 移除不影响其余 | ✅ | 移除 pet-4 后其余 3 只窗口、动画、位置均正常，配置同步减少 |

**未覆盖（需人工或后续补）**
- Agent 状态特效「只驱动主宠」仅做代码路径确认（`PetManager.PrimaryChanged → EffectLayerService.TargetPetId/ActionDriver`），实机验收时 `agentIntegration.enabled=false`，未跑真实 Hermes/DSH 状态流。
- 非主宠右键菜单「背景」显示为不可用状态（代码路径确认），未单独截图。
- 托盘「显示全部桌宠 / 重置到桌面中央（全部错开）」未在 macOS 菜单栏点验（代码路径确认）。

### 6.3 独立审查与修复（2026-09-14）

对 `git diff master` 全量做了一次独立只读审查（另起 agent），发现并已修：

| 级别 | 问题 | 修法 |
|------|------|------|
| 阻断 | 设置中心「📦 桌宠设置 → 确定应用」仍走「回调创建者」的旧链路：编辑目标 ≠ 创建该设置窗的宠物时，**两只都会被改成同一实体**（配置写目标、屏幕切创建者） | 新增定向事件 `EventType.EntityApplied`（`PetEntityAppliedEvent(petId, mobId, name, type)`）：设置中心按 `_targetPetId` 发布，PetWindow 按 `_petId` 过滤执行（与换装 `PaperdollApplied` 同构） |
| 重要 | 移除桌宠不释放资源：`Window.Close()` 不产生 `IsVisible` 变化 → `OnPetHidden` 不跑，30fps 帧循环 + 随机气泡调度继续运行；`BalloonService.OnBalloonReady` 匿名订阅无法退订 → 整棵窗口对象图不回收；`_settingsWindow.Close()` 被「关闭=隐藏」取消 → 常驻隐藏窗口 | `CloseForRemoval`/`OnClosed` 显式停 `_timer`/`_balloonTimer`/`_stopwatch`；气泡回调改命名字段订阅并在移除/关闭时 `-=`；`SettingsWindow` 新增 `ForceCloseForRemoval()` 真正销毁并退订 `PetsChanged` |
| 重要 | 纸娃娃「应用到桌宠」在目标 ≠ 创建者时，「✅ 应用到桌宠」按钮永久停在「⏳ 合成中…」（busy 解除走的是目标宠物自己的设置窗口） | 新增 `EventType.PaperdollAppliedDone`（payload = petId）：换装完成后广播，发起换装的设置窗口按 `_targetPetId` 匹配后解锁 |
| 重要 | 素材浏览器「保存搭配」仍用弹窗前旧快照 `SaveSettings(cfg)` 整份写回 → 弹窗期间其他宠物的位置/实体/外观被回滚 | 改 `SaveSettingsKeepingPets(cfg)` |
| 重要 | 「只有主宠挂地图背景」只拦了右键菜单，设置中心背景页「应用到主窗口」/点历史项仍可让非主宠挂上背景 | `ApplyMapSelection` 开头加 `_isPrimaryRole` 校验，非主宠弹提示并拒绝 |
| 重要 | `MiniPet.CrashSelfTest/SettingsPreviewSelfTest.cs` 仍按 5 参构造 `SettingsWindow` → 该项目编译失败（CS7036） | 补 `PetManager` / `EventBus` / `targetPetId` 参数；`MiniPet.CrashSelfTest.csproj` 实测 0 错误 |
| 次要 | `TrayService` 新增的 4 处 `if (…) return;` 未加花括号（违反项目铁律 S6）；`_removing` / `_mainWindow` / `SetAllVisible` / `AllHidden` 为死代码（S7）；`EnsurePets` 对手改配置里的 `null` 元素会 NRE（S10）；配置坐标来自已拔掉副屏时桌宠会落在屏外（S12）；Agent 非空闲会压掉**所有**桌宠的随机气泡（S4，与 PLAN 口径不符） | 逐项修：补花括号、删死代码、`RemoveAll(p => p == null)`、`IsPointOnAnyScreen` 回退默认摆放、非主宠忽略 `PetReflectStateChanged` |

**审查确认无问题**（摘录）：主宠移除/切换后不悬空驱动、Closed 不重复派发、5 条定向事件过滤齐全、`UpdateSettings`/`UpdatePet`/`AddPet`/`RemovePet`/`SetPrimaryPet` 原子性、迁移与主宠唯一性自愈、per-pet 缩放退订、`AnimService`/`PaperdollService` 的 per-pet 配对、地图 PNG 缓存 key 修正、气泡取消令牌分槽。

**未修（有意保留，均为既有行为或设计取舍）**
- `EventType.ConfigChanged` 处理里的 `SaveSettings(LoadSettings())` 非原子，且全仓无发布方（死路径）——保留不动。
- 主宠切换后新主宠**不会自动接管**背景（旧主宠背景收起、新主宠需再点一次「开启背景」）——README 已按此措辞描述。
- `EffectLayerService` 注入的 `PaperdollService` 在 Transient 化后是独立实例（`ExpressionDriver` 装在其上，当前无行为差异）——已在代码注释标注，将来接真实表情驱动时需随 `PrimaryChanged` 绑定主宠实例。
- `BalloonService._hideCtsMap` 不回收槽位；`launch-settings.sh` 的 `StartupObject=SettingsPreviewSelfTest` 路径本就无 `Main`（master 同样如此，非本次引入）。
- 设置中心「📦 桌宠设置 → 确定应用」的实机点击验收未走通（该环境鼠标自动化点击不稳定），机制与已实机验证过的换装定向同构，**建议人工复验一次**。
