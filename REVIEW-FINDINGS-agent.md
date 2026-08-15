# REVIEW-FINDINGS: MiniPet 全系统代码审查（agent 版）

_审查日期：2026-08-15 · 范围：全系统（渲染/业务/预览/交互/缓存/兜底/性能/规范）_
_方式：DSH agent 主导 + 4 个并行子审查（动画主窗口/地图渲染/数据服务/设置窗口）+ 纸娃娃渲染专项_
_参考文档：CODE_QUALITY.md / PERFORMANCE.md / ARCHITECTURE.md（注意：ARCHITECTURE.md 是 Swift 时代遗留，与现状不符，建议标注废弃）_

> 用法：本文件是修复任务书。每条含 文件:行号 / 问题 / 修复方向。修复顺序建议按 🔴→🟡→🟢。
> 部分条目已在 2026-08-15 修复：B 组（预设 bug）+ 输入样式统一 + 遮挡锁 + 按钮 hover + 皮肤名（见文末"已修复清单"）。

---

## 一、纸娃娃渲染专项（PaperdollService.cs）

| # | 级别 | 位置 | 问题 | 修复方向 |
|---|---|---|---|---|
| P1 | 🔴 | `CharacterAppearance.cs:29-30` + `PaperdollService.cs GetRoots(632)` | **坐骑/椅子配置不生效**：UI 可配置 Mount/Chair 且可保存预设，但 GetRoots/GetCategoryItemId 无 mount/chair category，渲染链路完全不消费 | 决定：要么实现渲染（TamingMob/Install 目录加载 + 坐姿动作），要么从 UI 移除该配置并清理预设数据 |
| P2 | 🔴 | `PaperdollService.cs:227` | **遮挡锁被注释禁用** `// ApplyLocks(...)`——帽子不遮头发 | **已修复（2026-08-15，对齐 MapleSalon2 全局锁表）**，验证：帽子/头发/披风遮挡正确 |
| P3 | 🟡 | `PaperdollService.cs GetBounds(633)` | GetBounds 整段 `WithWzLock` 且锁内首次 `GetBmp` 解码 PNG：后台 Warmup 持锁时间长，UI 渲染阻塞 | 锁收缩到 WZ 节点读取（FindNodeByPath/GetValueEx）；位图解码移锁外（SKBitmap.Decode 线程安全，解码后缓存） |
| P4 | 🟡 | `ResolveAnchors(396)` | 锚点解算完全依赖数据（body map 必须含 navel 链）；数据缺失时 body/head 整体不渲染且无诊断 | 解算后检查 body 是否 Locked，未 Locked 打 Console.Error 一次；body 缺失时给预览/主窗口降级提示 |
| P5 | 🟢 | `RenderFrame(241)` | `locked.Count==0` 静默返回空帧无日志 | 加 Console.Error（含 hash/action/frame） |
| P6 | 🟡 | `ResolveAction(164)` / `HasAction(144)` | 每次动作判定触发 GetBounds 全帧合成+解码（首次切换动作卡顿） | 动作有效性缓存（`(hash,action)→bool`），WZ 刷新/SetAppearance 失效 |
| P7 | 🟢 | `GetZmapTopToBottom(800)` | zmap 每次 GetZmapOrder 刷日志（已删 zmap 打印，确认无残留） | 确认已清理 |
| P8 | 🟡 | `GetFrameDelay(110)` | `_delayCache` 键 `{action}|{frame}` 不含 body/外观——换外观后延迟复用旧值（动作结构通常相同，风险低） | 键加 hash 或 SetAppearance 清 _delayCache（当前 SetAppearance 已清 ✅ 无需改） |

## 二、动画与主窗口链路（AnimService/PetWindow/SpriteService）

| # | 级别 | 位置 | 问题 | 修复方向 |
|---|---|---|---|---|
| A1 | 🔴 | `AnimService.cs:166-167` | **SwitchTo pending 合并条件取反**：OneShot 播放期间 move/fly 切换被静默丢弃（`if (!pending...)` 应为 `if (pending...)`） | 读代码确认条件语义并修正；补单测 |
| A2 | 🔴 | `PetWindow.axaml.cs:639-812,403-486` | 隐藏到托盘后 30fps 帧循环 + 逐帧 PNG 解码 + 随机气泡全量继续（幽灵气泡、CPU 浪费） | 隐藏时停 _timer/_balloonTimer，恢复时重启并追帧 |
| A3 | 🔴 | `PetWindow.axaml.cs:422-443` + `BalloonControl.axaml.cs:54-61,112-114` | **每颗气泡泄漏一个 Avalonia Bitmap**（new Bitmap 不 Dispose；替换 Source 不 Dispose 旧图；HideBalloon 不 Dispose） | BalloonControl 持有当前图，替换/隐藏前 Dispose |
| A4 | 🟡 | `AnimService.cs:284-296` / `PaperdollService.cs:163-180` | 纸娃娃 WZ 断线离线图条兜底不可达：fallback 归 "stand"，但离线条只有 stand1/2/walk1/2/jump，stand 解析不到 stand1 → 空白宠物 | 离线 fallback 链补 ResolveAction 语义（stand→stand1 等）或直接按离线条动作名匹配 |
| A5 | 🟡 | `SpriteService.cs:125-128,136-172` | `_framesCache` 重建后旧帧键残留：条带重建几何变化或解码失败时 GetFramePng 命中旧帧 → 显示错误帧 | CacheStrip 锁内先按 `mobId|action_` 前缀清旧帧键再合并 |
| A6 | 🟡 | `CacheManager.cs:408-432` | 内存缓存 FIFO 伪 LRU：枚举首个淘汰非 LRU；46MB 级条带写入清空全部活跃条目 | 真 LRU 或对活动 mob 免淘汰 |
| A7 | 🟡 | `PaperdollService.cs:59-60,95-100` | `_bmpCache/_dyeBmpCache` 无上限、ClearBitmapCache 全项目零调用 → 位图常驻无界增长 | 加容量上限（LRU）；设置窗口关闭/外观批量变更时调用 |
| A8 | 🟡 | `PetWindow.axaml.cs:1002-1018` | PreloadAction 失败静默：loading 消失但宠物没换、无提示；且 PetChanged 仍发布 → 托盘 tooltip 与最近使用记录脏数据 | ok=false 时提示 + 回滚 tooltip/不记录 |
| A9 | 🟡 | `AnimService.cs:616-648` + `PetWindow.axaml.cs:651-668` | fallback 链只以 stand 为终点且只查磁盘缓存：mob 无 stand 时空白 + 旧帧冻结 | fallback 依次尝试 move/fly/walk；空结果帧 Clear 画布 |
| A10 | 🟡 | `AnimService.cs:462-477` | 随机空闲动作 Loop/OneShot 判定分裂：walk1 走一遍 / fly 永循环 / pending 悬挂 / skill 优先永不命中 | 统一随机动作选择逻辑（只选已知 Loop 动作） |
| A11 | 🟡 | `AgentStateAggregator.cs:123-136` | Wave/Waiting 永不因 staleness 回 Idle（只衰减 Failed/Run/Review） | 全状态统一 staleness 衰减 |
| A12 | 🟡 | `PetWindow.axaml.cs:89-90,835-836` | `_anchorOriginX/Y` 只写不读（死字段） | 删除或补用途 |
| A13 | 🟡 | `PetWindow.axaml.cs:662,806` | 帧循环每帧 GetCachedStrip 两次（锁 + LRU 链表维护）；AnimService:479 随机空闲触发磁盘枚举 | AnimService 持有当前 strip 引用；随机动作池缓存 |
| A14 | 🟢 | `EffectLayerService.cs:118,180` | 每次 Agent 状态变化 2 次 LoadSettings（锁内磁盘 IO） | 一次读取传入；ConfigService 内存缓存 |
| A15 | 🟢 | `EventBus` Unsubscribe 零调用 | 订阅无退订模式（当前靠单例生命周期兜底） | 窗口销毁时退订（重构时处理） |
| A16 | 🟡 | 全项目 474 处 | 单行 if 不带 `{}`（CLAUDE.md 规则 11 违规，A1 条件取反即因此难查） | 批量修正（机械替换，注意语义） |

## 三、地图渲染链路（MapService/MapViewportWindow/BackgroundWindow）

| # | 级别 | 位置 | 问题 | 修复方向 |
|---|---|---|---|---|
| M1 | 🔴 | `MapService.cs:56-62` + `MapViewportWindow.cs:96,110` | **ClearSpriteCache 无锁 Dispose 与在途渲染竞态**（切图/停渲染时原生句柄并发释放 → 崩溃风险） | 收进 WZ 锁或独立锁；Dispose 延迟到渲染完成 |
| M2 | 🔴 | `BackgroundWindow.cs:151` | 模式1 渲染失败/全透明 → 静默空白窗口无提示 | 显示错误提示或自动隐藏窗口 |
| M3 | 🟡 | `MapService.cs:231` | tile Z0 取源错误：地图节点 z vs 参考实现（R2/PIXI）的精灵帧 z | 对照 `WzComparerR2C#/WzComparerR2.MapRender/` 修正 |
| M4 | 🟡 | `MapService.Render.cs:174` | screenMode!=0 的 back 全丢（R2 默认画 screenMode==1） | 按 R2 逻辑支持 screenMode==1 |
| M5 | 🟡 | `MapService.Render.cs:46` + `SettingsWindow.Background.cs:579` | RenderViewport 整帧持 WZ 锁 × 30fps × UI 线程预览 → 锁竞争冻结 | 锁收缩到精灵懒加载；预览走后台 |
| M6 | 🟡 | `BackgroundWindow.cs:163,181-197` | 离线 PNG 缓存丢失裁紧原点（世界坐标错位） | 缓存写入时持久化 crop origin |
| M7 | 🟡 | `MapService.cs:509-522` | GetGroundY 取最高平台而非地面（宠物站屋顶/树顶） | 按 foothold 类型/顺序取地面 |
| M8 | 🟡 | `MapCatalogService.cs:40-49` | Reset() 漏清 `_worldMapTree`/`_mapStreetInfo`；mapHierarchy.json 无 WZ 指纹 → 换 WZ 后永显旧数据 | Reset 全清；磁盘缓存带路径哈希 |
| M9 | 🟡 | `MapService.cs:548-551` | 换图时旧整图不 Dispose 只置 null（快速切图原生内存膨胀） | 锁内 Dispose |
| M10 | 🟡 | `MapViewportWindow.cs:118-120` | 无滚动 back 地图空闲时每帧重渲逐字节相同的图（纯烧 CPU） | 脏标记：相机/缩放/尺寸变化或存在滚动 back 才重渲 |
| M11 | 🟡 | `MapService.Render.cs:52,160` + `MapViewportWindow.cs:150` | 每帧分配 ≈4MB（SKBitmap 1.9MB + 每精灵 SKPaint + WriteableBitmap 1.9MB） | SKBitmap/WriteableBitmap 双缓冲复用；SKPaint 提字段 |
| M12 | 🟡 | `MapService.cs:71` | 精灵缓存 LRU 命中 O(n) 链表扫描（每帧数百次） | 键→节点字典 |
| M13 | 🟢 | `MapService.Render.cs:197` | back 显式 a=0 画成不透明（R2 为不可见） | a==0 跳过 |
| M14 | 🟢 | `MapService.cs:86-87` | 精灵负缓存永不过期（WZ 热更新后仍缺失） | 重载时清负缓存 |
| M15 | 🟢 | `MapService.cs:579` | CropFull 大 crop 无上限 | 加尺寸上限/内存保护 |

## 四、数据服务与装配层（WzService/CacheManager/ConfigService/HermesAdapter）

| # | 级别 | 位置 | 问题 | 修复方向 |
|---|---|---|---|---|
| D1 | 🟡 | `WzService.cs:120-184` | LoadWz 失败后 `_wzs` 半构建结构不释放（内存驻留 + 无法自动恢复） | catch 里 `_wzs=null; _mobFullRoot=null;` |
| D2 | 🟡 | `MapCatalogService.cs:323-343` | GetWorldMapTree 磁盘缓存读取无锁、双线程重复构建（各持锁数秒） | 锁内二次检查 |
| D3 | 🟡 | `SpriteService.cs:417-422` | 淘汰只清被淘汰 mob+action 的帧：同 mob 其他动作帧滞留；action 名前缀误伤（attack1_ 命中 attack1_2） | mob→frames 反向索引 |
| D4 | 🟡 | `PaperdollService.cs:934-1000` | BuildOfflineStrips 缓存键无外观指纹：任务被杀 → 配置新外观 + 磁盘旧条带 → 离线播放旧外观 | 键加 `_currentHash` 或保存前删旧哨兵 |
| D5 | 🟡 | `PetWindow.axaml.cs:343-360` | 启动恢复不重建离线条带（上次构建失败则离线兜底空 → 宠物静默不显示） | 启动时 WZ 可用则后台补 BuildOfflineStrips |
| D6 | 🟡 | `HermesAdapter.cs:328` | 轮询在 UI 线程同步 HTTP（`GetAwaiter().GetResult()`，默认超时 100s）→ Hermes 假死时 UI 冻结 100s | 轮询改后台线程/async |
| D7 | 🟡 | `PetWindow.axaml.cs:368` | LoadDefaultPet UI 线程同步 PreloadMob（磁盘未命中时构建整条 PNG 卡启动） | 后台 Task + 代际 |
| D8 | 🟢 | `WzService.cs:567-583` | GetItemName 每调用探测 9 条候选路径不缓存 | 结果缓存 |
| D9 | 🟢 | `WzService.cs:885-900` | GetBalloonList 每次打开选择器锁内 TryExtract 全部 1794 个戒指 | 缓存列表 |
| D10 | 🟢 | `CacheManager.cs:271-305,440-473` | ClearOldCache 按文件时间分别删 png/json 可能只删一半（悬挂 png，自愈可恢复） | 成对删除 |

## 五、设置窗口与素材浏览器（SettingsWindow.*/MaterialBrowserWindow）

| # | 级别 | 位置 | 问题 | 修复方向 |
|---|---|---|---|---|
| S1 | 🔴 | `SettingsWindow.axaml.cs:424-431` | **素材 tab 搜索框是死控件**：无任何 TextChanged 处理 | 接 TextChanged 过滤收藏/最近列表，或删除该框 |
| S2 | 🔴 | `SettingsWindow.Agent.cs`（开关区） | **Agent tab 开关控件缺失**：死字段假实现，开关在 UI 上不可用 | 补 CheckBox 装配或删除死字段 |
| S3 | 🟡 | `SettingsWindow.axaml.cs:94,888,924,981-987` | `_listThumbs` 无界增长且替换不 Dispose 旧 Bitmap | 替换前 Dispose；上限 + LRU |
| S4 | 🟡 | `SettingsWindow.axaml.cs:1141-1148,1158` | 预览源位图先 Dispose 再换 Image.Source：在飞 CroppedBitmap 引用已释放位图 | 先换 Source 再 Dispose |
| S5 | 🟡 | WZ 刷新后 | 背景树/气泡列表/皮肤列表一次性构建不重建（首次配置 WZ 后三个 tab 废到重启） | WZ 刷新回调重建 |
| S6 | 🟡 | `SettingsWindow.Paperdoll.cs:199-203` | 染发滑块：预设/草稿/换发型路径不重置滑块状态 | 统一从 _paperdollCurrent 初始化滑块 |
| S7 | 🟡 | `SettingsWindow.Balloon.cs` 列表 | 气泡列表默认选中组头（"🕘最近使用"标题项当选中项） | 选中首个实际气泡项 |
| S8 | 🟠 | `SettingsWindow.Paperdoll.cs:201-203,511-567` | 滑块每 tick 全量重建预览面板 + Task.Run 合成风暴（有 _previewSeq 防串台但 CPU/GC 浪费） | 滑块 debounce 100-150ms；面板静态化 |
| S9 | 🟠 | `MaterialBrowserWindow.cs:136-137` + `Logic.cs:447-511` | 构造时 UI 线程同步全量枚举 WZ（数千条目，打开即卡） | LoadFromWz 移后台分批填充 |
| S10 | 🟠 | `MaterialBrowserWindow.Logic.cs:216-241` | 条目预览同步构建整条精灵图（46MB 级条带选中即冻结） | Task.Run + 代际；先显示"加载中" |
| S11 | 🟡 | `SettingsWindow.Balloon.cs:102,219-257` | 气泡搜索每键 Console 日志 + 全列表重建 | 删日志 + debounce |
| S12 | 🟡 | `SettingsWindow.Balloon.cs:176-197` | 气泡预览每次重新解瓦片渲染（无缓存） | 按 balloonId 缓存 |
| S13 | 🟢 | `MaterialBrowserWindow.Logic.cs:513-525,447-511` | 素材库空目录/加载失败无空态提示 | 空态提示 + 错误条目 |
| S14 | 🟢 | `SettingsWindow.Background.cs:302` | WaitForMapCatalog 最长占线程池线程 15s | 可接受，记录 |

## 六、已修复清单（2026-08-15 当天完成，需回归验证）

1. **B 组预设 bug**（SettingsWindow.Paperdoll.cs）：新建入列表/唯一名/预览竞态版本号/刷新保选中/默认 BodyId=2000/摘要 14 部位/全量克隆/加载未选中提示/染发实时预览/死代码删除/按钮文案
2. **ConfigService.SaveSettings 原子写**（tmp + File.Replace + File.Move 回退 + finally 清理）
3. **PaperdollService WZ 访问 WithWzLock 串行化**（GetBounds/GetMeta/GetBmp/CollectPieces）+ 诊断日志收敛（7 个诊断字段删除，失败日志保留）
4. **输入控件样式统一**：全部对齐 AI tab（FontSize 12 / 白底黑字 / InputBorder / 圆角 6 / Padding(10,6)）+ 宽度自适应（Grid Star 列重构）；HermesChatWindow 按用户要求保持原样
5. **遮挡锁对齐 MapleSalon2**（全局锁表 + 双条件判定 + 重新启用，2026-08-15 修复中）
6. **按钮 hover 统一**（UiStyle.ApplyHover，2026-08-15 修复中）
7. **皮肤下拉中文名**（增强探测 + 缓存 + SkinIndex 未命中不回落，2026-08-15 修复中）

## 七、验收标准（修复完成后）

- 编译 0 错误：`"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug -p:UsedAvaloniaProducts=`
- 测试全绿：`"/Volumes/SSD/C#/dotnet" test MiniPet.Tests/MiniPet.Tests.csproj -p:UsedAvaloniaProducts=`
- 手动验证：宠物动画切换（OneShot 打断）、隐藏托盘 CPU、气泡连续显示内存、地图摄像机拖动、设置各 tab 交互、纸娃娃帽子/头发/披风遮挡

## 八、遗留问题登记（2026-08-15 追加）

| # | 级别 | 位置 | 问题 | 状态 |
|---|---|---|---|---|
| T1 | 🟡 | `SettingsWindow.axaml.cs:272-277`（原） | **深夜/白天模式切换异常**：切换只写 `_settings.Theme` 未真正应用主题（无 RequestedThemeVariant 应用逻辑），且与 FluentTheme + 全局 Button hover 覆写叠加出现显示异常。**已从 UI 隐藏**（注释保留代码与 `_isDarkMode` 读取逻辑），待修复：实现真正的主题切换（RequestedThemeVariant + 资源刷新生效）后恢复 | 隐藏待修 |
| T2 | 🟡 | `App.axaml.cs`（App 构造） | 全局样式 `Button:pointerover /template/ ContentPresenter#PART_ContentPresenter` 覆写（背景/边框恒跟随 Button 本地值）——用于禁用 Fluent 模板自带 hover 高亮、让 UiStyle 独占 hover 效果。若主题切换功能恢复，需验证该覆写与浅色/深色主题资源不冲突 | 验证中 |

## 九、纸娃娃体验优化方案（参考 MapleSalon2，2026-08-15 记录，待实施）

> 参考项目：`maplestory-wiki-backend/MapleSalon2/src/components/drawer/EqupimentDrawer/`
> （EquipItemButton / EquipSearchInput / CharacterAvatar / CurrentEquipmentDrawer / Favorite）
> 状态：方案已确认，**未改代码**。实施时按下面逐项落地。

### 参考项目做到的体验（对照）

| MapleSalon2 能力 | 实现方式 | MiniPet 现状 |
|---|---|---|
| 点击装备条目立即应用 | `EquipItemButton.onClick → selectNewItem`（实时穿衣预览） | ✅ 素材库草稿应用已有（OnPaperdollDraftApplied → 预览刷新） |
| 搜索防抖 + 清除按钮 | `EquipSearchInput`：300ms debounce + × 复位 | 🟡 素材库搜索无 debounce、无清除按钮 |
| 条目悬停 tooltip | `CssTooltip` 显示装备名 | ❌ 无 |
| 角色实时预览（icon/character 双模式） | `CharacterAvatar` + `CharacterRenderingSwitch` | 🟡 仅 stand1 第 0 帧静态图 |
| 当前穿戴列表（可编辑/删除/染发信息） | `CurrentEquipmentDrawer/EquipItem` | 🟡 仅只读摘要 14 行 |
| 性别切换 | `GenderSelect`（列表按性别过滤） | ❌ 无过滤（男体可选女发，渲染缺件） |
| 筛选开关（仅现金/可染） | `OnlyShowCashSwitch / OnlyShowDyeableSwitch` | ❌ 无 |
| 条目内收藏 | `AddToFavoriteButton` | ✅ 有收藏按钮 |

### 三场景优化方案（用户点名，优先实施）

**A. 新增选中**
1. 新建预设后：自动选中列表项（已有）+ 补 `ListBox.ScrollIntoView(选中项)` 滚动到可见（预设多时不盲选）。
2. 素材库选件应用后：对应装备按钮给短暂高亮反馈（边框闪 accent 蓝 0.6s，提示"该部位已更新"）。
3. 预设列表选中态强化：ItemTemplate 加选中样式（暗色主题下默认选中高亮不明显——选中项文字变 accent + 背景微亮）。

**B. 点击应用（应用到主窗口）**
1. 「✅ 应用到主窗口」按钮字段化（`_applyMainBtn`），点击后：禁用 + 文案「合成中…」，调 `OnPaperdollApplied`。
2. PetWindow.ApplyPaperdoll 后台构建完成（Dispatcher Post 回调，含 gen 过期分支）→ `_settingsWindow?.SetPaperdollApplyBusy(false)` 恢复按钮（设置窗口单例由 PetWindow 持有，直接调用）。
3. 状态栏同步：「已应用到主窗口」保留；按钮恢复后闪一下「✅ 已应用」再还原文案（可选）。

**C. 预览**
1. 预览区加**动作切换按钮组**：stand1（站立）/ walk1（行走）/ jump（跳跃）/ swingOF（攻击）/ alert（警戒）——固定动作集（字段 `_previewAction` + `PreviewActions`），点选后 `UpdatePaperdollPreview` 用该动作合成，当前动作按钮高亮（accent）。
2. **合成中状态**：figure 内显示「⏳ 合成中…」（Task 启动前设，完成替换为图；`_previewSeq` 已有防串台）。
3. 预览图已 Uniform 自适应（已完成），保持。

### 其余可参考项（后续按需）
- 搜索框 300ms debounce + × 清除（素材库 + 气泡 tab 搜索）
- 条目悬停 tooltip（装备名/怪物名）
- 性别联动过滤（素材库按 Gender 过滤发型/脸型）
- 预设列表右键菜单（删除/重命名/复制——当前无删除）
- 预览双模式（图标/角色）切换

### 补充需求（2026-08-15 用户追加，一并记录待实施）

**D. 素材选择器（MaterialBrowserWindow）内预览增强**
1. **icon 预览**：WZ 装备节点自带 `icon` / `iconRaw` 字段（如 `Character/Cap/01000000.img/icon`），可直接 `ExtractPng` 提取——比全身合成（LoadPaperdollPreview）和精灵图条（BuildSpriteStripPng）**快得多**。要求：
   - 详情预览区：优先显示 icon（无 icon 再回退现有合成/精灵图路径）；
   - 条目列表缩略图（LoadThumbAsync）：同样优先用 icon，替代当前慢路径。
2. 纸娃娃部件分类选中后"穿身上预览"已有（LoadPaperdollPreview，stand1 首帧合成）——保留，与 icon 预览并存（icon 快图 + 全身效果图）。
3. 预览区加载中状态（⏳）+ 失败占位已有，保持。

**E. 发型同名不同颜色 → 折叠分组 + 颜色调整**
1. 素材库发型列表按**显示名分组**：同名（如多个 id 同为"短发/褐色系"等，颜色变体）折叠为一个条目组，展开/选择后显示各颜色变体。
2. 组内提供**颜色调整入口**：复用现有染发（HSV 滑块 + Dye），选中变体后可在组内直接调色，联动 `_paperdollCurrent.Hair` 的 Hue/Saturation/Brightness。
3. 数据侧：发型条目需先读 `info/name`（String.wz 或 Character/Hair/{id}.img/info/name）归类——若名字缺失回退 id 不折叠。
4. 组折叠 UI：参考 MapleSalon2 的折叠/分组交互（列表分组头 + 展开态），与现有分页/搜索兼容（搜索时展开匹配组）。
