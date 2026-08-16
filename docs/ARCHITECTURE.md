# MiniPet 架构设计文档 v0.4

> 生成日期：2026-07-04
> 参考项目：MapleSalon2（Rust + React + PixiJS），mxd.dvg.cn/zhiwawa_v4（SolidJS + PixiJS）
> 当前状态：20 Swift 文件，~2700 行，编译通过

---

## 目录

1. [项目定位](#1-项目定位)
2. [架构总览](#2-架构总览)
3. [模块分层](#3-模块分层)
4. [纸娃娃系统](#4-纸娃娃系统)
5. [骑宠坐骑系统](#5-骑宠坐骑系统)
6. [聊天戒子（气泡）系统](#6-聊天戒子气泡系统)
7. [名片系统](#7-名片系统)
8. [特效系统](#8-特效系统)
9. [地图背景系统](#9-地图背景系统)
10. [NPC / MOB 系统](#10-npc--mob-系统)
11. [合成管线和渲染引擎](#11-合成管线和渲染引擎)
12. [数据流架构](#12-数据流架构)
13. [未来架构路线图](#13-未来架构路线图)

---

## 1. 项目定位

### 1.1 桌宠模型三大分类

```
MiniPet 桌宠系统
│
├── 📋 纸娃娃 (Character Compositing)
│   ├── 玩家自身 (Player Avatar)
│   │   ├── 身体主体 (Body Template: 00002000/00012000)
│   │   ├── 发型 (Hair)
│   │   ├── 脸型 (Face)
│   │   ├── 装备 (Equip: Cap/Cape/Coat/Pants/Shoes/Weapon/Shield/Glove)
│   │   ├── 饰品 (Accessory: Ring/Pendant/Belt/Earring/Shoulder)
│   │   └── 染色 (Colorvar / MixDye HSV)
│   └── 骑宠坐骑 (Mount / TamingMob)
│       ├── 坐骑 (地上/空中)
│       └── 椅子 (Chair)
│
├── 📋 游戏内资源 (In-Game Resources)
│   ├── NPC (非玩家角色)
│   └── MOB (怪物)
│
└── 📋 通用 (General-Purpose Components)
    ├── 聊天戒子 / 气泡 (ChatBalloon)
    ├── 名片 (NameTag / NickTag / Medal)
    ├── 特效 (Skill Effect / Item Effect)
    └── 地图 (Map Background)
```

### 1.2 架构原则

| 原则 | 说明 |
|------|------|
| **前端为主** | 仅从后端获取 WZ XML 元数据 + 单帧 PNG 图片，全部合成渲染在 Swift 端完成 |
| **零 Python 依赖** | 逐步淘汰 `fetch_and_generate.py`，改用纯 Swift 的 WZ 图片加载 + 合成管线 |
| **按模块拆分** | 每个模块独立类，通过协议组合而非继承 |
| **MapleSalon2 对齐** | 数据模型、WZ 路径、合成算法尽量与 MapleSalon2 Rust 版本保持一致 |

---

## 2. 架构总览

### 2.1 当前架构（v0.3）

```
┌─────────────────────────────────────────────────────────┐
│ PetPanel (NSPanel)                                      │
│  ┌──────────────────────────────────────────────────┐   │
│  │ ContainerView                                     │   │
│  │  ┌──────────────────────┐  ┌──────────────────┐  │   │
│  │  │ PetView (651行)      │  │ ChatBalloonView  │  │   │
│  │  │ · 精灵图引擎         │  │ · 9-slice 气泡   │  │   │
│  │  │ · 动画控制器         │  │ · 11 PNG 切片    │  │   │
│  │  │ · Mob 管理           │  │ · 自动消失       │  │   │
│  │  │ · 鼠标事件           │  └──────────────────┘  │   │
│  │  │ · Hermes 感应        │  ┌──────────────────┐  │   │
│  │  │ · 气球控制           │  │ TerminalView     │  │   │
│  │  └──────────────────────┘  │ · PTY 终端       │  │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

### 2.2 目标架构（v0.5+）

```
┌──────────────────────────────────────────────────────────────────┐
│ PetPanel (NSPanel)                                               │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ SceneView (场景容器)                                     │   │
│  │  ┌────────────────────┐  ┌───────────────────────────┐  │   │
│  │  │ BackgroundLayer    │  │ CharacterView (重写 PetView) │  │   │
│  │  │ · 地图瓦片背景     │  │ ┌───────────────────────┐  │  │   │
│  │  │ · 视差滚动效果     │  │ │ AvatarLayer          │  │  │   │
│  │  │ · 前景/背景分离    │  │ │ · 纸娃娃合成渲染     │  │  │   │
│  │  └────────────────────┘  │ │ · 动画引擎           │  │  │   │
│  │  ┌────────────────────┐  │ │ · 装备管理           │  │  │   │
│  │  │ EffectLayer        │  │ └───────────────────────┘  │  │   │
│  │  │ · 技能特效         │  │ ┌───────────────────────┐  │  │   │
│  │  │ · 道具特效         │  │ │ MountLayer           │  │  │   │
│  │  │ · 粒子系统         │  │ │ · 坐骑/椅子渲染      │  │  │   │
│  │  └────────────────────┘  │ └───────────────────────┘  │  │   │
│  │  ┌────────────────────┐  │ ┌───────────────────────┐  │  │   │
│  │  │ BalloonLayer       │  │ │ NameTagLayer         │  │  │   │
│  │  │ · 聊天气泡         │  │ │ · 名片/NickTag/勋章  │  │  │   │
│  │  └────────────────────┘  │ └───────────────────────┘  │  │   │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. 模块分层

### 3.1 现有模块

| 模块 | 文件 | 状态 | 说明 |
|------|------|------|------|
| WZ 节点模型 | `WzModel.swift` | ✅ | WZ XML 解析器 + WzNodeModel 树 |
| WZ 图片加载 | `WzImageLoader.swift` | ✅ | 单帧 PNG 下载 + 内存缓存 + 帧路径发现 |
| 合成引擎 | `WzCompositor.swift` | ✅ | Origin-based 合成 + z-order 排序 |
| 纸娃娃模型 | `WzCharacterModel.swift` | ✅ 新增 | EquipCategory / BodySlot / CharacterAppearance 等 |
| 纸娃娃合成 | `WzCharacterCompositor.swift` | ✅ 新增 | 完整角色合成管线 |
| API 扩展 | `APIClient+WZ.swift` | ✅ 新增 | equip/chair/mount/skill/map 查询接口 |
| 聊天气泡 | `ChatBalloonView.swift` | ✅ | 9-slice 渲染 |
| 怪物播放 | `PetView.swift` | ⚠️ 待拆分 | 上帝类，需按模块拆分 |

### 3.2 待创建模块

| 模块 | 建议文件名 | 优先级 | 依赖 |
|------|-----------|--------|------|
| 坐骑合成 | `WzMountCompositor.swift` | P1 | WzCharacterModel, WzImageLoader |
| 椅子合成 | `WzChairCompositor.swift` | P1 | WzCharacterModel, WzImageLoader |
| 地图渲染 | `WzMapRenderer.swift` | P2 | WzCompositor, WzImageLoader |
| 特效系统 | `WzEffectRenderer.swift` | P2 | WzCompositor, WzModel |
| 名人/名片 | `WzNameTagView.swift` | P2 | WzModel, WzImageLoader |
| 设置面板 | `SettingsPanel.swift` | P2 | — |
| 动画引擎 | `AnimationEngine.swift` | P1 | 从 PetView 抽取 |
| Mob 管理 | `MobLibrary.swift` | P2 | 从 PetView 抽取 |

---

## 4. 纸娃娃系统

### 4.1 参考来源

- **MapleSalon2** `renderer/character/character.ts`（1037 行）— 核心渲染类
- **MapleSalon2** `renderer/character/characterBodyFrame.ts`（268 行）— 单帧合成
- **MapleSalon2** `renderer/character/item.ts`（557 行）— 装备加载
- **MapleSalon2** `renderer/character/itemPiece.ts`（196 行）— 锚点系统
- **MapleSalon2** `renderer/character/loader.ts`（245 行）— WZ 加载器
- **MapleSalon2** `handlers/string.rs`（468 行）— EquipCategory 枚举
- **MapleSalon2** `handlers/zmap.rs`（23 行）— 渲染顺序
- 纸娃娃网页 `mxd.dvg.cn/zhiwawa_v4/` — PixiJS v8 Canvas 逐部件合成

### 4.2 数据模型

```
EquipCategory (32 种，匹配 MapleSalon2)
├── Cap / Cape / Coat / Dragon / Mechanic
├── Face / Glove / Hair / Longcoat / Pants
├── PetEquip / Ring / Shield / Shoes / Taming / Weapon
├── Android / Accessory / Bit / ArcaneForce / AuthenticForce
├── Skin / SkillSkin
├── ringEffect / necklessEffect / beltEffect / medal
├── nickTag / nameTag / chatBalloon / effect
└── unknown

BodySlot (16 种，z-order 排序)
├── back(0) → hairBelowHead(1) → body(2) → head(3) → ear(4) → face(5)
├── tail(6) → capeBack(7) → arm(8) → hand(9) → shoe(10) → pants(11)
├── coat(12) → glove(13) → gloveOver(14) → shield(15)
└── weapon(16) → cape(17) → hairOverHead(18) → cap(19)

CharacterAppearance (角色外观状态)
├── gender / skin / hair / face（基础身体）
├── cap / cape / coat / longcoat / pants / shoes（装备）
├── weapon / shield / glove（武器防具）
├── ring[] / pendant[] / belt / medal / earring（饰品）
├── mount / chair（坐骑椅子）
├── nameTag / nickTag / chatBalloon（UI 组件）
└── effect（特效）
```

### 4.3 合成流程

```
CharacterAppearance
  │
  ├── 1. loadBodyTemplate(gender)         → WzImageLoader 加载 body PNG
  ├── 2. loadHair(hairId)                 → WzImageLoader 加载 hairOverHead + hairBelowHead
  ├── 3. loadFace(faceId)                 → WzImageLoader 加载 face
  ├── 4. loadEquip(capId, .cap)           → WzImageLoader 加载 cap
  ├── 5. loadEquip(coatId, .coat)         → WzImageLoader 加载 coat
  ├── 6. loadEquip(pantsId, .pants)       → WzImageLoader 加载 pants
  ├── 7. loadEquip(shoesId, .shoes)       → WzImageLoader 加载 shoes
  ├── 8. loadEquip(weaponId, .weapon)     → WzImageLoader 加载 weapon
  ├── 9. loadEquip(shieldId, .shield)     → WzImageLoader 加载 shield
  ├── 10. loadEquip(gloveId, .glove)      → WzImageLoader 加载 glove
  ├── 11. loadAccessory(earringId)        → WzImageLoader 加载 ear
  │
  ├── 12. fetchOrigin(".../path/origin")  → WzXmlParser 解析 vector 坐标
  │
  └── 13. WzCompositor.composite()        → 按 z-order 排序 + origin 偏移合成
```

### 4.4 锚点系统（MapleSalon2 方式）

MapleSalon2 使用**锚点链**（anchor chain）进行身体定位：

```
navel (0,0)  ← 根锚点
  │
  ├── 每个身体部位声明 map: { navel: {x,y}, neck: {x,y}, brow: {x,y} }
  ├── buildAncher() 按链解析:
  │     1. 找 map 中第一个已存在的锚点作为父锚点
  │     2. piece.ancher = parentAnchor - piece.map[parentKey]
  │     3. 注册新锚点: newAnchor = piece.ancher + piece.map[newKey]
  └── 最终: bodyFrame.pivot = neck（角色世界定位锚点）
```

**MiniPet 简化方案**：由于我们只使用单帧 PNG（而非多部件分层），直接用 WZ origin 偏移定位即可。锚点系统仅在需要精确的椅子/坐骑对准时使用。

### 4.5 WZ 路径规则（匹配 MapleSalon2）

```
角色模板:     Character/{00002000|00012000}.img/{action}/{frame}/{slot}
发型:        Character/Hair/{8位ID}.img/{action}/{frame}/hairOverHead
脸型:        Character/Face/{8位ID}.img/{action}/{frame}/face
帽子:        Character/Cap/{8位ID}.img/{action}/{frame}/cap
披风:        Character/Cape/{8位ID}.img/{action}/{frame}/{cape|capeBack}
上衣:        Character/Coat/{8位ID}.img/{action}/{frame}/coat
裤子:        Character/Pants/{8位ID}.img/{action}/{frame}/pants
鞋子:        Character/Shoes/{8位ID}.img/{action}/{frame}/shoe
武器:        Character/Weapon/{8位ID}.img/{action}/{frame}/weapon
盾牌:        Character/Shield/{8位ID}.img/{action}/{frame}/shield
手套:        Character/Glove/{8位ID}.img/{action}/{frame}/{glove|gloveOver}
耳环:        Character/Accessory/{8位ID}.img/{action}/{frame}/ear
```

---

## 5. 骑宠坐骑系统

### 5.1 参考来源

- **MapleSalon2** `renderer/tamingMob/tamingMob.ts`（316 行）— 坐骑渲染
- **MapleSalon2** `renderer/chair/chair.ts`（564 行）— 椅子渲染
- **MapleSalon2** `handlers/mount.rs`（105 行）— 坐骑字符串
- **MapleSalon2** `handlers/chair.rs`（104 行）— 椅子字符串
- **MapleSalon2** `handlers/mount_skill_id.rs`（791 行）— 坐骑→技能 ID 映射

### 5.2 坐骑系统

```
WZ 路径: Character/TamingMob/{ID}.img/{action}/{frame}

数据加载:
├── fetchMountString()          → String/Eqp.img/Eqp/Taming 获取名称
├── fetchMountActions(id)       → 查询 TamingMob/id.img 下的动作列表
├── fetchMountFrame(id,act,frm) → 下载单帧 PNG
└── compositeWithMount()        → 角色骑在坐骑上合成

动作类型:
├── Stand1, Stand2, Sit, Move, Fly
├── 坐骑有独立动画循环，角色跟随
└── 坐骑的 navel 锚点 = 角色位置

多骑手支持:
├── ExtraAvatarCount 属性
└── 多个角色位置偏移
```

### 5.3 椅子系统

```
WZ 路径: Item/Install/{8位ID}.img 或 Item/Cash/0520.img

数据加载:
├── 分类: 普通椅(0301xxx/0302xxx) + 现金椅(05204xx)
├── fetchChairInfo(id)
│   ├── bodyRelMove: 角色坐标修正
│   ├── sitAction: 强制坐姿动作
│   ├── sitEmotion: 强制表情
│   ├── hideBody: 是否隐藏身体
│   ├── invisibleWeapon/Cape: 是否隐藏武器/披风
│   └── tamingMobId: 如果椅子是坐骑（如龙）
└── compositeWithChair()
    ├── 椅子层在角色下面
    ├── 角色被定位到椅子的特定位置
    ├── 椅子有 random effect 变体系统
    └── 椅子 + 坐骑互斥
```

---

## 6. 聊天戒子（气泡）系统

### 6.1 参考来源

- **MapleSalon2** `renderer/chatBalloon/chatBalloon.ts`（108 行）— 气泡渲染
- 当前 `ChatBalloonView.swift` — 已实现 9-slice

### 6.2 当前实现

```
9-slice 布局（11 PNG 切片）:
┌──┬─────┬──┐
│NW│ N*  │NE│   topRow: NW + N重复 + NE (bottom-aligned)
├──┼─────┼──┤
│W │ C*  │E │   midRow: W + C重复 + E (center区域放文字)
├──┼─────┼──┤
│SW│ S/A │SE│   botRow: SW + S + Arrow(中) + SE
└──┴─────┴──┘

缓存: ~/Library/Caches/MiniPet/balloons/{id}/{name}.png
文字颜色: UI/ChatBalloon.img/{id}/info/clr → 32-bit ARGB
```

### 6.3 优化方向

| 优化项 | 当前 | 目标 |
|--------|------|------|
| PNG 解码 | 每帧重解码 | 预缓存 CGImage |
| 箭头位置 | round 计算可能偏移 | 精确 midCount 定位 |
| 文本容器 | 400 固定宽度 | 基于动画尺寸自适应 |
| 头部图片 | 已加载未使用 | 用于名片/名字标签 |

---

## 7. 名片系统

### 7.1 参考来源

- **MapleSalon2** `renderer/nameTag/baseNameTag.ts`（179 行）— 名字标签
- **MapleSalon2** `renderer/medal/medal.ts`（5 个文件）— 勋章渲染
- **MapleSalon2** `handlers/string.rs` — NickTag / NameTag / Medal 分类

### 7.2 组件

```
NameTag (名字标签)
├── UI/NameTag.img/{id} 
├── 支持静态/动画/纯色背景
├── V1/V2 两种定位模式
└── 文字 + 背景 合成

NickTag (昵称标签)
├── Item/Install/0370.img/{id}/info/nickTag
├── 角色头顶的个性化昵称
└── 通常是动画效果

Medal (勋章)
├── 角色名字左边的小图标
├── 来自 Effect/ItemEff.img 的装备特效
└── 可随角色一起动画

宠物名片 (Pet NameTag)
├── 在气泡上方显示宠物名字
├── 字体、颜色、背景可配置
└── 与气泡箭头联动
```

### 7.3 实现计划

```
WzNameTagView (NSView)
  ├── init(nameTagId: String, text: String)
  ├── 加载背景 PNG 切片
  ├── 测量文字尺寸
  ├── 9-slice 合成背景 + 文字
  └── 支持动画背景帧切换
```

---

## 8. 特效系统

### 8.1 参考来源

- **MapleSalon2** `renderer/skill/skill.ts`（254 行）— 技能特效
- **MapleSalon2** `renderer/character/characterAnimatablePart.ts`（60 行）— 独立动画部件
- **MapleSalon2** `handlers/skill.rs`（77 行）— 技能字符串

### 8.2 特效分类

```
Effect Types
├── Skill Effects（技能特效）
│   ├── Skill/{job}/skill/{skillId}
│   ├── 前后分层（backLayers / frontLayers）
│   ├── random effect 变体
│   └── 独立动画时间线
│
├── Item Effects（道具特效）
│   ├── Effect/ItemEff.img/{itemId}
│   ├── 披风飘动、耳环闪光、武器光效
│   ├── 可独立于身体动画播放
│   └── 使用锚点链定位（通常 brow 锚点）
│
├── Animation Effects（动作特效）
│   ├── 攻击时的斩击/剑气特效
│   ├── 施法时的光环/魔法阵
│   └── hit 受击闪白
│
└── Particle Effects（粒子系统）
    ├── 掉落星星、花瓣、光点
    ├── PixiJS 的 particle 模块
    └── 可选：Swift 用 CAEmitterLayer
```

### 8.3 实现计划

```
WzEffectRenderer
  ├── loadSkillEffect(skillId, job)
  ├── loadItemEffect(itemId)
  ├── render(container: CALayer)
  ├── 独立帧循环管理
  └── 合成: effectLayer 在角色前后分层

宠物动画特效:
├── 已有: switchTo 时 0.12s 渐入
├── 新增: 受击时白色闪烁 (CLayer flash)
├── 新增: 空闲时随机粒子 (CAEmitterLayer)
└── 新增: 说话时文字弹出动画
```

---

## 9. 地图背景系统

### 9.1 参考来源

- **MapleSalon2** `renderer/map/`（8 个文件）— 地图渲染
- **MapleSalon2** `renderer/container/GapTilingContainer.ts`（171 行）— 瓦片系统
- **MapleSalon2** `handlers/map.rs`（74 行）— 地图字符串
- `MapRenderHandler.java`（1121 行）— 后端地图渲染

### 9.2 地图结构

```
Map.wz/Map/Map{type}/{mapId}.img
├── info (BGM, 可视范围, 地图名)
├── back (背景/前景 层, 每个 back 子节点)
│   ├── bS: 背景资源名 (e.g. "henesys")
│   ├── no: 背景编号
│   ├── type: 瓦片模式 (0-7)
│   ├── front: 0=背景 1=前景
│   ├── cx, cy: 视差系数
│   ├── rx, ry: 滚动速度系数
│   ├── x, y: 位置偏移
│   ├── flip: 水平翻转
│   └── (资源来自 Map/Back/{bS}.img/{no})
├── tile (地砖: x, y, bS, no, zM)
├── obj (物体: x, y, z, oS, l0, l1)
├── foothold (立足点: 碰撞检测)
└── portal (传送点)
```

### 9.3 Tile Mode 类型

| Type | 渲染方式 |
|------|---------|
| 0 | 单张不重复 |
| 1 | 水平重复 |
| 2 | 垂直重复 |
| 3 | 水平+垂直重复 |
| 4 | 水平重复+垂直覆盖 |
| 5 | 跟随角色 (hScrollBy) |
| 6 | 单张(以screen算) |
| 7 | 水平重复(以screen算) |

### 9.4 视差公式（来自 MapRenderHandler.java）

```
背景层滚动:
  px += cx * (100 + rx) / 100.0

其中:
  px = 背景层 x 位置
  cx = 视差系数（越大移动越快）
  rx = 滚动速度系数

前景层:
  front=1 的层在玩家前面渲染
```

### 9.5 实现计划

```
WzMapRenderer
├── loadMap(mapId: String)           → 加载地图 XML
├── loadBackgroundTiles(mapId: String) → 加载所有 back 层
├── renderBackground(parallaxOffset) → 按 tile mode 渲染
├── renderForeground(parallaxOffset) → 前景层渲染
└── compositeScene(layers: [SceneLayer]) → 场景分层合成

场景布局:
┌─────────────────┐
│ 背景层 (z:-100)  │  ← 远景，视差最慢
│ 中景层 (z:-50)   │  ← 正常视差
│ 宠物层 (z:0)     │  ← 宠物固定在屏幕中心
│ 前景层 (z:50)    │  ← 最快视差/覆盖
└─────────────────┘
```

---

## 10. NPC / MOB 系统

### 10.1 MOB 系统（已实现）

当前 PetView 已支持 MOB 精灵图播放：

```
Mob/{code}.img
├── stand (站立动画)
├── move (移动动画)
├── attack1 (攻击动画)
├── skill1 (技能动画)
├── fly (飞行动画)
├── die (死亡动画)
└── hit (受击动画)

精灵图格式: 水平条带 PNG
加载: 缓存 → API(fetch_and_generate.py) → 本地后备
```

### 10.2 NPC 系统（计划）

```
NPC.wz/{code}.img
├── stand (站立动画)
├── move (移动动画)
└── say (说话动画，带入对话气泡)

与 MOB 的区别:
├── NPC 需要对话气泡
├── NPC 有交互事件（点击→对话）
└── NPC 通常不移动
```

---

## 11. 合成管线和渲染引擎

### 11.1 多层次合成

```
┌─────────────────────────────────────────────────────┐
│ 1. SceneView 场景容器                                │
│    ├── 加载背景层 (Map/Map/Map{type}/{mapId}.img)    │
│    ├── 加载中景层 (obj/tile)                         │
│    └── 渲染到场景画布                                 │
├─────────────────────────────────────────────────────┤
│ 2. 角色合成 (CharacterView)                          │
│    ├── 加载 zmap 顺序                                 │
│    ├── 并行加载所有 body slot 的帧图片                │
│    ├── 获取每个 slot 的 origin 坐标                   │
│    ├── WzCompositor.composite() → 单层合成            │
│    └── 如果带坐骑: 坐骑层 + 角色层叠加               │
├─────────────────────────────────────────────────────┤
│ 3. 叠加层 (OverlayView)                              │
│    ├── 聊天气泡 (ChatBalloonView)                    │
│    ├── 名片标签 (WzNameTagView)                      │
│    ├── 特效层 (WzEffectRenderer)                     │
│    └── 调试标记 (debugDot)                           │
└─────────────────────────────────────────────────────┘
```

### 11.2 性能优化策略

| 策略 | 说明 |
|------|------|
| **精灵图条预合成** | 纸娃娃也采用水平条带精灵图，动画切换直接裁剪 |
| **缓存 WZ 合成** | `~/Library/Caches/MiniPet/avatar/{hash}.png` |
| **无效化缓存** | 装备变更时自动清除对应动作缓存 |
| **并行下载** | 使用 `withTaskGroup` 并行加载所有 body slot |
| **懒惰合成** | 只在动画切换时合成，不每帧重算 |
| **分帧预加载** | 播放当前帧时预加载后续 3 帧 |

### 11.3 渲染选项对比

| 方式 | 优点 | 缺点 | 适用场景 |
|------|------|------|---------|
| 精灵图条（当前） | 单帧 O(1)、低内存 | 预生成慢 | 怪物/MOB |
| 逐帧合成（新增） | 灵活、动态 | 每帧 O(n) | 纸娃娃 |
| 混合（推荐） | 首次合成后缓存 | 缓存失效管理 | 所有场景 |

---

## 12. 数据流架构

### 12.1 数据获取管道

```
┌──────────────┐    ┌──────────────┐    ┌──────────────┐
│  后端 API    │───▶│  Swift 模块   │───▶│  CALayer     │
│              │    │              │    │              │
│ POST /api/   │    │ WzXmlParser  │    │ NSImage →    │
│   wz/xml     │    │   → WzNode   │    │ CGImage →    │
│              │    │              │    │ layer.contents│
│ GET /api/    │───▶│ WzImageLoader│    │              │
│   wz/image   │    │   → NSImage  │    │  + origin    │
│              │    │              │    │  + z-order   │
│ POST /api/   │───▶│ APIClient    │    │              │
│   wz/data/   │    │   → [Models] │    │              │
│   query/     │    │              │    │              │
│   string     │    │              │    │              │
└──────────────┘    └──────────────┘    └──────────────┘
```

### 12.2 缓存层级

```
1. 内存缓存 (最快)
   ├── WzImageLoader.imageCache: [String: Data] (NSCache)
   └── WzCharacterCompositor.cachedZmap (数组)

2. 文件缓存 (持久化)
   ├── ~/Library/Caches/MiniPet/{mobId}/ (怪物精灵图)
   ├── ~/Library/Caches/MiniPet/avatar/{hash}/ (纸娃娃精灵图)
   ├── ~/Library/Caches/MiniPet/balloons/{id}/ (气泡切片)
   └── ~/Library/Caches/MiniPet/maps/{mapId}/ (地图背景)
```

---

## 13. 未来架构路线图

### 13.1 模块依赖图

```
APIClient (基础 HTTP)
  ├── APIClient+WZ (equip/chair/mount 扩展)
  ├── WzModel (XML 解析)
  └── WzImageLoader (图片下载 + 缓存)

WzCompositor (合成引擎)
  ├── WzCharacterCompositor (纸娃娃)
  │   ├── → WzMountCompositor (坐骑)
  │   └── → WzChairCompositor (椅子)
  ├── WzMapRenderer (地图)
  └── WzEffectRenderer (特效)

PetView (宠物资产)
  ├── ChatBalloonView (气泡)
  ├── WzNameTagView (名片)
  ├── WzEffectLayer (特效)
  └── → SceneView (场景)
```

### 13.2 里程碑

```
M1 (v0.3) — 当前 ✅ 已大部分完成
├── F12 聊天气泡         ✅ 85%
├── WZ 前端管线           ✅ 基础模块完成
├── F9 默认动画           ✅ 50%
└── PetView 拆分          ❌ 待做

M1.5 (v0.35) — 代码重构 ⬅️ 当前阶段
├── PetView 拆分为 4 个类   ❌
├── 调试系统升级            ❌
├── WzMountCompositor       ✅ 待审核
└── WzChairCompositor       ✅ 待审核

M2 (v0.4) — 纸娃娃集成
├── F5 纸娃娃系统           状态: WzCharacterModel+Compositor 已生成
├── 精灵图条生成管线         ❌
├── 装备选择 UI              ❌
└── 染色（MixDye HSV）       ❌

M3 (v0.5) — 场景扩展
├── F6 地图背景              ❌
├── F7 椅子 + 骑宠           ❌
├── F3 终端美化              ❌
└── F8 设置面板              ❌

M4 (v0.6) — 完善生态
├── F1 NPC 支持              ❌
├── F2 pet-chat 发言         ❌
├── F4 状态感知              ❌
├── F10 R2 Lua 导出          ❌
└── F13 QQ Bot 集成          ❌
```

### 13.3 文件结构目标

```
Sources/MiniPet/ (目标 ~35 个文件)
├── 入口层
│   ├── main.swift               ← app 入口
│   ├── AppDelegate.swift        ← 生命周期
│   ├── CLIArgs.swift            ← 命令行参数
│   └── Constants.swift          ← 全局常量
│
├── 网络层
│   ├── APIClient.swift          ← HTTP 基础
│   ├── APIClient+WZ.swift       ← WZ 查询扩展
│   ├── HermesClient.swift       ← Hermes 进程
│   └── BotClient.swift          ← QQ Bot
│
├── 数据层
│   ├── Models.swift             ← 基础模型 (SpriteEntry/PetConfig/MobInfo)
│   ├── WzCharacterModel.swift   ← 纸娃娃模型 (EquipCategory/BodySlot)
│   ├── CacheManager.swift       ← 文件缓存
│   └── Helpers.swift            ← 工具函数
│
├── WZ 引擎层
│   ├── WzModel.swift            ← XML 解析
│   ├── WzImageLoader.swift      ← 图片下载
│   ├── WzCompositor.swift       ← 合成引擎 + Ride/Chair/Background
│   ├── WzCharacterCompositor.swift ← 纸娃娃合成
│   └── WzMapRenderer.swift      ← 地图渲染
│
├── UI 组件层
│   ├── PetView.swift            ← 精灵图引擎 (待拆分)
│   ├── ContainerView.swift      ← 布局容器
│   ├── PetPanel.swift           ← NSPanel
│   ├── ChatBalloonView.swift    ← 聊天气泡
│   ├── WzNameTagView.swift      ← 名片
│   ├── TerminalView.swift       ← PTY 终端
│   ├── InputDialog.swift        ← 输入弹窗
│   └── StatusBarController.swift ← 状态栏
│
└── 渲染增强层
    ├── WzEffectRenderer.swift   ← 特效
    ├── SceneView.swift          ← 场景合成
    └── SettingsPanel.swift      ← 设置面板
```

---

## 附录 A: 数据模型对照表

| MiniPet 类型 | MapleSalon2 对应 | 说明 |
|-------------|-----------------|------|
| `EquipCategory` | `string.rs` `EquipCategory` | 32 变体枚举，完全对齐 |
| `BodySlot` | `zmap.rs` zmap 顺序 | 16 种身体部位 |
| `CharacterAppearance` | `store/character/store.ts` 状态 | 角色外观全状态 |
| `BodyPartFrame` | `characterBodyFrame.ts` 合成帧 | 单帧单部位 |
| `CompositedCharacter` | `character.ts` renderCharacter() | 合成结果 |
| `WzPaths` | `handlers/path.rs` | 所有 WZ 路径常量 |
| `CompositingLayer` | `dr()` 绘制模式 | `image + originX + originY + z + flip` |
| `ActionFrame` | `WzActionInstruction` | `(action, frame, delay, move, flip)` |

## 附录 B: 参考文件索引

| 来源 | 文件/模块 | 行数 | 说明 |
|------|----------|------|------|
| MapleSalon2 Rust | `handlers/string.rs` | 468 | EquipCategory 枚举 + 装备字符串解析 |
| MapleSalon2 Rust | `handlers/zmap.rs` | 23 | z-order 渲染顺序 |
| MapleSalon2 Rust | `handlers/path.rs` | 30 | WZ 路径常量 |
| MapleSalon2 Rust | `handlers/mount.rs` | 105 | 坐骑字符串 |
| MapleSalon2 Rust | `handlers/chair.rs` | 104 | 椅子字符串 |
| MapleSalon2 Rust | `handlers/item.rs` | 74 | 道具 info 查询 |
| MapleSalon2 Rust | `handlers/png.rs` | 100 | PNG 提取源码 |
| MapleSalon2 Rust | `handlers/mount_skill_id.rs` | 791 | 坐骑→技能 ID 映射表 |
| MapleSalon2 TS | `renderer/character/character.ts` | 1037 | 核心角色类 |
| MapleSalon2 TS | `renderer/character/characterBodyFrame.ts` | 268 | 单帧身体渲染 |
| MapleSalon2 TS | `renderer/character/item.ts` | 557 | 装备加载 |
| MapleSalon2 TS | `renderer/character/itemPiece.ts` | 196 | 锚点系统 |
| MapleSalon2 TS | `renderer/chair/chair.ts` | 564 | 椅子合成 |
| MapleSalon2 TS | `renderer/tamingMob/tamingMob.ts` | 316 | 坐骑合成 |
| MapleSalon2 TS | `renderer/chatBalloon/chatBalloon.ts` | 108 | 聊天气泡 |
| MapleSalon2 TS | `renderer/nameTag/baseNameTag.ts` | 179 | 名字标签 |
| MapleSalon2 TS | `renderer/skill/skill.ts` | 254 | 技能特效 |
| 纸娃娃网页 | PixiJS v8 + SolidJS | — | Canvas 逐部件合成 |
| 已有代码 | `PetView.swift` | 651 | 待拆分的上帝类 |
| 已有代码 | `WzCharacterCompositor.swift` | 842 | 已生成的纸娃娃合成器 |
| 已有代码 | `WzCharacterModel.swift` | 457 | 已生成的纸娃娃数据模型 |
| 已有代码 | `APIClient+WZ.swift` | ~400 | 已生成的 API 扩展 |
