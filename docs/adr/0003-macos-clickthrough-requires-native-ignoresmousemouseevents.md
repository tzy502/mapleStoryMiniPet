# ADR-0003: macOS 点击穿透须用原生 NSWindow.ignoresMouseEvents

- **Status**: Accepted
- **Date**: 2026-08-01

## Context
背景窗口（`BackgroundWindow`，ADR-0001 的中间层）需要默认**点击穿透**——不拦截鼠标事件，让用户能正常操作桌面上的其他应用。直觉做法是设置 Avalonia 的 `IsHitTestVisible=false`，这会让 Avalonia 自己的命中测试跳过该控件。但 Avalonia 的命中测试只决定**应用内**事件分发给哪个可视元素；macOS 上原生 `NSWindow` 仍然会接收鼠标事件并拦截其下的窗口。在 Codex 对抗审查（PLAN-REVIEW-LOG Round 1）中指出后，已验证该结论成立。

## Decision
背景窗口穿透**不能只靠 `IsHitTestVisible=false`**。`Interactive=false`（默认穿透态）时：
1. `IsHitTestVisible=false`（应用内不拦截）；
2. 经 `Window.TryGetPlatformHandle()` 取原生 `NSWindow` 指针，用 ObjC interop 设置 `ignoresMouseEvents = true`。

进入"背景交互模式"（`Interactive=true`）时把 `ignoresMouseEvents` 设回 `false`，并恢复 `IsHitTestVisible=true`，使窗口能接收拖拽/右键菜单。

## Alternatives considered
- **(A) 仅 `IsHitTestVisible=false`** —— 不穿透，原生窗口仍拦截点击。Rejected：无法满足"默认点击穿透"需求。
- **(B) 全局事件钩子** —— 用低层 hook 重分发事件。Rejected：需要辅助功能授权，复杂度高，且并非纯透明层所需。
- **(C) 依赖 Avalonia 内置穿透 API** —— Avalonia 12.1 无平台无关的 ignoresMouseEvents 等效 API，仍须走原生句柄。

## Consequences
- **+** 默认穿透语义正确，桌面操作不被背景窗口拦截。
- **−** 引入 ObjC interop 调用（macOS 平台专属代码路径）。
- **−** 交互模式切换必须成对恢复 `ignoresMouseEvents=false`，遗漏会导致窗口"卡死"无法接收事件。
- 穿透/交互的机制封装在 `BackgroundWindow` 内部（`SetInteractive`），对调用方透明。
