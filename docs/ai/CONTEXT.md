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
A horizontal concatenation of all frames of one action into a single PNG, plus per-frame metadata (delay, alpha, origin). The unit the frame loop consumes for Mob/NPC entities; switching actions = swapping strips. A 纸娃娃 is rendered via 实时合成 instead of strips (see [ADR-0005](docs/adr/0005-paperdoll-real-time-compositing.md)).

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

### 部件 (Piece)
A single layerable frame component that composes into a 纸娃娃 frame — Body, Head, Hair, Face, Cap, Coat, … Each piece carries its map anchor links, origin, and z layer.

### 耳朵类型 (Ear Type)
The four mutually-exclusive ear appearances of a 纸娃娃, identified by their WZ canvas names: `humanEar` (human), `ear` (elf), `lefEar` (LEF), `highlefEar` (high elf). Ears are the head's own canvases selected at render time by the ear type; a head that lacks the chosen ear type renders no extra ear (the human head carries its own round ears).

### 表情 (Expression)
A named face animation of a 纸娃娃 (e.g. `default`, `blink`), independent of the body 动作. Expressions cycle via the random blink cycle and can be driven by an external agent state through the expression driver interface.

### 皮肤 (Skin)
The complexion variant of a 纸娃娃, tied to a body/head pair. Changing skin updates Body and Head together.

### 染发 (Dye)
An HSV recolor of a dyeable 部件 (typically hair/face), persisted per item.

### 遮挡锁 (Layer Lock)
The z-map occlusion rule that hides a lower piece when an upper piece occupies the same slot region (e.g. a cap hiding hair). Applied per z-map layer via slot ownership.

### 搭配 (Outfit)
A complete, persistable appearance — gender, skin, 耳朵类型, hair, face, each equipment slot, dye — that fully determines how a 纸娃娃 is rendered. The most recently used outfit is cached locally for offline display when WZ is unavailable.

### 实时合成 (Real-time Compositing)
Rendering a 纸娃娃 frame by composing its 部件 per frame on demand, driven by the frame loop — as opposed to pre-baked 精灵图条. The frame loop displays the composited frame bitmap + origin via a writeable-bitmap path.

### WZ
The packed MapleStory data files, parsed via the WzComparerR2.WzLib DLL. The source of all sprites, map data, and balloon tiles.

### Agent 状态 (AgentState)
The 7-valued activity state MiniPet reflects to show what a connected coding agent (Hermes / Claude Code / Codex) is doing **right now**: `Idle / Run / Review / Wave / Jump / Failed / Waiting` (semantics borrowed from the Hermes official pet model). One value at a time; the Pet's current 动作 (Action) is chosen from it. MiniPet is a **passive observer** of these states — it never drives the agent.
- **Resolution priority** (highest first, only one shows): Failed > Jump > Wave > Waiting > Run > Review > Idle.
- **Not to be confused with**: the Pet's 动作 (Action) — an AgentState is *mapped to* an action (e.g. Failed → `hit`, Waiting → `mouse`, Run → `move`), it is not itself an animation.
