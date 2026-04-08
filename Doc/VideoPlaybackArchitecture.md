# Video Playback & Routing Architecture Plan

> **Status:** Design finalised — all §13 open questions resolved (April 2026)  
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
- **No CPU-side bit-shifting needed** — the data is already 16-bit aligned in the ArrayPool buffer
  (FFmpeg `Yuv422p10le` stores each sample left-justified in a `uint16_t`)

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

Conversion steps (zero intermediate heap allocation target):
1. Y plane: `memcpy` (16-bit LE is already valid `uint16_t`; no bit-shift needed — FFmpeg
   left-packs the 10 bits into the top of each `uint16_t`, which is exactly P216's layout)
2. UV plane: interleave U and V planes into a single UV buffer (SIMD-friendly)
3. Total cost at 4K (3840 × 2160): ~100 MB/frame × 60 fps ≈ 6 GB/s memory bandwidth  
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


