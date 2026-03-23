# S.Media.FFmpeg API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `Config/FFmpegRuntimeOptions.cs`
- `sealed record FFmpegRuntimeOptions`
- Planned API:
  - `string RootPath { get; init; } // consumer-provided native runtime root`
  - `bool FailFastIfRuntimeMissing { get; init; } // default: true`

### `Config/FFmpegOpenOptions.cs`
- `sealed record FFmpegOpenOptions`
- Planned API:
  - `string InputUri { get; init; }`
  - `FFmpegRuntimeOptions Runtime { get; init; }`
  - `int? AudioStreamIndex { get; init; } // null = first usable`
  - `int? VideoStreamIndex { get; init; } // null = first usable`
  - `bool OpenAudio { get; init; } // default: true`
  - `bool OpenVideo { get; init; } // default: true`
  - `bool UseSharedDecodeContext { get; init; } // default: true`
  - `bool EnableExternalClockCorrection { get; init; } // default: false`
  - `LiveReadTimeoutOptions TimeoutOptions { get; init; }`

### `Config/FFmpegDecodeOptions.cs`
- `sealed record FFmpegDecodeOptions`
- Planned API:
  - `bool EnableHardwareDecode { get; init; }`
  - `bool LowLatencyMode { get; init; }`
  - `int MaxQueuedPackets { get; init; } // clamped to >= 1`
  - `int MaxQueuedFrames { get; init; } // clamped to >= 1`

### `Audio/FFAudioChannelMappingPolicy.cs`
- `enum FFAudioChannelMappingPolicy`
- Planned API:
  - `PreserveSourceLayout = 0`
  - `ApplyExplicitRouteMap = 1`
  - `DownmixToStereo = 2`
  - `DownmixToMono = 3`

### `Audio/FFAudioChannelMap.cs`
- `readonly record struct FFAudioChannelMap`
- Planned API:
  - `int SourceChannelCount { get; }`
  - `int DestinationChannelCount { get; }`
  - `AudioChannelRouteMap RouteMap { get; }`
  - `int Validate(out string? validationError)`
  - `static FFAudioChannelMap Identity(int channelCount)`

### `Audio/FFAudioSourceOptions.cs`
- `sealed record FFAudioSourceOptions`
- Planned API:
  - `FFAudioChannelMappingPolicy MappingPolicy { get; init; } // default: PreserveSourceLayout`
  - `FFAudioChannelMap? ExplicitChannelMap { get; init; } // required for ApplyExplicitRouteMap`
  - `int? OutputChannelCountOverride { get; init; }`

### `Runtime/FFSharedDecodeContext.cs`
- `sealed class FFSharedDecodeContext : IDisposable`
- Planned API:
  - `int Open(FFmpegOpenOptions openOptions, FFmpegDecodeOptions decodeOptions)`
  - `int Close()`
  - `bool IsOpen { get; }`
  - `int RefCount { get; }`
  - `FFStreamDescriptor? AudioStream { get; }`
  - `FFStreamDescriptor? VideoStream { get; }`

### `Runtime/FFStreamDescriptor.cs`
- `readonly record struct FFStreamDescriptor`
- Planned API:
  - `int StreamIndex { get; }`
  - `string? CodecName { get; }`
  - `TimeSpan? Duration { get; }`
  - `int? SampleRate { get; }`
  - `int? ChannelCount { get; }`
  - `int? Width { get; }`
  - `int? Height { get; }`
  - `double? FrameRate { get; }`

### `Sources/FFAudioSource.cs`
- `sealed class FFAudioSource : IAudioSource, IDisposable`
- Planned API:
  - `AudioSourceState State { get; }`
  - `AudioStreamInfo StreamInfo { get; }`
  - `FFAudioSourceOptions Options { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, TimeSpan timeout, out int framesRead)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`
  - `int TryGetEffectiveChannelMap(out FFAudioChannelMap map)`

### `Sources/FFVideoSource.cs`
- `sealed class FFVideoSource : IVideoSource, IDisposable`
- Planned API:
  - `VideoSourceState State { get; }`
  - `VideoStreamInfo StreamInfo { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadFrame(out VideoFrame frame)`
  - `int ReadFrame(out VideoFrame frame, TimeSpan timeout)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`

### `Media/FFMediaItem.cs`
- `sealed class FFMediaItem : IMediaItem, IDynamicMetadata, IDisposable`
- Planned API:
  - `FFMediaItem(FFmpegOpenOptions openOptions, FFmpegDecodeOptions? decodeOptions = null, FFAudioSourceOptions? audioOptions = null)`
  - `FFMediaItem(FFAudioSource audioSource)`
  - `FFMediaItem(FFVideoSource videoSource)`
  - `FFMediaItem(FFAudioSource audioSource, FFVideoSource videoSource)`
  - `FFAudioSource? AudioSource { get; }`
  - `FFVideoSource? VideoSource { get; }`
  - `IReadOnlyList<AudioStreamInfo> AudioStreams { get; }`
  - `IReadOnlyList<VideoStreamInfo> VideoStreams { get; }`
  - `MediaMetadataSnapshot? Metadata { get; }`
  - `bool HasMetadata { get; }`
  - `event EventHandler<MediaMetadataSnapshot>? MetadataUpdated`

### `Decoders/Internal`
- Internal-only implementation blocks using `FFmpeg.AutoGen`:
  - `FFAudioDecoder`
  - `FFVideoDecoder`
  - `FFPacketReader`
  - `FFResampler`
  - `FFPixelConverter`
  - `FFSharedDemuxSession`

## Notes
- Public API is source-first (`FFAudioSource`, `FFVideoSource`, `FFMediaItem`); session/interop types are internal.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Doc/error-codes.md`.
- `FFmpeg.AutoGen` is used directly in internal runtime/decoder layers.
- Native runtime location is consumer-provided (`FFmpegRuntimeOptions.RootPath`); missing runtime fails fast by default.
- Optional shared decode context is supported for synchronized A/V and reduced duplicate demux work.
- Shared decode context lifetime must be deterministic (`ref-count` + explicit close/dispose ownership rules).
- Timestamp-led timing is default; external clock correction is opt-in via `EnableExternalClockCorrection`.
- Invalid seek behavior follows Core: non-zero code, no clamp, no state change.
- Timeout handling is optional and policy-driven via Core `LiveReadTimeoutOptions`.
- Audio timeout reads return success when partial data is available; timeout code only when no data arrives.
- Video timeout reads return success only when a new frame arrives before deadline.
- `ReadSamples(...)` with `requestedFrameCount <= 0` returns `MediaResult.Success` with `framesRead = 0`.
- `TimeSpan.Zero` timeout is non-blocking poll; negative timeout returns `MediaInvalidArgument`.
- `Stop()` is idempotent and returns `MediaResult.Success` when already stopped.

## Audio Channel Mapping Considerations
- `PreserveSourceLayout` should be the safest default to avoid hidden channel loss.
- `ApplyExplicitRouteMap` should fail fast with a dedicated FFmpeg invalid-config code when map validation fails.
- Downmix policies (`Stereo`, `Mono`) should document coefficients and clipping/normalization behavior explicitly.
- Route mapping should happen post-decode/pre-output so one decoded frame can serve multiple output route maps.
- `TryGetEffectiveChannelMap(...)` should expose the resolved mapping for diagnostics and debugging.

## Shared Decode Context Considerations
- Use shared context when both audio and video are opened from the same input and sync is required.
- Avoid sharing when only one stream type is used or when independent seeking behavior is required.
- Cross-thread safety should be explicit: packet read/decode ownership on one worker thread; source reads consume from bounded queues.
- On source disposal, shared context should only close when last owner releases.

## Initial FFmpeg Error Code Picks (`2000-2099`)
- `2000`: `FFmpegOpenFailed`
- `2001`: `FFmpegStreamNotFound`
- `2002`: `FFmpegDecoderInitFailed`
- `2003`: `FFmpegReadFailed`
- `2004`: `FFmpegSeekFailed`
- `2005`: `FFmpegAudioDecodeFailed`
- `2006`: `FFmpegVideoDecodeFailed`
- `2007`: `FFmpegResamplerInitFailed`
- `2008`: `FFmpegResampleFailed`
- `2009`: `FFmpegPixelConversionFailed`
- `2010`: `FFmpegInvalidConfig`
- `2011`: `FFmpegInvalidAudioChannelMap`
- `2012`: `FFmpegSharedContextOpenFailed`
- `2013`: `FFmpegSharedContextDisposed`
- Reserved chunks: `2100-2199` runtime/native loading; `2200-2299` mapping/resampler/format conversion.
- Canonical range/reserve policy is tracked in `Doc/error-codes.md`.

## FFmpeg Contract Test Matrix (Minimum)
- Lifecycle idempotency: repeated `Stop()`/shared-context `Close()` returns `MediaResult.Success`.
- Runtime loading: missing/invalid `RootPath` fails fast with deterministic FFmpeg runtime/open error codes.
- Queue clamp behavior: `MaxQueuedPackets` and `MaxQueuedFrames` values below `1` clamp to `1` deterministically.
- Timeout semantics: audio partial-before-timeout returns success; video timeout success only on new frame.
- Clock mode behavior: timestamp-led default remains stable; external clock correction path activates only when explicitly enabled.
- Channel mapping validation: invalid explicit maps fail with dedicated FFmpeg config/map codes.

