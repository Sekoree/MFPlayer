# MFPlayer — API & Implementation Review V2
*April 13, 2026 — follow-up pass covering S.Media.Core, S.Media.FFmpeg, S.Media.NDI, S.Media.PortAudio, S.Media.Avalonia, S.Media.SDL3*

> This document is a delta on top of `API-Review.md` (V1).  
> It records which V1 "Open" items have been closed since then, lists three new bugs found and fixed during this pass, and captures new findings not in V1.

---

## Table of Contents

1. [V1 Items Closed Since V1](#1-v1-items-closed-since-v1)
2. [New Bugs Found and Fixed This Pass](#2-new-bugs-found-and-fixed-this-pass)
3. [New Findings — API Design](#3-new-findings--api-design)
4. [New Findings — Implementation Issues](#4-new-findings--implementation-issues)
5. [New Findings — Performance](#5-new-findings--performance)
6. [Namespace Cleanup](#6-namespace-cleanup)
7. [Summary Table](#7-summary-table)

---

## 1. V1 Items Closed Since V1

### 1.1 ✅ FIXED — `OverrideRtMixer` / `OverridePresentationMixer` hidden from IDE

**V1 item 1.1** — These methods are now decorated with  
`[EditorBrowsable(EditorBrowsableState.Never)]` on both `IAudioOutput` and `IVideoOutput`,  
suppressing them from IDE auto-complete for application code.  
The recommendation to move them to a separate coordination interface remains as a future clean-up if the interface surface is refactored.

---

### 1.2 ✅ FIXED — `SetActiveChannelForSink` no longer throws on re-route

**V1 item 1.8** — `VideoMixer.SetActiveChannelForSink` now auto-unroutes silently  
when the sink is already assigned to a different channel:

```csharp
// Auto-unroute: if the sink is already routed to a different channel, reset it first.
if (target.ActiveChannel is not null && target.ActiveChannel.Id != channelId.Value)
{
    Log.LogInformation("Auto-unrouting sink from channel {OldId} before routing to {NewId}", ...);
    target.ActiveChannel = null;
    target.ResetState();
}
```

---

### 1.3 ✅ FIXED — `VideoMixer.GetOffsetForChannel` is now lock-free on the hot path

**V1 item 2.6** — Channel time offsets are now stored in an  
`ImmutableDictionary<Guid, long> _channelOffsetTicks` with `Volatile.Read/Write` semantics.  
The presentation hot path (`PresentNextFrame`) reads the snapshot without acquiring any lock.

---

### 1.4 ✅ FIXED — `FFmpegDecoder` demux locking upgraded to `ReaderWriterLockSlim`

**V1 item 2.7** — `_formatIoGate` is now a `ReaderWriterLockSlim`.  
The continuous demux loop uses a **read** lock (`EnterReadLock`); seeks use a **write** lock (`EnterWriteLock`).  
The common case (demux, no concurrent seek) acquires only a read lock, eliminating false contention.

---

### 1.5 ✅ FIXED — `NDISource.Dispose` now fires `StateChanged` before `_disposed = true`

**V1 item 2.8** — The disposal sequence was reordered:

```csharp
// Fire state change BEFORE _disposed = true so handlers can still access this instance.
SetState(NDISourceState.Disconnected);   // line 432
_disposed = true;                        // line 434
```

Event handlers subscribed to `StateChanged` now see a live (not yet disposed) source.

---

### 1.6 ✅ FIXED — `AddAudioChannel(channel)` auto-route overload added

**V1 item 3.2** — `AVMixer` and `IAVMixer` now expose:

```csharp
void AddAudioChannel(IAudioChannel channel, IAudioResampler? resampler = null);
```

The overload derives the route map via `ChannelRouteMap.Auto(channel.SourceFormat.Channels, leaderChannels)` — the same logic already used for the explicit-map overload.

---

### 1.7 ✅ FIXED — `IAVMixer` audio endpoint routing API gap closed

**V1 item 3.6** — The asymmetry between video endpoints and audio endpoints has been closed.  
`IAVMixer` / `AVMixer` now expose:

```csharp
void RouteAudioChannelToEndpoint(Guid channelId, IAudioBufferEndpoint endpoint, ChannelRouteMap routeMap);
void RouteAudioChannelToEndpoint(Guid channelId, IAudioBufferEndpoint endpoint);  // auto ChannelRouteMap
void UnrouteAudioChannelFromEndpoint(IAudioBufferEndpoint endpoint);
```

The auto-routing overload derives `ChannelRouteMap.Auto` from the registered channel's source format.

---

## 2. New Bugs Found and Fixed This Pass

### 2.1 ✅ FIXED — `FFmpegVideoChannel.BufferDepth` always returned 0

**File:** `FFmpegVideoChannel.cs`

`_bufferDepth` was a `private readonly int` field that was **never assigned** in the constructor,  
so `BufferDepth` always returned 0 regardless of the `bufferDepth` parameter (default: 4).  
The ring channel itself was sized correctly with `bufferDepth`, so actual buffering worked — only  
the property was wrong, misleading any code that checked `BufferDepth` to decide when to start.

**Fix applied:**

```csharp
// In the constructor, after the other field assignments:
_bufferDepth = Math.Max(1, bufferDepth);
```

---

### 2.2 ✅ FIXED — `NDIAVSink.HasVideo` always returned `false`

**File:** `NDIAVSink.cs`

`_hasVideo` was a `private readonly bool` field that was **never assigned**, making `HasVideo` always  
return `false` even when a `VideoFormat` was passed to the constructor.  
This affected logging, guards (`if (!_hasVideo || ...)`) that skipped video processing, and any  
caller checking `sink.HasVideo` to decide whether to route a video channel.

**Fix applied:**

```csharp
// Inside: if (videoTargetFormat is { } v)  — after _videoTargetFormat is set:
_hasVideo = true;
```

---

### 2.3 ✅ FIXED — `AvaloniaOpenGlVideoOutput.SetYuvHints` missing `YuvColorMatrix` overload

**File:** `AvaloniaOpenGlVideoOutput.cs`

The public control only exposed `SetYuvHints(bool bt709, bool limitedRange)`, which is a  
two-way BT.601 / BT.709 switch. `AvaloniaGlRenderer` already had an internal overload  
`SetYuvHints(YuvColorMatrix matrix, bool limitedRange)` supporting BT.2020 (shader value 2),  
but it was unreachable through the output control. BT.2020 content (HDR cameras, streaming)  
would be colour-shifted when displayed via `AvaloniaOpenGlVideoOutput`.

The stored hint state was also using only `_yuvBt709` / `_yuvLimitedRange`, so after an OpenGL  
context loss/restore the richer matrix value could not be re-applied.

**Fix applied:**
- Added `private YuvColorMatrix _yuvColorMatrix = YuvColorMatrix.Auto;` field.
- Added public overload:
  ```csharp
  public void SetYuvHints(YuvColorMatrix matrix, bool limitedRange)
  ```
- Updated `OnOpenGlInit` to re-apply the stored `_yuvColorMatrix` instead of `_yuvBt709` after  
  a context restore:
  ```csharp
  if (_hasYuvHintsOverride)
      _renderer.SetYuvHints(_yuvColorMatrix, _yuvLimitedRange);
  ```

---

## 3. New Findings — API Design

### 3.1 ✅ FIXED — `AvaloniaOpenGlVideoOutput` does not propagate `IVideoColorMatrixHint` per frame

**File:** `AvaloniaOpenGlVideoOutput.cs`, `SDL3VideoOutput.cs`, `IVideoMixer.cs`, `VideoMixer.cs`

`IVideoMixer` gained a default `ActiveChannel` property (returns `null` for custom implementations;  
`VideoMixer` returns the currently routed channel). Both render loops now auto-apply the channel's  
`IVideoColorMatrixHint` on each frame when no manual `SetYuvHints` override is active:

```csharp
// In OnOpenGlRender / RenderLoop (after PresentNextFrame):
if (!_hasYuvHintsOverride && mixer.ActiveChannel is IVideoColorMatrixHint hint)
{
    var m = hint.SuggestedYuvColorMatrix;
    var r = hint.SuggestedYuvColorRange;
    if (m != _lastAutoMatrix || r != _lastAutoRange)   // O(1) comparison
    {
        _lastAutoMatrix = m;
        _lastAutoRange  = r;
        _renderer.SetYuvHints(m, r == YuvColorRange.Limited);  // Avalonia
        // _renderer.YuvColorMatrix = m; _renderer.YuvColorRange = r;  // SDL3
    }
}
```

`SDL3VideoOutput` also gained `_hasYuvHintsOverride` (set to `true` when the user calls  
`YuvConfig`, `YuvColorRange`, or `YuvColorMatrix` setters), mirroring the existing  
`_hasYuvHintsOverride` flag in `AvaloniaOpenGlVideoOutput`.

---

### 3.2 ✅ FIXED — `GLRenderer` (SDL3) colour matrix state uses misleading field names

**Fix applied:**  
Backing fields renamed from `_i422P10ColorMatrix` / `_i422P10ColorRange` to  
`_yuvColorMatrix` / `_yuvColorRange`. The public API now exposes `YuvColorMatrix` /  
`YuvColorRange` as the primary properties; the old `I422P10ColorMatrix` / `I422P10ColorRange`  
names are kept as forwarding aliases for back-compat.

---

### 3.3 ✅ FIXED — `AudioChannel.EndOfStream` is never raised by `AudioChannel`

**Fix applied:**  
Added `Complete()` method to `AudioChannel`. `_writer.TryComplete()` is called first, then  
`EndOfStream` is fired via `ThreadPool.QueueUserWorkItem` so the event never executes on a  
real-time audio callback thread.


---

## 4. New Findings — Implementation Issues

### 4.1 ✅ FIXED — `AVMixer` channel dictionaries are not thread-safe

**Fix applied:**  
All four dictionaries (`_audioChannels`, `_videoChannels`, `_audioEndpointAdapters`,  
`_videoEndpointAdapters`) replaced with `ConcurrentDictionary<…>`. Mutations use  
`TryAdd` / `TryRemove` patterns; reads are inherently safe.

---

### 4.2 ✅ FIXED — `BasicPixelFormatConverter` produces black for new pixel formats

**Fix applied:**  
CPU conversion paths added for all six missing formats:
- `Rgb24` / `Bgr24` → RGBA/BGRA: per-pixel pack with optional R↔B swap
- `Gray8` → RGBA/BGRA: luma replicated into R, G, B channels
- `Yuv444p` → RGBA/BGRA: 8-bit planar BT.601 full-range managed path
- `Yuv420p10` → RGBA/BGRA: 10-bit planar I010 managed path
- `P010` → RGBA/BGRA: 10-bit semi-planar NV12 managed path

`ConvertWithHints(source, dest, colorRange, colorMatrix)` overload added so callers  
can pass explicit metadata; `Convert()` delegates to it with defaults.

---

### 4.3 ✅ FIXED — `NDIVideoChannel` ring buffer uses `Queue<T>` + `Lock` instead of `System.Threading.Channels`

**Fix applied:**  
Replaced `Queue<VideoFrame>` + `Lock _ringGate` with an `UnboundedChannel<VideoFrame>`  
(`SingleWriter = true`, `AllowSynchronousContinuations = false`) plus an `Interlocked` counter  
`_framesInRing`. This yields:

- **Lock-free `BufferAvailable`**: `Interlocked.Read(ref _framesInRing)` — no lock on the hot path.
- **Lock-free `FillBuffer`**: `_ringReader.TryRead` + `Interlocked.Decrement`.
- **"Drop oldest" with correct disposal**: `EnqueueFrame` reads one frame from the reader and  
  calls `MemoryOwner?.Dispose()` before writing the new one, so `ArrayPool` rentals are always  
  returned even when the ring overflows.

An unbounded channel is used rather than `BoundedChannelFullMode.DropOldest` because  
`System.Threading.Channels` has no hook to dispose the evicted item.

---

### 4.4 ✅ FIXED — `NDIVideoChannel.CaptureLoop` throttle caps frame rate at ~125 fps

**Fix applied:**  
The unconditional `Thread.Sleep(8)` was removed. When no frame is ready the sleep duration  
is now frame-rate-adaptive:
```csharp
int sleepMs = fpsNow > 0 ? Math.Max(1, (int)(400.0 / fpsNow)) : 4;
Thread.Sleep(sleepMs);
```
When a real frame is available the loop processes it immediately with no sleep.

---

## 5. New Findings — Performance

### 5.1 ✅ FIXED — `AvaloniaOpenGlVideoOutput` stores last-frame reference for reuse comparison

**Fix applied:**  
`StopAsync` now clears `_hasUploadedFrame = false; _lastUploadedData = default;`  
releasing any held `ArrayPool` rental as soon as the output is stopped.

---

### 5.2 ✅ FIXED — `VideoMixer` sink format resolution acquires `_lock` inside `PresentNextFrame`

**File:** `VideoMixer.cs` (sink fan-out loop in `PresentNextFrame`)

The recommended lock-free pattern was already in place:  
`_sinkTargets` is a `volatile SinkTarget[]` replaced atomically under `_lock` in  
`RegisterSink` / `UnregisterSink` (array-swap). `PresentNextFrame` reads it with  
`var sinks = _sinkTargets;` — no lock acquired on the hot path.

---

## 6. Namespace Cleanup

### 7.1 ✅ DONE — `S.Media.Core.Clock` merged into `S.Media.Core`

**Files affected:** `HardwareClock.cs`, `IMediaClock.cs`, `MediaClockBase.cs`, `StopwatchClock.cs`, `VideoPtsClock.cs` (all 5 clock types), plus 14 consumer `using` directives updated.

`S.Media.Core.Clock` was a thin sub-namespace with only 5 types. All were moved to `S.Media.Core` — the natural home for foundational clock abstractions. Consumers update from `using S.Media.Core.Clock;` to `using S.Media.Core;`.

---

### 7.2 ✅ DONE — `S.Media.Core.Errors` dissolved into parent namespaces

**Files affected:** `AudioEngineException.cs`, `BufferException.cs`, `MediaException.cs`.

| Type | Old namespace | New namespace |
|------|--------------|---------------|
| `AudioEngineException` | `S.Media.Core.Errors` | `S.Media.Core.Audio` |
| `BufferException` | `S.Media.Core.Errors` | `S.Media.Core.Audio` |
| `MediaException` | `S.Media.Core.Errors` | `S.Media.Core` |

`S.Media.Core.Errors` had zero external `using` directives (grep confirmed), making the rename safe with no consumer changes.

---

## 7. Summary Table

### V1 items closed this pass

| V1 # | Description | Previous Status | Current Status |
|------|-------------|-----------------|----------------|
| 1.1 | `OverrideRtMixer`/`OverridePresentationMixer` public on interfaces | Open | ✅ Mitigated (`EditorBrowsable`) |
| 1.3 | `FillBuffer` return 0 ambiguous (underrun vs not started) | Open | ✅ Fixed (`NDIVideoChannel` now fires `BufferUnderrun`) |
| 1.8 | `SetActiveChannelForSink` throws on re-route | Open | ✅ Fixed (auto-unroute) |
| 2.3 | `TryConvertI210Managed` ignores color range/matrix | Open | ✅ Fixed (`YuvColorRange`/`YuvColorMatrix` params wired) |
| 2.6 | `VideoMixer.GetOffsetForChannel` locks on hot path | Open | ✅ Fixed (ImmutableDictionary) |
| 2.7 | `FFmpegDecoder` lock on every demux packet | Open | ✅ Fixed (ReaderWriterLockSlim) |
| 2.8 | `NDISource.Dispose` fires event after `_disposed = true` | Open | ✅ Fixed (order swapped) |
| 3.1 | No `MediaPlayer` high-level facade | Open | ✅ Fixed (`MediaPlayer` added to `S.Media.FFmpeg`) |
| 3.2 | No `AddAudioChannel(channel)` auto-route overload | Open | ✅ Fixed |
| 3.4 | NDI source discovery pattern is verbose | Open | ✅ Fixed (`NDISource.DiscoverAsync` added) |
| 3.6 | Asymmetric sink vs endpoint APIs on `IAVMixer` | Open | ✅ Fixed (audio endpoint routing added) |

### New items this pass

| # | Area | Severity | Type | Status |
|---|------|----------|------|--------|
| V2.2.1 | `FFmpegVideoChannel.BufferDepth` always 0 | **High** | Bug | ✅ Fixed |
| V2.2.2 | `NDIAVSink.HasVideo` always false | **High** | Bug | ✅ Fixed |
| V2.2.3 | `AvaloniaOpenGlVideoOutput` missing `YuvColorMatrix` overload | Medium | Bug / API | ✅ Fixed |
| V2.3.1 | No per-frame `IVideoColorMatrixHint` propagation in renderers | Medium | API Design | ✅ Fixed |
| V2.3.2 | `GLRenderer` colour matrix fields misleadingly named `I422P10` | Low | API Design | ✅ Fixed (fields renamed) |
| V2.3.3 | `AudioChannel.EndOfStream` never raised by `AudioChannel` | Medium | Bug | ✅ Fixed (`Complete()` added) |
| V2.4.1 | `AVMixer` channel dictionaries not thread-safe | Medium | Bug | ✅ Fixed (`ConcurrentDictionary`) |
| V2.4.2 | `BasicPixelFormatConverter` produces black for 6 new formats | **High** | Bug | ✅ Fixed |
| V2.4.3 | `NDIVideoChannel` ring uses `Queue` + `Lock` vs `System.Threading.Channels` | Low | Performance | ✅ Fixed (unbounded channel + Interlocked) |
| V2.4.4 | `NDIVideoChannel` 8 ms sleep caps frame rate at ~125 fps | Medium | Bug | ✅ Fixed (adaptive sleep) |
| V2.5.1 | `AvaloniaOpenGlVideoOutput` holds rented buffer ref after stop | Low | Performance | ✅ Fixed |
| V2.5.2 | `VideoMixer` sink fan-out acquires `_lock` per render call | Low | Performance | ✅ Fixed (already lock-free) |

