# MiniPet — V0.2.1 F18 无限之路背景开发

> 提示语版本：claude-prompt-20260827-02
> 日期：2026-08-27
> 分支：feature/0.2.1（从 feature/0.2.0 拉）
> 需求：`docs/ai/requirements-analysis.md` 第 9 章 F18（16 条）
> **算法（实现级，必须通读）：`docs/ai/algorithm-infinite-road.md`（已过审查修复 2 P0 + 4 P1，审查记录 `docs/ai/algorithm-review-round1.md`）**
> 胶水约定：数据一切从 WZ，禁数据库/硬编码路径；避免 God Class；禁止加码

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 主项目：`MiniPet/MiniPet.csproj`
- dotnet 是**可执行文件**（`#` 是 shell 特殊字符，必须引号）：
  ```bash
  export DOTNET_ROOT="/Volumes/SSD/C#"
  "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug
  ```
- 编译必须 0 错误才交付

## 一句话任务

播放音乐时，背景窗口切「模式3 无限之路」：按 BGM 对应地图的素材，柱级随机拼接无限向右的路，桌宠锁定窗口中心沿路走（台阶强制 jump），NPC 与怪物随机出现——怪物到面前时纸娃娃播攻击动作（武器不消失）砍死它（hit1→die1→淡出），换图淡入淡出；停止播放恢复原背景。

## 模块拆分（6 个新类，各司其职，禁止 god class）

| 类（Services/Road/ 子目录） | 职责 | 算法文档 |
|----|------|---------|
| `RoadMaterialExtractor` | 源图 → 柱池/台阶/斜坡/obj/NPC+mob/back 表（一次性后台） | §2 |
| `InfiniteRoadGenerator` | 柱状态机 + surfaceY(x) | §3 |
| `InfiniteRoadRenderer` | 柱位图缓存（90×900 固定分辨率）+ 层序 + crossfade | §4/§6 |
| `PetFollower` | 人物 x 锁窗口中心 / y 下落取地 / jump 联动 | §7 |
| `RoadCombatDirector` | NPC/怪物生成 + 战斗状态机（approach→hit1→die1→淡出）+ 纸娃娃攻击触发 | §5 |
| `RoadMapResolver` | bgm→mapId 绑定 + 五城镇回退 + 分层懒建索引 | §8 |
| `RoadConstants` | §9 参数总表常量 | §9 |

窗口：模式3 枚举值加入现有 MapMode；背景窗口接入（BackgroundWindow 模式3 渲染源切换或独立窗口，按现有代码结构择优，设计上不动模式1/2 逻辑）。

## 探针实证的硬事实（照抄，禁止偏离）

1. **柱模型**：tile.x = 柱中心，柱距 90px（全库 401 图采样无一例外）；每柱垂直堆叠 2~14 块；射手村 L0 = 180 tile / 27 柱
2. **tileset 类别**：bsc（填充）/enH0/enH1（横边）/enV0/enV1（竖边）/edU/edD（角）/**slLU/slRU/slLD/slRD（斜坡——必须支持，§3.4 斜线插值）**
3. **柱位图固定 90×900**（与窗口高无关），blit 时缩放；缓存键 = 模板id+surfaceY
4. **obj 分类阈值**（实测修正版）：|dy|≤60 站地跟柱；60<dy≤250 且水平在柱±45px 内装饰带也跟柱；dy<-200 且宽>300 后景视差；其余不抄。.skel obj 取 idle 首帧
5. **BGM 索引分层懒建**：先扫 town=1（1955 图 ≈3s）→ 未命中扩全库（21254 图 ≈30s 后台+进度提示）
6. **五城镇回退**（town=1 终验）：100000000 射手村 / 101000000 魔法密林（bgm=Bgm02/WhenTheMorningComes）/ 102000000 勇士部落 / 103000000 废弃都市 / 105000000 林中之城
7. **攻击动作实证**：纸娃娃 body（00002000）有 swingO1~3/swingT1~3/swingP1~2/stabO1 系攻击动作；mob（9300000 系）有 hit1/die1；AnimService 已有 hit/die 动作定义（die 播完停最后帧）
8. **战斗数值**（算法 §5.3/§9）：ATTACK_RANGE=70px；攻击动作按武器类型选（剑 swingO1/斧 swingT1/枪 stabO1/无武器 swingO1）；命中帧=攻击动作 50% 处触发 hit1；die1 停帧 500ms + 淡出 300ms；怪不还手桌宠无敌
9. **⚠️ 武器不消失红线**：纸娃娃攻击动作期间合成链必须保持武器层渲染（F18-13 明文），禁止切动作时换裸 body
10. **WZ 路径**：tile=`Map/Tile/{tS}.img/{u}/{no}`（tS 从层 info/tS 取，带 .img）；obj=`Map/Obj/{oS}.img/{l0}/{l1}/{l2}`；back=`Back/{bS}.img/{type}/{no}`
11. **已知现存 bug（本任务不修）**：GetGroundY 的 L0-only 逻辑在城镇图有误（街道地面在 L3）——模式3 不查 foothold，用自己的柱表面，不受影响
12. **探针 ExtractPng 对 tile 返回 1×1 是探针环境问题**，实现走 SpriteService 正常解码，别被误导

## 开发任务顺序（依赖序）

1. **RoadConstants + 模型**：§9 全部常量 + Column/ColumnTemplate/StepTemplate/SlantColumn/NpcEntry 数据结构
2. **RoadMaterialExtractor**：§2 柱提取（含斜坡判定 2.2e）/台阶模板/obj 分类（2.4 修正版）/NPC 候选/back 表
3. **RoadMapResolver**：§8 分层懒建索引 + 五城镇回退链 + 换歌解析
4. **InfiniteRoadGenerator**：§3 状态机（初始填充/抽柱/游程限制/防重复/surfaceY 三种情况）
5. **InfiniteRoadRenderer**：§4 柱位图缓存 + 层序（back→远obj→柱→NPC→crossfade；**不渲染 portal/mob**）+ §6 crossfade
6. **PetFollower + 窗口接入**：§4.3 窗口 CanResize + 人物锁中心；§7 落地（40px 阈值 / 300ms jump / 兜底保上一帧）
7. **模式切换接线**：播放开始→模式3 淡入；暂停→路停；停止→淡出恢复原背景；换歌→crossfade

## 验收（黑盒，逐条过）

1. 编译 0 错误
2. 音乐 tab 播放 SleepyWood → 背景淡入，路从左向右滚（60px/s），视差远景生效
3. 路：地面连续无悬浮断裂；平地段 ≥4 柱；偶发台阶/斜坡自然衔接
4. 台阶处桌宠播 jump 动作跳过，落地恢复；平地脚始终贴地
5. 背景窗口拖边框缩放 → 人物保持水平居中，路正常，无卡顿爆点
6. **NPC 偶现（站地上播 idle），随路滚出消失；无传送门**
7. **怪物战斗全链路**：怪随路接近 → 距人物 70px 内纸娃娃播攻击动作（**武器全程可见不消失**）→ 怪播 hit1 → die1（停最后帧）→ 500ms 后淡出 → 路继续未停；怪不还手
8. 换歌（如 WhenTheMorningComes）→ 0.5s 淡入淡出切换到密林素材风格
9. 播放查无对应图的曲子 → 沿用上一首素材；首曲即未命中 → 五城镇风格随机
10. 停止播放 → 背景淡出，恢复播放前背景模式；暂停 → 画面静止
11. 内存：连续播放 10 分钟无持续增长（柱位图 LRU 回收正常）

## 任务边界（禁止加码）

- 算法文档 §1-§11 就是全部范围；柱池混抽增强、播放列表等一律不做（mob 战斗已在 §5 内）
- GetGroundY 现存 bug 不在本任务修（另列 V0.2.1 任务）
- P2 项（tex 1×1 说明、NPC 参数微调）已折算进文档，无遗留
