# MiniPet v1.0 — 需求分析

> 阶段：需求分析 · 已完成
> 日期：2026-07-19
> 原则：只说做什么，不说怎么做

---

## 1. 产品定位

桌面悬浮宠物应用。冒险岛风格动画角色，AI 驱动人格和行为。双平台（macOS + Windows）。

---

## 2. 功能需求总览

| 编号 | 功能 | 优先级 |
|------|------|--------|
| F1 | 桌面悬浮显示 | P0 |
| F2 | 素材获取管道（wzlib DLL） | P0 |
| F3 | 精灵图动画 + Origin 对齐 | P0 |
| F4 | 聊天气泡系统 | P0 |
| F5 | 动画行为逻辑 | P0 |
| F6 | 纸娃娃换装系统 | P1 |
| F7 | 素材浏览与搜索 | P1 |
| F8 | 设置面板（含 WZ 配置） | P1 |
| F9 | 右键菜单 | P0 |
| F10 | 地图背景系统 | P2 |
| F11 | 托盘/状态栏图标 | P2 |
| P2 | Hermes AI 集成（人格+感知+对话+关联） | P2 |

---

## 3. P0 需求详情

### F1 — 桌面悬浮显示

透明无边框窗口，始终置顶，跨桌面跟随，多显示器可用。

### F2 — 素材获取管道

**参考**：
- `mapRederWeb/backend/Program.cs`（R2 wzlib DLL 调用示例）
- `maplestory-wiki-backend/core/wzlib/`（Java 版 wzlib 移植）

直接引用 `WzComparerR2.WzLib` DLL，加载 WZ 文件，产出精灵图条（水平拼接）+ pet_config.json（origin、delay、帧数、帧尺寸）+ 气泡切片 + 纸娃娃部件。导出 PNG+JSON 为纯静态文件，运行时零依赖。

### F3 — 精灵图动画与 Origin 对齐

**参考**：
- `mapRederWeb/frontend/src/index.ts`（PixiJS origin pivot 渲染）
- `MapleSalon2`（纸娃娃合成）

动画顺序：`stand → say → mouse → move → hand → laugh → eye → fly → die`

水平条带按 frameWidth 取帧区域，origin 定位保证动作切换不抖动。60fps，缺失动作 fallback stand。

### F4 — 聊天气泡系统

冒险岛聊天戒指风格九宫格气泡，箭头指向宠物内容顶部，淡入 0.2s → 显示 3~5s → 淡出 0.3s，不阻挡拖拽。

### F5 — 动画行为逻辑

| 触发 | 动画 | 说明 |
|------|------|------|
| 无事件 | stand（循环） | 空闲 |
| 拖拽 | fly（拖拽中） | 结束后回 stand |
| 空闲太久 | 除 stand 外任意（单次） | 播完回 stand |
| 退出 | die（单次），无则直接退 | — |

### F9 — 右键菜单

| 菜单项 | 说明 |
|--------|------|
| 切换人物 | 一级：NPC / MOB / PLAYER → 二级：可用列表 |
| 切换动画 | 可用动作列表 |
| 切换背景 | 地图列表 |
| 切换气泡 | 气泡样式列表 |
| 切换椅子/骑宠 | 仅 PLAYER 时显示 |
| Hermes 终端 | 仅已配置时显示 |
| 设置中心 | 打开 F8 |
| 退出 | die 或直接退出 |

---

## 4. P1 需求详情

### F6 — 纸娃娃换装系统

**参考**：`MapleSalon2`

换装部位（z-order 分层）：皮肤、发型（前后层）、脸型、帽子、上衣、裤子、鞋子、武器、披风（前后层）、盾牌、手套、耳环。多套预设、坐骑、椅子（与坐骑互斥）、染色。

### F7 — 素材浏览与搜索

三栏布局（分类树 + 列表 + 详情预览），按名称/ID 搜索，按类型筛选，收藏、最近使用。三栏 UI 留到设计阶段确认。

### F8 — 设置面板

**参考**：`prototypes/index.html`

3 主 Tab（桌宠/AI/对话）+ 4 子 Tab（素材/纸娃娃/背景/气泡），修改实时生效。WZ 配置项：wzlib 路径 + BaseWZ 路径。

---

## 5. P2 需求详情

### F10 — 地图背景系统

**参考**：`mapRederWeb/frontend/src/index.ts`

地图 = 地板 + 多个 OBJ。用户可自定义展示已有地图或截取一段。窗口大小固定，不加载整张地图。默认透明。

### F11 — 托盘/状态栏图标

菜单：回归屏幕中心、显示/隐藏、设置中心、退出。

### Hermes AI 集成

- 人格：pet-chat 驱动发言，可切换性格
- 感知：Hermes 状态 → 动画映射（忙/move、出错/hit、完成/skill）
- 对话窗口：纯聊天，不搞复杂 GUI
- 连接状态：查看 WebSocket 连接状态
- 纸娃娃关联：角色随 Hermes 状态联动

---

## 6. 非功能需求

| 编号 | 指标 |
|------|------|
| NF1 | 空闲 CPU < 1%，动画 < 5% |
| NF2 | 空闲 < 50MB，多资源 < 150MB |
| NF3 | 热启动 < 1s，冷启动 < 5s |
| NF4 | 60fps 稳定，最低 30fps |
| NF5 | 崩溃自动重启 |
| NF6 | Windows 10/11 + macOS 14+ |
| NF7 | 缓存命中离线可用 |
| NF8 | 下载即用，零额外依赖 |

---

## 附录：参考项目

| 项目 | 参考内容 |
|------|---------|
| `mapRederWeb/backend/Program.cs` | R2 wzlib DLL 调用、WZ 加载、PNG 提取、origin/delay 解析 |
| `maplestory-wiki-backend/core/wzlib/` | WZ 解析器 Java 移植 |
| `mapRederWeb/frontend/src/index.ts` | PixiJS origin pivot、AnimatedSprite、地图渲染 |
| `MapleSalon2/` | 纸娃娃合成、装备管理、zmap |
| `mapleStoryMiniPet/main`（v0.3） | Swift 逻辑参考，不迁移 |