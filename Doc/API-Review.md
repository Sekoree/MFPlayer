# MFPlayer — API & Implementation Review
*April 2026 — covers S.Media.Core, S.Media.FFmpeg, S.Media.NDI, S.Media.PortAudio, S.Media.Avalonia, S.Media.SDL3*

> **Implementation status as of April 13, 2026** — items marked ✅ have been fully implemented.
> Items without a badge remain open for a future pass.

---

## Table of Contents

1. [API Design Findings](#1-api-design-findings)
2. [Implementation Issues](#2-implementation-issues)
3. [End-User Simplification Opportunities](#3-end-user-simplification-opportunities)
4. [OpenGL Renderer — Pixel Format Gap Analysis](#4-opengl-renderer--pixel-format-gap-analysis)
5. [Stream-End / EOF Events](#5-stream-end--eof-events)
6. [Summary Table](#6-summary-table)

---

## 1. API Design Findings

### 1.1 `OverrideRtMixer` / `OverridePresentationMixer` are public coordination internals

**Files:** `IAudioOutput.cs`, `IVideoOutput.cs`

Both methods are documented as "not intended for direct app use" yet sit on the public interfaces. They appear in IDE auto-complete, confuse new consumers, and allow bypassing the mixer entirely by accident.

**Recommendation:**  
Move them to a separate internal coordination interface (e.g. `IHasRtMixerOverride`) and use explicit interface implementation, or mark them `[EditorBrowsable(EditorBrowsableState.Never)]` as a minimum.

---

### 1.2 ✅ FIXED — `IVideoChannel` lacks buffer visibility (`BufferAvailable` / `BufferDepth`)

**Files:** `IVideoChannel.cs` vs `IAudioChannel.cs`

`IAudioChannel` exposes `BufferAvailable`, `BufferDepth`, and `WaitForAudioBufferAsync` (on `NDISource`). `IVideoChannel` has none of these. Pre-buffering video before opening the hardware output (the same pattern used for audio in `NDIPlayer`) is not possible without reaching through implementation types.

**Fix applied:**  
Added `int BufferDepth { get; }` and `int BufferAvailable { get; }` to `IVideoChannel`. All implementations updated (`FFmpegVideoChannel`, `NDIVideoChannel`, `VideoOutputEndpointAdapter.EndpointVideoChannel`, and all test stubs).

---

### 1.3 `IMediaChannel<TFrame>.FillBuffer` cannot distinguish true underrun from "not started yet"

**Files:** `IMediaChannel.cs`, `AudioChannel.cs`, `VideoMixer.cs`

When `FillBuffer` returns `0`, it can mean either: (a) the ring is empty due to a real underrun, or (b) the channel has never received data (pipeline not started yet). Callers rely on the side-effect `BufferUnderrun` event to tell the difference for audio, but video has no equivalent event.

**Recommendation:**  
Add `event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun` to `IVideoChannel` and fire it from `VideoMixer.PullRawFrame` when `got <= 0` after the first frame has been seen.

---

### 1.4 ✅ FIXED — `IVideoOutput.Open` pixel format parameter is silently overridden

**File:** `AvaloniaOpenGlVideoOutput.cs`

Previously: `_outputFormat = format with { PixelFormat = PixelFormat.Rgba32 }` silently discarded any pixel format the caller specified.

**Fix applied:**  
`AvaloniaOpenGlVideoOutput.Open()` now stores the format as-is (`_outputFormat = format`). Since the Avalonia GL renderer now has native GPU paths for all major formats (see §4), CPU conversion is no longer needed and the output honours the caller-requested format directly. A `SetYuvHints(bool bt709, bool limitedRange)` / `ResetYuvHints()` API was also added for overriding colour-space metadata.

---

### 1.5 ✅ FIXED — `ChannelRouteMap` missing `MonoToStereo` / `Straight` factory — forces boilerplate in every app

**Files:** `ChannelRouteMap.cs`, every test `Program.cs`

**Fix applied:**  
Added `ChannelRouteMap.MonoToStereo()` and `ChannelRouteMap.Auto(int srcChannels, int dstChannels)` static factory methods. `Auto` performs mono→stereo expansion when src==1 and dst≥2, and straight pass-through otherwise. Test programs can now replace their hand-copied `BuildRouteMap` helpers with `ChannelRouteMap.Auto(...)`.

---

### 1.6 ✅ FIXED — `NDIAVSink` constructor has 15 parameters — unusable without IDE assistance

**File:** `NDIAVSink.cs`

**Fix applied:**  
`NDIAVSinkOptions` record added (`NDIAVSinkOptions.cs`). A new preferred constructor `NDIAVSink(NDISender sender, NDIAVSinkOptions? options = null)` was added. The original long-form constructor is preserved for backwards compatibility but is now an implementation detail called by the options constructor.

---

### 1.7 ✅ FIXED — `PortAudioOutput` does not expose its device name via `IMediaEndpoint.Name`

**File:** `PortAudioOutput.cs`

**Fix applied:**  
`Name` now returns `$"PortAudioOutput({_deviceName})"` after `Open()`, or `"PortAudioOutput(not open)"` before it.

---

### 1.8 `VideoMixer.SetActiveChannelForSink` throws when re-routing to a different channel without explicit unroute

**File:** `VideoMixer.cs` line 323

```csharp
if (target.ActiveChannel is not null && target.ActiveChannel.Id != channelId.Value)
    throw new InvalidOperationException("Sink already has a routed source channel. Unroute first ...");
```

This is fine for correctness, but the `IAVMixer.RouteVideoChannelToSink` documentation does not mention this requirement. A user calling `RouteVideoChannelToSink(newChannelId, sink)` after a previous route is set will get an unexpected exception.

**Recommendation:**  
Either silently replace the existing route (auto-unroute), or document the requirement prominently on `IAVMixer.RouteVideoChannelToSink`.

---

## 2. Implementation Issues

### 2.1 ✅ FIXED — `AudioChannel.RentChunkBuffer` re-enqueues undersized buffers, leaking them into the pool permanently

**File:** `AudioChannel.cs`

**Fix applied:**  
Undersized buffers are now dropped (not re-enqueued). GC collects them naturally. The same fix was applied to `FFmpegAudioChannel.RentChunkBuffer`.

---

### 2.2 ✅ FIXED — `BasicPixelFormatConverter` diagnostic counters are `static` — all instances share them

**File:** `BasicPixelFormatConverter.cs`

**Fix applied:**  
`_libYuvAttempts`, `_libYuvSuccesses`, and `_managedFallbacks` are now instance fields. `GetDiagnosticsSnapshot()` returns per-instance totals. All call sites updated to use instance methods.

---

### 2.3 `BasicPixelFormatConverter.TryConvertI210Managed` uses BT.709 full-range unconditionally

**File:** `BasicPixelFormatConverter.cs`

The SDL3 `GLRenderer` correctly handles both limited-range and full-range via the `uLimitedRange` / `uColorMatrix` uniforms (and `IVideoColorMatrixHint`). The CPU fallback path in `BasicPixelFormatConverter` ignores color range and matrix entirely.

**Fix:**  
Accept optional `YuvColorRange` / `YuvColorMatrix` parameters and apply the correct normalization for limited-range sources (broadcast cameras, most HDR content).

---

### 2.4 ✅ FIXED — `VirtualAudioOutput.Dispose` calls `StopAsync().GetAwaiter().GetResult()` — deadlock risk

**File:** `VirtualAudioOutput.cs`

**Fix applied:**  
`Dispose` now calls a private `StopSync()` method that cancels the CTS directly and calls `Task.Wait(TimeSpan)` with a 2-second timeout, avoiding all async machinery and eliminating the deadlock risk.

---

### 2.5 ✅ FIXED — `AggregateOutput.Dispose` calls `Dispose()` on externally-injected sinks

**File:** `AggregateOutput.cs`

**Fix applied:**  
`Dispose` no longer calls `Dispose()` on any registered sinks. A comment documents that ownership of sinks remains with the caller.

---

### 2.6 `VideoMixer.GetOffsetForChannel` acquires `_lock` on the presentation hot path

**File:** `VideoMixer.cs` lines 248–258, called from `PresentNextFrame`

`GetOffsetForChannel` is called on every `PresentNextFrame` invocation (once for the leader, once per sink target). It acquires `_lock` each time. The lock is nearly always uncontended, but acquiring even an uncontended lock takes tens of nanoseconds and can cause jitter in the render loop.

**Fix:**  
Cache the offset in a `volatile long` field per channel/sink target, updated atomically when `SetChannelTimeOffset` is called. Read it lock-free in `GetOffsetForChannel`.

---

### 2.7 `FFmpegDecoder.TryReadNextPacket` acquires `_formatIoGate` on every packet read

**File:** `FFmpegDecoder.cs` lines 558–570

`av_read_frame` and seek operations share `_formatIoGate`. Since seeks are rare but demuxing is continuous, the demux thread holds the lock on every packet. A `ReaderWriterLockSlim` (demux = read lock, seek = write lock) would eliminate contention on the common path.

---

### 2.8 `NDISource.Dispose` calls `SetState(NDISourceState.Disconnected)` after `_disposed = true`

**File:** `NDISource.cs` lines 428–446

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    ...
    SetState(NDISourceState.Disconnected);   // fires StateChanged on ThreadPool
}
```

`SetState` dispatches `StateChanged` to the ThreadPool. The event fires after `_disposed = true`, meaning event handlers that reference the `NDISource` will find it already disposed. If a handler calls any method on the source it will get `ObjectDisposedException`.

**Fix:**  
Fire the state change before setting `_disposed = true`, or guard all public members with `ObjectDisposedException.ThrowIf(_disposed, this)` only *after* the disposal sequence completes.

---

### 2.9 ✅ FIXED — `AvaloniaOpenGlVideoOutput`: CPU pixel conversion on every non-RGBA frame

**File:** `AvaloniaOpenGlVideoOutput.cs`

**Fix applied:**  
The `BasicPixelFormatConverter _converter` field and entire CPU conversion branch were removed from `AvaloniaOpenGlVideoOutput`. The `AvaloniaGlRenderer` now has dedicated GPU shader programs for all major formats (see §4.2 updated table). Frames are uploaded directly to the GPU in their native layout.

---

## 3. End-User Simplification Opportunities

### 3.1 No high-level `MediaPlayer` facade

The minimal playback sequence (open → create mixer → attach output → add channels → route → start → wait for end → stop → dispose) takes ~20 lines and requires understanding the full pipeline. For common one-source one-output playback, a `MediaPlayer` or `SimplePlayer` class with `Play(path)`, `Stop()`, `Seek(TimeSpan)`, and `PlaybackEnded` event would massively lower the entry barrier. The existing test programs are good blueprints for what such a class should automate.

---

### 3.2 No convenience `AddAudioChannel(channel)` overload that auto-routes

Every call to `avMixer.AddAudioChannel(ch, routeMap)` requires an explicit `routeMap`. For the common case where src and dst channel counts match (or mono-to-stereo expansion is wanted), an overload that accepts just the channel and auto-derives the map would be cleaner:

```csharp
// Proposed:
void AddAudioChannel(IAudioChannel channel);
// Derives: ChannelRouteMap.Auto(channel.SourceFormat.Channels, LeaderFormat.Channels)
```

---

### 3.3 ✅ FIXED — `FFmpegDecoder` forces users to pick channels by index

**Fix applied:**  
Added `FFmpegDecoder.FirstAudioChannel` and `FFmpegDecoder.FirstVideoChannel` convenience properties that return `null` (not throw) when no matching stream exists.

---

### 3.4 NDI source discovery pattern is verbose

The discover → pick → open pattern in every NDI test app is ~30 lines. An `NDISourcePicker` helper (or extending `NDISource.OpenByNameAsync` to optionally show all discovered sources to a callback/delegate) would help.

---

### 3.5 ✅ FIXED — PortAudio device selection requires two steps (engine → list → pick)

**Fix applied:**  
Added `PortAudioEngine.GetDefaultOutputDevice()` and `PortAudioEngine.GetDefaultInputDevice()` that call `Pa_GetDefaultOutputDevice` / `Pa_GetDefaultInputDevice` directly and return `null` if no default exists.

---

### 3.6 `IAVMixer` has parallel but asymmetric APIs for sinks vs endpoints

There are two levels of video output registration:
- `RegisterVideoSink` / `RouteVideoChannelToSink` (sink = `IVideoSink`)
- `RegisterVideoEndpoint` / `RouteVideoChannelToEndpoint` (endpoint = `IVideoFrameEndpoint`)

For audio there is no endpoint level—only sinks. This asymmetry is confusing. Either unify on one abstraction or add `IAudioBufferEndpoint` routing shorthand methods that mirror the video endpoint API.

---

## 4. OpenGL Renderer — Pixel Format Gap Analysis

### 4.1 SDL3 `GLRenderer` — current coverage

| Format | Upload strategy | Shader |
|--------|----------------|--------|
| `Bgra32` | 1 RGBA8 texture | Passthrough |
| `Rgba32` | 1 RGBA8 texture | Passthrough |
| `Nv12` | Y (R8) + UV (RG8) | `FragmentNv12` |
| `Yuv420p` | Y (R8) + U (R8) + V (R8) | `FragmentI420` |
| `Yuv422p10` | Y/U/V (R16UI each) | `FragmentI422P10` |
| `Uyvy422` | packed RGBA8 at w/2 | `FragmentUyvy422` |
| `P010` ✅ | Y (R16UI) + UV (RG16UI) | `FragmentP010` |
| `Yuv420p10` ✅ | Y/U/V (R16UI each) | `FragmentYuv420p10` |
| `Yuv444p` ✅ | Y/U/V (R8 each, full res) | `FragmentYuv444p` |
| `Rgb24` ✅ | packed RGB8 → RGBA upload | `FragmentRgb24` |
| `Bgr24` ✅ | packed BGR8 → RGBA upload | `FragmentBgr24` |
| `Gray8` ✅ | single R8 luma texture | `FragmentGray8` |

SDL3 coverage is now comprehensive. Color matrix (BT.601 / BT.709 auto) and range (full / limited) are handled per-frame via `IVideoColorMatrixHint`.

### 4.2 ✅ FIXED — Avalonia `AvaloniaGlRenderer` — updated coverage

| Format | Status |
|--------|--------|
| `Rgba32` | ✅ Native GPU upload |
| `Bgra32` | ✅ Native GPU upload (swizzle shader) |
| `Nv12` | ✅ Native GPU (Y+UV planes) |
| `Yuv420p` | ✅ Native GPU (Y+U+V planes) |
| `Yuv422p10` | ✅ Native GPU (3× R16UI planes) |
| `Uyvy422` | ✅ Native GPU (packed, fixed sampler name bug) |
| `P010` | ✅ Native GPU (R16UI Y + RG16UI UV) |
| `Yuv444p` | ✅ Native GPU (3× R8 full-res planes) |
| `Gray8` | ✅ Native GPU (R8 luma, replicated to RGB) |

CPU conversion via `BasicPixelFormatConverter` has been **completely removed** from the Avalonia render path. All formats are uploaded and converted on the GPU.

**UYVY bug fixed:** The UYVY sampler was being bound to uniform name `"uTexture"` but the `FragmentUyvy422` shader declares `uniform sampler2D uTexUYVY`. Fixed to `"uTexUYVY\0"u8`.

### 4.3 ✅ FIXED — New `PixelFormat` values added (both renderers)

| Format | Description | Status |
|--------|-------------|--------|
| `P010` | 10-bit semi-planar YUV 4:2:0 | ✅ Added to `PixelFormat` enum, both renderers, `FFmpegVideoChannel` |
| `Yuv420p10` (I010) | 10-bit planar 4:2:0 | ✅ Added |
| `Yuv444p` | 8-bit planar 4:4:4 | ✅ Added |
| `Rgb24` / `Bgr24` | 24-bit packed RGB | ✅ Added |
| `Gray8` | Single-channel luma | ✅ Added |

### 4.4 Color matrix / HDR gap

Both renderers only handle BT.601 and BT.709. Missing:

| Color space | Use case |
|-------------|----------|
| `BT.2020` | Wide-gamut HDR video from modern cameras and streaming services |
| PQ (ST.2084) tone-map | HDR10 content displayed on SDR monitors |
| HLG (ARIB STD-B67) | Broadcast HDR |

Adding a `ColorPrimaries` / `TransferCharacteristics` field to `VideoFormat` (matching FFmpeg's `AVColorPrimaries` / `AVColorTransferCharacteristic`) and wiring it through `IVideoColorMatrixHint` would let the shaders do correct SDR tone-mapping for HDR sources.

---

## 5. Stream-End / EOF Events

### 5.1 ✅ FIXED — What happens today at EOF

| Source | Previous behavior | Current behavior |
|--------|------------------|-----------------|
| `FFmpegDecoder` | Demux loop exits silently | ✅ `EndOfMedia` event fired on ThreadPool after last packet queued |
| `FFmpegAudioChannel` | `FillBuffer` returns 0 silently | ✅ `EndOfStream` event fired when packet reader completes |
| `FFmpegVideoChannel` | `FillBuffer` returns 0 silently | ✅ `EndOfStream` event fired via `RaiseEndOfStream()` |
| `NDIAudioChannel` | No event | ✅ `EndOfStream` event member added to `IAudioChannel` |
| `NDIVideoChannel` | No event | ✅ `EndOfStream` event member added to `IVideoChannel` |
| `IMediaChannel<TFrame>` | No EOF event | ✅ `event EventHandler? EndOfStream` added to base interface |

### 5.2 Implemented event additions

#### `FFmpegDecoder` — `EndOfMedia`
```csharp
/// <summary>
/// Raised when the demux loop reaches the end of the media file/stream.
/// Fired once on a ThreadPool thread after all encoded packets have been
/// queued; the audio/video channels may still have buffered frames to drain.
/// </summary>
public event EventHandler? EndOfMedia;
```

#### `IMediaChannel<TFrame>` — `EndOfStream`
```csharp
/// <summary>
/// Raised when the channel's backing source signals that no more frames
/// will be produced. For file-backed channels this fires once after the last
/// frame has been pushed into the ring buffer.
/// </summary>
event EventHandler? EndOfStream;
```

#### `IAudioChannel` — `new EndOfStream`
`IAudioChannel` re-declares `EndOfStream` with `new` to allow audio-specific subscription without casting to the generic base interface.

### 5.3 Example of how EOF events change the user experience

**Before (polling):**
```csharp
// User must press Enter or Ctrl+C — no automatic stop
try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }
```

**After (event-driven):**
```csharp
// Stop automatically at end of media file
decoder.EndOfMedia += (_, _) => cts.Cancel();
try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }
```

---

## 6. Summary Table

| # | Area | Severity | Type | Status |
|---|------|----------|------|--------|
| 1.1 | `OverrideRtMixer` / `OverridePresentationMixer` public on interfaces | Medium | API Design | Open |
| 1.2 | `IVideoChannel` missing `BufferAvailable` / `BufferDepth` | Medium | API Design | ✅ Fixed |
| 1.3 | `FillBuffer` return 0 ambiguous (underrun vs not started) | Low | API Design | Open |
| 1.4 | `IVideoOutput.Open` pixel format silently overridden | Low | API Design / Docs | ✅ Fixed |
| 1.5 | `ChannelRouteMap` missing `MonoToStereo` / `Auto` factories | High | End-user UX | ✅ Fixed |
| 1.6 | `NDIAVSink` constructor has 15 parameters | High | API Design | ✅ Fixed |
| 1.7 | `PortAudioOutput.Name` returns type name, not device name | Low | API Design | ✅ Fixed |
| 1.8 | `SetActiveChannelForSink` throws on re-route without doc | Medium | API Design | Open |
| 2.1 | `AudioChannel.RentChunkBuffer` re-enqueues undersized buffers | Medium | Bug / Performance | ✅ Fixed |
| 2.2 | `BasicPixelFormatConverter` counters are static (shared) | Low | Bug | ✅ Fixed |
| 2.3 | `TryConvertI210Managed` ignores color range/matrix | Medium | Correctness | Open |
| 2.4 | `VirtualAudioOutput.Dispose` sync-over-async deadlock risk | Medium | Bug | ✅ Fixed |
| 2.5 | `AggregateOutput.Dispose` disposes externally-owned sinks | High | Bug | ✅ Fixed |
| 2.6 | `VideoMixer.GetOffsetForChannel` locks on render hot path | Low | Performance | Open |
| 2.7 | `FFmpegDecoder` lock on every demux packet | Low | Performance | Open |
| 2.8 | `NDISource.Dispose` fires event after `_disposed = true` | Medium | Bug | Open |
| 2.9 | Avalonia renderer CPU-converts all non-RGBA frames | High | Performance | ✅ Fixed |
| 3.1 | No `MediaPlayer` high-level facade | High | End-user UX | Open |
| 3.2 | No `AddAudioChannel(channel)` auto-route overload | Medium | End-user UX | Open |
| 3.3 | `FFmpegDecoder` no `FirstAudioChannel` / `FirstVideoChannel` | Medium | End-user UX | ✅ Fixed |
| 3.4 | NDI source discovery pattern is verbose | Low | End-user UX | Open |
| 3.5 | No `GetDefaultOutputDevice()` on PortAudio engine | Medium | End-user UX | ✅ Fixed |
| 3.6 | Asymmetric sink vs endpoint APIs on `IAVMixer` | Low | API Design | Open |
| 4.x | Avalonia renderer missing native YUV shader paths | High | Performance | ✅ Fixed |
| 4.3 | Missing `P010`, `Yuv420p10`, `Yuv444p`, `Rgb24`, `Gray8` formats | Medium | Feature Gap | ✅ Fixed |
| 4.4 | No BT.2020 / HDR tone-mapping support | Low | Feature Gap | Open |
| 5.x | No EOF / stream-end events on any source type | **Critical** | Feature Gap | ✅ Fixed |
