# Timing / clocking simplification review — 2026-08-11 (branch `next-experiment`, at 37082f61)

Scope: the timing, clocking and scheduling machinery of the framework as it is actually exercised by
HaCue2. Everything below was read in code and cross-checked with grep for real callers; "dead" means
**no production caller**, tests and `Tools/*Probe` excluded.

---

## 1. The headline problem: a nine-layer clock chain

For one sounding video voice in HaCue2, "what time is it" passes through:

| # | Type | File | What it adds |
|---|---|---|---|
| 1 | `PortAudioOutput` | `S.Media.Audio.PortAudio` | device stream time |
| 2 | `TerminalClockProxy` | `AudioPatchBay.cs:766` | dictionary lookup **under `bay._gate`, per read** |
| 3 | `AudibleClientClock` | `AudibleClientClock.cs` | per-client epoch, DAC-lead EMA, 2 CAS high-waters, wall fallback |
| 4 | `ProgramBusProducer` | `ProgramBusSource.cs:315` | re-exposes (3) as `IPlaybackClock` |
| 5 | `MediaClock` (per voice) | `MediaClock.cs` | base + epoch fold + high-water, **own driver thread** |
| 6 | `PlayheadPlaybackClock` | `ShowSession.TransportVoices.cs:636` | `IPlayhead` → `IPlaybackClock` adapter |
| 7 | `SessionClock` (per group) | `SessionClock.cs` | anchor + `_nowFloorTicks` + CAS rebase-retry loop |
| 8 | `TransportTimeline` | `TransportTimeline.cs` | master/source/cue coordinates, generation |
| 9 | `MediaClock` (per composition) | `ClipCompositionRuntime.cs:1219` | slaved again, **second driver thread** |

Then `LayerSlot.AlignmentTimeMapper` (`ClipCompositionRuntime.cs:914`) maps back out through
`TransportTimeline.SourceTimeAt`.

**Five of those nine independently enforce monotonicity** with their own high-water clamp
(`_maxSinceEpochTicks`, `_maxAudibleTicks`, `_masterElapsedHighWater`, `_nowFloorTicks`, plus
`CompositePlaybackClock._epochHighWater` when used). Each clamp is individually correct and individually
justified in its doc comment. Collectively they mean:

- A discontinuity has to be *announced and re-derived* five times before it is visible at the top.
- Any layer that gets it slightly wrong is **masked** by the clamp above it, so the symptom appears at
  layer 9 and the cause lives at layer 3. That is exactly the debugging pattern in the last several
  investigation rounds.
- Recovery from a real fault is bounded below by the slowest clamp, not the fastest.

This is the single biggest driver of "hard to manage when it comes to precise timing". It is not caused
by any one bad decision; it is caused by *layer 5 and layer 9 both being `MediaClock`*, and by adapters
(4, 6) that exist only to bridge two nearly-identical interfaces.

### Recommended shape

Collapse to **three** roles, with exactly one monotonic clamp:

1. **Hardware reference** — `AudibleClientClock` stays as-is. It is the only place the physical facts
   (device time, DAC lead, device loss) are known, and it is the right place for the clamp.
2. **Group timebase** — `SessionClock` + `TransportTimeline` stay. This is the coordinate system cues,
   layers and outputs schedule against. Drop its own floor and *trust* (1); assert instead of clamp.
3. **Cadence** — a tick source, not a clock (see §3).

Layers 4, 5, 6 and 9 become projections rather than clocks. Concretely: `IPlaybackClock` and `IPlayhead`
are the same shape (`Read()`/`ReadPosition()` returning `ClockReading`) with different names — merging
them removes adapters 4 and 6 outright.

---

## 2. Unreferenced timing surface — superseded vs. planned

About 1 400 lines of the timing surface have no production caller. But "no caller" splits into two very
different categories, and only one of them should be deleted. Checking `Doc/` settled it.

### 2a. Superseded — DELETED (2026-08-11)

| Type | Lines | Status |
|---|---|---|
| `CompositePlaybackClock` + `…Blend` | 393 | deleted |
| `MediaClockExtensions.SetMasterChain` (3 overloads) | 80 | deleted |

No design doc backs these. They merge clocks by priority, inferring a handoff from *which leaf happens
to be advancing* — and the framework converged on the opposite model: `SessionClock.SetReference`, an
explicit continuity-preserving swap. `CompositePlaybackClock`'s own remarks concede the point ("For a
stable playhead, prefer a single advancing primary"). They were also a documentation trap: `MediaClock`
and `IPlayhead` both pointed readers at `SetMasterChain` as *the* way to master a clock, so anyone
learning the clock model started at the one mechanism nothing uses.

Removed with their tests (`CompositePlaybackClockTests`, `MediaClockExtensionsTests`, the
`SetMasterChain` block of `MediaClockMasterTests`, the composite case in
`PlaybackClockAllocationTests`); `IPlayhead`'s doc now points at `SessionClock.SetReference`.
Core suite green at 836.

### 2b. Unwired but planned — KEEP

These read as dead by grep, but `Doc/HaPlay-MultiOutput-Sync.md` documents them as the deliberately
built Phase 1 / Phase 2b foundation of video-wall stitching ("built 2026-06-15"), and the live-ingest
set is the documented P7 model with a working probe. Deleting them would remove planned features.

| Type | Lines | Why it stays |
|---|---|---|
| `OutputSyncGroup` (+ `…Options`) | 216 | audio actuator of multi-output genlock (Option B, Phase 1) |
| `VideoPresentSyncGroup` | 244 | present scheduler for stitched canvases (Phase 2b) |
| `SyncPresentVideoOutput` + `ISyncPresentableVideoOutput` | 255 | the member contract for the above |
| `SourceTimeline` | 72 | P7 "live is a source scheduled against the master"; used by `Tools/LiveReceiveProbe` |
| `LiveTimelineDriver` | 95 | same; used by `Tools/LiveReceiveProbe` |
| `SourceSyncGroup` | 44 | documented answer to the recurring NDI A/V desync (`NDIModule` design note) |

**Recommendation:** don't delete — *label*. Move them under an `Unwired/` folder (or a
`S.Media.Time.Planned` namespace) with a one-line file header naming the design doc, so a reader can
tell "planned, built, not yet wired" from "load-bearing" without grepping for callers. That is the
actual cost these impose today, and labelling removes it without giving up the work.

`SetTransportTimeline` on `ClipCompositionRuntime` (`:721`) is in neither category — it is a superseded
duplicate of `AcquireTransportTimeline`, and goes with §6.

---

## 3. `MediaClock`: one thread per voice, ~70 % of its wakes serve nobody

`MediaClock` spawns a dedicated `AboveNormal` thread (`MediaClock.cs:428`). One per `MediaPlayer`
(`MediaPlayer.cs:944`/`:982`) plus one per composition (`ClipCompositionRuntime.cs:1219`). A 13-stem cue
with 3 compositions is 16 such threads.

Each thread services three deadline grids: audio 100 Hz, video ~60 Hz, position 30 Hz — roughly 190
deadline crossings per second.

**`AudioTick` has zero production subscribers. `PositionChanged` has zero production subscribers.**
(`AudioTick +=` and `PositionChanged +=` occur only in tests; `SubscribePositionChanged` likewise.)
Only `VideoTick` is consumed — by `VideoPlayer.cs:348` and `ClipCompositionRuntime.cs:1225`.

So ~130 of every 190 wakes exist to raise events nobody handles, and the 30 Hz one is not free: it calls
`CurrentPosition`, which takes `_gate` and walks the whole master chain in §1.

Two independent wins here:

**3a. Drop the unused grids — DONE (2026-08-11).** `AudioTick` is gone from `MediaClock` and
`IMediaClock`; `PositionChanged` survives as a re-anchor notification raised synchronously by
`Seek`/`Reset` but is no longer driven from a timer, which also removes the 30 Hz `CurrentPosition`
read (and its `_gate` acquisition and full master-chain walk) from the driver loop. The
`audioTickInterval` constructor parameter went with it, so the type can no longer claim a cadence it
does not serve.

Measured with 16 clocks at 60 fps (a 13-stem cue plus three compositions), 5 s sample:

| | wakeups/s per clock | CPU per clock | video ticks delivered |
|---|---|---|---|
| before | 173 | 0.25 % of a core | 4804 |
| after | **66** | **0.12 % of a core** | 4803 |

2.6× fewer driver-thread wakeups and ~2× less CPU, with tick delivery unchanged. The wakeup number is
the one that matters: each is a scheduler entry that can land late, and a show used to pay 173 of them
per second per voice for two events with no subscriber.

Suites green afterwards: Core 838 (incl. `MFP_TIMING_TESTS=1`), Players 31, Session 379,
HaCue2.Core 724, HaCue2 383.

**3b. Share the driver.** After 3a, a `MediaClock` is "raise `VideoTick` on a rational frame grid". That
does not need a thread each. One process-wide (or per-session) timing wheel — a single high-priority
thread holding a sorted deadline heap and dispatching to registered grids — removes 15 of 16 threads
and, more importantly, removes 15 of 16 independent sources of scheduler jitter. Voices on the *same*
frame rate then tick on the *same* instant by construction, which is a correctness improvement for group
fires, not just a performance one.

Caveat worth knowing: `ThreadPriority.AboveNormal` is a no-op for a normal-priority user process on
Linux, so the current threads are not actually prioritised. If you want real priority you need
`SCHED_FIFO`/`RLIMIT_RTPRIO` on a *small* number of threads — which is another argument for having one
timing thread instead of sixteen.

---

## 4. `SessionDispatcher` runs on the shared thread pool

`SessionDispatcher.cs:37` — `_pump = Task.Run(RunLoopAsync)`, and the loop `await work()`s each command
(`:194`). Consequences:

- Every show-critical commit (cue fire, start edge, level write, completion poll) is scheduled by the
  .NET thread pool, competing with UI async work, decode continuations, HTTP handlers and every
  `Task.Run` in the app. Under the thread-pool starvation the app demonstrably hits at GO, the hill-climbing
  injection delay is ~500 ms per thread — directly on the critical path of a cue start.
- Because the loop awaits, each command's continuation resumes on an *arbitrary* pool thread. The
  `AsyncLocal` keeps identity correct, but there is no thread affinity and no way to give the loop
  priority.

**DONE (2026-08-11).** The loop now owns a dedicated thread and installs a single-threaded
`SynchronizationContext`, so an `await` inside a command that does not say `ConfigureAwait(false)`
resumes on the dispatcher thread. Serialization is preserved exactly: `RunToCompletion` pumps that
context's continuations while a command is in flight and does not take the next command until the
current task completes.

`IsOnDispatcherThread` deliberately stays `AsyncLocal`-based. It is a *re-entrancy* guard, and the
session layer awaits with `ConfigureAwait(false)` almost everywhere — so a command mid-flight is often
running on a pool thread while the loop sits blocked on it, and a nested `InvokeAsync` from there must
still run inline. Switching to thread identity would make it post instead, onto a loop that cannot
reach it, and deadlock.

Measured — loop-resumption latency ("the loop resumed after an awaited command and started the next"),
200 commands, thread pool saturated with `ProcessorCount × 8` blocking hogs:

| | p95 | p99 |
|---|---|---|
| before (pool) | 0.016 / 0.018 / 0.015 ms | 4.72 / 3.97 / 5.07 ms |
| after (dedicated) | 0.014 / 0.022 ms | **0.24 / 1.20 ms** |

Three runs each; median is unchanged (~0.003 ms) and an idle-pool A/B shows no regression. The tail is
where it matters — that is the cue commit that lands late. Both configurations also show a ~105 s max
outlier under starvation; it appears identically on both and is an artifact of the harness's own
scaffolding being starved (`Task.Delay` rides the pool's timer queue), not attributable to either
dispatcher.

Suites green: Core 838, Players 31, Session 379, HaCue2.Core 724, HaCue2 383.

---

## 5. `AvPlaybackCoordinator` — 8 optional parameters, 5 branches, and a side table

`AvPlaybackCoordinator.PreparePlay` (`:67`) takes 8 optional parameters and returns one of five
different start actions depending on which combination is non-null. The state that decides between two
of those branches lives in a **static `ConditionalWeakTable<MediaClock, IPlaybackClock>`**
(`AvPlaybackCoordinator.cs:39`) because there is nowhere on the voice to put "this voice is genlocked to
the show clock".

The five branches are genuinely five different *start disciplines*:

1. genlock first-start — silent voice → show clock, deferred by measured pipeline lead
2. genlock resume — re-anchor, no defer
3. pre-roll release — sounding voice whose primary output is `IPreRollableOutput`
4. plain start
5. video-only (no audio router)

**Recommendation:** make this an instance type owned by the voice — `VoiceStartPolicy` or similar —
constructed once at arm time with the facts it needs, exposing `Prepare()` / `Start()`. The weak table
disappears (it becomes a field), the parameter soup becomes constructor arguments, and each discipline
becomes a small class with its own tests instead of a branch in a 190-line method. No behaviour change
required for the first cut.

Related, in the same file: `WaitForVideoBufferBeforeStartingAudio` (`:291`) polls with `Thread.Sleep(5)`
up to 8 s. On the prepare path that is off the dispatcher, so it's tolerable — but it is 1 600 sleeps in
the worst case and an event/`SemaphoreSlim` signalled by the decode thread would be both faster to
respond and cheaper.

---

## 6. `ClipCompositionRuntime` — three ways to master one clock

`ClipCompositionRuntime` is 3 202 lines with five nested classes. Its clock ownership alone has three
public entry points and five fields:

- `SetClockMaster(IPlaybackClock, IPlayhead?)` (`:685`) — legacy, only reached via HaPlay's
  `CueCompositionRuntime` wrapper
- `SetTransportTimeline(ITransportTimeline)` (`:721`) — **no production caller at all**
- `AcquireTransportTimeline(ITransportTimeline)` (`:754`) — the claim/lease model HaCue2 actually uses
- `ResetClockMaster()` (`:842`) — escape hatch for preserved compositions

backed by `_master`, `_timeline`, `_transportTimeline`, `_masterOwnedByTransportClaim`,
`_transportTimelineClaims` — with first-wins semantics that differ subtly per entry point.

**Recommendation:** keep the claim/lease model only. Delete `SetTransportTimeline`; migrate HaPlay's
`CueCompositionRuntime.SetClockMaster` (`UI/HaPlay/Playback/CueCompositionRuntime.cs:107`) to take a
claim. Five fields collapse to `_claims` + `_activeTimeline`. `PumpOneFrame`'s two-branch
transport-vs-legacy split (`:1364`–`:1393`) collapses with them.

Separately: the file wants splitting along its existing seams (mapping stages, `AcquiredOutput`,
`LayerSlot`/`SurfaceLayerSlot` are each self-contained and already ~500 lines apiece).

---

## 7. Follow / auto-continue accuracy is poll-bound

`SessionCompletionMonitor` (`SessionCompletionMonitor.cs:58`) is `Task.Delay(100 ms)` on the thread pool
→ `_dispatcher.InvokeAsync(PollCompletionWorkAsync)` → per-group `PollClipEndAsync`. Natural end,
pre-end-notify and therefore every follow/continue chain resolve on that grid.

So a follow hop costs up to 100 ms of poll + dispatcher queue latency + open time, **per hop**, and it
accumulates down a chain. Timeline mode is precise; follow mode is not — and nothing surfaces the
difference to the operator.

**Recommendation (longer-term, matches the cue-player goal):** the outgoing voice already knows its own
end in *its own* master coordinates. Compute the successor's due instant on that timeline at commit
time and schedule it through the same absolute-edge path
`FireCuesIndependentScheduledAsync` already implements, using the 100 ms poll only to *trigger
preparation*, never to define the start edge. That makes follow chains as accurate as timeline events
and removes the per-hop accumulation entirely.

---

## 8. Smaller, cheap items

- **`TerminalClockProxy` takes `bay._gate` on every clock read** (`AudioPatchBay.cs:766`), and its three
  members each take it separately. That gate is shared with terminal registration. Replace the
  dictionary with an immutable snapshot swapped on mutation (the codebase already uses this pattern —
  `_acquiredSnapshot`, `RepublishAcquiredSnapshot`) and the lock leaves the timing hot path.
- **`TimelineRunClock.Position` mutates on read** (`CueExecutor.cs:113`) — it accumulates the delta since
  the last look under a lock, so the value depends on who read last and it cannot be read concurrently
  without a gate. It wraps a `SessionClock`, which already solves exactly this with an immutable anchor.
  Re-express pause as an accumulated paused-offset on the anchor and `Position` becomes a pure,
  lock-free read.
- **`CueRunner._fireLock` is process-global** (`CueRunner.cs:44`) — one semaphore serialises fires across
  *all* cue lists. Correct for GO-vs-GO on one list; unnecessary coupling between independent lists.
  Per-list would let two lists GO simultaneously without one's slow open delaying the other's edge.
- `MediaClock.Stop` is literally `Pause` (`MediaClock.cs:337`) with a comment saying semantics may
  diverge. They haven't. Either delete one or make the distinction real.

---

## Suggested order

Each step is independently shippable and each makes the next one smaller.

1. **Delete the dead timing surface** (§2). Pure subtraction; nothing to verify but the build and suites.
2. **Drop the unused tick grids** (§3a). One file, immediately measurable in the cadence traces.
3. **Dedicated dispatcher thread** (§4). One file, contained, big effect on start-edge jitter.
4. **Collapse composition clock mastering to claims** (§6). Removes a whole branch from `PumpOneFrame`.
5. **`VoiceStartPolicy`** (§5). Removes the static weak table and makes the disciplines testable.
6. **Shared timing wheel** (§3b). Bigger, wants (2) and (3) done first.
7. **Merge `IPlaybackClock`/`IPlayhead`, drop the redundant clamps** (§1). The real prize; wants
   everything above.
8. **Timeline-accurate follows** (§7). Independent of 1–7; can be done any time.
