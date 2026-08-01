# ADR-0001: Three-window layered architecture (Pet / Background / Desktop)

- **Status**: Accepted
- **Date**: 2026-07-31

## Context
The spec (`claude-prompt-20260731-01.md` §3.4) wrapped a `BackgroundControl` inside `PetWindow` under a `Panel`, beneath the sprite grid. `PetWindow` uses `SizeToContent = WidthAndHeight` and the sprite sizes the window (~100×100 floating pet). An 800×600 map image inside that same window would resize the floating pet into a large opaque rectangle, destroying the F1 floating-pet UX. F6 ("固定窗口，宠物站在地图上") and F1 ("透明悬浮") describe two different window behaviours that cannot share one `SizeToContent` window.

## Decision
Adopt a **three-layer, multi-window** architecture:
- **Pet window** (existing `PetWindow`): unchanged — transparent, topmost, `SizeToContent`, frame-loop driven.
- **Background window** (new `BackgroundWindow`): a separate transparent topmost window that renders the map PNG into a fixed viewport, layered *behind* the Pet.
- **Desktop**: the OS desktop at the bottom.

The background layer is toggleable via the Pet's right-click menu ("隐藏背景"). Selecting a map loads it via `MapService` and shows the `BackgroundWindow`; "隐藏背景" hides it.

## Alternatives considered
- **(A) Mutually-exclusive modes** — enabling a map switches the Pet window to a fixed-size map scene. Rejected: changes the Pet window's nature depending on state; two behaviours in one window.
- **(C) Clip map to Pet size** — render map as a tiny backdrop inside the small Pet window. Rejected: visually meaningless.
- **(D) Settings-preview only** — never show map on the main window. Rejected: fails the acceptance criterion "主窗口显示地图背景".

## Consequences
- **+** Floating-pet UX (F1) is fully preserved.
- **+** Background is independently positionable and toggleable.
- **−** Z-order of three topmost windows is fragile on macOS; mitigation: the Pet window must raise/activate itself above the BackgroundWindow on show and after any background change.
- **−** Additional window lifecycle to manage (create, show/hide, teardown on exit).
- Supersedes spec §3.4's Panel-wrap approach; `PetWindow` layout is left unchanged.
