# S.Media.FFmpeg API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `Config/FFmpegOpenOptions.cs`
- `sealed record FFmpegOpenOptions`
- Planned API:
  - `string? InputUri { get; init; } // exclusive with InputStream`
  - `Stream? InputStream { get; init; } // exclusive with InputUri`
  - `bool LeaveInputStreamOpen { get; init; } // default: true`
  - `string? InputFormatHint { get; init; } // optional demux hint for stream inputs`
  - `int? AudioStreamIndex { get; init; } // null = first usable`
  - `int? VideoStreamIndex { get; init; } // null = first usable`
  - `bool OpenAudio { get; init; } // default: true`
  - `bool OpenVideo { get; init; } // default: true`
  - `bool UseSharedDecodeContext { get; init; } // default: true`
  - `bool EnableExternalClockCorrection { get; init; } // default: false`

### `Config/FFmpegDecodeOptions.cs`
- `sealed record FFmpegDecodeOptions`
- Planned API:
  - `bool EnableHardwareDecode { get; init; }`
  - `bool LowLatencyMode { get; init; }`
  - `int DecodeThreadCount { get; init; } // 0 = ffmpeg auto, 1 = single-thread, >1 explicit thread count (clamped to <= logical CPU count)`
  - `bool UseDedicatedDecodeThread { get; init; } // default: true`
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
  - `IReadOnlyList<int> SourceChannelByOutputIndex { get; } // -1 = silence, >=0 = source channel index`
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
  - `FFAudioSource(FFMediaItem mediaItem)`
  - `AudioSourceState State { get; }`
  - `AudioStreamInfo StreamInfo { get; }`
  - `FFAudioSourceOptions Options { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`
  - `int TryGetEffectiveChannelMap(out FFAudioChannelMap map)`

### `Sources/FFVideoSource.cs`
- `sealed class FFVideoSource : IVideoSource, IDisposable`
- Planned API:
  - `FFVideoSource(FFMediaItem mediaItem)`
  - `VideoSourceState State { get; }`
  - `VideoStreamInfo StreamInfo { get; }`
  - `int Start()`
  - `int Stop()`
  - `int ReadFrame(out VideoFrame frame)`
  - `int Seek(double positionSeconds)`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; }`

### `Media/FFMediaItem.cs`
- `sealed class FFMediaItem : IMediaItem, IDynamicMetadata, IDisposable`
- Planned API:
  - `FFMediaItem(FFmpegOpenOptions openOptions, FFmpegDecodeOptions? decodeOptions = null, FFAudioSourceOptions? audioOptions = null)`
  - `FFMediaItem(Stream inputStream, bool leaveInputStreamOpen = true, string? inputFormatHint = null, FFmpegDecodeOptions? decodeOptions = null, FFAudioSourceOptions? audioOptions = null)`
  - `FFMediaItem(Stream inputStream, FFmpegOpenOptions openOptions, FFmpegDecodeOptions? decodeOptions = null, FFAudioSourceOptions? audioOptions = null) // openOptions stream fields must be unset`
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
- Migration implementation matrix/source mapping: `Media/S.Media.FFmpeg/ffmpeg-migration-plan.md`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Media/S.Media.Core/error-codes.md`.
- For Core mixer detach/remove/clear orchestration, FFmpeg-specific failure codes remain authoritative when available; Core fallback `MixerDetachStepFailed` (`3000`) applies only when no more specific owned code exists.
- `FFmpeg.AutoGen` is used directly in internal runtime/decoder layers.
- Native runtime bootstrap is consumer-owned via `FFmpeg.AutoGen` init/runtime APIs before creating FFmpeg sources/media items.
- FFmpeg public operations follow int-first contracts (`0` success, non-zero failure); invalid state/config paths return deterministic error codes.
- Optional shared decode context is supported for synchronized A/V and reduced duplicate demux work.
- Shared decode context lifetime must be deterministic (`ref-count` + explicit close/dispose ownership rules).
- Decode threading is configurable; heavy formats may require `DecodeThreadCount > 1` for smooth realtime throughput.
- Decode thread-count clamping is deterministic: values below `0` return invalid-argument/config failure; values above logical CPU count clamp to logical CPU count.
- Input source is mutually exclusive: exactly one of `InputUri` or `InputStream` must be provided.
- Stream-open ownership is explicit: when `InputStream` is used, disposal honors `LeaveInputStreamOpen`.
- `InputStream` must be readable; invalid stream capability paths return deterministic config/argument failures.
- `FFMediaItem` stream overloads are convenience wrappers over open-options stream paths and follow the same exclusivity/ownership rules.
- Timestamp-led timing is default; external clock correction is opt-in via `EnableExternalClockCorrection`.
- Nonsensical open/config combinations return `FFmpegInvalidConfig` (`2010`) with no partial open side effects.
- Invalid seek behavior follows Core: non-zero code, no clamp, no state change.
- Stream seek behavior follows Core: seek on non-seekable `InputStream` returns `MediaSourceNonSeekable` with no state change.
- `ReadSamples(...)` with `requestedFrameCount <= 0` returns `MediaResult.Success` with `framesRead = 0`.
- `ReadSamples(...)` never stores caller `Span<float>` references; destination memory remains fully caller-owned.
- `ReadFrame(...)` returns a caller-owned `VideoFrame`; caller is responsible for disposing the returned frame.
- Callback/event dispatch policy is fixed in this phase (no module-level callback-dispatch configuration surface).
- Future evolution note: if callback latency becomes a verified issue, add a minimal dispatcher later without breaking `MetadataUpdated` ordering or teardown-fence guarantees.
- `DurationSeconds` semantics are explicit: finite non-negative value for known-length media; `double.NaN` for unknown/unbounded live duration.
- Concurrent `ReadSamples(...)` or `ReadFrame(...)` calls on the same source return `FFmpegConcurrentReadViolation` (`2014`).
- Audio read efficiency contract: callers should reuse destination buffers between reads; implementation must not retain caller-owned buffers.
- Video read efficiency contract: implementation may reuse internal decode/convert buffers, while each returned `VideoFrame` remains valid until caller disposal (including across subsequent `ReadFrame(...)` calls).
- `Stop()` is idempotent and returns `MediaResult.Success` when already stopped.
- Failure atomicity: failed stop/dispose/detach-facing paths must not leave partially detached or partially running FFmpeg source state.

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
- Source instances are single-reader by contract (`ReadSamples`/`ReadFrame` must not be called concurrently on the same instance).
- Control operations (`Start`/`Stop`/`Seek`/`Dispose`) are serialized per source/shared-context instance.

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
- `2014`: `FFmpegConcurrentReadViolation`
- `2010` is the canonical error for invalid/nonsensical option combinations (for example, both `OpenAudio` and `OpenVideo` disabled, or stream index specified for a disabled stream type).
- `2014` is the canonical single-reader violation code (same-instance concurrent read attempt).
- `FFmpegConcurrentReadViolation` (`2014`) maps to shared Core semantic `MediaConcurrentOperationViolation` (`950`).
- Reserved chunks: `2100-2199` runtime/native loading; `2200-2299` mapping/resampler/format conversion.
- Canonical range/reserve policy is tracked in `Media/S.Media.Core/error-codes.md`.

## FFmpeg Contract Test Matrix (Minimum)
- Lifecycle idempotency: repeated `Stop()`/shared-context `Close()` returns `MediaResult.Success`.
- Runtime bootstrap: source/media-item construction fails deterministically when consumer `FFmpeg.AutoGen` runtime init is missing/invalid.
- Input-source selection: both-set (`InputUri` + `InputStream`) and neither-set paths fail with `FFmpegInvalidConfig` (`2010`).
- Stream capability checks: unreadable stream fails open deterministically; non-seekable stream seek returns `MediaSourceNonSeekable`.
- Stream ownership checks: `LeaveInputStreamOpen=true` keeps caller stream open after dispose; `false` closes it.
- Queue clamp behavior: `MaxQueuedPackets` and `MaxQueuedFrames` values below `1` clamp to `1` deterministically.
- Decode-thread behavior: `DecodeThreadCount=0` uses FFmpeg auto selection; explicit values and clamping behavior must be applied deterministically.
- Duration semantics: finite non-negative `DurationSeconds` for known-length media; `double.NaN` for unknown/unbounded/live sources.
- Failure atomicity: invalid open/start paths must not leave partially-open state (no active handles/workers after failure return).
- Clock mode behavior: timestamp-led default remains stable; external clock correction path activates only when explicitly enabled.
- Channel mapping validation: invalid explicit maps fail with dedicated FFmpeg config/map codes.
- Invalid config matrix: deterministic codes for nonsensical combinations with no partial-open side effects.

| Case | OpenAudio | OpenVideo | AudioStreamIndex | VideoStreamIndex | MappingPolicy / Map | Expected result |
| --- | --- | --- | --- | --- | --- | --- |
| Both stream types disabled | `false` | `false` | `null` | `null` | `PreserveSourceLayout` | `FFmpegInvalidConfig` (`2010`) |
| Audio disabled with explicit audio index | `false` | `true` | `0` | `null` | `PreserveSourceLayout` | `FFmpegInvalidConfig` (`2010`) |
| Video disabled with explicit video index | `true` | `false` | `null` | `0` | `PreserveSourceLayout` | `FFmpegInvalidConfig` (`2010`) |
| Explicit route-map policy without map | `true` | `false` | `null` | `null` | `ApplyExplicitRouteMap` + `ExplicitChannelMap = null` | `FFmpegInvalidAudioChannelMap` (`2011`) |
| Nonsensical mixed open/config combo | any | any | any | any | contradictory option combination | `FFmpegInvalidConfig` (`2010`) |

- Ownership checks: frame/sample read APIs do not retain caller buffers and enforce caller-owned `VideoFrame` disposal.
- Thread-safety checks: concurrent read calls on the same source are rejected with `FFmpegConcurrentReadViolation` (`2014`) and classify to shared semantic `MediaConcurrentOperationViolation` (`950`).
- Queue saturation behavior: bounded decode queues apply deterministic backpressure behavior (no unbounded growth).
- Metadata event teardown fence: `FFMediaItem.MetadataUpdated` is not raised after successful `FFMediaItem.Dispose()` completion.
- Media-item construction paths: constructing `FFAudioSource`/`FFVideoSource` from `FFMediaItem` must preserve deterministic ownership and no-partial-open guarantees.

