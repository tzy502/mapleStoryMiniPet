# MiniPet v1.0 — 拖拽抖动修复

## 根因

`SetupDragEvents()` 中同时用了两种拖拽机制：

1. `BeginMoveDrag(e)` — 第 232 行，Avalonia 原生拖拽
2. `PointerMoved` 中手动设 `Position` — 第 242~245 行

两者互相冲突，原生拖拽移动窗口的同时手动 Position 也在移动，导致窗口来回抖动。

## 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**当前代码**（第 232~248 行）:
```csharp
// 用 OS 原生拖拽，避免手动 Position 更新滞后
BeginMoveDrag(e);
// ...
PointerMoved += (_, e) =>
{
    try
    {
        if (!_isDragging) return;
        // 原生拖拽下不需要手动设 Position，但保留计算做 fallback
        var pos = e.GetPosition(this);
        Position = new PixelPoint(
            (int)(_windowStartPos.X + pos.X - _dragStart.X),
            (int)(_windowStartPos.Y + pos.Y - _dragStart.Y));
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerMoved: {ex.Message}"); }
};
```

**改为**:
```csharp
PointerPressed += (_, e) =>
{
    try
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            ContextMenu?.Open(this);
            return;
        }
        _isDragging = true;
        _dragStart = e.GetPosition(this);
        _windowStartPos = Position;
        _eventBus.Publish(EventType.UserDragStart);
        // 用 OS 原生拖拽，不需要手动跟踪 PointerMoved
        BeginMoveDrag(e);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerPressed: {ex.Message}"); }
};
// 删除 PointerMoved 事件中手动设 Position 的代码
PointerMoved += (_, e) =>
{
    // 原生拖拽已处理，不需要手动设 Position
};
```

**删除 PointerMoved 中手动设 Position 的代码**，只保留原生 `BeginMoveDrag` 一种拖拽机制。

## 只改 `SetupDragEvents()` 方法

## 验证

`dotnet build --no-restore` 0 错误