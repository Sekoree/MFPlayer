# MFPlayer API and Implementation Audit (2026-04-12)

## Scope and method

This review focused on correctness, hot-path performance, and API ergonomics across:

- Core mixing and endpoints (`Media/S.Media.Core`)
- Decode pipeline (`Media/S.Media.FFmpeg`)
- Audio/video outputs and sinks (`Audio/S.Media.PortAudio`, `Video/S.Media.SDL3`, `NDI/S.Media.NDI`)
- Public-facing usage examples (`Test/*Player`)
- Adjacent transport libs (`OSC/OSCLib`, `MIDI/PMLib`)

I prioritized issues that can cause runtime failures, memory growth, or sustained perf loss in real-time pipelines.

---

## Findings (ordered by severity)

## Critical

### 1) `DropOldest` queues can leak pooled video frame buffers

- **Where:** `NDI/S.Media.NDI/NDIVideoChannel.cs:44-53`, `NDI/S.Media.NDI/NDIVideoChannel.cs:139`; `Media/S.Media.Core/Video/BufferedVideoFrameEndpoint.cs:29-35`, `Media/S.Media.Core/Video/BufferedVideoFrameEndpoint.cs:56`
- **Problem:** `Channel<VideoFrame>` is configured with `BoundedChannelFullMode.DropOldest`. `VideoFrame` often carries pooled memory (`MemoryOwner`). When the channel drops an old frame internally, no one disposes that dropped frame's `MemoryOwner`.
- **Impact:** Long-running streams can accumulate unreturned `ArrayPool<byte>` rentals, increasing retained memory and eventual allocation pressure.
- **Fix:** Replace `DropOldest` with explicit bounded queues that dispose dropped frames, or wrap write-path overflow handling to manually dequeue + dispose before enqueue.

## High

### 2) Per-frame managed allocations in FFmpeg audio decode hot path

- **Where:** `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:204-227`
- **Problem:** `ConvertFrame()` allocates `new float[samples * channels]` for every decoded frame, plus `trimmed` reallocation when `written < samples`.
- **Impact:** High GC churn under continuous decode (especially multi-stream). This is a primary hot path.
- **Fix:** Use pooled `float[]` chunks and return-on-consume strategy (similar to existing pooled video path), or persistent reusable decode buffers with copy-on-enqueue only when required.

### 3) Per-frame managed allocations in FFmpeg video conversion hot path

- **Where:** `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:202-203`, `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:216-247`
- **Problem:** Source/destination pointer arrays are allocated on each frame conversion (`new[]` arrays in `ConvertFrame`).
- **Impact:** Avoidable GC load at frame rate frequency.
- **Fix:** Switch to `stackalloc`-based pointer/stride arrays for fixed-size 4-plane FFmpeg calls.

### 4) Audio mixer sink routing lookup is quadratic in the RT callback

- **Where:** `Media/S.Media.Core/Mixing/AudioMixer.cs:381-398`
- **Problem:** For each channel and each sink, the mixer linearly scans `slot.SinkRoutes` to find the matching target.
- **Impact:** Cost grows quickly with channel/sink count; this is on the RT path (`FillOutputBuffer`).
- **Fix:** Precompute direct sink-indexed route tables during route updates (copy-on-write), so RT loop is O(1) per sink lookup.

### 5) Busy-spin writer loops can consume CPU when idle

- **Where:** `Audio/S.Media.PortAudio/PortAudioSink.cs:199-213`, `NDI/S.Media.NDI/NDIAVSink.cs:570-574`, `NDI/S.Media.NDI/NDIAVSink.cs:727-731`
- **Problem:** Threads call `Thread.Yield()` in a tight loop when queues are empty.
- **Impact:** Increased idle CPU and scheduler contention.
- **Fix:** Use `Channel<T>`/`BlockingCollection<T>` or wait handles to block until work arrives.

## Medium

### 6) Duplicate sink registration allowed in `AggregateOutput` user-facing API

- **Where:** `Media/S.Media.Core/Audio/AggregateOutput.cs:88-107`
- **Problem:** `AddSink()` appends without checking duplicates. Mixer registration is idempotent, but `_sinks` list contains duplicates.
- **Impact:** `StartAsync/StopAsync/Dispose` can run multiple times on the same sink instance, causing lifecycle inconsistencies.
- **Fix:** Enforce uniqueness by reference in `AddSink()` (mirror mixer idempotency).

### 7) API ergonomics friction: mixer access split across concrete vs interface usage

- **Where:** `Media/S.Media.Core/Video/IVideoOutput.cs` vs concrete usage in `Test/MFPlayer.VideoPlayer/Program.cs:213-215`, `Test/MFPlayer.VideoMultiOutputPlayer/Program.cs:112-114`
- **Problem:** Most orchestration needs mixer access, but `IVideoOutput` does not expose `Mixer`; samples rely on concrete type (`SDL3VideoOutput`).
- **Impact:** Harder to write backend-agnostic app code.
- **Fix:** Add an interface-level mixer accessor (or an adapter/factory that returns output + mixer pair) to avoid concrete-type coupling.

### 8) Public API complexity is higher than necessary for common app startup

- **Where:** End-to-end setup in `Test/MFPlayer.VideoPlayer/Program.cs`, `Test/MFPlayer.MultiOutputPlayer/Program.cs`, `Test/MFPlayer.NDISender/Program.cs`
- **Problem:** Typical playback requires many manual steps (decoder open, output open, channel add, route maps, sink registration, start ordering, diagnostics wiring).
- **Impact:** Steeper adoption for API consumers and higher misconfiguration risk.
- **Fix:** Provide a high-level `PlayerBuilder` / scenario presets (single output, multi-output, NDI forwarder) that internally wires mixers/routes with optional advanced override points.

## Low

### 9) Logging to console in library hot/runtime paths is inconsistent

- **Where:** Multiple files, e.g. `Media/S.Media.FFmpeg/FFmpegDecoder.cs:254-265`, `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:151-153`
- **Problem:** Direct `Console.WriteLine/Error.WriteLine` in library code mixes concerns and complicates host integration.
- **Impact:** Noisy output and harder structured telemetry.
- **Fix:** Standardize on `ILogger` across all libs, preserving current message content/verbosity levels.

---

## Positive observations

- RT-path safety intent is strong and explicit in docs/comments (especially mixer and PortAudio callback boundaries).
- Good use of copy-on-write snapshots for routing (`AudioMixer`) to keep RT path lock-free.
- Diagnostics coverage is already substantial in video and NDI paths, which will help validating optimizations.
- FFmpeg seek epoch design is thoughtful and protects against stale packets after seek.

---

## Priority fix plan

1. **Fix frame-owner leaks on drop policies** (Critical).
2. **Remove decode-path per-frame allocations** in `FFmpegAudioChannel` and `FFmpegVideoChannel` (High).
3. **Reduce RT complexity in `AudioMixer` sink route lookup** (High).
4. **Replace spin loops with blocking queue waits** in sink writer threads (High).
5. **Harden API ergonomics** via higher-level builder/presets and sink dedupe (Medium).

---

## Suggested validation after fixes

- Add stress tests for 30+ minute playback with memory snapshots (focus: pooled buffer return stability).
- Add microbenchmarks for:
  - audio decode allocations/frame
  - video conversion allocations/frame
  - mixer callback CPU with varying channel/sink fanout
- Add regression tests for duplicate `AggregateOutput.AddSink()` behavior.
- Add end-to-end tests that run against interface-only wiring (no concrete `SDL3VideoOutput` assumptions).

