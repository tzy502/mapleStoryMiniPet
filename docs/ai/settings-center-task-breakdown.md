# 设置中心 — 任务拆解

> 基于 `prototypes/index.html` 原型设计，将设置中心页面拆解为可独立交付的任务单元。
> **本文件仅拆解步骤，不包含实现代码。**

---

## 原型页面结构总览

原型共 3 个顶层 Tab（Pane），对应 App 的 3 个设置域：

| Pane | 名称 | 子 Tab 数量 |
|------|------|------------|
| Pane 0 | 桌宠设置 | 4（素材 / 纸娃娃 / 背景 / 聊天气泡） |
| Pane 1 | AI 设置 | 1 |
| Pane 2 | 对话记录 | 1 |

---

## 任务拆解

### Task 1 — 设置窗口骨架 (SettingsWindow)

**目标**：搭建 NSPanel 设置窗口框架，包含顶层 Tab 切换 + 子 Tab 切换。

**依赖**：无（纯 AppKit UI 基础）

**拆解步骤**：

1. **新建 `SettingsWindow.swift`**（或 `SettingsWindowController.swift`），创建 `SettingsWindowController: NSWindowController`
   - 窗口类型：`NSPanel`，`styleMask: [.titled, .closable, .miniaturizable, .resizable]`
   - 尺寸参考原型：约 1020×740
   - 窗口标题："MiniPet 设置中心"
   - 窗口层级：`.floating`（悬浮于桌面之上，区别于宠物面板的 `.statusBar`）

2. **顶部标题栏**（Traffic Light 红黄绿按钮 + 标题 + 主题切换开关）
   - 使用 `NSVisualEffectView` + `NSTextField` 实现标题栏
   - 主题切换：`NSSwitch` 或自定义 toggle，切换 `.light` / `.dark` appearance
   - 关联 `SettingsManager.current` 持久化主题偏好

3. **顶层 NSTabView / 自定义 Tab Bar**（对应原型 `.nst`）
   - 3 个 Tab："📦 桌宠设置"、"🔗 AI 设置"、"💬 对话记录"
   - 使用 `NSSegmentedControl` 或自定义 `NSView` 实现胶囊风格 Tab
   - 选中态高亮（蓝色下划线 + 文字变色）

4. **子 Tab 栏**（对应原型 `.ptl`）
   - 仅在 Pane 0 显示："素材桌宠设置"、"纸娃娃桌宠设置"、"背景设置"、"聊天气泡设置"
   - 实现方式同顶层 Tab

5. **内容区容器**：`NSTabView` 或自定义 `NSView` 切换，按当前选中的 Pane + Sub Tab 显示对应内容视图

---

### Task 2 — 素材桌宠设置 (Material Pet Settings)

**目标**：实现 Pane 0 → Sub Tab 0 的素材桌宠设置界面。

**依赖**：Task 1（设置窗口骨架）

**已有基础设施**：
- `PetView.loadSprites(for:)` — 已有素材加载
- `APIClient.fetchAndGenerateSprites(mobId:)` — 已有 API
- `CacheManager` — 已有缓存管理
- `MobStore` / `MobInfo` — 已有怪物列表持久化

**拆解步骤**：

1. **左侧边栏 — 搜索栏**
   - `NSSearchField` 或 `NSTextField` 带搜索图标
   - 实时过滤当前列表（怪物/NPC）

2. **左侧边栏 — 分段切换（怪物 / NPC）**
   - `NSSegmentedControl` 或自定义按钮组
   - 切换时重新加载对应分类的素材列表

3. **左侧边栏 — 素材浏览按钮**
   - "🔍 浏览素材库" 按钮 → 触发 Task 8（MaterialPicker 弹窗）
   - 选中素材后回调更新右侧预览

4. **左侧边栏 — 收藏列表 + 最近使用**
   - TableView 或 StackView 实现列表
   - "★ 收藏" section + "最近使用" section
   - 收藏状态持久化到 `SettingsManager`
   - 点击列表项 → 右侧预览切换

5. **右侧预览区**
   - 画布 `NSView`（对应原型 `.cv`）：透明网格背景，居中展示素材
   - 下方参数栏：素材名称 label + 素材 ID 输入框 + 收藏按钮 + 播放按钮 + 复制为 OBJ 按钮 + 确定应用按钮
   - 缩放滑块：`NSSlider`，范围 30%~200%
   - 背景色预设选择：透明 / 网格 / 预设渐变（弓箭手村/市场/森林）
   - 动画预览：利用已有 `PetView.tick()` 在预览区播放当前选中素材的动画

6. **"确定应用"按钮**
   - 调用 `petView.loadSprites(for: selectedMobId)` 切换当前桌宠
   - 更新 `SettingsManager.current.mobId`
   - 关闭设置窗口（或保持打开）

---

### Task 3 — 纸娃娃桌宠设置 (Paperdoll Pet Settings)

**目标**：实现 Pane 0 → Sub Tab 1 的纸娃娃设置界面。

**依赖**：Task 1（设置窗口骨架）

**已有基础设施**：
- `PlayerPaperdollView` — 纸娃娃渲染视图（已有）
- `CharacterAppearance` — 外观数据模型（已有）
- `WzCharacterCompositor` — 角色合成器（已有）
- `WzImageLoader` — WZ 图片加载器（已有）

**拆解步骤**：

1. **左侧边栏 — 多套纸娃娃预设管理**
   - 预设列表（对应原型 `.prs`）：`NSCollectionView` 或水平 StackView
   - "新建"按钮：创建空白外观预设，弹出命名输入框
   - 每个预设卡片：名称 + 删除按钮，支持多选
   - 预设数据持久化到 `SettingsManager.current.paperdollPresets`

2. **左侧边栏 — 基础外观设置**
   - 性别切换：`NSSegmentedControl`（♂/♀）
   - 发型选择：输入框（ID）+ "浏览"按钮 → 触发 MaterialPicker（类型=发型）
   - 脸型选择：同上（类型=脸型）
   - 皮肤选择：同上（类型=皮肤）

3. **左侧边栏 — 装备栏**
   - 8 个装备位：帽子、上衣、裤子、鞋、武器、披风、盾牌、手套
   - 每个装备位：图标 + 名称 + ID + "浏览"按钮 → MaterialPicker
   - 用 `NSTableView` 或 StackView 实现

4. **左侧边栏 — 坐骑/椅子**
   - 坐骑选择：popup + MaterialPicker
   - 椅子选择：popup + MaterialPicker

5. **左侧边栏 — 染色**
   - 开启开关：`NSSwitch`
   - 色相滑块：`NSSlider`，范围 0~360
   - 颜色预览

6. **右侧预览区**
   - 利用已有 `PlayerPaperdollView` 渲染当前纸娃娃外观
   - 缩放控制：`NSStepper` + 百分比显示
   - 动画开关：控制是否播放纸娃娃动画
   - 背景预设选择（同 Task 2）

7. **"确定应用"按钮**
   - 将选中的 `CharacterAppearance` 应用到 `PlayerPaperdollView`
   - 持久化外观到 `~/Library/Caches/MiniPet/player/dress.json`
   - 更新 `SettingsManager.current`

---

### Task 4 — 背景设置 (Background Settings)

**目标**：实现 Pane 0 → Sub Tab 2 的背景设置界面。

**依赖**：Task 1（设置窗口骨架）

**已有基础设施**：
- `BackgroundLayerView` + `BackgroundSceneView` — 地图背景渲染（已有）
- `BackgroundCompositor` — 背景合成器（已有）
- `WzImageLoader` + `WzXmlParser` — WZ 解析（已有）

**拆解步骤**：

1. **左侧边栏 — 地图树**
   - 树形结构 `NSOutlineView` 展示冒险岛地图层级
     - 世界 → 岛屿 → 区域 → 具体地图
   - 节点展开/折叠动画
   - 点击叶子节点（具体地图）→ 右侧预览切换
   - 地图数据来源：API 查询或本地预设（MVP 可用预设数据）

2. **左侧边栏 — 搜索栏**
   - 实时过滤地图树节点

3. **左侧边栏 — 自定义地图入口**
   - "🖼️ 自定义地图" 节点（非树结构，独立入口）
   - 支持组合多个素材作为背景（见 Task 4.5）

4. **右侧预览区**
   - 迷你地图预览 `NSView`（对应原型 `.mp`）
   - 调用已有 `BackgroundLayerView.loadMap(mapId:)` 渲染地图背景
   - 宠物占位图标（示意宠物在地图上的位置）
   - 地图名称标签

5. **右侧预览区 — OBJ 列表管理**
   - 已添加的 OBJ 列表（图标 + 名称 + ID + 删除按钮）
   - 支持拖拽排序（`NSCollectionView` drag & drop）
   - "添加"下拉框：从素材库选择 OBJ 添加到地图
   - OBJ 在地图预览上的位置：可拖拽放置

6. **"确定应用"按钮**
   - 调用 `ContainerView.showBackground(mapId:)` 设置背景
   - 持久化 `SettingsManager.current.mapId`

---

### Task 5 — 聊天气泡设置 (Chat Balloon Settings)

**目标**：实现 Pane 0 → Sub Tab 3 的聊天气泡设置界面。

**依赖**：Task 1（设置窗口骨架）

**已有基础设施**：
- `ChatBalloonView` — 聊天气泡渲染视图（已有）
- `PetView.currentBalloonId` — 当前气泡样式 ID（已有）
- `PetView.loadBalloonTiles()` — 气泡 tile 加载（已有）
- `APIClient.fetchBalloonTiles(balloonId:)` / `fetchBalloonClr(balloonId:)` — API（已有）

**拆解步骤**：

1. **左侧边栏 — 搜索栏**
   - 按气泡 ID 或名称过滤

2. **左侧边栏 — 气泡样式列表**
   - `NSTableView` 或 StackView，每行：缩略图 + 名称 + ID + 选中勾
   - 数据来源：预设气泡 ID 列表（5, 560, 3, 50, 100, 200）
   - 支持从后端导入更多气泡样式

3. **左侧边栏 — 自定义颜色**
   - 气泡 ID 输入框
   - 文字颜色选择器：`NSColorWell`
   - 气泡背景颜色选择器：`NSColorWell`
   - "应用"按钮：即时预览

4. **右侧预览区**
   - 聊天气泡实时预览（对应原型 `.bub`）
   - 利用已有 `ChatBalloonView` 渲染选中样式
   - 预览文字可编辑（模拟实际对话内容）
   - 预览测试按钮 / 隐藏按钮

5. **"确定应用"按钮**
   - 更新 `PetView.currentBalloonId`
   - 持久化到 `SettingsManager.current`

---

### Task 6 — AI 设置 (Hermes AI Settings)

**目标**：实现 Pane 1 的 AI 设置界面。

**依赖**：Task 1（设置窗口骨架）

**已有基础设施**：
- `HermesClient` — AI 客户端（已有）
- `TerminalView` — 终端视图（已有）
- `sessionPath` — 会话文件路径（已有）

**拆解步骤**：

1. **AI 连接状态卡片**
   - 状态指示器（绿/黄/红圆点）
   - 显示当前连接状态：已连接 / 断开 / 重连中
   - 模型信息：模型名称、上下文大小
   - 活动指标：进度条 + 百分比
   - 最近活动描述
   - "断开连接" / "重新连接" 按钮

2. **反应规则表格**
   - `NSTableView`，列：关键词、动画、延迟(秒)、优先级、启用开关、操作
   - 动画列：`NSPopUpButton`（move/stand/attack/skill/die/hit）
   - 操作列：编辑 / 删除按钮
   - "添加"按钮：新增一行
   - "启用在 Hermes 对话中自动侦测关键词"开关
   - 规则数据持久化到 `SettingsManager`

3. **最近对话列表**
   - 时间戳 + 发送者 + 消息内容
   - "清空"按钮

---

### Task 7 — 对话记录设置 (Chat Log Settings)

**目标**：实现 Pane 2 的对话记录设置界面。

**依赖**：Task 1（设置窗口骨架）

**已有基础设施**：
- `TerminalView` — 终端/对话视图（已有）
- `HermesClient` — AI 客户端（已有）

**拆解步骤**：

1. **快捷键设置**
   - 呼出快捷键：label + "录制"按钮
     - 点击"录制" → 捕获下一次按键组合 → 显示为快捷键
     - 默认：`⌘ + ⇧ + Space`
   - 隐藏快捷键：label（Esc）
   - 语音输入快捷键：label + "录制"按钮
     - 默认：`⌘ + ⇧ + M`
   - 持久化到 `SettingsManager`

2. **外观设置**
   - 主题切换：`NSSegmentedControl`（跟随系统 / 浅色 / 深色）
   - 字体：popup 选择
   - 消息对齐：`NSSegmentedControl`（左右 / 居中）
   - 透明度：`NSSlider`，范围 30%~100%

3. **语音输入设置**
   - 启用开关：`NSSwitch`
   - 引擎选择：popup（macOS 系统语音）
   - 语言选择：popup（中文/英文等）

---

### Task 8 — 素材选择器弹窗 (MaterialPicker Modal)

**目标**：实现可复用的素材浏览/选择弹窗，供 Task 2/3/4 中的"浏览"按钮触发。

**依赖**：Task 1（需要设置窗口作为父窗口）

**已有基础设施**：
- `APIClient+WZ.swift` — WZ 数据查询 API
- `WzImageLoader` — WZ 图片加载
- `WzModel` — WZ 数据模型

**拆解步骤**：

1. **弹窗骨架**
   - `NSPanel` 或 `NSWindow`，modal 模式
   - 尺寸参考原型：约 860×600
   - Traffic Light 按钮 + 标题

2. **搜索栏 + 高级筛选**
   - `NSSearchField`：搜索名称或 ID
   - 高级搜索 toggle：展开筛选面板
   - 类型筛选 chips：`NSSegmentedControl` 或自定义 chip 按钮组
     - 类型集合取决于调用方传入的 `types` 参数（如 ["怪物", "NPC"] 或 ["帽子"]）

3. **左侧分类树**
   - `NSOutlineView` 展示类型分组
   - 点击类型节点 → 右侧列表过滤

4. **中间素材列表**
   - `NSTableView`，列：类型图标 + 名称 + ID
   - 支持排序（名称/ID）
   - 分页：上一页/下一页按钮
   - 点击行 → 右侧详情面板更新

5. **右侧详情面板**
   - 素材预览图（带透明网格背景）
   - 素材名称 + ID
   - 类型标签
   - 描述文本
   - "✅ 选择此素材" 按钮 + "取消" 按钮

6. **回调机制**
   - `completion: (String, String) -> Void` — 返回 (素材ID, 素材名称)
   - 调用方通过 completion handler 接收选中结果

7. **数据来源**
   - 类型列表：从 WZ API 查询或使用本地预设数据
   - 素材列表：按类型查询 WZ 数据（已有 `APIClient+WZ` 基础设施）
   - MVP 可用静态 mock 数据，后续接入真实 API

---

### Task 9 — 数据模型扩展与持久化

**目标**：扩展 `PetSettings` 以覆盖所有设置项，统一持久化逻辑。

**依赖**：Task 1~7 按需并行

**拆解步骤**：

1. **扩展 `PetSettings`**（`Models.swift` 或 `SettingsManager.swift`）
   - 新增字段：
     - `theme: String` — 主题（dark/light/system）
     - `favoriteMobIds: [String]` — 收藏素材 ID 列表
     - `recentMobIds: [String]` — 最近使用素材 ID 列表
     - `paperdollPresets: [CharacterAppearance]` — 纸娃娃预设列表
     - `currentPaperdollIndex: Int` — 当前选中的纸娃娃预设索引
     - `objList: [MapObject]` — 地图上的 OBJ 列表
     - `balloonId: Int` — 聊天气泡样式 ID
     - `balloonCustomBg: String?` — 自定义气泡背景色
     - `balloonCustomTextColor: String?` — 自定义气泡文字色
     - `reactionRules: [ReactionRule]` — AI 反应规则列表
     - `shortcuts: [String: String]` — 快捷键映射
     - `chatTheme: String` — 对话主题
     - `chatFontSize: Double` — 对话字体大小
     - `chatAlignment: String` — 对话消息对齐

2. **迁移兼容**
   - 确保旧版本 `settings.json` 可被新 `PetSettings` 解码（所有新字段有默认值）

3. **统一读写接口**
   - `SettingsManager` 保持 `static var current` + `save(_:)` 模式
   - 各设置 Tab 的 ViewController 通过 `SettingsManager.current` 读写

---

### Task 10 — 设置入口与窗口生命周期

**目标**：将设置窗口接入 App 主流程，提供打开/关闭入口。

**依赖**：Task 1

**拆解步骤**：

1. **打开设置窗口的入口**
   - 状态栏菜单新增 "设置中心…" 菜单项（`StatusBarController.buildMenu`）
   - 快捷键：`⌘ + ,`
   - 右键菜单新增 "设置中心…"（`PetView.rightMouseDown`）

2. **窗口单例管理**
   - `SettingsWindowController` 使用单例模式
   - 重复点击设置菜单 → 将已有窗口 bringToFront，不重复创建

3. **窗口关闭行为**
   - 关闭时自动保存 `SettingsManager.current`
   - 关闭时通知 `PetView` 应用变更（如素材切换、背景切换等）

4. **与 PetView 双向同步**
   - 设置窗口修改 → 实时预览到 PetView（如切换素材、气泡样式）
   - PetView 状态变化 → 设置窗口同步显示（如当前动画、当前怪物）

---

## 依赖关系图

```
Task 1 (设置窗口骨架)
 ├── Task 2 (素材桌宠设置) ──── Task 8 (MaterialPicker) ──┐
 ├── Task 3 (纸娃娃设置) ────── Task 8 (MaterialPicker) ──┤
 ├── Task 4 (背景设置) ──────── Task 8 (MaterialPicker) ──┤
 ├── Task 5 (聊天气泡设置) ───────────────────────────────┤
 ├── Task 6 (AI 设置) ───────────────────────────────────┤
 └── Task 7 (对话记录设置) ───────────────────────────────┘
                              │
Task 9 (数据模型扩展) ←────────┘ (并行，无强依赖)
Task 10 (设置入口) ←── Task 1
```

## 建议执行顺序

| 优先级 | Task | 理由 |
|--------|------|------|
| P0 | Task 1 | 骨架先行，其他 Task 才能挂载 |
| P0 | Task 9 | 数据模型扩展宜早不宜晚 |
| P1 | Task 8 | MaterialPicker 被多个 Task 依赖 |
| P1 | Task 5 | 聊天气泡设置最轻量，可快速验证流程 |
| P2 | Task 2 | 素材桌宠是核心功能 |
| P2 | Task 10 | 入口打通后可端到端测试 |
| P3 | Task 3 | 纸娃娃设置（依赖现有 PlayerPaperdollView） |
| P3 | Task 4 | 背景设置（依赖 BackgroundLayerView） |
| P3 | Task 6 | AI 设置 |
| P4 | Task 7 | 对话记录设置 |

---

## 与现有代码的对接点

| 原型元素 | 现有 Swift 代码 | 对接方式 |
|----------|----------------|---------|
| 素材预览/切换 | `PetView.loadSprites(for:)` | 直接调用 |
| 纸娃娃预览 | `PlayerPaperdollView` | 更新 `characterAppearance` 后 `invalidateCompositedFrames()` |
| 地图背景预览 | `BackgroundLayerView.loadMap(mapId:)` | 直接调用 |
| 聊天气泡预览 | `ChatBalloonView` / `PetView.currentBalloonId` | 更新 `currentBalloonId` + `loadBalloonTiles()` |
| 怪物列表 | `MobStore` / `PetView.mobList` | 读写 `MobStore` |
| AI 连接状态 | `HermesClient` | 新增状态查询方法 |
| 设置持久化 | `SettingsManager` / `PetSettings` | 扩展字段 |
| 快捷键 | `AppDelegate` / 系统 `NSEvent.addLocalMonitorForEvents` | 注册全局快捷键 |
