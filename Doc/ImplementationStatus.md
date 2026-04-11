# MFPlayer — Implementation Status

> **Last updated:** 2026-04-11 — API unification pass; pixel-format auto-detect (`VideoTargetPixelFormat = null`); `HardwareDeviceType` removed (auto-detect via `PreferHardwareDecoding`); SDL3 letterbox/pillarbox viewport on resize; NDI Yuv422p10→Rgba32 conversion via libyuv `I210ToARGB`; `LibYuvRuntime` extended with I210 delegates; `BasicPixelFormatConverter` supports Yuv422p10→RGBA/BGRA; **`VideoMixer` fan-out for co-routed sinks** (leader + sinks sharing a channel now see the same decoded frames, per-sink format conversion applied — no double-pull); `SDL3VideoOutput` sets `LeaderBypassConversion=true` for native YUV formats; `NDIVideoSink.WriteLoop` wrapped in try/catch to prevent process crash on NDI native exceptions.

---

## Implemented ✅

### `S.Media.Core` (`Media/S.Media.Core/`)

#### Clock
| File | Type | Notes |
|---|---|---|
| `Clock/IMediaClock.cs` | Interface | Position, SampleRate, IsRunning, Tick, Start/Stop/Reset |
| `Clock/MediaClockBase.cs` | Abstract | Owns `System.Timers.Timer`; fires `Tick` off the RT thread |
| `Clock/HardwareClock.cs` | Class | `Func<double>` provider; Stopwatch fallback on ≤ 0 |
| `Clock/StopwatchClock.cs` | Class | Pure software clock for offline/test/NDI |
| `Clock/VideoPtsClock.cs` | Class | PTS-driven video clock; Stopwatch interpolation between frames; `UpdateFromFrame(TimeSpan pts)` |

#### Media types
| File | Type | Notes |
|---|---|---|
| `Media/SampleType.cs` | Enum | Float32 / Int16 / Int24 / Int32 |
| `Media/PixelFormat.cs` | Enum | Bgra32, Rgba32, Nv12, Yuv420p, Uyvy422, **Yuv422p10** |
| `Video/YuvColorRange.cs` | Enum | `Auto` / `Full` / `Limited` for YUV shader normalization policy |
| `Video/YuvColorMatrix.cs` | Enum | `Auto` / `Bt601` / `Bt709` for YUV shader matrix policy |
| `Media/AudioFormat.cs` | Record struct | SampleRate, Channels, SampleType |
| `Media/VideoFormat.cs` | Record struct | Width, Height, PixelFormat, FrameRate rational |
| `Media/VideoFrame.cs` | Record struct | Width, Height, PixelFormat, Data, Pts, **`IDisposable? MemoryOwner`** (pool rental, consumer disposes) |
| `Media/IMediaOutput.cs` | Interface | Clock, IsRunning, StartAsync, StopAsync; extends `IMediaEndpoint`; default `Name` impl |
| `Media/IMediaEndpoint.cs` | Interface | Shared base: `Name`, `IsRunning`, `StartAsync`, `StopAsync`, `IDisposable` |
| `Media/ArrayPoolOwner.cs` | Class | Shared public `IDisposable` wrapper around `ArrayPool<T>` rentals; idempotent via `Interlocked.Exchange` |

#### Audio
| File | Type | Notes |
|---|---|---|
| `Audio/AudioDeviceInfo.cs` | Records | `AudioHostApiInfo`, `AudioDeviceInfo`, `HostApiType` enum |
| `Audio/IAudioEngine.cs` | Interface | Initialize/Terminate, GetHostApis/Devices, GetDefaultOutputDevice, **GetDefaultInputDevice** |
| `Audio/IAudioOutput.cs` | Interface | HardwareFormat, Open; **`Mixer` removed** — accessed on concrete type only |
| `Audio/IAudioChannel.cs` | Interface | SourceFormat, Volume, Position, push/pull |
| `Audio/AudioChannel.cs` | Class | `BoundedChannel<float[]>` ring buffer; RT-safe pull; `_framesInRing` counter for accurate `BufferAvailable` |
| `Audio/BufferUnderrunEventArgs.cs` | EventArgs | Position + FramesDropped |
| `Audio/IAudioResampler.cs` | Interface | Resample, Reset |
| `Audio/LinearResampler.cs` | Class | Linear interp; pending-tail cross-buffer continuity; zero dependencies |
| `Audio/IAudioMixer.cs` | Interface | AddChannel, RemoveChannel, FillOutputBuffer, PeakLevels; **`RouteTo`, `UnrouteTo`, `RegisterSink`, `UnregisterSink`, `DefaultFallback`**; `LeaderFormat` (replaces old `Output` back-reference) |
| `Audio/IAudioSink.cs` | Interface | ReceiveBuffer (RT-safe); StartAsync/StopAsync |
| `Audio/IAudioBufferEndpoint.cs` | Interface | Unified push endpoint contract for audio buffer consumers |
| `Audio/AudioSinkEndpointAdapter.cs` | Class | Adapter from `IAudioSink` to `IAudioBufferEndpoint` |
| `Audio/AudioEndpointSinkAdapter.cs` | Class | Adapter from `IAudioBufferEndpoint` to `IAudioSink` (inverse — used internally by `AVMixer`) |
| `Audio/AudioOutputEndpointAdapter.cs` | Class | Bridges `IAudioOutput` to `IAudioBufferEndpoint`; takes explicit `IAudioMixer` param; grow-once scratch buffer (no per-call alloc) |
| `Audio/ChannelFallback.cs` | Enum | `Silent` / `Broadcast` |
| `Audio/VirtualAudioOutput.cs` | Class | Hardware-free `IAudioOutput`; `StopwatchClock`-driven tick loop; use as clock master when all audio goes to sinks (no physical device needed) |
| `Audio/AggregateOutput.cs` | Class | Leader + N sinks; creates own `AudioMixer` + `OverrideRtMixer` (no longer requires `leader.Mixer` on interface) |
| `Audio/Routing/ChannelRoute.cs` | Record struct | SrcChannel, DstChannel, Gain |
| `Audio/Routing/ChannelRouteMap.cs` | Class | Fluent Builder; Identity/StereoFanTo/StereoExpandTo/DownmixToMono/**Silence** |

#### Mixing
| File | Type | Notes |
|---|---|---|
| `Mixing/AudioMixer.cs` | Class | `AudioFormat LeaderFormat` (decoupled from `IAudioOutput`); copy-on-write slot array; null resampler on rate match; `PrepareBuffers(int)`; per-sink `SinkTarget`/`SinkRoute`/`ChannelSlot` nested types; pull-once-scatter-N RT path; in-line sink distribution |
| `Mixing/IAVMixer.cs` | Interface | Unified AV facade over existing mixers; includes clock policy and many-to-many routing helpers |
| `Mixing/AVMixer.cs` | Class | Composition wrapper around `IAudioMixer` + `IVideoMixer`; non-breaking migration path |

#### Errors
| File | Type | Notes |
|---|---|---|
| `Errors/MediaException.cs` | Exception | Base pipeline exception |
| `Errors/AudioEngineException.cs` | Exception | Wraps native error codes |
| `Errors/BufferException.cs` | Exception | Underrun/overflow with `FramesAffected` |

#### Video
| File | Type | Notes |
|---|---|---|
| `Video/IVideoChannel.cs` | Interface | Sub-interface of `IMediaChannel<VideoFrame>`; adds `SourceFormat`, `Position` |
| `Video/IVideoOutput.cs` | Interface | `IMediaOutput` + `OutputFormat`, `Open(title, w, h, format)`; **`Mixer` removed** — accessed on concrete type only |
| `Video/IVideoMixer.cs` | Interface | `AddChannel`, `RemoveChannel`, `SetActiveChannel`, `PresentNextFrame`; multi-sink: `RegisterSink`/`UnregisterSink`/`SetActiveChannelForSink` |
| `Video/IVideoFrameEndpoint.cs` | Interface | Unified push endpoint; extends `IMediaEndpoint`; `BypassMixerConversion` (renamed from `PreferRawFramePassthrough`) |
| `Video/IVideoSinkFormatCapabilities.cs` | Interface | Ordered acceptable sink pixel formats; `BypassMixerConversion` default impl (false) |
| `Video/YuvShaderConfig.cs` | Record struct | Combined `YuvColorRange` + `YuvColorMatrix` as single config value |
| `Video/IVideoFramePullSource.cs` | Interface | Pull-oriented frame source contract |
| `Video/IVideoColorMatrixHint.cs` | Interface | Optional source hint for YUV matrix selection (`Auto`/`Bt601`/`Bt709`) and range default (`Auto`/`Full`/`Limited`) |
| `Video/IVideoSinkFormatCapabilities.cs` | Interface | Ordered acceptable sink pixel formats (fallback negotiation) |
| `Video/IPixelFormatConverter.cs` | Interface | `Convert(VideoFrame, PixelFormat)` — shaped for future use |
| `Video/IVideoSink.cs` | Interface | `ReceiveFrame` — shaped for future NDI send / recording (no impl in v1) |
| `Video/YuvAutoPolicy.cs` | Class | Shared auto-policy resolver for YUV range/matrix defaults (`Auto` -> resolved based on policy + dimensions) |
| `Video/VideoEndpointDiagnosticsSnapshot.cs` | Record struct | Standard endpoint diagnostics (`Passthrough`, `Converted`, `Dropped`, `QueueDepth`, `QueueDrops`) |
| `Video/VideoMixer.cs` | Class | Multi-sink; `LeaderBypassConversion` property; `bypassConversion` per sink; **fan-out for co-routed sinks**: sinks sharing `_activeChannel` with the leader derive their frame from `_lastFrame` (converted per-sink) rather than independently pulling from the channel — ensures leader + all sinks see identical decoded frames |
| `Video/VideoSinkEndpointAdapter.cs` | Class | `IVideoSink`→`IVideoFrameEndpoint`; `BypassMixerConversion` forwarded |
| `Video/VideoEndpointSinkAdapter.cs` | Class | `IVideoFrameEndpoint`→`IVideoSink`; used internally by `AVMixer.RegisterVideoEndpoint` |
| `Video/VideoOutputEndpointAdapter.cs` | Class | `IVideoOutput`→`IVideoFrameEndpoint`; takes explicit `IVideoMixer` param |
| `Video/VideoFramePullSource.cs` | Class | Merged pull-source replacement for `VideoMixerPullSource` + `VideoOutputPullSourceAdapter` (both deleted) |
| `Video/BufferedVideoFrameEndpoint.cs` | Class | Bounded push/pull endpoint implementation for endpoint-requested flows |
| `Video/GlShaderSources.cs` | Class | Shared GLSL source + fullscreen quad data reused by SDL3/Avalonia renderers |

---

### `S.Media.SDL3` (`Video/S.Media.SDL3/`)

| File | Type | Notes |
|---|---|---|
| `S.Media.SDL3.csproj` | Project | Refs `S.Media.Core`, `SDL3-CS`, `SDL3-CS.Native`; `AllowUnsafeBlocks` |
| `SDL3VideoOutput.cs` | Class | `IVideoOutput`; SDL init ref-counted (safe multi-instance); `YuvConfig` property (`YuvShaderConfig`); `YuvColorRange`/`YuvColorMatrix` as shortcuts; `Dispose` calls `SDL.Quit()` only when last instance; **`LeaderBypassConversion=true` for native YUV formats** (Nv12/Yuv420p/Yuv422p10 decoded by GL shader, no CPU conversion needed) |
| `GLRenderer.cs` | Class | ~30 GL functions loaded via `SDL_GL_GetProcAddress`; shared shader sources; shader paths for `Nv12`, `Yuv420p`, and `Yuv422p10` (planar 16-bit upload); shared YUV full/limited normalization + BT.601/BT.709 matrix uniforms across YUV shader paths; RGBA/BGRA texture upload path with `glTexSubImage2D` fast path; fullscreen quad VAO; **letterbox/pillarbox viewport** (`SetVideoSize`+`UpdateViewportLetterbox`) preserves aspect ratio on window resize |

---

### `S.Media.Avalonia` (`Video/S.Media.Avalonia/`)

| File | Type | Notes |
|---|---|---|
| `S.Media.Avalonia.csproj` | Project | Refs `S.Media.Core`, `Avalonia`; `AllowUnsafeBlocks` |
| `AvaloniaOpenGlVideoOutput.cs` | Class | `OpenGlControlBase` + `IVideoOutput`; embedded control output; uses `VideoMixer` + `VideoPtsClock`; calls `RequestNextFrameRendering()` while running; includes diagnostics snapshot counters |
| `AvaloniaGlRenderer.cs` | Class | Minimal GL loader via `GlInterface.GetProcAddress`; BGRA texture upload + black clear path |
| `AvaloniaOpenGlVideoCloneSink.cs` | Class | `OpenGlControlBase` + `IVideoSink`; clone/preview sink that mirrors frames without extra decoder instances |
| `README.md` | Doc | Usage notes for embedding in Avalonia visual tree |

---

### `S.Media.PortAudio` (`Audio/S.Media.PortAudio/`)

| File | Type | Notes |
|---|---|---|
| `PortAudioEngine.cs` | Class | `Pa_Initialize/Terminate`; maps PA structs to Core info records; exposes both `GetDefaultOutputDevice` and `GetDefaultInputDevice` |
| `PortAudioClock.cs` | Class | `HardwareClock` subclass; `HandleRef` box set post-Open; `UpdateTickInterval` on Open |
| `PortAudioOutput.cs` | Class | PA stream callback mode; RT callback → `AudioMixer.FillOutputBuffer`; stores `_framesPerBuffer` and calls `mixer.PrepareBuffers()` in `StartAsync` |
| `PortAudioSink.cs` | Class | `IAudioSink`; PA blocking-write stream; 8-buffer pool; background write thread |

---

### `S.Media.FFmpeg` (`Media/S.Media.FFmpeg/`)

| File | Type | Notes |
|---|---|---|
| `FFmpegLoader.cs` | Class | One-time native library init; sets `ffmpeg.RootPath` |
| `FFmpegDecoderOptions.cs` | Class | PacketQueueDepth, AudioBufferDepth, VideoBufferDepth, **DecoderThreadCount**, **PreferHardwareDecoding** (auto-detects OS-preferred device); `HardwareDeviceType` removed |
| `FFmpegDecoder.cs` | Class | `avformat_open_input`; demux thread uses `WriteAsync` for back-pressure (no silent drops); optional hw device ctx via `av_hwdevice_ctx_create`; **skips `AV_DISPOSITION_ATTACHED_PIC` streams** (e.g. FLAC cover art) to avoid spurious video channels; demux loop catches `OperationCanceledException` on graceful stop |
| `FFmpegAudioChannel.cs` | Class | `IAudioChannel`; background decode thread; `thread_count` applied to codec ctx; SWR → Float32; bounded ring; **decode loop catches `OperationCanceledException`** on graceful stop |
| `FFmpegVideoChannel.cs` | Class | **`IVideoChannel`**; background decode; hw→CPU transfer via `av_hwframe_transfer_data`; SWS pixel conversion; Yuv422p10 mapped to `AV_PIX_FMT_YUV422P10LE`; exposes `IVideoColorMatrixHint` from FFmpeg colorspace + range metadata; **`SafePts()` guards against `AV_NOPTS_VALUE` (long.MinValue) overflow**; **decode loop catches `OperationCanceledException`** on graceful stop; **`ConvertFrame` rents from `ArrayPool<byte>` via `ArrayPoolOwner<T>`; `VideoFrame.MemoryOwner` allows consumer to return rental**; `Position` via `Volatile.Read/Write` on ticks |
| `ArrayPoolOwner.cs` | Class | `internal` `IDisposable` wrapper around `ArrayPool<T>` rentals; idempotent via `Interlocked.Exchange` |
| `SwrResampler.cs` | Class | `IAudioResampler`; libswresample sinc; stateful; reinitialises on param change |

---

### `S.Media.NDI` (`NDI/S.Media.NDI/`)

| File | Type | Notes |
|---|---|---|
| `S.Media.NDI.csproj` | Project | Refs `NDILib`, `S.Media.Core`; `AllowUnsafeBlocks` |
| `NdiClock.cs` | Class | `MediaClockBase`; Stopwatch interpolates between NDI frame timestamps; `UpdateFromFrame(long)` |
| `NdiAudioChannel.cs` | Class | `IAudioChannel`; background capture via `NDIFrameSync.CaptureAudio`; FLTP→interleaved; pre-allocated `ConcurrentQueue<float[]>` pool; manual DropOldest returns buffers to pool; **`_framesInRing` Interlocked counter for accurate `BufferAvailable` frame count** |
| `NdiVideoChannel.cs` | Class | **`IVideoChannel`**; background capture; BGRA32 copy; `FreeVideo` called immediately after pixel copy; `SourceFormat` via lock; `Position` via `Volatile.Read/Write` on ticks |
| `NdiAudioSink.cs` | Class | `IAudioSink`; interleaved→planar on write thread; preset-aware pool/pending limits (`Safe`/`Balanced`/`LowLatency`); optional `IAudioResampler` (auto-creates `LinearResampler` on rate mismatch) |
| `NdiVideoSink.cs` | Class | `IVideoSink` + `IVideoSinkFormatCapabilities`; **all NDI-native formats**: BGRA32, RGBA32, NV12, UYVY422, Yuv420p (→I420); `BypassMixerConversion=true` for YUV; **Yuv422p10 normalised to Rgba32** (mixer fan-out converts via `BasicPixelFormatConverter`/libyuv I210ToABGR); `BytesPerFrame`/`LineStride`/`ToFourCC` helpers; non-blocking `StopAsync` via `Task.Run`; preset-aware pool/pending limits; **`WriteLoop` wrapped in try/catch to prevent process crash on NDI native exceptions** |
| `NdiEndpointPreset.cs` | Types | User-facing endpoint presets (`Safe`, `Balanced`, `LowLatency`) and preset options |
| `NdiSource.cs` | Class | **New** — lifecycle wrapper: creates `NDIReceiver` + `NDIFrameSync`, constructs `NdiAudioChannel` + `NdiVideoChannel`, `Start()` starts clock + capture threads, `Dispose()` tears down in order |

---

## Tests ✅

> **Total: 204 / 204 passing** (152 core + 52 FFmpeg)

### `S.Media.Core.Tests` (`Test/S.Media.Core.Tests/`) — **152 / 152 passing**


#### `AudioDeviceInfoTests.cs` — 6 tests

| Test | Covers |
|---|---|
| `ClampOutputChannels_BelowMax_ReturnsRequested` | Requested < max passes through |
| `ClampOutputChannels_AtMax_ReturnsMax` | Exact-max passes through |
| `ClampOutputChannels_AboveMax_ClampsToMax` | Over-max clamped to max |
| `ClampInputChannels_BelowMax_ReturnsRequested` | Input variant |
| `ClampInputChannels_AboveMax_ClampsToMax` | Input clamp |
| `ClampOutputChannels_JackLike256Max_AllowsAnyUpTo256` | JACK 256-port device |

#### `LinearResamplerTests.cs` — 10 tests

| Test | Covers |
|---|---|
| `Resample_SameRate_ReturnsCopy` | Pass-through copy semantics |
| `Resample_SameRate_ReturnsSamplesPerChannel` | Frame count return value |
| `Resample_Downsample2x_HalfOutputFrames` | 2:1 downsampling |
| `Resample_Upsample2x_DoubleOutputFrames` | 1:2 upsampling |
| `Resample_CrossBufferContinuity_MatchesSingleCallResult` (×3) | Cross-buffer continuity at 48→44.1, 44.1→48, 48→32 kHz |
| `Resample_Stereo_CrossBufferContinuity` | Stereo cross-buffer continuity |
| `Reset_ClearsPhaseAndPrevTail` | `Reset()` clears all state |
| `Resample_AfterDispose_Throws` | `ObjectDisposedException` guard |

#### `AudioChannelTests.cs` — 16 tests

| Test | Covers |
|---|---|
| `WriteAsync_Then_FillBuffer_ReturnsCorrectSamples` | Basic push-pull round-trip |
| `FillBuffer_AcrossChunkBoundary_ReturnsAllSamples` | Partial reads spanning two ring chunks |
| `BufferAvailable_IncrementsOnWrite_DecrementsOnPull` | Frame-accurate counter tracking |
| `BufferAvailable_IsAccurateAfterPartialPull` | Counter accuracy on cross-chunk partial pull |
| `FillBuffer_OnEmptyRing_ReturnsSilenceAndZeroFrames` | Underrun → silence + 0 frames |
| `FillBuffer_OnUnderrun_RaisesBufferUnderrunEvent` | `BufferUnderrun` event with correct `FramesDropped` |
| `Seek_ClearsRingAndResetsBufferAvailable` | Seek flushes ring; `BufferAvailable` → 0 |
| `Seek_UpdatesPosition` | Position set from seek argument |
| `Seek_ToNonZero_ThenPullNewData_PositionAdvancesFromSeekPoint` | Position = seekTarget + pulled frames after fresh write |
| `Seek_ToNonZero_OnEmptyRing_PositionDoesNotRegress` | Underrun after non-zero seek does not alter position |
| `Seek_WhilePartiallyReadingChunk_NoStaleDataAfterSeek` | Mid-chunk seek flushes partial state; new data reads clean |
| `Seek_WhilePartiallyReadingChunk_BufferAvailableIsZeroAfterSeek` | `BufferAvailable` correct after mid-chunk seek |
| `TryWrite_ReturnsFalse_WhenRingFull` | Back-pressure on full ring |
| `TryWrite_IncrementsBufferAvailable` | Counter incremented by `TryWrite` |
| `WriteAsync_AfterDispose_Throws` | `ObjectDisposedException` guard |
| `Position_AdvancesWithPulledFrames` | Position advances by pulled frame count |

#### `ChannelRouteMapTests.cs` — 18 tests

| Test | Covers |
|---|---|
| `Identity_CreatesOneRoutePerChannel` (×3) | 1-, 2-, 6-channel identity maps |
| `Identity_BakeRoutes_EachSrcMapsToSameDst` | Baked table for identity |
| `StereoFanTo_Creates4Routes` | Route count |
| `StereoFanTo_LeftChannelFansToTwoDsts` | L → two destinations |
| `StereoFanTo_RightChannelFansToTwoDsts` | R → two destinations |
| `StereoExpandTo_Creates4Routes` | Route count |
| `StereoExpandTo_LeftMapsToBase0And1` | L → base+0, base+1 |
| `StereoExpandTo_RightMapsToBase2And3` | R → base+2, base+3 |
| `StereoExpandTo_BaseChannel2_LeftMapsTo2And3` | Non-zero base channel |
| `DownmixToMono_CreatesOneRoutePerSrcChannel` | Route count for 4-ch downmix |
| `DownmixToMono_GainAppliedToAllRoutes` | Per-route gain |
| `Builder_AddsRoutesInOrder` | Fluent builder ordering and gain |
| `Builder_DefaultGainIsOne` | Default gain value |
| `BakeRoutes_IgnoresOutOfRangeSrcChannel` | Robustness on invalid src index |
| `BakeRoutes_FanIn_MultipleSrcsToSameDst` | Fan-in topology |
| `BakeRoutes_FanOut_OneSrcToMultipleDsts` | Fan-out topology |

#### `AudioMixerTests.cs` — 16 tests

| Test | Covers |
|---|---|
| `FillOutputBuffer_NoChannels_OutputsAllZeros` | Empty mixer → silence |
| `FillOutputBuffer_SingleMonoChannel_RoutedToStereo` | Mono→stereo routing |
| `FillOutputBuffer_TwoChannelsSameRate_SumsCorrectly` | Additive mixing |
| `ChannelCount_ReflectsAddAndRemove` | Channel count property |
| `RemoveChannel_StopsChannelFromBeingPulled` | Remove takes effect immediately |
| `RemoveChannel_NonExistent_DoesNotThrow` | Robustness |
| `MasterVolume_ScalesOutput` | Master gain applied |
| `MasterVolume_ClampedToZeroMinimum` | Negative gain clamped to 0 |
| `ChannelVolume_ScalesChannelOutput` | Per-channel gain |
| `PeakLevels_UpdatedAfterFill` | Peak meter per output channel |
| `PeakLevels_ZeroWhenNoChannels` | Initial state |
| `AddChannel_SameRate_NoResamplerAllocated_OutputCorrect` | Null-resampler direct-copy path |
| `PrepareBuffers_AllowsFillOutputBuffer_WithNoFallbackAlloc` | Pre-allocation before fill |
| `PrepareBuffers_AfterAddChannel_AllocatesSlotBuffers` | Pre-allocation after add |
| `AddChannel_DifferentRate_AutoCreatesResampler_OutputNotSilent` | Auto-LinearResampler on rate mismatch |
| `AddChannel_AfterDispose_Throws` | `ObjectDisposedException` guard |

#### `AggregateOutputTests.cs` — 19 tests

| Test | Covers |
|---|---|
| `AddSink_AppearsinSinks` | Sink added to list |
| `AddSink_Multiple_AllAppear` | Multiple sinks tracked |
| `RemoveSink_RemovesSinkFromList` | Sink removed |
| `RemoveSink_NonExistent_DoesNotThrow` | Robustness |
| `RemoveSink_RemovesCorrectSink_WhenMultiplePresent` | Selective removal |
| `FillOutputBuffer_DistributesToRunningSink` | Fan-out to running sink |
| `FillOutputBuffer_DoesNotDistributeToStoppedSink` | Non-running sinks skipped |
| `FillOutputBuffer_DistributesToAllRunningSinks` | Fan-out to multiple sinks |
| `FillOutputBuffer_SinkReceivesCorrectBuffer` | Correct audio data via explicit `RouteTo` |
| `RemoveSink_AtRuntime_NoLongerReceivesBuffers` | Dynamic removal during playback |
| `Mixer_ChannelCount_DelegatestoInner` | Mixer delegation |
| `HardwareFormat_DelegatestoLeader` | Format delegation |
| `Silent_SinkReceivesZeroBuffer_WhenNoRouteConfigured` | Silent fallback: no route → zero buffer |
| `RouteTo_SinkReceivesChannelData` | Explicit route delivers channel audio |
| `UnrouteTo_SinkReceivesSilenceAfterRemoval` | Route removed → sink goes silent next tick |
| `RouteTo_TwoSinks_IndependentMixes` | Independent per-sink mixes (one routed, one silent) |
| `Broadcast_SinkReceivesLeaderMix_WithoutExplicitRoute` | `Broadcast` fallback: no route needed |
| `RouteTo_UnregisteredSink_Throws` | `InvalidOperationException` on unregistered sink |
| `AudioMixer_DefaultFallback_IsSilentByDefault` | `DefaultFallback` is `Silent` by default |

---

### `S.Media.FFmpeg.Tests` (`Test/S.Media.FFmpeg.Tests/`) — **52 / 52 passing**

Both pure-C# and FFmpeg-native tests. The `FfmpegFixture` collection fixture loads FFmpeg libraries once for all integration tests.

#### `ArrayPoolOwnerTests.cs` — 4 tests

| Test | Covers |
|---|---|
| `Dispose_ReturnsArrayToPool` | Rental returned without exception |
| `Dispose_IsIdempotent_NoDoubleReturn` | Second Dispose is no-op |
| `Dispose_OnCopiedStruct_SecondDisposeSafe` | Struct-copy scenario — only first Dispose returns |
| `Dispose_EmptyArray_DoesNotThrow` | Edge case: minimal array |

#### `FFmpegDecoderOptionsTests.cs` — 6 tests

| Test | Covers |
|---|---|
| `Defaults_PacketQueueDepth_Is64` | Default value |
| `Defaults_AudioBufferDepth_Is16` | Default value |
| `Defaults_VideoBufferDepth_Is4` | Default value |
| `Defaults_DecoderThreadCount_IsZero` | FFmpeg auto-detect default |
| `Defaults_PreferHardwareDecoding_IsTrue` | Auto hw-decode enabled by default |
| `Init_OverridesAllDefaults` | All fields can be overridden |

#### `SafePtsTests.cs` — 10 tests

| Test | Covers |
|---|---|
| `SafePts_AVNoptsValue_ReturnsZero` | `long.MinValue` (AV_NOPTS_VALUE) → zero |
| `SafePts_ZeroTimebase_ReturnsZero` | Zero timebase → zero |
| `SafePts_NegativeTimebase_ReturnsZero` | Negative timebase → zero |
| `SafePts_InfiniteTimebase_ReturnsZero` | `+∞` timebase → zero |
| `SafePts_ValidPts_ReturnsCorrectTimeSpan` | 90000 ticks @ 1/90000 = 1 s |
| `SafePts_ZeroPts_ReturnsZero` | PTS 0 → TimeSpan.Zero |
| `SafePts_NegativePts_ReturnsZero` | Negative PTS (pre-roll) → zero |
| `SafePts_HugeValue_ClampsToMaxValue` | Overflow clamps to `TimeSpan.MaxValue` |
| `SafePts_HalfSecond_VideoTimebase` | 12500 @ 1/25000 = 0.5 s |
| `SafePts_TypicalAudioPts_AudioTimebase` | 48000 @ 1/48000 = 1 s |

#### `SwrResamplerTests.cs` — 7 tests *(FFmpeg integration)*

| Test | Covers |
|---|---|
| `Resample_SameRate_OutputSampleCountMatches` | Pass-through frame count |
| `Resample_SameRate_OutputDataIsClose` | FLT→FLT same-rate fidelity |
| `Resample_Downsample2x_HalfOutputFrames` | 48k→24k sinc with latency tolerance |
| `Resample_Upsample2x_DoubleOutputFrames` | 24k→48k sinc |
| `Reset_DoesNotThrow` | `Reset()` is safe after use |
| `Dispose_Then_Resample_ThrowsObjectDisposedException` | Dispose guard |
| `Resample_ParameterChange_ReinitAndProducesOutput` | Auto-reinit on param change |

#### `FFmpegDecoderTests.cs` — 10 tests *(FFmpeg integration)*

| Test | Covers |
|---|---|
| `Open_InvalidPath_ThrowsInvalidOperationException` | Error handling |
| `Open_ValidWav_ReturnsOneAudioChannel` | WAV → 1 audio channel |
| `Open_ValidWav_NoVideoChannels` | No spurious video channels |
| `AudioChannel_SourceFormat_MatchesWavHeader_StereoAt48k` | Format detection 48k/2ch |
| `AudioChannel_SourceFormat_MatchesWavHeader_MonoAt44k` | Format detection 44.1k/1ch |
| `Start_ThenFillBuffer_ProducesNonSilentOutput` | Decode + pull round-trip |
| `Start_ThenDispose_DoesNotThrow` | Clean shutdown during decode |
| `Open_WithOptions_CustomBufferDepth_Applied` | `AudioBufferDepth` propagates |
| `Open_WithOptions_SingleThread_Applied` | Single-threaded decode option |
| `VideoFrame_WithMemoryOwner_CanBeDisposed` | `ArrayPoolOwner` integration |

---

### `JackLib` (`Audio/JackLib/`)

| File | Type | Notes |
|---|---|---|
| `JackLib.csproj` | Project | `AllowUnsafeBlocks`; `InternalsVisibleTo S.Media.PortAudio` |
| `Runtime/JackLibraryNames.cs` | Class | Library name constant (`libjack`) |
| `Types/JackTypes.cs` | Types | `JackOptions`, `JackStatus`, `JackPortFlags` enums; delegate types; `JackPortType` constants |
| `Native.cs` | Class | `internal` raw P/Invoke: client open/close/activate, callbacks, port register/unregister/get-buffer, connect/disconnect, get_ports, time functions |
| `JackClient.cs` | Class | `public` RAII wrapper; manages callback delegate GC roots; `AutoConnectToPhysicalOutputs()` helper |

---

## Pending design decisions 🔷

All audio pipeline decisions (Q1–Q19) resolved.  
All video pipeline design questions (§13.1–§13.8) resolved — see `VideoPlaybackArchitecture.md §12`.

---

## Test Apps

| App | Location | Notes |
|---|---|---|
| `MFPlayer.SimplePlayer` | `Test/MFPlayer.SimplePlayer/` | Audio-only; PortAudio host/device selection; FFmpeg decode; EOF via underrun |
| `MFPlayer.MultiOutputPlayer` | `Test/MFPlayer.MultiOutputPlayer/` | Multi-output audio with AggregateOutput + sinks |
| `MFPlayer.NDIPlayer` | `Test/MFPlayer.NDIPlayer/` | NDI source receive + audio playback |
| `MFPlayer.NDISender` | `Test/MFPlayer.NDISender/` | NDI audio send |
| `MFPlayer.VideoPlayer` | `Test/MFPlayer.VideoPlayer/` | **New** — Video-only; FFmpeg decode → SDL3VideoOutput; window close / Ctrl+C / Enter to stop |
| `MFPlayer.VideoMultiOutputPlayer` | `Test/MFPlayer.VideoMultiOutputPlayer/` | **New** — Video multi-output; SDL3 leader + optional `NDIVideoSink` secondary target; one input channel routed to multiple targets |
| `MFPlayer.AvaloniaVideoPlayer` | `Test/MFPlayer.AvaloniaVideoPlayer/` | **New** — Avalonia desktop sample; FFmpeg video-only decode → embedded `AvaloniaOpenGlVideoOutput` |
| `AvaloniaOpenGlVideoOutput` docs | `Video/S.Media.Avalonia/README.md` | Usage notes for embedding the control into custom Avalonia windows/views |

---


## Missing / TODO 🔲

### Tests — additional coverage needed

| Project | Priority | Notes |
|---|---|---|
| `S.Media.PortAudio.Tests` | Medium | Smoke tests (requires audio hardware or virtual device) |
| `S.Media.NDI.Tests` | Low | Requires NDI runtime |
| `SDL3VideoMixer.Tests` | Medium | VideoMixer unit tests (channel add/remove, active channel, frame pull) |

### Technical debt

| Item | Notes |
|---|---|
| `GetDefaultInputDevice` on NDI | `IAudioEngine` now declares `GetDefaultInputDevice()`; NDI does not model input devices the same way (sources are discovered via `NDIFinder`, not a device list). NDI input enumeration is handled by `NdiSource.Open` — no NDI-specific `IAudioEngine` implementation is needed. |

---

## Resolved design decisions (Q1–Q19)

| # | Question | Resolution |
|---|---|---|
| Q1 | `PixelFormat` coverage | Added `Rgba32` (already present) and `Yuv422p10`. Mapped to `AV_PIX_FMT_YUV422P10LE` in `FFmpegVideoChannel`. |
| Q2 | NDI lifecycle wrapper | Added `NdiSource` + `NdiSourceOptions` (analogous to `FFmpegDecoder`). |
| Q3 | Dead `TryWrite` in `FlushAfterSeek` | Removed. `FlushAfterSeek` now only calls `avcodec_flush_buffers` + `Seek(TimeSpan.Zero)`. |
| Q4 | `LinearResampler` cross-buffer bug | Confirmed by tests (4 failures). Fixed: unconsumed tail frames saved to `_pendingBuf`, prepended to next call. All 10 tests now pass. |
| Q5 | `BufferAvailable` units | `AudioChannel` tracks `_framesInRing` (actual frame count) via `Interlocked` on write, pull, and seek. `NdiAudioChannel` now does the same — `_framesInRing` incremented per captured frame count, decremented on DropOldest and on `FillBuffer` success. |
| Q6 | Resampler when rates match | `AudioMixer.AddChannel` leaves `Resampler = null` when rates match; hot path does direct `Span.CopyTo` instead of calling `Resample`. No `LinearResampler` allocated. |
| Q7 | RT-thread allocation in mixer | `PrepareBuffers(int framesPerBuffer)` pre-allocates all per-slot scratch buffers. Called by `PortAudioOutput.StartAsync` before stream opens. |
| Q8 | Silent packet drops + hw decode | Demux loop uses `WriteAsync` (back-pressure). `FFmpegDecoderOptions` adds `DecoderThreadCount` and `PreferHardwareDecoding` (auto-detects OS-preferred device type — VAAPI on Linux, D3D11VA on Windows, VideoToolbox on macOS). `HardwareDeviceType` string property removed. |
| Q9 | NDI per-frame allocation | `NdiAudioChannel` uses a `ConcurrentQueue<float[]>` pool pre-allocated at construction. `FreeAudio` called immediately after copy. Consumed buffers returned to pool in `FillBuffer`. |
| Q10 | `GetDefaultInputDevice` on `IAudioEngine` | Added to `IAudioEngine` interface and implemented in `PortAudioEngine`. NDI sources are enumerated via `NDIFinder` (not `IAudioEngine`); no NDI implementation needed. |
| Q11 | `FFmpegVideoChannel` per-frame allocation | `ConvertFrame` rents from `ArrayPool<byte>.Shared` via `ArrayPoolOwner<byte>`; the rental is attached to `VideoFrame.MemoryOwner`. Consumer calls `frame.MemoryOwner?.Dispose()` to return the buffer. Zero heap allocs per frame on the hot path. |
| Q12 | `NdiAudioSink` resampler support | `NdiAudioSink` accepts optional `IAudioResampler?`; auto-creates `LinearResampler` when `null` and rates differ; applies in `ReceiveBuffer`; disposes if owned. |
| Q13 | `IAudioOutput` vs `IAudioSink` merge | Keep separate. `IAudioOutput` owns clock (RT callback); `IAudioSink` is clock-follower (blocking write). `AggregateOutput` provides fan-out: 1 leader + N sinks. |
| Q14 | Channel count control per device | `AudioDeviceInfo` already has `MaxOutputChannels`/`MaxInputChannels`. Added `ClampOutputChannels(int)` and `ClampInputChannels(int)` helpers. `Open()` uses requested channels as-is; callers clamp before calling. |
| Q15 | JACK-specific features | Created `JackLib` P/Invoke wrapper project (`Audio/JackLib/`). `JackClient.AutoConnectToPhysicalOutputs()` handles JACK port autoconnect. Port count controlled via `requestedFormat.Channels` + `ClampOutputChannels`. |
| Q16 | Default fallback for unrouted sinks | `ChannelFallback.Silent` is the default (configurable on `AudioMixer` construction). Channels not explicitly `RouteTo`-d to a sink produce silence on that sink. |
| Q17 | API shape for per-sink routing | **Option C** — separate `RouteTo(channelId, sink, routeMap)` / `UnrouteTo(channelId, sink)` calls on `IAudioMixer` after `AddChannel`. Enables dynamic re-routing at runtime without re-adding the channel. |
| Q18 | Channel data sharing across targets | Fixed constraint: channel `FillBuffer` called **once** per tick, result cached in `slot.ResampleBuf`, then scattered into N+1 mix buffers (leader + each sink). No independent per-sink pulls. |
| Q19 | Per-sink sample rate conversion | Sinks receive audio at **leader sample rate** (the rate used to resample all source channels). Sinks that require a different rate should apply their own internal resampler (e.g. `NdiAudioSink` already does this via optional `IAudioResampler`). No per-`(channel, sink)` resampler path added to `AudioMixer`. |
