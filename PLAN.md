# Plan: 纸娃娃模块重新设计（实时合成 + 统一素材选择库 + 完整外观）
_Locked via grill-with-docs — by Claude + 胶水. Terms per CONTEXT.md._
_Rev 1 — Act 2 Codex Round 1 意见 9/9 采纳（F1–F9；其中 F5 修正采纳）。_

## Goal
重做桌宠纸娃娃模块。渲染核心从「预合成精灵图条 + 哨兵 mobId」（ADR-0002，已废弃）改为**实时部件合成**：修复耳朵选择/渲染、支持全部动作、眨眼表情 + 外部表情驱动、皮肤/染发、z-map 遮挡锁；选择 UI 从手输 ID 升级为**统一素材选择库**（保留怪物/NPC 分类，纸娃娃分类 WZ 真实加载 + 穿在身上合成预览 + 草稿模式）；搭配持久化 + 最近搭配离线缓存（WZ 不可用时仍可显示）。

## Approach

### 1. 渲染内核重写 — 实时合成（`MiniPet/Services/PaperdollService.cs`）
- 弃用 `BuildActionStrip` / `BuildAndCacheAppearance` / 哨兵精灵图条路径；新增 `RenderFrame(action, frame, expression)` 逐帧合成 → `(SKBitmap frame, int originX, int originY, int frameW, int frameH, int delay)`。
- 合成管线对齐 MapleSalon2 `characterBodyFrame` / `characterFaceFrame`：
  1. **收集 pieces**：遍历 body/head/hair/face/各装备根，按 `{action}/{frame}`（face 按 `{expression}`）取 frame 节点，读 piece 名/PNG/origin/map 锚点/z 字段。帧不存在跳过（现逻辑保留）。**补齐可渲染部位（F6）**：`EnumerateRoots` 增 Earring；105xxxx(Longcoat/Overall) 单独映射（现 `CharacterAppearance` 缺字段，104=Coat / 105=Overall 拆开，Coat+Pants 冲突处理对齐 MapleSalon2 `selectNewItem`）。
  2. **锚点解算**：`navel` 为种子，迭代式 `buildAncher`（上限 piece 数×4），piece 对齐到全局锚点表（对齐 MapleSalon2 `setAncher`）。
  3. **层排序（F4）**：精确移植 MapleSalon2 `resolveUseablePieceName` + `ZmapIndex`——除现有 `ResolveLayer` 的 z 字段/部位名映射外，补齐**数值 z、`Below/Under/Over/Above` 名、`ZmapIndex.fixLayers` 回退表**；层内按 (Z,Id) 排序。
  4. **遮挡锁（F3）**：精确移植 MapleSalon2 `buildLock` / `refreshLock`（character.ts:865 / characterZmapContainer.ts:24）——`buildLock` 按 **item ID 升序 + zmap 顺序**锁 vslot；`refreshLock` 同时判 **self-vslot 与 layer-vslot**，覆盖 `mailArm`/`mailChest`/`pants`+`backPants`/`backHead`(强制 `Hd`)/cap(vslot)/**ear-fix**(`accessoryOverHair`/`hairShade` 层锁 `Hd`) 全部分支。WzService 新增 smap/islot/vslot 读取。
  5. **union canvas 每帧**：该 action 各帧共用联合画布 + 公共 origin（保住窗口不抖，复用现窗口补偿逻辑），每帧把部件画进统一坐标系。
- **耳朵模型（核心修复）**：`EarType` 改为字符串（`ear`/`humanEar`/`lefEar`/`highlefEar`，对齐 `CharacterEarType`）；head 解析时 **key 以 `ear` 结尾**的 canvas 保留原始 piece 名分组；运行时按 `earType` 取组，`|| []` 空组不画——人类默认靠 head 自带圆耳（`humanEar` 为空图则自然不画）。删除现有 `ExpectedEarPiece` 过滤逻辑。耳朵 piece 保留自带 z 字段插入 zmap。
- **表情系统**：新增 `Expression`（default/blink），独立脸帧合成（对齐 `characterFaceFrame`）；随机眨眼交替；`isOverrideFace`（脸部饰品 `invisibleFace=1` 覆盖脸）、`isHideFace`（blink/hide 强制隐藏脸层）。
- **表情驱动接口（预留）**：定义 `IExpressionDriver`（默认=随机眨眼循环）；未来 Hermes/Claude/Codex gateway 接入后可按 agent 状态切换表情（通过接口或 EventBus 事件注入，不依赖 gateway 实现）。⚠️ blink 为**多帧脸表情**时，`RenderFrame`/`IEntityFrameProvider` 需 **face-expression 帧索引或内部表情时间线**（对齐 MapleSalon2 `CharacterFaceFrame` 按 expression+frame key，characterFaceFrame.ts:99）。
- **皮肤（F5，修正采纳）**：对齐 MapleSalon2 —— 皮肤 = **Body/Head 槽位 item id**（body 2000–2999，head = body+10000）；素材库「皮肤」分类枚举 **WZ 实际存在的 body**，换肤成对更新 Body+Head。Gender 保留用于装备过滤（对齐 `getGender` 推断）。⚠️ 现行 `Gender→2000/2001` 硬编码仅两种 body，须扩展为 id 选择；**不采用 Codex R1 的 female=12000/22000 数值**（经核实为误读——`store.ts` 的 12000 是默认 Head id）。实现时用诊断日志确认桌宠 WZ 的 body 实际范围。
- **染发**：`DyeablePiece` HSV 染色管线（`ItemInfo.Dye/Hue/Saturation/Brightness`），染色结果缓存（按 itemId+HSV key）。
- **并发安全（F7）**：`RenderFrame` 接收**不可变外观快照**（不可变 DTO），每渲染走代际 state；**禁止共享可变 `_current`**（背景预览后台 Task 与主窗口帧循环并发会渲染错外观）。

### 2. AnimService 帧提供者（`MiniPet/Services/AnimService.cs`）
- 新增**帧提供者接口**（如 `IEntityFrameProvider`：`GetFrame(action, frame, expression)` / `GetFrameCount(action)` / `GetAvailableActions()` / `IsAnimating`）。
- `Tick` 分支：`_currentMobId == PaperdollService.SentinelMobId` 时帧内容来自 PaperdollService 实时合成（含 delay/帧推进）；**Mob/NPC 走图条裁剪路径不变**。改动必须对 Mob/NPC **加法式**（不破坏现有行为）。
- **保留全部状态机行为**：空闲随机动作、拖拽 walk1/fly 切换、OneShot 播完回 stand。
- **武器动作映射**：对齐 MapleSalon2 `updateActionByWeapon`（单手/双手/枪 → stand/walk 变体映射，无对应动作回退）。
- **拖拽随机 fly/swing**：拖拽时从 fly/swing/walk 中随机（用户要求）。
- **哨兵调用点全量迁移（F1）**：空闲随机动作（`AnimService.GetRandomAction`）、拖拽分支（PetWindow.axaml.cs:211）、动画菜单（PetWindow.Menu.cs:230）、气泡锚点——所有经 `SpriteService`/`GetCachedActionList` 解析哨兵 action/frame/origin/delay 的调用点**全部改走 `IEntityFrameProvider`**。⚠️ **清扫须显式含** `AnimService.Play`/`HasSpriteForAction`/`LoadStripWithFallback` 与 `PetWindow.StartAnimation`（启动 `Play("stand")` 仍走旧图条门——AnimService.cs:84 / PetWindow.axaml.cs:342）。
- **player 动作定义（F2）**：`_animDefs` 增纸娃娃动作（`stand1`/`walk1`/`swingOF`/`stabOF`…）的循环/回退定义；OneShot 完成回退目标由**硬编码 `stand`** 改为按实体解析（纸娃娃 `stand→stand1` 别名）。

### 3. 显示路径（`MiniPet/Views/Controls/SpriteCanvas.axaml.cs` + `PetWindow.axaml.cs` OnTimerTick）
- `SpriteCanvas` 增加**位图直拷路径**：`SKBitmap` → `WriteableBitmap` BGRA 直拷（对齐 `MapViewportWindow` 现有做法，透明背景 premultiplied alpha），免每帧 PNG 编解码。
- `OnTimerTick`：纸娃娃帧走直拷；Mob/NPC 保持 PNG 路径。窗口尺寸/origin 补偿复用现有逻辑（union canvas 公共 origin 不变）。

### 4. 统一素材选择库（`MiniPet/Views/MaterialBrowserWindow.cs` 重做）
- **保留怪物/NPC 分类**：`LoadFromWz`（`ListMobIds`/`GetMobName`/`ListNpcIds`/`GetNpcName`）、条目缩略图（stand 首帧懒加载）、详情预览、选择逻辑原样。
- **补齐并重做纸娃娃分类**：「发型/脸型/装备（按部位细分 Cap/Coat/Overall/Pants/Shoes/Glove/Weapon/Shield/Cape/Earring/Accessory）/坐骑/椅子」**从 WZ 真实加载**，替换死分类（现分类按钮 8 类但数据只有怪物/NPC，`BuildSampleData` 假数据未启用）。**枚举统一（F9）**：用单一 `_Canvas` 枚举器 + `String.wz` 名称 + 分页（现 `GetEquipList` 枚举节点不同且 cap 200 条，须替换）；发型/脸型/装备按 `getSubCategory` 归一化（Coat/Overall 冲突处理）。
- **穿在身上合成预览**：选中部件 → 后台按当前草稿合成缩略图（`RenderFrame`），按外观哈希缓存（对齐 MapleSalon2 `SimpleCharacter`）。
- **草稿模式（未提交变更）**：选件只改素材库内草稿（预览区实时合成），点「应用到主窗口」才切换桌宠 + 持久化（对齐 MapleSalon2 `$currentItemChanges` / `applyCharacterChanges`）。
- **皮肤 + 染发 UI**：素材库加皮肤选择（body/head 成对）、发型染发（HSV 调节）。
- **预设/搭配管理**：素材库内保存/加载/重命名/删除搭配（走 §5 持久化）。
- 入口：右键菜单「切换人物」改为打开统一素材选择库；设置中心纸娃娃 tab **保留手输表单为精确输入高级模式**（不删）。

### 5. 持久化 + 离线缓存（`MiniPet/Models/AppConfig.cs` / `CacheManager`）
- **搭配 JSON 持久化**：预设列表 + 当前搭配（`CharacterAppearance` 全字段含 earType/skin/dye），启动时若上次实体为纸娃娃则按搭配重渲染。
- **最近搭配渲染缓存（F8）**：最近一套的合成结果（动作 PNG/精灵）本地持久化；**缓存按外观哈希 key，搭配变化即失效/重建**；WZ 不可用时经 `IEntityFrameProvider` 离线回退用缓存播放（复用 `CacheManager.SaveSpriteStrip` 哨兵路径）。

## Key decisions & tradeoffs
- **实时合成 vs 合成图条**（[ADR-0005](docs/adr/0005-paperdoll-real-time-compositing.md)，取代 ADR-0002）：实时切换/任意动作/表情，代价=每帧合成成本须 ≤30fps 预算（部件/PNG 缓存）。
- **帧提供者接口**：AnimService 不再禁改但改动加法式；Mob/NPC 不受影响，状态机全保留。
- **耳朵按 WZ piece 名分组**（非枚举映射）：人类靠 head 圆耳、空组不画，天然消化 `humanEar` 空图问题。
- **WriteableBitmap 直拷**：免 PNG 编解码，与 MapViewportWindow 一致。
- **草稿模式**：选件高频，重建成本限制在素材库预览区。
- **统一素材库**：单一入口，保留 Mob/NPC，纸娃娃分类 WZ 真实加载。
- **搭配持久化 + 离线缓存**：重启恢复 + WZ 断线可用。

## Risks / open questions
- **WZ 版本差异**：`humanEar` 是否空图（注释称空图）——已定人类靠 head 圆耳，实现时用现有 `[Paperdoll DBG]` 诊断日志确认 head 各 canvas 实际内容。
- **smap / islot / vslot 数据源位置**：需确认桌宠 WZ 中 `Character.zmap` / `Smap` / item `info/islot`、`info/vslot` 的读取路径（WzService 需新增读取方法）。
- **实时合成每帧成本**：30fps 下全动作合成是否达标；需按需缓存 piece SKBitmap 与 zmap/smap。
- **染发性能**：HSV 染色结果必须缓存，避免每帧染色。
- **表情驱动接口**：gateway（Hermes/Claude/Codex）尚未连入，接口先定义、默认随机眨眼，不实现具体 agent 状态。

## Out of scope
- 完整表情集（hit/angry/… 全部表情动作）——本轮仅 default/blink。
- extraParts 现金效果（兽耳/尾巴 501xxxx 等）——与 head 自带耳朵是两套系统，本轮不做。
- 坐骑/椅子的完整动画渲染（本轮做选择与挂件级显示，完整坐骑动画另议）。
- 地图/背景/气泡模块不动。
