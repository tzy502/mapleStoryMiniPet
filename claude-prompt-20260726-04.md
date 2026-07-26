# MiniPet v1.0 — SpriteCanvas 尺寸不匹配修复

## 根因

SpriteCanvas 固定 `Width = 300, Height = 200`，但精灵图的实际尺寸更大。Canvas 控件不会根据子元素自动调整大小，所以精灵图被限制在左上角 300×200 区域内显示不全，窗口其余部分空白。

## 文件

`/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`

## 修复

修改 `SetFrame()` 方法，在设置 `SpriteImage.Source` 后，根据 Bitmap 的实际尺寸调整 SpriteCanvas 的宽度和高度：

```csharp
public void SetFrame(byte[] pngData)
{
    if (pngData == null || pngData.Length == 0)
    {
        Clear();
        return;
    }

    _currentBitmap?.Dispose();
    using var ms = new MemoryStream(pngData);
    _currentBitmap = new Bitmap(ms);

    SpriteImage.Source = _currentBitmap;

    // 根据精灵图实际尺寸调整控件大小
    var pixelSize = _currentBitmap.Size;
    Width = pixelSize.Width;
    Height = pixelSize.Height;
    SpriteImage.Width = pixelSize.Width;
    SpriteImage.Height = pixelSize.Height;

    Canvas.SetLeft(SpriteImage, 0);
    Canvas.SetTop(SpriteImage, 0);
}
```

## 只改这一个文件这一个方法

## 验证

`dotnet build --no-restore` 0 错误