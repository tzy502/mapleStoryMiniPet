# Plan: 背景调整修复（渲染对齐 + 菜单历史快切 + 选图器并入设置中心）
_Locked via grill-with-docs — by Claude + 唐子煜. Terms per CONTEXT.md._
_Rev 3 — 采纳 Codex Round1 F1–F12 + Round2 N1–N4 全部意见。_

## Goal
修复已上线背景系统的 4 个反馈：①桌面背景渲染与设置中心预览不一致（back 层视差错位、漏减 origin、flip 绕错点、区域溢出）；②背景关闭后右键无"打开"入口；③无历史快切；④浏览器 crop 选择框难辨、且把选图/crop 从独立窗口提到设置中心背景 tab。渲染统一为「整图渲染(相机=地图中心)→按像素裁 crop（深拷贝）」，菜单仿「切换人物」加历史，`MapBrowserWindow` 删除、其完整选图器并入「设置中心 ▸ 背景设置」。

## Approach

### 0. 类型搬迁（先做，F1）
- `MapSelection` / `MapEntry` 两 record 现仅定义于 `MapBrowserWindow.cs:20,23`，被 `PetWindow.ApplyMapSelection`、`SettingsWindow._pendingMapSelection`/`OnBackgroundMapApplied`、新选图器引用。
- **删 `MapBrowserWindow` 之前**先把两 record 搬到新文件 `MiniPet/Models/MapSelection.cs`（更新 namespace 引用）。删该文件的前置步骤，否则 3 处编译断裂。

### 1. 渲染对齐（问题①，依据 ADR-0004 + 后端 `MapRenderHandler`）
**移植 back 层正确公式到 `MapService`**（对照 `maplestory-wiki-backend/.../MapRenderHandler.java` 的 `drawBacks`/`drTiled`/`getBackTileMode`/`dr`）：
- `GetBackTileMode(type)`：0→0,1→1,2→2,3→3,4→5,5→10,6→7,7→11。
- 定位（**相机恒为地图中心**）：`cx = MinX + worldW/2`，`cy = MinY + worldH/2`；`px = back.X`，若 `(tm & 4)==0` → `px += cx*(100+rx)/100`；`py = back.Y`，若 `(tm & 8)==0` → `py += cy*(100+ry)/100`。
- **画布坐标 + origin + flip（F6 + N2）**：`bx = floor(px) - MinX`，`by = floor(py) - MinY`，取 `(oriX,oriY) = _wz.GetOrigin(back.Resource.ResourceUrl)`。
  - **非 flip**：`DrawBitmap(bmp, bx - oriX, by - oriY)`（对齐 Java `dr` 的 `drawImage(im, x-ox, y-oy)`）。现 `DrawBack`/`DrawBackOnce` **未减 origin**——当前 back 错位主因之一，必须修。
  - **flip（N2，绕放置点而非中心）**：`canvas.Save(); Translate(bx, by); Scale(-1,1); DrawBitmap(bmp, -oriX, -oriY); Restore()`（对齐 Java `translate(x,y); scale(-1,1); drawImage(-ox,-oy)`，绕放置点 `bx` 镜像）。**不得**用现行的 `Scale(-1,1, bx-oriX+w/2, …)` 绕中心——非中心锚点会错位。`DrTiled` 每个 tile 同此规则（tile 放置点 = `bx+ix*tw`）。
- 平铺 `DrTiled`：`l=floor((0-bx)/tw)-1, r=ceil((w-bx)/tw)+1`（纵同），双层 `for` 铺满 ±1 padding；`tileW = (tm&1)!=0 ? (cx>0?cx:图宽) : 0`，`tileH = (tm&2)!=0 ? (cy>0?cy:图高) : 0`；`tm==0` 单次绘制。
- **`screenMode`（F7）**：`MapBack` 增 `public int ScreenMode { get; set; }`；`ParseBacks` 读 `_wz.GetIntProperty($"{bp}/screenMode")`；`!=0` 跳过。

**`MapService.RenderMapFull(MapInfo)`**（新增，**F4/F11/N4**）：
- 以 `worldW×worldH`、相机=地图中心整图渲染 → 产出 `SKBitmap`。
- **WZ 读取段包 `_wz.WithWzLock(...)`**（F4）：`ExtractPng`/`GetOrigin` 不自锁、靠调用方；`WithWzLock` 是 Monitor 可重入，嵌套 `CompositeMapThumbnail→RenderMapFull` 不死锁。**SKBitmap 发布与 crop 拷贝在锁外**。
- **缓存 + CAS 发布（F11 + N4）**：字段 `SKBitmap? _fullRender` + `string? _fullRenderMapId` + `_fullRenderGen`。渲染前捕获 `token = _fullRenderGen`；**发布时 CAS**：若 `_fullRenderGen != token`（已被更新调用覆盖）→ `Dispose()` 自己的 bitmap，**不发布**（防并发 last-wins 输者 native 泄漏）；否则 Dispose 旧 `_fullRender` 再赋值。切 map（`_fullRenderMapId` 变）与 `EventType.AppExit` 时 Dispose。

**crop 归一化 + 深拷贝（F3 + F5 + N1）**：`MapService.CropFull(SKBitmap full, int cx, int cy, int cw, int ch, MapInfo map)`：
- **归一化（N1，唯一规则点）**：`if (cw<=0 || ch<=0) { cw=worldW; ch=worldH; cx=MinX; cy=MinY; }`——即"无 crop = 整图"（全图）。取代被删的 `MapBrowser.ApplySelection` 默认与 `LoadDefaultPet` 的 zero→full 分支，全链路（迁移历史项 / 选图器未拖 crop / 开机）统一在此归一化，杜绝 `cropW<=0` 命中 `SetMap` 短路静默失败。
- **深拷贝（F3）**：新建 `SKBitmap(cw, ch)`，`SKCanvas(dst).Clear(Transparent)`；源矩形 `srcX=cx-MinX, srcY=cy-MinY` 与 `[0,worldW]×[0,worldH]` 求交（**F5，越界留透明、窗口尺寸不变**），`DrawBitmap(full, srcRect, dstRectAtOffset)`。**禁用 `ExtractSubset`/子集构造（别名，缓存换则野指针）**。拷贝后背景窗口不再依赖缓存。

**`BackgroundWindow.SetMap(map, cropX,cropY,cropW,cropH)`**（**N1**）：移除 `cropW<=0` 短路（归一化已在 `CropFull`）；`Width/Height=cropW/cropH`（归一化后必 >0）；后台 `RenderMapFull(map)`（命中缓存直接用）→ `CropFull(...)` 深拷贝 → `SKImage.Encode` PNG → UI 贴图；保留 `_renderGen` 代际。**不再传 viewX/viewY 平移相机**。

**`CompositeMapThumbnail`**：消费 `RenderMapFull` 后缩放（与桌面同源）。`CompositeMapFrame` 的 `viewX/viewY` 仅整图(offset 0)用途，加注释（ADR-0004）。

**删除死方法** `SettingsWindow.UpdateMapPreview`（无调用方）。

**重开自动加载（F12）**：`LoadDefaultPet` 去掉 `if (false && …)` 的 `false`；clamping/归一化交给 `SetMap/CropFull`（不再单独 clamp/zero→full）；整图渲染后台线程，确认大图首帧内存可接受。

### 2. 右键菜单「切换背景 ▸」重构（问题②③，F8/F9/F10/N3）
- 仿「切换人物」。**持 `MenuItem? _bgMenu` 引用**（F8）；`ContextMenu.Opening` 里 **清空并重建 `_bgMenu.Items`** = 1 toggle + 至多 4 最近（空历史→1 条 `IsEnabled=false` 占位"暂无最近"）+ 1「调整地图…」。
  1. **开启/关闭背景 toggle（F9）**：`Opening` 按 `_backgroundWindow.IsVisible` 刷新 Header。点击：开→若 `!_wzService.IsLoaded` 或 `MapId` 空 或 后台 `LoadMap(MapId)==null` → `OpenBackgroundTab()` 回退；否则 **后台 Task** `LoadMap`+`SetMap`(crop)+`Show`+`RaisePet`（不在 UI 线程同步走 WZ）。关→`HideMap()`+`Hide()`+`RaisePet()`。
  2. **最近使用 ×4（F10）**：取 `settings.MapHistory`（`List<MapHistoryEntry>`）前 4；每项 Header `🗺️ {entry.Name} [{entry.Id}]`；点击 → 用 **entry 自带 crop** 构造 `MapSelection(entry.Id, entry.Name, entry.CropX..)` → `ApplyMapSelection`。
  3. **🚓 调整地图…** → `OpenBackgroundTab()`（见 §3，单一入口）。
- **删除**：顶层 `🗺️ 打开地图浏览器`（PetWindow.axaml.cs:605）；`bgItem` 内旧 `浏览地图…`/`⚙️ 更多`；`OpenMapBrowser()` 方法。
- 保留 `🎨 背景交互模式`（Opening 刷新不变）。`BackgroundWindow.RequestSwitchMap`（交互模式右键"切换地图…"）→ `OpenBackgroundTab()`。

### 3. 选图器并入「设置中心 ▸ 背景设置」（问题④，删 `MapBrowserWindow`，N3）
- **`OpenBackgroundTab()` 归属（N3，单一所有权）**：`SettingsWindow.OpenBackgroundTab()`（public）独占 `SwitchSubTab(2)` + `Show()/Activate()`（`EnsureVisible`）；`PetWindow.OpenBackgroundTab()` 仅负责确保 `_settingsWindow` 实例存在（**不走会早返回跳过切 tab 的 `ShowSettingsWindow()` 早返回分支**），随后调 `_settingsWindow.OpenBackgroundTab()`。菜单 toggle 回退、`RequestSwitchMap`、"调整地图…"统一走 `PetWindow.OpenBackgroundTab()`。
- **删除 `MiniPet/Views/MapBrowserWindow.cs`**（§0 类型搬迁之后）。
- 改造 `BuildBackgroundTab()` 为完整选图器：左栏 搜索+全部地图 `ListBox`+分页（后台 `WaitForMapCatalog`→`ListMapIds`+`GetMapName`）；右栏 大预览 Canvas（≥480×360）+地图信息+crop 信息+「应用到主窗口」。选中地图→后台 `RenderMapFull` 缩略图；预览拖矩形选 crop（沿用 `_thumbScale` 换算，**未拖 crop 时 `CropFull` 归一化为整图**——N1）。应用→`OnBackgroundMapApplied(MapSelection)`→`ApplyMapSelection`。
- **crop 选择框高对比（问题④a）**：`Stroke=#FFE14D` 亮黄 + `StrokeThickness=2` + 外加 1px 黑色半透描边光晕；选中区填淡白(40 alpha)。
- 删除 `OpenMapBrowserFromSettings`。

### 4. 持久化与迁移（F2/F10）
- **新增 `MapHistoryEntry { Id, Name, CropX, CropY, CropW, CropH }`**（AppConfig.cs）；`PetSettings.MapHistory` 改 `List<MapHistoryEntry>`。
- **兼容 converter（F2，否则旧配置被清空）**：`JsonConverter<MapHistoryEntry>`，`Read` 同时接受 `TokenType.String`（→ `new MapHistoryEntry{Id=str, Crop*=0}`，**Crop*=0 经 N1 归一化为整图**）与 `StartObject`；在 `ConfigService.GetJsonOptions()` 注册（STJ 按数组元素逐个调）。`LoadSettings` 捕 `JsonException`→`CreateDefaultSettings`→`SaveSettings` 覆写（ConfigService.cs:54-65），不加 converter 则所有现有用户配置被默认值覆写。加旧配置 fixture 验证。
- `ApplyMapSelection` 写历史（F10）：`s.MapHistory.RemoveAll(x => x.Id == sel.Id)`；`s.MapHistory.Insert(0, new MapHistoryEntry{Id=sel.Id,Name=sel.Name,CropX=sel.CropX,CropY=sel.CropY,CropW=sel.CropW,CropH=sel.CropH})`；`Take(10)`。
- 所有写 `PetSettings` 走 `ApplyMapSelection` 单一入口。

## Key decisions & tradeoffs
- **crop = 整图像素子区 + 深拷贝**（ADR-0004，F3）：根治 back 错位且不引入缓存别名野指针；代价=整图渲染一次（缓存之）。
- **back 公式逐行对齐后端 `MapRenderHandler`**：含 `type` 重映射、Scroll 位 `(tm&4)/(tm&8)`、**减 origin**（F6）、**flip 绕放置点**（N2）、`screenMode` 跳过（F7）。
- **SKBitmap 缓存 + CAS 发布**（F11/N4）：per-map 缓存 + 切图/退出 Dispose + 过期输者 Dispose，防 native 泄漏。
- **"无 crop = 整图" 单一归一化点**（N1）：`CropFull` 入口 `cw<=0→全图`，全链路统一。
- **菜单仿「切换人物」最近使用**：toggle 解决"关闭后无入口"；历史每条带 crop 一键恢复。
- **删 `MapBrowserWindow`，选图器进设置中心**；`OpenBackgroundTab()` 单一归属（N3）。
- **历史 schema 变更 + converter**（F2）：必须迁移兼容，否则清空用户配置。

## Risks / open questions
- 整图渲染成本（大图 ~3000×900）：缓存 per-map，切图才重算；首次选中 spinner。
- back 公式 Java→Skia 移植：坐标/flip 已逐项对齐 Java `dr`（F6/N2 后为可算等价，非"目视验收"）；以射手村 100000000 目视最终确认。
- 设置中心背景 tab 预览尺寸放大到能拖 crop。
- "无 crop=整图"→ 全图窗口可能较大（迁移历史项首次显示）；用户随后可在选图器拖 crop 选区。

## Out of scope
- 地图缩放/滚轮、多地图同屏、全局热键、foothold 物理行走、crop 桌面实时平移。
- 后端 `renderMapObj` 分组、life/portal/foothold 调试绘制。
- 纸娃娃/精灵渲染（独立任务；耳朵问题已记，grill 后单独处理）。
- tile/obj 的 `DrawSprite` flip（同样是绕中心 vs 绕放置点）——pre-existing，非本轮 back 层范围，不并入。
- tile/obj 的 zMap 精细化排序（仅 back 层本轮修）。
