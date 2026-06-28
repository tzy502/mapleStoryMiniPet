# MapleStory MiniPet — Claude Code 上下文

Swift + AppKit 桌面悬浮宠物，显示冒险岛怪物动画。支持后端驱动、多怪物切换。

## 技术栈

- Swift 5.9+ / AppKit / NSPanel
- macOS 13+
- 精灵图：PIL (Python) 生成，水平条带 PNG 格式
- 冒险岛资源：WZ → XML（WzComparerR2）+ PNG 帧导出
- 后端 API：wiki-backend (192.168.3.46:10502)

## 核心文件

| 文件 | 用途 |
|------|------|
| `Sources/MiniPet/main.swift` | 全部代码（单文件） |
| `start.sh` | 启动脚本，支持 `--mob <id>` |
| `fetch_and_generate.py` | API 帧下载 → 接地锚点对齐 → 精灵图生成 |
| `sprites/pet_config.json` | 精灵图配置（动画名、帧数、尺寸） |
| `sprites/*.png` | 默认怪物精灵图条（8880150 路西德） |
| `regenerate_sprites.py` | WZ origin / 接地锚点 精灵图生成 |
| `refix_strip.py` | 从已有精灵图条重新对齐 |

## 架构

```
MiniPet (AppKit)
├── CLIArgs        命令行参数解析 (--mob, --debug-api)
├── APIClient      HTTP 客户端 → wiki-backend (192.168.3.46:10502)
│   ├── fetchAndGenerateSprites(mobId) → python3 fetch_and_generate.py
│   ├── fetchMobName(mobId)          → POST /api/wz/data/query/string
│   └── fetchMobList()               → POST /api/wz/data/query/string
├── CacheManager   本地缓存: ~/Library/Caches/MiniPet/{mobId}/
│   ├── isCached, loadConfig, saveConfig, extractZIP
├── PetPanel       NSPanel, 透明无边框悬浮
├── ContainerView
│   ├── PetView    精灵图播放器 + 动画引擎
│   │   ├── loadInitial(mobId) → 缓存→API→本地 三级回退
│   │   ├── applyConfig()      → 按 pet_config.json 加载
│   │   ├── tick()             → 240ms 逐帧，支持 loop/oneShot
│   │   ├── senseHermes()      → 每3s 检测 hermes 活动 → 切换 move
│   │   ├── idleTimer          → 5s 无操作回 stand
│   │   ├── randomSkillTimer   → 15~30s 随机 attack/skill
│   │   ├── mouseDown          → 拖拽时 move（fallback: fly→stand）
│   │   └── rightMouseDown     → 菜单：切换动画/怪物/终端
│   └── TerminalView  PTY 内嵌终端（可选）
└── PTYManager       伪终端，运行 hermes --continue
```

## 动画播放逻辑

```
默认动画           → stand (循环)
空闲 > 5s          → stand (循环)
Hermes 活动        → move (循环, 5s 后回 stand)
用户拖拽           → move → fly → stand (循环, 拖拽结束回 stand)
随机 15~30s        → attack{N} | skill{N} (播放一轮回 stand)
```

## 精灵图加载流程

```
loadInitial(mobId)
  ├── 1. CacheManager 检查 ~/Library/Caches/MiniPet/{mobId}/
  ├── 2. APIClient.fetchAndGenerateSprites() → python3 fetch_and_generate.py
  │       ├── POST /api/wz/renderImgPath  → 获取帧路径列表
  │       ├── GET  /api/wz/image?path=...  → 下载逐帧 PNG
  │       ├── 接地锚点对齐 (ground anchor, pad=4)
  │       └── 生成精灵图条 + pet_config.json → 写入缓存
  └── 3. Fallback 到 sprites/ 目录（仅 8880150）

## 编码约定

- 单文件架构，无模块拆分
- 精灵图：水平条带，`frameWidth × count = 总宽`
- 帧裁剪：`cg.cropping(to: CGRect(x: i*fw, y: 0, width: fw, height: fh))`
- 动画切换：`switchTo()` → `resize()` → 窗口自适应
- oneShot 动画（attack/skill）播完一轮后自动回 stand
- 循环动画（stand/move/fly）持续播放直到切换

## Origin 对齐

精灵图必须使用统一锚点对齐，避免抖动。

- WZ 数据可用 → WZ origin（`regenerate_sprites.py --method wz`）
- 无 WZ 数据 → 接地锚点（`regenerate_sprites.py --method ground`）

WZ origin 逻辑参考：`GifUtil.wzImgDetailToGif`
```java
finalOriginX = max(originX); finalOriginY = max(originY);
gc.drawImage(image, finalOriginX - originX, finalOriginY - originY, null);
```

## API 合约

**帧路径查询**: `POST /api/wz/renderImgPath`
- Body: `{"path": "Mob/_Canvas/{code}.img"}`
- Response: `{"success": true, "images": ["Mob/_Canvas/CODE.img/action/frame", ...]}`

**单帧下载**: `GET /api/wz/image?path={wzPath}`
- Response: PNG 二进制

# 从现有精灵图条重新对齐
python3 refix_strip.py --strip stand.png --frames 16 --frameW 312 --out stand_fixed.png
```

## 相关项目

- `maplestory-wiki-backend` — Java WZ 解析 + GIF 导出
- `mxd-spine` — PIXI v4 网页渲染
- API 文档: /Volumes/docker/hermes/download/api-docs/
