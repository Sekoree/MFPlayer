# PLAN.smedia-architecture.md

## S.Media Refactor Architecture Baseline (Finalized)

This document is the finalized architecture baseline for refactoring the current `Seko.OwnAudioNET.*` stack into a cohesive `S.Media.*` architecture with strict dependency direction, explicit manager/mixer/player APIs, and predictable playback semantics. The design keeps simple usage first (`MediaPlayer.Play(Media)`), preserves advanced scenarios through one controlled feature (multi-output), centralizes diagnostics/errors in Core, and introduces a dedicated FFmpeg decoding layer with audio-only, video-only, and synced A/V paths.

## Scope and Goals

- Move public namespaces to `S.Media` project-aligned roots (for example `S.Media.Core.*`, `S.Media.FFmpeg.*`).
- Preserve existing playback capabilities while simplifying top-level API shape.
- Keep direct `Add*`/`Remove*` methods on manager/mixer/player APIs (no indirect-only routing).
- Standardize seek and conflict behavior across audio/video/hybrid paths.
- Centralize debug keys and error code ranges in Core.
- Perform a hard cut from current `Seko.OwnAudioNET.*` implementations (no compatibility shims/wrappers).
- Remove all `Seko.OwnAudioNET.*` projects and move required functionality into the new `S.Media.*` project set.
- Remove `OwnAudio` as a dependency from the target architecture.
- Treat this architecture as a clean-cut new start (no migration/compatibility layer planning).

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
  - `Test/AudioEx` -> `Test/MediaDebug`
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
- `S.Media.OpenGL.Avalonia` canonical host path requires `OpenGLControlBase`.
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
- Split advanced routing capabilities:
  - `ISupportsAdvancedAudioRouting` defines APIs for mapping audio inputs/channels to mixer outputs.
  - `ISupportsAdvancedVideoRouting` defines APIs for mapping video inputs/sources to mixer outputs.
  - Both interfaces expose direct routing APIs (add/remove/update route operations) and a read-only list of current routes.
- `MediaPlayer` does not implement advanced routing interfaces by default.

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
- External clock support remains optional and pluggable.
- External clock correction is opt-in (default: disabled).
- Primary external clock target: NDI (`S.Media.NDI`).
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
- Non-seekable live sources must return shared non-zero code (`MediaSourceNonSeekable`).
- Timeout-based live reads should return shared non-zero code (`MediaSourceReadTimeout`).
- Live-read timeout policy is owned in `S.Media.Core` and is optional/config-driven (no mandatory hardcoded default timeout constants).
- `TimeSpan.Zero` timeout means non-blocking poll behavior.
- Negative timeout values must return invalid-argument code immediately.
- Audio timeout reads return success when partial data is available before deadline.
- Video timeout reads return success only when a new frame is available before deadline.
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
- Canonical allocation and reserve policy is tracked in `Doc/error-codes.md`.
- Core symbol scaffold for this policy: `Errors/ErrorCodeAllocationRange.cs` + `Errors/MediaErrorAllocations.cs`.
- Reserved chunks (initial):
  - `2000-2099`: FFmpeg active initial picks
  - `2100-2199`: FFmpeg runtime/native loading reserve
  - `2200-2299`: FFmpeg mapping/resampler reserve
  - `4300-4399`: PortAudio active initial picks
  - `4400-4499`: OpenGL clone/render active initial picks
  - `5000-5079`: NDI active + near-term reserve
  - `5080-5199`: NDI future reserve
  - `900-949`: MIDI reserve block (within generic/common range)
- Keep legacy mapping table from current `VideoErrorCode` in `VideoLibs/Seko.OwnAudioNET.Video/Events/VideoErrorEventArgs.cs`.

## Exception and Logging Policy (Finalized)

- Owned failure paths in `S.Media.*` should throw `MediaException` (or area-derived exceptions) with `MediaErrorCode`.
- Raw third-party/system exceptions remain unwrapped.
- When available, preserve backend-native exception detail/messages for maximum diagnostics quality.
- Logging standard is `Microsoft.Extensions.Logging` across all new projects.
- Logging levels are runtime-configurable with precedence:
  - global level first
  - per-area override second
- Fixed logging areas for initial implementation:
  - `Core`, `Decoding`, `Mixing`, `PortAudio`, `OpenGL`, `NDI`, `MIDI`
- Detailed logging/error conventions and correlation-id naming are deferred to `Doc/logging-and-errors.md`.

## Lifecycle Semantics (Required Standard)

- `Stop()` operations are idempotent and return success when already stopped.
- Backend `Terminate()` operations must auto-stop active inputs/outputs and return success when already terminated.

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
- `FFMediaItem` rules:
  - provide constructor overloads for audio-only, video-only, and combined A/V source inputs.
  - when created with sources, `FFMediaItem` owns and disposes those sources.
  - low-level `FFAudioDecoder` / `FFVideoDecoder` stay implementation-internal.

## MediaPlayer Design

- Primary simple API:
  - `Play(Media)` as the default happy path.
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
- Clean-cut rule is fixed: no compatibility layers, no migration wrappers, no dual-path runtime.

## Execution-Time Considerations (Non-Blocking)

- Validation matrix ownership:
  - Define a minimum matrix for Linux and Windows covering audio-only, video-only, synced A/V, and optional external clock (NDI) paths.
  - Keep this as execution planning, not an API design blocker.
- Contract-test rollout (phased):
  - Phase 1: lifecycle/idempotency + timeout/seek semantics + deterministic error-code assertions.
  - Phase 2: runtime loading/interop failure semantics + queue/clamp behavior.
  - Phase 3: A/V sync, external-clock opt-in behavior, and resilience/perf smoke coverage.
- Performance acceptance baselines:
  - Agree target telemetry thresholds (startup latency, steady-state drift envelope, frame-drop budget) before FFmpeg and mixer optimization work.
  - Validate drift-correction defaults (`200 ms`, `+/-2%`) against real device buffer behavior during implementation.
- Logging rollout order:
  - Sequence `MIDI/PMLib` logging refactor so `Microsoft.Extensions.Logging` integration lands before higher-level `S.Media.MIDI` wrapper API stabilization.
  - Preserve non-static logger injection pattern across `S.Media.PortAudio`, `S.Media.NDI`, and `S.Media.MIDI`.
- Exception and error-code guidance:
  - Keep third-party/system exceptions unwrapped.
  - Ensure owned throw paths have an associated `MediaErrorCode`.
  - Prefer backend-native details in logged exception context.
- Interop packaging checkpoints:
  - Use system/user-provided native dependencies for FFmpeg, NDI SDK runtime, and SDL3.
  - Fail fast on missing native dependencies (mapped `MediaErrorCode` where owned; otherwise raw system exception).
  - Keep packaging decisions recorded as deployment notes to avoid late-stage integration churn.
- Interop safety checkpoints:
  - Keep native demux/decode ownership on dedicated worker threads.
  - Use bounded queues with deterministic minimum clamp (`>= 1`) instead of unbounded buffering.
  - Enforce deterministic teardown so no native callback/event path publishes after dispose.
- Diagnostics spec handoff:
  - Keep the detailed structured logging/error conventions in `Doc/logging-and-errors.md`.

## Exact Per-Project File List (Planned)

### `Media/S.Media.Core` (mixers + player live here)

- `Media/S.Media.Core/Diagnostics/DebugKeys.cs` - `DebugKeys` (`frame.presented`, `frame.decoded`, `seek.fail`).
- `Media/S.Media.Core/Diagnostics/DebugInfo.cs` - `DebugInfo` (typed payload contract).
- `Media/S.Media.Core/Errors/MediaErrorCode.cs` - `MediaErrorCode` (range-based enum IDs).
- `Media/S.Media.Core/Errors/MediaResult.cs` - `MediaResult` (`Success = 0` constant).
- `Media/S.Media.Core/Errors/ErrorCodeRanges.cs` - `ErrorCodeRanges` (`0-999`, `1000-1999`, `2000-2999`, `3000-3999`, `4000-4999`, `5000-5199`).
- `Media/S.Media.Core/Errors/ErrorCodeAllocationRange.cs` - `ErrorCodeAllocationRange` (named inclusive allocation range contract).
- `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` - `MediaErrorAllocations` (1:1 symbol mirror of `Doc/error-codes.md` ranges/chunks).
- `Media/S.Media.Core/Errors/MediaException.cs` - `MediaException` (base exception with `MediaErrorCode`).
- `Media/S.Media.Core/Errors/AreaExceptions.cs` - area-derived exceptions with contextual detail payloads.
- `Media/S.Media.Core/Timeline/FrameIndex.cs` - `FrameIndex` (zero-based frame index value object).
- `Media/S.Media.Core/Timeline/SeekContracts.cs` - `SeekTarget`, `SeekResult`, `ISeekValidator` (invalid seek => immediate non-zero error code, no clamping, no state change).
- `Media/S.Media.Core/Timeline/LiveReadTimeoutOptions.cs` - `LiveReadTimeoutOptions` (optional timeout behavior and mode selection).
- `Media/S.Media.Core/Timeline/LiveReadTimeoutMode.cs` - `LiveReadTimeoutMode` (`Manual`, `ClockLatencyDerived`).
- `Media/S.Media.Core/Timeline/ClockLatencySnapshot.cs` - `ClockLatencySnapshot` (clock/buffer latency inputs for timeout policy).
- `Media/S.Media.Core/Timeline/LiveReadTimeoutPolicy.cs` - `LiveReadTimeoutPolicy` (timeout resolution helpers).
- `Media/S.Media.Core/Media/IMediaItem.cs` - `IMediaItem` (`AudioStreams`, `VideoStreams`, `Metadata`, `HasMetadata`).
- `Media/S.Media.Core/Media/IDynamicMetadata.cs` - `IDynamicMetadata` (`MetadataUpdated` with full snapshot payload).
- `Media/S.Media.Core/Media/MediaMetadataSnapshot.cs` - `MediaMetadataSnapshot` (`UpdatedAtUtc`, case-sensitive `ReadOnlyDictionary<string, string> AdditionalMetadata`).
- `Media/S.Media.Core/Media/AudioStreamInfo.cs` - `AudioStreamInfo` (typed basic stream metadata).
- `Media/S.Media.Core/Media/VideoStreamInfo.cs` - `VideoStreamInfo` (typed basic stream metadata).
- `Media/S.Media.Core/Audio/IAudioSource.cs` - `IAudioSource` (start/stop/read/seek contract with timeout overload semantics).
- `Media/S.Media.Core/Video/IVideoSource.cs` - `IVideoSource` (start/stop/read/seek contract with timeout overload semantics).
- `Media/S.Media.Core/Routing/AudioRoute.cs` - `AudioRoute`.
- `Media/S.Media.Core/Routing/VideoRoute.cs` - `VideoRoute`.
- `Media/S.Media.Core/Routing/ISupportsAdvancedAudioRouting.cs` - `ISupportsAdvancedAudioRouting` (route APIs + read-only current route list).
- `Media/S.Media.Core/Routing/ISupportsAdvancedVideoRouting.cs` - `ISupportsAdvancedVideoRouting` (route APIs + read-only current route list).
- `Media/S.Media.Core/Mixing/IAudioMixer.cs` - `IAudioMixer` (`AddSource`, `RemoveSource` direct APIs).
- `Media/S.Media.Core/Mixing/IVideoMixer.cs` - `IVideoMixer` (`AddSource`, `RemoveSource`, active source control).
- `Media/S.Media.Core/Mixing/IAudioVideoMixer.cs` - `IAudioVideoMixer` (hybrid sync + deterministic conflict policy).
- `Media/S.Media.Core/Mixing/AudioMixer.cs` - `AudioMixer` (default `AudioLed`).
- `Media/S.Media.Core/Mixing/VideoMixer.cs` - `VideoMixer` (default `VideoLed`).
- `Media/S.Media.Core/Mixing/AudioVideoMixer.cs` - `AudioVideoMixer` (default `Hybrid`).
- `Media/S.Media.Core/Playback/IMediaPlayer.cs` - `IMediaPlayer` (`Play(Media)` + direct typed audio/video output methods).
- `Media/S.Media.Core/Playback/MediaPlayer.cs` - `MediaPlayer` (simple orchestration + multi-output support).

### `Media/S.Media.FFmpeg`

- `Media/S.Media.FFmpeg/Config/FFmpegRuntimeOptions.cs` - `FFmpegRuntimeOptions` (consumer-provided native runtime root path).
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
- `Media/S.Media.NDI/Config/NDIReadOptions.cs` - `NDIReadOptions` (live read timeout options binding to Core timeout policy).
- `Media/S.Media.NDI/Config/NDISourceOptions.cs` - `NDISourceOptions` (per-source policy overrides, including diagnostics tick override; falls back to global limits/options).
- `Media/S.Media.NDI/Config/NDIReadRequest.cs` - `NDIReadRequest` (per-call timeout/diagnostics pause hints).
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

- `Media/S.Media.MIDI/MIDIRuntime.cs` - `MIDIRuntime`.
- `Media/S.Media.MIDI/MIDIDeviceCatalog.cs` - `MIDIDeviceCatalog`.
- `Media/S.Media.MIDI/MIDIInput.cs` - `MIDIInput` (`IsOpen` state flag for native input-handle lifecycle).
- `Media/S.Media.MIDI/MIDIOutput.cs` - `MIDIOutput` (`IsOpen` state flag for native output-handle lifecycle).
- `Media/S.Media.MIDI/MIDIMessageRouter.cs` - `MIDIMessageRouter`.

## Last Considerations (Non-Blocking)

- Define metadata snapshot monotonicity expectations (`UpdatedAtUtc` should not move backward for the same media item instance).
- Document metadata event ordering for `IDynamicMetadata` as best-effort across async producers.
- Keep a compact contract test matrix for metadata nullability (`audio-only`, `video-only`, `combined`, and NDI pre-metadata states).
- Add schema-drift guardrails for `AdditionalMetadata` keys (preserve case-sensitive keys exactly as received, no normalization).
- Track key architecture changes with a short decision log entry when any non-blocking execution policy is refined during implementation.
- Keep a compact NDI contract test matrix for lifecycle idempotency, timeout semantics, override precedence, diagnostics tick clamp (`>=16ms`), and output capability validation.
- No additional unclear non-blocking considerations remain at this stage.

