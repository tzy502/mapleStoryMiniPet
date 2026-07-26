# MiniPet v1.0 — 素材预览三个 Bug 修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`（export `DOTNET_ROOT=/Volumes/SSD/C#` && `PATH=$DOTNET_ROOT:$PATH`）
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && dotnet build --no-restore`

## 已知 Bug

### Bug 1 — 选中素材后没有展示

**现象**: 在左侧列表选中「提诺」「蜗牛」或「路西德」后，右侧预览区域没有显示对应的宠物形象/动画，只显示一个渐变色的占位方块。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `UpdatePreviewContent()`（第 650~675 行）

**当前代码**:
```csharp
private void UpdatePreviewContent(string name, string id)
{
    try
    {
        if (_previewCanvas == null) return;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10 };
        var previewFigure = new Border { Width = 160, Height = 160, CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush { ... }, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
        var spriteImage = new Image { Stretch = Stretch.Uniform, Width = 150, Height = 150, Margin = new Thickness(5) };
        previewFigure.Child = spriteImage;
        stack.Children.Add(previewFigure);
        stack.Children.Add(new TextBlock { Text = name, FontSize = 14, ... });
        stack.Children.Add(new TextBlock { Text = id, FontSize = 10, ... });
        _previewCanvas.Child = stack;
        StartPreviewAnimation();
    }
    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] UpdatePreview: {ex.Message}"); }
}
```

**问题**: `spriteImage` 没有设置 `Source`，所以永远是空白。`StartPreviewAnimation()` 虽然有动画帧逻辑，但只改变了预览图标的背景色渐变，没有把真实的精灵图设置到 `spriteImage.Source`。

**修复要求**:
1. 在 `UpdatePreviewContent()` 中，调用 `WzService` 加载选中怪物的 PNG 帧数据
2. 用 `WriteableBitmap` 或 `Bitmap` 把 PNG 数据设置到 `spriteImage.Source`
3. 确保 `StartPreviewAnimation()` 的定时器 `_previewAnimTimer` 能正确更新帧
4. 如果 WZ 未加载或加载失败，显示一个提示文字「未加载 WZ 资源」

### Bug 2 — 文字排版不对

**现象**: 素材预览区域的「路西德」名称和「8880150」编号输入框垂直不对齐，按钮排列不美观。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildMaterialTab()` 第 358~394 行

**修复要求**:
1. `paramBar`（StackPanel）中所有子元素添加 `VerticalAlignment = VerticalAlignment.Center`
2. 按钮顺序改为：「播放」→「收藏」→「确定应用」
3. 确保按钮文字完整显示不被截断（检查 `Padding` 和 `Width`）
4. 确保预览区域宽度不折叠

### Bug 3 — 没有预览

**现象**: 选中素材后，右侧预览区域没有显示出精灵图动画，只有渐变色背景。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `StartPreviewAnimation()`（第 1112~1137 行）和 `OnPreviewAnimTick()`（第 1139~1174 行）

**问题**:
1. `StartPreviewAnimation()` 尝试从 `WzService` 提取 PNG 帧，但 `_previewPngData` 可能为 null
2. `OnPreviewAnimTick()` 只改变了预览图标的背景色渐变，没有把真实帧渲染到 `spriteImage.Source`
3. 预览定时器 240ms 间隔可能不合理

**修复要求**:
1. 在 `StartPreviewAnimation()` 中，确保从 WzService 正确提取 PNG 数据
2. 在 `OnPreviewAnimTick()` 中，把真实帧数据（`_previewPngData`）转换为 `Bitmap` 设置到 `spriteImage.Source`
3. 使用 `Avalonia.Media.Imaging.Bitmap` 从 `byte[]` 创建位图
4. 如果 WZ 未加载，在预览区显示「请先配置 WZ 路径」
5. 确保帧切换时 `spriteImage.Source` 更新

## 验证

`dotnet build --no-restore` 0 错误

改动后运行 `dotnet run --project MiniPet/MiniPet.csproj` 验证：
- 选中怪物后预览区显示精灵图
- 文字垂直对齐
- 动画帧切换正常