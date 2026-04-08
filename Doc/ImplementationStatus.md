# MFPlayer — Implementation Status

> **Last updated:** 2026-04-08 (Q1–Q19 resolved; video architecture §13.1–§13.8 resolved; `IAudioMixer.Output` → `LeaderFormat`; `VirtualAudioOutput` + `ChannelRouteMap.Silence()` added; 122 tests passing)

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

#### Media types
| File | Type | Notes |
|---|---|---|
| `Media/SampleType.cs` | Enum | Float32 / Int16 / Int24 / Int32 |
| `Media/PixelFormat.cs` | Enum | Bgra32, Rgba32, Nv12, Yuv420p, Uyvy422, **Yuv422p10** |
| `Media/AudioFormat.cs` | Record struct | SampleRate, Channels, SampleType |
| `Media/VideoFormat.cs` | Record struct | Width, Height, PixelFormat, FrameRate rational |
| `Media/VideoFrame.cs` | Record struct | Width, Height, PixelFormat, Data, Pts, **`IDisposable? MemoryOwner`** (pool rental, consumer disposes) |
| `Media/IMediaOutput.cs` | Interface | Clock, IsRunning, StartAsync, StopAsync |
| `Media/IMediaChannel.cs` | Interface | Id, IsOpen, FillBuffer, CanSeek, Seek |

#### Audio
| File | Type | Notes |
|---|---|---|
| `Audio/AudioDeviceInfo.cs` | Records | `AudioHostApiInfo`, `AudioDeviceInfo`, `HostApiType` enum |
| `Audio/IAudioEngine.cs` | Interface | Initialize/Terminate, GetHostApis/Devices, GetDefaultOutputDevice, **GetDefaultInputDevice** |
| `Audio/IAudioOutput.cs` | Interface | HardwareFormat, Mixer, Open |
| `Audio/IAudioChannel.cs` | Interface | SourceFormat, Volume, Position, push/pull |
| `Audio/AudioChannel.cs` | Class | `BoundedChannel<float[]>` ring buffer; RT-safe pull; `_framesInRing` counter for accurate `BufferAvailable` |
| `Audio/BufferUnderrunEventArgs.cs` | EventArgs | Position + FramesDropped |
| `Audio/IAudioResampler.cs` | Interface | Resample, Reset |
| `Audio/LinearResampler.cs` | Class | Linear interp; pending-tail cross-buffer continuity; zero dependencies |
| `Audio/IAudioMixer.cs` | Interface | AddChannel, RemoveChannel, FillOutputBuffer, PeakLevels; **`RouteTo`, `UnrouteTo`, `RegisterSink`, `UnregisterSink`, `DefaultFallback`**; `LeaderFormat` (replaces old `Output` back-reference) |
| `Audio/IAudioSink.cs` | Interface | ReceiveBuffer (RT-safe); StartAsync/StopAsync |
| `Audio/ChannelFallback.cs` | Enum | `Silent` / `Broadcast` |
| `Audio/VirtualAudioOutput.cs` | Class | Hardware-free `IAudioOutput`; `StopwatchClock`-driven tick loop; use as clock master when all audio goes to sinks (no physical device needed) |
| `Audio/AggregateOutput.cs` | Class | Leader + N sinks; `AddSink(sink, channels=0)` registers with mixer; RT distribution inside `AudioMixer.FillOutputBuffer`; internal `AggregateAudioMixer` wrapper |
| `Audio/Routing/ChannelRoute.cs` | Record struct | SrcChannel, DstChannel, Gain |
| `Audio/Routing/ChannelRouteMap.cs` | Class | Fluent Builder; Identity/StereoFanTo/StereoExpandTo/DownmixToMono/**Silence** |

#### Mixing
| File | Type | Notes |
|---|---|---|
| `Mixing/AudioMixer.cs` | Class | `AudioFormat LeaderFormat` (decoupled from `IAudioOutput`); copy-on-write slot array; null resampler on rate match; `PrepareBuffers(int)`; per-sink `SinkTarget`/`SinkRoute`/`ChannelSlot` nested types; pull-once-scatter-N RT path; in-line sink distribution |

#### Errors
| File | Type | Notes |
|---|---|---|
| `Errors/MediaException.cs` | Exception | Base pipeline exception |
| `Errors/AudioEngineException.cs` | Exception | Wraps native error codes |
| `Errors/BufferException.cs` | Exception | Underrun/overflow with `FramesAffected` |

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
| `FFmpegDecoderOptions.cs` | Class | PacketQueueDepth, AudioBufferDepth, VideoBufferDepth, **DecoderThreadCount**, **HardwareDeviceType** |
| `FFmpegDecoder.cs` | Class | `avformat_open_input`; demux thread uses `WriteAsync` for back-pressure (no silent drops); optional hw device ctx via `av_hwdevice_ctx_create`; **skips `AV_DISPOSITION_ATTACHED_PIC` streams** (e.g. FLAC cover art) to avoid spurious video channels; demux loop catches `OperationCanceledException` on graceful stop |
| `FFmpegAudioChannel.cs` | Class | `IAudioChannel`; background decode thread; `thread_count` applied to codec ctx; SWR → Float32; bounded ring; **decode loop catches `OperationCanceledException`** on graceful stop |
| `FFmpegVideoChannel.cs` | Class | `IMediaChannel<VideoFrame>`; background decode; hw→CPU transfer via `av_hwframe_transfer_data`; SWS pixel conversion; Yuv422p10 mapped to `AV_PIX_FMT_YUV422P10LE`; **`SafePts()` guards against `AV_NOPTS_VALUE` (long.MinValue) overflow**; **decode loop catches `OperationCanceledException`** on graceful stop; **`ConvertFrame` rents from `ArrayPool<byte>` via `ArrayPoolOwner<T>`; `VideoFrame.MemoryOwner` allows consumer to return rental** |
| `ArrayPoolOwner.cs` | Class | `internal` `IDisposable` wrapper around `ArrayPool<T>` rentals; idempotent via `Interlocked.Exchange` |
| `SwrResampler.cs` | Class | `IAudioResampler`; libswresample sinc; stateful; reinitialises on param change |

---

### `S.Media.NDI` (`NDI/S.Media.NDI/`)

| File | Type | Notes |
|---|---|---|
| `S.Media.NDI.csproj` | Project | Refs `NDILib`, `S.Media.Core`; `AllowUnsafeBlocks` |
| `NdiClock.cs` | Class | `MediaClockBase`; Stopwatch interpolates between NDI frame timestamps; `UpdateFromFrame(long)` |
| `NdiAudioChannel.cs` | Class | `IAudioChannel`; background capture via `NDIFrameSync.CaptureAudio`; FLTP→interleaved; pre-allocated `ConcurrentQueue<float[]>` pool; manual DropOldest returns buffers to pool; **`_framesInRing` Interlocked counter for accurate `BufferAvailable` frame count** |
| `NdiVideoChannel.cs` | Class | `IMediaChannel<VideoFrame>`; background capture; BGRA32 copy; `FreeVideo` called immediately after pixel copy |
| `NdiAudioSink.cs` | Class | `IAudioSink`; interleaved→planar on write thread; 8-buffer pool; optional `IAudioResampler` (auto-creates `LinearResampler` on rate mismatch) |
| `NdiSource.cs` | Class | **New** — lifecycle wrapper: creates `NDIReceiver` + `NDIFrameSync`, constructs `NdiAudioChannel` + `NdiVideoChannel`, `Start()` starts clock + capture threads, `Dispose()` tears down in order |

---

## Tests ✅

> **Total: 122 / 122 passing** (85 core + 37 FFmpeg)

### `S.Media.Core.Tests` (`Test/S.Media.Core.Tests/`) — **85 / 85 passing**


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

### `S.Media.FFmpeg.Tests` (`Test/S.Media.FFmpeg.Tests/`) — **37 / 37 passing**

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
| `Defaults_HardwareDeviceType_IsNull` | Software-only default |
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


## Missing / TODO 🔲

### Tests — additional coverage needed

| Project | Priority | Notes |
|---|---|---|
| `S.Media.PortAudio.Tests` | Medium | Smoke tests (requires audio hardware or virtual device) |
| `S.Media.NDI.Tests` | Low | Requires NDI runtime |

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
| Q8 | Silent packet drops + hw decode | Demux loop uses `WriteAsync` (back-pressure). `FFmpegDecoderOptions` adds `DecoderThreadCount` and `HardwareDeviceType` (VAAPI, CUDA, DXVA2, VideoToolbox). |
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
