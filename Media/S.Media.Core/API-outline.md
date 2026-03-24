# S.Media.Core API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `Diagnostics/DebugKeys.cs`
- `static class DebugKeys`
- Planned API:
  - `const string FramePresented = "frame.presented"`
  - `const string FrameDecoded = "frame.decoded"`
  - `const string SeekFail = "seek.fail"`
  - `const string MixerDetachSecondaryFailure = "mixer.detach.secondaryFailure"`

### `Diagnostics/DebugInfo.cs`
- `readonly record struct DebugInfo`
- Planned API:
  - `string Key { get; }`
  - `DebugValueKind ValueKind { get; }`
  - `object Value { get; }`
  - `DateTimeOffset RecordedAtUtc { get; }`

### `Errors/MediaErrorCode.cs`
- `enum MediaErrorCode`
- Planned API:
  - IDs in ranges: `0-999`, `1000-1999`, `2000-2999`, `3000-3999`, `4000-4999`, `5000-5199`
  - `0` means success for operation return codes.
  - All non-zero values are failures.
  - Shared generic/common concurrency misuse code: `950` (`MediaConcurrentOperationViolation`), used as canonical semantic mapping target and may be surfaced directly by Core/orchestration paths.
  - Reserved generic audio subrange: `4200-4299` for backend-agnostic audio contract/runtime errors.
  - Reserved output subrange: `4300-4399` for PortAudio backend errors.

### `Errors/MediaResult.cs`
- `static class MediaResult`
- Planned API:
  - `public const int Success = 0`

### `Errors/ErrorCodeRanges.cs`
- `static class ErrorCodeRanges`
- Planned API:
  - `bool IsValid(MediaErrorCode code)`
  - `bool IsSuccess(int code)`
  - `bool IsFailure(int code)`
  - `MediaErrorArea ResolveArea(MediaErrorCode code)`
  - `int ResolveSharedSemantic(int code)` // returns canonical shared semantic code when applicable (e.g., `FFmpegConcurrentReadViolation` `2014` / `NDIAudioReadRejected` `5005` / `NDIVideoReadRejected` `5006` -> `MediaConcurrentOperationViolation` `950`); returns input when no mapping exists.
  - `bool IsGenericAudioCode(int code)`
  - `bool IsPortAudioCode(int code)`

### `Errors/ErrorCodeAllocationRange.cs`
- `readonly record struct ErrorCodeAllocationRange`
- Planned API:
  - `int Start { get; }`
  - `int End { get; }`
  - `string Owner { get; }`
  - `bool Contains(int code)`

### `Errors/MediaErrorAllocations.cs`
- `static class MediaErrorAllocations`
- Planned API:
  - `ErrorCodeAllocationRange GenericCommon { get; } // 0-999`
  - `ErrorCodeAllocationRange Playback { get; } // 1000-1999`
  - `ErrorCodeAllocationRange Decoding { get; } // 2000-2999`
  - `ErrorCodeAllocationRange Mixing { get; } // 3000-3999`
  - `ErrorCodeAllocationRange OutputRender { get; } // 4000-4999`
  - `ErrorCodeAllocationRange NDI { get; } // 5000-5199`
  - `ErrorCodeAllocationRange FFmpegActive { get; } // 2000-2099`
  - `ErrorCodeAllocationRange FFmpegRuntimeReserve { get; } // 2100-2199`
  - `ErrorCodeAllocationRange FFmpegMappingReserve { get; } // 2200-2299`
  - `ErrorCodeAllocationRange MixingActive { get; } // 3000-3099`
  - `ErrorCodeAllocationRange PortAudioActive { get; } // 4300-4399`
  - `ErrorCodeAllocationRange OpenGLActive { get; } // 4400-4499`
  - `ErrorCodeAllocationRange NDIActiveNearTerm { get; } // 5000-5079`
  - `ErrorCodeAllocationRange NDIFutureReserve { get; } // 5080-5199`
  - `ErrorCodeAllocationRange MIDIReserve { get; } // 900-949`
  - `int MediaConcurrentOperationViolation { get; } // 950 (shared semantic code for same-instance concurrent operation misuse)`
  - `int MixerDetachStepFailed { get; } // 3000 (remove/clear detach-step failure when no more specific code applies)`
  - `IReadOnlyList<ErrorCodeAllocationRange> All { get; }`

### `Errors/MediaException.cs`
- `class MediaException : Exception`
- Planned API:
  - `MediaErrorCode ErrorCode { get; }`
  - `string? CorrelationId { get; }`

### `Errors/AreaExceptions.cs`
- Planned API:
  - `PlaybackException : MediaException`
  - `DecodingException : MediaException`
  - `MixingException : MediaException`
  - `OutputException : MediaException`
  - `NDIException : MediaException`

### `Media/IMediaItem.cs`
- `interface IMediaItem`
- Planned API:
  - `IReadOnlyList<AudioStreamInfo> AudioStreams { get; }`
  - `IReadOnlyList<VideoStreamInfo> VideoStreams { get; }`
  - `MediaMetadataSnapshot? Metadata { get; }`
  - `bool HasMetadata { get; }`

### `Media/IDynamicMetadata.cs`
- `interface IDynamicMetadata`
- Planned API:
  - `event EventHandler<MediaMetadataSnapshot>? MetadataUpdated`

### `Media/MediaMetadataSnapshot.cs`
- `sealed record MediaMetadataSnapshot`
- Planned API:
  - `DateTimeOffset UpdatedAtUtc { get; init; }`
  - `ReadOnlyDictionary<string, string> AdditionalMetadata { get; init; }`

### `Media/AudioStreamInfo.cs`
- `readonly record struct AudioStreamInfo`
- Planned API:
  - `string? Codec { get; init; }`
  - `int? SampleRate { get; init; }`
  - `int? ChannelCount { get; init; }`
  - `long? Bitrate { get; init; }`
  - `TimeSpan? Duration { get; init; }`

### `Media/VideoStreamInfo.cs`
- `readonly record struct VideoStreamInfo`
- Planned API:
  - `string? Codec { get; init; }`
  - `int? Width { get; init; }`
  - `int? Height { get; init; }`
  - `double? FrameRate { get; init; }`
  - `long? Bitrate { get; init; }`
  - `TimeSpan? Duration { get; init; }`

### `Clock/IMediaClock.cs`
- `interface IMediaClock`
- Planned API:
  - `double CurrentSeconds { get; }`
  - `bool IsRunning { get; }`
  - `int Start()`
  - `int Pause()`
  - `int Stop()`
  - `int Seek(double positionSeconds)`
  - Seek contract: non-finite/negative targets return `MediaInvalidArgument`; invalid seek performs no state change.

### `Clock/IExternalClock.cs`
- `interface IExternalClock`
- Planned API:
  - `double CurrentSeconds { get; }`
  - Availability contract: when explicitly configured but unavailable (for example external transport/session loss), operations must fail with `MediaExternalClockUnavailable` and must not silently fall back to `CoreMediaClock`.

### `Clock/CoreMediaClock.cs`
- `sealed class CoreMediaClock : IMediaClock`
- Planned API:
  - `double CurrentSeconds { get; }`
  - `bool IsRunning { get; }`
  - `int Start()`
  - `int Pause()`
  - `int Stop()`
  - `int Seek(double positionSeconds)`
  - Default clock implementation for mixers/player unless an external clock is explicitly configured.

## Audio Engine and Routing Contracts (Core)

### `Audio/AudioFrame.cs`
- `readonly record struct AudioFrame`
- Planned API:
  - `ReadOnlyMemory<float> Samples { get; }`
  - `int FrameCount { get; }`
  - `int SourceChannelCount { get; }`
  - `AudioFrameLayout Layout { get; } // default: Interleaved`
  - `int SampleRate { get; }`
  - `TimeSpan PresentationTime { get; }`
  - Ownership contract: caller-owned frame/sample memory is call-scoped; implementations must not retain `Samples` references beyond method return.

### `Audio/AudioEngineConfig.cs`
- `sealed record AudioEngineConfig`
- Planned API:
  - `int SampleRate { get; init; }`
  - `int OutputChannelCount { get; init; }`
  - `int FramesPerBuffer { get; init; }`
  - `AudioSampleFormat SampleFormat { get; init; }`
  - `AudioDeviceId? PreferredOutputDevice { get; init; }`
  - `bool FailOnDeviceLoss { get; init; }`

### `Audio/Channel Mapping Contract`
- Planned API shape (no dedicated route-map type in this phase):
  - `ReadOnlySpan<int> sourceChannelByOutputIndex` passed per `PushFrame(...)` call.
  - Dense mapping example: `[0, 1, 0, 1]` routes output channels `0..3` from source channels `0,1,0,1`.
  - `-1` means silence for that output channel (unmapped output is silence).
  - Values `>= 0` are 0-based source channel indices.
  - Map length must match output-channel count.
  - 1-to-many is allowed (same source index can appear multiple times).
  - many-to-1 is not representable in this direct path and is therefore not supported.
  - Invalid/missing mappings return deterministic non-zero codes (`AudioRouteMapMissing` `4200`, `AudioRouteMapInvalid` `4201`, `AudioChannelCountMismatch` `4203`, `MediaInvalidArgument` `4210` for invalid call shapes).

### `Audio/IAudioEngine.cs`
- `interface IAudioEngine : IDisposable`
- Planned API:
  - `AudioEngineState State { get; }`
  - `AudioEngineConfig Config { get; }`
  - `int Initialize(AudioEngineConfig config)`
  - `int Start()`
  - `int Stop()`
  - `IReadOnlyList<AudioDeviceInfo> GetOutputDevices()`
  - `IReadOnlyList<AudioDeviceInfo> GetInputDevices()`
  - `int CreateOutput(AudioDeviceId deviceId, out IAudioOutput? output)`
  - `int CreateOutputByName(string deviceName, out IAudioOutput? output)`
  - `int CreateOutputByIndex(int deviceIndex, out IAudioOutput? output)`
  - `IReadOnlyList<IAudioOutput> Outputs { get; }`
  - `event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged`
  - Contract note: engine owns lifecycle/discovery + created-output tracking; frame push and device switching are owned by `IAudioOutput`.

### `Audio/IAudioOutput.cs`
- `interface IAudioOutput : IDisposable`
- Planned API:
  - `AudioOutputState State { get; }`
  - `AudioDeviceInfo Device { get; }`
  - `int Start(AudioOutputConfig config)`
  - `int Stop()`
  - `int SetOutputDevice(AudioDeviceId deviceId)`
  - `int SetOutputDeviceByName(string deviceName)`
  - `int SetOutputDeviceByIndex(int deviceIndex)`
  - `event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged`
  - `int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex)`
  - `int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount)`
  - `sourceChannelByOutputIndex` validation is deterministic: empty/default -> `4200`; invalid length/index -> `4201`; invalid `sourceChannelCount` shape -> `4210`; frame/channel mismatch -> `4203`.
  - No partial push on validation failure.

### `Audio/IAudioSource.cs`
- `interface IAudioSource : IDisposable`
- Planned API:
  - `AudioSourceState State { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`
  - `requestedFrameCount <= 0` returns `MediaResult.Success` with `framesRead = 0`.

### `Video/IVideoSource.cs`
- `interface IVideoSource : IDisposable`
- Planned API:
  - `VideoSourceState State { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadFrame(out VideoFrame frame)`
  - `int Seek(double positionSeconds)`
  - `int SeekToFrame(long frameIndex)`
  - `int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`
  - `long CurrentFrameIndex { get; } // presentation index`
  - `long? CurrentDecodeFrameIndex { get; } // optional diagnostics index; null when unavailable`
  - `long? TotalFrameCount { get; } // null when unknown/live`
  - `bool IsSeekable { get; }`
  - Ownership contract: returned `VideoFrame` is caller-owned and remains valid until caller disposal (including across subsequent reads).
  - Seek contract: non-finite/negative position seeks and negative frame seeks return `MediaInvalidArgument`.
  - Invalid frame seek returns non-zero code with no state change.
  - Non-seekable/live frame seek returns `MediaSourceNonSeekable`.

### `Audio/AudioEnums.cs`
- Planned API:
  - `enum AudioEngineState`
  - `enum AudioOutputState`
  - `enum AudioSampleFormat`
  - `enum AudioFrameLayout` // `Interleaved`, `Planar`

### `Audio/AudioDeviceId.cs`
- `readonly record struct AudioDeviceId`
- Planned API:
  - `string Value { get; }`

## Routing and Mixers

### `Routing/AudioRoute.cs`
- `readonly record struct AudioRoute`

### `Routing/VideoRoute.cs`
- `readonly record struct VideoRoute`

### `Routing/ISupportsAdvancedAudioRouting.cs`
- Planned API:
  - `int AddRoute(AudioRoute route)`
  - `int RemoveRoute(AudioRoute route)`
  - `int UpdateRoute(AudioRoute route)`
  - `IReadOnlyList<AudioRoute> Routes { get; }`

### `Routing/ISupportsAdvancedVideoRouting.cs`
- Planned API:
  - `int AddRoute(VideoRoute route)`
  - `int RemoveRoute(VideoRoute route)`
  - `int UpdateRoute(VideoRoute route)`
  - `IReadOnlyList<VideoRoute> Routes { get; }`

### `Mixing/IAudioMixer.cs`
- Planned API:
  - `AudioMixerState State { get; }`
  - `AudioMixerSyncMode SyncMode { get; }`
  - `IMediaClock Clock { get; }`
  - `IExternalClock? ExternalClock { get; }`
  - `double PositionSeconds { get; }`
  - `bool IsRunning { get; }`
  - `int Start()`
  - `int Pause()`
  - `int Resume()`
  - `int Stop()`
  - `int Seek(double positionSeconds)`
  - `int AddSource(IAudioSource source)`
  - `int AddSource(IAudioSource source, double startOffsetSeconds)`
  - `int RemoveSource(IAudioSource source)`
  - `int RemoveSource(Guid sourceId)`
  - `int ClearSources()`
  - `IReadOnlyList<IAudioSource> Sources { get; }`
  - `int SourceCount { get; }`
  - `MixerSourceDetachOptions SourceDetachOptions { get; }`
  - `int SetSourceStartOffset(IAudioSource source, double startOffsetSeconds)`
  - `int ConfigureSourceDetachOptions(MixerSourceDetachOptions options)`
  - `int SetSyncMode(AudioMixerSyncMode mode)`
  - `event EventHandler<AudioMixerStateChangedEventArgs>? StateChanged`
  - `event EventHandler<AudioSourceErrorEventArgs>? SourceError`
  - `event EventHandler<AudioMixerDropoutEventArgs>? DropoutDetected`
  - Clock ownership: `Clock` defaults to `CoreMediaClock`; `ExternalClock` is opt-in and nullable.
  - External clock contract: when `ExternalClock` is configured but unavailable, return `MediaExternalClockUnavailable` with no implicit fallback.
  - Seek behavior: `Seek(...)` is immediate in current transport state (running/paused/stopped), is not deferred/queued, and returns after the coordinated state update attempt.
  - Source ownership: add/remove/clear are detach-first operations.
  - Detach behavior is policy-driven by `SourceDetachOptions` (default: detach-only, no auto-stop, no auto-dispose).
  - When `SourceDetachOptions.StopOnDetach=true`, remove/clear stop sources before detach.
  - When `SourceDetachOptions.DisposeOnDetach=true`, remove/clear dispose sources after detach (implies caller has delegated ownership).
  - Error-selection contract: remove/clear iterate sources by registration order; when multiple detach-step failures occur, return the first deterministic non-zero error code and emit diagnostics for secondary failures.
  - Detach return-code precedence: return the most specific owned/module/backend detach-step failure code when available; use `MixerDetachStepFailed` (`3000`) only as fallback.
  - Secondary-failure diagnostics payload for `DebugKeys.MixerDetachSecondaryFailure`: `operation`, `sourceId`, `step`, `errorCode`, `correlationId`, and backend/native detail when available.
  - Parity contract: `RemoveSource(...)` and `ClearSources()` use identical detach-step ordering and error selection rules.
  - Failure atomicity: failed remove/clear operations do not partially mutate source-registration state.
  - Sync contract: source start/seek operations must be deterministic and sample-stable in realtime mode within buffer granularity.

### `Mixing/AudioMixerSyncMode.cs`
- Planned API:
  - `Realtime = 0`
  - `TimelineLocked = 1`

### `Mixing/AudioMixerState.cs`
- Planned API:
  - `Stopped = 0`
  - `Running = 1`
  - `Paused = 2`

### `Mixing/AudioMixerStateChangedEventArgs.cs`
- Planned API:
  - `AudioMixerState PreviousState { get; }`
  - `AudioMixerState CurrentState { get; }`

### `Mixing/AudioSourceErrorEventArgs.cs`
- Planned API:
  - `Guid SourceId { get; }`
  - `int ErrorCode { get; }`
  - `string? Message { get; }`

### `Mixing/AudioMixerDropoutEventArgs.cs`
- Planned API:
  - `Guid SourceId { get; }`
  - `int FramesRequested { get; }`
  - `int FramesReceived { get; }`
  - `double MixerPositionSeconds { get; }`

### `Mixing/IVideoMixer.cs`
- Planned API:
  - `VideoMixerState State { get; }`
  - `VideoMixerSyncMode SyncMode { get; }`
  - `IMediaClock Clock { get; }`
  - `IExternalClock? ExternalClock { get; }`
  - `double PositionSeconds { get; }`
  - `bool IsRunning { get; }`
  - `IVideoSource? ActiveSource { get; }`
  - `int SourceCount { get; }`
  - `int Start()`
  - `int Pause()`
  - `int Resume()`
  - `int Stop()`
  - `int Seek(double positionSeconds)`
  - `int AddSource(IVideoSource source)`
  - `int RemoveSource(IVideoSource source)`
  - `int RemoveSource(Guid sourceId)`
  - `int ClearSources()`
  - `IReadOnlyList<IVideoSource> Sources { get; }`
  - `MixerSourceDetachOptions SourceDetachOptions { get; }`
  - `int SetActiveSource(IVideoSource source)`
  - `int ConfigureSourceDetachOptions(MixerSourceDetachOptions options)`
  - `int SetSyncMode(VideoMixerSyncMode mode)`
  - `event EventHandler<VideoMixerStateChangedEventArgs>? StateChanged`
  - `event EventHandler<VideoSourceErrorEventArgs>? SourceError`
  - `event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveSourceChanged`
  - Clock ownership: `Clock` defaults to `CoreMediaClock`; `ExternalClock` is opt-in and nullable.
  - External clock contract: when `ExternalClock` is configured but unavailable, return `MediaExternalClockUnavailable` with no implicit fallback.
  - Seek behavior: `Seek(...)` is immediate in current transport state (running/paused/stopped), is not deferred/queued, and returns after the coordinated state update attempt.
  - Source ownership: add/remove/clear are detach-first operations.
  - Detach behavior is policy-driven by `SourceDetachOptions` (default: detach-only, no auto-stop, no auto-dispose).
  - When `SourceDetachOptions.StopOnDetach=true`, remove/clear stop sources before detach.
  - When `SourceDetachOptions.DisposeOnDetach=true`, remove/clear dispose sources after detach (implies caller has delegated ownership).
  - Error-selection contract: remove/clear iterate sources by registration order; when multiple detach-step failures occur, return the first deterministic non-zero error code and emit diagnostics for secondary failures.
  - Detach return-code precedence: return the most specific owned/module/backend detach-step failure code when available; use `MixerDetachStepFailed` (`3000`) only as fallback.
  - Secondary-failure diagnostics payload for `DebugKeys.MixerDetachSecondaryFailure`: `operation`, `sourceId`, `step`, `errorCode`, `correlationId`, and backend/native detail when available.
  - Parity contract: `RemoveSource(...)` and `ClearSources()` use identical detach-step ordering and error selection rules.
  - Failure atomicity: failed remove/clear operations do not partially mutate source-registration state.
  - Sync contract: active-source switching and seek operations must be deterministic with no partial state transitions.

### `Mixing/VideoMixerSyncMode.cs`
- Planned API:
  - `Realtime = 0`
  - `TimelineLocked = 1`

### `Mixing/VideoMixerState.cs`
- Planned API:
  - `Stopped = 0`
  - `Running = 1`
  - `Paused = 2`

### `Mixing/VideoMixerStateChangedEventArgs.cs`
- Planned API:
  - `VideoMixerState PreviousState { get; }`
  - `VideoMixerState CurrentState { get; }`

### `Mixing/VideoSourceErrorEventArgs.cs`
- Planned API:
  - `Guid SourceId { get; }`
  - `int ErrorCode { get; }`
  - `string? Message { get; }`

### `Mixing/VideoActiveSourceChangedEventArgs.cs`
- Planned API:
  - `Guid? PreviousSourceId { get; }`
  - `Guid? CurrentSourceId { get; }`

### `Mixing/IAudioVideoMixer.cs`
- Planned API:
  - `AudioVideoMixerState State { get; }`
  - `IMediaClock Clock { get; }`
  - `IExternalClock? ExternalClock { get; }`
  - `double PositionSeconds { get; }`
  - `bool IsRunning { get; }`
  - `IAudioMixer AudioMixer { get; }`
  - `IVideoMixer VideoMixer { get; }`
  - `int Start()`
  - `int Pause()`
  - `int Resume()`
  - `int Stop()`
  - `int Seek(double positionSeconds)`
  - `int AddAudioSource(IAudioSource source)`
  - `int RemoveAudioSource(IAudioSource source)`
  - `int AddVideoSource(IVideoSource source)`
  - `int RemoveVideoSource(IVideoSource source)`
  - `IReadOnlyList<IAudioSource> AudioSources { get; }`
  - `IReadOnlyList<IVideoSource> VideoSources { get; }`
  - `MixerSourceDetachOptions AudioSourceDetachOptions { get; }`
  - `MixerSourceDetachOptions VideoSourceDetachOptions { get; }`
  - `int ConfigureAudioSourceDetachOptions(MixerSourceDetachOptions options)`
  - `int ConfigureVideoSourceDetachOptions(MixerSourceDetachOptions options)`
  - `int SetActiveVideoSource(IVideoSource source)`
  - `event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError`
  - `event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError`
  - `event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged`
  - Clock ownership: `Clock` defaults to `CoreMediaClock`; `ExternalClock` is opt-in and nullable.
  - External clock contract: when `ExternalClock` is configured but unavailable, return `MediaExternalClockUnavailable` with no implicit fallback.
  - Seek behavior: `Seek(...)` is immediate in current transport state (running/paused/stopped), is not deferred/queued, and returns after the coordinated state update attempt.
  - Source ownership: add/remove/clear are detach-first operations for both domains.
  - Detach behavior for audio/video is policy-driven independently (default: detach-only, no auto-stop, no auto-dispose).
  - Error-selection contract: remove/clear iterate sources by registration order per domain; when multiple detach-step failures occur, return the first deterministic non-zero error code and emit diagnostics for secondary failures.
  - Detach return-code precedence: return the most specific owned/module/backend detach-step failure code when available; use `MixerDetachStepFailed` (`3000`) only as fallback.
  - Secondary-failure diagnostics payload for `DebugKeys.MixerDetachSecondaryFailure`: `operation`, `sourceId`, `step`, `errorCode`, `correlationId`, and backend/native detail when available.
  - Parity contract: remove/clear operations use identical detach-step ordering and error selection rules per domain.
  - Failure atomicity: failed remove/clear operations do not partially mutate source-registration state.
  - Sync contract: coordinated seek must keep audio/video timeline deltas deterministic and bounded.

### `Mixing/AudioVideoMixerState.cs`
- Planned API:
  - `Stopped = 0`
  - `Running = 1`
  - `Paused = 2`

### `Mixing/MixerSourceDetachOptions.cs`
- `sealed record MixerSourceDetachOptions`
- Planned API:
  - `bool StopOnDetach { get; init; } // default: false`
  - `bool DisposeOnDetach { get; init; } // default: false`
  - `DisposeOnDetach=true` is only valid when caller intentionally delegates source ownership to the mixer.


### `Mixing/AudioMixer.cs`
- Default mode: `AudioLed`.

### `Mixing/VideoMixer.cs`
- Default mode: `VideoLed`.

### `Mixing/AudioVideoMixer.cs`
- Default mode: `Hybrid`.

## Player

### `Playback/IMediaPlayer.cs`
- Planned API:
  - `int Play(IMediaItem media)`
  - `int Stop()`
  - `int Pause()`
  - `int AddAudioOutput(IAudioOutput output)`
  - `int RemoveAudioOutput(IAudioOutput output)`
  - `int AddVideoOutput(IVideoOutput output)`
  - `int RemoveVideoOutput(IVideoOutput output)`
  - `IReadOnlyList<IAudioOutput> AudioOutputs { get; }`
  - `IReadOnlyList<IVideoOutput> VideoOutputs { get; }`

### `Playback/MediaPlayer.cs`
- `sealed class MediaPlayer : IMediaPlayer`

## Notes
- Core owns shared contracts and invariants; backends implement them.
- Error allocation source of truth is `Media/S.Media.Core/error-codes.md`; `MediaErrorAllocations` mirrors it 1:1 in symbols.
- Audio path is mapping-per-frame by design using a dense output-indexed source-channel map.
- Interleaved frames with `SourceChannelCount > 2` are first-class: arbitrary mappings like source `1 -> output 3` and source `2 -> output 1` are valid.
- One-to-many routing is first-class: a single source channel can fan out to multiple output channels in one push.
- Unmapped output channels (map value `-1`) are rendered as silence.
- Route/device failures are deterministic: operation returns non-zero error code and must not partially mutate engine state.
- `Stop()` operations are idempotent and should return `MediaResult.Success` when already stopped.
- Hot path logging (for example per-frame `PushFrame`) is trace-level only; higher levels should emit only aggregated/sampled failures.
- `DebugKeys.MixerDetachSecondaryFailure` is scoped to non-primary detach-step failures observed during a single remove/clear operation and is diagnostics-only (it does not change the operation return code).
- `DebugKeys.MixerDetachSecondaryFailure` payload fields are: `operation`, `sourceId`, `step`, `errorCode`, `correlationId`, and backend/native detail when available.

## Initial Generic Mixing Error Code Picks (`3000-3099`)
- `3000`: `MixerDetachStepFailed`
- Return-code precedence: when a detach sub-step has a specific backend/module failure code, return that code; use `MixerDetachStepFailed` (`3000`) only as the generic fallback when no more specific owned code applies.

## Initial Generic Audio Error Code Picks (`4200-4299`)
- `4200`: `AudioRouteMapMissing`
- `4201`: `AudioRouteMapInvalid`
- `4202`: `AudioFrameInvalid`
- `4203`: `AudioChannelCountMismatch`
- `4204`: `AudioSampleRateMismatch`
- `4205`: `AudioOutputUnavailable`
- `4206`: `AudioEngineInvalidState`
- `4207`: `AudioOperationNotSupported`
- `4208`: `MediaSourceNonSeekable`
- `4209`: `MediaSourceReadTimeout`
- `4209` reservation note: reserved for future timeout-bounded read paths where no frame/sample arrives before deadline; not used for partial-read success or argument/state validation failures.
- `4210`: `MediaInvalidArgument`
- `4211`: `MediaExternalClockUnavailable`

