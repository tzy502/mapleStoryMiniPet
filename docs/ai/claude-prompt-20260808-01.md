# MiniPet v1.1 — WZ 加载失败弹窗 + 刷新素材 + 默认 wzlib 兜底

> 提示语版本：claude-prompt-20260808-01（Mode B 增量开发，单 Agent 串行）
> 日期：2026-08-08
> 对应需求：F12/F13/F14（docs/requirements-analysis.md 第 7 章，已逐条确认）
> 对应设计：docs/software-design.md 第 10 章（10.1~10.8）

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 主项目：`/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`
- 测试项目：`/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet.Tests/`（独立，不引用主项目）
- .NET SDK（注意 dotnet 是**可执行文件**，必须引号调用）：
  ```bash
  export DOTNET_ROOT="/Volumes/SSD/C#"
  export PATH="$DOTNET_ROOT:$PATH"
  "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore   # 编译（必须 0 错误）
  "/Volumes/SSD/C#/dotnet" test MiniPet.Tests/MiniPet.Tests.csproj --no-build  # 109/109 通过
  ```
- 启动（GUI 需显示器唤醒）：
  ```bash
  caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
  ```

## 本次任务（单 Agent 串行，按顺序完成）

### 任务 1 — WzService：wzlib 校验 + 错误文案映射

**文件**：`MiniPet/Services/WzService.cs`（以及 `MiniPet/Services/WzError.cs`）

**1a. 新增字段**（WzService 类内）：
```csharp
public string? LastWzLibWarning { get; private set; }   // 用户 wzlib 配置无效时设置
```

**1b. LoadWz 开头新增 wzlib 校验**：位置在 `_loading` 守卫之后、`baseWzPath` 校验（`string.IsNullOrEmpty(baseWzPath) || !Directory.Exists(baseWzPath)`）之前：

```csharp
// wzlib 路径校验（仅校验与提示，运行时仍用编译期内置 DLL）
try
{
    if (string.IsNullOrEmpty(wzLibPath))
    {
        LastWzLibWarning = null;
    }
    else if (File.Exists(wzLibPath))
    {
        LastWzLibWarning = null;
    }
    else if (Directory.Exists(wzLibPath))
    {
        if (File.Exists(Path.Combine(wzLibPath, "Lib", "WzComparerR2.WzLib.dll"))
            || File.Exists(Path.Combine(wzLibPath, "WzComparerR2.WzLib.dll")))
        {
            LastWzLibWarning = null;
        }
        else
        {
            LastWzLibWarning = "用户配置的 wzlib 无效，已回退默认";
        }
    }
    else
    {
        LastWzLibWarning = "用户配置的 wzlib 无效，已回退默认";
    }
}
catch (Exception ex) { Console.Error.WriteLine($"[WzService] wzlib 校验: {ex.Message}"); LastWzLibWarning = null; }
```

> ⚠️ 校验失败**只设警告，不 return，不阻断加载**——默认 DLL 仍可用。

**1c. WzError → 中文文案映射**（放 WzService 静态方法或 WzError.cs 扩展均可，实现后编译确认）：

| WzError | 文案 |
|---------|------|
| DllNotFound | wzlib DLL 文件不存在 |
| DllLoadFailed | wzlib DLL 加载失败 |
| WzPathInvalid | BaseWZ 路径无效 |
| WzFileCorrupted | WZ 文件损坏 |
| VersionMismatch | WZ 版本不兼容 |
| Unknown | 未知错误 |

签名：`public static string GetErrorText(WzError err)`，switch 表达式返回对应中文。

### 任务 2 — 新增 WzLoadFailWindow 弹窗

**文件**：`MiniPet/Views/WzLoadFailWindow.cs`（**纯代码窗口，不创建 .axaml**，参考现有纯代码窗口模式）

```csharp
public class WzLoadFailWindow : Window   // 不是 partial，不依赖 .axaml
{
    public WzLoadFailWindow(string errorText, string? wzLibWarning)
    {
        // 窗口属性
        Title = "WZ 素材读取失败";
        Width = 460;
        // 不设固定 Height，内容自适应；SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SystemDecorations = SystemDecorations.None;   // 无边框
        Background = new SolidColorBrush(Color.FromArgb(242, 38, 38, 44));
        // 普通窗口，不要 Topmost（不让弹窗压过一切）
    }
}
```

**内容结构**（纵向 StackPanel，暗色风格对齐现有 SettingsWindow 弹窗配色）：
1. 标题行：「WZ 素材读取失败」（粗体，FontSize 15）
2. 错误原因：errorText（FontSize 13）
3. wzLibWarning 非空时：追加一行黄色提示（如 `#E6C46A` 前景色）
4. 下载引导区：文案「可前往 GitHub 下载 WzComparerR2（含 WzComparerR2.WzLib.dll）」
5. 可点击链接 `https://github.com/Kagamia/WzComparerR2/releases`——用 TextBlock 蓝色下划线样式，PointerPressed 事件里：
   ```csharp
   try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/Kagamia/WzComparerR2/releases", UseShellExecute = true }); }
   catch (Exception ex) { Console.Error.WriteLine($"[WzLoadFailWindow] 打开链接: {ex.Message}"); }
   ```
   （需要 `using System.Diagnostics;`）
6. 底部「知道了」按钮 → `Close();`

**注意**：
- 链接 TextBlock 加 `Cursor = new Cursor(StandardCursorType.Hand)`，PointerPressed 打开浏览器
- 整个窗口构造用 try-catch 包裹，构造异常打印到 Console.Error 不抛
- 所有控件用代码构建（StackPanel/TextBlock/Button），不依赖任何 .axaml

### 任务 3 — SettingsWindow：刷新素材按钮 + 事件

**文件**：`MiniPet/Views/SettingsWindow.axaml.cs`

**3a. 新增事件**（类内其他事件附近）：
```csharp
/// <summary>WZ 素材刷新成功（含保存后自动刷新）后触发，PetWindow 据此重载当前宠物。</summary>
public event Action? OnWzRefreshed;
```

**3b. BuildWzOverlay 按钮行**（现 L643-650）：`[取消][保存]` → `[刷新当前素材][取消][保存]`（刷新按钮放最左，主操作靠前）：
```csharp
var refreshBtn = new Button { Content = "刷新当前素材", ... };
refreshBtn.Click += async (_, _) => await RefreshWzMaterialAsync();
btnRow.Children.Add(refreshBtn);
// 原有 cancelBtn / saveBtn 保持，顺序：refreshBtn, cancelBtn, saveBtn
```
按钮样式对齐 saveBtn（BrushAccent 背景可区分，或次级样式），存入字段 `_wzRefreshBtn` 以便改禁用态/文案。

**3c. 新增 `RefreshWzMaterialAsync()` 方法**（异步，防 UI 冻结）：

```csharp
private async Task RefreshWzMaterialAsync()
{
    try
    {
        if (_wzRefreshBtn == null) return;
        _wzRefreshBtn.IsEnabled = false;
        var oldText = _wzRefreshBtn.Content?.ToString() ?? "刷新当前素材";
        _wzRefreshBtn.Content = "刷新中…";

        var libPath = _wzLibInput?.Text ?? string.Empty;
        var basePath = _baseWzInput?.Text ?? string.Empty;

        // 异步加载 WZ（目录加载耗时，不能卡 UI 线程）
        var (success, error) = await Task.Run(() => _wzService.LoadWz(libPath, basePath));

        // 回主线程更新 UI
        if (success)
        {
            var mobCount = _wzService.ListMobIds().Count;
            var mapCount = _wzService.ListMapIds().Count;
            if (_statusBarText != null) _statusBarText.Text = $"素材刷新完成：Mob {mobCount} 个 / Map {mapCount} 个";
            // 刷新宠物列表（现有方法）
            LoadPetListFromWz();
            // 通知 PetWindow 重载当前宠物
            OnWzRefreshed?.Invoke();
        }
        else
        {
            var errText = WzService.GetErrorText(error ?? WzError.Unknown);
            if (_statusBarText != null) _statusBarText.Text = $"素材刷新失败：{errText}";
            // 弹窗内提示，不关闭弹窗（用户可改路径重试）
            ShowWzOverlayToast($"素材刷新失败：{errText}" + (_wzService.LastWzLibWarning != null ? $"\n{_wzService.LastWzLibWarning}" : ""));
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SettingsWindow] RefreshWzMaterialAsync: {ex.Message}");
        if (_statusBarText != null) _statusBarText.Text = $"素材刷新失败：{ex.Message}";
    }
    finally
    {
        if (_wzRefreshBtn != null) { _wzRefreshBtn.IsEnabled = true; _wzRefreshBtn.Content = "刷新当前素材"; }
    }
}
```

**3d. 新增 `ShowWzOverlayToast(string msg)`**：在 WZ overlay 弹窗内显示一条提示文字（footer 上方加一个 TextBlock 或复用现有布局，简单实现：footer 文案临时替换，2 秒后恢复）。实现从简，保证可见即可。

**3e. SaveWzConfig 复用刷新路径**（现 L724-756）：保存成功后**不再同步调 LoadWz**，改为：
```csharp
_config.SaveWzConfig(_wzConfig);
HideWzOverlay();
if (!string.IsNullOrEmpty(_wzConfig.BaseWzPath))
{
    // 保存后自动刷新（异步，走同一路径）
    _ = RefreshWzMaterialAsync();   // fire-and-forget，内部已 try-catch
}
else
{
    if (_statusBarText != null) _statusBarText.Text = "未配置 BaseWZ 路径";
}
```
> 注意：删除原来 SaveWzConfig 里的同步 LoadWz 块（L733-753），避免双重加载。刷新按钮不隐式保存（刷新只改内存加载态，保存只持久化，保存后再自动刷新一次）。

### 任务 4 — PetWindow：订阅刷新事件

**文件**：`MiniPet/Views/PetWindow.axaml.cs`

在创建 SettingsWindow 处（现 L646-648，`new SettingsWindow(...)` 后）新增订阅：
```csharp
_settingsWindow.OnWzRefreshed += () =>
{
    try { LoadDefaultPet(_wzService); }   // 重载当前宠物（从新 WZ）
    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OnWzRefreshed: {ex.Message}"); }
};
```

### 任务 5 — App.axaml.cs：启动失败弹窗

**文件**：`MiniPet/App.axaml.cs`

启动分支现 L52-61 失败路径（`ok == false` 分支）改造为：

```csharp
else
{
    Console.Error.WriteLine($"[App] WZ 自动加载失败: {err}");
    // 弹窗提示（一次启动只弹一次）
    try
    {
        var failWindow = new WzLoadFailWindow(
            WzService.GetErrorText(err ?? WzError.Unknown),
            wzService.LastWzLibWarning);
        failWindow.Show();
    }
    catch (Exception ex) { Console.Error.WriteLine($"[App] 失败弹窗: {ex.Message}"); }
    // 缓存加载默认宠物（现有逻辑保留）
    if (petWindow != null)
    {
        var spriteService = _serviceProvider.GetRequiredService<SpriteService>();
        petWindow.LoadDefaultPet(null);
    }
}
```

catch 异常分支（现 L63-68）同理：弹 `WzLoadFailWindow(WzService.GetErrorText(WzError.Unknown), wzService.LastWzLibWarning)`，然后保留现有缓存加载逻辑。

> ⚠️ 需要 `using MiniPet.Views;`（确认已有）和 `using MiniPet.Services;`（WzError 命名空间）。WzLoadFailWindow 的 Show() 在 UI 线程调用（App.OnFrameworkInitializationCompleted 主线程内），直接 Show 即可。

## 编码约定（硬性规则）

1. **所有方法 try-catch 包裹**，catch 里 `Console.Error.WriteLine($"[类名] 方法名: {ex.Message}")`，不让异常冒泡（防 SIGABRT）
2. **纯代码窗口**：不创建新 .axaml 文件；窗口类声明不加 partial；代码构建 UI
3. **异步 UI**：WZ 目录加载必须在 `Task.Run` 里，UI 更新用 Dispatcher.UIThread 或 await 后直接操作（确保在主线程）
4. **if 后面必须加 {}**，即使只有一行
5. **不引入新 NuGet 包**，不重构无关代码
6. **不改其他文件**——只改本提示语列出的文件
7. 编译 0 错误才交付；`dotnet build --no-restore` 验证（首次 build 可能因 NuGet 恢复误报，用 --no-restore 确认真实状态）

## 验证清单（全部通过才算完成）

1. `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore` → 0 错误
2. `"/Volumes/SSD/C#/dotnet" test MiniPet.Tests/MiniPet.Tests.csproj --no-build` → 109/109 通过
3. 检查新增文件存在：`MiniPet/Views/WzLoadFailWindow.cs`
4. grep 确认关键改动落地：
   - `grep -n "LastWzLibWarning" MiniPet/Services/WzService.cs`（≥3 处：字段+校验）
   - `grep -n "GetErrorText" MiniPet/Services/WzService.cs`（定义 + App/SettingsWindow 调用）
   - `grep -n "刷新当前素材" MiniPet/Views/SettingsWindow.axaml.cs`
   - `grep -n "OnWzRefreshed" MiniPet/Views/SettingsWindow.axaml.cs MiniPet/Views/PetWindow.axaml.cs`
   - `grep -n "WzLoadFailWindow" MiniPet/App.axaml.cs`

## 禁止事项

- 禁止重构/格式化与任务无关的代码
- 禁止改动画/地图/气泡等无关模块
- 禁止添加 NuGet 包
- 禁止删除现有方法（LoadPetListFromWz、LoadDefaultPet 等均保留）
- 禁止改测试项目
- 禁止 git 提交（工作区改动即可）
