# F18 无限之路 — 算法详细设计

> 版本：v1.0（2026-08-27）
> 需求：`docs/ai/requirements-analysis.md` 第 9 章 F18（16 条）
> 全部数据实证来源：/tmp/wmsound 探针（2026-08-27，射手村 100000000 实测）
> 本文是实现级算法文档：数据结构、状态机、公式、参数表、边界情况全量定义。

## 0. 术语

| 术语 | 定义 |
|------|------|
| **柱（Column）** | 源地图 90px 宽的垂直 tile 堆叠单元。`{x: 世界x, surfaceY: 表面y, tiles: [(u,no,y)], objs: [...], bscPad: bool}` |
| **柱模板（ColumnTemplate）** | 从源图提取的可复用柱（含表面高度差、tile 序列、绑定 obj、可站 NPC 标记） |
| **台阶模板（StepTemplate）** | 源图中真实存在的「相邻柱对/三柱组」，表面高度差 >40px，整体搬运 |
| **路（Road）** | 无限横向柱序列，世界 x 从 0 向右无限延伸 |
| **滚动偏移（scrollX）** | 当前窗口左边缘对应的世界 x，`scrollX += speed × dt` |
| **表面函数 surfaceY(x)** | 路 x 处的地面顶面 y（台阶过渡段内线性插值） |
| **人物锚点** | 人物屏幕 x = 背景窗口水平中心；对应世界 x = scrollX + windowW/2 |

## 1. 总体架构

```
[BGM 播放] → MapResolver（bgm→mapId，含回退链）
                ↓
[源图解析] RoadMaterialExtractor（一次性，后台线程）
     ├─ ColumnTemplate 池（平地柱 + 台阶模板分开索引）
     ├─ 站地 obj 关联到柱
     ├─ NPC 候选表（life type=n）
     └─ back 视差参数表 + 远景 obj 表
                ↓
[运行时] InfiniteRoadGenerator（柱序列状态机）
     → RoadColumn 激活列表（窗口内 + 前方预生成 12 柱）
                ↓
[渲染] InfiniteRoadRenderer（30fps）
     back 视差 → 柱位图缓存 blit → NPC → crossfade 层
                ↓
[人物] PetFollower（x 锁窗口中心，y 下落取地，jump 联动）
```

## 2. 离线准备：RoadMaterialExtractor

### 2.1 源图解析

对源 mapId 解析（复用 MapService 现有解析）：
- 8 层 tile：按层读 `tile/{id}` 的 `{x, y, u, no}`；层 tileset 名从 `info/tS`（如 woodMarble）
- foothold 全层段（不限 L0）：`{l0}/{l1}/{seg}/{x1,y1,x2,y2}`
- life：`type == "n"` 的 NPC `{id, x, y}`
- obj：按 obj 的世界 y 与地面关系分类（见 2.4）

### 2.2 柱提取算法

```
输入：L0 层（地面层）全部 tile
1. 按 tile.x 分组（tile.x 是柱中心；射手村实测柱间距 90px，全库 401 图采样 90px 间距无一例外）
2. 每柱：
   a. tiles 按 y 升序 = 从表面到底部的堆叠序列
   b. surfaceY = min(tiles.y)（最上面一块的 y）
   c. 记录每块的 {u, no, 相对柱surfaceY的dy}
   d. 判定底块类别：若柱底块是填充类(bsc系) → bscPad = true（可续堆）
   e. ⚠️ 斜坡柱判定（审查1轮 P0 修复）：柱内含 slLU/slRU/slLD/slRD 块 → slant = (dyLeft, dyRight)
      （斜坡块左右缘高度不同；slXU 上行坡 / slXD 下行坡；surfaceY 在柱内是斜线，见 §3.4）
3. 柱有效性过滤：
   - tiles.Count >= 2（至少表面块+一块填充）
   - surfaceY 已知
   - 柱宽完整（x 与相邻柱差 90±2px）
   - 斜坡柱单独成类（SlantColumn），不与平地柱混抽——它是天然的平滑过渡素材
```

**实测参考（射手村 L0）**：主走廊 928px ≈ 10 根柱、全图 180 tile / 85 在走廊内 / 27 个不同柱 x。

### 2.3 台阶模板提取

```
1. 对相邻柱对 (A,B)：|B.surfaceY - A.surfaceY| ∈ (40, 200] px 且 x 差 = 90px
   → 两柱模板捆绑为 StepTemplate(dir=up|down)
2. 三柱组（连续两步同向）也收：StepTemplate(len=3)
3. 实测参考：射手村 L0 高差走廊 64→124→184（60px 台阶）大量存在
```

### 2.4 obj 三分类（绑定规则，审查1轮 P0 修复后）

实测射手村：|dy|≤60 站地 43 个；60<dy≤250 装饰带 80+ 个（大量树/房子在此带，旧规则会丢 72% 装饰）。

| 类 | 判定 | 去向 |
|----|------|------|
| 站地 obj | `|obj.y - surfaceY(obj.x)| ≤ 60px` | 绑定到该柱，记 `{相对柱x的dx, 相对surfaceY的dy, oS/l0/l1/l2}` |
| 装饰带 obj | `60 < dy ≤ 250px` 且 obj 水平落在柱 x±45px 内 | **也绑定到柱**（y 锚定 = 柱表面上方 dy 处——树冠高于地面是正常美术） |
| 后景 obj | `dy < -200px` 且 obj 宽 > 300px | 远景表（视差带） |
| 其他（悬空/天空/远处） | 不满足上述 | **不抄** |

⚠️ `.skel` 骨骼动画 obj（如 2025MysticBloom 系）：取 idle 首帧静态渲染（MapService 已有支持）。

### 2.5 NPC 候选表

源图 life `type=n` 的 NPC：`{id, 相对柱surfaceY的dy}`（NPC 的 y 与地面差 <60px 才收，实测 NPC 都站地上）。提取 NPC 动画帧走现有 NpcService/SpriteService 链路。

## 3. 运行时：InfiniteRoadGenerator（柱状态机）

### 3.1 状态

```csharp
class RoadGenState {
    double scrollX;                  // 窗口左缘世界x
    int nextWorldX;                  // 下一柱的世界 x（= 已生成柱右缘）
    int currentSurfaceY;             // 当前柱表面 y
    int runDir;                      // 高度趋势计数：+连升 / -连降 / 0
    Queue<ColumnTemplate> recent;    // 最近 8 根已用柱（防重复）
    Random rng;                      // 种子可配（复现调试）
}
```

### 3.2 初始填充

```
currentSurfaceY = 源图主走廊 surfaceY（取柱池中出现频率最高的 surfaceY）
从 nextWorldX=0 起，抽 6~10 根「平地柱」（surfaceY == currentSurfaceY 容差 ±4px）铺满窗口 + 前方
```

### 3.3 抽柱算法（每次路推进到需要新柱时）

```
priority: 台阶 > 平地
1. 若 recent 中柱数 < 3（路刚开始）→ 强制平地柱
2. 决策是否上台阶：
   p(step) = 0.12（每柱 12% 概率），且：
   - 若 currentSurfaceY 距屏幕中带下界 < 120px → 禁止 up 台阶
   - 若距上界 < 120px → 禁止 down 台阶
   - |runDir| >= 3 → 强制反向（游程限制）
3. 平地柱抽取：
   candidates = 柱池中 |surfaceY - currentSurfaceY| ≤ 4px 的柱
   candidates -= recent 中的柱（防重复惩罚）
   若 candidates 空 → 放宽（去掉 recent 惩罚）；再空 → 用 bsc 平地柱
   加权随机：绑定 obj 多的柱权重 ×1.5（有装饰更好看）
4. 台阶抽取：
   candidates = StepTemplate 池中 dir 匹配、起点 surfaceY == currentSurfaceY 容差 ±4px
   随机一个 → 整组柱按序入路，currentSurfaceY 更新为终点表面，runDir 对应 ±len
5. 入路后：nextWorldX += 90 × 柱数；recent 入队（超 8 出队）
```

### 3.4 surfaceY(x) 表面函数

```
柱 i 覆盖 [xi, xi+90)：
- 平地柱：surfaceY = 柱 surfaceY
- 斜坡柱（slant = dyLeft→dyRight）：柱内线性插值
  surfaceY(x) = lerp(surfaceY+dyLeft, surfaceY+dyRight, (x-xi)/90)
- 台阶模板处：台阶柱整柱高变，人物脚 y 过渡带 = 台阶柱内前 30px 线性插值：
  surfaceY(x) = lerp(yA, yB, (x - xStep)/30)，clamp 到 [0,1]
  （视觉上台阶柱自带 enV/edU 边块，插值只服务人物脚 y，不影响绘制）
- 相邻平地柱表面同高（|Δ| ≤ 4px）→ 直接取左柱值（平段）
```

### 3.5 回收

`scrollX - 柱右缘 > 200px` → 柱出激活列表，位图缓存保留（LRU 128 张）。

## 4. 渲染管线（InfiniteRoadRenderer）

### 4.1 柱位图缓存（关键性能设计，审查1轮 P1 修复）

每根**激活柱**预渲染成一张 SKBitmap（**固定源分辨率 90×900，与窗口高无关**——blit 时按需缩放绘制，窗口缩放不再导致缓存失效）：
```
renderColumn(col) → bitmap(90×900)：
  1. 依模板 tiles 从 surfaceY 向下画（复用 SpriteService 精灵缓存，tile 纹理路径 Map/Tile/{tS}.img/{u}/{no}）
  2. 柱底不足位图底 → bsc 块循环堆到位图底（bscPad=true 时）
  3. 绑定 obj 画到 (45+dx, surfaceY+dy)（obj 在柱上层）
  缓存键 = 模板id + surfaceY（不含窗口尺寸）
```
滚动时每帧只做 `DrawBitmap`（blit），不重画 tile → 30fps 无压力。
内存预算（实测核算）：单柱位图 90×900×4B ≈ 0.32MB，LRU128 ≈ 41MB 上限，正常滚动仅活跃 ~20 张 ≈ 6.5MB。

### 4.2 层序（对齐现有 MapService.RenderViewport 顺序）

```
1. back 视差层（复用模式1/2 的 back 渲染：rx/ry/cx/cy，tileMode 平铺；
   滚动注入：相机 x = scrollX + windowW/2 —— 现有公式直接吃）
2. 远景 obj 视差带（慢速 0.5×）
3. 柱位图序列（按世界 x - scrollX 定位 blit）
4. NPC（见 §5）
5. crossfade 混合层（见 §6）
不渲染：portal（F18-11）、mob、悬空 obj
```

### 4.3 窗口大小可变 + 人物锁定（胶水 2026-08-27）

- 背景窗口 **CanResize**（同模式2 摄像机窗口交互：拖边框改大小）
- 人物屏幕 x **锁定背景窗口水平中心**：`petScreenX = bgWin.Position.X + bgWin.Width/2 - petW/2`
- 窗口移动 → 人物跟随（保持相对中心）；窗口缩放 → 人物重新居中 + surfaceY 按新中心世界 x 重算
- 人物 z 序：背景窗口之上（现有 RaisePet 机制不变）

### 4.4 滚动模型

```
speed = 60 px/s（固定常速，F18 开放点 a 已拍板；可配置项）
scrollX += speed × dt（暂停时 dt=0 → 路停，F18-7）
每帧推进后：检查激活列表右缘 < scrollX + windowW + 12×90 → 触发 §3.3 补柱
```

## 5. 路上实体：NPC 与怪物（F18-12/13）

### 5.1 共通生成机制

```
每次新柱入路时：
  p(npc) = 1/10 + 冷却 ≥6 柱；同屏 NPC < 3
  p(mob) = 1/6  + 冷却 ≥8 柱；同屏 mob < 2
  约束：该柱无实体/obj 冲突（柱 x±60px 内无站地 obj 或其他实体）
实体数据 = {类型, id, 柱世界x, y=柱表面+dy, 状态机}
生成位置偏向屏幕右缘外 1~2 柱（从右侧走入视野，符合「路把怪送来」的方向感）
柱滚出屏幕左缘 → 实体随柱销毁（未触发战斗的怪也算「走过去了」）
```

### 5.2 NPC 行为

站柱 idle 动画帧循环，无状态机，随柱滚出销毁。

### 5.3 怪物战斗状态机（F18-13，单向战斗）

```
[spawn] 站柱播放 move/stand 动画（随路滚动接近人物）
   │ 路滚动带动：mob 世界 x 固定，人物世界 x = scrollX+windowW/2 递增
   ▼
[approach] 怪物世界 x 距人物世界 x ≤ ATTACK_RANGE(70px) 时触发：
   1. 冻结 scrollX？——否：不停路，怪物进入受击流程由动画时长驱动
   2. 纸娃娃强制播攻击动作（swingO1/swingT1/stabO1 按武器类型选，AnimService OneShot）
      ⚠️ 攻击期间纸娃娃合成链保持武器层渲染（禁止裸 body）——PaperdollService 合成输出不因动作切换丢装备层
   3. 攻击动作播放到命中帧（约 50% 处）→ 怪物播 hit1（OneShot ~300ms）
   ▼
[dying] hit1 播完 → 播 die1（OneShot，播完停最后帧）
   │ die1 结束 → 500ms 停帧展示 → 300ms alpha 淡出 → 销毁，路继续（从未停）
   ▼
[dead] 释放实体；冷却计数重置（下一只怪 ≥8 柱后才会出现）
```

**战斗数值（初版）**：ATTACK_RANGE=70px；攻击动作按当前武器选（剑→swingO1/斧→swingT1/枪→stabO1，无武器→swingO1 拳系）；hit1/die1 时长按 WZ 帧数据实读；怪物不还手、桌宠无敌。

### 5.4 动作映射（探针实证）

| 实体 | 动作 | WZ 实证 |
|------|------|--------|
| mob | move/stand/hit1/die1 | 9300000/9300001 全有（hit1/die1 通用） |
| 纸娃娃 | swingO1~3/swingT1~3/swingP1~2/stabO1 系 | body 00002000 全有 |

## 6. 地图切换 crossfade（F18-16）

```
触发：换歌 → MapResolver 解析结果 ≠ 当前源图
流程：
  1. 新源图后台 RoadMaterialExtractor（柱池构建 ~百ms 级，后台线程）
  2. 构建完成 → 双缓冲：旧路面继续渲染，新路面渲染到离屏位图
  3. 500ms 内 alpha 0→1 混合（SkiaSharp DrawBitmap 带 GlobalAlpha，30fps = 15 帧插值）
  4. 完成后旧路释放，人物 surfaceY 无缝接新路（按当前世界 x 重算）
首次进入模式3（无旧路可淡出）：新背景直接 opacity 0→1（500ms）后开始滚动（审查1轮 P1 补）
退出模式3：opacity 1→0 后隐藏窗口、恢复播放前背景模式
```

## 7. 人物落地（PetFollower）

```
每帧（30fps）：
  petWorldX = scrollX + windowW / 2
  groundY  = surfaceY(petWorldX)          // §3.4 表面函数
  dy       = groundY - pet.feetY

  if (pet.jumping) {                      // 跳跃中不贴地
      pet.jumpT += dt; 抛物线 y = lerp(fromY, toY, jumpT/JUMP_MS)
      if jumpT >= JUMP_MS → 跳跃结束，贴地
  } else if (|dy| <= 40) {                // 平地/小起伏：直接贴
      pet.feetY = groundY
  } else {                                // 台阶：强制 jump（F18-10）
      触发 jump 动作（AnimService jump），JUMP_MS = 300ms
      fromY = pet.feetY; toY = groundY
  }
  兜底：surfaceY 返回 null（异常）→ 保持上一帧 feetY，绝不回退窗口底（F18 硬约束）
```

> 现有 GetGroundY 的 L0-only bug（探针实证城镇图街道地面在 L3）不影响本算法——模式3 的地面是**我们自己生成的柱表面**，不查 foothold。该 bug 的修复属 V0.2.1 另行任务。

## 8. MapResolver（BGM → 地图绑定）

```
输入：当前曲 MusicTrackRef(Img="Bgm00.img", Track="FloralLife")
1. bgmKey = "Bgm00/FloralLife"（Img 去 .img 后缀 + "/" + Track）
2. 查 bgmIndex[bgmKey]（分层懒建，审查1轮 P1 修复）：
   a. 首次查询：先扫 town=1 子集（实测 1955 图 ≈ 3s，覆盖绝大多数 BGM 命中）
   b. 未命中 → 后台扩全库扫（实测 21254 图 ≈ 30s，进度显示在音乐 tab「索引构建中」）
   c. 结果缓存；切歌查同一索引不重扫
3. 命中多图（F18-15）：优先 town=1 → 随机一张；无 town → 随机一张；不混池
4. 未命中（F18-2）：沿用上一首源图；第一首就未命中 → 五城镇随机（100000000/
   101000000/102000000/103000000/105000000，town=1 已终验）
5. 解析失败/柱池空（F18-8）：模式3 不可用提示，音乐继续
```

## 9. 参数总表（全部常量集中处，实现时放 RoadConstants）

| 常量 | 值 | 说明 |
|------|-----|------|
| COL_W | 90 px | 柱宽（实测射手村） |
| SCROLL_SPEED | 60 px/s | 滚动速度（固定，可配置） |
| STEP_THRESH | 40 px | 台阶判定阈值（>此值触发 jump） |
| STEP_MIN/MAX | 40/200 px | 台阶模板高度差范围 |
| P_STEP | 0.12 | 每柱上台阶概率 |
| RUN_LIMIT | 3 | 连续同向台阶游程上限 |
| BAND_MARGIN | 120 px | 屏幕中带上下保护边距 |
| FLAT_TOL | 4 px | 平地柱表面容差 |
| RECENT_N | 8 | 防重复队列长度 |
| PREGEN_COLS | 12 | 前方预生成柱数 |
| OBJ_GROUND_TOL | 60 px | obj 站地判定容差 |
| OBJ_FAR_THRESH | 200 px | 后景 obj 距地面阈值 |
| NPC_P | 1/10 | 每柱 NPC 概率 |
| NPC_MAX_SCREEN | 3 | 同屏 NPC 上限 |
| NPC_COOLDOWN | 6 柱 | NPC 生成冷却 |
| MOB_P | 1/6 | 每柱怪物概率 |
| MOB_MAX_SCREEN | 2 | 同屏怪物上限 |
| MOB_COOLDOWN | 8 柱 | 怪物生成冷却 |
| ATTACK_RANGE | 70 px | 攻击触发距离（怪 x - 人物 x） |
| HIT_FRAME_RATIO | 0.5 | 攻击动作命中帧位置（50% 处触发 hit1） |
| DIE_HOLD_MS | 500 ms | die1 播完停帧展示时长 |
| DIE_FADE_MS | 300 ms | 死亡淡出时长 |
| JUMP_MS | 300 ms | 台阶跳跃时长 |
| CROSSFADE_MS | 500 ms | 地图切换淡入淡出 |
| SURF_LERP_W | 30 px | 台阶表面插值带宽（仅影响脚 y） |
| BSC_CACHE_MAX | 128 | 柱位图 LRU 上限 |
| TOWN_FALLBACK | 100000000/101000000/102000000/103000000/105000000 | 五城镇 |

## 10. 边界与降级汇总

| 场景 | 行为 |
|------|------|
| 源图柱池 < 5 根有效柱 | 模式3 不可用，气泡提示（音乐继续） |
| 抽柱 candidates 空 | 逐级放宽：去 recent 惩罚 → bsc 平地柱 → 复制上一柱 |
| surfaceY(x) 无覆盖（理论不该发生） | 人物保持上一帧 y；渲染不中断 |
| 新源图后台构建中用户又切歌 | 取消旧构建（CancellationToken），以最新为准 |
| 窗口缩到极窄（<3 柱宽） | 仍正常（预生成保证）；人物始终居中 |
| 暂停音乐 | scrollX 冻结，路/NPC/人物全部静止（F18-7） |

## 11. 实现拆分建议（给 Claude Code 的模块边界，避免 god class）

| 类 | 职责 | 单一关注点 |
|----|------|-----------|
| RoadMaterialExtractor | 源图 → 柱池/台阶/斜坡/obj/NPC+mob/back 表 | 只做离线提取 |
| InfiniteRoadGenerator | 柱状态机 + surfaceY | 只管生成 |
| InfiniteRoadRenderer | 柱位图缓存 + 层序渲染 + crossfade | 只管画 |
| PetFollower | 人物 x 锁定/y 贴地/jump | 只管人物 |
| RoadCombatDirector | 实体生成（NPC/mob）+ 怪物战斗状态机（approach→hit1→die1→淡出）+ 纸娃娃攻击动作触发 | 只管战斗编排 |
| RoadMapResolver | bgm→map + 回退 + 索引 | 只管绑定 |
| RoadConstants | §9 参数表 | 常量集中 |

窗口接入：新模式3 枚举 + BackgroundWindow 模式3 渲染源切换（或独立 InfiniteRoadWindow，软设计阶段定）。
