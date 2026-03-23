# S.Media.Core API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `Diagnostics/DebugKeys.cs`
- `static class DebugKeys`
- Planned API:
  - `const string FramePresented = "frame.presented"`
  - `const string FrameDecoded = "frame.decoded"`
  - `const string SeekFail = "seek.fail"`

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
  - `ErrorCodeAllocationRange PortAudioActive { get; } // 4300-4399`
  - `ErrorCodeAllocationRange OpenGLActive { get; } // 4400-4499`
  - `ErrorCodeAllocationRange NDIActiveNearTerm { get; } // 5000-5079`
  - `ErrorCodeAllocationRange NDIFutureReserve { get; } // 5080-5199`
  - `ErrorCodeAllocationRange MIDIReserve { get; } // 900-949`
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

### `Timeline/FrameIndex.cs`
- `readonly record struct FrameIndex`
- Planned API:
  - `long Value { get; }`

### `Timeline/SeekContracts.cs`
- Planned API:
  - `readonly record struct SeekTarget`
  - `readonly record struct SeekResult`
  - `interface ISeekValidator`
- Contract rule:
  - Invalid seek returns non-zero error code and performs no clamping/state change.
  - Non-seekable live sources return shared code `MediaSourceNonSeekable`.
  - Live read operations may return shared timeout code `MediaSourceReadTimeout` when no frame/sample arrives before deadline.
  - `TimeSpan.Zero` timeout means non-blocking poll.
  - Negative timeout values return invalid-argument code immediately.

### `Timeline/LiveReadTimeoutOptions.cs`
- `sealed record LiveReadTimeoutOptions`
- Planned API:
  - `bool EnableTimeouts { get; init; } // optional, default false`
  - `LiveReadTimeoutMode Mode { get; init; } // Manual or ClockLatencyDerived`
  - `TimeSpan? Timeout { get; init; } // used in Manual mode`
  - `bool FailIfClockLatencyUnavailable { get; init; }`

### `Timeline/LiveReadTimeoutPolicy.cs`
- `static class LiveReadTimeoutPolicy`
- Planned API:
  - `int TryResolveTimeout(in LiveReadTimeoutOptions options, in ClockLatencySnapshot latency, out TimeSpan timeout)`
  - `TimeSpan FromAudioBuffer(int sampleRate, int framesPerBuffer) // e.g. 48k/1024 ~ 21.3 ms`

### `Timeline/LiveReadTimeoutMode.cs`
- `enum LiveReadTimeoutMode`
- Planned API:
  - `Manual = 0`
  - `ClockLatencyDerived = 1`

### `Timeline/ClockLatencySnapshot.cs`
- `readonly record struct ClockLatencySnapshot`
- Planned API:
  - `bool IsAvailable { get; }`
  - `int? SampleRate { get; }`
  - `int? FramesPerBuffer { get; }`
  - `TimeSpan? EstimatedLatency { get; }`

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

### `Audio/AudioEngineConfig.cs`
- `sealed record AudioEngineConfig`
- Planned API:
  - `int SampleRate { get; init; }`
  - `int OutputChannelCount { get; init; }`
  - `int FramesPerBuffer { get; init; }`
  - `AudioSampleFormat SampleFormat { get; init; }`
  - `AudioDeviceId? PreferredOutputDevice { get; init; }`
  - `bool FailOnDeviceLoss { get; init; }`

### `Audio/AudioChannelRoute.cs`
- `readonly record struct AudioChannelRoute`
- Planned API:
  - `int SourceChannel { get; }`
  - `int DestinationChannel { get; }`
  - `float Gain { get; }`
  - `bool IsMuted { get; }`

### `Audio/AudioChannelRouteMap.cs`
- `sealed class AudioChannelRouteMap`
- Planned API:
  - `IReadOnlyList<AudioChannelRoute> Routes { get; }`
  - `int SourceChannelCount { get; }`
  - `int DestinationChannelCount { get; }`
  - `bool AllowsOneToMany { get; }`
  - `bool IsIdentity { get; }`
  - `int Validate(out string? validationError)`
  - `static AudioChannelRouteMap IdentityMono()`
  - `static AudioChannelRouteMap IdentityStereo()`
  - `static AudioChannelRouteMap Create(int sourceChannelCount, int destinationChannelCount, IReadOnlyList<AudioChannelRoute> routes)`

### `Audio/IAudioEngine.cs`
- `interface IAudioEngine : IDisposable`
- Planned API:
  - `AudioEngineState State { get; }`
  - `AudioEngineConfig Config { get; }`
  - `int Initialize(AudioEngineConfig config)`
  - `int Start()`
  - `int Stop()`
  - `int PushFrame(in AudioFrame frame, in AudioChannelRouteMap routeMap)`
  - `int SetOutputDevice(AudioDeviceId deviceId)`
  - `IReadOnlyList<AudioDeviceInfo> GetOutputDevices()`
  - `IReadOnlyList<AudioDeviceInfo> GetInputDevices()`
  - `int SetOutputDeviceByName(string deviceName)`
  - `int SetOutputDeviceByIndex(int deviceIndex)`
  - `int SetInputDeviceByName(string deviceName)`
  - `int SetInputDeviceByIndex(int deviceIndex)`
  - `AudioDeviceId? ActiveOutputDevice { get; }`
  - `event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged`
  - `event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged`

### `Audio/IAudioOutput.cs`
- `interface IAudioOutput : IDisposable`
- Planned API:
  - `AudioOutputState State { get; }`
  - `int Start(AudioOutputConfig config)`
  - `int Stop()`
  - `int PushFrame(in AudioFrame frame, in AudioChannelRouteMap routeMap)`

### `Audio/IAudioSource.cs`
- `interface IAudioSource : IDisposable`
- Planned API:
  - `AudioSourceState State { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, TimeSpan timeout, out int framesRead)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`
  - `requestedFrameCount <= 0` returns `MediaResult.Success` with `framesRead = 0`.
  - Timeout reads return partial data with `MediaResult.Success` when any data is available before deadline.
  - Timeout reads return `MediaSourceReadTimeout` only when no data is available before deadline.
  - `TimeSpan.Zero` timeout means non-blocking poll.
  - Negative timeout returns `MediaInvalidArgument`.

### `Video/IVideoSource.cs`
- `interface IVideoSource : IDisposable`
- Planned API:
  - `VideoSourceState State { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadFrame(out VideoFrame frame)`
  - `int ReadFrame(out VideoFrame frame, TimeSpan timeout)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`
  - Timeout reads return `MediaResult.Success` only when a new frame is received before deadline.
  - Timeout reads return `MediaSourceReadTimeout` when no new frame is available before deadline.
  - `TimeSpan.Zero` timeout means non-blocking poll.
  - Negative timeout returns `MediaInvalidArgument`.

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
  - `int AddSource(IAudioSource source)`
  - `int RemoveSource(IAudioSource source)`

### `Mixing/IVideoMixer.cs`
- Planned API:
  - `int AddSource(IVideoSource source)`
  - `int RemoveSource(IVideoSource source)`

### `Mixing/IAudioVideoMixer.cs`
- Planned API:
  - Hybrid synchronization and deterministic conflict policies.

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
- Error allocation source of truth is `Doc/error-codes.md`; `MediaErrorAllocations` mirrors it 1:1 in symbols.
- Audio path is route-map-per-frame by design to support dynamic per-frame channel routing throughout the framework.
- Interleaved frames with `SourceChannelCount > 2` are first-class: arbitrary mappings like source `1 -> output 3` and source `2 -> output 1` are valid.
- One-to-many routing is first-class: a single source channel can fan out to multiple output channels in one route map.
- Route/device failures are deterministic: operation returns non-zero error code and must not partially mutate engine state.
- `Stop()` operations are idempotent and should return `MediaResult.Success` when already stopped.
- Hot path logging (for example per-frame `PushFrame`) is trace-level only; higher levels should emit only aggregated/sampled failures.

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
- `4210`: `MediaInvalidArgument`

