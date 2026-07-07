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

## Claude Code 行为准则

1. **全中文输出** — 所有确认/选项/提示零英文
2. **只给结果不给过程** — 不展示代码 diff/测试/中间步骤
3. **给结果不给理由** — 不做无用推荐/不搞分化
4. **禁止发散思维** — 不准可能/或许/我觉得/有望/预计
5. **禁止编造欺骗** — 数据必须从API获取，不估算
6. **禁止期盼** — 不准预测/预估/预期
7. **禁止加码** — 用户说什么做什么，不扩大范围
8. **模型标签诚实** — 实际跑什么模型标什么名
9. **Java 项目必须编译通过才结束** — mvn compile -q 无报错才交付 — 实际跑什么模型标什么名


## CodeGraph 项目记忆

- 回答涉及项目上下文、架构、代码关系的问题时，先使用 `codegraph_memory_search` 查询项目记忆
- 修改代码前，先使用 `codegraph_get_edit_context` 了解调用方、测试覆盖和最近的改动
- 排查问题路径时，使用 `codegraph_get_callers` / `codegraph_get_callees` 追踪调用链

## AppKit UI 调试经验

**编译通过但窗口不显示**时按以下顺序排查：

1. **先确认进程是否崩溃** — `pgrep -f MiniPet` 检查进程存活，`ls -lt ~/Library/Logs/DiagnosticReports/ | grep -i minipet` 查崩溃日志
2. **断点布局递归** — `lldb -o "b _NSDetectedLayoutRecursion" -o "run" .build/debug/MiniPet`，递归布局会导致窗口静默不显示
3. **用 osascript 绕过 UI 测试事件链路** — 即使窗口不可见，也能通过菜单触发逻辑，排除菜单事件传递问题
4. **直接调 show() 隔离问题** — main.swift 加 `asyncAfter` 直接调 `SettingsWindowController.show()`，判断是初始化崩还是事件没传到
5. **Auto Layout 异常** — `NSGenericException: no common ancestor` 意味着 addSubview 缺失
6. **避免存储属性初始化时创建重量级视图** — 用 `lazy var` 或移到 `commonInit()`
