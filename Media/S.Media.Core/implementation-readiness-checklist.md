# S.Media Implementation Readiness Checklist

Source references:
- `Media/S.Media.Core/PLAN.smedia-architecture.md`
- `Media/S.Media.Core/error-codes.md`
- `Media/S.Media.Core/API-outline.md`
- `Media/S.Media.Core/decision-log.md`
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
- [ ] Canonical API wording is aligned (`Play(IMediaItem media)`; prose shorthand must not imply a second signature).
- [ ] Error-code ownership is mapped to `Media/S.Media.Core/error-codes.md`.
- [ ] Return-code contract is explicit (`0` success, non-zero failure).
- [ ] Idempotency contract is explicit for lifecycle methods (`Stop`/`Close`/`Terminate` where applicable).
- [ ] Failure atomicity is explicit (failed operations do not partially mutate state).
- [ ] Teardown fence is explicit (no user-visible callbacks/events after successful teardown).
- [ ] Shared semantic mapping needs are documented (for example mapping to `950` where applicable).
- [ ] Migration policy is acknowledged in module plan: temporary coexistence allowed, but final sign-off requires legacy runtime/path removal.

## Module Checklists

### `Media/S.Media.Core`

- [ ] Core contracts are implementation-ready (audio/video/mixer/player/clock interfaces).
- [ ] Mixer/player clock contract is single-field (`IMediaClock Clock`) with `ClockType` leadership mode (no separate external-clock interface).
- [ ] Clock-type defaults are explicit and enforced (`AudioMixer=AudioLed`, `VideoMixer=VideoLed`, `AudioVideoMixer=Hybrid`).
- [ ] Nonsensical mixer/clock-type combinations return `MixerClockTypeInvalid` (`3002`) with no state mutation.
- [ ] `IAudioSource.SourceId` and `IVideoSource.SourceId` contracts are explicit (implementation-generated, immutable per instance, remove-by-guid canonical key).
- [ ] `SourceId` uniqueness scope is explicit (process-lifetime uniqueness preferred; no collisions among live/registered sources required).
- [ ] Duplicate `SourceId` registration behavior is explicit (reject with `MixerSourceIdCollision` `3001`, no registration mutation).
- [ ] `RemoveSource(Guid)` and `RemoveSource(instance)` parity is explicit for audio/video mixers.
- [ ] `VideoFrame` pooled ownership contract is explicit (caller-owned, caller-disposed, valid-until-dispose, deterministic pool return semantics).
- [ ] `VideoFrame.Dispose()` contract is explicit (idempotent and cross-thread safe).
- [ ] `VideoFrame` dispose/read race behavior is explicit (deterministic failure path, no undefined behavior).
- [ ] Pixel-format metadata model is explicit (`IPixelFormatData` plus typed per-format structs and invariants).
- [ ] Pixel-format invariants are adapted from legacy `VideoFrame` semantics per supported format (plane presence, stride minima, plane-length minima).
- [ ] Video-output backpressure is explicit/configurable with deterministic owned error codes (`4000`, `4001`).
- [ ] Video-output default policy is explicit (`DropOldest`) and wait-timeout derivation from effective frame duration is documented (with explicit-timeout fallback when cadence is unresolved).
- [ ] `IVideoOutput` startup requires explicit config (`Start(VideoOutputConfig)` only; no parameterless start path).
- [ ] Backpressure outcome matrix is documented and consistent with deterministic code outcomes (`4000`, `4001`, `4002`).
- [ ] Disposed-frame push behavior is explicit and error-coded (`VideoFrameDisposed`, `4002`).
- [ ] Video-output config rejects `QueueCapacity < 1` with `MediaInvalidArgument` (`4210`) and does not clamp invalid values.
- [ ] Wait-mode config rejects `BackpressureWaitFrameMultiplier <= 0` with `MediaInvalidArgument` (`4210`).
- [ ] `PushFrame(...)` validation order is explicit and deterministic: disposed-frame check runs before queue/backpressure checks (`4002` takes precedence over `4000`/`4001`).
- [ ] Contract tests explicitly cover `MixerSourceIdCollision` (`3001`) no-mutation behavior.
- [ ] Contract tests explicitly cover backpressure/config code paths: `VideoOutputBackpressureQueueFull` (`4000`), `VideoOutputBackpressureTimeout` (`4001`), `VideoFrameDisposed` (`4002`), and invalid-config paths (`4210`) for queue/wait settings.
- [ ] Contract tests include precedence case: disposed-frame push with simultaneous full-queue/backpressure condition must return `VideoFrameDisposed` (`4002`) (not `4000`/`4001`).
- [ ] Contract tests include negative precedence case: non-disposed frame push under full-queue/backpressure conditions must return policy outcome (`4000` or `4001`) and must not return `4002`.
- [ ] `PushFrame(...)` hot-path policy is explicit (performance-first steady state, zero-allocation target, no per-frame heavyweight logging/validation).
- [ ] Reentrancy/threading contract tests for `IVideoOutput` interaction paths (`PushFrame`/`Stop`/`Dispose`) are tracked for implementation-start execution.
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

