# MiniPet v1.0 — 软件设计文档

> 阶段：软件设计 · v1.0 · 已修复
> 日期：2026-07-19
> 修复来源：架构评审(26条) + 需求验证(40+条)

---

## 目录

1. [整体架构](#1-整体架构)
2. [模块分层](#2-模块分层)
3. [数据模型](#3-数据模型)
4. [服务层设计](#4-服务层设计)
5. [视图层设计](#5-视图层设计)
6. [数据流设计](#6-数据流设计)
7. [数据结构层](#7-数据结构层)
8. [原型页面拆分](#8-原型页面拆分)
9. [多 Agent 开发策略](#9-多-agent-开发策略)

---

## 1. 整体架构

### 1.1 架构分层

```
┌──────────────────────────────────────────────────────────────┐
│                    应用层 (App Layer)                         │
│  Program.cs · 启动入口、平台检测、DI 容器                     │
│  EventBus.cs · 事件总线（用户操作、系统事件、资源加载完成）    │
└──────────────────────┬───────────────────────────────────────┘
                       │
┌──────────────────────┴───────────────────────────────────────┐
│                    视图层 (View Layer)                       │
│  Avalonia Window + UserControl                               │
│                                                              │
│  PetWindow · 宠物主窗口（透明无边框、置顶）                   │
│  ├── SpriteCanvas · 精灵图渲染画布                           │
│  ├── BalloonControl · 聊天气泡控件                          │
│  └── BackgroundControl · 地图背景控件                        │
│                                                              │
│  SettingsWindow · 设置面板                                   │
│  ├── MaterialTabView · 素材桌宠                             │
│  ├── PaperdollTabView · 纸娃娃桌宠                           │
│  ├── BackgroundTabView · 背景设置                           │
│  ├── BalloonTabView · 聊天气泡设置                           │
│  ├── AISettingsTab · AI 设置                                │
│  └── ChatLogTab · 对话记录                                  │
│                                                              │
│  MaterialBrowserWindow · 素材浏览弹窗（三栏）                │
│  WzConfigDialog · WZ 配置弹窗                               │
│  HermesWindow · Hermes 对话窗口                             │
│                                                              │
│  TrayIcon · 系统托盘（Avalonia TrayIcon 控件）               │
└──────────────────────┬───────────────────────────────────────┘
                       │
┌──────────────────────┴───────────────────────────────────────┐
│                    业务逻辑层 (Service Layer)                 │
│                                                              │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐       │
│  │WzService │ │SpriteSvc │ │AnimSvc   │ │BalloonSvc│       │
│  │WZ加载    │ │精灵图管理│ │动画状态机│ │气泡渲染  │       │
│  │节点查询  │ │帧缓存    │ │帧循环    │ │9-slice   │       │
│  │PNG提取   │ │内存管理  │ │切换逻辑  │ │文字排版  │       │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └────┬─────┘       │
│       │            │            │            │              │
│  ┌────┴─────┐ ┌────┴─────┐ ┌────┴─────┐ ┌────┴─────┐       │
│  │Paperdoll │ │MapSvc    │ │ConfigSvc │ │HermesSvc │       │
│  │Svc       │ │地图加载  │ │设置持久化│ │WebSocket │       │
│  │角色合成  │ │图层合成  │ │JSON读写  │ │状态感知  │       │
│  │z-order   │ │视差计算  │ │变更通知  │ │消息发送  │       │
│  └──────────┘ └──────────┘ └──────────┘ └──────────┘       │
│                                                              │
│  ┌──────────┐                                                │
│  │TraySvc   │                                                │
│  │托盘图标  │                                                │
│  │菜单管理  │                                                │
│  └──────────┘                                                │
└──────────────────────┬───────────────────────────────────────┘
                       │
┌──────────────────────┴───────────────────────────────────────┐
│                    数据层 (Data Layer)                        │
│                                                              │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐                    │
│  │Models/   │ │Cache/    │ │Config/   │                    │
│  │全部模型  │ │缓存管理  │ │设置文件  │                    │
│  └──────────┘ └──────────┘ └──────────┘                    │
└──────────────────────────────────────────────────────────────┘
```

### 1.2 模块依赖关系

```
App Layer: EventBus
              │
              ▼
View Layer:   PetWindow ──▶ SpriteCanvas → AnimService → SpriteService → WzService
              │                │                                  │
              │           BalloonControl → BalloonService ────────┤
              │                │                                  │
              │           BackgroundControl → MapService ─────────┤
              │                │
              │           ContextMenu ────────────────────────────┤
              │                                                   │
              SettingsWindow ──▶ ConfigService ─────────────────┤
              │                ▶ PaperdollService ───────────────┤
              │                ▶ WzService ──────────────────────┘
              │
              MaterialBrowserWindow ──▶ WzService
              HermesWindow ──▶ HermesService
              TrayIcon ──▶ TrayService

Data Layer:  CacheManager ← WzService(写) / SpriteService(读)
             所有 Service ← Models（依赖注入）
```

---

## 2. 各模块职责边界

| 模块 | 做什么 | 不做什么 | 依赖 |
|------|--------|---------|------|
| `EventBus` | 分发用户操作、系统事件、资源加载完成事件 | 不包含业务逻辑 | 无 |
| `WzService` | 加载 WZ、查询节点、提取 PNG、解析 origin/delay、导出精灵图条、提供 WZ 路径映射 | 不缓存精灵图、不管理帧数据、不渲染 | WzComparerR2.WzLib DLL |
| `SpriteService` | 加载精灵图条+pet_config.json、帧缓存、内存 LRU | 不解析 WZ、不控制动画 | CacheManager |
| `AnimService` | 动画状态机、帧循环、动作切换、oneShot/loop 管理、空闲超时随机动作、获取可用动作列表 | 不渲染、不加载资源 | SpriteService |
| `BalloonService` | 气泡切片加载、9-slice 渲染计算、文字排版、显示时长控制、淡入淡出 | 不控制触发时机 | WzService |
| `PaperdollService` | 纸娃娃合成、z-order 排序、装备管理、多套预设、部位→WZ 路径映射 | 不控制动画 | WzService |
| `MapService` | 地图加载、图层合成、视差计算 | 不控制地图切换 | WzService |
| `TrayService` | 创建托盘图标、显示/隐藏/居中宠物、托盘菜单管理 | 不涉及其他业务逻辑 | PetWindow |
| `HermesService` | WebSocket 连接管理、状态感知、消息收发、连接状态指示 | 不控制 UI | — |
| `ConfigService` | 设置持久化、JSON 读写、默认值、变更通知（OnSettingsChanged 事件） | 不控制业务逻辑 | — |
| `CacheManager` | 磁盘缓存读写、缓存目录管理、过期清理 | 不涉及业务逻辑 | — |

---

## 3. 数据模型

### 3.1 精灵图模型

```
Sprite
├── Width: int
├── Height: int
├── OriginX: int
├── OriginY: int
├── Z: int
└── ResourceUrl: string

Frame : Sprite
├── Delay: int
├── A0: int (0-255)
└── A1: int (0-255)

FrameAnimate
└── Frames: List<Frame>

SpriteStrip
├── Action: string
├── FrameWidth: int
├── FrameHeight: int
├── TotalWidth: int
├── FrameCount: int
├── OriginX: int
├── OriginY: int
└── Frames: List<FrameData>

FrameData
├── Index: int
├── Delay: int
├── A0: int
└── A1: int

PetConfig
├── Name: string
├── Version: int
├── Type: string (mob/npc/player)
├── Sprites: Dictionary<string, SpriteStrip>
├── OriginX: int?
└── OriginY: int?

AnimationDef
├── Name: string
├── IsLoop: bool
├── Fallback: string
└── DefaultOrder: List<string>
```

### 3.2 聊天气泡模型

```
BalloonTiles
├── NW: byte[]
├── N: byte[]
├── NE: byte[]
├── W: byte[]
├── C: byte[]
├── E: byte[]
├── SW: byte[]
├── S: byte[]
├── Arrow: byte[]
├── SE: byte[]
└── Head: byte[]

BalloonData
├── Id: int
├── Name: string
├── Tiles: BalloonTiles
├── Clr: int (ARGB 文字颜色)
└── DefaultText: string
```

### 3.3 地图模型

```
MapInfo
├── Id: int
├── Layers: MapLayer[8]
├── Backs: List<MapBack>
├── MinX: int
├── MinY: int
├── MaxX: int
└── MaxY: int

MapLayer
├── Tiles: List<MapTile>
└── Objs: List<MapObj>

MapTile
├── Id: int
├── X: int
├── Y: int
└── Resource: Sprite

MapObj
├── Id: int
├── X: int
├── Y: int
├── Z: int
├── FlipX: bool
└── Resource: FrameAnimate

MapBack
├── Id: int
├── X: int
├── Y: int
├── Cx: int (视差系数 X)
├── Cy: int (视差系数 Y)
├── Rx: int (滚动系数 X)
├── Ry: int (滚动系数 Y)
├── Alpha: int
├── FlipX: bool
├── Front: bool (前景/背景)
├── Ani: int (0=静态/1=动画/2=spine)
├── Type: int (瓦片模式 0-7)
└── Resource: Sprite | FrameAnimate
```

### 3.4 纸娃娃模型

```
CharacterAppearance
├── Gender: int (0=男, 1=女)
├── Skin: string
├── Hair: string
├── Face: string
├── Cap: string?
├── Cape: string?
├── Coat: string?
├── Longcoat: string?
├── Pants: string?
├── Shoes: string?
├── Weapon: string?
├── Shield: string?
├── Glove: string?
├── Earring: string?
├── Mount: string?
├── Chair: string?
├── DyeHue: double
└── DyeEnabled: bool

ItemInfo
├── Id: int
├── EnableEffect: bool
├── Visible: bool
├── Dye: ItemDyeInfo?
├── Hue: double
├── Saturation: double
├── Brightness: double
└── Contrast: double

MountInfo
├── Id: int
├── Actions: Dictionary<string, FrameAnimate>
├── SitAction: string
├── HideBody: bool
├── HideWeapon: bool
└── HideCape: bool

ChairInfo
├── Id: int
├── IsCash: bool
├── BodyRelMove: Vec2
├── SitAction: string
├── SitEmotion: string
├── HideBody: bool
├── HideWeapon: bool
└── HideCape: bool
```

### 3.5 应用配置模型

```
PetSettings
├── WzLibPath: string
├── BaseWzPath: string
├── CurrentMobId: string
├── CurrentEntityType: string (mob/npc/player)
├── MapId: string
├── BalloonId: int
├── DefaultAnim: string
├── Theme: string (dark/light)
├── Personality: string
├── Favorites: List<FavoriteItem>
├── RecentItems: List<RecentItem>
├── WindowX: int
├── WindowY: int
├── WindowScale: double
└── HermesConfig: HermesConfig?

FavoriteItem
├── Type: string (mob/npc/equip/hair/face/mount/chair)
├── Id: string
├── Name: string
└── AddedAt: DateTime

RecentItem
├── Type: string
├── Id: string
├── Name: string
└── LastUsedAt: DateTime

WzConfig
├── WzLibPath: string
└── BaseWzPath: string

HermesConfig
├── Enabled: bool
├── WebSocketUrl: string
├── SessionId: string
├── AutoReconnect: bool
└── ReconnectDelayMs: int

ConnectionStatus : enum (Connected, Disconnected, Reconnecting, Error)
```

---

## 4. 服务层设计

### 4.1 WzService

**职责**：WZ 文件加载、节点导航、PNG 提取、origin/delay 解析、精灵图条导出、WZ 路径映射

**核心方法**：

```
LoadWz(wzLibPath, baseWzPath) → Result<WzHandle, WzError>
FindNodeByPath(path) → WzNode?
GetFramePaths(wzPath) → List<string>
ExtractPng(wzPath) → byte[]?
GetOrigin(wzPath) → (int x, int y)
GetDelay(wzPath) → int
ExportSpriteStrip(mobId, action) → (SpriteStrip strip, PetConfig config)?
ExportBalloonTiles(balloonId) → List<Sprite>
GetActionList(mobId) → List<string>
AutoDiscoverMappings(mobId) → Dictionary<string, List<string>>
GetCharacterPartWzPath(partType, itemId, action, frame) → string
GetMapWzPath(mapId) → string
```

**WzError 枚举**：
```
WzError
├── DllNotFound (DLL 文件不存在)
├── DllLoadFailed (DLL 加载失败)
├── WzPathInvalid (BaseWZ 路径无效)
├── WzFileCorrupted (WZ 文件损坏)
├── VersionMismatch (版本不兼容)
└── Unknown
```

**状态**：WZ 加载后常驻内存。无 WZ 配置时所有方法返回 null/空列表。

### 4.2 SpriteService

**职责**：精灵图加载、帧缓存、内存管理

**核心方法**：

```
LoadSpriteStrip(mobId, action) → SpriteStrip?
GetFrame(strip, frameIndex) → Bitmap?
PreloadSprites(mobId) → Dictionary<string, SpriteStrip>
CacheStrip(mobId, action, strip) → void
GetCachedStrip(mobId, action) → SpriteStrip?
ClearCache() → void
GetLoadedMobs() → List<string>
```

**缓存策略**：LRU 内存缓存，最多同时缓存 3 个实体的精灵图。

### 4.3 AnimService

**职责**：动画状态机、帧循环、动作切换

**核心方法**：

```
Play(action, mode) → void
Stop() → void
SwitchTo(action) → void
Tick(deltaMs) → (FrameData data, Bitmap? image)
GetCurrentFrame() → (FrameData data, Bitmap? image)
GetCurrentAction() → string
GetAnimationList() → List<string>
IsLooping(action) → bool
SetFallback(action, fallback) → void
SetIdleTimeout(minMs, maxMs) → void
EnableRandomIdle(enable) → void
GetRandomAction() → string
RegisterEvent(eventType, callback) → void
OnComplete(callback) → void
```

**动画状态机**：

```
Stand ── 拖拽开始 → Fly ── 拖拽结束 → Stand
Stand ── 空闲超时(15~30s) → Say/Mouse/Laugh/Eye (随机, oneShot) ── 播完 → Stand
Stand ── Hermes 忙(P2) → Move ── Hermes 完成 → Stand
Stand ── Hermes 出错(P2) → Hit(oneShot) ── 播完 → Stand
Stand ── Hermes 完成(P2) → Skill(oneShot) ── 播完 → Stand
任意状态 ── 退出 → Die(oneShot，无则直接退出) → Exit
```

**线程模型**：
- UI 线程：所有 Avalonia 控件渲染、动画帧切换（Tick 由 `DispatcherTimer` 驱动）
- 后台线程池：WZ 加载、PNG 解码、精灵图导出、磁盘缓存 IO
- 合成线程：纸娃娃/地图的 `CompositeFrame` 在后台线程执行，完成后回调 UI 线程更新
- 原则：> 50ms 的操作必须脱离 UI 线程

**首次启动流程**：
1. 检测 WZ 配置是否存在
2. 无配置 → 显示内置默认精灵图（Assets/fallback.png）+ 弹出 WZ 配置引导弹窗
3. 用户配置 WZ 路径 → 加载 WZ → 自动发现可用怪物/NPC → 加载默认宠物
4. 下次启动有配置 → 直接恢复上次状态

**EventBus 事件类型**：
```
UserDragStartEvent / UserDragEndEvent
ResourceLoadedEvent
SettingsChangedEvent
AppExitEvent
HermesStatusChangedEvent (P2)
```

### 4.4 BalloonService

**职责**：气泡加载、9-slice 渲染、文字排版、显示时长控制

**核心方法**：

```
Show(text, balloonData, durationMs) → void
Hide() → void
LoadBalloon(balloonId) → BalloonData?
RenderBalloon(text, balloonData) → Bitmap
CalculateArrowPosition(balloonSize, originPoint) → Point
SetDisplayDuration(ms) → void
```

**生命周期**：`Show(text, data, duration)` 自动管理淡入(0.2s)→显示(duration)→淡出(0.3s)→自动`Hide(

### 4.5 PaperdollService

**职责**：纸娃娃合成、z-order 排序、装备管理

**核心方法**：

```
LoadAppearance(appearance) → bool
CompositeFrame(action, frameIndex) → Bitmap?
GetActionList() → List<string>
GetEquipList(category) → List<EquippedItem>
SetEquip(category, itemId) → void
RemoveEquip(category) → void
SavePreset(name, appearance) → void
LoadPreset(name) → CharacterAppearance?
CompositeWithMount(mountInfo, action, frame) → Bitmap?
CompositeWithChair(chairInfo, action, frame) → Bitmap?
```

**z-order 合成顺序**（参考 MapleSalon2 zmap）：
```
back(0) → hairBelowHead(1) → body(2) → head(3) → ear(4) → face(5)
→ tail(6) → capeBack(7) → arm(8) → hand(9) → shoe(10) → pants(11)
→ coat(12) → glove(13) → gloveOver(14) → shield(15) → weapon(16)
→ cape(17) → hairOverHead(18) → cap(19)
```

### 4.6 MapService

**职责**：地图加载、图层合成、视差计算

**核心方法**：

```
LoadMap(mapId) → MapInfo?
CompositeMapFrame(mapInfo, viewportRect) → Bitmap?
CalculateParallaxOffset(back, cameraPos) → Point
GetMapList() → List<(string id, string name)>
```

### 4.7 TrayService

**职责**：托盘图标、显示控制

**核心方法**：

```
CreateTrayIcon(icon) → void
ShowPet() → void
HidePet() → void
TogglePet() → void
CenterPet() → void
ShowMenu() → void
Dispose() → void
```

### 4.8 HermesService（P2 预留接口）

**核心方法**：

```
Connect(url) → bool
Disconnect() → void
SendMessage(text) → void
GetConnectionStatus() → ConnectionStatus
OnStatusChange(callback) → void
OnMessage(callback) → void
```

### 4.9 ConfigService

**职责**：设置持久化、读写、变更通知

**核心方法**：

```
LoadSettings() → PetSettings
SaveSettings(settings) → void
LoadWzConfig() → WzConfig
SaveWzConfig(config) → void
ResetToDefaults() → void
```

**事件**：
```
event OnSettingsChanged
```

### 4.10 CacheManager

**职责**：磁盘缓存管理

**核心方法**：

```
IsSpriteCached(mobId, action) → bool
SaveSpriteStrip(mobId, action, pngData, configData) → void
LoadSpriteStrip(mobId, action) → (byte[] pngData, byte[] configData)?
IsBalloonCached(balloonId) → bool
SaveBalloonTiles(balloonId, tiles) → void
LoadBalloonTiles(balloonId) → BalloonTiles?
ClearOldCache(daysRetain) → void
GetCacheDir() → string
```

---

## 5. 视图层设计

### 5.1 PetWindow

**窗口属性**：
- 透明无边框：`WindowStyle="None"` `Background="Transparent"` `TransparencyLevelHint="Transparent"`
- 始终置顶：`Topmost="True"`
- 固定尺寸：312×355

**子控件 z 轴顺序（从底到顶）**：
1. BackgroundControl（地图背景，最底层）
2. SpriteCanvas（精灵图渲染，中间层）
3. BalloonControl（聊天气泡，最顶层）

**SpriteCanvas 实现方式**：使用 `Image` 控件 + 动画帧切换 `Image.Source`（非 WriteableBitmap 自绘）。Origin 定位使用 `Canvas.SetLeft(image, -originX)` / `Canvas.SetTop(image, -originY)` 实现负偏移。帧切换直接替换 `Source` 为裁剪后的 `Bitmap`。

**BalloonControl 实现方式**：用 9 个 `Image` 控件拼贴 9-slice（NW/N/NE/W/C/E/SW/S/SE），`Arrow` 为独立 `Image` 放在 S 区域下方。`C` 区域上叠 `TextBlock` 渲染文字。用 `Opacity` 动画实现淡入淡出。

**右键菜单构建**：`ContextMenu` 通过 `Items.Add(MenuItem)` 程序化构建，菜单项的显示/禁用态根据 `PetSettings.CurrentEntityType` 和 `WzConfig` 是否已配置动态调整。每个菜单项的 Click 事件触发 EventBus 发布相应事件。

**右键菜单结构**（含禁用态逻辑）：
```
切换人物
  ├── NPC → [NPC 列表]        (WZ 未加载 → 灰显)
  ├── MOB → [MOB 列表]        (WZ 未加载 → 灰显)
  └── PLAYER → [预设列表]     (WZ 未加载 → 灰显)
切换动画 → [可用动作列表]      (WZ 未加载 → 灰显)
切换背景 → [地图列表]          (WZ 未加载 → 灰显)
切换气泡 → [气泡样式列表]      (WZ 未加载 → 灰显)
切换椅子/骑宠 → [...]          (非 PLAYER 或 WZ 未加载 → 灰显)
──────────
Hermes 终端                    (未配置 Hermes → 灰显)
设置中心
──────────
退出
```

**拖拽行为**：
- 拖拽开始 → fly 循环
- 拖拽中 → 跟随鼠标
- 释放 → 立即切 stand，无过渡
- 边界限制：距屏幕边缘 >= 50px，优先在视口内

### 5.2 SettingsWindow

**三主 Tab**：
```
SettingsWindow
├── 📦 桌宠设置
│   ├── [素材桌宠]  [纸娃娃桌宠]  [背景]  [聊天气泡]
│   └── WZ 配置按钮（顶部工具栏）
├── 🔗 AI 设置
│   └── Hermes 连接状态 + 反应规则表格
└── 💬 对话记录
    └── 左栏 session 列表 + 右栏消息详情
```

**WzConfigDialog**（从设置面板触发）：
```
模态弹窗 500×280px
├── wzlib 路径: [_______________] [浏览]
├── BaseWZ 路径: [_______________] [浏览]
├── 提示: "配置后即可浏览和选择冒险岛素材"
└── [保存] [取消]
```

### 5.3 MaterialBrowserWindow

**三栏布局** 860×600px：
```
顶部: 搜索栏 + 类型筛选 chips
左侧(200px): 分类树（怪物/NPC/装备/发型/脸型/坐骑/椅子）
中间(flex:1): 素材列表 + 翻页
右侧(240px): 详情预览 + [选择此素材] + [取消]
底部: 状态栏 "共 N 个 · 第 X/Y 页"
```

### 5.4 HermesWindow

**对话窗口** 400×500px：
```
顶部: 标题"Hermes 对话" + 连接状态指示灯
中部: 消息列表（用户灰/AI蓝，头像+时间戳）
底部: 输入框 + 发送按钮
```

### 5.5 TrayIcon

**系统托盘**
- macOS：NSStatusBar 菜单栏图标
- Windows：NotifyIcon 托盘图标
- 菜单项：回归屏幕中心 | 显示/隐藏 | 设置中心 | 退出

### 5.6 原型文件对照

| 原型 | 视图 | 状态 |
|------|------|------|
| `prototypes/index.html` | SettingsWindow | ✅ |
| `prototypes/pet-window.html` | PetWindow + ContextMenu | ✅ |
| `prototypes/wz-config.html` | WzConfigDialog | ✅ |
| `prototypes/hermes-chat.html` | HermesWindow | ✅ |
| `prototypes/material-browser.html` | MaterialBrowserWindow | ✅ |
| 系统托盘 | TrayIcon（原生控件） | 无原型文件 |

---

## 6. 数据流设计

### 6.1 主数据流

```
EventBus (App Layer)
    ├── 用户拖拽 → AnimService.SwitchTo(fly)
    ├── 空闲超时 → AnimService → 随机动作 → SpriteCanvas 刷新
    ├── 资源加载完成 → AnimService → stand → SpriteCanvas 刷新
    ├── 设置变更 → ConfigService → OnSettingsChanged → 各 Service 刷新
    └── 退出 → AnimService → die → Exit
```

### 6.2 资源加载流

```
WzService.LoadWz()
    ↓
WzService.ExportSpriteStrip(mobId, action)
    ↓ 产出: 精灵图PNG + pet_config.json
CacheManager.SaveSpriteStrip()  ← 写入磁盘
    ↓
SpriteService.LoadSpriteStrip() ← 从磁盘或缓存读取
    ↓ 内存缓存
AnimService.Play(action)
    ↓ 每帧 Tick → FrameData
SpriteCanvas.Render(frame)
```

### 6.3 纸娃娃合成流

```
PaperdollService.LoadAppearance()
    ↓
WzService.GetCharacterPartWzPath() ← WZ 路径映射
    ↓
WzService.ExtractPng() ← 提取各部位 PNG
    ↓
PaperdollService.CompositeFrame()
    ↓ z-order 排序 + origin 偏移
Bitmap 合成输出 → SpriteCanvas 渲染
```

### 6.4 地图渲染流

```
MapService.LoadMap(mapId)
    ↓
WzService: 解析 Map.wz → MapInfo
    ↓
MapService.CompositeMapFrame(mapInfo, viewport)
    ↓ 背景层(视差) → 地砖层 → 物件层(z-order)
Bitmap → BackgroundControl 渲染
```

### 6.5 错误处理流

```
WZ 加载失败 → 弹窗提示 → 禁用所有 WZ 相关功能 → 菜单项灰显
DLL 找不到 → 弹窗提示路径无效 → 保持禁用
资源缺失 → 单动作缺失 → 用 stand fallback → 无感知
          → 全部缺失 → 显示占位图 + 日志记录
网络断开 → Hermes 自动重连(指数退避) → 托盘图标红点
```

---

## 7. 数据结构层

### 7.1 缓存结构

```
~/Library/Caches/MiniPet/v1/ (macOS)
%LOCALAPPDATA%/MiniPet/cache/v1/ (Windows)
├── sprites/{mobId}/
│   ├── stand.png
│   ├── move.png
│   └── config.json
├── balloons/{balloonId}/
│   ├── nw.png, n.png, ne.png, ...
│   └── config.json
└── maps/{mapId}/
    └── map.json
```

### 7.2 配置文件

```
~/.minipet/ (macOS)
%APPDATA%/MiniPet/ (Windows)
├── settings.json    ← PetSettings
└── wz-config.json   ← WzConfig
```

---

## 8. 原型页面拆分

| 原型 | 用途 | 对应 View |
|------|------|----------|
| `index.html` | 设置面板 + 4 子 Tab | SettingsWindow |
| `pet-window.html` | 宠物主窗口 + 右键菜单 | PetWindow + ContextMenu |
| `wz-config.html` | WZ 配置弹窗 | WzConfigDialog |
| `hermes-chat.html` | Hermes 对话 | HermesWindow |
| `material-browser.html` | 素材浏览/搜索 | MaterialBrowserWindow |

---

## 9. 多 Agent 开发策略

### 9.1 开发顺序

```
M1 (4周) P0:
  W1: Program.cs + EventBus + WzService + ConfigService + CacheManager + Models
  W2: SpriteService + AnimService + SpriteCanvas + PetWindow + 右键菜单
  W3: BalloonService + BalloonControl + 气泡显示
  W4: SettingsWindow + WzConfigDialog + 集成测试

M2 (3周) P1:
  W1: PaperdollService + 纸娃娃合成 + PaperdollTabView
  W2: 装备管理 UI + 预设管理
  W3: MaterialBrowserWindow + 收藏/最近使用

M3 (2周) P2:
  W1: MapService + BackgroundControl + BackgroundTabView
  W2: TrayService + TrayIcon + 修复 + 体验优化

M4 (2周) P2:
  W1: HermesService + WebSocket
  W2: HermesWindow + 状态感知集成
```

### 9.2 并行策略

每阶段 ≤3 个 agent，每个 agent 独立文件：

```
Agent A: 一个 Service + Model
Agent B: 另一个 Service + Model  
Agent C: 对应的 View (axaml + axaml.cs)
```

### 9.3 文件结构

```
MiniPet/
├── MiniPet.csproj
├── Program.cs
├── App.axaml / App.axaml.cs
├── EventBus.cs
│
├── Models/
│   ├── Sprite.cs, Frame.cs, FrameAnimate.cs
│   ├── SpriteStrip.cs, FrameData.cs, PetConfig.cs, AnimationDef.cs
│   ├── BalloonTiles.cs, BalloonData.cs
│   ├── MapInfo.cs, MapLayer.cs, MapTile.cs, MapObj.cs, MapBack.cs
│   ├── CharacterAppearance.cs, ItemInfo.cs, MountInfo.cs, ChairInfo.cs
│   ├── PetSettings.cs, WzConfig.cs, FavoriteItem.cs, RecentItem.cs
│   └── HermesConfig.cs, ConnectionStatus.cs
│
├── Services/
│   ├── WzService.cs, SpriteService.cs, AnimService.cs
│   ├── BalloonService.cs, PaperdollService.cs, MapService.cs
│   ├── TrayService.cs, HermesService.cs, ConfigService.cs
│   └── CacheManager.cs
│
├── Views/
│   ├── PetWindow.axaml / .axaml.cs
│   ├── SettingsWindow.axaml / .axaml.cs
│   ├── WzConfigDialog.axaml / .axaml.cs
│   ├── HermesWindow.axaml / .axaml.cs
│   ├── MaterialBrowserWindow.axaml / .axaml.cs
│   │
│   ├── Controls/
│   │   ├── SpriteCanvas.axaml / .axaml.cs
│   │   ├── BalloonControl.axaml / .axaml.cs
│   │   └── BackgroundControl.axaml / .axaml.cs
│   │
│   └── Tabs/
│       ├── MaterialTabView.axaml / .axaml.cs
│       ├── PaperdollTabView.axaml / .axaml.cs
│       ├── BackgroundTabView.axaml / .axaml.cs
│       ├── BalloonTabView.axaml / .axaml.cs
│       ├── AISettingsTab.axaml / .axaml.cs
│       └── ChatLogTab.axaml / .axaml.cs
│
└── Assets/
    └── icon.png
```

---

## 10. 增量设计 v1.1（2026-08-08）— WZ 加载失败提示 + 刷新素材 + 默认 wzlib 兜底

> 对应需求：F12（启动 WZ 加载失败弹窗）+ F13（WZ 设置弹窗刷新按钮）+ F14（默认 wzlib 兜底 + 下载引导）
> 需求来源：docs/requirements-analysis.md 第 7 章（2026-08-08 逐条确认）

### 10.1 设计总览

| 功能 | 改动位置 | 关键点 |
|------|---------|--------|
| F12+F14 启动失败弹窗 | `App.axaml.cs` 失败分支 + 新增 `WzLoadFailWindow` | 独立小窗口，一次启动只弹一次，含下载引导 |
| F14 默认 wzlib 兜底 | `WzService.LoadWz` 开头 wzlib 校验 | 用户配置无效 → 回退内置 + 警告字段 |
| F13 刷新按钮 | `SettingsWindow.BuildWzOverlay` + `SaveWzConfig` 重构 | 按输入框当前路径重载，成功通知 PetWindow |

**核心设计决策（已与胶水确认）**：
1. **wzlib 运行时语义**：WzLib DLL 是编译期引用（内置随应用发布），**运行时不做动态加载**。「用户配置优先」= 启动时校验用户配置的 wzlib 路径下存在 `WzComparerR2.WzLib.dll`；存在 → 视为用户版本（通过校验），不存在 → 回退内置并提示。
2. **F12 与 F14 弹窗合并**：同一场景（启动 LoadWz 失败）只弹一次，文案含错误原因 + 下载引导。
3. **弹窗形态**：独立小窗口（居中、可点「知道了」关闭），不用悬浮气泡。
4. **一次打开只读一次**：启动自动加载仅一次、失败不自动重试；F13 刷新按钮是用户手动触发例外。

### 10.2 WzService 改动

```csharp
// 新增字段
public string? LastWzLibWarning { get; private set; }   // 用户 wzlib 配置无效时设置

// LoadWz 开头（_loading 守卫之后、baseWzPath 校验之前）新增 wzlib 校验：
// 1. wzLibPath 为空 → LastWzLibWarning = null（用内置，无警告）
// 2. wzLibPath 非空：
//    - 若指向文件 → File.Exists 校验
//    - 若指向目录 → 检查 {dir}/Lib/WzComparerR2.WzLib.dll 与 {dir}/WzComparerR2.WzLib.dll
//    - 校验通过 → LastWzLibWarning = null
//    - 校验失败 → LastWzLibWarning = "用户配置的 wzlib 无效，已回退默认"; 继续加载（不阻断）
// ⚠️ 注意：wzLibPath 参数本身运行时仍不用于 DLL 加载（编译期引用），仅做校验与提示
```

**WzError → 中文文案映射**（新增静态方法，放 WzService 或 WzError.cs）：

| WzError | 中文文案 |
|---------|---------|
| DllNotFound | wzlib DLL 文件不存在 |
| DllLoadFailed | wzlib DLL 加载失败 |
| WzPathInvalid | BaseWZ 路径无效 |
| WzFileCorrupted | WZ 文件损坏 |
| VersionMismatch | WZ 版本不兼容 |
| Unknown | 未知错误 |

### 10.3 新增 WzLoadFailWindow（F12+F14 弹窗）

- **文件**：`MiniPet/Views/WzLoadFailWindow.cs`（纯代码窗口，无 .axaml——参考 PetWindow 纯代码模式）
- **形态**：`Window`，`Width≈460`，居中显示（`WindowStartupLocation.CenterScreen`），普通窗口（非 Topmost）
- **内容**：
  1. 标题：「WZ 素材读取失败」
  2. 错误原因：WzError 中文文案
  3. wzlib 警告：`LastWzLibWarning` 非空时追加一行（黄色提示）
  4. 下载引导：文案「可前往 GitHub 下载 WzComparerR2（含 WzComparerR2.WzLib.dll）」，下方显示可点击链接 `https://github.com/Kagamia/WzComparerR2/releases`（点击用 `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })` 打开默认浏览器；macOS/Windows 均可用）
  5. 按钮：「知道了」→ Close()
- **弹出时机**：`App.axaml.cs` 启动 LoadWz 失败分支（`ok == false` 或 catch 异常）→ `new WzLoadFailWindow(errText, warning).Show()`。**在 UI 线程**（App.OnFrameworkInitializationCompleted 主线程内直接 Show 即可，窗口自然出现在桌面）

### 10.4 SettingsWindow 改动（F13 刷新按钮）

**BuildWzOverlay 按钮行**（现 L643-650）：
```
[刷新当前素材]  [取消]  [保存]
```
- 「刷新当前素材」放最左（主操作靠前，符合胶水按钮顺序偏好）
- **点击行为**（`RefreshWzMaterial()` 新方法）：
  1. 立即禁用按钮 + 文案变「刷新中…」
  2. 读取**当前输入框**的 `_wzLibInput.Text` / `_baseWzInput.Text`（未保存也生效）
  3. `Task.Run(() => _wzService.LoadWz(libPath, basePath))` 异步执行（WZ 目录加载耗时，避免 UI 冻结）
  4. 完成后 `Dispatcher.UIThread.Post` 回主线程：
     - 成功 → 弹窗内提示「素材刷新完成：Mob X 个 / Map Y 个」；`LoadPetListFromWz()` 刷新列表；**触发 `OnWzRefreshed` 事件**通知 PetWindow 重载当前宠物
     - 失败 → 弹窗内提示 WzError 中文文案 + wzlib 警告（不关闭弹窗，用户可改路径重试）
  5. 恢复按钮可用
- **新增事件**：`public event Action? OnWzRefreshed;`

**SaveWzConfig 复用刷新逻辑**：保存后调用同样的刷新路径（保存 + 刷新分离，刷新按钮不隐式保存配置——刷新只改内存加载状态；保存只持久化。但保存后自动触发一次刷新，保持现行为）。

**注意**：`LoadWz` 有 `_loadLock` 单飞守卫，但刷新按钮自身也要防连点（Task.Run 并发），按钮禁用即可；刷新期间 `SaveWzConfig` 同时点 → `_loadLock` 已兜底。

### 10.5 PetWindow 改动（刷新成功后重载素材）

- 现有 `OpenSettings()` 创建 `SettingsWindow` 处（L646-648）新增订阅：
  ```csharp
  _settingsWindow.OnWzRefreshed += () =>
  {
      try { LoadDefaultPet(_wzService); }   // 重载当前宠物（从新 WZ）
      catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnWzRefreshed: {ex.Message}"); }
  };
  ```
- `LoadDefaultPet` 已存在（L266），签名 `LoadDefaultPet(WzService? wzService)`，传 `_wzService` 即从 WZ 重载（WZ 未加载时自动走缓存，现有逻辑已处理）
- 地图素材刷新：`MapService`/`MapCatalogService` 由 LoadWz 内部 `EnsureMapCatalog()` 重建，背景 tab 下次打开自动用新目录，无需额外处理

### 10.6 App.axaml.cs 改动（F12 弹窗接入）

启动分支 L52-61 失败路径改造：

```csharp
else
{
    Console.Error.WriteLine($"[App] WZ 自动加载失败: {err}");
    // 弹窗提示（一次启动只弹一次）
    try
    {
        var failWindow = new WzLoadFailWindow(
            WzService.GetErrorText(err ?? WzError.Unknown),
            wzService.LastWzLibWarning);
        failWindow.Show();
    }
    catch (Exception ex) { Console.Error.WriteLine($"[App] 失败弹窗: {ex.Message}"); }
    // 缓存加载默认宠物（现有逻辑保留）
    if (petWindow != null) petWindow.LoadDefaultPet(null);
}
```

catch 异常分支同理弹窗（err 文案「未知错误」+ 异常消息）。

### 10.7 改动文件清单

| 文件 | 改动 |
|------|------|
| `MiniPet/Services/WzService.cs` | LoadWz 开头 wzlib 校验 + LastWzLibWarning + GetErrorText 静态映射 |
| `MiniPet/Services/WzError.cs` | （可选）加 GetErrorText 映射放这里更内聚 |
| `MiniPet/Views/WzLoadFailWindow.cs` | **新增**：失败弹窗窗口 |
| `MiniPet/Views/SettingsWindow.axaml.cs` | BuildWzOverlay 加刷新按钮 + RefreshWzMaterial + OnWzRefreshed 事件 + SaveWzConfig 复用 |
| `MiniPet/Views/PetWindow.axaml.cs` | OpenSettings 处订阅 OnWzRefreshed → LoadDefaultPet |
| `MiniPet/App.axaml.cs` | 失败/异常分支弹窗 |

### 10.8 验证方案

1. 编译：`dotnet build --no-restore` 0 错误
2. 单元测试：109/109 通过
3. 黑盒（正常路径）：配置正确 WZ → 启动无弹窗、素材可用
4. 黑盒（失败路径）：改 wz-config.json 的 BaseWzPath 为无效路径 → 启动弹 WzLoadFailWindow，点「知道了」关闭，桌宠走缓存正常显示
5. 黑盒（刷新按钮）：WZ 路径无效启动 → 打开设置 → WZ 配置 → 修正路径 → 点「刷新当前素材」→ 提示成功 + 宠物列表出现 + 桌宠重载
6. 黑盒（wzlib 兜底）：wz-config.json 配无效 wzlib 路径 + 有效 BaseWzPath → 启动仍成功加载（回退内置），失败弹窗才显示警告
