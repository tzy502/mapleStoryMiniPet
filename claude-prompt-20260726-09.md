# MiniPet v1.0 — 展示不全 + 穿透点击 修复（第二版）

## 根因

### Bug 1 — 展示不全
`OnTimerTick` 中 `Width = _spriteCanvas.Width` 在第一帧时 `Width` 和 `_spriteCanvas.Width` 都是 0，条件不满足，窗口不设宽高。之后 `SpriteCanvas` 动态调了 `Width`，但 `OnTimerTick` 中 `Width != newW` 条件不触发（因为 `Width` 仍是 0），窗口保持最小尺寸。

### Bug 2 — 穿透点击
`grid.IsHitTestVisible = false` 切断了事件传递链，`PointerPressed` 加在 `this` 上收不到事件。需要改用 `PointerPressedEvent` 的路由策略，或者直接把事件加在不需要穿透的控件上，让穿透的控件不拦截。

## 文件

`/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

## 修复

### Bug 1 — 展示不全

**删除** `OnTimerTick` 中第 281~288 行的窗口尺寸调整代码：
```csharp
// 删除这段：
// 每帧调整窗口尺寸适应精灵图
var newW = (int)_spriteCanvas.Width;
var newH = (int)_spriteCanvas.Height;
if (Width != newW || Height != newH)
{
    Width = newW;
    Height = newH;
}
```

**改为**：在构造函数中直接设置 `SizeToContent = SizeToContent.WidthAndHeight`

```csharp
Title = "MiniPet";
SizeToContent = SizeToContent.WidthAndHeight;  // 窗口自适应内容大小
MinWidth = 64;
MinHeight = 64;
```

**同时**：在 `SpriteCanvas.SetFrame()` 中，设置 `Width/Height` 后，`SpriteCanvas` 是 Grid 的 Row 1 子元素，`GridLength.Auto` 会自动适应 `SpriteCanvas` 的尺寸，`SizeToContent` 会让整个 Window 跟着 Grid 走。

### Bug 2 — 穿透点击

**删除** `grid` 的 `IsHitTestVisible = false`。改为：**把所有子控件都设为 `IsHitTestVisible = false`，但 `grid` 本身不设**。这样事件从 Window 穿透到 grid，但 grid 的子控件不拦截，事件最终到达 Window 的 `PointerPressed` 处理。

```csharp
var grid = new Grid { Background = Brushes.Transparent };  // 去掉 IsHitTestVisible = false
```

```csharp
_infoText = new TextBlock
{
    // ...
    IsHitTestVisible = false  // 不拦截点击
};
```

**SpriteCanvas 和 BalloonControl 已经设了 `IsHitTestVisible = false`**，保留不变。

**关键**：`SetupDragEvents()` 中 `PointerPressed` 加在 `this` 上，当 `grid` 之外没其他控件拦截时，Window 上的 `PointerPressed` 能收到事件。右键菜单通过 `ContextMenu?.Open(this)` 直接触发，不依赖子控件的事件传递。

## 只改 `PetWindow.axaml.cs`

## 验证

`dotnet build --no-restore` 0 错误