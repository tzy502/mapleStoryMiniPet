# MiniPet Windows 构建与运行指南

本文件记录在 **Windows 真机**上构建、发布、运行 MiniPet 的步骤与注意事项（macOS 侧无法真跑 Windows，静态兼容性审查结论见 [WINDOWS-VERIFY.md](WINDOWS-VERIFY.md)）。

## 环境要求

- Windows 10/11 x64（本项目发布目标为 `win-x64`）
- .NET 9 SDK（含 Runtime）：https://dotnet.microsoft.com/download
- WZ 资源目录（与 macOS 同一套本地 WZ 文件）
- （可选）Agent 工具链：Hermes CLI / Claude Code / Codex CLI / DSH——各自按官方方式安装

## 构建（Debug）

```powershell
# 在仓库根目录执行
dotnet build MiniPet\MiniPet.csproj -c Debug -p:UsedAvaloniaProducts=
```

> `-p:UsedAvaloniaProducts=`：关闭 Avalonia 构建期 used-products 分析（与 macOS 验收命令一致，空值即禁用）。

## 测试

```powershell
dotnet test MiniPet.Tests\MiniPet.Tests.csproj -c Debug
```

## 发布（self-contained win-x64，免装 .NET 的目标机可直接运行）

```powershell
dotnet publish MiniPet\MiniPet.csproj -c Release -r win-x64 --self-contained true
# 产物：MiniPet\bin\Release\net9.0\win-x64\publish\MiniPet.exe
```

或一键执行 `scripts\build-win.ps1 -Publish`（编译 + 测试 + 发布三段式）。

## 运行

```powershell
# Debug 模式
dotnet run --project MiniPet\MiniPet.csproj

# 发布产物（免 .NET）
.\MiniPet\bin\Release\net9.0\win-x64\publish\MiniPet.exe
```

- 首次启动若未配置 WZ 路径，会弹 WZ 加载失败窗口，可在设置中心配置 `BaseWzPath`（指向含 Base.wz 的 WZ 根目录）后重载。
- 配置文件：`%APPDATA%\MiniPet\settings.json`、`wz-config.json`
- 缓存目录：`%LOCALAPPDATA%\MiniPet\cache\v1`（精灵图条 / 气泡瓦片 / 地图 PNG）

## Windows 注意事项

| 主题 | 说明 |
|------|------|
| 渲染后端 | Avalonia `UsePlatformDetect()` 在 Windows 自动走 DirectX/ANGLE（Skia 渲染）。透明顶层窗口依赖 DWM，需 Win10+；旧 Windows 透明/穿透可能退化（见验证清单）。 |
| 点击穿透 | 背景/气泡窗口在 Windows 用 `WS_EX_TRANSPARENT` 扩展样式实现点击穿透（`Utils/WindowsNative.cs`），等价 macOS `NSWindow.ignoresMouseEvents`。桌宠主窗口本身可交互（可拖拽），不穿透。 |
| 字体 | 气泡文字 Windows 优先宋体系（SimSun/MingLiU，`BalloonService.CreateBalloonTypeface` 已按平台分流），无需额外配置。 |
| 托盘 | `TrayService` 用 Avalonia `TrayIcon`（Windows 通知区域原生支持），无需 Win32 托盘实现。macOS 的 NSStatusItem 未使用。 |
| 进程重启 | `Program` 的渲染定时器错误重启逻辑仅在 macOS Avalonia.Native 出现（`-6661` 显示器休眠），Windows 不会触发，无影响。 |
| 启动器 | Windows 无 `caffeinate`（macOS 专属防休眠命令），无需处理——代码中未调用。 |
| Agent 数据源 | Hermes/Claude/Codex 适配器按 `UserProfile` 解析 `~/.hermes`、`~/.claude/projects`、`~/.codex/sessions`，Windows 上即 `C:\Users\<用户>\...`，与各 CLI 默认位置一致。 |

## 常见问题

| 现象 | 原因 | 处理 |
|------|------|------|
| 启动即退出 / 无窗口 | 缺 DirectX 或显卡驱动过旧 | 更新显卡驱动；确认系统 Win10+ |
| 托盘图标不显示 | 通知区域被折叠 | 系统托盘展开箭头里查找 MiniPet 图标 |
| 点击穿透不生效（背景挡点击） | 窗口样式未更新（少见） | 重启应用；确认系统为 Win10+ |
| WZ 加载失败 | BaseWzPath 配置错误或 WZ 版本不兼容 | 设置中心重新选择 WZ 根目录 |
