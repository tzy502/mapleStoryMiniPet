# PLAN · 桌宠纸娃娃表情功能（V0.2.x 候选）

> 胶水提问：「桌宠支持表情吗？比如笑之类的，需要介入。参考其他项目先出方案」
> 本文只做调研与方案，未改任何业务代码（f18-probe 目录内新增了 `--face`/`--faceacc` 临时诊断命令）。
> 2026-09-24 · 调研模型：GLM（ZCode subagent）

---

## 1. 现状结论

**桌宠目前不支持表情切换——脸永远只有一个表情（default），连眨眼都不会。**
但渲染管线已经把「表情」这条路修好了，只差驱动：

- `MiniPet/Services/PaperdollService.cs` 的表情链路**已完整存在**：
  - `RenderFrame(action, frame)` → `ResolveExpression()`（L2227）→ `ExpressionDriver?.GetExpression() ?? "default"`；
  - `IExpressionDriver` 接口已预留（L2501，注释写明「未来 Hermes/Claude/Codex 按 agent 状态注入」）；
  - `CollectPiecesForFrame` 的缓存 key **已含 expression**（L453 `{hash}|{action}|{frame}|{expression}`）；
  - `AddExpressionPieces`（L584）已实现两种 WZ 表情结构：数字帧取模（`frame % numericFrames`）与 default 直接 piece；
  - `ResolveFaceAction`（L1257）已实现**按部件回退**：请求表情在脸型 img 里不存在 → 回退 blink → 回退 default。
- **表情恒为 default 的原因**：当前唯一的 `IExpressionDriver` 实现是 `EffectLayerService.DefaultExpressionDriver`（EffectLayerService.cs L312），`GetExpression()` 恒返回 `"default"`。全仓库没有任何地方调用表情切换，也没有眨眼定时器。
- **101 面饰（faceaccessory）已随表情联动**：`AddAccessoryPieces` 无动作结构时走 `AddExpressionPieces`，传入与脸型**同一个 expression 参数**——表情一换，面饰跟着换帧，零额外改动。
- AnimService 的 `Play(action, PlayMode)` / `SwitchTo(action)` 有成熟 OneShot 语义（播完自动回 stand、pending 合并），但它驱动的是 **body 动作**维度（swing/laugh 等），不是脸表情维度；表情播放不经过 AnimService。
- 右键菜单 `PetWindow.Menu.cs` 的 `RefreshAnimationMenu()`（L630）是「Opening 时动态重建子菜单」的成熟样板，表情菜单可照抄。
- 行走模式（`Services/Walk/WalkModeService.cs`）只调度 body 动作（stand1/walk1/walk2/ladder/rope/jump）与窗口位移，与表情零耦合；`PaperdollService.Flip` 管转向。
- 气泡：`BalloonService.Show(text, data, durationMs, bold, targetPetId)`；随机气泡语料是 `PetWindow.axaml.cs` L68 的 `_randomLines` 硬编码 31 条，8~20s 调度 + 30% 静默轮 + 仅 Agent Idle 触发。
- **多宠隔离前提已满足**：`App.axaml.cs` L66-73 明确 `PaperdollService`/`AnimService` 都是 **Transient（每宠一实例）**——表情状态放 PaperdollService 实例字段即天然 per-pet。

结论：**不需要动渲染管线，只需要给已有的 expression 入口加一个「会说话的驱动」**。

---

## 2. WZ 实测表情清单

数据源：`/Volumes/SSD/mxd/mxd/Data`（Character/Face.wz 打包），用 `prototypes/f18-probe --face` 实测。
脸型总量：**6556 个** img（0002xxxx）；抽查 00020000 与 00020003，**表情集完全一致（全集标准化）**。

每脸 25 个表情节点（24 表情 + default），帧结构两种：
- `default`：无数字帧，直接 `default/face`（piece 名 `face`，带 origin / map.**brow** / z / _outlink）；
- 其余：`{expr}/{frame}/face` 数字帧；单帧表情**无 delay**（静态）；多帧表情每帧带 delay。

| # | key（WZ） | 中文名 | 帧数 | delay/帧 | 备注 |
|---|-----------|--------|------|----------|------|
| 1 | default | 默认 | 静态 1 图 | - | 常驻 |
| 2 | blink | 眨眼 | 3 | 60ms | 唯一高频动画表情 |
| 3 | hit | 受击（F1） | 1 | - | |
| 4 | smile | 微笑（F2） | 1 | - | 胶水要的「笑」 |
| 5 | troubled | 烦恼（F3） | 1 | - | |
| 6 | cry | 哭（F4） | 1 | - | |
| 7 | angry | 生气（F5） | 1 | - | |
| 8 | bewildered | 慌张（F6） | 1 | - | |
| 9 | stunned | 发晕（F7） | 1 | - | |
| 10 | vomit | 呕吐 | 2 | 120ms | |
| 11 | oops | 哎呀 | 1 | - | |
| 12 | cheers | 欢呼 | 1 | - | |
| 13 | chu | 亲亲 | 1 | - | |
| 14 | wink | 俏皮眨眼 | 1 | - | |
| 15 | pain | 痛苦 | 1 | - | |
| 16 | glitter | 闪亮 | 2 | 150ms | |
| 17 | despair | 绝望 | 2 | 150ms | |
| 18 | love | 爱心 | 2 | 150ms | |
| 19 | shine | 荣光 | 1 | - | |
| 20 | blaze | 怒火 | 2 | 110ms | |
| 21 | hum | 得意哼歌 | 2 | 110ms | |
| 22 | bowing | 鞠躬道歉 | 2 | 110ms | |
| 23 | hot | 热死了 | 2 | 110ms | |
| 24 | dam | 郁闷 | 2 | 240ms | |
| 25 | qBlue | 疑问蓝脸 | 1 | - | GMS 特有 |

F1~F7 键位（hit/smile/troubled/cry/angry/bewildered/stunned）即官方客户端表情快捷键（MapleSalon2 ExpressionSelect.tsx 同源）。

**101 面饰实测**（`Character/Accessory/01012000.img`，`--faceacc`/`--tree`）：
与脸型**同构**的表情结构 `{expr}/{frame}/default`（piece 名 `default`，带 origin/map/z），拥有与脸型几乎相同的表情组（少 qBlue）。
→ 面饰按表情换帧的数据齐全，现有 `AddExpressionPieces` 收集路径直接可用。

---

## 3. 参考项目结论

| 项目 | 位置 | 关键实现 |
|------|------|----------|
| **MapleSalon2** | `maplestory-wiki-backend/MapleSalon2/src` | `const/emotions.ts`：25 值枚举（与 WZ 实测**完全一致**）+ `CharacterExpressionsOrder` 菜单序 + `ExpressionsHasEye`（default/hit/blink/wink/glitter 露眼，用于眼饰染色判断）；`renderer/character/characterFaceFrame.ts`：表情帧 `frame % frames.length` 取模、脸部件锚 body 帧 **brow** 锚点、isBackAction/isHideFace/invisibleFace 隐藏脸——这四点 MiniPet 已全部对齐实现 |
| **WzComparerR2** | `WzComparerR2C#/WzComparerR2.Avatar/AvatarCanvas.cs` | 表情清单 = **脸 img 子节点动态枚举**（排除 info，L129-133）；`GetFaceFrame`：default 特判直接帧、其余读 `{emotion}/{index}`（L396-428）；delay 缺省 **120ms**（L474）；坐骑帧可 `forceCharacterFace/forceCharacterFaceFrameIndex/forceCharacterFaceHide` **强制角色表情**（L491-493，MiniPet 未实现，见风险 R4） |

MiniPet 现有实现与两个参考项目在数据结构认知上完全同源，方案可以直接建立在现有管线上。

---

## 4. 方案设计

### 4.1 总体思路

表情是「脸图层参数」，不是动作：**不进 AnimService 动作状态机**，在 PaperdollService 内部加一个轻量表情状态 + 时长控制，经现有 `ResolveExpression()` 注入现有渲染路径。改动集中在 3 个文件：

```
PaperdollService.cs   表情状态 + PlayExpression API + 眨眼/随机表情时钟（驱动层）
PetWindow.Menu.cs     右键菜单「表情」子菜单（交互层）
PetWindow.axaml.cs    表情×气泡配对（联动层，可选）
```

### 4.2 渲染实现（PaperdollService）

```csharp
// 新增状态（实例字段；PaperdollService 是 per-pet Transient，天然多宠隔离）
private volatile string _manualExpression = ExpressionDefault;   // 手动/自动播放中的表情
private DateTime _manualExpireUtc = DateTime.MinValue;           // 播放截止时刻
private DateTime _nextBlinkUtc = DateTime.MinValue;              // 下次眨眼时刻（可选开关）

// 公开 API
public void PlayExpression(string expr, int durationMs = 3000);  // 播表情 N ms 后回默认
public void CancelExpression();                                   // 立即回 default
public static readonly (string Key, string Cn, bool Friendly)[] KnownExpressions = ...; // §2 清单

// ResolveExpression() 优先级调整（关键一处改动）：
//   手动表情未过期 → _manualExpression
//   否则 → ExpressionDriver?.GetExpression() ?? default   （保留 agent gateway 语义）
private string ResolveExpression()
{
    if (_manualExpression != ExpressionDefault && DateTime.UtcNow < _manualExpireUtc)
        return _manualExpression;
    try { return ExpressionDriver?.GetExpression() ?? ExpressionDefault; }
    catch { return ExpressionDefault; }
}
```

- **帧推进**：沿用现状——表情帧号 = body 动画帧号取模（`frame % numericFrames`，与 MapleSalon2 一致）。30fps 下 2~3 帧表情会按 body 帧节奏循环（stand1 一轮 1.5~2s → 眨眼约 2s 一次、love 约 1s 一循环），可接受；V2 增强再做表情独立 delay 时钟（见 §6 M4）。
- **缓存**：`_frameSourcesCache` key 已含 expression，不同表情各占条目、换表情即查新 key，零改动。`SetAppearance` 切外观时顺带 `CancelExpression()` 复位。
- **101 面饰联动**：零改动（`AddAccessoryPieces` → `AddExpressionPieces` 已传同一 expression；某面饰缺该表情节点时 `ResolveFaceAction` 自动回退 blink/default）。
- **眨眼循环**（推荐做，最像「活着」）：空闲时每 3~6s 随机播一次 blink ≈180ms（走 `_manualExpression="blink"`、时长 200ms 的同一路径）。行走/攀爬中允许眨眼、禁其它表情。

### 4.3 交互（PetWindow.Menu.cs）

- 右键菜单在「切换动画」后新增 **「😊 表情」子菜单**（仅纸娃娃哨兵模式显示；mob/NPC 模式隐藏），照抄 `RefreshAnimationMenu()` 的 Opening 重建模式：
  - 按 MapleSalon2 `CharacterExpressionsOrder` 列 24 项，Header 格式 `微笑 (smile)`（中文名 + key）；
  - 点击 → `_paperdoll.PlayExpression(key, 3000)`，**不打断当前 body 动作**（表情只换脸层）；
  - 底部加「恢复默认」项 → `CancelExpression()`；
  - 分隔线下「自动眨眼」勾选项（默认开）与「随机表情」勾选项（默认关），状态存 `ConfigService`。
- 不做 F1~F7 全局热键（桌宠无焦点，易与用户冲突；菜单即够用，后需可加）。

### 4.4 自动表情策略

| 场景 | 策略 |
|------|------|
| 待机（stand 系） | 眨眼循环常驻；「随机表情」开启时每 20~45s 播一个友好表情（smile/wink/cheers/love/glitter/hum/chu/oops），时长 3s |
| 负面表情 | 不进随机池（pain/dam/cry/despair/vomit/blaze/hot/stunned/troubled/angry/bewildered/hit 仅菜单手动） |
| 行走/攀爬（F18 行走模式） | 只保留眨眼；正在播放的临时表情自然到期回 default，不强制打断 |
| 拖拽/点击 | 不触发随机表情（复用 `_isDragging` 判定） |
| Agent 忙碌（Run 态） | 手动表情让位：`ResolveExpression` 中 driver 值生效（agent 未来可经 gateway 指定「专注脸」），随机表情暂停 |

### 4.5 表情 × 气泡联动（可选，推荐）

- `_randomLines` 从 `string[]` 升级为 `(string Text, string Expr, int ExpireMs)[]`，31 条配对如：
  `"嘿嘿~"→smile`、`给你一个么么哒~→love`、`要不要听我讲个笑话？→hum`、`累了就休息一下吧→sleepy(→blink)`；
- `TryShowRandomBalloon()` 弹气泡同时 `_paperdoll.PlayExpression(expr, 8000)`——表情与气泡同屏出现、气泡 15s 先走、表情 8s 收回默认，节奏自然；
- 进阶（另一条线，不在本计划内）：EffectLayerService 状态气泡（Jump/Failed/Review）发布时同步 `PlayExpression`，或未来 Hermes gateway 按 agent 情绪驱动 `IExpressionDriver`——接口已备好。

---

## 5. 工作量评估（小时级粗估）

| 里程碑 | 内容 | 估时 |
|--------|------|------|
| **M1 手动表情** | PaperdollService 状态+PlayExpression/CancelExpression+ResolveExpression 优先级（~1h）；菜单子菜单+中文名+refresh（~1.5h）；联调+验收（~1h） | **~3.5h** |
| **M2 自动表情** | 眨眼循环（~1h）；随机表情时钟+行走/拖拽/Run 抑制（~1h）；ConfigService 开关持久化+菜单勾选（~0.5h） | **~2.5h** |
| **M3 气泡配对** | `_randomLines` 结构化+配对表+TryShowRandomBalloon 接线 | **~1.5h** |
| M4 增强可选 | 表情帧独立 delay 时钟（脱离 body 帧节奏，需全动作回归） | ~3h |
| 合计（M1~M3） | | **~7.5h**（含 M4 ~10.5h） |

---

## 6. 风险

| # | 风险 | 评估与对策 |
|---|------|-----------|
| R1 | **表情帧节奏随 body 动作**：取模跟随 body 帧，walk1（~100ms/帧）下 2 帧表情 200ms 循环偏快、body 帧少的动作下偏慢 | V1 接受（= MapleSalon2 行为）；不满足再上 M4 独立 delay 时钟 |
| R2 | **脸饰/帽子遮挡层级** | 低风险：表情只换 face 层**内容**，zmap 层级/遮挡锁（vslot）不变；face 层次（hairOverHead/cap 之下）与背面隐藏（FrontFaceLayers 含 accessoryFace）均与表情无关，现有验收点不受影响。唯一注意：`invisibleFace` 面罩覆盖脸型期间表情不可见——数据规则，非 bug |
| R3 | 多宠独立表情 | 已满足：PaperdollService 为 per-pet Transient（App.axaml.cs L66），表情状态在实例字段；但 **EffectLayerService 只绑主宠**（L150），随机/自动表情在每宠 PaperdollService 内各自跑即可，勿走全局单例 |
| R4 | 坐骑 forceCharacterFace（WzComparerR2 支持坐骑强制角色表情/隐藏脸） | MiniPet 未实现；骑乘时表情可能被坐骑帧视觉叠加怪异。V1 忽略（本 WZ 坐骑数据未验证含此节点），列出待 probe 验证 |
| R5 | 离线条带不含表情：`BuildOfflineStrip` 等路径固定 `ExpressionDefault`（L1949/2278/2320），WZ 断线回退图条时表情播放无效 | 已知限制，可接受（离线兜底本来就是降级渲染）；文档标注即可 |
| R6 | 「修 A 坏 B」 | 硬规矩①：渲染路径动了 `ResolveExpression()`，完成后必须重跑纸娃娃全部既有验收点（层解析 CapeProbe、面饰显示、背面动作无脸、invisibleFace、坐骑/椅子动作、预览草稿合成——`CompositeFrame` 默认参数仍是 default 不受影响） |

---

## 6.5 实现进度（2026-09-24 更新）

- ✅ **M1 手动表情**
  - `PaperdollService`：`PlayExpression(expr, durationMs)` / `CancelExpression()` / `CurrentExpression` / `IsExpressionActive` / `KnownExpressions`（25 项，含中文名 + Friendly 标记）；`ResolveExpression()` 改为「手动未过期 → 手动，否则 agent 驱动」；`SetAppearance()` 内 `CancelExpression()` 复位
  - 右键菜单：`😊 表情` 子菜单（`PetWindow.Menu.cs` 的 `RefreshExpressionMenu()`，仅纸娃娃显示；中文名 + key；末尾「恢复默认」）
- ✅ **M2 自动表情**
  - `TickExpressionClock()`（`PetWindow` 33ms 定时器驱动）：自动眨眼 3~6s / 180ms；随机表情 20~45s / 3s，池 = `Friendly` 表情
  - 抑制：行走模式激活时 `SuppressRandomExpression = true`（只留眨眼）
  - 开关落盘：`PetSettings.AutoBlink`（默认 true）/ `PetSettings.RandomExpression`（默认 false）+ `ConfigService.GetAutoBlink/SetAutoBlink/GetRandomExpression/SetRandomExpression`；菜单勾选写回配置，打开菜单时从配置同步
- ⏳ **M3 气泡配对**：进行中（`_randomLines` 结构化 + `TryShowRandomBalloon` 内 `PlayExpression` 接线）
- ⬜ **M4 表情帧独立 delay 时钟**：未做（§6 R1 的增强项，V1 先用 body 帧取模，与 MapleSalon2 一致）

---

## 7. 验收点（实现后逐条打勾）1. 菜单点「微笑」→ 脸变 smile，101 面饰（若佩戴 01012000 类表情结构面饰）同步换帧，3s 后回 default；
2. 点「眨眼」→ 播 3 帧眨眼动画后回 default；点「爱心」→ 2 帧循环 3s；
3. 佩戴不 love 表情节点的面饰 → 该表情期间面饰回退显示（blink/default），不消失；
4. 自动眨眼开 → 待机每 3~6s 眨一次，行走中仍眨眼；随机表情开 → 待机 20~45s 出友好表情，行走/拖拽/Agent Run 中不出；
5. 行走模式来回切图、爬绳（背面动作）→ 无正脸贴背（既有行为不回归）；
6. 两只桌宠各点不同表情 → 互不干扰；
7. WZ 断线 → 回退离线条带正常（表情降级为 default，不崩）；
8. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug` 0 错误。
