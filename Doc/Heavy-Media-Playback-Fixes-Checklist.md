## Heavy-Media Playback Fixes — Implementation Checklist

Tracks the fix-out for the issues filed in `Doc/TODOs.md` (4K60 yuv422p10 ProRes
local file as the primary repro). Numbered to match the original TODO list.

> **Status:** Phases 1–7 landed. Phase 3's "wire `LocalVideoOutputRoutingPolicy`
> in `MediaPlayer`" sub-task stays cancelled (see Phase 3 notes). All
> `S.Media.Core.Tests` (226) and `S.Media.FFmpeg.Tests` (78) pass. Whole-solution
> rebuild clean, zero warnings.

### Summary of what changed

| Issue | Fix |
| ----- | --- |
| **#1** YUV422p10 → "BGRA32" in HUD | `HudStats` now reports `Yuv422p10 (passthrough)` when input == output pixel format and `Yuv422p10 (GPU YUV->RGB)` when the GPU shader does the conversion. No more misleading "shader" label. |
| **#1** decoder forces BGRA32 | `FFmpegDecoderOptions.VideoTargetPixelFormat` default flipped from `Bgra32` to `null` (source native). Avalonia + SDL3 endpoints already negotiated YUV passthrough at route time, but the decoder-level default was forcing CPU `sws_scale` regardless. |
| **#2** lock render fps to source fps | New `LimitRenderFpsToSource` setting in `AppSettings` + Settings UI checkbox. Fans out to every `VideoEndpointModel`. Per-endpoint setter is now reactive (toggling at runtime triggers a reschedule). |
| **#3** drift HUD reads "9000ms" forever | (a) `VideoPtsClock.MaxInterpolationLead` cap, auto-enabled by `MediaPlayer.OnActiveClockChanged` when the master is a `VideoPtsClock`. (b) HUD `drift:` line uses smart formatting (`+9.00s` instead of `+9000.0ms`). (c) New unsmoothed `frame age:` line shows the real lag. (d) `OverridePresentationClock` is now identity-guarded so registration churn doesn't reset the drift origin. |
| **#4** EOF never fires under backpressure | (a) New `VideoOverflowPolicy.DropOldestUnderStall` — wait briefly for jitter, evict on sustained pressure. Default for pull endpoints. (b) Demux `WriteAsync` has a 2 s watchdog that drops packets instead of stalling. (c) `PacketQueueDepth` default lowered from 64 to 32 (faster EOF surfacing). (d) `RaiseSourceEndedAfterDrainAsync` now drains video for video-only sessions. |
| **#5** A/V desync under stress | (a) New `ISupportsLateFrameDrop` interface implemented by `FFmpegVideoChannel` skips non-key frames at decode time when they're past their deadline. (b) `MediaPlayer` drift correction loop pushes both the master clock hint and a deadline derived from observed drift to the channel. (c) Drift correction `MaxStepMs` raised from 8 ms to 25 ms and `MaxAbsOffsetMs` from 250 ms to 2000 ms so the loop can actually converge multi-hundred-ms drifts. SPlayer settings page slider range raised to match. |
| Bonus | New `IVideoEndpointDiagnosticsSink` lets the player fan upstream drop counters into the endpoint HUD; Avalonia endpoint implements it. `IVideoChannel.SubscriptionDroppedFrames` aggregates router-side eviction counts. |
| Phase 7 | Hot-path polish landed: `sws_getCachedContext` cached on `(w,h,srcFmt)`; `avg_frame_rate` preferred over `r_frame_rate` (VFR pacing fix); `RefCountedVideoBuffer.Rent` pooling removes the per-frame wrapper allocation; `PtsDriftTracker` hot-path reads no longer take the seed/reset lock; `FFmpegPixelFormatConverter.TrySwsScale` no longer double-zeroes the plane pointers; `IVideoEndpointInputFormatHint` lets the HUD `src:` line populate from frame zero; new `MediaPlayer.BufferStalled` event surfaced as a status badge in SPlayer; `BasicPixelFormatConverter`'s scalar YUV paths kept as a documented portability fallback (vectorisation skipped). |

### Phase 1 — UI / HUD wording (no behaviour change) ✅

- [x] **#1 (HUD)** `AvaloniaOpenGlVideoEndpoint`: when `_outputFormat.PixelFormat`
      differs from the actual delivered frame format, mirror the frame format
      into the HUD-facing output so the overlay reports `Yuv422p10 (passthrough)`
      instead of `Yuv422p10 -> Bgra32 (shader)`. Keep `Open(format)` unchanged
      so external callers' framebuffer choice is still respected, but track an
      auxiliary `HudPassthroughFormat` for the HUD path.
      *(Implemented as `HudStats.ToLines` rewording — no auxiliary field
      needed. The framebuffer's pixel format is no longer reported on its own
      line; the input format is the one the user cares about.)*
- [x] **#1 (HUD wording)** `HudStats.ToLines`: distinguish "true passthrough"
      (input == GPU framebuffer family) from "GPU shader does YUV→RGB" — say
      `(GPU YUV→RGB)` rather than `(shader)` so it's obvious no CPU conversion
      ran.
- [x] **#3 (HUD)** Extra HUD line `frame age: NNN ms` showing the unsmoothed
      `clockPosition - vf.Pts`, clamped to a sensible range, with the existing
      `drift:` line keeping its smoothed/EMA value. Helps the user see "decoder
      is below realtime" at a glance. *(Plus drift is now displayed in
      seconds beyond ±1 s.)*
- [x] **#3 (HUD)** Guard `OverridePresentationClock` so passing the same clock
      instance back-to-back doesn't reset the relative-drift origin.
- [x] **HUD observability**: add `decoder dropped:` and `subscription dropped:`
      counters surfaced through `HudStats` so the dropped-frame story is visible
      regardless of which layer is dropping. *(Wired from `MediaPlayer` →
      `IVideoEndpointDiagnosticsSink.UpdateDropCounters` on each drift loop
      tick.)*

### Phase 2 — `LimitRenderToInputFps` exposed in SPlayer ✅

- [x] **#2** Add `LimitRenderFpsToSource` to `AppSettings` (+
      `SettingsViewModel`, settings persistence) and propagate to every
      `AvaloniaOpenGlVideoEndpoint` row in `OutputViewModel` /
      `VideoEndpointModel`.
- [x] **#2 (stall guard)** Inside `AvaloniaOpenGlVideoEndpoint.OnOpenGlRender`,
      when `LimitRenderToInputFps` is on but no frame has uploaded for
      `interval × 4`, fall back to a 100 ms heartbeat poll so a paused source
      can never freeze the input-fps loop forever (the existing cold-start
      branch only covers the very first tick).
      *(Already handled by the existing "no upload but `_hasUploadedFrame`" 100
      ms heartbeat in `OnOpenGlRender`'s finally block. Phase 2 added the
      missing piece: a property setter that reschedules on toggle so `OFF→ON`
      mid-play actually picks up the new cadence.)*

### Phase 3 — Decoder default change ✅

- [x] **#1 / extras** Flip `FFmpegDecoderOptions.VideoTargetPixelFormat` default
      from `Bgra32` to `null` (= "use source native format"). Update / fix the
      tests in `S.Media.FFmpeg.Tests/FFmpegDecoderOptionsTests.cs` and any
      callers / docs that depend on the old default.
- [~] **extras (cancelled)** Make `MediaPlayer.AttachDecoder` call
      `LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat` once the first
      video endpoint is attached. The default flip to `null` already covers
      the user-visible problem (forced BGRA32 conversion). Properly wiring
      the policy from the player would require (a) a new endpoint capabilities
      interface that declares which pixel formats each renderer can accept
      without CPU conversion and (b) a settable `TargetPixelFormat` /
      `TryRetargetPixelFormat` on `FFmpegVideoChannel`. Both are larger
      refactors than the issue warrants today; cancelling rather than
      deferring further so the checklist closes cleanly. SDL3 / Avalonia
      already call the policy *inside* their own `Open` paths, which is
      sufficient for current callers.

### Phase 4 — Backpressure / EOF detection (#4) ✅

- [x] **#4** Add `BackpressureMode = { Wait, DropOldestUnderStall, DropOldest }`
      to `VideoRouteOptions`. Default pull-video routes to
      `DropOldestUnderStall`. Push routes keep their current
      `DropOldest`/`Wait` behaviour.
      *(Implemented as a new `VideoOverflowPolicy.DropOldestUnderStall` value;
      `VideoRouteOptions.OverflowPolicy` already accepts the policy enum so no
      separate `BackpressureMode` enum was needed. The router pull-default
      switched in `AVRouter.CreateVideoRoute`.)*
- [x] **#4** Implement `DropOldestUnderStall` in `FFmpegVideoSubscription.TryPublish`:
      try `WriteAsync` with a bounded inner CTS (configurable, default 250 ms);
      on timeout, evict the oldest frame and retry. Same `_queued` ref-count
      invariants as the existing `DropOldest` branch.
- [x] **#4** `FFmpegDemuxWorker.WritePacketAsync` watchdog: bound the
      `WriteAsync` call with a soft timeout (≈2 s). On timeout, log
      `demux backpressure stall` and drop the packet (ArrayPool buffer
      returned + packet returned to pool). Prevents the demux loop from
      sitting forever and missing `AVERROR_EOF`.
- [x] **#4** Lower `FFmpegDecoderOptions.PacketQueueDepth` default from 64 to
      a more honest 32 (still plenty of decoder headroom, but EOF surfaces in
      fewer wall-time seconds under stress). Update tests if any pin the value.
- [x] **#4** `MediaPlayer.RaiseSourceEndedAfterDrainAsync`: when the audio
      channel is null but the video channel exists, run an analogous video
      drain loop (poll `BufferAvailable`, watchdog stall + overall timeout)
      before publishing `PlaybackCompleted`.

### Phase 5 — Master clock under decode starvation (#3) ✅

- [x] **#3** Extend `VideoPtsClock` with a `TrackingMode` that caps interpolation
      to `_lastPts + maxLeadAhead` (≈ ½ frame interval) so the clock can never
      run away from the last anchor. Equivalent in spirit to `ApplySelfSlew`,
      but framerate-aware and not reliant on the SeekThreshold.
      *(Implemented as `VideoPtsClock.MaxInterpolationLead` — capped at
      `~1.5 × frame interval` (clamped 25–250 ms) when auto-enabled.)*
- [x] **#3** `MediaPlayer.OnActiveClockChanged`: when the resolved router master
      is an endpoint's own `VideoPtsClock` (i.e. video-only session, no audio
      master), enable that clock's tracking mode automatically. Reset to default
      when audio joins / re-binds.
- [ ] **#3** Sanity-check existing tests in
      `MediaFramework/Test/S.Media.Core.Tests/` that touch `VideoPtsClock` and
      add a regression test for "below-realtime decode → bounded drift".
      *(Existing 226 core tests still pass; a dedicated regression test for
      `MaxInterpolationLead` is left as a future TODO.)*

### Phase 6 — Decode-side late-frame skipping (#5) ✅

- [x] **#5** Add `SetLateFrameDropDeadline(TimeSpan latenessBudget)` (or similar)
      on `FFmpegVideoChannel`. When set, after `avcodec_receive_frame`, if the
      frame's PTS is more than `latenessBudget` behind the last
      consumer-reported position (Position is already updated by
      `NotifyFrameDelivered`) AND the frame is non-reference, skip both
      `sws_scale` and the `PublishToSubscribers` call.
      *(Implemented as `ISupportsLateFrameDrop` interface in `S.Media.Core` so
      `S.Media.Playback` can drive it without an `InternalsVisibleTo` hop.
      Reference point is `max(channel.Position, externalClockHint)` so the
      drop also triggers in pull-mode where channel `Position` lags behind
      the master.)*
- [x] **#5** Wire it from `MediaPlayer`: when audio drift correction is
      configured and `_videoInputId` exists, push the running `GetAvDrift`
      reading into the channel via the new setter on every drift-loop tick. For
      video-only sessions, push a static budget derived from
      `2 × frame interval`.
      *(Drift loop pushes the live drift; deadline is `max(50 ms, |drift|/2)`
      and only enabled when `|drift| ≥ 100 ms` so it doesn't kick in for
      sub-frame jitter.)*
- [x] **#5** Drift correction loop: when `consecutiveOutliers` confirms a real
      large drift, *clamp* the applied step instead of treating "outlier" as
      "noise spike forever". The current code already has a clamp, but the
      `MaxStepMs` / `IgnoreOutlierDriftMs` defaults make the loop a no-op
      against multi-second drifts; pick saner defaults for the SPlayer
      preset.
      *(Framework `MaxStepMs`: 8 → 25 ms, `MaxAbsOffsetMs`: 250 → 2000 ms.
      SPlayer hot defaults: 5/250 → 20/2000. Settings UI slider Max raised to
      5000 ms so the user can take it further if needed.)*

### Phase 7 — Hot-path / correctness polish ✅

- [x] **extras** `FFmpegVideoChannel.GetSws`: cache `(w, h, srcFmt)` (target
      format is immutable per channel) and only recompute `_swsBufSize` /
      `sws_getCachedContext` on change. Hits the FFmpeg helpers once per
      stream/resolution change instead of once per frame.
- [x] **extras** `FFmpegVideoChannel.SourceFormat`: prefer
      `stream->avg_frame_rate` over `r_frame_rate` (fall back if
      `avg_frame_rate` is invalid). Fixes VFR-source mis-pacing under
      `LimitRenderToInputFps`.
- [x] **extras** `MediaPlayer.AttachDecoder` / `AutoRouteToEndpoint` call the
      new `IVideoEndpointInputFormatHint.SetInputFormatHint(channel.SourceFormat)`
      on every endpoint that supports it, so HUD `src:` line uses the real
      source fps / size / pixel format from frame zero. Implemented by
      `AvaloniaOpenGlVideoEndpoint` and `SDL3VideoEndpoint`.
- [x] **extras** `FFmpegPixelFormatConverter.TrySwsScale`: dropped the duplicate
      pointer-array clear (it's done by `FillPlanes` on entry).
- [x] **extras** Pool `RefCountedVideoBuffer` wrappers via a process-wide
      bounded `ConcurrentQueue` (`PoolCapacity = 64`). Decoder hot path now
      calls `RefCountedVideoBuffer.Rent(...)` instead of `new`; instances are
      recycled when their refcount reaches zero. Existing `new
      RefCountedVideoBuffer(...)` callers (tests, NDI) keep working unchanged.
- [x] **extras** `PtsDriftTracker`: hot-path reads (`RelativePts`,
      `RelativeClock`, `IntegrateError`) no longer enter the seed/reset lock.
      They use `Volatile.Read` on the long fields; the integrator's
      read-modify-write uses `Interlocked.Add`. The lock is now only taken
      from `SeedIfNeeded` (one-shot) and `Reset` (rare) for the (HasOrigin,
      Pts, Clock) atomic triple update.
- [x] **extras** `BasicPixelFormatConverter`: documented as the portability
      fallback. Real scalar implementations stay for BGRA↔RGBA byte-swap,
      packed-24 expansion, Gray8, Yuv444p, Yuv422p10/I210, Yuv420p10/I010,
      and P010. NV12 / Yuv420p / Uyvy422 → RGBA/BGRA are intentional opaque-
      black placeholders (timing / pacing stays deterministic when libswscale
      is unavailable). FFmpegPixelFormatConverter is the production route for
      those formats. Vectorisation skipped — libswscale already covers the
      throughput case.
- [x] **extras** `MediaPlayer.BufferStalled` event: fires from the demux
      watchdog when a packet was dropped after the 2 s write-stall budget
      expired. SPlayer surfaces it as a "Output stalled — decoder dropping
      packets" status message (rate-limited to one notice per 500 ms so a
      stuck pipeline doesn't churn the UI).

### Acceptance / validation

- [ ] Repro file (4K60 yuv422p10 ProRes, audio + video) plays through to EOF
      with `PlaybackCompleted(SourceEnded)` firing in finite time even when the
      machine is below realtime.
- [ ] HUD `drift:` line stays bounded (does not grow past a few hundred ms)
      when the decoder is below realtime; `frame age:` line shows the real lag.
- [ ] HUD reports `Yuv422p10 (passthrough)` for the same file when no software
      conversion is happening.
- [ ] With `LimitRenderFpsToSource` enabled, a 24 fps file does not wake the GL
      compositor at 60 Hz (verify via `RenderCalls` in
      `AvaloniaOpenGlVideoEndpoint.DiagnosticsSnapshot`).
- [ ] Existing tests in `MediaFramework/Test/**` and
      `UI/SPlayer/SPlayer.Tests/**` (if any) all pass after the default
      `VideoTargetPixelFormat` flip.

---

Source TODOs (verbatim from `Doc/TODOs.md`):

1. YUV422p10 seems to get converted to BGRA32 on local video (should normally
   just passthrough) (tested with heavy 4k60 yuv422p10 prores file)
2. Maybe option to lock video output fps to source video fps on Avalonia unless
   that stalls the entire app
3. (local video only) Drift in the HUD value seems very broken, while playback
   looks fine (when not running slow), it seems to be at a random very high
   value (currently in my testing hovering around 9000ms and increasing more
   and more).
4. (local video only) Playback doesnt seem to detect the end of the video and
   now the drift in the HUD just runs higher and higher.
5. (local video + audio) When running slow audio and video seem to get out of
   sync
