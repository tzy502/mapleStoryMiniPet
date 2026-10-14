# V0.2.1 开发进度交接（2026-09-14）

> 本文件由本轮 harness 整理，供**下一个 harness 接手**。全部内容来自实际代码与实跑验证，未验证的会明确标注。
> 分支：`feature/0.2.1`（HEAD = `a344c66`，**本文件所有改动尚未提交**）

## 0. 一句话状态

F18「行走模式」已跑通：**播放音乐 → 选图 → 图搜索规划路径 → 行走/爬绳/跳跃 → 走到传送门淡入淡出切图**；没有可传送的门时走「半程折返」。
**两个 P0 未解决**：① 曲目→地图反向索引首次扫描太慢，导致**地图总兜底到默认城镇**；② 人物**频繁瞬移**（折返阈值/时机）与绳下徘徊。

---

## 0.5 当前需求（胶水 2026-09-14 口述）——**接手先读这一节**

### ⚠️ 与 `requirements-analysis.md` §9 的关系

- 仓库里 `docs/ai/requirements-analysis.md` §9 的 F18 = **「无限之路」**（算法把路段切片合成一条无限向右延伸的路 + 台阶 jump + NPC/怪物随机 + 单向战斗，17 条）。
- **该方案已被胶水废弃**（原话：「算法全部重新设计 这个方案继续抛弃」「放弃 直接把 bgm 相关的地图拼接吧」）。
- **现在要的是下面这套「沿真实地图行走 + 传送门切图」**，不要再按 §9 的 17 条去实现切片/合成。

### 需求原文（逐条）

1. **播放音乐时行走**：人物**从上往下**或**从左往右**行走；方向由「模拟行走的长度」决定——哪边更长走哪边。
2. **走到传送门 → 淡入淡出切换地图**。
3. **没有下一个可传送的传送门时**（定义：① 全图只有一个传送门；或 ② 除入口传送门外，唯一的那个传送门的目标地图 **BGM 与当前 BGM 不一致**）→ 沿当前 foothold 走，**走到尽头的一半**时**回归初始位置**，然后从**一开始的反方向**走。
4. **歌曲播完** → 人物**立即停止** → 淡入淡出切换**下一张地图**。
5. **先默认只有主宠有**（行走模式下其他宠物全部隐藏）。
6. 后续修正（同日陆续提出，均已列入 §2 待办或已修）：
   - 人物朝向要与行进方向一致（曾完全相反）
   - **人物位置应直接绑定当前的移动位置**
   - **脚要踩在 foothold 线上**
   - **obj 排序：先 z，z 内部再 id**
   - **走斜坡是「移动」不是「挑（跳）」**
   - 跳跃时挂到绳子上（异常）
   - **播放哪首音乐就走那首歌对应的地图**（不是默认主城）
   - **播放音乐时不应该强焦点**
   - **人物的 z 和脚下的 tile 同级，且同级最上面**

### 已确认的默认值（胶水拍板）

| # | 项 | 取值 |
|---|---|---|
| ① | 「模拟行走的长度」口径 | **路径累计长度**（含跳跃/爬绳） |
| ② | 半程折返方式 | **瞬移回起点**再反向（后来说过可能改成「掉头走回」，未定） |
| ③ | 淡入淡出时长 | **0.5s**（250ms 淡出 + 250ms 淡入） |
| ④ | 行走速度 | **sdlMS 物理**（顶速 125px/s，含加速 1400 / 摩擦 800） |

### 现在要做什么（交接任务清单，按优先级）

**P0（不修就没法用）**

1. **反向索引首次扫描太慢 → 地图总兜底**
   - 现象：播任何歌都走默认城镇（胶水看到的是勇士部落）
   - 必做：**先实测一次完整扫描耗时**（胶水原话质疑：「耗时以分钟计 这个为什么」「缓存到本地」「一首歌都没有分钟」）
   - 方向：只读 `info` 避免整图解压 / 并行分批 + 进度 / 或放弃全量索引改启发式选图
   - 验收：无缓存启动 + 扫描完成后，`Bgm00/FloralLife` 应落到它真实关联的地图
2. **人物频繁瞬移**（`/tmp/mp-walk.log` 有样本，见 §2.2）
   - 现象：每约 5 秒瞬移回起点一次；胶水：「任务一直在瞬移」
   - 排查：`WalkModeService.GraphHalfLen / HalfLegLength / PathCost`、`SetupLeg` 里 `_halfLen`
3. **绳下徘徊 / 跳跃挂到绳子上**
   - 上绳判定两条路（有规划路径时只上指定绳；无路径兜底走近就上，±15px）
   - 需要时让胶水给地图+位置，或直接收窄兜底上绳条件

**P1（体验/正确性）**

4. **人物的 z 与脚下 tile 同级且同级最上**：现在宠物是独立窗口（天然压住地图但**不会被前景 obj 遮挡**）；若要做成「人物并入地图渲染管线」→ 属于后续
5. **折返方式**：确认保持「瞬移回起点」还是改「掉头走回」
6. **淡入淡出视觉确认**：`WalkTransition` 用的是 `Window.Opacity`（Avalonia 合成树 alpha），macOS 上是否明显**未经胶水确认**

**P2（需求 §9 里仍然有效的部分，若还要做无限之路再说）**

7. 台阶 jump 联动（>40px 强制 jump）、传送门不渲染、NPC/怪物随机 + 单向战斗、柱长兜底、obj 三分类、一 BGM 多地图（不混池）、地图切换淡入淡出——**这些属于被废弃的合成方案，除非胶水重新启用**

---

## 1. 本轮已完成

### 1.1 F18 行走模式（全新，模式 3）

新增文件：

| 文件 | 作用 |
|---|---|
| `MiniPet/Services/Walk/MapWalker.cs` | 单图行走：沿 foothold `prev/next` 走、段内贴线（`YOn` 插值）、贴边停、绳梯进出（±15px 吸附、100px/s）、`Trace` 几何干跑 |
| `MiniPet/Services/Walk/WalkPathPlanner.cs` | 选方向（累计长度更长的一边）、选传送门（`tm` 有效 + 目标地图 BGM == 当前曲目）、`HasNoNextPortal`、`TrackKeyOf` |
| `MiniPet/Services/Walk/WalkGraph.cs` | 图搜索：节点=foothold 段，边=相邻/绳梯/跳跃/下落，Dijkstra `Plan`、`Reachable`、`Farthest` |
| `MiniPet/Services/Walk/WalkModeService.cs` | 状态机：`Start/Tick/Stop/Pause/Resume/OnTrackFinished` + 事件 `MapChanged/Exited`；按 hops 执行（走/爬/跳-落视觉过渡） |
| `MiniPet/Services/Walk/WalkMusicBridge.cs` | 音乐事件 → 行走：播放→Start、Stop→Stop、暂停/继续、`TrackEnded`→OnTrackFinished、切歌→Start(新 key) |
| `MiniPet/Views/WalkTransition.cs` | 0.5s 淡入淡出（250+250，按窗口 CTS，可重入取消） |
| `MiniPet/Views/WalkPetVisibility.cs` | 行走期间只留主宠，其余 `Hide()`，Dispose 还原 |
| `MiniPet.Tests/WalkPlannerTests.cs` | 6 个 Fact |
| `MiniPet.Tests/WalkModeServiceTests.cs` | 8 个 Fact |

接线（`MiniPet/Views/PetWindow.axaml.cs`）：
- **懒创建** `EnsureWalkServices()`（关键：`PetManager` 是「先建完所有宠物再 `ApplyPrimaryRole`」，构造期 `IsPrimaryRole` 恒 false）
- 33ms 主定时器里 `TickWalkMode()`：`Tick(dt)` → 相机缓动跟随 → `FlipX` 朝向 → **窗口位置直接绑定行走坐标**（`× RenderScaling`）→ 动作切换
- `MapChanged` → `LoadMap` + `SetMap` + `SetCamera` + `CrossFadeAsync` + `HideOthers`
- `Exited` → 还原宠物显示、关随机 idle、恢复行走前动作

**胶水已确认的规则**（不要擅自改）：

| 项 | 取值 |
|---|---|
| 方向选择 | 模拟行走取**累计长度更长**的一边；轴向由 `|Δx| ≥ |Δy|` 决定（向右/向下） |
| 折返 | 无门或目标不可达 → 走到**半程** → **瞬移回起点** → 反向（胶水后来说过可能想改成"掉头走回"，未定） |
| 物理 | sdlMS：水平上限 ±125、加速度 1400、摩擦 800、重力 2000、起跳 −555、落速 +670、爬绳 100 |
| 淡入淡出 | 0.5s（250ms 淡出 + 250ms 淡入） |
| 多宠 | 行走期间只保留主宠可见 |
| 切图 | 走到门 → 淡出 → 换图 → 淡入；**1.5s 冷却** + **同图不切** |
| 歌播完 | 立即停走 → 切下一张图 → 继续走 |

**调试入口**：环境变量 `MINIPET_WALK_AUTOSTART="Bgm00/FloralLife"` → 启动即进入行走模式（无需点菜单）。

**运行时坐标日志**：`/tmp/mp-walk.log`，每秒一条：
```
map=… pos=(x,y) phase=… act=… facing=±1 hop=第几跳/总跳数 noNext=… half=折返阈值
```

### 1.2 地图渲染排序（按 R2 源码逐条纠正，**权威**）

参考：`maplestory-wiki-backend/WzComparerR2C#/WzComparerR2.MapRender`

- **排序键**（`FrmMapRender.cs:1353-1358`）：
  `ZIndex = [ RenderObjectType.Obj(3), layer, patch.ObjectType, z, loadIndex, zM ]`
- **比较器**（`FrmMapRender.cs:1075`）：`ZIndex[]` **逐元素**比较
- **枚举**（`Patches/RenderObjectType.cs`）：`Back=1, Reactor=2, Obj=3, Tile=4, Npc=5, Mob=6, Foothold=7, LadderRope=8, Portal=9, Front=10, NpcName=11, MobName=12, Effect=13`
- **结论**（`MapService.Render.cs` 层循环）：
  - 层序 **0 → 7**（layer 是第二排序键）
  - 层内 **先全部 Obj → 再全部 Tile → 再该层 life**（ObjectType 优先于 z）
  - tile 的 z 取**精灵帧** z
  - life 归**它踩的 foothold 所在层**（R2 把 life 挂在 `LayerNode.Foothold` 的 Slots，见 `MapScene.cs:35-41`）
- **obj/tile 解析**（`MapService.cs`）：**不能** `for(i=0..) {layer}/obj/{i}`——WZ 子节点名不保证从 0 连续/升序（实测 `100000200` 层 0 是 `2,3,…`）。已改为遍历全部子节点 + `Id = int.Parse(nodeText)`（对齐 `MapData.cs:247` `foreach (var node in objNode.Nodes)`）

验证：射手公园 `100000200` 与 R2 渲染一致（灌木压石墙、推车可见、NPC 层次正确）。**改动影响所有地图渲染**，接手后如再动这块必须重渲抽查。

### 1.3 纸娃娃（参考 `maplestory-wiki-backend/MapleSalon2`）

`MiniPet/Services/PaperdollService.cs`：
- body 帧 `{action}/{frame}/face == 0`（ladder/rope）→ **不渲染脸 + 101 面饰**（102/103 保留）
- **脸层容器整体隐藏**：`FrontFaceLayers` = `accessoryFace / capAccessoryBelowAccFace / accessoryFaceOverFaceBelowCap / face / accessoryEyeBelowFace / accessoryFaceBelowFace`
- `isHideFace`：body 动作为 `blink` / `hide` 时也不画脸
- `invisibleFace`：穿戴物 `info/invisibleFace == 1`（面罩）覆盖脸型
- `backDefault`：静态回退优先用物品自带的背面粉（103 耳环有该节点）
- `handMove` 锚点：`alert` / `heal` 时给动作型装备注入（3 档 `(-8,-2)/(-10,0)/(-12,3)`）
- 层别名表补到 **43 条**（对齐 MapleSalon2 `zmapIndex.ts fixLayers`，含 `weapnBelowBody`/`weponBelowBody`/`weaponBelowbody` 等拼写与大小写错名）

### 1.4 地图数据层

- `MapInfo.Ropes` + `LadderRope`（x/y1/y2/l）—— **必须遍历子节点**（`ladderRope` 子节点不从 0 连续，魔法密林 14 根但没有 "0"）
- `MapPortal.Tm/Tn/HasTarget`
- `MapLife.Z`（参与排序）

### 1.5 音乐

- `MusicDecisions`：`Bgm00/Silence` 硬忽略；`FirstMapByReturnMap`（returnMap 众数，tie 取小）
- `MusicPlayerService.TrackEnded` 事件（**自然播完**才触发，区别于用户 Stop/Next）
- 反向索引 key = `"{Img}/{Track}"`（必须**去掉 `.img`**，与 `WalkPathPlanner.TrackKeyOf` 口径一致）

---

## 2. 未完成 / 待排查（交给下一个 harness）

### 2.1【P0】曲目→地图反向索引首次扫描太慢 → **地图总兜底**

- **现象**：播放任何音乐都走默认城镇（清单里含勇士部落 `102000000`，所以胶水看到「一直在勇士部落」）
- **实测证据**：
  ```
  --bgmdiag "Bgm00/FloralLife"   →  Kick 后 10/20/…/90s 全是 (null)
  --bgmdiag "Bgm10/Timeless"     →  同样 90s 全 (null)
  ```
- **根因**：`MusicCatalogService.ScanReverseCore`（第 539 行起）**全量遍历** `Map/Map/Map0..Map9` 下**两万多张地图 img**，每张 `GetValue<Wz_Image>() + TryExtract()` 读 `info/bgm`
- **已做**：`WarmUpReverseIndex()`（主宠服务就绪即预热）+ `PersistReverseCacheToDisk`（扫完落盘）+ `PickWalkMap` 播放时最多等 3 秒
- **胶水的质疑**：「耗时以分钟计 这个为什么 / 缓存到本地 / 一首歌都没有分钟」——**接手后必须先实测一次完整扫描耗时**，再决定优化方向：
  - 只读 `info` 节点、避免整图解压（WzLib `TryExtract` 行为待查）
  - 并行/分批 + 进度事件
  - 或者换思路：不做全量反向索引，改为**按曲目名/分类启发式**选图
- **验收**：无磁盘缓存的情况下启动应用，等扫描完成后（或第二次启动）播放任意曲目，应落到该曲目真实关联地图（如 `Bgm00/FloralLife` → 应有非默认图）

### 2.2【P0】人物频繁瞬移 / 绳下徘徊

- **日志**：`/tmp/mp-walk.log`（每秒一条，由 `PetWindow.TickWalkMode` 写）
- **已抓到的样本**（`104000000` 彩虹岛，`noNext=True`）：
  ```
  11:29:14 pos=(1410,227) hop=2/35 half=502
  11:29:16 pos=(1663,227) hop=5/35
  11:29:17 pos=(1118,227) hop=0/35   ← 瞬移回起点（折返）
  11:29:24 pos=(1079,227) hop=0/35   ← 又一次
  ```
- **特征**：`noNext=True`（判定「没有下一个可传送的门」）+ `half` 仅 402/502 → 约每 5 秒折返一次
- **待判断**：这是折返规则的正常行为，还是 `half` 阈值/路径长度算错（胶水认为「任务一直在瞬移」= bug）
- **排查入口**：`WalkModeService.GraphHalfLen` / `HalfLegLength` / `PathCost`，以及 `SetupLeg` 里 `_halfLen` 的赋值

### 2.3【P1】人物 z 与脚下 tile 同级且同级最上

- 胶水要求：人物的 z 应**等于脚下 tile 所属层/容器**，且在同级**最上**
- 现状：宠物是**独立透明窗口**（在 `MapViewportWindow` 之上）→ 视觉上天然压住地图，但**不能被前景 obj 遮挡**
- 若要做成「人物并入地图渲染管线」（被树/草丛遮挡）→ 属于 F18「无限之路」后续步骤

### 2.4 其他待办

- 折返方式可能要从「瞬移回起点」改成「掉头走回」（胶水未最终确认）
- 「跳跃挂到绳子上」：上绳判定分两条路（有规划路径时只上规划指定的绳；无路径兜底行走时「走近就上」，sdlMS ±15px）。若仍异常，需胶水给地图+位置
- `WalkTransition` 用 `Window.Opacity`（Avalonia 合成树 alpha），macOS 上是否明显**未经胶水确认**

---

## 3. 关键接口（接手用）

```csharp
// 行走
new WalkModeService(WzService wz, MapService mapService, PaperdollService paperdoll,
                    MusicCatalogService catalog,
                    Func<string, MapInfo?>? mapLoader = null,
                    Func<string, string>? mapBgmOf = null,
                    Func<string, string?>? mapPickerOf = null);
void Start(string trackKey); void Tick(double dt); void Stop(); void Pause(); void Resume();
void OnTrackFinished();
event Action<string>? MapChanged; event Action? Exited;
bool IsActive; WalkPhase Phase; string CurrentMapId; float X, Y; int Facing; string CurrentAction;
int CurrentHopIndex; int PathLength; bool NoNextPortal; double HalfLen;   // 后四个是诊断用

// 图搜索
List<WalkHop>? WalkGraph.Plan(MapInfo, int startFh, int goalFh);
HashSet<int> WalkGraph.Reachable(MapInfo, int startFh);
(int FootholdId, double Len)? WalkGraph.Farthest(MapInfo, int startFh);

// 规划
(bool Forward, WalkAxis Axis, double Length) WalkPathPlanner.PickDirection(MapInfo, int startFh);
MapPortal? WalkPathPlanner.ChooseNextPortal(MapInfo, int entrancePortalId, string trackKey, Func<string,string> mapBgmOf);
bool WalkPathPlanner.HasNoNextPortal(...);
static string? WalkPathPlanner.TrackKeyOf(string? bgm);   // "Bgm00.img/X" → "Bgm00/X"

// 行走器
new MapWalker(MapInfo, int startFootholdId);
bool WalkStep(float dt, int dirInput); void ClimbStep(float dt, int dirInput);
bool EnterRope(int ropeId); void LeaveRope(); void PlaceOn(int fhId, float x); LadderRope? RopeNearby();
static (double Len,double Dx,double Dy,int EndFh) MapWalker.Trace(MapInfo, int, bool forward);
static float MapWalker.YOn(MapFoothold, float x);

// 音乐
string? MusicCatalogService.GetFirstMapByReturnMap(string? trackKey);
void MusicCatalogService.WarmUpReverseIndex();
void MusicCatalogService.KickTrackMapsForImg(string? imgName);
event Action<MusicTrack?>? MusicPlayerService.TrackEnded;
```

---

## 4. 构建 / 运行 / 验证

```bash
export DOTNET_ROOT="/Volumes/SSD/C#"        # dotnet 是可执行文件，路径含 # 必须引号

# 编译（0 错误才算完成）
"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug
# 测试（本轮基线 369 passed / 0 failed）
"/Volumes/SSD/C#/dotnet" test MiniPet.Tests/MiniPet.Tests.csproj
# 运行（GUI 需显示器唤醒）
caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
# 调试自启行走模式
MINIPET_WALK_AUTOSTART="Bgm00/FloralLife" caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
```

**真 WZ 数据探针**（`prototypes/f18-probe/`，未跟踪；验证算法不用开 GUI）：

```bash
cd prototypes/f18-probe
D="/Volumes/SSD/C#/dotnet"; cp -f ../../MiniPet/bin/Debug/net9.0/MiniPet.dll bin/Debug/net9.0/
"$D" build f18-probe.csproj -v q --nologo
"$D" run --project f18-probe.csproj --no-build -- --full 100000200          # 整图渲染
"$D" run ... -- --walk 101000000                                            # 行走诊断（连通分量/路径）
"$D" run ... -- --walkmode "Bgm00/FloralLife" 40 100000000                  # 行走模式端到端
"$D" run ... -- --bgmdiag "Bgm10/Timeless"                                  # 曲目→地图反查诊断
"$D" run ... -- --objraw 100000200 1000 1700                                # obj 明细（含 z/id）
"$D" run ... -- --chains 101000000                                          # 纯 prev/next 链统计
"$D" run ... -- --ropes 105040300                                           # ladderRope 解析检查
"$D" run ... -- --doll                                                      # 纸娃娃多动作对照
```

WZ 数据源：`/Volumes/SSD/mxd/mxd/Data`。

---

## 5. 未提交改动清单（`git status`）

已修改（M）：
```
MiniPet.Tests/MusicModuleTests.cs      MiniPet/Models/MapData.cs
MiniPet/Services/MapService.Render.cs  MiniPet/Services/MapService.cs
MiniPet/Services/MusicCatalogService.cs MiniPet/Services/MusicDecisions.cs
MiniPet/Services/MusicPlayerService.cs MiniPet/Services/PaperdollService.cs
MiniPet/Services/TrayService.cs        MiniPet/Views/Controls/SpriteCanvas.axaml.cs
MiniPet/Views/MapViewportWindow.cs     MiniPet/Views/PetWindow.axaml.cs
```
新增（未跟踪 / 已 add）：
```
MiniPet/Services/Walk/{MapWalker,WalkPathPlanner,WalkGraph,WalkModeService,WalkMusicBridge}.cs
MiniPet/Views/WalkTransition.cs  MiniPet/Views/WalkPetVisibility.cs
MiniPet.Tests/{WalkPlannerTests,WalkModeServiceTests}.cs
docs/ai/PLAN-F18-行走与地图切换.md   docs/ai/REF-sdlMS-移动跳跃逻辑.md
docs/ai/infinite-road-handoff.md     prototypes/f18-probe/
prototypes/map-slice/                AGENTS.md  CONTEXT.md  .tmp/
```

规模：`12 files changed, 718 insertions(+), 52 deletions(-)`（不含未跟踪文件）。

---

## 6. 参考实现位置（权威性从高到低）

| 用途 | 路径 |
|---|---|
| **地图渲染排序（权威）** | `maplestory-wiki-backend/WzComparerR2C#/WzComparerR2.MapRender`（`FrmMapRender.cs`、`MapScene.cs`、`Patches/RenderObjectType.cs`、`Patches2/`） |
| PIXI 参考 | `maplestory-wiki-backend/mapRederWeb/frontend/src/index.ts` |
| **纸娃娃/脸饰/层（权威）** | `maplestory-wiki-backend/MapleSalon2` |
| 移动/跳跃物理 | `docs/ai/REF-sdlMS-移动跳跃逻辑.md`（本轮整理，来源 sdlMS） |
| 需求原文 | `docs/ai/requirements-analysis.md` §9（F18） |

---

## 7. 2026-09-15 追加：P0 修复 + 胶水当日拍板（未提交）

### 7.1 P0-1 反向索引（已修，实测）

- **根因不是慢，是扫描从没跑通过**：① `AccumulateTrackMap` 首条记录 NRE（TryGetValue miss → null slice），后台扫描开局即死、异常被吞；② 存/查 key 不一致（`Bgm00.img/X` vs `Bgm00/X`）。
- 修复：NRE + key 归一化（无 `.img` 口径，兼容两种查询形态）+ `ScanReverseCore` 并行化（13 分片 × 4 线程，每任务独立 `Wz_Structure`，失败自动回退旧串行）。
- 实测：冷启动扫描 30.5s(串)→**10.2s(并)**；`--bgmdiag "Bgm00/FloralLife"` 90s 全 null → **10.1s 命中 706020100**；二次启动磁盘缓存（`~/.minipet/music-track-maps.json`）**0.0s 命中**。
- `PickWalkMap` 重写：等待移后台线程（旧 Thread.Sleep 循环在 UI 线程会冻结 UI），索引就绪后自动 Stop+Start 落到正确地图（`ReassignWalkMap`，GUI 实测 10s 后自动切图成功）。

### 7.2 P0-2 瞬移 + 绳梯（已修）

- noNext 判定本身正确（104000000 邻图 BGM 不匹配是真死胡同）；旧 half 口径（单链长/2、Dijkstra 代价/2 被跳跃捷径边压缩）作废。
- **胶水拍板（2026-09-15）：折返改「掉头走回」**——走到该方向真正尽头（贴边 blocked）→ 原地掉头反向走，**无瞬移**。掉头阈值（`HalfLen`，仅诊断）= 图口径全程。禁用位移阈值触发（跨度估算会低估，101000057 实测中途掉头）。
- 绳梯 F1a/F1b/F2/F4/F6 全落地：建图吸附与运行时同包络（±15/±20）、够不到 2s 超时降级、Rope 直判 hop.RopeId、兜底收窄 + 3s 冷却 + 离绳 >40px 闩。F3 由 F1a 包络间接达成；F5（绳惩罚）未做。
- **向后跳根因（三处叠加，全修）**：① Jump 边双向 → 改单向（后端点→前端点）；② x 重叠段之间不建 Jump 边（100000000 y=334 双链重叠，1.4s 连跳 3 次）；③ `NextHopIndex` 只连续推进 → 改「顺路完成」连推（当前段出现在路径后方某 hop → 前面全部视为完成；沿链走到 991 却回头跳 fh#154 的 848 端即此病）。
- 探针 hop 日志实证：修复后 x 全程单调前向；104000000 掉头走回单腿 30s+（右端 3097 ↔ 左端 -673）。

### 7.3 动作乱切（胶水拍板，已修）

- **行走动作固定**：默认 walk1，仅当当前武器在 walk1 下无法展示（`WeaponHasAction=false`）而 walk2 可时才 walk2（`ResolveActions`）；**废弃 walk1/walk2 0.4s 交替**（`_walkAnimTime/_walkFrame` 已删）。
- **切图/换腿 stand 顿挫**：`BeginMap` 设 stand1 后 SetupLeg 不重置动作 → 被采样即「走一半变 stand」。修：SetupLeg 尾部显式 `_currentAction = _walkAction`。
- **平常待机**：空闲随机候选剔除全部 stand*（基础站姿不参与随机切换，`GetRandomIdleAction`）；展示类 OneShot 保留。

### 7.4 人物悬空（胶水截图，已修）

- **fd 本身是对的**：`--fdoverlay 706020100`（新探针命令）红线与桥面草皮顶/悬空平台顶完全贴合。
- 根因 = 窗口定位错位：`KickRender` 用 **ClampCamera 后**的相机渲染（小图钳到地图中点），`TickWalkMode` 却用未钳制缓动值定位 → 706020100 等小图人物系统性悬空/横偏。修：定位改用 `MapService.ClampCamera(…)` 同参计算（`_walkMapInfo` 字段新增）。
- 顺修：① `_lastOriginX/Y` 双乘 `_scale`（OnTimerTick 已存 oxScaled，TickWalkMode 又乘一次；缩放≠1 时脚偏 origin×scale×(scale-1)）；② 行走中 `OnTimerTick` 的 origin 增量补偿停用（与 TickWalkMode 全量定位打架，只同步基准）。

### 7.5 死胡同 30s 回初始地图（胶水拍板，新增）

- 无门图（noNext=True）累计 30s → `BeginMap(会话初始图)`（Start 时选中的第一张图；`_sessionStartMapId` 只在会话开始记录，Stop 后 Start 重置）。同图不切规则保留：初始图本身无门时切回自己被挡 → 留在初始图走。有门图计时清零。
- 单测：`DeadEndFor30s_ReturnsToSessionStartMap`、`DeadEndTimer_ResetsWhenMapHasUsablePortal`。

### 7.6 验证状态（诚实口径）

- build 0 错误；测试 **404 passed / 0 failed**（369 基线 + A 26 + B 7 + 死胡同 2）。
- 探针实测过：`--bgmdiag` 冷/热启动、`--walkmode` 104000000（掉头 90s 稳定循环）、706020100（小图来回散步）、100000000（hop 单向前向 + 连续穿门 100000000→100000200→100000203 + 爬绳）、`--fdoverlay` 706020100。
- **GUI 未重启复验**（胶水要求先不重启）——定位修复（ClampCamera/origin/补偿停用）只有代码级验证，胶水重启后重点看：小图（706020100）人物是否踩线、缩放窗口是否脚底对齐。
- 遗留：① FloralLife 选中 706020100（returnMap 众数）vs 交接文档示例 100000200——胶水若要后者改 `MusicDecisions.FirstMapByReturnMap` 口径；② `/tmp/mp-walk.log` 两种时间戳混用（本地 + UTC）小瑕疵；③ ACT 诊断日志（每动作变化一条）还在 PetWindow，稳定后可撤。

### 7.7 死胡同退出强化 + 100000203 爬塔图脱困（胶水 2026-09-15 二次反馈，未提交）

- **死胡同退出改快**：无门图**第一圈掉头走回完成**（`_deadEndTurns >= 2`，第二次到尽头）即切回初始地图，不再等 30s；`DeadEndReturnSec=30` 仅作卡死兜底（`ReturnToSessionStartMap`）。
- **100000203（爬塔图）被困根因**：22 个门里 20 个是「tm=自己」的跳层门（up01~07/h001/h002），其 BGM 必然匹配当前曲 → 抢占候选位，但同图不切规则永远挡住切图 → 人物困塔 90s。修：`ChooseNextPortal` **过滤 tm==当前图 的门**——out00（→100000200）成为唯一出路，人物会走到正门退图。单测 `SameMapPortals_AreNotExits`。
- 选门逻辑其余保持现状（存储顺序第一个 BGM 匹配门，胶水确认）；100000200 ↔ 100000203 来回是这条规则的确定行为。
- 验证：build 0 错误；**405 passed / 0 failed**；探针实测 100000200 → 100000203 → 100000200（能退出）。

### 7.8 贪心遍历：所有匹配图各走一次、从远到近（胶水 2026-09-15 拍板，未提交）

- **选门规则升级**（`ChooseNextPortal` 加可选 `visited`/`currentX`，不传 = 老行为兼容）：BGM 匹配候选中**优先「本次行走没去过的图」，多个未访问取「离当前 x 最远」的门**；全部去过 → 返回 null（noNext）→ 复用死胡同退图链路回初始地图。
- **死胡同图跨轮记忆**（`_deadEndMaps`）：触发过死胡回退图的图不再优先去——否则每轮清空后「最远的死胡同图」永远第一站（100000001 实测 000↔001 卡死循环）。换曲目（新会话）清空：noNext 判定依赖目标图 BGM。
- 回初始图 = 开启新一轮遍历；人已在初始图时只重置计数不重入。
- **250s 探针实测**（FloralLife 自 100000000）：`000 → 001(死胡同两圈) → 000 → 002(死胡同两圈) → 000 → 200 → 203(30s 兜底退) → 000 → 200…`——从远到近逐个覆盖、死胡同不重复、非死胡同图轮间重访。
- 单测：`GreedyVisits_PrefersUnvisitedMap_AllVisitedMeansNoNext`、`GreedyVisits_FarthestUnvisitedFirst`。**407 passed / 0 failed**，build 0 错误。GUI 未重启（胶水要求）。

### 7.9 物理跳跃/下坠重修（胶水 2026-09-15 拍板「对齐 sdlMS」，未提交）

- **废弃「预选落点直线插值」**（旧实现人先前移、跳跃又插回身后落点 = 倒退观感），改为**真实物理积分**（`StartAirborne`/`AdvanceAirborne`）：
  - 起跳 vspeed=-555、重力 2000、落速上限 +670（sdlMS 三参数）；下坠 vspeed=0 起步；
  - 空中水平「按住方向键」惯性 vx 恒定（AirHspeed=200：125 会同高跳距仅 69px，图内 70~110px 设计跳口全跳不过、104000000→104010000 门路径实测断裂；200 → 111px 覆盖全部跳口）；
  - **飞到哪落到哪**：扫掠位移线段 vs 全部段面求交，取最先穿越的最高面；起跳段豁免（等价 sdlMS 落地保护）；落段由物理决定后跳过起跳 hop + SyncHops 顺路对齐；
  - 安全网：空中 >2s 或 y 超图底 → 强制落规划目标段。
- `WalkGraph.JumpMaxDx` 保持 110（vx=200 下物理可达）。单测适配：JumpHop/FallHop 用物理断言（中途 Y<0 升空 / Y 递减），FragmentedMap 缺口 100→60、TraverseLength 580→540、全程 1950→1960。
- **顺修入口门回退 bug**（`MatchEntrancePortal`）：Tn 匹配失败时旧代码拿**对方图坐标**在本图 NearestPortal（跨图坐标系不同，001 回 000 落错入口段 → 贪心遍历卡死）。改为「本图中同 Tm 门里最近者，再不行 PickEntryPortal」。
- 验证：build 0 错误，**407 passed / 0 failed**；探针验收：104000000 掉头走回 ✓、000→010→010100 穿门链 ✓、跳跃中途 Y<0 物理抛物线 ✓。
- **【遗留·下一项】垂直 foothold 卡住**：100000000 贪心路径去 in04(-56 高台门) 的第 26/47 跳是**纯垂直段** fh#50 (3247,210)-(3247,150)，Adjacent 走边连上垂直段但 `MapWalker.WalkStep` 沿垂直段走不动（x 恒定）→ 人物在主街徘徊不到门。方向：Adjacent 边排除垂直段 / 垂直段按攀爬（climb）处理。`[setup]` 诊断日志（WalkModeService.SetupLeg，stdout）还在，稳定后撤。

### 7.10 墙壁逻辑对齐 sdlMS（胶水 2026-09-15「碰到墙壁掉头」，未提交）

- **sdlMS 依据**（REF 文档 §4/§5）：行走 `physic::walk(fall=true)` 段尽头无相邻段 = **走出去掉落**（current_fh=0 进下落态），不是贴墙停；下落横移撞垂直段（无 k=墙）→ `x 钳墙边 ±0.1、hspeed=0`、继续下落。
- MiniPet 实现：① blocked 分支新增 `TryWalkOffEdge`（端点外侧正下方有落脚面 → `StartWalkOffFall` 惯性走出掉落；真无路才掉头）；② 空中 `FindWallDuringSweep` 横向撞墙检测（穿越垂直段 → x 钳 ±0.1、vx=0、继续落）；③ 落地/撞墙检测均跳过垂直段（垂直段非立足面）。
- 验证：build 0 错误，407 全绿；探针 104000000 掉头走回 ✓、穿门链 ✓。
- 遗留不变：100000000 去高台门路径卡垂直段（§7.9 遗留，Adjacent 排除垂直段/垂直段按攀爬，下一轮）。

### 7.11 垂直立柱转梯 + 贪心逐候选（2026-09-15 深夜，未提交）

- **「走出去掉落」按胶水指示注释停用**（`StepOnce.blocked` 分支，实现保留在 `TryWalkOffEdge`/`StartWalkOffFall`），blocked 恢复掉头。
- **垂直 foothold 支持（胶水「垂直段需要」）**：
  - `MapService` 加载时把垂直段（x1==x2）转为 `LadderRope`（id=100000+fid，IsLadder）→ 复用整条攀爬链路；
  - `WalkGraph.Build`：垂直段**排除出图节点**（不能作行走面），但为立柱梯建 **prev/next 链邻段直连的绳边**（立柱两端常悬空 >20px，F1a 吸附窗口不适用；链邻段是 WZ 给定的真实连通）；
  - 空中落地/撞墙检测跳过垂直段。
- **贪心逐候选（`OrderCandidates`）**：SetupLeg 按「未访问优先+从远到近」排序全部候选门**逐个尝试 Plan**，排头不可达（100000000 的 in04 在 -56 高台、与主街落差 390px 无过渡段）换下一个；全部不可达才 noNext。
- 验证：build 0 错误，**407 passed / 0 failed**；探针实测物理跳跃成功上 -56 高台（hop39 Jump→fh#67）。
- **【新发现·待处理】小平台振荡**：人物从高台掉进 -56 高台正下方 76px 宽小平台（fh#12/13, y=304, x 4403~4493），两端 blocked + 「走出去掉落」被注释 → 无限掉头振荡（0.5s/次）。解除 `StepOnce.blocked` 分支的注释（1 处）即可掉回下层继续走；或对小宽度平台强制掉落。

### 7.12 鼠标交互明确化（胶水 2026-09-15 确认，未提交）

- **行走模式左键禁拖 = 故意设计**（胶水确认）：宠物是地图里的角色，TickWalkMode 每帧覆盖 Position，拖拽必然被拉回。PointerPressed 行走激活时 `_dragCandidate=false` 直接不进拖拽流程；**右键菜单不受影响**。
- **视口鼠标控制在行走中禁用**（`MapViewportWindow.MouseControlEnabled=false`）：左键拖相机/右键移窗/滚轮缩放全忽略——手动拖动会让相机偏离人物（胶水实测「拖动内部以后摄像机移动了 人物没了」）。退出行走恢复 true。

### 7.13 物理跳跃执行链路修复 + 魔法密林剩余卡点（2026-09-15 深夜，未提交）

- **hop 加起跳段（`WalkHop.FromFootholdId`，Plan 回填）**：Jump/Fall **必须站在起跳段上才起跳**，不在则沿链走过去——杜绝「半路原地起跳够不到目标段」。
- **hop 推进加几何兜底（SyncHops）**：重叠等价段（不同 id、同 x 范围/坡面）id 永远失配 → 几何口径（x 范围 ±5 且距段面 ≤10px）视为到达。
- **hop 卡住自救**：8s 无推进 → 从当前位置重 Plan；仍不可达 → 放弃候选门（noNext）。探针实证自救循环工作。
- **台阶归走加高度约束**（ResolveLink）：Adjacent 归走原只看 dx≤90——魔法密林 fh#123→fh#48 高差 75px 被归走、人物在下方上不去。加 |Δy|≤20。
- **【下一项·已精确定位】跨高度链邻段**：魔法密林 fh#123(-212,-335) 链 next 链邻 fh#47(-190,-423)（跨高度 70px 的链），路径规划为 Adjacent「走」，执行切段在 y 突变处上不去 → 人物在 -335 层徘徊。修法 = 原需求 §9「台阶 jump 联动」：Adjacent 链邻但 |Δy|>40 → 归成 Jump（物理跳上/跳下），在 WalkGraph 建边或 ResolveLink 落。诊断工具已就位（[setup]/hop/CurrentFootholdId）。
- 验证：build 0 错误，407 passed / 0 failed。魔法密林探针：人物持续活动不再死卡（自救循环），登 -423 高层待 7.13 下一项。

### 7.14 交互更正 + 图内路径贪心（2026-09-15，未提交）

- **左键拖相机恢复**（撤销禁用；胶水更正「左键拖动需要」）：拖动中 `UserDraggingCamera=true` → TickWalkMode 暂停 SetCamera（相机跟手），松手把渲染相机读回 `_walkCamX/Y` 缓动追回人物。`MouseControlEnabled` 保留但默认 true。
- **交互结束宠物提回最前**：`MapViewportWindow.RaisedByUserInteraction`（PointerReleased 触发）→ PetWindow `RaisePet()`——修双 Topmost（背景/宠物均 Topmost）点视口后人物被背景盖住。
- **右键完全失效**（行走中不弹菜单，胶水确认）。
- **图内路径贪心（「走路也是贪心 尽量每条路都走过一次」）**：`_walkedFh`（当前图已走段），`WalkGraph.Plan/RouteToEnd` 加可选 `segmentPenalty`（已走段 +400），SetupLeg/掉头重规划/卡住自救全部传 `WalkedPenalty`——路径优先探索没走过的段；死胡同唯一路仍会重复（无法避免）。
- 验证：build 0 错误，407 passed / 0 failed；探针 101000000 回归（自救正常，重规划路径受罚函数影响开始分化）。
- 遗留（§7.13 下一项不变）：跨高度链邻段 |Δy|>40 归 Jump（魔法密林 -423 高层登顶的最后一步）。

### 7.15 五条遍历规则 + 整晚日志三大问题修复（2026-09-16，多 agent，未提交）

**整晚日志分析（21:05→次日 8:21，45157 条采样）三问题：**
1. **100000000 停滞 10 小时**（88% 采样、掉头 5235 次、hop 卡 5/15）：跨高度链邻段（|Δy|≈70px Adjacent）走不上去。
2. **反向索引扫描 NRE 109 次**（第二处，恢复预选路径触发）：根因 = `_reverseStore = working` 按引用发布可变字典 + `AccumulateTrackMap` 复用已发布 List → 并发读写撕裂。修：锁内整体替换发布 + 无条件拷贝新 List + 堆栈诊断补全。实测冷/热/端到端 3 轮扫描 0 失败（19871 条目/907 曲目）。
3. **死胡同图秒退连环快切**（5 秒切 4 图）：turns 每次 blocked +1 无完整单程语义。

**五条规则落地（多 agent）：**
- 规则 2+补充（Agent A）：`GetTrackMapsOrdered` = 合格 town 优先（mapId 升序）→ 其余 mapId 升序；town 合格校验 = returnMap 指向图 BGM == 当前曲（TrackMapRef.IsTown 零锁读；指向图不在候选集才查 WZ）。`GetFirstMapByReturnMap` 改新口径。实测 FloralLife → **910000000**（合格 town 最小）、Beachway（无 town）→ 109080000（mapId 最小）。
- 规则 3/1（Agent B）：死胡同三层判定——真尽头（图口径前方无路）才计趟；完整单程 = 位移 ≥ max(全长/3, 300px)；最短停留 20s。实测 001：短途 blocked 只掉头不计趟 ✓、两方向完整单程 + 26s → 退图 ✓。
- 规则 4（Agent B）：退图目标 = `GetTrackMapsOrdered` 列表当前图之后的下一个（回绕）；全死胡同回退来源图/初始图/原地。
- 规则 5：无候选不走默认链路验证通过（反查 null 才默认 + 索引就绪自动重开）。
- **「走出掉落」仍注释停用**（胶水指示）；hop5/15 跨高度卡住由台阶高度约束（§7.13）+ 本轮罚函数缓解，跨高度 |Δy|>40 归 Jump 仍为下一项。
- 验证：build 0 错误，**429 passed / 0 failed**（基线 407 + A 16 + B 6）；探针实测 NRE 0 失败、排序新口径、死胡同完整单程。已重启 GUI（射手村起步 → 910000000）。

### 7.16 墙/爬/跳语义修正（2026-09-16，未提交）

胶水实测三问题：①绳子没爬就走了（覆盖缺失，待复现排查）；②有的地图跳过了墙壁；③面对墙用爬而不是跳跃。

- **撤销「垂直段转梯」**（§7.9 引入）：sdlMS 语义垂直 foothold = 墙（无 k 不可站不可爬），转梯让人物面对墙用爬、且空中撞墙检测形同虚设。`MapService` 撤销转换 + 清理 id≥100000 残留。
- **跨高度链邻段归 Jump（台阶 jump 联动落地）**：`WalkGraph` 建图 ①链邻边 |Δy|>40 且 ≤77 → Jump（物理跳上/跳下）；≤40 保持 Adjacent 走。高台连通由物理跳承担（不再依赖假梯）。ResolveLink 的台阶归走已有 |Δy|≤20 约束配合。
- 验证：build 0 错误，**429 passed / 0 failed**；探针实测 000 不再卡 hop5（自救+跨高度 Jump 生效），150s 完成 001 完整覆盖往返并持续向 in01 推进。已重启 GUI。
- **遗留**：①「绳子没爬就走了」——Rope hop 覆盖缺失，需按图复现（怀疑：重规划/罚函数导致绳边路径被换、或几何兜底把 Rope hop 误判顺路完成）；②空中跳过墙壁的具体复现（FindWallDuringSweep 检测在，待复现定位漏点）。

### 7.17 射手村周边 8 图逐图实测（2026-09-16，验证 agent，未提交）

- **「墙当绳爬」确认零残留**：8 图 12 份日志、约 460 攀爬帧全部落在 WZ 真绳 x±5 内，遇墙行为是 jump。
- **「绳子没爬」定位三处**：
  1. **100000200 绳 x=3283 永久盲区**：候选门 in00 在 x=3368 墙右侧 322px「口袋区」；跳边单向（左→右）+ Dijkstra 选跳墙捷径（比爬绳路线短）→ 高台区永无通路。**修法候选：跳边恢复双向**（当年锯齿三根因中 SyncHops 失配与重叠段均已修，单向可能是过度修正——需回归验证）。
  2. **100000203 起始 16 绳 0 帧**：入口门被选为唯一外联门 out00 → OrderCandidates 排除入口 → 候选直奔 200 → 200 落入口袋区死胡回 → 永不进覆盖模式。**入口门排除已放宽**（排除后候选为空则保留入口门，走回它=原路返回）。
  3. 910000000 绳为纯捷径非必经（覆盖无损失，可选）。
- **其他**：死胡同图作起始在探针（catalog=null）下原地死循环——真 app 有 catalog 走有序表，需真机复核；100000000 起始 300s 内 15 次 hop 卡住重规划（跳弧失配，均自恢复，遍历效率待观察）；出生点可能落在隐藏自环门（不致命）。
- 验证：build 0 错误，**429 passed / 0 failed**。实测日志 /tmp/f18walk/（analyze.py 可复跑）。
- **下一项清单**：①跳边恢复双向回归（口袋区）；②「绳子没爬」修复后逐图复测；③hop 跳弧失配频率观察；④死胡同起始真机复核。

### 7.18 全量模拟（19871 图）+ TOP3 修复（2026-09-16，双 agent + 主 agent，未提交）

**全量验证**（双 agent）：①静态审计 19871 图 4.4 分钟（AuditAll.cs `--auditall`）：绳吸附失败率 63.4%（5235 图绳盲区）、无立足段 5587 图（28%）、碎片化 2653 图、不可达门 402 图、跨高度不可跳 372 图；②动态遍历 19871 图 × 90s 仿真 15 分钟（WalkModeProbe `--speed 10` + `--walkmode-batch` 8 进程）：行走中 stand1 全量 0、死胡同秒退已防、魔法密林十六绳已修复（爬绳 100 帧）。产物：/tmp/audit-all.csv、/tmp/dynwalk-all.csv、/tmp/dynwalk-abnormal.txt。
**口径更正**：music-track-maps.json 是 TOP5 截断缓存（3574 张）；全量 = WZ 扫 info/bgm 非空（19871）。

**B 类修复（Agent B TOP3，主 agent 落地）**：
- B1 乒乓秒切（919 图）：进图冷却 `_portalCooldown ≥2.5s`（BeginMap）。
- B2 自救盲区（252 图，含入口=垂直段 path=0 场景）：新增「位置停滞 8s」自救（ Walking 且位移 <5px 累计 8s → 重规划/放弃候选），与 hop stall 并行。
- B3 Jump 链失败（1036 图 maxHop 停滞 + 289 有门未穿出）：同一跳连续 3 次落地 hop 未推进 → 放弃候选门换下一个（`_airFailStreak`）。

**复验（3573 json 关联图 × 90s 仿真）**：卡住 324→90、有门未穿出 395→108、有绳未爬 20→9、stand1 保持 0。秒退 630（17.6%，2.5s 冷却后仍偏高，乒乓图对可再观察）。

**选门口径最终版**（胶水 2026-09-16 更正）：候选门排序 = **人物与门的距离**（|门x−人物x|）降序，未访问优先——跟着传送门走，从远到近。（曾短暂改为目标图 mapId 距离，已回滚。）

**下一项**：①「绳子没爬」残余 9 图复现；②秒退 630 图的乒乓对观察；③跨高度 |Δy|>40 链邻归 Jump 的覆盖效果复核（本轮跨高度 Jump 已在 7.16 落地）。

### 7.19 路线模拟：射手村→六岔路口→勇士部落顶部（2026-09-16，未提交）

- 门图 BFS（探针新增 `--route <from> <to>`）：可行路线 = **100000000（西门 west00）→ 100010000（六岔路口）→ 100010100 → 104020000 → 102010100 → 102010000 → 102000000**，共 7 图 6 段（东线 100020000 不通勇士部落）。
- 段 1 ✓（000 → 100010000，从远到近贪心正常，只是「最远的门」是 east——东侧探索后贪心会回来走西门）。
- **段 2 卡点（100010000 → 100010100，已精确定位）**：
  `[setup] 入口段=144(-405,-295) candidate=west00@-363,-296→100010100 path=0` + `位置停滞 8s at (-483,455) → 放弃候选`
  ① 候选门 west00 与**入口段 144 同段**（Plan 返回空=path=0），但人物实际在下层 (-483,455)——**入口吸附定位与实际落点不符**（MatchEntrancePortal 的 Tn 匹配/回退在 100010000 的双层 west00 门上选错）；② path=0 时没有「人已在门口→直接切图」的处理，候选被放弃 → 整图误判死胡同退图（且退图目标不含 100010100）→ 路线中断。
  **修法**：① MatchEntrancePortal 修正 100010000 双层门的定位；② path=0 且候选门吸附段==入口段时，应校验人物与门实际坐标（判定带）→ 在带内直接 CrossToPortal、不在带内 → 视作「需先走到门口」继续行走而非放弃。
- 段 3-6 待段 2 修复后逐段验证。

### 7.20 撞墙停顿 + 跳抓 + 绳端物理脱离（2026-09-16，未提交）

胶水三反馈（撞墙回头突兀 / 能 jump 的地方误判死胡同 / 能爬的绳误判死胡同 + 绳到头下来不是 jump）：

1. **撞墙转身停顿**：blocked → 站住 0.4s（stand 动作）→ 停顿结束重新确认尽头状态再掉头（死胡同走 TurnAround 计趟，普通路直接反向）。观感自然，不再瞬间 180°。
2. **绳吸附窗口扩大 ±15/±20 → ±25/±40**（建图 F1a 与执行 RopeNearby 同源常量同步扩）——63.4% 吸附失败绳中的「站得上但要跳」场景恢复可达。
3. **跳抓**：绳 hop「站得上 x 够不着 y」→ 原地起跳（vx 朝绳、vy=-555），空中每子步查绳包络命中即 EnterRope 转 Climbing；落地未抓计失败，2 次后 F1b 降级跳过。魔法密林实测爬绳 99 帧、遍历健康。
4. **绳端物理脱离**：ClimbStep 越端只钳位标记（`JustReachedEnd`），服务层转**下落态**（jump 动作、vx=0、扫掠落地到端头地面段）——废除「瞬移吸附最近段」。连带修：物理落地后 `Phase` 必须回 Walking（否则永久卡 Climbing）。
5. **链邻跨高度按方向分类**（修「没有全路径走」）：|Δy|≤40 走 / 上方 ≤77 跳 / 上方 >77 墙不建 / **下方任意落差 Fall（走出掉落，物理下落）**——SleepyWood 706030xxx 链邻下台阶卡死误判死胡同的根因修复。

验证：build 0 错误，**429 passed / 0 failed**；魔法密林 150s 探针：爬绳 99 帧、穿门链 ✓、无卡死。
**遗留**：①「绳子没爬」残余 9 图（静态审计口径）；② 空中穿墙复现；③ 段 2 双层门定位（§7.19）。

### 7.21 SleepyWood 树图根因 + 同图传送门（2026-09-16，未提交，执行链路未通）

- **根因确认**：706030xxx 是「分层树图」——up00/down00 门 **tm=自己、tn 互指**（图内传送：树下↔树顶）。此前一刀切排除 tm=自己门 → 树图被判死胡同（候选数=0）、上下层永不覆盖 → 「绳没爬」「来回走」「5 张图轮着各走两圈退图」全部由此。
- **已落地**：
  - WalkGraph ②a：同图传送门建边（门吸附段 → 目标门吸附段，Jump 型 cost60，双向）；`NearestFootholdNode`（垂直段排除的 y 差吸附）
  - CrossToPortal：tm=自己且有 tn 目标 → **图内传送**（PlaceOn 目标门 + 重规划继续遍历）；乒乓防护 = `_usedSelfPortals`（每门本腿一次，进图清空）
  - 撞墙转身停顿 **已回退**（胶水「转头回退吧 问题更大」）——恢复直接掉头
  - 绳吸附窗口 ±25/±40、跳抓（起跳空中抓绳，2 次失败降级）、绳端物理脱离（jump 落地 + Phase 回 Walking）
- **未通（下一项·已定位）**：传送边 Link=Jump 被执行层当**物理跳**（起跳段+抛物线），物理跳到不了传送目标 → 路径含传送边时执行失败；且 Dijkstra 直连传送边导致路径里没有「走到门口」的中间 hop，门口判定带不触发。**修法**：传送边改用专用 `WalkLink.Warp`，执行层 Warp hop = 沿链走到门口（FromFootholdId）→ 判定带内触发图内传送（PlaceOn+重规划），不做物理跳。
- 验证：build 0 错误，**429 passed / 0 failed**。SleepyWood 探针：仍未传送（如上），轮换退图依旧——这是当前第一优先。

### 7.22 蚂蚁洞自测：瞬移根因修复 + 乒乓最后一环（2026-09-16，未提交）

- **瞬移根因修复（胶水日志「瞬间挪到出口、没走全路径」）**：7.20 的「链邻下跨任意落差 Fall」缺水平约束——WZ 链存在**跨区域传送链**（105010300 fh#349 next 直指 2400px 外/1380px 下方出口段），建出物理不可能的 Fall 边 → 执行超时兜底瞬移。修：链邻跨高度加水平约束 dx≤180（远离的链邻=坏链不建）。
- **修复生效实测（蚂蚁洞 105010300）**：瞬移消失、能走到出口穿门（300↔301 互通）、**爬绳 1515 帧 + Climbing 54 次**（「绳子没爬」修复生效）。
- **剩余最后一环**：300↔301 乒乓——AncientMove 曲目 300 只有 301 一个出图门；301 死胡回 30s 兜底退图后仍回 300（PickNextMapInOrderedTrackMaps 的 eligible 已排 deadEndMaps，但实测仍选了 300——**需加日志实证 target 选择**：怀疑 GetTrackMapsOrdered 列表内容/顺序或 IsEligibleExitTarget 之外另有覆盖）。预期正确行为：曲目列表下一个 = 105020000（龙族洞穴）。
- 验证：build 0 错误，**429 passed / 0 failed**。
- 诊断资产：[setup] 选门/停滞日志、[hop] 跳详情、--route 门图 BFS、/tmp/ancient2.log（修复后健康段）、/tmp/ancient4.log（乒乓复现）。
- **下一轮**：① ReturnToSessionStartMap 加 target 日志，查乒乓选目标；② 修好后跑通 AncientMove 全候选（300/301/105020000）顺序遍历；③ 段 2 双层门（§7.19）。

### 7.23 全量覆盖率对比（Warp 修复后，2026-09-16，未提交）

- **Warp 死循环修复效果**（同边 10s 冷却 + 罚金 + 双向标记修正 + 钉墙/图外段/覆盖腿/盒式自救四连带修复）：
  - 706030400 树图覆盖 **46/46 = 100%**（300s 传送 3 次方向交替无乒乓）
  - 三张 TOP 死循环图：326000000 0.002→**0.167**、109080000 0.002→**0.229**、910510100 0.002→**0.284**（300s 0.292，69 段→233 段）
- **全量覆盖率对比**（分母 8588 有立足段&可达>5&非993xxx）：
  - ≥90%：2197（25.6%）→ **3070（35.7%）**（+10pt）
  - <50%：3951（46.0%）→ **3191（37.2%）**（−9pt）
- **距 90% 线的剩余三层原因**：①仿真时长（超大图 90s 仿真预算走不完，300s 可显著提升——时长参数非逻辑）；②**规划可行性**（有向图单向 Fall/Jump 边让 Plan 产出物理执行不了的跳——需「执行可行性感知规划」，Agent B 建议单独任务线）；③绳吸附 63.4% 失败（§7.17 遗留）。
- 验证：build 0 错误，**433 passed / 0 failed**（+4 Warp 测试）。产物：/tmp/cover-after.csv。

### 7.24 跳距语义裁决 + Warp 执行收尾（2026-09-16，双 agent，未提交）

- **裁决（Agent A）**：FragmentedMap 的 fh4 物理可达（fh2→fh4 有 60px 缺口跳边，JumpReach(80)≈84px 可达；fh3→fh4 另有垂直 Fall 边）——RouteToEnd 尽头=fh4 正确。真病灶 = `FarthestUnvisited` **Dijkstra 距离精确并列（540）时按字典序随机裁决** → 选中 fh3（延伸 960）。修：并列（容差 1e-6）取**水平延伸更远者**。测试断言按 2 跳物理现实适配（fh2→fh4 Jump 直接落下层，不再经 fh3）。
- **Warp 执行核验（Agent B）**：此前会话已落地（StepOnce Warp 分支 + ExecuteSelfWarp 统一 + 10s 边冷却 + 双向标记修正 + PlanLegToTarget 锚定 + 盒式卡死自救），本轮逐条核验 + 4 新测。SleepyWood 300s×2 实测：传送 15/13 次间隔 17-18s 无乒乓、y 覆盖 6 层（-255~225 上下贯通）。
- 验证：build 0 错误，**433 passed / 0 failed**；三图回归：SleepyWood 同图传送 ✓、蚂蚁洞互穿+爬绳 22 ✓、射手村走向远门途中（从远到近正常）。
- **覆盖率（§7.23 基线）**：≥90% 达标图 35.7%（修复 Warp 死循环后 +10pt）。剩余差距三原因：①大图仿真时长（物理预算）②单向 Fall/Jump 边的规划可行性（建议单独任务线）③绳吸附 63.4% 失败。

### 7.25 全量覆盖率最终验证（2026-09-16，含 Warp/跳距/墙语义全部修复）

- **覆盖分布**（分母 8638 = 有立足段&可达>5&非993xxx 活动图）：
  - ≥90%：**2770（32.1%）**；50-90%：2324（26.9%）；<50%：3544（41.0%）
  - （对比 Warp 修复前基线 ≥90% 25.6% / <50% 46.0%——提升但未达 90% 线）
  - 993xxx 活动图分片卡住未跑完（330 张，单独归类不影响主统计）
- **低覆盖样例**：000010000（0.22）、000050000（0.14）、001020000（0.26）——均为 000/001 前缀特殊图与多层未探索图。
- 产物：/tmp/cover-final.csv（21088 行）。
- **距 90% 线差距的已知三大原因不变**（§7.23）：①超大图仿真时长（物理预算）②单向边规划可行性 ③绳吸附失败率。
- **行为修复确认项（本轮全部在位）**：物理跳距公式、墙语义、绳窗口±25/±40、跳抓、绳端 jump 落地、同图传送 Warp、乒乓冷却、位置停滞自救、Fall 水平约束、最短停留、退图顺序遍历。

### 7.26 移动跳抓（2026-09-16，未提交）

- **跳跃高度分析（胶水问「跳抓 vs 普通跳谁高」）**：sdlMS 起跳速度统一 -555、重力 2000 → **跳高固定 77px，与移动无关**。跳抓的本质优势是**空中横移**（斜飞过绳边抓取），不是高度。
- **移动跳抓落地**：抓取触发窗从「站在绳 x±25」放宽到 **±(25 + 125×0.5s≈87px)**——行走路过绳旁（不必站定）即自动起跳，vx 朝绳、空中每子步对齐（x±25 且 y ∈ 绳身）即 EnterRope。
- 验证：build 0 错误，433 全绿；蚂蚁洞 Climbing 22→**31**（移动跳抓生效）；SleepyWood 树图由 Warp 传送承担上下覆盖（绳为非必经，合理）。

### 7.27 气泡「炸了」排查 + 随机气泡开关（2026-09-16，未提交）

- **随机气泡发言开关已落地**（胶水「右键设置里自选」）：
  - 「💬 随机气泡发言」勾选项 → 移到**「切换气泡」下**（胶水指定位置），`PetSettings.RandomBalloonEnabled`（默认 true）持久化
  - 三重防御：定时器停 + 气泡收起 + `TryShowRandomBalloon` 入口拦截；构造时按配置应用
  - 「😊 随机表情」开关此前已存在（_exprMenu + RandomExpression 配置），本轮清理了我误加的重复接线
- **气泡「炸了」（白框）排查结论**：
  - 缓存瓦片**不是损坏文件**（520 号气泡 11 张瓦片全部为合法 32×32 PNG，今日 16:31 新种子）——已按授权清理 `~/Library/Caches/MiniPet/v1/balloons/`（下次运行自动重新提取）
  - 白框气泡出现在**随机气泡已关闭**状态 → 非 TryShowRandomBalloon 路径；候选路径：`ShowBalloonNow(text)`（外部调用）或 `EffectLayerService` 状态气泡（:218）
  - 白框内容「…x0」疑似文本渲染缺失/合成异常——**下一轮**：① 复现时抓 `RenderBalloon` 的输入（balloonId/text）与合成输出；② 排查 9-slice 合成的文本绘制层（字体/glyph 缺失会渲染空白）；③ 确认哪条 Show 路径弹的白框
- GUI 当前由胶水自行启停；未 commit。

### 7.28 椅子渲染参考实现（MapleSalon2 chair 模块精读，2026-09-16）

参考代码：`/Users/a502/IdeaProjects/maplestory-wiki-backend/MapleSalon2/src/renderer/chair/`（chair.ts 1096 行 + chairGroupUtil.ts）。

**椅子渲染本质（web 正确行为）**：
1. **WZ 椅子数据 = `Item/Install/0301xx.img/{chairId}`**（getChairPath：Install 目录 8 位补零；非 Item/Special）。数据核心在 **info 节点**，含每个「座位组」的参数：
   - `position {x,y}` = **人物坐在道具上的偏移坐标**（人物摆放依据——正是胶水指出「人物坐的地方不对」的数据源）
   - `action`（默认 sit，可被 info.sitAction 覆盖，'hide'→sit）
   - `expression`（info.sitEmotion/face）
   - `removeBody`/`hideBody` = 变身椅隐藏人物身体（变身椅 3015632 大概率走这里）
   - `tamingMobId` = 坐骑型椅子（有则强制 sit）
   - `info.group` = 多座位分组（多人椅）；`lvChairInfo`/`customChair` = 其他型
   - `z` = 人物与椅子的层级
2. 椅子图形 = `{path}/0/{z 图层}` + `effect[0-9]?` 特效层（effectReg）
3. **渲染组合**：椅子图层（z 排序）+ 人物 Character 以 gd.action（sit）渲染、position 偏移、expression（sitEmotion）、hideBody 时人物隐藏
4. **cash 椅子 id ≥ 5204000 → Cash 目录**；其余 Install

**MiniPet 实现任务（下一轮，对齐 web 逐字段）**：
1. 修 WzService：`Item/Install/0301xx.img` 分片加载（当前 Item/Special 路径本来就错——正确目录是 **Install**）
2. PaperdollService 椅子管线对齐 chairGroupUtil：position/action/expression/hideBody/z/flip 逐字段
3. 3015632 预览 = 花田特效层 + 人物按 info.position 坐姿（sit）摆放
4. 移除 TamingMob 错误回退（沙漏来源）

### 7.29 椅子（变身椅）渲染修复 R-chair2（2026-10-10，未提交）

**WZ 考证（f18-probe --chairdiag/--chairrender/--chairscan，全部实测）**：
- **任务给的 3015632 在本 WZ ≠ 花田椅**：`Item/Install/030156.img/03015632` = **「和雪孩子一起」雪屋椅**
  （info 仅 sitAction=stand1 + price 等；effect 10 帧 251×217 雪屋+雪孩+篝火场景图）。
  web（KMS 数据）3015632 = 花田魔法椅——**同 id 不同道具**（国服本地化 id 漂移）。
- **本 WZ 的花田椅 = `3019632「变出花田的魔法椅子」`**（`Item/Install/03019.img/03019632`）：
  - info：**无 sitAction（→默认 sit）**、`bodyRelMove=(47,−2)`、`sitEmotion=14`、`removeEffect=1`、
    `invisibleWeapon=1`、`invisibleCape=1`、collabo/noPrism/tradeBlock；**无 position 字段、无 removeBody、无 tamingMob**
    （任务描述的 info.position 在 MapleSalon2 wz.ts 中不存在，人物摆放实际依据 = bodyRelMove）
  - effect 12 帧（delay=210，origin≈(117,179)，帧级 z=0、节点级 **z=−1**、**pos=3**）+ effect2 1 帧（花横条，z=2 置顶）
  - effect 帧图 = **整幅场景（花田+花瓣+特效自带的持杖坐姿女孩）**——web 观感的主角主要来自这层
- 全表扫描（3114 把椅子）：bodyRelMove 49%、removeEffect 48%、invisibleWeapon 45%、tamingMob 34%、
  removeBody 206 把、sitAction 552、sitEmotion 187、group 66、lvChairInfo 24、customChair 350
- **分片真相**：Install 数据在 `Install_000.wz` 顶层（03010…03019 / 030150…030159 / 0302 / 0304…0399.img），**无 0301.img**；
  3015xxx 按 id/100 分组（030156.img），3019632 按 id/1000（03019.img），3020+ 按 id/10000（0302.img）。
  ChairRoot 运行时探测早已命中（7.19 的修复），「0301.img 加载不到」不是渲染错误根因。
- **「带翅膀的金色沙漏」未在本 WZ 任何花田相关椅子数据中出现**（名字含 花田/沙漏 的椅子 effect 全部导出核对）；
  疑为对 effect 场景图中「持杖女孩+金色法杖」的低分辨率误读或旧版渲染记忆。TamingMob 错误回退不存在
  （`info/tamingMob` 探测只用于 pos==1 的 −30/−50 偏移选择）。
- 探针曾见 ExtractPng 首调失败：**探针直读分片文件段污染 WzComparerR2 静态状态**（DefaultImgCheckDisabled 等）
  的伪象——重排探针顺序后消失，主工程不受影响（GetBmp 也不缓存 null）。

**渲染修复（MiniPet/Services/PaperdollService.cs，R-chair2）**：
1. `ChairDataSnapshot` 缓存（bodyRelMove/sitEmotion/sitAction/removeBody/removeEffect 一次读取，热帧零锁）
2. **effect 锚定偏移**（新纯函数 `ComputeChairEffectOffset`，对齐 ChairEffectPart×sitCharacter 相对关系）：
   有 bm 且无 sitAction 且 pos<2 → 锚 navel 不动（effect 与人物 web 中一起平移）；pos==1 保留 −50；
   其余（pos≥2 或有 sitAction）→ 锚 = navel − bm（3019632：**(−47,+2)**，人物坐进 WZ 指定坐标）；
   无 bm pos==1 保持现状（tamingMob 且 id<3011000 → −30）；removeBody → +30（web 人物容器 y−30 的等效）
3. **sitEmotion**：数字索引按 MapleSalon2 CharacterExpressionsOrder 查表（`ResolveChairSitEmotion`，14→glitter），
   CollectPiecesForFrame 开头覆盖 expression（缓存 key 同步），脸型走 glitter 表情
4. **removeBody（变身椅，206 把）**：人物部件全隐藏只留椅子层（对齐 gd.hideBody→isHideBody）
5. **removeEffect**：装备特效全隐（对齐 ChairTab useCharacterEffectVisible）；invisibleWeapon/invisibleCape
   web 端 getter 无消费者——不实现
6. **素材浏览器椅子 icon**（MaterialBrowserWindow.Logic.ChairIconRoot）：原恒用 0301.img（本 WZ 无）→
   icon 全失败；改按 id 分组（3015xxx→id/100，3020+→id/10000，其余→id/1000），与 ChairRoot 首选一致

**验证（真实运行）**：
- 3019632：人物 sit 坐姿**右移 47px 坐进花田**（修复前压在特效人物下方）+ **glitter 星星眼**（修复前默认脸）
  + removeEffect 生效；out/chairrender_3019632_sit_0_3x.png
- 3015812（变身椅）：人物隐藏只留花丛场景；3010205（普通椅 bm=(0,−14) pos=0）：与修复前逐像素同尺寸零回归；
  3015632（雪屋椅无 bm）：渲染尺寸/origin 与修复前完全一致
- build：MiniPet 0 错误；test：**445 全绿**（433 基线 + 9 R-chair2 + 3 icon 分组新断言）
- 探针：--chairdiag（数据树 dump+effect 导出）、--chairrender（PaperdollService 真渲染复现）、
  --chairscan（3114 椅 info 字段全表统计）

### 7.30 椅子层抖动修复 + 验证方法记录（2026-09-16，未提交）

- **抖动根因链**：①椅子/特效层合成锚 = body 当前帧 navel（呼吸 3 帧 navel 变 ±8px）→ 锚定层循环抖动；②冻结字典 key 用 PiecePath（含 effect 帧号）→ effect 动画帧间路径不同 → 冻结 miss。
- **修复**：①合成时 chair/effect 部件**位置冻结+减数配对**（FinalX/Y 与 body 锚 bx/by 首次合成时整体缓存，后续帧恒定恢复——`_chairFrozenPos` 四元组）；②frozen key 改 **ZIndex**（每层稳定，PiecePath 跨动画帧会 miss）。
- **验证方法教训**：effect 是 15 帧动画（雪花/篝火），像素 diff 无法区分「正常动画」与「椅子层位移」——静态 PNG 对比法失效，**需实机观察**（或对 chair 基础层单独渲染对比——探针 out/ 下有分层输出 `chairrender_3019764_sit_0.png`（chair 层）与 `_3x.png`（含 effect））。
- 验证：build 0 错误，**445 passed / 0 failed**。

### 7.31 「sceneObj 固定基准」实验回退（2026-09-16，未提交）

- 胶水实测「花田被改出问题」→ **回退合成循环的 sceneObj 固定基准锚实验**（fx/fy/frame0 锚 + frozen 三元组画位），恢复原合成（全部部件统一 bx/by + mv）。
- 保留：`_chairFrozenPos` 字典与 GetBodyFrameAnchor helper（不再被合成消费，供下轮参考）；跳抓/绳窗口/Warp/退图顺序等其余修复不受影响。
- 445 全绿保持。呼吸抖动问题的正确修法需先精读 MapleSalon2 的坐标分层语义（effect position 与 body navel 的关系），下轮专门做。

### 7.32 位置冻结实验回退（2026-09-16，胶水实测「还在抖动」）

- **回退**：`_chairFrozenPos` 位置冻结实验（FinalX 恢复为帧 0 值但减数 bx 随呼吸帧变 → 不匹配组合 = 抖动根源）。恢复原始合成（FinalX 链式每帧 + bx 每帧 → 椅子跟 body 呼吸一致摆，自然）。
- 验证：build 0 错误、445 全绿、蚂蚁洞 300↔301 互穿+爬绳 330 帧、105010300 全图渲染正常（探针 --full）。
- GUI 已重启（胶水实测）。

### 7.33 枫树山丘 10000 渲染失败复现（2026-09-16，未修）

- **胶水实测**：背景设置页选「枫树山丘 [10000]」→ 黑屏 +「⚠️ 地图渲染失败」。
- **探针复现**：`--full 10000` → 渲染成功但输出**纯黑单色图**（800×1050，颜色数=1）；`--thumb 10000` → **LoadMap OK 但 Footholds=0 段**、CompositeMapThumbnail 返回 1159B（近空）。
- **根因方向**：10000 是 **5 位短 id**——`GetMapWzPath` 按首字符选分片（'1'→Map1），加载「成功」但内容不完整（段0）。5 位短 id 的分片定位/加载处理需专项修（历史遗留，非本轮引入——附带发现工具：`--thumb <mapId>` 复刻设置页渲染路径、`--reach/--navel` 诊断命令已入探针）。
- 注意：10000 与行走遍历无关（行走图集为 9 位 id），此为背景设置页功能缺陷。

### 7.34 枫树山丘 10000 根因定位（2026-09-16，未修）

- **String 表实锤**：`--findmap` 按 id/name 扫 String/Map.img → **「枫树山丘 = 10000」（5 位短 id）**，另有 110000 同名。
- **加载缺陷复现**：`--thumb 10000` → LoadMap「成功」但 **Footholds=0**、CompositeMapThumbnail 输出 1159B 近空 → 设置页黑屏+「地图渲染失败」。
- **根因方向**：`GetMapWzPath` 按首字符选分片（'1'→Map1）且按散 img 探测——**5 位短 id 的 img 打包在 Map0.wz 等分片 wz 内**（非散文件），当前加载路径探测不到 → 返回空壳 map。
- **修法（下轮）**：LoadMapCore 对 9 位补零路径 miss 时，回退尝试「短 id 原样」与「其他分片 wz 包内」的 img 节点解压（走 GetExtractedImgNode 同口径）；或预建「短 id → 实际分片」探测。附带：探针新增 `--findmap <关键词>`（按名称找图）、`--reach`、`--navel`、`--thumb`、`--route` 诊断命令。

### 7.35 椅子特效动画不流畅修复（2026-09-16，未提交）

- **根因**：`effectFrame = Math.Min(frame, effectFrames-1)`——椅子特效帧号直接**绑 body 动画帧号**：
  - body stand1 3 帧 × ~100ms = 300ms/圈，effect 12 帧（WZ delay 110ms/帧 ≈1.3s/圈）→ **播放加速 4 倍**、帧间跳变不均 = 不流畅
  - 坐姿（sit 单帧）时 frame 恒 0 → 特效完全静止
- **修复**：椅子特效**独立 delay 时钟**（`AdvanceChairEffectClock`）——effect 帧按 WZ 自带 delay 推进（`{eff}/{frame}/delay`，缺省 110ms 对齐 MapleSalon2/WzComparerR2），循环播放；切椅子/特效层重置时钟；带 delay 缓存（`_chairEffectDelayCache`）。
- 验证：build 0 错误，**445 passed / 0 failed**。

### 7.36 椅子特效 delay 三修（2026-09-16，未提交）

- **WZ 实测**：花田椅 3019632 effect 12 帧每帧 delay=210ms（明确配置，非缺省）。
- **三连修**：
  1. 独立 delay 时钟 v1：`_chairEffectClockUtc = now` **漏加 delay** → 每 33ms UI 帧都推进 = 闪烁；
  2. 预览/主窗口共享 PaperdollService 时钟被双重推进 → 快进乱跳；
  3. 无状态分片改 `DateTime.UtcNow.Ticks`（Windows 精度 ~15.6ms）→ 帧切换时机跳变 = 卡顿。
- **最终版**：无状态时间分片 + 高精度时钟 `Environment.TickCount64 % total`（幂等、无时钟竞争、无精度跳变）；缺失帧自动跳过。
- 验证：build 0 错误，445 passed / 0 failed。

### 7.37 椅子特效动画「卡顿不播放」真根因：sources 缓存冻结时间片（2026-10-14，未提交）

- **现象**：3019632 花田椅坐下后特效不动/偶尔跳帧（§7.35/7.36 的 delay 时钟修了时序，但没修到病根）。
- **实证**：新探针 `--chairanim <itemId> [action] [frame]`——同一 (action, frame) 350ms 间隔连续渲染 8 次
  （3019632 单帧 delay=210ms，2.8s 跨 ~13 个时间片）：**修前 8 帧 PNG md5 全同 = 冻结实锤**。
- **根因**：`CollectPiecesForFrame` 的 sources 缓存 key = `hash|action|frame|expression` **无时间维度**，
  而椅子特效帧号是 §7.36 改的「无状态时间分片」——首帧算好存进缓存后，后续全部命中旧时间片：
  - sit 单帧动作（key 恒定）→ 特效**永久静止**；
  - stand1 三帧循环 → 只在 body 换帧/表情变化时 key 变 → 特效**随机跳变**（卡顿感）。
- **对照参考实现**：MapleSalon2 `BaseAnimatablePart` 挂 pixi Ticker 连续推进（每帧 delay 独立、位置每帧
  重算 −origin），渲染列表从不按动作帧缓存；minipet-esp32 服务端 `Math.Min(frame,…)` 的帧号由外部
  调用方持续递增——两者特效播放都与缓存无关。MiniPet 时序逻辑已对齐，缺的只是让缓存感知时间。
- **修复**（MiniPet/Services/PaperdollService.cs）：
  1. `CollectPiecesForFrame`：带椅子时把各特效层当前时间片帧号编进缓存 key（`|cf0.3.7`）；
     帧号本调用内一次计算（`ComputeCurrentChairFrames`）并传参 `AddChairPieces` → key 与部件路径同源不漂移；
     无多帧层的椅子 key 保持原样（不引入时间维度）。热路径零 WZ 锁（帧数/delay 全走缓存）。
  2. `GetBounds` 改 `sampleAllChairFrames: true`（key 后缀 `|cfall`）：椅子特效 **union 全部帧**——
     3019632 各帧 241x173~207、origin y 160~194 均不等，按单帧采样其他帧会裁边/画布随时间抖。
  3. `WarmupFor`：带椅子时追加预热 `sit` 动作 + chair 类部件位图预解码（All 模式顺带预解码全部
     特效帧，切椅子首帧不触发 12 帧冷解码持锁）。
- **验证**：build 0 错误；**445 passed / 0 failed**；`--chairanim 3019632 stand1 0` 修后 8 帧 8 个相位
  （花瓣逐帧飘动、人物位置稳定无层抖，out/chairanim_3019632_*.png）。
- **遗留提示**：离线条（BuildOfflineStrips）仍烘焙当前时间片（离线兜底为静态图，可接受）；
  装备节点级特效（AddEquipEffectPieces）传原始帧号，坐姿时同样冻结——本轮不动，下轮如需再对齐。

### 7.38 椅子动画缓存修复验证 + 摄像机排查 + 历史菜单中文缺失修复（2026-10-14，未提交）

- **§7.37 实机验证**：胶水确认椅子动画恢复（此前「没看到区别」是旧进程未重启——运行中进程不热加载新二进制，改完必须重启 app）。
- **摄像机「无法关闭/大小不生效」排查**：三层验证（Avalonia 12.1 最小复现 app：Hide/resize 正常；真实 app 钩子自动驱动 开→缩放→关：原生窗口状态经 CGWindowList 外部确认全部生效；入口/事件链审查无缺陷），用户配置 mapId=10000 + mapMode=2 + 600×450 下全程零异常——**未发现代码缺陷**，胶水重启后自愈（疑似旧进程/旧二进制状态）。排查用临时钩子与 [BgDiag] 日志已全部移除。
- **历史菜单中文缺失修复**（截图：历史项显示 `10000 [10000]`）：
  - **根因**：settings.json mapHistory 存量坏数据——`Name` 字段存成了 id 本身（10000/1000000），展示链路「Name 非空即用」→ 中文永远不查。WZ 数据本身完好（`String/Map.img/maple/10000` = 彩虹岛：枫树山丘，短 id **不补零** key，探针 --namediag 实测）。
  - **修复**：
    1. `MapCatalogService.GetMapName` 短 id 双查：miss 时试 `PadLeft(9)` 与 `TrimStart('0')`（key 格式随 WZ 版本差异）；
    2. 新纯函数 `MapCatalogService.ResolveDisplayName(id, storedName, lookup)`：存量名有效（非空、≠id、非 map_ 哨兵）→ 用存量；否则查缓存；全失败回退 `storedName ?? id`（WzService 转发）；
    3. 应用 5 处：右键菜单历史项 / 设置历史列表 / 设置历史点击 / BgApplySelection / **ApplyMapSelection 写历史前重解析**（坏数据不再自我延续）。
  - **验证**：--namediag 端到端（短 id/9 位补零/坏存量名 三形态全部返回中文名）；build 0 错误；**450 passed / 0 failed**（新增 MapDisplayNameTests 5 断言）。
- 探针新增：`--namediag`（地图名考证）。
