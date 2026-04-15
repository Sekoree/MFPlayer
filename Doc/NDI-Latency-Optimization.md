# NDI Receive Pipeline – Latency Optimization Plan

> **Date:** 2026-04-15  
> **Goal:** Match the glass-to-glass latency of the official *NDI Tools Monitor* app.  
> **Current gap:** ~20–60 ms above NDI Tools Monitor, depending on content and system.

---

## Table of Contents

1. [Current Architecture](#1-current-architecture)
2. [Identified Latency Sources](#2-identified-latency-sources)
3. [Proposed Optimizations](#3-proposed-optimizations)
4. [Implementation Order & Expected Impact](#4-implementation-order--expected-impact)
5. [Risk Notes](#5-risk-notes)
6. [Appendix A: NDI SDK References](#appendix-a-ndi-sdk-references)
7. [Appendix B: Estimated Latency Budget](#appendix-b-estimated-latency-budget-current-vs-target)

---

## 1. Current Architecture

```
NDI SDK  ──►  NDIFrameSync (TBC)
                  │
        ┌─────────┴──────────┐
        ▼                    ▼
  CaptureVideo()       CaptureAudio()        (both under _frameSyncGate lock)
        │                    │
   Marshal.Copy        planar→interleaved
   to ArrayPool         conversion
        │                    │
   Ring-buffer           Ring-buffer
   (Channel<T>)          (Channel<T>)
        │                    │
   VideoMixer            AudioMixer
        │                    │
   SDL3VideoOutput       PortAudioOutput
   (vsync-driven)        (callback-driven)
```

### Key parameters (current defaults)

| Parameter | Value | Location |
|---|---|---|
| Latency preset (video ring-buffer capacity) | `LowLatency = 4` / `Balanced = 8` / `Safe = 12` | `NDILatencyPreset.cs` |
| Audio capture chunk | `1024` samples (~21.3 ms @ 48 kHz) | `NDIAudioChannel.cs` `FramesPerCapture` |
| PortAudio `framesPerBuffer` | `0` (driver-chosen, typically 512–1024 ≈ 10–21 ms) | `Program.cs` line 218 |
| Pre-buffer wait | 3 audio chunks + 2 video frames | `Program.cs` lines 324-329 |
| Video capture throttle | `Thread.Sleep(¼ frame interval)` or `1 ms` in low-latency | `NDIVideoChannel.cs` lines 218-224 |
| Color format | `Fastest` (= UYVY / P216, no CPU conversion) | `Types.cs` `NDIReceiverSettings` default |

---

## 2. Identified Latency Sources

Each source is listed with an estimate of the additional latency it contributes **beyond what NDI Tools Monitor pays**.

### 2.1 FrameSync Time-Base Corrector (~16–33 ms)

**What:** `NDIFrameSync` is NDI's built-in TBC. It absorbs jitter by holding roughly one frame of video internally, then serves the "current" frame on pull. The SDK documentation (§15.4) explicitly states it adds hysteresis for smooth playout.

**Why it matters:** NDI Tools Monitor does **not** use FrameSync for its low-latency mode – it uses raw `NDIlib_recv_capture_v3` on separate audio/video threads (documented in §7.4). This is likely the single largest contributor to our latency gap.

**Evidence:** §7.4 of the SDK documentation recommends:
> *"The best approach for receiving is to have two threads, one that receives audio and one that receives video [...] These calls are all thread safe and so can all be called on separate threads if needed."*

### 2.2 Video Frame Copy (`Marshal.Copy`) (~1–2 ms)

**What:** `TryCopyFrameToTightBuffer()` in `NDIVideoChannel.cs` calls `Marshal.Copy` to duplicate the entire video frame (e.g., ~4 MB for 1080p UYVY) into an `ArrayPool<byte>` buffer before calling `NDIFrameSync.FreeVideo()`.

**Why it matters:** The copy itself is fast (~1 ms), but it blocks the capture thread and delays freeing the NDI buffer back to the SDK, which can increase back-pressure on the sender and prevent the next frame from arriving promptly. NDI Tools Monitor likely processes directly from the NDI buffer.

### 2.3 Pre-Buffer Depth (~30–65 ms startup, ongoing ring-buffer depth)

**What:** The test player waits for **3 audio chunks** (~64 ms) + **2 video frames** (~33–67 ms @ 30–60 fps) before starting playback. The ring-buffer `BoundedCapacity` is set by the latency preset (4–12 frames).

**Why it matters:** While pre-buffering is a startup cost, the ring-buffer capacity also determines the *steady-state* pipeline depth. A capacity of 4 means up to 4 frames can be queued, each adding one frame-interval of latency if the consumer falls behind.

### 2.4 PortAudio Output Buffer (~10–21 ms)

**What:** `framesPerBuffer: 0` in the `PortAudioOutput` constructor lets the audio driver choose its own buffer size. On Linux/ALSA this is typically 512–1024 samples (10–21 ms).

**Why it matters:** This is the irreducible minimum audio latency in the output path. NDI Tools Monitor on Windows likely uses WASAPI exclusive mode with very small buffers (128–256 samples ≈ 2.7–5.3 ms).

### 2.5 Audio Capture Chunk Size (~21 ms floor)

**What:** `NDIAudioChannel.FramesPerCapture = 1024` means each call to `CaptureAudio()` requests 1024 samples. The FrameSync accumulates incoming audio until this many samples are available.

**Why it matters:** This sets a ~21 ms floor on the latency between an audio sample entering the receiver and being queued to the ring-buffer. Reducing to 256–512 samples would halve or quarter this.

### 2.6 `_frameSyncGate` Lock Contention (~0–2 ms, jitter)

**What:** Both `CaptureVideo` + `FreeVideo` and `CaptureAudio` + `FreeAudio` acquire the same `_frameSyncGate` lock (in `NDISource.cs`). While the SDK documentation says FrameSync is thread-safe, the lock serialises audio and video capture.

**Why it matters:** If a video capture (including the `Marshal.Copy`) holds the lock while audio needs to capture, the audio thread is delayed. This mostly adds jitter rather than steady-state latency, but jitter forces larger buffers elsewhere.

**Update from code review:** `NDISource.Open()` actually creates *separate* lock objects per channel (line 184–186). So this issue may already be mitigated. Need to verify the actual code path.

### 2.7 Video Capture Loop Sleep (~1–8 ms jitter)

**What:** After each video capture, the loop calls `Thread.Sleep(frameInterval / 4)` in normal mode, or `Thread.Sleep(1)` in low-latency mode. On Linux, `Thread.Sleep(1)` has ~1–4 ms granularity.

**Why it matters:** This adds 1–4 ms of average latency per frame on the capture side. More critically, it means we don't immediately capture a new frame when one becomes available.

### 2.8 No Hardware Decode Metadata (variable, 0–5 ms)

**What:** The NDI SDK supports requesting hardware-accelerated video decoding via connection metadata:
```xml
<ndi_video_codec type="hardware"/>
```
This is not currently sent by our `NDIReceiver`.

**Why it matters:** Hardware decoding can reduce CPU load and decode latency for high-resolution streams (especially 4K or high-bandwidth codecs). Impact varies by GPU and stream type.

**Update from SDK §7.4:** Modern NDI versions (v5+) "almost certainly already default to using hardware acceleration in most situations where it would be beneficial." Sending the metadata explicitly is still a low-risk safety net, but the real-world gain may be negligible on current SDK versions.

### 2.9 VSync-Quantized Rendering (~0–16.7 ms)

**What:** `SDL3VideoOutput.cs` enables VSync with `SDL.GLSetSwapInterval(1)` (line 241). The render loop calls `mixer.PresentNextFrame(clockPosition)` to select the frame, then `SDL.GLSwapWindow()` blocks until the next vertical blanking interval.

**Why it matters:** VSync quantizes frame presentation to the monitor's refresh rate — at 60 Hz that's a ~16.67 ms grid. If a frame arrives just *after* a vsync deadline, it waits up to 16.67 ms for the next one. On average this adds **~8 ms** of latency. NDI Tools Monitor likely uses either:
- `GLSetSwapInterval(0)` (immediate swap, no vsync wait), or
- Mailbox/adaptive mode (`GLSetSwapInterval(-1)`) which swaps immediately on a new frame but avoids tearing by using the most recent buffer, or
- A compositor-aware presentation path (e.g., Wayland `wp_presentation` feedback, or Windows DWM direct flip).

**Evidence:** The render loop in `SDL3VideoOutput.cs` (lines 382–467) is entirely paced by `GLSwapWindow()`. There is no independent "new frame available" wake-up; frames sit in the `VideoMixer` staging slot until the next vsync iteration pulls them.

### 2.10 Planar-to-Interleaved Audio Conversion – Scalar Loop (~0.5–1.5 ms)

**What:** `NDIAudioChannel.PlanarToInterleaved()` (lines 235–248) performs a manual scalar nested loop to transpose planar float audio into interleaved layout:
```csharp
for (int ch = 0; ch < channels; ch++)
{
    float* pCh = pBase + ch * stride;
    for (int s = 0; s < samples; s++)
        dest[s * channels + ch] = pCh[s];
}
```

**Why it matters:** This transpose has a scattered write pattern (`dest[s * channels + ch]`) that is unfriendly to CPU caches, and processes one float at a time. For stereo 1024-sample chunks it's fast (~0.1 ms), but at higher channel counts (e.g., 8-channel audio) or on slower CPUs it becomes measurable. More importantly, the NDI SDK already provides a native optimized version — `NDIlib_util_audio_to_interleaved_32f_v3` — which is already wrapped in `NDIWrappers.cs` (line 823) but **not used** by `NDIAudioChannel`.

**Why the native function is better:** The SDK implementation is SIMD-optimized and performs the same planar→interleaved conversion without an extra managed allocation or pinning.

### 2.11 Presentation Clock Origin Latching (~variable, 5–30 ms startup offset)

**What:** When `SDL3VideoOutput` uses an external presentation clock (the PortAudio hardware clock in NDIAutoPlayer), it latches the clock's first observed position as the "origin" on the first render iteration (`_presentationClockOriginTicks`, lines 398–399). All subsequent video PTS values are rebased relative to this origin.

**Why it matters:** The PortAudio clock (`Pa_GetStreamTime`) starts counting from the moment the PA stream is opened and begins playing. By the time the SDL3 render thread performs its first iteration and latches the origin, some time has already elapsed — this includes the PA startup latency, the pre-buffer fill time, and any scheduling delay before the render thread runs. This baked-in offset means the video timeline starts "behind" the audio timeline by however long that gap is.

**Evidence:** In `SDL3VideoOutput.cs` lines 392–404:
```csharp
var clockPosition = presentationClock.Position;
if (externalClock != null)
{
    if (Interlocked.CompareExchange(ref _hasPresentationClockOrigin, 1, 0) == 0)
        Volatile.Write(ref _presentationClockOriginTicks, rawTicks);
    long originTicks = Volatile.Read(ref _presentationClockOriginTicks);
    long relTicks = rawTicks - originTicks;
    clockPosition = TimeSpan.FromTicks(relTicks);
}
```
The origin is whatever `Pa_GetStreamTime` returns on the first render-loop vsync tick, which is non-deterministic.

### 2.12 VideoMixer Lead Tolerance & Drop Threshold (design-level, ~5–30 ms interaction)

**What:** `VideoMixer` uses two scheduling constants:
- `LeadTolerance = 5 ms` — a frame is presented only when its PTS ≤ clockPosition + 5 ms.
- `_dropLagThreshold = max(30 ms, 2 / fps)` — frames whose PTS is more than this behind the clock are dropped as stale.

**Why it matters:** The 5 ms `LeadTolerance` means a frame that arrives "slightly early" (within 5 ms of its target time) will be presented, but a frame arriving 6 ms early will be held until the *next* vsync iteration (adding up to 16.67 ms). Combined with vsync quantization (§2.9), this creates a worst-case delay of `LeadTolerance` + one vsync period. Additionally, the `_dropLagThreshold` of 30 ms at 30 fps means frames up to 30 ms behind the clock are *not* dropped but are shown late — contributing to perceived latency rather than being skipped in favor of a newer frame.

For low-latency live NDI, a more aggressive strategy would be: always present the newest available frame, even if it means skipping intermediate frames.

### 2.13 `MediaClockBase` Tick Interval (indirect, ~0–10 ms jitter for event-driven consumers)

**What:** `NDIClock` fires its `Tick` event every 10 ms (default `tickIntervalMs = 10` in `NDIClock.cs` line 28). `HardwareClock` (PortAudio) defaults to 20 ms but is updated to match the audio buffer duration via `UpdateTickInterval()`.

**Why it matters:** The `Tick` event is used by event-driven consumers to poll for clock position updates. While the SDL3 render loop and PortAudio callback don't use `Tick` (they read `.Position` directly on each iteration), any middleware or monitoring component listening to `Tick` will only see updates at this interval, adding quantization jitter. The main impact is indirect — if future optimizations add event-driven frame scheduling (e.g., wake-on-new-frame), the tick interval would become the bottleneck.

This is **not** a current latency source for the NDI playback path, but is worth noting as a constraint for future event-driven architectures.

---

## 3. Proposed Optimizations

### OPT-1: Bypass FrameSync – Use Raw `recv_capture_v3` ⭐ Highest Impact

**Approach:** Add a new **`NDIRawVideoChannel`** (or a mode flag on the existing channel) that calls `NDIReceiver.Capture()` directly on a dedicated thread instead of going through `NDIFrameSync.CaptureVideo()`.

**Details:**
- Use `NDIlib_recv_capture_v3` with a reasonable timeout (e.g., 50–100 ms) on a **dedicated video thread**.
- Use a separate `NDIlib_recv_capture_v3` call on a **dedicated audio thread** (as recommended by SDK §7.4).
- When `recv_capture_v3` returns a video frame, push it to the ring-buffer immediately.
- When it returns an audio frame, push those samples immediately.
- Frame pacing becomes the responsibility of the downstream mixer/renderer (which already has clock-based scheduling).

**Fallback:** Keep FrameSync as an option for scenarios where smooth TBC behavior is preferred (e.g., multi-source mixing, variable-framerate sources).

**Expected latency reduction:** **~16–33 ms** (one full frame of video)

### OPT-2: Reduce Pre-Buffer Depth

**Approach:** 
- Reduce audio pre-buffer from 3 chunks to **1** chunk.
- Reduce video pre-buffer from 2 frames to **1** frame (or 0 with a "start on first frame" policy).
- Add a new `UltraLowLatency` preset with ring-buffer capacity of **2** (down from 4).

**Changes:**
- `NDILatencyPreset.cs`: Add `UltraLowLatency = 2`
- `Program.cs`: Change `WaitForAudioBufferAsync(3)` → `WaitForAudioBufferAsync(1)` and `WaitForVideoBufferAsync(2)` → `WaitForVideoBufferAsync(1)` when ultra-low-latency is selected.

**Expected latency reduction:** **~20–45 ms** (startup), **~16–33 ms** (steady-state ring-buffer depth)

### OPT-3: Zero-Copy Video Frame Path

**Approach:** Instead of `Marshal.Copy`-ing the entire frame, keep a reference to the NDI buffer and only free it when the downstream consumer is done with it.

**Details:**
- Wrap the NDI frame pointer in a custom `IMemoryOwner<byte>` / `IDisposable` that calls `NDIFrameSync.FreeVideo()` (or `NDIReceiver.FreeVideo()` for raw mode) on dispose.
- The ring-buffer consumer (VideoMixer / SDL3VideoOutput) disposes the wrapper after uploading to GPU texture.
- This eliminates the `Marshal.Copy` and immediately unblocks the capture thread.

**Caveat:** Must ensure the NDI buffer is not held for too long, or the SDK will run out of internal buffers and drop frames. The ring-buffer capacity acts as a natural bound.

**Expected latency reduction:** **~1–2 ms** (small but removes capture-thread blocking)

### OPT-4: Request Hardware Decode Metadata

**Approach:** After connecting the receiver, send connection metadata requesting hardware decoding:

```csharp
receiver.AddConnectionMetadata("<ndi_video_codec type=\"hardware\"/>");
```

**Details:**
- This is a single API call already exposed in `NDIWrappers.cs` → `NDIReceiver.AddConnectionMetadata()`.
- The sender will negotiate hardware-friendly codec if both sides support it.
- No downside if hardware decoding is unavailable – the sender falls back to software.

**Expected latency reduction:** **~0–5 ms** (content-dependent, bigger impact at high resolutions)

### OPT-5: Reduce PortAudio Buffer Size

**Approach:** Change `framesPerBuffer` from `0` (driver default) to an explicit small value like **128** or **256** samples.

**Details:**
- `PortAudioOutput` constructor: pass `framesPerBuffer: 128` (2.7 ms @ 48 kHz) or `256` (5.3 ms).
- On Linux, ensure ALSA period size matches. May require JACK backend for reliable low-latency.
- Consider adding `suggestedLatency` parameter to `PortAudioOutput` (PA supports `PaStreamParameters.suggestedLatency`).

**Risk:** Too-small buffers cause underruns/glitches on slower systems. Should be configurable.

**Expected latency reduction:** **~5–15 ms**

### OPT-6: Reduce Audio Capture Chunk Size

**Approach:** Reduce `NDIAudioChannel.FramesPerCapture` from `1024` to **256** or **512**.

**Details:**
- Smaller chunks mean audio reaches the ring-buffer sooner.
- Must ensure the downstream `AudioMixer` and `PortAudioOutput` can handle smaller/more-frequent buffers efficiently.
- The FrameSync (or raw capture) will return partial chunks if not enough audio is available, so smaller values just mean more frequent polls.

**Expected latency reduction:** **~5–15 ms**

### OPT-7: Replace `Thread.Sleep` with Spin-Wait or Event-Driven Capture

**Approach:** In the video capture loop, replace `Thread.Sleep(1)` with either:
- `SpinWait` for very short intervals (< 1 ms)
- `Thread.Yield()` + `Stopwatch`-based timing
- Or, in raw-capture mode, just block on `recv_capture_v3` with a timeout (the call itself blocks until a frame arrives).

**Details:**
- For raw capture (OPT-1), this is naturally solved: `recv_capture_v3` blocks until data arrives, so no sleep is needed.
- For FrameSync mode, use `Thread.Yield()` or a hybrid spin with `Thread.SpinWait(n)` for the first ~1 ms and then `Thread.Sleep(0)`.

**Expected latency reduction:** **~1–4 ms** (removes sleep granularity jitter)

### OPT-8: Disable or Use Adaptive VSync ⭐ High Impact

**Approach:** Replace `SDL.GLSetSwapInterval(1)` with adaptive vsync or no vsync for live NDI playback.

**Options (ranked by preference):**
1. **Adaptive vsync** — `SDL.GLSetSwapInterval(-1)`: Behaves like vsync-on for frames arriving on time, but swaps immediately (like vsync-off) when a frame is late. Avoids tearing in the common case while eliminating the worst-case 16.67 ms wait. Not all drivers support this — check the return value.
2. **VSync off** — `SDL.GLSetSwapInterval(0)`: Immediate swap, no blocking. Frame presentation happens as fast as the render loop runs. Risk of screen tearing, but minimizes latency. Acceptable for a monitoring application.
3. **Mailbox mode** (Vulkan) — Not directly available via SDL3 OpenGL, but if the renderer is ever ported to Vulkan, `VK_PRESENT_MODE_MAILBOX_KHR` gives the best of both worlds: no tearing, and the latest frame is always presented.

**Details:**
- Make this configurable per output, e.g. an enum `VsyncMode { On, Off, Adaptive }` on `SDL3VideoOutput`.
- For live NDI monitoring, `Adaptive` or `Off` should be the default.
- For file-based playback, `On` remains the correct default.

**Expected latency reduction:** **~0–16.7 ms** (average ~8 ms at 60 Hz)

### OPT-9: Use NDI Native Audio Interleave Function

**Approach:** Replace the manual scalar `PlanarToInterleaved()` loop in `NDIAudioChannel.cs` with the NDI SDK's built-in `NDIlib_util_audio_to_interleaved_32f_v3`.

**Details:**
- The function is already wrapped: `NDIWrappers.cs` line 823 exposes `NDIAudioHelper.AudioToInterleaved32f()`.
- It takes the planar `NDIAudioFrameV3` and writes interleaved 32-bit float into a provided buffer.
- The SDK implementation is SIMD-optimized and avoids the cache-unfriendly scattered-write pattern of the current managed loop.
- The current managed allocation of `float[]` from the pool and `unsafe` pinning can remain as-is — just call the NDI function instead of the manual transpose.

**Caveat:** The NDI utility function allocates the output buffer internally (via `p_data` on the output struct). Need to check whether it can write into a pre-allocated buffer or if we need to copy out of its allocation (which would negate the benefit). If it allocates internally, wrap the NDI-allocated buffer in a custom `IMemoryOwner<byte>` and free via `NDIlib_util_audio_free`.

**Expected latency reduction:** **~0.1–0.5 ms** (small, but removes a CPU bottleneck for high-channel-count audio)

### OPT-10: Align Presentation Clock Origin at Startup

**Approach:** Instead of latching the PortAudio clock origin on the first render-loop iteration (which is non-deterministic), explicitly set the origin at a well-defined point — ideally immediately after the pre-buffer fill completes and just before playback starts.

**Details:**
- Add a `SetClockOriginNow()` or `ResetOrigin()` method to `SDL3VideoOutput` that captures the current PortAudio clock position as the video timeline origin.
- Call this from `NDIAutoPlayer` immediately before `output.Start()` / starting the mixer.
- This removes the non-deterministic gap between PA stream open and the first render iteration.
- Alternatively, pass the origin timestamp explicitly: `videoOutput.SetClockOrigin(audioOutput.Clock.Position)`.

**Expected latency reduction:** **~5–15 ms** (removes startup timing skew, variable per system)

### OPT-11: Aggressive "Newest Frame" Presentation for Live Sources

**Approach:** Add a `LiveMode` option to `VideoMixer` that bypasses the PTS-based scheduling logic and always presents the newest available frame, dropping all older frames.

**Details:**
- In `LiveMode`, `PresentNextFrame()` would drain the channel until only the last frame remains, then present it unconditionally — ignoring PTS, `LeadTolerance`, and `_dropLagThreshold`.
- This is appropriate for live NDI monitoring where "show the latest picture" is always the right answer, and PTS-based scheduling only adds delay.
- The existing PTS-based mode remains the default for file playback and recorded content.
- `LeadTolerance` of 5 ms is conservative for live content; in live mode it becomes irrelevant.

**Expected latency reduction:** **~5–30 ms** (eliminates PTS-based holding and the interaction with vsync quantization)

### OPT-12: PortAudio `suggestedLatency` — Explicit Low-Latency Value

**Approach:** When `framesPerBuffer` is `0` (driver-chosen), the current code falls through to `device.DefaultLowOutputLatency` for `suggestedLatency`. On many Linux ALSA setups, this is ~20–40 ms. Explicitly set a low `suggestedLatency` (e.g., 5 ms) even when the buffer size is driver-chosen.

**Details:**
- `PortAudioOutput.TryOpenStream()` (line 239): change the fallback from `device.DefaultLowOutputLatency` to a configurable value, defaulting to e.g., `0.005` (5 ms).
- PortAudio will attempt to configure ALSA with period/buffer sizes that meet the requested latency; if the hardware can't support it, PA adjusts upward automatically.
- This is complementary to OPT-5 (explicit `framesPerBuffer`): OPT-5 fixes buffer size, OPT-12 fixes the hint to the driver. Both can be used together.

**Expected latency reduction:** **~5–15 ms** (depends on current driver default vs. requested value)

---

## 4. Implementation Order & Expected Impact

| Priority | Optimization | Est. Latency Saved | Effort | Risk |
|----------|-------------|-------------------|--------|------|
| **1** | OPT-1: Raw `recv_capture_v3` bypass | 16–33 ms | High | Medium – new code path, need A/V sync strategy |
| **2** | OPT-8: Disable/adaptive VSync | 0–16.7 ms (avg ~8 ms) | Low | Low – possible tearing with VSync off |
| **3** | OPT-11: Newest-frame live mode in VideoMixer | 5–30 ms | Medium | Low – opt-in for live sources only |
| **4** | OPT-2: Reduce pre-buffer depth | 20–45 ms (startup) | Low | Low – may underrun on slow systems |
| **5** | OPT-5: Smaller PortAudio buffer | 5–15 ms | Low | Medium – underruns on some hardware |
| **6** | OPT-12: Explicit low `suggestedLatency` | 5–15 ms | Trivial | Low – PA auto-adjusts if unsupported |
| **7** | OPT-6: Smaller audio capture chunks | 5–15 ms | Low | Low – more CPU overhead from frequent calls |
| **8** | OPT-10: Align presentation clock origin | 5–15 ms | Low | None – deterministic startup |
| **9** | OPT-4: Hardware decode metadata | 0–5 ms | Trivial | None (likely already default in SDK v5+) |
| **10** | OPT-3: Zero-copy video frames | 1–2 ms | Medium | Medium – buffer lifetime management |
| **11** | OPT-7: Remove `Thread.Sleep` | 1–4 ms | Low | Low – slightly higher CPU in FrameSync mode |
| **12** | OPT-9: NDI native audio interleave | 0.1–0.5 ms | Trivial | None – drop-in replacement |

### Recommended first pass (quick wins): OPT-8 + OPT-2 + OPT-10 + OPT-12 + OPT-6 + OPT-7 + OPT-9

These are all low-effort changes that together could save **~20–50 ms** with minimal risk. They can be implemented and tested in an afternoon. OPT-8 (adaptive vsync) is the single highest-value quick win — it requires changing one line of code.

### Recommended second pass (medium effort): OPT-11 + OPT-5 + OPT-4

The live-mode VideoMixer (OPT-11) eliminates PTS scheduling overhead for live NDI and pairs well with adaptive vsync. Smaller PortAudio buffers (OPT-5) require testing across target hardware. Hardware decode metadata (OPT-4) is trivial and harmless.

### Recommended third pass (major change): OPT-1 + OPT-3

The raw-capture bypass is the most impactful single change but requires the most work: a new channel implementation, A/V synchronization strategy without FrameSync's TBC, and careful testing. Combined with zero-copy frames, this should close the remaining gap to NDI Tools Monitor.

---

## 5. Risk Notes

1. **A/V Sync without FrameSync:** When bypassing FrameSync, we lose its automatic clock correction. We'll need to rely on NDI frame timestamps (`timecode` or `timestamp` fields) and the existing `NDIClock` to schedule playout. The `NDIClock` already derives from frame timestamps, so this should work, but edge cases (dropped frames, timestamp discontinuities) need handling.

2. **Buffer Underruns:** Reducing buffer sizes (OPT-2, OPT-5, OPT-6, OPT-12) trades robustness for latency. The `UltraLowLatency` preset should be explicitly opt-in, and the player should monitor for underruns and log warnings.

3. **NDI Buffer Lifetime (Zero-Copy):** Holding NDI buffers too long in OPT-3 could starve the SDK's internal pool. The ring-buffer capacity (2–4 in low-latency mode) naturally bounds this, but we should add a safety timeout that force-frees buffers older than N frames.

4. **Linux `Thread.Sleep` Granularity:** On Linux, `Thread.Sleep(1)` may sleep for 1–4 ms due to timer resolution. The `timerfd` or `clock_nanosleep` APIs offer better precision but aren't easily accessible from managed code. For the raw-capture path (OPT-1), this is a non-issue since `recv_capture_v3` blocks internally.

5. **PortAudio Backend:** On Linux, the default ALSA backend may not support very small buffers reliably. For production low-latency use, the JACK backend is recommended. The existing `JackLib` project in the workspace could be leveraged for a dedicated JACK output path.

6. **VSync Tearing (OPT-8):** Disabling vsync (`GLSetSwapInterval(0)`) will cause screen tearing on non-composited displays. Adaptive vsync (`-1`) is preferable but not universally supported — check the return value and fall back gracefully. On Wayland compositors, vsync behavior is compositor-controlled and `GLSetSwapInterval` may be overridden regardless.

7. **Live-Mode Frame Dropping (OPT-11):** Always presenting the newest frame means intermediate frames are never shown. For 30 fps content on a 60 Hz display this is fine (each frame gets ~2 vsync slots anyway). For higher-framerate content or variable-framerate sources, this could cause visible judder if frames are dropped non-uniformly. The live mode should be opt-in and clearly documented.

8. **Presentation Clock Origin (OPT-10):** The current non-deterministic origin latching works "well enough" because the A/V drift correction loop in `NDIAutoPlayer` (30s interval, 50% gain, ±40ms max step) compensates over time. However, fixing the origin alignment removes the need for the first several correction cycles and gives a cleaner startup experience.

---

## Appendix A: NDI SDK References

- **§7.4 – Receiving Video:** Recommends dedicated threads per media type with `recv_capture_v3`, confirms thread-safety. Notes that "modern versions of NDI almost certainly already default to using hardware acceleration."
- **§15.4 – Frame Synchronization:** Documents FrameSync as a TBC with internal hysteresis; transforms push sources into pull sources. Explicitly designed for vsync-driven playback: "A very common application of the frame-synchronizer is to display video on screen timed to the GPU v-sync."
- **§7.4.1 – Hardware Acceleration:** `<ndi_video_codec type="hardware"/>` metadata enables GPU decode negotiation, but likely already the default in SDK v5+.
- **`NDIlib_recv_color_format_fastest` (= 100):** Already used as default in `NDIReceiverSettings`.
- **`NDIlib_util_audio_to_interleaved_32f_v3`:** SDK-provided SIMD-optimized planar→interleaved conversion. Already wrapped in `NDIWrappers.cs` but not used by `NDIAudioChannel`.

---

## Appendix B: Estimated Latency Budget (Current vs. Target)

| Pipeline Stage | Current Estimate | After Quick Wins | After Full Optimization |
|---|---|---|---|
| FrameSync TBC | 16–33 ms | 16–33 ms | **0 ms** (OPT-1) |
| VSync quantization | 0–16.7 ms (avg ~8) | **0–1 ms** (OPT-8) | 0–1 ms |
| Pre-buffer fill | 30–65 ms startup | **10–20 ms** (OPT-2) | 10–20 ms |
| Ring-buffer depth | 0–16 ms | 0–8 ms | 0–8 ms |
| VideoMixer PTS scheduling | 0–21 ms | 0–21 ms | **0 ms** (OPT-11) |
| PortAudio output buffer | 10–21 ms | **5–10 ms** (OPT-5/12) | 2.7–5.3 ms |
| Audio capture chunk | 21 ms | **5–10 ms** (OPT-6) | 5–10 ms |
| Marshal.Copy (video) | 1–2 ms | 1–2 ms | **0 ms** (OPT-3) |
| PlanarToInterleaved (audio) | 0.1–1 ms | **~0.05 ms** (OPT-9) | ~0.05 ms |
| Clock origin skew | 5–30 ms | **~0 ms** (OPT-10) | ~0 ms |
| Thread.Sleep jitter | 1–4 ms | **0–1 ms** (OPT-7) | 0 ms (OPT-1) |
| **Estimated total** | **~85–210 ms** | **~40–105 ms** | **~18–45 ms** |

*Note: These are rough estimates. Many sources overlap (e.g., vsync + PTS scheduling), so the actual reduction may differ. The "After Full Optimization" target of ~18–45 ms should be competitive with NDI Tools Monitor.*

---

## Appendix C: Quick-Start API — `NDIPlaybackProfile`

All latency-related knobs are pre-configured by choosing one of the four
`NDIEndpointPreset` values.  Two objects carry the configuration:

| Object | Scope | Created by |
|---|---|---|
| `NDISourceOptions` | Source/capture side (queue depth, capture chunk size, polling mode) | `NDISourceOptions.ForPreset(preset, channels)` |
| `NDIPlaybackProfile` | Output side (VSync, live-mode, pre-buffer, audio latency) | `NDIPlaybackProfile.For(preset)` |

### Preset Summary

| Property | Safe | Balanced | LowLatency | UltraLow |
|---|---|---|---|---|
| AudioFramesPerCapture | 1024 | 1024 | 256 | 128 |
| AudioSuggestedLatency | 0 (default) | 0 | ~5.3 ms | ~2.7 ms |
| AudioPreBufferChunks | 4 | 3 | 1 | 1 |
| VideoPreBufferFrames | 3 | 2 | 1 | 1 |
| VideoLiveMode | ✗ | ✗ | ✓ | ✓ |
| AdaptiveVSync | ✗ | ✗ | ✓ | ✓ |
| ResetClockOrigin | ✗ | ✗ | ✓ | ✓ |
| LowLatencyPolling | ✗ | ✗ | ✓ | ✓ |

### Minimal Consumer Code

```csharp
var preset  = NDIEndpointPreset.LowLatency;
var profile = NDIPlaybackProfile.For(preset);
var options = NDISourceOptions.ForPreset(preset, channels: 2);

// 1. Open NDI source — all capture knobs are set automatically
var avSource = await NDIAVChannel.OpenByNameAsync(name, options, ct);

// 2. Open audio output — just pass the profile's suggested latency
output.Open(device, hwFmt, suggestedLatency: profile.AudioSuggestedLatency);

// 3. Configure video output
if (profile.AdaptiveVSync)
    videoOutput.VsyncMode = VsyncMode.Adaptive;
videoOutput.Open(title, w, h, fmt);
videoOutput.OverridePresentationClock(output.Clock);
if (profile.ResetClockOrigin)
    videoOutput.ResetClockOrigin();

// 4. Configure mixer
avMixer.VideoLiveMode = profile.VideoLiveMode;

// 5. Pre-buffer with profile-recommended depths
await Task.WhenAll(
    avSource.WaitForAudioBufferAsync(profile.AudioPreBufferChunks, ct),
    avSource.WaitForVideoBufferAsync(profile.VideoPreBufferFrames, ct));

// 6. Start playback
await output.StartAsync();
await videoOutput.StartAsync();
```

All preset values can be overridden via `with { … }` on the `NDIPlaybackProfile`
record if the consumer needs fine-grained control.

