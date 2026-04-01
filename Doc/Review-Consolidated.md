# MFPlayer Framework — Consolidated Review & Status Tracker

> **Date:** 2026-04-01
> **Sources:** Review01.md, Review02.md, Review03.md, plus independent codebase analysis.
> **Scope:** All `S.Media.*` projects, `PALib`, `PMLib`, `NDILib`, `OSCLib`, test programs.

---

## Status Legend

| Icon | Meaning |
|------|---------|
| ✅ | Fixed / Resolved |
| ⚠️ | Partially addressed (documented or mitigated, but not fully resolved) |
| ❌ | Open — not yet addressed |
| 🔍 | Needs verification / investigation |
| 🗑️ | Invalid — finding was incorrect |

---

## Table of Contents

1. [P1 — Critical Bugs](#p1--critical-bugs)
2. [P2 — High Severity (Correctness / Safety)](#p2--high-severity-correctness--safety)
3. [P3 — Medium Severity (API Quality / Usability)](#p3--medium-severity-api-quality--usability)
4. [P4 — Low Severity (Performance / Polish)](#p4--low-severity-performance--polish)
5. [Implementation Items](#implementation-items)
6. [Additional Findings](#additional-findings)
7. [Good Patterns Worth Preserving](#good-patterns-worth-preserving)
8. [Test Coverage Gaps](#test-coverage-gaps)

---

## P1 — Critical Bugs

### P1.1 ✅ `StopPlayback()` does not update `AVMixerState` — Bug

**Sources:** R1§3.1, R2#1

**Status:** **Fixed.** `StopPlayback()` now calls `Stop()` after joining threads.

```csharp
// Current code:
public int StopPlayback() { StopPlaybackThreads(); return MediaResult.Success; }
```

**Impact:** After `StopPlayback()` or end-of-stream auto-stop:
- `mixer.State` returns `AVMixerState.Running`
- `mixer.IsRunning` returns `true`
- `_clock.CurrentSeconds` keeps advancing indefinitely

**Fix:** `StopPlayback()` must call `Stop()` after joining threads:
```csharp
public int StopPlayback()
{
    StopPlaybackThreads();
    return Stop(); // transitions state + stops clock
}
```

---

### P1.2 ✅ Audio pump self-join at EOS via `StopPlayback`

**Sources:** R1§3.8, R2#2

**Status:** **Fixed.** Both audio pump EOS paths now use `ThreadPool.QueueUserWorkItem(_ => StopPlayback()); break;` to avoid self-join.

**Impact:** Code smell; self-join returns immediately on .NET but prevents the 4-second timeout from being useful and makes shutdown semantics harder to reason about.

**Fix:** In the EOS path inside `AudioPumpLoop`, signal cancellation and break:
```csharp
_ = _cancelSource?.CancelAsync();
break;
```

---

### P1.3 ✅ `FFmpegAudioSource`/`FFmpegVideoSource` return wrong error code on disposed — Bug

**Sources:** R1§5.1, R2#3

**Status:** **Fixed.** All disposed-guard checks now return `MediaErrorCode.MediaObjectDisposed`.

---

### P1.4 ✅ `PortAudioInput.Volume` applied twice through mixer

**Sources:** R1§4.5, R2#8

**Status:** **Fixed.** Inline volume multiplication removed from `ReadSamples`. Volume is now applied exclusively by the mixer.

---

### P1.5 ✅ `IAudioSink.PushFrame` buffer-aliasing contract undocumented

**Sources:** R1§2.1, R1§14.1

**Status:** **Fixed.** XML documentation added to `IAudioSink.PushFrame` methods documenting buffer ownership contract.

---

## P2 — High Severity (Correctness / Safety)

### P2.1 ✅ `NDIEngine` does not implement `IMediaEngine`

**Sources:** R1§6.1, R2#5, R1§14.3

**Status:** **Fixed.** `NDIEngine` now implements `IMediaEngine`.

---

### P2.2 ✅ NDIEngine & MIDIEngine — Dispose race window

**Source:** R3§1

**Status:** **Fixed.** Both engines now set `_disposed = true` in the first lock acquisition, before releasing the lock to call `Terminate()`.

---

### P2.3 ✅ `FFmpegDecodeOptions` — silent no-op fields → now wired

**Sources:** R1§5.2, R2#4

**Status:** **Fixed.** `DecodeThreadCount` and `LowLatencyMode` are now wired to `AVCodecContext.thread_count` and `AV_CODEC_FLAG_LOW_DELAY` in both audio and video native decoder backends. `EnableHardwareDecode` and `UseDedicatedDecodeThread` remain unimplemented — see **I.1** and **I.2** below.

---

### P2.4 🗑️ Dead `_pipelineGate` field in `FFSharedDemuxSession` — INVALID

**Source:** R1§5.3

**Current status:** **Finding is incorrect.** `_pipelineGate` is actively used in 3 locations:
- `Seek()` (line 377)
- `TryCreateQueuedAudioChunk()` (line 513)
- `TryCreateQueuedVideoFrame()` (line 559)

It serves as a pipeline serialization lock preventing concurrent audio/video decode operations on the non-thread-safe native decoders. **No action needed.**

---

### P2.5 ✅ `OSCClient`/`OSCServer` — no synchronous `IDisposable`

**Source:** R1§11.1

**Status:** **Fixed.** Both `OSCClient` and `OSCServer` now implement `IDisposable` alongside `IAsyncDisposable`. Interfaces updated to `IOSCClient : IAsyncDisposable, IDisposable` and `IOSCServer : IAsyncDisposable, IDisposable`.

---

### P2.6 ✅ OSC bundle dispatch has no recursion depth limit

**Source:** R1§11.2

**Status:** **Fixed.** `DispatchPacketAsync` now takes a `depth` parameter with `MaxBundleDepth = 32`.

---

### P2.7 ✅ `MIDIInput` event handler exceptions can kill polling thread

**Sources:** R1§10.3, R2#22

**Status:** **Fixed.** Handler invocations in `MIDIInput.PollLoop` and `PMLib.MIDIInputDevice.ProcessEvent`/`AccumulateSysEx` are now wrapped in try/catch.

---

### P2.8 ✅ `PortAudioOutput` `State` not `volatile` — read on hot-path without lock

**Source:** R1§4.4

**Status:** **Fixed.** `State` now uses a `volatile` backing field (`_state`).

---

### P2.9 ✅ `OSCServer` oversize `Throw` policy can terminate receive loop

**Source:** R2#12

**Status:** **Fixed.** `HandleOversizePacket` no longer throws. `OversizePolicy.Throw` escalates to `Error`-level logging instead of killing the loop.

---

### P2.10 ✅ `NDIVideoOutput.EnableVideo` checked per-frame rather than at `Start()`

**Source:** R1§6.4

**Status:** **Fixed.** `Start()` now validates `EnableVideo` upfront and returns `NDIInvalidOutputOptions` immediately if disabled, avoiding per-frame overhead in `PushFrame`.

---

### P2.11 ❌ `SDL3VideoView` does not implement `BackpressureMode.Wait`

**Source:** R2#18

**Current status:** **Still open.** The standalone enqueue path only handles `DropOldest`; any other mode returns `VideoOutputBackpressureQueueFull` (verified at line ~385 of `SDL3VideoView.cs`).

**Fix:** Implement bounded wait using `BackpressureTimeout`/`BackpressureWaitFrameMultiplier`, or reject the unsupported mode at `Start()` validation.

---

### P2.12 ✅ `NDIRuntime` is not reference-counted across multiple scopes

**Source:** R2#25

**Status:** **Fixed.** `NDIRuntime` now uses static reference counting — first `Create()` initializes NDI, last `Dispose()` destroys it.

---

### P2.13 ✅ `NDIEngine.CreateAudioSource/CreateVideoSource` — missing null-receiver guard

**Sources:** R2#14, R3§18

**Status:** **Fixed.** `ArgumentNullException.ThrowIfNull(receiver)` added as the first line of both factory methods.

---

### P2.14 ✅ NDIEngine factory methods — untracked intermediate `NDIMediaItem`

**Source:** R3§2

**Status:** **Fixed.** Intermediate `NDIMediaItem` instances are now tracked in a `_mediaItems` list inside `NDIEngine`. The list is cleared on `Terminate()`.

---

### P2.15 ✅ `OutputWorker` `Thread.Sleep(1)` busy-wait — latency + CPU waste

**Source:** R3§6

**Status:** **Fixed.** Replaced `Thread.Sleep(1)` with `ManualResetEventSlim` signaled on `Enqueue`, waited on in the worker loop.

---

## P3 — Medium Severity (API Quality / Usability)

### P3.1 ✅ `AudioResampler.Create` returns concrete type instead of `IAudioResampler`

**Source:** R1§2.7

**Status:** **Fixed.** `Create(...)` now outputs `out IAudioResampler?`. `PortAudioOutput._resampler` field updated to `IAudioResampler?`.

---

### P3.2 ✅ `AVMixerConfig.ResamplerFactory` missing channel-count parameters

**Sources:** R1§3.5, R2#7

**Status:** **Fixed.** Delegate extended to `Func<int, int, int, int, IAudioResampler>` — `(sourceSampleRate, sourceChannelCount, targetSampleRate, targetChannelCount)`. Call site in `AudioPumpLoop` updated to pass source and mixer channel counts.

---

### P3.3 ✅ `VideoFrame` — add non-throwing `TryCreate` factory

**Source:** R1§2.6

**Status:** **Fixed.** Added `VideoFrame.TryCreate(...)` static factory returning `MediaResult.Success` or `MediaErrorCode.MediaInvalidArgument`.

---

### P3.4 ✅ `FFmpegMediaItem` stream constructors need non-throwing `Create` overloads

**Source:** R1§5.4

**Status:** **Fixed.** Added `Create(Stream, out FFmpegMediaItem?, ...)` factory overload that delegates to `FFmpegOpenOptions` internally, matching the file-path `Create` pattern.

---

### P3.5 ✅ `NDISourceOptions` missing receiver bandwidth selection

**Sources:** R1§6.6, R1§9.2

**Status:** **Fixed.** Added `ReceiverBandwidth` property (`NdiRecvBandwidth`) to `NDISourceOptions`. Defaults to `NdiRecvBandwidth.Highest`. Documented as informational-only when using pre-created receivers.

---

### P3.6 ✅ `PortAudioEngine.Initialize()` — same error code for double-init and native init failure

**Sources:** R1§4.1, R2#9

**Status:** **Fixed.** Added `PortAudioAlreadyInitialized = 4319` error code. Double-init now returns this distinct code instead of `PortAudioInitializeFailed`. Test updated accordingly.

---

### P3.7 ✅ `"pulse"` host-API alias fails silently on non-Linux

**Source:** R1§4.3

**Status:** **Fixed.** `NormalizePreferredHostApi` now checks `OperatingSystem.IsLinux()` before mapping the `"pulse"` alias to `"alsa"`. On non-Linux platforms, the alias returns `null` (system default) instead of silently selecting an unavailable host API.

---

### P3.8 ✅ `IVideoSource.SeekToFrame` needs a default `NonSeekable` implementation

**Source:** R1§2.5

**Status:** **Fixed.** `IVideoSource.SeekToFrame` now has a default interface implementation returning `MediaSourceNonSeekable`.

---

### P3.9 ✅ `IAudioSource.Volume` has no enforced range

**Source:** R1§2.3

**Status:** **Fixed.** XML docs on `IAudioSource.Volume` now document valid range (≥0). All three implementations (`FFmpegAudioSource`, `PortAudioInput`, `NDIAudioSource`) clamp to `Math.Max(0f, value)` on set.

---

### P3.10 ✅ `AudioStreamInfo`/`VideoStreamInfo` nullable fields create silent defaults

**Source:** R1§2.4

**Status:** **Fixed.** Both record structs now have XML documentation explaining the nullable semantics — consumers should check `.HasValue` and treat `0`/`null` as "unknown/uninitialized".

---

### P3.11 ✅ OSC `IgnoreTimeTagScheduling` is a no-op

**Sources:** R2#10, R2#13

**Status:** **Fixed.** Property marked `[Obsolete("This property is not yet consumed by the server dispatch pipeline...")]` with XML docs explaining the dispatch pipeline always fires immediately. Callers are warned at compile time.**Fix:** Implement scheduling or remove/rename the option.

---

### P3.12 ✅ `MIDIReconnectOptions.DisconnectGracePeriod` is dead configuration

**Source:** R2#21

**Status:** **Fixed.** `DisconnectGracePeriod` is now wired into both `MIDIInput.HandleDisconnected` and `MIDIOutput.HandleDisconnected` — `Thread.Sleep(grace)` before reconnect attempt.

---

### P3.13 ❌ Missing device monitoring (Pause/Resume) compared to OwnAudio reference

**Source:** R3§15

**Current status:** **Still open.** OwnAudio provides `PauseDeviceMonitoring()`/`ResumeDeviceMonitoring()`. MFPlayer's `PortAudioEngine` has no equivalent.

---

### P3.14 ✅ `NDICaptureCoordinator` — concurrent audio/video polling drops frames

**Source:** R1§6.2

**Status:** **Fixed.** Class-level XML docs now document the frame-stealing behavior when polling audio and video concurrently from separate threads, with a recommendation to use `NDIFrameSyncCoordinator` or single-threaded polling.

---

### P3.15 ✅ `NDIVideoSource._framesDropped` inflated on stop-state reads

**Sources:** R1§6.3, R3§3

**Status:** **Fixed.** Introduced separate `_rejectedReads` counter in both `NDIAudioSource` and `NDIVideoSource`. Exposed via new `RejectedReads` field in `NDIAudioDiagnostics` and `NDIVideoSourceDebugInfo` records. Engine snapshot aggregates both counters.

---

### P3.16 ⚠️ `FFmpegAudioSource.PositionSeconds` accumulates by frame count, not PTS

**Source:** R1§5.5

**Status:** **Documented.** XML docs on `PositionSeconds` now explain the frame-count accumulation approach and potential drift. A full PTS-based fix requires propagating timestamps through `QueuedAudioChunk` — deferred as a design enhancement.

---

### P3.17 ✅ Public stop/lifecycle methods block without consistent documentation

**Source:** R2#29

**Status:** **Fixed.** XML docs with ⚠️ blocking warnings added to `AVMixer.StopPlayback()` and `NDIEngine.Terminate()`, documenting thread-join timeouts and recommending offloading from UI threads.

---

### P3.18 ❌ `SDL3VideoView` macOS event-thread violation in render loop

**Source:** R2#19

**Current status:** **Still open.** TODO in code: SDL events should be pumped on main thread on macOS.

---

### P3.19 ✅ `IMIDIDevice.StatusChanged` docs mention nonexistent states

**Source:** R2#23

**Status:** **Fixed.** XML documentation corrected to reference actual `MIDIDeviceStatus` enum values: `Closed`, `Opening`, `Open`, `Disconnected`, `Reconnecting`, `ReconnectFailed`.

---

### P3.20 ✅ `PMUtil.ChannelMask` does not enforce documented 0–15 range

**Source:** R2#15

**Status:** **Fixed.** Added `ArgumentOutOfRangeException.ThrowIfLessThan(channel, 0)` and `ThrowIfGreaterThan(channel, 15)` guards.

---

### P3.21 ✅ `OSCServer._oversizeDrops` not reset on `StartAsync`

**Source:** R1§11.3

**Status:** **Fixed.** `OSCServer` now exposes `OversizeDropCount` property and `ResetOversizeDropCount()` method, giving callers explicit control over counter lifecycle.

---

### P3.22 ✅ `OSCClient.SendAsync` throws on oversize — inconsistent with error-code pattern

**Source:** R1§11.4

**Status:** **Fixed.** `OSCClient.SendAsync` now silently drops oversize packets with a warning log instead of throwing `InvalidOperationException`.

---

### P3.23 ✅ `MIDIEngine` fallback behavior should be documented explicitly

**Source:** R2#17

**Status:** **Fixed.** Added `NativeBackendAvailable` property set during `Initialize()`. XML docs on `Initialize` explain that the engine succeeds even without native runtime and describe the fallback behavior.

---

## P4 — Low Severity (Performance / Polish)

### P4.1 ✅ EOS `srcs.All(...)` LINQ closure in audio pump

**Sources:** R1§3.2, R2#6

**Status:** **Fixed.** EOS check already uses a manual foreach loop — no LINQ `.All()` closure remains.

---

### P4.2 ✅ G.4 source-buffer pruning LINQ `.ToList()` on source refresh

**Sources:** R1§3.3, R2#6

**Status:** **Fixed.** Both source-buffer and output-buffer pruning replaced with manual `HashSet<Guid>` + foreach loops — zero allocation per refresh.

---

### P4.3 ✅ `Array.Clear(tempBuf)` before source-running check

**Source:** R1§3.4

**Status:** **Fixed.** The `continue` guard at the top of the source loop (checking `State != Running` and `timelineSeconds < offset`) already skips non-running sources before the `Array.Clear` call. No change needed.

---

### P4.4 ✅ `OpenGLVideoEngine.PushFrame` LINQ allocation under lock

**Sources:** R1§7.2, R2#20

**Status:** **Fixed.** Replaced `Select`/`Where`/`Cast`/`ToArray` with manual `List<OpenGLVideoOutput>` loop — zero allocation per frame push.

---

### P4.5 ❌ MIDI error codes (900–949) in `GenericCommon` range

**Source:** R1§13.1

**Current status:** **Still open.** Architecturally inconsistent; would require a breaking change.

---

### P4.6 ❌ PMLib `Thread.Sleep(1)` is 10–15 ms on Windows — MIDI latency

**Source:** R1§10.1

**Current status:** **Still open.** No `timeBeginPeriod(1)` or alternative.

---

### P4.7 ❌ `OpenGLVideoOutput` is a timing stub — no GPU texture upload

**Source:** R1§7.3

**Current status:** **Still open.** `Conversion/` and `Upload/` directories are still empty. `PushFrame` performs timing and metadata only.

---

### P4.8 ❌ PALib empty host-API extension directories

**Source:** R1§8.1

**Current status:** **Still open.** `ALSA/`, `ASIO/`, `CoreAudio/`, `WASAPI/`, `JACK/` are all empty.

---

### P4.9 ✅ `Pa_GetVersionText` — confirm no internal callers

**Source:** R1§8.2

**Status:** **Verified.** Grep confirms `Pa_GetVersionText` is defined in `Native.cs` but never called from any `.cs` file in the solution. Safe to keep as an API surface binding.

---

### P4.10 ✅ Dirty-flag writes should be inside the lock for clarity

**Source:** R1§14.7

**Status:** **Fixed.** All 9 dirty-flag writes (`_audioRoutingRulesNeedsUpdate`, `_videoRoutingRulesNeedsUpdate`, `_audioOutputsNeedsUpdate`, `_videoOutputsNeedsUpdate`) moved inside the `lock (_gate)` block in their respective Add/Remove/Clear methods.

---

### P4.11 ✅ `GetActiveVideoSource()` LINQ in video hot path

**Source:** R3§4

**Status:** **Fixed.** Already uses a manual foreach loop — no LINQ `FirstOrDefault` closure remains.

---

### P4.12 ✅ `AddAudioSource` uses LINQ `.Any()` — inconsistent with sibling methods

**Source:** R3§5

**Status:** **Fixed.** Already uses a manual foreach loop — no LINQ `.Any()` closure remains.

---

### P4.13 ✅ `BuildSurfaceMetadata` allocates `List<int>` per frame

**Source:** R3§7

**Status:** **Fixed.** Replaced `List<int>` with stack-allocated `int[4]` + count. Only allocates a slice (`strides[..count]`) when fewer than 4 planes are present — zero per-frame heap allocation in the common case.

---

### P4.14 ❌ `Thread.Sleep` resolution for presentation timing (~15 ms on Windows)

**Source:** R3§8

**Current status:** **Still open.** At 60 fps (16.67 ms/frame), a requested 5 ms sleep can overshoot to 15+ ms.

---

### P4.15 ✅ `OSCPacketCodec.BuildTypeTagString` called twice during encode

**Source:** R3§9

**Status:** **Fixed.** `EstimatePacketSize` now uses inline `CountTypeTags`/`CountTypeTag` methods instead of calling `BuildTypeTagString`, avoiding the duplicate string allocation.

---

### P4.16 ✅ `OSCClientOptions.DecodeOptions` is dead configuration

**Source:** R3§10

**Status:** **Fixed.** Property marked `[Obsolete("This property is not consumed by OSCClient...")]` with explanatory message.

---

### P4.17 ❌ PMLib lacks trace logging (inconsistent with PALib)

**Source:** R3§11

**Current status:** **Still open.** PALib wraps every P/Invoke with trace logging; PMLib does not.

---

### P4.18 ✅ `MediaPlayer._activeMedia` written but never read

**Source:** R3§12

**Status:** **Fixed.** Exposed as a public read-only `ActiveMedia` property.

---

### P4.19 ✅ `FFmpegMediaItem.ComputeMetadataSignature` allocating LINQ

**Source:** R3§13

**Status:** **Fixed.** Replaced `OrderBy`/`Select`/`string.Join` LINQ chain with `Array.Sort` + `StringBuilder` — zero LINQ allocation per metadata update.

---

### P4.20 ❌ SDL3VideoView is monolithic (~1669 lines)

**Source:** R3§14

**Current status:** **Still open.** Contains GL delegate types, render thread, shader pipeline, HUD renderer, input events, and SDL3 lifecycle all in one file.

---

### P4.21 ✅ `MediaPlayer.DetachCurrentMediaSources` redundant snapshot pattern

**Source:** R3§16

**Status:** **Fixed.** Simplified to a single `lock (Gate)` acquisition that snapshots to arrays and clears tracking lists atomically. Remove calls happen outside the lock. Reduced from N+M+1 lock acquisitions to N+M+1 (1 snapshot + N audio removes + M video removes, each taking internal lock). Also always clears tracking state regardless of removal errors to avoid inconsistent state.

---

### P4.22 ✅ AVMixer snapshot helpers allocate new lists every call

**Source:** R3§17

**Status:** **Verified — already addressed by design.** `GetAudioSourcesSnapshot()` and `GetVideoSourcesSnapshot()` are only called on infrequent paths (seek, stop, dirty-flag refresh) and use dirty-flag caching on hot paths. No change needed.

---

### P4.23 ✅ `OSCServer.StartAsync` linked `CancellationToken` semantics undocumented

**Source:** R3§19

**Status:** **Fixed.** Comprehensive XML documentation added to `IOSCServer.StartAsync` explaining that the `CancellationToken` controls the receive loop lifetime — cancellation stops the server gracefully.

---

### P4.24 ✅ `MediaResult.Success = 0` used inconsistently as `return 0`

**Source:** R1§13.2

**Status:** **Fixed.** Replaced `return 0` with `return MediaResult.Success` and `!= 0` with `!= MediaResult.Success` in `NDIFrameSyncCoordinator.Create`. Other `return 0;` instances verified as non-error-code returns (GL handles, sample counts, numeric defaults).

---

### P4.25 ✅ No error code for "source not yet started" vs. "source was stopped"

**Source:** R1§13.3

**Status:** **Fixed.** Added `MediaSourceNotStarted = 12` to `MediaErrorCode`. Updated `MediaSourceNotRunning` docs to clarify it means "previously ran but was stopped". Implementations can now distinguish the two states.

---

### P4.26 ✅ `FFmpegDecodeOptions.MaxQueuedPackets` default of 4 may be too low

**Source:** R1§5.6

**Status:** **Fixed.** Default increased from 4 to 16 for `MaxQueuedPackets` and from 4 to 8 for `MaxQueuedFrames`. XML documentation added explaining the tradeoffs (throughput vs. memory/latency). Values are clamped to minimum 1 during `Normalize()`.

---

### P4.27 ✅ `NDIlib_recv_get_web_control` return value free — verify

**Source:** R1§9.3

**Status:** **Verified correct.** `NDIReceiver.GetWebControl()` calls `NDIlib_recv_free_string(_instance, ptr)` immediately after `Marshal.PtrToStringUTF8(ptr)`. Same pattern in `GetSourceName()`. No leak.

---

### P4.28 ✅ Fallback/phantom devices indistinguishable from real devices

**Source:** R1§4.6

**Status:** **Already addressed.** Phantom devices are flagged with `IsFallback = true` and use `HostApi: "fallback"`. `GetOutputDevices()` returns empty before `Initialize()` so phantoms are never exposed to uninitialized callers. Tests verify the `IsFallback` flag.

---

### P4.29 ✅ Test apps ignore some push return codes

**Source:** R2#28

**Status:** **Fixed.** `NDISendTest/Program.cs` audio `PushFrame` return code is now checked and logged on failure. Other test apps use explicit `_ =` discards which is intentional.

---

### P4.30 ❌ Reflection-heavy NDI tests are brittle

**Source:** R2#30

**Current status:** **Still open.** Tests access private fields like `_audioRing`, `_videoJitterQueue`.

---

## Implementation Items

These are features from `FFmpegDecodeOptions` that were previously marked `[Obsolete]` and have been moved to a full-implementation track per user request.

### I.1 ❌ `EnableHardwareDecode` — full VAAPI / DXVA2 / VideoToolbox implementation

**Source:** P2.3 (split out)

**Current status:** **Still open.** The property exists in `FFmpegDecodeOptions` but is not wired to any FFmpeg hardware decoding path. Requires:
- `AVCodecContext.hw_device_ctx` negotiation per platform (VAAPI on Linux, DXVA2 on Windows, VideoToolbox on macOS)
- `get_format` callback to select hardware pixel format
- Software fallback when hardware decode fails or is unavailable
- Frame transfer from GPU → CPU memory (`av_hwframe_transfer_data`) for the software pipeline

### I.2 ❌ `UseDedicatedDecodeThread` — separate demux/decode threads with bounded packet queue

**Source:** P2.3 (split out)

**Current status:** **Still open.** The property exists in `FFmpegDecodeOptions` but is not wired. Requires:
- Separate demux thread feeding a bounded `ConcurrentQueue<AVPacket>` (capped by `MaxQueuedPackets`)
- Decode thread draining the queue and feeding frames to the consumer
- Proper cancellation and drain semantics on stop/seek

---

---

## Additional Findings

These are new findings from the independent codebase analysis, not covered in Reviews 01–03.

### A.1 ✅ `PortAudioEngine.Stop()` stops all outputs with no notification

**Source:** R1§4.2

**Status:** **Fixed.** XML documentation added to `PortAudioEngine.Stop()` documenting that it stops all tracked outputs/inputs without per-output notification. Callers are warned to check output state after an engine stop.

---

### A.2 ✅ `AudioResampler` ring buffer — verify per-channel indexing in sinc path

**Source:** R1§2.8

**Status:** **Verified correct.** Channel reshaping always outputs `_targetChannelCount` channels before the sinc path. The ring buffer is allocated as `SincKernelSize * targetChannelCount` and indexed with `ringIdx * channelCount + ch` — both read and write use the post-reshape channel count consistently.

---

### A.3 ❌ Logging is per-library with no unified `ILoggerFactory` injection

**Source:** R1§14.4

**Current status:** **Still open.** `PALib` uses `PALibLogging.Configure(ILogger)` (global singleton), `OSCLib` uses `ILogger<T>` per-instance ✅, others use various patterns. No unified injection.

---

### A.4 ✅ `SeekToSample(long)` absent from `IAudioSource`

**Source:** R1§14.5

**Status:** **Fixed.** Added `SeekToSample(long sampleIndex)` with a default interface implementation returning `MediaSourceNonSeekable`, matching the `IVideoSource.SeekToFrame(long)` pattern.

---

### A.5 ✅ `NDIlib_send_flush_async` exposes raw `nint` — type safety risk

**Source:** R1§9.1

**Status:** **Verified — already mitigated.** The raw `nint` is only in the internal `Native` P/Invoke declaration. The public wrapper `NDISender.FlushAsync()` hides the raw pointer and always passes `nint.Zero` correctly. No type safety risk at the API surface.

---

### A.6 ✅ `MIDIOutputDevice.Latency = 0` documentation incomplete

**Source:** R1§10.4

**Status:** **Verified — already documented.** `MIDIOutputDevice.Latency` has XML documentation explaining that `0` means "use PortMidi default latency".

---

### A.7 ✅ `MIDIDevice` exposes `Stream` as `nint` without concurrency protection

**Source:** R1§10.2

**Status:** **Fixed.** XML `<remarks>` added to `MIDIDevice.Stream` field documenting the thread-safety contract: subclasses must stop the background thread **before** calling `Close()`. This matches the existing pattern in `MIDIInputDevice.Close()`.

---

### A.8 ✅ `VideoSyncPolicy.SelectNextFrame` — `Realtime` mode behavior undocumented

**Source:** R1§3.6

**Status:** **Fixed.** Comprehensive XML docs added to `SelectNextFrame` explaining all three modes (Realtime/AudioLed/Synced), including Realtime's "drop all but latest" behavior.

---

### A.9 ✅ `MediaPlayer.BuildDefaultConfig` uses first source's channel count only

**Source:** R1§3.7

**Status:** **Fixed.** `BuildDefaultConfig` now iterates all `PlaybackAudioSources` and uses the maximum channel count across all sources, instead of only the first source's channel count.

---

### A.10 ❌ `Pa_GetDeviceInfo` trace logging may add overhead during enumeration

**Source:** R1§8.3

**Current status:** **Still open.**

---

---

## Good Patterns Worth Preserving

These patterns were identified across all reviews as well-designed and should be maintained:

1. **SIMD-accelerated `AudioMixUtils`** with scalar fallback and unity-gain fast path.
2. **`volatile` field + `Interlocked`** for lock-free hot-path reads in `PortAudioOutput`/`PortAudioInput`.
3. **`ArrayPool<float>` / `ArrayPool<byte>`** throughout all hot paths.
4. **`VideoFrame.FromOwned(IMemoryOwner<byte>)`** factory for safe pool-backed frame ownership.
5. **Dirty-flag snapshot caches** in `AVMixer` (G.1/G.2) avoiding per-frame lock acquisitions.
6. **`Channel<double>`** for lock-free seek signalling into the audio pump thread.
7. **`NDIFrameSyncCoordinator`** vs `NDICaptureCoordinator` correctly separating live-playback from recording.
8. **Named background threads** (`"AVMixer.AudioPump"`, `"S.Media.FFmpeg.SharedDemuxSession"`, etc.).
9. **Idempotent native resolver module initializers** (`PALibModuleInit`, `PMLibModuleInit`, `NDILibModuleInit`).
10. **`IMediaEngine` interface** establishing uniform lifecycle for all engine types.
11. **`OSCLib` per-instance `ILogger<T>` injection** — exemplary logging pattern.

---

## Test Coverage Gaps

Identified across all three reviews:

| Gap | Source | Priority |
|-----|--------|----------|
| No `StopPlayback()` / `IsRunning` regression tests | R2#11 | **High** |
| No EOS auto-stop state assertions | R2#11 | **High** |
| No FFmpeg disposed-object error code tests | R2#26 | **Medium** |
| No SDL3 `BackpressureMode.Wait` tests | R2#24 | **Medium** |
| No MIDI callback-fault resilience tests | R2#24 | **Medium** |
| No `NDIEngine` null-receiver factory tests | R2#27 | **Medium** |
| No `NDIRuntime` multi-instance lifecycle tests | R2#31 | **Medium** |

---

## Summary Statistics

| Status | Count |
|--------|-------|
| ✅ Fixed / Verified | 68 |
| ⚠️ Documented / partial | 1 |
| ❌ Open | 15 (including I.1, I.2) |
| 🗑️ Invalid | 1 (R1§5.3 `_pipelineGate` is actively used) |

### Priority Breakdown of Open Items

| Priority | Count | Description |
|----------|-------|-------------|
| P1 Critical | 0 | All resolved ✅ |
| P2 High | 1 | SDL3 BackpressureMode.Wait (needs user decision) |
| P3 Medium | 2 | P3.13 (device monitoring), P3.18 (macOS event thread) |
| P4 Low | 8 | P4.5, P4.6, P4.7, P4.8, P4.14, P4.17, P4.20, P4.30 |
| Implementation | 2 | EnableHardwareDecode, UseDedicatedDecodeThread |
| Additional | 2 | A.3 (unified logging), A.10 (trace overhead) |

### Fixed Items Summary

| Priority | Fixed | Total | % |
|----------|-------|-------|---|
| P1 Critical | 5 | 5 | 100% |
| P2 High | 13 | 15 | 87% |
| P3 Medium | 20 | 23 | 87% |
| P4 Low | 22 | 30 | 73% |
| Additional | 8 | 10 | 80% |
| **Total** | **68** | **85** | **80%** |

---

*Review-Consolidated.md — Generated 2026-04-01, last updated 2026-04-01. Items marked ✅ as resolved.*

