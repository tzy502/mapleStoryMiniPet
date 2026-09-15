# V0.2.1 开发进度交接（2026-09-14）

> 本文件由本轮 harness 整理，供**下一个 harness 接手**。全部内容来自实际代码与实跑验证，未验证的会明确标注。
> 分支：`feature/0.2.1`（HEAD = `a344c66`，**本文件所有改动尚未提交**）

## 0. 一句话状态

F18「行走模式」已跑通：**播放音乐 → 选图 → 图搜索规划路径 → 行走/爬绳/跳跃 → 走到传送门淡入淡出切图**；没有可传送的门时走「半程折返」。
**两个 P0 未解决**：① 曲目→地图反向索引首次扫描太慢，导致**地图总兜底到默认城镇**；② 人物**频繁瞬移**（折返阈值/时机）与绳下徘徊。

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
