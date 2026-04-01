# MFPlayer Framework — Comprehensive Review

> **Date:** 2026-04-01 v1
> **Scope:** All projects — `S.Media.*`, `PALib`, `NDILib`, `PMLib`, `OSCLib`, test programs, and reference comparison against `OwnAudio`.
> **Status:** Initial pass complete. Subsequent passes will mark items ✅.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [S.Media.Core — Interfaces & Core Types](#2-smediacore--interfaces--core-types)
3. [AVMixer & MediaPlayer](#3-avmixer--mediaplayer)
4. [S.Media.PortAudio](#4-smediaportaudio)
5. [S.Media.FFmpeg](#5-smediaffmpeg)
6. [S.Media.NDI](#6-smedianDI)
7. [S.Media.OpenGL](#7-smediaopengl)
8. [PALib — Native PortAudio Wrapper](#8-palib--native-portaudio-wrapper)
9. [NDILib — Native NDI Wrapper](#9-ndilib--native-ndi-wrapper)
10. [PMLib — Native PortMidi Wrapper](#10-pmlib--native-portmidi-wrapper)
11. [OSCLib](#11-osclib)
12. [S.Media.MIDI](#12-smediamidi)
13. [Error Code System](#13-error-code-system)
14. [Cross-Cutting Concerns](#14-cross-cutting-concerns)
15. [Prioritised Fix Roadmap](#15-prioritised-fix-roadmap)

---

## 1. Executive Summary

The framework has a mature, well-considered architecture. The source/sink split (`IAudioSource`/`IAudioSink`/`IAudioOutput`, `IVideoSource`/`IVideoOutput`), integer error-code returns, `IDisposable` everywhere, and per-object `Guid` identity are solid. Three prior review passes (March 28–30, 2026, documented in `OldReviews/`) resolved most critical API issues. This review focuses on what remains.

**Most critical remaining items:**

- **§3.1** — `StopPlayback()` does not update `AVMixerState`. After `StopPlayback`, `mixer.State` is still `Running` and `mixer.IsRunning` returns `true`. End-of-stream auto-stop (G.6) exhibits the same problem — the pump exits but the state machine does not transition to `Stopped`.
- **§5.1** — `FFmpegAudioSource.Start()` and `FFmpegVideoSource.Start()` return `MediaErrorCode.MediaInvalidArgument` when disposed, inconsistent with every other component returning `MediaErrorCode.MediaObjectDisposed`.
- **§5.2** — Several `FFmpegDecodeOptions` fields (`EnableHardwareDecode`, `LowLatencyMode`, `DecodeThreadCount`, `UseDedicatedDecodeThread`) are validated and normalised but silently have no effect. Users who configure these will observe no behaviour change — and get no warning.
- **§6.1** — `NDIEngine` declares `class NDIEngine : IDisposable` but does not formally implement `IMediaEngine`, breaking the uniform engine contract used by `PortAudioEngine` and `S.Media.MIDI`'s engine.
- **§14.1** — `AudioFrame.Samples` is a `ReadOnlyMemory<float>` that points directly into the mixer's reused `mixBuf`/`outBuf` arrays. The `IAudioSink` contract does not document that implementations must copy before returning. Any async output implementation that defers the copy would read corrupt data.

**Good patterns worth preserving:**
- SIMD-accelerated `AudioMixUtils` with scalar fallback and unity-gain fast path.
- `volatile` field + `Interlocked` for lock-free hot-path reads in `PortAudioOutput`/`PortAudioInput`.
- `ArrayPool<float>` / `ArrayPool<byte>` throughout all hot paths.
- `VideoFrame.FromOwned(IMemoryOwner<byte>)` factory for safe pool-backed frame ownership.
- Dirty-flag snapshot caches in `AVMixer` (G.1/G.2) avoiding per-frame lock acquisitions.
- `Channel<double>` for lock-free seek signalling into the audio pump thread.
- `NDIFrameSyncCoordinator` vs `NDICaptureCoordinator` correctly separating live-playback from recording workflows.
- Named background threads (`"AVMixer.AudioPump"`, `"S.Media.FFmpeg.SharedDemuxSession"`, etc.) making profiler and dump analysis straightforward.

---

## 2. S.Media.Core — Interfaces & Core Types

### 2.1 `AudioFrame` buffer ownership contract is undocumented

`AudioFrame.Samples` is a `ReadOnlyMemory<float>` that **points directly into the mixer's reused `mixBuf`/`outBuf` arrays** inside `AVMixer.AudioPumpLoop`. The `IAudioSink` interface does not document that `PushFrame` must complete all reads of `frame.Samples` before returning. Any implementation that is truly asynchronous (enqueues a reference without first copying) will read corrupt or overwritten data on the next mixer iteration.

Current implementations (`PortAudioOutput`, `NDIVideoOutput`) consume or copy `Samples` before returning, so there is no active bug. But this is an invisible contract.

**Recommendation:** Add to `IAudioSink.PushFrame` XML doc:
> *Implementations must either (a) consume all data from `frame.Samples` before returning, or (b) copy the span into a private buffer. The memory referenced by `frame.Samples` is only valid for the duration of this call.*

### 2.2 `IAudioSink` identity overload — consider `[AggressiveInlining]`

`IAudioSink.PushFrame(in AudioFrame frame)` creates `Span<int> identity = stackalloc int[ch]`. For typical stereo (ch=2) this is 8 bytes on the stack — negligible. Adding `[MethodImpl(MethodImplOptions.AggressiveInlining)]` to this default interface method would encourage the JIT to inline the trivial stackalloc + delegate call, avoiding the indirect dispatch overhead on the hot path.

### 2.3 `IAudioSource.Volume` has no enforced range

`Volume` is declared as `float { get; set; }`. There is no contract on valid range. Callers who set `Volume = 2.5f` or `Volume = -0.1f` will get inconsistent or broken behaviour: the mixer's `AudioMixUtils.MixInto` applies any value including negative without clamping.

**Recommendation:** Document the valid range `[0.0, 1.0]` (or `[0.0, ∞)` for gain > unity) in the interface XML doc, and clamp in either the hot-path `MixInto` call or at the setter of each implementation.

### 2.4 `AudioStreamInfo` and `VideoStreamInfo` nullable fields create silent defaults

Both info structs use nullable fields (`int? SampleRate`, `int? ChannelCount`, etc.). In `AVMixer.AudioPumpLoop`, `src.StreamInfo.SampleRate.GetValueOrDefault(0)` is used to detect an unknown rate — `0` silently disables resampling. A source that forgets to populate `SampleRate` will silently play at the wrong rate.

**Recommendation:** Emit a `MediaErrorCode.AudioSampleRateMismatch` warning (via `AudioSourceError`) when `SampleRate` is `null` and resampling is requested, rather than silently skipping.

### 2.5 `IVideoSource.SeekToFrame(long)` has no default implementation — forces boilerplate

`IVideoSource.SeekToFrame(long frameIndex)` is in the interface. Non-seekable sources (`NDIVideoSource`, etc.) must implement it with a boilerplate `return (int)MediaErrorCode.MediaSourceNonSeekable`. A default interface method would eliminate this.

**Recommendation:**
```csharp
int SeekToFrame(long frameIndex) =>
    (int)MediaErrorCode.MediaSourceNonSeekable; // default
```
Sources that support frame-index seeking override this.

### 2.6 `VideoFrame` constructor throws exceptions — inconsistent with error-code paradigm

`VideoFrame(...)` throws `ArgumentOutOfRangeException` on invalid `width`/`height` and `ArgumentException` on plane shape mismatches. The rest of the framework returns integer error codes. `VideoFrame.FromOwned(...)` also throws.

**Recommendation:** Add a `VideoFrame.TryCreate(...)` static factory returning an `int` error code, consistent with `AudioResampler.Create(...)`. Keep the throwing constructors as a convenience but document the discrepancy.

### 2.7 `AudioResampler.Create` returns concrete type instead of `IAudioResampler`

```csharp
public static int Create(..., out AudioResampler? resampler, ...)
```

This couples the caller to the implementation. `AVMixerConfig.ResamplerFactory` correctly uses `IAudioResampler` as its return type. The `Create` factory should do the same.

**Recommendation:** Change signature to `out IAudioResampler? resampler`.

### 2.8 `AudioResampler` ring buffer — verify per-channel indexing in sinc path

`_ringBuffer = new float[SincKernelSize * targetChannelCount]` stores look-back data for the sinc kernel. The buffer interleaves `targetChannelCount` samples per position. When channel reshaping occurs before the sinc path, verify the ring-write logic correctly handles the reshaped channel count to avoid a latent off-by-one or buffer overread.

---

## 3. AVMixer & MediaPlayer

### 3.1 `StopPlayback()` does not update `AVMixerState` — **Bug**

```csharp
public int StopPlayback() { StopPlaybackThreads(); return MediaResult.Success; }
```

`StopPlaybackThreads()` cancels the `CancellationToken` and joins pump threads but **does not call the protected `Stop()` method**, which is the only place that transitions `_state` to `AVMixerState.Stopped` and stops the clock. After `StopPlayback()`:

- `mixer.State` returns `AVMixerState.Running`
- `mixer.IsRunning` returns `true`
- `_clock.CurrentSeconds` keeps advancing indefinitely

The end-of-stream auto-stop (G.6 branch) also calls `_ = StopPlayback()`, so **when a file finishes playing the mixer reports `IsRunning = true` and the clock ticks forever**.

**Recommendation:** `StopPlayback()` (or `StopPlaybackThreads()`) must call `Stop()` after joining threads. If the intent is to keep the clock running through a pump restart (for gapless playback), make this opt-in and document it explicitly. A clean fix:

```csharp
public int StopPlayback()
{
    StopPlaybackThreads();
    return Stop(); // transitions state + stops clock
}
```

### 3.2 End-of-stream `srcs.All(...)` uses a LINQ closure in a tight loop

```csharp
if (srcs.Length > 0 && srcs.All(s => s.Source.State == AudioSourceState.EndOfStream))
```

This allocates a delegate closure on every "no data" iteration. While infrequent during normal playback, it fires on every empty read at end-of-stream, creating a GC pressure spike precisely when the system is cleaning up.

**Recommendation:** Replace with a manual loop:
```csharp
bool allEos = srcs.Length > 0;
foreach (var (s, _) in srcs)
    if (s.State != AudioSourceState.EndOfStream) { allEos = false; break; }
if (allEos) { _ = StopPlayback(); break; }
```

### 3.3 G.4 source-buffer pruning uses LINQ `.ToList()` inside the audio pump

```csharp
foreach (var key in sourceBufs.Keys.Where(k => !activeIds.Contains(k)).ToList())
    sourceBufs.Remove(key);
```

Two allocations per source-list refresh: the LINQ enumerator and the intermediate list. A two-pass loop over `sourceBufs.Keys` would be zero-allocation and runs equally rarely (only on source-list changes).

### 3.4 `Array.Clear(tempBuf, ...)` executed before source running check

In the fast path, `tempBuf` is cleared unconditionally for every source, including those with `State != AudioSourceState.Running` which are skipped immediately after. Clear should occur only immediately before the `ReadSamples` call.

### 3.5 `AVMixerConfig.ResamplerFactory` parameter tuple is incomplete

```csharp
public Func<int, int, IAudioResampler>? ResamplerFactory { get; init; }
// Parameters: (sourceSampleRate, targetSampleRate)
```

The factory takes only sample rates. If a source's channel count differs from `config.SourceChannelCount`, the resampler will be created with wrong channel parameters. The delegate signature should be:

```csharp
Func<int, int, int, int, IAudioResampler>?
// (sourceSampleRate, sourceChannelCount, targetSampleRate, targetChannelCount)
```

### 3.6 `VideoSyncPolicy.SelectNextFrame` — `Realtime` mode drops all but the last frame unconditionally

In `AVSyncMode.Realtime`, all queued frames except the newest are disposed regardless of the stale threshold. This is intentional for truly live scenarios (always show freshest frame), but differs significantly from `AudioLed` which only drops frames older than `StaleFrameDropThreshold`. The XML doc on `AVSyncMode.Realtime` should explicitly state this "drop all but latest" behaviour to avoid surprises.

### 3.7 `MediaPlayer.BuildDefaultConfig` uses first source's channel count only

```csharp
var firstAudio = binding.PlaybackAudioSources.FirstOrDefault();
var channels = firstAudio?.StreamInfo.ChannelCount.GetValueOrDefault(2) ?? 2;
```

If multiple audio sources have different channel counts, the config is built for the first source only. Sources 2+ will be mixed as if they share the same channel layout, producing potential garbage audio.

**Recommendation:** Document that `MediaPlayer.Play` is designed for single-source playback. For multi-source mixing, callers should construct `AVMixerConfig` explicitly and call `StartPlayback` directly.

### 3.8 `StopPlayback` called from pump thread — potential Join deadlock

In the G.6 EOS path, the audio pump thread calls `_ = StopPlayback()`, which calls `StopPlaybackThreads()`, which calls `_audioPumpThread?.Join(TimeSpan.FromSeconds(4))`. A thread joining itself produces an immediate return (no deadlock) on .NET, but it is still a code smell and prevents the 4-second timeout from being useful. The pump should signal a stop request rather than joining itself.

**Recommendation:** In the EOS path inside `AudioPumpLoop`, break out of the loop and let `StopPlaybackThreads` (called from outside) join the thread normally. Use the existing `_seekChannel` or a dedicated signal:
```csharp
// Inside AudioPumpLoop EOS branch:
_ = _cancelSource?.CancelAsync(); // request shutdown
break;
```

---

## 4. S.Media.PortAudio

### 4.1 `PortAudioEngine.Initialize()` returns `PortAudioInitializeFailed` on double-init

```csharp
if (State == AudioEngineState.Initialized || State == AudioEngineState.Running)
    return (int)MediaErrorCode.PortAudioInitializeFailed;
```

Re-initialization is a caller programming error, not the same failure as the native library failing to load. The same error code is used for both cases, making it impossible for callers to distinguish "already running" from "DLL not found".

**Recommendation:** Return `MediaErrorCode.MediaConcurrentOperationViolation` (or a new dedicated code) for the double-init case.

### 4.2 `PortAudioEngine.Stop()` stops all outputs with no notification

```csharp
foreach (var output in _outputs) output.Stop();
foreach (var input  in _inputs)  input.Stop();
```

Outputs and inputs are stopped silently. Callers holding `IAudioOutput` references will receive `PortAudioStreamStartFailed` on the next `PushFrame` without knowing why. No event or callback notifies them that the engine stopped their output.

**Recommendation:** Document explicitly that `engine.Stop()` invalidates all active streams. Alternatively, fire `AudioDeviceChanged` or a new `OutputStopped` event on each affected output.

### 4.3 "pulse" host API alias silently fails on non-Linux platforms

`NormalizePreferredHostApi("pulse")` returns `"alsa"`. On Windows/macOS, ALSA does not exist, so `RefreshNativeDevices` returns `false` and `Initialize` returns `PortAudioInvalidConfig` with no explanation.

**Recommendation:** Either treat `"pulse"` as equivalent to `null` on non-Linux (use the default host API), or log a warning: `"PreferredHostApi 'pulse' is a Linux/ALSA alias. Ignored on this platform."`.

### 4.4 `PortAudioOutput` — `State` field is not `volatile`; read in hot-path write loop without lock

```csharp
while (framesRemaining > 0)
{
    if (_disposed || State != AudioOutputState.Running || stream == nint.Zero) ...
```

`_stream` and `_disposed` are `volatile`, but `State` is a plain `AudioOutputState` property backed by a non-volatile auto-property. On ARMv8 and other weakly-ordered architectures, the write to `State` in `Stop()` may not be visible to the write-loop thread without a memory barrier. Either make the backing field `volatile` or snapshot `State` before entering the loop (where it is already covered by the lock in `Start()`/`Stop()`).

### 4.5 `PortAudioInput.ReadSamples` applies `Volume` but the mixer also applies `src.Volume`

`PortAudioInput` multiplies captured samples by `Volume` inline before returning them to the mixer. The `AVMixer.AudioPumpLoop` then calls `AudioMixUtils.MixInto(..., src.Volume)` — applying volume a second time. For a `PortAudioInput` with `Volume = 0.5f`, the effective gain through the mixer is `0.25f`.

**Recommendation:** Remove the inline `Volume` application from `PortAudioInput.ReadSamples`. Source volume should be applied exclusively by the consumer (mixer or custom caller), not inside `ReadSamples`. If standalone gain is desired, document that `Volume` is not applied when the source is used inside `AVMixer`.

### 4.6 Fallback/phantom devices are indistinguishable by name from real devices

The phantom fallback output is named `"Default Output"` and input `"Default Input"`. Real PortAudio devices on many systems also have names like `"Default"` or `"default"`. After a successful `Initialize()`, the phantom devices are replaced with real ones — but the name collision could confuse logs and user-facing UI.

**Recommendation:** Prefix phantom device names to make them clearly synthetic, e.g. `"[Fallback] Default Output"`.

---

## 5. S.Media.FFmpeg

### 5.1 `FFmpegAudioSource`/`FFmpegVideoSource` return wrong error code on disposed — **Bug**

```csharp
if (_disposed)
    return (int)MediaErrorCode.MediaInvalidArgument; // Wrong
```

Every other component returns `MediaErrorCode.MediaObjectDisposed` when operated after disposal. `MediaInvalidArgument` misleads callers into thinking the method's parameters are incorrect. This inconsistency affects `Start()`, `Stop()`, `ReadSamples()`, `ReadFrame()`, and `Seek()` on both source types.

**Recommendation:** Replace `MediaInvalidArgument` with `MediaObjectDisposed` in all disposed-guard checks in `FFmpegAudioSource` and `FFmpegVideoSource`.

### 5.2 `FFmpegDecodeOptions` — silent no-op fields mislead callers

The following fields are documented as "reserved" or "not yet implemented" but silently do nothing:

| Field | Status |
|---|---|
| `EnableHardwareDecode` | No effect — documented "reserved" |
| `LowLatencyMode` | No effect — documented "reserved" |
| `UseDedicatedDecodeThread` | No effect — documented "reserved", defaults to `true` |
| `DecodeThreadCount` | Validated + clamped but **not passed to `AVCodecContext.thread_count`** |

A user who sets `EnableHardwareDecode = true` expecting VAAPI or DXVA2 acceleration will be silently ignored. `DecodeThreadCount` is the most misleading — it is normalized against `ProcessorCount` and then discarded without any effect on decode performance.

**Recommendation:** Either implement these fields or mark them `[Obsolete("Not yet implemented.")]`. At minimum, emit a `MediaErrorCode.FFmpegInvalidConfig` (or a log warning) when non-default values are set for unimplemented options, so callers are not left wondering why their performance settings have no effect.

### 5.3 `FFSharedDemuxSession` has a dead `_pipelineGate` field

```csharp
private readonly Lock _pipelineGate = new(); // never acquired anywhere
private readonly Lock _gate = new();
```

`_pipelineGate` is declared but never used. It suggests an incomplete refactoring. Remove it.

### 5.4 `FFmpegMediaItem` stream constructors have no non-throwing `Create` overloads

The `FFmpegOpenOptions`-based path has `static int Create(FFmpegOpenOptions, out FFmpegMediaItem?)` which catches `DecodingException`. However the `Stream`-based constructors:

```csharp
public FFmpegMediaItem(Stream inputStream, ...)
public FFmpegMediaItem(Stream inputStream, FFmpegOpenOptions openOptions, ...)
```

…have no error-code factory counterparts. Applications using the stream path must catch `DecodingException`, breaking the established error-code pattern.

**Recommendation:** Add `static int Create(Stream inputStream, out FFmpegMediaItem? item, ...)` overloads that wrap the throwing constructors in try/catch, consistent with the `FFmpegOpenOptions` factory.

### 5.5 `FFmpegAudioSource.PositionSeconds` accumulates by frame count, not PTS

```csharp
_positionSeconds += (double)framesRead / Math.Max(1, StreamInfo.SampleRate.GetValueOrDefault(48_000));
```

Position accumulates by counting decoded frames rather than using the packet's presentation timestamp. If the decoder returns variable-length frames, skips packets during seeking, or encounters gaps, `PositionSeconds` drifts from the true timeline position.

**Recommendation:** Propagate PTS from `FFSharedDemuxSession.ReadAudioSamples` and use it to update `_positionSeconds`, falling back to frame counting only when PTS is unavailable (`AV_NOPTS_VALUE`).

### 5.6 `FFmpegDecodeOptions.MaxQueuedPackets` default of 4 is very low

`MaxQueuedPackets = 4` and `MaxQueuedFrames = 4` are the defaults. For high-bitrate video (4K H.265), a queue of 4 packets may be insufficient to absorb decoder latency, causing starvation in the video pump. For audio, 4 packets is approximately 85 ms at 48 kHz/1024 samples/packet — acceptable but tight.

**Recommendation:** Increase defaults to at least `MaxQueuedPackets = 8`, `MaxQueuedFrames = 8`, or document the tuning guidance clearly.

---

## 6. S.Media.NDI

### 6.1 `NDIEngine` does not implement `IMediaEngine` — **inconsistency**

```csharp
public sealed class NDIEngine : IDisposable  // missing : IMediaEngine
```

`IMediaEngine` is defined in `S.Media.Core.Runtime` and implemented by `PortAudioEngine`. `NDIEngine` has `Terminate()` and `IsInitialized` that satisfy the interface contract, but it does not formally declare `IMediaEngine`. Code accepting `IMediaEngine` (e.g. a shutdown coordinator) cannot accept `NDIEngine`.

**Recommendation:** Change to `NDIEngine : IMediaEngine, IDisposable`. Verify the same for `S.Media.MIDI`'s engine class.

### 6.2 `NDICaptureCoordinator` — concurrent audio/video polling from separate threads

When `NDIAudioSource.ReadSamples` and `NDIVideoSource.ReadFrame` are polled concurrently (e.g. from the mixer's audio pump and video decode threads), both call into `NDICaptureCoordinator`. Each checks its internal queue; when both are empty, both call `CaptureOnce`. The semaphore `Wait(0)` ensures only one native call runs — the other silently returns `false`. One entire frame (audio or video) is dropped per cycle where both queues are empty simultaneously.

This is a known architectural limitation; `NDIFrameSyncCoordinator` handles this correctly. Add a visible warning:

**Recommendation:** Add to `NDICaptureCoordinator` XML doc:
> *Not suitable for simultaneous audio and video polling from separate threads. Use `NDIFrameSyncCoordinator` when audio and video are consumed concurrently (e.g. inside `AVMixer`).*

### 6.3 `NDIVideoSource._framesDropped` incremented when source is intentionally stopped

```csharp
if (State != VideoSourceState.Running)
{
    lock (_gate) { _framesDropped++; }
    return (int)MediaErrorCode.MediaSourceNotRunning;
}
```

Incrementing `_framesDropped` when the source is deliberately stopped inflates the diagnostic counter. A "stopped source was polled" is not a dropped frame — it is a rejected read.

**Recommendation:** Add a separate `_rejectedReads` counter for reads rejected due to non-running state. Reserve `_framesDropped` for frames lost during active capture (queue overflow, stale discard, etc.).

### 6.4 `NDIVideoOutput.EnableVideo` checked per-frame rather than at `Start()`

```csharp
// Inside PushFrame, per-frame:
if (!Options.EnableVideo)
    return (int)MediaErrorCode.NDIInvalidOutputOptions;
```

If `EnableVideo = false` is a configuration choice, this should be enforced at `Start(VideoOutputConfig)` (returning an error immediately) rather than consuming push overhead on every frame.

### 6.5 `NDIFrameSyncCoordinator.TryReadAudio` — verify zero-sample probe `FreeAudio` semantics

```csharp
_frameSync.CaptureAudio(out var probe, 0, 0, 0); // zero-sample probe
if (probe.PData != nint.Zero) _frameSync.FreeAudio(probe);
```

Cross-reference the NDI SDK docs to confirm whether `NDIlib_framesync_capture_audio_v2` with `no_samples=0` can ever set `PData` to a non-null value. If the SDK guarantees `PData == null` for zero-sample calls, the conditional free is unnecessary (but harmless). If the SDK may return a non-null `PData` for the probe, the conditional is correct but should be documented.

### 6.6 `NDISourceOptions` missing receiver bandwidth selection

The NDI SDK's `NdiRecvCreateV3` has a `bandwidth` field (`NDIlib_recv_bandwidth_e`) controlling whether the receiver requests best-quality video, low-bandwidth proxy video, or audio-only. This is inaccessible via `NDISourceOptions` / `NDIIntegrationOptions`.

**Recommendation:** Add `NDIReceiverBandwidth` enum (`BestQuality`, `LowBandwidth`, `AudioOnly`) and a corresponding property to `NDISourceOptions`.

---

## 7. S.Media.OpenGL

### 7.1 `OpenGLVideoOutput.PushFrame` blocks the mixer presentation thread during sleep

```csharp
if (delay > TimeSpan.Zero)
    Thread.Sleep(delay); // blocks AVMixer.VideoPresent thread
```

When using `VideoDispatchPolicy.DirectThread` (the default), the mixer's `VideoPresentLoop` thread calls `PushFrame` directly on each output in sequence. A sleep in one output delays all others. For use cases with multiple outputs or latency-sensitive scenarios, this is a problem.

**Recommendation:** Add a strong recommendation in `OpenGLVideoOutput` and `IVideoOutput.PushFrame` XML doc that implementations with synchronous timing sleeps should be used with `VideoDispatchPolicy.BackgroundWorker`. Alternatively, make `OpenGLVideoOutput` always non-blocking and push timing to a dedicated background timer.

### 7.2 `OpenGLVideoEngine.PushFrame` allocates under lock per frame

```csharp
lock (_gate)
{
    clones = cloneIds.Select(...).Where(...).Cast<OpenGLVideoOutput>().ToArray();
}
```

The LINQ query and resulting array are allocated on every `PushFrame` call, under `_gate`. For a stable clone topology (the common case), this can be eliminated by caching the clone array and invalidating it only when `AttachCloneOutput`/`RemoveOutput` is called.

### 7.3 `Conversion/` and `Upload/` directories are empty — output is a timing stub

```
S.Media.OpenGL/
    Conversion/   ← empty
    Upload/       ← empty
```

`OpenGLVideoOutput.PushFrame` performs timing and diagnostics but does not upload pixels to a GPU texture. The `Surface` property returns an `OpenGLSurfaceMetadata` struct but there is no actual GL texture handle or pixel transfer. The output is a skeleton.

**Recommendation:** Either implement the pixel upload pipeline or add a prominent `[Obsolete]` / XML warning on `OpenGLVideoOutput` and `OpenGLVideoEngine`: _"PushFrame currently performs timing and clone dispatch only. GPU texture upload is not yet implemented."_

---

## 8. PALib — Native PortAudio Wrapper

### 8.1 Host-API extension directories are empty

```
PALib/
    ALSA/     ← empty
    ASIO/     ← empty
    CoreAudio/← empty
    WASAPI/   ← empty
    JACK/     ← empty
    ...
```

PortAudio exposes per-host-API stream parameters (e.g. `PaWasapiStreamInfo` for WASAPI exclusive mode, `PaAsioStreamInfo` for ASIO buffer size selection, `PaJackConnectionManager`). Without these, pro-audio driver configurations are inaccessible.

**Recommendation:** Implement at minimum `PaWasapiStreamInfo` (Windows low-latency exclusive mode) and `PaJackStreamInfo` (Linux JACK routing). If these are out of scope, remove the empty directories to avoid misleading contributors.

### 8.2 `Pa_GetVersionText` is `[Obsolete]` — confirm no internal callers

`Pa_GetVersionText` is correctly marked obsolete (deprecated since PortAudio 19.5.0). Grep the entire solution for any remaining internal calls; if none exist, consider removing the method entirely.

### 8.3 Trace-level logging on `Pa_GetDeviceInfo` may add overhead during device enumeration

`Pa_GetDeviceInfo` logs at `LogLevel.Trace` on every call. During `RefreshNativeDevices`, this is called once per discovered device (potentially dozens of calls). On systems with TRACE logging enabled (e.g. during development), this generates substantial log noise during initialization. Consider moving the per-device log to `LogLevel.Debug` or making it a single summary log after enumeration.

---

## 9. NDILib — Native NDI Wrapper

### 9.1 `NDIlib_send_flush_async` exposes `nint p_video_data` — type safety risk

```csharp
internal static partial void NDIlib_send_flush_async(nint p_instance, nint p_video_data);
```

This entry point is the flush-async overload that requires passing `nint.Zero` as `p_video_data`. Any non-zero value would corrupt the sender silently. The intent should be encapsulated:

```csharp
internal static void NDIlib_send_flush_async(nint instance)
    => NDIlib_send_flush_async_Import(instance, nint.Zero);
```

### 9.2 NDI bandwidth mode not surfaced to `NDISourceOptions` (see §6.6)

`NdiRecvCreateV3.bandwidth` is bound at the native level but inaccessible from the managed `NDISourceOptions`. See §6.6 for the full recommendation.

### 9.3 `NDIlib_recv_get_web_control` return value is not freed

```csharp
[LibraryImport(LibraryName)]
internal static partial nint NDIlib_recv_get_web_control(nint p_instance);
```

The NDI SDK documentation for `NDIlib_recv_get_web_control` states the returned string must be freed with `NDIlib_recv_free_string`. If `NDIWrappers.cs` exposes this to managed callers without a corresponding free, the string memory leaks. Verify the wrapper correctly calls `NDIlib_recv_free_string` after consuming the result.

---

## 10. PMLib — Native PortMidi Wrapper

### 10.1 `MIDIInputDevice` poll sleep is ~10–15 ms on Windows — MIDI latency concern

`Thread.Sleep(PollIntervalMs)` where `PollIntervalMs = 1`. On Windows without `timeBeginPeriod(1)`, this typically sleeps 10–15 ms, adding perceptible MIDI latency for real-time performance.

**Recommendation:** Either:
- P/Invoke `timeBeginPeriod(1)` from `winmm.dll` in `PMLib`'s module initializer and `timeEndPeriod(1)` on shutdown.
- Or document the Windows latency limitation and recommend setting `PollIntervalMs = 0` with a `Thread.SpinWait` / busy-wait loop for latency-critical MIDI.

### 10.2 `MIDIDevice` exposes `Stream` as `nint` without concurrency protection

The `PmStream*` handle is accessible to subclasses as `nint`. `MIDIInputDevice.PollLoop` reads `Stream` on a background thread while `Close()` can set it to `nint.Zero` from a different thread. `_polling = false` → join → `Close()` ordering prevents a race in the happy path, but a direct call to `base.Close()` without stopping the poll first would be unsafe.

**Recommendation:** Make `Stream` `volatile` or protect all accesses through a lock.

### 10.3 `MIDIInputDevice` event handlers can crash the poll thread

`MessageReceived` and `SysExReceived` are fired directly from `PollLoop`. An unhandled exception in a handler propagates into `PollLoop`, crashes it, and the `_polling` flag remains `true` (thread exits silently). Subsequent `Close()` calls would hang on `_pollThread.Join()` because the thread has already exited.

**Recommendation:**
```csharp
try { MessageReceived?.Invoke(this, msg); }
catch (Exception ex) { Logger.LogError(ex, "Exception in MIDI MessageReceived handler (deviceId={Id})", DeviceId); }
```

### 10.4 `MIDIOutputDevice.Latency = 0` documentation is incomplete

The doc says "0 ignores timestamps and delivers messages immediately." More precisely, `Latency = 0` bypasses PortMidi's software timestamping and sends through the hardware MIDI port's own scheduler. For real-time output (live keyboard), `0` is correct. For sequenced output with precise scheduling, a non-zero latency is required. Expand the XML doc to explain this distinction.

---

## 11. OSCLib

### 11.1 `OSCClient` and `OSCServer` implement `IAsyncDisposable` but not `IDisposable`

```csharp
public sealed class OSCClient : IOSCClient  // DisposeAsync only
public sealed class OSCServer : IOSCServer  // DisposeAsync only
```

A synchronous `using` statement will not call `DisposeAsync`, leaving the underlying `UdpClient` unclosed until finalization. In synchronous contexts (common in older codebases or non-async test fixtures), this is a silent resource leak.

**Recommendation:** Implement `IDisposable` on both classes as a synchronous counterpart:
```csharp
public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
```
Or implement it separately, synchronously closing the `UdpClient`.

### 11.2 OSC bundle dispatch is recursive without a depth limit

```csharp
foreach (var child in bundle.Elements)
    await DispatchPacketAsync(child, remote, bundle.TimeTag, receivedAt, cancellationToken);
```

A malformed or adversarial OSC packet with deeply nested bundles could cause a stack overflow or large async continuation chain. Add a depth limit:

```csharp
private async Task DispatchPacketAsync(..., int depth = 0)
{
    if (depth > 8) return; // discard malformed deep bundles
    // ...
    foreach (var child in bundle.Elements)
        await DispatchPacketAsync(child, remote, ..., depth + 1);
}
```

### 11.3 `OSCServer._oversizeDrops` counter not reset on `StartAsync`

The oversize-drop counter persists across `StopAsync`/`StartAsync` cycles, making per-session diagnostics impossible without an explicit reset API.

**Recommendation:** Reset `_oversizeDrops` and `_lastOversizeLogUtc` in `StartAsync`.

### 11.4 `OSCClient.SendAsync` throws on oversize — inconsistent with error-code pattern

```csharp
throw new InvalidOperationException($"OSC packet size {encoded.Length} exceeds configured max {Options.MaxPacketBytes}.");
```

All `S.Media.*` APIs return `int` error codes; this throws an exception for an expected, handleable condition. Consider returning a `ValueTask<int>` or adding an `OSCSendResult` discriminated union as a long-term improvement.

---

## 12. S.Media.MIDI

### 12.1 `S.Media.MIDI` engine — verify `IMediaEngine` implementation

The `S.Media.MIDI.csproj` references both `S.Media.Core` and `PMLib`. The engine class (if it exists) should implement `IMediaEngine`. Verify this is consistent with §6.1 and §14.3.

### 12.2 `PMLib.MIDIInputDevice` events fire on the polling thread — ensure exception safety

See §10.3. This concern applies equally to `S.Media.MIDI` if it delegates event firing without its own exception guard.

---

## 13. Error Code System

### 13.1 MIDI error codes (900–949) are in the `GenericCommon` range (0–999)

```csharp
MIDINotInitialized = 900,
MIDIConcurrentOperationRejected = 918,
```

All other subsystems have their own dedicated contiguous block. MIDI at 900 inside "GenericCommon" is handled via a special case in `ErrorCodeRanges.ResolveArea`, which works, but is architecturally inconsistent and makes the range boundary documentation confusing.

**Recommendation:** Move MIDI codes to a dedicated range (e.g. 6000–6099) in a future breaking-change release.

### 13.2 `MediaResult.Success = 0` is used inconsistently as `return 0`

Both `return MediaResult.Success;` and `return 0;` appear throughout the codebase. While functionally identical, consistency aids readability and future-proofing (if `Success` is ever redefined, which is unlikely but not impossible).

**Recommendation:** Standardize on `return MediaResult.Success;` everywhere.

### 13.3 No error code for "source not yet started" vs. "source was stopped"

`MediaSourceNotRunning = 11` covers both "never started" and "explicitly stopped". Callers cannot distinguish a source that was never started from one that completed playback and was stopped. Consider `MediaSourceNotStarted = 12` for clarity in diagnostic messaging.

---

## 14. Cross-Cutting Concerns

### 14.1 `AudioFrame` buffer aliasing (see §2.1)

Documented in §2.1. Critical contract gap in `IAudioSink`.

### 14.2 Exception vs. error-code inconsistency in public constructors

The following public constructors **throw exceptions** rather than returning error codes:

| Type | Exception thrown |
|---|---|
| `VideoFrame(...)` | `ArgumentOutOfRangeException`, `ArgumentException` |
| `FFmpegMediaItem(FFmpegOpenOptions, ...)` | `DecodingException` |
| `OSCClient(IPEndPoint, ...)` | `ArgumentOutOfRangeException` |
| `OSCServer(OSCServerOptions, ...)` | `ArgumentOutOfRangeException` |

Non-throwing factory overloads exist for `FFmpegMediaItem` (partially) and `AudioResampler`. `VideoFrame` and `OSC*` classes have no factories.

A clear style rule: *"Constructors throw for programmer errors (`ArgumentNullException`, etc.); non-throwing `Create` / `TryCreate` factories exist for all runtime-failable construction"* — should be documented and applied consistently.

### 14.3 `IMediaEngine` not universally implemented

| Engine | Implements `IMediaEngine` |
|---|---|
| `PortAudioEngine` | ✅ (via `IAudioEngine`) |
| `NDIEngine` | ❌ (only `IDisposable`) |
| `S.Media.MIDI` engine | Needs verification |

All engines should implement `IMediaEngine` for framework consistency.

### 14.4 Logging is per-library with no unified `ILoggerFactory` injection

| Library | Logging mechanism |
|---|---|
| `PALib` | `PALibLogging.Configure(ILogger)` — global singleton |
| `S.Media.PortAudio` | `PortAudioEngine.ConfigureLogging(ILogger)` — global singleton |
| `NDILib` | No managed logging |
| `OSCLib` | `ILogger<T>` per-instance ✅ |
| `PMLib` | `PMLibLogging` — presumably global singleton |
| `S.Media.NDI` | Uses `ILogger` injected via engine or constructor? |

An application using `Microsoft.Extensions.DependencyInjection` must manually wire each static logger at startup. Long-term, engine constructors should accept `ILoggerFactory?` and create typed loggers internally, following the `OSCLib` pattern.

### 14.5 `SeekToSample(long)` is absent from `IAudioSource`

`IVideoSource` has `SeekToFrame(long)`. `IAudioSource` has only `Seek(double positionSeconds)`. For gapless loop editing or sample-accurate DAW-style sync, sample-index seeking is valuable. Consider `int SeekToSample(long sampleIndex)` as an optional default-returning-`MediaSourceNonSeekable` method.

### 14.6 Thread-naming convention is consistent — maintain it

Background threads are consistently named:
- `"AVMixer.AudioPump"`, `"AVMixer.VideoDecode"`, `"AVMixer.VideoPresent"`
- `"AVMixer.Worker-{output.Id}"`
- `"S.Media.FFmpeg.SharedDemuxSession"`
- `"MIDIInput[{DeviceId}]"`

All future background threads should follow the same `"Library.Component[-InstanceId]"` pattern.

### 14.7 Dirty-flag write occurs outside the lock in `AddAudioOutput` / routing rule mutations

```csharp
public int AddAudioOutput(IAudioSink output)
{
    lock (_gate) _audioOutputs.Add(output);
    _audioOutputsNeedsUpdate = true; // written outside _gate
    return MediaResult.Success;
}
```

The `volatile bool` dirty flag is written after releasing the lock. On ARMv8 (AArch64), the store-store reordering between the list mutation (inside lock) and the flag write (outside lock) is prevented by the lock's release barrier. This is correct on .NET's memory model but is subtle. Moving the dirty-flag write inside the lock makes the intent explicit and is marginally cleaner:

```csharp
lock (_gate) { _audioOutputs.Add(output); _audioOutputsNeedsUpdate = true; }
```

---

## 15. Prioritised Fix Roadmap

### P1 — Critical Bugs

| # | Description | File(s) |
|---|---|---|
| P1.1 | `StopPlayback()` does not update `AVMixerState` or stop the clock | `AVMixer.cs` |
| P1.2 | Audio pump self-join at EOS via `StopPlayback` | `AVMixer.cs` |
| P1.3 | `FFmpegAudioSource`/`FFmpegVideoSource` return `MediaInvalidArgument` on disposed | `FFmpegAudioSource.cs`, `FFmpegVideoSource.cs` |
| P1.4 | `PortAudioInput.Volume` applied twice through mixer | `PortAudioInput.cs` |
| P1.5 | `IAudioSink.PushFrame` buffer-aliasing contract undocumented | `IAudioSink.cs` |

### P2 — Correctness / Safety

| # | Description | File(s) |
|---|---|---|
| P2.1 | `NDIEngine` does not implement `IMediaEngine` | `NDIEngine.cs` |
| P2.2 | `FFmpegDecodeOptions` silent no-op fields (`EnableHardwareDecode`, etc.) | `FFmpegDecodeOptions.cs` |
| P2.3 | Dead `_pipelineGate` field in `FFSharedDemuxSession` | `FFSharedDemuxSession.cs` |
| P2.4 | `OSCServer`/`OSCClient` implement `IAsyncDisposable` but not `IDisposable` | `OSCClient.cs`, `OSCServer.cs` |
| P2.5 | OSC bundle dispatch has no recursion depth limit | `OSCServer.cs` |
| P2.6 | `MIDIInputDevice` event handler exceptions crash the poll thread | `MIDIInputDevice.cs` |
| P2.7 | `NDIVideoOutput.EnableVideo` checked per-frame not at `Start()` | `NDIVideoOutput.cs` |
| P2.8 | `PortAudioOutput` `State` not `volatile` — read on hot-path without lock | `PortAudioOutput.cs` |

### P3 — API Quality / Usability

| # | Description | File(s) |
|---|---|---|
| P3.1 | `AudioResampler.Create` should return `out IAudioResampler?` | `AudioResampler.cs` |
| P3.2 | `AVMixerConfig.ResamplerFactory` missing channel-count parameters | `AVMixerConfig.cs` |
| P3.3 | `VideoFrame` — add non-throwing `TryCreate` factory | `VideoFrame.cs` |
| P3.4 | `FFmpegMediaItem` stream constructors need non-throwing `Create` overloads | `FFmpegMediaItem.cs` |
| P3.5 | `NDISourceOptions` missing `ReceiverBandwidth` | `NDISourceOptions.cs` |
| P3.6 | `PortAudioEngine.Initialize()` wrong error code for double-init | `PortAudioEngine.cs` |
| P3.7 | "pulse" alias fails silently on non-Linux | `PortAudioEngine.cs` |
| P3.8 | `IVideoSource.SeekToFrame` needs a default `NonSeekable` implementation | `IVideoSource.cs` |
| P3.9 | `NDISendTest` — verify `NDIlib_recv_get_web_control` string free | `NDILib/NDIWrappers.cs` |

### P4 — Performance / Polish

| # | Description | File(s) |
|---|---|---|
| P4.1 | EOS `srcs.All(...)` LINQ closure in audio pump | `AVMixer.cs` |
| P4.2 | G.4 pruning LINQ `.ToList()` on source refresh | `AVMixer.cs` |
| P4.3 | `Array.Clear(tempBuf)` before source-running check | `AVMixer.cs` |
| P4.4 | `OpenGLVideoEngine.PushFrame` LINQ allocation under lock | `OpenGLVideoEngine.cs` |
| P4.5 | MIDI error codes should move to dedicated range (breaking change) | `MediaErrorCode.cs` |
| P4.6 | PMLib 1 ms sleep is 10–15 ms on Windows — MIDI latency | `MIDIInputDevice.cs` |
| P4.7 | `OpenGLVideoOutput` is a timing stub — document or implement GPU upload | `OpenGLVideoOutput.cs` |
| P4.8 | PALib empty host-API extension directories | `ALSA/`, `ASIO/`, etc. |
| P4.9 | `Pa_GetVersionText` — confirm no internal callers remain | `Native.cs` (PALib) |
| P4.10 | Dirty-flag writes should be inside the lock for clarity | `AVMixer.cs` |

---

*Review01.md — Initial pass complete 2026-04-01. Items will be marked ✅ as resolved.*

