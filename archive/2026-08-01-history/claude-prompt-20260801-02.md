# Claude Code 修复提示语 — 精灵图帧间抖动

> 生成日期：2026-08-01
> 模式：B（Debug 修复）
> 问题：桌宠精灵图播放动画时持续高频抖动（1~2px 位置颤动）

---

## 项目信息

| 项 | 值 |
|------|------|
| 项目根 | `/Users/a502/IdeaProjects/mapleStoryMiniPet` |
| 主项目 | `MiniPet/MiniPet.csproj` |
| 测试项目 | `MiniPet.Tests/MiniPet.Tests.csproj`（独立，不引用主项目） |
| 分支 | `master` |
| SDK | .NET 9.0 |
| SDK 路径 | `DOTNET_ROOT=/Volumes/SSD/C#` |
| 编译命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet build --no-restore` |
| 测试命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet test --nologo` |
| 运行命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet run --project MiniPet/MiniPet.csproj` |

**⚠️ 编译必须用 `--no-restore`**，否则 NuGet 恢复阶段产生临时文件导致 CS0579 假阳性。

---

## 问题描述

桌宠精灵图播放动画（stand / skill / attack 等）时持续高频抖动，表现为精灵图在原位置做 1~2 像素的颤动。切换到地图背景后，精灵图相对静态地图的抖动更明显。

---

## 根因分析

抖动来自 `SpriteCanvas.SetFrame` **每帧无条件重设 5 个尺寸属性 + Image.Source**，触发 Avalonia 完整布局传递 + 渲染管线重建。

**关键事实**：`SpriteStrip.OriginX` 和 `SpriteStrip.FrameWidth/Height` 是 `WzService` 构建 strip 时算的**整个 action 的固定常量**（`maxL`/`maxT`/`maxL+maxR`/`maxT+maxB`，见 `WzService.cs:499-506`），不是每帧值。因此：

- `OnTimerTick` 里的 origin 补偿（L519-529）`dx/dy` **永远为 0**，`Position = ...` 永远不执行——是死代码。
- 但 `SpriteCanvas.SetFrame` 每帧重设尺寸 + Source，66ms 一次的重布局导致亚像素级视觉抖动。
- `_scale != 1.0` 时，`fw * _scale` 用 `int` 截断会在 N 和 N+1 之间波动，加重抖动。

---

## 改动文件清单

| 文件 | 改动 |
|------|------|
| `MiniPet/Views/Controls/SpriteCanvas.axaml.cs` | SetFrame 只在尺寸真正变化时重设，Source 每帧设 |
| `MiniPet/Views/PetWindow.axaml.cs` | OnTimerTick 窗口 resize 用 double + 删除 origin 死代码 |

---

## 具体改动

### 改动 1（核心）— SetFrame 避免每帧重设尺寸

**文件**：`MiniPet/Views/Controls/SpriteCanvas.axaml.cs`
**方法**：`SetFrame`（L26-56）

**当前代码**（每帧无条件设尺寸 + Source）：
```csharp
public void SetFrame(byte[] pngData, int originX = 0, int originY = 0, double scale = 1.0)
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

    double s = scale > 0 ? scale : 1.0;
    double w = size.Width * s;
    double h = size.Height * s;
    this.Width = w;
    this.Height = h;
    RootCanvas.Width = w;
    RootCanvas.Height = h;
    SpriteImage.Width = w;
    SpriteImage.Height = h;
    SpriteImage.Source = _currentBitmap;
    Canvas.SetLeft(SpriteImage, 0);
    Canvas.SetTop(SpriteImage, 0);
}
```

**改为**：
1. 新增字段：`private int _lastPixelW;` / `private int _lastPixelH;` / `private double _lastScale = -1;`
2. 尺寸只在 `pixelW/pixelH/scale` 变化时才重设，`Source` 每帧设（换帧核心）。

```csharp
private int _lastPixelW;
private int _lastPixelH;
private double _lastScale = -1;

public void SetFrame(byte[] pngData, int originX = 0, int originY = 0, double scale = 1.0)
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

    double s = scale > 0 ? scale : 1.0;

    // 尺寸不变时不重设 Width/Height，避免触发 Avalonia 布局传递
    bool sizeChanged = (size.Width != _lastPixelW || size.Height != _lastPixelH || s != _lastScale);
    if (sizeChanged)
    {
        double w = size.Width * s;
        double h = size.Height * s;
        this.Width = w;
        this.Height = h;
        RootCanvas.Width = w;
        RootCanvas.Height = h;
        SpriteImage.Width = w;
        SpriteImage.Height = h;
        _lastPixelW = size.Width;
        _lastPixelH = size.Height;
        _lastScale = s;
    }

    // Source 必须每帧设（换帧的核心）
    SpriteImage.Source = _currentBitmap;
    Canvas.SetLeft(SpriteImage, 0);
    Canvas.SetTop(SpriteImage, 0);
}
```

### 改动 2 — OnTimerTick 窗口 resize 用 double，避免 int 截断抖动

**文件**：`MiniPet/Views/PetWindow.axaml.cs`
**方法**：`OnTimerTick`（L508-517）

**当前代码**（`int` 截断 `fw * _scale`）：
```csharp
int scaledW = fw > 0 ? (int)(fw * _scale) : 0;
int scaledH = fh > 0 ? (int)(fh * _scale) : 0;
if (scaledW > 0 && scaledH > 0 && (scaledW != _lastFrameW || scaledH != _lastFrameH))
{
    Width = scaledW;
    Height = scaledH;
    _lastFrameW = scaledW;
    _lastFrameH = scaledH;
}
```

**改为**（去掉 `int` 截断，用 `double` 比较和赋值）：
```csharp
double scaledW = fw > 0 ? fw * _scale : 0;
double scaledH = fh > 0 ? fh * _scale : 0;
if (scaledW > 0 && scaledH > 0 && (Math.Abs(scaledW - _lastFrameW) > 0.5 || Math.Abs(scaledH - _lastFrameH) > 0.5))
{
    Width = scaledW;
    Height = scaledH;
    _lastFrameW = scaledW;
    _lastFrameH = scaledH;
}
```

**配套字段类型修改**（L62-63）：
```csharp
// 当前
private int _lastFrameW;
private int _lastFrameH;

// 改为
private double _lastFrameW;
private double _lastFrameH;
```

### 改动 3 — 删除 origin 补偿死代码

**文件**：`MiniPet/Views/PetWindow.axaml.cs`

**删除** `OnTimerTick` 中的 L519-529 整段（`dx/dy` 永远为 0，不执行）：
```csharp
// 删除以下所有行
int oxScaled = (int)(ox * _scale);
int oyScaled = (int)(oy * _scale);
int dx = oxScaled - _lastOriginX;
int dy = oyScaled - _lastOriginY;
if (dx != 0 || dy != 0)
{
    Position = new PixelPoint(Position.X - dx, Position.Y - dy);
}
_lastOriginX = oxScaled;
_lastOriginY = oyScaled;
```

**删除**字段（L60-61）：
```csharp
// 删除
private int _lastOriginX;
private int _lastOriginY;
```

**删除**构造函数中对它们的初始化（L139-140）：
```csharp
// 删除
_lastOriginX = 0;
_lastOriginY = 0;
```

---

## 禁止事项

1. **禁止改 `AnimService.Tick` 的帧推进逻辑**（帧索引、delay 累积已稳定）
2. **禁止改 `WzService` 构建 `SpriteStrip` 的逻辑**（`maxL/maxT/maxR/maxB` 算法正确）
3. **禁止给 origin 偏移补偿加新的坐标计算**（它是死代码，直接删除）
4. **禁止改窗口透明性**（`Background = Brushes.Transparent` + `TransparencyLevelHint` 不动）
5. **禁止改 `SizeToContent`**（保持 `WidthAndHeight`）
6. **禁止把 `SpriteImage.Source` 改成非每帧设**（换帧核心，必须每帧设）

---

## 验收标准

- [ ] `dotnet build --no-restore` 0 错误
- [ ] `dotnet test` ≥109 通过（不回退）
- [ ] 播放 stand/skill/attack 动画，精灵图**视觉上不抖动**
- [ ] 开启地图背景后，精灵图相对地图**不抖动**
- [ ] `_scale != 1.0`（缩放）时也不抖动
- [ ] 动画切换（stand → attack → stand）时精灵图位置正确，无跳变
