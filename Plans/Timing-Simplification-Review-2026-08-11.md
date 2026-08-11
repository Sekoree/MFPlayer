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

### Started 2026-08-11 — and the plan changed once I looked at what each clamp actually does

**Step 1, done: `IPlayhead` now extends `IPlaybackClock`.** They were always the same shape — both
report `(epoch, time, running)`, both promise per-epoch monotonicity — and keeping them as unrelated
*types* meant every seam between them needed an adapter that did nothing but rename. Defaulted interface
members express the clock half over the playhead half, so no existing implementer changed, and
`ShowSession`'s `PlayheadPlaybackClock` (layer 6 of the table above) is **deleted**: a group's
`SessionClock` now takes `voice.Player.PlayClock` directly. Naming them apart is still worth something
— see `CurrentPosition`'s remarks on the three position-like properties — but that was a documentation
problem, not a type-system one.

**Step 2: I was wrong that three of the five clamps should be removed.** Reading each one properly, four
of the five have a second job that is load-bearing:

| Clamp | Also does | Removable? |
|---|---|---|
| `AudibleClientClock._maxSinceEpochTicks` | the resume point for terminal re-anchor recovery | no |
| `AudibleClientClock._maxAudibleTicks` | the only thing making the lead-compensated report monotonic — the lead estimate genuinely moves | no |
| `MediaClock._masterElapsedHighWater` | nothing else — pure defensive clamp | in principle, but it guards a *public* contract any caller's clock can break |
| `SessionClock._nowFloorTicks` | the value an automatic rebase PRESERVES when the reference re-anchors | no |
| `TimelineRunClock._position` | integrates a *polled* pause flag, so it cannot be made pure without a pause-transition hook on the host | not without an API change |

So "delete three clamps" would have made the system less safe, not simpler. **The actual defect was
never that the clamps exist — it is that they fire silently.** A clamp firing means the layer *below*
broke its contract; absorbing that quietly is precisely what makes a fault at layer 3 surface as a
symptom at layer 9, which is the debugging pattern this subsystem kept producing.

**Done instead: make every clamp nameable at its source.** `MediaClock` and `SessionClock` now count
same-epoch regressions and the worst one, `MediaClock` logs the first occurrence at Warning with the
offending master's type name (once per clock — a broken master is read hundreds of times a second), and
the counts surface through `MediaClockMetricsSnapshot.MasterRegressions` /
`SessionClock.ReferenceRegressions` for `ShowHost.Report()`. An announced epoch change is explicitly
*not* counted, so the diagnostic does not cry wolf at every seek and output flush — there is a test for
each.

Net effect on the original complaint: the chain is one layer shorter, and a contract breach anywhere in
it now has a number attached to the clock that broke it rather than a symptom attached to one nine
layers up.

Suites green: Core 840, Players 31, Session 379, HaCue2.Core 732, HaCue2 383, HaPlay 1026.

### Remaining shape

The three roles the chain should read as, and where each stands now:

1. **Hardware reference** — `AudibleClientClock`. The only place the physical facts (device time, DAC
   lead, device loss) are known, and the right place for a clamp. Unchanged, and should stay so.
2. **Group timebase** — `SessionClock` + `TransportTimeline`: the coordinate system cues, layers and
   outputs schedule against. Keeps its floor (it is also the rebase-preservation value), but the floor
   is now instrumented rather than silent.
3. **Cadence** — a tick source, not a clock. `MediaClock` is closest to this after §3a stripped it to a
   single grid, and §3b put every same-rate grid on one origin.

Layer 6 is gone. Layer 4 (`ProgramBusProducer` re-exposing `AudibleClientClock`) is the last
rename-only adapter and is the obvious next subtraction — it is a smaller job than layer 6 was, because
nothing outside `ProgramBusSource` depends on the distinction. Layers 5 and 9 stay two objects: they are
genuinely two different cadences (a voice's source rate and a canvas rate), and §3b showed the cost of
pretending otherwise is zero once they share an origin.

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

**3b. Share the driver — RECONSIDERED (2026-08-11), do NOT do it as scoped.** After 3a, a `MediaClock`
is "raise `VideoTick` on a rational frame grid", which looks like it wants one shared timing wheel. The
measurements from 3a undercut the case, and looking at what actually runs on a tick kills it:

- **The CPU case is now small.** 3a took a clock thread to 0.12 % of a core. Collapsing 16 threads into
  one saves ~1.8 % of a core — real, but not what "hard to manage precise timing" is about.
- **The tick handlers are not uniform, and one of them is heavy.**
  `ClipCompositionRuntime.OnSlaveVideoTick` runs `PumpOneFrame` — a full composite, measured at ~5.6 ms
  against a 16.7 ms budget on a real show. Three compositions on one shared thread is ~16.8 ms of work
  per 16.7 ms period: saturated. A shared wheel would turn independent pumps into a head-of-line queue,
  and one slow output would stall every other composition *and* every voice's frame delivery.

Sharing would therefore trade a small CPU win for a new class of correlated stall — the opposite of the
goal. If it is ever revisited, the shape has to be "share the wheel only among *light* consumers (voice
frame delivery), keep a dedicated thread per composition pump", and that is a lot of machinery for
~1.8 % of a core.

**What was worth taking from the idea — DONE (2026-08-11): phase alignment.** Each `MediaClock`'s grid
was anchored at its own `Start()` (`DriverLoopCore`'s `sessionStart = Stopwatch.GetTimestamp()`), so 13
voices at 60 fps ticked at 13 unrelated phases — a full frame period of avoidable spread in when
siblings select frames for the same master instant, decided by nothing but the order their clocks
happened to start in. `SharedGridEpoch` makes the grid one process-wide origin; a starting driver seeks
forward to the first boundary still ahead of it and joins the cadence already in progress.

Measured — 13 clocks at 60 fps started 7 ms apart (the way serial commits stagger a group fire), 1 s of
recorded ticks, bucketed into rounds:

| | within-round tick spread (median) | p95 | max |
|---|---|---|---|
| before (per-clock epoch) | 15.03 ms | 16.12 ms | 16.25 ms |
| after (shared epoch) | **0.55 ms** | **0.98 ms** | 1.47 ms |

A full frame period collapsed to sub-millisecond, for ~15 lines and no thread sharing. It also removes a
discontinuity for free: the grid index no longer restarts at 1 on every `Start`, so nothing derived from
it rewinds across a pause/resume (`ClipCompositionRuntime`'s freerun fallback carries a long comment
about having to avoid exactly that).

`LastVideoTickIndex` is now epoch-relative rather than session-relative. Nothing in production consumes
it — only a test assertion and two stale comments — but the change is worth knowing about.

Suites green: Core 838 (incl. `MFP_TIMING_TESTS=1`), Players 31, Session 379, HaCue2.Core 724,
HaCue2 383, HaPlay 1026.

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

**DONE (2026-08-11).** New `VoiceStartPolicy` (S.Media.Players), one instance per
`MediaPlaybackSession` — i.e. one per voice, since the clock, router and source id are created together
by one open and never change. It owns the graph, so the eight optional parameters became four
constructor arguments plus four genuine per-start options, and each discipline is a named method:
`GenlockFirstStart`, `GenlockResume`, `PreRollRelease`, `PlainStart`, `VideoOnlyStart`, selected by one
readable `SelectAudioDiscipline`.

The static `ConditionalWeakTable<MediaClock, IPlaybackClock>` is gone — it becomes the `_appliedGenlock`
field it always wanted to be. That is also strictly more correct: a process-global table keyed on a
clock could in principle alias across players, where a field cannot.

`AvPlaybackCoordinator` keeps only what it is actually good at: the stateless pause/stop/seek ORDERING
across a voice's graph (527 → 252 lines). The split is along the real seam — starting is stateful,
ordering is not.

`WaitForVideoBufferBeforeStartingAudio`'s `Thread.Sleep(5)` poll is replaced by
`VideoPlayer.WaitForFrames`, woken by a `ManualResetEventSlim` the decode thread pulses on every
enqueue. Each wait iteration is still capped (50 ms) because some predicates callers pass do not depend
on an enqueue — source exhaustion is read straight off the source, and hosts supply their own — so the
signal is the fast path and the cap is the safety net. This matters more than it looks: `Prepare` runs
BEFORE the group start barrier, so up to 5 ms of poll latency per voice was pure lateness in reaching a
barrier where every sibling waits for the slowest.

Suites green: Core 838, Players 31, Session 379, HaCue2.Core 724, HaCue2 383, HaPlay 1026.

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

**DONE (2026-08-11).** `SetClockMaster` and `SetTransportTimeline` are gone; `AcquireTransportTimeline`
is the only way to master a composition. The five fields collapsed to `_activeTimeline` +
`_timelineClaims`, and `PumpOneFrame`'s legacy branch went with them — the else branch is now just the
freerun fallback, with no `masterTime`/`sourceTime` juggling.

HaPlay migrated alongside, and it was cheaper than expected: `CueCompositionRuntime` is instantiated
only in tests (production uses just its static `CreateShowSessionCompositor` factory, which goes through
`ShowSession` and was already on the claim model), so `SetClockMaster` there was dead API on a
test-only surface. The wrapper now exposes `AcquireTransportTimeline`, and the four tests were rewritten
against it — including two new cases the old model could not express: a second timeline waiting behind
the first and taking over on release, and the last claim returning the composition to freerun.

The claim model is not merely "the one that survived": it is the only one that can hand the clock to the
next clip. `SetClockMaster`/`SetTransportTimeline` were first-wins *forever*, so a composition reused by
successive cues stayed slaved to the first group's stopped timeline until the composition was rebuilt —
which is what `ResetClockMaster` existed to paper over. That escape hatch stays, but only for its real
job: a composition PRESERVED across a document reload.

Suites green: Core 838, Players 31, Session 379, HaCue2.Core 724, HaCue2 383, HaPlay 1026.

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

**Traced properly (2026-08-11) — the poll is NOT the dominant term.** Walking the whole chain:

1. `PollClipEndAsync` releases the voice and raises `ClipNaturallyEnded` when
   `position >= End - EndMonitorGuard`, and the guard is **120 ms** (`ShowSession.Completion.cs:18`).
   With a 100 ms poll the release therefore lands somewhere in `[End-120 ms, End-20 ms]` — *early*, and
   jittering across a 100 ms window, not late.
2. `ShowHost.OnClipNaturallyEnded` → `CueExecutor.OnNaturalEndAsync` → `ContinueFromAsync` →
   `FireAsync(next)` (`CueExecutor.cs:843`).
3. `FireAsync` does the whole thing from cold: **open the media, commit, start**.

So the successor starts at `(out-point ± 50 ms) + open time`, and the open time is unbounded and
uncompensated — a cold 4K file is hundreds of milliseconds. **That** is the per-hop error, and the
100 ms poll is a minority of it. Chasing the poll first would have been optimizing the small term.

There is one path that already does this right and proves the machinery exists:
`CueRunner.FireCuesIndependentScheduledAsync` prepares every voice (open + commit + pre-roll + sync
present) and *then* holds them behind one caller-supplied absolute edge. Timeline events use it. Follow
chains do not.

**The fix is to make a Follow cue use that path**: treat the outgoing clip's out-point as the edge,
start preparation a lead ahead of it (the `PreEndNotify` / `ClipApproachingEnd` hook already exists and
is currently wired only for playlist crossfades), and release at the edge. Open time then happens
*before* the edge instead of after it, and the chain stops accumulating.

**DONE (2026-08-11), opt-in via `ProjectSettings.FollowLeadMs`** (default 0 = historical behaviour;
Settings → Show behaviour → "Follow lead").

- **Compile** (`ShowCompiler.FollowLead`): a media cue that actually hands on — `Trigger == Follow` or
  an explicit `EndTargetCueId` — gets `PreEndNotify = max(playlist crossfade, follow lead)`. A cue that
  simply stops needs no lead and does not pay for one.
- **Prepare** (`CueExecutor.TryBeginPreparedFollowAsync`): on "approaching end", resolve the successor
  with *exactly* the resolution the cold path uses (`ResolveFollowSuccessor`, honouring
  `DisabledCueFollow`), then hand it to `host.PlayTimelineMediaAsync` — the same seam timeline events
  use — with an edge that has not completed yet. Open, commit, pre-roll and sync-present all happen here.
- **Release** (`TryReleasePreparedFollowAsync`): the out-point completes the edge. **The edge is the
  existing natural-end event**, so this needs no new time source and can never fire early — the instant
  the successor starts is unchanged, only the open moved out of it.

Deliberate details worth knowing:

- The cursor advances at the **edge**, not at prepare. During the lead window the show has not moved on,
  and advancing early would make the standby readout jump seconds ahead — and leave it pointing at a cue
  that never started if the operator stops inside the window.
- The release **awaits the successor's start**, not just the edge completion. `TrySetResult` merely
  releases it; without the await, the natural-end handler could return before the hop had happened.
  (Found by a test, which failed with "collection was modified" — a genuine race, not a test artifact.)
- The setting is checked in the **executor** as well as the compiler. A playlist crossfade sets
  `PreEndNotify` on its own account, so "approaching end" reaches cues in shows that configured no lead
  at all; without the second gate those would have started preparing too. Also found by a test.
- A prepared follow is rolled back — and **awaited** — by `CancelTimelineRunsAsync`, alongside timeline
  runs, so a stop-everything cannot let a parked successor cross its edge while the active set fades.
  `OnStopped` cancels the single cue's prepared follow for the same reason.

Opt-outs, all "there is no fixed edge, or nothing to pre-open": a `PostWaitMs` on the outgoing cue, a
`PreWaitMs` on the successor, a successor that is not a media/text cue (groups, jumps, fades, patches
and actions resolve to decisions rather than media), and a lead of 0.

Eight tests in `CueExecutorTests` cover it (HaCue2.Core 724 → 732), including that the lead resolves the
same successor the cold path would, and that a stopped cue's parked successor is rolled back.

**The original write-up of this item, kept because the reasoning still applies to anyone tuning it:**
A Follow cue would begin opening its successor seconds before the current clip ends. That means:

- a follow chain holds two decoders and two producer leases during the lead window, where today it holds
  one — the voice budget has to account for it;
- an operator who stops or edits during the lead window is now cancelling work already in flight
  (`CueRunner` has the cancellation plumbing for this — `CancelFiresForCue` / `CancelFiresForGroup` —
  but the *visible* behaviour changes);
- `EndTargetCueId` jumps, playlists, armed runs and `PostWaitMs` all route through
  `OnNaturalEndAsync`/`ContinueFromAsync` and each needs deciding: does a jump target pre-open too?
  Does a non-zero `PostWaitMs` disable the lead (there is no fixed edge to schedule against once an
  authored wait intervenes)?

None of that is hard, but all of it is a product decision about how the cue player behaves. Deferred
pending that call rather than guessed at.

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

## 9. Would any hot path benefit from being rewritten in C? (asked 2026-08-11)

**Short answer: no — with one narrow exception that is a ~50-line shim, not a DSP rewrite.**

The question assumes the GC threatens timing because managed DSP allocates. Measured, it doesn't.

### 9a. The DSP already allocates nothing, and is already vectorized

From the committed BenchmarkDotNet artifacts (`BenchmarkDotNet.Artifacts/results/`, `[MemoryDiagnoser]`):

| Path | Allocated |
|---|---|
| `ChannelMap.ApplyAdditive` (all map shapes, incl. 6ch/8ch) | **0 B** |
| `FrameAlignedFloatRing` write+read (256 / 960 / 4096 floats) | **0 B** |
| `MatrixMix` — all four kernels | **0 B** |
| `AudioClipVoice.ReadInto` | **0 B** |
| Compositor `FadeBgra32` / `FadeNv12` | **0 B** |
| `CpuVideoCompositor.Composite` (720p, 1 and 4 layers) | 312 B (fixed — the `VideoFrame` + its release closure, not per-pixel) |

And the mix kernel is not leaving performance on the table for C to pick up: `FusedVectorized` is
5.9 µs against `PerCellRoutes` at 134 µs — **22× faster**, via `Vector<float>`. A C rewrite would be
writing the same SIMD by hand for, at best, parity.

### 9b. `ArrayPool<byte>.Shared` pools canvas-sized buffers fine — I checked

`HaCue2.Desktop.csproj`'s GC comment states the video path "allocates canvas-sized frame buffers
continuously … tens of megabytes a second straight into the large-object heap". I expected to confirm
that and instead disproved it. Rent/Return of one size, 60 times, measuring
`GC.GetAllocatedBytesForCurrentThread()` on .NET 10:

| Size | Allocated per rent |
|---|---|
| 64 KB / 1 MB / 2 MB | 0 B |
| 720p BGRA (3.5 MB) | 0 B |
| 1080p BGRA (8.3 MB) | 0 B |
| 4K BGRA (33 MB) | 0 B |

`ArrayPool<T>.Shared` pools cleanly all the way up. The compositor rents and returns
(`CpuVideoCompositor.cs:85`, released through `DisposableRelease.Wrap`), so the steady state does not
churn the LOH at all. Server GC was still the right call for this app, but **the stated rationale in
that csproj comment is wrong and should be corrected** so nobody optimizes against a phantom.

The one genuinely LOH-heavy path is `WarpMeshTessellator.Tessellate` (644 KB and Gen2 traffic at a
17×17 grid) — and `GlVideoCompositor` already caches it by mesh reference identity, so it runs on
control-point edits, not per frame. That cache is load-bearing; keep it.

### 9c. Where the GC actually threatens timing: managed code on a foreign RT thread

This is the real hazard, and it is not about allocation volume. A GC suspension only causes an *xrun*
when it catches managed code running on a thread the **audio server** owns. If the callback runs on a
thread the app owns, the pause costs the app's own audio and the ring absorbs it.

| Backend | Callback thread | Exposed? |
|---|---|---|
| PortAudio (default) | none — blocking-write feeder thread + `Pa_WriteStream` | **No** (fixed round 6) |
| PortAudio, `MFP_PORTAUDIO_CALLBACK=1` | PipeWire/JACK graph thread | **Yes** — the original bug, kept as an escape hatch |
| MiniAudio, most backends | miniaudio's own device thread (app-owned) | No — absorbed, `Periods=6` ≈ 60 ms cushion |
| MiniAudio, **JACK backend** | JACK graph thread | **Yes** — the PortAudio-class hazard, still open |

`MiniAudioNative.DataProc` (`MALib/MiniAudioNative.cs:125`) is `[UnmanagedCallersOnly]`, so native→managed
entry, then a `ConcurrentDictionary.TryGetValue` keyed by device pointer, then an indirect call through a
function pointer. Allocation-free, but it is managed code, and it is suspendable.

**This is the only place C would earn its keep**, and the scope is small: a native shim that keeps the
device callback entirely in C — drain an SPSC ring the managed feeder fills, fill silence on underrun —
so the graph thread never enters the runtime. The dictionary lookup goes away with it (it exists only to
avoid needing the offset of `pUserData` inside the opaque `ma_device`; a shim owns its own context
struct and reads it directly).

**Cheaper alternatives that need no C, in order of preference:**
1. Give MiniAudio the same blocking-write treatment PortAudio got (`ma_device` in
   `ma_device_type_playback` with `ma_device_start` + a writer thread), which removes the hazard for
   *every* MiniAudio backend rather than just JACK.
2. Detect the JACK backend at device-open and warn/steer to PortAudio, which is already the default.

Option 1 is the one to do. Only reach for the C shim if a measurement shows it is not enough.

### 9d. One non-GC finding while in here

`CpuVideoCompositor.Composite` does `Array.Clear(buffer, 0, _outputByteCount)` unconditionally
(`:87`) — 8.3 MB of memset per frame at 1080p, ~500 MB/s of pure memory bandwidth at 60 fps — and then
passes `dstUntouched: !anyLayerDrawn` to `DrawLayer`, which already knows how to write rather than
blend into untouched destination. When the bottom layer covers the canvas opaquely with `Source`
blending, the clear is dead work. This is the CPU fallback path (HaCue2 uses the GL compositor), so it
is low priority — but it is free to fix and it is the single largest constant cost in that path.

---

## Suggested order

Each step is independently shippable and each makes the next one smaller.

| # | Item | Status |
|---|---|---|
| 1 | Delete the superseded timing surface (§2a) | **done** — 473 src + ~845 test lines |
| 2 | Drop the unused tick grids (§3a) | **done** — 173 → 66 wakeups/s per clock |
| 3 | Dedicated dispatcher thread (§4) | **done** — p99 loop resumption 4.7 → 0.24 ms under starvation |
| 4 | Collapse composition clock mastering to claims (§6) | **done** — 5 fields → 2, HaPlay migrated |
| 5 | `VoiceStartPolicy` (§5) | **done** — static weak table gone, coordinator 527 → 252 lines |
| 6 | Shared grid epoch (§3b) | **done** — tick spread 15.03 → 0.55 ms across 13 voices |
| 7 | GC / native-C analysis (§9) | **done** — no DSP rewrite warranted; one MiniAudio-JACK shim identified |
| 8 | Label the unwired-but-planned surface (§2b) | open — cheap, no behaviour change |
| 9 | Timeline-accurate follows (§7) | **done** — opt-in `FollowLeadMs`, open moves before the edge |
| 10 | Merge `IPlaybackClock`/`IPlayhead` (§1, step 1) | **done** — chain one layer shorter, an adapter deleted |
| 11 | Make every monotonicity clamp nameable (§1, step 2) | **done** — clamps kept, silence removed |
| 12 | Remove the remaining rename-only adapter (`ProgramBusProducer` over `AudibleClientClock`) | open |
| 13 | `TimelineRunClock.Position` mutates on read (§8) | open — needs a pause-transition hook on `ICueExecutionHost` |

Everything here is verified only against the test suites. **The next thing that should happen is a real
show**, not more changes: the timing work is the kind whose failure mode is drift an assertion cannot
see. `FollowLeadMs` in particular is off by default and wants deliberate testing at a value like 2000
before it is trusted in performance.
