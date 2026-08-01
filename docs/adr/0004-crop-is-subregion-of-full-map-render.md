# ADR-0004: Crop is a sub-region of one full-map render (map-center camera), not a camera-shifted viewport

- **Status**: Accepted
- **Date**: 2026-08-01

## Context
The desktop background window shows a 裁剪视口 (Crop Rect) of a MapleStory map. `BackgroundWindow.SetMap` rendered this crop by calling `MapService.CompositeMapFrame(map, cropX−MinX, cropY−MinY, cropW, cropH)` — i.e. re-rendering the map with the **camera centered on the crop** (originWorldX = cropX).

The authoritative renderer (`maplestory-wiki-backend` `MapRenderHandler.drawBacks`) positions each parallax background tile relative to the **map center**: `px += cx·(100+rx)/100`, where `cx = MinX + worldW/2` is always the whole-map center. It renders the entire map in one pass; there is no per-crop camera.

Re-rendering a crop with the camera shifted to crop-center moves the back 层 by `(cropCenter − mapCenter)·parallax`, so the desktop crop no longer matches the corresponding region of the settings-center preview (which renders full-map with map-center camera). Symptom reported by the user: "**区域多 + 背景拼接错位**" — parallax backgrounds land in the wrong place and overflow the crop. (`SettingsWindow` preview via `CompositeMapThumbnail` looked correct because it already uses the map-center camera; only the desktop crop diverged.)

## Decision
A Crop Rect is **always a pixel sub-region of one full-map render**:
1. Render the whole map once at full world resolution (`worldW × worldH`) with the camera at **map center** (the same render the settings preview downscales from). Port `MapRenderHandler.drawBacks` / `drTiled` / `getBackTileMode` verbatim, including the `type→tileMode` remap and `ScrollHorizontal/Vertical` (no-parallax) bits, and skip `screenMode ≠ 0`.
2. Cache that full render per map.
3. The desktop background window extracts the crop rectangle `[cropX−MinX, cropY−MinY, cropW, cropH]` from the cached full render (a pixel copy), and sizes the window to `cropW × cropH`. The settings preview downscales the same full render.
4. `CompositeMapFrame`'s `viewX/viewY` camera-shift path is **not used for crops** (kept only for the legacy full-map call with offset 0).

## Alternatives considered
- **(A) Keep camera-shifted crop, just fix the back-formula constants.** Rejected: the parallax reference point is fundamentally map-center; any crop camera ≠ map-center misaligns the back 层. There is no constant tweak that makes a shifted camera match a map-center render.
- **(B) Render each crop at crop-size with map-center camera offset.** Rejected: still a separate render whose camera differs from the preview's; duplicates work and can still diverge.

## Consequences
- **+** Desktop crop and settings preview become pixel-identical (same source render) — "不一致" eliminated.
- **+** Parallax back 层 correct on both desktop and preview (matches authoritative backend).
- **−** Must render the full map (potentially large, e.g. ~3000×900) once per map before any crop shows. Mitigation: cache the full render per map; crop extraction and preview downscale are cheap copies off the cache.
- **−** Thread-safety: the cached full `SKBitmap` is produced on a background task and consumed by UI threads; must swap atomically (build a new bitmap, publish, extract immutable copies) — same generational-guard pattern already used by `_renderGen`.
- Supersedes the camera-shift crop logic in `BackgroundWindow.SetMap`; `CompositeMapFrame`'s `viewX/viewY` params are demoted to legacy/full-map-only.
