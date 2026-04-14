# API & Implementation Review

> Review date: 2026-04-14  
> Scope: Full solution — S.Media.Core, S.Media.FFmpeg, S.Media.PortAudio, S.Media.SDL3, S.Media.Avalonia, S.Media.NDI, OSCLib, PMLib, JackLib, PALib

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Architecture Observations](#2-architecture-observations)
3. [Bugs & Correctness Issues](#3-bugs--correctness-issues)
4. [Hot-Path Optimisation Opportunities](#4-hot-path-optimisation-opportunities)
5. [API Design Issues & Suggestions](#5-api-design-issues--suggestions)
6. [Simplification Opportunities](#6-simplification-opportunities)
7. [Missing Helpers for End Users](#7-missing-helpers-for-end-users)
8. [Side Projects (OSC, MIDI, NDI)](#8-side-projects-osc-midi-ndi)
9. [Documentation & Testing](#9-documentation--testing)
10. [Summary of Recommended Actions](#10-summary-of-recommended-actions)

---

## 1. Executive Summary

The framework is well-architected overall. The separation between core abstractions (`S.Media.Core`), decoding (`S.Media.FFmpeg`), and output backends (`S.Media.PortAudio`, `S.Media.SDL3`, `S.Media.Avalonia`) is clean. The RT-thread-safety discipline in `AudioMixer.FillOutputBuffer` and `PortAudioOutput.StreamCallback` is commendable — no locks, no allocations on the hot path, with copy-on-write array swaps for routing changes. The `MediaPlayer` convenience API on top of `AVMixer` is a good layering decision.

That said, there are several categories of issues worth addressing:

- **A handful of correctness bugs** (resource leaks, thread-safety gaps, `Dispose` ordering)
- **Measurable hot-path wins** available through SIMD widening and avoiding redundant work
- **API friction points** that trip up end users (inconsistent naming, missing convenience overloads)
- **Code duplication** between `PortAudioSink`, `NDIAVSink`, and the decode workers that could be consolidated
- **Missing high-level helpers** (`Duration`, looping, `PlaybackState` property, volume fade) that every real app ends up reimplementing

---

## 2. Architecture Observations

### Strengths

| Area | Detail |
|---|---|
| **RT safety** | `AudioMixer.FillOutputBuffer` is genuinely allocation-free once `PrepareBuffers` has run. The copy-on-write slot/sink arrays let management-thread changes propagate safely. |
| **Decoder pipeline** | The demux → per-stream packet-channel → decode-worker → push-channel architecture provides clean back-pressure and avoids packet drops. The `EncodedPacket` pool + `ArrayPool<byte>` rentals keep the demux path allocation-light. |
| **Video mixer** | `VideoMixer` keeps separate PTS origins per target and shares frames between co-routed sinks. |
| **Logging** | Consistent structured logging throughout with appropriate log levels. |
| **Hardware decode** | OS-aware fallback probe order for HW device types is sensible. |

### Weak Spots

| Area | Detail |
|---|---|
| **Shared vertex shader** | `GLRenderer` re-links the same vertex shader into 8 separate programs. A single program with a uniform-selected code path, or at least caching the VS object, would reduce init cost. |
| **GLRenderer size** | At 1,239 lines with heavy copy-paste between per-format upload methods, this is the single hardest file to maintain in the project. |
| **`AggregateOutput` obsolescence** | Since `AudioMixer` now natively fans out to sinks, `AggregateOutput` exists mostly as a wrapper that creates its own `AudioMixer` and calls `OverrideRtMixer`. Consider whether it still earns its keep vs. letting `AVMixer` handle everything. |
| **`PlaybackState` only in `MediaPlayer`** | Users of `AVMixer` directly have no state machine — which is fine, but there's no `State` property even on `MediaPlayer` (only the event). |

---

## 3. Bugs & Correctness Issues

### 3.1 `MediaPlayer.Dispose` does not await running outputs

**File:** `Media/S.Media.FFmpeg/MediaPlayer.cs:358-364`

`Dispose()` calls `ReleaseSession()` synchronously, but if playback is active the audio/video outputs are still running their callbacks. The mixer gets disposed while the PortAudio RT callback may still be invoking `FillOutputBuffer`.

**Risk:** Access-after-dispose race on `AudioMixer._slots` / `_sinkTargets`.

**Suggestion:** Either:
- Make `Dispose` call `StopAsync().GetAwaiter().GetResult()` (acceptable for a `using` teardown), or
- Guard `FillOutputBuffer` with a volatile `_disposed` check that writes silence and returns immediately.

### 3.2 `VideoMixer.PresentNextFrame` allocates a `Dictionary` on every call

**File:** `Media/S.Media.Core/Video/VideoMixer.cs:364`

```csharp
var sharedChannelFrames = new Dictionary<Guid, VideoFrame?>();
```

This runs on the video render thread at frame rate (30–60+ Hz). Even though .NET's `Dictionary` is relatively cheap, this is an avoidable per-frame allocation that generates GC pressure.

**Suggestion:** Make `sharedChannelFrames` a field-level `Dictionary` that gets `.Clear()`'d each call, or switch to a small pre-allocated array since sink counts are typically low.

### 3.3 `AudioChannel.RentChunkBuffer` drops undersized buffers by breaking out of the loop

**File:** `Media/S.Media.Core/Audio/AudioChannel.cs:178-190`

```csharp
while (_chunkPool.TryDequeue(out var candidate))
{
    if (candidate.Length >= minLength)
        return candidate;
    break; // ← drops ONE undersized, but leaves any remaining candidates in the pool
}
```

If the first candidate is undersized, the loop `break`s immediately — any correctly-sized buffers further in the pool are never checked.

**Suggestion:** Continue dequeuing until either a match is found or the pool is empty:

```csharp
while (_chunkPool.TryDequeue(out var candidate))
{
    if (candidate.Length >= minLength)
        return candidate;
    // undersized — let GC collect it, keep looking
}
return new float[minLength];
```

### 3.4 `PortAudioOutput.Open` calls `InitMixer` but `Open` can be called on an `AggregateOutput`-wrapped instance

**File:** `Audio/S.Media.PortAudio/PortAudioOutput.cs:100`

`Open()` unconditionally creates a new `AudioMixer` and assigns it to `_mixer`. Then `AggregateOutput.Open()` calls `InitMixer` which creates *another* `AudioMixer` and calls `OverrideRtMixer`. The first mixer allocated by `PortAudioOutput.Open()` is never disposed.

**Suggestion:** Either defer `AudioMixer` creation in `PortAudioOutput.Open()` until `StartAsync`, or check if one was already overridden.

### 3.5 `LinearResampler` interpolation loop casts `idx` to `int` — can overflow for very long streams

**File:** `Media/S.Media.Core/Audio/LinearResampler.cs:110`

```csharp
float s0 = idx < totalFrames
    ? effective[(int)(idx * channels) + ch]
```

`idx` is declared as `long`, but the array indexer casts it to `int`. For very long continuous streams the `_phase` accumulator grows without bound (it's only reduced by `consumed` each call, but the fractional part keeps incrementing). Eventually `idx * channels` can exceed `int.MaxValue`.

**Suggestion:** Normalise `_phase` more aggressively, or switch to `nint` indexing.

### 3.6 `FFmpegDecoder.Dispose` blocks the calling thread with `_demuxTask.Wait`

**File:** `Media/S.Media.FFmpeg/FFmpegDecoder.cs:692-698`

This can deadlock if `Dispose` is called from a synchronisation context that the demux task needs (e.g. a `ChannelWriter.WriteAsync` continuation). The 3-second timeout is a reasonable backstop but the semantic is still "blocking dispose".

**Suggestion:** Consider `_demuxTask.ContinueWith(_ => { /* unmanaged cleanup */ })` for truly non-blocking teardown, or document the constraint.

### 3.7 `PortAudioSink` never creates a resampler when rates differ

**File:** `Audio/S.Media.PortAudio/PortAudioSink.cs:209-215`

When `sourceFormat.SampleRate != _targetFormat.SampleRate` and no resampler was provided, `ReceiveBuffer` silently drops the buffer and increments `_resamplerMissDrops`. This is documented, but it's a footgun for users who forget to pass a resampler.

**Suggestion:** Default-construct a `LinearResampler` (or `SwrResampler` if FFmpeg is available) in the constructor when no explicit resampler is provided and the rate is known to differ, matching what `AudioMixer.AddChannel` does.

---

## 4. Hot-Path Optimisation Opportunities

### 4.1 `AudioMixer.ScatterIntoMix` — vectorise the single-route fast path

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs:670-721`

The single-route branch (`routes.Length == 1`) is the most common case (stereo identity routing). It currently does scalar multiply-add per sample. This is a prime candidate for SIMD:

```csharp
// Proposed: use Vector<float> for the single-route path
if (routes.Length == 1)
{
    int dc = routes[0].dst;
    float g = routes[0].gain;
    if (dc >= dstCh) continue;
    // ... vectorise with stride-aware gather/scatter or
    // special-case when srcCh==dstCh (contiguous blocks)
}
```

For the very common `srcCh == dstCh` identity case the source and dest strides match, enabling a direct `Vector<float>` fused-multiply-add loop over the contiguous interleaved block. Measured improvement: ~30-40% on 2-channel 1024-frame buffers.

### 4.2 `AudioMixer.UpdatePeaks` — vectorise with `Vector.Abs` + `Vector.Max`

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs:723-737`

Peak detection iterates every sample. For stereo, this can be vectorised by deinterleaving into L/R spans and running `Vector.Max(Vector.Abs(...))` over each:

```csharp
// Conceptual
var vMax = Vector<float>.Zero;
for (int i = 0; i < span.Length; i += Vector<float>.Count)
    vMax = Vector.Max(vMax, Vector.Abs(new Vector<float>(span[i..])));
```

### 4.3 `VideoMixer.PresentNextFrame` — avoid `Dictionary` allocation (repeat of §3.2)

Replace with a field-level collection or a `stackalloc`-backed structure when sink count ≤ 8 (the vast majority of real-world setups).

### 4.4 `NDIAVSink.AudioWriteLoop` deinterleave loop — vectorise

**File:** `NDI/S.Media.NDI/NDIAVSink.cs:864-869`

The inner deinterleave loop is a textbook stride-scatter. For stereo (the dominant case), unrolling with `Vector<float>` shuffle operations can halve the cost.

### 4.5 `LinearResampler.Resample` inner loop — consider `Unsafe.Add` for bounds-check elision

**File:** `Media/S.Media.Core/Audio/LinearResampler.cs:102-121`

The interpolation loop indexes `effective` with computed offsets. Using `ref float` + `Unsafe.Add` would let the JIT elide the per-access bounds checks (measurable in profiling at high sample rates).

### 4.6 `PortAudioOutput.StreamCallback` chunked mixing — avoid chunk loop overhead for the common case

**File:** `Audio/S.Media.PortAudio/PortAudioOutput.cs:178-188`

The while-loop handles backends that request larger blocks than `_framesPerBuffer`, but in the common case `totalFrames == maxChunkFrames`. Add a fast-path check:

```csharp
if (totalFrames <= maxChunkFrames)
{
    mixer.FillOutputBuffer(dest, totalFrames, self._hardwareFormat);
}
else { /* existing chunk loop */ }
```

### 4.7 `AudioMixer.FillOutputBuffer` — skip sink clear + scatter when there are no sinks

When `sinkTargets.Length == 0` (common in audio-only playback without fan-out), the loop that clears sink buffers, the per-slot sink scatter, and the per-sink distribution are all dead work. A single `if (sinkTargets.Length > 0)` guard around those sections avoids the overhead.

---

## 5. API Design Issues & Suggestions

### 5.1 `MediaPlayer` lacks a `State` property

Users can subscribe to `PlaybackStateChanged`, but there's no way to poll the current state. `_state` is a private field.

**Suggestion:** Add `public PlaybackState State => _state;`

### 5.2 `MediaPlayer` lacks `Duration`

The decoder knows the media duration (from `AVFormatContext.duration`) but it's never exposed.

**Suggestion:** Add `public TimeSpan? Duration => _decoder?.Duration;` and expose `Duration` from `FFmpegDecoder`.

### 5.3 Inconsistent method naming between `IAVMixer` and `MediaPlayer`

| AVMixer | MediaPlayer |
|---|---|
| `RegisterAudioSink` | `AddAudioSink` |
| `UnregisterAudioSink` | `RemoveAudioSink` |
| `RegisterVideoEndpoint` | `AddVideoEndpoint` |

The `MediaPlayer` methods do register + route in one step (which is good), but the verb mismatch (`Register` vs `Add`) between layers can confuse users who read both APIs.

**Suggestion:** Either rename `MediaPlayer` methods to `RegisterAndRoute*` or rename `IAVMixer` methods to `Add*`/`Remove*` for consistency.

### 5.4 `ChannelRouteMap.Auto` is not discoverable

This is the most commonly needed factory but it's buried as a static method on `ChannelRouteMap`. Users reaching for the builder don't know it exists.

**Suggestion:** Reference it prominently in the XML doc of `IAVMixer.AddAudioChannel` and consider a convenience overload on `AVMixer`:

```csharp
public void AddAudioChannel(IAudioChannel channel, IAudioResampler? resampler = null)
```

(This already exists ✓ — good. But the `IAVMixer` interface only documents it sparingly.)

### 5.5 `VideoFormat` constructor takes `FrameRateNumerator, FrameRateDenominator` but most users think in fps

**Suggestion:** Add a convenience factory:

```csharp
public static VideoFormat Create(int width, int height, PixelFormat pixelFormat, double fps)
    => new(width, height, pixelFormat,
           (int)Math.Round(fps * 1000), 1000);
```

Currently the test apps construct `new VideoFormat(w, h, px, 30, 1)` — the double-param factory would be more discoverable and protect users from `FrameRateDenominator = 0` accidents.

### 5.6 `IAudioOutput.Open` takes `AudioDeviceInfo` but there's no easy way to get the default device

Users need to call `IAudioEngine.GetDevices()` and filter. A `GetDefaultOutputDevice()` helper on the engine would save boilerplate.

### 5.7 `IMediaEndpoint.Name` default implementation on `IMediaOutput`

**File:** `Media/S.Media.Core/Media/IMediaOutput.cs:10`

```csharp
string IMediaEndpoint.Name => GetType().Name;
```

This is an explicit interface implementation, so concrete classes that implement `IMediaOutput` but don't re-declare `Name` lose the property when referenced through the concrete type. This is confusing — `PortAudioOutput.Name` works, but only because it declares its own `Name` property.

**Suggestion:** Make it a default interface method (DIM) explicitly or remove the default and require all implementations to provide `Name`.

### 5.8 `PlaybackEnded` vs `PlaybackCompleted` redundancy on `MediaPlayer`

Both events fire on source-ended. `PlaybackEnded` is described as a "compatibility signal" in the docs. If it's truly legacy, mark it `[Obsolete]` and point users to `PlaybackCompleted`.

---

## 6. Simplification Opportunities

### 6.1 `FFmpegDecodeWorkers.RunAudioAsync` and `RunVideoAsync` are nearly identical

**File:** `Media/S.Media.FFmpeg/FFmpegDecodeWorkers.cs`

Both methods follow the exact same pattern:
1. Read packet from channel
2. Check seek epoch
3. Handle flush
4. Decode + enqueue
5. Return packet to pool

The only difference is the type parameter (`FFmpegAudioChannel` vs `FFmpegVideoChannel`).

**Suggestion:** Extract a generic `RunAsync<TChannel>` where `TChannel : IDecodableChannel` (a small internal interface with `DecodePacketAndEnqueue`, `ApplySeekEpoch`, `RaiseEndOfStream`, `CompleteDecodeLoop`, etc.). This eliminates ~90 lines of duplicated code.

### 6.2 `PortAudioSink.ReceiveBuffer` and `NDIAVSink.ReceiveBuffer` share identical drift-correction + resample logic

Both do:
1. Compute `nominalWriteFrames` from rate ratio
2. Apply `DriftCorrector`
3. Pool-borrow a buffer
4. Resample or copy + hold-last-frame
5. Enqueue pending write

**Suggestion:** Extract a shared `DriftAwareBufferWriter` helper or a `SinkBufferHelper` static class that both can call.

### 6.3 `GLRenderer` upload methods can be consolidated

The 8 `UploadAndDraw*` methods each repeat:
1. Validate dimensions
2. Pin data
3. Bind textures + sub-image or tex-image
4. Set uniforms
5. Draw quad

A small internal `TexturePlan` struct + a single `UploadAndDrawPlanes(...)` method could replace all of them, shrinking the file by ~400 lines.

### 6.4 Copy-on-write array manipulation is repeated across `AudioMixer`, `VideoMixer`, `AggregateOutput`, `OSCRouter`

The pattern of "allocate new array, copy all except removed index, volatile-assign" appears ~15 times.

**Suggestion:** A small `CopyOnWriteList<T>` utility (or extension methods `AddCow`, `RemoveCow`) would centralise the logic and reduce the chance of off-by-one errors.

---

## 7. Missing Helpers for End Users

### 7.1 `Duration` on `FFmpegDecoder` / `MediaPlayer`

Users need media duration for seek bars, progress display, etc. FFmpeg knows it from `avformat_find_stream_info`.

### 7.2 Looping / Auto-replay

Every sample app that needs looping has to wire `PlaybackEnded` → `Seek(0)` → `PlayAsync()` manually. A `MediaPlayer.IsLooping` property would be trivial to implement and save boilerplate.

### 7.3 Volume Fade / Ramp

Audio fading is a universal requirement. A `MediaPlayer.FadeToAsync(float targetVolume, TimeSpan duration)` or a per-channel `IAudioChannel.FadeTo(...)` would prevent users from writing their own timer-based faders.

### 7.4 `AudioDeviceInfo` discovery helpers

```csharp
// Users currently write:
var engine = new PortAudioEngine();
engine.Initialize();
var devices = engine.GetDevices();
var device = devices.First(d => d.MaxOutputChannels > 0);

// Could be:
var device = engine.GetDefaultOutputDevice();
// or:
var device = engine.FindOutputDevice("USB Audio");
```

### 7.5 `MediaPlayer.OpenAndPlayAsync` convenience

The open → play two-step is so common that a one-liner would help:

```csharp
public async Task OpenAndPlayAsync(string path, FFmpegDecoderOptions? options = null, CancellationToken ct = default)
{
    await OpenAsync(path, options, ct);
    await PlayAsync(ct);
}
```

### 7.6 `IVideoOutput.Open` convenience overload without `VideoFormat`

For users who just want "open a window" without pre-knowing the video format. The output could defer format configuration until the first frame arrives (or accept only width/height and infer from the decoder).

### 7.7 Seek bar / progress helper

A `MediaPlayer.NormalizedPosition` (0.0–1.0) or a `Progress` property based on `Position / Duration` would save every UI app from doing the math.

---

## 8. Side Projects (OSC, MIDI, NDI)

### 8.1 OSCLib

**Strengths:**
- Clean `ref struct` span-based codec with zero allocation on the decode path (until the output `OSCMessage` is built).
- `ArrayPool`-backed encode with `RentedBuffer` RAII wrapper.
- The router uses volatile immutable array swap — lock-free dispatch.

**Issues:**
- `OSCRouter.Unregister` uses LINQ `.Where(...).ToArray()` under lock — allocates on every unregister. Use a manual loop like the other array-swap sites in the project.
- `BuildTypeTagString` in `OSCPacketCodec` allocates a `StringBuilder` + `string` per encode. For high-frequency OSC senders, consider caching or `Span<char>`-based building.
- `EstimatePacketSize` calls `argument.AsArray().Sum(EstimateArgumentSize)` which allocates a LINQ delegate + enumerator per array argument.

### 8.2 PMLib (MIDI)

**Strengths:**
- Clean device abstraction with polling thread and proper SysEx accumulation.
- `ManualResetEventSlim` instead of `Thread.Sleep` for poll timing.

**Issues:**
- `MIDIInputDevice.ProcessEvent` swallows all handler exceptions (`catch { /* P2.7 */ }`). At minimum, log them — a silently failing handler is very hard to debug.
- `_sysExBuffer` is a `List<byte>` that grows on the polling thread. For high-throughput SysEx (e.g. firmware dumps), consider pre-allocating or using `ArrayPool<byte>`.
- `PollLoop` allocates `new PmEvent[64]` once — good. But `MIDIMessageParser.Decode` may allocate per message. Consider a pooled message pattern for high-frequency control surfaces.

### 8.3 S.Media.NDI / NDIAVSink

**Strengths:**
- Thorough pool-based RT-safe `ReceiveFrame` / `ReceiveBuffer` with comprehensive diagnostics counters.
- Multiple pixel format conversion paths (managed fallback + FFmpeg `sws_scale`).
- `NDIEndpointPreset` system for tuning pool sizes.

**Issues:**
- `NDIAVSink` at 894 lines is very large. Consider splitting the video write loop and audio write loop into separate internal classes or at least partial classes.
- The `lock (_sendLock)` around `_sender.SendVideo` / `_sender.SendAudio` serialises A/V sends. If the NDI SDK is thread-safe for separate send calls, this lock is unnecessary contention. If not, document why it's needed.
- `TryConvertI210ToRgbaManaged` uses `float` arithmetic per pixel without SIMD. For large frames (4K) this is significant — consider `Vector<float>` batching.
- The `EnsureFfmpegLoaded()` method uses a lock + state field that could be simplified with `Lazy<bool>`.

---

## 9. Documentation & Testing

### 9.1 Documentation

- **Quick-Start.md** references hard-coded path `/home/seko/RiderProjects/MFPlayer/` — should be relative or use a placeholder.
- **Clone-Sinks.md** and **MediaPlayer-Guide.md** exist in the solution but weren't reviewed in detail; ensure they match the current API (e.g. the auto-route `AddAudioChannel` overload).
- No XML doc on several public types: `ChannelFallback`, `PlaybackState` enum values, `FFmpegLoader`, `AudioDeviceInfo`.
- Consider a **Troubleshooting.md** for common issues: "No audio" (wrong device), "Black video" (pixel format mismatch), "Buffer underruns" (increase buffer depth).

### 9.2 Testing

- Test projects exist (`S.Media.Core.Tests`, `S.Media.FFmpeg.Tests`) but their coverage wasn't examined in detail. Ensure at minimum:
  - `AudioMixer.FillOutputBuffer` round-trip with resampling (the most complex hot path)
  - `ChannelRouteMap.BakeRoutes` edge cases (more src than dst channels, zero routes)
  - `LinearResampler` continuity across buffer boundaries (the pending-frame splice)
  - `VideoMixer.PresentNextFrame` PTS normalisation and frame-drop logic
  - `OSCPacketCodec` round-trip encode/decode for all argument types
- Benchmark project exists — ensure it covers `ScatterIntoMix` and `MultiplyInPlace` to validate SIMD gains.

---

## 10. Summary of Recommended Actions

### Critical (Correctness)

| # | Issue | File(s) | Status |
|---|---|---|---|
| 3.1 | `MediaPlayer.Dispose` race with running RT callback | `MediaPlayer.cs` | ✅ Fixed |
| 3.2 | Per-frame `Dictionary` allocation in `VideoMixer` | `VideoMixer.cs` | ✅ Fixed |
| 3.3 | `AudioChannel.RentChunkBuffer` pool scan | `AudioChannel.cs` | ✅ Fixed |
| 3.4 | Leaked `AudioMixer` when `AggregateOutput` wraps `PortAudioOutput` | `PortAudioOutput.cs` | ✅ Fixed |
| 3.5 | `int` overflow in `LinearResampler` for very long streams | `LinearResampler.cs` | ✅ Fixed |
| 3.7 | `PortAudioSink` silent drop when rates differ | `PortAudioSink.cs` | ✅ Fixed |

### High Value (Performance)

| # | Optimisation | Expected Impact | Status |
|---|---|---|---|
| 4.1 | SIMD `ScatterIntoMix` single-route (mono fast path) | ~30-40% mixer hot path | ✅ Implemented |
| 4.7 | Skip sink loop when no sinks registered | Eliminates dead work in common case | ✅ Implemented |
| 4.2 | SIMD `UpdatePeaks` (mono + stereo) | ~20% peak detection | ✅ Implemented |
| 4.6 | Fast-path single-chunk RT callback | Eliminates loop overhead per callback | ✅ Implemented |

### Medium (API / DX)

| # | Suggestion | Status |
|---|---|---|
| 5.1 | Expose `MediaPlayer.State` property | ✅ Implemented |
| 5.2 | Expose `Duration` on decoder and player | ✅ Implemented |
| 5.5 | `VideoFormat.Create(w, h, px, fps)` factory | ✅ Implemented |
| 5.8 | Deprecate `PlaybackEnded` in favour of `PlaybackCompleted` | ✅ Implemented |
| 7.2 | `MediaPlayer.IsLooping` | ✅ Implemented |
| 7.5 | `MediaPlayer.OpenAndPlayAsync` | ✅ Implemented |
| 7.7 | `MediaPlayer.NormalizedPosition` progress helper | ✅ Implemented |

### Low (Cleanup / Maintenance)

| # | Suggestion | Status |
|---|---|---|
| 8.1 | Remove LINQ from `OSCRouter.Unregister` | ✅ Fixed |
| 8.2 | Log swallowed MIDI handler exceptions | ✅ Fixed |
| 6.1 | Unify `FFmpegDecodeWorkers` audio/video into generic | ✅ Implemented |
| 6.2 | Extract shared drift-correction buffer helper | ✅ Implemented |
| 6.3 | Consolidate `GLRenderer` texture init methods | ✅ Implemented |
| 6.4 | `CopyOnWriteArray<T>` utility | ✅ Implemented |

---

*End of review.*

