# S.Media Implementation Readiness Checklist

Source references:
- `Media/S.Media.Core/PLAN.smedia-architecture.md`
- `Media/S.Media.Core/error-codes.md`
- `Media/S.Media.Core/API-outline.md`
- `Media/S.Media.FFmpeg/API-outline.md`
- `Media/S.Media.PortAudio/API-outline.md`
- `Media/S.Media.OpenGL/API-outline.md`
- `Media/S.Media.OpenGL.Avalonia/API-outline.md`
- `Media/S.Media.OpenGL.SDL3/API-outline.md`
- `Media/S.Media.NDI/API-outline.md`
- `Media/S.Media.MIDI/API-outline.md`

## Purpose

Use this checklist before starting implementation work for a module. A module is `Go` only when all required gates pass or an explicit blocker/waiver is recorded.

Execution sequencing is defined in `Media/S.Media.Core/implementation-execution-schedule.md`.

## Global Go/No-Go Gates (All Modules)

- [ ] Namespace policy passes (`S.Media.<Project>.*` roots only).
- [ ] Dependency direction passes (module depends only on allowed layers from `PLAN.smedia-architecture.md`).
- [ ] Error-code ownership is mapped to `Media/S.Media.Core/error-codes.md`.
- [ ] Return-code contract is explicit (`0` success, non-zero failure).
- [ ] Idempotency contract is explicit for lifecycle methods (`Stop`/`Close`/`Terminate` where applicable).
- [ ] Failure atomicity is explicit (failed operations do not partially mutate state).
- [ ] Teardown fence is explicit (no user-visible callbacks/events after successful teardown).
- [ ] Shared semantic mapping needs are documented (for example mapping to `950` where applicable).

## Module Checklists

### `Media/S.Media.Core`

- [ ] Core contracts are implementation-ready (audio/video/mixer/player/clock interfaces).
- [ ] `MixerDetachStepFailed` (`3000`) fallback precedence is explicit.
- [ ] `DebugKeys.MixerDetachSecondaryFailure` payload fields are fixed and documented.
- [ ] `MediaSourceReadTimeout` (`4209`) remains reserved semantics only (future timeout paths).
- [ ] Shared `ResolveSharedSemantic(int code)` behavior is defined for active mappings.

### `Media/S.Media.FFmpeg`

- [ ] Adaptation policy is clear (implementation adaptation only; no legacy class moves).
- [ ] Invalid-config matrix and deterministic `2010` behavior are covered.
- [ ] Single-reader concurrency behavior and `2014 -> 950` mapping are covered.
- [ ] Stream ownership semantics (`LeaveInputStreamOpen`) are explicit.
- [ ] Decode thread/queue clamp behavior is explicit and testable.

### `Media/S.Media.PortAudio`

- [ ] Engine factory shape aligns with Core (`IAudioOutput`-typed creation/collections).
- [ ] Input path is explicit non-timeout pull semantics in this phase.
- [ ] Dense map push validation/failure behavior is deterministic.
- [ ] Device switch/event ordering and teardown fences are explicit.
- [ ] Overflow/underflow behavior and test gates are present.

### `Media/S.Media.OpenGL`

- [ ] Adaptation policy is clear (implementation adaptation only; no legacy class moves).
- [ ] Clone-graph invariants are explicit (no cycles, no self-attach, deterministic detach behavior).
- [ ] Error precedence is explicit (specific OpenGL codes first, generic fallback only when needed).
- [ ] Failure atomicity for attach/detach/remove paths is explicit.
- [ ] OpenGL contract test matrix section is present.

### `Media/S.Media.OpenGL.Avalonia`

- [ ] Adapter remains UI-only (no decode/session logic).
- [ ] Adapter error policy references canonical OpenGL clone codes.
- [ ] Parent lifecycle projection rules are explicit.
- [ ] Failure atomicity and teardown fence are explicit.
- [ ] Avalonia contract test matrix section is present.

### `Media/S.Media.OpenGL.SDL3`

- [ ] Adapter remains rendering/embedding-only.
- [ ] Handle/descriptor lifetime contract is explicit and deterministic.
- [ ] Parent-loss behavior is deterministic and error-coded.
- [ ] Failure atomicity and teardown fence are explicit.
- [ ] SDL3 contract test matrix section is present.

### `Media/S.Media.NDI`

- [ ] Live duration semantics (`double.NaN`) are explicit and tested.
- [ ] External clock unavailability behavior (`4211`, no implicit fallback) is explicit.
- [ ] Queue/fallback/tick precedence and clamp behavior are explicit.
- [ ] Same-instance read rejection mapping to `950` is explicit.
- [ ] NDI contract test matrix covers lifecycle, diagnostics thread, and overflow behavior.

### `Media/S.Media.MIDI`

- [ ] Reconnect policy has one control source (`ReconnectMode`) and deterministic stop conditions.
- [ ] Status transition guarantees and per-instance ordering are explicit.
- [ ] Callback delivery model is fixed (synchronous/no-queue/no-drop in this phase).
- [ ] Error split (`MIDIInvalidMessage` vs `MIDIOutputSendFailed`) is explicit.
- [ ] MIDI contract test matrix covers reconnect/status/event-lifetime guarantees.

## Readiness Decision Record

- Module: `...`
- Owner: `...`
- Date (UTC): `...`
- Decision: `Go` | `No-Go`
- Blocking gaps (if `No-Go`):
  - `...`
- Approved waivers (optional, include expiry):
  - `...`

