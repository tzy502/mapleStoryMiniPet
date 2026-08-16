# MiniPet 性能问题清单（基于当前代码）

> 基准：以「当前桌宠素材大 5 倍」预估。
> 参照现行 mob `8880837`（路西德）：stand 612×403 / 16 帧 / 1.6MB，最大单动作 `die1.png` 已达 **46MB**。
> 大 5 倍 ≈ 帧尺寸 ~3000×2000、帧数 ~80、单动作条带 200MB+。
>
> 复杂度推算基于代码 O(n) 分析；绝对耗时/内存以实测为准。**本文档仅列问题，未修改。**

---

## 严重度图例

- 🔴 严重：大素材下直接卡死/爆内存，必须优先
- 🟠 中等：明显卡顿或资源浪费
- 🟡 轻微：当前无感，特定场景才暴露

---

## 一、桌宠每帧渲染热路径（影响日常流畅度）

### 🔴 P1-1 每帧重新解码整帧 PNG
- **位置**：`MiniPet/Views/Controls/SpriteCanvas.axaml.cs:34-36`
- **问题**：每 33ms tick 都 `new MemoryStream(pngData)` + `new Bitmap(ms)` 把整帧 PNG 解码一次，并 `Dispose` 上一帧。桌宠链路（区别于地图摄像机）**没有**用 `WriteableBitmap` 直拷，仍走 PNG 解码。
- **大 5 倍影响**：单帧解码耗时随帧像素数线性增长；叠加上锁与布局，单帧可能超过 33ms 预算 → 掉帧。
- **建议方向**：参照摄像机模式的 `WriteableBitmap` 直拷；或缓存解码后位图按 (mobId,action,frame) 复用。

### 🔴 P1-2 整帧像素在管线里被反复编解码
- **位置**：`WzService.cs:535`（`BuildSpriteStripPng`）/ `SpriteService.cs:139`（`BuildFramesCache`）/ `SpriteCanvas.axaml.cs:34`（`SetFrame`）
- **问题**：一帧从 WZ 到屏幕经历
  `PngEncoder.DecodePixels`(解 WZ) → `PngEncoder.Encode` 整条 → 落盘/内存 → `PngEncoder.DecodePng` 整条 → 逐帧 `PngEncoder.Encode` → `_framesCache` → 每帧 Avalonia `Bitmap` 解码。
  **同一像素被编/解码 4~5 次。**
- **大 5 倍影响**：每次编解码都是 O(像素数)，叠加帧数为 O(像素数 × 帧数)，是桌宠全链路最大 CPU 亏损。
- **建议方向**：以 BGRA 字节/`SKBitmap` 为统一缓存格式；播放路径直接像素拷贝到 `WriteableBitmap`，彻底跳过 PNG。

### 🟠 P1-3 每帧两次持 `SpriteService._lock`
- **位置**：`PetWindow.OnTimerTick`（`GetCachedStrip`）+ `AnimService.Tick`（`GetFramePng`）
- **问题**：每个 tick 两次进/出 `_lock`；`GetCachedStrip` 内还做 `_accessOrder.Remove/AddFirst`（LRU 链表维护）。
- **大 5 倍影响**：本身轻，但 `_framesCache` 变大后与后台 `CacheStrip` 抢锁概率上升。
- **建议方向**：`AnimService` 持有当前 strip 引用，UI 不每帧重复 `GetCachedStrip`。

---

## 二、桌宠构建 / 加载（一次性，大素材下达数秒~数十秒）

### 🔴 P2-1 一次性分配整条 BGRA + 嵌套像素拷贝
- **位置**：`WzService.cs:535-592`（`BuildSpriteStripPng`；巨型数组 569，嵌套 `Buffer.BlockCopy` 570-584，整条 `PngEncoder.Encode` 585）
- **问题**：`new byte[cw * frames.Count * ch * 4]` 是「帧宽 × 帧数 × 帧高 × 4」的连续巨型数组，再双层循环 `Buffer.BlockCopy` 逐行拷，最后 `PngEncoder.Encode` 整条。另外 564-565 连调 4 次 `frames.Max(...)` 重复遍历 frames。
- **大 5 倍影响**：复杂度 O(帧宽 × 帧高 × 帧数)；帧尺寸 ×5 且帧数 ×5 时达 **25 倍**工作量，单数组可达 GB 级。
- **建议方向**：流式逐帧写入；或直接缓存单帧 BGRA，避免整条巨型数组与逐行拷贝。

### 🔴 P2-2 解码整条 + 逐帧重编码
- **位置**：`SpriteService.cs:139-175`（`BuildFramesCache`）
- **问题**：先 `DecodePng` 把整条条带解成像素，再逐帧 `BlockCopy` 裁出 + `PngEncoder.Encode` 每帧重编一次。整条已在 P2-1 编码过一次，这里又 N 次编码。
- **大 5 倍影响**：构建阶段最大耗时项；与 P2-1 叠加，单动作构建可达数秒~十几秒。
- **建议方向**：缓存裁剪后的 BGRA（不再逐帧 PNG 编码）。

### 🟠 P2-3 `ExportSpriteStrip` 与 `BuildSpriteStripPng` 重复遍历
- **位置**：`WzService.cs:458-527`（`ExportSpriteStrip`，帧遍历 475-496）；与 `BuildSpriteStripPng`（535-592）逻辑高度重复
- **问题**：两者各自遍历帧、取 origin/PNG，却先后各跑一遍（`SpriteService.PreloadAction` 先 `BuildSpriteStripPng` 再 `ExportSpriteStrip`），等于做了两遍；且各独立持 `_wzLock`（461 / 538）+ `TryExtract`。
- **建议方向**：合并为一次遍历，同时产出 PNG 字节与 strip/config。

### 🟠 P2-4 纸娃娃逐件解码 + 海量调试日志
- **位置**：`PaperdollService.cs`（`CollectPieces` / `ResolveAnchors`）
- **问题**：每个 piece 都 `ExtractPng` + `SKBitmap.Decode`；每 piece 多条 `Console.WriteLine("[Paperdoll DBG]…")` 同步 IO。
- **大 5 倍影响**：5 动作 × 多帧 × 十几件 × 日志同步写盘，构建墙钟被日志显著拖长。
- **建议方向**：移除/条件化 DBG 日志；piece 位图按 (root,action,frame,piece) 缓存复用。

---

## 三、内存占用（大素材下爆内存 / 缓存策略失效）

### 🔴 P3-1 `_framesCache` key 不含 mobId（正确性 + 性能双重 bug）
- **位置**：`SpriteService.cs:400`（`MakeFrameKey = "{action}_{frameIndex}"`）
- **问题**：不同 mob 同名 action 共用同一 key，后进覆盖先进 → **切换 mob 显示错帧**；`EvictIfNeeded` 按 action 前缀清理时会误删其他 mob 的帧，触发反复重载。
- **建议方向**：key 改为 `{mobId}_{action}_{frameIndex}`。

### 🔴 P3-2 `_framesCache` 常驻所有帧 PNG 字节
- **位置**：`SpriteService.cs:90,166`
- **问题**：每个动作的每一帧都缓存一份独立 PNG 字节，随「动作数 × 帧数 × 单帧大小」膨胀。
- **大 5 倍影响**：常驻内存大头；多动作高帧数时轻易突破 GB 级。
- **建议方向**：缓存 BGRA 或只缓存当前动作的可见帧；结合 P1-2 改格式。

### 🟠 P3-3 `EvictIfNeeded` 全表扫描 + 循环重算
- **位置**：`SpriteService.cs:402-438`
- **问题**：`_framesCache.Keys.Where(k => k.StartsWith(action+"_")).ToList()` 每次淘汰遍历整个帧缓存；while 里还反复 `_memoryCache.Keys.Select(...).Distinct().Count()` 重算 mob 数。
- **大 5 倍影响**：帧缓存越大，每次 `CacheStrip` 的淘汰开销越高（O(n) × 循环）。
- **建议方向**：维护 mob→frames 反向索引，淘汰时 O(1) 定位；mob 计数增量维护。

### 🟠 P3-4 `MaxMobCache=3` 与 128MB 上限过低
- **位置**：`SpriteService.cs:20`；`CacheManager.cs:17`
- **问题**：内存层最多留 3 个 mob、128MB。大素材下 1~2 个 mob 即超限，LRU 频繁淘汰 → 反复走 P2-1/P2-2 构建链路。
- **建议方向**：上限改为按字节数动态评估；或区分「热数据（BGRA）」与「冷数据（PNG 字节）」两级。

### 🟡 P3-5 地图精灵缓存 `_spriteCache` 无上限
- **位置**：`MapService.cs:44`
- **问题**：摄像机模式下 `wzPath → SKBitmap` 懒加载缓存，无 LRU/容量上限。大地图（obj/tile/back 数千）累积大量 SKBitmap，常驻内存膨胀。
- **建议方向**：加上限 + LRU；或在 `ClearSpriteCache` 之外做按容量淘汰。

---

## 四、锁

### 🔴 P4-1 `RenderViewport` 整帧持 `_wzLock`
- **位置**：`MapService.cs:675`（`RenderViewport` 全程 `WithWzLock`）
- **问题**：摄像机模式 30fps（拖动时更高频）每帧渲染全程持有 `_wzLock`，期间桌宠的 WZ 操作（`BuildSpriteStripPng`、`GetActionList`、`ExtractPng`、诊断预热）全部排队阻塞。
- **大 5 倍影响**：摄像机开起来后，桌宠任何后台构建/菜单枚举被串行阻塞，叠加素材变大使单帧渲染更慢，互相拖累。
- **建议方向**：精灵缓存命中后 `DrawBitmap` 不需要锁（只是 SKBitmap 读取），把锁范围收缩到「首次 `ExtractPng`/`GetOrigin` 懒加载」；或读路径改读写锁（多读单写）。

### 🟠 P4-2 `_wzLock` 串行化所有 WZ 操作
- **位置**：`WzService.cs:22`
- **问题**：构建（P2-1/P2-3）、`GetActionList`（含 `HasValidFramePng` 逐帧校验）、`EnsureMapCatalog` 后台建表、`PetWindow` 启动诊断预热、`RenderViewport`（P4-1），全部抢同一把锁。
- **建议方向**：读路径读写锁；`HasValidFramePng` 校验结果缓存，避免每次开菜单重复解帧。

---

## 五、地图摄像机渲染（模式2，每帧开销）

### 🟠 P5-1 每帧新建 SKBitmap 画布 + 多个 SKPaint
- **位置**：`MapService.RenderViewport`（`new SKBitmap(screenW,screenH)` 681 行；各绘制路径 `new SKPaint` 756/782/820 等）
- **问题**：30fps 每帧分配整张画布 + 多个 `SKPaint`，未复用。
- **大 5 倍影响**：`WriteableBitmap` 直拷已省了 PNG 编解码，但每帧大画布/paint 分配仍造成 GC 压力；窗口尺寸大时单画布内存高。
- **建议方向**：复用画布（按窗口尺寸缓存，尺寸变才重建）；`SKPaint` 提为字段复用。

### 🟠 P5-2 每帧 LINQ 排序 8 层混合
- **位置**：`MapService.cs:703-708`
- **问题**：每帧每层 `new List<>` + `.OrderBy(Z0).ThenBy(Z1)`，obj/tile 多时每帧分配大量临时对象与排序开销。
- **建议方向**：排序结果按 mapId 缓存（地图不变则 (Z0,Z1) 序固定），切图/分层 toggle 时失效。

### 🟡 P5-3 `CropFull` 全像素 alpha 扫描
- **位置**：`MapService.cs:586-609`（`FindContentBBox`）
- **问题**：裁紧 padding 时 O(w×h) 扫描 alpha。仅在 `SetMap` 时调用一次（非每帧），但大 crop 单次重。
- **建议方向**：渲染时记录非透明 bbox；或按行/列提前终止。

---

## 六、气泡 / 列表 / 其他（与 mob 大小无关；次要）

### 🟡 P6-1 气泡切片每次重新解码 + 每次新建画布/PNG 编码
- **位置**：`BalloonService.cs:91-100,122,183-185`
- **问题**：`RenderBalloon` 每次把 10 个 9-slice 切片 `SKBitmap.Decode` 一遍（未缓存解码结果）+ 新建整张画布 + PNG 编码。渲染在 `Task.Run` 不卡 UI，但 CPU/GC 浪费。
- **建议方向**：按 balloonId 缓存解码后的切片 `SKBitmap`；相同 (balloonId,text) 缓存渲染结果。

### 🟡 P6-2 素材/地图浏览器全量加载
- **位置**：`MaterialBrowserWindow.cs`（`LoadFromWz`）；`MapBrowserWindow.cs`（列表构造）
- **问题**：一次性枚举上千 ID + 逐条查名构造列表项，再内存分页。
- **建议方向**：复用 `EnsureMapCatalog` 的 `_mapNameCache`；名称查询结果缓存；列表虚拟化。

### 🟡 P6-3 `LoadSettings` 持锁做磁盘 IO
- **位置**：`ConfigService.cs:39-67`
- **问题**：`File.ReadAllText` + JSON 反序列化全程持 `_lock`。
- **建议方向**：锁外读字节，锁内仅赋值与发布。

---

## 优先级建议

| 序号 | 问题 | 收益面 |
|------|------|--------|
| 1 | P1-2 + P3-2 + P2-2 | 统一缓存格式为 BGRA/位图，消除反复编解码与每帧解码 → 覆盖日常播放与构建，收益最大 |
| 2 | P3-1 | `_framesCache` key 加 mobId → 顺带修正确性 bug，并让 P3-3 淘汰不再误删 |
| 3 | P4-1 + P4-2 | `RenderViewport` 收锁 + WZ 读路径读写锁 → 摄像机与桌宠不再互相阻塞 |
| 4 | P2-1 + P2-3 | 构建期去巨型数组、合并重复遍历 → 切换/首次加载不再长时间卡 |
| 5 | P5-1 + P5-2 | 摄像机画布/paint 复用 + 排序结果缓存 → 拖动跟手更稳、GC 压力下降 |

---

## 已有的良好设计（保持）

- **摄像机模式 `WriteableBitmap` 直拷**（`MapViewportWindow.KickRender`）：跳过 PNG 编解码，`_rendering` CAS 防堆积，代际保护防串台。
- **`SpriteService.CacheStrip` 重活在锁外、锁内仅字典合并**（`SpriteService.cs:99-133`）。
- **地图精灵缓存 `_spriteCache`**（`MapService.cs:44-71`）：懒加载，避免每帧 `ExtractPng`。
- **整图渲染缓存 `_fullRender`**（`MapService.cs:16,494-511`）：桌面 crop 与设置预览共用一张。
- **后台渲染 + 代际保护**：`BackgroundWindow` / `MapViewportWindow` / `PetWindow.ApplyPaperdoll` / `PetWindow.SwitchToPet`。
- **空动作过滤**：构建期跳过 1×1 退化帧（`WzService`），加载期清理坏缓存（`SpriteService`），菜单不列出空动作。
- **`BalloonService` 渲染放 `Task.Run`**，不阻塞 UI。
- **LRU 限制 mob 数与 128MB 内存上限**（机制在，阈值需随素材重估 → 见 P3-4）。
