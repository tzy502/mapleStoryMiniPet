# MiniPet v1.0 — 点击「确定应用」后不渲染修复

## 根因

`PetWindow.ShowSettingsWindow()` 中 `OnPetApplied` 事件只调了 `SetCurrentMobId` 和 `SwitchTo`，**没有调 `PreloadMob`**，所以精灵图缓存一直是空的，`Tick()` 永远返回 `(null, null)`。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**第 436~445 行**:

```csharp
_settingsWindow.OnPetApplied += (id, name) =>
{
    try
    {
        _animService.SetCurrentMobId(id);
        _animService.SwitchTo("stand");           // ← 只改了 mobId，但缓存里没数据
        // 缺少: _spriteService.PreloadMob(_wzService, id);
        _eventBus.Publish(EventType.PetChanged, (id, name));
    }
    catch (Exception ex) { ... }
};
```

## 修复

在 `SetCurrentMobId` 和 `SwitchTo` 之间，添加 `PreloadMob`：

```csharp
_settingsWindow.OnPetApplied += (id, name) =>
{
    try
    {
        _animService.SetCurrentMobId(id);
        _spriteService.PreloadMob(_wzService, id);  // ← 添加这一行
        _animService.SwitchTo("stand");
        _eventBus.Publish(EventType.PetChanged, (id, name));
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnPetApplied: {ex.Message}"); }
};
```

**只改第 440 行后加一行，不要修改其他任何代码。**

## 验证

`dotnet build --no-restore` 0 错误