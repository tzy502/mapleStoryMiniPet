# MiniPet v1.0 — 展示不全 + 穿透点击 修复（第三版）

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 展示不全

**根因**: `SizeToContent.WidthAndHeight` 和 `SpriteCanvas` 动态设 `Width` 冲突

- `SizeToContent` 模式下，窗口大小由内容决定
- `SpriteCanvas` 初始没有 Width/Height，Grid 的 Row 1 是 `Auto`，第一帧时窗口缩到 64×64
- `SetFrame` 动态设了 `SpriteCanvas.Width` 但 `SizeToContent` 不会自动重新计算

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. **删除** `SizeToContent = SizeToContent.WidthAndHeight;`
2. **在构造函数中设置初始宽高**为一个合理的默认值（如 320×300）
3. **在 `OnTimerTick` 中直接设 Window 的 Width/Height**，不依赖 SpriteCanvas 的尺寸

```csharp
Title = "MiniPet";
Width = 320;
Height = 300;
MinWidth = 64;
MinHeight = 64;
```

**修改 `OnTimerTick`**（第 275~289 行）:
```csharp
if (pngData != null)
{
    var strip = _spriteService.GetCachedStrip(...).Item1;
    int ox = strip?.OriginX ?? 0;
    int oy = strip?.OriginY ?? 0;
    _spriteCanvas.SetFrame(pngData, ox, oy);

    // 直接设置窗口尺寸
    var bmp = new Bitmap(new MemoryStream(pngData));
    Width = bmp.PixelSize.Width + Math.Max(0, ox);
    Height = bmp.PixelSize.Height + Math.Max(0, oy);
    bmp.Dispose();
}
```

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`

**修改 `SetFrame()`**:
- 不再设 `Width = canvasW; Height = canvasH;`（让 PetWindow 控制窗口大小）
- 只设 `RootCanvas.Width/Height` 和 `SpriteImage.Width/Height`
- 添加 `SpriteImage.Stretch = Stretch.None`（不缩放，保持原始像素）

## Bug 2 — 穿透点击

**根因**: `IsHitTestVisible = false` 设在了子控件上，但 `PointerPressed` 加在 `this` 上。`BeginMoveDrag(e)` 在拖拽开始后捕获了鼠标，后续事件不再传递到 Window。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. **删除所有子控件的 `IsHitTestVisible = false`**（包括 `_infoText`、`_spriteCanvas`、`_balloonControl`）
2. **`grid` 也不设 `IsHitTestVisible = false`**
3. **在 `SetupDragEvents()` 中，`PointerPressed` 加在 `this` 上**，但把 `BeginMoveDrag` 改成只在**按住精灵图非透明区域**时触发
4. 关键：`PointerPressed` 获取 `e.GetCurrentPoint(this)` 的 `Position`，如果点击位置在精灵图范围内（`_spriteCanvas.Bounds`）才触发拖拽，否则穿透到下层

**简化方案**:
- 删除所有 `IsHitTestVisible = false`
- 在 `PointerPressed` 中判断：如果点击位置在 `_spriteCanvas` 范围内，用 `BeginMoveDrag` 拖拽
- 如果点击位置不在精灵图范围内，不处理（事件穿透到下层窗口）

```csharp
private void SetupDragEvents()
{
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

            // 只在精灵图范围内拖拽
            var pos = e.GetPosition(this);
            if (_spriteCanvas.Bounds.Contains(pos))
            {
                _isDragging = true;
                _dragStart = e.GetPosition(this);
                _windowStartPos = Position;
                _eventBus.Publish(EventType.UserDragStart);
                BeginMoveDrag(e);
            }
            // 否则不处理，事件穿透到下层窗口
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PointerPressed: {ex.Message}"); }
    };
    // PointerMoved 和 PointerReleased 保持空
}
```

## 验证

`dotnet build --no-restore` 0 错误