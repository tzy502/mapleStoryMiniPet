# MiniPet v1.0 — 展示不全 + 穿透点击 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 展示不全

**根因**: PetWindow 固定 `Width = 320, Height = 300`，`SpriteCanvas.SetFrame()` 动态调整了控件尺寸，但 PetWindow 本身的尺寸没跟着变，精灵图被裁剪在窗口左上角。

**修复**: 在 `OnTimerTick` 中，每次收到精灵图帧数据后，根据实际像素尺寸调整窗口大小。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改 `OnTimerTick` 方法**（第 272~279 行）:
```csharp
var (fd, pngData) = _animService.Tick(delta);
if (pngData != null)
{
    var strip = _spriteService.GetCachedStrip(_animService.GetCurrentMobId(), _animService.GetCurrentAction()).Item1;
    int ox = strip?.OriginX ?? 0;
    int oy = strip?.OriginY ?? 0;
    _spriteCanvas.SetFrame(pngData, ox, oy);

    // 调整窗口大小适应精灵图
    using var ms = new MemoryStream(pngData);
    using var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
    Width = bmp.PixelSize.Width + Math.Max(0, ox);
    Height = bmp.PixelSize.Height + Math.Max(0, oy);
}
```

**注意**: 这里创建 Bitmap 是必要的，因为需要通过 Bitmap 获取像素尺寸。`SetFrame` 内部已经创建了 `_currentBitmap`，但这个 Bitmap 的 PixelSize 可能被 Avalonia 缓存。

## Bug 2 — 穿透点击

**根因**: 子控件 `IsHitTestVisible = false` 和 `grid` 拦截了事件。Avalonia 中 `Background = Brushes.Transparent` 的控件仍然接收鼠标事件，必须设置 `IsHitTestVisible = false` 才能穿透。

**修复**: `grid` 不设 `IsHitTestVisible = false`，只让具体子控件穿透。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 第 55 行 `grid` **去掉** `IsHitTestVisible = false`
2. 第 69 行 `_infoText` **去掉** `IsHitTestVisible = false`  
3. 第 75 行 `_spriteCanvas` **去掉** `IsHitTestVisible = false`
4. 第 80 行 `_balloonControl` 保留 `IsHitTestVisible = false`（气泡不阻挡点击）

`SetupDragEvents()` 中 `PointerPressed` 加在 `this` 上，事件从子控件自然传递到 Window。

## 验证

`dotnet build --no-restore` 0 错误