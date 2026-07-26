# MiniPet v1.0 — 6 个 Bug 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — PetWindow 有黑边

**根因**: 窗口 `Background = Brushes.Transparent` 已设置，但 macOS 上透明窗口有时会显示黑色/灰色背景边框。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 确认第 48 行 `Background = Brushes.Transparent` 存在
2. 第 49 行 `TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }` 存在
3. 添加 `SystemDecorations = SystemDecorations.None`（删除窗口边框装饰）
4. 第 51 行 grid 容器设置 `Background = Brushes.Transparent`，确保子容器不覆盖透明

## Bug 2 — 红框位置（精灵图透明区域）应该穿透点击到下层窗口

**根因**: SpriteCanvas 中的 Image 控件的透明区域拦截了鼠标事件，无法穿透到下层窗口。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`

**修改**:
1. `SpriteImage` 添加 `IsHitTestVisible = false`，让图片的透明区域不拦截点击
2. `RootCanvas`（或整个 SpriteCanvas）设置 `Background = Brushes.Transparent`，确保点击穿透

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml`
**修改**:
```xml
<Canvas x:Name="RootCanvas" Background="Transparent">
    <Image x:Name="SpriteImage" IsHitTestVisible="False" />
</Canvas>
```

## Bug 3 — 精灵图放不下被截断，窗口应自适应大小

**根因**: PetWindow 固定 `Width = 320, Height = 300`，精灵图可能大于这个尺寸。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 第 44~45 行删除固定宽高，改为 `SizeToContent = SizeToContent.WidthAndHeight`
2. `_spriteCanvas` 不设固定 `Width = 300, Height = 200`，改为自动适应精灵图尺寸
3. 在 `SetFrame` 中，根据 PNG 尺寸动态调整 SpriteCanvas 的大小
4. 确保窗口最小宽度和高度（防止窗口太小无法操作）

## Bug 4 — 拖动时切换 move 而不是 fly

**根因**: 拖拽事件中 `EventBus` 触发 `UserDragStart` → PetWindow 订阅中调 `_animService.SwitchTo("fly")`，但 AnimService 可能没有 fly 动画数据，fallback 到 stand。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 第 106 行 `_animService.SwitchTo("fly")` 确认调用，但需要确保 fly 动画被预加载
2. 在 `PreloadMob` 中，优先加载 `stand`、`move`、`fly`、`walk` 等动作
3. 如果 fly 不存在，fallback 到 move 而不是 stand

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/SpriteService.cs`

**修改**: `PreloadMob()` 方法中第 195~198 行的优先级动作列表确认包含 `fly`：
```csharp
var priorityActions = actions.Where(a =>
    a is "stand" or "move" or "fly" or "walk" or "jump").ToList();
```
（当前代码已包含 fly，检查是否被正确加载）

## Bug 5 — 右键切换动画失效，且没有根据可用内容动态生成

**根因**: `BuildContextMenu()` 中「切换动画」子菜单固定写了 10 个动作名称，没有从 WZ 实际获取可用动作列表。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**第 329~341 行**:

```csharp
var animNames = new[] { "stand", "move", "fly", "say", "mouse", "laugh", "eye", "hit", "skill", "die" };
foreach (var name in animNames)
{
    var capturedName = name;
    var item = new MenuItem { Header = name };
    item.Click += (_, _) =>
    {
        try { _animService.SwitchTo(capturedName); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedName}: {ex.Message}"); }
    };
    animItem.Items.Add(item);
}
```

**修改**:
1. 从 `WzService.GetActionList(_animService.GetCurrentMobId())` 获取实际可用动作列表
2. 根据可用动作动态生成菜单项
3. 每个菜单项的 Click 事件调 `_animService.SwitchTo(action)`
4. 如果某个动作没有精灵图数据，在菜单项上显示灰色或标注「不可用」

## Bug 6 — 待机空闲时偶尔没有触发随机动作

**根因**: `AnimService.Tick()` 中空闲超时逻辑的阈值随机范围 15~30 秒，可能因为帧延迟计算问题导致 `_idleAccumulated` 累积不准确。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/AnimService.cs`

**修改**:
1. 检查 `_randomIdleEnabled` 是否在 PetWindow 构造函数中被启用（第 86 行已 `EnableRandomIdle(true)`）
2. 确认 `Tick()` 中空闲累积逻辑在 OneShot 模式下也正常工作
3. 添加调试日志，在触发随机动作时打印 `Console.WriteLine`
4. 缩短默认空闲超时范围，从 15~30s 改为 8~15s，便于测试验证

## 验证

`dotnet build --no-restore` 0 错误