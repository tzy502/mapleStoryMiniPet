# 设计评审 — MapleStory MiniPet v0.3

> 生成日期：2026-07-03
> 分析范围：Sources/MiniPet/ 下 20 个 Swift 文件，共 ~2637 行
> 编译状态：✅ 编译通过（1 个 unused variable 警告）
> 后端状态：❌ API 不可达（connection refused，无法运行时验证）

---

## 目录

1. [架构全景](#1-架构全景)
2. [代码质量评估](#2-代码质量评估)
3. [UI 设计模式分析](#3-ui-设计模式分析)
4. [WZ 合成管线集成设计](#4-wz-合成管线集成设计)
5. [优化建议](#5-优化建议)
6. [设计决策记录](#6-设计决策记录)
7. [里程碑更新建议](#7-里程碑更新建议)

---

## 1. 架构全景

### 1.1 当前架构图

```
main.swift (58 行入口)
  │
  ├── PetPanel             NSPanel 透明悬浮面板 (8 行)
  │
  └── ContainerView (109 行)         布局容器
        ├── PetView (651 行)         精灵图引擎 + 动画控制器 + 鼠标事件 + Mob 管理
        ├── TerminalView (129 行)    PTY 终端（黑底绿字）
        └── ChatBalloonView (365 行) 9-slice 聊天气泡渲染

独立模块:
├── APIClient (174 行)          HTTP 客户端（health/mob/balloon/taming 等）
├── CacheManager (51 行)        本地缓存管理
├── HermesClient (66 行)        hermes CLI per-message 进程管理
├── BotClient (61 行)           QQ Bot HTTP 通信协议
├── CLIArgs (84 行)             命令行参数 + 管理命令执行
├── Constants (14 行)           全局常量
├── Models (59 行)              数据模型 + MobStore 持久化
├── Helpers (45 行)             日志 + ANSI 剥除
├── InputDialog (58 行)         输入弹窗
├── StatusBarController (120 行) 系统菜单栏控制器
├── AppDelegate (33 行)         退出动画 + 生命周期

WZ 前端处理模块 (NEW, 未集成):
├── WzModel (244 行)            WZ XML 解析器 + 节点模型
├── WzImageLoader (106 行)      WZ 图片下载器 + 缓存 + 帧路径发现
└── WzCompositor (149 行)       合成引擎 + Avatar/Background/Ride 编排
```

### 1.2 核心数据流

```
API (wiki-backend)                  PetView (内存)                  CALayer (显示)
  │                                    │                              │
  ├── fetch_and_generate.py            ├── strips[String: StripData]  ├── layer.contents = CGImage
  │     → 精灵图条 PNG                  │     per-animation 条状图    │     逐帧裁剪显示
  │     → pet_config.json               │     + 独立 origin           │
  │                                    ├── images[String: [CGImage]]  │
  ├── fetchMobList()                   │     裁剪后的逐帧数组         │
  ├── fetchMobName()                   │                              │
  ├── fetchBalloonTiles()              ├── tick() 120ms 循环          │
  │     → 11 张 PNG 切片               │     fi = (fi+1) % count      │
  └── (WZ XML/Image APIs)             └── switchTo() 切换动画         └── 尺寸 + origin 自适应
        ↓                             ↓
  未接入                     WzModel / WzImageLoader
                            (定义完成, 等待集成)
```

### 1.3 架构关键观察

**好的方面**：
- 模块拆分清晰：API、缓存、UI、模型各司其职
- Swift 原生渲染，无第三方依赖
- 精灵图条方案成熟（水平条带 + CGImage 裁剪）

**问题**：
- `PetView` 是上帝类：651 行，6 个不相关职责（动画引擎、鼠标事件、mob 管理、timers、balloon 控制、hermes 感应）
- WZ 前端处理代码已实现但完全未接入主渲染管线
- 无单元测试 / UI 测试
- 错误处理分散：多数 `try?` 静默失败

---

## 2. 代码质量评估

### 2.1 文件级别指标

| 文件 | 行数 | 函数数 | 职责数 | 评价 |
|------|------|--------|--------|------|
| PetView.swift | 651 | 35 | 6+ | ⚠️ 上帝类 |
| ChatBalloonView.swift | 365 | 13 | 3 | 适中，drawNineSlice 过度 |
| TerminalView.swift | 129 | 12 | 2 | 合理 |
| ContainerView.swift | 109 | 5 | 2 | 合理 |
| WzModel.swift | 244 | 10 | 3 | 新代码，合理 |
| WzCompositor.swift | 149 | 5 | 3 | 新代码，合理 |
| WzImageLoader.swift | 106 | 8 | 3 | 新代码，合理 |
| APIClient.swift | 174 | 9 | 5 | 职责过多 |

### 2.2 PetView.swift 分解建议

当前 PetView 混入的职责：

| 职责 | 当前实现 | 建议 |
|------|---------|------|
| **Sprite 管理** | strips/images/config 加载 | 保留 |
| **动画引擎** | tick/switchTo/frameLoop | 抽取为 `AnimationEngine` |
| **Timers** | frame/hermes/idle/randomSkill | 抽取为 `AnimationController` |
| **鼠标事件** | mouseDown/rightMouseDown | 保留（AppKit 事件） |
| **Mob 管理** | mobList CRUD, add/rename/delete | 抽取为 `MobLibrary` |
| **Balloon 控制** | loadBalloonTiles/show/hide | 抽取为 `BalloonController` |
| **Hermes 感应** | senseHermes/hermesActive | 保留或移到控制器 |
| **状态栏** | statusBar 桥接 | 保持（组合而非继承） |

**建议拆分方案**：

```
PetView (200-250 行)
  ├── 帧裁剪 + CALayer 显示
  ├── mouseDown/rightMouseDown → 事件分发
  └── strips/config 持有

AnimationEngine (150 行)
  ├── tick() 帧循环
  ├── switchTo() 动画切换 + origin 保持
  ├── resolveMoveAnimation() / bestDefaultAnim()
  └── isOneShot 逻辑

AnimationController (100 行)
  ├── frameTimer/hermesTimer/idleTimer/randomSkillTimer 管理
  ├── senseHermes() 状态感应
  └── idle 回退策略

MobLibrary (100 行)
  ├── mobList 数据持有
  ├── CRUD 方法
  └── MobStore 持久化

BalloonController (80 行)
  ├── loadBalloonTiles() 缓存优先
  ├── showBalloon/hideBalloon
  └── balloonId 管理
```

### 2.3 常见代码问题

#### 2.3.1 ChatBalloonView.drawNineSlice 过于臃肿

`drawNineSlice()` 方法 120 行，包含大量重复的 tile 布局逻辑 + debug 边框绘制。

**解决方案**：
- 将每行（top/mid/bottom）的绘制和 debug 抽象为独立方法
- debug 边框统一使用循环 + 条件判断而非逐块硬编码
- 取消 `actualMidH` 未使用变量（已有编译警告）

#### 2.3.2 WzModel parseAttributes 使用正则

`parseAttributes()` 在 `WzModel.swift:220` 使用 `NSRegularExpression` 匹配 XML 属性。对于 XML 解析来说没问题，但字符串操作效率低（`String` 的 UTF-8 View 反复创建）。

**建议**：保留当前实现（XML 解析不是热点路径），后续优化可改用 `XMLParser` 或逐字符扫描。

#### 2.3.3 魔术数字和硬编码

| 位置 | 值 | 问题 |
|------|-----|------|
| PetView.swift:214 | `0.12` | 帧间隔硬编码，应为常量 |
| PetView.swift:335 | `5.0` | idle 超时硬编码 |
| PetView.swift:222 | `15...30` | 随机技能间隔硬编码 |
| PetView.swift:644 | `[5, 560, 3, 50, 100, 200, 300, 400]` | 气球 ID 列表 |
| PetView.swift:90 | `priority` 数组 | 动画优先级魔法值 |
| ChatBalloonView.swift:83 | `400` | 文本容器最大宽度 |
| Constants.swift:5 | `apiBases` | API 地址列表 |
| HermesClient.swift:14 | session ID 硬编码 | `"20260626_200327_096c1d"` |

**建议**：帧间隔/fps、idle 超时、技能间隔应在 `Constants.swift` 中定义为可配置常量，以便后续支持配置文件覆盖。

#### 2.3.4 APIClient 职责膨胀

`APIClient` 当前处理：base URL 健康检查、mob 列表、mob 名称、精灵图生成（触发 python）、气球资源 + clr、taming mob 查询。共 6 类 API 端点。

**建议**：按领域拆分或使用协议扩展组织：
- `APIClient+Mob.swift` — mob 相关
- `APIClient+Balloon.swift` — 气球相关
- `APIClient+WZ.swift` — WZ image/xml/property 相关

#### 2.3.5 错误处理模式

项目中大量使用 `try?` 静默失败：

```
guard let base = await api.resolveBase() else { return [] }
guard let url = URL(string: "\(base)/api/wz/...") else { return nil }
try? await URLSession.shared.data(for: req)  // 静默丢弃 error
```

部分错误通过 `logDebug` 记录，但使用 `cli.debugAPI` 做防护，非 debug 模式下用户完全看不到错误信息。

**建议**：
- 为 `APIClient` 添加正式的 `Result` 返回类型或 `throws`
- 关键错误（网络中断、缓存加载失败）在状态栏或气泡中提示用户
- 非 debug 模式下错误也应输出到 stderr 但 UI 不展示

### 2.4 内存管理

| 关注点 | 当前做法 | 风险 |
|--------|---------|------|
| 缓存 | `WzImageLoader` 内存缓存 `[String: Data]` 无大小限制 | 高并发加载多个怪物后可能 OOM |
| 图片 | `PetView.strips` 直接存储 `CGImage` | 正常，CGImage 在显存 |
| Timer | `Timer.scheduledTimer` 强引用 `self` | 需要 `weak self`（已使用） |
| Process | `HermesClient.pendingProcess` 单进程 | 正常 |

**建议**：`WzImageLoader` 使用 `NSCache` 替代 `Dictionary`，自动响应低内存压力。

---

## 3. UI 设计模式分析

### 3.1 当前 UI 构成

```
NSPanel (PetPanel)
  └── ContainerView
        ├── [ChatBalloonView]    9-slice 聊天气泡（可隐藏）
        ├── [PetView]            CALayer 精灵图动画
        └── [TerminalView]       PTY 终端（可隐藏，默认隐藏）
```

### 3.2 设计模式总结

#### 3.2.1 9-Slice Balloon（九宫格聊天气泡）

**当前实现**：
- 11 个 PNG 切片（nw/n/head/ne/w/c/e/sw/s/arrow/se）
- 中心列数 = `ceil(contentW / cW)`，行数 = `ceil(contentH / cH)`
- 左侧列（nw/w/sw）右对齐到 `maxLeftW`
- 右侧列（ne/e/se）左对齐到 `rightStartX`
- head 替换 N 中间格，arrow 替换 S 中间格

**评价**：
- ✅ 完全适配冒险岛 WZ 气球资源
- ✅ 无缝隙拼接算法正确
- ⚠️ 频繁调用 `loadCGImage(from:)` 每次绘图都解码 PNG — 应在 `loadTileData` 时缓存 `CGImage`

**优化建议**：
- 在 `tileData` 之外增加 `tileImages: [String: CGImage]` 缓存
- `canvasSize()` 和 `drawNineSlice()` 中的重复 `loadCGImage` 应复用
- 箭头位置计算简化：直接使用 `midCount / 2` 而非 `round(midAreaW / cW) / 2`

#### 3.2.2 Origin-Based 动画引擎

**当前实现**：
- 每个动画独立 origin（`strips[name].ox/.oy`）
- 动画切换时：`saveOrigin → resize → restoreOrigin`
- `originScreenPosition()` 将 view-level origin 映射到屏幕坐标
- Debug：原点红点 + 垂直品红线到气泡底部

**评价**：
- ✅ 正确支持多 origin 动画（stand vs fly 不同锚点）
- ✅ origin 保持策略有效（拖拽 + 动画切换不跳跃）
- ⚠️ 未考虑 macOS 多显示器情况（screen frame 计算待确认）
- ⚠️ resize() 后调用 `container.petDidResize()` 触发布局循环，但未做防抖

#### 3.2.3 气泡 + Origin 联动

**布局流程**：
1. 动画切换 → `PetView.resize()` → `ContainerView.petDidResize()` → `needsLayout`
2. `ContainerView.layout()` → 重新计算气球位置：
   `balloon.frame.origin.x = originX - arrowTipX()`
   `balloon.frame.origin.y = termH + petH - oy + 30`

**评价**：
- ✅ y 偏移 +30 是一个不错的视觉审美调整
- ✅ 动画切换后气泡自动跟随 origin
- ⚠️ `+30` 硬编码，应考虑基于怪物大小的自适应偏移

### 3.3 冒险岛 UI 设计语言

要求 Design Review 给出游戏 UI 风格的设计建议：

#### 3.3.1 配色方案

```
主调色板:
├── 深棕背景     #3D2B1F (文字区背景)
├── 米白文字     #FFF8DC (正文 / NPC 对话)
├── 暗金边框     #8B7355 (窗口边框 + 饰钉)
├── 深红强调     #8B0000 (重要提示 / 错误)
├── 翠绿文字     #00FF00 (终端 / 游戏系统消息)
├── 淡黄高亮     #FFD700 (选中 / 交互反馈)
└── 透明黑       rgba(0,0,0,0.6) (面板背景)
```

#### 3.3.2 窗口装饰

- 四角「金属饰钉」- 使用 + 15×15 装饰图片（类似 WZ UI 的 `Basic.img` 资源）
- 标题栏：冒险岛风格标题（无原生 titlebar，自定义绘制）
- 边框：暗金色 2px 线框 + 内阴影

#### 3.3.3 字体

- 主字体：`NanumBarunGothic` 或 `MaplestoryOTFBold`（需随 App 打包）
- 后备方案：系统 `Helvetica Neue` 足够接近

#### 3.3.4 动效

- 弹窗出现：从底部上弹 + 弹性缓动（`CASpringAnimation`）
- 悬浮提示：缩放出现 + 淡出
- 窗口 resize：同步 0.15s 过渡

### 3.4 当前 UI 与目标差异

| 组件 | 当前状态 | 目标风格 |
|------|---------|---------|
| TerminalView | 黑底绿字原生 NSTextView | 棕底米字 + 边框装饰 |
| PetWindow | 完全透明 | 可选地图背景（射手村等） |
| 菜单 | 原生 NSMenu | 自定义 NSView 绘制 |
| 设置面板 | 无 | 左侧导航 + 右侧内容 (F8) |
| 气泡 | 9-slice 已实现 | ✅ 符合要求 |
| 纸娃娃 | 无 | 多层合成叠加 (F5) |

---

## 4. WZ 合成管线集成设计

### 4.1 现有资源

已经实现了 3 个 WZ 前端模块，但尚未集成到主渲染管线：

| 模块 | 功能 | 集成状态 |
|------|------|---------|
| `WzModel` | 解析 XML → `WzNodeModel` 树 | ✅ 可独立工作 |
| `WzImageLoader` | 单帧 PNG 下载 + 缓存 + 帧路径发现 | ✅ 可独立工作 |
| `WzCompositor` | 合成引擎 + Avatar/Background/Ride | ✅ 可独立工作 |

### 4.2 集成路径

#### Phase 1: WzImageLoader → PetView （优先级：高）

```
当前：精灵图条 PNG（python 生成）→ PetView 直接播放
目标：WZ 单帧 + origin XML → PetView 热加载（不经过精灵图条）
```

**为什么要做**：
- 消除对 `fetch_and_generate.py` 的依赖
- 支持动态帧发现（新怪物不需要改 python 脚本）
- 支持 WZ 空洞/替换（_outlink/_inlink）

**实现步骤**：
1. `WzImageLoader` 作为 `PetView` 的后备图片源
2. `WzXmlParser.fetchNode(path:)` 获取帧 origin + 各层信息
3. `WzCompositor.composite()` 逐帧合成 → `CGImage`
4. 缓存合成的精灵图条（替代精灵图条文件）

#### Phase 2: Background → ContainerView （优先级：中）

```
当前：容器透明背景
目标：冒险岛地图作为宠物窗口背景
```

**关键点**：
- `BackgroundCompositor.fetchBackgrounds(mapId:)` → 获取所有 back 层的 `info`
- 按 front/type 分类：前景（front=1）在宠物之上，背景（front=0）在下
- `WzCompositor.composite()` 分层渲染 → `NSImage`
- 定位：宠物固定在地面中心偏下

#### Phase 3: Avatar → PetView 旁 （优先级：低）

```
目标：纸娃娃角色站立在宠物旁边
```

- 用 zmap 顺序排层
- 每层 `(ox - originX, oy - originY)` 定位
- 需独立 CALayer 或合成到背景

#### Phase 4: Chair/Ride → PetView 叠加渲染 （优先级：低）

```
目标：宠物可坐椅子或骑坐骑
```

- 椅子在下层 -> 宠物在上层
- 合成方式：`WzCompositor.composite(layers: [chairLayer, petLayer])`
- 或宠物 + 椅子分别用独立 NSView/CALayer，Z 轴叠加

### 4.3 架构决策：合成 vs 分层

**合成方式**（后端模式）：
- 每帧合成到一张 `CGImage` → `layer.contents`
- 优势：动画切换无 Z 顺序问题
- 劣势：每帧 CPU 合成开销

**分层方式**（AppKit 模式）：
- 宠物、背景、椅子各用独立 NSView 或 CALayer
- 优势：系统合成（GPU），无额外 CPU 开销
- 劣势：Z 顺序和透明处理复杂

**建议**：**混合策略**：
- 背景（静态/缓动）使用独立 CALayer 在 PetView 下方
- 宠物自身动画帧使用加法合成（无需额外层）
- 椅子/骑宠作为新 NSView 插入到 PetView 后面
- 仅在需要复杂合成（多怪物合并/纸娃娃）时才使用 `WzCompositor` 预合成

### 4.4 XML 数据依赖关系

```
WzXmlParser.fetchNode(path:)
  │
  ├── WzCompositor
  │   ├── draw(image:at:originX:originY:flipX:)
  │   └── composite(layers:bundleX:bundleY:)
  │
  ├── AvatarCompositor
  │   ├── fetchZmap()          → Base.wz/zmap.img/zmap
  │   ├── fetchActions(gender:) → Character/CODE.img/children
  │   └── framePath()          → 构建路径
  │
  ├── BackgroundCompositor
  │   └── fetchBackgrounds(mapId:) → Map/Map/MapX/MAPID.img/back
  │
  └── RideCompositor
      ├── fetchTamingMob(itemCode:)  → Item/Install/CODE.img/info/tamingMob
      └── fetchRideActions(tamingMobId:) → TamingMob/CODE.img/children
```

---

## 5. 优化建议

### 5.1 性能优化

| 项 | 当前情况 | 优化建议 | 预估效果 |
|----|---------|---------|---------|
| 帧裁剪 | 每 tick 裁剪 `CGImage.cropping(to:)` | 预裁剪所有帧到 `images[String: [CGImage]]` | 减少 98% 裁剪开销 |
| Canvas 尺寸计算 | 每次 `desiredSize()` 全量计算 | 缓存 `canvasSize` 到文本不变 | 减少 50% 布局计算 |
| PNG 解码 | `drawNineSlice` 每帧重新解码 11 个 PNG | `loadTileData` 时预缓存 `CGImage` | 消除每帧解码 |
| Timer 精度 | `Timer.scheduledTimer` 0.12s | 使用 `CVDisplayLink` 同步屏幕刷新 | 帧同步无撕裂 |
| 图片缓存 | `WzImageLoader` 用 Dictionary | 改用 `NSCache` | 自动响应内存压力 |
| 布局触发 | `resize()` 立即 `petDidResize()` | 使用 `NSAnimationContext` 批量 | 减少冗余布局 |

### 5.2 缓存策略优化

**当前**：
```
精灵图缓存: CacheManager (~/Library/Caches/MiniPet/{id}/)
  ├── pet_config.json → 动画元数据
  ├── stand.png, move.png, ... → 精灵图条
  └── (Python 生成)
```

**优化建议**：
```
新增 WZ 模式缓存:
  ├── wz_cache/{path_hash}.png → 单帧 PNG（WzImageLoader 缓存）
  ├── wz_cache/{path_hash}_composited.png → 合成后的精灵图条
  └── wz_animations/{id}_meta.json → WZ 解析出的帧列表 + origin

气球缓存:
  ├── balloons/{id}/ → 11 个切片 PNG
  └── (当前实现已正确，仅需增加 CGImage 预加载)
```

### 5.3 启动性能

| 阶段 | 当前时间 | 瓶颈 | 优化方案 |
|------|---------|------|---------|
| CLI 参数解析 | < 1ms | — | 无需优化 |
| App 启动 | ~300ms | NSApplication 初始化 | 无需优化 |
| PetView 加载 | 100ms~30s | API 响应 | 缓存优先 + 并行加载 |
| 气球加载 | 100ms~5s | 11 个串行 API 调用 | 使用 `fetchFrames(paths:)` 并行下载 |

### 5.4 架构性优化

#### 5.4.1 可配置性提升

**现在**：所有参数硬编码在 Swift 代码中。

**目标**：将以下参数提取到 `config.yaml` 或 `~/.minipet/config.json`：

```yaml
# 动画参数
animation:
  fps: 8.33          # 120ms 帧间隔
  idle_timeout: 5     # 空闲回退秒数
  skill_interval: [15, 30]  # 随机技能区间
  
# 显示参数
display:
  debug_overlay: false
  balloon_font_size: 12
  balloon_max_width: 400
  fade_duration: 0.12
  
# 行为参数
behavior:
  auto_center_on_start: true
  hermes_poll_interval: 3.0
  # ...
```

#### 5.4.2 插件化渲染管线

```
渲染管线接口:
protocol RenderLayer {
    func render(at time: CFTimeInterval) -> CGImage?
    func zOrder() -> Int
}

管线顺序:
  BackgroundLayer (z=0)     → 冒险岛地图
  ChairLayer (z=1)          → 椅子
  PetLayer (z=2)            → 宠物主体
  BalloonLayer (z=3)        → 聊天气泡
  DebugOverlayLayer (z=10)  → debug 信息
```

这不是当前阶段必须实现的方案，但可以作为长期架构规划。

---

## 6. 设计决策记录

### 决策 1: 精灵图条 vs 单帧合成

| 维度 | 精灵图条（当前） | 单帧合成（未来） |
|------|-----------------|-----------------|
| 帧切换 | O(1) CGImage 裁剪 | O(n) 合成再裁剪 |
| 内存 | 每动画 1 CGImage | 每帧 1 CGImage |
| 启动速度 | 快（缓存命中） | 慢（需逐帧合成） |
| 灵活性 | 固定帧序列 | 动态支持 WZ 空洞/UOL |
| 依赖 | Python 生成 | 纯 Swift + XML |

**结论**：精灵图条适合生产环境（快速、低内存），单帧合成适合开发/动态场景。两者可共存：缓存命中走精灵图条，API 可用时用 WZ 模式动态生成。

### 决策 2: NSView vs CALayer

| 维度 | NSView | CALayer |
|------|--------|---------|
| 事件处理 | 原生 | 需 HitTesting 扩展 |
| 动画 | CAAnimation 支持 | CAAnimation 原生 |
| 性能 | 重量级 | 轻量级 |
| 透明合成 | 需 isOpaque | 天生支持 |
| 子视图管理 | addSubview | addSublayer |

**结论**：宠物主体使用 `CALayer`（当前 PetView 是 NSView + layer），背景/装饰层也使用 CALayer。只有需要事件处理的组件（输入框、菜单）使用 NSView。

### 决策 3: 合成时机 — 帧前 vs 帧中

两种策略：

| 策略 | 时机 | 性能 | 复杂度 |
|------|------|------|--------|
| 帧前合成 | 动画切换时预合成 | 好（1次） | 需管理缓存失效 |
| 帧中合成 | 每 tick 重新合成 | 差（N次/秒） | 简单 |

**建议**：帧前合成。例如 background + pet + chair，在 `switchTo()` 时预合成所有图层，后续 tick 只切帧图（同当前精灵图条逻辑）。

### 决策 4: 异步加载策略

```
PetView.loadInitial(mobId)
  ├── 同步: Task { await loadSprites() }
  │     └── 缓存命中 → applyConfig → switchTo
  │
  ├── 后台: 并行加载气球 tiles
  │
  └── 延迟: 异步加载 hermes 状态检测
```

当前已使用 `Task` 异步模式，但错误处理只返回 `Bool`。

**改进**：返回 `Result<PetConfig, LoadingError>` 枚举：
```swift
enum LoadingError: Error {
    case noCache, apiUnavailable, configCorrupt, imageLoadFailed
}
```

### 决策 5: 调试系统的演进

当前 `cli.debugAPI` 是全局布尔值。建议演进为多级调试系统：

```swift
struct DebugConfig {
    var showOrigin: Bool = false
    var showBorders: Bool = false
    var showFPS: Bool = false
    var showAPILog: Bool = false
    var showLayout: Bool = false
    var persistToFile: Bool = false  // 输出到文件而非 stderr
}
```

通过启动参数 `--debug origin,borders,fps` 独立控制。

---

## 7. 里程碑更新建议

基于当前实现状态和 REQUIREMENTS.md 的里程碑规划：

### 当前进度

```
M1 (v0.3) — 基础闭环          完成度
├── F12 聊天气泡系统            ✅ 85%（缓存 CGImage、去除硬编码箭头）
├── F2 发言系统 + pet-chat      ✅ 20%（HermesClient 定义、BotClient 定义）
├── F4 状态感知增强              ✅ 30%（senseHermes 基础实现）
├── F9 临时默认动画              ✅ 50%（setDefaultAnim 已实现）
├── WZ 前端处理                  ✅ 定义完成（WzModel/WzImageLoader/WzCompositor）
└── BUGFIX                      - （气包布局 + origin 跟踪已修复）

M2 (v0.4) — 人格与美化          完成度
├── F3 终端美化                  ❌ 0%
├── F5 纸娃娃                    ✅ WzCompositor + AvatarCompositor 定义完成
├── F6 地图背景                  ✅ WzCompositor + BackgroundCompositor 定义完成
├── F7 椅子 + 骑宠              ✅ WzCompositor + RideCompositor 定义完成
├── F8 设置面板                  ❌ 0%
└── 体验优化                     ❌ 0%

M3 (v0.5) — 生态建设            完成度
├── F1 NPC 支持                  ❌ 0%
├── F10 R2 Lua 导出              ❌ 0%
├── F13 QQ Bot 集成              ✅ 20%（BotClient 定义）
└── F11 Windows 版               ❌ 0%
```

### 建议的新里程碑路线

基于实际开发进度和 WZ 前端处理的完成，建议调整：

```
M1 (v0.3) — 基础闭环 + WZ 管线
  ├── F12 聊天气泡 ✅ 优化剩余 15%（CGImage 缓存、调试分离）
  ├── WZ 前端管线集成 (Phase 1)     ⬅️ NEW
  ├── F9 临时默认动画
  └── PetView.swift 分解 (代码重构)

M1.5 (v0.35) — 代码质量提升
  ├── PetView 拆分为 4 个类
  ├── 调试系统升级
  ├── 可配置化（config file）
  └── Magic numbers → Constants

M2 (v0.4) — 渲染管线
  ├── WZ 合成管线集成 (Phase 2: Background)
  ├── WZ 合成管线集成 (Phase 3: Avatar) 
  ├── WZ 合成管线集成 (Phase 4: Chair/Ride)
  └── F8 设置面板

M3 (v0.5) — 人格 + 美化
  ├── F2 pet-chat 完整集成
  ├── F3 终端美化
  ├── F4 状态感知增强
  └── F13 QQ Bot 集成

M4 (v0.6) — 内容扩展
  ├── F1 NPC 支持
  ├── F10 R2 Lua 导出
  └── 更多地图/椅子默认内容
```

### 近期推荐工作（按优先级排序）

1. **🔴 P0: PetView 代码拆分** — 651 行上帝类是长期维护瓶颈
2. **🔴 P0: ChatBalloonView CGImage 缓存** — 消除每帧 PNG 解码
3. **🟡 P1: WzImageLoader 集成到 PetView** — 消除 Python 依赖的第一步
4. **🟡 P1: 调试系统升级** — 按模块控制 debug 开关
5. **🟢 P2: 魔数常量化** — 提取到 Constants.swift
6. **🟢 P2: 气球箭头位置优化** — 当前 round 逻辑可能导致箭头偏移
7. **🔵 P3: WzCompositor background 集成** — 地图背景渲染
8. **🔵 P3: 配置文件支持** — 参数可配置化

---

## 附录 A: 文件索引

| 文件 | 路径 | 行数 | 主要功能 |
|------|------|------|---------|
| 入口 | main.swift | 58 | App 启动 + 视图组装 |
| 上帝类 | PetView.swift | 651 | 精灵图引擎 + 动画 + Mob 管理 |
| 容器 | ContainerView.swift | 109 | 布局：终端 + 宠物 + 气泡 |
| 气泡 | ChatBalloonView.swift | 365 | 9-slice 聊天气泡渲染 |
| 终端 | TerminalView.swift | 129 | PTY 终端（黑底绿字） |
| 面板 | PetPanel.swift | 8 | NSPanel |
| 菜单 | StatusBarController.swift | 120 | 系统状态栏菜单 |
| API | APIClient.swift | 174 | HTTP 客户端 |
| WZ 模型 | WzModel.swift | 244 | XML 解析 + 节点模型 |
| WZ 图片 | WzImageLoader.swift | 106 | 图片下载 + 缓存 |
| WZ 合成 | WzCompositor.swift | 149 | 合成引擎 + Avatar/Background/Ride |
| 缓存 | CacheManager.swift | 51 | 文件缓存 |
| CLI | CLIArgs.swift | 84 | 命令行参数 |
| 常量 | Constants.swift | 14 | 全局常量 |
| 模型 | Models.swift | 59 | 数据模型 + MobStore |
| 工具 | Helpers.swift | 45 | 日志 + ANSI 剥除 |
| 弹窗 | InputDialog.swift | 58 | 输入弹窗 |
| Hermes | HermesClient.swift | 66 | hermes CLi 进程 |
| Bot | BotClient.swift | 61 | QQ Bot 通信 |
| 委托 | AppDelegate.swift | 33 | 退出动画 |

---

## 附录 B: 修改优先级矩阵

| 优先级 | 改动项 | 文件 | 预估时间 | 风险 |
|--------|--------|------|---------|------|
| P0 | 拆分 PetView | PetView.swift | 2-3h | 中（需做好组合设计） |
| P0 | Balloon CGImage 缓存 | ChatBalloonView.swift | 30min | 低 |
| P1 | WzImageLoader 集成 | PetView + WzImageLoader | 2h | 中（API 可用性） |
| P1 | 调试系统升级 | 多文件 | 1h | 低 |
| P2 | 魔数常量化 | 多文件 | 30min | 低 |
| P2 | 箭头位置优化 | ChatBalloonView.swift | 15min | 低 |
| P3 | 背景集成 | WzCompositor + ContainerView | 2h | 高（API 依赖） |
| P3 | 配置文件支持 | 新文件 | 1h | 低 |