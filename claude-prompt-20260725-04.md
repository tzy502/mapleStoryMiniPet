# MiniPet v1.0 — 素材预览行对齐修复

## 问题

「素材桌宠设置」标签页中，预览区域的「路西德」名称和 `8880150` 编号输入框**不在同一水平高度**，名称偏上，输入框偏下，需要垂直对齐。

## 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildMaterialTab()` 第 358~373 行的 `paramBar`（StackPanel）

**当前代码**（第 358~373 行）:
```csharp
var paramBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
_petNameLabel = new TextBlock { Text = "路西德", FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
paramBar.Children.Add(_petNameLabel);
_petIdInput = new TextBox { Text = "8880150", Width = 80, FontSize = 11, ... };
paramBar.Children.Add(_petIdInput);
```

**修改**:
1. 给 `_petNameLabel` 添加 `VerticalAlignment = VerticalAlignment.Center`（如果已有则保留）
2. 给 `_petIdInput` 添加 `VerticalAlignment = VerticalAlignment.Center`
3. 确保 `paramBar` 本身也有 `VerticalAlignment = VerticalAlignment.Center`
4. 所有按钮（收藏、播放、确定应用）也添加 `VerticalAlignment = VerticalAlignment.Center`

**只改 `BuildMaterialTab()` 方法中 `paramBar` 及其子元素的 `VerticalAlignment`**，不要修改其他代码。

## 验证

`dotnet build --no-restore` 0 错误