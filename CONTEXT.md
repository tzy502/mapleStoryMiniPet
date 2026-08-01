# CONTEXT

> Glossary of ubiquitous language for the MapleStory MiniPet domain.
> A glossary only — no implementation details. Terms resolved during grill-with-docs.

## Terms

### 桌宠 (Pet)
The animated creature shown in the main always-on-top transparent window, driven by the frame loop. The Pet is the thing the user sees "floating" on the desktop.
- **Usually**: a Mob sprite.
- **Not to be confused with**: 纸娃娃 (Paperdoll) — a Paperdoll can *replace* the Mob as the Pet's current entity, but "Pet" = the window/slot, "Mob"/"Paperdoll" = what fills it.

### Mob
A monster entity from `Mob/{id}.img`. One of the entity types that can fill the Pet slot. Animated via `stand`/`move`/`fly`/`skill`/… actions.

### NPC
A non-player-character entity from `Npc/{id}.img`. The other non-player entity type for the Pet slot.

### 纸娃娃 (Paperdoll / Player)
A composite character built by layering Character equipment parts (Body, Head, Hair, Face, Cap, Coat, …) in a fixed z-order. A Player, distinct from a Mob/NPC. Mutually substitutable with a Mob as the Pet's current entity.

### 动作 (Action)
A named animation within an entity (e.g. `stand1`, `walk1`, `move`, `fly`, `skill1`). Each action is a sequence of frames with per-frame delay.

### 精灵图条 (Sprite Strip)
A horizontal concatenation of all frames of one action into a single PNG, plus per-frame metadata (delay, alpha, origin). The unit the frame loop consumes; switching actions = swapping strips.

### 地图背景 (Map Background)
A MapleStory map rendered as a backdrop, shown in its **own window** layered behind the Pet. Independent of the Pet window; toggleable. Not part of the Pet window.
- **Not to be confused with**: the Pet's own background color / transparency.

### 裁剪视口 (Crop Rect)
The rectangular sub-region of a 地图背景 actually shown in the desktop background window. A crop is a viewport onto **one fixed rendering of the whole map**: the parallax background layers (back 层) stay anchored to the map as a whole, so the crop is cut from that single whole-map picture rather than re-framed with an independently shifted camera. See [ADR-0004](docs/adr/0004-crop-is-subregion-of-full-map-render.md).
- **Not to be confused with**: a camera re-centered on the crop — that detaches the back 层 from the map and misaligns the parallax (the "拼接错位" bug).

### 聊天气泡 (Balloon)
A chat-ring-style speech bubble overlay shown above the Pet, composited from 9-slice `UI/ChatBalloon.img/{id}` tiles. Non-interactive (does not block dragging).

### origin (锚点)
The WZ anchor point of a frame; the pivot used to align frames so animation does not jitter. Frames within one action share a common canvas anchored at origin.

### WZ
The packed MapleStory data files, parsed via the WzComparerR2.WzLib DLL. The source of all sprites, map data, and balloon tiles.
