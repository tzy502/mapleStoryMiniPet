# 需求文档 — MapleStory MiniPet v2

## 版本目标

从「静态精灵图桌宠」升级为「后端驱动的多怪物可切换桌宠」。

---

## 核心需求

### 1. 后端精灵图获取

**现状**：精灵图预生成在 `sprites/` 目录，固定 8880150。

**目标**：通过 wiki-backend API 拉取 WzComparerR2 导出的精灵图数据。

```
MiniPet ──HTTP──▶ wiki-backend (:9334)
                      │
                      ├── 解析 .wz 文件 (WzComparerR2)
                      ├── 导出动画帧 (PNG)
                      ├── 计算 origin（参考 GifUtil.wzImgDetailToGif）
                      ├── 拼接地锚精灵图条
                      └── 返回 {sprites, config}
```

**API 设计文档**：
/Volumes/docker/hermes/download/api-docs/
获取xml接口/api/wz/xml
**缓存策略**：
- 首次拉取后缓存到本地 `~/Library/Caches/MiniPet/{mobId}/`
- 缓存命中直接使用，过期或缺失时重新拉取

---

### 2. 怪物切换

用户可切换不同冒险岛怪物作为桌宠。

**交互**：
- 右键菜单 → 「切换怪物」→ 列表选择
- 或命令行：`./start.sh --mob 9602538`

**预加载**：
- 切换时异步拉取精灵图，显示 loading 状态
- 拉取完成前保持当前怪物

**初始怪物列表**：

根据WZ内查询的数据

---

### 3. 动画播放逻辑

**默认动画**：`stand`

**动画选择策略**：

```
if (空闲超过 5 秒)         → stand
if (Hermes 有活动)          → stand（attack 开头 skill 开头技能）
if (用户拖拽窗口)           → move（有） / fly（无 move 时）
if (随机触发，每 15~30 秒)  → attack 开头 skill 开头技能）1（随机选一个）
```

**无 move 动画时的回退**：

```
优先级: move → fly → stand
```

如果没有 `fly` 也没有 `move`，拖拽时保持 `stand`。

**随机攻击/技能**：

每隔 15~30 秒（随机间隔），从以下候选池随机选一个播放一轮后回到 stand：

```
候选池: 所有 attack{N} + skill{N}（如 attack1, skill1, skill2, skill3...）
```

播放规则：
- 播放完整一轮（所有帧播放一次）
- 播完后回到 `stand`
- 播放期间拖拽不受影响（拖拽结束后继续当前随机动画）

---

### 4. Origin 对齐（关键）

**问题回顾**：原 bbox 对齐导致帧间锚点漂移 → 动画抖动。

**正确逻辑**（参考 `GifUtil.wzImgDetailToGif`）：



**后端实现要求**：

- `WzImgDetail` 模型已含 `originX/originY/l/t/r/b`
- 精灵图生成时逐帧应用 origin 偏移
- 前端（MiniPet）无需关心 origin，直接使用生成好的精灵图条

**验证方法**：
- 生成后检查每帧：角色「脚部」在同一像素行
- 对比原版 bbox 精灵图：应无可见抖动

---

### 5. 精灵图规范

| 字段 | 说明                      |
|------|-------------------------|
| 格式 | PNG RGBA 水平条带           |
| 单帧尺寸 | `frameW × frameH`（统一）   |
| 总宽度 | `frameW × frameCount`   |
| origin 对齐 | WZ origin（唯一） |

`pet_config.json` 格式：

```json
{
  "mobId": "8880150",
  "name": "路西德",
  "version": 2,
  "animations": {
    "stand":  { "file": "stand.png",  "frames": 16, "frameW": 312, "frameH": 355 },
    "move":   { "file": "move.png",   "frames": 16, "frameW": 312, "frameH": 356 },
    "attack1":{ "file": "attack1.png","frames": 16, "frameW": 679, "frameH": 423 },
    "skill1": { "file": "skill1.png", "frames": 32, "frameW": 887, "frameH": 898 }
  }
}
```

---

## 技术约束

- macOS 13+, Swift 5.9+
- 精灵图生成：Java（wiki-backend 侧）/ Python（本地工具）
- 透明无边框 NSPanel
- 网络请求：URLSession（async/await）
- 离线可用：缓存精灵图

---

## 实现优先级

| 优先级 | 功能 |
|--------|------|
| P0 | Origin 对齐（后端 + 精灵图重生成） |
| P0 | 动画回退逻辑（move→fly→stand） |
| P1 | 后端 API：精灵图拉取 |
| P1 | 本地缓存 |
| P2 | 怪物切换 UI |
| P2 | 随机 attack/skill |
| P3 | 动态怪物列表（从 wiki-backend） |

---

## 参考

- `GifUtil.wzImgDetailToGif` — origin 计算标准实现
- `WzGifHandler.changeToDetail` — WZ 帧→WzImgDetail 转换
- `regenerate_sprites.py --method wz` — 本地 WZ origin 精灵图生成
