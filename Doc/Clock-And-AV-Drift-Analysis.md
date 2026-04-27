# Clock System & A/V Drift Analysis

**Scope:** investigation of `IMediaClock` implementations, the `AVRouter` push/pull
synchronization paths, the auto A/V drift correction loop in `MediaPlayer`, and
related issues observed on the WIP `SPlayer` UI when an `NDIAVEndpoint` is the
only output.

**Symptoms reported by the user:**
1. With **NDI as the only output** and the router clock set to **NDIClock**,
   the video runs **ahead of audio**, reaching roughly **1 s of lead at the
   end of a 90 s clip**.
2. With the same setup but the router clock set to **Internal / StopwatchClock**,
   audio and video stay in sync, but the **automatic A/V drift correction
   produces visible "big steps"** rather than a smooth nudge.

Both observations are real and reproducible from the code as it stands. The
core mechanisms responsible are described below, followed by recommended fixes
and a list of unrelated issues / optimization opportunities found while
walking the pipeline.

---

## 1. Executive summary

| # | Severity | Area | Problem |
|---|----------|------|---------|
| 1 | **High** | `AVRouter.PushVideoTick` (sender-clock bypass pacing) | Pace logic delivers up to one full **video tick cadence** *early* per frame, biasing video forward of audio. With `NDIAVEndpoint`'s `NominalTickCadence = 4 ms` this dominates and matches the user's "video ~1 s ahead at 90 s." |
| 2 | **High** | `MediaPlayer.StartDriftCorrectionLoop` | The outlier guard `IgnoreOutlierDriftMs = 250 ms` *silently disables* the corrector once drift exceeds the cap. With `Interval = 20 s` and any systematic drift > ~12 ms/s, the loop sees its first sample already over the cap and never engages. This is why the user sees ever-growing drift on NDIClock. |
| 3 | **High** | `PlayerViewModel.ConfigureAutoAvDriftCorrection` | `MaxStepMs = 25` + `Interval = 20 s` + `Gain = 0.5` produces visibly large 25 ms jumps every 20 s when the loop *does* engage (Internal clock case). |
| 4 | Med | `VideoPtsClock.UpdateFromFrame` | The 500 ms "ignore small drift" rule masks all drift below 500 ms when `VideoPtsClock` is used as the master clock without an external corrector — the clock free-runs on the Stopwatch and only re-anchors on seeks. |
| 5 | Med | `AVRouter.ResetAllDriftTrackers` race | `PtsDriftTracker.Reset()` is called from the router thread on `SetClock`/`RegisterClock`/`UnregisterClock` while `PushVideoTick` reads it on the push thread. No synchronization. |
| 6 | Med | `AVRouter.ResetAllDriftTrackers` incomplete | Resets `PushDrift` and the bypass-push timestamps but **not** `BypassNudgeDrift`. After clock changes, the bypass-nudge tracker carries stale origins. |
| 7 | Med | `NDIClock.UpdateFromFrame` discarding monotonic-floor clamp | The trade-off is correct (avoids accumulating sender/receiver rate drift) but it leaves the `Position` getter "floor-stuck" for several reads when an out-of-order PTS arrives. Worth a short comment about the implication. |
| 8 | Low | `StopwatchClock.Position` lock | Acquired on every `Clock.Position` read. Used in two locations per route per video tick (1× hot, 2× warm) and once per audio tick. Lockless `Interlocked` snapshot would remove a contention point. |
| 9 | Low | `NDIAVEndpoint.AudioWriteLoop` deinterleave | Scatter writes (`planar[c * samplesPerChannel + s] = …`) are cache-unfriendly for 4+ channel layouts. SIMD-aware transpose for 2/8/16 ch helps. |
| 10 | Low | Multiple `Clock.Position` reads per route per video tick | `PushVideoTick` re-samples `Clock.Position` once per route. With NDIClock that is a seqlock retry loop; with StopwatchClock a lock acquisition. Re-using the same sample within a tick is safe. |

---

## 2. Background — how each clock is wired

The router has three concrete `IMediaClock` implementations that can serve as
master, plus the per-endpoint pull `VideoPtsClock` instances inside SDL3 /
Avalonia:

- `StopwatchClock` (`MediaFramework/Media/S.Media.Core/Clock/StopwatchClock.cs`)
  — pure software, advances exactly with wall time, no external feedback.
  Acquires Windows `timeBeginPeriod(1)` while running.

- `NDIClock` (`MediaFramework/NDI/S.Media.NDI/NDIClock.cs`) — driven by
  `UpdateFromFrame(ndiTimestamp)` at frame *acceptance* time on the router
  thread (via `NDIAVEndpoint.NotifyNdiClockOfRouterVideoPts`). Implements
  `ISuppressesAutoAvDriftCorrection` so the auto drift loop uses
  `GetAvStreamHeadDrift` instead of `GetAvDrift`.

- `VideoPtsClock` (`MediaFramework/Media/S.Media.Core/Clock/VideoPtsClock.cs`)
  — owned by each pull video endpoint. When a router-master clock is set
  via `SetClock`, `SDL3VideoEndpoint`/`AvaloniaOpenGlVideoEndpoint` switch
  to a presentation clock override and **stop calling
  `_clock.UpdateFromFrame(vf.Pts)`** (see `833:SDL3VideoEndpoint.cs` and
  `769:AvaloniaOpenGlVideoEndpoint.cs`).

`NDIAVEndpoint` advertises a `NominalTickCadence` of 4 ms (the lowest cadence
of any registered endpoint becomes `AVRouter.EffectiveVideoTickCadence`).

`NDIAVEndpoint` does **not** implement `IClockCapableEndpoint`, so adding it to
the player does **not** auto-register `NDIClock` with the router. The user
must select it explicitly in the UI (`PlayerViewModel.ApplyClockSelectionToRouter`)
for it to become the master.

---

## 3. Issue #1 — Why video runs ahead with NDIClock master

### 3.1 Code path

`AVRouter.PushVideoTick` (`Routing/AVRouter.cs` ~line 1970+) takes one of two
branches per route:

```csharp
bool bypassScheduledGate = route.LiveMode
    || (ep.Video is ISenderMediaClockProvider sc
        && ReferenceEquals(Clock, sc.SenderMediaClock));

bool senderClockBypassPace = bypassScheduledGate
    && !route.LiveMode
    && ep.Video is ISenderMediaClockProvider sPace2
    && ReferenceEquals(Clock, sPace2.SenderMediaClock);
```

When the user picks NDIClock as the router master, `Clock == NDIAVEndpoint.SenderMediaClock`
holds, `bypassScheduledGate = true` and `senderClockBypassPace = true`.

The pacing block (lines ~2103–2158) does **not** use `PtsDriftTracker`.
Instead, it paces against wall time using `Stopwatch.GetTimestamp()` and the
delta between the previous and current frame's stream PTS:

```csharp
double dMediaMs = dPtsTicks / 10_000.0;          // stream-PTS delta in ms
const double paceEps = 0.02;
if (elapsedMs < dMediaMs - paceEps)
{
    double pushMs = EffectiveVideoTickCadence.TotalMilliseconds;  // 4 ms with NDIAVEndpoint
    bool nextTickOvershoots =
        dMediaMs > 2.0 * pushMs + 0.5
        && elapsedMs + pushMs > dMediaMs - paceEps;
    if (!nextTickOvershoots)
    {
        // hold for next tick
        _pushVideoPending[route.Id] = candidate;
        continue;
    }
    // fall through and deliver "slightly early"
}
```

### 3.2 Why this biases video forward

The motivation in the comment is correct: at a 10 ms tick cadence, 30 fps
(`dMedia ≈ 33.3 ms`) hits the deadline only on every fourth tick, so a strict
`elapsed < dMedia` test would lag one frame per second.

But the early-deliver branch fires whenever the *next* tick (`elapsedMs + pushMs`)
would land past the deadline. Concretely with 4 ms ticks and 33.33 ms per
frame:

- Re-evaluations happen at `elapsed = 4, 8, 12, …, 28, 32, …`.
- At `elapsed = 28`, `28 + 4 = 32 < 33.31`, so we wait.
- At `elapsed = 32`, `32 + 4 = 36 > 33.31`, so we **deliver now** at ~32 ms.

Result: every frame ships 0–`pushMs` early. Across many frames the average
bias is ≈ `pushMs / 2` ≈ 2 ms early per frame. After delivery,
`route.LastBypassPushTimestamp` is set to *now* (line ~2200), so the next
frame paces from the early reference. The bias compounds.

For 30 fps this is roughly:
```
2 ms bias × 30 fps = 60 ms/s of drift forward.
60 ms/s × 90 s     = 5.4 s of "video ahead" worst case.
```

In practice the per-tick jitter (Linux `Thread.Sleep`/`WaitOne` granularity is
~1 ms but variable, and the spin-wait tail is exact) reduces the average bias
toward `pushMs / 2 / 2`, putting real-world drift in the **0.8 – 3 s/90 s
range** — fully consistent with the user's observation of ~1 s.

The same code with the previous default (10 ms `InternalTickCadence`) was even
worse at 30 fps: up to ~9 s/90 s. NDIAVEndpoint's 4 ms hint makes things
better, but the bias is still systematic and non-zero.

Audio, meanwhile, is paced exactly at wall time by `PushAudioTick`'s
time-aware frame counter (`AVRouter.cs` ~line 1729: `framesPerBuffer ≈
sampleRate × elapsedSeconds + accum`). So audio Position advances at exactly
1× and video at slightly faster than 1× — the "video ahead" symptom is the
direct cumulative result.

### 3.3 Why the auto A/V drift loop does not catch it

`PlayerViewModel.ConfigureAutoAvDriftCorrection` sets:

```
InitialDelay = 15 s, Interval = 20 s, MinDriftMs = 20,
IgnoreOutlierDriftMs = 250, CorrectionGain = 0.5,
MaxStepMs = 25, MaxAbsOffsetMs = 200
```

The loop in `MediaPlayer.StartDriftCorrectionLoop` (line ~558) does:

```csharp
if (absDriftMs < options.MinDriftMs)         { /* no-op */ }
else if (absDriftMs < options.IgnoreOutlierDriftMs) { /* apply step */ }
else { /* log & ignore as outlier */ }
```

When the drift is genuinely large (the very case it was meant to fix), it is
silently suppressed by the `> 250 ms` guard. With ~30 ms/s of real drift, the
**first measurement at 15 s already shows ~450 ms** through the EMA (alpha =
0.4, raw 600 ms → EMA ≈ 240 ms first sample, then ~600 ms by sample 2). After
that, every sample is over 250 ms and the loop never steps.

The intent of `IgnoreOutlierDriftMs` is to ignore *transient* spikes (one-off
GC pauses producing a 500 ms blip), but it is implemented as an unconditional
gate. Without state tracking, the loop cannot distinguish "my system has a
large persistent error" from "I just saw a single outlier."

### 3.4 Recommended fix for issue #1

There are two complementary fixes:

**A. Remove the early-deliver bias in `senderClockBypassPace`.**

The comment is right that `elapsed < dMedia` alone lags at coarse cadences,
but the asymmetric *next-tick-overshoots* test should be replaced with a
*symmetric* one based on whichever boundary is closer. Practical patch:

```csharp
double pushMs = EffectiveVideoTickCadence.TotalMilliseconds;
double half  = pushMs * 0.5;
// Deliver when this tick's wall-elapsed is at least dMedia - pushMs/2;
// this minimises |elapsed - dMedia| with no systematic forward bias.
if (elapsedMs < dMediaMs - half)
{
    if (_pushVideoPending.TryRemove(route.Id, out var st))
        st.MemoryOwner?.Dispose();
    _pushVideoPending[route.Id] = candidate;
    continue;
}
```

This caps absolute pace error at `±pushMs/2` per frame (≈ ±2 ms at 4 ms
cadence) with zero mean. The same change works for the 10 ms cadence path
too — just with a wider error band.

A more thorough fix is to track an integrator: reference the *expected*
delivery wall-time from the first frame's wall stamp + cumulative stream
deltas, instead of "previous deliver wall-time." That is what `PtsDriftTracker`
already does for the scheduled path. The bypass path should grow an
equivalent integrator (or simply re-use `BypassNudgeDrift` for pacing as well
as nudge gating) so the per-frame ε does not compound.

**B. Replace `IgnoreOutlierDriftMs` with persistence-based outlier filter.**

Either:

* widen the cap proportionally to the elapsed time since startup
  (e.g. `cap = 250 + 10 ms × secondsSinceStart`), or
* require *N consecutive* samples above `IgnoreOutlierDriftMs` before treating
  them as real drift (a simple counter), or
* drop the cap and rely on `MaxStepMs` alone — the gain × max-step combination
  already bounds correction speed.

Independent of the choice, the corrective `clampedStepMs` already saturates at
`MaxStepMs`, so removing the outer cap cannot make a single iteration go
unstable. The cap was a defense against a wild EMA spike, which is not what
is happening here.

---

## 4. Issue #2 — Big correction steps with the Internal clock

When the router runs on `StopwatchClock`, `senderClockBypassPace` is false
(NDIClock is no longer the master), the scheduled gate fires and
`PtsDriftTracker.IntegrateError` does its job per frame with `gain = 0.08`
and a 2.5 ms dead-band. The user sees A/V stay in sync.

The "big correction steps" the user notices are the **`MediaPlayer` auto
A/V drift loop** itself, not the per-frame integrator:

* every 20 s the loop reads `GetAvDrift`, computes
  `step = -drift × gain` clamped to `[-25, +25] ms`, and writes that as a
  *step* on the input `TimeOffset` (not a smooth ramp);
* the gain is high (0.5) so even a 50 ms drift saturates the 25 ms cap;
* the input `TimeOffset` is a hard offset, applied immediately to the
  scheduled gate. There is no easing.

This produces visible "jumps" of up to 25 ms every 20 s on otherwise
synced playback.

### 4.1 Recommended fix for issue #2

* Lower `MaxStepMs` to e.g. 5 ms (one push tick worth) and
  raise `Interval` to compensate, or
* Make the corrector apply the step gradually inside the audio/video push
  path over the next N ticks rather than as a one-shot offset write.
* Also lower `CorrectionGain` to 0.1 – 0.15 so a single sample does not
  saturate the step cap. With the EMA smoothing inside `GetAvDrift`
  (alpha = 0.4) a low gain still converges in a few samples.

For everyday playback the simplest improvement is raising `MinDriftMs` to
8–10 ms (so brief sub-frame wobbles do not trigger any nudge) **and**
lowering `MaxStepMs` to ~5 ms. That makes corrections invisible to the user
even when they happen.

---

## 5. Other clock issues found while reading

### 5.1 `VideoPtsClock` cannot self-correct

`VideoPtsClock.UpdateFromFrame` ignores any drift below 500 ms in either
direction (lines 117–130) and only re-anchors on seeks. The comment justifies
this by pointing at `PtsDriftTracker` upstream:

> the AVRouter already runs its own cross-origin drift correction
> (PtsDriftTracker) on the presentation path, and chasing raw PTS forward
> here would form a positive feedback loop with that correction.

That holds when `VideoPtsClock` is the **endpoint's** clock and the *router*
master is something else (StopwatchClock / NDIClock / Hardware). The pull
endpoint then disables `_clock.UpdateFromFrame` entirely
(`SDL3VideoEndpoint.cs:833`, `AvaloniaOpenGlVideoEndpoint.cs:769`), so the
ignore-window is irrelevant.

But when **the user picks `VideoPtsClock` as the router master** (which the
UI permits via `RefreshClockChoices`), there is no upstream corrector and
`VideoPtsClock` free-runs purely on its `Stopwatch` between seeks. Sub-500 ms
drift accumulates indefinitely. This case is currently rare but not
explicitly forbidden, and the UI does not warn about it.

**Suggested change:** keep the no-correction mode when an `_externalCorrector`
flag is set; otherwise apply a smooth slew of e.g. ±0.5 ms/s back toward the
incoming PTS so the clock cannot diverge unbounded. Document the new
contract on the class.

### 5.2 `PtsDriftTracker.Reset` race

`AVRouter.ResetAllDriftTrackers` (line 899) iterates routes and calls
`route.PushDrift.Reset()`. It runs on the router thread (driven by
`SetClock` / `RegisterClock` / `UnregisterClock`).

`PtsDriftTracker.Reset` writes `HasOrigin = false`, `PtsOriginTicks = 0`,
`ClockOriginTicks = 0` non-atomically. The push-video thread reads these
fields in `PushVideoTick` (`drift.HasOrigin`, `drift.RelativeClock(...)`,
`drift.RelativePts(...)`) without any acquire fence. The class even
documents "no internal synchronization" in its XML doc.

The race window is small but real: a clock change during playback can leave
the push-tick computing relative PTS against `(HasOrigin=true, oldPts, 0)`
or similar half-state for one tick, until `SeedIfNeeded` re-seeds it.
Worst-case: the gate caches or bursts a frame for one tick.

**Suggested fix:** make `Reset` write to a single sentinel field
(`Volatile.Write(ref _state, default(state))`) or guard `Reset()` and the
read sites with the same lock the seqlock pattern already uses for `NDIClock`.

### 5.3 `BypassNudgeDrift` is not reset on clock change

`AVRouter.ResetAllDriftTrackers` resets `route.PushDrift`,
`route.LastBypassPushedStreamPtsTicks`, and `route.LastBypassPushTimestamp`,
but does **not** call `route.BypassNudgeDrift.Reset()`. The bypass-nudge
tracker is therefore seeded against the previous clock domain after a
clock switch. The very next nudge calculation uses stale origins; the
gate may drop or burst a frame until the discontinuity-reset path catches it
(none currently exists for the bypass path).

**Fix:** add `route.BypassNudgeDrift.Reset();` next to the existing reset
calls.

### 5.4 `NDIClock.UpdateFromFrame` floor-stuck dead zones

The decision **not** to clamp `ndiTimestamp` to `_monotonicFloorTicks` is
correct (clamping would inflate `_lastFramePositionTicks` and produce ~2 ms/s
of receiver-vs-sender rate drift). However, it does mean that whenever a
backward-jumping NDI timestamp lands (e.g. occasional out-of-order delivery
from the network path), `_lastFramePositionTicks` moves backward while the
floor stays. The `Position` getter then returns the floor for several reads
(until `lastFrame + elapsed` catches up), which looks like a "frozen clock"
to consumers.

This is hard to observe in the local-file NDI sender case because PTS values
are monotonic by construction, but worth a dedicated comment + log at
`Log.LogTrace` level so future debugging is easier.

### 5.5 `AvDriftCorrectionOptions.IgnoreOutlierDriftMs` and `MaxStepMs`

Already covered under §3.3 / §3.4. The defaults baked into
`PlayerViewModel` are appropriate for short-duration testing but actively
prevent corrections on long clips with persistent drift.

### 5.6 `PushVideoTick` early-tolerance is per-route, not per-frame-rate

`AVRouterOptions.VideoPtsEarlyTolerance` defaults to 5 ms — comfortable for
60 fps (one frame ~ 16.7 ms) but over a third of a frame at 24 fps. The
dead-band in `PtsDriftTracker.IntegrateError` is `tolerance/2 = 2.5 ms`,
narrow enough to chase quantization noise at 24 fps where each frame is at
±20.8 ms granularity. Combined with `VideoPushDriftCorrectionGain = 0.08`,
this is just barely stable at 24 fps. Worth either increasing the tolerance
to `~max(5 ms, 1/4 framePeriod)` automatically when `videoChannel.SourceFormat.FrameRate`
is known, or documenting that low-fps content benefits from
`VideoPtsEarlyTolerance = 8–10 ms`.

### 5.7 SDL3 / Avalonia disable internal `VideoPtsClock` updates under override

This is intentional, but it has a side effect: any consumer that reads the
**endpoint's** `VideoPtsClock.Position` while a router override is active
sees the clock frozen at the last frame presented before the override was
installed (because `UpdateFromFrame` is no longer called). The `Position`
interpolates with the Stopwatch but never re-anchors. Currently nothing
downstream reads it during overridden playback, but the staleness is a
trap for future endpoint code.

**Suggested change:** keep calling `UpdateFromFrame` even under override
(it is cheap), or expose the override's clock as the endpoint's `Clock`
property so external consumers always read the active clock.

---

## 6. Optimization opportunities

These are independent of the drift discussion but worth noting while the
clock paths are being touched.

### 6.1 `StopwatchClock.Position` lock

```csharp
public override TimeSpan Position
{
    get
    {
        lock (_swLock) return _offset + _sw.Elapsed;
    }
}
```

The lock guards the `_offset += _sw.Elapsed; _sw.Reset()` window in `Stop()`.
A lockless variant using a seqlock-style version (the same pattern already in
`NDIClock`) gives the push thread an uncontended hot path:

```csharp
// Writers (Stop): bump version (odd), update offset, reset sw, bump version (even).
// Readers: snapshot version, read offset & sw, re-check version.
```

`Clock.Position` is read 1–2× per route per video tick (and once per audio
tick), so on a 4 ms cadence with N routes the lock churn is N×250 Hz. Not
catastrophic, but free win for a hot path.

### 6.2 Re-sample `Clock.Position` once per tick

`AVRouter.PushVideoTick` re-reads `Clock.Position` for every route in its
`foreach`. With multiple push routes against the same clock (e.g. one input
fanned out to two NDI sinks) each route pays the lock acquisition again.
Hoist `var clockPos = Clock.Position;` to once per tick (above the
`foreach (var route in routes)`) — within one tick the few-microsecond
discrepancy across routes is below the dead-band tolerance.

### 6.3 `NDIAVEndpoint.AudioWriteLoop` deinterleave

```csharp
for (int s = 0; s < samplesPerChannel; s++)
{
    int srcBase = s * channels;
    for (int c = 0; c < channels; c++)
        planar[c * samplesPerChannel + s] = interleaved[srcBase + c];
}
```

Reads are sequential on `interleaved`, but writes are scatter — for 8 ch /
1024 frame buffers each iteration writes to 8 cache lines spaced
`samplesPerChannel × 4` bytes apart. For stereo there is a known SSE2/SSSE3
unzip kernel (`_mm_unpacklo_ps`/`_mm_unpackhi_ps`); for ≥ 4 ch a 2-pass blocked
transpose (`for (cb : channelBlocks) for (sb : sampleBlocks)`) hits L1 reuse.
At 48 kHz × 8 ch × 4 ms = 1536 floats this is a meaningful per-buffer cost.

`System.Numerics.Vector`/SIMD shuffles are an option; the simpler win is the
loop-blocked variant which is portable and has no intrinsics.

### 6.4 `_pushVideoPending` allocation profile

`ConcurrentDictionary<RouteId, VideoFrame>` is fine for a small set of routes
(typical ≤ 4). The `TryRemove`/`indexer-set` pattern avoids per-frame
allocation in steady state. `ResetAllDriftTrackers` does
`foreach (var kv in _pushVideoPending)` which allocates an enumerator (small
struct, but a tiny GC chunk) once per clock change. Replace with a single
`if (_pushVideoPending.IsEmpty) return; _pushVideoPending.Clear();` loop
that disposes memory owners — `Clear` is also racy with concurrent inserts,
so the safer pattern is:

```csharp
foreach (var key in _pushVideoPending.Keys)        // allocates a snapshot list
    if (_pushVideoPending.TryRemove(key, out var f))
        f.MemoryOwner?.Dispose();
```

Same allocation cost as today; mostly mentioned for completeness.

### 6.5 `NDIAVEndpoint.NotifyNdiClockOfRouterVideoPts`

Two cheap improvements:

* On the retained-handle path the call sequence is
  `_videoWork.EnqueueReserved(pending)` → `NotifyNdiClockOfRouterVideoPts`. The
  `pending.PtsTicks` value is already pinned in `pending`, so no extra work
  here, but `TrySetNdiFirstVideoPts` runs `Interlocked.CompareExchange` every
  frame even after the first is set. A `Volatile.Read(_ndiFirstVideoPts) ==
  long.MinValue` early-out before the CAS removes one atomic on the steady-state
  fast path.

* `NdiRebaseToFirstVideo` reads `_ndiFirstVideoPts` again with
  `Interlocked.Read`. After `TrySetNdiFirstVideoPts` we already know the value
  is set; a single read is enough.

Tiny fixes, but they sit on the hot router-thread path.

### 6.6 `PushVideoTick` `EffectiveVideoTickCadence.TotalMilliseconds`

```csharp
double pushMs = EffectiveVideoTickCadence.TotalMilliseconds;
```

`EffectiveVideoTickCadence` does a `Volatile.Read` + a `TimeSpan.FromSeconds`
calculation per call. It is invoked inside the per-frame pace branch.
Cache `pushMs` for the duration of `PushVideoTick` (the cadence cannot change
mid-tick).

### 6.7 `MediaClockBase.OnTimerTick` and reentrancy

Not a bug today, but `MediaClockBase` keeps a `System.Threading.Timer` that
fires `Tick` on a thread-pool thread regardless of which clock is the active
master. None of the in-tree clocks have subscribers in production paths, but
the timer still rounds up CPU on every running clock instance. Consider lazy
subscription: only start the timer when `Tick` has at least one subscriber.

### 6.8 Audio interpolation EMA allocations

`GetAvDrift` and `GetAvStreamHeadDrift` create `DriftEmaState` /
`StreamHeadDriftState` lazily via `GetOrAdd`. Steady state is alloc-free.
The inner `lock (state)` on each call is cheap; left here so future readers
do not "fix" it.

### 6.9 `WaitUntil` precision on Linux

`WaitUntil` coarse-sleeps via `WaitHandle.WaitOne(sleepMs)` and spin-waits
the final 3 ms. On Linux, `WaitOne(int)` boils down to `pthread_cond_timedwait`
with the kernel's HZ resolution (often 250 Hz / 4 ms). At a 4 ms NDI cadence
this means many ticks fall on the boundary or miss by ~1 ms in either
direction. The 3 ms spin tail covers this, but the spin burns CPU.

Worth investigating `Thread.Sleep(0)` / yield in the spin tail and a
`SocketAsyncEventArgs`-style timer (or simply `WaitForSingleObjectEx` on
Windows). For Linux specifically, `nanosleep` via P/Invoke gives sub-ms
resolution if you raise the process to `SCHED_FIFO`. Out of scope for the
current bug, but bears on how tight the bypass-pace can ever be.

---

## 7. Other interesting findings (not directly drift-related)

### 7.1 `MediaPlayer.CreateVideoRouteToEndpoint` overflow choice

For `IAVEndpoint` (NDI), `LiveMode = false, OverflowPolicy = Wait, Capacity = 12`.
`Wait` blocks the producer (the FFmpeg decoder) when the route subscription
fills, which is correct for preventing video bursts. With `Capacity = 12` and
30 fps, the decoder back-pressures after 400 ms of buffering — fine for
playback but worth noting that very fast machines (decoder bandwidth ≫ wall
rate) will see `Wait` throttle the decode loop, which is the intent.

### 7.2 NDIAVEndpoint `AnnounceUpcomingVideoFormat` resets shared state

`_timing.Reset()` and `_clock.ResetForNewSource()` fire from
`AnnounceUpcomingVideoFormat`, which `AVRouter` calls during `CreateRoute`
(line 1466). That is the right entry point for *new sessions*, but if a
single source pushes a profile change mid-stream (live source FPS change),
`AnnounceUpcomingVideoFormat` is **not** called — only at route creation.
For pure file playback this is fine.

### 7.3 NDI underrun-recovery feedback

`NDIAvTimingContext.ReserveAudioTimecode` snaps the audio cursor up to
`_latestVideoPtsTicks` whenever the gap exceeds 25 ms (per-buffer creep) or
500 ms (catastrophic snap). With the senderClockBypassPace bias from §3, the
video PTS observed by `_timing.ObserveVideoPts` is already biased forward.
Each underrun-creep nudge then drags audio forward as well, **partially
masking** the symptom on the wire (NDI receivers see the audio *and* video
timecodes both creep forward). On the user's local 90 s clip this produces a
smaller perceived A/V offset than the raw drift, but at the cost of
introducing ≤ 40 ms of audio jumps every time the creep fires (default
`MaxUnderrunPullPerCallMs = 40`). The user did not list this as a complaint,
but it is part of why the drift "feels like 1 s" rather than the 3 s the raw
math predicts.

Fixing §3.4 (issue #1) removes the input that makes
`ReserveAudioTimecode`'s creep necessary in this scenario, so the audio
timecodes will run unperturbed at sample rate.

### 7.4 Auto drift loop logs at `Debug` for "ignored outlier"

When the loop suppresses a measurement via `IgnoreOutlierDriftMs`, it logs at
`Debug`. The outer `applying video input time offset` log sits at
`Information`. So in default UI logging configuration the user sees no
hint that the loop *is running but ignoring everything*. Worth promoting that
"ignored outlier" line to `Information` (or at least `Warning` after N
consecutive outliers) so the silent-failure mode is observable.

### 7.5 `PlayerViewModel.ConfigureAutoAvDriftCorrection` runs once in the ctor

The settings cannot be changed at runtime from the UI. A future iteration
that exposes them as bindable settings would let the user tune in-flight,
which is far more useful than rebuilding the app for each gain experiment.

---

## 8. Suggested fix order

If addressing only what the user reported, the highest-impact, lowest-risk
changes are:

1. **§3.4 A — symmetric pace gate.** Replace the next-tick-overshoots branch in
   `AVRouter.PushVideoTick` (sender-clock bypass) with the symmetric
   `elapsedMs < dMediaMs - pushMs/2` test described above. This eliminates
   the systematic forward bias and brings the NDIClock-master scenario back
   to "wall-rate within ±2 ms / frame."

2. **§3.4 B — outlier guard.** Either widen
   `AvDriftCorrectionOptions.IgnoreOutlierDriftMs` (and `MaxAbsOffsetMs`) for
   the NDI case, or replace the gate with a "N consecutive outliers" counter
   so the corrector engages on persistent drift.

3. **§4.1 — softer correction.** Reduce `MaxStepMs` to ~5 ms and `Gain` to
   ~0.15 in the `PlayerViewModel` defaults so the user does not see "big
   jumps" when the corrector runs against the stopwatch clock.

4. **§5.3 — reset `BypassNudgeDrift`.** One-line addition in
   `ResetAllDriftTrackers`. Cheap insurance against future stalls after
   clock changes.

5. **§5.2 — make `PtsDriftTracker.Reset` thread-safe.** Volatile-writing a
   tagged state struct or guarding read/write with a `Lock` removes the
   torn-read window during `SetClock`.

6. **§6.1 / §6.2 / §6.6 — performance polish.** Lockless `StopwatchClock`,
   one `Clock.Position` sample per tick, cached `pushMs`. None of these
   change behaviour, just remove avoidable contention.

Items 7+ from §5 / §6 are quality-of-implementation improvements that can be
scheduled later.

---

## 9. Implementation checklist

Tracking checklist so the fixes can be applied in successive PRs without
forgetting any sub-step. **Status legend:** `[ ]` pending · `[x]` done in
current branch · `[~]` partial / superseded.

### High-impact correctness fixes

- [x] **A. Symmetric pace gate in `senderClockBypassPace`** — replace the
  asymmetric "next-tick-overshoots" branch in
  `AVRouter.PushVideoTick` (`Routing/AVRouter.cs` ~L2103–2158) with a
  symmetric `elapsedMs < dMediaMs - pushMs/2` test. Reduces per-frame error
  to `±pushMs/2` (still biased; see B).
- [x] **B. Long-term wall reference for sender-clock bypass pace** —
  replace `route.LastBypassPushTimestamp = Stopwatch.GetTimestamp();`
  after delivery with `route.LastBypassPushTimestamp += dPtsQpc;` so the
  next deadline is computed against an *ideal* wall reference instead of
  the last actual delivery wall. Eliminates compounding drift.
- [x] **C. Persistence-based outlier filter for the auto drift loop** —
  replace `if (absDriftMs >= IgnoreOutlierDriftMs)` hard cap in
  `MediaPlayer.StartDriftCorrectionLoop` (`Playback/MediaPlayer.cs` L583+)
  with a "require N consecutive samples above the cap" counter, so
  long-running persistent drift is corrected rather than silenced.
- [x] **D. Softer correction defaults in `PlayerViewModel`** — reduce
  `MaxStepMs` to 5 ms, `CorrectionGain` to 0.15, `MinDriftMs` to 8 ms in
  `UI/SPlayer/SPlayer.Core/ViewModels/PlayerViewModel.cs:137–146` so
  steps stay below one push tick worth of audio nudge.
- [x] **E. Reset `BypassNudgeDrift` on clock change** — add
  `route.BypassNudgeDrift.Reset();` to
  `AVRouter.ResetAllDriftTrackers` (`Routing/AVRouter.cs` ~L899) alongside
  the existing `route.PushDrift.Reset();` line.

### Thread-safety / robustness

- [x] **F. Make `PtsDriftTracker.Reset` torn-read-safe** — guard its three
  mutable fields with the same lock the seqlock pattern uses elsewhere,
  or wrap them in a single tagged state struct written via
  `Volatile.Write`. Add a brief XML doc note on which threads call which
  methods.
- [x] **G. New `AvDriftCorrectionOptions.OutlierConsecutiveSamples`
  setting** — defaults to 3; gates how many over-cap samples in a row are
  needed before treating the drift as real. Wires into fix C.
- [x] **H. Promote the "ignored outlier" log to `Information`** after the
  Nth consecutive ignore so silent-failure mode is observable in the
  default UI logging configuration.

### Performance polish

- [x] **I. Hoist `Clock.Position` to once per video push tick** — sample
  once at the top of `PushVideoTick` instead of per-route; cache in a
  local. Removes N×F lock acquisitions per tick where N = routes.
- [x] **J. Lockless `StopwatchClock.Position`** — replaced the `_swLock`
  acquire-on-read with a seqlock-style version field
  (`_snapshotVersion` toggled odd→even around writes,
  `Interlocked.Read` on `_offsetTicks`). Position reads now retry on a
  torn snapshot instead of taking a lock; writers (`Start`/`Stop`/`Reset`)
  remain serialized via `_swLock` and bump the version. Hot path is
  contention-free and the per-route hoist (item I) is preserved.
- [x] **K. Cache `EffectiveVideoTickCadence.TotalMilliseconds`** — read
  once per `PushVideoTick` invocation instead of per frame inside the
  pace branch.

### Lower-priority

- [x] **L. `VideoPtsClock` opt-in slew** — added
  `ApplySelfSlew` / `SelfSlewMaxMsPerSec` knobs and a
  `SeekThreshold = 500 ms` constant. When `ApplySelfSlew = false`
  (default; matches today's external-correction behaviour), small drift
  is ignored and only seeks ≥ 500 ms re-anchor. When
  `ApplySelfSlew = true`, sub-seek drift is bounded-slewed toward the
  PTS at ≤ `SelfSlewMaxMsPerSec` (default 0.5 ms/s) so the clock stays
  on PTS without an external corrector. Three new tests cover the three
  branches in `S.Media.Core.Tests/VideoPtsClockTests.cs`.
- [x] **M. SIMD/blocked deinterleave in `NDIAVEndpoint.AudioWriteLoop`** —
  scalar scatter loop replaced by a `DeinterleaveAudio` dispatcher:
  mono → `Buffer.BlockCopy`, stereo → `DeinterleaveStereo` using
  `System.Runtime.Intrinsics.X86.Sse` 8-float shuffles with a scalar
  tail, ≥ 3 channels → cache-blocked scalar transpose (block size
  tuned to keep working set in L1). Meaningful at ≥ 4 ch and removes
  scatter writes from the per-route hot path.
- [x] **N. Skip CAS on `_ndiFirstVideoPts` after first set** — added an
  `Interlocked.Read != long.MinValue` fast-path before
  `Interlocked.CompareExchange` in both
  `TrySetNdiFirstVideoPts` and `TrySetNdiFirstAudioPts` so once the
  per-track origin is set every subsequent frame skips the CAS.
- [x] **O. Lazy-start `MediaClockBase` tick timer** — `MediaClockBase`
  now tracks `_started` and re-arms / disables the internal
  `System.Threading.Timer` based on `Start`/`Stop` and the presence of
  `Tick` subscribers (reconciled under the existing tick lock). Idle or
  unsubscribed clocks no longer wake the threadpool every period.

### MediaPlayer state-machine fixes

- [x] **P. `MediaPlayer` not returning to `Stopped` after EOF** — root
  causes were (i) the `RaiseSourceEndedAfterDrainAsync` `try`/`catch`
  swallowing exceptions before `SetState(Stopped)` could fire,
  (ii) the drain loop's `StillRunning` check excluding `Paused`, so a
  pause near EOF could leave the player stuck in `Paused`, and
  (iii) drain tasks from a previous session clobbering a fresh session.
  Fixed by:
  1. Logging the exception and *always* attempting the final
     `SetState(PlaybackState.Stopped)` once draining completes;
  2. Including `PlaybackState.Paused` in the `StillRunning` predicate;
  3. Adding a monotonic `_playSessionId` field on `MediaPlayer`,
     incremented in `ReleaseSession`, captured by `OnEndOfMedia` and
     verified inside the drain task before mutating state.

### UI refactor (`SPlayer.Core`)

- [x] **Q. Persistent app settings** — new
  `AppSettingsService` writes user prefs as JSON at
  `~/.local/share/SPlayer/settings.json` (or platform equivalent) with
  atomic temp-file rename. Persists default-output set + per-output
  NDI A/V mode, player defaults (auto-advance, loop, default volume,
  whether to remember playlist overrides) and the full
  `AvDriftCorrectionOptions` block.
- [x] **R. Settings tab** — new `SettingsView` /
  `SettingsViewModel` exposed as a third `MainView` tab, alongside
  Player and Outputs. Sliders + checkboxes for the player defaults and
  the A/V drift correction parameters; "Restore defaults" + "Open
  settings folder" actions; surfaces the current settings path as
  selectable text.
- [x] **S. Default output selection** — `PlayerViewModel` consumes
  `SettingsViewModel.DefaultOutputs` on first row build per launch
  (and when the output pool changes) so newly-discovered endpoints
  appear pre-selected in the routing list.
- [x] **T. Per-playlist output overrides** —
  `PlaylistDocumentViewModel` carries an
  `OutputOverrideKeys` collection. Two new `PlayerViewModel` commands
  (`UseOutputOverridesForPlaylist`,
  `ClearOutputOverridesForPlaylist`) snapshot or clear the current
  selection, and switching playlists applies the override (or falls
  back to the global defaults). The active playlist tab gets a green
  dot in its header when overrides are in effect.
- [x] **U. Playlist entry management** — playlist toolbar adds
  *Clear*, an inline title editor and the per-playlist output-override
  buttons. The entry list now has a context menu and keyboard
  shortcuts (`Delete` removes, `Enter` plays, `Alt+Up/Down` reorders),
  plus a "now playing" row indicator (driven by a new
  `PlaylistEntry.IsPlaying` flag synced from `CurrentIndex`).

### Drift signal selection (PortAudio / Stopwatch master)

- [x] **V. Mark `HardwareClock` and `StopwatchClock` as
  `ISuppressesAutoAvDriftCorrection`.** Symptom: with `PortAudioClock`
  (or any other wall-clock-style master) selected, the auto drift loop
  was reading "drift" values that grew from ~900 ms to ~2.7 s over
  ~50 s and then plateaued — but the actual A/V playback was in sync.
  Root cause: `GetAvDrift` measures `aChannel.Position − vChannel.Position`,
  i.e. the audio decode head minus the video present head. When the
  master clock represents the **output side** (DAC time, wall time) the
  audio decode head sits the entire audio output pipeline depth (PA
  buffer + resampler + bounded fanout + chunked input ring) ahead of
  what the user actually hears, and that pipeline depth grows during
  startup and only stabilises once everything has been pre-fed.
  `NDIClock` already had this marker for the same reason (NDI sender
  timecode is also output-side). Marking the base
  `HardwareClock` (so all hardware-backed sample clocks inherit it,
  including `PortAudioClock`) and `StopwatchClock` lets
  `MediaPlayer.StartDriftCorrectionLoop` route through
  `GetAvStreamHeadDrift` for these masters, which baselines the first
  measurement and only tracks subsequent change.
- [x] **W. Defer `GetAvStreamHeadDrift` baselining until the pipeline
  has settled.** Even with the §V switch, taking the first sample after
  `InitialDelay` as the baseline still bakes the pre-roll transient into
  the zero point — for an audio output stack that takes 30–60 s to
  reach steady state (PortAudio + 44.1→48 kHz resampler is a typical
  case), the corrector then sees a slow ramp toward steady state and
  reads it as real drift. Updated `GetAvStreamHeadDrift` to require two
  consecutive samples within a 50 ms tolerance before locking in the
  baseline; until then it returns `TimeSpan.Zero`. The state struct
  gains `LastAudioMinusVideoTicks` / `HasPriorSample` fields. After the
  pipeline settles, the corrector engages with a clean zero point so
  any subsequent drift is real (not transient pipeline depth growth).
- [x] **X. Pull-callback audio path was over-pulling source frames when
  resampling.** Symptom (caught by user testing on local PortAudio +
  Avalonia video, after §§V/W silenced the false drift signal): every
  ~20 s of playback there was an audible "correction-like" stutter even
  though the drift log showed `raw=0.00 ms`.
  Root cause:
  `AudioFillCallbackForEndpoint.Fill` in `AVRouter.cs` called
  `channel.FillBuffer(srcSpan, frameCount)` with the **output** frame
  count — i.e. the PortAudio callback's hardware-rate buffer size —
  even when a resampler was attached. For a 44.1 kHz source feeding a
  48 kHz endpoint at 1024 frames/callback the resampler only needs
  ~941 source frames per call, but we were pulling 1024. The 83
  unconsumed source frames every callback accumulated in
  `LinearResampler._pendingBuf`/`_combinedBuf`, which:
  1. **Caused audible stutter** — the resampler's combined buffer kept
     hitting its capacity threshold and reallocating + copying on the
     PortAudio RT thread (e.g. growing from 4 KB → 16 KB → 64 KB …),
     each growth event a measurable GC/copy spike that produced an
     audible click that matched the user's "stutter every ~20 s" report.
  2. **Caused false A/V drift readings** — `aChannel.Position` advances
     by `frameCount / srcRate.SampleRate` after each `FillBuffer`, so
     the audio decode head was racing ahead at
     `srcRate / dstRate × wall time` (≈ +88 ms/s for 44.1 → 48 kHz).
     This is exactly the 0.9–2.7 s "growing drift" the user originally
     reported with `PortAudioClock` / `GetAvDrift`. Even after §§V/W
     hid the false signal, the underlying buffer growth remained.
  3. **Truncated the audio source by ~9 % of its run time** — for a
     90 s clip the source channel was being drained ~7 s before video
     EOF.
  Fix: mirror the push-tick path and ask the resampler how much input
  it needs:
  ```csharp
  int inputFrames = route.Resampler is not null
      ? route.Resampler.GetRequiredInputFrames(frameCount, srcFormat, endpointFormat.SampleRate)
      : frameCount;
  int filled = channel.FillBuffer(srcSpan.Slice(0, inputFrames * srcFormat.Channels), inputFrames);
  ```
  Source span and scratch buffer sizing were updated to match. With the
  fix the resampler's pending count stays at 1–3 frames in steady state
  (zero RT allocations), `aChannel.Position` advances at exactly wall
  time, and drift correction has no false signal to chase. Covered by
  `AVRouterPullAudioResampleTests.cs`, which drives the router's
  pull-fill callback with a recording channel and asserts the requested
  source frame count never exceeds the steady-state ratio (~941 frames
  per 1024 output, ±8 frames).
- [x] **Y. Audio fanout dead-locked when one of the sibling routes was
  disabled.** Symptom: with a video-only NDI sink alongside local PortAudio
  output, the moment the NDI route was switched to "video only" via
  `MediaPlayer.SetAveStreamSelection(VideoOnly)` the entire playback
  froze. Setting the same NDI sink back to "audio + video" worked again
  (audio went into a black hole because the underlying NDI sender was
  created with `clockAudio:false`, but at least nothing stalled).
  Root cause: `AudioFanout.RunAsync` in `AVRouter.cs` wrote to every
  subscriber and waited on a `Task.WhenAll`. `SetRouteEnabled(false)`
  flipped the route's `Enabled` flag, which caused the push-tick path
  (and the pull-callback path) to skip the route — but **the fanout
  itself still kept feeding the disabled subscriber's bounded ring**.
  After 4 chunks the ring was full; `WriteAsync` blocked indefinitely
  on a subscriber nobody was draining; the fanout stopped pulling from
  the input AudioChannel; the decoder back-pressured; everything
  downstream of the input froze. Any sibling route through the same
  input (PortAudio, in this scenario) stalled with it.
  Fix:
  1. Each fanout subscriber is wrapped in a small `Subscriber` slot
     with an `Enabled` bit that mirrors `RouteEntry.Enabled`.
  2. `AVRouter.SetRouteEnabled` propagates the flag onto the fanout
     via a new `AudioFanout.SetSubscriberEnabled(routeId, enabled)`
     hook so the change takes effect immediately.
  3. The fanout's writer now partitions subscribers into enabled vs
     disabled. Enabled ones get the original back-pressuring
     `WriteAsync`/`WhenAll`. Disabled ones get a non-blocking
     `TryWrite` (drop-on-full) so a non-draining sibling can never
     deadlock its peers. When *no* enabled subscribers remain we
     sleep one tick instead of busy-spinning the source.
  Covered by `AVRouterFanoutDisabledRouteTests.cs`, which wires two
  push endpoints to the same input, disables one route, pushes 32×512
  frames into the input and asserts (a) no `WriteAsync` deadlock
  inside a 5 s budget and (b) the surviving endpoint actually
  receives audio.

---

## 10. Testing notes

* The `S.Media.Core.Tests` project already has a `VideoPtsClockTests.cs`. The
  pace fix in §3.4 A is testable headlessly: register a mock
  `ISenderMediaClockProvider` endpoint, drive it on the push thread for N
  frames at a known dPts, and assert `|Σelapsed − Σstreamdelta| < pushMs/2`.
* The drift-loop fix is testable by injecting a fake `IAVRouter` whose
  `GetAvStreamHeadDrift` returns a configurable ramp; verify the corrector
  applies steps and that they do not stall above `IgnoreOutlierDriftMs`.
* For end-to-end NDI verification, the existing
  `MFPlayer.NDIAutoPlayer/Program.cs` already polls `TryGetAvDrift`. Adding a
  long-clip regression script that asserts |drift| < 100 ms after 90 s on
  both clock modes would catch regressions of either fix.
