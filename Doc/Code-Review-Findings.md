# MFPlayer — Code Review Findings

Date: 2026-04 (review pass against current workspace)
Scope: All modules under Audio/, Media/, NDI/, Video/, MIDI/, OSC/, Test/
Reference docs consulted: Doc/AVMixer-Refactor-Plan.md, Clone-Sinks.md, MediaPlayer-Guide.md, Quick-Start.md, Usage-Guide.md, README.md

---

## 1. Executive Summary

Top priorities (in order of user impact):

1. **`AVRouter.GetAvDrift` compares incompatible time domains** (critical). It subtracts an audio “elapsed-consumed” position from a video stream-PTS position, producing values that are off by a constant (stream start PTS) and that sawtooth by ±½ video-frame period. This is the root cause of the wrong-looking AVDrift in MFPlayer.NDISender. See §2.
2. **Sub-frame video drift correction in `PushVideoTick` is applied in the wrong direction for late frames** and interacts badly with the catch-up loop (high). See §2.3.
3. **Push-video endpoints never dispose the incoming `VideoFrame.MemoryOwner`** (high) — `NDIAVSink.ReceiveFrame`, `SDL3VideoCloneSink.ReceiveFrame`, `AvaloniaOpenGlVideoCloneSink.ReceiveFrame`, and the two cache slots inside `AVRouter` all drop rented `ArrayPool<byte>` buffers without returning them. This starves the pool under load. See §3.
4. **`NDIVideoChannel` ring violates its own `SingleReader = true` contract** — the capture thread calls `TryRead` to implement drop-oldest while the render thread also calls `TryRead` via `FillBuffer` (high). See §3.
5. **Per-tick heap allocations in video hot paths** (`new VideoFrame[1]`, `HashSet<EndpointId>`, LINQ over `_endpoints.Values`, `GetOrAdd` closures, `FirstOrDefault`) (medium). See §4.
6. **`NDIAVSink` audio de-interleave allocates `planar` with channel-contiguous layout every buffer that exceeds initial capacity, and grows into the heap rather than using a pool** (medium). See §4.
7. **Architectural**: router, sinks and decoders each implement their own drift/pending/origin logic. The AVMixer-Refactor-Plan explicitly aims to remove this duplication; today the same concepts exist in four places (medium). See §6.

---

## 2. NDISender AVDrift / sub-frame drift — deep dive

The user reports that the “A-V: {x}ms” readout in `MFPlayer.NDISender` is wrong, and that sub-frame drift correction “seems broken.” The root causes are in `AVRouter`, `FFmpegAudioChannel`, and `FFmpegVideoChannel`; they are independent of the NDI module itself.

### 2.1 What the readout shows

`Test/MFPlayer.NDISender/Program.cs:322`
```csharp
var drift = router.GetAvDrift(audioInputId.Value, videoInputId.Value);
parts.Add($"A-V: {drift.TotalMilliseconds:+0;-0}ms");
```

`Media/S.Media.Core/Routing/AVRouter.cs:489–497`
```csharp
public TimeSpan GetAvDrift(InputId audioInput, InputId videoInput)
{
    ...
    return aEntry.AudioChannel!.Position - vEntry.VideoChannel!.Position;
}
```

### 2.2 Why the readout is meaningless (Critical bug)

- `FFmpegAudioChannel.Position` (`FFmpegAudioChannel.cs:63–64`)
  ```csharp
  public TimeSpan Position =>
      TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);
  ```
  `_framesConsumed` is a counter of **samples pulled out of the ring** (incremented in `FillBuffer`). It starts at zero at `Start()`, monotonically increases, and is unrelated to the stream’s container PTS.

- `FFmpegVideoChannel.Position` (`FFmpegVideoChannel.cs:83–84`, written in `FillBuffer` at `:399`)
  ```csharp
  public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
  ...
  Volatile.Write(ref _positionTicks, vf.Pts.Ticks);
  ```
  This is the **container PTS** of the most recently dequeued video frame.

Consequences of subtracting them:

1. **Constant offset when stream start-PTS ≠ 0.** Any container where the first video PTS is not zero (HLS segments, MP4 with edit lists, MOV, some transport streams) produces a constant multi-second offset that never converges. Audio `Position` begins at 0, video `Position` begins at e.g. 5.000 s → AV drift reads −5 000 ms forever.

2. **Sawtooth of ±½ video-frame period even in a perfect file.** The push video tick (`AVRouter.cs:928`) runs at 10 ms cadence (the `InternalTickCadence`). A 29.97 fps source produces a new PTS every 33.4 ms. So between any two consecutive video PTS updates, audio Position advances ~33 ms while video Position is frozen at the previous frame’s PTS. The displayed drift therefore crawls from roughly −33 ms back to 0 ms and jumps again each time `PushVideoTick` actually dequeues a new frame.

3. **Audio Position advances at wall-clock rate in the push-audio path, not at stream rate.** In `PushAudioTick` (`AVRouter.cs:803–926`), `framesPerBuffer = fmt.SampleRate * elapsedSeconds + accum`. That gives audio Position a precise wall-clock growth, but it is *not* the PTS of the audio actually sent. If the decoder underruns, audio stops advancing (because `FillBuffer` returns fewer frames and only credits what was pulled — `FFmpegAudioChannel.cs:276`), but video Position still advances when frames dequeue, so drift readouts invert transiently. If the decoder races ahead, audio Position grows at exactly the Stopwatch rate while video PTS steps by true frame durations → the two are not directly comparable.

### 2.3 Recommended fix for AVDrift

Stop comparing a sample-counter to a stream PTS. Give both channels a **single, comparable time domain**: the PTS of the last sample/frame actually delivered from the ring.

**Minimal change** — propagate PTS on audio:

1. Add a `long StartPtsTicks` field to `FFmpegAudioChannel.AudioChunk` (`FFmpegAudioChannel.cs:17–27`).
2. In `ConvertFrame()` (`FFmpegAudioChannel.cs:229`), compute:
   ```csharp
   double tbSeconds = _stream->time_base.num / (double)_stream->time_base.den;
   var framePts = FFmpegVideoChannel.SafePts(_frame->pts, tbSeconds);
   return new AudioChunk(outBuf, writtenSamples, framePts.Ticks);
   ```
3. Replace `_framesConsumed` with `_positionTicks` and update it in `FillBuffer` based on the current chunk’s start PTS + offset within the chunk:
   ```csharp
   long startTicks = chunk.StartPtsTicks;
   long offsetTicks = (long)((_currentOffset / channels) * TimeSpan.TicksPerSecond / SourceFormat.SampleRate);
   Volatile.Write(ref _positionTicks, startTicks + offsetTicks);
   ```
4. Report `Position = TimeSpan.FromTicks(_positionTicks)`.

After that change, `GetAvDrift` subtracts two container-PTS values and reports the *actual* A-V offset.

**Reduce quantization noise on video** — expose an interpolated position:

Add on `IVideoChannel`:
```csharp
TimeSpan NextExpectedPts { get; }  // PTS of the next frame queued in the ring, or last-presented + frame period
```

Then `GetAvDrift` can read the interpolated video position as:
```
videoPos = Clamp(audioClock, lastFramePts, nextFramePts)
```
…or, more simply, the router can expose **the clock-domain drift** directly, using the master clock as reference:

```csharp
public TimeSpan GetAvDrift(InputId audio, InputId video)
{
    var clock = Clock.Position;
    var aOff  = audio.Position - clock;      // how far audio is ahead of the master clock
    var vOff  = video.Position - clock;      // how far video is ahead of the master clock
    return aOff - vOff;                      // audio-leads-video when positive
}
```

Because both offsets are noisy with the same clock, the diff cancels a lot of the wall-clock jitter. Pair this with an **EMA** inside `GetAvDrift` (one-pole IIR, α ≈ 0.1) so the UI readout is stable.

### 2.4 Sub-frame drift correction — concrete root cause in PushVideoTick

`AVRouter.cs:1031–1041`
```csharp
// Smooth drift correction applied AFTER catch-up, using the
// PTS of the frame that will actually be presented. ...
double pushGain = _options.VideoPushDriftCorrectionGain;
if (pushGain > 0)
{
    long errorTicks = relativePtsTicks - relativeClockTicks;
    drift.PtsOriginTicks += (long)(errorTicks * pushGain);
}
```

Problems:

1. **No dead-band.** `errorTicks` is the quantized sub-frame error, which oscillates between roughly 0 and +(frame-period − earlyTolerance) every frame because the gate rejects anything > tolerance and anything ≤ 0 is accepted. The mean is roughly +½ frame period, biased positive. Every tick adds `gain × errorTicks` to the origin, which produces a **steady ramp of `PtsOriginTicks`** equal to `~0.5 × frame_period × pushGain × fps`. At 30 fps and default gain 0.03, that is `~0.5 ms/s` of artificial origin drift — the video timeline slowly slides forward relative to clock until the catch-up loop starts skipping, at which point it whips back. Classic limit-cycle oscillation.

   **Fix**: introduce a dead-band equal to half the tolerance:
   ```csharp
   long deadBandTicks = _options.VideoPtsEarlyTolerance.Ticks / 2;
   long magnitude = Math.Abs(errorTicks);
   if (magnitude > deadBandTicks)
   {
       long signedErr = errorTicks - Math.Sign(errorTicks) * deadBandTicks;
       drift.PtsOriginTicks += (long)(signedErr * pushGain);
   }
   ```

2. **Wrong direction on late frames.** When `errorTicks < 0` (frame’s PTS is behind the clock), the update `PtsOriginTicks += negative` makes subsequent frames’ `relativePts = pts − origin` *larger*, i.e. appear **more early** relative to the clock — the opposite of what “catch up” wants. The catch-up loop at `:1005–1029` does the right thing at a higher level by skipping frames, but the drift integrator then fights it. In bursty decoder scenarios this produces “catch-up, then over-wait, then catch-up again” stutter.

   **Fix**: only integrate while within tolerance and while the frame was accepted *without* a catch-up skip. Track a `bool didCatchUp` and skip the integrator when true. Alternatively, apply an asymmetric gain: `gain_pos = gain_neg / 2` so late-frame corrections are slower.

3. **`relativePtsTicks` is not refreshed after the catch-up loop promotes `candidate`.** Re-reading lines `:1026–1027` shows it *is* updated inside the loop (`relativePtsTicks = nextRelPts`), so that part is fine — but the comment at `:1031–1035` is the only thing keeping this correct. A subtle follow-up bug waiting to happen.

4. **`_pushVideoPending` loses a frame on `UnregisterInput` during catch-up.** `AVRouter.cs:250` removes `_pushVideoPending[inp.Id]` but does not dispose the `VideoFrame.MemoryOwner`, leaking one pool rental per teardown. Low severity but a real leak.

5. **`LastVideoFrame` cross-endpoint share is subtly broken.** `AVRouter.cs:937–943` clears `entry.LastVideoFrame` at the *top* of `PushVideoTick`, but `VideoPresentCallbackForEndpoint.TryPresentNext` (`:1257`) writes it after pulling a frame from its own call — and that call happens on a different thread (the render thread of a pull endpoint). A pull-endpoint render tick occurring *before* the push tick copies that frame into its own endpoint is fine, but if it occurs *after* the push clear-phase, the frame is cleared before the push endpoint reads it, then pulled *again* by the push endpoint’s own `FillBuffer` → same frame delivered twice via NDI, followed by a pull-endpoint frame skip. Low-probability but correctness-relevant under concurrent push+pull sharing.

### 2.5 Summary of recommended changes to fix the reported issue

- `FFmpegAudioChannel.Position` → report stream-PTS, not sample-counter (see §2.3 code sketch).
- Optionally add `NextExpectedPts` to `IVideoChannel` or change `GetAvDrift` to a clock-relative form with an EMA.
- In `PushVideoTick`:
  - Add a dead-band around 0 before integrating drift.
  - Do not integrate when the catch-up loop fired.
  - Consider reducing default `VideoPushDriftCorrectionGain` from 0.03 to 0.01 once the dead-band is in place; with a dead-band equal to half the tolerance, the feedback is stable at the current gain.
- Dispose `VideoFrame.MemoryOwner` when evicting pending frames (see §3.1).

---

## 3. Bugs and correctness issues

### 3.1 Push-video endpoints leak `ArrayPool<byte>` rentals (High)

- **Location**: `AVRouter.cs:945–1046` (`PushVideoTick`, no `MemoryOwner.Dispose()` anywhere); `NDI/S.Media.NDI/NDIAVSink.cs:297–332` (`ReceiveFrame` copies to pool, never disposes inbound frame); `Video/S.Media.SDL3/SDL3VideoCloneSink.cs:126–150` and `Video/S.Media.Avalonia/AvaloniaOpenGlVideoCloneSink.cs` equivalent — they copy inbound to their *own* pool and `Dispose` the **previous** `_latestFrame?.MemoryOwner`, but never the **incoming** one.
- **Description**: Every `VideoFrame` produced by `FFmpegVideoChannel.ConvertFrame` (`:205–355`) carries an `ArrayPoolOwner<byte>` wrapping a rented buffer. `PushVideoTick` does `ep.Video.ReceiveFrame(in candidate)` and then overwrites `inp.LastVideoFrame = candidate;` without disposing anything. None of the push sinks dispose it either. `ArrayPool<byte>.Shared` does not track unreturned buffers, so these silently become GC-managed allocations — pool gains nothing, and under sustained load the LOH gets blown up (4K RGBA ≈ 33 MB per frame).
- **Recommended fix**:
  - Establish ownership rule in `Media/S.Media.Core/Media/VideoFrame.cs` doc: “push endpoints become owners of the frame; they must `Dispose` `MemoryOwner` once they’re done.”
  - `NDIAVSink.ReceiveFrame`, `SDL3VideoCloneSink.ReceiveFrame`, `AvaloniaOpenGlVideoCloneSink.ReceiveFrame`: add `frame.MemoryOwner?.Dispose();` after the copy.
  - `AVRouter.PushVideoTick` `:1044–1045`:
    ```csharp
    var previous = inp.LastVideoFrame;
    inp.LastVideoFrame = candidate;
    ep.Video.ReceiveFrame(in candidate);
    // Push endpoints that retain must clone; the router's cache retains one reference
    // so previous must be released here.
    if (previous.HasValue && !ReferenceEquals(previous.Value.MemoryOwner, candidate.MemoryOwner))
        previous.Value.MemoryOwner?.Dispose();
    ```
    And at end of tick, after fan-out is complete, dispose the final cached frame when it won’t be used by a pull endpoint anymore.
  - `AVRouter.UnregisterInput` `:250`: `if (_pushVideoPending.TryRemove(id, out var pending)) pending.MemoryOwner?.Dispose();`
  - `AVRouter.VideoPresentCallbackForEndpoint._pendingFrame` assignment at `:1238`: dispose any previous pending before overwriting.

### 3.2 `NDIVideoChannel` violates `SingleReader = true` (High)

- **Location**: `NDI/S.Media.NDI/NDIVideoChannel.cs:90–98` declares `SingleReader = true`, but both the capture thread (`EnqueueFrame` at `:455–470`) and the RT consumer (`FillBuffer` at `:412–427`) call `_ringReader.TryRead(...)`.
- **Description**: `SingleReader = true` allows `System.Threading.Channels` to elide synchronization in the reader fast-path. Concurrent `TryRead` from two threads is UB per the contract; today it happens to work because `Channel.CreateUnbounded` with `SingleReader = true` still uses an internally thread-safe data structure, but this is an implementation detail. Also, the drop-oldest path reads one item and disposes it — if the RT thread concurrently reads the very same item, the dispose races with the consumer presenting the frame.
- **Recommended fix**: set `SingleReader = false`, or move the drop-oldest logic to a non-reader mechanism (sentinel field, use `BoundedChannelFullMode.DropOldest` and leak the buffer to GC for now, or keep a dedicated `ConcurrentQueue<VideoFrame>` with explicit capacity tracking and `Interlocked.CompareExchange` to claim the eviction slot).

### 3.3 `FFmpegVideoChannel.FillBuffer` decrements ring count even when `_framesDequeued == 0` but ring is empty, then raises underrun (Medium)

- **Location**: `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:392–408`.
- **Description**: `BufferUnderrun` only fires when `filled == 0` AND at least one frame has been dequeued historically. However `_framesDequeued` is incremented per dequeue *inside the loop*, so on the very first empty-ring call after startup the event is suppressed — good. But during seek/flush, `ApplySeekEpoch` (`:197–203`) clears the ring without touching `_framesDequeued`; after the seek the next call with empty ring fires an underrun even though this is the expected post-seek state.
- **Recommended fix**: reset `_framesDequeued = 0` in `ApplySeekEpoch` and `Seek`.

### 3.4 `FFmpegAudioChannel.ApplySeekEpoch` does not reset `_framesConsumed` (Medium)

- **Location**: `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:166–176`.
- **Description**: `Seek` (`:324–335`) *does* reset `_framesConsumed`, but `ApplySeekEpoch` — called from the decode worker when it processes a flush packet — does not. After a seek, the UI’s audio `Position` is temporarily wrong until the first `FillBuffer` from the new epoch actually consumes frames. For NDI timecode computation in `NDIAVSink` this can cause a wrong audio timecode on the very first buffer following a seek.
- **Recommended fix**: take an optional `seekPositionTicks` argument in `ApplySeekEpoch` (already in the signature) and set `_framesConsumed = (long)(seekPositionTicks / TimeSpan.TicksPerSecond * SourceFormat.SampleRate)`.

### 3.5 `FFmpegAudioChannel.FillBuffer` partial-fill semantics are asymmetric (Medium)

- **Location**: `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:257–306`.
- **Description**: On the underrun branch (`:268–288`) the method returns `consumed` (an int count of whole frames actually delivered). On the complete branch (`:303–305`) it returns `frameCount` (the requested count). But the dest span was zeroed only in the underrun case. If an earlier partial chunk was delivered and then the final chunk fills the remainder exactly, the logic is correct; but if the underrun path fires mid-loop after writing `filled > 0`, the caller sees a partial fill with trailing zeros — good. There is no real bug here, but the code is brittle: the caller contract on return value is ambiguous (“frames actually filled” vs. “frames requested, remainder zeroed”). The router treats the return value as “frames actually filled” at `AVRouter.cs:878–879`.
- **Recommended fix**: document on `IAudioChannel.FillBuffer` explicitly that the return value is the number of frames with non-silent content, and that the remainder up to `frameCount` is zeroed.

### 3.6 `AVRouter.PushAudioTick` dead stackalloc and O(E·R) scan (Medium)

- **Location**: `Media/S.Media.Core/Routing/AVRouter.cs:811`.
- **Description**:
  ```csharp
  Span<EndpointId> seenEps = stackalloc EndpointId[0]; // will use list below
  var processedEndpoints = new HashSet<EndpointId>();
  ```
  `seenEps` is never used (dead). `processedEndpoints` is allocated **every tick** (~100× per second). For every endpoint the method scans *all* routes twice (once for format, once for mixing). Complexity per tick is O(E·R).
- **Recommended fix**: delete the dead span. Replace the hash-set with a small `stackalloc Span<EndpointId>` (endpoints are registered via `EndpointId.New()`; count is tiny in practice). Build a per-endpoint route list on `RebuildAudioRouteSnapshot` and drop the inner scan: `_audioRoutesByEndpoint[ep.Id]`.

### 3.7 `AVRouter.PushVideoTick` allocates `VideoFrame[1]` per iteration (Medium)

- **Location**: `AVRouter.cs:970`, `:1007`, `:1207`.
- **Description**: Three separate `new VideoFrame[1]` allocations per video tick per input. At 100 Hz tick × 4 max catch-up × 2 call sites, that’s ~800 short-lived arrays per second for a single video input.
- **Recommended fix**: `Span<VideoFrame> oneFrame = stackalloc VideoFrame[1];` — `VideoFrame` is a `readonly record struct` so it’s stack-allocatable. `FillBuffer(Span<VideoFrame>, int)` already takes a `Span<>`.

### 3.8 `NDIAVSink.AudioWriteLoop` reallocates `planar` on heap (Medium)

- **Location**: `NDI/S.Media.NDI/NDIAVSink.cs:842, 860–861`.
- **Description**: `planar` starts as `new float[framesPerBuffer × channels]`. If `ReceiveBuffer` submits more frames than `_audioFramesPerBuffer` in a single call (happens when the drift corrector or resampler inflates the count, or when the upstream uses a larger buffer), the loop does `planar = new float[planarNeed]` — a GC allocation on a real-time thread.
- **Recommended fix**: use `ArrayPool<float>` with rent/return, or compute a safe upper bound at construction (`_audioFramesPerBuffer × channels × audioPreset.BufferHeadroomMultiplier`).

### 3.9 `NDIAVSink.ReceiveFrame` queue depth check is racy (Medium)

- **Location**: `NDI/S.Media.NDI/NDIAVSink.cs:302–306`.
- **Description**:
  ```csharp
  if (Volatile.Read(ref _videoPendingFrames) >= _videoMaxPendingFrames) { drop; return; }
  ...
  _videoPending.Enqueue(...);
  Interlocked.Increment(ref _videoPendingFrames);
  ```
  Between the check and the increment, multiple producers (unlikely in current architecture but permitted by `IVideoEndpoint`) can overshoot by N. Not catastrophic because the queue is unbounded, but the “max pending frames” cap becomes a soft guideline.
- **Recommended fix**: use a single `Interlocked.Increment`-then-check-then-decrement-on-reject pattern, or move to a `BoundedChannel<PendingVideo>` with `FullMode.DropWrite`.

### 3.10 `NDIAVSink.ReceiveBuffer` does not dispose pooled buffer on zero write path correctly (Low)

- **Location**: `NDIAVSink.cs:375–380`.
- **Description**: On `writtenSamples <= 0` the buffer is re-enqueued into `_audioPool`. Correct, but note that `SinkBufferHelper.CopySameRate` may have clobbered it — fine because next user will re-fill. More importantly the calling code incremented `_audioPoolMiss` sometimes but not here (`_audioCapacityMissDrops` is used — mis-named).

### 3.11 `NDIAvTimingContext.ReserveAudioTimecode` only seeds from video if video was observed first (Medium)

- **Location**: `NDI/S.Media.NDI/NDIAvTimingContext.cs:23–49`.
- **Description**: When the first call is `ReserveAudioTimecode` and no video PTS has ever been observed (audio-only stream or audio arriving before the first frame), it seeds with 0. If video later arrives with PTS=T, audio timecodes will be at t=0 while video timecodes are at t=T — they diverge permanently. Note that `ObserveVideoPts` only seeds once (`:19–20`), so even if video arrives second, it no longer re-aligns.
- **Recommended fix**: for audio-only we want 0-seeded monotonic timecode (fine). For mixed streams, do *not* seed audio until the first video PTS is seen, or, if audio must flow first, defer the first audio send until `_latestVideoPtsTicks` is non-`long.MinValue`. Alternatively, expose a `ResetFromVideo` so a later first-video-PTS can re-origin the audio timeline (acceptable because NDI receivers tolerate a timecode discontinuity if it’s before any audio is heard).

### 3.12 `FFmpegVideoChannel.Seek` writes `_positionTicks` but does not clear `_framesDequeued` (Low)

- **Location**: `FFmpegVideoChannel.cs:422–428`.
- **Description**: Same as §3.3.

### 3.13 `NDIClock.Position` is not thread-safe (Medium)

- **Location**: `NDI/S.Media.NDI/NDIClock.cs:20–21, 66–72`.
- **Description**: `Position` reads `_lastFramePosition`, `_sw.Elapsed`, `_swAtLastFrame` with no synchronization. `UpdateFromFrame` writes the first two and the third with no synchronization. Torn reads on 32-bit platforms (and reordering on any platform) are possible; `_running` (plain `bool`) is read without volatile.
- **Recommended fix**: mark `_running` volatile; guard the three fields with `Volatile.Read/Write` on the `.Ticks` `long` representation, or use a lock (cheap relative to NDI capture rate).

### 3.14 `FFmpegVideoChannel` keeps `_rgbFrame` but never uses it (Low — dead code)

- **Location**: `FFmpegVideoChannel.cs:31, 173, 509`.
- **Description**: `_rgbFrame` is allocated and freed but never referenced elsewhere.
- **Recommended fix**: delete.

### 3.15 `FFmpegVideoChannel.ConvertFrame` plane-stride math is wrong for odd width (Medium)

- **Location**: `FFmpegVideoChannel.cs:235–248` (`Yuv420p` path).
- **Description**: Uses `w / 2` for UV stride/size. For odd `w`, `sws_scale` actually writes `(w+1)/2` bytes per chroma row (FFmpeg aligns up). With `w=1919, h=1080`, UV row stride should be 960 bytes, not 959. The buffer may be undersized by `h` bytes and `sws_scale` writes out of bounds. Same issue in `Yuv420p10` at `:321–325` and in the `Nv12` chroma size at `:254`.
- **Recommended fix**: use `(w + 1) / 2` and `(h + 1) / 2` throughout chroma sizing (matching `NDIAVSink.GetVideoBufferBytes` at `:457–460`, which already does it correctly).

### 3.16 `FFmpegAudioChannel.ConvertFrame` uses `_codecCtx->ch_layout` input layout but `_frame->ch_layout.nb_channels` for output sizing (Low)

- **Location**: `FFmpegAudioChannel.cs:231–233`.
- **Description**: Most sources produce the same channel count on context and frame, but decoders can emit mono from a stereo stream (or vice versa) for transient frames. The `SourceFormat` reported by the channel is the *context*’s layout, so downstream sinks may mix with the wrong channel count for a burst.
- **Recommended fix**: detect `_frame->ch_layout.nb_channels != SourceFormat.Channels` and either reinit `_swr` to down/upmix to the declared `SourceFormat`, or drop the frame with a warning.

### 3.17 `StreamAvioContext` lifetime (not re-read here) — audit (Medium)

- **Location**: `Media/S.Media.FFmpeg/StreamAvioContext.cs`.
- **Description**: Not reviewed in detail; `avio_context_free` must be paired with freeing the underlying internal buffer (FFmpeg may realloc it). Confirm the dispose path frees `ctx->buffer` after `avio_context_free`.

### 3.18 `NDIAVSink.Dispose` `_cts?.Cancel()` called from finalizer-equivalent path without thread exit wait on drain phase (Low)

- **Location**: `NDIAVSink.cs:413–443`.
- **Description**: After `_videoThread?.Join(2s)` / `_audioThread?.Join(2s)`, the code drains queues and returns buffers to the pool. If a thread did not exit within 2 s (e.g. blocked in `_sender.SendVideo` because the native NDI send has an internal wait), the drain proceeds concurrently with the still-running thread, corrupting the pool enqueue/dequeue.
- **Recommended fix**: increase the timeout or abort, mark the sink as `_disposed = true` before `Cancel` so the loop checks won’t re-enter `SendVideo`, and reconsider the 2-s timeout — the NDI SDK can genuinely take several seconds on slow networks.

### 3.19 `NDIAVSink.StopAsync` reads `_started` with `Interlocked.CompareExchange` then `Join`s with `ct` but cancellation doesn’t propagate to join (Low)

- **Location**: `NDIAVSink.cs:285–295`. Marginal, but `Task.Run(() => _videoThread?.Join(...), ct)` is fine.

### 3.20 `FFmpegLoader.EnsureLoaded` double-locked init in `NDIAVSink.EnsureFfmpegLoaded` is redundant (Low)

- **Location**: `NDIAVSink.cs:474–493`.
- **Description**: `FFmpegLoader.EnsureLoaded` is already idempotent with its own lock in that project; wrapping it in another `lock + state` pair is redundant and mis-reports failure (`sFfmpegLoadState = -1` is sticky for the process lifetime even if the caller later installs libraries).

### 3.21 `NDIVideoChannel` synthetic PTS is shared with real PTS, creating discontinuity (Low)

- **Location**: `NDIVideoChannel.cs:173–190`.
- **Description**: When a real timestamp stops advancing or jumps, the code switches to `GetSyntheticPtsSeconds()`. But the synthetic clock starts whenever it’s first touched, so the synthetic PTS can be **behind** the last real PTS on the first switch, causing a backward time step — and the mixer logic upstream treats that as invalid.
- **Recommended fix**: initialize `_syntheticClock.Restart()` with an offset equal to the last real PTS when switching modes.

---

## 4. Performance / optimization opportunities

### 4.1 `AVRouter.PushVideoTick` & `VideoPresentCallbackForEndpoint` — stack-alloc the 1-frame buffer

See §3.7.

### 4.2 `AVRouter.PushAudioTick` — replace hot-path allocations

- `new HashSet<EndpointId>()` per tick (`AVRouter.cs:812`): replace with a small stack-array or add a pre-sized pooled set.
- `_endpoints.Values` enumerator (`:814`): switch to a `ConcurrentDictionary.Values` snapshot array kept alongside the dictionary, refreshed on register/unregister.
- `foreach (var route in routes) { ... }` inside the endpoint loop: build `RouteEntry[] routesByEndpoint[i]` alongside `_audioRouteSnapshot` — O(1) look-up, no filtering per tick.

### 4.3 `AVRouter.ApplyChannelMap` — inner loop bound check

`AVRouter.cs:1289–1310`: `if (dstCh < dstChannels)` is redundant if `BakeRoutes` guarantees the invariant; drop the check inside the tight loop. Worth measuring with a benchmark.

### 4.4 `FFmpegVideoChannel.ConvertFrame` — ArrayPoolOwner allocation per frame

`FFmpegVideoChannel.cs:217–218`: `new ArrayPoolOwner<byte>(rented)` per frame. This is ~32 bytes × frame-rate. At 60 fps that’s ~115 KB/min into Gen0. Not huge, but combined with §3.1 (never returned) it matters. Consider a small object pool for `ArrayPoolOwner<byte>` keyed by the rented array reference.

### 4.5 `FFmpegVideoChannel` sws scratch arrays are instance-field but `byte*[]` — cannot be reused across frames safely

`FFmpegVideoChannel.cs:35–38`: the arrays `_srcDataArr`, `_dstDataArr` are filled and immediately consumed by `sws_scale`, and no other thread uses them (decode loop is single-threaded). Fine. But the `switch` at `:233–351` duplicates all eight planar cases. A small helper `static void FillPlaneDescriptors(PixelFormat, int w, int h, byte* pBuf, byte*[] data, int[] stride)` cuts this to one line per case.

### 4.6 `NDIAVSink` video pool sizing mismatch causes buffer oscillation

`NDIAVSink.cs:177–183`: `bytes = Math.Max(srcBytes, dstBytes)`. The pool is pre-seeded with this max size, but the convert path (`:720–755`) rents a *second* buffer from `ArrayPool<byte>.Shared` for I210→RGBA conversion. Under sustained conversion this is a separate allocation pattern that bypasses the pool. Consider adding a second pool for the post-convert buffer sized for `_videoTargetFormat`, or always rent post-convert from `ArrayPool<byte>.Shared` and drop the pre-seeded pool entirely.

### 4.7 `NDIAVSink.VideoWriteLoop` BGRA/RGBA not byte-swapped fast-path

`NDIAVSink.cs:697`: if source is BGRA and target is RGBA (or vice versa), the code falls through to the default converter path (`_videoConverter.Convert`). A simple SIMD byte swap would avoid full sws_scale. Relevant for captures from software decoders that emit BGRA but NDI prefers RGBA or vice versa.

### 4.8 `NDIVideoChannel.CopyPacked/Nv12/I420` use `Marshal.Copy` row-by-row

`NDIVideoChannel.cs:321–410`: row-by-row `Marshal.Copy` is slow when `srcStride != rowBytes`. For GPU-captured NDI frames the stride typically matches so the fast path (line 330) fires, but for stride-padded frames consider `Buffer.MemoryCopy` via `NativeMemory` or `Unsafe.CopyBlockUnaligned` which outperform `Marshal.Copy` by ~2–3× for small rows.

### 4.9 `AVRouter.GetDiagnosticsSnapshot` LINQ + lock

`AVRouter.cs:506–524`: holds `_lock` while running three LINQ `Select().ToArray()` passes. Diagnostics shouldn’t block route mutation. Build snapshots from the copy-on-write arrays (`_audioRouteSnapshot`, `_videoRouteSnapshot`) and cached endpoint/input snapshot arrays that live alongside the `ConcurrentDictionary`s.

### 4.10 `PushThreadLoop` sleep granularity

`AVRouter.cs:759–767`: uses `Thread.Sleep(sleepMs)` which on Linux has ~1–4 ms granularity unless `timerBeginPeriod` equivalent is active. For a 10 ms tick this is ±10–40 % jitter, feeding directly into the audio frame count jitter. Consider the spin-wait pattern from `NDIVideoChannel.cs:226–236` for the last ~3 ms.

---

## 5. Simplification / refactor opportunities

### 5.1 Unify the two PTS drift state machines

`AVRouter.PushVideoDriftState` (private nested class, `:147–154`) and `VideoPresentCallbackForEndpoint._hasOrigin / _ptsOriginTicks / _clockOriginTicks` (`:1160–1162`) implement the *same* drift integrator with slightly different code. Extract a `PtsDriftTracker` struct with `TryOriginShift(pts, clock, gain, tolerance, deadband)` returning the pass/fail + corrected origin. Share between pull and push paths. This also lets the AV-Router expose `GetInputPtsDrift(InputId)` — a clean fix for the AVDrift readout (§2.3) that doesn’t require touching `FFmpegAudioChannel`.

### 5.2 Consolidate the “pending frame + last frame + drop oldest” trio

The same pattern appears in four places: `AVRouter._pushVideoPending`, `NDIVideoChannel` ring, `SDL3VideoCloneSink._latestFrame`, `AvaloniaOpenGlVideoCloneSink._latestFrame`. Extract a single `VideoFrameSlot` primitive (atomic swap + dispose of displaced).

### 5.3 The three “decode-loop error reporting” overloads on `IDecodableChannel`

`FFmpegAudioChannel.ReportDecodeLoopError` and `FFmpegVideoChannel.ReportDecodeLoopError` do the same structured log, just with a different Logger category. Collapse to `FFmpegDecodeWorkers.Log` and drop the interface member.

### 5.4 `NDIAVSink` audio + video thread pair is the same pattern as `PortAudioSink` write thread

`NDIAVSink.VideoWriteLoop` / `AudioWriteLoop` / `PortAudioSink.WriteLoop` all do: wait on a `SemaphoreSlim`, dequeue from a `ConcurrentQueue`, process, release pool buffer. Extract a `PooledWorkQueue<TWork>` with `Enqueue(TWork)`/`TryDequeue(out TWork)` and a hosted processing callback.

### 5.5 `AVRouterOptions` doc says default is “PI at 0.03” — comment drift

`AVRouterOptions.cs:59–61` describes `VideoPushDriftCorrectionGain = 0.03` as “corrects an 8 ms drift in ~40 frames at 60 fps.” With §2.4 the semantic changes; update doc.

### 5.6 `VirtualClockEndpoint` only wraps a `StopwatchClock` and a bool

`Media/S.Media.Core/Media/Endpoints/VirtualClockEndpoint.cs` — 70 lines wrapping a 40-line `StopwatchClock` and discarding audio. Could be a 15-line sealed class by making `StopwatchClock` directly implement `IAudioEndpoint` + `IClockCapableEndpoint`. Probably not worth it, but flag.

### 5.7 `NDIAVSink` has two constructors — one takes 13 params, one takes `NDIAVSinkOptions`

`NDIAVSink.cs:131–247`: the verbose ctor is the canonical one; the options ctor delegates. Delete the verbose overload; pass `NDIAVSinkOptions` everywhere. `Test/MFPlayer.NDISender/Program.cs:184–189` already creates the sink via the verbose ctor — migrate the sample to options.

### 5.8 Remove legacy `RunAudioAsync`/`RunVideoAsync` wrappers

`FFmpegDecodeWorkers.cs:129–141`: the comments say “legacy wrappers.” Both callers (`FFmpegAudioChannel.StartDecoding` and `FFmpegVideoChannel.StartDecoding`) can call the generic `RunAsync<T>` directly.

### 5.9 `AVRouter.CreateRoute(..., AudioRouteOptions)` and `AVRouter.CreateRoute(..., VideoRouteOptions)` vs. the generic one

`AVRouter.cs:337–376`: three overloads. If `AudioRouteOptions` and `VideoRouteOptions` shared a common marker interface (`IRouteOptions` with `Audio` / `Video` discriminator), the single overload would handle both. Minor API cleanup.

### 5.10 `CopyOnWriteArray<T>` appears unused

`Media/S.Media.Core/CopyOnWriteArray.cs`: grep this file — if it’s not referenced anywhere, drop it. The router hand-rolls its own copy-on-write at `:117–118, 709–717`.

---

## 6. API & architectural observations

### 6.1 AVMixer-Refactor-Plan Phase 1 appears partially executed

The plan calls for the new endpoint hierarchy (§Phase 1). The codebase does have `IAudioEndpoint / IVideoEndpoint / IAVEndpoint`, and `AVRouter` forwards to them without owning a leader format — good. However:

- `IPullAudioEndpoint` still coexists with push endpoints and has a separate `FillCallback` injection point (`AVRouter.cs:563–578`). This matches the plan but the plan’s endpoint unification has not removed the three control flows (pull via callback, push via tick, push via RT-from-hardware). Consider following the plan’s §6 (“Push/pull duality handled inside endpoint”) to fold pull-audio into the same `ReceiveBuffer` contract, with the callback inversion living behind the endpoint rather than in the router.
- `IFormatCapabilities<T>` exists and is consulted (`AVRouter.cs:589, 654`) but only emits warnings. Consider adding strict / preferred-format negotiation so the router can avoid sending frames the endpoint will drop anyway.

### 6.2 `FFmpegAudioChannel` vs. `NDIAudioChannel` — divergent `Position` semantics

`FFmpegAudioChannel.Position` is sample-counter based (§2.2). `NDIAudioChannel.Position` is the same shape (`_framesConsumed / SampleRate`, `NDIAudioChannel.cs:65–66`). That consistency is good *within* the channel family, but since video channels use stream-PTS, the router cannot give a meaningful AV drift without §2.3.

### 6.3 Endpoint ownership of incoming `VideoFrame`

Undefined in code or docs. The core issue in §3.1. Add a line to `IVideoEndpoint.ReceiveFrame`’s XML doc: “Implementations take ownership of `frame.MemoryOwner`; they must `Dispose` it once they are finished with the pixel data. Implementations that need the frame past the call MUST copy and dispose the inbound owner before returning.”

### 6.4 Naming inconsistency: `IAudioChannel`/`IVideoChannel` vs. `IMediaChannel<T>`

`Media/S.Media.Core/Media/IMediaChannel.cs` defines a generic contract, but `IAudioChannel` and `IVideoChannel` don’t just use `IMediaChannel<T>` — they add domain-specific members (`AudioFormat SourceFormat`, `VideoFormat SourceFormat`, `FillBuffer`, `SuggestedYuv*`). Fine, but `FillBuffer(Span<float>, int)` is declared on `IAudioChannel` and `FillBuffer(Span<VideoFrame>, int)` on `IVideoChannel` — they could both specialize `IMediaChannel<T>.FillBuffer(Span<T>, int)`.

### 6.5 Test project organization

Tests are under `Test/` for unit tests plus sample programs under the same folder. Consider separating `Test/Samples/` (MFPlayer.* executables) from `Test/Unit/` (S.Media.Core.Tests, S.Media.FFmpeg.Tests). Minor.

### 6.6 Central package management is consistent

`Directory.Packages.props` pins versions for all packages, all projects target `net10.0`, nullable + implicit usings are enabled almost everywhere. Good.

### 6.7 `PortAudioSink` / `JackSink` / `NDIAVSink` audio paths all re-implement the drift+rate-ratio compute

`SinkBufferHelper.ComputeWriteFrames` exists but only `NDIAVSink` uses it (`NDIAVSink.cs:342–344`). Audit `Audio/S.Media.PortAudio/PortAudioSink.cs` to confirm it uses the helper; if not, migrate.

---

## 7. Minor / nits

1. `Program.cs:311` (NDISender): `new List<string>` allocated every 100 ms for the status line. Use a pooled `StringBuilder` or a fixed-size array of strings.
2. `NDIAVSink.cs:67` uses `Lock` (C# 13 `System.Threading.Lock`). Good. Elsewhere the codebase mixes `object _lock = new()` with `Lock _lock = new()` — standardize on `Lock`.
3. `FFmpegVideoChannel.cs:141–144`: `ReportDecodeLoopError` stores `ep.ActualLength` but the `EncodedPacket` may have been returned to the pool by the time `finally` runs; currently the error branch sits before the `finally`, so it’s safe, but fragile.
4. `NDIAVSink.cs:58–62`: static `IReadOnlyList<PixelFormat>` arrays typed as `PixelFormat[]` via collection-expression `[...]`. Consider `ImmutableArray<PixelFormat>` for clarity; equivalent cost.
5. `AVRouter.cs:129`: `_disposed` not volatile; races against `DisposeAsync` check at `:530`. Mark volatile or use `Interlocked.Exchange(ref _disposed, true)` returning the old value.
6. `StopwatchClock.cs:27–30`: the `sampleRate` parameter is accepted and ignored. Remove the overload or document that the parameter is for compatibility only.
7. `FFmpegAudioChannel.cs:313–320`: `WriteAsync`/`TryWrite` throw `NotSupportedException` on an `IAudioChannel` push path. Consider splitting `IAudioChannel` into `IPullAudioChannel` (no push members) and `IWritableAudioChannel` (push) to remove the throwing stubs.
8. `NDIAVSink.cs:799–800`: `lock (_sendLock) _sender.SendVideo(vf);` — the pointer `p` is inside `fixed`; good. But the `_sendLock` is shared between audio and video; at high rates a large RGBA send can block audio. Consider separating into `_videoSendLock` and `_audioSendLock`, because NDI SDK’s `NDIlib_send_send_video_v2` is thread-safe per sender; some bindings guard it for safety — confirm against `NDILib/Native.cs`.
9. `OSCLib/OSCRouter.cs` and `PMLib/Devices/*`: not deeply reviewed; spot-check for the same ownership issues (buffers from `ArrayPool` not returned on async cancellation paths).
10. `Doc/Quick-Start.md` and `Doc/Usage-Guide.md` reference `AttachAudioOutput` still in places (pre-refactor). Sync with current router-based API.
11. `Video/S.Media.Avalonia/README.md` exists while other projects have none — either drop it or add README to each project for consistency.
12. `FFmpegLoader` global static path (`ffmpeg.RootPath = "/lib";` in `Program.cs:35`) is hard-coded to Linux. Move to a helper or config.

---

## Severity key

- **Critical**: visible bug in a supported scenario; produces wrong results.
- **High**: resource leak, race, or correctness issue that degrades over time.
- **Medium**: sub-optimal behavior or edge-case bug; not visible on happy path.
- **Low**: style, dead code, minor nit.

---

## 8. Action Checklist

Progress tracking for fixes. Grouped by priority so the NDISender drift issue is resolved first.

### Priority A — NDISender AVDrift & sub-frame drift (user-reported)

- [x] A1. Propagate stream PTS through `FFmpegAudioChannel.AudioChunk` (`StartPtsTicks`) and report `Position` as interpolated stream-PTS (§2.3).
- [x] A2. Add `NextExpectedPts` to `IVideoChannel` and `FFmpegVideoChannel` for interpolated video position (§2.3).
- [x] A3. Rework `AVRouter.GetAvDrift` to use clock-relative positions + one-pole EMA filter (§2.3).
- [x] A4. Add dead-band (~tolerance/2) around zero in `PushVideoTick` drift integrator (§2.4 #1).
- [x] A5. Skip drift integration on ticks where the catch-up loop fired (`didCatchUp`) (§2.4 #2).
- [x] A6. Re-documented `VideoPushDriftCorrectionGain` / `VideoPullDriftCorrectionGain` XML comments to describe the dead-band + catch-up gating behaviour; default gain 0.03 kept per §2.4 since dead-band now stabilises the feedback (§5.5).
- [x] A7. Apply the same dead-band + catch-up gating to `VideoPresentCallbackForEndpoint` pull-path drift integrator (§5.1).

### Priority B — Resource leaks & correctness (High)

- [x] B1. Define ownership contract on `IVideoEndpoint.ReceiveFrame` XML doc (router owns the `MemoryOwner`; endpoints copy if they retain) (§6.3).
- [x] B2. `NDIAVSink.ReceiveFrame` copies into its own pooled buffer; under the new contract the router disposes the inbound owner (§3.1).
- [x] B3. `SDL3VideoCloneSink.ReceiveFrame` copies into its own pooled buffer (§3.1).
- [x] B4. `AvaloniaOpenGlVideoCloneSink.ReceiveFrame` copies into its own pooled buffer (§3.1).
- [x] B5. Dispose displaced `VideoFrame.MemoryOwner` in `AVRouter.PushVideoTick` fan-out and in `_pushVideoPending` eviction (§3.1, §2.4 #4).
- [x] B6. Dispose previous pending in `VideoPresentCallbackForEndpoint._pendingFrame` reassignment (§3.1).
- [x] B7. Dispose `LastVideoFrame` on `AVRouter.UnregisterInput` / router `DisposeAsync` (§3.1).
- [x] B8. Fix `NDIVideoChannel` `SingleReader = true` violation — set `SingleReader = false` (drop-oldest eviction requires the capture thread to also read) (§3.2).
- [x] B9. Reset `_framesDequeued` in `FFmpegVideoChannel.ApplySeekEpoch` / `Seek` to suppress spurious underrun (§3.3, §3.12).
- [x] B10. Reset `_framesConsumed` / `_currentChunkStartPtsTicks` in `FFmpegAudioChannel.ApplySeekEpoch` using `seekPositionTicks` (§3.4).
- [x] B11. Fix chroma stride math to `(w+1)/2 × (h+1)/2` in `FFmpegVideoChannel.ConvertFrame` for Yuv420p / Yuv420p10 / Nv12 (§3.15).

### Priority C — Correctness (Medium)

- [x] C1. Documented partial-fill contract on `IMediaChannel<T>.FillBuffer` (non-silent frame count + required zero-fill of remainder) (§3.5).
- [x] C2. Make `NDIClock` thread-safe (`volatile` on `_running`, `Interlocked.Read/Exchange` on tick fields) (§3.13).
- [x] C3. Fix `NDIAvTimingContext` audio-before-video seeding so mixed streams don't diverge (CAS re-origin on first video PTS) (§3.11).
- [x] C4. Harden `NDIAVSink.ReceiveFrame` pending-queue cap against racy producers (atomic reserve-slot pattern) (§3.9).
- [x] C5. Detect transient channel-count mismatch in `FFmpegAudioChannel.ConvertFrame` (drop frame + warning log) (§3.16).
- [x] C6. Initialize `NDIVideoChannel._syntheticClock` with last-real-PTS offset on switch (§3.21).
- [x] C7. `StreamAvioContext.Dispose` audited — `av_free(_avioCtx->buffer)` + null + `avio_context_free` is correct (§3.17).
- [x] C8. `NDIAVSink.Dispose` marks `_disposed = true` and `_started = 0` before `Cancel`, bumps Join timeout to 5 s (§3.18).

### Priority D — Performance

- [x] D1. `VideoFrame[1]` hot-path allocations replaced with lazy-init per-instance buffers (one total, not per-tick) (§4.1 / §3.7).
- [x] D2. Removed dead `stackalloc` and redundant `HashSet<EndpointId>` in `PushAudioTick` (dictionary iteration already dedups by key) (§3.6, §4.2).
- [x] D3. Maintain `_audioRoutesByEndpoint` cache rebuilt alongside `_audioRouteSnapshot` / `_videoRouteSnapshot`; `PushAudioTick` now picks its per-endpoint route list in O(1) instead of scanning the full snapshot per endpoint (§4.2).
- [x] D4. Use `ArrayPool<float>` for `NDIAVSink.AudioWriteLoop` `planar` buffer (§3.8).
- [x] D5. Removed `new List<string>` per-tick allocation in NDISender status line (§7 #1).
- [x] D6. Lock-free `AVRouter.GetDiagnosticsSnapshot` — reads from the `ConcurrentDictionary.Values` enumerators (eventually consistent, never blocks route mutation) (§4.9).
- [x] D7. Hybrid spin+sleep `WaitUntil` helper in `PushAudioThreadLoop` / `PushVideoThreadLoop` — coarse sleep to 3 ms before deadline, spin the tail (§4.10).

### Priority E — Simplification & dead code

- [x] E1. Extracted shared `PtsDriftTracker` class (`Media/S.Media.Core/Routing/PtsDriftTracker.cs`) used by both push (`AVRouter.PushVideoTick`) and pull (`VideoPresentCallbackForEndpoint`) paths — encapsulates origin seeding, relative-PTS/clock arithmetic, and the dead-band drift integrator in one place (§5.1).
- [x] E2. Delete unused `_rgbFrame` in `FFmpegVideoChannel` (§3.14).
- [x] E3. Deleted unused `CopyOnWriteArray<T>` (no external references) (§5.10).
- [x] E4. Collapsed legacy `RunAudioAsync`/`RunVideoAsync` wrappers in `FFmpegDecodeWorkers`; callers use the generic `RunAsync<T>` directly (§5.8).
- [x] E5. Removed duplicate `sFfmpegLoadLock` / `sFfmpegLoadState` state in `NDIAVSink.EnsureFfmpegLoaded`; delegates directly to the already-idempotent `FFmpegLoader.EnsureLoaded` (§3.20).
- [x] E6. Extracted `VideoFrameSlot` primitive (`Media/S.Media.Core/Video/VideoFrameSlot.cs`) — atomic set/peek/take/clear with automatic dispose of the displaced frame. Applied to `SDL3VideoCloneSink._latestFrame` and `AvaloniaOpenGlVideoCloneSink._latestFrame` (the two symmetric consumers). `AVRouter._pushVideoPending` keeps its per-input `ConcurrentDictionary<InputId, VideoFrame>` because the push-video thread is the single writer/reader there — adding a locked slot would introduce unnecessary contention on the RT path (§5.2).
- [x] E7. Extracted `PooledWorkQueue<T>` primitive (`Media/S.Media.Core/PooledWorkQueue.cs`) — wraps the `ConcurrentQueue<T>` + `SemaphoreSlim` + `Interlocked` depth-counter triplet with an atomic `TryReserveSlot`/`EnqueueReserved` path for bounded producers. Replaced the three hand-rolled instances in `NDIAVSink` (video + audio) and `PortAudioSink` (§5.4).

### Priority F — API / architecture

- [x] F1. Updated Quick-Start / Usage-Guide docs to router-based API (dropped all `AttachAudioOutput` / `AVMixer` references in favour of `AVRouter.RegisterInput` / `RegisterEndpoint` / `CreateRoute`) (§7 #10).
- [x] F2. Marked `AVRouter._disposed` volatile; `Dispose` / `DisposeAsync` publish it *before* `StopAsync` so in-flight work can observe the teardown (§7 #5).
- [x] F3. Split `NDIAVSink._sendLock` into `_videoSendLock` + `_audioSendLock` — verified against NDI SDK §13 which guarantees frames may be submitted "at any time, off any thread, and in any order" (§7 #8).
- [x] F4. Split `IAudioChannel` into pull base + `IWritableAudioChannel` push-capable subtype; `FFmpegAudioChannel` now implements only `IAudioChannel` (no throwing push stubs). Producer-side code that pushes PCM data takes the narrower `IWritableAudioChannel` (§7 #7).
- [x] F5. Removed `StopwatchClock(double sampleRate, …)` ignored-parameter overload (§7 #6).
- [x] F6. Added `FFmpegLoader.ResolveDefaultSearchPath()` with `MFPLAYER_FFMPEG_PATH` env-var override + OS defaults; test samples now use it instead of a hard-coded `"/lib"` (§7 #12).

### Priority G — Nits

- [x] G1. Standardized on `System.Threading.Lock` (migrated `MediaClockBase._tickLock` and `AvaloniaOpenGlVideoOutput._stateLock`; only one intentional `object?` reference remains in `OSCLib/OSCTypes` — that one is a typed payload holder, not a monitor lock) (§7 #2).
- [x] G2. Migrated `NDIAVSink` preferred-format lists from `IReadOnlyList<PixelFormat>` to `ImmutableArray<PixelFormat>` (§7 #4).
- [x] G3. Added README files to the five consumer-facing library projects (`S.Media.Core`, `S.Media.FFmpeg`, `S.Media.NDI`, `S.Media.SDL3`, `S.Media.PortAudio`) describing purpose + key types (§7 #11). *(low-level interop wrappers `NDILib`/`PALib`/`JackLib`/`PMLib`/`OSCLib` intentionally left README-less — they're consumed only via the `S.Media.*` layers and their API is exposed through the parent project's README.)*
- [x] G4. Removed `IDecodableChannel.ReportDecodeLoopError` duplication — logging consolidated into `FFmpegDecodeWorkers.RunAsync<T>` with its own Logger category and the same `kind` tag ("Audio"/"Video") used elsewhere in the loop (§5.3).
- [x] G5. Added `IRouteOptions` marker interface on `AudioRouteOptions` / `VideoRouteOptions` plus a default-interface `CreateRoute(InputId, EndpointId, IRouteOptions)` dispatch on `IAVRouter`, keeping the strongly-typed overloads for type-safe call sites (§5.9).
- [x] G6. Confirmed — `PortAudioSink` already uses `SinkBufferHelper.ComputeWriteFrames` at line 207; no `JackSink` class exists in this codebase so nothing to migrate there (§6.7).
