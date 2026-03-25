# PLAN.smedia-architecture.md

## S.Media Refactor Architecture Baseline (Finalized)

This document is the finalized architecture baseline for refactoring the current `Seko.OwnAudioNET.*` stack into a cohesive `S.Media.*` architecture with strict dependency direction, explicit manager/mixer/player APIs, and predictable playback semantics. The design keeps simple usage first (`MediaPlayer.Play(IMediaItem media)`; `Play(Media)` is shorthand), preserves advanced scenarios through one controlled feature (multi-output), centralizes diagnostics/errors in Core, and introduces a dedicated FFmpeg decoding layer with audio-only, video-only, and synced A/V paths.

## Scope and Goals

- Move public namespaces to `S.Media` project-aligned roots (for example `S.Media.Core.*`, `S.Media.FFmpeg.*`).
- Preserve existing playback capabilities while simplifying top-level API shape.
- Keep direct `Add*`/`Remove*` methods on manager/mixer/player APIs (no indirect-only routing).
- Standardize seek and conflict behavior across audio/video/hybrid paths.
- Centralize debug keys and error code ranges in Core.
- Perform a hard cut from current `Seko.OwnAudioNET.*` implementations for final runtime sign-off (no compatibility shims/wrappers).
- Remove all `Seko.OwnAudioNET.*` projects and move required functionality into the new `S.Media.*` project set.
- Remove `OwnAudio` as a dependency from the target architecture.
- Treat this architecture as a clean-cut new start (no migration/compatibility layer planning).
- During implementation, temporary legacy/new project coexistence in the workspace is allowed for staged migration; final completion requires full legacy removal from solution/runtime paths.

## Namespace Policy

- Namespace roots must align with project names:
  - `S.Media.Core.*`
  - `S.Media.FFmpeg.*`
  - `S.Media.PortAudio.*`
  - `S.Media.OpenGL.*`
  - `S.Media.NDI.*`
  - `S.Media.OpenGL.Avalonia.*`
  - `S.Media.OpenGL.SDL3.*`
  - `S.Media.MIDI.*`
- Namespace roots that do not match project names are not allowed.
- Do not introduce compatibility namespaces such as `S.Media.Compat`.

## Target Project and File Map

- `Media/S.Media.Core` (exists; currently placeholder at `Media/S.Media.Core/Class1.cs`)
  - Core contracts, diagnostics keys, error codes/ranges, shared enums/options.
  - Mixers live here (`AudioMixer`, `VideoMixer`, `AudioVideoMixer`) per final decision.
- `Media/S.Media.FFmpeg` (new; planned rename from `Media/S.Media.Decoding.FFmpeg`)
  - Move/adapt from `VideoLibs/Seko.OwnAudioNET.Video/Decoders/*`.
- `Media/S.Media.PortAudio` (new)
  - Higher-level PortAudio engine over `Audio/PALib` with integrated runtime/device catalog responsibilities.
- `Media/S.Media.OpenGL` (new)
  - Core OpenGL engine/output integration.
- `Media/S.Media.NDI` (new)
  - Move/adapt from `VideoLibs/Seko.OwnAudioNET.Video.NDI/*`.
- `Media/S.Media.OpenGL.Avalonia` (new)
  - Move/adapt from `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/*`.
- `Media/S.Media.OpenGL.SDL3` (new)
  - Move/adapt from `VideoLibs/Seko.OwnAudioNET.Video.SDL3/*`.
- `Media/S.Media.MIDI` (new)
  - Easy-to-use MIDI API layer over `MIDI/PMLib` (PortMidi).
- App renames:
  - `Test/VideoTest` -> `Test/VideoStress`

## Per-Project Responsibilities

- `S.Media.Core`
  - Owns canonical diagnostics key registry and error code ranges.
  - Defines shared primitives: media identity, timeline/frame position types, seek result enums.
  - Contains conflict policy enums/default policy selection.
  - Contains mixer interfaces and implementations.
  - Contains `MediaPlayer` contracts and basic orchestration API surface.
  - Defines `IMediaItem`, dynamic metadata contracts, and typed stream info contracts.
- `S.Media.FFmpeg`
  - Owns FFmpeg decode lifetimes and source-first media APIs.
  - Exposes `FFAudioSource`, `FFVideoSource`, and `FFMediaItem`; session/interop layers remain implementation-internal.
  - Owns stream selection and `FFMediaItem` implementations (not `IMediaItem` selection policy in Core).
- `S.Media.PortAudio`
  - Owns real audio engine/output implementation for playback and source-style input using PALib.
  - Runtime/device discovery responsibilities are part of `PortAudioEngine`.
  - Engine tracks created outputs; device switching is owned by output instances.
  - Output instances emit device-change events; engine event surface remains lifecycle/discovery-focused.
  - Provides Microsoft logging-based wrappers (no static loggers).
- `S.Media.OpenGL`
  - Owns core OpenGL video engine/output runtime and HUD data feeding.
  - Owns clone graph policy (self-attach rejection, cycle detection, configurable max clone depth).
  - Parent output disposal destroys all child clones deterministically.
  - Performance-first defaults: attach while running allowed with 1-frame default pause budget (best-effort + warning on exceed), and pixel-format fast-path required by default.
- `S.Media.NDI`
  - Owns NDI input/output adapters and external clock provider.
  - Owns `NDIMediaItem` implementations for discovered-source and active-connection flows.
- `S.Media.MIDI`
  - Owns easy-to-use MIDI device/message APIs.
  - Uses `MIDI/PMLib` (PortMidi) as backend; `S.Media.MIDI` is the friendly wrapper layer.
  - Default-device APIs are discovery-only (`GetDefaultInput()`/`GetDefaultOutput()`); input/output creation stays explicit.
  - Supports configurable short-disconnect auto-reconnect policy for input/output handles.
  - Reconnect policy supports timeout/no-recover behavior with deterministic failure transitions.
  - Input event payloads include source-device identity and receive timestamps (backend timestamp when available).
  - Callback delivery is synchronous and serialized per instance in this phase (no callback-dispatch configuration surface).
  - Message callbacks are no-drop while open; no callback-overflow/drop policy is applied in this phase.
  - Input/output connection status transitions are exposed via status events and guaranteed for all internally observed transitions.
  - Failure atomicity is required for `Initialize()`/`Create*()`/`Open()` paths (no partially-open state on failure).
  - Planning note: refactor `MIDI/PMLib` for uppercase `MIDI` naming alignment and `Microsoft.Extensions.Logging` integration (no static loggers).
- `S.Media.OpenGL.Avalonia` and `S.Media.OpenGL.SDL3`
  - Own adapter/view/output controls only; no decoding or mixer policy logic.

## Dependency Direction

- `S.Media.Core` <- all projects depend on Core.
- `S.Media.FFmpeg` -> depends on `S.Media.Core`.
- `S.Media.PortAudio` -> depends on `S.Media.Core`.
- `S.Media.OpenGL` -> depends on `S.Media.Core`.
- `S.Media.NDI` -> depends on `S.Media.Core`, and optionally `S.Media.OpenGL` for output interop.
- `S.Media.MIDI` -> depends on `S.Media.Core` and `MIDI/PMLib` backend only.
- `S.Media.OpenGL.Avalonia` and `S.Media.OpenGL.SDL3` -> depend on `S.Media.Core` + project-specific UI libs + `S.Media.OpenGL`.
- `S.Media.OpenGL.Avalonia` canonical host path requires `OpenGlControlBase`.
- Avalonia host lifecycle should mirror legacy `VideoGL` control flow (`OnOpenGlInit` / `OnOpenGlRender` / `OnOpenGlDeinit`) while remaining adapter/UI-only.
- Avalonia/SDL3 HUD adapters should preserve legacy-style token ordering/abbreviation semantics in this phase (`VideoGL.HUD` / `VideoSDL.HUD`).
- `S.Media.OpenGL.Avalonia` uses base `Avalonia` NuGet package only.
- Test apps depend on high-level surfaces, not lower internals when avoidable.
- New modules must not depend on legacy `Seko.OwnAudioNET.*` or `OwnAudio` runtime components.

## API Contract Principles (Direct Add/Remove)

- Relevant manager/mixer/player APIs must expose direct methods:
  - `AddSource(...)` / `RemoveSource(...)` on mixers.
  - `AddAudioOutput(...)` / `RemoveAudioOutput(...)` and `AddVideoOutput(...)` / `RemoveVideoOutput(...)` on player/output managers.
  - `AddEngine(...)` / `RemoveEngine(...)` where engine fan-out is supported.
- Expose read-only output collections via:
  - `IReadOnlyList<IAudioOutput> AudioOutputs`
  - `IReadOnlyList<IVideoOutput> VideoOutputs`
- Indirect routing abstractions can remain, but cannot be the only registration path.
- Return values should use explicit operation codes (`0` success, non-zero error code) for deterministic orchestration.

## Core Interface Model

- `AudioMixer` implements `IAudioMixer` and `ISupportsAdvancedAudioRouting`.
- `VideoMixer` implements `IVideoMixer` and `ISupportsAdvancedVideoRouting`.
- `AudioVideoMixer` implements both `IAudioMixer` and `IVideoMixer`, plus both routing interfaces:
  - `ISupportsAdvancedAudioRouting`
  - `ISupportsAdvancedVideoRouting`
- Mixer source ownership is detach-first with configurable policy:
  - default behavior is detach-only (no auto-stop, no auto-dispose).
  - optional `StopOnDetach` can stop sources on remove/clear.
  - optional `DisposeOnDetach` can dispose sources on remove/clear when caller delegates ownership.
  - duplicate source registration by `SourceId` must be rejected deterministically (`MixerSourceIdCollision`, `3001`) with no registration mutation.
  - when multiple detach-step failures occur, return the first deterministic error code and emit diagnostics for secondary failures.
  - `RemoveSource(...)` and `ClearSources()` must use identical detach-step ordering and error-selection rules.
- Split advanced routing capabilities:
  - `ISupportsAdvancedAudioRouting` defines APIs for mapping audio inputs/channels to mixer outputs.
  - `ISupportsAdvancedVideoRouting` defines APIs for mapping video inputs/sources to mixer outputs.
  - Both interfaces expose direct routing APIs (add/remove/update route operations) and a read-only list of current routes.
- `MediaPlayer` does not implement advanced routing interfaces by default.
- Core video-output policy defaults: `VideoOutputBackpressureMode.DropOldest` is default; Wait mode timeout derives from effective frame duration multiplied by configurable frame-time multiplier, with explicit-timeout requirement when cadence is unresolved.
- Core video outputs require explicit config at start (`Start(VideoOutputConfig)`); no parameterless start path in the target contract.
- Core keeps a backpressure outcome matrix (DropNewest/DropOldest/Wait plus disposed-input precedence) as the canonical behavior table in `Media/S.Media.Core/API-outline.md`.

## Media Item and Dynamic Metadata Contracts

- `S.Media.Core` defines `IMediaItem` as the shared media wrapper contract for player/mixer-facing usage.
- `IMediaItem` requirements:
  - supports audio-only, video-only, and combined media via concrete implementations.
  - exposes stream lists as read-only properties:
    - `IReadOnlyList<AudioStreamInfo> AudioStreams`
    - `IReadOnlyList<VideoStreamInfo> VideoStreams`
  - exposes nullable metadata snapshot and state flag:
    - `MediaMetadataSnapshot? Metadata`
    - `bool HasMetadata`
- `IDynamicMetadata` lives in Core and provides live metadata updates:
  - event payload must be a full metadata snapshot, not a partial delta.
  - includes `UpdatedAtUtc` in the snapshot contract for update ordering.
- Metadata model rules:
  - keep strongly typed basic stream metadata in typed contracts (`duration`, `bitrate`, `codec`, plus nullable `frameRate`, `channelCount`, `sampleRate` as applicable).
  - keep optional/non-core tags (for example ID3-like tags) in `ReadOnlyDictionary<string, string> AdditionalMetadata`.
  - `AdditionalMetadata` keys are case-sensitive.
- Selection boundary:
  - stream selection remains in `S.Media.FFmpeg` source/open-option APIs.
  - `IMediaItem` does not own selection policy.

## Mixer Defaults and Clocking

- Default clock leadership modes:
  - `AudioMixer`: `AudioLed`
  - `VideoMixer`: `VideoLed`
  - `AudioVideoMixer`: `Hybrid`
- Mixer clock contract uses one `IMediaClock Clock` plus `ClockType` selector.
- `ClockType.External` is the external-clock path (external clocks implement `IMediaClock` directly).
- Nonsensical mixer/clock-type pairs (for example `VideoLed` on `AudioMixer`) must fail with `MixerClockTypeInvalid` (`3002`) and make no state change.
- Default runtime clock implementation is `CoreMediaClock`.
- External clock support remains optional and pluggable.
- External clock correction is opt-in (default: disabled).
- Primary external clock target: NDI (`S.Media.NDI`).
- If an external clock is explicitly configured but unavailable (for example NDI/network transport loss), operations must fail with `MediaExternalClockUnavailable` (no implicit fallback to `CoreMediaClock`).
- If configured clock chain cannot be resolved, fail startup rather than silently switching to a hidden mode.
- Drift correction is opt-in only (disabled by default).
- Drift correction values must be configurable in mixer configs.
- Drift polling cadence should use a smoothed FPS window derived from effective video/render cadence.
- Default drift values for `AudioVideoMixer`:
  - hard-resync threshold: `200 ms`
  - smooth micro-correction cap: `+/-2%`

## Seek Semantics (Required Standard)

- Timeline seek index is zero-based frame index.
- Invalid seek (time-based or frame-based) must:
  - return non-zero error code
  - make no state change
  - perform no clamping
- Non-finite/negative seek targets must return `MediaInvalidArgument`.
- Mixer `Seek(...)` operations apply immediately in the current transport state (running/paused/stopped), are not deferred/queued, and return after coordinated state update attempt.
- Non-seekable live sources must return shared non-zero code (`MediaSourceNonSeekable`).
- Unknown/unbounded source duration must be exposed as `double.NaN` (`DurationSeconds`) rather than clamped/sentinel finite values.
- `IVideoSource.CurrentFrameIndex` is the presentation index.
- Optional decode-side frame index may be exposed for diagnostics (`CurrentDecodeFrameIndex`), nullable when unavailable.
- This rule applies consistently to player, mixer, and decoder-facing seek entrypoints.

## Debug Framework in Core

- Define a centralized diagnostics key catalog in `S.Media.Core`.
- Required keys:
  - `frame.presented`
  - `frame.decoded`
  - `seek.fail`
- Keep keys as stable strings and document them as compatibility-sensitive.
- `DebugInfo` value types:
  - scalar primitives
  - `TimeSpan`
  - fixed arrays up to 8 elements
- Producers (decoder/mixer/player/output) report through Core-owned abstractions.

## Error Code Ranges in Core

- Define error code ranges in `S.Media.Core` and enforce non-overlap:
  - `0-999`: generic/common
  - `1000-1999`: playback/player lifecycle
  - `2000-2999`: decoding (FFmpeg)
  - `3000-3999`: mixing/sync/conflict
  - `4000-4999`: output/render
  - `5000-5199`: NDI integration
- Canonical allocation and reserve policy is tracked in `Media/S.Media.Core/error-codes.md`.
- `MediaSourceReadTimeout` (`4209`) is reserved for future timeout-bounded read paths only; canonical semantics are defined in `Media/S.Media.Core/error-codes.md`.
- Core symbol scaffold for this policy: `Errors/ErrorCodeAllocationRange.cs` + `Errors/MediaErrorAllocations.cs`.
- Core error classification includes shared-semantic normalization via `Errors/ErrorCodeRanges.cs` (`ResolveSharedSemantic`), so module-local codes can map to canonical semantics (for example `FFmpegConcurrentReadViolation` `2014`, `NDIAudioReadRejected` `5005`, `NDIVideoReadRejected` `5006` -> `MediaConcurrentOperationViolation` `950`).
- `MediaConcurrentOperationViolation` (`950`) may also be surfaced directly by Core/orchestration paths when no module-specific code is more precise.
- Reserved chunks (initial):
  - `2000-2099`: FFmpeg active initial picks
  - `2100-2199`: FFmpeg runtime/native loading reserve
  - `2200-2299`: FFmpeg mapping/resampler reserve
  - `4000-4099`: Core generic video-output/backpressure initial picks (`4000` queue-full, `4001` wait-timeout, `4002` disposed-frame push)
  - `4300-4399`: PortAudio active initial picks
  - `4400-4499`: OpenGL clone/render active initial picks
  - `5000-5079`: NDI active + near-term reserve
  - `5080-5199`: NDI future reserve
  - `900-949`: MIDI reserve block (within generic/common range)
- Keep legacy mapping table from current `VideoErrorCode` in `VideoLibs/Seko.OwnAudioNET.Video/Events/VideoErrorEventArgs.cs`.

## Exception and Logging Policy (Finalized)

- Operational failure paths in `S.Media.*` return deterministic int error codes (`0` success, non-zero failure).
- Throwing `MediaException` (or area-derived exceptions) is reserved for programmer misuse, invariant violations, or unrecoverable construction failures.
- Raw third-party/system exceptions remain unwrapped.
- When available, preserve backend-native exception detail/messages for maximum diagnostics quality.
- Logging standard is `Microsoft.Extensions.Logging` across all new projects.
- Video output push paths are hot paths and remain performance-first: branch-light steady state, no allocation-heavy per-frame checks, and no verbose per-frame logging outside trace/sampled diagnostics.
- Logging levels are runtime-configurable with precedence:
  - global level first
  - per-area override second
- Fixed logging areas for initial implementation:
  - `Core`, `Decoding`, `Mixing`, `PortAudio`, `OpenGL`, `NDI`, `MIDI`
- Detailed logging/error conventions and correlation-id naming are deferred to `Doc/logging-and-errors.md`.

## Lifecycle Semantics (Required Standard)

- `Stop()` operations are idempotent and return success when already stopped.
- Backend `Terminate()` operations must auto-stop active inputs/outputs and return success when already terminated.
- Shared event dispatch is per-instance FIFO; cross-instance ordering is unspecified.
- Successful `Stop()`/`Close()`/`Terminate()`/`Dispose()` completion is an event-publication fence (no user-visible callbacks after completion).

## FFmpeg Decoding Project Plan

- Create `S.Media.FFmpeg` with source-first public surfaces:
  - Audio source path (minimal allocations, no video dependency requirement).
  - Video source path (minimal audio coupling).
  - Optional shared decode context for synced A/V and reduced duplicate demux work.
- Timestamp-led timing is default; external clock correction remains opt-in.
- Migrate/adapt existing foundations from:
  - `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFAudioDecoder.cs`
  - `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFVideoDecoder.cs`
  - `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFSharedDemuxSession.cs`
- Default stream selection: first usable audio stream + first usable video stream.
- Provide explicit stream selection through open options.
- Keep selection ownership in FFmpeg-side source/open options, not Core media-item contracts.
- FFmpeg native runtime bootstrap is consumer-owned through `FFmpeg.AutoGen` APIs; `S.Media.FFmpeg` does not expose a separate runtime-path options type.
- Nonsensical open/config combinations must return `FFmpegInvalidConfig` (`2010`) with no partial-open side effects.
- Unknown/live duration semantics are explicit for FFmpeg sources: `DurationSeconds` returns `double.NaN` when duration is unknown or unbounded/live.
- FFmpeg single-reader concurrency violations use dedicated code `FFmpegConcurrentReadViolation` (`2014`) from the active FFmpeg allocation pool (`2000-2099`).
- Shared Core concurrency-misuse semantic is `MediaConcurrentOperationViolation` (`950`, generic/common); module-specific concurrency codes map to this semantic.
- Invalid FFmpeg option combinations are covered by a maintained invalid-config matrix in `Media/S.Media.FFmpeg/API-outline.md` and validated by deterministic code assertions.
- Read ownership contract:
  - caller owns buffers passed to `ReadSamples(...)`; implementations must not retain those references.
  - caller-owned `AudioFrame` sample memory is call-scoped; implementations must not retain references after method return.
  - caller owns/disposes `VideoFrame` returned from `ReadFrame(...)`.
- Efficiency contract:
  - audio reads are caller-buffer-reuse friendly and must not retain caller-provided buffers.
  - video reads may reuse internal decode/convert buffers while preserving caller-owned `VideoFrame` lifetime guarantees.
- Thread-safety contract:
  - source instances are single-reader (`ReadSamples`/`ReadFrame` are not concurrent-safe on the same instance).
  - `Start`/`Stop`/`Seek`/`Dispose` are serialized per source/shared context.
  - shared decode internals keep demux/decode ownership on dedicated worker thread(s) with bounded queues.
- `FFMediaItem` rules:
  - provide constructor overloads for audio-only, video-only, and combined A/V source inputs.
  - when created with sources, `FFMediaItem` owns and disposes those sources.
  - low-level `FFAudioDecoder` / `FFVideoDecoder` stay implementation-internal.

## MediaPlayer Design

- Primary simple API:
  - `Play(IMediaItem media)` as the default happy path (`Play(Media)` is shorthand wording only).
- Interface composition:
  - `IMediaPlayer : IAudioVideoMixer` (friendly mixer surface) with no advanced-routing interfaces on player.
- Required player properties/events:
  - properties: `State`, `Position`, `Duration`, `CurrentFrame`, `Volume`, `ActiveMedia`
  - collections:
    - `IReadOnlyList<IAudioOutput> AudioOutputs`
    - `IReadOnlyList<IVideoOutput> VideoOutputs`
  - events: `StateChanged`, `VolumeChanged`, `PlaybackEnded`, `PlaybackFailed`
- Advanced feature scope in this phase:
  - multi-output support via direct typed methods:
    - `AddAudioOutput(...)`, `RemoveAudioOutput(...)`
    - `AddVideoOutput(...)`, `RemoveVideoOutput(...)`
- Keep advanced routing logic in mixers, not `MediaPlayer`.
- `ActiveMedia` uses the shared `IMediaItem` contract and supports audio-only/video-only/combined items.

## Conflict Policies

- Audio conflict default: sum/mix (`AudioConflictPolicy.SumWithLevelControl`).
- Video conflict default: throw (`VideoConflictPolicy.ThrowOnConflict`).
- Policies must be configurable but deterministic, with defaults applied in mixer/player constructors.
- Conflict decisions must emit diagnostics/error codes via Core.

## Finalization Status

- Architecture decisions in this document are finalized.
- No additional blocking design decisions are required before implementation.
- Migration policy is fixed: temporary solution-level coexistence is allowed while migrating, but production/runtime sign-off requires removing legacy projects and legacy runtime paths.
- Clean-cut runtime rule is fixed: no compatibility layers, no migration wrappers, and no dual-path runtime in final architecture.

## Shared Wording Template (API Outlines)

Documentation placement policy:
- Migration/error planning docs should live in the owning `Media/S.Media.*` project folder (or `Media/S.Media.Core` for shared policy docs), not in generic top-level docs.

| Doc | Canonical location |
| --- | --- |
| Shared error allocation policy | `Media/S.Media.Core/error-codes.md` |
| Implementation readiness checklist | `Media/S.Media.Core/implementation-readiness-checklist.md` |
| Implementation execution schedule | `Media/S.Media.Core/implementation-execution-schedule.md` |
| FFmpeg migration plan | `Media/S.Media.FFmpeg/ffmpeg-migration-plan.md` |
| OpenGL migration plan | `Media/S.Media.OpenGL/opengl-migration-plan.md` |

Migration status legend (for all migration tracker tables):

| Status | Meaning |
| --- | --- |
| `Planned` | Scoped and approved; implementation not started |
| `In Progress` | Actively being implemented/refactored |
| `Done` | Implemented and validated against contract goals |
| `Blocked` | Cannot proceed until dependency/decision is resolved |

Use these canonical lines in module `API-outline.md` `Notes` sections to avoid wording drift:

- `Return-code baseline: 0 is success; all non-zero values are failures.`
- `Idempotency baseline: Stop/Close/Terminate returns MediaResult.Success when already stopped/closed/terminated.`
- `Failure atomicity baseline: failed lifecycle/remove/detach operations must not leave partially mutated runtime state.`
- `Detach return-code precedence: return the most specific owned module/backend detach-step failure code when available; use MixerDetachStepFailed (3000) only as fallback.`
- `Secondary detach failures are diagnostics-only and must not change the operation return code.`
- `DebugKeys.MixerDetachSecondaryFailure payload fields: operation, sourceId, step, errorCode, correlationId, and backend/native detail when available.`
- `Callback/event dispatch policy is fixed in this phase; future minimal dispatcher evolution must preserve per-instance ordering and teardown-fence guarantees.`

## Execution-Time Considerations (Non-Blocking)

- Validation matrix ownership:
  - Define a minimum matrix for Linux and Windows covering audio-only, video-only, synced A/V, and optional external clock (NDI) paths.
  - Keep this as execution planning, not an API design blocker.
  - Use `Media/S.Media.Core/implementation-readiness-checklist.md` as the pre-implementation go/no-go gate for each module.
  - Use `Media/S.Media.Core/implementation-execution-schedule.md` as the dependency-ordered module execution plan.
- Contract-test rollout (phased):
  - Phase 1: lifecycle/idempotency + seek semantics + deterministic error-code assertions + duration semantics (`DurationSeconds` finite for known media; `double.NaN` for live/unknown).
  - Phase 1: shared-semantic mapping assertions (`ResolveSharedSemantic`) for module-local codes that normalize to canonical generic/common semantics.
  - Phase 1: event contract assertions (per-instance ordering, no post-teardown callbacks, and deterministic overflow behavior under bounded queues).
  - Phase 1: clock contract assertions (default `CoreMediaClock`, configured external-clock unavailability returns `MediaExternalClockUnavailable`, no implicit fallback).
  - Phase 1: seek execution assertions (seek is immediate/non-queued for running/paused/stopped transports).
  - Phase 1: detach-policy assertions (default detach-only, `StopOnDetach`, `DisposeOnDetach`, remove/clear parity, source-registration-order iteration, deterministic first-error selection, and secondary-failure diagnostics emission).
  - Phase 1: video-output precedence assertions include both branches: disposed-frame push returns `VideoFrameDisposed` (`4002`) before queue/backpressure outcomes (`4000`/`4001`) when overlapping conditions are true, and non-disposed frame push under the same pressure returns policy outcome (`4000`/`4001`) (not `4002`).
  - Phase 2: output reentrancy/threading contract assertions for `IVideoOutput` (`PushFrame`/`Stop`/`Dispose` interaction ordering and deterministic error outcomes under contention).
  - Phase 2: runtime loading/interop failure semantics + queue/clamp behavior.
  - Phase 3: A/V sync, external-clock opt-in behavior, and resilience/perf smoke coverage (including NDI live-path `DurationSeconds = double.NaN` assertions).
  - Future timeout-read gate: if any module reintroduces timeout-bounded read APIs, that module must add explicit contract tests asserting `partial-before-deadline = success` and `no-arrival-before-deadline = MediaSourceReadTimeout (4209)`.
- Performance acceptance baselines:
  - Agree target telemetry thresholds (startup latency, steady-state drift envelope, frame-drop budget) before FFmpeg and mixer optimization work.
  - Validate drift-correction defaults (`200 ms`, `+/-2%`) against real device buffer behavior during implementation.
- Logging rollout order:
  - Sequence `MIDI/PMLib` logging refactor so `Microsoft.Extensions.Logging` integration lands before higher-level `S.Media.MIDI` wrapper API stabilization.
  - Preserve non-static logger injection pattern across `S.Media.PortAudio`, `S.Media.NDI`, and `S.Media.MIDI`.
- Exception and error-code guidance:
  - Keep third-party/system exceptions unwrapped.
  - Keep operational failures on deterministic int error codes.
  - Ensure throw paths (misuse/invariant/unrecoverable) have an associated `MediaErrorCode` when owned.
  - Prefer backend-native details in logged exception context.
- Interop packaging checkpoints:
  - Use system/user-provided native dependencies for FFmpeg, NDI SDK runtime, and SDL3.
  - Fail fast on missing native dependencies (mapped `MediaErrorCode` where owned; otherwise raw system exception).
  - Keep packaging decisions recorded as deployment notes to avoid late-stage integration churn.
- Interop safety checkpoints:
  - Keep native demux/decode ownership on dedicated worker threads.
  - Use bounded queues with deterministic minimum clamp (`>= 1`) instead of unbounded buffering.
  - Enforce deterministic teardown so no native callback/event path publishes after dispose.
- MIDI callback evolution checkpoint:
  - Keep current synchronous/no-queue/no-drop callback model in this phase.
  - If callback latency becomes a verified issue, introduce a minimal dispatcher later without breaking `MessageReceived`/`StatusChanged` contracts, per-instance ordering, or no-post-teardown guarantees.
- Non-Core callback evolution checkpoint:
  - Keep current module-level callback/event dispatch behavior fixed in this phase (no new callback-dispatch option surfaces).
  - If callback latency becomes a verified issue in FFmpeg/NDI/OpenGL/PortAudio adapters, introduce minimal dispatcher mechanics later without breaking event ordering or teardown-fence guarantees.
- Non-Core callback note template (for module outlines):
  - Callback/event dispatch policy is fixed in this phase (no module-level callback-dispatch configuration surface).
  - Future evolution note: if callback latency becomes a verified issue, add a minimal dispatcher later without breaking per-instance event ordering or teardown-fence guarantees.
- Diagnostics spec handoff:
  - Keep the detailed structured logging/error conventions in `Doc/logging-and-errors.md`.
  - `DebugKeys.MixerDetachSecondaryFailure` scope: emit only for secondary (non-returned) failures encountered inside one remove/clear detach operation; include `operation`, `sourceId`, `step`, `errorCode`, `correlationId`, and backend/native detail when available.
  - Detach return-code precedence: return the most specific owned/module/backend detach-step failure code when available; use `MixerDetachStepFailed` (`3000`) only as fallback.

## Exact Per-Project File List (Planned)

### `Media/S.Media.Core` (mixers + player live here)

- `Media/S.Media.Core/Diagnostics/DebugKeys.cs` - `DebugKeys` (`frame.presented`, `frame.decoded`, `seek.fail`, `mixer.detach.secondaryFailure`).
- `Media/S.Media.Core/Diagnostics/DebugInfo.cs` - `DebugInfo` (typed payload contract).
- `Media/S.Media.Core/Errors/MediaErrorCode.cs` - `MediaErrorCode` (range-based enum IDs).
- `Media/S.Media.Core/Errors/MediaResult.cs` - `MediaResult` (`Success = 0` constant).
- `Media/S.Media.Core/Errors/ErrorCodeRanges.cs` - `ErrorCodeRanges` (`0-999`, `1000-1999`, `2000-2999`, `3000-3999`, `4000-4999`, `5000-5199`).
- `Media/S.Media.Core/Errors/ErrorCodeAllocationRange.cs` - `ErrorCodeAllocationRange` (named inclusive allocation range contract).
- `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` - `MediaErrorAllocations` (1:1 symbol mirror of `Media/S.Media.Core/error-codes.md` ranges/chunks).
- `Media/S.Media.Core/Errors/MediaException.cs` - `MediaException` (base exception with `MediaErrorCode`).
- `Media/S.Media.Core/Errors/AreaExceptions.cs` - area-derived exceptions with contextual detail payloads.
- `Media/S.Media.Core/Media/IMediaItem.cs` - `IMediaItem` (`AudioStreams`, `VideoStreams`, `Metadata`, `HasMetadata`).
- `Media/S.Media.Core/Media/IDynamicMetadata.cs` - `IDynamicMetadata` (`MetadataUpdated` with full snapshot payload).
- `Media/S.Media.Core/Media/IMediaPlaybackSourceBinding.cs` - optional playback-source bridge for media items that provide ready mixer sources.
- `Media/S.Media.Core/Media/MediaMetadataSnapshot.cs` - `MediaMetadataSnapshot` (`UpdatedAtUtc`, case-sensitive `ReadOnlyDictionary<string, string> AdditionalMetadata`).
- `Media/S.Media.Core/Media/AudioStreamInfo.cs` - `AudioStreamInfo` (typed basic stream metadata).
- `Media/S.Media.Core/Media/VideoStreamInfo.cs` - `VideoStreamInfo` (typed basic stream metadata).
- `Media/S.Media.Core/Clock/IMediaClock.cs` - `IMediaClock`.
- `Media/S.Media.Core/Clock/CoreMediaClock.cs` - `CoreMediaClock` (default Core clock implementation).
- `Media/S.Media.Core/Audio/IAudioSource.cs` - `IAudioSource` (start/stop/read/seek contract).
- `Media/S.Media.Core/Video/IVideoSource.cs` - `IVideoSource` (start/stop/read/seek + frame-seek contract).
- `Media/S.Media.Core/Video/VideoFrame.cs` - `VideoFrame` (caller-owned/disposable decoded frame contract with format/plane metadata).
- `Media/S.Media.Core/Video/VideoPixelFormat.cs` - `VideoPixelFormat`.
- `Media/S.Media.Core/Video/IPixelFormatData.cs` - `IPixelFormatData` (format-specific metadata abstraction).
- `Media/S.Media.Core/Video/Rgba32PixelFormatData.cs` - `Rgba32PixelFormatData`.
- `Media/S.Media.Core/Video/Bgra32PixelFormatData.cs` - `Bgra32PixelFormatData`.
- `Media/S.Media.Core/Video/Yuv420PPixelFormatData.cs` - `Yuv420PPixelFormatData`.
- `Media/S.Media.Core/Video/Nv12PixelFormatData.cs` - `Nv12PixelFormatData`.
- `Media/S.Media.Core/Video/Yuv422PPixelFormatData.cs` - `Yuv422PPixelFormatData`.
- `Media/S.Media.Core/Video/Yuv422P10LePixelFormatData.cs` - `Yuv422P10LePixelFormatData`.
- `Media/S.Media.Core/Video/P010LePixelFormatData.cs` - `P010LePixelFormatData`.
- `Media/S.Media.Core/Video/Yuv420P10LePixelFormatData.cs` - `Yuv420P10LePixelFormatData`.
- `Media/S.Media.Core/Video/Yuv444PPixelFormatData.cs` - `Yuv444PPixelFormatData`.
- `Media/S.Media.Core/Video/Yuv444P10LePixelFormatData.cs` - `Yuv444P10LePixelFormatData`.
- `Media/S.Media.Core/Video/VideoOutputBackpressureMode.cs` - `VideoOutputBackpressureMode`.
- `Media/S.Media.Core/Video/VideoOutputConfig.cs` - `VideoOutputConfig`.
- `Media/S.Media.Core/Video/IVideoOutput.cs` - `IVideoOutput` (start/stop/push contract for video sinks).
- `Media/S.Media.Core/Routing/AudioRoute.cs` - `AudioRoute`.
- `Media/S.Media.Core/Routing/VideoRoute.cs` - `VideoRoute`.
- `Media/S.Media.Core/Routing/ISupportsAdvancedAudioRouting.cs` - `ISupportsAdvancedAudioRouting` (route APIs + read-only current route list).
- `Media/S.Media.Core/Routing/ISupportsAdvancedVideoRouting.cs` - `ISupportsAdvancedVideoRouting` (route APIs + read-only current route list).
- `Media/S.Media.Core/Mixing/IAudioMixer.cs` - `IAudioMixer` (sync-focused transport + source scheduling + deterministic source/dropout events).
- `Media/S.Media.Core/Mixing/AudioMixerSyncMode.cs` - `AudioMixerSyncMode`.
- `Media/S.Media.Core/Mixing/AudioMixerState.cs` - `AudioMixerState`.
- `Media/S.Media.Core/Mixing/AudioMixerStateChangedEventArgs.cs` - `AudioMixerStateChangedEventArgs`.
- `Media/S.Media.Core/Mixing/AudioSourceErrorEventArgs.cs` - `AudioSourceErrorEventArgs`.
- `Media/S.Media.Core/Mixing/AudioMixerDropoutEventArgs.cs` - `AudioMixerDropoutEventArgs`.
- `Media/S.Media.Core/Mixing/MixerSourceDetachOptions.cs` - `MixerSourceDetachOptions` (detach/stop/dispose policy for source removal/clear paths).
- `Media/S.Media.Core/Mixing/ClockType.cs` - `ClockType` (`External`, `AudioLed`, `VideoLed`, `Hybrid`).
- `Media/S.Media.Core/Mixing/MixerKind.cs` - `MixerKind`.
- `Media/S.Media.Core/Mixing/MixerClockTypeRules.cs` - `MixerClockTypeRules` (mixer-kind clock-type validation).
- `Media/S.Media.Core/Mixing/IVideoMixer.cs` - `IVideoMixer` (transport + active-source control + deterministic video sync/seek).
- `Media/S.Media.Core/Mixing/VideoMixerSyncMode.cs` - `VideoMixerSyncMode`.
- `Media/S.Media.Core/Mixing/VideoMixerState.cs` - `VideoMixerState`.
- `Media/S.Media.Core/Mixing/VideoMixerStateChangedEventArgs.cs` - `VideoMixerStateChangedEventArgs`.
- `Media/S.Media.Core/Mixing/VideoSourceErrorEventArgs.cs` - `VideoSourceErrorEventArgs`.
- `Media/S.Media.Core/Mixing/VideoActiveSourceChangedEventArgs.cs` - `VideoActiveSourceChangedEventArgs`.
- `Media/S.Media.Core/Mixing/IAudioVideoMixer.cs` - `IAudioVideoMixer` (coordinated audio/video transport + deterministic combined seek policies).
- `Media/S.Media.Core/Mixing/AudioVideoMixerState.cs` - `AudioVideoMixerState`.
- `Media/S.Media.Core/Mixing/AudioMixer.cs` - `AudioMixer` (default `AudioLed`).
- `Media/S.Media.Core/Mixing/VideoMixer.cs` - `VideoMixer` (default `VideoLed`).
- `Media/S.Media.Core/Mixing/AudioVideoMixer.cs` - `AudioVideoMixer` (default `Hybrid`).
- `Media/S.Media.Core/Playback/IMediaPlayer.cs` - `IMediaPlayer` (`Play(IMediaItem media)` + direct typed audio/video output methods; `Play(Media)` shorthand in prose).
- `Media/S.Media.Core/Playback/MediaPlayer.cs` - `MediaPlayer` (simple orchestration + multi-output support).

### `Media/S.Media.FFmpeg`

- `Media/S.Media.FFmpeg/Config/FFmpegOpenOptions.cs` - `FFmpegOpenOptions`.
- `Media/S.Media.FFmpeg/Config/FFmpegDecodeOptions.cs` - `FFmpegDecodeOptions`.
- `Media/S.Media.FFmpeg/Audio/FFAudioChannelMappingPolicy.cs` - `FFAudioChannelMappingPolicy`.
- `Media/S.Media.FFmpeg/Audio/FFAudioChannelMap.cs` - `FFAudioChannelMap`.
- `Media/S.Media.FFmpeg/Audio/FFAudioSourceOptions.cs` - `FFAudioSourceOptions`.
- `Media/S.Media.FFmpeg/Runtime/FFSharedDecodeContext.cs` - `FFSharedDecodeContext` (optional shared demux/decode coordinator).
- `Media/S.Media.FFmpeg/Runtime/FFStreamDescriptor.cs` - `FFStreamDescriptor`.
- `Media/S.Media.FFmpeg/Sources/FFAudioSource.cs` - `FFAudioSource`.
- `Media/S.Media.FFmpeg/Sources/FFVideoSource.cs` - `FFVideoSource`.
- `Media/S.Media.FFmpeg/Media/FFMediaItem.cs` - `FFMediaItem` (audio-only/video-only/combined constructor overloads; owns passed sources).
- `Media/S.Media.FFmpeg/Decoders/Internal/FFAudioDecoder.cs` - `FFAudioDecoder` (internal implementation building block).
- `Media/S.Media.FFmpeg/Decoders/Internal/FFVideoDecoder.cs` - `FFVideoDecoder` (internal implementation building block).
- `Media/S.Media.FFmpeg/Decoders/Internal/FFPacketReader.cs` - `FFPacketReader` (internal implementation building block).
- `Media/S.Media.FFmpeg/Decoders/Internal/FFResampler.cs` - `FFResampler` (internal implementation building block).
- `Media/S.Media.FFmpeg/Decoders/Internal/FFPixelConverter.cs` - `FFPixelConverter` (internal implementation building block).
- `Media/S.Media.FFmpeg/Decoders/Internal/FFSharedDemuxSession.cs` - `FFSharedDemuxSession` (internal implementation building block).

### `Media/S.Media.PortAudio`

- `Media/S.Media.PortAudio/Engine/PortAudioEngine.cs` - `PortAudioEngine` (engine lifecycle + runtime/device discovery + per-device output creation).
- `Media/S.Media.PortAudio/Output/PortAudioOutput.cs` - `PortAudioOutput`.
- `Media/S.Media.PortAudio/Input/PortAudioInput.cs` - `PortAudioInput` (`IAudioSource`-style pull input).
- `Media/S.Media.PortAudio/Diagnostics/PortAudioLogAdapter.cs` - `PortAudioLogAdapter`.

### `Media/S.Media.OpenGL`

- `Media/S.Media.OpenGL/OpenGLVideoEngine.cs` - `OpenGLVideoEngine`.
- `Media/S.Media.OpenGL/OpenGLVideoOutput.cs` - `OpenGLVideoOutput`.
- `Media/S.Media.OpenGL/Output/OpenGLCloneOptions.cs` - `OpenGLCloneOptions`.
- `Media/S.Media.OpenGL/Output/OpenGLClonePolicyOptions.cs` - `OpenGLClonePolicyOptions`.
- `Media/S.Media.OpenGL/Output/OpenGLCloneMode.cs` - `OpenGLCloneMode`.
- `Media/S.Media.OpenGL/Output/OpenGLClonePixelFormatPolicy.cs` - `OpenGLClonePixelFormatPolicy`.
- `Media/S.Media.OpenGL/Output/OpenGLHUDCloneMode.cs` - `OpenGLHUDCloneMode`.
- `Media/S.Media.OpenGL/Output/OpenGLSurfaceMetadata.cs` - `OpenGLSurfaceMetadata`.
- `Media/S.Media.OpenGL/Upload/OpenGLUploadPlanner.cs` - `OpenGLUploadPlanner`.
- `Media/S.Media.OpenGL/Upload/OpenGLTextureUploader.cs` - `OpenGLTextureUploader`.
- `Media/S.Media.OpenGL/Conversion/YuvToRgbaConverter.cs` - `YuvToRgbaConverter`.
- `Media/S.Media.OpenGL/Diagnostics/OpenGLOutputDiagnostics.cs` - `OpenGLOutputDiagnostics`.
- `Media/S.Media.OpenGL/Diagnostics/OpenGLDiagnosticsEvents.cs` - `OpenGLDiagnosticsEvents`.
- `Media/S.Media.OpenGL/Diagnostics/OpenGLCapabilitySnapshot.cs` - `OpenGLCapabilitySnapshot`.

### `Media/S.Media.NDI`

- `Media/S.Media.NDI/Runtime/NDIEngine.cs` - `NDIEngine` (lifecycle/orchestration for sources, outputs, and diagnostics snapshots).
- `Media/S.Media.NDI/Clock/NDIExternalTimelineClock.cs` - `NDIExternalTimelineClock`.
- `Media/S.Media.NDI/Input/NDIAudioSource.cs` - `NDIAudioSource`.
- `Media/S.Media.NDI/Input/NDIVideoSource.cs` - `NDIVideoSource`.
- `Media/S.Media.NDI/Config/NDISourceOptions.cs` - `NDISourceOptions` (per-source policy overrides, including diagnostics tick override; falls back to global limits/options).
- `Media/S.Media.NDI/Config/NDIOutputOptions.cs` - `NDIOutputOptions` (explicit output capability/startup validation contract).
- `Media/S.Media.NDI/Output/NDIVideoOutput.cs` - `NDIVideoOutput`.
- `Media/S.Media.NDI/Config/NDIIntegrationOptions.cs` - `NDIIntegrationOptions`.
- `Media/S.Media.NDI/Config/NDILimitsOptions.cs` - `NDILimitsOptions` (bounded queues and fan-out guardrails).
- `Media/S.Media.NDI/Config/NDIQueueOverflowPolicy.cs` - `NDIQueueOverflowPolicy` (configurable overflow handling; default `DropOldest`).
- `Media/S.Media.NDI/Config/NDIVideoFallbackMode.cs` - `NDIVideoFallbackMode` (no-frame vs last-frame fallback behavior).
- `Media/S.Media.NDI/Diagnostics/NDIDiagnosticsOptions.cs` - `NDIDiagnosticsOptions` (dedicated diagnostics thread + tick policy; effective minimum clamp `16ms`; diagnostics updates/callbacks raised on diagnostics thread).
- `Media/S.Media.NDI/Diagnostics/NDIAudioDiagnostics.cs` - `NDIAudioDiagnostics`.
- `Media/S.Media.NDI/Diagnostics/NDIVideoDiagnostics.cs` - `NDIVideoDiagnostics`.
- `Media/S.Media.NDI/Diagnostics/NDIEngineDiagnostics.cs` - `NDIEngineDiagnostics`.
- `Media/S.Media.NDI/Media/NDIMediaItem.cs` - `NDIMediaItem` (constructor overloads for discovered source and active receiver/connection; nullable metadata until available).

### `Media/S.Media.OpenGL.Avalonia`

- `Media/S.Media.OpenGL.Avalonia/Controls/AvaloniaOpenGLHostControl.cs` - `AvaloniaOpenGLHostControl`.
- `Media/S.Media.OpenGL.Avalonia/Output/AvaloniaVideoOutput.cs` - `AvaloniaVideoOutput`.
- `Media/S.Media.OpenGL.Avalonia/Output/AvaloniaCloneOptions.cs` - `AvaloniaCloneOptions`.
- `Media/S.Media.OpenGL.Avalonia/Diagnostics/MediaHudOverlay.cs` - `MediaHudOverlay`.
- `Media/S.Media.OpenGL.Avalonia/Diagnostics/AvaloniaOutputDiagnostics.cs` - `AvaloniaOutputDiagnostics`.


### `Media/S.Media.OpenGL.SDL3`

- `Media/S.Media.OpenGL.SDL3/SDL3VideoView.cs` - `SDL3VideoView` (includes embedding handle/descriptor APIs, state-bound handle lifetime, platform-specific thread-affinity checks, and host-loss error+teardown behavior).
- `Media/S.Media.OpenGL.SDL3/SDL3HudRenderer.cs` - `SDL3HudRenderer`.
- `Media/S.Media.OpenGL.SDL3/SDL3ShaderPipeline.cs` - `SDL3ShaderPipeline`.
- `Media/S.Media.OpenGL.SDL3/SDL3CloneOptions.cs` - `SDL3CloneOptions`.
- `Media/S.Media.OpenGL.SDL3/Diagnostics/SDL3OutputDiagnostics.cs` - `SDL3OutputDiagnostics`.

### `Media/S.Media.MIDI`

- `Media/S.Media.MIDI/Runtime/MIDIEngine.cs` - `MIDIEngine` (runtime + device catalog responsibilities folded into engine).
- `Media/S.Media.MIDI/Input/MIDIInput.cs` - `MIDIInput` (`IsOpen` state flag for native input-handle lifecycle).
- `Media/S.Media.MIDI/Output/MIDIOutput.cs` - `MIDIOutput` (`IsOpen` state flag for native output-handle lifecycle).
- `Media/S.Media.MIDI/Config/MIDIReconnectOptions.cs` - `MIDIReconnectOptions` (configurable short-disconnect recovery policy).
- `Media/S.Media.MIDI/Config/MIDIReconnectMode.cs` - `MIDIReconnectMode` (`AutoReconnect`/`NoRecover`).
- `Media/S.Media.MIDI/Events/MIDIMessageEventArgs.cs` - `MIDIMessageEventArgs` (message + source-device + receive timestamp payload).
- `Media/S.Media.MIDI/Events/MIDIConnectionStatusEventArgs.cs` - `MIDIConnectionStatusEventArgs` (connection status transition payload).
- `Media/S.Media.MIDI/Types/MIDIConnectionStatus.cs` - `MIDIConnectionStatus`.
- `Media/S.Media.MIDI/Diagnostics/MIDILogAdapter.cs` - `MIDILogAdapter`.

## Last Considerations (Non-Blocking)

- Define metadata snapshot monotonicity expectations (`UpdatedAtUtc` should not move backward for the same media item instance).
- Document metadata event ordering for `IDynamicMetadata` as best-effort across async producers.
- Keep a compact contract test matrix for metadata nullability (`audio-only`, `video-only`, `combined`, and NDI pre-metadata states).
- Add schema-drift guardrails for `AdditionalMetadata` keys (preserve case-sensitive keys exactly as received, no normalization).
- Track key architecture changes with a short decision log entry when any non-blocking execution policy is refined during implementation.
- Keep a compact NDI contract test matrix for lifecycle idempotency, read/fallback semantics, override precedence, diagnostics tick clamp (`>=16ms`), and output capability validation.
- No additional unclear non-blocking considerations remain at this stage.

