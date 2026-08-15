# MiniPet 代码规范与可读性审查（完整版）

> 范围：全项目 10798 行 C#（27 个 `.cs` 文件）。基于当前代码静态审查，**未修改**。
> 行号尽量精确；个别次要文件来自扫描，标注「扫描」。

---

## 整体评估

- **能跑、功能完整**，注释密度高、中文说明详尽（这点优于多数项目）。
- **主要问题集中在结构层**：5 个 God Class、UI 大量手写而非 axaml、异常全量吞噬、调试残留未清。
- 单个文件平均行数偏高，**前 5 个文件占总代码量 58%**，长尾小文件健康。

---

## 一、God Class（最突出问题）

| 文件 | 行数 | 承担职责（过载） | 拆分建议 |
|------|------|------------------|----------|
| `Views/SettingsWindow.axaml.cs` | **2266** | 手写整套 UI + 宠物/纸娃娃/背景/气泡/地图/Hermes 多 tab 全部逻辑 + 预览动画 + WZ 配置弹窗 + 浏览器嵌入 | 按 tab 拆局部类（`PetTab`/`PaperdollTab`/`BackgroundTab`/`BalloonTab`），UI 迁 `.axaml` |
| `Views/PetWindow.axaml.cs` | **1214** | UI 构建 + 右键菜单（人物/动画/背景模式/气泡/Hermes/设置）+ 33ms tick + 拖拽 + 气泡定时 + Background/MapViewport 窗口管理 + 模式切换 + SettingsWindow 管理 + 纸娃娃应用 + 地图落地 + 缩放 + 托盘 | 抽 `PetMenuBuilder` / `MapModeController` / `BalloonScheduler` |
| `Services/WzService.cs` | **1053** | WZ 加载 + 节点查找 + PNG 提取 + 精灵图条构建 + 动作列表/空动作校验 + 气泡切片 + 地图目录与三级层级 + 纸娃娃诊断 | 拆 `WzRepository`（薄数据层）+ `SpriteBuilder` + `MapCatalogService` |
| `Services/MapService.cs` | **891** | 地图解析 + bounds + 精灵缓存 + 整图缓存 + `RenderViewport` + CropFull 裁紧 + 缩略图 + 地面 Y | 拆 `MapDataParser` + `MapRenderer` + 独立缓存层 |
| `Views/MaterialBrowserWindow.cs` | **866**（扫描） | UI + 数据加载 + 分页 + 搜索 + 预览 + WZ 交互 | 拆 ViewModel + Pagination + UI |
| `Services/PaperdollService.cs` | 544 | 临界，职责相对聚焦（角色合成） | 可暂不拆 |

> 判定标准：行数 > 500 且多条不相干职责挤在一个类。前 5 个均满足。

---

## 二、手写 UI 而非 axaml（声明式）

- 🔴 `SettingsWindow.axaml.cs`：2266 行几乎全用 `new Border/StackPanel/Button/TextBlock` 手写布局，内联颜色/字号/边距魔法值。**应迁到 `.axaml` + DataTemplate + Styles**。
- 🟠 `MaterialBrowserWindow.cs:116-514`（扫描，400+ 行 UI 代码）：分类按钮、列表、分页栏全手写。
- 🟠 `MapBrowserWindow.cs`（扫描）：同类问题。
- 🟠 `PetWindow.BuildContextMenu`：菜单树手写，可考虑 axaml 化或数据驱动。
- 🟡 `BackgroundWindow` / `MapViewportWindow`：UI 较简单，手写可接受。

---

## 三、重复代码

- 🟠 `WzService.ExportSpriteStrip`（458-527）与 `BuildSpriteStripPng`（535-592）：帧遍历 + origin/PNG 提取 + 1×1 过滤逻辑高度重复，`PreloadAction` 还先后各调一次。**应合并为单次遍历**同时产出 PNG 字节与 strip/config。
- 🟠 **try/catch 模板**：全项目 ~200+ 处 `catch (Exception ex) { Console.Error.WriteLine($"[X] Y: {ex.Message}"); }`，每个方法开头都包一层。应抽统一 `Safe.Run(() => ..., "标签")` 或改用日志框架。
- 🟡 WZ 路径拼接：`$"Mob/_Canvas/{PadId(id,7)}.img/..."` 等模式散落多处，可抽路径构造器。
- 🟡 `MapService` 的 `DrawSpriteScreen` + `DrawWorldSprite` + `DrawBackViewport` 里 `new SKPaint { IsAntialias=false, FilterQuality=None }` 重复出现。

---

## 四、宽泛异常吞噬

- 🔴 **全项目 `catch (Exception)`** 模式：只 `Console.Error.WriteLine` 不抛、不区分类型、不分级。
  - 典型：`TrayService.cs`（扫描，15+ 处）、`MaterialBrowserWindow.cs:93-96`、几乎每个 `PetWindow` 事件 handler、`WzService` 各公开方法。
- **危害**：吞掉真实 bug（空引用、越界、OOM），出问题时只能从控制台捞日志，难定位；也掩盖了「设计上不该为 null 却 null」的契约违反。
- **建议**：
  - 区分「可恢复」（WZ 缺资源→回退）与「不可恢复」（编码错误→应抛）。
  - 捕获具体异常类型（`IOException`/`JsonException`/`InvalidOperationException`）。
  - 关键路径（启动、渲染循环）异常应真正暴露，而非静默。

---

## 五、魔法数字 / 硬编码常量

| 位置 | 值 | 含义 |
|------|----|------|
| `PetWindow.axaml.cs` tick | `33` ms | 帧间隔 |
| `AnimService` idle | `8000`~`15000` ms | 空闲随机动作阈值 |
| `PetWindow` 气泡定时 | `30`~`91` s | 随机气泡间隔 |
| `BalloonService._displayDurationMs` | `5000` | 气泡显示时长 |
| `BalloonControl.axaml.cs:70`（扫描） | `3000`~`5001` | 显示毫秒 |
| `MaterialBrowserWindow._pageSize`（扫描） | `10` | 分页大小 |
| `Program.cs:12`（扫描） | `MaxAttempts=360` | 渲染定时器重试 |
| `SpriteService.MaxMobCache` | `3` | 内存 mob 上限 |
| `CacheManager.MaxMemoryCacheSize` | `128MB` | 内存上限 |
| 缩放滑块 | `0.3`~`2.0` | 缩放范围 |

- **建议**：集中到 `Constants.cs` / 配置类，避免散落。

---

## 六、命名问题

### 单字母 / 缩写变量（像素处理密集区，可读性差）
- `WzService.BuildSpriteStripPng`：`cw/ch/fw/fh/ox/oy/dx/dy/mL/mT/mR/mB`（564-583）。
- `SpriteService.BuildFramesCache`：`fw/fh/srcX/srcOffset/dstOffset`（147-161）。
- `MapService.DrawBackViewport`：`cx/cy/tm/l/r/t/b/ix/iy`（800-845）。
- `MapService.FindContentBBox`：`l/t/r/b`（590-608）。
- **建议**：用 `frameWidth/frameHeight/originX/originY/cropLeft/...`，密集数学运算尤其需要。

### 中英混杂
- 字段 `_paperdollCurrent`、`BrushTxtPrimary`、`BrushInputBg`（英文）vs 中文注释。
- 局部变量 `act/frm/gen/ok/sc`（AnimService / PetWindow）。
- **建议**：标识符统一英文，注释中文。

### 魔法字符串当枚举
- `MapData.Type = "n"/"m"`（NPC/Mob）。
- `_currentEntityType = "Mob"/"Npc"`。
- 动作名 `"stand"/"move"/"fly"` 散落比较。
- **建议**：抽 `enum EntityType { Mob, Npc }`、`enum LifeType { Npc, Mob }`。

---

## 七、死代码 / 调试残留

- 🟠 **PaperdollService DBG 日志**：`CollectPieces`/`ResolveAnchors` 每 piece 多条 `Console.WriteLine("[Paperdoll DBG]…")`（148/164/174/193/243 行）。生产无需。
- 🟠 **PetWindow 启动诊断 Task**：`PetWindow.axaml.cs:235-247` 启动时跑 `CompositeFrame("stand1",0)` 打印诊断，常驻每次启动。
- 🟡 `WzService.LogTopLevelNodes` / `DiagPiece`：诊断用 public 方法，生产可关。
- 🟡 `Console.WriteLine` 调试输出散落（`TrayService` 8+ 处、`MaterialBrowser`、`SpriteService` 缓存加载等，扫描）。
- 🟡 旧禁用块：`PetWindow` 早期有 `if (false && ...)` 自动加载背景地图（需复核是否仍在）。
- **建议**：DBG/诊断改为 `#if DEBUG` 或日志级别开关；移除确认无用块。

---

## 八、注释质量

- ✅ **优点**：注释密度高、中文说明清楚，复杂算法（锚点扩散、视差、9-slice）有原理说明，并对齐参考实现（WzComparerR2.MapRender）。
- 🟡 **内部编号外人难懂**：`MapService` 注释里大量 `#6`、`ADR-0004`、`F3`、`N1`、`F5`、`V2` 等。
  - 如 `CropFull` 注释「ADR-0004 / F3」「#6」「N1」「F5」（MapService.cs:513-519）。
  - 外人/半年后的自己看不懂这些编号指向什么。
- **建议**：要么改用人类可读描述（「裁紧透明边距」），要么在文件头建编号索引表。

---

## 九、null 安全 / 防御式编程过度

- 🟡 `?? 0` / `?.` 链密集：`strip?.OriginX ?? 0`、`FindNodeByPath(...)?.GetValueEx<int>(100) ?? 100` 等（`WzService`/`MapService` 大量）。
  - 数据缺失静默回退默认值，掩盖「本该有值却没」的契约违反。
- 🟡 过度 null 检查：`MaterialBrowserWindow.RefreshList`（扫描 592-593）`if (_itemList == null) return;`，构造已初始化。
- **建议**：明确「必填/可空」契约；必填字段在加载阶段校验，运行期不再 `?? 0` 掩盖。

---

## 十、字段过多 / 状态混杂

- 🟠 `PetWindow`：50+ 实例字段，混合服务依赖（10+）、UI 控件（`_infoText`/`_spriteCanvas`/`_balloonControl`/各 MenuItem）、状态（`_isDragging`/`_paperdollGen`/`_petSwitchGen`/`_scale`）、计时器、缓存。一个类像在管半个应用。
- 🟠 `MaterialBrowserWindow`：22 个字段（扫描 42-63）混 UI/数据/状态。
- **建议**：按职责分组成内嵌类或 ViewModel；UI 字段用 `partial` + axaml 消化。

---

## 十一、配置类职责混乱

- 🟠 `Models/AppConfig.cs` 的 `PetSettings`（108 行，扫描）：塞了 WZ 路径 + 地图（MapMode/Crop*/MapHistory）+ 宠物（CurrentMobId/DefaultAnim）+ 气泡（BalloonId）+ UI（Theme/WindowScale）+ 网络（HermesConfig）+ 收藏/最近 6 类无关配置。
- **建议**：拆 `WzSettings`/`MapSettings`/`PetProfileSettings`/`BalloonSettings`/`UiSettings`/`HistorySettings`，`PetSettings` 作为聚合根持有各子节。
- 🟡 命名风格不一：`MapMode`（int 枚举）vs `MapShowBack`（bool）共存。

---

## 十二、可访问性

- 🟡 公开字段 vs 属性混用：`MapData` 里部分用 `{ get; set; }`，部分裸字段。
- 🟡 `WzService.DiagPiece` / `LogTopLevelNodes` 为 public 但属诊断用，应 internal 或 `#if DEBUG`。
- 🟡 `EventBus` 内部 `_subscribers`/`_lock` 私有无封装（扫描），如需测试可见性应显式定义。

---

## 优先级建议

| 序号 | 项 | 收益 | 工作量 |
|------|----|------|--------|
| 1 | 移除调试残留 + 砍 `catch(Exception)` 到具体类型 | 立刻提升可定位性，工作量小 | 小 |
| 2 | 合并 `ExportSpriteStrip`/`BuildSpriteStripPng` 重复 | 降复杂度 + 顺带修性能（见 PERFORMANCE.md P2-3） | 小 |
| 3 | 拆 `WzService` → Repository + Builder + MapCatalog | 解锁后续拆分，是底层依赖 | 中 |
| 4 | `SettingsWindow` UI 迁 axaml + 按 tab 拆 | 2266 行最大痛点，可分批做 | 大 |
| 5 | 魔法数字集中 + 单字母变量改名 | 机械重构，风险低 | 中 |
| 6 | `PetSettings` 拆分 + enum 化字符串 | 配置可维护性 | 中 |

---

## 已有的良好实践（保持）

- 注释密度高、中文说明详尽，复杂算法有原理 + 参考实现对齐说明。
- 代际 token（`_paperdollGen`/`_petSwitchGen`/`_renderGen`/`_previewGen`）防并发/快速切换串台。
- 锁注释清楚（`_wzLock`/`_loadLock`/`_mapCatalogLock` 各司其职，注释说明为何串行化）。
- 后台 + 代际保护 + CAS 防堆积（`MapViewportWindow._rendering`）的并发模式成熟。
- 空动作过滤、坏缓存自愈（构建/加载/菜单三层一致）。
- LRU + 上限机制在位（阈值待随素材重估）。
- `WzService.CacheStrip` 重活在锁外的优化模式。
- 文档化（CLAUDE.md / docs/adr/ / PERFORMANCE.md / CODE_QUALITY.md）习惯好。
