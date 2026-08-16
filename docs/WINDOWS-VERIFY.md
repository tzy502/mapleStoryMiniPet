# MiniPet Windows 验证清单（Windows 真机）

本清单用于 **Windows 真机**逐项验证桌宠功能。本机为 macOS，无法真跑 Windows——此清单由静态兼容性审查产出：每项含「操作 / 预期结果 / 归属代码」，按功能域分组。全部通过即视为 Windows 适配达成。

构建与运行步骤见 [BUILD-WINDOWS.md](BUILD-WINDOWS.md)。

---

## 0. 构建与启动

- [ ] `dotnet build MiniPet\MiniPet.csproj -c Debug` 0 错误
  - 预期：编译通过，无平台相关错误。
- [ ] `dotnet publish -r win-x64 --self-contained` 成功，`publish\MiniPet.exe` 可双击启动
  - 预期：self-contained 产物免装 .NET 直接运行；窗口透明、置顶、无边框。
- [ ] 启动后任务管理器出现 MiniPet 进程且不崩溃
  - 预期：进程常驻；右键菜单可打开；窗口可拖动。

## 1. WZ 加载

- [ ] 设置中心配置 BaseWzPath（含 Base.wz 的 WZ 根目录）→ 重载
  - 预期：状态栏提示加载成功（Mob/Map 计数）；无 WzLoadFailWindow 弹窗。
- [ ] 路径含中文/空格时仍可加载
  - 预期：`Path.Combine`/`Path.GetFullPath` 全量使用（WzService 自动探测 Base/Mob/Packs 子目录），中文路径正常。
- [ ] 缓存动作清单（无 WZ 时）
  - 预期：删掉/清空 WZ 路径后重启，右键菜单仍列出已缓存动作（`GetCachedActionList` 已改走 `%LOCALAPPDATA%\MiniPet\cache\v1\sprites\{mob}`）。

## 2. 纸娃娃 / 角色 / 动画

- [ ] 切换 mob / NPC / 纸娃娃，动画正常播放
  - 预期：精灵条带拼装正确、origin 对齐、无撕裂；30fps 帧循环顺滑。
- [ ] 拖动桌宠到屏幕任意位置（含贴顶/贴边）
  - 预期：左键手动拖拽（`Position + GetPosition*RenderScaling` 绝对坐标法）在 Windows 上跟手；顶贴屏顶 clamp 生效。
- [ ] 动画切换时窗口不跳位、气泡锚点稳定
  - 预期：resize 前后恢复 Position 的补偿逻辑在 Windows 同样生效（Windows resize 保持左上角，补偿为 no-op，安全）。

## 3. 地图（模式1 静态 / 模式2 摄像机）

- [ ] 模式1 静态地图窗口显示，背景不拦截点击（穿透）
  - 预期：背景窗口 `WS_EX_TRANSPARENT`（`WindowsNative.SetClickThrough`）→ 点击穿透到下层；桌面图标可点。
- [ ] 模式1 交互模式（右键菜单「交互」）：左键拖动窗口 + 右键菜单 + Esc 退出
  - 预期：`GetGlobalMousePosition` Windows 分支（`GetCursorPos` ÷ `RenderScaling` 转 DIP）拖动跟手；退出交互恢复穿透。
- [ ] 模式2 摄像机窗口：左键拖相机、右键拖窗口、滚轮缩放（0.25~4 以光标为中心）
  - 预期：全部走 Avalonia 指针事件，平台无关，Windows 行为与 macOS 一致。
- [ ] 地图缓存（无 WZ 离线）
  - 预期：渲染过的地图 PNG 落盘 `%LOCALAPPDATA%\MiniPet\cache\v1\maps`，清 WZ 路径后仍可离线显示（含裁紧原点 sidecar 对齐落地）。

## 4. 聊天气泡

- [ ] 气泡显示（手动/随机/Agent 状态触发），文字中文正常
  - 预期：Windows 字体分流生效——宋体系（SimSun/MingLiU）渲染，中文无豆腐块。
- [ ] 气泡窗口不拦截点击
  - 预期：`BalloonWindow` Windows 分支 `WS_EX_TRANSPARENT` → 点击穿透。

## 5. Agent 接入（Hermes / Claude Code / Codex / DSH）

- [ ] 设置页 Agent 开关 + Hermes api_server/state.db 连通状态
  - 预期：`%USERPROFILE%\.hermes\state.db`（或 HERMES_HOME）可读；HTTP 主链路 `127.0.0.1:8642` 连接正常。
- [ ] Claude Code 状态轮询
  - 预期：监听 `C:\Users\<用户>\.claude\projects\**\*.jsonl`（FileSystemWatcher + poll），Agent 干活时桌宠进入 Run/Review 态。
- [ ] Codex 状态轮询
  - 预期：监听 `C:\Users\<用户>\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`，task_started/task_complete 反映到桌宠动画。
- [ ] DSH 监控/对话
  - 预期：`ws://127.0.0.1:3080/api/events.mux` 连接；DSH 回合驱动桌宠状态 + DshChatWindow 对话。
- [ ] 关掉全部 Agent 开关 → 桌宠回 Idle，无异常日志刷屏
  - 预期：adapter 未装配时不轮询；熔断/退避不产生噪音。

## 6. 托盘 / 生命周期

- [ ] 托盘图标显示，左键隐藏/恢复主窗口，菜单「显示主窗口 / 设置中心 / 重置到桌面中央 / 退出」
  - 预期：Avalonia `TrayIcon` Windows 通知区域原生支持；菜单各项生效。
- [ ] 关闭主窗口 → 隐藏到托盘不退出；托盘「退出」→ 进程结束
  - 预期：`OnClosing` 拦截 + `AppExit` 事件链完整。
- [ ] 切换 WZ 后重载，缓存/配置不损坏
  - 预期：`settings.json` 原子写（File.Replace/File.Move 回退在 NTFS 上正常）；缓存键带 WZ 指纹不误命中。

## 7. 已知边界（Windows 真机待观察，非阻塞）

| 项 | 说明 |
|----|------|
| 多显示器混缩放拖动 | 背景窗口拖动按本窗口 `RenderScaling` 换算全局鼠标坐标；光标在异缩放显示器时可能偏差（macOS 侧同样存在该假设）。 |
| 旧版 Windows（<10） | 透明/穿透依赖 DWM，Win10 以下可能退化（代码按 Win10+ supportedOS 声明）。 |
| Win32 P/Invoke 位宽 | `WindowsNative` 用 `GetWindowLongPtrW`/`SetWindowLongPtrW`，仅保证 win-x64 目标（发布目标即 win-x64）。 |
| 点击穿透粒度 | 穿透 = 整窗 `WS_EX_TRANSPARENT`（等价 macOS ignoresMouseEvents），非逐像素 alpha 判定；背景/气泡穿透、主窗口交互，与 macOS 语义一致。 |

---

## 审查结论速览（macOS 静态审查产出）

- **已修复**：
  1. `PetWindow.GetCachedActionList` 硬编码 macOS 缓存路径 → 改走 `CacheManager.GetCacheDir()`（Windows 上原读不到任何动作）。
  2. `BackgroundWindow` 点击穿透 / 全局鼠标坐标 / `makeKeyAndOrderFront` 增加 `OperatingSystem.IsWindows()` 分流（新增 `Utils/WindowsNative.cs`：WS_EX_TRANSPARENT + GetCursorPos，user32 P/Invoke，无新 NuGet 依赖）。
  3. `BalloonWindow` 点击穿透增加 Windows 分支（WS_EX_TRANSPARENT）。
- **本就跨平台（无需改）**：CacheManager/ConfigService 平台分流（%LOCALAPPDATA%/%APPDATA%）；BalloonService Windows 字体分流；TrayService 用 Avalonia TrayIcon（无 NSStatusItem）；Hermes/CC/Codex 数据源路径经 `UserProfile + Path.Combine` 解析；WZ 节点路径 `/` 分隔属 WZ 内部语义（FindNodeByPath 统一 Replace('/','\\')）；`caffeinate` 仅存在于文档，代码未调用。
- **需 Windows 真机确认**：见上方第 0~6 节逐项。
