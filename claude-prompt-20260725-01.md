# MiniPet v1.0 — WZ 配置弹窗修复

## 问题

WZ 配置弹窗的「浏览…」按钮已经绑定了文件选择器，但 wzlib 路径的选择目标是**DLL 文件本身**。用户只需要知道 DLL 所在的**目录**即可，不需要精确到 DLL 文件。

## 修复要求

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/SettingsWindow.axaml.cs`
**方法**: `BuildConfigRow()` 第 556~588 行的 `browseBtn.Click` 事件

**当前代码**（第 562~574 行）:
```csharp
if (label == "wzlib 路径")
{
    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
        Title = "选择 wzlib DLL 文件",
        AllowMultiple = false,
        FileTypeFilter = new List<FilePickerFileType>
        {
            new("DLL 文件") { Patterns = new[] { "*.dll" } }
        }
    });
    if (files.Count > 0)
        capturedInput.Text = files[0].Path.LocalPath;
}
```

**修改为**:
```csharp
if (label == "wzlib 路径")
{
    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    {
        Title = "选择 wzlib 目录",
        AllowMultiple = false
    });
    if (folders.Count > 0)
        capturedInput.Text = folders[0].Path.LocalPath;
}
```

**只改这一处**，不要修改任何其他代码。

## 验证

`dotnet build --no-restore` 0 错误