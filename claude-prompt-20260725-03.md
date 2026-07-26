# MiniPet v1.0 — 三个 Bug 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`（编译前 export `DOTNET_ROOT=/Volumes/SSD/C#` && `PATH=$DOTNET_ROOT:$PATH`）
- **编译命令**: `dotnet build --no-restore`
- **运行命令**: `dotnet run --project MiniPet/MiniPet.csproj`

## 多 Agent 并行任务

### Agent 1 — 文字水平居中

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildTitleBar()` 第 195~223 行

**问题**: `🐾 MiniPet 桌宠 — 设置中心` 文字没有在标题栏中水平居中。

**修复**:
1. 找到 `titlePanel`（`StackPanel`，第 204 行），将其改为：
```csharp
var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
```
2. **删除**第 205 行 `titlePanel` 的 `Margin = new Thickness(12, 0, 0, 0)`（因为居中对齐不需要左边距）
3. 确保 `bar`（`DockPanel`）的子元素布局正确——`titlePanel` 居中，`rightPanel` 靠右

**只改这 2 处**，不要修改其他任何代码。

**验证**: `dotnet build --no-restore` 0 错误

### Agent 2 — 右键退出进程不关闭

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**方法**: 右键菜单「退出」按钮的 Click 事件

**问题**: 点击右键菜单「退出」后，进程没有真正退出，最后强制关闭才关闭。

**修复**:
1. 找到「退出」MenuItem 的 Click 事件（第 214~225 行）
2. 在 `_eventBus.Publish(EventType.AppExit)` 和 `Close()` 之后，添加：
```csharp
Environment.Exit(0);
```
3. 确保 `Environment.Exit(0)` 在 `Close()` 之后调用

**只改这一处**，不要修改其他任何代码。

**验证**: `dotnet build --no-restore` 0 错误

### Agent 3 — macOS 顶部状态栏没有展示托盘图标

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/TrayService.cs`

**问题**: 在 macOS 上，`TrayIcon` 没有显示在顶部状态栏。

**修复**:
1. 检查 `TrayService` 的 `ShowTrayIcon()` 方法，确认 `TrayIcon` 的创建和显示逻辑
2. 在 macOS 上，Avalonia 的 `TrayIcon` 有时需要设置 `ToolTipText` 和 `Icon` 才能显示
3. 确保 `TrayIcon` 的 `Icon` 属性从项目资源或文件路径加载
4. macOS 上 `TrayIcon` 的菜单项点击事件需要 `Dispatcher.UIThread.Post` 包裹
5. 如果仍然无法显示，添加日志输出 `Console.WriteLine` 确认 `ShowTrayIcon()` 被调用

**只改 `TrayService.cs`**，不要修改其他任何代码。

**验证**: `dotnet build --no-restore` 0 错误

## 工作流

1. **3 个 agent 同时启动**，各自独立工作
2. 每个 agent 完成后，运行 `dotnet build --no-restore` 确保编译通过
3. 所有 agent 完成后，汇总编译结果

## 禁止事项

- 不要重构或重写已完成的代码文件
- 不要修改项目架构
- 不要修改 `.csproj`
- 禁止使用 `System.Drawing`