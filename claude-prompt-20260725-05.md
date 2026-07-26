# MiniPet v1.0 — 素材预览栏布局修复

## 问题

素材桌宠设置标签页的预览区域，按钮排列不美观：
1. 名称和编号输入框不在同一水平高度
2. 按钮被折叠/显示不全，部分按钮只显示一半
3. 按钮顺序不符合常规逻辑（「播放」应该在「收藏」前面）

## 修复

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildMaterialTab()` 第 358~394 行

**当前布局**（paramBar + zoomRow + bgRow 在一个 StackPanel 里）:
```
[路西德] [8880150] [★ 收藏] [▶ 播放] [确定应用]
[缩放] [=====o=====] [100%]
[背景设置] [透明] [网格] [弓箭手村] [市场] [森林]
```

**改为**:

第一行（水平居中对齐）:
```
[路西德] [8880150]    [▶ 播放] [★ 收藏]    [确定应用]
```

第二行:
```
[缩放] [=====o=====] [100%]
```

第三行:
```
[背景设置] [透明] [网格] [弓箭手村] [市场] [森林]
```

**具体修改**:

1. **垂直对齐**: 确保 `paramBar` 中所有子元素都有 `VerticalAlignment = VerticalAlignment.Center`
2. **按钮顺序**: 把「播放」按钮移到「收藏」按钮前面
3. **按钮尺寸**: 检查按钮的 `Padding` 和 `Width`，确保文字完整显示不被截断
4. **预览区域宽度**: 检查 `preview` StackPanel 的宽度是否足够，按钮不会被右侧边界截断
5. 必要时调整 `paramBar` 的 `Spacing` 或给按钮设置合适的 `Margin`

**只改 `BuildMaterialTab()` 方法**，不要修改其他任何代码。

## 验证

`dotnet build --no-restore` 0 错误