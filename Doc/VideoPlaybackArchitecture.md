# Video Playback & Routing Architecture Plan

> **Status:** Design finalised — all §13 open questions resolved (April 2026); §15/A–L decided, §15/Group M final questions added (April 2026)  
> **Scope:** OpenGL-based video rendering (Avalonia), multi-sink routing, hardware decode,
> pixel-format negotiation, and A/V synchronisation — mirroring the flexibility of the
> existing audio pipeline.

---

## 1. Goals

| Goal                                                    | Notes |
|---------------------------------------------------------|-------|
| Render video to an Avalonia `OpenGlControlBase` control | Primary local display |
| Send video to NDI simultaneously with local display     | Same source, two sinks |
| Keep audio and video in sync across all active sinks    | Shared clock master |
| Support hardware-accelerated decoding                   | VA-API (Linux), NVDEC, VideoToolbox, D3D11VA |
| Handle wide pixel-format set natively in shaders        | Avoid CPU-side colour conversion where possible |
| Handle ProRes YUV422P10LE at 4K/60 fps as the benchmark | Confirmed target: 3840×2160 or 4096×2160 @ 60 fps |
| Mirror audio pipeline flexibility                       | Per-sink format negotiation, clone-to-multiple-sinks, aggregate routing |

---

## 2. Current State

### Already implemented

| Component | Location | Relevance |
|-----------|----------|-----------|
| `VideoFrame` record | `S.Media.Core/Media/VideoFrame.cs` | Frame envelope; carries `Data`, `Pts`, `MemoryOwner` |
| `VideoFormat` record | `S.Media.Core/Media/VideoFormat.cs` | Width × Height, PixelFormat, frame-rate fraction |
| `PixelFormat` enum | `S.Media.Core/Media/PixelFormat.cs` | Bgra32, Rgba32, NV12, Yuv420p, Uyvy422, **Yuv422p10** |
| `IMediaChannel<VideoFrame>` | `S.Media.Core/Media/IMediaChannel.cs` | Pull interface: `FillBuffer(Span<VideoFrame>, int)` |
| `FFmpegVideoChannel` | `S.Media.FFmpeg/FFmpegVideoChannel.cs` | Software decode → `VideoFrame`; `ArrayPool` rental |
| `NDIVideoChannel` | `S.Media.NDI/NDIVideoChannel.cs` | NDI framesync pull → `VideoFrame`; `ArrayPool` rental |
| `ArrayPoolOwner<T>` / `NDIVideoFrameOwner` | Both projects | `IDisposable` RAII wrapper returning rentals to pool |
| `IMediaClock` / `MediaClockBase` | `S.Media.Core/Clock/` | Shared clock abstraction; audio already uses it |
| Hardware decode option | `FFmpegDecoderOptions.HardwareDeviceType` | Passed to `av_hwdevice_ctx_create`; output lands in `VideoFrame` |

### Gaps

- No `IVideoSink` / `IVideoOutput` — no display-side abstraction
- No `IVideoConverter` — no per-sink pixel-format negotiation
- No `VideoRouter` / `AggregateVideoOutput` — no multi-sink fan-out
- No Avalonia GL control
- No zero-copy GPU→GL path for hardware-decoded frames
- `PixelFormat` enum needs extension (P010, P210, YUV444P10, etc.)
- No NDI video sender sink

---

## 3. New Abstractions

### 3.1 `VideoFormat` — pixel-format expansion

Extend the `PixelFormat` enum to cover the full set of formats the pipeline will encounter.
Grouped by bit-depth and chroma sub-sampling:

```csharp
public enum PixelFormat
{
    // ── 8-bit packed ──────────────────────────────────────────────────────────
    Bgra32,         // existing — OpenGL: GL_BGRA / GL_UNSIGNED_BYTE
    Rgba32,         // existing — OpenGL: GL_RGBA / GL_UNSIGNED_BYTE
    Bgrx32,         // NDI BGRX / no alpha
    Rgbx32,         // NDI RGBX / no alpha

    // ── 8-bit planar / semi-planar YUV ────────────────────────────────────────
    Yuv420p,        // existing — planar 4:2:0 (I420)
    Yv12,           // planar 4:2:0, V before U (YV12)
    Nv12,           // existing — semi-planar 4:2:0 (Y + interleaved UV)
    Yuv422p,        // planar 4:2:2
    Uyvy422,        // existing — packed 4:2:2 (UYVY)

    // ── 10-bit planar ─────────────────────────────────────────────────────────
    Yuv420p10,      // planar 4:2:0 10-bit LE (p010 / av: yuv420p10le)
    Yuv422p10,      // existing — planar 4:2:2 10-bit LE (ProRes 422 native)
    Yuv444p10,      // planar 4:4:4 10-bit LE (ProRes 4444 / HEVC 4:4:4)

    // ── 10/12-bit semi-planar ─────────────────────────────────────────────────
    P010,           // semi-planar 4:2:0 10-bit (VAAPI/NVDEC HEVC output; D3D11VA)
    P210,           // semi-planar 4:2:2 10-bit (VAAPI ProRes output on some drivers)

    // ── 16-bit semi-planar — NDI high-bit-depth ───────────────────────────────
    P216,           // NDI P216: semi-planar 4:2:2 16-bit per component
    Pa16,           // NDI PA16: P216 + alpha plane

    // ── High-bit-depth RGB ────────────────────────────────────────────────────
    Rgb48,          // planar 48-bit RGB (16 bits per channel)
    Rgba64,         // packed 64-bit RGBA (16 bits per channel)
}
```

Add a companion `PixelFormatInfo` static helper with bytes-per-pixel, plane count, horizontal
and vertical chroma sub-sampling ratios, and whether the format is natively NDI-sendable.

---

### 3.2 `IVideoSink`

Mirrors `IAudioSink`.  Called once per frame from the video clock/output thread.

```csharp
public interface IVideoSink : IDisposable
{
    string Name { get; }
    bool   IsRunning { get; }

    /// <summary>
    /// Pixel formats this sink can accept, in preference order.
    /// The router selects the first format that matches the source (or converts to it).
    /// </summary>
    IReadOnlyList<PixelFormat> PreferredFormats { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Called by VideoRouter before the first ReceiveFrame(), supplying the negotiated
    /// source format (resolution, frame rate, pixel format).
    /// Allows sinks to pre-declare NDI frame rate, allocate per-format resources, etc.
    /// Decision (§13.6): declared frame rate is read from the channel's VideoFormat;
    /// for NDI this is the most efficient path as it avoids runtime inter-frame Pts inference.
    /// </summary>
    void Configure(VideoFormat format);

    /// <summary>
    /// Called by <see cref="VideoRouter.NotifyStreamChanged"/> when the source channel
    /// reports a mid-stream format change (resolution, frame rate, or pixel format).
    /// Implementations must adapt accordingly: <c>NDIVideoSink</c> restarts the NDI sender
    /// and raises a <c>SenderRestarted</c> diagnostic event; other sinks reallocate buffers
    /// or reconfigure internal state as needed.
    /// Decision (§13.6).
    /// </summary>
    void OnStreamChanged(VideoFormat newFormat);

    /// <summary>
    /// Called once per frame on the video-output thread.
    /// Must not block — copy to internal ring and return.
    /// Sinks that need the frame data past this call must call
    /// <c>((SharedVideoFrameOwner)frame.MemoryOwner).AddRef()</c> before returning
    /// and dispose the owner when done.
    /// </summary>
    void ReceiveFrame(in VideoFrame frame);
}
```

---

### 3.3 `IVideoConverter`

Per-sink CPU-side format converter, analogous to `IAudioResampler`.

```csharp
public interface IVideoConverter : IDisposable
{
    /// <summary>Convert <paramref name="src"/> to <paramref name="dstFormat"/>.</summary>
    VideoFrame Convert(in VideoFrame src, PixelFormat dstFormat);
}
```

Default implementation: `FFmpegVideoConverter` (wraps `sws_scale`).
For common single-step conversions (e.g. `Yuv422p10` → `P216`) a hand-written SIMD path
is preferable to avoid `sws_scale` overhead on large frames.

---

### 3.4 `VideoRouter` (the video-side `AudioMixer`)

Pulls frames from registered `IMediaChannel<VideoFrame>` sources and distributes them to
registered `IVideoSink` targets.  Because video is frame-based (not sample-stream-based), the
design differs from `AudioMixer`:

```csharp
public sealed class VideoRouter : IDisposable
{
    // Register a source channel.
    public void AddChannel(IMediaChannel<VideoFrame> channel);
    public void RemoveChannel(Guid id);

    // Register a sink.  The router negotiates the pixel format at this point.
    public void AddSink(IVideoSink sink, IVideoConverter? converter = null);
    public void RemoveSink(IVideoSink sink);

    // Broadcast a mid-stream format change to all registered sinks (decision §13.6).
    // Call this when a source channel's VideoFormat changes; each sink's OnStreamChanged()
    // is invoked so it can reconfigure (e.g. NDIVideoSink restarts the NDI sender).
    public void NotifyStreamChanged(Guid channelId, VideoFormat newFormat);

    // Called by the output / VSync loop with the current clock position.
    // Selects the best frame per channel, converts if needed, and calls
    // sink.ReceiveFrame() for every registered sink.
    public void Dispatch(TimeSpan clockPosition);
}
```

**Frame selection** (`Dispatch` internals):

1. For each registered channel call `channel.FillBuffer(singleFrameSpan, 1)`.
2. The `FFmpegVideoChannel` / `NDIVideoChannel` rings expose the frame nearest to
   `clockPosition` — i.e. the last frame whose `Pts ≤ clockPosition + halfFramePeriod`.
3. For each sink, apply converter if the source format differs from the sink's negotiated format.
4. Call `sink.ReceiveFrame(in frame)`.
5. Dispose the frame's `MemoryOwner` if none of the sinks retained it.

> **Note:** For a single-source single-sink case (most common) there is zero allocation in
> the steady state: the frame's memory is the `ArrayPool` rental from the decoder, passed
> through without copy.  Only multi-sink or format-mismatch paths allocate.

---

### 3.5 `IVideoOutput` and `VirtualVideoOutput`

```csharp
public interface IVideoOutput : IDisposable
{
    VideoRouter  Router  { get; }
    IMediaClock  Clock   { get; }
    bool         IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// A headless video output that drives VideoRouter.Dispatch() on a timer
/// (or externally from a VSync callback).  Useful when the only sinks are
/// NDI / recording — no local window needed.
/// </summary>
public sealed class VirtualVideoOutput : IVideoOutput { ... }
```

---

### 3.6 `AggregateVideoOutput`

Mirrors `AggregateOutput` for audio: wraps a leader `IVideoOutput` and fans out to
additional `IVideoSink` instances.

```csharp
var glControl  = new OpenGLVideoControl(...);          // leader (drives VSync timing)
var agg        = new AggregateVideoOutput(glControl);
agg.AddSink(ndiVideoSink);                             // simultaneous NDI
agg.Router.AddChannel(videoChannel);

await agg.StartAsync();
```

---

### 3.7 `VideoClockSource` — user-selectable clock mode

**Decision (from §12.2):** The clock source is user-selectable via an enum.
`IVideoOutput` / `VirtualVideoOutput` expose a `ClockSource` property that can be set before
`StartAsync()` or potentially switched at runtime (with a brief recalibration step).

```csharp
public enum VideoClockSource
{
    /// <summary>
    /// VSync-driven.  The GL control's OnOpenGlRender callback triggers each Dispatch.
    /// Tightest A/V sync for local display; ties dispatch rate to the monitor refresh rate.
    /// Telecine-aware cadence (configurable — see §3.9) minimises judder when the source
    /// and display frame rates differ.
    /// </summary>
    VSync,

    /// <summary>
    /// External IMediaClock master (e.g. NDIClock for NDI sources, HardwareClock when
    /// PortAudio is the audio output).  Best for networked / multi-output scenarios.
    /// </summary>
    External,

    /// <summary>
    /// Fixed frame rate driven by a Stopwatch timer thread (same pattern as
    /// VirtualAudioOutput.TickLoopAsync).  Useful for headless NDI / recording
    /// pipelines where no display is present.
    /// </summary>
    FixedFrameRate,

    /// <summary>
    /// Drives the output at the frame rate advertised by the source channel
    /// (e.g. 24 fps, 29.97 fps, 60 fps from VideoFormat.FrameRate).
    /// Distinct from FixedFrameRate in that the rate is read from the source at
    /// startup rather than user-specified.  Falls back to FixedFrameRate with a
    /// configurable default if the source declares a variable or unknown frame rate.
    /// </summary>
    SourceFrameRate,
}
```

`VirtualVideoOutput` accepts a `VideoClockSource` constructor parameter.
`OpenGLVideoControl` always uses `VSync` as its internal driver but can forward to an
external clock for frame selection (i.e. VSync triggers the render loop, but the frame
chosen is selected by `clock.Position`).


---

### 3.8 Telecine-aware cadence (configurable)

**Decision (from §13.1):** Implement telecine-aware frame scheduling as a configurable
option on `VideoRouter`.  Safe defaults are chosen so the pipeline works correctly out of
the box without any configuration:

```csharp
public enum CadenceMode
{
    /// <summary>Basic nearest-frame selection. Default.</summary>
    NearestFrame,
    /// <summary>Adaptive cadence that detects 3:2, 2:2, etc. pulldown patterns.</summary>
    TelecineAdaptive,
}
```

**Safe defaults:**

| Parameter | Default | Notes |
|-----------|---------|-------|
| `CadenceMode` | `NearestFrame` (auto-upgraded by `Preflight()`) | Upgraded to `TelecineAdaptive` when fps mismatch detected |
| NTSC threshold | **0.5 %** | Covers 23.976≈24, 29.97≈30, 59.94≈60; values within 0.5 % treated as equal |
| Observation window | **30 frames** | ~0.5 s at 60 fps, ~1.25 s at 24 fps; configurable via `TelecineObservationFrames` |
| Scope | **Per-output** | One cadence pattern per `VideoRouter`; not per-channel |
| Sink preference | **Optional** | Sinks may expose `CadenceMode? PreferredCadence { get; }` (nullable; `null` = no preference); router honours sink preference if all sinks agree, otherwise uses auto-selected mode |

`Preflight()` auto-upgrades `CadenceMode` from `NearestFrame` to `TelecineAdaptive` when the
source-to-display fps ratio is not within the NTSC threshold.  Explicit user assignment of
`router.CadenceMode` before `Preflight()` always wins.


---

### 3.9 `SharedVideoFrameOwner` — ref-counted frame lifetime

**Decision (from §12.6 + §13.4):** Use ref-counting to allow multiple sinks to hold the
same frame buffer alive without copying.  Instances are pooled via `ObjectPool<T>`
(`Microsoft.Extensions.ObjectPool`) to eliminate per-frame GC pressure.

```csharp
/// <summary>
/// Ref-counted wrapper around a VideoFrame's MemoryOwner.
/// Created once per dispatched frame by VideoRouter (obtained from an ObjectPool);
/// returned to the pool on final Dispose().
/// Thread-safe via Interlocked.
/// </summary>
public sealed class SharedVideoFrameOwner : IDisposable
{
    // Accessed only by the owning ObjectPool — not part of the public API.
    internal IDisposable? Inner;

    private int _refCount;

    /// <summary>Called by VideoRouter after taking from pool; sets the wrapped owner.</summary>
    internal void Initialise(IDisposable? inner)
    {
        Inner      = inner;
        _refCount  = 1;   // router holds the initial reference
    }

    /// <summary>Called by each sink that intends to hold the frame past ReceiveFrame().</summary>
    public void AddRef() => Interlocked.Increment(ref _refCount);

    /// <summary>
    /// Release one reference.  When count reaches zero, disposes the inner MemoryOwner
    /// and returns this instance to the ObjectPool for reuse.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Inner?.Dispose();
            Inner = null;
            _pool.Return(this);   // pool reference injected at construction
        }
    }
}
```

The `ObjectPool<SharedVideoFrameOwner>` is owned by `VideoRouter` (one pool per router).
Pool capacity: configurable via `VideoRouter` constructor (default 32 — sufficient for 60 fps
with up to ~4 sinks each holding 1 frame).

**Pool exhaustion strategy (decision §13.4):** Each sink gets its **own** independent
`SharedVideoFrameOwner` reference (one `AddRef` per registered sink per dispatched frame),
enabling per-sink drop without affecting other sinks.  A congested sink's write-thread
queue being full means that sink simply skips the current frame — the GL sink continues
unaffected.

Sinks opt in to global-drop behaviour via a `SinkDropPolicy` supplied at `AddSink` time:

```csharp
public enum SinkDropPolicy
{
    /// <summary>Default: only this sink drops when its queue is full.</summary>
    DropIndependent,
    /// <summary>Opt-in: when this sink is congested, all sinks drop the current frame.</summary>
    DropWithAll,
}
```

`AddSink` signature:
```csharp
public void AddSink(IVideoSink sink,
                    IVideoConverter? converter  = null,
                    SinkDropPolicy   dropPolicy = SinkDropPolicy.DropIndependent);
```

The pool capacity is exposed as a `VideoRouter` constructor parameter (default 32;
one slot consumed per sink per frame, so capacity ≥ `sinkCount × maxInFlightFrames`):

```csharp
public VideoRouter(int ownerPoolCapacity = 32) { ... }
```


---

## 4. Avalonia OpenGL Control

### 4.1 Class hierarchy

```
Avalonia.OpenGL.Controls.OpenGlControlBase
    └── OpenGLVideoControl  (in new S.Media.Avalonia project)
            IVideoOutput, IVideoSink (self-renders)
```

`OpenGlControlBase` provides:
- `OnOpenGlInit(GlInterface gl)` — one-time shader/texture setup
- `OnOpenGlRender(GlInterface gl, int framebufferId)` — called every VSync
- `OnOpenGlDeinit(GlInterface gl)` — cleanup

### 4.2 Rendering pipeline per VSync

```
OnOpenGlRender()
    ├─ 1. Acquire latest VideoFrame from internal ring (lock-free CAS swap)
    ├─ 2. If frame is new → upload pixel planes to GL textures
    │         (texture is NOT re-uploaded if frame PTS matches previous)
    ├─ 3. glUseProgram(shader for current PixelFormat)
    ├─ 4. glDrawArrays(fullscreen quad)
    └─ 5. Return old frame's MemoryOwner to pool (deferred to avoid blocking VSync)
```

The control also participates as an `IVideoSink`:

```csharp
void IVideoSink.ReceiveFrame(in VideoFrame frame)
{
    // CAS-swap into _pendingFrame; the old frame's MemoryOwner is released.
    // No GL calls here — rendering always happens on the GL thread in OnOpenGlRender.
    // RequestNextFrameRendering() must be called on the UI thread (decided §12.3).
    Dispatcher.UIThread.Post(() => RequestNextFrameRendering(),
        DispatcherPriority.Render);
}
```

**Decision (§12.3):** `RequestNextFrameRendering()` is NOT safe to call from off the UI
thread.  The implementation always posts to `Dispatcher.UIThread` with
`DispatcherPriority.Render`.  The added latency is one dispatcher frame (< 1 ms in practice),
which is negligible relative to the VSync period.

### 4.3 Texture layout per pixel format

| Format | Texture planes | GL internal format | Sampler type |
|--------|---------------|--------------------|--------------|
| `Bgra32` / `Rgba32` | 1 (packed) | `GL_RGBA8` | `sampler2D` |
| `Nv12` | 2 (Y + UV) | `GL_R8` + `GL_RG8` | `sampler2D` × 2 |
| `Yuv420p` / `Yuv422p` | 3 (Y + U + V) | `GL_R8` × 3 | `sampler2D` × 3 |
| `Yuv422p10` (ProRes) | 3 planes × 16-bit | `GL_R16` × 3 | `sampler2D` × 3 |
| `Yuv420p10` / `P010` | 2 planes | `GL_R16` + `GL_RG16` | `sampler2D` × 2 |
| `P216` (NDI) | 2 planes | `GL_R16` + `GL_RG16` | `sampler2D` × 2 |
| `Uyvy422` | 1 (packed 4:2:2) | `GL_RG8` (half-width) | `usampler2D` |

For `Yuv422p10` (the ProRes 4K benchmark):
- Y plane: `width × height × 2` bytes → single `GL_R16` texture
- U plane: `(width/2) × height × 2` bytes → half-width `GL_R16` texture
- V plane: same as U
- Upload via `glTexImage2D(..., GL_RED, GL_UNSIGNED_SHORT, ...)` with `GL_R16` internal format
- **Bit alignment note (corrected — see §15/A.2):** FFmpeg `AV_PIX_FMT_YUV422P10LE` stores each
  10-bit sample **right-aligned** in a `uint16_t` (bits 9..0 hold the value; bits 15..10 = 0).
  This matches libyuv I210 layout.  The GLSL shader must normalise by `1023.0` (not `65535.0`)
  when reading these textures, OR the upload path must left-shift each sample by 6 before
  calling `glTexImage2D` to fill the full `GL_R16` range.  The decision is to **left-shift by 6
  in the passthrough upload path** (SIMD, done once per frame) so shaders can uniformly
  normalise by `65535.0` regardless of source bit-depth.

### 4.4 Fragment shader library

One GLSL shader program per source pixel format, selected at upload time:

```
shaders/
    yuv422p10_to_rgb.frag    ← ProRes 422 / 422 LT / 422 HQ
    yuv420p10_to_rgb.frag    ← HEVC Main10 / VP9 Profile 2
    nv12_to_rgb.frag         ← H.264 / HEVC hardware decode
    yuv420p_to_rgb.frag      ← H.264 software
    uyvy422_to_rgb.frag      ← NDI default / MJPEG
    p216_to_rgb.frag         ← NDI high-bit-depth
    passthrough.frag         ← Bgra32 / Rgba32 (identity)
```

All shaders implement the full BT.601 / BT.709 / BT.2020 matrix selectable via a uniform,
so the correct colour space is applied based on the stream's `color_space` metadata from FFmpeg.

HDR tone-mapping (PQ → SDR) can be added as a shader uniform flag without changing the
texture pipeline.

### 4.5 Colour space and HDR metadata

**Decision (§12.8):** Add full colour-space and HDR metadata to `VideoFrame` now to avoid a
breaking API change when HDR display support is added in Phase 4.

```csharp
public readonly record struct VideoFrame(
    int                  Width,
    int                  Height,
    PixelFormat          PixelFormat,
    ReadOnlyMemory<byte> Data,
    TimeSpan             Pts,
    ColourSpace          ColourSpace     = ColourSpace.Bt709,
    ColourRange          ColourRange     = ColourRange.Limited,
    ColourTransfer       Transfer        = ColourTransfer.Bt709,     // PQ / HLG / SDR
    ColourPrimaries      Primaries       = ColourPrimaries.Bt709,    // DCI-P3, BT.2020, etc.
    HdrMasteringDisplay? MasteringDisplay = null,   // SMPTE ST 2086 — peak luminance, primaries
    HdrContentLight?     ContentLight    = null,    // CEA-861.3 — MaxCLL / MaxFALL
    IDisposable?         MemoryOwner     = null);

/// <summary>SMPTE ST 2086 mastering display metadata.</summary>
public readonly record struct HdrMasteringDisplay(
    float RedX, float RedY,
    float GreenX, float GreenY,
    float BlueX, float BlueY,
    float WhiteX, float WhiteY,
    float MaxLuminance,   // cd/m²
    float MinLuminance);  // cd/m²

/// <summary>CEA-861.3 content light level.</summary>
public readonly record struct HdrContentLight(
    ushort MaxCll,   // Maximum Content Light Level  (cd/m²)
    ushort MaxFall); // Maximum Frame Average Light Level (cd/m²)
```

`FFmpegVideoChannel` populates all fields from `AVFrame`:
- `colorspace` → `ColourSpace`
- `color_range` → `ColourRange`
- `color_trc` → `Transfer`
- `color_primaries` → `Primaries`
- `side_data[AV_FRAME_DATA_MASTERING_DISPLAY_METADATA]` → `MasteringDisplay`
- `side_data[AV_FRAME_DATA_CONTENT_LIGHT_LEVEL]` → `ContentLight`

The fragment shaders accept `Transfer` and `Primaries` as uniforms, enabling:
- SDR BT.709 (default) — identity tone curve
- HDR PQ (BT.2020-ST2084) — Perceptual Quantizer for HDR10 content
- HLG (BT.2020-HLG) — Hybrid Log-Gamma for broadcast HDR

---

## 5. Hardware Decode → OpenGL Zero-Copy

**Decision (§12.1):** Prefer hardware decode where available; fall back to software decode
transparently.  `FFmpegDecoder` will attempt hardware device types in a platform-appropriate
priority order, validate that the output surface format is usable, and fall back to software
if any step fails.  Frame dropping is acceptable during sustained software-decode overload
on the benchmark (4K/60 ProRes).

**Decision (§12.5):** Attempt the highest-quality decode path first (e.g. 10-bit 4:2:2
surface from VA-API); if the driver returns a lower-quality format (e.g. NV12 instead of
P210), accept it and log a warning rather than aborting.  A `VideoDecodeDiagnostics` struct
attached to each `VideoFrame` can record the actual decode path used, enabling the UI to
surface "hardware decode active / surface format downgraded" status to the user.

### 5.1 Hardware decode priority order (runtime probe)

```
Platform                  Priority order
─────────────────────────────────────────────────────────────────
Linux                     vaapi → vdpau → cuda (nvdec) → software
Windows                   d3d11va → dxva2 → cuda (nvdec) → software
macOS                     videotoolbox → software
Platform-agnostic         (try all in order; use first that succeeds)
```

`FFmpegDecoderOptions.HardwareDeviceType` can still be set to a specific type to override
the auto-probe; `null` (default) triggers the priority-order probe.

### 5.2 VA-API (Linux) — DMA-BUF path

```
FFmpeg → VA-API decode → VAAPI surface (GPU memory)
    → av_hwframe_transfer_data with AV_PIX_FMT_DRM_PRIME
    → DRM PRIME fd (dma-buf)
    → EGL: eglCreateImageKHR(EGL_LINUX_DMA_BUF_EXT, ...)
    → glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, eglImage)
    → Sample in shader — no CPU copy at any step
```

Output formats from VA-API:
- H.264 / HEVC SDR → NV12
- HEVC Main10 → P010
- ProRes 422 (on drivers that expose it) → NV12 or Yuv422p  
  > **Note:** VA-API ProRes support is driver-dependent; Mesa/RadeonSI is the most complete.
  > NVIDIA proprietary drivers do NOT support ProRes via VA-API at all.
  > Software decode is the reliable fallback for ProRes everywhere.

### 5.3 NVDEC (NVIDIA) — CUDA→GL interop

```
FFmpeg → NVDEC → CUDA device pointer (AV_PIX_FMT_CUDA)
    → cuGraphicsGLRegisterBuffer (or cuGraphicsGLRegisterImage)
    → Map CUDA pointer into GL texture memory
    → No copy; upload happens on the GPU bus
```

Output formats: NV12 (8-bit), P016 (10-bit padded to 16), YUV444P16 for certain streams.

### 5.4 VideoToolbox (macOS) / D3D11VA (Windows)

Both produce hardware surfaces (CVPixelBuffer / ID3D11Texture2D) that can be imported
into OpenGL via platform-specific extensions.  Cross-platform abstraction:

```csharp
/// <summary>
/// Encapsulates a GPU-resident video frame that can be directly sampled in GL.
/// Implemented per-platform (EGL image, CUDA surface, CVPixelBuffer, D3D11 texture).
/// </summary>
public interface IGpuVideoFrame : IDisposable
{
    void BindToTexture(int textureUnit);
    PixelFormat PixelFormat { get; }
}
```

`VideoFrame` gains an optional `IGpuVideoFrame? GpuFrame` property.
`OpenGLVideoControl` checks `GpuFrame != null` and binds directly rather than uploading
from CPU-side `Data`.

### 5.5 EGL access under Avalonia

**Decision (§12.4):** Use Avalonia's `IPlatformOpenGlInterface` API.  Avalonia exposes EGL
via `AvaloniaLocator.Current.GetService<IPlatformOpenGlInterface>()` and its associated
context types.  These APIs are considered stable as of Avalonia 11.x and provide enough
surface to obtain the `EGLDisplay` / `EGLContext` handles needed for DMA-BUF import.
EGL entry points not directly exposed by Avalonia can be resolved via
`GlInterface.GetProcAddress("eglCreateImageKHR")` etc., without resorting to
`NativeLibrary.Load("libEGL.so.1")` directly.

### 5.6 Software fallback

**Decision (§12.1 + §12.5):** When hardware decode is unavailable, `FFmpegVideoChannel`
falls back to software decode automatically.  At 4K/60 ProRes 422 HQ, FFmpeg's multithreaded
software decoder consumes roughly 60–80 % of a modern 8-core CPU.  Frame dropping under
sustained overload is acceptable — `VideoRouter.Dispatch` naturally selects the newest
available frame when the decoder lags, so the display skips ahead without stalling.

When a hardware decoder returns a lower-quality surface format than the source warrants
(e.g. NV12 instead of P210 for a 10-bit stream), the pipeline accepts the downgraded
format, logs a warning, and records the actual path in `VideoFrame.DecodePath` (a lightweight
enum: `Software`, `HardwareFull`, `HardwareDowngraded`).

---

## 6. Pixel Format Negotiation Per Sink

This is the video analogue of the per-device `LinearResampler` on the audio side.

### 6.1 Negotiation at `AddSink` time

```
VideoRouter.AddSink(sink, converter?)
    ├─ Query sink.PreferredFormats (ordered list)
    ├─ For each preferred format:
    │     if source format == preferred → zero-copy path, no converter needed
    │     elif IVideoConverter.CanConvert(sourceFormat, preferred) → register converter
    ├─ If no match → use sink's last preference and attach a sws_scale converter
    └─ Store (sink, negotiatedFormat, converter?) in routing table
```

### 6.2 Format preference policies by sink type

| Sink | `PreferredFormats` rationale |
|------|------------------------------|
| `OpenGLVideoControl` | `[sourceFormat, ...]` — accepts all formats via shaders; list the source format first to get zero-copy |
| `NDIVideoSink` | `[Uyvy422, P216, Nv12, Bgra32]` — NDI-native formats only; P216 for 10-bit sources to preserve quality |
| `RecordingVideoSink` | `[sourceFormat, Yuv420p]` — prefer lossless pass-through; fall back to 8-bit for compatibility |
| `VirtualVideoOutput` | N/A — headless, no display; delegates to child sinks |

### 6.3 ProRes 4K → NDI conversion specifics

Source: `Yuv422p10` (planar, 3 planes, 10-bit LE)  
NDI best match: `P216` (semi-planar 4:2:2, 16-bit per component)

**Corrected bit-layout (see §15/A.2):** FFmpeg `AV_PIX_FMT_YUV422P10LE` stores each 10-bit
sample *right-aligned* in a `uint16_t` (0–1023; bits 15..10 = 0).  NDI P216 requires samples
in the full 16-bit range (0–65535).  A `<< 6` left-shift per sample is mandatory.

Conversion steps (zero intermediate heap allocation target):
1. Y plane: SIMD `<< 6` shift — copy each `uint16_t` sample as `(sample << 6)` into the P216 Y plane.
2. UV plane: interleave U and V planes into a single UV buffer **and** shift each value `<< 6`
   in the same pass (SIMD-friendly — two loads, two shifts, interleaved store per 4 samples).
3. `libyuv::I210ToP210` can be used to perform the interleave step, followed by a SIMD scale pass;
   or a single custom AVX2 loop can combine both steps.  `FFmpegSwsConverter` is the fallback
   when libyuv is unavailable.
4. Total cost at 4K (3840 × 2160): ~100 MB/frame × 60 fps ≈ 6 GB/s memory bandwidth  
   → achievable with AVX2; AVX-512 gives comfortable headroom

### 6.4 `IVideoConverter` default selection and libyuv

**Decision (§12.7 + §13.8):** Use `libyuv` for vectorised format conversions with version
validation at startup.  Include consumer, professional, and broadcast/SDI paths in the
initial binding so future `BlackmagicChannel` / `AJAChannel` support requires no binding
changes (decision §13.8).  A managed `sws_scale` fallback is always available.

```csharp
internal static class VideoConverterFactory
{
    private static readonly bool _libyuvAvailable =
        NativeLibrary.TryLoad("libyuv", out _);

    public static IVideoConverter Create(PixelFormat src, PixelFormat dst) =>
        (src, dst) switch
        {
            // ── Consumer (H.264 / HEVC software decode) ───────────────────────────
            (Nv12,      Bgra32)    when _libyuvAvailable => new LibyuvNv12ToBgraConverter(),
            (Yuv420p,   Nv12)      when _libyuvAvailable => new LibyuvI420ToNv12Converter(),
            // ── Professional (ProRes / HEVC Main10) ───────────────────────────────
            (Yuv422p10, P216)      when _libyuvAvailable => new LibyuvI210ToP210Converter(),
            (Yuv422p10, Bgra32)    when _libyuvAvailable => new LibyuvI210ToBgraConverter(),
            (Yuv420p10, P010)      when _libyuvAvailable => new LibyuvI010ToP010Converter(),
            // ── Broadcast / SDI (future BlackmagicChannel / AJAChannel) ──────────
            (V210,      Yuv422p10) when _libyuvAvailable => new LibyuvV210ToI210Converter(),
            (Ayuv,      Yuv444p10) when _libyuvAvailable => new LibyuvAyuvToI444Converter(),
            // ── Universal fallback (always available) ─────────────────────────────
            _ => new FFmpegSwsConverter(src, dst)
        };
}
```

`LibyuvNative` validates the loaded library's version symbol (`libyuv_version`) at startup
and falls back silently to `FFmpegSwsConverter` for any path if the version is below the
minimum known-good value.  `VideoConverterFactory.Create` is called during
`VideoRouter.AddSink` format negotiation, so converter selection is transparent to the caller.

> **Decision (§13.8):** The broadcast/SDI conversion paths (`V210→I210`, `AYUV→I444`) are
> included in the initial binding set so future `BlackmagicChannel` / `AJAChannel` support
> requires no additional binding work.  `sws_scale` fallback remains universally available.

---

## 7. A/V Synchronisation

### 7.1 Clock master selection

**Decision (§13.2):** VFR / unknown frame-rate sources using `SourceFrameRate` mode fall back
automatically in the following priority order (configurable per-output):

1. **Audio clock** — preferred when audio is present; keeps A/V in sync by definition.
   Injected via `IVideoOutput.SetExternalClock(IMediaClock)` (decision §13.2).
2. **`FixedFrameRate`** with a user-configurable default rate (default 60 fps) — used when
   no audio output is active.

```csharp
public interface IVideoOutput : IDisposable
{
    VideoRouter Router    { get; }
    IMediaClock Clock     { get; }
    bool        IsRunning { get; }

    /// <summary>
    /// Inject or replace the external clock.  Safe to call mid-playback;
    /// internally synchronised with the dispatch loop.
    /// Used for VFR audio-clock fallback and runtime clock-source switching.
    /// </summary>
    void SetExternalClock(IMediaClock clock);

    // Fallback policy when SourceFrameRate reports FrameRate = 0 (VFR):
    VfrFallbackClock VfrFallback     { get; set; }  // default: AudioClock
    double           VfrFallbackRate { get; set; }  // default: 60.0 fps
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public enum VfrFallbackClock { AudioClock, FixedFrameRate }
```

| Scenario | Clock master |
|----------|-------------|
| Local file playback (PortAudio + OpenGL) | Audio hardware clock (`HardwareClock`) |
| NDI receive (audio + video) | `NDIClock` (driven by NDI frame timestamps) |
| NDI send only (no audio hardware) | `StopwatchClock` inside `VirtualAudioOutput` |
| Headless NDI send (video only) | `StopwatchClock` inside `VirtualVideoOutput` |
| VFR source, audio present | Audio clock (`External`) |
| VFR source, no audio | `FixedFrameRate` at `VfrFallbackRate` |

### 7.2 Frame selection algorithm (`VideoRouter.Dispatch`)

```
clockNow = clock.Position
for each channel:
    ring contains frames with monotonically increasing Pts
    target = clockNow + renderAheadBudget  (≈ 0.5 × frame_period)
    selectedFrame = last frame in ring where frame.Pts ≤ target
    if no such frame → repeat previous frame (clock running ahead of decoder)
    if all frames older than clockNow − maxJitter → underrun, display black
```

`renderAheadBudget` compensates for the latency between `Dispatch` being called and the
frame actually appearing on screen after GPU scan-out.  Typical value: 8–16 ms.

### 7.3 Multi-sink synchronisation

When both `OpenGLVideoControl` (VSync-driven) and `NDIVideoSink` (send-on-receive) are
active as sinks of the same `AggregateVideoOutput`:

- Both sinks call `IVideoSink.ReceiveFrame()` from the same `Dispatch` call.
- The GL sink copies to its pending-frame slot (displayed at next VSync).
- The NDI sink enqueues to its sender ring (sent on its write thread).
- Both therefore see the **same frame at the same clock position** — they stay in sync by
  construction, since the router selects one frame per dispatch and distributes it to all sinks.

### 7.4 Audio/Video sync across aggregate outputs

When `AggregateOutput` (audio) and `AggregateVideoOutput` (video) share the same
`IMediaClock` instance:

```csharp
var clock      = new StopwatchClock(48000);
var audioOut   = new VirtualAudioOutput(audioFmt, framesPerBuffer: 1024, clock: clock);
var videoOut   = new VirtualVideoOutput(clock: clock);
```

Both pull from the same `clock.Position`, so audio and video selection are driven by
the same timeline.

### 7.5 Runtime clock source switching

**Decision (from §13.3):** Runtime switching is supported.  When `SetExternalClock` or
`SetClockSource` is called mid-playback, the output snaps `expectedTicks` to the new
clock's `Position`.

**Large backward-jump handling (decision §13.3 — safest option):**

1. If `|oldPosition − newPosition| > JumpThreshold` (configurable, default 5 s):
   - Log a warning via `ILogger` with both positions and delta.
   - Seek the router's video channels to `newPosition`.
   - Fire a `LargeClockJump` event on `VideoRouter` / `IVideoOutput` so the application
     can independently seek the audio decoder (which the video pipeline does not own):
     ```csharp
     public event EventHandler<LargeClockJumpEventArgs>? LargeClockJump;
     // Args carry OldPosition, NewPosition, Delta
     ```
2. Frames decoded before the seek arrive with PTS < newPosition; the frame selector skips
   them until PTS catches up.
3. The last displayed frame is held during the short decoder warm-up window.

The application wires up the event to seek the audio output atomically:
```csharp
videoOut.LargeClockJump += (_, e) => decoder.Seek(e.NewPosition);
```


---

## 8. NDI Video Sink

```csharp
public sealed class NDIVideoSink : IVideoSink
{
    public IReadOnlyList<PixelFormat> PreferredFormats =>
        [PixelFormat.Uyvy422, PixelFormat.P216, PixelFormat.Nv12, PixelFormat.Bgra32];

    // ReceiveFrame is called on the VideoRouter dispatch thread.
    // Enqueues to a ConcurrentQueue; NDI send happens on a dedicated write thread
    // (mirroring NDIAudioSink's design).
    public void ReceiveFrame(in VideoFrame frame) { ... }
}
```

NDI frame struct mapping:

| `PixelFormat` | `NdiFourCCVideoType` | Notes |
|---------------|---------------------|-------|
| `Uyvy422` | `Uyvy` | 8-bit 4:2:2, lowest CPU cost |
| `P216` | `P216` | 16-bit 4:2:2 semi-planar, best quality for 10-bit source |
| `Nv12` | `Nv12` | 8-bit 4:2:0, hardware decode output |
| `Bgra32` | `Bgra` | Full quality fallback |

Frame rate and timecode passed from `VideoFrame.Pts` → `NdiVideoFrameV2.Timecode`
(100 ns ticks, same units).

---

## 9. Multi-Sink Example Pipeline

```
FFmpegDecoder.Open("prores_4k.mov")
    VideoChannels[0]   ← FFmpegVideoChannel (Yuv422p10, 3840×2160, 60fps, HW optional)
    AudioChannels[0]   ← FFmpegAudioChannel (PCM 48kHz stereo)

── Audio pipeline ────────────────────────────────────────────────────────────────
VirtualAudioOutput (48kHz, 1024 frames, StopwatchClock)
  AggregateOutput
    ├─ PortAudioSink (local headphones)
    └─ NDIAudioSink  (NDI audio)

── Video pipeline ────────────────────────────────────────────────────────────────
OpenGLVideoControl (Avalonia, VSync-driven, IVideoOutput leader)
  AggregateVideoOutput
    ├─ self (GL rendering)                 ← PreferredFormats: [Yuv422p10, ...]
    └─ NDIVideoSink                        ← PreferredFormats: [P216, Uyvy422, ...]

VideoRouter
    AddChannel(videoChannel)
    AddSink(glControl,    converter: null)              // zero-copy, shader converts
    AddSink(ndiVideoSink, converter: Yuv422p10ToP216)   // SIMD interleave

── Shared clock ─────────────────────────────────────────────────────────────────
StopwatchClock — owned by VirtualAudioOutput, referenced by VirtualVideoOutput
```

---

## 10. Suggested New Projects / Files

| New item | Location | Purpose |
|----------|----------|---------|
| `S.Media.Avalonia` project | `UI/S.Media.Avalonia/` | Avalonia-specific controls |
| `OpenGLVideoControl.cs` | `S.Media.Avalonia/` | `OpenGlControlBase` subclass; `IVideoOutput` + `IVideoSink` |
| `VideoShaders/` folder | `S.Media.Avalonia/Assets/` | GLSL files embedded as resources |
| `IVideoSink.cs` | `S.Media.Core/Video/` | Sink interface |
| `IVideoConverter.cs` | `S.Media.Core/Video/` | Format conversion interface |
| `VideoRouter.cs` | `S.Media.Core/Video/` | Multi-sink routing + frame selection |
| `VideoClockSource.cs` | `S.Media.Core/Video/` | `VideoClockSource` enum |
| `VirtualVideoOutput.cs` | `S.Media.Core/Video/` | Headless clock-driven output |
| `AggregateVideoOutput.cs` | `S.Media.Core/Video/` | Multi-sink fan-out |
| `SharedVideoFrameOwner.cs` | `S.Media.Core/Video/` | Ref-counted frame lifetime wrapper |
| `ColourSpace.cs` | `S.Media.Core/Media/` | `ColourSpace`, `ColourRange`, `ColourTransfer`, `ColourPrimaries` enums |
| `HdrMetadata.cs` | `S.Media.Core/Media/` | `HdrMasteringDisplay`, `HdrContentLight` structs |
| `PixelFormatInfo.cs` | `S.Media.Core/Media/` | Static helpers: plane count, strides, NDI-sendable flag |
| `VideoConverterFactory.cs` | `S.Media.FFmpeg/` | Runtime libyuv / sws_scale selection |
| `FFmpegVideoConverter.cs` | `S.Media.FFmpeg/` | `sws_scale`-backed `IVideoConverter` |
| `LibyuvConverters.cs` | `S.Media.FFmpeg/` | P/Invoke bindings + fast-path `IVideoConverter` impls |
| `IGpuVideoFrame.cs` | `S.Media.Core/Video/` | Zero-copy GPU frame abstraction |
| `VaapiGpuVideoFrame.cs` | `S.Media.FFmpeg/` | Linux DMA-BUF / EGL image wrapper |
| `NDIVideoSink.cs` | `S.Media.NDI/` | NDI video sender sink |

---

## 11. Implementation Phases

### Phase 1 — Software decode + OpenGL display (no multi-sink)

- Add `IVideoSink`, `IVideoConverter`, `VideoRouter`
- Extend `PixelFormat` enum + `PixelFormatInfo` helper
- Add `ColourSpace` / `ColourRange` to `VideoFrame`
- Implement `OpenGLVideoControl` with shader set for Yuv422p10, NV12, Bgra32
- Wire `FFmpegVideoChannel` → `VideoRouter` → `OpenGLVideoControl`
- A/V sync via shared `StopwatchClock`
- **Benchmark:** ProRes 4K/60 — measure HW vs SW decode CPU, GL upload time, frame budget

### Phase 2 — Multi-sink routing + NDI video output

- Implement `AggregateVideoOutput`, `VirtualVideoOutput`
- Implement `NDIVideoSink` with P216 / UYVY paths
- Implement `Yuv422p10ToP216Converter` (SIMD; consider `libyuv`)
- Test: single ProRes 4K/60 source → GL window + NDI simultaneously in sync

### Phase 3 — Hardware decode + zero-copy GL path

- VA-API / DRM-PRIME → EGL image integration
- NVDEC → CUDA→GL interop
- `IGpuVideoFrame` abstraction + platform implementations
- Benchmark zero-copy vs CPU-upload paths at 4K/60
- Hardware surface seek tests in `S.Media.FFmpeg.Tests`.  Skipped automatically via a
  `[SkipIfNoHardware("vaapi")]` / `[SkipIfNoHardware("cuda")]` attribute that probes
  `av_hwdevice_iterate_types` at test collection time.  The attribute is defined in a shared
  `S.Media.TestUtils` project so it can be reused by future hardware tests in other
  assemblies.  Decision (§13.7).

### Phase 4 — Advanced features

- HDR tone-mapping shader (PQ→SDR, HLG→SDR)
- Multi-source compositing via scene graph inside `OpenGLVideoControl` (GPU-side blending;
  no CPU memory round-trip).  `VideoRouter` itself remains single-source; compositing is a
  presentation-layer concern in the GL control.  Decision (§13.5).
- Recording sink (`FFmpegRecordingVideoSink`)
- Frame-accurate seeking with pre-roll flush
- SDL3-based video sink (`SDL3VideoSink : IVideoSink`) for simple/lightweight playback
  scenarios without Avalonia.  Lives in `S.Media.SDL`.  When an SDL3 GL context cannot
  coexist with Avalonia's GL context in the same process, the SDL3 sink falls back to
  SDL3's software renderer automatically.  In practice the SDL3 and Avalonia sinks are
  not used together.  Decision (§13.5).

---

## 12. Resolved Design Decisions

All decisions are reflected in the relevant sections above.

| # | Topic | Decision |
|---|-------|----------|
| 12.1 | ProRes 4K/60 CPU decode | Prefer hardware; fall back to software. Frame dropping acceptable. `VideoFrame.DecodePath` records actual path. (§5.1, §5.6) |
| 12.2 | Clock source selection | `VideoClockSource` enum: `VSync`, `External`, `FixedFrameRate`, `SourceFrameRate`. Runtime-switchable. (§3.7, §7.5) |
| 12.3 | `RequestNextFrameRendering()` thread safety | `Dispatcher.UIThread.Post(..., DispatcherPriority.Render)`. (§4.2) |
| 12.4 | EGL under Avalonia | `IPlatformOpenGlInterface` (stable, Avalonia 11.x); entry points via `GlInterface.GetProcAddress`. (§5.5) |
| 12.5 | GPU-decoded ProRes format quality | Attempt highest quality; accept downgrade with logged warning + `DecodePath = HardwareDowngraded`. (§5.6) |
| 12.6 | Multi-sink frame memory lifecycle | `SharedVideoFrameOwner` ref-counting; pooled via `ObjectPool<T>`. (§3.9) |
| 12.7 | `libyuv` dependency | Accepted; `NativeLibrary.TryLoad` detection; `sws_scale` fallback always available. (§6.4) |
| 12.8 | HDR metadata in `VideoFrame` | Full metadata added: `ColourSpace/Range/Transfer/Primaries`, SMPTE ST 2086, CEA-861.3. (§4.5) |
| 13.1 | Telecine cadence negotiation | Safe defaults: 0.5 % NTSC threshold normalises 23.976/29.97/59.94 to their integer equivalents; observation window 30 frames (configurable via `TelecineObservationFrames`); `CadenceMode` defaults to `NearestFrame`, auto-upgraded by `Preflight()` on fps mismatch; sinks may declare `CadenceMode? PreferredCadence` (null = no preference; router honours if unanimous). (§3.8) |
| 13.2 | VFR / unknown frame-rate fallback clock injection | `SetExternalClock(IMediaClock)` on `IVideoOutput` — injectable at any time, internally synchronised with the dispatch loop. VFR fallback priority: audio clock (`External`) → `FixedFrameRate` (default 60 fps). `VfrFallback` + `VfrFallbackRate` properties expose the policy. (§7.1) |
| 13.3 | Runtime clock switching + large jumps | Safest option: snap to new clock position; log warning when `|delta| > JumpThreshold` (default 5 s, configurable); seek video channels; fire `LargeClockJump` event so the application independently seeks the audio decoder. No direct coupling from `VideoRouter` to `FFmpegDecoder`. (§7.5) |
| 13.4 | `SharedVideoFrameOwner` pool exhaustion — per-sink drop | Default: sinks drop independently (`SinkDropPolicy.DropIndependent`) — a congested NDI sink does not affect the GL sink. Opt-in `SinkDropPolicy.DropWithAll` causes all sinks to drop when that sink's queue is full. Policy supplied per-sink at `AddSink` time. Pool capacity configurable via `VideoRouter` ctor (default 32). (§3.9) |
| 13.5 | SDL3 sink — GL context coexistence | SDL3 sink falls back to SDL3 software renderer when its GL context cannot coexist with Avalonia's GL context. In practice SDL3 and Avalonia sinks are not typically deployed together; the software fallback is a worst-case safety net. Phase 4 (`S.Media.SDL`). (§11) |
| 13.6 | Dynamic metadata / mid-stream format change | `NDIVideoSink.OnStreamChanged(VideoFormat)` restarts the NDI sender and raises a `SenderRestarted` diagnostic event so callers can observe the interruption. `VideoRouter.NotifyStreamChanged(channelId, newFormat)` broadcasts the change to all registered sinks via `IVideoSink.OnStreamChanged`. (§3.2, §3.4) |
| 13.7 | `[SkipIfNoHardware]` attribute design | Attribute accepts a hardware-type string (e.g. `[SkipIfNoHardware("vaapi")]`); probes `av_hwdevice_iterate_types` at xUnit test-collection time. Defined in shared `S.Media.TestUtils` project for reuse across test assemblies. (§11 Phase 3) |
| 13.8 | `libyuv` broadcast/SDI binding set | Broadcast/SDI bindings included in the initial set: `V210→I210` and `AYUV→I444` (alongside consumer + professional paths) so future `BlackmagicChannel` / `AJAChannel` support requires no additional binding work. Minimal P/Invoke in `LibyuvNative.cs`; system-installed; version validated at startup. (§6.4, §10) |

---

## 13. ~~Remaining Open Questions~~ — All Resolved

All §13 design questions raised during the video pipeline review have been answered.
Final decisions are captured in the **§12 Resolved Design Decisions** table (rows 13.1–13.8).
The relevant body sections have been updated to reflect the final choices:

| Question | Resolved in body section |
|----------|--------------------------|
| 13.1 — Telecine cadence safe defaults, NTSC threshold, observation window | §3.8 |
| 13.2 — VFR clock injection via `SetExternalClock` | §7.1 |
| 13.3 — Large clock-jump: safest option (event-based, no direct decoder coupling) | §7.5 |
| 13.4 — Per-sink independent drop; opt-in `DropWithAll` | §3.9 |
| 13.5 — SDL3 software fallback; sinks not typically combined with Avalonia | §11 Phase 4 |
| 13.6 — `OnStreamChanged` restarts NDI sender + `SenderRestarted` event | §3.2, §3.4 |
| 13.7 — `[SkipIfNoHardware("vaapi")]` attribute in `S.Media.TestUtils` | §11 Phase 3 |
| 13.8 — Broadcast/SDI bindings (V210, AYUV) included in initial `libyuv` set | §6.4 |

---

## 14. References

- [NDI SDK Documentation](../Reference/NDI/NDI%20SDK%20Documentation.pdf) — video frame types, P216 spec
- [FFmpeg Pixel Formats](https://ffmpeg.org/doxygen/trunk/pixfmt_8h.html) — AV_PIX_FMT_YUV422P10LE etc.
- [Avalonia OpenGlControlBase source](https://github.com/AvaloniaUI/Avalonia) — threading model, `IPlatformOpenGlInterface`
- [libyuv project](https://chromium.googlesource.com/libyuv/libyuv/) — SIMD conversion routines
- [VA-API DRM PRIME export](https://01.org/linuxmedia/vaapi) — zero-copy EGL path
- [OpenGL ES / Desktop GL texture formats](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glTexImage2D.xhtml) — GL_R16, GL_RG16
- [SMPTE ST 2086](https://ieeexplore.ieee.org/document/8353899) — HDR mastering display metadata
- [CEA-861.3](https://www.cta.tech/) — MaxCLL / MaxFALL content light level metadata

---

## 15. Additional Considerations & Open Points

> **Added:** April 2026 — raised after reviewing the `Reference/libyuv/` headers (version 1922)
> and the existing implementation in `S.Media.FFmpeg` and `S.Media.Core`.
> Items marked **✅ Decision made** are resolved; body sections have been updated where noted.
> **Group C** items are new open questions raised during the decision pass.

---

### Group A — libyuv Integration

#### A.1 P210 ≠ P216: bit-depth mismatch in the `Yuv422p10 → P216` converter

The `VideoConverterFactory` maps `(Yuv422p10, P216)` to `LibyuvI210ToP210Converter`, which
wraps `libyuv::I210ToP210`.  However:

| Format | Layout | Luma value range |
|--------|--------|-----------------|
| libyuv I210 (= FFmpeg `Yuv422p10`) | 10-bit right-aligned in `uint16_t`; bits 15..10 = 0 | 0–1023 |
| P210 (libyuv output) | same — 10-bit right-aligned in `uint16_t` | 0–1023 |
| NDI P216 | **16-bit full-range** in `uint16_t` | 0–65535 |

`I210ToP210` only interleaves the U/V planes — it does **not** scale the sample values.
Sending P210-layout data through an NDI P216 connection will produce a signal at ~1.6 %
of expected brightness (1023/65535 ≈ 0.016).

> **✅ Decision:** Use `libyuv::I210ToP210` for the interleave step, then apply a `<< 6`
> SIMD shift pass on both Y and UV planes to scale from 0–1023 to 0–65535.
> `FFmpegSwsConverter` remains the fallback when libyuv is unavailable.
> See §6.3 for the updated conversion steps.

---

#### A.2 FFmpeg `Yuv422p10le` bit alignment: right-aligned, not left-aligned ✅ corrected in body

§4.3 and §6.3 previously stated:

> *"FFmpeg left-packs the 10 bits into the top of each `uint16_t`, which is exactly P216's
> layout — no CPU-side bit-shifting needed."*

This was incorrect. `AV_PIX_FMT_YUV422P10LE` stores each sample **right-aligned** — the 10
significant bits occupy bits 9..0 and bits 15..10 are always zero.  This matches libyuv's I210
layout, **not** P216.

> **✅ Decision:** §4.3 and §6.3 have been corrected.  The GL upload path will left-shift
> each sample by 6 before `glTexImage2D` so all shaders can normalise by `65535.0`
> uniformly.  The NDI P216 path (§6.3) also applies `<< 6` during the UV interleave pass.
>
> **Verification:** Run the ProRes 422 benchmark frame through `av_frame_get_buffer` and
> inspect `frame->data[0][0..1]` — a white pixel should read `0x03FF` (right-aligned)
> to confirm this before the passthrough path is implemented.

---

#### A.3 V210 → I210: no libyuv binding exists

The `VideoConverterFactory` table includes:

```csharp
(V210, Yuv422p10) when _libyuvAvailable => new LibyuvV210ToI210Converter(),
```

After examining the reference headers (`Reference/libyuv/`, version 1922), there is **no**
`V210ToI210` or equivalent function anywhere in libyuv — not in `convert.h`,
`convert_argb.h`, `convert_from.h`, `planar_functions.h`, or `row.h`.

V210 is a QuickTime/SDI packed format: 3 × 10-bit YUV samples packed into each 32-bit word
with 2-bit padding (word layout: `0b00VVVVVVVVVVUUUUUUUUUUYYYYYYYYYY`).  libyuv does not
implement this unpack.

> **✅ Decision:** Implement a hand-written managed V210 unpack loop directly in
> `LibyuvConverters.cs` (fastest option, ~50 lines C#, no native dependency).  The loop
> reads 32-bit words, extracts three 10-bit samples per word, and writes them as
> `uint16_t` in I210 layout.  `FFmpegSwsConverter` remains the fallback.
> The `VideoConverterFactory` entry remains under the `_libyuvAvailable` guard since the
> managed loop lives in the same file, but it does not actually call libyuv.

---

#### A.4 AYUV → `Yuv444p10`: no libyuv binding exists

The table includes:

```csharp
(Ayuv, Yuv444p10) when _libyuvAvailable => new LibyuvAyuvToI444Converter(),
```

libyuv provides `AYUVToNV12` and `AYUVToNV21` only — both produce **8-bit** 4:2:0 outputs.
There is no AYUV → planar 4:4:4 path and no 10-bit upconversion path in libyuv.

Additionally, AYUV is an 8-bit packed format (8 bits per A/Y/U/V); `Yuv444p10` is 10-bit
planar.  The conversion crosses both chroma subsampling and bit depth.

> **✅ Decision:** Remove `LibyuvAyuvToI444Converter` from the fast-path table.
> The `_ => new FFmpegSwsConverter(src, dst)` fallback handles this conversion.

---

#### A.5 `libyuv_version` runtime symbol availability

The doc says `LibyuvNative` validates the loaded library version via the `libyuv_version`
symbol.  In the reference headers, `LIBYUV_VERSION` is a compile-time `#define` (1922);
it is **not** declared as an exported data symbol in any of the provided headers.

In practice libyuv's CMake build does export an `int libyuv_version` global (initialised
from the `#define`), but distro-packaged builds may or may not export it.

> **✅ Decision:** Use `NativeLibrary.TryGetExport(handle, "libyuv_version", out var addr)`.
> If the export exists, read the version via `Marshal.ReadInt32(addr)`.
> If the export is absent, treat version as 0 and fall back to `FFmpegSwsConverter` for
> all paths (log a one-time warning: *"libyuv loaded but version symbol not found; fast
> paths disabled"*).  Minimum known-good version is **1922** (Reference/libyuv/ headers).

---

#### A.6 `NativeLibrary.TryLoad("libyuv")` — platform-specific library names

A single string `"libyuv"` may not resolve on all platforms:

| Platform | Typical install name |
|----------|---------------------|
| Linux (Ubuntu/Debian) | `libyuv.so.0` |
| Linux (Arch/CachyOS) | `libyuv.so` |
| Windows (Chromium / NDI) | `yuv.dll` |
| macOS (Homebrew) | `libyuv.dylib` |

> **✅ Decision:** Use a platform-conditional name list in `LibyuvNative.cs`:
>
> ```csharp
> private static readonly string[] _candidateNames =
>     OperatingSystem.IsWindows() ? ["yuv", "libyuv"] :
>     OperatingSystem.IsMacOS()   ? ["libyuv", "libyuv.0"] :
>                                   ["libyuv", "libyuv.so.0"];
> ```
>
> Iterate `NativeLibrary.TryLoad` over `_candidateNames` and take the first hit.
> `_libyuvAvailable` is `true` only when a name resolves **and** the version check passes.

---

#### A.7 `ArrayPool<byte>` alignment and libyuv SIMD performance

libyuv's AVX2 row kernels require source and destination buffers to start at a **32-byte
boundary**.  `ArrayPool<byte>.Shared` provides no alignment guarantee.  At 4K 10-bit,
misaligned buffers cause libyuv to skip AVX2 — a correctness-safe but ~10–20% slower path.

> **✅ Decision:** Rent output conversion buffers via `NativeMemory.AlignedAlloc(size, 32)`
> in `LibyuvConverters.cs`, wrapped in a custom `AlignedMemoryOwner : IMemoryOwner<byte>`
> that calls `NativeMemory.AlignedFree` on disposal.  This ensures all libyuv output
> buffers qualify for AVX2 paths.  Source buffers (FFmpeg-decoded frames) are not under
> our control; libyuv handles misaligned sources gracefully at a smaller penalty.
> Document the expected throughput range in the Phase 2 benchmark notes.

---

#### A.8 BT.2020 colour-space matrix selection for CPU-path converters

libyuv exposes colour-space–aware conversion via `*Matrix`-suffixed functions:

```c
I210ToARGBMatrix(src_y, stride_y, src_u, ..., dst_argb, dst_stride, &kYuv2020Constants, w, h);
```

The available constants are `kYuvI601Constants` (BT.601), `kYuvH709Constants` (BT.709),
`kYuv2020Constants` (BT.2020 limited), and `kYuvV2020Constants` (BT.2020 full).

If a `RecordingVideoSink` or thumbnail extractor uses a CPU-side `I210ToARGB` path for
BT.2020 content, defaulting to `kYuvH709Constants` will produce incorrect colours
(≈ ±10% luminance error in the highlights for PQ content).

> **✅ Decision:** `LibyuvConverters` constructors accept a `ColourSpace colourSpace` parameter
> (from `VideoFrame.ColourSpace`).  A private helper `SelectYuvConstants(ColourSpace)`
> maps the enum to the appropriate `YuvConstants*` constant and passes it to the
> `*Matrix`-suffixed libyuv function.  Defaults to `kYuvH709Constants` if the colour space
> is unknown or unrecognised.

---

### Group B — Pipeline & Architecture

#### B.1 `VideoFrame` lacks per-plane stride information

`VideoFrame.Data` is a single flat `ReadOnlyMemory<byte>`.  For packed single-plane formats
(Bgra32, Uyvy422) this is sufficient, but for the planned native `Yuv422p10` passthrough the
decoder's `AVFrame.linesize[0/1/2]` row strides are **not carried with the frame**.

FFmpeg's software decoder pads plane rows to a multiple of 32 or 64 bytes.  For a 4K
(`3840 × 2160`) `Yuv422p10` frame:

| Plane | Logical stride | FFmpeg-padded stride |
|-------|---------------|---------------------|
| Y     | `3840 × 2 = 7680 B` | `7680 B` (already 64-byte aligned) |
| U / V | `1920 × 2 = 3840 B` | `3840 B` (aligned) |

For non-standard widths (e.g., `3696 × 2112`) the pad is non-trivial.  Without strides, the
GL uploader and libyuv converters cannot determine safe row boundaries.

> **✅ Decision:** Add `Stride0`, `Stride1`, `Stride2` fields to `VideoFrame` (defaulting to 0,
> interpreted as *"use `width × bytesPerSample`"*).  `FFmpegVideoChannel` populates these from
> `AVFrame.linesize[]` in native-passthrough mode; `sws_scale`-produced packed frames leave
> them at 0.  See also B.2 and B.7.

---

#### B.2 `FFmpegVideoChannel` always converts via `sws_scale` — passthrough mode needed

The current `ConvertFrame()` always calls `sws_scale` to produce a packed `TargetPixelFormat`
frame.  The pipeline plan assumes `OpenGLVideoControl` receives native `Yuv422p10` frames for
shader-based conversion — bypassing `sws_scale` is necessary to avoid paying the scale cost.

> **✅ Decision:** Introduce a `TargetPixelFormat.Native` sentinel value (new enum member).
> When set, `FFmpegVideoChannel` skips `sws_scale`, copies plane data from `AVFrame`
> using `linesize`-based strides, and populates `VideoFrame.Stride0/1/2`.  Existing callers
> that rely on packed output are unaffected.

---

#### B.3 `glPixelStorei(GL_UNPACK_ROW_LENGTH)` for padded plane strides

When the stride of a plane passed to `glTexImage2D` is wider than `width × bps`, OpenGL reads
past the intended row boundary, producing a diagonal skew artefact.

> **✅ Decision:** `OpenGLVideoControl.UploadPlane()` will:
> 1. Call `gl.PixelStorei(GL_UNPACK_ROW_LENGTH, stride / bytesPerElement)` before each
>    `glTexImage2D` / `glTexSubImage2D` when `VideoFrame.StrideN > 0`.
> 2. Reset `GL_UNPACK_ROW_LENGTH` to `0` immediately after each upload.
> This is a no-op for packed formats where stride = width × bps (StrideN == 0).

---

#### B.4 OpenGL ES and `GL_R16` / `GL_RG16` availability

§4.3 uses `GL_R16` (0x822A) and `GL_RG16` (0x822C).  These are **OpenGL 3.0 core** (desktop)
internal formats.  On **OpenGL ES 3.x** they require `GL_EXT_texture_norm16`, which is not
present on all drivers.

> **✅ Decision:** At `OnOpenGlInit`, query for the extension string.  If `GL_EXT_texture_norm16`
> is absent:
> - Log a diagnostic: *"GL_EXT_texture_norm16 not available; 10/16-bit formats will be
>   downsampled to 8-bit."*
> - Fall back to `GL_R8` / `GL_RG8` textures for 10/16-bit source formats.
> - Store a `bool _highBitDepthSupported` flag used by the shader selector to switch between
>   the 16-bit and 8-bit shader variants.

---

#### B.5 NDI `NdiFourCCVideoType.Uyva` has no `PixelFormat` counterpart

`NdiFourCCVideoType` in `NDILib/Types.cs` includes `Uyva` (4:2:2 + alpha plane), but the
`PixelFormat` enum extension in §3.1 has no matching entry.  An NDI source sending UYVA frames
would have no `PixelFormat` to carry them through the pipeline.

> **✅ Decision:** Add `Uyva422` to the `PixelFormat` enum in §3.1 (8-bit packed 4:2:2 + alpha,
> same wire layout as `Uyvy422` but with an additional alpha byte per 2 pixels).  If the
> alpha plane is not needed downstream, `NDIVideoChannel` can demote `Uyva` frames to
> `Uyvy422` (stripping alpha) as a configurable option.

---

#### B.6 Pool capacity default may be tight with a recording sink

The `SharedVideoFrameOwner` pool default capacity of **32** (§3.9) was sized for
`sinkCount × maxInFlightFrames`.  With a recording sink:

| Sinks | Encoder in-flight frames | Required pool slots |
|-------|-------------------------|---------------------|
| GL + NDI | 1–2 each | ≤ 6 — comfortable |
| GL + NDI + Recording (x264/HEVC) | 4–8 encode queue | up to 24 — at limit |
| GL + NDI + Recording + Preview | 4–8 each recording | ≥ 32 — exhausts pool |

> **✅ Decision:** Add a `PoolExhausted` event on `VideoRouter`:
> ```csharp
> public event EventHandler<PoolExhaustedEventArgs>? PoolExhausted;
> // Args carry: timestamp, active sink count, current pool capacity
> ```
> Fired (rate-limited, max once per second) when `ObjectPool<SharedVideoFrameOwner>` cannot
> return an instance.  Document the sizing formula `capacity ≥ sinkCount × (encodeQueueDepth + 2)`
> in the `VideoRouter` constructor XML doc.

---

#### B.7 `VideoFrame` plane offset convention for multi-plane formats

For planar formats packed into a single `ReadOnlyMemory<byte>`, consumers need to know where
each plane starts.  Currently this is undocumented and not enforced.

> **✅ Decision (most performant):** Use a **contiguous single-buffer layout** (no per-frame
> array allocation):
> - Plane 0 (Y) starts at offset 0.
> - Plane 1 (U/Cb) starts at offset `Stride0 × Height`.
> - Plane 2 (V/Cr) starts at offset `Stride0 × Height + Stride1 × ChromaHeight`.
> - `ChromaHeight` = `Height` for 4:2:2 / 4:4:4; `Height / 2` for 4:2:0.
>
> This layout is identical to what `av_image_copy_to_buffer` and `sws_scale` produce for
> packed-planar output.  Document the convention in `VideoFrame`'s XML summary.
> `PixelFormatInfo` will expose a `GetPlaneOffset(PixelFormat, int planeIndex, int width,
> int height, int stride)` helper so consumers don't compute offsets manually.

---

### Group C — Open Questions (decided April 2026)

#### C.1 NDI timecode vs `VideoFrame.Pts` — semantic mismatch

§8 maps `VideoFrame.Pts` → `NdiVideoFrameV2.Timecode` (100 ns ticks, matching NDI's unit).
However NDI timecode conventionally carries a **SMPTE drop-frame timecode** (frame number
within the current second, encoded as a `int64_t` of 100 ns ticks from the NDI epoch).
`VideoFrame.Pts` is a **playback position from file start** — these have different origins and
meanings.

Sending raw `Pts` as the NDI timecode means:
- Frame 0 appears at timecode 00:00:00:00 (fine for a file player).
- After a seek the timecode resets, which may confuse NDI receivers that use timecode for
  frame-accurate sync or recording.
- Jitter in `Pts` (from variable-frame-rate sources) propagates to the NDI timecode.

> **✅ Decision:** Expose a `TimecodeMode` property on `NDIVideoSink`:
>
> ```csharp
> public enum NdiTimecodeMode
> {
>     /// <summary>
>     /// Let the NDI runtime synthesize timecode from its own wall clock.
>     /// Recommended default — robust across seeks, VFR sources, and clock switches.
>     /// </summary>
>     Synthesize,
>
>     /// <summary>
>     /// Pass VideoFrame.Pts directly as the NDI timecode (100 ns ticks from file start).
>     /// Suitable when downstream receivers use timecode for frame-accurate sync and
>     /// the source is a non-seeking, constant-frame-rate file.
>     /// </summary>
>     FromPts,
>
>     /// <summary>
>     /// Use the host's UTC wall clock at the moment ReceiveFrame() is called.
>     /// Suitable for live-capture scenarios where wall-clock timecode is required.
>     /// </summary>
>     FromWallClock,
> }
> ```
>
> Default: `NdiTimecodeMode.Synthesize`.

---

#### C.2 GPU frame lifetime inside `SharedVideoFrameOwner`

§5.4 adds `IGpuVideoFrame? GpuFrame` to `VideoFrame`.  `SharedVideoFrameOwner.Inner` holds
one `IDisposable` (the `ArrayPool` rental).  When hardware decode is active, two distinct
resources must be jointly lifetime-managed per frame:
1. CPU-side `ArrayPool<byte>` rental (may be null for GPU-only path)
2. `IGpuVideoFrame` (GPU surface / DMA-BUF / CUDA mapping)

A single `IDisposable Inner` cannot hold both.

> **✅ Decision (zero extra allocation):** `IGpuVideoFrame` itself acts as the `MemoryOwner`
> — it implements `IDisposable` and holds a reference to the CPU `ArrayPool` rental
> (if any).  `VideoFrame.MemoryOwner` is set to the `IGpuVideoFrame` instance.
> `IGpuVideoFrame.Dispose()` releases the GPU surface first, then the CPU rental.
> This avoids any composite-disposable allocation per frame.
>
> For software-decode frames (no GPU surface), `VideoFrame.MemoryOwner` continues to be
> the `ArrayPoolOwner<byte>` directly, as today.  `VideoFrame.GpuFrame` is `null`.

---

#### C.3 Colour range (limited vs full) in GL shaders — distinct from colour space

BT.709 limited range uses Y ∈ [16/255, 235/255]; full range uses Y ∈ [0, 255/255].
ProRes 422 content is typically **full-range** (`AVCOL_RANGE_JPEG` in FFmpeg terms).
HEVC/H.264 from broadcast sources is typically **limited-range**.

The fragment shaders must apply the appropriate offset/scale independently of the colour-space
matrix (BT.601/709/2020) — these are easily conflated.

> **✅ Decision:** Use `vec2 lumaRange` and `vec2 chromaRange` uniforms, each carrying
> `(offset, scale)` for the respective component:
>
> ```glsl
> uniform vec2 uLumaRange;    // e.g. (16.0/255.0, 219.0/255.0) limited; (0.0, 1.0) full
> uniform vec2 uChromaRange;  // e.g. (16.0/255.0, 224.0/255.0) limited; (0.0, 1.0) full
>
> float Y  = (texture(uPlaneY, vTexCoord).r - uLumaRange.x)   / uLumaRange.y;
> float Cb = (texture(uPlaneCb, ...).r       - uChromaRange.x) / uChromaRange.y - 0.5;
> ```
>
> `OpenGLVideoControl` sets these uniforms from `VideoFrame.ColourRange` at each frame
> upload.  This approach handles mid-stream range changes (e.g., mixed SDR/HDR playlists)
> without recompiling shaders.

---

#### C.4 `VideoRouter.AddSink` / `RemoveSink` thread safety

The doc did not specify whether `AddSink`/`RemoveSink`/`NotifyStreamChanged` are safe to
call concurrently with `Dispatch`.

> **✅ Decision (Option C — command queue, matching audio pattern):**
> `AddSink`, `RemoveSink`, `AddChannel`, `RemoveChannel`, and `NotifyStreamChanged` post
> commands to a lock-free `ConcurrentQueue<RouterCommand>` (a discriminated union).
> At the start of each `Dispatch` call, the queue is drained and commands applied before
> the frame-selection loop runs.  This avoids lock contention on the hot `Dispatch` path
> while providing the same thread-safety guarantee as the audio mixer.

---

#### C.5 VSync-driven `AggregateVideoOutput` → repeated frames to NDI

When `OpenGLVideoControl` (VSync at 60 Hz) is the leader and the source is 23.976 fps,
`Dispatch` is called 60 times/second but only ~24 frames are unique.  The NDI sink would
send 60 frames/second with ~60 % duplicates, wasting ~2.5× network bandwidth.

> **✅ Decision:** Expose `NdiSendMode` on `NDIVideoSink`:
>
> ```csharp
> public enum NdiSendMode
> {
>     /// <summary>
>     /// Send every ReceiveFrame() call regardless of duplicate PTS.
>     /// Use when NDI is the sole output (VirtualVideoOutput) and rate matches source.
>     /// </summary>
>     AlwaysSend,
>
>     /// <summary>
>     /// Skip NDISend when frame.Pts == last sent Pts (duplicate from VSync cadence).
>     /// Recommended default — avoids bandwidth waste when used with a VSync leader.
>     /// NDI receivers see the declared frame rate; no repeated frames are sent.
>     /// </summary>
>     DeduplicateByPts,
> }
> ```
>
> Default: `NdiSendMode.DeduplicateByPts` (smoothest for mixed VSync + NDI scenarios).

---

#### C.6 `FFmpegDecoder.Seek` — interface not yet defined

§7.5 uses `videoOut.LargeClockJump += (_, e) => decoder.Seek(e.NewPosition)` but
`FFmpegDecoder` does not yet expose `Seek` on a public interface.

> **✅ Decision:** `LargeClockJumpEventArgs` carries a `SeekAction` delegate rather than
> requiring `VideoRouter` to know about `FFmpegDecoder`:
>
> ```csharp
> public sealed class LargeClockJumpEventArgs : EventArgs
> {
>     public TimeSpan   OldPosition { get; init; }
>     public TimeSpan   NewPosition { get; init; }
>     public TimeSpan   Delta       { get; init; }
>     /// <summary>
>     /// Optional action the application should invoke to seek the audio/video decoder
>     /// to NewPosition.  Null if no seek is required (e.g. clock-source switch only).
>     /// </summary>
>     public Action<TimeSpan>? SeekAction { get; init; }
> }
> ```
>
> The application wires this at startup: `videoOut.LargeClockJump += (_, e) =>
> e.SeekAction?.Invoke(e.NewPosition)`.  `VideoRouter` remains independent of
> `FFmpegDecoder`.

---

### Group D — Open Questions (decided April 2026)

#### D.1 `OpenGLVideoControl` disposed while still registered as a sink

If the Avalonia window is closed (or the control removed from the visual tree) while it is
still registered in a `VideoRouter`, the next `Dispatch` call invokes `ReceiveFrame` on a
disposed `IVideoSink`.  The disposed control posts to `Dispatcher.UIThread` which no longer
has a valid GL context — this could silently drop frames or throw.

> **✅ Decision (Option C):** `IVideoSink` gains a `bool IsDisposed { get; }` property.
> At the start of each sink's iteration in `Dispatch`, the router checks `sink.IsDisposed`
> and auto-removes the sink (posts a remove command to the command queue per C.4 so the
> removal is applied at the next `Dispatch` boundary without corrupting the current
> iteration).  No exceptions are swallowed; sinks are responsible for setting
> `IsDisposed = true` in their `Dispose()` implementation.

---

#### D.2 `SharedVideoFrameOwner.AddRef` after final `Dispose` — use-after-free potential

`SharedVideoFrameOwner` uses `Interlocked.Decrement` in `Dispose` and `Interlocked.Increment`
in `AddRef`.  If a sink calls `AddRef` after the instance has been returned to the pool (ref
count already 0), the count goes 0 → 1 on an instance already reused for another frame.
Similarly a double-`Dispose` decrements to -1 and the instance is never returned to the pool.

> **✅ Decision (least surprises — fail fast):**
> - `AddRef` performs a CAS loop: it only increments if the current count is `> 0`; if the
>   count is already 0, it throws `ObjectDisposedException`.  This surfaces misuse immediately
>   rather than producing a silent use-after-free.
> - `Dispose` uses a CAS loop to atomically decrement only if count is `> 0`; if count is
>   already 0, the call is a no-op (idempotent double-dispose is allowed, matching standard
>   .NET `IDisposable` convention).  The pool return happens only on the single 1→0 transition.

---

#### D.3 Shader compilation failure — graceful degradation strategy

`OnOpenGlInit` compiles all GLSL shaders for the format set.  A shader compilation failure
(driver bug, unsupported GLSL version, missing extension) would currently propagate as an
uncaught exception, crashing the Avalonia control.

> **✅ Decision (Option B + log):** On per-shader compilation failure:
> 1. Log the full GLSL info log at `Error` level, including the failing shader source.
> 2. Mark that shader variant as unavailable and fall back to a CPU-side `FFmpegSwsConverter`
>    (Bgra32) for that pixel format, rendering via the always-compilable `passthrough.frag`.
> 3. Fire `ShaderCompilationFailed` on `OpenGLVideoControl` so the application can surface
>    a UI warning.
> Video remains visible at reduced quality (8-bit Bgra32) rather than showing a blank control.

---

#### D.4 `VideoRouter` single-source constraint until Phase 4 compositing

§3.4 defines `AddChannel` accepting multiple channels but §13.5 states `VideoRouter` remains
single-source until Phase 4.  The method is public with no guard.

> **✅ Decision:** `VideoRouter.AddChannel` throws `InvalidOperationException` if a second
> channel is added while the router is in single-source mode (Phase 1–3).  The exception
> message references the Phase 4 roadmap item.  A `bool MultiSourceEnabled` constructor
> parameter (default `false`) opts into multi-source behaviour for future Phase 4 use.

---

#### D.5 Native-passthrough ring buffer memory footprint at 4K

Decision B.2 introduces native-passthrough mode.  Each uncompressed `Yuv422p10` 4K frame
is ~49.8 MB; the default `bufferDepth = 4` would hold **~200 MB** in the ring.

> **✅ Decision (most performant):** Expose a separate `NativeBufferDepth` constructor
> parameter on `FFmpegVideoChannel`, defaulting to **2**.
>
> - Depth 2 = ~100 MB ring; sufficient for one decode-ahead frame at 60 fps (decode finishes
>   before the display thread consumes the previous frame under normal load).
> - The converted (sws_scale) path retains `bufferDepth = 4` unchanged.
> - XML doc on the constructor documents the per-frame size formula:
>   `Y + U + V planes × 2 bytes × resolution`, so callers can calculate the footprint
>   for non-4K resolutions.

---

#### D.6 `PixelFormat.Native` sentinel propagation through `PixelFormatInfo`

Decision B.2 adds `PixelFormat.Native` as a configuration-time sentinel.  `PixelFormatInfo`
helpers for plane count, stride, and NDI-sendable flag are undefined for `Native`.

> **✅ Decision (least surprises):** `PixelFormat.Native` is resolved to the concrete
> `AVPixelFormat`-mapped `PixelFormat` value **inside `FFmpegVideoChannel`** before the
> `VideoFrame` is constructed and enqueued.  `Native` therefore never appears in a live
> `VideoFrame.PixelFormat`.  `PixelFormatInfo` does not handle `Native` — any call with
> `Native` as input is a programming error and throws `ArgumentOutOfRangeException`.
> This keeps the sentinel purely at the configuration layer.

---

### Group E — Open Questions (decided April 2026)

#### E.1 `VideoFrame` struct size growth — copy cost in ring buffer

`VideoFrame` is a `readonly record struct`.  Adding the planned fields from §4.5 (B.1 strides,
HDR metadata, `GpuFrame`) brings the struct to approximately:

| Field | Size |
|-------|------|
| `Width`, `Height` | 8 B |
| `PixelFormat` | 4 B |
| `Data` (`ReadOnlyMemory<byte>`) | 16 B |
| `Pts` (`TimeSpan`) | 8 B |
| `Stride0/1/2` | 12 B |
| `ColourSpace`, `ColourRange`, `Transfer`, `Primaries` (enums) | 16 B |
| `MasteringDisplay?` (`HdrMasteringDisplay?` — 10 floats + nullable flag) | 44 B |
| `ContentLight?` (`HdrContentLight?` — 2 ushorts + nullable flag) | 8 B |
| `GpuFrame?` (reference) | 8 B |
| `MemoryOwner?` (reference) | 8 B |
| **Total** | **~132 B** |

`System.Threading.Channels.Channel<VideoFrame>` stores values by copy — each `WriteAsync`/
`TryRead` copies the full struct.  At 60 fps this is ~7920 B/s of struct copying, which is
negligible on its own, but the struct exceeds the 16-byte threshold where the JIT prefers
reference semantics and avoids stack spilling.

**Open question:** Should `VideoFrame` be a class (reference type) rather than a struct,
using the existing `MemoryOwner` pattern for lifetime management?  Or should the nullable
HDR fields (`MasteringDisplay?`, `ContentLight?`) be boxed into a separate `HdrMetadata`
class reference (null when SDR), keeping the core struct compact (≤ 80 B)?

> **✅ Decision (most efficient, avoids per-frame allocation):** Move the nullable HDR
> fields into a sealed immutable `HdrMetadata` **class** reference (null when SDR):
>
> ```csharp
> public sealed class HdrMetadata
> {
>     public HdrMasteringDisplay MasteringDisplay { get; init; }
>     public HdrContentLight     ContentLight     { get; init; }
> }
> ```
>
> `VideoFrame` carries `HdrMetadata? Hdr` (8 B reference) instead of the 52 B inline
> nullable structs, reducing the struct to **~88 B**.  Since HDR metadata is per-stream,
> the same `HdrMetadata` instance is shared across all frames from the same stream —
> allocated once at stream open and reused by reference.  No per-frame allocation occurs.
> `HdrMetadata` is immutable (`init` setters only) so concurrent reads are safe without
> locking.  See also F.5 for the case where HDR metadata changes mid-stream.

---

#### E.2 `P210` and `Yuv444p10` missing from the GL texture layout table (§4.3)

The texture layout table in §4.3 lists formats planned for Phase 1–2 but omits two entries
from the §3.1 `PixelFormat` enum that will reach the GL path:

| Missing format | Expected in pipeline |
|----------------|---------------------|
| `P210` | VA-API ProRes output on some drivers (§5.2); also VAAPI HEVC 4:2:2 |
| `Yuv444p10` | ProRes 4444 / HEVC 4:4:4 (software decode) |

`P210` layout is: Y plane (`GL_R16`, full width) + interleaved UV plane (`GL_RG16`, full
width, half height — same as P010 but with 4:2:2 chroma).
`Yuv444p10` layout is: 3 planes × `GL_R16`, all at full width × full height.

**Open question:** Should these be added to §4.3 now with corresponding shader stubs added to
the Phase 2 shader set, or deferred to Phase 3 when VA-API hardware paths are implemented?

> **✅ Decision:** Add both formats to §4.3 now and include shader stubs in the Phase 2
> shader set.  Updated table rows:
>
> | Format | Texture planes | GL internal format | Sampler type |
> |--------|---------------|--------------------|--------------|
> | `P210` | 2 (Y + UV) | `GL_R16` + `GL_RG16` | `sampler2D` × 2 |
> | `Yuv444p10` | 3 (Y + U + V) | `GL_R16` × 3 | `sampler2D` × 3 |
>
> Shader files to add:
> - `shaders/p210_to_rgb.frag` — same matrix as `yuv422p10_to_rgb.frag`; chroma UV plane
>   is full height (4:2:2), not half (unlike P010).
> - `shaders/yuv444p10_to_rgb.frag` — 3 × `GL_R16` full-resolution planes; same
>   BT.601/709/2020 colour-space matrix switch as the other shaders.

---

#### E.3 `OnStreamChanged` — does `NotifyStreamChanged` trigger converter re-negotiation?

`VideoRouter.NotifyStreamChanged(channelId, newFormat)` notifies sinks of a source format
change via `IVideoSink.OnStreamChanged(newFormat)`.  However, the routing table entry for
each sink (containing the negotiated `PixelFormat` and `IVideoConverter?`) is established at
`AddSink` time and is not updated.

If the new format has a different `PixelFormat` (e.g., source switches from `Yuv422p10` to
`Nv12` mid-stream), the previously negotiated converter is now wrong:
- A sink negotiated for `Yuv422p10 → P216` now receives `Nv12` frames but its converter
  still expects `Yuv422p10` input — `sws_scale` will produce garbage or crash.

**Open question:** Should `NotifyStreamChanged` automatically re-run format negotiation
(as if `AddSink` were called again with the new source format) for each registered sink?
Or should it be the sink's responsibility to handle format changes internally via
`OnStreamChanged`, with the converter unchanged?

> **✅ Decision (safest — automatic re-negotiation):**
> `NotifyStreamChanged` re-runs `VideoConverterFactory.Create` for each registered sink
> using the new source `PixelFormat` and the sink's `PreferredFormats` list.  The new
> converter is posted as a command queue entry (per C.4) and applied atomically at the
> next `Dispatch` boundary — no race with an in-progress frame dispatch.
> If no converter is found for the new format, `FFmpegSwsConverter` is used as the
> universal fallback and a warning is logged — the sink always receives valid frames.
> `IVideoSink.OnStreamChanged(newFormat)` is called **after** the converter is replaced.

---

#### E.4 `FFmpegVideoChannel.GetSws` does not validate width/height — mid-stream resize bug

The current `GetSws` implementation:

```csharp
private SwsContext* GetSws(int w, int h, AVPixelFormat srcFmt)
{
    if (_sws != null) return _sws;   // ← keyed only on first call's srcFmt
    ...
}
```

The context is cached on the first call.  If the source stream changes resolution mid-stream
(common in adaptive streaming, some broadcast streams, and files with resolution changes at
chapter boundaries), subsequent frames use a `SwsContext` sized for the **original**
resolution — producing a corrupted or wrong-size output frame, or a segfault.

**Open question:** Should `GetSws` be refactored to re-create the context when `w`, `h`, or
`srcFmt` differ from the cached values?  This is straightforward but introduces a per-frame
width/height check in the decode hot path.  Alternatively, should `FFmpegVideoChannel` fire
`OnStreamChanged` when `frame->width` or `frame->height` differ from `Format.Width/Height`,
letting the router handle re-negotiation?

> **✅ Decision (least surprises — correct output always):** Refactor `GetSws` to cache
> `(_cachedW, _cachedH, _cachedSrcFmt)` and re-create the `SwsContext` on any change:
>
> ```csharp
> private SwsContext* GetSws(int w, int h, AVPixelFormat srcFmt)
> {
>     if (_sws != null && w == _cachedW && h == _cachedH && srcFmt == _cachedSrcFmt)
>         return _sws;
>     if (_sws != null) ffmpeg.sws_freeContext(_sws);
>     // recreate context, update cached values ...
>     _cachedW = w; _cachedH = h; _cachedSrcFmt = srcFmt;
>     return _sws;
> }
> ```
>
> When `w` or `h` differ from `Format.Width/Height`, `FFmpegVideoChannel` also updates
> `Format` and invokes the `NotifyStreamChanged` callback (injected at construction) so
> the router re-negotiates converters per E.3.  End users see a brief reconfiguration
> rather than corrupted or segfaulting frames.

---

#### E.5 Audio/video seek thread-safety — `LargeClockJump` fires from video dispatch thread

The `LargeClockJump` event (§7.5) fires from the video dispatch thread when a clock jump
is detected.  The event handler (per C.6) invokes `e.SeekAction?.Invoke(e.NewPosition)`.

If the `SeekAction` calls into `FFmpegDecoder.Seek`, which flushes audio packet queues and
resets `PortAudioSink`'s audio buffer — all while the PortAudio hardware callback thread
may be reading from those same queues — there is a potential data race unless the audio
pipeline's seek is internally synchronised.

**Open question:** Should `SeekAction` be guaranteed to be called on a specific thread
(e.g., always the UI thread via `Dispatcher.UIThread.InvokeAsync`), or is it the caller's
responsibility to marshal the seek to a thread-safe context?  If the application passes an
unsafe `SeekAction`, a diagnostic `Debug.Assert` on the caller's thread context would help
surface the issue early.

> **✅ Decision (safest for the end user):**
> 1. `LargeClockJumpEventArgs` XML documents clearly: *"SeekAction is invoked on the
>    video dispatch thread.  Implementations touching audio pipeline state must be
>    thread-safe."*
> 2. `VideoRouter` provides a static helper that off-loads seek to a thread-pool thread,
>    preventing the caller from accidentally blocking the dispatch loop:
>    ```csharp
>    /// <summary>
>    /// Wraps <paramref name="seek"/> so it executes on a ThreadPool thread,
>    /// keeping the video dispatch thread unblocked during seek.
>    /// </summary>
>    public static Action<TimeSpan> CreateThreadSafeSeekAction(Action<TimeSpan> seek) =>
>        pos => Task.Run(() => seek(pos));
>    ```
> 3. The wiring example in §7.5 is updated to show this helper as the recommended pattern.

---

### Group F — Open Questions (decided April 2026)

#### F.1 `Preflight()` call site and timing

§3.8 defines `Preflight()` as upgrading `CadenceMode` from `NearestFrame` to
`TelecineAdaptive` when a source/display fps mismatch is detected.  The doc did not
specify who calls it or when.

> **✅ Decision:** `Preflight()` is called **automatically** by `VideoRouter` at the
> start of `StartAsync()`, using the display refresh rate supplied by the `IVideoOutput`
> leader (passed to `VideoRouter` at construction or via a `SetDisplayRate(double hz)`
> call).  It operates purely on `VideoFormat.FrameRate` values already registered — no
> frame reads required.  If `AddChannel` is called after `StartAsync`, `Preflight` is
> re-evaluated automatically within the C.4 command queue at the next `Dispatch`.
> Explicit user assignment of `CadenceMode` before `StartAsync` always overrides
> auto-upgrade (as per §3.8).

---

#### F.2 `OpenGLVideoControl.OnOpenGlDeinit` — pending frame resource leak

`OnOpenGlRender` defers disposal of the previous frame's `MemoryOwner` to avoid blocking
VSync (step 5 of §4.2).  When the GL context is destroyed via `OnOpenGlDeinit`, any
`SharedVideoFrameOwner` reference in `_pendingFrame` or a deferred-disposal slot is
never disposed — leaking `ArrayPool<byte>` rentals and GPU surfaces.

> **✅ Decision:** Both `OnOpenGlDeinit` and `OpenGLVideoControl.Dispose` explicitly
> drain and dispose all pending frames:
> - `_pendingFrame`: CAS-swap to `null`, dispose the swapped-out owner.
> - Deferred-disposal queue (if any): drain and dispose all queued owners.
> `OnOpenGlDeinit` runs first (GL context still valid — GPU surface release needs the
> context); `Dispose` performs the same drain as a safety net if deinit was skipped.

---

#### F.3 `FFmpegVideoChannel` ring `BoundedChannelFullMode.Wait` — audio decode stall risk

The video ring uses `BoundedChannelFullMode.Wait`.  If audio and video share a decode
thread and the video ring fills (GPU stall, seek), audio decoding stalls too.

> **✅ Decision (safest — preserve frame accuracy, enforce thread independence):**
> Keep `BoundedChannelFullMode.Wait` for video (no dropped frames under normal load).
> Document as a hard architectural requirement: `FFmpegDecoder` **must** run each
> `IMediaChannel.DecodeLoop` on its own independent thread — audio and video channels
> never share a decode thread.  Add a `Debug.Assert` in `FFmpegDecoder` that each
> channel's decode thread is distinct.  If a future refactor accidentally collapses
> threads, the assert fires in debug builds before any audio dropout reaches the user.

---

#### F.4 Hardware decode + NDI sink — mandatory CPU transfer undocumented

When hardware decode is active and `NDIVideoSink` is registered, a GPU→CPU transfer
(~49.8 MB/frame × 60 fps ≈ ~3 GB/s PCI-e) is required before `NDISendVideo`.

> **✅ Decision (least surprises — transparent to the end user):**
> Add `bool SupportsGpuFrames { get; }` to `IVideoSink` (default `false`).
> `VideoRouter.Dispatch` checks this flag: if a sink returns `false` and the dispatched
> frame has `GpuFrame != null`, the router performs `av_hwframe_transfer_data` to a
> rented CPU buffer **before** calling `sink.ReceiveFrame`, then disposes the CPU buffer
> via the normal `SharedVideoFrameOwner` ref-counting path.
> `OpenGLVideoControl` returns `true` (it binds GPU frames directly).  `NDIVideoSink`
> returns `false`.  The end user never needs to know whether hardware decode is active —
> each sink receives frames in the format it can handle.

---

#### F.5 `HdrMetadata` instance sharing — mid-stream HDR metadata change

Decision E.1 shares one `HdrMetadata` instance across all frames from a stream; HDR
metadata can change mid-stream and queued frames would then carry stale metadata.

> **✅ Decision (most performant — lazy allocation on change only):**
> `FFmpegVideoChannel` compares the raw bytes of `AVFrame` side-data
> (`AV_FRAME_DATA_MASTERING_DISPLAY_METADATA`, `AV_FRAME_DATA_CONTENT_LIGHT_LEVEL`)
> against the cached previous values on each decoded frame.  If they differ, a new
> `HdrMetadata` is allocated and the cached reference updated; all subsequent frames
> share the new instance.  If unchanged (the common case for every frame in a normal
> stream), no allocation occurs.  Frames already queued in the ring carry the instance
> that was current at their decode time — per-frame correctness is maintained at zero
> cost for constant-metadata streams.

---

### Group G — Open Questions (decided April 2026)

#### G.1 `VideoFormat` missing colour space and HDR info for `IVideoSink.Configure`

`IVideoSink.Configure(VideoFormat format)` is called before the first frame to allow sinks
to pre-declare their output parameters (e.g., NDI frame rate, buffer allocation).
`VideoFormat` currently carries only `Width`, `Height`, `PixelFormat`, and `FrameRate`.

Sinks need colour-space metadata at configure time:
- `NDIVideoSink` must declare HDR metadata to NDI receivers in the initial frame.
- `RecordingVideoSink` must write colour-space tags into the container header.
- `OpenGLVideoControl` should pre-select the correct shader variant.

> **✅ Decision:** Extend `VideoFormat` with the same colour-space set as `VideoFrame`
> (populated from codec-level stream parameters, which are more stable than per-frame values):
>
> ```csharp
> public readonly record struct VideoFormat(
>     int            Width,
>     int            Height,
>     PixelFormat    PixelFormat,
>     int            FrameRateNumerator,
>     int            FrameRateDenominator,
>     ColourSpace    ColourSpace    = ColourSpace.Bt709,
>     ColourRange    ColourRange    = ColourRange.Limited,
>     ColourTransfer Transfer       = ColourTransfer.Bt709,
>     ColourPrimaries Primaries     = ColourPrimaries.Bt709,
>     HdrMetadata?   Hdr            = null);
> ```
>
> `FFmpegVideoChannel` populates from `AVCodecParameters.color_space/range/trc/primaries`
> (stream-level) at open time and updates via `NotifyStreamChanged` if they change.
> The existing `ToString()` override is updated accordingly.

---

#### G.2 `PixelFormatInfo.GetPlaneOffset` API for semi-planar formats

Decision B.7 specifies `GetPlaneOffset` indexed by plane.  For semi-planar formats there
are only 2 memory regions (Y + interleaved UV), yet the logical plane count is 3 (Y/U/V).

> **✅ Decision (most efficient, least surprises):** `PixelFormatInfo` exposes
> `int MemoryPlaneCount` reflecting distinct byte regions (2 for semi-planar, 3 for
> fully-planar, 1 for packed).  `GetPlaneOffset` is indexed by **memory plane**:
>
> ```csharp
> // planeIndex ∈ [0, MemoryPlaneCount)  — throws ArgumentOutOfRangeException otherwise
> static int GetPlaneOffset(PixelFormat fmt, int planeIndex, int height, int stride0,
>                           int stride1 = 0);
> ```
>
> - Plane 0 always starts at offset 0.
> - Plane 1 (UV for semi-planar, U for planar) starts at `stride0 × height`.
> - Plane 2 (V for fully-planar) starts at `stride0 × height + stride1 × chromaHeight`.
>
> An additional `bool IsInterleaved(PixelFormat, int memoryPlane)` helper signals that a
> memory plane contains interleaved components (U+V), so callers who need to address U/V
> separately within the UV plane can compute sub-strides themselves.  This keeps the API
> surface minimal while covering all format families without surprises.

---

#### G.3 `NDIVideoSink.OnStreamChanged` — avoid unnecessary sender restart

§13.6 specifies `OnStreamChanged` always restarts the NDI sender, causing brief receiver
disconnects.  However not all format changes need a restart:

| Change | Restart required? |
|--------|------------------|
| `PixelFormat` only (same resolution + frame rate) | No — NDI allows changing `FourCC` between frames |
| Resolution change | Yes |
| Frame rate change | Yes |

> **✅ Decision:** `NDIVideoSink.OnStreamChanged` compares the new `VideoFormat` against
> the currently active format:
> - If only `PixelFormat` changed: update the internal `_fourCC` field; the next
>   `ReceiveFrame` call uses the new FourCC without restarting.  Fires `SenderFourCCChanged`
>   diagnostic event (no receiver interruption).
> - If `Width`, `Height`, or `FrameRate` changed: restart the NDI sender as before and
>   fire `SenderRestarted` (per §13.6).

---

#### G.4 `AlignedMemoryOwner` pooling — native memory leak risk

Decision A.7 allocates conversion output buffers via `NativeMemory.AlignedAlloc`.
At 4K/60, each conversion call uses ~50–100 MB.  If `Dispose` is delayed (sink holds a
frame), native memory accumulates outside GC pressure tracking.

> **✅ Decision (safest, minimal allocations — hot path efficiency):**
> Each `LibyuvConverter` instance owns a small **internal pool of 2 pre-allocated aligned
> buffers** (sized for the negotiated output format at construction time).  The pool uses a
> `ConcurrentQueue<AlignedBuffer>`:
> - `Convert()` dequeues a buffer; if the queue is empty (both buffers in use by sinks
>   that called `AddRef`), allocates a fresh one (rare path, safe).
> - `AlignedMemoryOwner.Dispose()` returns the buffer to the converter's queue.
>
> This caps native memory at `2 × frameSize` per converter instance under normal load, with
> a bounded overflow path for slow sinks.  No per-frame syscall in the common case.
> Buffer size is computed at construction from `PixelFormatInfo` + negotiated resolution.

---

#### G.5 `ObjectPool<SharedVideoFrameOwner>` exhaustion — detection for `PoolExhausted` event

`DefaultObjectPool<T>` silently allocates new instances when the pool is empty and silently
drops on return when full — no exhaustion signal exists.

> **✅ Decision (wrapping — safest, good for diagnostics):**
> `VideoRouter` wraps the pool with a thin live-count tracker:
>
> ```csharp
> private int _liveOwnerCount;
>
> private SharedVideoFrameOwner Rent()
> {
>     var owner = _pool.Get();
>     if (Interlocked.Increment(ref _liveOwnerCount) > _ownerPoolCapacity)
>         RaisePoolExhausted();   // rate-limited, max once/second
>     return owner;
> }
>
> internal void Return(SharedVideoFrameOwner owner)
> {
>     Interlocked.Decrement(ref _liveOwnerCount);
>     _pool.Return(owner);
> }
> ```
>
> `SharedVideoFrameOwner` remains unaware of the pool capacity; encapsulation is
> preserved.  `PoolExhaustedEventArgs` carries `LiveCount`, `Capacity`, and `Timestamp`
> for diagnostics.

---

### Group H — Decided

#### H.1 `IVideoSink.SupportsGpuFrames` — default interface implementation needed

Decision F.4 adds `bool SupportsGpuFrames { get; }` to `IVideoSink`.  Since `IVideoSink`
is an interface, this is a **breaking change** for any existing or third-party
implementations that don't implement the new property.

**Open question:** Should `SupportsGpuFrames` be added as a **default interface method**
(`bool SupportsGpuFrames => false;`) so existing implementations automatically return
`false` without modification?  Default interface members are supported from C# 8 / .NET
Core 3.0 onward.  The alternative — adding it as an abstract member — would require all
existing `IVideoSink` implementations to be updated.

> ✅ **Decision (H.1):** Use a default interface member: `bool SupportsGpuFrames => false;`.
> The project targets .NET 10, so default interface members are fully supported.  Existing
> implementations receive the safe default automatically; sinks that support GPU frames
> override it.

---

#### H.2 `<<6` bit-shift for GL upload must not mutate the shared rental buffer

Decisions A.1 and A.2 require left-shifting `Yuv422p10` samples by 6 before
`glTexImage2D`.  The plane data lives in an `ArrayPool<byte>` rental held by a
`SharedVideoFrameOwner` — the same rental may simultaneously be referenced by `NDIVideoSink`
via `AddRef`.

Mutating the buffer in-place would corrupt the NDI sink's view of the data.

**Open question:** Should the GL upload path always copy plane data into a transient
**upload-only buffer** (shifted in the process), keeping the original rental intact?
Or should the shift be applied lazily inside the GLSL shader (normalise by `1023.0`
instead of `65535.0`), eliminating the need for a CPU-side buffer entirely?
The shader approach (no CPU buffer, no copy) is faster on the hot path but requires two
normalisation constants in the shader (one for 10-bit sources, one for 16-bit).

> ✅ **Decision (H.2):** Apply the bit-shift inside the **GLSL shader** (normalise 10-bit
> samples by `1023.0` and 16-bit samples by `65535.0`).  This eliminates the need for a
> CPU-side upload buffer entirely, keeping the hot path allocation-free.  The shader must
> carry a `uint uBitDepth` or `float uNormScale` uniform that the GL sink sets per-frame
> based on `VideoFrame.PixelFormat`.

---

#### H.3 `RouterCommand` discriminated union — type design

Decision C.4 uses a `ConcurrentQueue<RouterCommand>` for thread-safe router mutations.
The exact shape of `RouterCommand` has not been defined.  It must cover at minimum:

| Command | Payload |
|---------|---------|
| `AddSink` | `IVideoSink`, `IVideoConverter?`, `SinkDropPolicy` |
| `RemoveSink` | `IVideoSink` |
| `AutoRemoveSink` (D.1 — IsDisposed) | `IVideoSink` |
| `AddChannel` | `IMediaChannel<VideoFrame>` |
| `RemoveChannel` | `Guid` |
| `NotifyStreamChanged` | `Guid channelId`, `VideoFormat newFormat` |
| `UpdateConverter` (E.3 re-negotiation) | sink reference, new `IVideoConverter?` |

**Open question:** Should `RouterCommand` be a sealed class hierarchy
(`abstract RouterCommand` + concrete subclasses), a `readonly record struct` union, or a
C# discriminated union pattern using `interface` + pattern matching?  The sealed class
hierarchy is the most idiomatic and GC-friendly for small, infrequent objects; the struct
union avoids heap allocation per command but requires padding to the largest payload size.

> ✅ **Decision (H.3):** Use a **sealed class hierarchy**: `abstract record RouterCommand`
> with one `sealed record` subclass per command variant (e.g. `AddSinkCommand`,
> `RemoveSinkCommand`, …).  `record` types give value-equality and `ToString()` for free,
> making debugging easy.  Commands are rare control-plane events so per-command heap
> allocation is negligible; the sealed hierarchy is the most idiomatic C# pattern and
> causes the fewest surprises.

---

#### H.4 `FFmpegVideoChannel` + `VideoRouter` callback wiring — chicken-and-egg at registration

Decisions E.3 and E.4 require `FFmpegVideoChannel` to call back into `VideoRouter` when a
resolution or format change is detected (`NotifyStreamChanged`).  However:

1. `FFmpegVideoChannel` is created by `FFmpegDecoder` before any router exists.
2. `VideoRouter.AddChannel(channel)` registers the channel after the router is created.

There is a timing gap: the channel cannot hold a `VideoRouter` reference at construction,
and the router cannot inject a callback into the channel at `AddChannel` time without
breaking the `IMediaChannel<VideoFrame>` interface (which has no `SetRouter` method).

**Open question:** What is the cleanest injection mechanism?
- **Option A:** `IMediaChannel<VideoFrame>` gains `Action<VideoFormat>? OnFormatChanged`
  (settable property); `VideoRouter.AddChannel` sets it.
- **Option B:** `FFmpegVideoChannel` exposes a `FormatChanged` event; `VideoRouter`
  subscribes at `AddChannel` time and unsubscribes at `RemoveChannel`.
- **Option C:** `VideoRouter` polls `FFmpegVideoChannel.Format` on each `Dispatch` call
  and detects changes without any callback — no interface change required.
Option C is the simplest (zero interface changes, hot-path check is a single struct
compare) and matches the principle of keeping hot paths fast.

> ✅ **Decision (H.4):** **Option C** — `VideoRouter` stores the last-seen `VideoFormat`
> per channel and compares it on every `Dispatch` call.  A format change enqueues a
> `NotifyStreamChangedCommand` internally so the router applies it at the start of the
> next dispatch cycle.  No interface changes required; the struct comparison is a single
> `==` on a small `readonly record struct` and is effectively free on the hot path.

---

#### H.5 `VideoRouter.SetDisplayRate` — source of the VSync refresh rate under Avalonia

Decision F.1 requires `VideoRouter` to know the display refresh rate for `Preflight()`
cadence mode selection.  The source of this rate has not been specified.

Avalonia's `OpenGlControlBase` does not directly expose the monitor refresh rate.
Possible sources:
- `Screen.PixelDensity` + platform APIs (varies by OS, not a reliable refresh rate source)
- The measured inter-frame interval in `OnOpenGlRender` (accurate after the first few
  frames, but not available before `Preflight` runs at `StartAsync`)
- A constructor/property on `IVideoOutput` where the application specifies the rate
  explicitly (simplest, most reliable for known displays)

**Open question:** Should `IVideoOutput` expose `double DisplayRefreshRate { get; set; }`
(application-specified, defaulting to 60.0 Hz) rather than attempting to auto-detect it?
Auto-detection could be added later as an optional enhancement if the rate proves to matter
significantly for telecine decisions in practice.

> ✅ **Decision (H.5):** `IVideoOutput` exposes `double DisplayRefreshRate { get; set; }`
> defaulting to **60.0 Hz**.  The application sets this explicitly for known displays.
> Auto-detection via measured inter-frame intervals can be added later as an opt-in
> enhancement without breaking the interface (a `TryMeasureRefreshRate()` helper method).

---

### Group I — Decided

#### I.1 `IVideoConverter` — interface shape

Decisions E.3 and E.4 require `VideoRouter` to negotiate and apply an `IVideoConverter`
per sink.  The interface itself has not been defined.

Candidates for the conversion call signature:

- **Option A:** `VideoFrame Convert(in VideoFrame source)` — returns a new frame (may
  return the same frame if no conversion needed).
- **Option B:** `bool TryConvert(in VideoFrame source, out VideoFrame result)` — explicit
  success/failure, but callers must handle the `false` case everywhere.
- **Option C:** `ValueTask<VideoFrame> ConvertAsync(VideoFrame source, CancellationToken ct)`
  — supports GPU-accelerated converters that need async dispatch; overhead on CPU-only
  converters.

The converter must also expose the formats it can accept and produce, so `VideoRouter`
can select the right converter at `Preflight` / re-negotiation time:

```csharp
IReadOnlyList<PixelFormat> SupportedInputFormats  { get; }
IReadOnlyList<PixelFormat> SupportedOutputFormats { get; }
```

**Open question:** Which call signature should `IVideoConverter.Convert` use?  Is a
synchronous `VideoFrame Convert(in VideoFrame)` sufficient for all planned converters
(libyuv, `sws_scale`), or is `ValueTask` needed to support future GPU converters?

> ✅ **Decision (I.1):** Synchronous `VideoFrame Convert(in VideoFrame source)`.  All
> planned converters (libyuv, `sws_scale`) are CPU-bound and complete synchronously.  GPU
> converters can be added in a future phase with a separate `IAsyncVideoConverter`
> interface without changing the existing API.  The capability-query properties
> `SupportedInputFormats` and `SupportedOutputFormats` are confirmed as part of
> `IVideoConverter`.

---

#### I.2 `VideoFrame` struct size

`VideoFrame` is a `readonly record struct`.  With all planned additions it will carry:

| Field | Size |
|-------|------|
| `Width`, `Height` (int × 2) | 8 B |
| `PixelFormat` (enum/int) | 4 B |
| `Stride0`, `Stride1`, `Stride2` (int × 3) | 12 B |
| `ColourSpace`, `ColourRange`, `Transfer`, `Primaries` (enum/int × 4) | 16 B |
| `Data` (`ReadOnlyMemory<byte>`) | 16 B |
| `Pts` (`TimeSpan`) | 8 B |
| `MemoryOwner?` (ref) | 8 B |
| `HdrMetadata?` (ref) | 8 B |
| `GpuFrame?` (ref) | 8 B |
| **Total** | **~88 B** |

Passed `in` (by-reference), this cost is one pointer per call.  However, the struct is
also stored by value inside `BoundedChannel<VideoFrame>` (the FFmpeg ring buffer) and
inside any `List<VideoFrame>` or array.

**Open question:** Is an 88-byte struct acceptable for the ring buffer / collection
storage scenarios, or should `VideoFrame` be converted to a **sealed class** (16-byte
reference everywhere, but one heap allocation per frame)?  For the hot path the class
approach allocates on every decode; the struct approach copies 88 bytes on enqueue.

> ✅ **Decision (I.2):** Keep `VideoFrame` as a `readonly record struct`.  An 88-byte
> copy on `BoundedChannel<VideoFrame>` enqueue is ~2–3 cache-line writes — much cheaper
> than a heap allocation and GC pressure per decoded frame.  Everywhere else the struct
> is passed `in` (by-reference), costing one pointer.  No per-frame heap allocations.

---

#### I.3 `VideoRouter.Dispatch()` — sink exception isolation

`VideoRouter.Dispatch()` iterates all registered sinks and calls `sink.ReceiveFrame(in frame)`.
If a sink throws an unhandled exception the behaviour has not been defined.

Options:
- **Option A:** Let the exception propagate — crashes or terminates the dispatch loop.
- **Option B:** Catch per-sink, log the exception, mark the sink as faulted, remove it
  on the next command drain, continue dispatching to remaining sinks.
- **Option C:** Catch per-sink, log, skip for this frame but leave the sink registered
  (recoverable transient errors).

**Open question:** Should the router isolate sinks from each other (catch per-sink and
continue), or treat any sink exception as fatal to the dispatch loop?  If isolating, should
a faulted sink be auto-removed (Option B) or left in place for recovery (Option C)?

> ✅ **Decision (I.3):** **Option B** — catch per-sink, log the exception with sink
> identity, enqueue an `AutoRemoveSinkCommand`, and continue dispatching to remaining
> sinks.  A faulted sink is auto-removed on the next command drain.  This is the safest
> option: one broken sink cannot stall or crash delivery to all other sinks, and the
> auto-removal prevents the faulted sink from being called on every subsequent frame.

---

#### I.4 Colour metadata enum types

`VideoFormat` will carry `ColourSpace`, `ColourRange`, `Transfer`, and `Primaries` fields
(decided G.1).  The concrete .NET types for these fields have not been specified.

Options:
- **Option A:** Define project-local enums (`enum ColourSpace`, `enum ColourPrimaries`,
  …) with values that mirror the ITU / ISO standards.  A helper maps to/from
  `AVColorSpace`/`AVColorTransferCharacteristic`/`AVColorPrimaries`/`AVColorRange`.
- **Option B:** Use the FFmpeg enum values directly (expose `AVColorSpace` etc. from
  `S.Media.Core` or a shared types assembly).  No mapping needed, but ties the public API
  to FFmpeg's numbering scheme.
- **Option C:** Use `int` (raw) for each field.  Minimal coupling, but loses type safety
  and discoverability.

**Open question:** Should colour metadata use project-local enums (Option A, cleanest
public API with an FFmpeg mapping helper), FFmpeg enum values directly (Option B, zero
mapping cost), or raw `int` (Option C)?

> ✅ **Decision (I.4):** **Project-local enums whose underlying integer values are
> deliberately identical to the corresponding FFmpeg / ISO-IEC enums** (`AVColorSpace`,
> `AVColorRange`, `AVColorTransferCharacteristic`, `AVColorPrimaries`).  Because the
> numeric values match, `FFmpegVideoChannel` can cast directly (e.g.
> `(ColourSpace)avFrame->colorspace`) with zero mapping cost.  `S.Media.Core` stays free
> of any FFmpeg dependency, and the public API retains full type safety.

---

#### I.5 `VideoRouter.Dispose()` — ownership and teardown contract

When `VideoRouter.Dispose()` is called the teardown contract for registered channels and
sinks has not been specified.

Possible policies:
- **Policy A:** The router stops dispatching and drains the command queue; it does **not**
  dispose channels or sinks (they are externally owned and may be shared).
- **Policy B:** The router disposes all registered **channels** (it "owns" them via
  `AddChannel`) but not sinks (externally owned).
- **Policy C:** The router disposes both channels and sinks.

This matters for resource cleanup: an `FFmpegVideoChannel` holds native FFmpeg resources
and must be disposed; an `OpenGlVideoSink` holds GPU resources that are typically disposed
by the UI control that owns the sink.

**Open question:** Should `VideoRouter.Dispose()` follow Policy A (dispose nothing), Policy B
(dispose channels only), or Policy C (dispose everything)?

> ✅ **Decision (I.5):** **Policy B** — `VideoRouter.Dispose()` drains the command queue,
> stops dispatching, and calls `Dispose()` on every registered channel (it took ownership
> when `AddChannel` was called).  Sinks are **not** disposed — they are externally owned
> (typically by a UI control or application-level owner) and may outlive the router.
> This matches the principle of least surprise: the router manages what it was given
> ownership of, and nothing else.

---

### Group J — Decided

#### J.1 `VideoFrame.Data` — multi-plane layout convention

`VideoFrame` carries a single `ReadOnlyMemory<byte> Data` field.  Planar formats have
**physically separate memory planes**:

| Format | Planes in `AVFrame.data[]` |
|--------|---------------------------|
| `Yuv420p` | `data[0]` = Y, `data[1]` = U, `data[2]` = V |
| `Yuv422p10` | `data[0]` = Y, `data[1]` = U, `data[2]` = V (10-bit LE words) |
| `Nv12` | `data[0]` = Y, `data[1]` = interleaved UV |

FFmpeg returns each plane as a separate pointer.  When `FFmpegVideoChannel` copies frame
data into a managed buffer (to escape the decode thread), the layout of planes within
that buffer must be unambiguous so that `PixelFormatInfo.GetPlaneOffset` and every
converter can locate each plane without an extra allocation.

Options:
- **Option A:** **Concatenate planes** in a single rental: `[Y plane][U plane][V plane]`.
  `GetPlaneOffset(fmt, plane, height, stride0, stride1)` computes the byte offset.
  One allocation, simple ownership, all existing `IMemoryOwner<byte>` / `ArrayPool`
  patterns work unchanged.
- **Option B:** Add `Data1` and `Data2` fields to `VideoFrame` for planes 1 and 2.
  Each plane is an independent `ReadOnlyMemory<byte>`.  Direct pointer per plane, no
  offset arithmetic, but struct grows further and ownership of 3 separate rentals must
  be managed.
- **Option C:** Change `Data` to `ReadOnlyMemory<byte>[]` — one entry per plane.
  Clean API but allocates an array on every frame.

**Open question:** Should planes be **concatenated into a single `Data` buffer** (Option A),
stored as **separate `Data`/`Data1`/`Data2` fields** (Option B), or stored as an
**array of Memory** (Option C)?

> ✅ **Decision (J.1):** **Option A** — all planes are concatenated into the single
> `Data` rental in order `[plane 0][plane 1][plane 2]`.  `PixelFormatInfo.GetPlaneOffset(fmt,
> memPlane, height, stride0, stride1)` returns the byte offset of each plane.  One
> `ArrayPool<byte>` rental per frame, one `MemoryOwner`, minimal struct size growth,
> cache-friendly sequential layout.  The offset arithmetic is pure integer math and
> is negligible compared to the memory bandwidth of the plane data itself.

---

#### J.2 `IVideoConverter` — target format parameter

Decision I.1 confirms the call signature `VideoFrame Convert(in VideoFrame source)`.
However, `IVideoConverter` may support multiple output formats (e.g. a libyuv converter
can produce both `Bgra32` and `Nv12`).  Without a target format argument, the converter
must be pre-configured at construction time or at `Preflight`.

Options:
- **Option A:** Target format is **fixed at construction**: `new LibyuvConverter(PixelFormat.Bgra32)`.
  One converter instance per (input, output) pair.  `VideoRouter` instantiates the right
  one during `Preflight`.  Simple; `Convert` needs no extra argument.
- **Option B:** Target format is passed **per call**: `VideoFrame Convert(in VideoFrame source, PixelFormat target)`.
  One converter instance can serve multiple targets.  More flexible but changes the
  already-decided I.1 signature.

**Open question:** Should the target output format be fixed at converter construction
(Option A, simpler, consistent with I.1) or passed per-call (Option B)?

> ✅ **Decision (J.2):** **Option A** — target format is fixed at construction.  Each
> converter instance is a single (input→output) pair; `VideoRouter.Preflight()` selects
> and instantiates the right one per sink.  The `Convert` call signature stays `VideoFrame
> Convert(in VideoFrame source)` with no additional arguments, keeping the hot path free
> of per-call format branching or dictionary lookups.

---

#### J.3 `AlignedMemoryOwner` ↔ `ReadOnlyMemory<byte>` bridge

Decision E.1 introduces `AlignedMemoryOwner` (32-byte aligned native memory via
`NativeMemory.AlignedAlloc`) for libyuv AVX2 paths.  The output of a libyuv conversion
(e.g. `I210ToARGB`) will live in one of these aligned buffers and needs to be stored in
`VideoFrame.Data` as a `ReadOnlyMemory<byte>`.

`ReadOnlyMemory<byte>` backed by a native pointer requires a custom `MemoryManager<byte>`
subclass.  Without this bridge, the only way to expose the native buffer is to copy it
into a managed `byte[]`, defeating the purpose of the aligned pool.

**Open question:** Should `AlignedMemoryOwner` implement `MemoryManager<byte>` (exposing
`GetSpan()` / `Pin()` / `Unpin()`) so its buffer can be wrapped in a zero-copy
`ReadOnlyMemory<byte>` for `VideoFrame.Data`?  Or should the aligned buffer be used
only as a transient scratch (never stored in `VideoFrame`) and the final output always
written into a managed `ArrayPool<byte>` rental?

> ✅ **Decision (J.3):** `AlignedMemoryOwner` **implements `MemoryManager<byte>`**.
> `GetSpan()` returns a `Span<byte>` over the native allocation; `Pin()` returns a
> `MemoryHandle` wrapping the raw pointer (already pinned by virtue of being native
> memory); `Unpin()` is a no-op.  The converter writes its output into the aligned buffer
> and wraps it as `Memory<byte>` via `this.Memory`, which is stored directly in
> `VideoFrame.Data`.  Zero extra copies.  `AlignedMemoryOwner` also acts as the
> `IDisposable` `MemoryOwner` stored in `VideoFrame.MemoryOwner`, returning the buffer
> to the per-converter pool on `Dispose()`.

---

#### J.4 `VideoRouter.Dispatch()` — threading model

`VideoRouter.Dispatch()` has been designed but the **call site** has not been specified.
The caller determines whether the sink list needs read-side locking and whether `Dispatch`
is re-entrant.

Possible models:
- **Model A:** Dispatch is called exclusively from the **GL render thread** (inside
  `OnOpenGlRender`), driven by Avalonia's VSync.  Single-threaded dispatch; no locking
  on the sink list is needed beyond the command-queue drain.
- **Model B:** Dispatch is called from a **dedicated background thread** spawned by the
  router (`Task.Run` loop), decoupled from the GL thread.  The GL sink pulls the latest
  rendered texture separately.  Allows non-GL sinks (NDI, file writer) to receive frames
  without waiting for VSync.
- **Model C:** Dispatch is called from **any thread** (caller-owned).  Router is
  re-entrant; sink list is protected by a reader-writer lock.

**Open question:** Which threading model should `VideoRouter.Dispatch()` follow?

> ✅ **Decision (J.4):** **Model B** — the router owns a dedicated background `Task`
> (started by `StartAsync`, cancelled by `Dispose`).  The loop reads frames from the
> channel source and dispatches to all sinks sequentially on that thread.  The GL sink
> holds a `Channel<VideoFrame>(capacity:1, DropOldest)` internal ring; the router writes
> to it and the GL render thread calls `TryRead` inside `OnOpenGlRender`.  Non-GL sinks
> (NDI, file writer) receive frames directly on the router thread at source cadence,
> fully decoupled from VSync.  No reader-writer lock is needed on the sink list because
> all sink list mutations happen at the start of each dispatch loop iteration via the
> command-queue drain (same thread).

---

#### J.5 `libyuv` native library loading

`FFmpegLoader` handles platform-specific discovery and loading of FFmpeg native
libraries.  A parallel mechanism is needed for `libyuv` (decision A.6 specifies
platform names `libyuv.so` / `libyuv.dylib` / `libyuv.dll`).

Questions to resolve before implementing any libyuv P/Invoke:

1. **Load timing:** Eager (alongside FFmpeg at application start) or lazy (on first
   P/Invoke call via `NativeLibrary.SetDllImportResolver`)?
2. **Optional vs required:** If `libyuv` is absent, the pipeline falls back to
   `sws_scale` (decision A.1).  Should `LibyuvLoader` return a success/failure bool so
   callers can branch, rather than throwing?
3. **Search path:** Same `runtimes/<rid>/native/` convention as FFmpeg, or a
   user-configurable path?

**Open question:** Should a `LibyuvLoader` class be created mirroring `FFmpegLoader`,
with lazy loading and a boolean availability flag so converters can fall back gracefully?

> ✅ **Decision (J.5):** A `LibyuvLoader` class is created in `S.Media.FFmpeg` mirroring
> `FFmpegLoader`.  Loading is **lazy** — a `NativeLibrary.SetDllImportResolver` delegate
> is registered once on first use (thread-safe via `Lazy<bool>`).  The static property
> `LibyuvLoader.IsAvailable` returns the load result without throwing.  Search path
> follows the same `runtimes/<rid>/native/` convention as FFmpeg.  Any converter that
> calls libyuv checks `IsAvailable` and falls back to `SwsConverter` if `false`.

---

### Group K — Decided

#### K.1 `OpenGlVideoSink` latest-frame handoff — `Channel<VideoFrame>` details

Decision J.4 (Model B) specifies that the `OpenGlVideoSink` holds a
`Channel<VideoFrame>(capacity:1, DropOldest)` internal ring.  The router background
thread writes to it; Avalonia's GL render thread calls `TryRead` in `OnOpenGlRender`.

However, `VideoFrame` carries a `MemoryOwner?` (`AlignedMemoryOwner` or `ArrayPool`
rental).  When the channel drops the oldest frame (capacity overflow), the displaced
frame's `MemoryOwner` must be disposed to return the buffer to the pool — otherwise the
pool leaks.

**Open question:** Who calls `Dispose()` on the displaced `VideoFrame.MemoryOwner` when
`DropOldest` silently overwrites it?  Options:
- **Option A:** The GL sink wraps the `Channel` in a helper that calls
  `Dispose()` on the outgoing frame before writing the new one (requires reading the old
  frame from the channel first, which changes it from lock-free to a CAS loop).
- **Option B:** The displaced frame's `MemoryOwner` is a `SharedVideoFrameOwner`
  (ref-counted); the channel simply stores the frame struct; the render thread calls
  `Dispose()` on the frame it dequeues after uploading; dropped frames are cleaned up
  by the ref-count falling to zero in the router thread after the write.
- **Option C:** All frames routed to the GL sink go through `SharedVideoFrameOwner`
  (`AddRef` before enqueue into the sink channel; render thread `Dispose` after upload).
  The `Channel(DropOldest)` is replaced by a two-slot atomic swap (write new, compare
  and swap old out, dispose old).

> ✅ **Decision (K.1):** **Option C** — the `Channel<VideoFrame>(DropOldest)` is replaced
> by a single `volatile VideoFrameHolder? _latest` field (a small `sealed class
> VideoFrameHolder { VideoFrame Frame; }`) protected by `Interlocked.Exchange`.
> Protocol:
> - Router thread's `ReceiveFrame`: calls `frame.MemoryOwner.AddRef()` (→ refcount 2),
>   atomically exchanges `_latest` for the new holder, calls `Dispose()` on the
>   displaced holder's `MemoryOwner` (→ refcount drops by 1 for the old frame; refcount
>   drops by 1 for the router's own reference once dispatch is complete → 0, pool return).
> - GL render thread's `OnOpenGlRender`: atomically exchanges `_latest` for `null`,
>   uploads the texture, calls `Dispose()` on the holder's `MemoryOwner` (→ GL sink's
>   ref goes to 0 → pool return).
>
> Every `AddRef` is matched by exactly one `Dispose`.  No pool leaks, no hidden state,
> fully explicit lifetime — the least surprising contract.

---

#### K.2 `SwsConverter` — context lifecycle on re-negotiation

`SwsConverter` wraps FFmpeg's `SwsContext*` and implements `IVideoConverter`.  Decision
J.2 fixes the target format at construction.  However, decision H.4 (Option C) means
the router detects source resolution/format changes and re-runs `Preflight`-equivalent
logic, potentially replacing the old converter with a new instance.

**Open question:** When `VideoRouter` detects a `NotifyStreamChangedCommand` and
re-negotiates converters, should it:
- **Option A:** Dispose the old `SwsConverter` and create a new one with the updated
  source dimensions/format — `SwsConverter` is always constructed for a fixed
  (src w×h×fmt → dst fmt) tuple.
- **Option B:** `SwsConverter` accepts dimension updates via an internal `Reconfigure(int
  w, int h, AVPixelFormat srcFmt)` method (similar to the `GetSws` cache-invalidation
  fix) and recreates its `SwsContext*` lazily.  The router keeps the same instance.

Option A is simpler and matches the "fixed at construction" principle of J.2.

> ✅ **Decision (K.2):** **Option A** — on `NotifyStreamChangedCommand`, the router
> disposes the existing `SwsConverter` (which frees the `SwsContext*`) and creates a
> fresh instance with the new source dimensions and format.  `SwsConverter` is always
> constructed for a fixed `(srcW × srcH × srcFmt → dstFmt)` tuple; no internal
> reconfiguration path exists.  This is the least surprising behaviour when inputs vary:
> the converter's construction contract is unconditionally honoured.

---

#### K.3 `VideoRouter.Preflight()` — return type and incompatibility handling

`VideoRouter.Preflight()` selects a converter for each sink (or confirms passthrough),
and is called before `StartAsync` and again after each `NotifyStreamChangedCommand`.

If a sink's `PreferredFormats` cannot be served (no converter covers the gap and the
source format is not in `PreferredFormats`), the contract has not been specified.

Options:
- **Option A:** Throw `VideoRouterException` (listing which sinks could not be
  configured).  `StartAsync` fails; the application must fix its converter registrations.
- **Option B:** Return a `PreflightResult` record with a list of warnings/errors per
  sink.  Non-fatal; the router starts anyway and skips unconfigurable sinks.
- **Option C:** Log a warning per unconfigurable sink, skip it silently, return `void`.

**Open question:** Should `Preflight()` throw on incompatibility (Option A — safest for
the developer), return a result object (Option B — most informative), or silently skip
(Option C)?

> ✅ **Decision (K.3):** **Option B** — `Preflight()` returns a `PreflightResult` record
> containing an `IReadOnlyList<PreflightDiagnostic>` (each entry carries the affected
> `IVideoSink`, a severity level `Warning`/`Error`, and a human-readable message).  The
> router continues with whatever sinks it could configure; unconfigurable sinks are
> skipped.  The application inspects the result and decides whether to proceed or abort.
> `Preflight()` itself never throws for format incompatibility — only for catastrophic
> state errors (e.g., called after `Dispose`).  This is informative without being fatal,
> matching the global policy of "safest / least surprising" on control-plane operations.

---

#### K.4 `VideoFrame` construction ergonomics

With the full set of fields (Width, Height, PixelFormat, Stride0/1/2, ColourSpace,
ColourRange, Transfer, Primaries, Data, Pts, MemoryOwner?, HdrMetadata?, GpuFrame?),
the positional `readonly record struct` constructor has **14 parameters**.
`FFmpegVideoChannel` must set all of them on every decoded frame.

Options:
- **Option A:** Accept the large positional constructor; `FFmpegVideoChannel` builds the
  struct inline with named arguments.  No extra type needed.
- **Option B:** Provide a set of static factory methods covering common cases:
  `VideoFrame.FromPlanar(...)`, `VideoFrame.FromPacked(...)`, defaulting uncommon fields.
- **Option C:** A mutable `VideoFrameBuilder` struct that `FFmpegVideoChannel` fills
  incrementally and calls `.Build()` to produce the immutable `VideoFrame`.

**Open question:** Should `VideoFrame` rely on its generated positional constructor
(Option A), gain static factory methods (Option B), or use a builder (Option C)?

> ✅ **Decision (K.4):** **Option A** — use the positional constructor with named
> arguments at call sites.  Zero overhead: the constructor is a simple field-by-field
> assignment that the JIT eliminates entirely.  `record struct` `with`-expressions handle
> the rare cases where a frame needs to be copied with one field changed (e.g. when a
> converter produces a new `Data` buffer but preserves all metadata).  No extra type
> is introduced to the public API.

---

#### K.5 NDI P216 scratch buffer for the `<<6` shift

Decision §6.3 requires `NDIVideoSink` to produce a `<<6` left-shifted copy of the Y
and UV planes when outputting P216 (10-bit YUV422 right-aligned → 16-bit NDI P216
left-aligned).  This shifted copy must not mutate the shared `VideoFrame.Data` buffer
(which may be held by `SharedVideoFrameOwner` and referenced by the GL sink
simultaneously).

The NDI P/Invoke (`NDIlib_send_send_video_v2`) requires a contiguous buffer pointer.
The scratch must exist for the duration of the NDI send call (synchronous).

**Open question:** Should the NDI sink's P216 scratch buffer be:
- **Option A:** A **per-sink `ArrayPool<byte>` rental** — rented before the shift, returned
  after the NDI send.  Simple, no alignment needed (NDI doesn't require AVX2 alignment),
  no persistent state.
- **Option B:** A **per-sink pre-allocated fixed buffer** (sized to the maximum expected
  frame) — allocated at `Configure(VideoFormat)` time and reused every frame.  Zero
  per-frame allocation; buffer is replaced on resolution changes.
- **Option C:** Reuse the `AlignedMemoryOwner` pool (from E.1) — gets 32-byte alignment
  for free; consistent with the libyuv buffer strategy.

> ✅ **Decision (K.5):** **Option B** — a plain `byte[]` (managed array) pre-allocated
> at `Configure(VideoFormat)` time, sized to hold the full shifted P216 frame
> (`width × height × 4` bytes for 16-bit YUV422).  Stored as a field in `NDIVideoSink`
> and reused on every `ReceiveFrame` call.  Zero per-frame allocation in steady state.
> On the next `Configure` call (resolution change), the old array is abandoned (GC
> collects it) and a new one is allocated.  NDI requires no special alignment, and
> `byte[]` is directly pinnable for the P/Invoke call.

---

### Group L — Decided

#### L.1 `IVideoSink.Configure` vs `OnStreamChanged` — call contract

`IVideoSink` exposes two format-notification methods that have not had their call
contract formally specified:

```csharp
void Configure(VideoFormat format);
void OnStreamChanged(VideoFormat newFormat);
```

**Open question:** When is each called?
- Is `Configure` the **initial** setup call (before `StartAsync`) and `OnStreamChanged`
  the **mid-stream** notification (after a resolution or pixel-format change mid-playback)?
- Or are they synonymous and one should be removed?
- When `Preflight` re-runs after `NotifyStreamChangedCommand`, which method does the
  router call on each sink?

> ✅ **Decision (L.1):** The two methods serve distinct purposes and both are kept:
>
> - **`Configure(VideoFormat format)`** — called by the router during `StartAsync`
>   (before the dispatch loop) and again after any `NotifyStreamChangedCommand` that
>   changes dimensions or pixel format.  Sinks must allocate or reallocate resources
>   here (GL textures, NDI send instance, scratch buffers, K.5 pre-allocated byte[]).
>   Called regardless of whether this is first time or a mid-stream change.
> - **`OnStreamChanged(VideoFormat newFormat)`** — called for mid-stream changes that do
>   **not** require buffer reallocation (e.g. colour-space tag update, HDR metadata
>   change, frame-rate change with unchanged dimensions and pixel format).  Sinks may
>   update lightweight metadata state here without reallocating anything.
>
> The router calls `Configure` after re-negotiating converters (Preflight re-run) and
> `OnStreamChanged` for stream events that don't trigger re-negotiation.  Sink
> implementors follow the rule: `Configure` = may allocate; `OnStreamChanged` = no
> allocation.

---

#### L.2 `SinkDropPolicy` — values and default

The `AddSinkCommand` payload (decided H.3) carries a `SinkDropPolicy` per sink.  The
type and its values have not been defined.

`SinkDropPolicy` controls what the router does when a sink's internal buffer is full
(e.g., the `NDIVideoSink` is stalled waiting for a network send to complete):

- **`DropFrame`** — skip the current frame for this sink; keep dispatching to others.
- **`Block`** — wait for the sink to accept the frame (stalls the entire router thread
  for that frame duration).
- **`RemoveSink`** — auto-remove the sink after N consecutive drops.

**Open question:** What values should `SinkDropPolicy` carry, and what should the
default be?

> ✅ **Decision (L.2):** `SinkDropPolicy` is a discriminated union (sealed record
> hierarchy, consistent with H.3):
>
> ```csharp
> abstract record SinkDropPolicy;
> sealed record DropFrame : SinkDropPolicy;                        // default
> sealed record Block : SinkDropPolicy;
> sealed record AutoRemove(int ConsecutiveDropThreshold = 30)
>     : SinkDropPolicy;
> ```
>
> Default: **`DropFrame`** — the current frame is silently skipped for that sink and
> the router continues to other sinks.  The drop count is incremented in a per-sink
> counter for diagnostics (logged at `Debug` level; a `Warning` is logged every 60
> consecutive drops).

---

#### L.3 `IVideoConverter` creation — application or router?

The `AddSinkCommand` payload includes `IVideoConverter?`.  It is not yet specified
**who creates converter instances**:

- **Option A:** The **application** creates and passes a converter at `AddSink` time:
  `router.AddSink(sink, new LibyuvConverter(PixelFormat.Bgra32))`.  `Preflight` merely
  validates that the provided converter's `SupportedInputFormats` covers the source.
- **Option B:** The **router** maintains a `IVideoConverterFactory` registry
  (`router.RegisterConverterFactory(factory)`).  `AddSink(sink)` passes no converter;
  `Preflight` queries the registry to find and instantiate the best converter for each
  (source, sink) format pair automatically.
- **Option C:** Hybrid — the application can pass an explicit converter (Option A) or
  pass `null` to let `Preflight` auto-select from a built-in default registry.

**Open question:** Who is responsible for creating `IVideoConverter` instances — the
application (Option A), the router's factory registry (Option B), or a hybrid (Option C)?

> ✅ **Decision (L.3):** **Option C** (hybrid) — `AddSink(sink, converter: null)` is
> the common case; `Preflight` auto-selects from a **built-in default registry** that
> tries `LibyuvConverter` first (if `LibyuvLoader.IsAvailable`) and falls back to
> `SwsConverter`.  The application may also pass an explicit `IVideoConverter` to
> override auto-selection (e.g. a custom passthrough no-op or a hardware-accelerated
> converter).  This requires zero boilerplate for typical usage while remaining fully
> extensible.

---

#### L.4 `IMediaChannel<VideoFrame>` — how does the router read frames?

Decision J.4 (Model B) gives the router a dedicated background task that reads frames
from registered channels.  The exact reading API on `IMediaChannel<VideoFrame>` has not
been specified.

Options:
- **Option A:** `IMediaChannel<T>` exposes `ChannelReader<T> Reader { get; }` — the
  router calls `await channel.Reader.ReadAsync(ct)` in its loop.  Strongly typed, works
  directly with `System.Threading.Channels`.
- **Option B:** `IMediaChannel<T>` exposes `ValueTask<T> ReadAsync(CancellationToken ct)`
  — a simple awaitable method.  Hides the implementation detail of whether the channel
  uses `System.Threading.Channels` or another mechanism.
- **Option C:** `IMediaChannel<T>` exposes `bool TryRead(out T item)` — synchronous
  polling.  The router loop uses `Task.Delay` / `SpinWait` for backpressure.

**Open question:** Should the router read from channels via `ChannelReader<T>` (Option A),
an awaitable `ReadAsync` method (Option B), or synchronous polling (Option C)?

> ✅ **Decision (L.4):** **Option B** — `IMediaChannel<T>` exposes
> `ValueTask<T> ReadAsync(CancellationToken ct)`.  This is the cleanest abstraction:
> it hides whether the underlying implementation uses `System.Threading.Channels`,
> a ring buffer, or a network source; implementations that do use `BoundedChannel<T>`
> delegate trivially to `_channel.Reader.ReadAsync(ct)`.  The `ValueTask` return avoids
> allocation in the common fast path where a frame is immediately available.  The router
> background loop is simply:
> ```csharp
> while (!ct.IsCancellationRequested)
> {
>     var frame = await _channel.ReadAsync(ct);
>     DrainCommands();
>     DispatchToSinks(in frame);
>     frame.MemoryOwner?.Dispose();
> }
> ```

---

#### L.5 `VideoRouter.Preflight()` timing — explicit or implicit

`Preflight()` has been designed as a separate method, but it has not been decided
whether the application must call it explicitly or whether the router calls it
automatically.

- **Option A:** `Preflight()` is called **automatically inside `StartAsync`** before
  the dispatch loop begins.  The `Task<PreflightResult>` returned by `StartAsync` carries
  the result.  Application has no extra step.
- **Option B:** `Preflight()` must be called **explicitly by the application** before
  `StartAsync`.  The application inspects the `PreflightResult`, adjusts sinks/converters
  if needed, then calls `StartAsync`.  `StartAsync` throws if `Preflight` was never run
  (or if sinks were added since the last run).

**Open question:** Should `Preflight()` be called automatically inside `StartAsync`
(Option A, simpler, fewer steps for callers) or explicitly by the application before
`StartAsync` (Option B, more control)?

> ✅ **Decision (L.5):** **Option A** — `Preflight()` is called automatically inside
> `StartAsync`.  `StartAsync` returns `Task<PreflightResult>` so the application can
> still inspect diagnostics after the fact.  The startup sequence inside `StartAsync`
> is: (1) drain any queued `AddSink`/`AddChannel` commands, (2) run `Preflight`
> (selects converters, calls `Configure` on each sink), (3) start the background
> dispatch loop.  `Preflight` may also be called manually at any time for diagnostic
> inspection without side effects (idempotent query).

---

### Group M — Final Remaining Questions

#### M.1 Router background loop — frame dispatch timing

Decision J.4 gives the router a dedicated background task that calls
`await _channel.ReadAsync(ct)` and dispatches immediately.  This works correctly for
live sources (NDI input, camera) where frames arrive at the natural capture rate.

For **file sources** (FFmpeg decoder), frames may be pre-decoded and queued in the
`BoundedChannel<VideoFrame>` much faster than real-time (especially on fast hardware or
when reading from a local SSD).  Without pacing, the router would blast all frames to
sinks as fast as the decoder can run, which is correct for transcoding use-cases but
wrong for A/V-synchronised playback.

**Open question:** Should the router background loop apply **PTS-based pacing** — comparing
`frame.Pts` against an `IClock` (the existing audio clock or a dedicated video clock) and
sleeping until the presentation time — or should it dispatch **immediately** and leave
pacing responsibility to the source (`FFmpegVideoChannel` already rate-limits via the
`BoundedChannel` capacity)?

---

#### M.2 `StartAsync` startup sequence — command drain before Preflight

Decision L.5 (Option A) defines the startup sequence as: (1) drain commands, (2) Preflight,
(3) start loop.  However, a timing edge case exists:

If the application calls `AddSink` or `AddChannel` **concurrently** with `StartAsync`
(i.e., on a different thread after `StartAsync` has already begun), the new command
arrives in the queue after step (1) but before step (3).  Step (2) (Preflight) would
then run without seeing the newly queued sink.

**Open question:** Should the router **prohibit** `AddSink`/`AddChannel` during the
startup window (throw `InvalidOperationException` if called while `StartAsync` is in
progress), or should it **re-drain** the command queue a second time immediately before
starting the loop (step 3), accepting that a re-drain after Preflight may miss
converter selection for the late-arriving sink?

---

#### M.3 `IGpuVideoFrame` — Phase 1 placeholder definition

Decision F.4 adds `IGpuVideoFrame? GpuFrame` to `VideoFrame`.  The interface has never
been defined.  In Phase 1 (CPU-only pipeline) this field is always `null`, but the
type must exist for `VideoFrame` to compile.

**Open question:** Should `IGpuVideoFrame` be defined now as a **minimal empty marker
interface** (`interface IGpuVideoFrame { }`) in `S.Media.Core`, to be expanded in a
later phase with texture-handle properties?  Or should it be omitted entirely from Phase
1 and the `GpuFrame` field added only when GPU paths are implemented?

---

#### M.4 `IMediaChannel<T>` — existing interface audit

`IMediaChannel<T>` already exists in `S.Media.Core` and `FFmpegVideoChannel` already
implements it (for the audio pipeline).  Before adding `ReadAsync(CancellationToken ct)`
(decided L.4) and verifying `Guid Id` exists (needed for `RemoveChannel(Guid)` in H.3),
the existing interface definition must be audited.

**Open question:** Does the current `IMediaChannel<T>` already expose a `Guid Id { get; }`
and a `ReadAsync`-compatible API?  If not, which additions are needed without breaking
existing audio-pipeline implementations?

---

#### M.5 `SinkDropPolicy.AutoRemove` — sink notification on forced removal

Decision L.2 defines `AutoRemove(int ConsecutiveDropThreshold)`.  When the threshold
is hit, the router enqueues an `AutoRemoveSinkCommand` (same mechanism as I.3).  The
sink is removed on the next command drain.

**Open question:** Should the router call any notification on the sink before removing
it due to consecutive drop threshold?  Options:
- **Option A:** Silent removal — the sink is simply de-registered; no method is called.
  The application discovers the removal via `PreflightResult` diagnostics on the next
  re-run or by polling `VideoRouter.RegisteredSinks`.
- **Option B:** Call `sink.OnStreamChanged(default)` or a dedicated
  `sink.OnRemoved(RemovalReason reason)` method before de-registration, so the sink
  can clean up or log.
- **Option C:** Raise a `VideoRouter.SinkRemoved` event (carries sink reference and
  reason) that the application can subscribe to.

