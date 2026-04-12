# Implementation Audit — 2026-04-12

Comprehensive review of the MFPlayer media framework.  
Each entry documents a bug fix, performance optimization, or code simplification, along with the file(s) affected and the rationale.

---

## Bug Fixes

### 1. BufferedVideoFrameEndpoint — semaphore stall on queue-at-capacity

**File:** `Media/S.Media.Core/Video/BufferedVideoFrameEndpoint.cs`  
**Severity:** High — could permanently stall video pull consumers.

**Problem:**  
`WriteFrame()` computed `shouldSignal` before checking queue capacity and called `_available.Release()` outside the lock. When the queue was at capacity, the old frame was dequeued and replaced, but `Release()` was never called. This meant a reader blocked on `WaitAsync()` would never wake, even though a frame was available.

**Fix:**  
Moved `Release()` inside the lock and only call it when count actually increases (below capacity). When replacing at capacity the semaphore count is already ≥1 from the previous enqueue, so no new signal is needed.

---

### 2. AggregateOutput — mixer resource leak on Dispose

**File:** `Media/S.Media.Core/Audio/AggregateOutput.cs`  
**Severity:** Medium — leaked internal AudioMixer and its timer/buffers.

**Problem:**  
`AggregateOutput.InitMixer()` creates its own `AudioMixer`, but `Dispose()` only disposed the leader and sinks — never the mixer itself.

**Fix:**  
Added `_mixer?.Dispose()` before `_leader.Dispose()` in the `Dispose()` method.

---

### 3. HardwareClock — race condition in Position getter fallback logic

**File:** `Media/S.Media.Core/Clock/HardwareClock.cs`  
**Severity:** Medium — corrupted fallback state under concurrent access.

**Problem:**  
The `Position` getter mutates `_usingFallback`, `_lastValidPosition`, and `_fallbackSw` without synchronization. The timer tick thread and render threads can call `Position` concurrently, causing torn reads and incorrect fallback transitions.

**Fix:**  
Added a `_fallbackLock` and wrapped the mutable fallback state transitions inside `lock (_fallbackLock)`.

---

### 4. StopwatchClock — transient double-counting in Stop()

**File:** `Media/S.Media.Core/Clock/StopwatchClock.cs`  
**Severity:** Medium — transient forward time jump during Stop().

**Problem:**  
In `Stop()`, between `_offset += _sw.Elapsed` and `_sw.Reset()`, a concurrent `Position` read (`_offset + _sw.Elapsed`) sees the already-accumulated elapsed *plus* the still-frozen stopwatch value — a transient doubling of the accumulated time.

**Fix:**  
Added `_swLock` around `Position`, `Start()`, `Stop()`, and `Reset()` to serialize access to `_offset` and `_sw`. This is not an RT-thread clock (VirtualAudioOutput uses it), so the lock is acceptable.

---

### 5. FFmpegVideoChannel.GetSws — stale SwsContext on resolution change

**File:** `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs`  
**Severity:** Medium — corrupted output if video resolution or pixel format changes mid-stream.

**Problem:**  
`GetSws()` created the `SwsContext*` once and returned the cached pointer on all subsequent calls, ignoring the passed `w`, `h`, and `srcFmt` parameters. If the source stream changed resolution (adaptive bitrate, dynamic resolution change), the context would produce corrupted or mis-scaled output.

**Fix:**  
Replaced the one-shot `sws_getContext` + early-return cache with `sws_getCachedContext`, which automatically detects parameter changes and recreates the context when needed.

---

### 6. Duplicate ArrayPoolOwner\<T\> across projects

**Files:**  
- Deleted: `Media/S.Media.FFmpeg/ArrayPoolOwner.cs`  
- Updated: `Test/S.Media.FFmpeg.Tests/ArrayPoolOwnerTests.cs` — added `using S.Media.Core.Media;`  
- Updated: `Test/S.Media.FFmpeg.Tests/FFmpegDecoderTests.cs` — added `using S.Media.Core.Media;`  
- Updated: `Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj` — added project reference to S.Media.Core  

**Severity:** Low — code duplication, no behavioral bug.

**Problem:**  
`S.Media.FFmpeg` contained an `internal` copy of `ArrayPoolOwner<T>` that was identical to the `public` version in `S.Media.Core.Media`. Since FFmpeg already references Core, the duplicate was unnecessary.

**Fix:**  
Deleted the FFmpeg copy and pointed all consumers at the canonical `S.Media.Core.Media.ArrayPoolOwner<T>`.

---

## Performance Optimizations

### 7. AudioMixer.MultiplyInPlace — SIMD vectorization

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs`  
**Impact:** High — innermost audio hot path, called on every channel volume and master volume pass every buffer cycle.

**Before:** Scalar per-sample `buf[i] *= gain` loop.  
**After:** `System.Numerics.Vector<float>` SIMD loop processes `Vector<float>.Count` samples per iteration (typically 4–8 on modern x64), with a scalar tail for the remainder.

---

### 8. AudioMixer.UpdatePeaks — eliminate per-sample modulo

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs`  
**Impact:** Medium — called once per buffer cycle on every mixed output.

**Before:** `i % outCh` per sample to find the channel index.  
**After:** Nested `for frame / for channel` loop that inherently knows the channel index. For a typical 1024-frame stereo buffer, this removes 2,048 modulo operations per cycle.

---

### 9. NDIAVSink — interleaved-to-planar cache locality

**File:** `NDI/S.Media.NDI/NDIAVSink.cs`  
**Impact:** Low-Medium — audio send path for NDI.

**Before:** Channel-outer / sample-inner loop (sequential writes, scattered reads on the interleaved buffer).  
**After:** Sample-outer / channel-inner loop — sequential reads on the interleaved source buffer for better L1 cache utilization.

---

### 10. PortAudioEngine — direct device lookup

**File:** `Audio/S.Media.PortAudio/PortAudioEngine.cs`  
**Impact:** Low — called once during initialization, not a hot path.

**Before:** `GetDefaultOutputDevice()` and `GetDefaultInputDevice()` called `GetDevices()` which P/Invoked `Pa_GetDeviceInfo` for *every* device in the system, built a list, then searched it with LINQ `.FirstOrDefault()`.  
**After:** Added `GetDeviceByIndex(int idx)` helper that queries `Pa_GetDeviceInfo` directly for the target index — O(1) instead of O(n).

---

## Code Simplifications

### 11. AudioMixer.TryGetLeaderChannels — remove pointless try/catch

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs`  
**Impact:** Clarity.

**Before:** `try { channels = LeaderFormat.Channels; return true; } catch { ... }` — wrapping a readonly record struct field access that can never throw.  
**After:** Expression-body `=> (true, LeaderFormat.Channels)` (or equivalent simple return).

---

### 12. NDIAVSink.Dispose — pending queue buffer leak

**File:** `NDI/S.Media.NDI/NDIAVSink.cs`  
**Severity:** Low-Medium — leaked pooled buffers until GC collection.

**Problem:**  
`Dispose()` cancelled threads and joined them, but never drained `_videoPending` / `_audioPending`. Pooled byte/float arrays enqueued in the pending queues were never returned to the pool.

**Fix:**  
Added drain loops after thread join that dequeue all pending items and return their buffers to the respective pools.

---

## Recommendations (Not Yet Implemented)

### R1. VirtualAudioOutput.Dispose — sync-over-async

`Dispose()` calls `StopAsync().GetAwaiter().GetResult()`. This is pragmatic but could deadlock under a synchronization context. Consider adding `IAsyncDisposable` with `DisposeAsync()` that properly awaits `StopAsync()`.

### R2. AudioChannel.WriteAsync — allocation per push

`WriteAsync` and `TryWrite` call `frames.ToArray()` on every push, allocating a new `float[]`. This is correct (the channel needs ownership of the data), but heavy producers could benefit from an `ArrayPool<float>`-backed overload.

### R3. VideoMixer._pullBuffer — shared single-element array

`_pullBuffer` is a pre-allocated `VideoFrame[1]` used by `PullRawFrame`. It is safe under the current single-threaded presentation model, but should be documented as not thread-safe for external callers of `PresentNextFrame`.

### R4. AvaloniaOpenGlVideoOutput catch-up loop — stale frame reference

In `OnOpenGlRender`, the catch-up loop calls `mixer.PresentNextFrame()` repeatedly. Each call internally disposes the previous `_lastFrame`'s memory owner. The caller's `vf` struct still references that memory momentarily before being overwritten by `vf = nvf`. This is safe in practice (the old data is never read after reassignment), but is fragile — any future code that accesses `vf.Data` between the mixer call and the assignment would read recycled pool memory. Consider cloning the frame reference before the catch-up loop, or restructuring to keep ownership explicit.

### R5. BasicPixelFormatConverter — BGRA↔RGBA byte-swap could use SIMD

The scalar `for (i = 0; i + 3 < n; i += 4)` byte-swap loop in `Convert()` processes one pixel at a time. A `Vector<byte>` shuffle or `Ssse3.Shuffle` intrinsic could process 4–16 pixels per iteration on x64. This is a moderate-frequency path (called once per frame that needs format conversion).

---

## Pass 2 — Optimizations & Simplifications (Breaking OK)

### 13. PortAudioSink.Dispose — pending queue buffer leak

**File:** `Audio/S.Media.PortAudio/PortAudioSink.cs`  
**Severity:** Low-Medium — same class of bug as NDIAVSink (#12).

**Problem:**  
`Dispose()` cancelled the write thread and joined it, but never drained `_pending`. Pooled `float[]` buffers stuck in the pending queue were never returned to `_pool`.

**Fix:**  
Added `while (_pending.TryDequeue(out var p)) _pool.Enqueue(p.Buffer);` after thread join.

---

### 14. AudioMixer — eliminate same-rate SrcBuf→ResampleBuf copy

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs`  
**Impact:** High — removes a full-buffer `memcpy` per same-rate channel per RT callback. For a stereo 1024-frame buffer that's 8 KB of unnecessary copy per channel per cycle (~50 Hz = 400 KB/s per channel).

**Before:** `SrcBuf.CopyTo(ResampleBuf)` followed by volume/scatter on `ResampleBuf`.  
**After:** Introduced `activeBuf` alias that points directly to `SrcBuf` when rates match; volume and scatter operate on `SrcBuf` in-place. `ResampleBuf` is no longer allocated for same-rate slots (saves memory too).

---

### 15. AudioMixer.ScatterIntoMix — restructured loop for hoisted route lookup

**File:** `Media/S.Media.Core/Mixing/AudioMixer.cs`  
**Impact:** High — the innermost mixing loop, called once per channel per mix target per RT callback.

**Before:** Frame-outer / srcChannel-inner — `bakedRoutes[sc]` looked up on every frame iteration, and `f * srcCh + sc` / `f * dstCh + dc` computed with multiplications per frame.  
**After:** Source-channel-outer / frame-inner — route array loaded once per source channel. Running offset variables (`srcOff += srcCh`, `dstOff += dstCh`) replace per-frame multiplications. Specialized single-route fast path for the common identity/mono→stereo case avoids the inner `routes` loop entirely.

---

### 16. AggregateOutput — remove LINQ allocations in Start/Stop

**File:** `Media/S.Media.Core/Audio/AggregateOutput.cs`  
**Impact:** Low — called once per session, not a hot path.

**Before:** `_sinks.Select(s => s.StartAsync(ct))` + `Task.WhenAll(tasks)` — allocates a delegate, an `IEnumerable<Task>`, and a `Task[]` array.  
**After:** Simple `for` loop awaiting each sink sequentially. No allocations.

---

### 17. BasicPixelFormatConverter — SIMD BGRA↔RGBA byte-swap

**File:** `Media/S.Media.Core/Video/BasicPixelFormatConverter.cs`  
**Impact:** Medium — the managed fallback path when libyuv is unavailable. Called once per frame that needs format conversion.

**Before:** Scalar `for (i = 0; i + 3 < n; i += 4)` loop swapping bytes one pixel at a time.  
**After:** `Vector<uint>` SIMD path reinterprets pixel data as `uint` spans and uses bit-mask + shift operations to swap R and B channels across `Vector<uint>.Count` pixels per iteration (typically 4–8 on x64). Scalar tail handles remaining pixels.

---

## Pass 3 — Allocation Reduction (GC Pressure)

### 18. NDIAudioChannel.FillBuffer — closure allocation on RT underrun path

**File:** `NDI/S.Media.NDI/NDIAudioChannel.cs`  
**Impact:** High (correctness) — allocating a closure on the RT audio thread risks triggering a GC pause in the audio callback.

**Before:** `ThreadPool.QueueUserWorkItem(_ => BufferUnderrun?.Invoke(this, ...))` — captures `this`, `Position`, and `dropped` in a compiler-generated closure class (heap allocation on every underrun).  
**After:** Static delegate + boxed `ValueTuple` state — matches the allocation-minimal pattern already used by `AudioChannel.RaiseUnderrun` and `FFmpegAudioChannel.FillBuffer`. The tuple still boxes, but avoids the closure class + delegate allocation.

---

### 19. AudioChannel / FFmpegAudioChannel.RentChunkBuffer — pool buffer leak

**Files:** `Media/S.Media.Core/Audio/AudioChannel.cs`, `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs`  
**Impact:** Medium — caused permanent buffer loss and forced new heap allocations.

**Problem:**  
`RentChunkBuffer` dequeued all candidates from the `ConcurrentQueue<float[]>` pool until it found one large enough. Undersized candidates were silently dropped (never re-enqueued), leaking them to GC. When codec frame sizes vary (e.g. Opus variable frame sizes), this gradually drains the pool and forces fresh `new float[]` allocations.

**Fix:**  
On encountering an undersized candidate, re-enqueue it immediately and break out of the scan (the pool typically holds same-sized buffers, so further scanning is unlikely to find a match). This preserves pool occupancy and avoids GC pressure from leaked buffers.

---

### 20. MediaClockBase — replace System.Timers.Timer with System.Threading.Timer

**File:** `Media/S.Media.Core/Clock/MediaClockBase.cs`  
**Impact:** Medium — eliminates ~50–62 `ElapsedEventArgs` allocations per second.

**Before:** `System.Timers.Timer` fires an `Elapsed` event that allocates a new `ElapsedEventArgs` object per tick, plus an internal delegate dispatch. At the default 20 ms tick interval, that's 50 allocations/sec of short-lived objects.  
**After:** `System.Threading.Timer` with a `TimerCallback` — the callback receives a pre-captured `object? state` (null in this case). No event args allocation per tick. Start/stop is done via `Change(interval, interval)` / `Change(Infinite, Infinite)`.

---

### 21. NDIAVSink.TryConvertI210ToRgbaFfmpeg — per-frame array allocations

**File:** `NDI/S.Media.NDI/NDIAVSink.cs`  
**Impact:** Low-Medium — eliminated 4 heap array allocations per video frame in the I210→RGBA conversion path.

**Before:** `byte*[] srcData = [y, u, v, null]`, `int[] srcStride = [...]`, `byte*[] dstData = [...]`, `int[] dstStride = [...]` — C# collection expressions create new heap arrays on each call. At 30fps that's 120 small array allocations per second.  
**After:** Pre-allocated scratch arrays at the start of `VideoWriteLoop()` and passed as parameters to the static conversion method. Zero per-frame allocations.

---

### 22. EncodedPacket object pooling — eliminate per-packet heap allocation

**Files:**  
- `Media/S.Media.FFmpeg/FFmpegDecoder.cs` — added `PacketPool` (`ConcurrentQueue<EncodedPacket>`), added `EncodedPacket.Reset()` method, pool-aware `TryReadNextPacket`  
- `Media/S.Media.FFmpeg/FFmpegDecodeWorkers.cs` — returns consumed `EncodedPacket` wrappers to pool after use  
- `Media/S.Media.FFmpeg/FFmpegDemuxWorker.cs` — returns failed-write packets to pool  
- `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs` — `StartDecoding` accepts pool parameter  
- `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs` — `StartDecoding` accepts pool parameter  

**Impact:** Medium — eliminates ~80+ `new EncodedPacket(...)` allocations per second during playback.

**Before:** Every demuxed packet created a `new EncodedPacket(...)` class instance on the heap. At typical A+V rates (~30 video + ~50 audio packets/sec), that's ~80 short-lived objects per second flowing through the decode pipeline.  
**After:** `FFmpegDecoder` maintains a `ConcurrentQueue<EncodedPacket>` pool. The demux thread tries `TryDequeue` before allocating; decode workers return consumed wrappers to the pool in their `finally` blocks via `packetPool.Enqueue(ep)`. After warm-up, the steady-state allocation rate for packet wrappers drops to zero.

---

## Pass 4 — Feature: Stream-Based Media Loading

### 23. FFmpegDecoder.Open(Stream) — load media from any System.IO.Stream

**Files:**  
- New: `Media/S.Media.FFmpeg/StreamAvioContext.cs` — bridges `System.IO.Stream` to FFmpeg AVIO via custom read/seek callbacks  
- Modified: `Media/S.Media.FFmpeg/FFmpegDecoder.cs` — new `Open(Stream, ...)` overload, refactored initialisation into `InitialiseFromPath` / `InitialiseFromStream` / `DiscoverStreams`  
- Modified: `Doc/Quick-Start.md` — added Stream loading example  

**Problem:**  
Media could only be loaded via file path or URL (anything `avformat_open_input` accepts natively). There was no way to load from a `MemoryStream`, HTTP response stream, or any other `System.IO.Stream`-compatible source.

**Solution:**  
Implemented FFmpeg's custom AVIO I/O protocol:

1. **`StreamAvioContext`** — allocates an `AVIOContext*` via `avio_alloc_context` with:
   - A **read callback** (`avio_alloc_context_read_packet`) that reads from the .NET `Stream` into the native buffer, returning bytes read or `AVERROR_EOF`.
   - A **seek callback** (`avio_alloc_context_seek`) that maps POSIX seek origins + `AVSEEK_SIZE` to `Stream.Seek` / `Stream.Length`.
   - The class pins itself via `GCHandle` so callbacks can recover `this` from the opaque `void*`.
   - Delegates stored as fields to prevent GC collection while native code holds function pointers.
   - `Dispose()` frees the AVIO buffer, context, GCHandle, and optionally closes the stream.

2. **`FFmpegDecoder.Open(Stream stream, FFmpegDecoderOptions? options, bool leaveOpen)`** — new public overload that:
   - Creates a `StreamAvioContext` wrapping the stream.
   - Allocates an `AVFormatContext` via `avformat_alloc_context`.
   - Wires `fmt->pb = avioCtx.Context` before calling `avformat_open_input`.
   - Calls the shared `DiscoverStreams()` to find audio/video tracks.
   - On failure, properly cleans up the AVIO context.

3. **Disposal safety:**
   - `Dispose()` nulls `_fmt->pb` before `avformat_close_input` to prevent double-free.
   - AVIO context is disposed after format context teardown.
   - `leaveOpen` parameter controls stream ownership (matches `StreamReader`/`BinaryReader` convention).

**API:**
```csharp
// From file path (existing)
using var decoder = FFmpegDecoder.Open("/path/to/media.mp4");

// From any Stream (new)
using var stream = File.OpenRead("media.mp4");
using var decoder = FFmpegDecoder.Open(stream);

// With options and leaveOpen
using var decoder = FFmpegDecoder.Open(httpStream,
    new FFmpegDecoderOptions { EnableVideo = false },
    leaveOpen: true);
```

Seekable streams enable full seek support; non-seekable streams (e.g. `NetworkStream`) allow forward-only playback.

---

## Phase 5 — Microsoft.Extensions.Logging Integration

### Overview

Added comprehensive `Microsoft.Extensions.Logging` (`ILogger`) support across **all** libraries.
Every library now has a static `XxxLogging` factory class with a `Configure(ILoggerFactory?)` entry point.
Logging is optional — when unconfigured, `NullLoggerFactory.Instance` is used (zero overhead).

### Logging Factory Classes (new files)

| File | Library |
|------|---------|
| `Media/S.Media.Core/MediaCoreLogging.cs` | S.Media.Core |
| `Media/S.Media.FFmpeg/FFmpegLogging.cs` | S.Media.FFmpeg |
| `Audio/S.Media.PortAudio/PortAudioLogging.cs` | S.Media.PortAudio |
| `NDI/S.Media.NDI/NDIMediaLogging.cs` | S.Media.NDI |
| `Video/S.Media.Avalonia/AvaloniaVideoLogging.cs` | S.Media.Avalonia |
| `Video/S.Media.SDL3/SDL3VideoLogging.cs` | S.Media.SDL3 |
| `Audio/JackLib/JackLogging.cs` | JackLib |

Each factory provides:
```csharp
public static void Configure(ILoggerFactory? loggerFactory);
internal static ILogger GetLogger(string category);
internal static ILogger<T> GetLogger<T>();
```

### Logging Levels

| Level | Usage |
|-------|-------|
| **Trace** | Per-packet / per-frame / per-buffer hot-path (guarded with `IsEnabled`) |
| **Debug** | State transitions, configuration details, port registration |
| **Information** | Lifecycle events: create, open, start, stop, dispose (with stats) |
| **Warning** | Recoverable issues: codec send failures, pool exhaustion fallbacks |
| **Error** | Failures: decode-loop exceptions, render exceptions, GL errors |

### Classes with Logging Added

| Class | Logger Source | Key Log Points |
|-------|-------------|----------------|
| `FFmpegDecoder` | FFmpegLogging | Open (path/stream), stream discovery, close with stats |
| `FFmpegDemuxWorker` | FFmpegLogging | Start, EOF, per-packet trace, error |
| `FFmpegDecodeWorkers` | FFmpegLogging | Start, per-frame trace, error |
| `FFmpegAudioChannel` | FFmpegLogging | Decode errors, send failures, dispose |
| `FFmpegVideoChannel` | FFmpegLogging | Codec open, decode errors, send failures, dispose |
| `FFmpegLoader` | FFmpegLogging | Loading/loaded with version |
| `StreamAvioContext` | FFmpegLogging | Create/dispose |
| `AudioMixer` | MediaCoreLogging | Channel/sink management, config |
| `AVMixer` | MediaCoreLogging | Lifecycle, attach |
| `VideoMixer` | MediaCoreLogging | Create, channel/sink management, dispose |
| `AggregateOutput` | MediaCoreLogging | Create, AddSink, dispose |
| `AudioChannel` | MediaCoreLogging | Create, dispose with stats |
| `VirtualAudioOutput` | MediaCoreLogging | Create, start, stop, dispose |
| `PortAudioOutput` | PortAudioLogging | Open, start, stop, dispose (no RT callback logging) |
| `PortAudioSink` | PortAudioLogging | Create, start, stop, dispose with drop stats |
| `PortAudioEngine` | PortAudioLogging | Init, terminate, dispose |
| `NDIAVSink` | NDIMediaLogging | Create, start, stop, dispose with video/audio stats |
| `NDISource` | NDIMediaLogging | Open, start, stop, dispose |
| `NDIAudioChannel` | NDIMediaLogging | Create, start capture, capture errors, dispose |
| `NDIVideoChannel` | NDIMediaLogging | Create, start capture, capture errors, dispose |
| `NDIClock` | NDIMediaLogging | Start, stop, reset |
| `AvaloniaOpenGlVideoOutput` | AvaloniaVideoLogging | Open, start, stop, render errors, dispose with stats |
| `SDL3VideoOutput` | SDL3VideoLogging | Open, start, stop, GL errors, render errors, dispose with stats |
| `JackClient` | JackLogging | Create, activate, deactivate, port registration, dispose |

### Console.Error.WriteLine Replacements

All `Console.Error.WriteLine` calls in library code have been replaced with structured `ILogger` calls:

| File | Old | New |
|------|-----|-----|
| `FFmpegAudioChannel.cs` | `Console.Error.WriteLine(...)` ×3 | `Log.LogError` / `Log.LogWarning` |
| `FFmpegVideoChannel.cs` | `Console.Error.WriteLine(...)` ×3 + `Console.WriteLine` ×1 | `Log.LogError` / `Log.LogWarning` / `Log.LogInformation` |
| `NDIAVSink.cs` | `Console.Error.WriteLine(...)` ×1 | `Log.LogError` |
| `AvaloniaOpenGlVideoOutput.cs` | `Console.Error.WriteLine(...)` ×1 | `Log.LogError` |
| `SDL3VideoOutput.cs` | `Console.Error.WriteLine(...)` ×2 | `Log.LogError` |

### RT-Safety

Logging is deliberately **excluded** from `[UnmanagedCallersOnly]` callbacks and real-time audio process callbacks (`PortAudioOutput.StreamCallback`, JACK process callbacks). Managed exceptions in those contexts would cause a fast-fail crash.

### Usage

```csharp
// At application startup, wire up a logging factory:
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

MediaCoreLogging.Configure(loggerFactory);
FFmpegLogging.Configure(loggerFactory);
PortAudioLogging.Configure(loggerFactory);
NDIMediaLogging.Configure(loggerFactory);
AvaloniaVideoLogging.Configure(loggerFactory);
SDL3VideoLogging.Configure(loggerFactory);
JackLogging.Configure(loggerFactory);
```

---

## Files Changed Summary

| File | Change Type |
|------|------------|
| `Media/S.Media.Core/Video/BufferedVideoFrameEndpoint.cs` | Bug fix |
| `Media/S.Media.Core/Audio/AggregateOutput.cs` | Bug fix + simplification + logging |
| `Media/S.Media.Core/Clock/HardwareClock.cs` | Bug fix |
| `Media/S.Media.Core/Clock/StopwatchClock.cs` | Bug fix |
| `Media/S.Media.Core/Mixing/AudioMixer.cs` | Optimization + simplification + logging + resampler pull-count fix |
| `Media/S.Media.Core/Video/BasicPixelFormatConverter.cs` | SIMD optimization |
| `Media/S.Media.Core/Clock/MediaClockBase.cs` | Allocation reduction (Timer swap) |
| `Media/S.Media.Core/Audio/AudioChannel.cs` | Allocation reduction (pool leak fix) + logging |
| `Media/S.Media.Core/Audio/VirtualAudioOutput.cs` | Logging |
| `Media/S.Media.Core/Video/VideoMixer.cs` | Logging |
| `Media/S.Media.Core/Mixing/AVMixer.cs` | Logging |
| `Media/S.Media.Core/MediaCoreLogging.cs` | New — logging factory |
| `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs` | Bug fix + pool wiring + logging |
| `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs` | Pool leak fix + pool wiring + logging |
| `Media/S.Media.FFmpeg/FFmpegDecoder.cs` | EncodedPacket pooling + Stream loading + logging |
| `Media/S.Media.FFmpeg/FFmpegDecodeWorkers.cs` | EncodedPacket pool return + logging |
| `Media/S.Media.FFmpeg/FFmpegDemuxWorker.cs` | EncodedPacket pool return + logging |
| `Media/S.Media.FFmpeg/FFmpegLoader.cs` | Logging |
| `Media/S.Media.FFmpeg/StreamAvioContext.cs` | New — AVIO ↔ Stream bridge + logging |
| `Media/S.Media.FFmpeg/FFmpegLogging.cs` | New — logging factory |
| `Media/S.Media.FFmpeg/ArrayPoolOwner.cs` | Deleted (duplicate) |
| `NDI/S.Media.NDI/NDIAVSink.cs` | Bug fix + optimization + alloc reduction + logging |
| `NDI/S.Media.NDI/NDISource.cs` | Major enhancement: state management, `OpenByNameAsync`, connection watchdog, `MatchSource`, auto-reconnection |
| `NDI/S.Media.NDI/NDIAudioChannel.cs` | RT closure fix + logging |
| `NDI/S.Media.NDI/NDIVideoChannel.cs` | Logging |
| `NDI/S.Media.NDI/NDISource.cs` | Major enhancement: state management, `OpenByNameAsync`, connection watchdog, auto-reconnection + logging |
| `NDI/S.Media.NDI/NDISourceState.cs` | New — `NDISourceState` enum + `NDISourceStateChangedEventArgs` |
| `NDI/S.Media.NDI/NDIMediaLogging.cs` | New — logging factory |
| `Audio/S.Media.PortAudio/PortAudioOutput.cs` | Logging + sample-rate auto-negotiation |
| `Audio/S.Media.PortAudio/PortAudioSink.cs` | Bug fix (drain) + logging + drift correction + sample-rate auto-negotiation |
| `Audio/S.Media.PortAudio/PortAudioEngine.cs` | Optimization + logging |
| `Audio/S.Media.PortAudio/PortAudioLogging.cs` | New — logging factory |
| `Audio/JackLib/JackClient.cs` | Logging |
| `Audio/JackLib/JackLogging.cs` | New — logging factory |
| `Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs` | Logging |
| `Video/S.Media.Avalonia/AvaloniaVideoLogging.cs` | New — logging factory |
| `Video/S.Media.SDL3/SDL3VideoOutput.cs` | Logging |
| `Video/S.Media.SDL3/SDL3VideoLogging.cs` | New — logging factory |
| `Test/S.Media.FFmpeg.Tests/ArrayPoolOwnerTests.cs` | Updated import |
| `Test/S.Media.FFmpeg.Tests/FFmpegDecoderTests.cs` | Updated import |
| `Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj` | Added project ref |
| `Test/S.Media.Core.Tests/DriftCorrectorTests.cs` | New — unit tests |
| `Test/S.Media.Core.Tests/NDISourceTests.cs` | New — source matching + state tests |
| `Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj` | Added NDILib/S.Media.NDI project refs |
| `Test/MFPlayer.NDIAutoPlayer/Program.cs` | **New** — test app demonstrating `OpenByNameAsync` with auto-reconnect |
| `Test/MFPlayer.NDIAutoPlayer/MFPlayer.NDIAutoPlayer.csproj` | **New** — project file |
| `Doc/README.md` | Added audit link |
| `Doc/Quick-Start.md` | Added Stream loading example |
| `Doc/API-Implementation-Audit-2026-04-12.md` | New |

---

## Phase 6 — Automatic Drift Correction for Audio Sinks

### Problem

When multiple audio outputs with independent hardware clocks are mixed together (e.g. a PortAudio sound card + an NDI sender), each sink's hardware clock runs at a slightly different rate than the leader output's clock. Typical crystal oscillator drift is 1–50 ppm. Over long sessions this causes the pending-write queue in each sink to gradually grow (leader faster) or drain (leader slower), eventually leading to buffer overflow drops or underrun glitches.

**Example:** A 10 ppm drift at 48 kHz over 1 hour accumulates ~1.7 seconds of offset — enough to exhaust any reasonable buffer pool.

### Solution — `DriftCorrector` (PI Controller + Fractional Accumulation)

A new `DriftCorrector` class in `S.Media.Core.Audio` implements a discrete PI (Proportional–Integral) controller that:

1. **Monitors** the sink's pending-buffer queue depth on every `ReceiveBuffer` call.
2. **Computes** a correction ratio (e.g. 1.0002 or 0.9998) using:
   - **Proportional term** (`Kp × error`): responds to instantaneous queue deviations.
   - **Integral term** (`Ki × Σerror`): eliminates residual steady-state offset.
   - **Anti-windup clamp**: prevents integral overshoot after prolonged saturation.
3. **Adjusts** the output frame count via fractional accumulation — the ±1 frame adjustments are distributed evenly over time so the long-term average exactly matches the corrected ratio.

### Key Design Choices

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| **Opt-in** | `enableDriftCorrection = false` parameter | Zero overhead when unused; no breaking API change. |
| **PI gains** | Kp = 2×10⁻³, Ki = 1×10⁻⁵ | Conservative: ~2 s response time for ±1 buffer deviation. |
| **Max correction** | ±0.5 % (0.005) | Covers ±240 Hz at 48 kHz — far beyond any real crystal drift. |
| **Cross-rate sinks** | Resampler output buffer sized to corrected frame count | The `LinearResampler`'s stateful phase accumulator handles ±1 frame naturally. |
| **Same-rate sinks** | Copy + hold-last-frame / drop-last-frame | ±1 frame every ~100–500 buffers is completely inaudible (sample-and-hold). |
| **RT-safety** | No allocations, no locks in `CorrectFrameCount` | Safe for `ReceiveBuffer` calls on the RT thread. |
| **Diagnostics** | `CorrectionRatio`, `TotalCalls`, `TargetDepth` properties | Enables runtime monitoring; final ratio logged at Dispose. |

### Files

| File | Change |
|------|--------|
| `Media/S.Media.Core/Audio/DriftCorrector.cs` | **New** — PI controller with fractional accumulation |
| `Audio/S.Media.PortAudio/PortAudioSink.cs` | Added `enableDriftCorrection` ctor parameter, drift-corrected `ReceiveBuffer`, `DriftCorrection` property, reset on Start, ratio in Dispose log |
| `NDI/S.Media.NDI/NDIAVSink.cs` | Added `enableAudioDriftCorrection` ctor parameter, drift-corrected `ReceiveBuffer`, `AudioDriftCorrection` property, reset on Start, ratio in Dispose log |
| `Test/S.Media.Core.Tests/DriftCorrectorTests.cs` | **New** — 8 unit tests: at-target, below-target, above-target, clamping, fractional accumulation, reset, minimum frame count, call counting |

### API

```csharp
// PortAudioSink — opt in to drift correction
var sink = new PortAudioSink(device, format, framesPerBuffer: 512,
    enableDriftCorrection: true);

// NDIAVSink — opt in to audio drift correction
var ndiSink = new NDIAVSink(sender,
    audioTargetFormat: audioFmt,
    enableAudioDriftCorrection: true);

// Monitor at runtime
double ratio = sink.DriftCorrection?.CorrectionRatio ?? 1.0;
long   calls = sink.DriftCorrection?.TotalCalls ?? 0;
```


---

## Phase 7 — Per-Channel Time Offsets & A/V Drift Monitoring

### Problem

When audio and video tracks are supplied separately (e.g. separate NDI sources, or independent capture devices), the tracks can have a fixed timing mismatch — for example, a video feed that is known to be 200ms behind its companion audio. Additionally, once tracks are playing, monitoring the instantaneous drift between audio and video positions is essential for diagnosing sync issues.

### Solution — Per-Channel Time Offsets

Added `SetChannelTimeOffset(Guid channelId, TimeSpan offset)` and `GetChannelTimeOffset(Guid channelId)` to both the audio and video mixer interfaces.

**Semantics:**
- **Positive offset** → channel content is **delayed** (plays later relative to the master clock).
- **Negative offset** → channel content is **advanced** (plays earlier).
- `TimeSpan.Zero` → removes any offset (default).

#### Video Implementation (`VideoMixer`)

The offset is applied by adjusting the effective clock position before PTS comparison:

```
effectiveClockPosition = clockPosition − offset
```

- A **positive** offset reduces the effective clock, making frames present later.
- A **negative** offset increases the effective clock, making frames present earlier.
- Applied per-channel: the leader and each sink target can have independent offsets.
- Offsets are stored in a `Dictionary<Guid, long>` (ticks) guarded by the existing `_lock`.
- The render-thread lookup is a single `TryGetValue` per channel per frame — negligible overhead.

#### Audio Implementation (`AudioMixer`)

Audio is pull-based with no PTS concept, so the offset is applied as a one-shot delay/advance:

- **Positive offset (delay):** `OffsetFramesRemaining = offset.TotalSeconds × sampleRate`. On each `FillOutputBuffer` call, while frames remain, the channel contributes silence instead of real data. Decremented each cycle until exhausted.
- **Negative offset (advance):** `OffsetFramesRemaining = −(|offset| × sampleRate)`. On each `FillOutputBuffer` call, the channel is pulled but its data is discarded. Decremented each cycle until exhausted.
- Uses `Volatile.Read`/`Volatile.Write` on the RT path — no locks, no allocations.
- Granularity: one buffer period (~5ms at 48kHz/256 frames). This is well below audible threshold.

### Solution — A/V Drift Monitoring

Added `GetAvDrift(Guid audioChannelId, Guid videoChannelId)` to `IAVMixer` / `AVMixer`:

```csharp
TimeSpan drift = avMixer.GetAvDrift(audioChannelId, videoChannelId);
// Positive = audio is ahead of video
// Negative = audio is behind video
```

`AVMixer` now tracks registered channels in internal dictionaries (`_audioChannels`, `_videoChannels`), populated in `AddAudioChannel`/`AddVideoChannel` and cleaned up in `Remove*`. The drift is simply `audioChannel.Position − videoChannel.Position` — a zero-allocation read of two `TimeSpan` properties.

### Key Design Choices

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| **Video offset location** | In `PresentForTarget` clock adjustment | Clean, single point of change; no per-frame allocation. |
| **Audio offset mechanism** | Silence insertion / frame discard in `FillOutputBuffer` | Pull-based audio has no PTS; one-shot delay is the simplest RT-safe approach. |
| **RT-safety (audio)** | `Volatile.Read`/`Volatile.Write` for offset counter | No locks on the RT path. |
| **Offset direction** | Positive = delay, negative = advance | Matches intuitive "shift timeline forward/backward" mental model. |
| **Drift monitoring** | `audioPosition − videoPosition` | Zero-allocation; caller decides policy (log, alert, auto-correct). |
| **Channel tracking in AVMixer** | `Dictionary<Guid, IAudioChannel/IVideoChannel>` | Avoids adding lookup methods to IAudioMixer/IVideoMixer interfaces. |
| **Cleanup** | Offsets removed on `RemoveChannel` | Prevents stale offset entries for removed channels. |

### Files

| File | Change |
|------|--------|
| `Media/S.Media.Core/Video/IVideoMixer.cs` | Added `SetChannelTimeOffset`, `GetChannelTimeOffset` |
| `Media/S.Media.Core/Video/VideoMixer.cs` | Offset dictionary, `GetOffsetForChannel`, clock adjustment in `PresentNextFrame`, cleanup in `RemoveChannel` |
| `Media/S.Media.Core/Audio/IAudioMixer.cs` | Added `SetChannelTimeOffset`, `GetChannelTimeOffset` |
| `Media/S.Media.Core/Mixing/AudioMixer.cs` | `TimeOffset` + `OffsetFramesRemaining` on `ChannelSlot`, silence/discard logic in `FillOutputBuffer`, `SetChannelTimeOffset`/`GetChannelTimeOffset` methods |
| `Media/S.Media.Core/Mixing/IAVMixer.cs` | Added `SetAudioChannelTimeOffset`, `GetAudioChannelTimeOffset`, `SetVideoChannelTimeOffset`, `GetVideoChannelTimeOffset`, `GetAvDrift` |
| `Media/S.Media.Core/Mixing/AVMixer.cs` | Channel tracking dictionaries, forwarding methods, `GetAvDrift` implementation |
| `Test/S.Media.Core.Tests/TimeOffsetTests.cs` | **New** — 13 tests: video offset round-trip, video delay/advance, audio offset round-trip, audio delay/advance/zero, AVMixer forwarding, A/V drift monitoring |
| `Test/S.Media.Core.Tests/AVMixerTests.cs` | Updated spy/stub classes for new interface methods |
| `Test/S.Media.Core.Tests/VideoOutputPullSourceAdapterTests.cs` | Updated stub class for new interface methods |

### API

```csharp
// ── Per-channel time offsets ──────────────────────────────────────────

// Video is 200ms behind → delay audio by 200ms
avMixer.SetAudioChannelTimeOffset(audioChannelId, TimeSpan.FromMilliseconds(200));

// Or advance video by 200ms (equivalent effect)
avMixer.SetVideoChannelTimeOffset(videoChannelId, TimeSpan.FromMilliseconds(-200));

// Read back current offset
TimeSpan audioOffset = avMixer.GetAudioChannelTimeOffset(audioChannelId);

// Remove offset
avMixer.SetAudioChannelTimeOffset(audioChannelId, TimeSpan.Zero);

// ── A/V drift monitoring ──────────────────────────────────────────────

TimeSpan drift = avMixer.GetAvDrift(audioChannelId, videoChannelId);
if (Math.Abs(drift.TotalMilliseconds) > 100)
    Console.WriteLine($"A/V drift: {drift.TotalMilliseconds:F1}ms");
```

---

## Phase 8 — NDI Reconnection & Name-Based Discovery

### Problem

The NDI receive pipeline (`NDISource`) had three limitations:

1. **No reconnection handling.** If an NDI source went offline, `NDIFrameSync` gracefully returned silence/black (no crash), but the receiver never attempted to reconnect. The capture threads kept spinning uselessly, and there was no way to detect the disconnect programmatically.

2. **No name-based discovery.** `NDISource.Open(NDIDiscoveredSource)` required a pre-discovered source from `NDIFinder`. There was no way to say "connect to the source called 'OBS' and wait until it appears on the network."

3. **No connection state visibility.** Consumers had no way to know whether the source was connected, disconnected, or reconnecting.

### Analysis: Should NDIAudioChannel and NDIVideoChannel be combined?

**No.** They implement different interfaces (`IAudioChannel` vs `IVideoChannel`), have very different:
- **Data types:** `float[]` audio vs `VideoFrame` video
- **Ring buffers:** `Channel<float[]>` with `DropOldest` vs lock-guarded `Queue<VideoFrame>`
- **Capture timing:** audio uses tick-accurate 1024-frame intervals (~21ms); video uses 8ms sleep throttle
- **Memory strategies:** pre-allocated `float[]` pool vs `ArrayPool<byte>` rentals

`NDISource` already serves as the unified facade that owns both channels, the receiver, frame-sync, and clock. Merging the channels would create a monolithic class with two unrelated hot loops.

### Solution

#### 1. Connection State Management (`NDISourceState`)

New enum with four states:

| State | Meaning |
|-------|---------|
| `Disconnected` | Not connected (initial state, or after Dispose) |
| `Discovering` | Searching for an NDI source by name |
| `Connected` | Actively receiving frames |
| `Reconnecting` | Source went offline; attempting to reconnect |

`NDISource.StateChanged` event fires on every transition (delivered via `ThreadPool` to avoid blocking the watchdog).

#### 2. Automatic Reconnection

When `NDISourceOptions.AutoReconnect = true`, `NDISource.Start()` launches a background watchdog thread that:

1. **Polls** `NDIReceiver.GetConnectionCount()` at a configurable interval (default 2s).
2. **Detects disconnect** when count drops to 0 → transitions to `Reconnecting`.
3. **Reconnects:**
   - **Name-based sources** (`OpenByNameAsync`): uses the retained `NDIFinder` to rediscover the source by name, handling IP changes after source restart.
   - **Direct sources** (`Open`): calls `NDIReceiver.Connect()` with the stored `NDIDiscoveredSource`.
4. **Detects reconnection** when count goes back to > 0 → transitions to `Connected`.

Key insight: the NDI SDK supports calling `recv_connect` multiple times on the same receiver instance. The `NDIFrameSync` continues to work across reconnections — it returns silence/black during the gap, then resumes producing real frames.

#### 3. Name-Based Discovery (`OpenByNameAsync`)

New async factory method that:

1. Creates an `NDIFinder` internally.
2. Polls `WaitForSources()` + `GetCurrentSources()` looking for a matching source.
3. **Source name matching** (via `NDISource.MatchSource`): tries exact match first (case-insensitive), then falls back to contains match. This handles NDI's `"HOSTNAME (SourceName)"` naming convention — you can match by full name, just the host, or just the source name.
4. Once found, delegates to the existing `Open()` flow.
5. Keeps the finder alive for reconnection when `AutoReconnect` is enabled.

### Key Design Choices

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| **Keep A/V channels separate** | Not combined | Different interfaces, data types, timing, and memory strategies; `NDISource` is the facade |
| **Watchdog thread** | Dedicated `BelowNormal` priority thread | Simple, no async state machine; `WaitHandle.WaitOne` for clean cancellation |
| **Reconnect via same receiver** | `NDIReceiver.Connect()` on existing instance | NDI SDK supports re-connecting; avoids teardown/rebuild of FrameSync and channels |
| **Name matching** | Exact first, then contains (case-insensitive) | Handles full names like `"MY-PC (OBS)"` and partial like `"OBS"` |
| **Finder lifetime** | Kept alive only when `AutoReconnect` is enabled | Avoids unnecessary native resource usage for non-reconnecting sources |
| **State events** | Delivered via `ThreadPool.QueueUserWorkItem` | Prevents consumer handlers from blocking the watchdog thread |
| **Backward compatibility** | `Open()` unchanged; new options have safe defaults | `AutoReconnect = false` by default; existing code works without modification |

### Files

| File | Change |
|------|--------|
| `NDI/S.Media.NDI/NDISourceState.cs` | **New** — `NDISourceState` enum + `NDISourceStateChangedEventArgs` |
| `NDI/S.Media.NDI/NDISource.cs` | Major enhancement: state management, `OpenByNameAsync`, connection watchdog, `MatchSource`, auto-reconnection |
| `Test/S.Media.Core.Tests/NDISourceTests.cs` | **New** — 11 tests: source matching (exact, partial, case-insensitive, ambiguous, no match, empty), state event args, enum coverage |
| `Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj` | Added project references to NDILib and S.Media.NDI for tests |

### API

```csharp
// ── Existing: Open by discovered source (unchanged) ───────────────────

using var finder = ...;  // NDIFinder
var source = finder.GetCurrentSources()[0];
using var ndiSource = NDISource.Open(source);

// ── New: Open by discovered source with auto-reconnect ────────────────

using var ndiSource = NDISource.Open(source, new NDISourceOptions
{
    AutoReconnect = true,
    ConnectionCheckIntervalMs = 2000  // check every 2s (default)
});

ndiSource.StateChanged += (_, e) =>
    Console.WriteLine($"NDI: {e.OldState} → {e.NewState} ({e.SourceName})");

ndiSource.Start();  // also starts watchdog thread

// ── New: Open by name (waits for source to appear) ────────────────────

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

using var ndiSource = await NDISource.OpenByNameAsync(
    "OBS",                      // matches "MY-PC (OBS)" on the network
    new NDISourceOptions
    {
        SampleRate    = 48000,
        Channels      = 2,
        EnableVideo   = true,
        AutoReconnect = true    // keeps finder alive for reconnection
    },
    cts.Token);

ndiSource.Start();  // starts capture + watchdog

// ── Source name matching utility ──────────────────────────────────────

var sources = finder.GetCurrentSources();
var match = NDISource.MatchSource(sources, "Camera 1");
// Returns first exact match (case-insensitive), or first contains match

// ── Monitor connection state ──────────────────────────────────────────

Console.WriteLine($"State: {ndiSource.State}");
// NDISourceState.Connected / Reconnecting / Disconnected / Discovering
```

---

## Phase 8 Addendum — Sample Rate Auto-Negotiation & Resampler Pull-Count Fix

### Problem 1: PortAudio Sample Rate Mismatch

When an NDI source delivers 48 000 Hz audio but the PortAudio output device only supports its native rate (e.g. JACK at 44 100 Hz), `Pa_OpenStream` returned `paInvalidSampleRate` and the application crashed. Every test app implemented its own retry-with-fallback logic, violating DRY and requiring every new app to know about PA error codes.

### Solution: Library-Layer Auto-Negotiation

Moved the negotiation into `PortAudioOutput.Open()` and `PortAudioSink`'s constructor:

1. **`PortAudioOutput.Open()`** — extracted `TryOpenStream()` helper that returns `PaError` instead of throwing. On `paInvalidSampleRate`, retries with `device.DefaultSampleRate`. Logs a warning.
2. **`PortAudioSink` constructor** — same pattern via `TryOpenSinkStream()` static helper. `_targetFormat` is assigned *after* the successful open so it reflects the actual rate.
3. **Test apps simplified** — `NDIAutoPlayer`, `NDIPlayer`, and `SimplePlayer` all reduced to a single `output.Open(device, hwFmt)` call. The `AudioMixer` auto-creates a `LinearResampler` when `channel.SourceFormat.SampleRate ≠ LeaderFormat.SampleRate`.

### Problem 2: Audio Crackling Every Few Seconds (Resampler Over-Pull)

**Root cause:** In `AudioMixer.FillOutputBuffer()`, the per-callback input frame count was calculated with a fixed formula:

```csharp
int srcFrames = (int)Math.Ceiling(frameCount * (srcRate / outRate)) + 1;
```

This consistently over-pulled by ~1 frame per callback. The `LinearResampler` saved the excess as "pending" frames. At 44 100 Hz / 1024 frames (~43 callbacks/sec), the pending count grew by ~1 per callback:

| Time | Pending frames | Effect |
|------|---------------|--------|
| 1 s | ~43 | _combinedBuf doubles |
| 3 s | ~130 | another doubling |
| 7 s | ~300 | another doubling |
| … | unbounded | periodic GC pauses → crackle |

Each `_combinedBuf` growth triggered a heap allocation on the RT thread → GC pause → audible crackle. The intervals between crackles increased (doubling buffer) but never stopped.

**Fix:** Replaced the fixed formula with `slot.Resampler!.GetRequiredInputFrames()`, which subtracts the internally buffered pending frames:

```csharp
int srcFrames = sameRate ? frameCount
    : slot.Resampler!.GetRequiredInputFrames(frameCount, srcFmt, outputFormat.SampleRate);
```

With this fix, the pending frame count stabilises at 1 frame (steady state). No further allocations on the RT thread.

**Note:** The `AllocateSlotBuffers()` pre-allocation formula still uses the conservative `ceil + 1` formula — it needs the worst-case maximum for buffer sizing, which is correct since `GetRequiredInputFrames` always returns ≤ that value.

### Files

| File | Change |
|------|--------|
| `Audio/S.Media.PortAudio/PortAudioOutput.cs` | Extracted `TryOpenStream()`, added `paInvalidSampleRate` auto-fallback |
| `Audio/S.Media.PortAudio/PortAudioSink.cs` | Extracted `TryOpenSinkStream()`, added `paInvalidSampleRate` auto-fallback |
| `Media/S.Media.Core/Mixing/AudioMixer.cs` | **Bug fix:** replaced fixed `ceil + 1` formula with `GetRequiredInputFrames()` in `FillOutputBuffer` |
| `Test/MFPlayer.NDIAutoPlayer/Program.cs` | Simplified — removed app-level rate fallback |
| `Test/MFPlayer.NDIPlayer/Program.cs` | Simplified — removed app-level rate fallback |
| `Test/MFPlayer.SimplePlayer/Program.cs` | Simplified — removed app-level rate fallback |

---

## Phase 8 Addendum B — SDL3 Video Output for NDIAutoPlayer

### Goal

Add live SDL3 video output to the NDIAutoPlayer test app, turning it into a full A/V NDI receiver with auto-reconnection.

### Changes

**`Test/MFPlayer.NDIAutoPlayer/Program.cs`**
- Enabled video: `EnableVideo = true`, `VideoBufferDepth = 4`
- After connecting, starts NDI source to receive frames, then polls `videoChannel.SourceFormat` until the first video frame arrives (10 s timeout, falls back to 1920×1080 BGRA32 @ 29.97 fps)
- Creates `SDL3VideoOutput`, opens an SDL3 window sized via `FitWithin()` helper
- Creates `AVMixer` with both audio and video formats; attaches both outputs and channels
- Starts video output alongside audio; hooks `WindowClosed` event to cancel the main CTS
- Status ticker now includes video position + presented/black frame counts
- Clean shutdown: stops video output before audio, disposes SDL3 window

**`Test/MFPlayer.NDIAutoPlayer/MFPlayer.NDIAutoPlayer.csproj`**
- Added project reference to `S.Media.SDL3`

**`NDI/S.Media.NDI/NDISource.cs`**
- Made `Start()` idempotent with a `_started` guard — safe to call multiple times (needed because the NDIAutoPlayer calls `Start()` early to detect video format, then again in the main start section)

### Files

| File | Change |
|------|--------|
| `Test/MFPlayer.NDIAutoPlayer/Program.cs` | Full SDL3 video pipeline: format detection, window creation, AV mixer wiring, window-close handler, video diagnostics in status ticker |
| `Test/MFPlayer.NDIAutoPlayer/MFPlayer.NDIAutoPlayer.csproj` | Added `S.Media.SDL3` project reference |
| `NDI/S.Media.NDI/NDISource.cs` | Made `Start()` idempotent (`_started` guard) |

---

## Phase 8 Addendum C — Native UYVY422 GL Renderer Support

### Problem

NDI's default receive colour format (`NDIRecvColorFormat.Fastest`) delivers frames in **UYVY422** packed format on most platforms. The `GLRenderer` used by `SDL3VideoOutput` had no UYVY422 code path — frames with that pixel format fell through to `DrawBlack()`, resulting in a black screen with working audio.

### Background: UYVY422 Packed Format

UYVY is a 4:2:2 packed YUV format where each pair of pixels is encoded as 4 bytes: `[U, Y0, V, Y1]`. Unlike planar formats (NV12, YUV420p), all components are interleaved in a single plane. At 1920×1080, the data is 1920 × 1080 × 2 = 4,147,200 bytes.

### Solution

Added a complete UYVY422 rendering pipeline to the GL renderer, with no CPU-side pixel conversion.

#### GLSL Shader (`GlShaderSources.FragmentUyvy422`)

The packed UYVY data is uploaded as an **RGBA8 texture at half width** — each texel maps to one pixel pair:
- R = U (shared chroma)
- G = Y0 (left pixel luma)
- B = V (shared chroma)
- A = Y1 (right pixel luma)

The fragment shader uses a `uVideoWidth` uniform to determine the actual pixel position and selects the correct Y sample:

```glsl
float pixelX = vUV.x * float(uVideoWidth);
float yRaw   = (fract(pixelX / 2.0) < 0.5) ? packed.g : packed.a;
float uRaw   = packed.r;
float vRaw   = packed.b;
```

Standard YUV→RGB conversion follows, with `uLimitedRange` and `uColorMatrix` uniforms for BT.601/BT.709 and limited/full range support (matching the existing NV12/YUV420p shaders).

#### GLRenderer Changes

- New texture `_textureUyvy`, program `_programUyvy422`, and associated uniform locations
- `UploadAndDrawUyvy422()`: uploads the packed data as `GL_RGBA` / `GL_UNSIGNED_BYTE` at `(width/2) × height`, sets `uVideoWidth` so the shader resolves even/odd pixels
- `UploadAndDraw()` dispatch: added `PixelFormat.Uyvy422` case routing to the new method
- `Initialise()`: compiles UYVY422 shader, queries uniform locations
- `Dispose()`: cleans up texture and program

#### Routing Policy & Video Output

- `LocalVideoOutputRoutingPolicy`: added `bool supportsUyvy422 = false` parameter; routes `Uyvy422` source formats to native rendering when the renderer supports it
- `SDL3VideoOutput`: passes `supportsUyvy422: true` to `SelectLeaderPixelFormat()`; added `Uyvy422Frames` counter to `DiagnosticsSnapshot`

### Key Design Choices

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| **Upload format** | RGBA8 at half-width | Each texel naturally maps to one UYVY pixel pair; no CPU-side unpacking needed |
| **Even/odd Y selection** | `fract(pixelX / 2.0) < 0.5` | Selects G (Y0) for even pixels, A (Y1) for odd pixels within each pair |
| **No CPU conversion** | GPU-only decode | Avoids ~4 MB/frame memcpy + conversion on the CPU; GPU handles YUV→RGB in the shader |
| **Shared color matrix/range** | Same `uLimitedRange` + `uColorMatrix` uniforms as NV12/YUV420p | Consistent behaviour across all YUV formats |
| **Opt-in routing** | `supportsUyvy422 = false` default | Non-breaking; renderers that don't support UYVY422 still fall back to BGRA32 conversion |

### Files

| File | Change |
|------|--------|
| `Media/S.Media.Core/Video/GlShaderSources.cs` | Added `FragmentUyvy422` shader constant |
| `Video/S.Media.SDL3/GLRenderer.cs` | UYVY422 texture, program, uniforms, upload method, dispatch, init, dispose |
| `Video/S.Media.SDL3/SDL3VideoOutput.cs` | UYVY422 frame counter, `supportsUyvy422: true` in routing call |
| `Media/S.Media.Core/Video/LocalVideoOutputRoutingPolicy.cs` | Added `supportsUyvy422` parameter and `Uyvy422` routing case |
| `Test/S.Media.Core.Tests/LocalVideoOutputRoutingPolicyTests.cs` | Added 2 tests: UYVY422 selected when supported, falls back when unsupported |

---

## Phase 8 Addendum D — VideoPtsClock Build Fix & Bootstrap Hold

**Date:** 2026-04-12

### Problem

After the UYVY422 changes, the project failed to compile because `VideoPtsClock.UpdateFromFrame()` referenced an undeclared `_initialised` field. This was added in the prior session's `UpdateFromFrame` logic but the field declaration was omitted, resulting in `CS0103` errors. Because the binary couldn't build, the SDL3 window couldn't open and video position stayed at 0.

Additionally, even after declaring the field, the clock's `Position` property was free-running from `Start()` via the internal `Stopwatch`. This caused a secondary problem: if the first frame arrives after a startup delay (e.g. 50–200 ms for NDI format detection), the `VideoMixer` would see a clock position of ~200 ms but the first normalized frame PTS would be 0, causing the mixer to drop all early frames as stale.

### Root Cause

1. **Missing field declaration:** `private bool _initialised;` was never added to the class fields.
2. **Clock races ahead of frames:** `Position` returned `_lastPts + (_sw.Elapsed - _swAtLastPts)` from the moment `Start()` was called, even before any frame had been received. Since `_lastPts` and `_swAtLastPts` both start at zero, Position equals the raw stopwatch elapsed time — far ahead of the normalized frame PTS timeline.

### Fix

1. **Declared `_initialised` field** — resolves the compilation error.
2. **`Position` holds at `TimeSpan.Zero` until the first `UpdateFromFrame()` call** — prevents the clock from racing ahead before frames arrive. Once the first frame anchors the clock, interpolation proceeds normally.
3. **`Reset()` clears `_initialised`** — ensures the clock can be restarted cleanly.

### Test Changes

- **`UpdateFromFrame_LatePts_DoesNotPullClockBackwards`** — updated to anchor the clock with an initial `UpdateFromFrame(Zero)` before testing late-PTS rejection. This reflects the real pipeline where frames always arrive before the late-PTS scenario applies.
- **`Position_HoldsAtZero_UntilFirstFrameAnchors`** — new test verifying that Position stays at zero before any frame is received, then progresses after the first frame anchors the clock.

### Files

| File | Change |
|------|--------|
| `Media/S.Media.Core/Clock/VideoPtsClock.cs` | Added `_initialised` field; `Position` returns zero until initialised; `Reset()` clears `_initialised` |
| `Test/S.Media.Core.Tests/VideoPtsClockTests.cs` | Updated late-PTS test to anchor first; added `Position_HoldsAtZero_UntilFirstFrameAnchors` test |

### Test Results

- **S.Media.Core.Tests:** 195 passed, 0 failed
- **S.Media.FFmpeg.Tests:** 52 passed, 0 failed
- **Total:** 247 passed

---

## Phase 8 Addendum E — GLSL `packed` Reserved Keyword Fix

**Date:** 2026-04-12

### Problem

The SDL3 video window failed to open with a shader compilation error:

```
Shader compilation failed: 0:10(7): error: syntax error, unexpected PACKED_TOK, expecting ',' or ';'
```

The UYVY422 fragment shader used `packed` as a local variable name (`vec4 packed = texture(...)`). In GLSL, `packed` is a reserved keyword (used for `packed` layout qualifiers in buffer layout declarations). While some drivers (NVIDIA) accept it as an identifier, Mesa/AMD drivers correctly reject it at compilation time.

Because the shader failed to compile, `GLRenderer.Initialise()` threw an exception during `SDL3VideoOutput.Open()`. The `try/catch` in NDIAutoPlayer caught it, printed "SDL3 video FAILED", disposed the output, and continued audio-only.

### Fix

Renamed the variable from `packed` to `uyvy` in the `FragmentUyvy422` shader source. No functional change — just a safe identifier that isn't a reserved keyword in any GLSL version.

### Files

| File | Change |
|------|--------|
| `Media/S.Media.Core/Video/GlShaderSources.cs` | Renamed `vec4 packed` → `vec4 uyvy` in `FragmentUyvy422` shader |

