# MiniPet v1.0 — 全量修复 + 完善 + 测试

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **测试项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests/`
- **.NET SDK**: `/Volumes/SSD/C#`（编译前 export `DOTNET_ROOT=/Volumes/SSD/C#` && `PATH=$DOTNET_ROOT:$PATH`）
- **编译命令**: `dotnet build --no-restore`
- **运行命令**: `dotnet run --project MiniPet/MiniPet.csproj`

## 参考项目

### WZ 动画帧 Origin 对齐
**文件**: `/Users/a502/IdeaProjects/maplestory-wiki-backend/src/main/java/com/maplestory/core/util/GifUtil.java`
**核心逻辑**: `finalOriginX = maxL`, `finalOriginY = maxT`，每帧 `drawImage(image, finalOriginX - originX, finalOriginY - originY, null)`

## 已完成的代码（禁止重写，只做 bug 修复和补全）

### Models（6 个）
`AppConfig.cs`, `BalloonData.cs`, `CharacterAppearance.cs`, `MapData.cs`, `SpriteSheet.cs`, `SpriteStrip.cs`

### Services（11 个）
`WzService.cs`, `SpriteService.cs`, `AnimService.cs`, `BalloonService.cs`, `ConfigService.cs`, `CacheManager.cs`, `EventBus.cs`, `MapService.cs`, `PaperdollService.cs`, `TrayService.cs`, `WzError.cs`

### Views（6 个）
`PetWindow.axaml.cs`（纯代码窗口）, `SettingsWindow.axaml.cs`（纯代码）, `MaterialBrowserWindow.cs`（纯代码）, `SpriteCanvas.axaml`+`.axaml.cs`, `BalloonControl.axaml`+`.axaml.cs`, `BackgroundControl.axaml`+`.axaml.cs`

### 其他
`Program.cs`, `App.axaml`+`.axaml.cs`, `MiniPet.csproj`

## 编码约定

### 异常处理
所有方法和事件处理必须 try-catch，catch 中:
```csharp
catch (Exception ex) { Console.Error.WriteLine($"[ClassName] 错误: {ex.Message}"); }
```

### 窗口属性
```csharp
Background = Brushes.Transparent;
TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
SystemDecorations = SystemDecorations.None;
```

### 渲染 API
- 禁止使用 `System.Drawing`（macOS 不支持）
- 使用 `WriteableBitmap`（`Avalonia.Media.Imaging`）或 `SkiaSharp` 替代

### 动画顺序
`stand → say → mouse → move → hand → laugh → eye → fly → die`
缺失动画 fallback 到 `stand`

### Origin 对齐公式
```csharp
int finalOriginX = frames.Max(f => f.OriginX);
int finalOriginY = frames.Max(f => f.OriginY);
drawImage(image, finalOriginX - currentOriginX, finalOriginY - currentOriginY);
```

## 多 Agent 并行任务

### Agent 1 — ToggleSwitch 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**只改第 217 行到第 218 行**

**当前代码**:
```csharp
var themeToggle = new ToggleSwitch { IsChecked = _isDarkMode, Width = 32, Height = 18 };
themeToggle.IsCheckedChanged += (_, _) => { _isDarkMode = themeToggle.IsChecked ?? true; themeLabel.Text = _isDarkMode ? "暗黑" : "白天"; _settings.Theme = _isDarkMode ? "dark" : "light"; _config.SaveSettings(_settings); };
```

**改为**:
```csharp
var themeToggle = new ToggleSwitch { IsChecked = _isDarkMode, Width = 44, Height = 24, VerticalAlignment = VerticalAlignment.Center };
themeToggle.IsCheckedChanged += (_, _) => { _isDarkMode = themeToggle.IsChecked ?? true; themeLabel.Text = _isDarkMode ? "暗黑" : "白天"; _settings.Theme = _isDarkMode ? "dark" : "light"; _config.SaveSettings(_settings); };
```

### Agent 2 — 删除红黄绿圆点

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildTitleBar()` 第 195~202 行

**删除**:
1. 第 198~201 行（`dots` StackPanel 及其 foreach 循环）
2. 第 202 行（`DockPanel.SetDock(dots, Dock.Left)` 和 `bar.Children.Add(dots)`）

### Agent 3 — 添加底部状态栏

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`

**在类字段区域添加**:
```csharp
private TextBlock? _statusBarText;
```

**在 `BuildBody()` 方法返回前添加**:
```csharp
var statusBar = new Border
{
    Height = 24,
    Background = new SolidColorBrush(Color.FromArgb(255, 22, 22, 28)),
    Child = new TextBlock
    {
        Text = "就绪",
        FontSize = 10,
        Foreground = BrushTxtDim,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0)
    }
};
_statusBarText = (TextBlock)((Border)statusBar).Child;
```

然后将 `root` 改为 4 行，把 statusBar 放在第 3 行（Row 3）。

**在 `SaveWzConfig()` 方法中**，WZ 加载成功时 `_statusBarText!.Text = "WZ 加载完成"`，失败时 `_statusBarText!.Text = $"WZ 加载失败: {error}"`

### Agent 4 — 地图预览增强

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `UpdateMapPreview()` 第 963~979 行

**修改**：在 `mapVisual` 的渐变背景上，用 `Grid` 叠加层添加小房子字符画。
- 底部添加 3 个 `🏠` emoji 或 `TextBlock` 字符画
- 使用 `Grid` 叠加，不替换现有渐变背景

### Agent 5 — 核心动画与交互完善

**文件**:
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/AnimService.cs`
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/BalloonControl.axaml.cs`

**任务**:
1. `AnimService.Tick()` 帧延迟从硬编码 240ms 改为从 `SpriteStrip.FrameData` 读取
2. `PetWindow`: 拖拽时 `PointerPressed` 切换到 fly，`PointerReleased` 回到 stand
3. 右键菜单补充：切换人物、切换动画、切换背景、切换气泡、Hermes 终端、设置中心、退出
4. `BalloonControl`: 淡入 0.2s → 显示 3~5s → 淡出 0.3s
5. 空闲随机动作启用（`_randomIdleEnabled = true`）
6. 帧循环从 500ms 改为 33ms（30fps）

### Agent 6 — 白盒测试

**目录**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests/`
**框架**: xUnit

**要求**: 为以下 Service 编写单元测试，每个至少 3 个测试方法：

| 测试类 | 测试内容 |
|--------|----------|
| `ConfigServiceTests` | 读写配置、JSON 序列化、变更通知 |
| `EventBusTests` | 发布/订阅、取消订阅、多订阅者 |
| `WzServiceTests` | 节点路径解析、查询异常处理 |
| `SpriteServiceTests` | 帧缓存命中/未命中、加载/卸载 |
| `AnimServiceTests` | 状态切换、OneShot/Loop 模式、fallback 链、空闲超时触发 |
| `BalloonServiceTests` | 9-slice 计算、文字排版 |
| `MapServiceTests` | 图层叠加、背景加载 |
| `PaperdollServiceTests` | 装备层级排序、z-order 计算 |

**规则**: 测试项目独立不引用主项目，纯逻辑测试不依赖 Avalonia UI，必须实际运行通过。

**运行命令**: `dotnet test MiniPet.Tests/MiniPet.Tests.csproj --no-restore`

## 工作流

1. **6 个 agent 同时启动**，各自独立工作
2. 每个 agent 完成后，运行 `dotnet build --no-restore` 确保编译通过
3. 所有 agent 完成后，汇总编译结果和测试结果
4. 编译报错时记录问题列表 → 修复 → 重新编译
5. 测试失败时记录失败测试 → 修复代码 → 重新运行测试

## 禁止事项

- 不要重构或重写已完成的代码文件
- 不要修改项目架构（Models/Services/Views 分层）
- 不要修改 `.csproj` 添加新 NuGet 包
- 禁止使用 `System.Drawing`（macOS 不支持）
- 禁止使用 `System.Windows.Forms`