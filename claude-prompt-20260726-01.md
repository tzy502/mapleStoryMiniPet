# MiniPet v1.0 — PetWindow 渲染修复

## 项目信息

- **项目路径**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- **.NET SDK**: `/Volumes/SSD/C#`
- **编译命令**: `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && export DOTNET_ROOT=/Volumes/SSD/C# && PATH=$DOTNET_ROOT:$PATH && dotnet build --no-restore`

## Bug 1 — 启动时 PetWindow 在 WZ 加载之前创建

**根因**: `App.axaml.cs` 第 31 行先创建 PetWindow，第 34 行后加载 WZ。PetWindow 构造函数中调 `PreloadMob()` 时 WzService 还没加载完。

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/App.axaml.cs`

**修改**:
1. 删除第 53~62 行自动弹出 SettingsWindow 的代码
2. 在第 51 行后（WZ 加载成功后）添加延迟加载默认宠物：
```csharp
if (ok)
{
    var petWindow = desktop.MainWindow as PetWindow;
    if (petWindow != null)
    {
        var mwz = _serviceProvider.GetRequiredService<WzService>();
        petWindow.LoadDefaultPet(mwz);
    }
}
```

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**添加方法**（在构造函数后面）:
```csharp
public void LoadDefaultPet(WzService wzService)
{
    try
    {
        var settings = _configService.LoadSettings();
        if (settings != null && settings.CurrentMobId > 0)
        {
            var mobId = settings.CurrentMobId;
            _spriteService.PreloadMob(wzService, mobId);
            _animService.SetCurrentMobId(mobId);
            _animService.Play("stand", PlayMode.Loop);
            Console.WriteLine($"[PetWindow] 延迟加载默认宠物: {mobId}");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] LoadDefaultPet: {ex.Message}"); }
}
```

**删除**构造函数中第 90~108 行的旧加载逻辑，保留 `_animService.Play("stand", PlayMode.Loop);` 在原来的位置。

## Bug 2 — 点击「确定应用」后没有 PreloadMob

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`
**第 436~445 行** `OnPetApplied` 事件处理

**当前代码**:
```csharp
_animService.SetCurrentMobId(id);
_animService.SwitchTo("stand");
```

**改为**:
```csharp
_animService.SetCurrentMobId(id);
_spriteService.PreloadMob(_wzService, id);
_animService.SwitchTo("stand");
```

## 只改 2 个文件

1. `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/App.axaml.cs`
2. `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

## 验证

`dotnet build --no-restore` 0 错误