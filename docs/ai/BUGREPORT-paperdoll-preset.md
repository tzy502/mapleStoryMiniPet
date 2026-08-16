# BUGREPORT: 纸娃娃预设「新 建」无效果 + 纸娃娃链路 Code Review

_报告日期：2026-08-15 · 来源：DSH agent code review（只读分析，未改动代码）_
_涉及文件：`MiniPet/Views/SettingsWindow.Paperdoll.cs`、`MiniPet/Services/PaperdollService.cs`、`MiniPet/Services/ConfigService.cs`_

---

## 一、主诉问题：「预设 点击新增 没有效果」

### 根因

`SettingsWindow.Paperdoll.cs:359` `NewPaperdollPreset()`：

```csharp
private void NewPaperdollPreset()
{
    try { _paperdollCurrent = CreateDefaultAppearance(); SyncInputsFromPaperdoll(); UpdatePaperdollPreview(); } catch ...
}
```

**「新 建」的语义是"把当前编辑外观重置为默认"，不是"向预设列表新增一项"**（列表 `_paperdollPresets` 完全不动）。因此：

1. **必现路径**：打开设置窗口时当前外观即默认外观（`SettingsWindow.axaml.cs:117` 初始值 = `CreateDefaultAppearance()`），点「新 建」→ 外观、下拉、按钮文本、预览全部零变化 → 表现为"没效果"。
2. **预览异步竞态**（`SettingsWindow.Paperdoll.cs:470-488`）：`Task.Run` 后台合成无取消、无版本校验。从复杂外观重置为默认时，旧外观合成慢、后完成，把新预览覆盖回去 → 有变化也被吞。
3. **预览静默失败**：WZ 未加载 → `if (!_wzService.IsLoaded) return;` 预览永远显示占位符 🧑；素材缺失 → `PaperdollService.CompositeFrame` 返回 null → 预览不更新（错误仅打 Console.Error，用户不可见）。
4. **皮肤下拉副作用**：`SyncInputsFromPaperdoll` 设置 `_skinCombo.SelectedIndex` 可能触发 `SelectionChanged` → 再次 `UpdatePaperdollPreview`，与 2 叠加竞态。

### 建议修复方向

- 方案 A（对齐按钮名）：「新 建」改为"新建空白预设（默认外观）并加入列表 + 选中"。
- 方案 B（保留重置语义）：重置后状态栏提示「已重置为默认外观」（`_statusBarText` 已有），并在默认外观下也强制刷新一次预览（清掉旧合成结果）。
- 无论哪种：预览后台任务加版本号（`_previewSeq`），Post 前校验，过期结果丢弃。

---

## 二、Code Review Bug 清单（按严重度）

### 🔴 功能级

| # | 位置 | 问题 | 修复方向 |
|---|---|---|---|
| B1 | `SettingsWindow.Paperdoll.cs:359-362` | 「新 建」不新增列表项，默认状态零反馈（主诉） | 见上 |
| B2 | `SettingsWindow.Paperdoll.cs:470-488` | 预览 `Task.Run` 竞态：无取消/序号校验，旧合成覆盖新结果 | `_previewSeq` 版本号 + Post 前校验 |
| B3 | `ConfigService.cs:86` | `SaveSettings` 非原子写（`File.WriteAllText` 直写）：崩溃/断电损坏 settings.json → 预设全丢（项目已有 settings.json.bak 损坏先例） | tmp 文件 + `File.Replace`（或先写 tmp 再 move） |
| B4 | `SettingsWindow.Paperdoll.cs:369-371` | 重复保存同名单覆盖：`SavedPaperdolls[name] = clone` 无重名处理，磁盘互相覆盖 + 列表重复项 | 重名自动追加序号（`预设 2`、`预设 3`） |
| B5 | `SettingsWindow.Paperdoll.cs:435-437` | 刷新列表 `ItemsSource = null` 再赋值，保存后选中项被清空 | 刷新后恢复原选中（或不清空 ItemsSource 直接刷新项） |

### 🟡 中等

| # | 位置 | 问题 | 修复方向 |
|---|---|---|---|
| B6 | `SettingsWindow.Paperdoll.cs:506-511` | `CreateDefaultAppearance` 不设 `BodyId`（=0，靠 `BodyNumber` 回落 2000/2001）：性别切换后皮肤下拉显示与实际渲染 body 不一致 | 默认显式 `BodyId = 2000` |
| B7 | `SettingsWindow.Paperdoll.cs:457` | 预览摘要漏 4 部位：长袍(Overall)/饰品(Earring)/坐骑(Mount)/椅子(Chair) | parts 数组补全 |
| B8 | `SettingsWindow.Paperdoll.cs:513-531` | `CloneAppearance` 非 Hair/Face 部位只克隆 `Id`：Cap 等部位的 Dye/EnableEffect/Visible 丢失 | 统一用 `CloneItemFull` |
| B9 | `SettingsWindow.Paperdoll.cs:385-394` | 未选中列表项点「加 载」静默 return，无提示 | 状态栏提示「请先选择预设」 |
| B10 | `PaperdollService.cs` 多处 | WZ 调用不遵守 `WithWzLock` 约定：`GetBounds`(:676)、`CollectPiecesForFrame`(:328/333)、`GetMeta`(:702)、`GetBmp`(:714) 直接调 `FindNodeByPath`/`ExtractPng`，仅靠 WzService 内部 `lock(_wzLock)` 单点保底；与 `GetActionFrameCount`(:828) 锁内遍历复合操作非原子 | 复合遍历包 `WithWzLock`；至少 `GetBounds` 全帧循环整体加锁 |
| B11 | `SettingsWindow.Paperdoll.cs:470-488` | 窗口关闭后 `Task.Run` 仍 `Dispatcher.UIThread.Post` 到已释放对象（异常被吞） | Task 前检查窗口 `IsVisible`/IsClosed，Post 包 try |
| B12 | `SettingsWindow.Paperdoll.cs:199-202` | 染发滑块改值不实时刷新预览（需点「应用染发」），交互割裂 | 滑块 onChanged 末尾 `UpdatePaperdollPreview()`（或保持现状但按钮文案提示） |

### 🟢 轻/风格

| # | 位置 | 问题 |
|---|---|---|
| B13 | `SettingsWindow.Paperdoll.cs:345` | `EquipBtnText` 死代码，删除 |
| B14 | `PaperdollService.cs` 全文件 | 大量调试 `Console.WriteLine`（zmap 每次加载打日志、一次性诊断标记），收敛或加开关 |
| B15 | `SettingsWindow.Paperdoll.cs:143-145` | 按钮文本带空格「新 建」，如非排版故意则去掉 |
| B16 | `SettingsWindow.Paperdoll.cs:421-434` | `RefreshPresetList` 的 `Count==0` 判定导致空预设时每次刷新重读磁盘 |

---

## 三、验收标准（修复完成后）

1. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug` **0 错误**。
2. `dotnet test MiniPet.Tests`（或项目既有测试命令）全绿；纸娃娃相关测试（`PaperdollServiceTests.cs`、`ConfigServiceTests.cs`）不回归。
3. 手动验证：
   - 打开设置 → 纸娃娃 tab → 点「新 建」：默认状态也有明确反馈（新列表项或状态栏提示），预览不残留旧外观。
   - 「保 存」两次 → 列表出现「预设 1」「预设 2」不互相覆盖；重启后预设仍在（settings.json 完好）。
   - 选中预设 → 「加 载」生效；未选中 → 有提示。
   - 快速连点「新 建」/切皮肤/切性别：预览最终一致（无旧帧覆盖）。
4. 不改变既有行为：预设数据结构、序列化格式、其他 tab 功能不回归。
