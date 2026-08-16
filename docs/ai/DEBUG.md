# 调试指南 — MiniPet（C# / Avalonia）

## 构建与启动

```bash
# dotnet 是可执行文件（# 是 shell 特殊字符），必须引号调用
"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug

# 启动（GUI 需显示器唤醒，否则渲染定时器 -6661）
caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
```

## 进程与崩溃

```bash
pgrep -f MiniPet/bin                    # 查进程
ls -lt ~/Library/Logs/DiagnosticReports/ | grep -i minipet   # 崩溃日志
# 崩溃日志解析：.ips 文件第 2 行起是 JSON，faultingThread 即崩溃线程
```

## 窗口状态检查（无需屏幕录制权限）

```swift
// CGWindowListCopyWindowInfo 列 MiniPet 窗口（owner 含 "MiniPet"），
// 看 bounds（X/Y/W/H）判断窗口是否在屏内、是否被拖出屏幕
```

## 拖拽抖动（2026-08-01 最终结论，已实证）

三个窗口三种方案：

| 窗口 | 方案 | 说明 |
|------|------|------|
| PetWindow（桌宠） | `BeginMoveDrag(e)` 原生拖拽 | 一直正常，勿改 |
| BackgroundWindow（静态图，超大窗口 5830×1173） | `NSEvent.mouseLocation`（objc_msgSend_NSPoint） | AppKit 原生全局坐标，无需辅助功能权限；左下原点需翻转 y = screenH − y |
| MapViewportWindow（摄像机，800×600） | `Position + GetPosition(null)×RenderScaling` | 绝对屏幕坐标，见下 |

**核心坑：`e.GetPosition(null)` 返回的是窗口客户区坐标（toplevel 坐标系），不是屏幕坐标！**
直接用它的差值做 delta 会形成反馈循环（窗口动 → 相对坐标变 → 再动 → 抖）。

正确算法（摄像机右键拖窗口）：
```csharp
// 按下
var rel = e.GetPosition(null);
_winDragStartScreen = new Point(Position.X + rel.X * RenderScaling, Position.Y + rel.Y * RenderScaling);
_winDragStartWindow = Position;
// 移动
var rel2 = e.GetPosition(null);
var absScreen = new Point(Position.X + rel2.X * RenderScaling, Position.Y + rel2.Y * RenderScaling);
int winDx = (int)(absScreen.X - _winDragStartScreen.X);
int winDy = (int)(absScreen.Y - _winDragStartScreen.Y);
Position = new PixelPoint(_winDragStartWindow.X + winDx, _winDragStartWindow.Y + winDy);
```

**已废弃方案（勿再用）**：
- `CGEventCreate/CGEventGetLocation`（CoreGraphics）：需要辅助功能权限，MiniPet 无权限时**恒返回 (0,0)**，表现为拖不动（曾用 CamTest 日志实证）
- `e.GetPosition(this)` 差值：反馈循环，更抖

## 气泡 Height 负数（已修）

`BalloonControl.ShowBalloon` 里 `BalloonText.Height = Height - 40 - 30` 在小气泡下算负，
Avalonia Height 不允许负值会抛异常。已加 `Math.Max(0, ...)` 保护。
`BalloonService.RenderBalloon` 的 `new SKBitmap(totalW, totalH)` 若尺寸非法返回无效句柄，
后续 `SKImage.FromBitmap` 会 **native SIGSEGV（try-catch 抓不到）**——必须在创建前加 `totalW<=0 || totalH<=0` 前置保护。

## SKBitmap 跨线程崩溃（已修）

`MapService._fullRender` 缓存被后台 Task 使用期间若被 `Dispose()` → 悬空指针崩溃。
规则：**只置 null 让 GC 回收，不主动 Dispose**（`InvalidateFullRenderCache` 和 `RenderMapFull` 均如此）。

## 模拟鼠标做黑盒测试（限制）

- `CGEvent` 注入鼠标事件需要辅助功能权限；Hermes 授权后**其子进程（swift/python）仍可能 AXIsProcessTrusted=false**，注入无效
- osascript(System Events) 授权后可用
- 最终交互验证需人工操作

## 日志

- 桌宠标准输出在 Hermes 后台进程里，用 process log 查看
- `[WzDiag]`/`[Paperdoll DBG]` 是 WZ 解析诊断（量大，排查时看，平时可忽略）
- `[BalloonControl] ShowBalloon: -19` 已修复（2026-08-01）
