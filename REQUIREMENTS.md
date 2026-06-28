# 需求文档 — MapleStory MiniPet v2

> 冒险岛桌面悬浮宠物，Swift + AppKit 原生实现。后端驱动多怪物切换，内嵌 Hermes 对话终端。

---

## 1. 已实现功能

### 1.1 精灵图播放

- 水平条带 PNG 精灵图，`frameWidth × frameCount = 总宽`
- 4 种动画循环：stand / move / attack{N} / skill{N}
- 240ms 逐帧播放，oneShot（attack/skill 播完回 stand）/ loop（stand/move 持续）

### 1.2 动画调度

```
空闲 > 5s           → stand（循环）
Hermes 感知到活动    → move（5s 后回 stand）
用户拖拽窗口         → move → fly → stand（fallback 链）
随机 15~30s          → attack{N} | skill{N}（随机选一个，播完回 stand）
```

### 1.3 怪物切换

- 右键菜单 → 切换怪物 → 从后端拉取精灵图
- 命令行：`./start.sh --mob 9602538`
- 缓存：`~/Library/Caches/MiniPet/{mobId}/`

### 1.4 内嵌终端

- 右键 → 💬 内嵌终端
- 底层使用 `hermes -z "msg" --cli --resume {sessionId}`，固定 session
- 输出天然不含 thinking（与 QQ/飞书 Bot 行为一致）
- 用户消息灰色 `>` 前缀，Hermes 回复绿色

### 1.5 Origin 对齐

精灵图使用统一锚点对齐，消除帧间抖动。

**正确逻辑**（参考 `GifUtil.wzImgDetailToGif`）：

```java
// 取所有帧的 maxOrigin 作为统一锚点
int finalOriginX = max(所有帧的 originX);
int finalOriginY = max(所有帧的 originY);

// 每帧按差值偏移
drawImage(image, finalOriginX - frameOriginX, finalOriginY - frameOriginY);
```

- WZ 数据可用 → WZ origin（`regenerate_sprites.py --method wz`）
- 无 WZ 数据 → 接地锚点（`regenerate_sprites.py --method ground`）
- 已验证：8880150（接地锚点）、9602538（WZ origin）

---

## 2. 待实现需求

### 2.1 批量编号合并

**背景**：冒险岛 BOSS 怪物在 WZ 中可能对应多个编号（不同形态/阶段），对用户来说是同一个怪物。

**需求**：
- 用户输入逗号分隔的多个编号，如 `9602538,9602539,9602540`
- 后端批量查询所有编号的动画节点
- 合并为单一怪物配置，动画命名格式为 `动作-编号`（如 `stand-9602538`、`attack1-9602539`）

**后端接口**：
```
POST /api/wz/renderImgPath/batch
  Body: {"paths": ["Mob/_Canvas/9602538.img", "Mob/_Canvas/9602539.img"]}
  → 返回合并后的动画列表
```

### 2.2 动画命名规则

- 单编号：`stand`、`move`、`attack1`（不变）
- 多编号合并：`动作-编号`，如 `stand-9602538`、`attack1-9602539`
- 编号缺失时回退到基础名

### 2.3 怪物名称查询

**需求**：通过后端 `POST /api/wz/data/query/string` 查询怪物名称（非编号）。

```
POST /api/wz/data/query/string
  Body: {"path": "Mob/9602538.img/info/name"}
  → 返回怪物中文名
```

用于右键菜单和状态栏显示。

---

## 3. 架构

```
MiniPet (AppKit)
├── CLIArgs         命令行解析 (--mob, --debug-api)
├── APIClient       wiki-backend HTTP 客户端
├── CacheManager    本地缓存: ~/Library/Caches/MiniPet/{mobId}/
├── PetPanel        NSPanel 透明无边框悬浮窗
├── ContainerView
│   ├── PetView     精灵图播放 + 动画引擎
│   └── TerminalView 内嵌终端（可选）
├── HermesClient    每次输入执行 hermes -z --cli --resume
├── StatusBarController 菜单栏图标
└── AppDelegate     退出时播放 die 动画
```

---

## 4. API 合约

| 接口 | 方法 | 用途 |
|------|------|------|
| `/api/wz/renderImgPath` | POST | 查询动画帧路径列表 |
| `/api/wz/image?path=...` | GET | 下载单帧 PNG |
| `/api/wz/data/query/string` | POST | 查询怪物名称 |

---

## 5. 精灵图生成管线

```
fetch_and_generate.py <mob_code>
  ├── 查询怪物名称 → POST /api/wz/data/query/string
  ├── 获取帧路径   → POST /api/wz/renderImgPath
  ├── 批量下载帧   → GET  /api/wz/image
  ├── Origin 对齐  → WZ origin / 接地锚点
  └── 输出精灵图条 + pet_config.json → 缓存目录
```

---

## 6. 技术约束

- macOS 13+, Swift 5.9+
- 精灵图生成：Java（wiki-backend）+ Python（本地工具）
- 透明无边框 NSPanel，`level: .floating`
- 网络：URLSession async/await
- 离线：缓存精灵图到 `~/Library/Caches/MiniPet/`

---
## 7. 验证

- 模拟输入8880191,8880155,8880154,8880153,8880152,8880151,8880150
- 期望图片合并 菜单叫做路西德
---





## 8. 参考

- `GifUtil.wzImgDetailToGif` — origin 计算标准实现
- `WzGifHandler.changeToDetail` — WZ 帧 → WzImgDetail
- `regenerate_sprites.py` — 本地精灵图生成（wz/ground/bbox 三种方法）
- Hermes CLI：`hermes -z "msg" --cli --resume {sessionId}`



