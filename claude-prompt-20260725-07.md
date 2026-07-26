# MiniPet v1.0 — 桌宠不显示 + 预览区域过小 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 桌宠 PetWindow 不显示精灵图（P0 阻塞）

### 根因
`PetWindow.axaml.cs` 的 `OnTimerTick` 方法（第 158~174 行）用 `System.Drawing.Bitmap` 做帧渲染：

```csharp
var (fd, bmp) = _animService.Tick(500);
if (bmp != null)
{
    using var ms = new System.IO.MemoryStream();
    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);  // ← macOS 上抛 PlatformNotSupportedException
    ms.Position = 0;
    _spriteImage.Source = new Avalonia.Media.Imaging.Bitmap(ms);
}
```

macOS 上 `System.Drawing` 已被 .NET 禁用，`bmp.Save()` 抛异常被顶层 catch 吞掉，`_spriteImage.Source` 永远是 null。

### 修复

**改 PetWindow.axaml.cs 的 OnTimerTick（只改帧渲染部分，不动其他）：**

1. **AnimsService.Tick()** 返回的是 `(FrameData?, Bitmap?)` — `Bitmap` 来自 System.Drawing。需要改为返回原始像素数据 `byte[]`（BGRA32 格式），而不是 `Bitmap`。

2. **渲染管线改为**:
   ```
   WzService.GetRawData(framePath) → 原始像素 byte[]
   → PngEncoder（自建） → PNG byte[]
   → new Avalonia.Media.Imaging.Bitmap(pngStream)
   → _spriteImage.Source
   ```

3. **修改 AnimService**，让 `Tick()` 返回 `(FrameData?, byte[]? pngData)` 而不是 `(FrameData?, Bitmap?)`：
   - `AnimService.Tick()` 调用 `SpriteService.GetFramePng(frameIndex)` 返回 PNG byte[]
   - `SpriteService` 新增方法 `GetFramePng(int frameIndex)` 用 `WzService.GetRawData()` + 自建 PngEncoder 返回 PNG byte[]

4. **修改 PetWindow.OnTimerTick**，用 `byte[] pngData` 创建 `Avalonia.Media.Imaging.Bitmap`:
   ```csharp
   var (fd, pngData) = _animService.Tick(500);
   if (pngData != null)
   {
       using var ms = new MemoryStream(pngData);
       _spriteImage.Source = new Avalonia.Media.Imaging.Bitmap(ms);
   }
   ```

5. **删除**所有 `System.Drawing` 的 using 和引用（`using System.Drawing;` / `using System.Drawing.Imaging;`）

### 参考：WzLib 的 PNG 提取替代方案
WzLib 的 `Wz_Png.ExtractPng()` 在 macOS 上不可用（内部调 `System.Drawing.Bitmap`）。替代方案：
```
WzCanvasProperty.GetRawData() → 解压出原始像素（BGR565/ARGB4444 等）
→ 转为 BGRA32 → ZLibStream 写 PNG
```

## Bug 2 — 素材预览区域太小

### 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`

**修改 `BuildMaterialTab()`** 中 `_previewCanvas` 的定义（第 354 行附近）：

```csharp
// 当前
_previewCanvas = new Border { ..., MinHeight = 300, ... };

// 改为
_previewCanvas = new Border { ..., MinHeight = 400, MinWidth = 320, ... };
```

同时修改 `UpdatePreviewContent()` 中的 `previewFigure` 尺寸：
```csharp
// 当前
var previewFigure = new Border { Width = 160, Height = 160, ... };

// 改为
var previewFigure = new Border { Width = 280, Height = 280, ... };
var spriteImage = new Image { Stretch = Stretch.Uniform, Width = 260, Height = 260, Margin = new Thickness(10) };
```

## Bug 3 — PetWindow 窗口相对于精灵图太大

### 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

PetWindow 的初始尺寸 `Width=320, Height=240` 不需要修改，但 `_spriteImage` 的 `Stretch = Stretch.Uniform` 让精灵图按比例缩放。确认 `_spriteImage` 的父容器不限制其填充空间。

**改为**：
```csharp
_spriteImage = new Image { Stretch = Stretch.UniformToFill };
```

或让窗口尺寸能自适应精灵图大小：
```csharp
SizeToContent = SizeToContent.WidthAndHeight;
```

## 禁止事项

- 禁止使用 `System.Drawing`（`System.Drawing.Bitmap`、`System.Drawing.Imaging`）
- 禁止引用 `System.Drawing.Common` NuGet 包
- WZ 像素数据走 `WzComparerR2.WzLib` 的 `GetRawData()` 接口
- PNG 编码用自建 `PngEncoder`（`ZLibStream` + 手写 IHDR/IDAT/IEND chunk）
- 不要修改 .csproj