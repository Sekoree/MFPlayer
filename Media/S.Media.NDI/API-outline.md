# S.Media.NDI API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `Runtime/NDIEngine.cs`
- `sealed class NDIEngine : IDisposable`
- Planned API:
  - `int Initialize(NDIIntegrationOptions integrationOptions, NDIReadOptions readOptions, NDILimitsOptions limitsOptions, NDIDiagnosticsOptions diagnosticsOptions)`
  - `int Terminate()`
  - `bool IsInitialized { get; }`
  - `int CreateAudioSource(NdiReceiver receiver, in NDISourceOptions sourceOptions, out NDIAudioSource? source)`
  - `int CreateVideoSource(NdiReceiver receiver, in NDISourceOptions sourceOptions, out NDIVideoSource? source)`
  - `int CreateOutput(string outputName, in NDIOutputOptions outputOptions, out NDIVideoOutput? output)`
  - `int GetDiagnosticsSnapshot(out NDIEngineDiagnostics snapshot)`

### `Clock/NDIExternalTimelineClock.cs`
- `sealed class NDIExternalTimelineClock : IExternalClock`
- Planned API:
  - `double CurrentSeconds { get; }`
  - `void OnAudioFrame(long timecode100ns, int frameCount, int sampleRate)`
  - `double ResolveVideoPtsSeconds(long timestamp100ns, long timecode100ns, double frameDurationSeconds)`

### `Input/NDIAudioSource.cs`
- `sealed class NDIAudioSource : IAudioSource, IDisposable`
- Planned API:
  - `AudioSourceState State { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead, in NDIReadRequest readRequest = default)`
  - `int Seek(double positionSeconds) // returns MediaSourceNonSeekable for live input`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; } // live source returns double.NaN`

### `Input/NDIVideoSource.cs`
- `sealed class NDIVideoSource : IVideoSource, IDisposable`
- Planned API:
  - `VideoSourceState State { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadFrame(out VideoFrame frame, in NDIReadRequest readRequest = default)`
  - `int Seek(double positionSeconds) // returns MediaSourceNonSeekable for live input`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; } // live source returns double.NaN`

### `Config/NDIReadOptions.cs`
- `sealed record NDIReadOptions`
- Planned API:
  - `LiveReadTimeoutOptions TimeoutOptions { get; init; }`
  - `NDIVideoFallbackMode VideoFallbackMode { get; init; } // default: NoFrame`

### `Config/NDISourceOptions.cs`
- `sealed record NDISourceOptions`
- Planned API:
  - `NDIQueueOverflowPolicy? QueueOverflowPolicyOverride { get; init; } // null uses global fallback`
  - `NDIVideoFallbackMode? VideoFallbackModeOverride { get; init; } // null uses global fallback`
  - `TimeSpan? DiagnosticsTickIntervalOverride { get; init; } // null uses global fallback; clamped to >= 16ms`

### `Config/NDIReadRequest.cs`
- `readonly record struct NDIReadRequest`
- Planned API:
  - `TimeSpan? Timeout { get; init; } // null uses configured timeout policy`
  - `bool AllowShortDiagnosticsPause { get; init; } // default: false, best-effort and bounded by diagnostics options`

### `Config/NDIOutputOptions.cs`
- `sealed record NDIOutputOptions`
- Planned API:
  - `bool EnableVideo { get; init; } // default: true`
  - `bool EnableAudio { get; init; } // default: false`
  - `bool ValidateCapabilitiesOnStart { get; init; } // default: true`
  - `bool RequireAudioPathOnStart { get; init; } // default: false`
  - `NDIVideoSendFormat? SendFormatOverride { get; init; } // null uses integration default`

### `Output/NDIVideoOutput.cs`
- `sealed class NDIVideoOutput : IVideoOutput, IDisposable`
- Planned API:
  - `NDIOutputOptions Options { get; }`
  - `int Start()`
  - `int Stop()`
  - `int PushFrame(VideoFrame frame, TimeSpan presentationTime)`
  - `int PushAudio(in AudioFrame frame, TimeSpan presentationTime)`

### `Config/NDIIntegrationOptions.cs`
- `sealed class NDIIntegrationOptions`
- Planned API:
  - `string? RuntimeRootPath { get; init; } // consumer-provided NDI runtime root`
  - `bool UseIncomingVideoTimestamps { get; init; }`
  - `bool EnableExternalClockCorrection { get; init; } // default: false`
  - `NDIVideoSendFormat SendFormat { get; init; }`
  - `bool RequireAudioPathOnStart { get; init; }`

### `Config/NDILimitsOptions.cs`
- `sealed record NDILimitsOptions`
- Planned API:
  - `int MaxChildrenPerParent { get; init; }`
  - `int MaxPendingAudioFrames { get; init; } // clamped to >= 1`
  - `int MaxPendingVideoFrames { get; init; } // clamped to >= 1`
  - `NDIQueueOverflowPolicy QueueOverflowPolicy { get; init; } // default: DropOldest`

### `Config/NDIQueueOverflowPolicy.cs`
- `enum NDIQueueOverflowPolicy`
- Planned API:
  - `DropOldest = 0`
  - `DropNewest = 1`
  - `RejectIncoming = 2`

### `Config/NDIVideoFallbackMode.cs`
- `enum NDIVideoFallbackMode`
- Planned API:
  - `NoFrame = 0`
  - `PresentLastFrameOnRepeatedTimestamp = 1`
  - `PresentLastFrameUntilTimeout = 2`

### `Diagnostics/NDIDiagnosticsOptions.cs`
- `sealed record NDIDiagnosticsOptions`
- Planned API:
  - `bool EnableDedicatedDiagnosticsThread { get; init; } // default: true`
  - `TimeSpan DiagnosticsTickInterval { get; init; } // default: 100ms, clamped to >= 16ms`
  - `TimeSpan MaxReadPauseForDiagnostics { get; init; }`
  - `bool PublishSnapshotsOnRequestOnly { get; init; }`

### `Diagnostics/NDIAudioDiagnostics.cs`
- `readonly record struct NDIAudioDiagnostics`
- Planned API:
  - `long FramesCaptured { get; }`
  - `long FramesDropped { get; }`
  - `double LastReadMs { get; }`

### `Diagnostics/NDIVideoDiagnostics.cs`
- `readonly record struct NDIVideoDiagnostics`
- Planned API:
  - `long FramesCaptured { get; }`
  - `long FramesDropped { get; }`
  - `long RepeatedTimestampFramesPresented { get; }`
  - `double LastReadMs { get; }`

### `Diagnostics/NDIEngineDiagnostics.cs`
- `readonly record struct NDIEngineDiagnostics`
- Planned API:
  - `NDIAudioDiagnostics Audio { get; }`
  - `NDIVideoDiagnostics Video { get; }`
  - `double ClockDriftMs { get; }`
  - `DateTimeOffset CapturedAtUtc { get; }`

### `Media/NDIMediaItem.cs`
- `sealed class NDIMediaItem : IMediaItem, IDynamicMetadata`
- Planned API:
  - `NDIMediaItem(NdiDiscoveredSource source, NDIIntegrationOptions? options = null)`
  - `NDIMediaItem(NdiReceiver receiver, NDIIntegrationOptions? options = null)`
  - `IReadOnlyList<AudioStreamInfo> AudioStreams { get; }`
  - `IReadOnlyList<VideoStreamInfo> VideoStreams { get; }`
  - `MediaMetadataSnapshot? Metadata { get; }`
  - `bool HasMetadata { get; }`
  - `event EventHandler<MediaMetadataSnapshot>? MetadataUpdated`

## Notes
- Optional OpenGL interop is allowed for output integration only.
- `NDIAudioSource.ReadSamples(...)` follows safe read semantics: bounded writes, zero-fill remainder, and `framesRead` reports captured frames.
- `NDIAudioSource.ReadSamples(..., readRequest)` returns partial data with `MediaResult.Success` when samples arrive before timeout.
- `NDIAudioSource.ReadSamples(..., readRequest)` returns `MediaSourceReadTimeout` only when no samples arrive before timeout.
- `NDIVideoSource.ReadFrame(..., readRequest)` returns `MediaResult.Success` only when a new frame arrives before timeout.
- `NDIVideoSource.ReadFrame(..., readRequest)` returns `MediaSourceReadTimeout` only when no frame arrives before timeout.
- `NDIAudioSource.ReadSamples(... )` with `requestedFrameCount <= 0` returns `MediaResult.Success` with `framesRead = 0`.
- `TimeSpan.Zero` timeout is non-blocking poll; negative timeout returns `MediaInvalidArgument`.
- `Stop()` is idempotent and returns `MediaResult.Success` when already stopped.
- `Terminate()` auto-stops active source/output paths and returns `MediaResult.Success` when already terminated.
- `0` is success; all non-zero return values are failures.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Doc/error-codes.md`.
- Invalid global/per-source/output config values fail with dedicated NDI invalid-config codes.
- NDI-specific read rejection paths use dedicated NDI error codes in `5000-5199` (instead of reusing generic rejection codes).
- Video fallback applies when a fresh frame is unavailable; with `PresentLastFrameOnRepeatedTimestamp`, repeated timestamps present the previous frame generation again and publish a non-fatal warning diagnostic.
- If `PublishSnapshotsOnRequestOnly` is disabled, diagnostics snapshots are emitted on the dedicated diagnostics thread at `DiagnosticsTickInterval`.
- Queue overflow handling is configurable through `QueueOverflowPolicy`; default behavior is `DropOldest` for live-stream continuity.
- Queue limits are bounded with a safety clamp of `>= 1` pending frame.
- Queue overflow policy precedence is per-source override first, then global `QueueOverflowPolicy` fallback.
- Video fallback policy precedence is per-source override first, then global `VideoFallbackMode` fallback.
- Diagnostics tick precedence is per-source override first, then global `DiagnosticsTickInterval` fallback.
- Effective diagnostics tick uses a minimum clamp of `16ms` after override/fallback resolution; values below the minimum are clamped, not rejected.
- Safest diagnostics threading default is dedicated thread enabled; diagnostics publication stays off hot render/read threads unless explicitly relaxed.
- Diagnostics snapshots/updates and diagnostics-related callbacks are raised on the diagnostics thread.
- `AllowShortDiagnosticsPause` is opt-in and never exceeds `MaxReadPauseForDiagnostics`.
- Defaults are performance-first: bounded queues, no implicit frame synthesis, and typed diagnostics snapshots only.
- `NDIVideoOutput.PushAudio(...)` on a video-only sender returns `NDIOutputAudioStreamDisabled`.
- `NDIVideoOutput.Start()` returns `NDIOutputAudioStreamDisabled` when `RequireAudioPathOnStart` is enabled and the target sender path is video-only.
- External clock correction is opt-in through `EnableExternalClockCorrection`; timestamp-led behavior remains default.
- NDI-specific public types (`NdiDiscoveredSource`, `NdiReceiver`, `NDIVideoSendFormat`) are defined in the NDI integration layer and should not leak legacy `Seko.OwnAudioNET.*` types.

## Initial NDI Error Code Picks (`5000-5199`)
- `5000`: `NDIInitializeFailed`
- `5001`: `NDITerminateFailed`
- `5002`: `NDIReceiverCreateFailed`
- `5003`: `NDISourceStartFailed`
- `5004`: `NDISourceStopFailed`
- `5005`: `NDIAudioReadRejected`
- `5006`: `NDIVideoReadRejected`
- `5007`: `NDIVideoReadTimeout`
- `5008`: `NDIAudioReadTimeout`
- `5009`: `NDIVideoFallbackUnavailable`
- `5010`: `NDIVideoRepeatedTimestampPresented` (warning diagnostic, non-fatal)
- `5011`: `NDIOutputPushVideoFailed`
- `5012`: `NDIOutputPushAudioFailed`
- `5013`: `NDIMaxChildrenPerParentExceeded`
- `5014`: `NDIDiagnosticsThreadStartFailed`
- `5015`: `NDIDiagnosticsSnapshotUnavailable`
- `5016`: `NDIOutputAudioStreamDisabled`
- `5017`: `NDIInvalidConfig`
- `5018`: `NDIInvalidSourceOptions`
- `5019`: `NDIInvalidReadRequest`
- `5020`: `NDIInvalidOutputOptions`
- `5021`: `NDIInvalidDiagnosticsOptions`
- `5022`: `NDIInvalidLimitsOptions`
- `5023`: `NDIInvalidQueueOverflowPolicyOverride`
- `5024`: `NDIInvalidVideoFallbackOverride`
- `5025`: `NDIInvalidDiagnosticsTickOverride` (reserved for non-finite/negative tick override inputs; sub-minimum values are clamped)
- `5030-5049`: Reserved for additional NDI source/output lifecycle errors.
- `5050-5079`: Reserved for additional NDI diagnostics and timing errors.

## NDI Contract Test Matrix (Minimum)
- Lifecycle idempotency: repeated `Stop()`/`Terminate()` returns `MediaResult.Success`.
- Audio read semantics: partial-before-timeout returns success; timeout code only when no data.
- Video read semantics: success only on new frame; repeated timestamp fallback obeys configured mode.
- Override precedence: per-source override wins over global fallback for queue/fallback/tick policies.
- Diagnostics tick clamp: configured tick values below `16ms` are clamped by policy with deterministic behavior.
- Diagnostics thread affinity: diagnostics snapshots/updates and diagnostics callbacks are raised on the diagnostics thread.
- Output capability checks: `Start()` and `PushAudio(...)` return `NDIOutputAudioStreamDisabled` when audio path is unavailable and required.
- Queue clamp behavior: pending queue limits below `1` are clamped to `1` with deterministic behavior.
- Clock mode behavior: timestamp-led behavior is default; external clock correction activates only when explicitly enabled.

