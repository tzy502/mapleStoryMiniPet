# Claude Code 全量开发提示语 — F4 气泡 / F6 纸娃娃 / F10 地图 补完整可用

> 生成日期：2026-07-31
> 模式：A（全量开发，vibe coding）
> 执行方式：**单 Agent 串行**（任务 1 → 2 → 3 → 4 顺序执行，不并行）
> 目标：把三个"底层 Service 有壳、接线全断"的缺口补到**真正可运行**
> 验收标准：编译 0 错误 + 三个功能在运行时实际可用（不是编译通过就完事）

---

## 项目信息

| 项 | 值 |
|------|------|
| 项目根 | `/Users/a502/IdeaProjects/mapleStoryMiniPet` |
| 主项目 | `MiniPet/MiniPet.csproj` |
| 测试项目 | `MiniPet.Tests/MiniPet.Tests.csproj`（独立，不引用主项目） |
| 分支 | `master` |
| SDK | .NET 9.0 |
| SDK 路径 | `DOTNET_ROOT=/Volumes/SSD/C#` |
| 编译命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet build --no-restore` |
| 测试命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet test --nologo` |
| 运行命令 | `cd /Users/a502/IdeaProjects/mapleStoryMiniPet && DOTNET_ROOT=/Volumes/SSD/C# PATH=$DOTNET_ROOT:$PATH dotnet run --project MiniPet/MiniPet.csproj` |
| WZ BaseWZ | `/Volumes/home/Backup/MS/客户端/冒险岛online/mxd/Data/Base` |
| WZ Lib DLL | `/Volumes/home/Backup/MS/拆包/R2/Lib/WzComparerR2.WzLib.dll` |

**⚠️ 编译必须用 `--no-restore`**，否则 NuGet 恢复阶段产生临时文件导致 CS0579 假阳性。

---

## 当前状态（已验证，必须基于此改）

- 主项目编译：**0 错误 0 警告**（`dotnet build --no-restore`）
- 测试项目：**109/109 通过**
- 三个缺口的真实状态（已逐行读过代码）：

### 缺口 1 — F4 聊天气泡（接线全断）

| 现状 | 说明 |
|------|------|
| `BalloonService.RenderBalloon` | ✅ 真实 9-slice SkiaSharp 合成（`BalloonService.cs:42-130`） |
| `BalloonService.Show` | ❌ **空壳**（`BalloonService.cs:158-175`）：只 `await Task.Delay(ms)` 然后调 `Hide()`，没渲染、没调 RenderBalloon、没回调 UI |
| `BalloonControl.ShowBalloon` | ✅ 真实（`BalloonControl.axaml.cs:34-83`）：淡入 0.2s→显示 3~5s→淡出 0.3s |
| `PetWindow` 持有 `_balloonControl` | ✅（`PetWindow.axaml.cs:27,84-86`）实例化并加入 Grid 第 2 行 |
| **触发源** | ❌ 全代码库没有任何地方调用 `BalloonService.Show` 或 `_balloonControl.ShowBalloon` |

### 缺口 2 — F6 纸娃娃（Service 是壳，UI 有壳无逻辑）

| 现状 | 说明 |
|------|------|
| `PaperdollService.CompositeFrame` | ✅ 合成算法真实（20 层 z-order + SkiaSharp，`PaperdollService.cs:36-89`） |
| `PaperdollService.GetActionList` | ❌ **硬编码** `new(){"stand","walk","jump","alert","swingOF","stabOF"}`（`:117`），不查 WZ |
| `PaperdollService.GetEquipList` | ❌ **永远返回空** `new()`（`:119`） |
| `PaperdollService.CompositeWithMount/Chair` | ❌ 直接 `=> CompositeFrame(action,frame)`（`:156-157`），坐骑椅子没实现 |
| `SettingsWindow` 纸娃娃 Tab | ⚠️ UI 框架真实（`SettingsWindow.axaml.cs:989-1038`），有性别/发型/脸型/装备输入框 + 预设管理，但预览 `UpdatePaperdollPreview`（`:1155`）是色块占位，不调用 PaperdollService |
| 右键菜单 PLAYER 入口 | ⚠️ 存在（`PetWindow.axaml.cs:444-451`），点击只调 `ShowSettingsWindow()`，没把纸娃娃结果应用到主窗口 |

### 缺口 3 — F10 地图背景（解析有 bug + 合成画色块 + View 完全没接）

| 现状 | 说明 |
|------|------|
| `MapService.LoadMap` | ✅ WZ 节点遍历框架真实（`MapService.cs:20-68`） |
| `MapService.ParseTiles` | ❌ **取了 u/no 但丢弃**（`:116-117` 取了 `u`/`no` 值，但 `new MapTile{Id,X,Y}` 只填了 3 个字段，u/no 丢失）→ 拼不出纹理路径 |
| `MapService.ParseObjs` | ❌ **完全没解析 oS/l0/l1/l2**（`:124-141` 只取 x/y/z/f）→ obj 纹理路径组件全缺 |
| `MapService.ParseBacks` | ❌ 取了 bS/no 但 `new MapBack` 没填 Resource，且 `MapBack.Resource` 是 `object` 类型（设计错误） |
| `MapService.CalculateBounds` | ❌ **连锁 bug**（`:82-83` 引用 `tile.Resource.Width`，但 Resource 没填，永远为默认 0）→ bounds 算错 |
| `MapService.CompositeMapFrame` | ❌ **画色块占位**（`:182-232`）：tile 画绿色矩形，obj 画棕色矩形，back 循环体空 |
| `MapService.GetMapList` | ⚠️ 硬编码 5 个（`:242-252`），可接受作为默认列表 |
| `BackgroundControl.SetMap` | ❌ **空实现**（`BackgroundControl.axaml.cs:17-21`）：只有 `// Render map via MapService and set source` 注释 |
| `PetWindow` | ❌ 没有 BackgroundControl 实例，grid 只有 infoText/spriteCanvas/balloonControl 三行 |
| `SettingsWindow` 背景 Tab | ⚠️ UI 真实（`:1202-1239`），列表+预览，但 `UpdateMapPreview` 不调 MapService |

---

## 已完成代码清单（禁止重写，只可新增方法/字段）

以下文件已实现且可用，Agent 只能在其中**新增方法/字段**，禁止重写已有方法：

| 文件 | 状态 |
|------|------|
| `MiniPet/Models/SpriteSheet.cs` | ✅ 禁改 |
| `MiniPet/Models/BalloonData.cs` | ✅ 禁改 |
| `MiniPet/Models/CharacterAppearance.cs` | ✅ 禁改 |
| `MiniPet/Models/AppConfig.cs` | ✅ 禁改 |
| `MiniPet/Services/ConfigService.cs` | ✅ 禁改 |
| `MiniPet/Services/CacheManager.cs` | ✅ 禁改 |
| `MiniPet/Services/EventBus.cs` | ✅ 禁改 |
| `MiniPet/Services/AnimService.cs` | ✅ 禁改 |
| `MiniPet/Services/SpriteService.cs` | ✅ 禁改 |
| `MiniPet/Services/TrayService.cs` | ✅ 禁改 |
| `MiniPet/Services/WzService.cs` | ✅ 禁改（可新增方法） |
| `MiniPet/Utils/PngEncoder.cs` | ✅ 禁改 |
| `MiniPet/Views/Controls/SpriteCanvas.axaml.cs` | ✅ 禁改 |
| `MiniPet/Views/MaterialBrowserWindow.cs` | ✅ 禁改 |

**允许修改的文件**（仅限下列）：

| 文件 | 涉及任务 |
|------|----------|
| `MiniPet/Models/MapData.cs` | 任务 3（修 MapBack.Resource 类型） |
| `MiniPet/Services/BalloonService.cs` | 任务 1 |
| `MiniPet/Services/PaperdollService.cs` | 任务 2 |
| `MiniPet/Services/MapService.cs` | 任务 3 |
| `MiniPet/Views/Controls/BalloonControl.axaml.cs` | 任务 1（可不动，已可用） |
| `MiniPet/Views/Controls/BackgroundControl.axaml.cs` | 任务 3 |
| `MiniPet/Views/PetWindow.axaml.cs` | 任务 1/2/3 |
| `MiniPet/Views/SettingsWindow.axaml.cs` | 任务 2/3 |
| `MiniPet/App.axaml.cs` | 任务 1/3（DI 注册） |

---

## 编码约定（硬性规则）

### 1. 异常处理

所有新增 Service 方法和事件处理必须用 try-catch 包裹，catch 中打印到 `Console.Error.WriteLine`，返回安全默认值，不让异常冒泡到主线程（触发 SIGABRT）。

```csharp
try { /* 业务代码 */ }
catch (Exception ex) { Console.Error.WriteLine($"[ServiceName] xxx: {ex.Message}"); return defaultValue; }
```

### 2. 渲染

- **禁止用 `System.Drawing`**（macOS 不支持）。统一用 `SkiaSharp`（`SKBitmap`/`SKCanvas`/`SKImage`）。
- PNG 编码：`SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray()`
- byte[] → Avalonia Bitmap：`using var ms = new MemoryStream(pngData); new Bitmap(ms);`

### 3. WZ 路径与 origin

- `WzService.GetOrigin(wzPath)` 返回 `(int x, int y)`（`WzService.cs:150`），origin 在**原始帧节点**上，已正确处理 UOL，直接用。
- `WzService.ExtractPng(wzPath)` 返回 `byte[]?` PNG。
- `WzService.FindNodeByPath(path)` 返回 `Wz_Node?`。
- `WzService.GetIntProperty(path)` / `GetStringProperty(path)` 取属性值。
- 地图 WZ 路径格式：`Map/Map/Map{首位}/{mapId}.img`（如 `Map/Map/Map0/100000000.img`）。

### 4. 素材必然存在

WZ 内所有纹理路径组件（tileSet、oS、l0/l1/l2、bS、no）**在 WZ 中一定存在**。解析和渲染层**不需要写 `if (png == null) return` 这种防御逻辑**——按规范路径拼接后直接 `ExtractPng` 取纹理即可。防御性 null 检查是伪命题，一律删除。

### 5. 窗口透明性

- 不允许在渲染链路里引入不透明背景。
- `BalloonControl`/`BackgroundControl` 的 `IsHitTestVisible` 不随意改（气泡 `IsHitTestVisible=false` 不阻挡拖拽）。

### 6. 线程模型

- > 50ms 的操作（WZ 节点遍历、纹理提取、合成）放后台线程，完成后 `Dispatcher.UIThread.Post` 回 UI 线程更新。
- `DispatcherTimer` 驱动的帧循环里**不能**做同步 WZ 读取。

### 7. 只改指定范围

- 单 Agent 串行执行，每完成一个任务后立即编译验证再进下一个。
- 不重构、不改架构、不加 NuGet 包（SkiaSharp 已有）。
- 修改前先 `read_file` 确认当前内容，避免基于过期快照改。

---

## 参考项目（关键算法）

| 参考文件 | 参考内容 |
|---------|---------|
| `/Users/a502/IdeaProjects/maplestory-wiki-backend/mapRederWeb/backend/Program.cs` | R2 wzlib DLL 调用、WZ 加载、PNG 提取、origin/delay 解析 |
| `/Users/a502/IdeaProjects/mapRederWeb/frontend/src/index.ts` | PixiJS origin pivot、地图 tile/obj/back 渲染、视差计算 |

### 地图纹理路径拼接规则（任务 3 必须遵循）

WZ 地图的三种图元的纹理路径规则（素材在 WZ 中必然存在，直接拼接）：

| 图元 | WZ 纹理路径格式 | 路径组件来源 |
|------|----------------|-------------|
| **tile** | `Map/Tile/{tS}.img/{u}/{no}` | `tS` = 该 layer 节点的 `info/tS` 属性（**字段名是 tS 不是 tileSet，漏 .img 会找不到节点**）；`u` = tile 节点的 `u` 属性；`no` = tile 节点的 `no` 属性（已取，当前被丢弃） |
| **obj** | `Map/Obj/{oS}.img/{l0}/{l1}/{l2}` | `oS` = obj 节点的 `oS` 属性；`l0`/`l1`/`l2` = obj 节点的同名属性（**当前完全没解析，必须新增**）。obj 节点下还有 `flow`/`z`/`x`/`y`/`f` 属性。**不要硬编码帧号 `/0`**——action 节点下帧数不固定，应迭代子节点取第 0 帧 |
| **back** | `Map/Back/{bS}.img/{aniDir}/{no}` | `bS` = back 节点的 `bS` 属性（已取）；`no` = back 的 `no` 属性。**`aniDir` 按 `Ani` 字段计算**（不是用 `type`，type 是瓦片渲染模式不是路径）：`Ani=0` → `aniDir=back`；`Ani=1` → `aniDir=ani`；`Ani=2` → `aniDir=spine{N}`（N 取 spine 索引） |

> 以上路径格式以 `WzComparerR2.MapRender/MapData.cs`（同款 wzlib DLL）为权威来源，与该源码完全一致。

---

## 串行任务（顺序执行）

### 任务 1 — F4 聊天气泡全链路打通

**目标**：让宠物能在运行时实际显示聊天气泡。

**改动文件**：
1. `MiniPet/Services/BalloonService.cs`
2. `MiniPet/Views/PetWindow.axaml.cs`（只改字段区 + 构造函数 + `OnTimerTick` 后新增方法）
3. `MiniPet/App.axaml.cs`（注册 DI）

#### 1.1 修复 BalloonService.Show 为真实渲染+回调

**文件**：`MiniPet/Services/BalloonService.cs:158-175`

**当前代码**（空壳）：
```csharp
public async Task Show(string text, BalloonData data, int? durationMs = null)
{
    _hideCts?.Cancel();
    _hideCts = new CancellationTokenSource();
    var ms = durationMs ?? _displayDurationMs;
    try
    {
        await Task.Delay(ms, _hideCts.Token);
        Hide();
    }
    catch (TaskCanceledException) { }
}
```

**改为**：`Show` 负责后台线程渲染合成 PNG，然后通过新增的 `OnBalloonReady` 事件把渲染好的 PNG byte[] + text 回调给上层（PetWindow），由上层调 `BalloonControl.ShowBalloon`。

```csharp
// 新增字段（在类顶部）
public event Action<string, BalloonData, byte[]?>? OnBalloonReady;

public async Task Show(string text, BalloonData data, int? durationMs = null)
{
    _hideCts?.Cancel();
    _hideCts = new CancellationTokenSource();
    var ms = durationMs ?? _displayDurationMs;
    try
    {
        byte[]? renderedPng = null;
        await Task.Run(() =>
        {
            try { renderedPng = RenderBalloon(text, data); }
            catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Render: {ex.Message}"); }
        }, _hideCts.Token);

        if (renderedPng != null)
        {
            OnBalloonReady?.Invoke(text, data, renderedPng);
            // 计时隐藏交给 BalloonControl 内部（3~5s 自动淡出）
        }
    }
    catch (TaskCanceledException) { }
    catch (Exception ex) { Console.Error.WriteLine($"[BalloonService] Show: {ex.Message}"); }
}
```

#### 1.2 PetWindow 接入气泡触发

**文件**：`MiniPet/Views/PetWindow.axaml.cs`

**新增字段**（在 `_balloonControl` 字段附近，约 L27）：
```csharp
private readonly BalloonService _balloonService;
private readonly DispatcherTimer _balloonTimer;
private static readonly string[] _randomLines = {
    "今天天气真好~", "加油！", "记得喝水哦", "嘿嘿~", "我在看着你呢",
    "累了就休息一下吧", "你今天也很努力呢", "要一起冒险吗？"
};
```

**修改构造函数**（`:41`）：增加 `BalloonService balloonService` 参数，存入字段；初始化随机气泡定时器：
```csharp
_balloonService = balloonService;
_balloonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(new Random().Next(30, 91)) };
_balloonTimer.Tick += (_, _) => TryShowRandomBalloon();
_balloonTimer.Start();

_balloonService.OnBalloonReady += (text, data, png) =>
{
    Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            if (png != null)
            {
                using var ms = new MemoryStream(png);
                var bmp = new Bitmap(ms);
                await _balloonControl.ShowBalloon(text, data, bmp);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonShow: {ex.Message}"); }
    });
};
```

**新增方法**（在 `OnTimerTick` 之后，约 L352）：
```csharp
private async void TryShowRandomBalloon()
{
    try
    {
        _balloonTimer.Interval = TimeSpan.FromSeconds(new Random().Next(30, 91));
        if (_animService.GetCurrentAction() != "stand") return;
        var line = _randomLines[new Random().Next(_randomLines.Length)];
        var data = _balloonService.LoadBalloon(_configService.LoadSettings().BalloonId);
        if (data == null) return;
        await _balloonService.Show(line, data);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RandomBalloon: {ex.Message}"); }
}

public async Task ShowBalloonNow(string text)
{
    try
    {
        var data = _balloonService.LoadBalloon(_configService.LoadSettings().BalloonId);
        if (data == null) return;
        await _balloonService.Show(text, data);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowBalloonNow: {ex.Message}"); }
}
```

#### 1.3 DI 注册 BalloonService

**文件**：`MiniPet/App.axaml.cs:17-25`

在 `services.AddSingleton<TrayService>();` 之后加一行：
```csharp
services.AddSingleton<BalloonService>();
```

**任务 1 验证**：
- [ ] `dotnet build --no-restore` 0 错误
- [ ] `dotnet test` 109/109 通过
- [ ] 运行后 30~90 秒能看到气泡淡入显示（需有缓存的气泡 tile）

---

### 任务 2 — F6 纸娃娃补真实数据 + UI 接入

**目标**：PaperdollService 从 WZ 查真实装备/动作列表，SettingsWindow 预览调用真实合成，右键菜单 PLAYER 能应用纸娃娃到主窗口。

**改动文件**：
1. `MiniPet/Services/PaperdollService.cs`
2. `MiniPet/Views/SettingsWindow.axaml.cs`（只改 `UpdatePaperdollPreview` + 新增应用方法）
3. `MiniPet/Views/PetWindow.axaml.cs`（新增 `ApplyPaperdoll` 方法 + 修改 `ShowSettingsWindow` 内订阅）

#### 2.1 PaperdollService 从 WZ 查真实数据

**文件**：`MiniPet/Services/PaperdollService.cs:117-119`

**当前**：
```csharp
public List<string> GetActionList() => new() { "stand", "walk", "jump", "alert", "swingOF", "stabOF" };

public List<EquippedItem> GetEquipList(string category) => new();
```

**改为**：
```csharp
public List<string> GetActionList()
{
    try
    {
        var node = _wz.FindNodeByPath("Character/00002000.img");
        if (node != null)
        {
            var actions = new List<string>();
            foreach (Wz_Node child in node.Nodes)
            {
                if (child.Text != "info") actions.Add(child.Text);
            }
            if (actions.Count > 0) return actions;
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] GetActionList: {ex.Message}"); }
    return new() { "stand1", "stand2", "walk1", "walk2", "jump", "alert", "swingOF", "stabOF" };
}

public List<EquippedItem> GetEquipList(string category)
{
    try
    {
        var result = new List<EquippedItem>();
        var dirPath = $"Character/{category}";
        var dirNode = _wz.FindNodeByPath(dirPath);
        if (dirNode != null)
        {
            foreach (Wz_Node child in dirNode.Nodes)
            {
                var name = child.Text.Replace(".img", "");
                if (int.TryParse(name, out int id) && id > 0)
                {
                    var nameNode = child.FindNodeByPath("info/name")?.GetValueEx<string>(null);
                    result.Add(new EquippedItem { Id = id.ToString(), Name = nameNode ?? $"{category} {id}", Category = category });
                    if (result.Count >= 200) break;
                }
            }
        }
        return result;
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] GetEquipList: {ex.Message}"); return new(); }
}
```

#### 2.2 实现 Mount/Chair 合成 + 复用 BuildLayers

**文件**：`MiniPet/Services/PaperdollService.cs`

新增辅助方法 `BuildLayers`，重构 `CompositeFrame`/`CompositeWithMount`/`CompositeWithChair` 复用它（注意：`BuildLayers` 内部按约定素材必存在，不写 png==null 防御，但提取失败仍要 try-catch 防崩）：

```csharp
public byte[]? CompositeFrame(string action, int frameIndex)
{
    try
    {
        var layers = BuildLayers(action, frameIndex, hideBody: false, hideWeapon: false, hideCape: false);
        return CompositeLayers(layers);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] CompositeFrame: {ex.Message}"); return null; }
}

public byte[]? CompositeWithMount(MountInfo mount, string action, int frame)
{
    try
    {
        var layers = BuildLayers(action, frame, hideBody: false, hideWeapon: mount.HideWeapon, hideCape: mount.HideCape);
        var mountPath = $"TamingMob/{mount.Id:00000000}.img/{action}/{frame}";
        var mountPng = _wz.ExtractPng(mountPath);
        using var mSkImg = SKImage.FromEncodedData(mountPng);
        var mountBmp = SKBitmap.FromImage(mSkImg);
        var (mOx, mOy) = _wz.GetOrigin(mountPath);
        layers.Insert(0, (mountBmp, mOx, mOy, -1));
        return CompositeLayers(layers);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] Mount: {ex.Message}"); return null; }
}

public byte[]? CompositeWithChair(ChairInfo chair, string action, int frame)
{
    try
    {
        var layers = BuildLayers(action, frame, hideBody: chair.HideBody, hideWeapon: chair.HideWeapon, hideCape: chair.HideCape);
        var chairPath = $"Item/Cash/0301{chair.Id:0000000}.img/{action}/{frame}";
        var chairPng = _wz.ExtractPng(chairPath);
        using var cSkImg = SKImage.FromEncodedData(chairPng);
        var chairBmp = SKBitmap.FromImage(cSkImg);
        var (cOx, cOy) = _wz.GetOrigin(chairPath);
        layers.Insert(0, (chairBmp, cOx, cOy, -1));
        return CompositeLayers(layers);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] Chair: {ex.Message}"); return null; }
}

private List<(SKBitmap img, int ox, int oy, int z)> BuildLayers(string action, int frameIndex, bool hideBody, bool hideWeapon, bool hideCape)
{
    var layers = new List<(SKBitmap img, int ox, int oy, int z)>();
    if (_current == null) return layers;
    for (int zi = 0; zi < ZOrder.Length; zi++)
    {
        var slot = ZOrder[zi];
        if (hideBody && (slot == "body" || slot == "head" || slot == "arm" || slot == "hand")) continue;
        if (hideWeapon && slot == "weapon") continue;
        if (hideCape && (slot == "cape" || slot == "capeBack")) continue;
        var (category, itemId) = GetPartForSlot(slot, _current);
        if (category == null || itemId == null) continue;
        var wzPath = $"Character/{category}/{itemId.PadLeft(8, '0')}.img/{action}/{frameIndex}/{slot}";
        var pngData = _wz.ExtractPng(wzPath);
        using var skImg = SKImage.FromEncodedData(pngData);
        var bmp = SKBitmap.FromImage(skImg);
        var (ox, oy) = _wz.GetOrigin(wzPath);
        layers.Add((bmp, ox, oy, zi));
    }
    return layers;
}
```

#### 2.3 SettingsWindow 纸娃娃预览调用真实合成

**文件**：`MiniPet/Views/SettingsWindow.axaml.cs:1155`（`UpdatePaperdollPreview` 方法）

**改为**：调用 `PaperdollService.CompositeFrame` 真实合成，显示 PNG 到 `_paperdollPreviewCanvas`。
- 新增字段 `private readonly PaperdollService _paperdollSvc;`，构造函数注入 `PaperdollService`
- 预览逻辑：`SyncPaperdollFromInputs()` → `_paperdollSvc.LoadAppearance(_paperdollCurrent)` → `_paperdollSvc.CompositeFrame("stand1", 0)` → `new Bitmap(new MemoryStream(png))` 显示到预览 Border
- 合成异常时 try-catch 兜底，`_statusBarText.Text = "纸娃娃预览失败（WZ 未加载或装备无效）"`，**不能崩**
- 新增"应用纸娃娃"按钮放在 sidebar 底部，click 绑 `ApplyPaperdoll`

```csharp
public event Action<CharacterAppearance>? OnPaperdollApplied;

private void ApplyPaperdoll()
{
    try
    {
        SyncPaperdollFromInputs();
        OnPaperdollApplied?.Invoke(CloneAppearance(_paperdollCurrent));
        _statusBarText?.Text = "纸娃娃已应用";
    }
    catch (Exception ex) { Console.Error.WriteLine($"[SettingsWindow] ApplyPaperdoll: {ex.Message}"); }
}
```

#### 2.4 PetWindow 接收纸娃娃应用

**文件**：`MiniPet/Views/PetWindow.axaml.cs:633-645`（`ShowSettingsWindow` 方法内）

在 `_settingsWindow.OnPetApplied += ...` 之后，新增订阅：
```csharp
_settingsWindow.OnPaperdollApplied += (appearance) =>
{
    Dispatcher.UIThread.Post(() =>
    {
        try
        {
            var paperdollSvc = new PaperdollService(_wzService, _cacheManager);
            paperdollSvc.LoadAppearance(appearance);
            var png = paperdollSvc.CompositeFrame("stand1", 0);
            if (png != null) _spriteCanvas.SetFrame(png, 0, 0);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollApply: {ex.Message}"); }
    });
};
```
> 注：PetWindow 需新增 `private readonly CacheManager _cacheManager;` 字段，构造函数注入。

**任务 2 验证**：
- [ ] `dotnet build --no-restore` 0 错误
- [ ] `dotnet test` 109/109 通过
- [ ] 纸娃娃 Tab 编辑装备后预览能显示合成图；"应用"能把合成结果显示到主窗口

---

### 任务 3 — F10 地图背景真实渲染 + View 接入

**目标**：修 ParseTiles/Objs/Backs 的数据解析 bug，让 CompositeMapFrame 取真实纹理，BackgroundControl 接 MapService，PetWindow 加背景层，右键菜单切地图。

**这是最重的任务，按 3.0 → 3.1 → 3.2 → 3.3 → 3.4 顺序做。**

**改动文件**：
1. `MiniPet/Models/MapData.cs`（修 MapBack.Resource 类型）
2. `MiniPet/Services/MapService.cs`
3. `MiniPet/Views/Controls/BackgroundControl.axaml.cs`
4. `MiniPet/Views/Controls/BackgroundControl.axaml`
5. `MiniPet/Views/PetWindow.axaml.cs`（新增 BackgroundControl + MapService 字段 + SetBackground 方法 + 改"切换背景"菜单）
6. `MiniPet/Views/SettingsWindow.axaml.cs`（只改 `UpdateMapPreview`）
7. `MiniPet/App.axaml.cs`（DI 注册 MapService）

#### 3.0 修 MapData.cs 的 MapBack.Resource 类型

**文件**：`MiniPet/Models/MapData.cs:77`

**当前**（类型错误）：
```csharp
public object Resource { get; set; } = new object();
```

**改为**：
```csharp
public Sprite Resource { get; set; } = new Sprite();
```

> tile 用 `Sprite`，obj 用 `FrameAnimate`，back 也应该用 `Sprite`（背景是单张图，动画 back 的 Ani 字段另行处理）。

#### 3.1 修 ParseTiles / ParseObjs / ParseBacks 填充 Resource 和路径组件

**文件**：`MiniPet/Services/MapService.cs:105-174`

**ParseTiles 当前**（`:105-122`，丢弃 u/no）：
```csharp
private List<MapTile> ParseTiles(string layerPath, string mapId)
{
    var tiles = new List<MapTile>();
    for (int i = 0; ; i++)
    {
        var tilePath = $"{layerPath}/tile/{i}";
        var node = _wz.FindNodeByPath(tilePath);
        if (node == null) break;
        var x = _wz.GetIntProperty(tilePath + "/x");
        var y = _wz.GetIntProperty(tilePath + "/y");
        var u = _wz.GetStringProperty(tilePath + "/u") ?? "";
        var no = _wz.GetIntProperty(tilePath + "/no");
        tiles.Add(new MapTile { Id = i, X = x, Y = y });
    }
    return tiles;
}
```

**改为**（填充 Resource.ResourceUrl，纹理路径 = `Map/Tile/{tileSet}/{u}/{v}`，tileSet 从 layer 的 `info/tileSet` 取）：
```csharp
private List<MapTile> ParseTiles(string layerPath, string mapId)
{
    var tiles = new List<MapTile>();
    // 取当前 layer 的 tileSet 名（素材必存在）
    var tileSet = _wz.GetStringProperty($"{layerPath}/info/tileSet") ?? "";
    for (int i = 0; ; i++)
    {
        var tilePath = $"{layerPath}/tile/{i}";
        var node = _wz.FindNodeByPath(tilePath);
        if (node == null) break;
        var x = _wz.GetIntProperty(tilePath + "/x");
        var y = _wz.GetIntProperty(tilePath + "/y");
        var u = _wz.GetStringProperty(tilePath + "/u") ?? "";
        var no = _wz.GetIntProperty(tilePath + "/no");
        var v = no.ToString();
        // 纹理路径：Map/Tile/{tileSet}/{u}/{v}，素材必存在
        var texPath = $"Map/Tile/{tileSet}/{u}/{v}";
        tiles.Add(new MapTile
        {
            Id = i, X = x, Y = y,
            Resource = new Sprite { ResourceUrl = texPath }
        });
    }
    return tiles;
}
```

**ParseObjs 当前**（`:124-141`，没解析 oS/l0/l1/l2）：
```csharp
private List<MapObj> ParseObjs(string layerPath, string mapId)
{
    var objs = new List<MapObj>();
    for (int i = 0; ; i++)
    {
        var objPath = $"{layerPath}/obj/{i}";
        var node = _wz.FindNodeByPath(objPath);
        if (node == null) break;
        var x = _wz.GetIntProperty(objPath + "/x");
        var y = _wz.GetIntProperty(objPath + "/y");
        var z = _wz.GetIntProperty(objPath + "/z");
        var f = _wz.GetIntProperty(objPath + "/f");
        objs.Add(new MapObj { Id = i, X = x, Y = y, Z = z, FlipX = f != 0 });
    }
    return objs;
}
```

**改为**（解析 oS/l0/l1/l2，填充 Resource 的第一帧 ResourceUrl）：
```csharp
private List<MapObj> ParseObjs(string layerPath, string mapId)
{
    var objs = new List<MapObj>();
    for (int i = 0; ; i++)
    {
        var objPath = $"{layerPath}/obj/{i}";
        var node = _wz.FindNodeByPath(objPath);
        if (node == null) break;
        var x = _wz.GetIntProperty(objPath + "/x");
        var y = _wz.GetIntProperty(objPath + "/y");
        var z = _wz.GetIntProperty(objPath + "/z");
        var f = _wz.GetIntProperty(objPath + "/f");
        var oS = _wz.GetStringProperty(objPath + "/oS") ?? "";
        var l0 = _wz.GetStringProperty(objPath + "/l0") ?? "";
        var l1 = _wz.GetStringProperty(objPath + "/l1") ?? "";
        var l2 = _wz.GetStringProperty(objPath + "/l2") ?? "";
        // 纹理路径：Map/Obj/{oS}.img/{l0}/{l1}/{l2}，第 0 帧
        var texPath = $"Map/Obj/{oS}.img/{l0}/{l1}/{l2}/0";
        objs.Add(new MapObj
        {
            Id = i, X = x, Y = y, Z = z, FlipX = f != 0,
            Resource = new FrameAnimate { Frames = new() { new Frame { ResourceUrl = texPath } } }
        });
    }
    return objs;
}
```

**ParseBacks 当前**（`:143-174`，没填 Resource，且 MapBack.Resource 现在改成了 Sprite 类型）：
```csharp
backs.Add(new MapBack
{
    Id = i, X = x, Y = y, Cx = cx, Cy = cy, Rx = rx, Ry = ry,
    Alpha = alpha, Front = front != 0, FlipX = flip != 0,
    Ani = ani != 0, Type = type
    // 没填 Resource
});
```

**改为**（填充 Resource.ResourceUrl）：
```csharp
// 纹理路径：静态 back → Back/{bS}.img/{type}/{no}；动画 back → Back/{bS}.img/ani/{no}/{frame}
var bNo = _wz.GetIntProperty(bp + "/no");
string texPath;
if (ani != 0)
    texPath = $"Back/{bS}.img/ani/{bNo}/0";
else
    texPath = $"Back/{bS}.img/{type}/{bNo}";
backs.Add(new MapBack
{
    Id = i, X = x, Y = y, Cx = cx, Cy = cy, Rx = rx, Ry = ry,
    Alpha = alpha, Front = front != 0, FlipX = flip != 0,
    Ani = ani != 0, Type = type,
    Resource = new Sprite { ResourceUrl = texPath }
});
```

#### 3.2 MapService.CompositeMapFrame 真实纹理渲染

**文件**：`MiniPet/Services/MapService.cs:182-232`

**当前**（色块占位）→ **改为**：按 Resource.ResourceUrl 取真实 PNG 纹理贴图。素材必存在，不写 png==null 防御。

```csharp
public byte[]? CompositeMapFrame(MapInfo mapInfo, int viewX, int viewY, int w, int h)
{
    if (mapInfo == null || w <= 0 || h <= 0) return null;
    try
    {
        using var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        // 1. 画 back 层（背景，带视差）
        foreach (var back in mapInfo.Backs.Where(b => !b.Front))
        {
            DrawBack(canvas, back, viewX, viewY);
        }

        // 2. 画 8 层 tile + obj
        for (int i = 0; i < 8; i++)
        {
            var layer = mapInfo.Layers[i];

            foreach (var tile in layer.Tiles)
            {
                var screenX = tile.X - viewX;
                var screenY = tile.Y - viewY;
                var png = _wz.ExtractPng(tile.Resource.ResourceUrl);
                using var tSkImg = SKImage.FromEncodedData(png);
                var tBmp = SKBitmap.FromImage(tSkImg);
                var (tox, toy) = _wz.GetOrigin(tile.Resource.ResourceUrl);
                canvas.DrawBitmap(tBmp, screenX - tox, screenY - toy);
            }

            foreach (var obj in layer.Objs.OrderBy(o => o.Z))
            {
                var screenX = obj.X - viewX;
                var screenY = obj.Y - viewY;
                var framePath = obj.Resource.Frames[0].ResourceUrl;
                var png = _wz.ExtractPng(framePath);
                using var oSkImg = SKImage.FromEncodedData(png);
                var oBmp = SKBitmap.FromImage(oSkImg);
                var (oox, ooy) = _wz.GetOrigin(framePath);
                float dx = screenX - oox;
                float dy = screenY - ooy;
                if (obj.FlipX)
                {
                    canvas.Save();
                    canvas.Scale(-1, 1, dx + oBmp.Width / 2f, dy + oBmp.Height / 2f);
                    canvas.DrawBitmap(oBmp, dx, dy);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawBitmap(oBmp, dx, dy);
                }
            }
        }

        // 3. 画 front back 层
        foreach (var back in mapInfo.Backs.Where(b => b.Front))
        {
            DrawBack(canvas, back, viewX, viewY);
        }

        return SKImage.FromBitmap(result).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
    catch (Exception ex) { Console.Error.WriteLine($"[MapService] Composite: {ex.Message}"); return null; }
}

private void DrawBack(SKCanvas canvas, MapBack back, int viewX, int viewY)
{
    try
    {
        var (px, py) = CalculateParallaxOffset(back, viewX, viewY);
        var screenX = back.X - viewX + px;
        var screenY = back.Y - viewY + py;
        var png = _wz.ExtractPng(back.Resource.ResourceUrl);
        using var bSkImg = SKImage.FromEncodedData(png);
        var bBmp = SKBitmap.FromImage(bSkImg);
        if (back.FlipX)
        {
            canvas.Save();
            canvas.Scale(-1, 1, screenX + bBmp.Width / 2f, screenY + bBmp.Height / 2f);
            canvas.DrawBitmap(bBmp, screenX, screenY);
            canvas.Restore();
        }
        else
        {
            canvas.DrawBitmap(bBmp, screenX, screenY);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"[MapService] DrawBack: {ex.Message}"); }
}
```

> CalculateBounds（`:70-103`）引用了 `tile.Resource.Width/Height`。Resource 填充后会从默认 0 变成真实值——但注意 Sprite 模型的 Width/Height 字段需要在解析时填充。简单方案：CalculateBounds 改为用 X/Y 的 min/max（不依赖 Resource 尺寸），避免额外 WZ 读取。把 `CalculateBounds` 改为：
```csharp
private void CalculateBounds(MapInfo mi)
{
    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
    foreach (var layer in mi.Layers)
    {
        foreach (var tile in layer.Tiles)
        {
            minX = Math.Min(minX, tile.X); minY = Math.Min(minY, tile.Y);
            maxX = Math.Max(maxX, tile.X); maxY = Math.Max(maxY, tile.Y);
        }
        foreach (var obj in layer.Objs)
        {
            minX = Math.Min(minX, obj.X); minY = Math.Min(minY, obj.Y);
            maxX = Math.Max(maxX, obj.X); maxY = Math.Max(maxY, obj.Y);
        }
    }
    if (minX == int.MaxValue) { minX = 0; minY = 0; maxX = 800; maxY = 600; }
    mi.MinX = minX; mi.MinY = minY; mi.MaxX = maxX; mi.MaxY = maxY;
}
```

#### 3.3 BackgroundControl 接 MapService

**文件**：`MiniPet/Views/Controls/BackgroundControl.axaml.cs:17-21`

**当前**（空实现）→ **改为**：
```csharp
public void SetMap(MapInfo? map, MapService? mapService, int viewW = 800, int viewH = 600)
{
    try
    {
        if (map == null || mapService == null) { BgImage.Source = null; return; }
        Task.Run(() =>
        {
            var png = mapService.CompositeMapFrame(map, 0, 0, viewW, viewH);
            Dispatcher.UIThread.Post(() =>
            {
                using var ms = new MemoryStream(png);
                BgImage.Source = new Bitmap(ms);
            });
        });
    }
    catch (Exception ex) { Console.Error.WriteLine($"[BackgroundControl] SetMap: {ex.Message}"); }
}
```

#### 3.4 PetWindow 添加背景层 + 切换地图

**文件**：`MiniPet/Views/PetWindow.axaml.cs`

**新增字段**（L27 附近）：
```csharp
private readonly BackgroundControl _backgroundControl;
private readonly MapService _mapService;
private MapInfo? _currentMap;
```

**修改构造函数**（`:41`）：增加 `MapService mapService` 参数。布局改为 `Panel` 根容器（背景层叠底）：
```csharp
_mapService = mapService;
var rootPanel = new Panel();
_backgroundControl = new BackgroundControl();
rootPanel.Children.Add(_backgroundControl);  // 最底
rootPanel.Children.Add(grid);                  // grid = infoText+sprite+balloon
Content = rootPanel;
```

**修改 `BuildContextMenu` 的"切换背景"项**（`:460-468`）：
当前 `bgItem` 只有一个子项。改为动态生成地图列表 + 关闭背景：
```csharp
var bgItem = new MenuItem { Header = "切换背景" };
var clearBg = new MenuItem { Header = "🚫 关闭背景" };
clearBg.Click += (_, _) =>
{
    try { _currentMap = null; _backgroundControl.SetMap(null, null); }
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ClearBg: {ex.Message}"); }
};
bgItem.Items.Add(clearBg);
bgItem.Items.Add(new Separator());
foreach (var (id, name) in _mapService.GetMapList())
{
    var capturedId = id; var capturedName = name;
    var mi = new MenuItem { Header = $"🗺️ {name}" };
    mi.Click += (_, _) =>
    {
        try
        {
            var map = _mapService.LoadMap(capturedId);
            if (map != null)
            {
                _currentMap = map;
                _backgroundControl.SetMap(map, _mapService);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BgSelect: {ex.Message}"); }
    };
    bgItem.Items.Add(mi);
}
bgItem.Items.Add(new Separator());
var bgSettingsItem = new MenuItem { Header = "⚙️ 更多 (设置中心)" };
bgSettingsItem.Click += (_, _) => ShowSettingsWindow();
bgItem.Items.Add(bgSettingsItem);
```

#### 3.5 SettingsWindow 地图预览调用真实渲染

**文件**：`MiniPet/Views/SettingsWindow.axaml.cs:1241`（`UpdateMapPreview` 方法）

**改为**调用 MapService 真实渲染（小尺寸预览 400×300）显示到 `_mapPreviewCanvas`：
- 新增 `MapService` 引用（构造函数注入）
- 渲染失败 try-catch 兜底，fallback 到当前色块占位显示

#### 3.6 DI 注册 MapService

**文件**：`MiniPet/App.axaml.cs`

在 `services.AddSingleton<BalloonService>();`（任务 1 加的）之后加：
```csharp
services.AddSingleton<MapService>();
```

**任务 3 验证**：
- [ ] `dotnet build --no-restore` 0 错误
- [ ] `dotnet test` 109/109 通过
- [ ] 右键"切换背景"→选地图→主窗口显示地图背景
- [ ] "关闭背景"恢复透明
- [ ] 设置中心背景 Tab 能预览真实渲染

---

## 工作流

```
1. 任务 1（F4 气泡）→ 编译验证 → 进下一步
2. 任务 2（F6 纸娃娃）→ 编译验证 → 进下一步
3. 任务 3（F10 地图，按 3.0→3.6 子步）→ 编译验证 → 进下一步
4. 全部完成后整体运行时验证
5. 黑盒验证由 Hermes 其他 profile 执行（截图 + vision_analyze）
```

每完成一个任务立即 `dotnet build --no-restore`，0 错误再进下一个。不要攒到最后一起编译。

---

## 禁止事项

1. **禁止重写已有可用的代码**（见"已完成代码清单"）
2. **禁止重构架构**（不加新项目、不改分层）
3. **禁止添加 NuGet 包**（SkiaSharp 已有）
4. **禁止改 `PetWindow.OnTimerTick`**（帧循环逻辑已稳定）
5. **禁止用 `System.Drawing`**
6. **禁止在帧循环里做同步 WZ 读取**
7. **禁止写素材不存在的防御逻辑**（WZ 内纹理路径组件一定存在，按规范拼接直接 ExtractPng）
8. **修改前必须 `read_file` 确认当前内容**
9. **所有新增方法必须 try-catch**（防崩，但不做素材存在性防御）
10. **DI 链改动同步**：PetWindow 构造函数新增 BalloonService（任务1）、MapService（任务3）、CacheManager（任务2）参数后，App.axaml.cs 的 `GetRequiredService<PetWindow>()` 会自动解析，无需额外改。

---

## 验收总标准

| 功能 | 验收点 |
|------|--------|
| F4 气泡 | 运行后随机触发气泡显示；`ShowBalloonNow` 可外部调用 |
| F6 纸娃娃 | Tab 内编辑装备→预览真实合成图→应用→主窗口显示合成结果 |
| F10 地图 | 右键切地图→主窗口显示背景；关闭背景恢复透明；设置中心可预览 |
| 编译 | `dotnet build --no-restore` 0 错误 |
| 测试 | `dotnet test` ≥109 通过（不回退） |
