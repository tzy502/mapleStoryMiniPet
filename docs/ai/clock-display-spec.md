# WZ 地图时钟显示规格（开发需求文档）

> 固化日期：2026-09-24（定稿）
> 适用：桌面版 V0.3.0 时钟需求（原 PENDING-v0.3.0-clock-pomodoro.md F19/F20）
> 与 F19（ESP32 硬件分支）待机时钟共用同一套素材与排布规则
> 实证来源：WzComparerR2 探针全量扫描 + 项目 MapService 渲染验证

## 一、素材（全部实测，数据从 WZ 走）

| 素材 | 路径 | 规格 |
|---|---|---|
| 数字 | `Map/Obj/etc.img/clock/fontTime/{0..9}` | 26×35 |
| 上下午 | `Map/Obj/etc.img/clock/fontTime/{am,pm}` | 22×20（同位置显隐切换） |
| 分隔符 | `Map/Obj/etc.img/clock/fontTime/comma` | 17×35（视觉=冒号） |

- **origin 全部为 (0,0)**（实测），按固定像素位排布即可
- 素材为 **outlink 形式**（源节点 1×1 占位 + `_outlink` 指向 `_Canvas`）——`WzService.ExtractPng` / `GetOrigin` 已含解析链，直接调用
- 备选素材（本轮未采用，留档）：`Obj/clock.img/number`、`etc.img/clock1`（127×50 带底板）、`houseTC.img/house5/clock`（6 帧挂钟）、`UI/UITimer.img`

## 二、显示排布规则（定稿 2026-09-24）

```
显示内容（12 小时制）：  [am|pm] + 时十位 + 时个位 + comma + 分十位 + 分个位
绘制起点（世界坐标）：  X = clock.x + 18 + 3      ← 18 = 官方固定偏移；+3 = 胶水确认的右移微调
                        Y = clock.y + 83          ← 官方固定偏移
排布顺序与间距：        am|pm(22) → [GAP=12px] → H1(26) → H2(26) → comma(17) → M1(26) → M2(26)
                        数字之间无额外间距；仅 AM/PM 与时间数字之间留 12px
```

**定稿参数（固化）**

| 参数 | 值 | 说明 |
|---|---|---|
| 官方偏移 | +18 / +83 | 来自官方客户端时钟机制说明 |
| `AMPM_GAP` | **12px** | AM/PM 与后接时间的间距（定稿） |
| `CLOCK_X_SHIFT` | **+3px** | 整组（含 AM/PM）右移微调（定稿） |

## 三、时间规则（对齐官方客户端）

1. 内部按 24 小时制，显示转 12 小时制 + AM/PM：0~11 = AM、12~23 = PM、13~23 显示减 12
2. **午夜 0 点显示 `AM 00:xx`**（官方原样，不做 `AM 12:xx` 修正）
3. 时分固定两位补零
4. comma 闪烁：**偶数秒显示、奇数秒隐藏**（1 秒周期），时间走时由本地计时器推进
5. 位置属地图场景层（跟随镜头），非固定屏幕元素

## 四、地图 clock 配置

字段（已全量实测定型）：

```
clock/ x, y                  ← 锚点坐标（配合 +18/+83 偏移）
       width=200, height=200 ← 显示区域
       page                  ← 地图分层页（1/2/6）
       z                     ← 层内 z 顺序
       countdown=1           ← 仅 899000000（离别之山）：倒计时模式
```

**含 clock 配置的地图共 26 张**（全量扫描 21,422 张命中，全部为码头/售票处/升降场类）；
完整清单（地图名 + 坐标 + 字段）见 `~/Desktop/wz-clock-maps.md`。

- 最典型：200000100 天空之城售票处、220000100 玩具城售票处、240000100 神木村售票处、104020110 金银岛升降场、200000111 神秘岛码头
- **注意**：clock 坐标与地图上的"显示屏面板 obj"没有固定包含关系（实测面板在 `toyCastle/station/15`、`house10/basic/2` 等 obj 上，而 clock 可能落在面板外 100px+），因此**必须严格按官方偏移公式绘制，不能改为"按面板居中"**

## 五、验证与出图工具

- 出图工具：`~/mapprobe_backup/`（探针工程，**直接 Compile Include 项目 MapService/WzService 等源文件** + SkiaSharp 本地 dll + StubLoader 替身）
  - 流程：`LoadWz("", 数据目录)` → `LoadMap(id)` → `ClampCamera` → `RenderViewport(zoom=1, viewTime=0, 1080×620)` → 叠加时钟数字 → PNG
  - 关键：**必须调 `ClampCamera`**（否则相机越界、back 层露底）；缩放画布勿与未缩放坐标混用
- 验收标准：渲染图中时钟数字应落在该地图的显示屏面板上（玩具城/天空之城已通过）
- 输出目录：`~/Desktop/clockshot/proj_*.png`

## 六、待开发项（给实现阶段的输入）

1. 桌面版：MapService 渲染含 clock 配置的地图时，按本规格叠加时钟（走时 + comma 闪烁）
2. F19 设备端：待机时钟用同一套素材与排布参数（时间源 = PCF85063 RTC，断网可走时）
3. 899000000（countdown=1）需单独的倒计时分支，非普通走时
