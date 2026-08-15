# ADR-0006: Hermes client surface via api_server SSE (initiator, not observer)

- **Status**: Accepted
- **Date**: 2026-08-13
- **Supersedes**: the earlier revision of this ADR (state.db single-source heuristic) — MiniPet changes role from passive observer to active Hermes client surface.
- **Related**: [PLAN-agent-integration](../../PLAN-agent-integration.md) (state.db polling, kept as fallback)

## Context

MiniPet should reflect Hermes agent activity as pet animations. Three feed paths were evaluated and rejected, then the design changed the role:

- **WS `/api/ws`** (verified): JSON-RPC routes events by session id (`write_json`); a passive observer with no session ownership gets no agent events (only global broadcasts). No passive-subscribe RPC; `session.resume` has side effects. The official desktop's agent events come from its **own bound session message stream** (`use-message-stream/gateway-event.ts`, gated by `isActiveEvent`) — not from a bare WS.
- **REST `/api/sessions` polling** — reads the same state.db, zero incremental info, plus auth burden.
- **state.db single-source heuristic** (previous ADR revision) — works, honest 5-state, but MiniPet stays a *passive observer*; it cannot react to the user's own live turns with full fidelity, and the core product promise (pet reflects agent work) is best served when MiniPet *is* the surface.

**Final decision (user, 2026-08-13)**: MiniPet becomes a **Hermes client surface** — like the official desktop app, it owns its own session, so agent events route to it naturally. The **api_server platform (port 8642)** is Hermes' official channel for external clients (Open WebUI et al. connect this way). Chain: to have session ownership → you must have a session → you must be an initiator → api_server is the only official initiator path. This is a closed loop, not a compromise.

Verified facts (source + live):
- `POST /api/sessions` creates a session (optional `id`, else auto `api_{time}_{uuid}`).
- `POST /api/sessions/{id}/chat/stream` → SSE (`text/event-stream`) with events `run.started / message.started / tool.progress(_thinking=reasoning) / tool.started / tool.completed / tool.failed / assistant.completed / run.completed / error / done`. User message field: `message` / `input`.
- Auth: `Authorization: Bearer <API_SERVER_KEY>`; the server refuses to start without it.
- Session is reusable (multiple POSTs keep history).
- Local host: api_server not yet enabled (port 8642 not listening) — enable + restart required.

## Decision

1. **MiniPet connects as an api_server client**: create/reuse one session per profile, POST `chat/stream` on user input, parse the SSE stream, map events → AgentState, drive pet animations.
2. **Event → AgentState mapping** (mirrors `derivePetState`):
   - `tool.started` / `message.started` → Run
   - `tool.progress` (`tool_name="_thinking"`) → Review
   - `tool.failed` / `error` → Failed
   - `assistant.completed` / `run.completed` (no error) → Wave
   - `done` → Idle
   - **Jump**: synthesized locally on successful `run.completed` (SSE has no celebrate event).
   - **Waiting**: no clarify/approval events in SSE → mapped via the configurable per-state animation table.
3. **AgentState → animation mapping becomes user-configurable** in Settings (7 states, each: action name + balloon text). Defaults keep the current EffectLayerService switch.
4. **MiniPet mini-dialog**: click/double-click the pet opens a mini chat dialog (input + reply display); send → POST chat/stream → SSE drives animation + reply text scrolls in. Session reused for multi-turn context.
5. **state.db heuristic kept as fallback** (api_server unreachable → existing SQLite path with its white-box tests).
6. **Ops prerequisite** (accepted): enable `platforms.api_server` in config, set `API_SERVER_KEY` (plaintext local credential), restart Hermes.

## Alternatives considered

- **(a) WS `/api/ws` bare connection** — rejected (Codex R1 F3): no passive subscription; events route by session; MiniPet would get only global broadcasts.
- **(b) WS via `session.resume(sid)`** — rejected: reattaches detached transports / cancels orphan-reaping (interferes with Hermes' own surfaces), and MiniPet still wouldn't own the turn.
- **(c) REST polling / state.db-only observer** — rejected as the primary path: no session ownership, can't serve the "pet IS the surface" product; state.db kept only as offline fallback.
- **(d) Remote direct-connect** — rejected: non-loopback OAuth gate blocks third parties; SSH tunnel is the user's concern.

## Consequences

- **+** MiniPet owns its session → agent events route to it naturally, full-fidelity animation, exactly like the official desktop app.
- **+** SSE is request-response per turn (no persistent connection when idle) — cheaper than a WS long connection.
- **+** Per-state animation config gives the user control (Jump/Waiting, which SSE lacks signals for, map to user-chosen actions).
- **+** state.db heuristic + its tests remain as a fallback; no work thrown away.
- **−** Requires enabling the api_server platform + API_SERVER_KEY (plaintext local credential) + a Hermes restart.
- **−** SSE has no Jump/Waiting events — those states rely on local synthesis / config mapping.
- **−** MiniPet becomes an initiator (a real Hermes client), not a passive observer — a role change the user explicitly chose.
