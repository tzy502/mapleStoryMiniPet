# Claude Code 修复提示语 — 精灵图抖动（WriteableBitmap 方案）

> 生成日期：2026-08-01
> 模式：B（Debug 修复）
> 问题：精灵图每帧抖动，根因是透明窗口 + Image 每帧换 Source 导致 Metal 后端纹理重建抖动

---

## 项目信息

| 项 | 值 |
|------|------|
| 主项目 | `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/MiniPet.csproj` |
| 编译命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet build --no-restore` |

---

## 问题描述

精灵图播放动画时持续 1~2px 抖动。已排除：origin 补偿（已恢复，同 action 内 dx/dy=0）、SetFrame 尺寸优化（已做，尺寸不变不重设）。抖动依旧。

---

## 根因（最终确认）

`SpriteCanvas.SetFrame` 每帧执行 `SpriteImage.Source = new Bitmap(ms)`（L37-39, L63）。每次 Source 变化：

1. Avalonia dispose 旧 GPU 纹理，上传新纹理
2. 透明窗口（`TransparencyLevelHint = Transparent`）在 macOS 走 Metal 后端
3. Core Animation 对 CALayer 子层（图片纹理）重建时做**亚像素对齐**，导致 Image 视觉上做 1px 抖动

这是 Avalonia 11 + macOS 透明窗口 + Image 控件的已知渲染行为，不是代码逻辑 bug。

---

## 修复方案：WriteableBitmap 复用

用 `WriteableBitmap` 替代每帧 `new Bitmap`。**复用同一个 WriteableBitmap 实例**，每帧只通过 `WritePixels` 更新像素数据，不触发 GPU 纹理重建。

### 改动文件

`MiniPet/Views/Controls/SpriteCanvas.axaml.cs`（完整重写 SetFrame 方法 + 新增字段）

### 改动 1 — 新增 WriteableBitmap 字段

**文件**：`MiniPet/Views/Controls/SpriteCanvas.axaml.cs`

**当前字段**（L14-19）：
```csharp
private Bitmap? _currentBitmap;
private int _originX;
private int _originY;
private int _lastPixelW;
private int _lastPixelH;
private double _lastScale = -1;
```

**改为**：
```csharp
private WriteableBitmap? _writeableBmp;
private int _originX;
private int _originY;
private int _lastPixelW;
private int _lastPixelH;
private double _lastScale = -1;
```

### 改动 2 — SetFrame 用 WriteableBitmap

**当前 SetFrame**（L29-66）：

**改为**：
```csharp
public void SetFrame(byte[] pngData, int originX = 0, int originY = 0, double scale = 1.0)
{
    if (pngData == null || pngData.Length == 0)
    {
        Clear();
        return;
    }

    _originX = originX;
    _originY = originY;
    double s = scale > 0 ? scale : 1.0;

    // 先用 Bitmap 解码 PNG（一次性，用于取尺寸和像素）
    using var ms = new MemoryStream(pngData);
    using var bmp = new Bitmap(ms);
    var pixelSize = bmp.PixelSize;
    int pw = pixelSize.Width;
    int ph = pixelSize.Height;

    // 尺寸变化时重建 WriteableBitmap + 调整容器尺寸
    bool sizeChanged = (pw != _lastPixelW || ph != _lastPixelH || s != _lastScale);
    if (sizeChanged)
    {
        _writeableBmp?.Dispose();
        // BGRA 格式，dpi 用默认 96
        _writeableBmp = new WriteableBitmap(
            new PixelSize(pw, ph),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        double w = pw * s;
        double h = ph * s;
        this.Width = w;
        this.Height = h;
        RootCanvas.Width = w;
        RootCanvas.Height = h;
        SpriteImage.Width = w;
        SpriteImage.Height = h;
        _lastPixelW = pw;
        _lastPixelH = ph;
        _lastScale = s;

        // 首次或尺寸变化时设一次 Source
        SpriteImage.Source = _writeableBmp;
    }

    // 将解码后的像素写入 WriteableBitmap（不重建 GPU 纹理）
    using var fb = bmp.PixelSize.Width > 0 ? bmp.CopyToCompositeBitmap() : null;
    // 上面 CopyToCompositeBitmap 方式可能不稳定，改用直接像素拷贝：
    // 用 SkiaSharp 解码 PNG 到 BGRA byte[]，再 WritePixels
    // —— 见下方替代方案，更可靠
    // [Claude 注意]：如果 CopyToCompositeBitmap 不可用或报错，用替代方案
}
```

> **[Claude 注意] 替代像素拷贝方案（推荐，更可靠）**：
> Avalonia 的 `Bitmap` 没有直接的 `CopyPixelsTo` 方法。最可靠的做法是**用 SkiaSharp 解码 PNG 到 BGRA byte[]**，再 `WriteableBitmap.WritePixels`。
> 
> 改用以下完整实现：
```csharp
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

public void SetFrame(byte[] pngData, int originX = 0, int originY = 0, double scale = 1.0)
{
    if (pngData == null || pngData.Length == 0)
    {
        Clear();
        return;
    }

    _originX = originX;
    _originY = originY;
    double s = scale > 0 ? scale : 1.0;

    // 用 SkiaSharp 解码 PNG → BGRA 像素
    using var skBmp = SKBitmap.Decode(pngData);
    if (skBmp == null) return;

    // 确保 BGRA8888 格式（如果不是则转换）
    SKBitmap bgraBmp;
    if (skBmp.ColorType == SKColorType.Bgra8888)
    {
        bgraBmp = skBmp;
    }
    else
    {
        bgraBmp = new SKBitmap(skBmp.Info.WithColorType(SKColorType.Bgra8888));
        skBmp.ReadPixels(bgraBmp.Info, bgraBmp.GetPixels(), bgraBmp.RowBytes, 0, 0);
    }

    int pw = bgraBmp.Width;
    int ph = bgraBmp.Height;

    bool sizeChanged = (pw != _lastPixelW || ph != _lastPixelH || s != _lastScale);
    if (sizeChanged)
    {
        _writeableBmp?.Dispose();
        _writeableBmp = new WriteableBitmap(
            new PixelSize(pw, ph),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        double w = pw * s;
        double h = ph * s;
        this.Width = w;
        this.Height = h;
        RootCanvas.Width = w;
        RootCanvas.Height = h;
        SpriteImage.Width = w;
        SpriteImage.Height = h;
        _lastPixelW = pw;
        _lastPixelH = ph;
        _lastScale = s;

        SpriteImage.Source = _writeableBmp;
    }

    // 写像素（不重建纹理）
    using (var fb = _writeableBmp.Lock())
    {
        Marshal.Copy(bgraBmp.Bytes, 0, fb.Address, bgraBmp.Bytes.Length);
    }
    _writeableBmp.AddDirtyRect(new Rect(0, 0, pw, ph));

    Canvas.SetLeft(SpriteImage, 0);
    Canvas.SetTop(SpriteImage, 0);

    // bgraBmp 是转换出来的需要释放，原色的不用
    if (bgraBmp != skBmp)
    {
        bgraBmp.Dispose();
    }
}
```

### 改动 3 — Clear 方法同步修改

**当前 Clear**（L71-76）：
```csharp
public void Clear()
{
    SpriteImage.Source = null;
    _currentBitmap?.Dispose();
    _currentBitmap = null;
}
```

**改为**：
```csharp
public void Clear()
{
    SpriteImage.Source = null;
    _writeableBmp?.Dispose();
    _writeableBmp = null;
}
```

---

## 禁止事项

1. **禁止改 PetWindow.OnTimerTick**（origin 补偿已正确，不再动）
2. **禁止改窗口透明性**
3. **禁止每帧 new WriteableBitmap**（只在尺寸变化时重建）
4. **禁止改 Image 的 Stretch**（保持 None）
5. **禁止在帧循环里做同步 WZ 读取**

---

## 验收标准

- [ ] `dotnet build --no-restore` 0 错误
- [ ] 播放 stand/skill/attack 动画，精灵图**视觉上不抖动**
- [ ] 开启地图背景后，精灵图相对地图**不抖动**
- [ ] 动画切换时精灵图位置正确，无跳变
- [ ] `_scale != 1.0` 时也不抖动
