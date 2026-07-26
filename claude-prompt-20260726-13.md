# MiniPet v1.0 — 展示不全 + 穿透点击 修复（优化版）

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 展示不全

**根因**: PetWindow 固定 `Width = 320, Height = 300`，SpriteCanvas 动态调整了尺寸但 Window 没跟着变。精灵图只在左上角显示。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**:
1. 构造函数中删除固定宽高，改用 `SizeToContent`：
```csharp
Title = "MiniPet";
SizeToContent = SizeToContent.WidthAndHeight;
MinWidth = 64;
MinHeight = 64;
```

2. 在 `SpriteCanvas.SetFrame()` 中，设 `Width/Height` 后，`SizeToContent` 会让窗口自动适应。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`

**修改 `SetFrame()`**:
```csharp
public void SetFrame(byte[] pngData, int originX = 0, int originY = 0)
{
    if (pngData == null || pngData.Length == 0)
    {
        Clear();
        return;
    }

    _currentBitmap?.Dispose();
    using var ms = new MemoryStream(pngData);
    _currentBitmap = new Bitmap(ms);

    var size = _currentBitmap.PixelSize;
    _originX = originX;
    _originY = originY;

    // 根据精灵图尺寸调整画布
    int canvasW = size.Width + Math.Max(0, originX);
    int canvasH = size.Height + Math.Max(0, originY);
    RootCanvas.Width = canvasW;
    RootCanvas.Height = canvasH;
    SpriteImage.Width = size.Width;
    SpriteImage.Height = size.Height;
    SpriteImage.Source = _currentBitmap;
    Canvas.SetLeft(SpriteImage, -originX);
    Canvas.SetTop(SpriteImage, -originY);

    // 通知父容器调整大小
    InvalidateMeasure();
}
```

关键添加：`InvalidateMeasure()` 通知 Avalonia 布局系统重新计算大小。

## Bug 2 — 穿透点击

**根因**: 子控件拦截了鼠标事件，无法穿透到下层窗口。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**修改**: 所有子控件去掉 `IsHitTestVisible = false`，事件自然传递到 Window 的 `PointerPressed`。

```csharp
var grid = new Grid { Background = Brushes.Transparent };  // 去掉 IsHitTestVisible

_infoText = new TextBlock { ... };  // 去掉 IsHitTestVisible

_spriteCanvas = new SpriteCanvas();  // 去掉 IsHitTestVisible

_balloonControl = new BalloonControl();  // 去掉 IsHitTestVisible
```

## 验证

`dotnet build --no-restore` 0 错误