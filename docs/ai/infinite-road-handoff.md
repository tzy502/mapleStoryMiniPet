# 无限之路（F18）实现交接 —— 给接手开发的 Agent

> 固化时间：2026-09-10　｜　需求原文：`docs/ai/requirements-analysis.md` §9（F18，17 条核心要求）
> 本文只记录**已验证到像素级的实现结论、验收标准、踩过的坑、可复现的素材与工具**。
> 需求原文优先于本文；本文与需求冲突处（如斜坡/台阶/柱）以胶水最新拍板为准，见文末 §7 开放点。

---

## 0. 现状一句话

选砖 → 拍平 → obj 组合搬运 → 三层画序 → 贴地对齐 → 密度/防重叠，**全部定稿并通过像素级验证**；
算法参考实现 = 探针工程（§5），**尚未落进 MiniPet 项目本体**。

---

## 1. 定稿算法

### 1.1 选砖（只搬「真实可走路面」）

1. **红线段判定**：foothold 段中点 ±90×60 内有 tile → 红线（真路面）；否则黄线（爬梯/绳索/隐形判定），忽略。
2. **候选只留 enH0**：路面材质**只有 enH0**。enH1 是悬空平台顶板不是路；bsc（填充）/ edU / edD（收边）/ enV（竖边）全部排除。
3. **顶面坐标比距**（关键坑）：enH0 底对齐（origin.y = 高度）、enH1 顶对齐（origin.y = 0），y 语义不同 → 必须换算 `top = y - origin.y` 后再比距离。
4. **段锚定槽位**：以段找砖（tile 的 x 范围与段 x 相交即候选），**不要**以砖中心找段（段起点 mod 90 有 38 种余数，中心判定漏砖）。
5. 每槽位只取一块：顶面误差最小者。
6. **禁止距离窗口**（±15 / ±60 都试过，全错）：窗口宽了越权选下层，窄了斜坡段两砖间全漏。

### 1.2 直线路三层画序（顺序错即报废）

设 T = 地表草皮绿带顶线画布 y（定稿 **T = 223**）：

1. **bsc 填充墙**先画，**起点 = T**（写成 T+27 会留蓝天缝）。
2. **obj 包**次之。
3. **enH0 表面砖行最后画** —— 盖住 obj 底边产生「插进草地」的踩地感（R2 语义：obj 层 3/4/5 先于地面 tile 层 6 绘制）。

材质池：地图内全部 enH0 变体随机循环，每块 90px 首尾相接（woodMarble / dryRock / brownBrick 宽度全 90）。

### 1.3 表面顶线对齐（关键铁律，2026-09 定稿）

- 表面砖各变体 PNG **高度不一**（废都 26、射手 28~33）。
- **表面砖必须按「可见顶边」（PNG 首个不透明行）统一对齐到 T**；
  按**底边**对齐会让顶线参差（废都实测落在 y=227），而贴地公式写死 223 → 每个物件脚下露 **4px 天蓝缝 = 悬空**。
- **obj 按自身可见底边对齐**：`ddy = T - (visBottom + 1)`（末行不透明像素正好落在 T 上一行）。
- 可见行范围用 `SKBitmap.GetPixel(x, y).Alpha > 8` 预扫并缓存（tile / obj 通用）。

### 1.4 obj 组合搬运（对齐 Java `renderMapObj`）

- **并查集聚组**：obj 框（含 origin 展开）相交或邻近 ≤20px 即同组。
- **超宽包拆分**（对齐 Java `splitWideGroups`）：组宽 >800px 时在**最大间隙**处递归切开（否则整包被丢弃 → 主体全没了）。
- **组内相对位置 + WZ z 原样**，绘制按 z 升序。
- **不筛大小**（用户明确否决过「排除大背景」）。
- **落线用视觉底边**，不要用锚点：`visualBottom = Y - originY + imgH` 对齐 T（锚点 origin.y 不都在图底，锚点对齐会让物件沉进地面 125px）。
- **落位密度**：`px += max(原地图包间距均值, 包宽 + 60)`
  ⚠️ 写成 `包宽 + 间距` 会把密度砍半（实测 18 包只落 6、覆盖率 <50%、肉眼见大段空白）。窄包保持原地图节奏，宽包才拉开防重叠。
- obj 提取用项目解析器 `o.Resource.Frames[0]`（Frame 继承 Sprite，含 ResourceUrl / OriginX / OriginY / Z）；skel 动画 obj 提取失败，跳过即可。

---

## 2. 验收标准（每项都要过，缺一不可）

用像素统计判定（脚本见 §5），**不要用视觉模型判细缝**：

| 验收项 | 判定 |
|---|---|
| 地表顶线平齐 | 非物件列「首个非天空像素 y」众数 = T，其它值只出现在物件列 |
| 物件贴线（不悬空/不深陷） | 物件列「末行 → T−1」间隙 = 0 占绝大多数（余为物件轮廓空隙，非悬空） |
| 组间不重叠 | 相邻物件段最小间隔 = 60px |
| 铺满（无大段空白） | 覆盖率 ≥ 84%，最长空白段 ≤ 60px |
| 不露天空 | 地表线下方无天空色像素（画布边缘留白除外） |
| 画序 | 表面砖最后画，obj 底边被草皮盖住 |

---

## 3. 全部踩过的坑（勿重犯）

1. `SKPaint` 画空心框必须 `Style = SKPaintStyle.Stroke`（默认 Fill 涂实心糊图）。
2. `RenderViewport` 参数顺序：`(MapInfo, camX, camY, zoom, viewTimeMs, screenW, screenH)`（zoom 在 viewTimeMs 前）；整图 = 800×600 分块拼接，越界自动裁。
3. foothold 段字段是 `x1/y1/x2/y2`，每段一个节点，不是点串。
4. `MapService` 构造两参：`new MapService(wz, new CacheManager())`。
5. `tS` 在 `层节点/info/tS`，不是层节点直接属性。
6. `img` 子节点必须读 `img.Node.Nodes`（Wz_Image.Node 属性），外层 `node.Nodes` 恒 0。
7. **斜坡（sl）整个废弃**：游戏渲染斜坡 = 普通贴图零特殊逻辑（全项目无 slLU/slRU 分支），坡感来自地图作者手工交错摆放，无法程序生成 → **无限路 = 纯平直路**。
8. 改动渲染后必须**重跑全部验收点**（本任务 3 次「修 A 坏 B」：改画序把 bsc 起点写坏 → 草皮下悬 27px 蓝天缝；防重叠改法把密度砍半）。
9. 视觉模型（volc / DS）判「细缝、平齐、组间重叠」会**自相矛盾**：同一张图全图说无缝、裁片说有缝；DS 会把「组内前后景叠压」误判成「组间重叠」。**以像素统计为准。**

---

## 4. 素材

- **样张（已导出）**：`~/Desktop/无限路平路素材_v2/` —— 遍历 town=1 地图 1955 张，有 enH0 的导出 3000px 直线样张，**478 张 / 95 组**，按 tS 分目录（`{tS}/{mapId}.png`），附 `分组索引.txt`。
  - 已剔除占位砖组：allblackTile / allInvisibleTile / blackTile / blackTileFly / invisibleTile / PL_ClearTile / platformer / classicBlock（209 张无美术色块，会出「奇怪黑图」）。
  - 大组：fallenLeaves 66、woodMarble 29、syarenInside 22、darkWood 20、grassySoil 18。
- **最新验收样张**：`~/Desktop/103000000_obj包无限路.png`、`~/Desktop/100000000_obj包无限路.png`（废都 / 射手村，4840×560）。

---

## 5. 工具与参考实现

- **算法参考实现（最重要）**：`~/wmsound_probe_backup/Program.cs` —— 探针主程序，完整实现了 §1 全部算法（选砖、聚组、拆分、落位、三层画序、可见边对齐）。移植时以它为准。
- 备份目录：`~/wmsound_probe_backup/`（`probe.csproj` + `Program.cs` + `dsvision.py`）
- 探针运行环境：`/tmp/wmsound`（**会被系统清理**：csproj / Program.cs / bin 全丢，MSB1009 即信号）。重建步骤见 `~/.hermes/profiles/desktop-pet/skills/desktop-pet-development/infinite-road-roadbuild/references/wmsound-probe-session.md`。
- **像素复验脚本**：`~/.hermes/profiles/desktop-pet/skills/desktop-pet-development/infinite-road-roadbuild/scripts/verify-render.py`
  用法：`python3 verify-render.py <图片路径> ...`（输出顶线分布 / 贴线间隙 / 覆盖率 / 空白段）
- 独立视觉复核（可选）：`dsvision.py`（调 DeepSeek 视觉模型；注意它是 reasoning 型，max_tokens 要给到 16000 才出结论，且结论只作参考）。

---

## 6. 建议的落地路径

1. 算法落进 `MiniPet/Services/`（建议独立 `InfiniteRoadService`），**模式3 独立编号，不改动模式1/2 逻辑**（需求 §6）。
2. 复用 `MapService` 的 WZ 读取与精灵缓存链路；顶线对齐逻辑严格按 §1.3。
3. 滚动渲染复用模式2 的 30fps DispatcherTimer + WriteableBitmap 直拷方案（需求 §9）。
4. 音乐联动：播放 → 切入模式3，Stop（不含暂停）→ 隐藏并恢复原模式（需求 §7）；音乐→地图反查走 F17 已有链路（需求 §1/§2 回退链）。
5. 每完成一步按 §2 验收；改动渲染后重跑全部验收项。

---

## 7. 需要与胶水确认的开放点

- **斜坡/台阶/柱**：需求 §10（台阶 jump 联动）、§12（NPC 放柱上）、§14（柱长兜底）均建立在「柱」概念上，而**胶水已拍板：纯平路、「柱」概念作废**。这几条随之作废或改写，动手前先确认。
- 滚动速度：需求开放点 a（默认固定常速，可配置）。
- 默认五图 mapId 须**从 WZ 实证**（需求开放点 b：射手 / 废都 / 勇士 / 密林 / 林中），禁止拍脑袋。
- 模式3 与 F17 场景联动是否互斥（需求开放点 c，默认模式3 下 F17 自动暂停响应）。
- 当前分支 `feature/0.2.1` 有 46 项未提交改动 —— 动手前与胶水确认基线。
