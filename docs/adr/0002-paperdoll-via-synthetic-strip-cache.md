# ADR-0002: Paperdoll as animated Pet replacement via synthetic strip cache

- **Status**: Accepted
- **Date**: 2026-07-31

## Context
The spec (§2.4) "applies" a Paperdoll by calling `_spriteCanvas.SetFrame(paperdollPng, 0, 0)` — a single static `stand1` frame 0 composite. This overwrites the live animated Mob frame and **freezes** the Pet (the frame loop keeps ticking but its frame is overwritten). `AnimService.cs` is on the project's 禁改 (do-not-modify) list, so the Paperdoll cannot be wired into the animation clock by editing `AnimService`. The user wants the Paperdoll to be **animated** and **mutually substitutable** with a Mob as the Pet's current entity.

Separately, the spec's §2.2 `CompositeFrame(action, frameIndex)` computes a **per-frame bounding box**. Animating per-frame boxes would make the character jitter frame-to-frame, because each frame's canvas size and anchor differ as limbs move.

## Decision
Treat the Paperdoll exactly like a Mob from the frame loop's perspective:
1. Build a proper **SpriteStrip per Paperdoll action** (`stand1`, `walk1`, …): first compute the **union canvas + common origin across all frames** of the action (mirroring `WzService.BuildSpriteStripPng`'s max-L/T/R/B logic), then composite each frame into that shared canvas and concatenate horizontally into one PNG.
2. Cache each strip under a **reserved sentinel mobId** (e.g. `int.MaxValue`) via the allowed `SpriteService` / `CacheManager` path, keyed by action.
3. Drive animation through the **unmodified** `AnimService`: `SetCurrentMobId(sentinel)` + `Play("stand1", Loop)`. `AnimService` already pulls frames from `SpriteService` by (mobId, action), so it animates the Paperdoll with zero changes.

Switching back to a Mob is the inverse: `SetCurrentMobId(realMobId)` + `SwitchTo("stand")`.

## Alternatives considered
- **(b) Static frame on apply** (spec §2.4 as written) — freezes the Pet. Rejected: worst UX.
- **(c) Third window for the Paperdoll** — parallel to ADR-0001. Rejected: adds a fourth window and z-order complexity; the user explicitly chose "互相替换" (mutual replacement), not co-existence.
- **(d) Settings-preview only** — drops "apply to main window". Rejected by acceptance criterion.

## Consequences
- **+** Animated Paperdoll with **zero** changes to the 禁改 `AnimService`.
- **+** Mob ↔ Paperdoll are symmetric and substitutable, as the user required.
- **−** Sentinel-mobId indirection is non-obvious to a future reader (hence this ADR); `GetCachedActionList(sentinel)` must resolve the Paperdoll's actions from the cache dir.
- **−** Paperdoll strip build is heavier (union-canvas pass) than the spec's per-frame `CompositeFrame`; §2.2 is deviated from on purpose to avoid jitter.
- The Paperdoll action set exposed to the user is limited to those pre-built into strips (at minimum `stand1`, `walk1`).
