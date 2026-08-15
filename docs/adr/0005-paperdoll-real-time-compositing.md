# ADR-0005: Paperdoll via real-time compositing with a frame-provider AnimService

- **Status**: Accepted
- **Date**: 2026-08-09
- **Supersedes**: [ADR-0002](0002-paperdoll-via-synthetic-strip-cache.md)

## Context

ADR-0002 pre-baked paperdoll actions into synthetic sprite strips under a sentinel mobId to drive the then-禁改 `AnimService`. That worked but imposed hard limits that block the module redesign requested in 2026-08-09:

- Switching equipment or ear type requires **rebuilding every action strip** (slow, background-only).
- Only the pre-built actions are available (5); no expression/blink cycle.
- Strips are large PNGs held in memory.
- Selection UX is hand-typed item IDs; ear switching renders wrong.

The redesign requirements: real-time ear switching, a unified material browser (keeping Mob/NPC), blink expressions with an external-driver hook, skin/dye, z-map layer locks, full-action support, and outfit persistence with an offline cache.

## Decision

1. Rewrite the paperdoll core as **real-time per-frame compositing**: for a given action/frame, collect the frame's pieces (body/head/hair/face/equip), resolve anchors, order by z-map layers, apply layer locks, and composite one frame bitmap. No pre-baked strips for paperdolls.
2. Add an **entity frame-provider interface** to `AnimService`: when the current mobId is the paperdoll sentinel, `Tick`'s frame comes from the compositor; Mob/NPC keep the existing strip path untouched. `AnimService` keeps its whole state machine (idle-random actions, drag walk1/fly switching, OneShot→stand).
3. Display paperdoll frames through a **writeable-bitmap direct copy** (`SKBitmap` → BGRA) instead of PNG encode/decode every frame.
4. Ear model aligns MapleSalon2: the ear type *is* the WZ canvas name (`ear`/`humanEar`/`lefEar`/`highlefEar`); head canvases whose name ends in `ear` are grouped by raw name; an empty group renders nothing (the human head carries its own round ears). See [CONTEXT.md 耳朵类型](../CONTEXT.md).
5. Layer occlusion uses the z-map **lock** mechanism (cap hides hair, `backHead` forces `Hd`, plus the dedicated ear-fix branch for `accessoryOverHair`/`hairShade`), mirroring MapleSalon2 `buildLock`/`refreshLock`.

## Alternatives considered

- **(b) Keep synthetic strips, fix internals only** — rejected: rebuild latency, 5-action cap, no expressions, still can't react to ear switches instantly.
- **(c) Separate render loop in PetWindow bypassing AnimService** — rejected: duplicates the action state machine (idle-random, drag switching, OneShot completion) outside its home.
- **(d) PNG-byte display path** — rejected: per-frame PNG encode + decode waste at 30fps.

## Consequences

- **+** Real-time appearance/ear switching, arbitrary actions, blink expressions — all from one compositor.
- **+** Mob/NPC path and AnimService state machine are unchanged for those entities.
- **−** `AnimService` is no longer 禁改; the frame-provider change must stay additive (Mob/NPC keep using strips).
- **−** Per-frame compositing cost must stay within the 30fps budget; WZ reads and decoded piece bitmaps are cached.
