# MiniPet v1.0 — 展示不全 + 穿透点击 修复（最终版）

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 展示不全

**根因**: `OnTimerTick` 第 284~288 行创建了多余的 Bitmap 并 Dispose，导致 `SpriteImage.Source` 被释放。

**当前代码**（第 273~289 行）:
```csharp
var (fd, pngData) = _animService.Tick(delta);
if (pngData != null)
{
    _spriteCanvas.SetFrame(pngData, ox, oy);  // 这里已经创建了 Bitmap 并设到 Source

    // 下面这段是多余的，干扰了渲染
    var bmp = new Bitmap(new MemoryStream(pngData));
    Width = bmp.PixelSize.Width + Math.Max(0, ox);
    Height = bmp.PixelSize.Height + Math.Max(0, oy);
    bmp.Dispose();
}
```

**修复**: 删除 `OnTimerTick` 中 `SetFrame` 之后创建 Bitmap 并设 Width/Height 的代码。在构造函数中设置固定初始尺寸 + 用 `SizeToContent`。

**文件 1**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 构造函数中：
```csharp
Title = "MiniPet";
Width = 320;
Height = 300;
MinWidth = 64;
MinHeight = 64;
```

2. `OnTimerTick` 中（第 273~289 行），只保留：
```csharp
var (fd, pngData) = _animService.Tick(delta);
if (pngData != null)
{
    var strip = _spriteService.GetCachedStrip(_animService.GetCurrentMobId(), _animService.GetCurrentAction()).Item1;
    int ox = strip?.OriginX ?? 0;
    int oy = strip?.OriginY ?? 0;
    _spriteCanvas.SetFrame(pngData, ox, oy);
}
```

**文件 2**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml`

**修改**:
```xml
<UserControl ...>
    <Canvas x:Name="RootCanvas" Background="Transparent" HorizontalAlignment="Left" VerticalAlignment="Top">
        <Image x:Name="SpriteImage" Stretch="None" />
    </Canvas>
</UserControl>
```

## Bug 2 — 穿透点击

**根因**: Avalonia 中 `PointerPressed` 绑定在 `this` 上，但之前 `grid` 被设了 `IsHitTestVisible = false`，切断了事件传递路径。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 第 55 行 `grid` **去掉** `IsHitTestVisible = false`
2. 第 69 行 `_infoText` **去掉** `IsHitTestVisible = false`
3. 第 75 行 `_spriteCanvas` **去掉** `IsHitTestVisible = false`
4. 第 80 行 `_balloonControl` **去掉** `IsHitTestVisible = false`

```csharp
var grid = new Grid { Background = Brushes.Transparent };
// 去掉 IsHitTestVisible = false

_infoText = new TextBlock { ... };
// 去掉 IsHitTestVisible = false

_spriteCanvas = new SpriteCanvas();
// 去掉 IsHitTestVisible = false

_balloonControl = new BalloonControl { IsHitTestVisible = false };
// 保留 IsHitTestVisible = false（气泡不阻挡点击，这个是对的）
```

**关键**: 所有子控件不设 `IsHitTestVisible = false`，事件自然传递到 Window 的 `PointerPressed` 处理。

## 只改 2 个文件

1. `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
2. `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml`

## 验证

`dotnet build --no-restore` 0 错误