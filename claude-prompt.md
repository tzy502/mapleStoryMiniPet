# MiniPet v1.0 — Debug 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **测试项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests/`
- **.NET SDK**: `/Volumes/SSD/C#`（编译前 export `DOTNET_ROOT=/Volumes/SSD/C#` && `PATH=$DOTNET_ROOT:$PATH`）
- **编译命令**: `dotnet build --no-restore`
- **测试命令**: `dotnet test MiniPet.Tests/MiniPet.Tests.csproj --no-restore`
- **运行命令**: `dotnet run --project MiniPet/MiniPet.csproj`

## 多 Agent 并行任务

### Agent 1 — ToggleSwitch 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`

**只改第 217 行到第 218 行**，不要修改任何其他代码。

**当前代码**:
```csharp
var themeToggle = new ToggleSwitch { IsChecked = _isDarkMode, Width = 32, Height = 18 };
themeToggle.IsCheckedChanged += (_, _) => { _isDarkMode = themeToggle.IsChecked ?? true; themeLabel.Text = _isDarkMode ? "暗黑" : "白天"; _settings.Theme = _isDarkMode ? "dark" : "light"; _config.SaveSettings(_settings); };
```

**修复要求**:
1. 把 `ToggleSwitch` 的宽高从 `Width=32, Height=18` 改为 `Width=44, Height=24`
2. 确保 `themeLabel`（"暗黑"/"白天"文字）和 `ToggleSwitch` 水平居中在 `rightPanel` 中
3. 用 `VerticalAlignment = VerticalAlignment.Center` 确保它们垂直居中
4. 不要改动任何其他代码行

**验证**: `dotnet build --no-restore` 0 错误

### Agent 2 — 地图预览增强

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `UpdateMapPreview()`（第 963~979 行）和 `GetMapBackgroundGradient()`（第 981~991 行）

**修复要求**:
1. 在 `UpdateMapPreview()` 方法的 `mapVisual` 中，添加小房子 SVG 元素或字符画
2. 在渐变背景上绘制 3 个小房子的字符组合（使用 `TextBlock` 叠加）
3. 参考效果：浅蓝→绿色渐变背景上，底部有 3 个小房子（`🏠` emoji 或 ASCII 字符）
4. 使用 `Grid` 叠加层，不要替换现有的渐变背景
5. 不要修改其他任何方法或代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 3 — WZ 配置弹窗的浏览按钮绑定文件选择器

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildConfigRow()`（第 524~539 行）

**修复要求**:
1. 给"浏览…"按钮的 `Click` 事件绑定文件/目录选择器
2. 参数 `label` 为 "wzlib 路径" 时用 `OpenFileDialog`（选择 `.dll` 文件，filter: `*.dll|*.dll`）
3. 参数 `label` 为 "BaseWZ 路径" 时用 `OpenFolderDialog`（选择目录）
4. 选择后自动填入 `input.Text`
5. 使用 Avalonia 的 `StorageProvider.OpenFileBookmarkAsync` / `OpenFolderBookmarkAsync`
6. 不要修改其他任何代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 4 — 删除红黄绿圆点按钮

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildTitleBar()`（第 195~223 行）

**修复要求**:
1. **删除**第 198~202 行的 `dots` StackPanel 及其 foreach 循环（红黄绿圆点）
2. 删除 `DockPanel.SetDock(dots, Dock.Left)`（第 202 行）
3. 删除 `bar.Children.Add(dots)`（第 202 行）
4. 确保 `titlePanel` 在 `bar` 中 `HorizontalAlignment = HorizontalAlignment.Left`
5. 不要修改其他任何代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 5 — 添加底部状态栏

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`

**修复要求**:
1. 在 `BuildBody()` 方法（第 240~255 行）中，**在返回之前**，添加一个底部状态栏
2. 状态栏是一个 `Border`，`Height = 24`，`Background = new SolidColorBrush(Color.FromArgb(255, 22, 22, 28))`
3. 状态栏内容是一个 `TextBlock`，水平左对齐，`Margin = new Thickness(10, 0)`
4. 状态栏文本初始显示 `"就绪"`
5. 状态栏的 `Name` 属性设为 `_statusBarText`，在类字段区域添加 `private TextBlock? _statusBarText;`
6. 在 `SaveWzConfig()` 方法中，WZ 加载成功时更新状态栏文本为 `"WZ 加载完成"`，失败时更新为 `"WZ 加载失败: {error}"`
7. 不要修改其他任何代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 6 — 文字水平居中

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`

**修复要求**:
1. `BuildTitleBar()` 第 204~205 行：`titlePanel` 添加 `HorizontalAlignment = HorizontalAlignment.Left`，`Margin = new Thickness(12, 0, 0, 0)`
2. 删除第 205 行的 `Margin = new Thickness(12, 0, 0, 0)`（从 titlePanel 移到了 StackPanel 上）
3. 确保 `🐾 MiniPet 桌宠 — 设置中心` 文字左对齐（在红黄绿圆点被删除后，文字应该紧贴左侧）
4. 不要修改其他任何代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 7 — 素材浏览按钮选中回传

**文件**: 
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`（第 1095~1107 行）
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/MaterialBrowserWindow.cs`（第 321~325 行）

**修复要求**:
1. 在 `MaterialBrowserWindow` 的 `OnMaterialSelected` 事件中，确保 `id` 参数正确传递
2. 在 `SettingsWindow` 的 `OpenMaterialBrowser()` 中，确认 `SelectPet()` 被正确调用
3. 添加日志输出 `Console.WriteLine` 确认事件触发
4. 不要修改其他任何代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 8 — TrayService 静默降级

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/TrayService.cs`

**修复要求**:
1. 确认所有方法都有 try-catch 包裹，catch 中 `Console.Error.WriteLine` 打印异常
2. 托盘菜单包含：显示主窗口 / 退出
3. 在 macOS 上，TrayIcon 不可用时不崩溃，静默降级
4. 不要修改其他任何代码

**验证**: `dotnet build --no-restore` 0 错误

### Agent 9 — 核心动画与交互完善

**文件**: 
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/AnimService.cs`
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`
- `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/BalloonControl.axaml.cs`

**修复要求**:
1. `AnimService.Tick()` 第 386~388 行：帧延迟从硬编码 240ms 改为从 `SpriteStrip.FrameData` 读取
2. `PetWindow`: 拖拽时 `PointerPressed` 切换到 fly，`PointerReleased` 回到 stand
3. 右键菜单补充：切换人物、切换动画、切换背景、切换气泡、Hermes 终端、设置中心、退出
4. `BalloonControl`: 淡入 0.2s → 显示 3~5s → 淡出 0.3s
5. 空闲随机动作启用（`_randomIdleEnabled = true`）
6. 帧循环从 500ms 改为 33ms（30fps）

**验证**: `dotnet build --no-restore` 0 错误

### Agent 10 — 白盒测试

**文件目录**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests/`
**测试框架**: xUnit

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

**规则**:
1. 测试项目独立，不引用主项目（用内嵌最小化模型定义）
2. 纯逻辑测试，不依赖 Avalonia UI
3. 测试必须实际运行通过

**运行命令**: `dotnet test MiniPet.Tests/MiniPet.Tests.csproj --no-restore`

## 工作流

1. **10 个 agent 同时启动**，各自独立工作
2. 每个 agent 完成后，**运行 `dotnet build --no-restore` 确保编译通过**
3. 所有 agent 完成后，**汇总编译结果和测试结果**
4. **如果编译报错**: 记录问题列表 → 修复 → 重新编译
5. **如果测试失败**: 记录失败测试 → 修复代码 → 重新运行测试

## 禁止事项

- 不要重构或重写已完成的代码文件
- 不要修改项目架构（Models/Services/Views 分层）
- 不要修改 `.csproj` 添加新 NuGet 包除非确实需要
- 禁止使用 `System.Drawing`（macOS 不支持）
- 禁止使用 `System.Windows.Forms`