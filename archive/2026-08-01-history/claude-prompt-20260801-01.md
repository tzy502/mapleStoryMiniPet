# Claude Code 修复提示语 — 双窗口 z-order 优化

> 生成日期：2026-08-01
> 问题：地图背景窗口盖住桌宠精灵图，层级冲突

---

## 项目信息

| 项 | 值 |
|------|------|
| 项目根 | `/Users/a502/IdeaProjects/mapleStoryMiniPet` |
| 主项目 | `MiniPet/MiniPet.csproj` |
| SDK 路径 | `DOTNET_ROOT=/Volumes/SSD/C#` |
| 编译命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet build --no-restore` |
| 运行命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet run --project MiniPet/MiniPet.csproj` |

---

## 问题描述

当前架构是**双窗口**（保留，不改单窗口）：
- `PetWindow`：桌宠精灵图窗口，`Topmost=true`
- `BackgroundWindow`：地图背景窗口，`Topmost=true`

**Bug**：两个窗口都是 `Topmost=true`（同为 Floating 层级），macOS 对同级 Topmost 窗口的 z-order 排序不稳定，BackgroundWindow 经常反弹盖住 PetWindow。

现有补救手段是 `RaisePet()`（`PetWindow.axaml.cs:948-951`）：
```csharp
private void RaisePet()
{
    Topmost = false; Topmost = true; Activate();
}
```
这个翻转只在调用瞬间生效，之后系统随时重排，治标不治本。

---

## 修复方案：降级 BackgroundWindow 的 Topmost

**核心改动**：BackgroundWindow 的 `Topmost` 从 `true` 改为 `false`。

层级关系变为：
- BackgroundWindow：`Topmost=false`（Normal 层级）
- PetWindow：`Topmost=true`（Floating 层级）

Floating 层级天然 > Normal 层级，PetWindow **永远在 BackgroundWindow 之上**，不需要任何 z-order 抢救。

---

## 具体改动

### 改动 1 — BackgroundWindow.Topmost = false

**文件**：`MiniPet/Views/BackgroundWindow.axaml.cs`（或 BackgroundWindow 的构造函数/初始化处）

找到 BackgroundWindow 的 `Topmost` 设置，改为 `false`。如果构造函数里没有显式设置，则在构造函数中添加：
```csharp
Topmost = false;
```

### 改动 2 — 同步处理 MapViewportWindow

**文件**：`MiniPet/Views/MapViewportWindow.axaml.cs`（如果存在）

MapViewportWindow（摄像机窗口）同理，设 `Topmost = false`。

### 改动 3 — 清理 RaisePet 的 Topmost 翻转逻辑

**文件**：`MiniPet/Views/PetWindow.axaml.cs:948-951`

**当前**：
```csharp
private void RaisePet()
{
    try { Topmost = false; Topmost = true; Activate(); }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RaisePet: {ex.Message}"); }
}
```

**改为**（不再翻转 Topmost，只做 Activate 确保焦点）：
```csharp
private void RaisePet()
{
    try { Activate(); }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RaisePet: {ex.Message}"); }
}
```

> PetWindow 的 `Topmost=true` 在构造函数（L95）已设好，保持不动。RaisePet 不再需要翻转。

### 改动 4 — 确认 PetWindow 构造函数 Topmost 不被覆盖

**文件**：`MiniPet/Views/PetWindow.axaml.cs:95`

确认 `Topmost = true;` 保持，不被其他地方覆盖为 false。

---

## 禁止事项

1. **禁止改成单窗口架构**（双窗口是明确要求，保留）
2. **禁止删除 BackgroundWindow 类**（保留双窗口）
3. **禁止给 BackgroundWindow 加 Topmost=true**（这就是 bug 根源）
4. **禁止保留 Topmost 翻转逻辑**（RaisePet 里的 false/true 翻转删掉）

---

## 验收标准

- [ ] `dotnet build --no-restore` 0 错误
- [ ] 开启地图背景 → 桌宠精灵图清晰显示在地图**上方**
- [ ] 地图背景不会反弹盖住桌宠
- [ ] 关闭地图 → 背景消失，桌宠正常显示
- [ ] 拖动桌宠 → 拖拽正常，精灵图不被地图遮挡
- [ ] 切换到其他应用（如浏览器）→ 桌宠仍浮在最前，地图被盖住（这是预期行为）
