# MiniPet v1.0 — 展示不全 + 穿透点击 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 精灵图展示不全

**根因**: PetWindow 固定 `Width = 320, Height = 300`，SpriteCanvas 动态调大后窗口大小没跟着变。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 第 45~46 行删除固定宽高 `Width = 320; Height = 300;`
2. 添加 `SizeToContent = SizeToContent.WidthAndHeight;`
3. `MinWidth = 64; MinHeight = 64;` 保留

```csharp
Title = "MiniPet";
SizeToContent = SizeToContent.WidthAndHeight;  // 窗口自适应内容大小
MinWidth = 64;
MinHeight = 64;
Topmost = true;
WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
WindowStartupLocation = WindowStartupLocation.CenterScreen;
Background = Brushes.Transparent;
TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
```

## Bug 2 — 透明区域无法穿透点击

**根因**: `SpriteCanvas` 设了 `IsHitTestVisible = false`，但 PetWindow 的 `grid` 容器和 `_infoText` 等子控件仍然拦截了鼠标事件。Avalonia 中 `IsHitTestVisible` 在子控件上设了，如果父 Grid 是透明的但**事件仍然被 Grid 容器的非透明区域拦截**。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 第 55 行 Grid 设置 `IsHitTestVisible = false`
2. 第 62 行 `_infoText` 设置 `IsHitTestVisible = false`
3. 第 74 行 `_spriteCanvas` 已设置 `IsHitTestVisible = false`（保留）
4. 第 79 行 `_balloonControl` 已设置 `IsHitTestVisible = false`（保留）
5. 拖拽事件 `SetupDragEvents()` 中，`PointerPressed` 加在 `grid` 上而不是 `PetWindow` 本身上

```csharp
// 整个 Grid 容器不拦截鼠标事件
var grid = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
```

但拖拽需要点击事件才能触发 `BeginMoveDrag`，所以需要**让拖拽事件绑定在 PetWindow 本身上，子控件全部不拦截**。

**确保拖拽事件在 PetWindow 层面处理**：`SetupDragEvents()` 已经加在 `PointerPressed` 上（`this.PointerPressed`），这是对的。当子控件 `IsHitTestVisible = false` 时，事件会穿透到 PetWindow。

**完整修改**:
```csharp
var grid = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
```

```csharp
_infoText = new TextBlock
{
    Text = "MiniPet",
    FontSize = 12,
    Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
    TextAlignment = TextAlignment.Center,
    Margin = new Thickness(0, 4),
    IsHitTestVisible = false  // 不拦截点击
};
```

**注意**: `ContextMenu` 需要能接收右键事件。右键菜单加在 PetWindow 上，`PointerPressed` 中判断 `IsRightButtonPressed` 时直接 `ContextMenu?.Open(this)`，不需要子控件传递事件。

## 验证

`dotnet build --no-restore` 0 错误