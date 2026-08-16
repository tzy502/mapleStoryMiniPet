# MiniPet API 迁移文档

> 生成时间：2026-07-05
> 目标：记录所有 API 端点，评估 wiki-backend (192.168.3.46:10502) 支持状态，为后续迁移到新后端提供参考。

---

## 当前后端地址

| 服务 | 地址 | 端口 | 用途 |
|------|------|------|------|
| wiki-backend (首选) | `127.0.0.1` | 10502 | WZ 资源、属性、字符串查询 |
| wiki-backend (备选) | `192.168.3.46` | 10502 | WZ 资源、属性、字符串查询 |
| Hermes Gateway (NAS Docker) | `172.17.0.1` | 5200 | WebSocket 实时通信、REST 状态查询 |
| Hermes Gateway (蒲公英 VPN) | `172.16.1.13` | 5200 | WebSocket 实时通信（从 Mac 访问） |
| QQ Bot 中间件 | `127.0.0.1` | 8080 | 消息转发到 Hermes |
| Hermes Dashboard | `172.16.1.13` | 9119 | Web 管理面板（非 API） |

### 后端选择逻辑

```swift
// Constants.swift
let apiBases = ["http://127.0.0.1:10502", "http://192.168.3.46:10502"]
```

`APIClient.resolveBase()` 遍历 apiBases 列表，调用 `GET /api/health`，第一个成功响应的 base URL 被缓存为 `resolvedBase`。

---

## 接口清单

### 1. 健康检查

| 项目 | 内容 |
|------|------|
| **方法** | GET |
| **路径** | `/api/health` |
| **用途** | API 后端探测、可用性检测 |
| **调用位置** | `APIClient.resolveBase()` (Swift)、`fetch_and_generate.py` 第 246-256 行 (Python) |
| **当前支持** | ✅ |
| **请求格式** | 无 |
| **响应格式** | JSON（任意字段，仅检查 HTTP 200） |

---

### 2. 图片/资源类

#### 2.1 GET `/api/wz/image?path=...`

| 项目 | 内容 |
|------|------|
| **方法** | GET |
| **路径** | `/api/wz/image?path={wzPath}` |
| **用途** | 下载 WZ 节点下的单帧 PNG 图片 |
| **当前支持** | ✅ |
| **响应格式** | PNG 二进制 |

调用位置及示例：

| 调用位置 (Swift/Python) | WZ 路径示例 |
|------------------------|-------------|
| `WzImageLoader.fetchImage(wzPath:)` | 任意 WZ 帧路径，如 `Mob/_Canvas/8880150.img/stand/0` |
| `APIClient.fetchBalloonTileImage(balloonId:tileName:)` | `UI/ChatBalloon.img/{id}/{tileName}` |
| `MaterialBrowserPanel.loadThumbnail(for:)` | `Mob/{code}.img/stand/0` 等 |
| `EquipSlotControl.loadThumbnail(id:)` | `Character/{category}/{id}.img/stand/0` |
| `SettingsPanelController.loadMaterialPreview(id:)` | `Mob/_Canvas/{id}.img/stand/0` 或 `Npc/{id}.img/stand/0` |
| `MaterialGridItem.configure(with:type:)` | `Mob/_Canvas/{code}.img/stand/0` 或 `Npc/{code}.img/stand/0` |
| `fetch_and_generate.py` `api_get_image()` | 来自 `renderImgPath` 返回的路径 |

---

#### 2.2 POST `/api/wz/renderImgPath`

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/api/wz/renderImgPath` |
| **用途** | 获取某个 WZ 节点下所有 Canvas 帧的完整路径列表 |
| **当前支持** | ✅ |

**请求体：**
```json
{"path": "Mob/_Canvas/8880150.img/stand"}
```

`fetch_and_generate.py` 额外支持 type/code 字段：
```json
{"type": "mob", "code": "8880150", "path": "Mob/_Canvas/8880150.img"}
```

**响应格式：**
```json
{"success": true, "images": ["Mob/_Canvas/8880150.img/stand/0", "Mob/_Canvas/8880150.img/stand/1", ...]}
```

**调用位置：**

| 文件 | 函数 | 用途 |
|------|------|------|
| `WzImageLoader.discoverFramePaths(wzPath:)` | Swift | 发现怪物帧路径 |
| `APIClient.discoverFramePaths(wzPath:)` | Swift | 发现任意 WZ 节点帧路径 |
| `MaterialBrowserPanel.loadThumbnail(for:)` | Swift | 获取预览图第一帧路径 |
| `fetch_and_generate.py` `_fetch_code_groups()` | Python | 获取怪物所有动作的帧路径 |
| `fetch_and_generate.py` 主流程 | Python | 批量模式下也使用 |

---

### 3. 属性查询类

#### 3.1 POST `/api/wz/property`

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/api/wz/property` |
| **用途** | 查询 WZ 节点下的属性值（单值或子节点） |
| **当前支持** | ✅ |

**请求体：**
```json
{"path": "UI/ChatBalloon.img/{id}/info/clr"}
```

**响应格式（推断）：**
```json
{"value": 123}  // 或包含 "children" 字段的返回
```

**调用位置：**

| 函数 | 查询路径示例 | 用途 |
|------|------------|------|
| `APIClient.fetchBalloonClr(balloonId:)` | `UI/ChatBalloon.img/{id}/info/clr` | 聊天气泡颜色值 |
| `APIClient.isCashItem(category:itemId:)` | `Character/{category}/{id}.img/info/cash` | 判断是否为现金道具 |
| `APIClient.hasColorvar(category:itemId:)` | `Character/{category}/{id}.img/info/colorvar` | 判断是否有染色 |
| `APIClient.isNameTag(category:itemId:)` | `Character/{category}/{id}.img/info/nameTag` | 判断是否为名字标签 |
| `APIClient.isChatBalloon(category:itemId:)` | `Character/{category}/{id}.img/info/chatBalloon` | 判断是否为聊天气泡 |
| `APIClient.isMedalTag(category:itemId:)` | `Character/{category}/{id}.img/info/medalTag` | 判断是否为勋章标签 |
| `APIClient.isNickTag(category:itemId:)` | `Character/{category}/{id}.img/info/info/nickTag` | 判断是否为昵称标签 |
| `APIClient.hasEquipEffect(category:itemId:)` | `Effect/ItemEff.img/{id}/effect` | 判断装备是否有特效 |
| `APIClient.fetchInfoProperties(wzPath:)` | `Character/{category}/{id}.img/info` | 获取 info 节点全部属性 |
| `APIClient.fetchZmap()` | `Base/zmap.img/zmap` | 获取身体渲染顺序 |
| `APIClient.fetchSmap()` | `Base/smap.img` | 获取子部位映射 |
| `APIClient.fetchSetEffectMap()` | `Effect/SetEff.img` | 获取套装特效映射 |
| `WzImageLoader.fetchProperty(wzPath:)` | 任意 WZ 属性路径 | 通用单值属性查询 |

---

#### 3.2 POST `/api/wz/xml`

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/api/wz/xml` |
| **用途** | 获取 WZ 节点的 XML 表示，用于解析 origin、子节点结构等 |
| **当前支持** | ✅ |

**请求体：**
```json
{"path": "Mob/8880150.img/stand"}
```

**响应格式：**
```json
{"xml": "<dir name=\"stand\"><canvas name=\"0\">...</canvas></dir>"}
```

**调用位置：**

| 文件 | 函数 | 用途 |
|------|------|------|
| `WzXmlParser.fetchNode(path:)` | Swift | 解析任意 WZ 节点为 WzNodeModel 树 (特效帧、origin、delay 等) |
| `fetch_and_generate.py` `fetch_wz_xml()` | Python | 获取 WZ origin 数据用于精灵图对齐 |
| `WzSkillEffectRenderer.loadFramesFromNode(path:)` | Swift | 解析技能特效帧的子节点（delay、origin） |
| `WzSkillEffectRenderer.loadFrameImage(wzPath:)` | Swift | 尝试查找子节点下的 PNG 帧 |
| `WzSkillEffectRenderer.effectFrameCount(skillId:jobFolder:)` | Swift | 获取特效总帧数 |
| `WzSkillEffectRenderer.loadCharacterWithEffect(...)` | Swift | 加载角色+特效合成帧 |

---

#### 3.3 POST `/api/wz/data/query/string`

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/api/wz/data/query/string` |
| **用途** | 按类型和名称查询 WZ 字符串（怪物名、装备名、地图名、技能名等） |
| **当前支持** | ✅ |

**请求体（不同 type 参数）：**
```json
// 查询怪物
{"type": "mob", "name": ""}
{"type": "mob", "code": "8880150"}

// 查询装备
{"type": "equip", "category": "Cap", "name": ""}

// 查询 NPC
{"type": "npc", "name": ""}

// 查询椅子
{"type": "chair", "name": ""}

// 查询坐骑
{"type": "mount", "name": ""}

// 查询技能
{"type": "skill", "name": ""}

// 查询地图
{"type": "map", "name": ""}

// 查询现金特效
{"type": "cashEffect", "category": "", "name": ""}

// 通用查询（MaterialBrowserPanel）
{"type": "item"|"equip"|"mob"|"npc"|"map"|"skill", "name": "搜索关键词"}
```

**响应格式：**
```json
[
  {"code": "8880150", "name": "路西德", "category": "...", "parentFolder": "...", "isCash": false, "hasColorvar": false, "hasEffect": false}
]
```

**调用位置：**

| 函数 | type 参数 | 用途 |
|------|-----------|------|
| `APIClient.fetchMobList()` | `"mob"` | 获取全部怪物列表 |
| `APIClient.fetchMobName(mobId:)` | `"mob"` + `code` | 获取单个怪物名称 |
| `APIClient.fetchEquipStrings(extraInfo:)` | `"equip"` / `"cashEffect"` | 获取装备字符串（含 isCash/hasColorvar/hasEffect） |
| `APIClient.fetchChairStrings()` | `"chair"` | 获取椅子列表 |
| `APIClient.fetchMountStrings()` | `"mount"` | 获取坐骑列表 |
| `APIClient.fetchSkillStrings()` | `"skill"` | 获取技能列表 |
| `APIClient.fetchMapStrings()` | `"map"` | 获取地图列表 |
| `APIClient.fetchWzStrings(type:)` | `"npc"` 等 | 通用字符串查询 |
| `MaterialBrowserPanel.queryType(api:type:query:)` | `"item"`/`"equip"`/`"mob"`/`"npc"`/`"map"`/`"skill"` | 素材浏览器搜索 |
| `SettingsPanelController.loadAllData()` | 调用各个专业方法 | 初始化加载所有数据 |
| `fetch_and_generate.py` `_process_mob()` | `"mob"` + `code` | 获取怪物名称 |

---

### 4. WZ 树查询

#### 4.1 POST `/api/wz/tree`

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/api/wz/tree` |
| **用途** | 获取 WZ 目录树结构（子节点名称和类型列表） |
| **当前支持** | ✅ |

**请求体：**
```json
{"path": "UI/ChatBalloon.img"}
```
`fetch_and_generate.py` 额外支持 type/code：
```json
{"type": "mob", "code": "8880150", "path": "Mob/8880150.img"}
```

**响应格式（推断）：**
```json
[
  {"type": "容器", "name": "000"},
  {"type": "容器", "name": "001"}
]
```

**调用位置：**

| 函数 | 用途 |
|------|------|
| `APIClient.fetchBalloonList()` | 获取所有聊天气泡 ID |
| `fetch_and_generate.py` `_fetch_code_groups()` | 获取怪物动画子节点（当 _Canvas 不存在时） |
| `fetch_and_generate.py` `_fetch_code_groups()` | 检查怪物 link 属性 |

---

### 5. 唯一直连 API（非统一封装）

#### 5.1 GET `/node/json/{path}?simple=true`

| 项目 | 内容 |
|------|------|
| **方法** | GET |
| **路径** | `/node/json/{path}?simple=true` |
| **用途** | 直接查询技能特效帧的 delay 值，绕过 APIClient |
| **当前支持** | ❌ |
| **调用位置** | `WzSkillEffectRenderer.frameDelay(...)` 第 196-205 行 |

```swift
// WzSkillEffectRenderer.swift:196-205
let path = "\(jobFolder)/\(skillId).img/effect/\(frame)/delay"
guard let base = await resolveBase() else { return 100 }
let urlStr = "\(base)/node/json/\(path)?simple=true"
```

**原因**：这是唯一一处绕过 APIClient 封装的直接 URL 构建调用。路径格式为 `/node/json/{wzPath}`，与其他所有 API 路径（`/api/wz/...`）不一致。怀疑是旧版后端 API 的残留，可能在当前 wiki-backend (192.168.3.46:10502) 上不可用。

**建议**：改用 `GET /api/wz/property`（POST）（已有 `WzImageLoader.fetchProperty(wzPath:)` 封装）或 `POST /api/wz/xml` → 解析 delay 值的标准路径。

---

### 6. QQ Bot 中间件接口

#### 6.1 POST `/api/send`

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/api/send` |
| **用途** | 向 QQ Bot 发送消息，bot 转发给 Hermes，回复通过 delegate 回调 |
| **当前支持** | ⚠️ 外部服务（非 wiki-backend） |
| **基础地址** | `http://127.0.0.1:8080`（本地 QQ Bot 中间件） |

**请求体：**
```json
{"message": "说一句短话", "source": "minipet"}
```

**响应格式：**
```json
{"reply": "回复文字..."}
```

**调用位置：** `BotClient.speak(_:)` 第 29-51 行

---

### 7. Hermes 本地进程调用（非 HTTP）

#### 7.1 本地进程执行

| 项目 | 内容 |
|------|------|
| **执行路径** | `/Users/a502/.hermes/hermes-agent/venv/bin/hermes` |
| **参数** | `-z {text} --cli --resume 20260626_200327_096c1d` |
| **用途** | 直接调用 Hermes CLI 发送消息并获取回复（当前实现方式） |
| **当前支持** | ✅ 本地进程 |

**调用位置：** `HermesClient.send(_:silent:isGreeting:)` 第 13-14 行

```swift
proc.executableURL = URL(fileURLWithPath: "/Users/a502/.hermes/hermes-agent/venv/bin/hermes")
proc.arguments = ["-z", text, "--cli", "--resume", "20260626_200327_096c1d"]
```

---

### 8. 带默认可行的 WebSocket 接口

以下接口记录在 `hermes-pet-integration.md`，尚未在 Swift 代码中实现（当前使用本地进程调用）。

#### 8.1 WS `/ws` (WebSocket)

| 项目 | 内容 |
|------|------|
| **协议** | WebSocket |
| **路径** | `/ws` |
| **用途** | 与 Hermes Gateway 实时双向通信 |
| **当前支持** | ⚠️ Hermes Gateway 支持，但 Swift 代码尚未实现 WebSocket 客户端 |

**推送事件（Hermes → 宠物）：**

| 事件类型 | 格式 | 用途 |
|---------|------|------|
| `activity` | `{"type":"activity", "state":"run"}` | 驱动宠物动画 (idle/run/review/wave/failed/jump) |
| `tool_progress` | `{"type":"tool_progress", "tool":"terminal"}` | 显示当前工具使用 |
| `streaming` | `{"type":"streaming", "token":"这"}` | 逐字气泡/TTS |
| `response` | `{"type":"response", "text":"完整回复"}` | 一次性完整回复 |

**发送消息（宠物 → Hermes）：**
```json
{"type": "chat", "message": "帮我查一下数据库连接状态"}
{"type": "status"}
```

**地址：**
- 同机器：`ws://127.0.0.1:5200/ws`
- NAS Docker：`ws://172.17.0.1:5200/ws`
- Mac 蒲公英 VPN：`ws://172.16.1.13:5200/ws`

**当前实现状态：** ❌ (Swift 代码未实现，文档阶段)

---

#### 8.2 GET `/status` (REST)

| 项目 | 内容 |
|------|------|
| **方法** | GET |
| **路径** | `/status` |
| **用途** | 查询 Hermes Agent 当前状态 |
| **当前支持** | ⚠️ Hermes Gateway 支持，Swift 代码尚未实现 |
| **响应格式** | `{"active": true, "state": "run", "current_tool": "terminal"}` |

---

#### 8.3 GET `/health` (Hermes)

| 项目 | 内容 |
|------|------|
| **方法** | GET |
| **路径** | `/health` |
| **用途** | Hermes Gateway 健康检查 |
| **当前支持** | ⚠️ Hermes Gateway 支持，Swift 代码尚未实现 |
| **响应格式** | `{"status": "ok", "version": "0.17.0"}` |

---

#### 8.4 POST `/chat` (REST)

| 项目 | 内容 |
|------|------|
| **方法** | POST |
| **路径** | `/chat` |
| **用途** | 向 Hermes 发送消息并获取回复（REST 方式） |
| **当前支持** | ⚠️ Hermes Gateway 支持，Swift 代码尚未实现 |
| **请求体** | `{"message": "...", "session_id": "pet-001"}` |
| **响应格式** | `{"reply": "...", "session_id": "pet-001"}` |

---

## Python 脚本使用的接口 (fetch_and_generate.py)

| 接口 | 方法 | 用途 | 支持状态 |
|------|------|------|----------|
| `/api/health` | GET | 健康检查确定可用后端 | ✅ |
| `/api/wz/renderImgPath` | POST | 获取帧路径列表 | ✅ |
| `/api/wz/tree` | POST | 获取怪物动画子节点 / 检查 link | ✅ |
| `/api/wz/xml` | POST | 获取 WZ XML 解析 origin | ✅ |
| `/api/wz/image?path=...` | GET | 下载单帧 PNG | ✅ |
| `/api/wz/data/query/string` | POST | 获取怪物名称 | ✅ |

Python 脚本特有逻辑：
- 支持批量处理多个 mob code（合并精灵图）
- 自动回退：`127.0.0.1:10502` → `192.168.3.46:10502`
- 走 `/api/wz/tree` 检查 mob link（当 _Canvas 不存在时）

---

## Hermes WebSocket 接口（未实现）

以下接口记录在设计文档中，Swift 代码尚未实现任何 WebSocket 或 Hermes HTTP 客户端：

| 接口 | 用途 | Swift 实现状态 |
|------|------|:------------:|
| `WS /ws` | 实时双向通信（activity/streaming/response/chat） | ❌ 未实现 |
| `GET /status` | 查询 Agent 状态 | ❌ 未实现 |
| `GET /health` | Hermes 健康检查 | ❌ 未实现 |
| `POST /chat` | REST 消息发送 | ❌ 未实现 |

当前 Hermes 通信方式：通过 `HermesClient` 启动本地进程 `/Users/a502/.hermes/hermes-agent/venv/bin/hermes`，使用 `-z` 参数发送消息。输出通过 pipe 读取。

---

## 默认备选怪物

```swift
// Constants.swift
let fallbackMobs: [(code: String, name: String)] = [
    ("9602078", "黑暗奥尔卡"),
    ("9602448", "光冕塞伦"),
]
```

这些怪物不依赖 API，但精灵图数据需在本地缓存存在。

---

## 等待后端不支持的接口（❌）

| 接口 | 文件位置 | 问题 |
|------|----------|------|
| `GET /node/json/{path}?simple=true` | `WzSkillEffectRenderer.swift:196-205` | 这是唯一直连非 `/api/wz/` 路径的调用。路径格式 `/node/json/` 不是 wiki-backend 的标准路径。**建议改用 `POST /api/wz/property` 或 `POST /api/wz/xml` 解析 delay。** |

---

## 当前架构的数据流总结

```
┌─────────────────────────────────────────────────────────────┐
│                       MiniPet (macOS App)                    │
│                                                             │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────────┐  │
│  │ APIClient   │  │ HermesClient │  │ BotClient          │  │
│  │ (Swift)     │  │ (进程调用)    │  │ (HTTP POST)        │  │
│  │             │  │              │  │                    │  │
│  │ fetchMobList│  │ hermes -z    │  │ POST /api/send     │  │
│  │ fetchWzStr  │  │ --cli        │  │ → QQ Bot           │  │
│  │ fetchImage  │  │              │  │                    │  │
│  │ fetchXML    │  │              │  │                    │  │
│  │ fetchProp   │  │              │  │                    │  │
│  └──────┬──────┘  └──────┬───────┘  └─────────┬──────────┘  │
│         │                │                    │              │
└─────────┼────────────────┼────────────────────┼──────────────┘
          │                │                    │
          ▼                ▼                    ▼
   wiki-backend      Hermes CLI          QQ Bot Middleware
   :10502            (本地进程)           :8080
```

---

## 迁移注意事项

1. **唯一不标准路径**：`/node/json/{path}?simple=true` 在 `WzSkillEffectRenderer.frameDelay()` 中。迁移时应改为使用 `POST /api/wz/property`（已有 `WzImageLoader.fetchProperty(wzPath:)` 封装）。

2. **Hermes WebSocket 待实现**：当前 Hermes 通信通过本地子进程进行。设计文档 `hermes-pet-integration.md` 中规划的 WS/HTTP 接口尚未实现。迁移步骤：
   - 实现 WebSocket 客户端（连接 `ws://172.16.1.13:5200/ws`）
   - 替换 `HermesClient` 的进程调用方式为 WS 或 `POST /chat`

3. **API 版本兼容性**：所有 `/api/wz/` 路径均由 wiki-backend 提供。如果新后端使用不同的路径前缀，需要在 `APIClient` 层进行路径适配。

4. **响应格式依赖**：多处代码直接使用 `JSONSerialization` 解析后端响应，未使用 Codable 模型。后端响应格式变更需要修改多个解析点。

5. **测试地址**：
   - NAS: `172.17.0.1:10502` (Docker 内部)
   - 蒲公英: `172.16.1.13:10502` (VPN)
   - Hermes WS: `172.16.1.13:5200/ws`
   - Hermes Dashboard: `http://172.16.1.13:9119`