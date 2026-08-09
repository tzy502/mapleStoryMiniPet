# Plan Review Log

## Iteration 3 — 纸娃娃模块重新设计（实时合成 + 统一素材选择库）
Act 1 (grill-with-docs) complete — plan locked，CONTEXT.md 增「部件/耳朵类型/表情/皮肤/染发/遮挡锁/搭配/实时合成」术语，新增 [ADR-0005](docs/adr/0005-paperdoll-real-time-compositing.md)（取代 ADR-0002）。MAX_ROUNDS=5。
11 项决策：①实时合成弃图条；②AnimService 帧提供者接口(加法式)；③耳朵按 WZ piece 名分组(人类靠 head 圆耳)；④统一素材选择库重做(保留 Mob/NPC)；⑤眨眼表情+外部驱动接口；⑥全部动作+武器映射+拖拽随机 fly/swing；⑦zmap 遮挡 locks；⑧搭配持久化+离线缓存；⑨皮肤+染发；⑩草稿模式；⑪WriteableBitmap 直拷。

## Round 1 — Codex（thread=019fe6a8-7a0e-7471-81eb-b8c06f58da83，model=glm-5.2）
VERDICT: **REVISE** — 9 findings：
- **F1（高）**：哨兵调用点未迁移——空闲随机(AnimService.cs:308)、拖拽(PetWindow.axaml.cs:211)、动画菜单(PetWindow.Menu.cs:230)、气泡锚点仍经 `SpriteService`/`GetCachedActionList` 解析哨兵动作。→ 全部改经 `IEntityFrameProvider`。
- **F2（高）**：player 动作破坏现有状态机——纸娃娃动作 `stand1`/`walk1`/`swingOF` 不在 `_animDefs`；OneShot 完成硬编码回 `stand`(AnimService.cs:199)。→ 加 player 动作定义/别名(`stand→stand1`)、OneShot 回退按实体解析。
- **F3（高）**：locks 算法未明确——`buildLock` 按 item ID 升序+走 zmap 锁 vslot；`refreshLock` 双查 self/layer-vslot + `mailArm`/`mailChest`/`pants`/`backPants`/`backHead`/cap/ear-fix 分支。→ 移植精确算法，非仅"smap+islot/vslot"。
- **F4（高）**：layer resolution 不完整——`ResolveLayer` 缺数值 z、`Below/Under/Over/Above`、`ZmapIndex.fixLayers`。→ 移植 `resolveUseablePieceName` + `ZmapIndex`。
- **F5（高/修正采纳）**：female body/skin 映射——Codex 称 MapleSalon2 用 female 12000/head 22000；**核实为误读**（store.ts:47 的 12000 是默认 Head id）。采核心：皮肤=Body/Head 槽 id(2000–2999 / +10000)，拒绝错误数值。
- **F6（高）**：Earring/Overall 可选不可渲染——`EnumerateRoots` 缺 Earring、105xxxx 无字段。→ 加字段与前缀映射。
- **F7（高）**：共享合成器状态竞态——预览/帧循环并发改 `_current`。→ `RenderFrame` 用不可变外观快照+代际 state。
- **F8（中）**：离线缓存与重写矛盾——弃图条又复用哨兵图条，无 provider fallback/outfit-keyed 失效。→ 按外观哈希 key + 搭配变化失效。
- **F9（中）**：素材枚举未统一——`GetEquipList` 枚举不同节点+cap 200。→ 单一 `_Canvas` 枚举器 + `String.wz` 名称 + 分页。

### Claude's response
9/9 采纳（F5 修正采纳——核心采纳、错误数值拒绝，已核实 MapleSalon2 itemId.ts 皮肤=body/head 槽 id）。已修订 PLAN.md（Rev 1）：F1→§2 哨兵调用点全量迁移；F2→§2 player 动作定义/别名+OneShot 回退；F3→§1 locks 精确分支移植；F4→§1 layer resolution 精确移植；F5→§1 皮肤=Body/Head 槽 id + 诊断验证，拒绝 female=12000/22000；F6→§1 `EnumerateRoots` 增 Earring/Longcoat；F7→§1 `RenderFrame` 不可变快照+代际；F8→§5 离线缓存外观哈希 key+失效；F9→§4 单一枚举器+`String.wz`+分页。无拒绝。

## Round 2 — Codex（resume 同 session，model=glm-5.2）
VERDICT: **APPROVED** — F1–F9 全部确认解决，无新阻塞缺陷。两条非阻塞实施约束，均已纳入 PLAN：①F1 清扫须显式含 `AnimService.Play`/`HasSpriteForAction`/`LoadStripWithFallback` + `PetWindow.StartAnimation`（启动 `Play("stand")` 仍走旧图条门 AnimService.cs:84 / PetWindow.axaml.cs:342）；②blink 为多帧表情时 `RenderFrame`/`IEntityFrameProvider` 需 face-expression 帧索引或内部表情时间线（对齐 characterFaceFrame.ts:99）。

**Resolution (converged)** — 两 act 完成，计划锁定（PLAN.md Rev 1），等待实现方式裁决。有效评审 2 轮（R1 REVISE×9、R2 APPROVED）；另有 1 次 Round 1 代理故障重跑（cc-switch 未启动，用户启动后重跑成功）。



## Iteration 2 — 背景调整修复（渲染对齐 + 菜单历史 + 选图器并入设置中心）
Act 1 (grill-with-docs) complete — plan locked，CONTEXT.md 增「裁剪视口」术语，新增 ADR-0004。MAX_ROUNDS=5。
四问题方案：①整图渲染(相机=地图中心)→按像素裁 crop + 移植后端 back 公式；②菜单「开启/关闭背景」toggle；③「最近使用×4」；④删 MapBrowserWindow，完整选图器并入设置中心背景 tab，crop 框高对比。

## Round 1 — Codex（thread=019fbbeb-fafc-71b0-9a7b-11d7befb4adf，model=glm-5.2）
VERDICT: **REVISE** — 12 findings（首轮空 prompt / 误读 iter1 日志两次失败已重跑，本轮为有效评审）：
- **F1（阻断/编译）**：删 `MapBrowserWindow.cs` 会连带删 `MapSelection`/`MapEntry`（仅定义于此文件:20,23），致 PetWindow/SettingsWindow/新选图器 3 处编译断。→ 删文件前先搬 record 到 Models。
- **F2（阻断/数据丢失）**：`MapHistory` schema `List<string>`→`List<MapHistoryEntry>` 使旧配置反序列化抛 `JsonException`，`ConfigService.LoadSettings` 捕获→`CreateDefaultSettings`→`SaveSettings` 覆写（ConfigService.cs:54-65），用户丢全部配置。→ 必须加 `JsonConverter<MapHistoryEntry>`（string/object 兼容）。
- **F3（高/内存）**：`SKBitmap` 子集是别名非深拷贝；缓存一换 crop 读野指针。→ crop 用 `SKCanvas(dst).DrawBitmap(full,src,dst)` 深拷贝到新 `SKBitmap(cw,ch)`。
- **F4（高/正确性）**：`RenderMapFull` 读 WZ 须 `WithWzLock`（`ExtractPng`/`GetOrigin` 不自锁）；Monitor 可重入不死锁。锁外做发布/拷贝。
- **F5（中）**：crop 越界规则自相矛盾（clamp vs 留透明）+ ApplyMapSelection/LoadDefaultPet 不一致。→ 统一：固定 cropW×cropH 画布、清透明、源矩形求交 blit。
- **F6（中/关键）**：现 `DrawBack` **未减 origin**；PLAN 符号写反（`+oriX`）。→ 绘制时 `floor(px)-MinX-oriX`，取 `_wz.GetOrigin`。
- **F7（中）**：`MapBack` 无 `ScreenMode` 字段。→ 加属性 + ParseBacks 读取 + `!=0` 跳过。
- **F8（低）**：`bgItem` 局部、`Opening` 不重建；空历史 4 槽塌成 1 占位（非固定 6 项）。→ 持 `_bgMenu` 引用、Opening 重建。
- **F9（低）**：toggle-open 同步 `LoadMap` 阻 UI；null/未加载未回退设置 tab。→ 后台 Task + 失败回退 `OpenBackgroundTab`。
- **F10（低）**：`MapHistory.RemoveAll(x=>x==sel.Id)` 改 schema 后不编译；最近项应用从 entry 自带 crop。→ `x.Id==sel.Id`、`Insert(MapHistoryEntry{...})`。
- **F11（低）**：缓存 SKBitmap 须切图/退出 Dispose；并发 last-wins。→ Dispose 旧 + `_fullRenderGen`。
- **F12（低）**：开机重启用与 F5 互动；clamping 交 SetMap，后台线程渲染。

### Claude's response
12/12 全部采纳，已修订 PLAN.md（Rev 2）：F1→§0 类型搬迁前置；F2→§4 `JsonConverter<MapHistoryEntry>` 注册 + fixture；F3→§1 crop 深拷贝；F4→§1 `WithWzLock` 包 WZ 段；F5→§1 `CropFull` 统一越界规则；F6→§1 back 绘制减 origin、修正符号；F7→§1 `MapBack.ScreenMode`；F8→§2 持 `_bgMenu`+Opening 重建、措辞改"toggle+0~4+调整"；F9→§2 后台 Task+回退；F10→§4 `MapHistoryEntry`+调用方改写；F11→§1 缓存 Dispose+`_fullRenderGen`；F12→§1 开机 clamping 交 SetMap。无拒绝。

## Round 2 — Codex（resume 同 session，model=glm-5.2）
VERDICT: **REVISE** — F1–F12 全部确认解决；新增 4 条：
- **N1（必然静默失败）**：迁移产生 `Crop*=0` 历史项→`SetMap` 命中 `cropW<=0` 短路（`_image.Source=null`）静默不显示；PLAN 称"无 crop=整图"却无归一化点，且删了原两个默认来源（MapBrowser 默认 / LoadDefaultPet zero→full）。
- **N2（flip 符号 bug）**：back flip 现绕 `(bx-oriX+w/2)` 中心，Java 绕放置点 `bx`（`translate(bx,by);scale(-1,1);draw(-ox,-oy)`）；非中心锚点错位。可算符号错，非"目视验收"。
- **N3（归属歧义）**：`OpenBackgroundTab()` 在 PetWindow/SettingsWindow 各定义一半；`ShowSettingsWindow` 已可见早返回会跳过切 tab。
- **N4（低/native 泄漏）**：并发缓存未命中，输者 SKBitmap 不 Dispose（F3 已防 corruption，仅内存）。

### Claude's response
4/4 全部采纳，已修订 PLAN Rev 3：N1→§1 `CropFull` 入口 `cw<=0→worldW/worldH`（"无 crop=整图"唯一归一化点，全链路统一；`SetMap` 移除短路）；N2→§1 back flip 改 `Save/Translate(bx,by)/Scale(-1,1)/DrawBitmap(-ori)` 绕放置点，`DrTiled` 每 tile 同规则；N3→§3 `SettingsWindow.OpenBackgroundTab()` 独占 `SwitchSubTab(2)+Show/Activate`，PetWindow 仅确保实例不早返回；N4→§1 发布 CAS：`_fullRenderGen` 过期则 Dispose 自己的 bitmap 不发布。tile/obj 的 `DrawSprite` flip 同类问题标注为 out-of-scope（pre-existing，非本轮 back 范围）。无拒绝。

## Round 3 — Codex（resume 同 session，model=glm-5.2）
VERDICT: **APPROVED** — N1–N4 全部具体解决，F1–F12 复核仍在。两条非阻塞备注（均已涵盖）：tile/obj `DrawSprite` flip 的 out-of-scope 划分正确；选图器单击不拖 crop 也会落到 N1 的整图默认（与"无 crop=整图"契约一致，不仅迁移路径可达）。无新阻塞缺陷。渲染数学现已与 Java 参考可算等价（非目视），schema 迁移安全，缓存/线程模型无 corruption 或泄漏路径。

**Resolution (converged)** — 两 act 完成，计划锁定（PLAN.md Rev 3），等待实施方式裁决。有效评审共 3 轮（R1 REVISE×12、R2 REVISE×4、R3 APPROVED）；另有 2 次 R1 失败重跑（空 prompt / 误读 iter1 日志）。

## 实现（Claude，2026-08-01，按 PLAN Rev 3）
8 步全部落地：
- F1：新增 `MiniPet/Models/MapSelection.cs`（搬 `MapSelection`/`MapEntry`），删 `MapBrowserWindow.cs`。
- F2/F10：`AppConfig` 加 `MapHistoryEntry`，`PetSettings.MapHistory`→`List<MapHistoryEntry>`；`ConfigService` 加 `MapHistoryEntryConverter`（兼容旧 `List<string>`）注册到 `GetJsonOptions`。
- F7：`MapBack.ScreenMode` 字段 + `ParseBacks` 读取 + `!=0` 跳过。
- 渲染（F3/F4/F5/F6/N1/N2/N4/F11）：`MapService` 新增 `RenderMapFull`(WithWzLock+缓存+Dispose)、`RenderCore`、`CropFull`(归一化+求交+深拷贝)、改写 `DrawBack`/`DrTiled`/`DrawBackOnce`(减 origin + flip 绕放置点)、`GetBackTileMode`；`CompositeMapThumbnail` 消费 `RenderMapFull`；删 `CompositeMapFrame`。
- `BackgroundWindow.SetMap` 改 `CropFull`、移除 cropW<=0 短路。
- PetWindow（F8/F9/F10/F12/N3）：`_bgMenu` + `RebuildBgMenu`(Opening 重建：toggle+最近×4+调整地图)、`ToggleBackground`(后台 LoadMap+回退)、`OpenBackgroundTab`；删 `OpenMapBrowser`/顶层打开地图浏览器；`ApplyMapSelection` 改 `MapHistoryEntry`；`LoadDefaultPet` 重开自动加载。
- SettingsWindow（§3/N3）：`OpenBackgroundTab()` public；`BuildBackgroundTab` 重写为完整选图器(搜索/列表/分页/大预览/拖crop，亮黄高对比框)；删 `OpenMapBrowserFromSettings`/`RefreshMapSummary`/`UpdateMapPreviewThumbnail`/`ApplyBackgroundToMain`；死方法 `UpdateMapPreview` 的 `CompositeMapFrame` 引用改 `CompositeMapThumbnail`。
- 行为细化：选图器未拖 crop → 默认中心 800×600 视口（与图1期望一致）；`CropFull` 的 cw<=0→整图 仅作用于迁移/零 crop 历史项。
- **编译自检未做**：本 shell 无 `dotnet`（用户环境构建）。MapService 渲染段已目视复核结构连贯。

---

## Iteration 1 — 背景地图系统重构（已完成 Act 3 构建）
Act 1 (grill) complete — plan locked with the user. MAX_ROUNDS=5（后上调至 7）。

## Round 1 — Codex (2026-08-01, model=deepseek-v4-flash, thread=019fb921-4d88-79e3-aa9c-a5f8f8a01411)

VERDICT: **REVISE** — 8 findings:

1. **macOS 穿透**：`IsHitTestVisible` 只影响 Avalonia 应用内命中测试，`Background=Transparent` 的原生 NSWindow 仍捕获点击。需 ObjC interop 设 `NSWindow.ignoresMouseEvents`。
2. **`_wzLock` 非完整操作锁**：`FindNodeByPath`/`ResolveToCanvas` 锁内返回节点后，调用方在锁外遍历 `.Nodes`/`TryExtract`（WzService.cs:413、696）。并发地图枚举+名称缓存+后台预览会与现有操作竞态。
3. **`CompositeMapFrame` 非全图缩略**：预览 w/h 渲染的是以地图内容中心为相机中心的视口，不是整图缩略（MapService.cs:241）。
4. **`PlacePetOnMapGround` 忽略 crop origin**：仍用 `map.MinX`/`map.MinY`，crop 起点偏离地图左上角时宠物错位（PetWindow.axaml.cs:865）。
5. **`SetMap` 无保护异步渲染**：快速切图/切 crop 时旧 `Task.Run` 覆盖新图（BackgroundWindow.cs:61）。
6. **crop 字段缺失 + 无校验**：PetSettings 无 crop 字段；全零/非法值会产生零尺寸窗口。
7. **设置无背景应用事件**：`SettingsWindow` 只有 `OnPetApplied`/`OnPaperdollApplied`，缺地图应用事件与统一持久化入口。
8. **菜单勾选态过期 + 手动拖动脆弱**：菜单构建一次，`Opening` 只刷新动画菜单；应持 MenuItem 引用刷新勾选态，拖动用现有 `BeginMoveDrag` 模式。

### Claude's response
8/8 全部采纳并核实到具体代码行（BackgroundWindow.cs、MapService.cs:241、PetWindow.axaml.cs:865、WzService.cs 多处锁外访问、SettingsWindow.axaml.cs 事件、AppConfig.cs PetSettings 无 crop 字段）。均修订进 PLAN.md，见 Approach 对应条目。

## Round 2 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 5 findings:

1. **WZ 锁修复仍不完整**：现有 `ExportSpriteStrip`/`BuildSpriteStripPng`/`GetActionList`/`GetBalloonIdNode` 仍在 `_wzLock` 外调 `TryExtract()`（WzService.cs:413、551），后台地图预览仍会与现有预加载路径并发损坏节点树。
2. **`MapService.LoadMap` 元数据遍历不在完整锁内**：`FindNodeByPath` 后锁外枚举返回节点（如 foothold），两个并发 LoadMap 或 LoadMap + 精灵提取仍竞态。
3. **启动名称缓存构建是 UI 线程 + 陈旧风险**：上千条 `id→名` 字典启动同步构建会卡 UI；WZ 重建后缓存陈旧。
4. **交互模式无焦点/激活处理**：`BackgroundWindow` 是 `ShowActivated=false`，进入交互模式后 Esc 可能仍发给 PetWindow、`BeginMoveDrag` 收不到可靠输入。
5. **全图缩略缓存无界**：浏览上千张地图时缓存每张 2000×1500 位图会耗尽内存。

### Claude's response
5/5 全部采纳。1 → Approach 1 锁范围扩大到 4 个现有提取方法；2 → `LoadMap` 元数据经 WzService 持锁入口；3 → 目录缓存后台构建 + `LoadWz` 成功后重建；4 → `SetInteractive` 时焦点/Esc 处理；5 → 缩略图只缓存当前选中地图。

## Round 3 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 4 findings:

1. **WZ 节点遍历序列化仍不完整**：`GetDirectoryChildren`/`GetFramePieceNames`/`GetPieceMap`/`GetZmapOrder`（WzService.cs:696）及 `PaperdollService`（GetActionFrameCount:414/GetActionList:428/GetEquipList:446）仍锁外枚举 `Wz_Node.Nodes`。只锁部分方法，地图预览与纸娃娃/精灵工作仍可竞态。
2. **`MapBrowserWindow` 预览无代际保护**：只给 `BackgroundWindow.SetMap` 加了 `_mapRenderGen`，浏览器快速切地图时旧缩略图任务可最后完成覆盖当前预览/crop。
3. **目录重建与 WZ 重载竞态**：每次 `LoadWz` 后重建目录无版本化，用户改 WZ 路径时旧构建任务可能发布陈旧名称。
4. **克隆 `MaterialBrowserWindow` 带入同步 WZ 加载**：WZ 未加载时打开浏览器，其构造函数同步 `LoadWz`+同步枚举全量目录，阻塞 UI 线程。

### Claude's response
4/4 全部采纳。1 → WzService 4 个枚举方法整体持锁 + 暴露 `WithWzLock`，PaperdollService 锁外枚举包进同一锁；2 → `MapBrowserWindow` 加 `_previewGen`；3 → 目录构建版本号，仅发布最新 `LoadWz` 结果；4 → `MapBrowserWindow` 构造只显示加载态，目录后台就绪后填充。

## Round 4 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 3 findings:

1. **`MapBrowserWindow` 无 WZ 加载启动路径**：有加载态但没说谁启动后台 `LoadWz`，打开时 WZ 未就绪可能永久停在加载态。
2. **交互模式在背景隐藏时不重置**：`HideMap()`/`Hide()` 留下 `Interactive=true`+`ignoresMouseEvents=false`，下次 `Show()` 变可点击而非默认穿透。
3. **交互菜单在无背景/无 map 时被调用**：`TryGetPlatformHandle()` 为 null，原生穿透切换静默 no-op。

### Claude's response
3/3 全部采纳。1 → Approach 2 明确后台 `LoadWz`→构建目录→填充列表的启动路径（spinner）；2 → Approach 3 `HideMap()`/`Hide()` 时 `SetInteractive(false)`，每次显示回到穿透态；3 → Approach 3/4 `SetInteractive` 句柄不可用时安全返回，菜单项无背景时 `IsEnabled=false`。

## Round 5 — Codex（resume 同 session，2026-08-01）

VERDICT: **REVISE** — 2 findings:

1. **"所有 WZ 节点访问持锁"声明仍不完整**：`PaperdollService.CollectPieces`（:143）锁外枚举 `faceRootNode.Nodes`；`WzService.GetFrameCount`/`AutoDiscoverMappings` 锁外遍历子节点。逐个点名总会有遗漏。
2. **多 `MapBrowserWindow` 实例重复启动后台 LoadWz/目录构建**：无共享 in-flight 任务，`WzService.LoadWz` 未防并发调用。

### Claude's response
2/2 全部采纳。1 → Approach 1 改为实施时对**全部 Wz_Node 消费者**做锁审计统一包装（含 CollectPieces:143、GetFrameCount、AutoDiscoverMappings），不逐个点名；2 → Approach 1/2 目录构建**单飞** + `LoadWz` 并发守卫，多窗口 await 同一就绪任务。

**MAX_ROUNDS=5 已到，未获 APPROVED → Resolution (deadlock)。Claude 最终立场：5 轮 22 条意见全部采纳修订，无拒绝；计划已收敛，剩余为实施时的锁审计 checklist 而非结构性缺陷。交用户裁决。**
→ 用户裁决：**提高 MAX_ROUNDS 至 7 再跑**，复审最后两条修订。

## Round 6 — Codex（resume 同 session，2026-08-01）

VERDICT: **APPROVED** — 先前意见全部解决：全部 `Wz_Node` 消费者锁审计、`LoadWz` 并发守卫 + 目录构建单飞、多窗口 await 同一就绪任务、交互模式/持久化/渲染代际/macOS 穿透修复均保留。无新实质缺陷。剩余风险（ObjC `ignoresMouseEvents` 互操作写法、crop 预览到世界坐标换算）为实施细节而非计划阻塞。

**Resolution (converged)** — 两 act 完成，计划锁定，等待实施方式裁决。

## Act 3 — Build（2026-08-01，Claude 实现）

实现 PLAN 全部 7 个 Approach（+ 设置中心卡顿修复）：
- **卡顿修复**：SettingsWindow 预览动画去磁盘 IO（缓存源 Bitmap + CroppedBitmap）、打开异步化（LoadPreviewActionAsync 代际保护）。
- **Approach 1**：WzService ListMapIds(21230)/BuildMapNameCache(21206)/EnsureMapCatalog(单飞+版本化后台)/WithWzLock；4 个 TryExtract 方法整体 `_wzLock`；LoadWz 并发守卫；MapService.LoadMap→LoadMapCore 经 WithWzLock；PaperdollService 3 个锁外枚举方法包 WithWzLock。
- **Approach 2**：MapBrowserWindow（三栏+搜索+分页+crop 拖矩形选择器，后台 CompositeMapThumbnail+代际，await 目录缓存）；MapService.CompositeMapThumbnail（整图缩略，WithWzLock）。
- **Approach 3**：BackgroundWindow 交互模式+原生 NSWindow.ignoresMouseEvents(ObjC interop)+crop 渲染+_renderGen 代际+BeginMoveDrag+Esc+HideMap 重置。
- **Approach 4**：PetWindow 菜单接线（浏览地图/交互模式勾选项 Opening 刷新 IsChecked+IsEnabled）、PlacePetOnMapGround(map,cropX,cropY)、ApplyMapSelection 持久化。
- **Approach 5**：SettingsWindow 背景 tab 重构（移除硬编码 _mapPresets，改浏览器入口+摘要+应用）、OnBackgroundMapApplied 事件、AppConfig CropX/Y/W/H。
- **Approach 6+7**：开机自动展示（LoadDefaultPet crop 校验 clamp 边界）+ 运行验证。

### 验证
- **编译**：0 错误。
- **运行**：WZ 加载(Mob 4387/Map 21230)、地图目录 21206、纸娃娃合成 2085 字节（锁审计未破坏）、无死锁/Exception、进程稳定。
- **手动关键点审查**：_wzLock 重入安全（Monitor 可重入，WithWzLock→FindNodeByPath/LoadMapCore→ParseTiles 均同线程重入）；crop 坐标换算正确（_thumbScale=worldW/thumbW、viewX=cropX-MinX、cropX+bgW/2）；代际保护正确（_previewGen/_renderGen 丢弃过期）。
- **Codex 验证**：API 返回 404（bigmodel.cn coding/paas/v4/responses endpoint 故障，ACT2 时正常），重试多次均失败，无法执行 Codex 审查。改用上述手动审查 + 编译/运行验证代替。
- **待用户交互测试确认**：macOS 原生 ignoresMouseEvents interop 的 objc_msgSend arm64 ABI（bool 参数传 IntPtr 1/0）、crop 拖矩形交互、交互模式穿透↔拖动切换。
