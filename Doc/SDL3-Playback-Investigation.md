# SDL3 Playback Investigation

> **Follow-up update #2 — ACTUAL root cause found**
>
> After the catch-up / texture-reuse / HUD-dropped-frame fixes were applied, the user reported
> the SDL3 output still ran at half (24 fps content → 12 fps uploaded) or a third (60 fps
> content → 20 fps uploaded) of the content rate, while NDI remained perfectly smooth.
>
> **Root cause:** `InputEntry.VideoChannel` (the FFmpeg decoder's bounded ring) is a
> **shared multi-reader** channel, and both the pull path (SDL3 render loop at ~60 Hz vsync)
> and the push path (`AVRouter.PushVideoTick` at ~100 Hz) were independently calling
> `channel.FillBuffer(oneFrame, 1)` on it. Each fresh frame decoded by FFmpeg is consumed
> by whichever consumer ticks next — so push@100 Hz wins roughly 100 / (100 + 60) = ~62 %
> of the races and pull sees only the remaining ~38 % of fresh frames. For 24 fps content
> that's ~9 fps reaching SDL3, and the texture-reuse path correctly re-shows the last frame
> for every other vsync (hence the `12 fps` HUD reading). For 60 fps the math gives ~22 fps
> which matches the reported `~20 fps`. NDI appeared unaffected because it was receiving
> the majority of fresh frames via the push path — plus `clockVideo=true` re-paces whatever
> it receives into a clean stream on the wire.
>
> The `InputEntry.LastVideoFrame` "borrow" mechanism was meant to prevent this, but it
> read-and-cleared the slot — so the first push tick after a pull presentation borrowed
> the frame, and subsequent push ticks (before the next pull presentation) fell through
> to a ring `FillBuffer`, stealing frames from pull.
>
> **Fix applied:**
>
> 1. `InputEntry.HasPullVideoEndpoint` — new flag, recomputed by
>    `RebuildVideoRouteSnapshot`, true when any routed video endpoint on that input
>    implements `IPullVideoEndpoint`.
> 2. `_pushVideoLastBorrowed` — new per-input cache of the most recent frame the push
>    tick borrowed from `InputEntry.LastVideoFrame`.
> 3. `PushVideoTick` now, for inputs with a pull consumer:
>    - Caches each borrowed frame into `_pushVideoLastBorrowed`.
>    - When `LastVideoFrame` is empty between pull presentations, re-forwards the cached
>      borrowed frame and **does not** fall through to the ring-read path.
>    - NDI's `clockVideo=true` absorbs/dedupes the duplicate forwards and paces the output
>      stream at the target rate.
>
> Inputs with no pull consumer keep the old behaviour (push reads the ring directly).
>
> This is additional to the previous round of fixes (SDL3 catch-up loop, texture reuse,
> proper dropped-frame HUD counter, bufferDepth 4 → 8, `SetClock` de-duplication).

---

# SDL3 Playback Investigation

> **Status update (follow-up commit #1)**: the P0 / P2 fixes described in this document
> that don't compromise video quality have been implemented. Specifically:
>
> - **Catch-up loop in the SDL3 render loop** — `SDL3VideoOutput.RenderLoop` now drains
>   stale frames up to `MaxCatchupPullsPerRender` (default 6) when the presentation clock
>   has moved past the pulled frame's PTS by more than `CatchupLagThreshold` (default 45 ms),
>   mirroring `AvaloniaOpenGlVideoOutput`.
> - **Texture-reuse** — `GLRenderer.DrawLastFrame()` re-runs only the draw portion of the
>   pipeline (no `glTexSubImage2D`) when the pull callback re-presents the same frame.
>   Wired up in both `SDL3VideoOutput` and `SDL3VideoCloneSink`. Each `UploadAndDrawXxx`
>   was refactored into an upload step plus a shared `DrawXxxFromTextures` helper.
> - **Proper dropped-frame HUD counter** — `_droppedFrames` counts real catch-up skips (not
>   render exceptions, as before). A new `_uniqueFrames` counter tracks actual content
>   frames and the HUD FPS now reports the content rate rather than the display-refresh
>   rate that was masking the bug. The HUD also shows texture-reuse vs upload counts on an
>   extra line.
> - **Default `bufferDepth` raised from 4 → 8** in `FFmpegVideoChannel` to give the render
>   loop more tolerance for hiccups.
> - **`AVRouter.SetClock` de-dupes** prior registrations of the same clock instance, so a
>   clock auto-registered at `Hardware` priority is replaced cleanly by the `Override`
>   entry instead of appearing twice in the registry.
>
> Explicitly **not** applied (would trade quality):
>
> - Default `ScalingFilter` left at `Bicubic` — keeping broadcast-quality scaling.
>   The 1:1 short-circuit inside `BeginFboIfNeeded` and the PBO upload path remain
>   open for a future pass.
>
> The remainder of the original investigation follows unchanged.

---

## Summary

The SDL3 local video output (`Video/S.Media.SDL3/SDL3VideoOutput.cs`) plays back noticeably slower / with visible frame-skipping compared to the NDI output, which is smooth. The NDI path is driven by the router's push thread and re-clocked by the NDI SDK itself (`clockVideo=true`), so it is largely immune to presentation-side stalls. The SDL3 path is driven by **the display's vsync** on a dedicated render thread and pulls **exactly one frame per vsync** from the ring. It has **no catch-up path, no texture-reuse detection, default-on bicubic two-pass rendering, and synchronous `glTexSubImage2D` uploads on every swap** — even when the frame has not changed. On high-resolution content this easily misses the display's vsync deadline, which (with VSync ON, the default) drops the swap cadence and, because the clock is the `VideoPtsClock` updated by *this same loop*, visibly slows playback and produces skipped / re-shown frames.

A direct proof point is the parallel Avalonia endpoint (`Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs`), which has both mitigations (catch-up loop + "same-data" texture-reuse path) and, unsurprisingly, does not exhibit the same symptoms.

---

## How the SDL3 output currently works (end-to-end frame flow)

1. **Decode → ring** (`Media/S.Media.FFmpeg/FFmpegVideoChannel.cs`)
   - `FFmpegDecoder.Open(...)` creates an `FFmpegVideoChannel` with `bufferDepth = 4` (hard-coded default, `FFmpegVideoChannel.cs:110`).
   - Ring is `Channel.CreateBounded<VideoFrame>(4)` with `BoundedChannelFullMode.Wait` (`FFmpegVideoChannel.cs:138-144`). The decoder writer **blocks** when the ring is full.
   - `DecodePacketAndEnqueue` converts via `sws_scale` into an `ArrayPool<byte>`-rented buffer wrapped in a `VideoFrame` and writes into the ring (`FFmpegVideoChannel.cs:540-605`).

2. **Endpoint registration** (`Test/MFPlayer.VideoPlayer/Program.cs`)
   - `router.RegisterEndpoint(videoOutput)` → `AVRouter.SetupPullVideo` installs a `VideoPresentCallbackForEndpoint` on `SDL3VideoOutput.PresentCallback` (`Media/S.Media.Core/Routing/AVRouter.cs:641-647`).
   - `AutoRegisterEndpointClock` auto-registers `videoOutput.Clock` (a `VideoPtsClock`) at `ClockPriority.Hardware` (`AVRouter.cs:343-347`).
   - `router.SetClock(videoOutput.Clock)` additionally registers the *same* clock at `ClockPriority.Override` (`Program.cs:221`, `AVRouter.cs:437-450`). So the master clock is the SDL output's own PTS clock (self-feedback; see below).

3. **Render thread** (`Video/S.Media.SDL3/SDL3VideoOutput.cs:431-604`)
   - Dedicated `SDL3VideoOutput.Render` thread.
   - Vsync = `VsyncMode.On` (swap-interval 1) by default (`SDL3VideoOutput.cs:74`, applied via `SDL.GLSetSwapInterval` at `:329-335`).
   - Each iteration:
     a. Pump SDL events (`:451-476`).
     b. `presentationClock.Position` — for this test this is the **same `VideoPtsClock`** the render loop itself updates.
     c. `PresentCallback.TryPresentNext(clockPosition, out frame)` (`:506`).
     d. If a frame came back, call `GLRenderer.UploadAndDraw(frame)` and `VideoPtsClock.UpdateFromFrame(frame.Pts)` (`:540-543`).
     e. `SDL.GLSwapWindow(_window)` — **blocks until next vsync** (`:583`).

4. **`TryPresentNext` pull logic** (`Media/S.Media.Core/Routing/AVRouter.cs:1342-1508`, `VideoPresentCallbackForEndpoint.TryPresentNext`)
   - Tries pending-frame cache first; otherwise pulls **exactly one** frame from the ring via `channel.FillBuffer(oneFrame, 1)` (`:1396`).
   - Runs a `PtsDriftTracker` seed + early-gate (`:1437-1478`):
     - If the candidate's PTS is *ahead* of the clock by more than `VideoPtsEarlyTolerance = 5 ms` (`AVRouterOptions.cs:35`), cache as `_pendingFrame`, re-present `_lastPresentedFrame`.
     - Otherwise present the candidate and store it as `_lastPresentedFrame`.
   - **There is no catch-up path**: if the candidate is *late* (PTS < clock by any amount), it is presented as-is, and exactly one frame per vsync is drained regardless of how far behind we are.
   - If the ring is empty (`got == 0`), re-present `_lastPresentedFrame` (`:1399-1405`).

5. **GL upload + draw** (`Video/S.Media.SDL3/GLRenderer.cs`)
   - Default `ScalingFilter = Bicubic` (`GLRenderer.cs:244`, `SDL3VideoOutput.cs:93`).
   - For **every** call to `UploadAndDraw`, whether the frame is new or the re-presented last frame:
     - `frame.Data.Pin()` + synchronous `glTexSubImage2D` full-frame upload (e.g. `UploadAndDraw` line 695, `UploadAndDrawNv12` lines 737/744, I420 lines 790/797/804, etc.).
     - With bicubic: allocate/reuse FBO at native video resolution (`EnsureFboGeneral`, `:989-1028`), render into it (`BeginFboIfNeeded`, `:957-964`), then second pass blits with the bicubic Catmull-Rom shader (`BlitFboToScreen`, `:970-985`). That is 16 texture taps per output pixel plus an extra full-sized RGBA FBO texture for every frame — for every vsync.
   - No PBO / persistently-mapped / orphaned-upload path; no frame-identity check before upload.

6. **Clock self-feedback**
   - `VideoPtsClock.UpdateFromFrame` (`Media/S.Media.Core/Clock/VideoPtsClock.cs:74-106`): only anchors `_lastPts` on the first frame; thereafter it keeps the early-return branch (`pts <= predicted`) and effectively just lets the internal `Stopwatch` drive `Position`.
   - Because the master clock is driven by the same render loop that reads it, any stall in the render thread (GPU stall, vsync miss) does not advance the clock backwards — but also the pull callback cannot notice it is "behind wall time" in any self-correcting way, because the clock *is* wall time.

---

## Comparison with the NDI path (why NDI is smooth)

1. **Different transport cadence.**
   NDI is a `IVideoEndpoint` (push-only), driven by `AVRouter.PushVideoTick` on the dedicated `AVRouter-PushVideo` thread at `InternalTickCadence = 10 ms` (`AVRouter.cs:865-887`, `AVRouterOptions.cs:19`). It is **not** blocked by display vsync.

2. **Catch-up built into the push path.**
   `PushVideoTick` has a real catch-up loop bounded by `VideoMaxCatchUpFramesPerTick = 4` (`AVRouter.cs:1173-1201`, `AVRouterOptions.cs:43`): when a candidate is late and newer frames exist, it drains and disposes stale frames until it finds the latest on-time one. The pull path (used by SDL) has no such code.

3. **NDI SDK re-paces video itself.**
   `NDISender.Create(..., clockVideo: true, clockAudio: true)` (`Program.cs:267`). SDK §13 says `clockVideo` makes `SendVideo` rate-limit to the declared frame rate and timecodes the stream with stream PTS (`NDIAVSink.cs:938-960`). Any jitter in *frame arrival at the sink* is absorbed in the `PooledWorkQueue<PendingVideo>` + dedicated `VideoThread` pipeline (`NDIAVSink.cs:812-996`), then re-clocked by the NDI library. Receivers see a clean 25/30/60 fps stream regardless of pipeline jitter.

4. **No per-frame GPU work.**
   NDI does only a memcpy into a pooled buffer (`NDIAVSink.cs:392-393`) plus at most a format conversion (UYVY/NV12/I210) on the sink thread. No shader compilation, FBO, swap-interval blocking, or vsync coupling.

5. **A/V timing is anchored to stream PTS, not wall clock.**
   `NDIAvTimingContext` seeds the audio timecode cursor from the first observed video PTS (`NDIAvTimingContext.cs:13-34`), so the receiver aligns by media time rather than wall-clock submit order. Irrelevant for the local-only slowness but relevant to the surrounding "NDI was already fixed" context.

In short: NDI's pipeline is **rate-decoupled from the render thread** (router push → queue → NDI thread → SDK clock), while SDL3's pipeline is **serialized onto the vsync thread** with a 1-frame-per-tick bottleneck and two sources of heavy per-tick GPU work (bicubic FBO + full-frame uploads on every swap).

---

## Root cause analysis (ranked by likelihood)

### 1. Pull callback has no catch-up / frame-skipping (HIGH)

**File:** `Media/S.Media.Core/Routing/AVRouter.cs`, `VideoPresentCallbackForEndpoint.TryPresentNext` around lines **1369-1508**.

- Only one frame is pulled per call (`FillBuffer(oneFrame, 1)` at line 1396).
- When a candidate is late (`relativePtsTicks < relativeClockTicks`), it is presented unchanged; there is no code to consume additional stale frames to get back on the live PTS.
- Compare to `AvaloniaOpenGlVideoOutput.OnOpenGlRender` (`Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs:343-366`): after getting `vf`, it loops up to `_maxCatchupPullsPerRender = 6` times calling `presentCb.TryPresentNext` while `vf.Pts + _catchupLagThreshold < clockPosition`. SDL3 has *nothing equivalent*.
- Consequence on 60 Hz display, 60 fps content: any short stall (GPU hiccup, Window resize, compositor pause, bicubic FBO miss) leaves the ring backed up by N frames. The SDL loop then drains at only 1 frame per vsync — i.e. real time — so content **permanently lags wall time by N frames** after the stall, and the ring stays full forever. On 30 fps content on a 60 Hz display we have 2 vsync per content frame as budget, so a single missed vsync causes the same permanent lag.

### 2. Every vsync re-uploads the full texture, even when the frame didn't change (HIGH)

**File:** `Video/S.Media.SDL3/SDL3VideoOutput.cs:540`, `Video/S.Media.SDL3/GLRenderer.cs`.

- The render loop unconditionally calls `_renderer!.UploadAndDraw(frame.Value)` every iteration (`SDL3VideoOutput.cs:540`) — including when `TryPresentNext` returned the previous `_lastPresentedFrame`.
- `GLRenderer.UploadAndDraw` always uploads via `glTexSubImage2D` (e.g. lines 695, 737/744, 790/797/804, 849/856/863, 906, 1103/1110, 1147/1154/1161, 1194).
- For 4K BGRA (33 MB) at a 60 Hz display that is ~2 GB/s of PCIe upload even when nothing changes. For 30 fps content on a 60 Hz display, half of those uploads are byte-identical re-uploads — pure waste that also causes driver-level synchronization (many drivers implicitly `glFinish` the previous texture use before reallocating/updating texels).
- `AvaloniaOpenGlVideoOutput.OnOpenGlRender:371-391` shows the right pattern: compare `(width, height, pts, data)`; if identical, call `_renderer.DrawLastTexture(...)` and skip the upload entirely.

### 3. Default scaling filter is `Bicubic`, which forces a two-pass FBO pipeline per frame (HIGH)

**Files:** `Video/S.Media.SDL3/SDL3VideoOutput.cs:93` and `Video/S.Media.SDL3/GLRenderer.cs:244` both set `ScalingFilter.Bicubic` as the default.

- For every non-UYVY format the `BeginFboIfNeeded` path triggers: render into `_fboGeneral` at native resolution (`GLRenderer.cs:957-964`), then `BlitFboToScreen` runs a Catmull-Rom bicubic (16 taps/px) pass to the window (`:970-985`).
- UYVY uses a two-pass FBO unconditionally (`UploadAndDrawUyvy422`, `:883-948`), and when the scaling filter is bicubic the second pass uses `_programBicubic` as well (`:940-943`).
- At 4K + 60 Hz with a non-native upscaling factor this alone can push a full frame past 16 ms on mainstream GPUs. Under VSync ON, that turns an ~16.6 ms miss into a ~33 ms swap, which **halves** the effective presentation rate. Combined with root cause #1 this produces a visible slow/stutter.
- Notable: even for 1:1 pixel mapping the FBO pass still runs (`BeginFboIfNeeded` doesn't short-circuit when `w == _vpW && h == _vpH`), so we pay the cost even without a visual benefit.

### 4. VSync ON combined with the above amplifies every miss (MEDIUM)

- `VsyncMode.On` → `SDL_GL_SetSwapInterval(1)` (`SDL3VideoOutput.cs:329-335`). `SDL.GLSwapWindow` blocks until vsync. Any frame that takes longer than the vblank interval dilates by a full vblank — a classic "60 → 30 → 20 fps" cliff.
- Pairs badly with #2 and #3 because the GPU work happens inline in the render thread.

### 5. `bufferDepth = 4` is small for a 60 Hz presentation of 60 fps content (MEDIUM)

**File:** `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:110`.

- With a 4-slot ring and `BoundedChannelFullMode.Wait`, the decoder blocks as soon as the consumer (SDL render loop) stops draining. With root cause #1 this means one render-thread stall → ring fills to 4 → decoder blocks → demux keeps producing packets into its queue → demux queue also fills → everything backs up on wall time.
- `MediaPlayer.cs` / test program never bump this depth; no public option to do so.

### 6. Clock is a self-feedback loop driven by the render thread (LOW but noteworthy)

**Files:** `Test/MFPlayer.VideoPlayer/Program.cs:220-221`, `Media/S.Media.Core/Clock/VideoPtsClock.cs`.

- `router.SetClock(videoOutput.Clock)` makes the master clock the SDL output's own `VideoPtsClock`, which is also auto-registered at `Hardware` priority inside `RegisterEndpoint` (`AVRouter.cs:343-347`). The clock appears in the registry *twice* (one `Hardware`, one `Override`) after `SetClock`.
- `VideoPtsClock.Position` = `_lastPts + (sw.Elapsed - _swAtLastPts)` — wall-clock after first frame. Functionally correct, but there is no mechanism by which a stalled render thread signals the clock to pause; the clock just keeps advancing, so a stall manifests as "frames fall behind the clock", which in turn (given #1) cannot be caught up.
- Minor bug: `ClockPriority.Override` is meant to dominate, but because `RegisterClock` de-dupes by instance and re-adds (`AVRouter.cs:414-424`), the order `RegisterEndpoint → SetClock` leaves both entries in `_clockRegistry`. `ResolveActiveClock` still picks `Override`, so behavior is correct, but the registry is confusing and the second registration's log line says `Hardware` before `SetClock` later overrides.

### 7. Clone-sink render loops are busy-loops at vsync without present throttling (LOW; may or may not be active)

**File:** `Video/S.Media.SDL3/SDL3VideoCloneSink.cs:147-186`.

- If any clone sinks are active, each one has its own window, its own GL context and its own vsync-paced `SDL_GL_SwapWindow` on a separate `_renderThread`. They always upload and draw every iteration (no same-frame skip) — same pattern as root cause #2 but per clone.
- The VideoPlayer test does not create clone sinks, so this is unlikely to be the cause here, but the pattern is the same and should be fixed together.

---

## Recommended fixes (ordered by priority)

### P0. Add a catch-up loop in the SDL3 render loop (mirror Avalonia)

**Where:** `SDL3VideoOutput.cs:506` (right after the first `TryPresentNext` call) or inside a new helper. Simplest equivalent to `AvaloniaOpenGlVideoOutput:344-366`:

- After a successful `TryPresentNext`, while `frame.Pts + catchupLagThreshold < clockPosition` and catch-up budget (e.g. 6) remaining, call `TryPresentNext` again; only keep the last returned frame.
- Break out when two successive calls return the same `(Pts, Width, Height, Data)` (the pull callback currently re-presents `_lastPresentedFrame` when the ring is empty, so this equality is how we detect "no newer frame available").
- Use `_catchupLagThreshold ≈ 45 ms` and `_maxCatchupPullsPerRender = 6` (same constants as Avalonia; they're reasonable and proven).

Alternative / complementary: lift the catch-up into the shared `VideoPresentCallbackForEndpoint` so every pull endpoint benefits — see "Further Considerations" below.

### P0. Skip the GPU upload when the frame is the same as last presented

**Where:** `SDL3VideoOutput.cs:511-544` and `GLRenderer.cs`.

- Track `(Pts, Width, Height, Data)` of the last uploaded frame inside `SDL3VideoOutput` (or inside `GLRenderer`).
- If they match the incoming frame, call a new `GLRenderer.DrawLastTexture()` that just re-renders the already-uploaded texture (or even re-uses the FBO pass-2 output) without any `glTexSubImage2D`.
- Implementation reference: `AvaloniaOpenGlVideoOutput.OnOpenGlRender:371-391` + `AvaloniaGlRenderer.DrawLastTexture`.

### P1. Change `ScalingFilter` default to `Bilinear`, or short-circuit the FBO pass when 1:1

**Where:** `SDL3VideoOutput.cs:93`, `GLRenderer.cs:244`, `GLRenderer.BeginFboIfNeeded` (:957).

- Set the default to `ScalingFilter.Bilinear` (same default as the Avalonia path visually, cheap GPU bilinear). Keep Bicubic as an opt-in for frame-grabbing / stills.
- Independently, add an early-out in `BeginFboIfNeeded` when the output viewport equals native video resolution: direct-draw with `GL_LINEAR` is visually indistinguishable.

### P1. Decouple the pull rate from the display refresh for > 60 fps content

**Where:** `SDL3VideoOutput.RenderLoop` around `:444-592`.

- Either: allow a short busy/yield path *before* `GLSwapWindow` so the catch-up loop above can fire multiple times per vsync when the ring is full (fits naturally with P0 #1).
- Or: expose an option to change `VsyncMode` default to `Adaptive` for content whose fps may exceed the display rate. Already implemented, just not wired up in the test app.

### P2. Raise default `bufferDepth` (or expose it)

**Where:** `FFmpegVideoChannel.cs:110` + `FFmpegDecoderOptions` (not shown; likely `FFmpegDecoder.Open`).

- 4 is tight for 60 fps on a 60 Hz display; 8–12 gives more tolerance for render-loop hiccups without changing steady-state latency much (steady-state depth ≈ 1–2 anyway).
- Expose it on `FFmpegDecoderOptions` so hosts can tune.

### P2. Clean up clock registration ordering / duplication

**Where:** `AVRouter.SetClock` (`AVRouter.cs:437-450`) and `RegisterClock` (:414-424).

- `SetClock` should de-dupe by instance (as `RegisterClock` does) before adding at `Override`, so a clock that was also auto-registered at `Hardware` appears only once with priority `Override`.
- Same for `ResolveActiveClock` log message.

### P3. Texture-reuse for clone sinks

**Where:** `SDL3VideoCloneSink.RenderLoop` (:147-186) and `AvaloniaOpenGlVideoCloneSink` if same pattern.

- Same "compare frame to last-uploaded, skip upload if identical" as P0 #2.

### P3. Use PBO or `glBufferSubData` for async uploads (optional, large content)

**Where:** `GLRenderer.UploadAndDraw*`.

- For 4K+ content the synchronous `glTexSubImage2D` stalls. A simple 2- or 3-ring PBO pattern removes the stall; visible benefit at high resolution and high refresh rate.

---

## Other observations (unrelated improvement opportunities)

1. **`SDL3VideoOutput._presentedFrames` counts every render iteration that had a frame, including re-presents of `_lastPresentedFrame`** (`:543`). This makes the stats printout in `Program.cs:438-441` (`fps=presentDelta/expectedFps`) report ~display-refresh-rate regardless of whether new content frames are actually arriving — masking exactly the bug we're investigating. Suggest splitting into `_presentedFramesTotal` and `_presentedFramesUnique` (increment the "unique" counter only when the candidate PTS differs from the last presented).

2. **Clock registered twice** (see root cause #6). Not a bug, but log output during startup is misleading.

3. **`router.SetClock(videoOutput.Clock)` is ordered *before* `await videoOutput.StartAsync()`** in `Program.cs:221` vs `:365`. The clock hasn't started yet, so for a brief period the resolved master clock returns `Position = _lastPts = 0`. Because `PushVideoTick` runs as soon as `router.StartAsync` is called on line 366, and the SDL render loop hasn't started yet on the first tick, a PUSH endpoint (if any besides NDI) could see a 0 clock. Minor, but worth noting.

4. **`FFmpegVideoChannel.ConvertFrame` re-rents from `ArrayPool<byte>` for every frame** (`:233`). Fine in general, but the buffer size is stable per resolution — a per-channel pool of exact-sized buffers would reduce GC/pool churn. Not related to the SDL3 slowness.

5. **`GLRenderer.UploadAndDraw` for Bgra32 branches on `w == _texWidth && h == _texHeight`** (:692-702). The NV12/I420/I422P10/P010/Yuv444p paths each duplicate this logic (lines 736-749, 789-806, 848-868, 1101-1115, 1146-1167). Factor into a helper `UploadPlane(texHandle, level, fmt, type, w, h, data, ref lastW, ref lastH)` — trivial refactor, reduces 60+ lines.

6. **`GLRenderer` holds 18+ managed delegate instances loaded via `Marshal.GetDelegateForFunctionPointer`** (:63-158, :1317-1366). Considering .NET 8+, replace with `[LibraryImport]` function pointers or `delegate* unmanaged<>` to eliminate marshaller overhead and keep JIT inlining possible. Measurable on tight loops (we make ~10 GL calls per frame per plane).

7. **`SDL3VideoCloneSink.RenderLoop` always uploads + draws and swaps even if `_latestFrame` is null from start** (:147-182). Specifically, `DrawBlack` followed by `SwapWindow` at vsync spins at 60 Hz forever if the parent never produces a frame. Fine, but could idle-wait on a ManualResetEvent to save battery/CPU.

8. **`SDL3VideoOutput.Dispose` has a TOCTOU between `_isRunning = false` and `_renderThread?.Join`** (`:617-624`) and also in `StopAsync` (`:420-426`) — the thread may observe `_isRunning = false` but the cancellation token is set immediately afterwards; currently benign because the loop checks `token.IsCancellationRequested` as well, but the two-step pattern is redundant and can be simplified to cancellation-token-only.

9. **`SDL3VideoCloneSink.Dispose` calls `_ = StopAsync()` without awaiting** (:194) then immediately destroys GL resources. With a slow `_renderThread.Join(2 s)` in `StopAsync`, there's a race if the render thread is still running when destruction starts. Should `await`/`.Wait()` `StopAsync` before teardown.

10. **`SDL3VideoOutput.GetAvSyncSnapshot` not exposed** — NDI has one (`NDIAVSink.GetAvSyncSnapshot`) and the stats code uses it. SDL could expose similar per-frame timing counters (real inter-swap interval, last upload time, last draw time) to make this class of bug diagnosable without guessing.

11. **`SDL3VideoLogging.cs`** and **`NDIMediaLogging.cs`** have diverged in layout; consider consolidating logger acquisition under a common helper.

12. **Docs drift:** `README.md` in `Doc/` lists guides but not an architecture/troubleshooting page. Once fixes land, the SDL3 presentation/timing model (catch-up, texture-reuse, vsync semantics) should be added — it's non-obvious that the SDL3 path is vsync-driven while NDI is push-rate-limited.

---

## Open questions

1. **Is VSync ON the right default for file playback?** With the catch-up + texture-reuse fixes above, VSync ON is the correct default. Without them, VSync ON is a trap. Confirm the intended UX; if "live monitoring" is the primary use case, flip the default to `Adaptive`.

2. **Should `VideoPtsEarlyTolerance = 5 ms` scale with frame period?** For 120 fps content (8.3 ms period) a 5 ms tolerance is 60 % of a frame — much larger than for 24 fps (0.5 ms of a frame). A tolerance of `min(5 ms, framePeriod/4)` would be more uniformly tight.

3. **Should the catch-up loop be part of the shared `VideoPresentCallbackForEndpoint` or per-endpoint?** Putting it in the shared callback (single source of truth, like `PushVideoTick`) benefits every pull endpoint (SDL3, Avalonia, future ones) but requires `TryPresentNext` to know how many frames to drain in one call. Per-endpoint is what Avalonia does today, which is simpler but duplicates logic. Recommend: push the catch-up into `TryPresentNext` and remove the Avalonia-side loop.

4. **Are there real workloads that need `Bicubic` by default?** If yes, gate it on an explicit user flag or auto-enable only on down-scale / non-integer up-scale. Otherwise default `Bilinear`.

5. **Is the double clock-registration in `AVRouter` (Hardware + Override for the same instance) an accepted pattern, or should `SetClock` replace *all* prior registrations of that instance?**

