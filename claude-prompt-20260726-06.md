# MiniPet v1.0 — 托盘菜单补充 + 精灵图显示修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 托盘右键菜单缺少「设置中心」和「重置到桌面中央」

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/TrayService.cs`
**方法**: `BuildTrayMenu()` 第 151~192 行

**当前菜单**: 显示主窗口 | 退出

**改为**: 显示主窗口 | 设置中心 | 重置到桌面中央 | 退出

在 `menu.Items.Add(showItem)` 之后、`exitItem` 之前添加：

```csharp
// 设置中心
var settingsItem = CreateMenuItem("设置中心", () =>
{
    try
    {
        if (_mainWindow == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // 触发 EventBus 打开设置窗口
            _eventBus.Publish(EventType.OpenSettings);
        });
    }
    catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Settings: {ex.Message}"); }
});
menu.Items.Add(settingsItem);

menu.Items.Add(new NativeMenuItemSeparator());

// 重置到桌面中央
var centerItem = CreateMenuItem("重置到桌面中央", () =>
{
    try
    {
        if (_mainWindow == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is Window win)
            {
                var screens = win.Screens;
                if (screens != null && screens.ScreenCount > 0)
                {
                    var screen = screens.Primary ?? screens.All[0];
                    var bounds = screen.Bounds;
                    win.Position = new PixelPoint(
                        (int)(bounds.X + (bounds.Width - win.Width) / 2),
                        (int)(bounds.Y + (bounds.Height - win.Height) / 2));
                }
            }
        });
    }
    catch (Exception ex) { Console.Error.WriteLine($"[TrayService] Center: {ex.Message}"); }
});
menu.Items.Add(centerItem);

menu.Items.Add(new NativeMenuItemSeparator());
```

然后在 `EventBus` 或 `EventType` 中添加 `OpenSettings` 事件类型。如果已有则使用已有事件。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/EventBus.cs`
**检查** `EventType.OpenSettings` 是否存在，不存在则添加。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**在构造函数中**订阅 `OpenSettings` 事件：
```csharp
_eventBus.Subscribe(EventType.OpenSettings, _ =>
{
    Dispatcher.UIThread.Post(() =>
    {
        try { ShowSettingsWindow(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenSettings: {ex.Message}"); }
    });
});
```

## Bug 2 — 精灵图显示不全，窗口只显示左上角

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml.cs`
**方法**: `SetFrame()`

**当前代码**在设置 `_currentBitmap` 后调了 `Width = pixelSize.Width` 等，但 `Canvas` 不是 `AutoSize` 容器。

**改为**: 不在 SetFrame 中调 Width/Height，在 PetWindow 中直接根据精灵图尺寸调整窗口大小。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**修改 `OnTimerTick` 方法**:

```csharp
var (fd, pngData) = _animService.Tick(delta);
if (pngData != null)
{
    _spriteCanvas.SetFrame(pngData);

    // 每帧检查精灵图实际大小，让窗口大小自适应
    if (_spriteCanvas.SpriteImage.Source is Bitmap bmp)
    {
        var ps = bmp.Size;
        var newW = (int)ps.Width;
        var newH = (int)ps.Height;
        if (Width != newW || Height != newH)
        {
            Width = newW;
            Height = newH;
        }
    }
}
```

**注意**: 使用 `SizeToContent` 模式时，直接设 `Width/Height` 可能被覆盖。改为不使用 `SizeToContent`，手动设宽高。或者保持 `SizeToContent` 但确保 `SpriteCanvas` 没有固定宽高。

**改为**:
1. 删除 PetWindow 的 `SizeToContent`（如果有）
2. 在 PetWindow 构造函数中初始化一个合理的默认尺寸（如 320×300）
3. 在 `OnTimerTick` 中根据精灵图实际尺寸调整窗口大小
4. `_spriteCanvas` 不设固定 Width/Height

## 验证

`dotnet build --no-restore` 0 错误