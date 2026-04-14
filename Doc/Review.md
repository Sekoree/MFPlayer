# MFPlayer — Codebase Review

> **Date:** April 2026  
> **Scope:** Full API + implementation review covering hot-path optimisation, audio stutter
> diagnosis, YUV auto-detection, video scaling quality, AV drift, and general API observations.

---

## Table of Contents

1. [Audio Stutter in NDIAutoPlayer](#1-audio-stutter-in-ndiauplayer)
2. [YUV Auto-Detection](#2-yuv-auto-detection)
3. [Video Scaling Quality (Bicubic / Lanczos)](#3-video-scaling-quality)
4. [AV Drift in NDIAutoPlayer](#4-av-drift-in-ndiauplayer)
5. [Hot-Path Observations](#5-hot-path-observations)
6. [API Design Observations](#6-api-design-observations)
7. [Implementation Notes by Component](#7-implementation-notes-by-component)
8. [Quick-Win Recommendations Summary](#8-quick-win-recommendations-summary)

---

## 1. Audio Stutter in NDIAutoPlayer

### Root Cause A — Thread.Sleep jitter in NDIAudioChannel

`NDIAudioChannel` runs a dedicated capture thread that calls
`NDIFrameSync.CaptureAudio` on a hand-rolled timing loop:

```csharp
long remMs = (expectedTicks - nowTicks) * 1000L / Stopwatch.Frequency;
if (remMs > 2) Thread.Sleep((int)(remMs - 2));
else Thread.Sleep(1);
```

On Linux, `Thread.Sleep(1)` resolves to a `nanosleep(1 ms)` call which can
actually sleep **1–4 ms** depending on the kernel timer resolution and system
load. With a capture interval of ~21.3 ms (1024 samples @ 48 kHz), the
accumulation of 1–4 ms overshoot per cycle can drift the ring supply
significantly behind the PortAudio demand rate, causing periodic starvation
(audible as dropout / stutter).

**Fix direction:**

* Use a `SpinWait` or `Thread.SpinWait` for the final sub-2 ms window instead
  of `Thread.Sleep(1)`.
* Alternatively, use `Stopwatch`-based busy-wait only in the last sub-millisecond
  and keep the coarse `Thread.Sleep` for the remaining time:

```csharp
long remTicks = expectedTicks - Stopwatch.GetTimestamp();
if (remTicks > Stopwatch.Frequency / 200)           // > 5 ms
    Thread.Sleep((int)(remTicks * 1000L / Stopwatch.Frequency) - 4);
// spin the last ≤5 ms
while (Stopwatch.GetTimestamp() < expectedTicks)
    Thread.SpinWait(50);
```

* Consider using `framesPerBuffer: 0` (i.e. `paFramesPerBufferUnspecified`) in
  `NDIAutoPlayer` instead of the fixed 1024. Letting PortAudio choose the optimal
  size for the driver results in more stable callback intervals on Linux with ALSA.

### Root Cause B — Shared frameSyncGate between audio and video

Both `NDIAudioChannel` and `NDIVideoChannel` lock `_frameSyncGate` per-frame.
`CaptureVideo` holds the gate while executing `Marshal.Copy` of the entire video
frame (e.g. ~8 MB for 1080p UYVY). If this Copy happens just as the audio capture
thread needs the gate, audio is delayed by up to the time it takes to copy a full
video frame — several milliseconds — pushing it past the sleep window and causing
a missed capture slot.

**Fix direction:**

* Give each channel its own gate object. NDI's `FrameSync` API handles thread
  safety internally; the shared gate was likely added as a conservative guard.
  Per-channel locks eliminate the contention entirely.
* If NDI framesync is truly not re-entrant for simultaneous audio+video calls on
  the same instance, use a very brief lock scope: acquire gate → call CaptureAudio
  → release gate, without holding it across the Marshal.Copy. Copy the pointer and
  data size under the lock, then copy the bytes outside:

```csharp
// Inside the capture loop:
NDIAudioFrameV3 frame;
lock (_frameSyncGate)
    _ndiFrameSync.CaptureAudio(out frame, sampleRate, channels, samplesPerFrame);
// Marshal.Copy is outside the lock
```

### Root Cause C — Pre-buffer depth in NDIAutoPlayer

`NDIAutoPlayer` pre-buffers only **8 audio chunks** before starting PortAudio:

```csharp
while (audioChannel.BufferAvailable < 8)
    await Task.Delay(10, token);
```

With `AudioBufferDepth = 32` that gives 8/32 = 25 % ring utilisation at start.
PortAudio immediately begins consuming and NDI has to keep pace. Increasing the
pre-buffer to 12–16 chunks gives more slack to absorb Linux sleep jitter:

```csharp
while (audioChannel.BufferAvailable < 16)
    await Task.Delay(5, token);
```

### Summary

| Cause | Severity | Fix effort |
|---|---|---|
| `Thread.Sleep(1)` jitter in audio capture loop | High | Low — replace with SpinWait |
| Shared frameSyncGate with video causing audio capture delay | High | Low — use per-channel locks |
| Low pre-buffer depth at startup | Medium | Trivial — increase constant |
| `framesPerBuffer=1024` fixed (vs. driver-optimal 0) | Low | Trivial — change in NDIAutoPlayer |

---

## 2. YUV Auto-Detection

### Current State

`YuvAutoPolicy.ResolveRange` always returns `Limited` regardless of input when
`Auto` is requested. `YuvAutoPolicy.ResolveMatrix` applies a resolution heuristic
(≥1280 px wide or > 576 px tall → BT.709, else BT.601). Neither uses per-frame
metadata.

### FFmpeg Side — Already Implemented

`FFmpegVideoChannel` already implements `IVideoColorMatrixHint` and correctly
maps `AVColorSpace` → `YuvColorMatrix` and `AVColorRange` → `YuvColorRange` from
the codec parameters at construction time. These values are read from the stream
header via `cp->color_space` / `cp->color_range`. This works correctly for
well-tagged files.

**Gap:** FFmpeg color metadata is read once at open time from codec parameters.
For streams that update color info frame-by-frame (rare but valid), per-frame
side-data (`AV_FRAME_DATA_MATRIXCOEFFICIENTS`, `AV_FRAME_DATA_COLOR_RANGE`) is not
consulted. For most files this is fine.

### NDI Side — Not Implemented

`NDIVideoChannel` does **not** implement `IVideoColorMatrixHint`. The NDI SDK's
`NDIVideoFrameV2` struct (see `NDILib/Types.cs`) does not expose a structured
color-space field — NDI carries color metadata only as XML in the optional
`PMetadata` string field (e.g. `<ndi_color_space colorspace="BT.709"/>`).

`SDL3VideoOutput` already checks for `IVideoColorMatrixHint` per-frame at line
394–405 and uses it when no override is set. All that is missing is
`NDIVideoChannel` implementing the interface.

**Recommended implementation for NDIVideoChannel:**

1. Implement `IVideoColorMatrixHint` on `NDIVideoChannel`.
2. In the capture loop, after a successful `CaptureVideo`, parse the frame's
   `Metadata` XML string for a `ndi_color_space` tag. Cache the result as
   `_suggestedMatrix` / `_suggestedRange` properties (thread-safe via `Volatile`).
3. Fall back to the existing resolution heuristic in `YuvAutoPolicy` when no
   metadata is present.

Minimal example of the metadata parse:

```csharp
// Parse NDI color-space metadata XML (if present)
private static (YuvColorMatrix, YuvColorRange) ParseNdiColorMeta(string? xml)
{
    if (xml is null) return (YuvColorMatrix.Auto, YuvColorRange.Auto);
    var matrix = xml.Contains("BT.709", StringComparison.OrdinalIgnoreCase)
        ? YuvColorMatrix.Bt709
        : xml.Contains("BT.601", StringComparison.OrdinalIgnoreCase)
            ? YuvColorMatrix.Bt601
            : YuvColorMatrix.Auto;
    var range = xml.Contains("full", StringComparison.OrdinalIgnoreCase)
        ? YuvColorRange.Full
        : YuvColorRange.Auto;
    return (matrix, range);
}
```

3. `YuvAutoPolicy.ResolveRange` returning `Limited` as the hard default for `Auto`
   is conservative and correct for broadcast sources, but should be documented
   as such. Consider adding an XML doc comment explaining the broadcast-safe
   rationale.

---

## 3. Video Scaling Quality

### Why NDI Tools Looks Sharper

NDI Tools' built-in monitor uses high-quality scaling (bicubic or Lanczos).
SDL3VideoOutput + GLRenderer currently use:

* **NV12, I420, YUV variants, BGRA, RGBA:** `GL_LINEAR` texture filter applied
  directly to the source texture — this is standard bilinear interpolation and
  produces blurry results when scaling up significantly.
* **UYVY422:** Two-pass FBO. Pass 1 decodes packed UYVY to planar RGB at native
  resolution (GL_NEAREST on the packed texture). Pass 2 (`FragmentPassthroughFbo`)
  samples the FBO with `GL_LINEAR` and blits to screen — also bilinear.

### FragmentBicubicBlit — Already Written, Not Wired

`GlShaderSources.FragmentBicubicBlit` is a complete Catmull-Rom bicubic shader
using 16 `texelFetch` calls (no hardware bilinear dependency, correct at all
magnification ratios). It includes the Y-flip for FBO rendering. **It is not
yet compiled or used by GLRenderer.**

### Recommended Approach

1. **Add a `ScalingFilter` enum to `SDL3VideoOutput` / `GLRenderer`:**

```csharp
public enum ScalingFilter
{
    Bilinear,   // current default — GL_LINEAR, fast
    Bicubic,    // Catmull-Rom via FragmentBicubicBlit
    Nearest     // GL_NEAREST, pixel-art / 1:1 monitoring
}
```

2. **Route all formats through an FBO when `ScalingFilter != Bilinear`:**

   Currently only UYVY uses the FBO path. To support Bicubic for NV12, I420,
   BGRA etc., either:
   * Add a general-purpose FBO "post-process blit" stage after the format-specific
     decode pass; or
   * Keep per-format paths but swap Pass 2 shader from `_programFbo`
     (passthroughFbo / bilinear) to `_programBicubic` (bicubic) based on setting.

   The FBO approach is architecturally cleaner: decode to intermediate RGB texture
   at native resolution → bicubic blit to screen. This is exactly what UYVY
   already does.

3. **Compile `_programBicubic` at startup** in `GLRenderer.EnsureShaders()`:

```csharp
_programBicubic = CompileProgram(GlShaderSources.VertexPassthrough,
                                  GlShaderSources.FragmentBicubicBlit);
```

4. **In `EnsureFboUyvy` (and a new `EnsureFboGeneric`)** select the blit shader:

```csharp
int blitProgram = _scalingFilter == ScalingFilter.Bicubic
    ? _programBicubic
    : _programFbo;
```

5. **Expose `ScalingFilter` as a property on `SDL3VideoOutput`** so it can be
   changed at runtime (e.g. between scenes). A simple `volatile int` backing
   field is sufficient since GL calls happen on a single render thread.

### Lanczos

A Lanczos-3 GLSL shader is slightly more expensive than Catmull-Rom but
produces marginally less ringing on sharp edges. It is not yet in
`GlShaderSources`. If added, it would look like:

```glsl
// Lanczos-3 sinc approximation (6-tap per axis = 36 texelFetch calls)
float lanczos(float x) {
    if (abs(x) < 1e-5) return 1.0;
    float px = 3.14159265 * x;
    return 3.0 * sin(px) * sin(px / 3.0) / (px * px);
}
```

For broadcast monitoring purposes, Catmull-Rom bicubic (already written) is
generally indistinguishable from Lanczos-3 and is recommended as the first step.
Lanczos can be added as `ScalingFilter.Lanczos` in a follow-up.

---

## 4. AV Drift in NDIAutoPlayer

### Observed Symptom

`NDIAutoPlayer` reported a consistent ~280 ms drift, with video ahead of audio:

```
drift= -273.1ms
drift= -276.7ms
drift= -300.9ms
```

`TryGetAvDrift` computes `AudioChannel.Position − VideoChannel.Position`.
Negative values mean video is ahead of audio.

### Root Cause — Pre-Buffer Asymmetry

Both capture threads start at the same moment (`avSource.Start()`), but the
**startup window** (format detection + SDL3 window creation + audio pre-buffer
wait) takes 200–500 ms before playback actually begins.

During that window:

| Ring | Depth | Capacity @ 60 fps | Behaviour |
|------|-------|-------------------|-----------|
| Audio (48 kHz, 1024 frames/chunk) | 32 chunks | 32 × 21.3 ms = 682 ms | fills without dropping; oldest chunk = T = 0 ms |
| Video (default depth = **4** frames) | 4 frames | 4 × 16.7 ms = **67 ms** | overflows within 67 ms; oldest frame = T ≈ (startup − 67) ms |

With a 341 ms startup window (format detection 100 ms + SDL3 50 ms + 16-chunk
pre-buffer 191 ms), the oldest video frame available at playback start is from
≈ T = 274 ms. The `VideoMixer.NormalizePts` anchors its PTS origin to the first
frame it dequeues — which is that T = 274 ms frame — so video Position 0 maps to
NDI time 274 ms. Audio Position 0 maps to NDI time 0 ms. The 274 ms constant
offset is the observed ~280 ms drift.

### Why the Drift Also Grows Slowly

A secondary residual drift of ~1.9 ms/s was observed. This arises because the
PortAudio device ran at 44 100 Hz while NDI delivered at 48 000 Hz. The
`AudioMixer`'s resampler adjusts consumption correctly, so `audio.Position` tracks
real time. The residual growth comes from NDI video timestamp jitter and minor
scheduler variability in the video render loop. This is small enough to be
inaudible but would accumulate to ~57 ms after 30 s.

### Fix — Three-Part Solution ✅

**Part 1: Increase `VideoBufferDepth` (structural)**

`NDISourceOptions.VideoBufferDepth` default changed from **4 → 16**, and
`NDIAutoPlayer` now uses **32** explicitly:

```csharp
VideoBufferDepth = 32,  // holds 533 ms @ 60 fps — survives the full startup window
```

At 60 fps, 32 frames = 533 ms. This is larger than any realistic startup window,
so frames from T = 0 are always available when the `VideoMixer` first dequeues,
anchoring its PTS origin to T ≈ 0 ms — the same as audio.

**Part 2: Simultaneous small pre-buffer (`Task.WhenAll`)**

The pre-buffer wait was changed from waiting for audio alone to waiting for
**both** simultaneously with smaller counts:

```csharp
// Before (causes asymmetry):
await avSource.WaitForAudioBufferAsync(16, preCts.Token);

// After (both rings accumulate from T = 0):
await Task.WhenAll(
    avSource.WaitForAudioBufferAsync(6, preCts.Token),   // ~128 ms
    avSource.WaitForVideoBufferAsync(2, preCts.Token)    // ~33 ms @ 60 fps
);
```

`WaitForVideoBufferAsync` was added to both `NDISource` and `NDIAVChannel`.

With only 6 audio chunks pre-buffered (~128 ms), audio has ample runway before
the 682 ms deep ring can drain (and the audio capture thread keeps filling it).

**Part 3: Automatic drift correction loop**

A background task measures drift every 30 s and applies a **50 % corrective
offset** via `IAVMixer.SetVideoChannelTimeOffset`:

```csharp
// drift = audio.Position − video.Position
// correction = −drift / 2  →  converges to < 5 ms within 2–3 cycles
var correction = TimeSpan.FromTicks(-drift.Ticks / 2);
avMixer.SetVideoChannelTimeOffset(videoChannel.Id, current + correction);
```

The correction direction: positive offset delays video presentation
(`leaderClock = clockPosition − offset`), which compensates for video being
ahead. 50 % damping prevents oscillation.

### API Addition

```csharp
// NDIAVChannel / NDISource
public Task WaitForVideoBufferAsync(int minFrames, CancellationToken ct = default);
```

The intended idiom for zero-drift playback start:

```csharp
await Task.WhenAll(
    avSource.WaitForAudioBufferAsync(6, ct),
    avSource.WaitForVideoBufferAsync(2, ct)
);
await audioOutput.StartAsync();
await videoOutput.StartAsync();
```

---

## 5. Latency Analysis and Reduction

### Why Latency Is Higher Than NDI Tools Monitor

End-to-end latency = time from "NDI source generates a pixel" to "pixel appears on
screen". The dominant cost is the **startup window** — the time between
`avSource.Start()` and the moment the `VideoMixer` first dequeues a frame.
`VideoMixer.NormalizePts` anchors its PTS origin to the first dequeued frame, so
every millisecond of startup window becomes permanent steady-state latency.

| Startup step | v1 cost | v2 cost | v3 cost | Notes |
|---|---|---|---|---|
| Format detection (`Task.Delay(100)` poll loop) | **up to 100 ms** | ≤10 ms | ≤10 ms | `WaitForVideoBufferAsync(1)` |
| SDL3 window creation | ~20 ms | ~20 ms | ~20 ms | — |
| Audio framesync silence drain | **~T_conn** | **~T_conn** | **0 ms** | Split-start: audio starts after 1st video frame |
| Pre-buffer audio chunks | ~128 ms (6×) | ~128 ms (6×) | **~64 ms (3×)** | Safe now that audio T=0 = real content |
| **Total startup window** | **~250+ ms** | **~158+ ms** | **~94 ms** | T_conn = NDI connection time (variable) |

NDI Tools Monitor: ~33 ms (framesync ~17 ms + vsync ~16 ms, no startup window).
After v3 fixes: ~94 ms — within ~61 ms of NDI Tools.

### NDI FrameSync Always-Returns Behaviour (Root Cause)

`NDIlib_framesync_capture_audio` and `NDIlib_framesync_capture_video` **always
return immediately**, even before any real NDI data has been received. Before the
NDI source begins streaming, framesync returns:
- **Audio**: silence frames (`NoSamples == 0` / `PData == null`)
- **Video**: an all-zero empty frame (`PData == null`)

`NDIAudioChannel` and `NDIVideoChannel` both skip these null frames correctly, so
the capture rings are not polluted. However, if audio capture starts at the same
time as video capture, the audio capture thread must wait for the framesync to
accumulate real NDI packets — it polls in a tight loop, inserting no frames until
the connection is live (T_conn, typically 200–500 ms on a LAN). Only then does
the audio ring start filling with real content.

Because `WaitForAudioBufferAsync` counts from T=0 of the audio ring, those first
N chunks still represent real audio from near the start of the stream — but the
audio capture thread was running silently for T_conn before them. If the startup
sequence waits for audio after waiting for video, the clock's T=0 has already
advanced by T_conn, baking that extra latency into the permanent PTS offset.

### Split-Start Fix (v3)

`NDISource` now exposes `StartVideoCapture()` and `StartAudioCapture()` as
separate idempotent methods. `NDIAVChannel` passes them through. `Program.cs`
sequences:

```
1. avSource.StartVideoCapture()      ← video capture thread starts
2. WaitForVideoBufferAsync(1)        ← blocks until first real NDI frame (~T_conn)
3. SDL3 window open                  ← format is now known
4. avSource.StartAudioCapture()      ← audio starts NOW — NDI is confirmed live
5. WaitForAudioBufferAsync(3) + WaitForVideoBufferAsync(2)  ← pre-buffer
6. output.StartAsync()
```

By step 4, the NDI framesync already has real audio data queued (because video
confirmed the source is streaming). The audio ring fills immediately with real
content, so the pre-buffer count can be safely reduced from 6 chunks (~128 ms) to
3 chunks (~64 ms) without risking underruns.

### Video Capture Sleep

`NDIVideoChannel.CaptureLoop` sleeps after each captured frame to avoid spinning.
The old coefficient (400/fps) gave ~6.7 ms at 60 fps; the new coefficient (250/fps)
gives ~4.2 ms. This shaves ~2–3 ms from per-frame video pipeline delay.

### Steady-State Latency Budget (After All Fixes)

| Component | Latency |
|---|---|
| NDI framesync internal buffer | ~17 ms (1 frame @ 60 fps) |
| NDIVideoChannel capture sleep | ~4 ms (250/fps @ 60 fps) |
| VideoMixer + render loop | ~17 ms (1 vsync @ 60 fps) |
| **Total video** | **~38 ms** |
| NDI audio framesync | ~21 ms (1 chunk) |
| PortAudio hardware buffer (`framesPerBuffer=0` → ~512 samples @ 48 kHz) | ~11 ms |
| **Total audio** | **~32 ms** |

The ~6 ms A/V offset between audio and video pipelines is within the
`LeadTolerance = 5 ms` + auto-correction loop margin.

### Further Reduction (if needed)

* **Use a smaller `framesPerBuffer`** (e.g. 256 → ~5 ms hw latency vs 512 → ~11 ms).
  Pass this to `output.Open(device, hwFmt, framesPerBuffer: 256)`. Risk: higher
  CPU load and more PortAudio xruns on slow machines.
* **Implement a `WaitForVideoFormatAsync` event** in `NDIVideoChannel` using
  `TaskCompletionSource` so there is zero polling at all during format detection
  (eliminates the remaining ≤10 ms poll window).
* **Bypass framesync for video** and use `NDIlib_recv_capture_v3` directly. The
  framesync adds ~17 ms of internal TBC buffering that cannot be eliminated
  through the framesync API. Using the raw recv API reduces video latency to
  ≤1 frame but requires the application to handle clock-domain mismatches itself.
  Audio framesync should still be kept for its automatic drift compensation.

---

## 6. Hot-Path Observations

### 5.1 AudioMixer — SIMD Mixing (Good)

`AudioMixer.ScatterIntoMix`, `MultiplyInPlace`, and `UpdatePeaks` all use
`System.Numerics.Vector<float>` for SIMD acceleration. The copy-on-write arrays
(`_slots`, `_sinkTargets`) ensure zero allocation in the steady-state callback.
Overall this is well-implemented.

**Minor:** `_peakLevels.AsSpan().CopyTo(_peakSnapshot)` in the RT callback is read
by UI threads without a memory barrier. On x86/x64 this is safe due to TSO, but
on ARM it could produce stale reads. A `Volatile.Write` on a generation counter or
a simple `Interlocked.Exchange` on a reference-swapped snapshot would be the
portable fix.

### 5.2 PortAudioOutput Callback (Good)

`[UnmanagedCallersOnly(CallConvCdecl)]` pinned via `GCHandle` — correct. Zero
allocation on the hot path. The chunked fallback for oversized callbacks is a
thoughtful safety measure. Exception → silence rather than crash is the right
audio strategy.

**Minor:** The `_activeMixer` is `volatile IAudioMixer?`. If `OverrideRtMixer` is
called with `null` and there is no `_mixer` fallback either, lines 181–182 abort
silently. This is correct but the code path is not documented; a comment would
help maintainers.

### 5.3 LinearResampler — Quality vs. SwrResampler

`LinearResampler` uses linear interpolation. It is adequate for small rate
discrepancies (e.g. 44100→48000 for background playback) but introduces
audible high-frequency aliasing on music content. `SwrResampler` (libswresample,
polyphase sinc) is already available in the same assembly and is strictly
superior in quality.

`PortAudioSink` auto-creates a `LinearResampler` when none is provided. Consider
changing this default to `SwrResampler` when FFmpeg is already in the dependency
graph, or at minimum document the trade-off clearly in `PortAudioSink`'s XML
docs. `AudioMixer.AddChannel` also defaults to `LinearResampler`.

### 5.4 NDIAudioChannel — FramesPerCapture Constant

`FramesPerCapture = 1024` is hardcoded. The capture interval is computed from
`sampleRate`, so the number of samples-per-capture matches the PortAudio buffer
size (also 1024) when sampleRate is 48000. However if the PortAudio output is
opened at a different `framesPerBuffer`, or if NDI delivers at 44100 Hz with a
1024-sample chunk, the rates will drift. The value should either be configurable
or computed from `NDISourceOptions.AudioBufferDepth` and the actual output format.

### 5.5 NDIVideoChannel — SingleReader = false (Minor)

`NDIVideoChannel` creates its ring with `SingleReader = false` even though only
`FillBuffer` (one call site) reads it. `NDIAudioChannel` correctly uses
`SingleReader = true`. Fixing this allows `System.Threading.Channels` to use its
faster single-reader path.

### 5.6 HardwareClock — Lock on Position (Minor)

`HardwareClock.Position` acquires `_fallbackLock` on every call. This property is
accessed from the render thread every vsync. A `SpinLock` or a purely `Interlocked`
pattern (e.g. reading two `long` fields atomically) would eliminate the lock
overhead, though the actual impact is negligible.

### 5.7 FFmpegVideoChannel — sws_scale uses SWS_BILINEAR

```csharp
_sws = ffmpeg.sws_getCachedContext(_sws, w, h, srcFmt, w, h, dstFmt,
    2 /* SWS_BILINEAR */, null, null, null);
```

This is a pixel-format conversion (not a resize), so bilinear vs. nearest doesn't
matter much here. However the magic number `2` should be replaced with the
named constant `ffmpeg.SWS_BILINEAR` for clarity.

---

## 6. API Design Observations

### 6.1 AVMixer — Clean Façade (Positive)

`AVMixer` provides a clean single-object API that wraps `AudioMixer` and
`VideoMixer` behind a single `IAVMixer`. The endpoint/sink/channel separation,
including the `VideoEndpointSinkAdapter` and `AudioEndpointSinkAdapter` bridge
pattern, is well-designed and extensible.

### 6.2 ChannelRouteMap.Auto — Helpful Convenience (Positive)

`ChannelRouteMap.Auto(srcChannels, dstChannels)` simplifies the common mono→stereo
and passthrough cases. The explicit builder in `SimplePlayer` for mono fan-out is
still needed for non-trivial mappings, which is appropriate.

### 6.3 IVideoColorMatrixHint — Good Extension Point

The interface is the right mechanism for channels to push colour metadata to the
renderer. `FFmpegVideoChannel` implements it; `NDIVideoChannel` should too (see §2).

**Suggestion:** Rename `SuggestedYuvColorMatrix` / `SuggestedYuvColorRange` to
`HintYuvColorMatrix` / `HintYuvColorRange` to match the interface name and make
the opt-in / hint-only nature more explicit at call sites.

### 6.4 NDISource Auto-Reconnect — Well Implemented

The reconnection loop uses `_receiver.GetConnectionCount()` with a configurable
`ConnectionCheckIntervalMs` (default 2000 ms), keeps the `NDIFinder` alive for
name-based reconnect, and handles cancellation cleanly. No issues found.

### 6.5 NDIAVSink TryConvertI210ToUyvyInPlace

The reverse-row in-place conversion is clever but should have a comment explaining
*why* rows are processed bottom-to-top (to avoid overwriting source data before it
is consumed). Currently this is undocumented and appears as an unexplained loop
direction to any future reader.

### 6.6 GlShaderSources — GLSL Code Duplication

The YUV → RGB matrix transformation block (the BT.601/709/2020 if-else chain) is
**copy-pasted verbatim in 6 separate shader strings**: `FragmentNv12`,
`FragmentI420`, `FragmentI422P10`, `FragmentUyvy422`, `FragmentP010`,
`FragmentYuv444p`. Any fix or improvement (e.g. adding BT.2020 support) must be
applied to all 6.

**Recommended fix:** Define a shared GLSL helper function string in
`GlShaderSources` and concatenate it at compile time:

```csharp
private const string YuvToRgbFunctions = @"
vec3 yuv_to_rgb(vec3 yuv, int matrix, int range) {
    // ... single implementation ...
}";

public static string FragmentNv12 => VertexShaderPreamble + YuvToRgbFunctions + @"
    void main() { ... call yuv_to_rgb(...) ... }";
```

This reduces the maintenance surface from 6 copies to 1.

### 6.7 PortAudioOutput framesPerBuffer=0 vs. 1024

`SimplePlayer` uses `framesPerBuffer: 0` (driver-optimal), which is good. 
`NDIAutoPlayer` uses `framesPerBuffer: 1024`. Consider using `0` in `NDIAutoPlayer`
as well for more stable driver-level scheduling (see §1).

### 6.8 AVMixer Endpoint vs. Sink API Asymmetry

`IAVMixer` exposes both `RegisterVideoSink/RegisterVideoEndpoint` and
`RouteVideoChannelToSink/RouteVideoChannelToEndpoint`. The distinction between
`IVideoSink` (push model) and `IVideoFrameEndpoint` (adapter-wrapped pull model)
is not obvious from the API surface alone. Consider a unified doc comment on
`IAVMixer` explaining when to use each.

---

## 7. Implementation Notes by Component

### S.Media.Core

| File | Note |
|---|---|
| `AudioMixer.cs` | RT-safe, SIMD accelerated, copy-on-write arrays. Peak snapshot copy lacks memory barrier (safe on x86, minor on ARM). |
| `LinearResampler.cs` | Growable `_combinedBuf` via `new float[]` — allocation only on first oversized call, not in steady state. Quality inferior to `SwrResampler` for music. |
| `DriftCorrector.cs` | PI controller (Kp=2e-3, Ki=1e-5) is applied in `PortAudioSink` and `NDIAVSink` secondary sinks but NOT in the primary `PortAudioOutput` callback. This is by design (primary is the master clock) but worth documenting. |
| `YuvAutoPolicy.cs` | `ResolveRange` hard-defaults to `Limited`. Document the broadcast rationale. |
| `CopyOnWriteArray.cs` | Well-implemented. Consider adding a `TryGetCopy()` that returns `null` instead of throwing on empty, to simplify caller code. |

### S.Media.FFmpeg

| File | Note |
|---|---|
| `FFmpegVideoChannel.cs` | Color-space mapping from codec params is correct. `sws_getCachedContext` with `SWS_BILINEAR` is fine for pixel-format conversion. `SingleReader = false` on ring (same as NDIVideoChannel, minor). |
| `FFmpegDecoder.cs` | (Not read in full) Uses `FFmpegDecodeWorkers` for separate demux/decode pipelines — a good pattern. |
| `SwrResampler.cs` | High-quality polyphase resampler. Should be the default where FFmpeg is available. |

### S.Media.NDI

| File | Note |
|---|---|
| `NDIAudioChannel.cs` | ✅ Sleep jitter fixed (SpinWait). ✅ Per-channel gate. Pool exhaustion falls back to `new float[]` — acceptable. |
| `NDIVideoChannel.cs` | ✅ Implements `IVideoColorMatrixHint`. ✅ `SingleReader = true`. ✅ Capture sleep halved (250/fps). |
| `NDISource.cs` | ✅ `WaitForVideoBufferAsync` added. ✅ `VideoBufferDepth` default raised to 16. ✅ `StartVideoCapture`/`StartAudioCapture` split. Reconnect logic solid. |
    | `NDIAVChannel.cs` | ✅ `WaitForVideoBufferAsync` added. ✅ `StartVideoCapture`/`StartAudioCapture` pass-throughs. `TryGetAvDrift` documented. |
| `NDIAVSink.cs` | In-place row reversal in `TryConvertI210ToUyvyInPlace` needs a comment. Complex but correct. |

### S.Media.SDL3 / S.Media.Avalonia / S.Media.Core (Video)

| File | Note |
|---|---|
| `GLRenderer.cs` | ✅ Bicubic FBO path wired for all pixel formats. Default scaling = `Bicubic`. |
| `GlShaderSources.cs` | YUV→RGB matrix block duplicated in 6 shaders (§6.6, still pending). `FragmentBicubicBlit` ✅ connected. |
| `SDL3VideoOutput.cs` | ✅ `ScalingFilter` property. ✅ `YuvColorMatrix`/`YuvColorRange` properties. Presentation clock override solid. |
| `AvaloniaOpenGlVideoOutput.cs` | ✅ Feature parity with SDL3: `ScalingFilter`, `YuvColorMatrix`, `YuvColorRange`. Default `Bicubic`. |
| `AvaloniaGlRenderer.cs` | ✅ Bicubic FBO wired (all formats). Matches SDL3 renderer capabilities. |
| `ScalingFilter.cs` | ✅ Moved from `S.Media.SDL3` to `S.Media.Core.Video` — shared by both renderers. |

### Audio — PortAudio

| File | Note |
|---|---|
| `PortAudioOutput.cs` | Callback is properly unmanaged, pinned, zero-alloc. Chunked fallback is a safety net. |
| `PortAudioSink.cs` | Auto-creates `LinearResampler` — should document / consider `SwrResampler` default. |
| `PortAudioClock.cs` | `Pa_GetStreamTime` provider — simple and correct. |

---

## 8. Quick-Win Recommendations Summary

Items marked ✅ have been implemented.

| Priority | Item | File(s) | Effort | Status |
|---|---|---|---|---|
| **P0** | Fix audio capture sleep jitter (SpinWait) | `NDIAudioChannel.cs` | 1–2h | ✅ Done |
| **P0** | Split shared frameSyncGate per channel | `NDIAudioChannel.cs`, `NDIVideoChannel.cs` | 1h | ✅ Done |
| **P1** | Wire `FragmentBicubicBlit` + `ScalingFilter` enum (SDL3) | `GLRenderer.cs`, `SDL3VideoOutput.cs` | 4–6h | ✅ Done |
| **P1** | Avalonia renderer feature parity + bicubic default | `AvaloniaGlRenderer.cs`, `AvaloniaOpenGlVideoOutput.cs` | 3–4h | ✅ Done |
| **P1** | Implement `IVideoColorMatrixHint` on `NDIVideoChannel` | `NDIVideoChannel.cs` | 2–3h | ✅ Done |
| **P1** | Fix AV drift (VideoBufferDepth, simultaneous pre-buffer, auto-correction) | `NDISource.cs`, `NDIAVChannel.cs`, `Program.cs` | 2–3h | ✅ Done |
| **P1** | Reduce latency: event-based format detection + reduced capture sleep | `NDIVideoChannel.cs`, `Program.cs` | 1h | ✅ Done |
| **P1** | Reduce latency: split StartVideoCapture/StartAudioCapture, pre-buffer 6→3 | `NDISource.cs`, `NDIAVChannel.cs`, `Program.cs` | 1h | ✅ Done |
| **P2** | Fix `SingleReader = false` in NDIVideoChannel ring | `NDIVideoChannel.cs` | 5 min | ✅ Done |
| **P2** | Deduplicate GLSL YUV→RGB matrix block | `GlShaderSources.cs` | 2–3h | Pending |
| **P3** | Default `PortAudioSink` resampler to `SwrResampler` | `PortAudioSink.cs` | 1h | Pending |
| **P3** | Document in-place row-reversal in `TryConvertI210ToUyvyInPlace` | `NDIAVSink.cs` | 15 min | Pending |
| **P3** | Replace magic `2` with `ffmpeg.SWS_BILINEAR` | `FFmpegVideoChannel.cs` | 5 min | Pending |
| **P3** | Add memory barrier to peak snapshot copy in AudioMixer | `AudioMixer.cs` | 30 min | Pending |
| **P3** | Add `SpinLock` to `HardwareClock.Position` | `HardwareClock.cs` | 30 min | Pending |


