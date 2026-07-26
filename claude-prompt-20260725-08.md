# MiniPet v1.0 — PetWindow 不显示精灵图修复

## 根因

PetWindow 启动时 `_animService.Play("stand")`，但 `_currentMobId = 0`，没有精灵图加载，`Tick()` 永远返回 `(null, null)`，`SpriteCanvas.SetFrame()` 从未被调用。

SettingsWindow 预览能显示是因为直接调 `WzService.BuildSpriteStripPng()`，绕过了缓存链。PetWindow 走的是 `AnimService → SpriteService → CacheManager`，缓存是空的。

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`（export `DOTNET_ROOT=/Volumes/SSD/C#` && `PATH=$DOTNET_ROOT:$PATH`）
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && dotnet build --no-restore`

## 修复 — 只改 PetWindow.axaml.cs 构造函数

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**方法**: `PetWindow` 构造函数，第 34~163 行

**在构造函数末尾（第 89 行 `_timer.Start()` 之后、`_trayService` 之前）插入**:

```csharp
// 启动时加载配置中保存的默认宠物
try
{
    var settings = _configService.LoadSettings();
    if (settings != null && settings.CurrentMobId > 0)
    {
        _animService.SetCurrentMobId(settings.CurrentMobId);
        _spriteService.PreloadMob(_wzService, settings.CurrentMobId);
        _animService.Play("stand", PlayMode.Loop);
    }
}
catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] LoadDefaultPet: {ex.Message}"); }
```

**然后在第 86 行删除旧的无 mob 启动调用**:
```csharp
// 删除这一行（因为会被上面的 try 块中的 Play 替代，或没有宠物时不启动动画）:
_animService.Play("stand", PlayMode.Loop);
```

改为：
```csharp
// 如果没配置宠物，先不启动动画，等用户从设置中选择
if (!loadedPet) // 上面的 try 块中成功加载了宠物则跳过
{
    _animService.Play("stand", PlayMode.Loop);
}
```

**只改 `PetWindow.axaml.cs`，不要修改其他任何代码。**

## 验证

`dotnet build --no-restore` 0 错误