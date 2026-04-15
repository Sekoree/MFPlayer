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

---

## 4. Implementation Order & Expected Impact

| Priority | Optimization | Est. Latency Saved | Effort | Risk |
|----------|-------------|-------------------|--------|------|
| **1** | OPT-1: Raw `recv_capture_v3` bypass | 16–33 ms | High | Medium – new code path, need A/V sync strategy |
| **2** | OPT-2: Reduce pre-buffer depth | 20–45 ms (startup) | Low | Low – may underrun on slow systems |
| **3** | OPT-5: Smaller PortAudio buffer | 5–15 ms | Low | Medium – underruns on some hardware |
| **4** | OPT-6: Smaller audio capture chunks | 5–15 ms | Low | Low – more CPU overhead from frequent calls |
| **5** | OPT-4: Hardware decode metadata | 0–5 ms | Trivial | None |
| **6** | OPT-3: Zero-copy video frames | 1–2 ms | Medium | Medium – buffer lifetime management |
| **7** | OPT-7: Remove `Thread.Sleep` | 1–4 ms | Low | Low – slightly higher CPU in FrameSync mode |

### Recommended first pass (quick wins): OPT-2 + OPT-4 + OPT-5 + OPT-6 + OPT-7

These are all low-effort changes that together could save **~15–35 ms** with minimal risk. They can be implemented and tested in an afternoon.

### Recommended second pass (major change): OPT-1 + OPT-3

The raw-capture bypass is the most impactful single change but requires the most work: a new channel implementation, A/V synchronization strategy without FrameSync's TBC, and careful testing. Combined with zero-copy frames, this should close the remaining gap to NDI Tools Monitor.

---

## 5. Risk Notes

1. **A/V Sync without FrameSync:** When bypassing FrameSync, we lose its automatic clock correction. We'll need to rely on NDI frame timestamps (`timecode` or `timestamp` fields) and the existing `NDIClock` to schedule playout. The `NDIClock` already derives from frame timestamps, so this should work, but edge cases (dropped frames, timestamp discontinuities) need handling.

2. **Buffer Underruns:** Reducing buffer sizes (OPT-2, OPT-5, OPT-6) trades robustness for latency. The `UltraLowLatency` preset should be explicitly opt-in, and the player should monitor for underruns and log warnings.

3. **NDI Buffer Lifetime (Zero-Copy):** Holding NDI buffers too long in OPT-3 could starve the SDK's internal pool. The ring-buffer capacity (2–4 in low-latency mode) naturally bounds this, but we should add a safety timeout that force-frees buffers older than N frames.

4. **Linux `Thread.Sleep` Granularity:** On Linux, `Thread.Sleep(1)` may sleep for 1–4 ms due to timer resolution. The `timerfd` or `clock_nanosleep` APIs offer better precision but aren't easily accessible from managed code. For the raw-capture path (OPT-1), this is a non-issue since `recv_capture_v3` blocks internally.

5. **PortAudio Backend:** On Linux, the default ALSA backend may not support very small buffers reliably. For production low-latency use, the JACK backend is recommended. The existing `JackLib` project in the workspace could be leveraged for a dedicated JACK output path.

---

## Appendix: NDI SDK References

- **§7.4 – Receiving Video:** Recommends dedicated threads per media type with `recv_capture_v3`, confirms thread-safety.
- **§15.4 – Frame Synchronization:** Documents FrameSync as a TBC with internal hysteresis; best for "process faster than real-time" scenarios.
- **§7.4.1 – Hardware Acceleration:** `<ndi_video_codec type="hardware"/>` metadata enables GPU decode negotiation.
- **`NDIlib_recv_color_format_fastest` (= 100):** Already used as default in `NDIReceiverSettings`.

