# HaCue2 — framework gap analysis

Status: audit of `MediaFramework/` (and the HaPlay-side pieces the split must relocate) against the
approved rev-3 UI design and the 30-item decision register in
`Plans/HaCue-Extraction-And-Project-Audio-Patch-Plan.md`
Date: 2026-08-01
Method: six parallel code audits over the current tree at `60a02f49`, plus recovery and inspection of
the abandoned first attempt's framework work (see §0).
Companions: `Plans/HaCue-Extraction-And-Project-Audio-Patch-Plan.md` (the plan),
`Plans/MockUps/HaCue2/HaCue2 — UI design, all screens.html` (the approved UI),
`Plans/HaCue-Feature-Ideas.md` (backlog).

Every claim below is anchored to `file:line` in the tree as it stands. Where this document and the
extraction plan disagree, **this document reports what the code actually does** — the plan's
prose was written before several of these areas were read closely, and §8 lists the specific
statements that need correcting.

---

## Executive summary

**Five things matter more than the rest of this document.**

1. **Most of the audio-patch framework work already exists.** The abandoned first attempt built
   `AudioPatchBay`, the V-wide program bus, the producer-lease clock, the session program-audio
   target, the preview monitoring seam, and ~1100 lines of tests — 22 framework files, +3342/−654.
   It survives only on an **unreferenced commit reachable through the reflog**. Tag it today (§0).
   Phase 3 of the plan is not greenfield; it is recover → review → rebase.

2. **The largest genuine gap is not audio — it is the document load path.** `ShowSession`'s
   `LoadDocumentCoreAsync` unconditionally tears down every transport group, which destroys every
   group's GO cursor *and stops every playing voice in every list*. HaPlay reloads the document on a
   300 ms debounce after any structural edit and flushes before every fire. So rev-3's **multi-list
   transport (register item 5)** and "editing never blocks playback" are, today, mutually exclusive
   with the app's normal editing loop. This needs an incremental/non-destructive load and it is the
   single biggest new framework work item (§2).

3. **Audio level metering does not exist in the framework**, and the one app-side implementation is the
   wrong shape: it meters per *voice-to-device edge* and then collapses every tap to a single max per deck.
   There is no graph node corresponding to a logical output, so "the level on *Lobby*" is currently
   unanswerable. The rev-3 design shows program meters in three places. Related: the diagnostics screen's
   **clock epoch, "advancing" state and submit-to-output latency have no data source** either — the values
   exist on live objects but nothing snapshots them (§6.1, §6.2).

4. **The control-input layer is in materially better shape than the plan claims.** The plan says MIDI/
   OSC device ownership lives in a HaPlay ViewModel; in fact `S.Control` already owns enumeration,
   open/close ref-counting, the monitor-record stream, resolution and persistence. What is actually
   stuck in HaPlay is ~120 lines of lifecycle glue plus two duplicate learn implementations (§5).

5. **Several rev-3 v1 decisions have no framework support at all** and were not costed in the plan:
   per-output test patterns, the audition *video* surface, opacity/outbound automation lanes,
   continuous-controller→parameter bindings, per-endpoint test messages, per-output video telemetry, the
   in-memory log sink, and "Copy report". None are deep, but together they are a phase's worth of work
   (§3, §4, §5, §6).

**What is in better shape than expected:** per-output video mapping (already fully wired, warp-to-
projector/clean-to-TV works today), the visualizer decoupling (the composition flag is already dead
code), per-transition crossfades (needs zero framework change), the per-cue `Disabled` flag (exists in
the framework, missing only in the app model), the Program/Monitoring classification seam, a single
consistent `Microsoft.Extensions.Logging` pipeline (only the in-memory sink is missing), a real
document validator to seed Project status, and a shared media cache that is already shared.

**Two owner questions answered after the first pass** (2026-08-01): **LTC** is in scope and tractable —
audio capture already exists, the decoder is independent pure DSP, and the one thing worth doing *now* is
making the timecode contract transport-neutral before anything depends on it (§5.4a). **`ShowSession`
ownership** — the fault line is not HaPlay-vs-HaCue2 but **engine-vs-cue-semantics**: the document is
already a shared engine contract with two mappers targeting it, so forking would duplicate the
concurrency-critical engine to solve a cheap problem. Recommendation is a shared engine with
HaCue2-owned cue semantics, and additive-nullable document evolution — a pattern already proven in-tree
(§10).

---

## Owner decisions from this audit (2026-08-01)

Four questions this audit raised were put to the owner and answered. Three of them **expand scope**
relative to what the plan assumed; all four are binding.

| # | Question | Decision | Consequence |
|---|---|---|---|
| D1 | HaPlay's trajectory (§10.5) | **Keeps evolving alongside HaCue2** | The §10 recommendation stands and hardens: a shared engine with a *real* cue/engine seam, because both apps must change independently. Forking is off the table; seam quality is now load-bearing, not optional. |
| D2 | Wedged pacing master (§1.3) | **Watchdog + pre-emptive detach** | New framework work in the riskiest area: detect a stalling master and hand the clock off *before* `RunLoop` faults. Adds a Phase 3 item that the archived bay does not have. |
| D3 | Outbound OSC/MIDI ramps (§3.2) | **In v1** | The automation-lane work is three features, not two: internal audio, internal video, and an outbound sender with its own rate/landing/coalescing contract. |
| D4 | Control-surface feedback (§5.1) | **Extract with `HaControl.Input`** | Phase 2 grows: the feedback half (LED, scribble strip, motor faders) leaves HaPlay's Control workspace alongside the input half. |
| D5 | Multiple visualizer cues per composition (§4.2) | **Yes, must be possible** | The one-slot-per-composition limit becomes real framework work: key slots by cue id, one surface per source. |
| D6 | Disabled cue in an auto-follow chain (§2.2) | **Project-level setting** | New project setting (skip onward vs stop the chain); the framework currently only stops. |
| D7 | `MaxReleasingVoices` (§3.3) | **Whatever is best long-term** — recommendation: raise to a small bounded N | Delegated; see §3.3 for the reasoning and the proposed default. |
| D8 | Audition channel count (§4.5, plan open Q17) | **Follow the selected audition output's channels** — never hardcoded | Settles plan open question 17: not "audition into the logical channels", but "match the configured audition device". |
| D9 | `hacue2 --check` CLI verb naming (§6.6) | **Draft / backlog for now** | Headless status still ships; the verb name is not fixed. |
| D10 | Arch-test exemption for `UI/**/*.Tests` (§7.3) | **Exempt**, as `Test/` already is | One line in the new UI scope. |
| D11 | CI branch filter (§7.4) | **Update to cover the extraction branch** | **Done** — `build.yml` now triggers on `master`, `next-*` and `cue-*`. |

Because D2–D4 all add work, the honest read is that **Phase 2 and Phase 3 each grew**. None of the
additions are architecturally risky in isolation, but see §9 for where they land, and note that D2 in
particular touches `AudioRouter`'s fault path — the one place this repo has repeatedly been bitten.

---

## 0. Recover before you build

The first extraction attempt (P1–P6, rolled back 2026-08-01) did substantial framework work that the
plan still describes as unbuilt. It is **not on any branch**: `origin/next-fix-enhance-round` was reset
to `5e1f8ee5`, so the work survives only at reflog commit **`bdf27ffd`** ("WIP archive: HaCue standalone
state before rolling back to the plan-stage commit"), with the usual ~90-day reflog horizon from
2026-08-01.

```sh
git tag hacue-archive-2026-08-01 bdf27ffd     # do this first
git push origin tag hacue-archive-2026-08-01  # and this
```

### What it contains (framework only: `git diff 27ceea99 bdf27ffd -- MediaFramework/`)

| File | Δ | What it is |
|---|---|---|
| `S.Media.Routing/Audio/AudioPatchBay.cs` | +513 | the bay |
| `S.Media.Routing/Audio/ProgramBusSource.cs` | +451 | V-wide program bus + per-producer leases |
| `S.Media.Routing/Audio/AudibleClientClock.cs` | +304 | clock/latency machinery extracted from `SharedAudioOutput` |
| `S.Media.Routing/Audio/SharedAudioOutput.cs` | −301 | shrunk accordingly |
| `S.Media.Session/ShowProgramAudio.cs` | +179 | `IShowProgramAudioTarget`, patch-bay impl, monitor output |
| `S.Media.Session/ClipAudioOutputRuntime.cs` | −310 | **deleted** — the dead decoy the plan flagged |
| `S.Media.Routing/Audio/AudioRouter.{cs,FusedMix,OutputPump}` | +157 | wide-matrix + the blocker fix below |
| `S.Media.Session/ShowSession{,.LiveEdits}.cs` | +195 | program-audio integration, `ApplyActiveLogicalSendsAsync` |
| `S.Media.Session/VoicePlayer.cs` | +45 | preview → borrowed monitoring lease |
| `S.Media.Session/ShowDocument.cs` + validator | +28 | `ShowClipLogicalSend`, `ShowClipBinding.LogicalSends` |
| `S.Media.Arch.Tests/ArchitectureTests.cs` | +85 | reference rules for the new libraries |
| `Benchmarks/…/WideMatrixBenchmarks.cs` | +100 | the plan's Phase 0 measurement (8/16/32/64 sweep + program-sum-at-maximums) |
| 5 test files | +1114 | see below |

**Drift is effectively zero.** `git diff --stat 27ceea99 HEAD -- MediaFramework/` shows only
`AudioRouter.OutputPump.cs` (+33) and `AudioRouterPumpLifecycleTests.cs` (+58) — and both came *from*
this archive, carried forward in `c1a27e9f`/`5e1f8ee5`. The archived framework work rebases onto today's
HEAD nearly cleanly.

### Test coverage already written

`AudioPatchBayTests` — `LiveTopology_AddUpdateRemoveTerminals_WithoutInterruptingVoices`,
`ClockMaster_AtForeignRate_IsANamedValidationFailure`,
`ForeignRateSecondary_RequiresAndUsesTheResamplerFactory_AndOwnsOnlyTheWrapper`,
`Validation_RejectsBadPatchesDuplicatesAndSecondMasters`,
`Dispose_InvalidatesOutstandingProducerEndpoints`,
`AcquireProducer_RacingDispose_NeverReturnsALiveEndpointAfterDispose`,
`MonitorInput_ReachesOnlyItsTerminal_AndBypassesTheProgramPatch`,
`MonitorInput_ValidatesTerminalAndMix_AndIsRevokedByBayDisposal`,
`ReplaceTerminal_HotSwapsAWedgedLine_WithoutInterruptingTheOtherLine`,
`ReplaceTerminal_ValidationFailure_LeavesTheOldLineAttached`,
`ReplaceTerminal_ClockMaster_HandsProducerClocksToTheNewMaster`.

`ProgramBusSourceTests` — two-producer summing, underrun-is-silence-never-stale, `UpdateSends` one-chunk
ramp, per-producer dispose isolation, overflow drops oldest and counts, send validation, empty-bus
silence, end-to-end through router patches to two terminals.

`ProgramBusClockTests` — producer clock starts at zero and follows the master, late-acquired producer
still starts at zero, flush reanchors with a fresh epoch and no backwards read, producer lead counts ring
backlog + downstream measurement, fallback clock without a context, `WaitForCapacity` blocking/waking,
and end-to-end survival of master removal.

### Recommendation

Make **recovery and review** the first task of the audio phase, not a reimplementation. Concretely:
cherry-pick the framework subset onto a branch, run the tests, re-read the bay against §1's open
items below, and only then decide what to change. Rewriting it from the plan text would discard a
working, tested implementation *and* the two subtle fixes described next.

---

## 1. Audio — the patch bay

### 1.1 What the current tree has (without the archive)

The router has roughly 70% of the mechanics but **none of the plan's topology**. Today's shape is
literally the `P×R` form the plan rejects: one `AudioRouter` **per producer**
(`S.Media.Players/MediaPlayer.cs:874`), each opening its own terminal per route
(`S.Media.Session/ShowSession.cs:185-190`, `:206-265`), plus a second router per terminal inside
`SharedAudioOutput` (`S.Media.Routing/Audio/SharedAudioOutput.cs:61`).

Present and reusable:

- **Dense fused matrix pass.** `ApplyMatrix` installs one single-cell route per non-zero cell
  (`AudioRouter.Matrix.cs:39-110`); the run loop fuses co-routed single-cell routes per
  `(source, output)` into one dense matrix-vector pass (`AudioRouter.FusedMix.cs:47-104`, `:126-296`)
  with AVX2/SSE paths at source widths 8 and 4 (`:186-210`). A V-wide bus → terminal is exactly one
  fused group.
- **Live graph mutation.** `AddOutput` starts the pump when running (`AudioRouter.cs:387-388`, test
  `AudioRouterPumpLifecycleTests.cs:31,50`); route state is lock-swapped immutable and read lock-free
  per chunk (`:1509`). Gain-only edits via `SetRouteGainById` (`:667`) are allocation-free with a
  click-free per-chunk ramp (`:1743-1764`, `FusedMix.cs:234-296`).
- **Output health.** `GetPumpStats`/`TryGetPumpStats`/`GetAggregatePumpStats` (`:724,738,763`),
  `OutputPumpStats` with `Enqueued/Processed/Dropped/Abandoned/ReadyEvictions/IsStuck/InFlight`
  (`:1787-1813`), plus `PumpPressure`, `OutputErrored`, `Faulted`, `StuckOutputPumpIds`, `MixTiming`
  (`:138,153-170`). Essentially complete.
- **`ClientInput`** (`SharedAudioOutput.cs:196-641`) is the right shape for a producer lease and is the
  seed the archive actually used: bounded bus, backpressure on consumption (`:556-598`), epoch/re-anchor
  (`:466,508`), DAC-lead low-pass (`:422`).
- **The resampler factory already exists one layer up**: `IMediaRegistry.CreateResamplingOutput`
  (`S.Media.Core/Registry/IMediaRegistry.cs:111`, impl `MediaRegistry.cs:298`, wired at
  `S.Media.Decode.FFmpeg/FFmpegModule.cs:26-27`), with a working precedent at
  `ShowSession.Taps.cs:43-63`. No framework change needed to inject it.

### 1.2 Traps that are easy to rediscover

- **Keepalive is mandatory.** With sources registered and all exhausted, the run loop sets
  `CompletedNaturally` and stops itself (`AudioRouter.cs:1522,1605-1612`). `SharedAudioOutput` avoids
  this with a permanent `SilenceSource` (`:72-74,643-653`) plus `FlushOutputsOnNaturalEof = false`
  (`:66`). A bay that omits either dies silently when the last cue ends.
- **`chunkSamples` is immutable for a router's life**; only Hz can move, and
  `ReconfigureSampleRate`/`…WhileRunning` (`AudioRouter.cs:878,904`) require *every* registered source
  and output to already report the new rate (`:917-931`). So a project mix-rate change is a full
  terminal rebuild — which is exactly why rev-3's "Apply & restart audio" (register item 14) is the
  right UI, and it should be stated as a framework constraint rather than a UX preference.
- **`ApplyMatrix` always swaps `Routes`** via `builder.ToImmutable()` (`Matrix.cs:108`) even when only
  gains changed; the new array identity invalidates `_mixPlanIdentity` (`FusedMix.cs:49`) and forces a
  full mix-plan rebuild — `List`, `Dictionary`, `HashSet` and two `float[src*dst]` per group
  (`:52-99`) — **on the router thread**. Steady-state gain/mute must go through `SetRouteGainById`;
  `ApplyMatrix` is for structural change only. (Or fix `Matrix.cs:108` to skip the swap when pass 2
  added/removed nothing.)
- **`MinFusedMatrixCells = 4`** (`FusedMix.cs:24`): a pair with ≤3 live cells falls back to per-route
  passes. The plan's "always one dense pass" is false for sparse patches — harmless for cost, but the
  budget arithmetic should not assume it.
- **Auto-promotion will happily master the clock to a *resampled* terminal.**
  `ResamplingAudioOutput.Wrap` returns a type implementing both `IClockedOutput` and `IPlaybackClock`
  (`S.Media.Decode.FFmpeg/Audio/ResamplingAudioOutput.cs:147-162`), and `AutoWirePrimaryOutputIfNeeded`
  (`AudioRouter.Playback.cs:80-113`) has no marker to refuse it — the `IAdaptiveRateWrappedOutput`
  marker (`:87`) covers only drift wrappers. Either add a "rate-adapted" marker (public API change) or
  set `AutoWirePrimary = false` and drive `SlaveTo`/`RetargetSlaveClock` explicitly. Note `SlaveTo`
  throws while running (`AudioRouter.cs:977-978`); only `RetargetSlaveClock` (`:813`) is live-safe, and
  auto-promotion is disabled mid-stream (`Playback.cs:97-103`) so a master added after `Start` is never
  promoted.
- **`ResamplingAudioOutput` under-reports latency**: `SubmitToOutputLatency => AudioOutputLatency.Of(Inner)`
  (`:66`) omits the swr buffered/filter delay (`:62-65`), and it **requires identical channel counts**
  inner vs router (`:34-35`), so the V×R matrix must land on the wrapper's router-side format. Its
  `Flush` disposes and recreates the swr context (`:96-109`), so every pause/seek pays a converter rebuild.
- **`ClipAudioOutputRuntime` is confirmed dead** (only self-references; prose mentions in the plan
  `:353,1113` and `Doc/HaPlay-MultiOutput-Sync.md`). It is **not** a usable seed — its topology is one
  router per terminal at the *output's* rate (`:51,54`) with clips routed directly per cell
  (`:88-101,292-307`), i.e. the `P×R` form, no lease, no matrix reconcile, no monitoring. The archive
  already deletes it; keep that deletion.
- **`SoundboardGrid.cs` is a second dead decoy** in the same folder — 267 lines,
  **zero external references** anywhere in `MediaFramework/` or `UI/`. It was not previously flagged.
  Together the two account for 577 lines of dead weight in `S.Media.Session`, both shaped like
  responsibilities the session layer was once expected to own. Delete it with the other one during the
  session work, or it will be read as evidence that the framework owns soundboard concepts (§10).

### 1.3 The two blockers, and their status

The current-tree audit identified two hard blockers. **The archive fixed the first and only partly
addresses the second.**

**Blocker 1 — deferred pump disposal ran on the run-loop thread. FIXED in the archive.**
`RemoveOutput` on a running router defers the pump to `_pumpsAwaitingDispose` and the run loop disposed
it inline at the top of the next iteration (`AudioRouter.cs:1481`), where `OutputPump.Dispose` joins 2 s
then cancels and joins 1 s more (`OutputPump.cs:349-359`) — up to **3 seconds of dead air on every
terminal** when detaching a wedged one. The archive moves that dispose to the thread pool with the
rationale recorded inline ("a drainer wedged in a native Submit holds that join for the full cap …
inline it would stall this mix loop and starve every other output"), and additionally makes
`MarkOutputPumpStuck` identity-checked under the gate so a completed background teardown cannot
mis-quarantine a freshly hot-swapped line. This is precisely the fix the plan's quarantine story needs,
already written and covered by `ReplaceTerminal_HotSwapsAWedgedLine_WithoutInterruptingTheOtherLine`.

**Blocker 2 — a wedged *pacing master* faults and permanently kills the router. DECIDED (D2): build the
watchdog.**
If the wedged terminal is the pacing master, `WaitForCapacity` returning false makes `RunLoop` raise a
fault and stop the whole router (`AudioRouter.cs:1483-1503`); a cancelled/timed-out stop sets
`_runThreadStuck` and the router becomes permanently non-restartable (`:1006-1009,1227-1246`, tests
`AudioRouterFaultTests.cs:39,66`). Recovery for *removal* exists — `OutputSlavedRouterClock` falls back
to wall clock when the slaved id no longer resolves (`OutputSlavedRouterClock.cs:68-87`) and
`RetargetSlaveClock` is running-safe (`:813`) — so surviving a bad master requires **detecting and
detaching before it wedges**. The archived bay exposes `QuarantinedTerminalIds` and hot-swap, and its
tests cover deliberate master replacement, but **no watchdog exists** in either the bay or the router.

**Decision D2: build the watchdog.** The pieces to assemble already exist and none of them require
touching the fault path itself — which matters, because that path is fragile:

- **Detection** — the pump already tracks in-flight depth and stuck state (`OutputPumpStats.InFlight`,
  `IsStuck`, `AudioRouter.cs:1791,1812`) and raises `PumpPressure`. A master climbing toward its cap is
  the early warning; do not wait for `WaitForCapacity` to fail, because that is already the fault.
- **Handoff** — `RetargetSlaveClock` (`:813`) is explicitly running-safe, and the archived bay already
  has `ReplaceTerminal_ClockMaster_HandsProducerClocksToTheNewMaster` proving deliberate master
  replacement works. The watchdog's job is to *trigger* that path early, not to invent it.
- **Fallback target** — `NullClockedAudioOutput` is a real-time-paced clocked output with the same
  master-promotion path as hardware, so there is always somewhere to hand the clock to, even with no
  second device present.
- **Safety rail** — promotion is deliberately disabled mid-stream (`AudioRouter.Playback.cs:97-103`) and
  `SlaveTo` throws while running (`:977-978`), so the handoff must go through `RetargetSlaveClock` only.

Two things to get right in design review: the watchdog must not itself run on the router thread (that is
blocker 1's lesson), and a false positive must be cheap — retargeting the clock to a healthy terminal
should be inaudible, so a conservative trigger that occasionally fires early is far better than one that
fires late. Ship the "report and stop" behaviour as the fallback for when the handoff itself fails.

### 1.4 Open items after recovery

- **Input-side health is absent.** The router exposes only `SourceIds`/`Routes` (`:697-720`); short
  source reads are silence-padded with no counter (`:1530-1541`); `SharedAudioOutput` exposes only
  `ActiveLeaseCount` (`:92`). The archive's `ProgramBusProducer` does expose `BufferedFrames`,
  `OverflowFloats`, `UnderrunFloats` — surface those per lease in diagnostics (§6).
- **`SharedAudioOutput.Acquire()`** (`:101`) is public API consumed by HaPlay
  (`OutputPreview/PortAudioOutputRuntime.cs:81-105`) and is identity-mapped, terminal-width, with no
  format argument. Decide whether the bay supersedes it for patched lines only (the plan's intent) and
  what HaPlay keeps using.
- **Raw-terminal acquisition is genuinely absent** (§7.1) — `PortAudioOutputRuntime` keeps
  `IAudioOutput? _output` private (`:24-25`) and `Start()` unconditionally wraps it in
  `SharedAudioOutput` (`:63`); `AcquireForPlayback` (`:81`) only ever returns a lease. This is the
  HaOutput-side half of the bay's exclusive ownership and it is net-new.
- **Per-voice clocks.** Today each voice's `MediaClock` is mastered to its device output through
  `AutoWirePrimary` (`Playback.cs:105-108`). Under a bay no voice owns a terminal, so the lease **must**
  expose `IPlaybackClock` with epoch + DAC-lead compensation or every video-bearing cue desyncs. The
  archive's `ProgramBusProducer` + `AudibleClientClock` do exactly this and are covered by
  `ProgramBusClockTests` — verify against those tests rather than re-deriving.
- **Decide the per-voice router question explicitly.** "One `AudioRouter` for the whole project" is true
  only of the bay; each cue voice still carries its own (`MediaPlayer.cs:874`) unless opened with
  `IncludeAudioRouter: false` and fed via `MediaPlayer.AudioSource` (`:132`) — which forfeits the
  audio-mastered `MediaClock` (`:904` vs the free-run path `:908`). This changes the lease contract.

---

## 2. Session and transport — the biggest gap

### 2.1 Multi-list transport (register item 5) — PARTIAL, with a hard blocker

**The good news: N independent, list-scoped transports already exist.** `TransportGroup`
(`ShowSession.TransportVoices.cs:398`) owns its own `SessionClock`, `TransportTimeline` and voice list,
with exactly one `ActiveVoice` (`:412`) and one crossfade tail (`MaxReleasingVoices = 1`, `:431`). A
per-group GO cursor exists — `LastFiredNumber` (`:433`), selected by `SelectNextGoCueAsync`
(`ShowSession.Transport.cs:187-197`) and advanced by `AdvanceGoCursorAsync` (`:202`). HaPlay already
gives every cue list its own group (`HaPlayShowMapper.RuntimeGroupId`, `HaPlayShowMapper.cs:106,110`)
and already has per-list *run* scoping (`GoForeignListAsync`, `CuePlayerViewModel.Transport.cs:283`).

**Standby/next is not a framework concept at all.** No standby pointer, no "next cue" query, no public
getter/setter for the GO cursor (`AdvanceGoCursorAsync` is `internal`; only `GoAsync` writes it).
`ClipStandbyEngine` is decoder pre-roll, not a playhead. The only standby that exists is HaPlay's single
`StandbyCueNode`/`CurrentCueNode` for the selected list (`CuePlayerViewModel.Transport.cs:461,521,526`),
and `GoForeignListAsync` deliberately writes neither (`:277-282`). HaPlay never calls `session.GoAsync`;
it fires by explicit id.

**The blocker.** `LoadDocumentCoreAsync` unconditionally calls `DisposeGroupsAsync()`
(`ShowSession.cs:523`), which clears `_groups` (`ShowSession.Queries.cs:197-204`). That means:

1. **every per-group GO cursor resets on every document reload**, and
2. **every active voice in every group is released** — editing list A stops list B's playback.

And reload is the app's *normal* path: debounced 300 ms after any structural edit and flushed before
every fire (`CueShowSessionCoordinator.cs:457,505,802`).

So rev-3's "each list remembers its position" (item 5) and "editing never blocks playback" (item 3) are
mutually exclusive with today's load path. **This is the single largest new framework work item in the
whole plan** and it was not costed: it needs an incremental/partial document load — or per-group
generation tracking — that leaves untouched groups running.

**Shape recommendation: one `ShowSession` with N transport groups, not N sessions.** Compositions,
video/audio output leases, master trim, the sounding bus, stop-all/Panic and the serial dispatcher are
all per-session (`ShowSession.cs:92-148`, `ShowSession.Levels.cs:121`); N sessions would force the app to
re-implement cross-list Panic and single-holder line arbitration. Given that, per-list standby can stay
**app-level** (a dictionary keyed by list id) and the framework's only required change is the
non-destructive load. Public `Get/SetGoCursor` is needed only if the framework is to own positions.

**Two constraints to respect:** cue `Number` must be globally unique document-wide
(`ShowDocumentValidator.cs:45-46`) and GO selects "min Number > cursor within group" — so auto-renumber
(register item 20) renumbers the merged document. And **one active clip per group** (`:412`) with
**one tail** (`:431`): a list that must sound two cues at once still needs the app's existing
runtime-group trick (`manual:{id}` / simultaneous groups, `CueShowSessionCoordinator.cs:509,541`).

### 2.2 Per-cue `Disabled` — EXISTS in the framework, missing in the app

`CueDefinition.Enabled` (`CueGraph.cs:52`) with `CueGraph.SetCueState` (`:186`); GO filters
`c.Armed && c.Enabled` (`ShowSession.Transport.cs:192-194`); explicit fire returns `SkippedDisabled`
(`CueGraph.cs:272-273`, `ShowSession.Transport.cs:80-81`); test at `ShowSessionTests.cs:309-310`.

The app is the whole job: `CueNode` has no enable flag (`Enabled` exists only on trigger bindings
`CueList.cs:321`, schedules `:399`, mapping sections `:129,159`) and `HaPlayShowMapper` never sets it
(`:225-230`), so every mapped cue is `Enabled = true`. Add the model field and map it — the framework
then skips it at fire time for free — then filter in `EnumerateFireableCueOrder`, the trigger plan,
auto-follow and compile.

Two small framework notes. Pre-roll does not filter disabled cues (`ShowSession.cs:995-998` filters by
number only). And a disabled cue in an `AutoContinue` chain **stops** the chain rather than skipping
onward (`CueGraph.cs:290-297`).

**Decision D6: skip-vs-stop becomes a project-level setting.** Both behaviours are legitimate — skipping
suits "this cue is cut for previews, carry on"; stopping suits "this cue is disabled because the sequence
is not safe to run". The framework today implements only *stop*, so the work is: add the skip path in
`CueGraph`'s auto-continue recursion (minding the existing cycle detection), surface the choice in project
settings (Show behaviour), and thread it through the compile. Note this interacts with HaCue2's own
auto-follow, which — like HaPlay's — is a host path over `ClipNaturallyEnded` rather than the framework
chain, so **both paths must honour the same setting** or the behaviour will differ by cue kind.

---

## 3. Fades, curves and automation lanes

### 3.1 Custom fade curves (register item 16) — ABSENT as data, cheap to add

Evaluation is centralised in exactly one place: `FadeCurves.Shape` (`S.Media.Session/FadeRamp.cs:57-63`)
behind `LevelUp`/`LevelDown`/`LevelBetween` (`:33-53`); every session ramp runs through
`FadeRamp.RunAsync`/`Start` at 25 ms steps (`:121,127,144`); `VolumeEnvelopes.Sample` reuses
`LevelBetween` (`:103`). Nothing else switches on the enum.

**The evaluator for a user-drawn curve already exists.** `VolumeEnvelopes.Sample` (`:80-105`) is
piecewise interpolation over a point list with a **per-segment curve** (`ShowEnvelopePoint.CurveToNext`),
binary search and endpoint clamping — structurally identical to a normalized custom fade curve. The gap
is only that a *fade* takes `FadeCurve` (an enum) by value.

Work: introduce a curve value type (builtin enum | point list with smooth/hold segments) exposing one
`Evaluate(p)`, give `FadeCurve` an implicit conversion, and swap the parameter type at ~15 sites — 3
document fields (`ShowDocument.cs:146,153,231`) and ~12 signatures (`ShowSession.Transport.cs:37`,
`ShowSession.Levels.cs:32,154`, `ShowSession.Stops.cs:35,57,181`, `SoundingSourceRegistry.cs:24`,
`ShowSession.TransportVoices.cs:421`). Mechanical. **The real cost is serialization**: `ShowDocument` is
source-generated (`:288-292`) and the enum serialises as a scalar, so a record-typed curve changes the
on-disk shape (also `UI/HaPlay/Models/ShowDocumentSidecar.cs`) — add a nullable `CustomCurve` beside the
enum, or version the document. `ShowDocumentValidator` needs point-list rules (sorted, in range, ≥2 points).

**Flag for rev-3's "same curve control everywhere":** three current curve choices are *hardcoded, not
picked* — crossfade `EqualPower` at `CuePlayerViewModel.Transport.cs:311,499` and loop crossfade
`EqualPower` at `ShowSession.Completion.cs:240` — and the **loop-crossfade curve is not reachable from
`ShowDocument` at all** (`LoopCrossfade` is a duration only, `ShowDocument.cs:164`). Making the promise
true requires exposing these three.

### 3.2 Effect lanes replacing `VolumeEnvelope` (register item 18) — volume only

**Volume automation exists and is well-built**: `ShowClipBinding.VolumeEnvelope`
(`ShowDocument.cs:221-225`) → `ShowEnvelopePoint` (`:231`) → `VolumeEnvelopes.Sample` →
`StartEnvelopeRunner` (`ShowSession.Levels.cs:72-94`), a 25 ms loop sampling clip-relative time (survives
seeks and loops) → `TransportVoice.ApplyEnvelopeLevel` (`ShowSession.TransportVoices.cs:342`) →
`ApplyAudioScale` (`:286`), composing as `Source × Fade × Envelope × Master` (`SoundingSourceRegistry.cs:49-70`),
seeded pre-attach so a quiet start never bursts (`ShowSession.cs:743`). It is **audio-only by explicit
design** — "Audio-only - layer opacities belong to the fades" (`ShowSession.TransportVoices.cs:341`).

- **Opacity lane: ✅ LANDED (2026-08-02).** The deliverable named below - a video level-composition chain -
  now exists as `ClipCompositionRuntime.VisualLevel`: `Base` (authored, rewritten by every placement apply)
  x `Fade` (every session ramp) x `Automation` (the lane), composed by the slot and clamped once. Both slot
  kinds carry it, `IPlacedClipLayer` exposes the three components plus `EffectiveOpacity`, and the trap is
  closed by construction - a live placement edit writes `Base` and leaves a ramp in flight untouched.
  `ShowClipBinding.OpacityEnvelope` + `StartOpacityLaneRunner` (the video twin of the volume runner, same
  25 ms step, same clip-relative time basis, seeded pre-attach) drive the third component. **Semantic note:**
  a fade cue's per-layer level is now a FACTOR over the authored opacity, exactly as the audio side's level
  composes over a clip's own gain - it is no longer an absolute opacity that discarded the authoring.
  Original finding, for the record:
- **Opacity lane: ABSENT, and the obvious route is a trap.** Layer opacity is written only by fade ramps
  (`ApplyFadeLevel :308`, `ApplyClipFadeLevel :328`) and by whole-placement live edits
  (`ShowSession.LiveEdits.cs:22`). A live placement write does **not** refresh `BaseLayerOpacities`
  (captured once at commit, `:266-274`), so an opacity lane driven through `UpdateActivePlacementAsync`
  would fight fades and make them ramp toward a stale authored opacity. A real opacity lane needs a
  per-layer multiplicative automation component mirroring `SoundingLevel` — **video has no
  level-composition chain today; audio has exactly one.** That chain is the actual deliverable.
- **Outbound OSC/MIDI ramps: ABSENT everywhere — and now v1 scope (D3).** The control side cannot cover
  for it:
  `ControlScriptApiLibrary` documents that it has **no timing primitives**;
  `ControlPeriodicOSCSendConfig` is a fixed-address keep-alive, not a ramp; nothing in `S.Control`
  interpolates except MTC position; `CueActionKind` has no script kind. Today's workaround is 50 action
  cues for a 2 s ramp at 25 Hz. This does not belong in `ShowSession` (no media involved) — build it
  host-side or in `S.Control`, reusing `FadeCurves` plus a scheduler. `Plans/HaCue-Feature-Ideas.md`
  already fixed the two non-negotiables: an explicit `SendRateHz` (25 default, never per-frame) and the
  ramp **must land exactly on the final keyframe value**, including on stop/panic mid-ramp, coalescing
  rather than backlogging when an endpoint is slow.

  Because D3 puts this in v1, three contract points need settling in design review rather than in code:
  **(a)** an outbound lane must *not* be undone when the cue stops — it owns a value in another system,
  which is the opposite rule from internal lanes; **(b)** Panic's behaviour toward in-flight ramps must be
  explicit (land on the final value, freeze, or send nothing further) — silence here becomes a
  lighting-desk surprise; **(c)** the runner belongs beside the action-endpoint sender, **not** in
  `ShowSession` — no media is involved, and putting a timer in the session would couple the cue engine to
  outbound I/O for no benefit.
- **Group lanes have no document home.** `ShowDocument` is flat; groups exist only as a `GroupId` string
  on `CueDefinition` (`CueGraph.cs:55`). Resolve rev-3's "child overrides group" (item 18) **in the
  mapper** — child lane wins, group lane copied down — so the document stays flat.

Framework change: generalise `VolumeEnvelope` → `IReadOnlyList<ShowAutomationLane>` (kind + target +
points), one runner per lane replacing `StartEnvelopeRunner`, plus the video automation component. Keep
`VolumeEnvelope` as a shim.

### 3.3 Playlist crossfades (register item 17) — **no framework change needed**

Dual-voice crossfade is fully implemented and **duration and curve are already per-fire arguments, never
document data**: `FireCueAsync(cueId, crossfade, curve)` (`ShowSession.Transport.cs:36-42`) →
`_pendingFireCrossfade` (`:111-118`) → `CommitClipAsync` (`ShowSession.cs:643-658`) →
`TransportGroup.ActivateAsync` handoff + `BeginRelease` + armed release ramp
(`ShowSession.TransportVoices.cs:454-482,121,421`). The app already decides at fire time; today it just
reads one group-level integer (`CueList.cs:494`) and hardcodes the curve.

The pre-end trigger is likewise already per-clip — `ShowClipBinding.PreEndNotify`
(`ShowDocument.cs:183-188`) raising `ClipApproachingEnd` (`ShowSession.cs:338-345`), filled per child from
the parent group by the mapper (`HaPlayShowMapper.cs:207-220,233`). Per-child override = change what the
mapper writes.

**Decision D7 (delegated — recommendation): raise `MaxReleasingVoices` to a small bounded N, default 3.**
Today it is hardcoded to 1 (`ShowSession.TransportVoices.cs:431`), which means a rapid GO sequence
hard-cuts the previous tail: fire A, then B, then C in quick succession and A is cut the moment C starts,
because B already occupies the single release slot. For a cue player — where rapid sequences are normal
operating, not an edge case — that is an audible artifact with no authored cause, which is the worst kind.

Long-term the right shape is a **bounded** N rather than an unbounded list: each releasing voice still
holds a decoder and a producer lease, so unbounded tails let a stuck-GO operator exhaust resources. A
default of 3 covers the realistic "GO GO GO" case while staying far inside the plan's budget of 8
simultaneous program voices, and the cap belongs next to that budget so the two are reasoned about
together. Two details worth fixing at the same time: the bound is **per transport group**, so with
multi-list transport (§2.1) each list gets its own tails — the global ceiling is N × active lists, which
is what the voice budget must actually be checked against; and the oldest tail should be *faded* rather
than hard-cut when the bound is hit, so exceeding it degrades gracefully instead of clicking.

Also still worth doing here: make the loop-crossfade curve data rather than hardcoded `EqualPower`
(`ShowSession.Completion.cs:240`).

### 3.4 Patch-cue fades — no framework counterpart, and that is fine

Live route/matrix edits are instantaneous writes (`ShowSession.LiveEdits.cs:63,123`); nothing in the
framework ramps a non-clip gain target. The patch is app-owned; the framework's contribution is
`FadeCurves` as shared public math. State this in the plan so nobody looks for a session API.

---

## 4. Video

### 4.1 Per-output mapping (register item 22) — **EXISTS, fully wired**

This is better than the plan assumes. Mapping is a property of the **output lease**, not of the
composition frame: `ClipCompositionOutputLease(..., ClipOutputMappingSpec? Mapping)`
(`S.Media.Session/ClipCompositionRuntime.cs:24-30`); each lease becomes an `AcquiredOutput` with its own
`OutputMappingStage`, compositor and output `VideoFormat` (`:1470-1532`). The pump runs the
composition-level stage **once** on the canvas (`:1002-1018`), then **per output** composites that
output's warp sections into its own frame (`:1027-1048`), while unmapped outputs get zero-copy fan-out
views of the raw canvas (`:1050-1094`). Live edits without restart: `UpdateOutputMapping(outputId, spec)`
(`:273-292`), `AddOutput`/`RemoveOutput` (`:304,328`).

So **"warped to the projector, clean to the TV/NDI" works today**. HaPlay is wired per binding end to
end: `CueVideoOutputBinding.Mapping/MappingEnabled` (`CueList.cs:122-131`) →
`HaPlayShowMapper.ResolveEffectiveVideoOutputMappings` keyed by binding id (`:34-83`) → `CueShowVideoOutput`
per line (`CueShowSessionCoordinator.cs:1808-1812`) → lease.

Two caveats worth designing around:
- A **composition-level** `VideoFx` mapping (`CueList.cs:92-97` → `ShowComposition.OutputMapping`)
  disables both GPU integrated-warp paths (`ClipCompositionRuntime.cs:181-187,1102-1107`), forcing the
  chained CPU/GL readback path. Rev-3 item 21 says a composition owns only size/rate/idle — **if that
  drop is real, retire `ShowComposition.OutputMapping` and the `_compositionMappingStage` path too, which
  re-enables the GPU integrated multi-output warp.** That is a performance win hiding in a cleanup.
- Mesh warp silently degrades to affine on the CPU compositor (`:1436-1441`).

### 4.2 Visualizer decoupling (register item 21) — a **deletion**, with one caveat

No composition-level visualizer enablement exists in the framework at all: `ShowComposition` carries only
id/name/size/rate/mapping (`ShowDocument.cs:240-247`), and `SetCompositionVisualizerAsync`
(`ShowSession.Taps.cs:143-175`) attaches the source as N ordinary surface layers with arbitrary
`VideoPlacementSpec`s — the same placement model as media cues. `CueComposition.VisualizerEnabled`/
`VisualizerPresetDirectory` (`CueList.cs:96-105`) are read by **nothing** in the cue runtime; they are only
round-tripped through `CueCompositionViewModel` and are not even bound in `CuePlayerView.axaml`. HaPlay
already treats the visualizer as a cue type (`VisualizerCueNode`, `CueList.cs:704-721`, executed by
`CueShowSessionCoordinator.cs:288-395`).

**So item 21 is removing dead fields — a persistence/UI change with no runtime coupling to unwind.**
(Do not sweep up the deck's identically-named `MediaPlayerViewModel.VisualizerEnabled`; different thing.)

What the projectM source genuinely requires, and must survive the change: a GL-capable compositor
(`composition.SupportsSurfaceLayers`, `Taps.cs:155-160` — the CPU fallback has no visualizer,
`ClipCompositionRuntime.cs:643`); a session-level audio tap with optional per-cue-id filter, auto-attached
to already-playing clips (`Taps.cs:190-198`); **GL thread affinity** — one context per composition, since
each composition's pump is its own driver thread (`ClipCompositionRuntime.cs:882-898`) and the SDL GL
context is `[ThreadStatic]` (`SharedSDLGlContext.cs:30,48-58`), so teardown must be `ReleaseGl` on the GL
thread (`ProjectMGlLayerSurface.cs:16-20`, `DisposeOnDriverThread`, `ClipCompositionRuntime.cs:46,907,1298`);
and **one surface per source** — `CreateLayerSurface()` at most once per source or projectM crashes, with
layer 0 owning disposal (`ShowSessionVisualizerService.cs:19-24,92-100,260-268`).

**Caveat that contradicts "an ordinary layer like any other":** visualizer slots are keyed by composition
(`ShowSessionVisualizerService.cs:45`) and `Attach` **replaces** any existing one (`:109-119`). So **two
visualizer cues cannot coexist on one composition**, and a visualizer cannot be z-ordered below a specific
media layer (surface layers always render above frame layers, `Taps.cs:161-170`).

**Decision D5: multiple simultaneous visualizer cues per composition must be possible.** That makes this
real framework work rather than a flag deletion:

- **Key the slot dictionary by cue id**, not composition id, and give each source its own surface and GL
  resources. The hard constraint to respect is that `CreateLayerSurface()` may be called **at most once
  per source** or projectM crashes, with layer 0 owning disposal
  (`ShowSessionVisualizerService.cs:19-24,92-100,260-268`) — so "one surface per source, N placements per
  surface" stays the rule; what changes is that a composition may now host several such sources.
- **GL cost is per source, not per placement.** Each projectM instance is a real renderer on the
  composition's single GL thread (`ClipCompositionRuntime.cs:882-898`, `[ThreadStatic]` context
  `SharedSDLGlContext.cs:30,48-58`), so N visualizers on one canvas is N renderers sharing one thread.
  This is the one place where "just allow more" has a genuine performance ceiling, and it wants a
  documented, validator-enforced cap rather than being discovered at a get-in.
- **Teardown still routes through `DisposeOnDriverThread`** (`ClipCompositionRuntime.cs:46,907,1298`) —
  with several sources per composition, the disposal ordering matters more, not less.
- **The z-order limitation remains** unless separately addressed: surface layers always render above frame
  layers, so a visualizer cannot sit *beneath* a media layer even once multiples are allowed. Worth
  confirming whether that is acceptable — it is a distinct piece of work from allowing N.

### 4.3 Idle images (register item 23) — PARTIAL, and effectively unavailable during a show

Per-output idle exists **only** on `LocalVideoOutputDefinition.BackgroundImagePath`
(`UI/HaPlay/Models/OutputDefinitions.cs:155-157`); NDI, file and live-stream definitions have no
equivalent (`:216-302`). Worse, it is a *preview-runtime* idle frame drawn only while the line is **not**
held by playback (`OutputPreview/LocalVideoPreviewRuntime.cs:178-182,289-311`) — and the cue session
acquires every bound video line at document load and holds it for the session's lifetime
(`CueShowSessionCoordinator.cs:1782-1800`). **So once a cue list with video bindings is loaded, the idle
image never shows again; the composition submits black.** Composition-level idle is absent at every layer.

Work: an idle concept owned by the composition pump (a held bottom layer, or a "no live layers → submit
idle frame" branch in `PumpOneFrame`), a per-output fallback field on the shared `OutputDefinition` base
rather than only the local-video subtype, image→format conversion for NDI/file/stream sinks, and the
composition-takes-precedence resolution rev-3 specifies. `FallbackImageLoader.TryBuildHoldCpuFrame` and
`StaticSlateVideoOutput` are reusable primitives; `IdleLogoSlateSession` (`Playback/IdleLogoSlateSession.cs:39-137`)
is a deck-idle concept, not per-output config.

### 4.4 Test pattern and Identify (register item 22) — **the plan is wrong here**

A grid pattern exists (`Playback/MappingTestPattern.cs:14-85`, masked to a mapping's visible slice) and is
injected via `ShowSession.SetCompositionTestPatternAsync` — but as a **top-most full-canvas layer on the
composition** (`ShowSession.Queries.cs:122-140`, `_testPatternSlots` keyed by composition id). It therefore
appears on **every output bound to that composition**; the `outputLineId` argument only selects which
mapping to mask by (`CueShowSessionCoordinator.cs:651-674`). No "Identify" exists anywhere (zero hits in
`UI/` and `MediaFramework/`).

So rev-3's "a per-output test pattern ships in v1" describes something that does not exist, and today's
nearest equivalent **lights up the TV while you calibrate the projector**. Cheapest correct design: give
`AcquiredOutput` an optional override frame/source consulted in `SubmitToOutput`
(`ClipCompositionRuntime.cs:1172-1186`) — a new `SetOutputTestPattern(outputId, frame)` on
`ClipCompositionRuntime` + `ShowSession`, applied *after* that output's mapping stage. Identify is the
same mechanism with a text-rendered frame (`S.Media.Source.Text` exists) plus a timer.

### 4.5 Audition video surface (register item 15) — ✅ LANDED 2026-08-02

**Option (a) was chosen and built** (owner: "Composed — accurate, costs a GL thread"). `ShowSession` gained
an audition canvas held OUTSIDE `_compositions` so it survives document loads and never appears among the
show's own compositions: `EnableAuditionCompositionAsync(AuditionCompositionSpec)` (opt-in; same-spec
re-enable is a no-op so a settings save cannot flicker the monitor), `DisableAuditionCompositionAsync`,
`AttachAuditionOutputAsync` / `DetachAuditionOutputAsync`, `GetAuditionCompositionStatsAsync`.
`VoicePlayer.PreviewCueAsync` places the previewed clip onto it as a `SlotKeepPolicy.Latest` layer (a
preview claims no transport timeline, so a master-aligned slot would freeze on the first frame), released
symmetrically before the clip. **D8 also landed**: the audition audio width now follows the selected
device's `MaxChannels` instead of a hardcoded stereo — which was not merely a mis-placement, it made
audition fail outright on interfaces whose driver only accepts their native width. 8 tests.

Original finding, for the record:

### 4.5a Audition video surface — the original ABSENT analysis

The whole audition path is audio: `VoicePlayer.PreviewCueAsync` attaches only an audio output and never
touches `VideoSource` (`S.Media.Session/VoicePlayer.cs:126-205`); the UI side is device selection plus a
waveform (`CuePlayerViewModel.Preview.cs:1-80`). `LocalVideoPreviewWindow`/`LocalVideoPreviewRuntime` are
*output-line* windows (a real program output rendered in-app), not a monitor path.

**Mirroring a whole composition to a second surface is already free and live**:
`ShowSession.AddCompositionOutputAsync` / `ClipCompositionRuntime.AddOutput`, explicitly documented "e.g.
a UI preview surface" (`ShowSession.Queries.cs:89`, `ClipCompositionRuntime.cs:299-304`), with zero-copy
unmapped fan-out (`:1058-1094`); `VideoOpenGlControl` (`S.Media.Present.Avalonia/VideoOpenGlControl.cs:31`)
is an in-app `IVideoOutput` that can be leased directly.

What is missing is previewing a **single cue** without touching program. Either (a) a hidden audition
composition — its own pump and GL context, one extra driver thread, real cost — that the preview clip's
video is placed onto, or (b) extend `VoicePlayer` preview to `AttachVideoOutput` straight to an audition
sink at source resolution (cheaper, no compositor, but no placement/fit/mapping semantics). Plus the
audition-rig config itself (device + surface), which has no model today.

**Decision D8: the audition channel count follows the configured audition output — never a hardcoded
value.** Today `VoicePlayer.cs:182` builds `new AudioFormat(rate, 2)`, hardcoded stereo, and bypasses the
host output factory entirely. The fix is to take the channel count from the selected audition device's
own format, exactly as any other output line does. **This settles plan open question 17** with a third
answer the plan did not consider: not "stereo forever" and not "audition into the project's logical
channels and let the patch place it", but "the audition rig is an output like any other, and its width is
whatever that output is". That is also the answer that survives someone auditioning through a multichannel
interface, which the other two do not.

---

## 5. Control input, triggers and the remote API

### 5.1 `HaControl.Input` — PARTIAL, and **better than the plan states**

The plan (`:505`) says MIDI/OSC input device ownership lives in `ControlWorkspaceViewModel`. **That is
inaccurate.** The device layer is already framework-side and Avalonia-free in `S.Control`:
`ControlInputSession` (`MediaFramework/Control/S.Control/ControlInputSession.cs:20`) is precisely the
wanted contract — ref-counted device open/close (`:78,121`), `event Action<ControlMonitorRecord> InputObserved`
(`:50`), `AttachDispatcher`/`AttachMonitor` leases (`:183,192`), `HasConfiguredDevices` (`:69`), with its
own doc stating the "devices open independent of arm" contract. Alongside it:
`ControlMIDIPortCatalogProvider`, `ControlMIDIDeviceResolver` (`:37`),
`ControlSystemMIDIDeviceSessions` (`:8,15`), `ControlOSCListenerManager`, `UdpControlOSCSender`,
`ControlMonitor.cs:7,31,99`, `ControlSystemConfig`/`ControlSystemIO`, `ControlDeviceHealthRegistry`.

What is actually stuck in HaPlay, and is the real extraction unit:

- **Session lifecycle glue** — `ControlWorkspaceViewModel.MidiFallback.cs:165-280`:
  `DeviceInputSessionFactory`, `SyncDeviceInputSession(Async)` with config-signature diffing (`:281`),
  retry-on-failed-open, `_inputSyncGate`, `_inputShutdown` teardown ordering, `CreateDeviceInputSession`
  (`:240`), `OnDeviceInputObserved` (`:248`). ~120 lines of carefully-commented lifetime code that every
  host needs but only an Avalonia ViewModel can reach today.
- **The learn flow, duplicated twice** — script triggers at `ControlWorkspaceViewModel.Learn.cs:110-302`
  (`ToggleLearn`/`ConfirmLearn`, `FindLearnCapture :187`, `BuildLearnedTrigger :207`,
  `InferMIDIMessageType :255`) versus a separate simpler cue implementation
  (`CuePlayerViewModel.CueEditing.cs:334` + `CueTriggerService.OnMidiInput:172-185`). Unify into one
  record→binding capture service; the pure parts are already `internal static` and Avalonia-free.
- **Trigger matching** — `CueTriggerService.cs` (`MidiMatches:292`, `OscMatches:333`,
  `TryMapMidiRecord:349`, `TryMapOscRecord:386`, rising-edge latch `:75`, retrigger guard `:66`).
  Avalonia-free except the `KeyEventArgs` hotkey path (`:208`) and `Strings`.
- **Outbound endpoints** — `Models/ActionEndpoint.cs`, `ViewModels/ActionEndpointProbe.cs`,
  `Services/EndpointHealthMonitor.cs` (swap its `DispatcherTimer` for a timer abstraction).
- **Control-surface feedback (D4, newly in scope)** — the LED / scribble-strip / motor-fader half that
  today lives in HaPlay's Control workspace (the X32/XTouch layer) leaves with the input half, so HaCue2
  can light up the surface that fires it. Two things make this less alarming than it sounds: the outbound
  MIDI sender already exists framework-side (`ControlSystemMIDIDeviceSessionManager : IControlMIDISender`,
  `ControlSystemMIDIDeviceSessions.cs:8,15`) as does `UdpControlOSCSender`, and
  `ControlFeedbackMode` (`Models/ControlGraphConfig.cs:143`) already models echo suppression. What must
  come across is the *mapping* layer (what state lights which control) and the feedback throttle — and
  the natural first consumer is standby/active state, which is exactly the "which cue is standing by"
  gap the ideas doc names. Scope it as: shared sender + a feedback mapping HaCue2 owns, not a port of
  HaPlay's full mixer-surface logic.
- **Host fan-out** — `MainViewModel.cs:107,215-231`: the I/O-thread pre-filter → chase → `WouldAccept` →
  `Dispatcher.UIThread.Post` chain. 15 lines that encode the whole performance contract.

Nothing needs rewriting; this is lift-and-rehome. **Correct the plan's premise** so the work is scoped
right.

### 5.2 Continuous-controller → parameter bindings (register item 24) — ABSENT for cues

`CueTriggerService` treats CC purely as a rising-edge *event* (`:309-314`, value discarded); `TriggerMatch`
has no continuous member; `CueTriggerBinding` (`CueList.cs:316`) has no target/range/curve/takeover fields.

**A designed-but-dead graph already models exactly this**: `Models/ControlGraphConfig.cs` —
`MIDIInputControlNodeSettings.SoftTakeoverEnabled/Tolerance` (`:78-79`),
`MapRangeControlNodeSettings` with `InputMin/Max → OutputMin/Max/Clamp` (`:89-95`),
`ScriptTransformControlNodeSettings` (`:98`), `MinSendIntervalMs`, `ControlFeedbackMode` (`:143`). It has
**no runtime** (referenced only by itself and `HaPlayProject.cs:45,118` persistence) and all its sinks are
*external* (`ControlNodeKind :37-47` has no internal-parameter kind). Reuse the schema; do not reinvent it.

The framework has numeric plumbing but no parameter sink: `ControlMIDITriggerBridge` fires
`TriggerPayload.FromNumeric(cc/127)` (`ControlTriggerBridges.cs:33`), but `TriggerActionDescriptor` is
`(Kind, TargetId, Command)` strings, `TriggerActionKind` has no Parameter member, and
`IControlShowActions` exposes only Go/FireCue/Seek/Stop — **a control script literally cannot touch a
level.** Bindable parameters are app-level VM properties with side-effect setters and no registry:
`CuePlayerViewModel.MasterTrimDb` (`:499-528`, clamped −60..0, → `SetMasterTrimAsync :485`) is the plan's
own example and a clean first target.

Must build: a parameter-target descriptor (id, range, unit, curve, setter), a continuous binding kind, and
a value path that survives the `WouldAccept` I/O-thread filter and the UI-thread post **at rate** — a
fader sweep is ~100 msg/s and today each accepted record posts one dispatcher item (`MainViewModel.cs:230`),
so this needs coalescing like the MTC path already does.

### 5.3 The single "External input" toggle (register item 3) — three differently-shaped gates today

- **Triggers**: `CueTriggerService.GateOpen = TriggersArmed && !IsCueEditMode` (`:117`, re-checked `:225`,
  pre-filtered in `WouldAccept :132-143`, with a deliberate MIDI-learn exception at `:143`).
- **Schedules**: `CueSchedulerService` gate at `:170` **plus arm-edge baselining** (`:124-142`) that resets
  `_armedBaselineWall`, `_handled`, `_handledTimecode` and re-baselines the chase position on the OFF→ON
  edge. Not a boolean.
- **Chase**: `CueTimecodeChaseService.Enabled` (`:69-83`) **is not an arm at all** — the scheduler sweep
  sets it to "does any loaded list carry a Timecode schedule" (`CueSchedulerService.cs:108-111`)
  *specifically so the operator can watch incoming timecode before arming*; turning it off resets the
  decoder and drops the source latch (`:80`).

So the collapse must preserve (a) the scheduler's arm-edge baselining, (b) the chase's
decode-without-arming behaviour as a **separate decode-enable beneath** the master toggle, and (c) delete
the `IsCueEditMode` term from all three call sites (register item 3 removes edit-mode gating). Also note
the remote API's `arm`/`disarm` (`RemoteApiDispatcher.cs:343-350`) currently targets the **Control
workspace** arm, not either cue arm — it must be re-pointed.

### 5.4 Timecode — MTC exists and is good; LTC does not exist

`MediaFramework/Control/S.Control/MidiTimecode.cs` (820 lines) provides rates (`:11,29`),
`MidiTimecodeValue` with frame math and parsing (`:76,102,161`), a decoder with quarter-frame assembly and
full-frame locate (`:284,287,336,358,403,429`), `MidiTimecodeChaseState` (`:589`), and
`MidiTimecodeChaseClock` with stall timeouts and free-run extrapolation (`:623-747`). HaPlay adds only
source-selection policy (`CueTimecodeChaseService`, 174 lines, Avalonia-free — moves as-is).

**LTC is absent**: the only occurrence is an unused enum member
`TimecodeSyncKind { None, Mtc, Ltc, Smpte }` (`S.Media.Session/TriggerBindingSet.cs:33-37`) with
`TimecodeSyncPlan` (`:62`), a setter and validation (`:112,117`) — a reserved seam with no
implementation, no consumer and no audio-domain decoder.

#### 5.4a LTC — owner decision 2026-08-01: **in scope, but sequenced**

The owner has confirmed LTC will definitely be needed for later use cases, so it should not be dismissed
as backlog. The audit says it is genuinely tractable, and it splits into three independent pieces with
very different costs and prerequisites.

**Prerequisite — audio input capture already exists and is good.** `PortAudioInput : IAudioSource`
(`S.Media.Audio.PortAudio/PortAudioInput.cs:17`) offers exactly what a realtime decoder wants:
`ReadInto(Span<float>)` (`:49`), ring depth and capacity (`:64-65`), `OverflowSamples` (`:67`),
`RebaseToLatest` (`:78`), plus stream-fault and liveness detection (`:90-124`). Capture is already a
first-class media source through the `padev:` scheme
(`PortAudioCaptureDecoderProvider.cs:23,32,38`), and MiniAudio has an input path too. So an LTC reader
needs no new I/O layer.

**Piece 1 — the decoder (pure DSP, no dependencies, do it any time).** Biphase-mark (Manchester) decode
over mono float samples: bit-clock recovery, sync-word (`0x3FFD`) detection, forward/reverse detection,
drop-frame flag, and rate inference across 24/25/29.97DF/30. This is self-contained, trivially unit
testable against generated waveforms, and parallelisable with any other work. It is also the only piece
with real signal-processing risk (varispeed, low level, tape wow, half-speed shuttle).

**Piece 2 — the chase integration (cheap, but decide the contract early).** LTC is *simpler to chase than
MTC*: every frame arrives complete, so none of the eight-quarter-frame assembly complexity applies. The
valuable runtime logic already exists and is transport-neutral in substance — stall timeouts
(`MidiTimecode.cs:627,638`), free-run extrapolation and `MaxPositionLeadSeconds` clamping (`:675,623-747`)
— but its **API surface is MIDI-shaped**: ingestion is `FeedQuarterFrame(byte)` (`:690`) and
`FeedFullFrame(ReadOnlySpan<byte>)` (`:713`), and the type names all say `Midi`.

> **Recommendation: make the naming/ingestion neutral now, while nothing depends on it.** Introduce
> `TimecodeValue`/`TimecodeChaseClock` with a value-level entry point (`FeedFrame(TimecodeValue, long
> timestampTicks)`) and keep the MIDI types as thin adapters over it. Renaming public framework types
> after HaCue2 ships is churn; doing it during the extraction is nearly free. `TimecodeSyncPlan` already
> reserves the `Ltc` slot, so the config model needs no change.

**Piece 3 — LTC generation (wait for the patch bay).** Generating LTC belongs *after* the bay, because it
lands naturally as an audio source patched to a logical output like anything else — which is exactly what
`Plans/HaCue-Feature-Ideas.md` proposes. The constraint recorded there is the important one: generated
timecode must derive from **the bay master's audible clock**, not wall time, or the generated code and the
program audio drift apart. That makes generation a post-bay item by dependency, not by priority.

**Also worth stating:** an LTC input is a *control* signal, not program audio — it must be its own capture
stream feeding the decoder and must never be routed through the cue audio patch.

### 5.5 Remote API (register item 5 / `POST /lists/{id}/go`) — PARTIAL, app-level only

Lives entirely in `UI/HaPlay/Remote/`. The dispatcher holds direct VM references (`RemoteApiDispatcher.cs:51-66`)
and hops to `Dispatcher.UIThread` per request (`:94-99`); the transport (`RestApiServer`) is VM-free and
portable, the dispatcher is not. Dispatch is a **hand-written nested switch over string literals with no
route table** (`:118-127`) — which is exactly why self-documentation is impossible today and why
`AllowedMethodsFor` (`:103-116`) has to re-parse the path and duplicate rules.

**The hard part of per-list GO is not routing — it is transport state.** GO's fireable order is scoped to
the *selected* list (`CuePlayerViewModel.cs:1069-1070`) and there is exactly one standby/current pointer for
the whole workspace; firing into a non-selected list goes through `GoForeignListAsync`
(`Transport.cs:283`), which is **deliberately headless** — its doc (`:271-274,283-289`) says it writes
neither pointer so a trigger/schedule/remote fire cannot move the visible transport. A real
`POST /lists/{id}/go` therefore depends on §2.1's per-list standby, not on a new route. Cue lists do
already have stable identity (`CueListEditorViewModel.RuntimeId:29`).

Telemetry is absent (no counters; only `ILogger` traces at `RestApiServer.cs:284,338-352` with a 250 ms
slow warning). Worth keeping as-is: bearer/query token with fixed-time compare (`:404-430`), size guards
(`:360`), 30 s deadline, OPTIONS/`Allow` (`:286-292`), graceful drain.

Must build: a route table/registry (which yields both `GET /endpoints` and per-route counters), atomic
counters, a `lists` domain, and the per-list standby it depends on.

### 5.6 Per-endpoint test messages (register item 24) — PARTIAL, all hardcoded

`ActionEndpoint` has no test-message field of any kind. The OSC probe hardcodes `/haplay/ping` with
`Int32(1)` (`ActionEndpointProbe.cs:18`). **The MIDI probe sends nothing at all** — it enumerates, resolves
by id → name → *falls back to the first device* (`:56-71`), opens the port and reports success on open
(`:27-52`), so "reachable" means "a port opened", which is weaker than the UI implies.

Reusable: `EndpointHealthMonitor` lifecycle (interval only while there is work `:45`, single-flight with
supersede-cancel `:62-72`), `ActionEndpointHealthState` (`ActionEndpointRowViewModel.cs:14-38`), and
especially **`ViewModels/CueMIDIActionMessage.cs`**, which already composes arbitrary MIDI messages from
persisted text (`CueMIDICommandType`, per-type labels/ranges, SysEx default `"F0 7D 00 F7" :16`,
`ParseEditorState :160`, `BuildCommandText :125`, `CreateMessage :200`) — store the configured test
message in that same serialized form rather than building a second MIDI editor. The Control workspace also
already has an operator-editable OSC test address (`Learn.cs:34-50`), just not attached to an
`ActionEndpoint`.

---

## 6. Telemetry, metering, logging and project status

### 6.1 Audio level metering — framework ABSENT; one app-level decorator exists but is the wrong shape

There is no peak/RMS/level measurement anywhere in `MediaFramework/` — no matches for
`Peak|Rms|LevelMeter` in `S.Media.Routing`, none for `PeakLevel|CalculatePeak|MeasureLevel|LevelTap`
across the framework.

**One implementation exists, app-side:** `UI/HaPlay/Playback/MeteringAudioOutput.cs:13`, a
disposal-transparent `IAudioOutput` decorator that scans `Submit` for absolute peak (`:64-81`) and exposes
`ReadAndResetPeakDb()` (`:56-62`). It is installed on **every** resolved audio-output lease by the
ShowSession audio-output factory (`MediaPlayerViewModel.ShowSession.cs:976`), after the line's effect
inserts, so it meters the processed signal. It does not satisfy rev-3 for four reasons:

- **Aggregation destroys identity.** All taps go into one `_meterTaps` list and `PollAudioMeters()` reduces
  them to a **single max per deck** (`MediaPlayerViewModel.cs:1247,1268-1284`). The per-output value is
  measured and then thrown away.
- **The measurement point is wrong.** A tap exists per *(firing clip × output lease)* — a voice-to-device
  edge. **No node in the audio graph corresponds to a logical output**: `AudioRouter` mixes source→output
  through the channel-map matrix straight into the device. Nothing sums "everything sent to *Lobby*".
- **Peak only, destructive read.** No RMS, no peak-hold decay, no clip/over latch, and
  `ReadAndResetPeakDb` permits exactly one consumer.
- **The UI control is an orphan.** `Views/Controls/LevelMeterControl.cs:11` has zero usages; its dB→0..1
  normalization is duplicated rather than shared at `MediaPlayerViewModel.cs:1261-1263`.

**Two traps worth naming, because both look like the answer and are not.**
`ShowSession.Levels.cs` / `ClipAudioLevels` (`ShowSession.cs:1178`, query `ShowSession.Transport.cs:370`)
are **control** levels — `fade × envelope × master` — not measured signal. And
`ShowSession.RegisterAudioTapAsync` (`ShowSession.Taps.cs:~85`) registers on the sounding bus as
**monitoring** (`RegisterMonitoring`), deliberately, so master trim cannot duck it and Panic cannot silence
it — a program meter must show exactly what those do affect.

**Where it can go.** Three insert points already exist: `IAudioBusEffect` in an `AudioEffectOutput` chain
(`S.Media.Routing/AudioEffects.cs:30,55,80`; today's only implementation is `GainAudioEffect:198`, so a
`MeterAudioEffect` is a ~40-line addition), `AudioEffectBus` (`S.Media.Routing/AudioEffectBus.cs:13`), and
`AudioBus` (`S.Media.Core/Audio/AudioBus.cs:30`) which is both output and source and can therefore act as a
summing node registered as a router source — at the cost of one ring hop per logical output.

**Recommended: meter the archived `ProgramBusSource` directly** — one peak/RMS per logical channel per
chunk, computed where the V-wide bus already exists, with no extra hop and no new graph node. That is also
the only place "per logical output" is physically meaningful. Then add, in the framework, a proper metering
primitive (peak + RMS + hold + clip latch, **non-destructive, multi-consumer**), a keyed query returning
`logicalOutputId → levels`, and wire `LevelMeterControl` for the first time (adding hold and clip). Rev-3
already makes ballistics, peak-hold and clip-reset application settings, so the app owns only presentation.

### 6.2 Output health and pump counters — counters EXIST, exposure is lossy, clock/latency ABSENT

**Every column in the rev-3 audio-bay table already exists** on `AudioRouter.OutputPumpStats`
(`AudioRouter.cs:1787`): enqueued/processed/dropped (`:1787`), capacity (`:1787`), abandoned — host flush,
correctly excluded from dropped (`:1800`), ready-evictions (`:1808`), in-flight (`:1812`, derived and
clamped), `IsStuck` (`:1791`); with `GetPumpStats` (`:723`), non-throwing `TryGetPumpStats` (`:738`) and
`GetAggregatePumpStats` (`:763`).

**But the session-level exposure throws most of it away.** `GetActiveAudioPumpStatsByDevice`
(`ShowSession.Queries.cs:232`) and `TryGetActiveAudioPumpStats` (`:253`) return only `(Enqueued, Dropped)`
per device id — processed, abandoned, ready-evictions, in-flight and capacity are read and discarded — and
that is exactly what HaPlay's line-health poll consumes (`CueShowSessionCoordinator.cs:878-891`). The full
stats *do* reach the app by another path (`GetActiveClipPipelineMetrics`, `Queries.cs:287` →
`MediaPlayerMetrics.AudioOutputs`, `MediaPlayerMetrics.cs:42-44`) but keyed by opaque router output id with
no device or terminal name.

- **Submit-to-output latency is absent from telemetry.** `IAudioOutputLatency.SubmitToOutputLatency` exists
  and is implemented/forwarded (`MeteringAudioOutput.cs:46`, `AudioEffects.cs:112`,
  `SharedAudioOutput.cs:272`) but appears in **no** metrics record and has no query; its only consumer is
  `SharedAudioOutput.cs:175`, for its own clock.
- **The clock surface is absent.** `MediaClockMetricsSnapshot` (`MediaPlayerMetrics.cs:17`) carries only
  `CurrentPosition` + `MasterTypeName`. `EpochId`, `IsAdvancing`, `ElapsedSinceStart` and `Read()` all exist
  on `IPlaybackClock` implementations (`SharedAudioOutput.cs:298,301,307,487`, forwarded through
  `MeteringAudioOutput.cs:96-102`, `AudioEffects.cs:171-179`) but nothing snapshots them. **The rev-3
  diagnostics screen's Epoch and "advancing" columns have no data source today.**
- **Terminal state is partial and the wrong vocabulary.** `Playback/OutputLineHealthEvaluator.cs:16,35,48`
  produces a 3-state traffic light from 1 Hz deltas (consumed at `OutputManagementViewModel.cs:1676-1706`) —
  enough for "behind" colouring, but not for rev-3's `open / advancing / armed / absent / releasing / idle`.
  **`absent` has no producer at all**: device disappearance is noticed only at open time, and `Error` is set
  only on arm failure (`OutputManagementViewModel.cs:1095`).
- **"Leases nested under their terminal" is derivable today, just not exposed.** The join key already
  exists: each voice carries `AudioPumps` as `IReadOnlyList<(string OutputId, string DeviceId)>`
  (`ShowSession.TransportVoices.cs:81`, set at `ShowSession.cs:831`, `ShowSession.LiveEdits.cs:273`), and
  `PublishGroupViews` walks *active voice → (router, outputId, deviceId)* (`Queries.cs:215-225`) before
  collapsing it, with cue identity on the same view (`:287`). So `(cueId, routerOutputId, deviceId)` plus
  full `OutputPumpStats` is computable inside `ShowSession` with no new plumbing — it needs a query that
  does not sum. Terminal-side lease count already exists (`SharedAudioOutput.cs:92 ActiveLeaseCount`).
- **"Reset counters" is absent** — all pump counters are monotonic with no reset API. Do it client-side with
  baselines, exactly as `PipelineStatsViewModel` already does (`:148,169`); do not add a framework reset.

Build: one `ShowSession` query returning terminal rows (device id, format, capacity, full `OutputPumpStats`,
latency, clock epoch/advancing) with nested lease rows (cue id, output id, that lease's counters), plus a
real terminal-state enum including `absent`. Lease-side counters come from the archived `ProgramBusProducer`
(`BufferedFrames`, `OverflowFloats`, `UnderrunFloats`).

### 6.3 Video telemetry — rich per composition, thin per output

`ClipCompositionRuntimeStats` (`ClipCompositionRuntime.cs:2024`, built at `:231-256`) already gives frames
composited/submitted, pump overruns, slot overflow, last/max pump time, frames-behind-master, **layer count**
(`:2034`), timing histograms and canvas period (`:2036`) — surfaced by `GetCompositionStatsAsync`/
`GetAllCompositionStats` (`ShowSession.Queries.cs:105-109`) and rendered live at
`PipelineStatsViewModel.cs:162-182`. Useful for the panel's alert line: `ClipCompositionDriftWarning` and
`ClipCompositionPumpPressureWarning` (`:2038,2045`).

- **fps is absent as a field but derivable** — nothing in the framework computes it;
  `PipelineStatsViewModel.cs:169` does `FramesComposited − prev` on a 1 Hz tick, with `CanvasPeriod` as the
  target for amber/green. Either add the field or document the delta contract.
- **Per composition output: only a throttled drop event** (`ClipCompositionRuntime.cs:1242-1252,1557-1573`),
  and only when the sink is a `VideoOutputPump`. No per-output fps, late/dropped counter or queue depth;
  `FramesSubmitted` is summed across outputs — which is why the cue-line health probe reports
  composition-wide numbers and hardcodes queue depth/cap to 0 (`CueShowSessionCoordinator.cs:843-892`).
  Per-output data *does* exist at the pump (`VideoOutputPump.cs:12`: `SubmittedFrames`, `DroppedFrames`,
  `MaxQueueDepth`, `CurrentQueuedDepth`, `SubmitTiming`) but is reachable only per clip via
  `MediaPlayerMetrics.VideoOutputs`.
- **The GPU/backend string is absent.** `S.Media.Gpu/Diagnostics/` holds only NV12 upload counters; GL
  vendor/renderer/version is captured solely to format a one-shot warning
  (`Nv12Win32SharedHandleGpuUploader.cs:377`) and is never stored or queryable. Rev-3's "GPU: GL" column has
  no source.

Build: counters on `AcquiredOutput` surfaced as `IReadOnlyList<ClipCompositionOutputStats>` inside the
composition stats (joined with `VideoOutputPumpMetrics` where present), an fps field or a documented delta
contract, and a small GPU-identity record captured once at renderer init.

### 6.4 Log tail (register item 27) — the premise holds; only the sink is missing

**`Microsoft.Extensions.Logging` is used uniformly and there is genuinely one pipeline.** Packages at
`Directory.Packages.props:32-33,55`; every framework project references `Logging.Abstractions`; the
framework funnels through one static — `S.Media.Core/Diagnostics/MediaDiagnostics.cs:28`, with
`LoggerFactory` (`:51`) and `CreateLogger(category)` (`:65`) — and HaPlay uses the **same** factory with
`HaPlay.*` categories in static fields (`App.axaml.cs:23`, `MediaRuntime.cs:22`,
`OutputManagementViewModel.cs:27`, `RestApiServer.cs:20`, `SessionRecoveryService.cs:37`,
`CuePlayerView.axaml.cs:23`, …).

Host wiring: `UI/HaPlay.Desktop/Program.cs:95-158` builds the factory with `AddSimpleConsole` +
`RollingFileLoggerProvider` (`:130-140`), assigns `MediaDiagnostics.LoggerFactory` (`:142`), installs crash
diagnostics (`:143`) — all **before** Avalonia builds, deliberately, so static-field loggers resolve through
it (`:82-84`). The file log is one file per process under `--media-log-dir` with retention pruning and a
bounded `DropOldest` channel drained by a background writer (`RollingFileLogger.cs:30,63-69,76`); CLI knobs
are `--media-log-level` (default **Trace**), `--media-log-dir`, `--media-log-queue`, `--media-log-retain`,
`--media-log off`, `--media-log-first-chance`.

**No in-memory sink exists** anywhere (the only other provider in the tree is
`UI/HaViz.Android/Platform/LogcatLogger.cs:7`). Two properties make adding one cheap:

1. The factory minimum level is **Trace by default** (`Program.cs:113,132`), so a new provider sees
   essentially everything and the UI level picker can be a pure sink-side filter — no
   `LoggerFilterOptions` reconfiguration needed.
2. A runtime level switch **cannot** copy the file logger's approach: `RollingFileLoggerOptions.MinimumLevel`
   is `init`-only (`:248`) and baked into each category logger at creation (`:80,267,277`), and this host has
   no `IOptionsMonitor`/config binding. Use a `volatile LogLevel` field read in the new provider's
   `IsEnabled`/`Log`.

Build: an `ILoggerProvider` holding a fixed-capacity ring of **structured** entries (timestamp, level,
category, formatted message, exception, eventId) with a change notification — note `RollingFileLogger.Log`
(`:279-297`) pre-formats to a single string, which is the wrong model to copy, since rev-3 renders time,
level and category as separate columns — plus a volatile minimum-level field, registration next to the file
provider in `ConfigureLogging` (or later via `MediaDiagnostics.LoggerFactory.AddProvider`, which refreshes
existing loggers), and a dropped-line count like the file provider's.

**Do not conflate two existing domain streams with the log tail** — both are legitimate and should stay:
`CueExecutionLogEntry` (`S.Media.Session/CueGraph.cs:92`, list `:110`, query `ShowSession.Queries.cs:100`)
and `ShowPlaybackAlert` (`ShowSession.Taps.cs:286-294`, consumed at `CueShowSessionCoordinator.cs:767`).

### 6.5 "Copy report" — ABSENT

No diagnostics-report serialization of any kind exists (no `CopyReport`/`BuildReport`/`DiagnosticsReport`,
no clipboard export on any stats path). Nearest prior art is `PipelineStatsViewModel`'s row formatting
(`:113,151,173`) — the right tone, but per-row UI string building, not a serializer. Cheapest path: one
snapshot aggregate (terminals + leases + compositions + clock + versions + log tail) consumed by both the
view and a text/JSON writer.

### 6.6 Project status checks (register item 25) — one real validator exists, and it never leaves the document

**The seed exists and is good.** `S.Media.Session/ShowDocumentValidator.cs:20` —
`Validate(ShowDocument) → IReadOnlyList<string>` (`:26`) with a throwing variant (`:220`) — is a genuine,
well-covered pass: schema version (`:29`); duplicate/empty cue ids, labels, duplicate numbers, unsupported
fault policy (`:35-52`); composition ids/dimensions/frame rate (`:55-67`); clip→cue and clip→composition
references, extra placements, scalar sanity, placement geometry (`:70-137`); follow-on (`:139`); stop
targets (`:143-147`); audio output id uniqueness and route→cue/declared-output resolution (`:148-171`).

Against rev-3's check list: **dead references are covered**; **missing media is absent** (`MediaPath` is only
checked non-empty, `:81` — the validator never touches the filesystem, by design); **absent devices are
absent** (no device awareness); and **an unresolvable clock master cannot be validated because the concept
does not exist at document level** — clock mastering is per-composition at runtime
(`ClipCompositionRuntime.cs:409,566`, flag `:2033`) and per-router (`IRouterClock`,
`OutputSlavedRouterClock`, `PlaybackSlavedRouterClock`); nothing in the document or in HaPlay's output
definitions names a project clock master. The project patch model introduces that concept, so the check
arrives with it.

**The probes needed all exist, and the hard part is already written.** Media:
`Playback/CueMediaProbe.cs:34` (`File.Exists` + `MediaContainerDecoder.Open` off-thread, null on failure,
returns duration/streams/channels — `internal` and FFmpeg-coupled), or cheaper and registry-level with no
open at all, `IMediaDecoderProvider.Probe(uri, kind) → confidence`
(`S.Media.Core/Registry/IMediaDecoderProvider.cs:28`, used by `MediaRegistry.cs:187`). Devices:
`IAudioBackend.EnumerateOutputDevices` (`S.Media.Core/Audio/IAudioBackend.cs:43`),
`PortAudioDeviceCatalog.EnumerateOutputDevices`/`EnumerateHostApis`
(`S.Media.Audio.PortAudio/PortAudioDeviceCatalog.cs:81,34`), and the session cache
`S.Media.Session/AudioOutputDeviceCache.cs:26,51`. **Crucially the matching logic already exists** —
`PortAudioOutputRuntime.ResolveCurrentOutputDevice` (`:249`, `internal static`, reusable as-is) and
`MatchBackendDevice` (`:216`) — but it is buried in the open path, runs only while opening (`:122-136`), and
logs a warning on drift instead of reporting status. For the non-audio half,
`Services/EndpointHealthMonitor.cs` already monitors MIDI/OSC endpoint reachability.

Build: a **second, environment-aware pass** (the document validator must stay pure — it runs on load and
throws): resolve every clip `MediaPath` via `File.Exists` + registry `Probe`, async and cached, off the UI
thread; lift `ResolveCurrentOutputDevice`/`MatchBackendDevice` into a reusable presence probe returning
present/substituted/absent; and once the patch model lands, check every logical output for
patched-but-unfed / unpatched-but-fed and validate the clock master. **Note the shape change:**
`Validate` returns bare strings, which is too weak for rev-3's error/warning split and per-row fix
navigation — upgrade to a record carrying severity, subject id and a navigation target.

**Decision D9: the CLI verb is draft.** The headless form ships (it is what makes the checks usable as a
pre-show script and a CI gate over fixture projects), but whether it is `hacue2 --check`, `--preflight`
or a subcommand is deliberately unfixed — treat every occurrence in these docs as a placeholder.

---

## 7. App-support libraries, architecture, build and test

### 7.1 `HaOutput` — PARTIAL; the engine exists, extraction is blocked by eight couplings

`UI/HaPlay/ViewModels/OutputManagementViewModel.cs` (1869 lines) genuinely is the thing the plan
describes, and already has most seams: stable-ID definitions (`Models/OutputDefinitions.cs`, 306 lines,
**already Avalonia-free** — the "Avalonia" hits at `:12,20,168` are enum *names*), five per-kind runtime
dictionaries with per-kind locks (`:259-267`) over `OutputPreview/` runtimes, transactional lease
acquire/release with documented ordering (`AcquireVideoOutputForLine :47` → admission gate `:49-51` → raw
hold `:79` → effect wrap, with a catch that releases both `:70-76`; release disposes wrapper then hold
`:174-190`), reconfiguration callbacks (`:455,457`, `ReconfigureLineAsync :699` with ID/kind guards
`:709-717` and a single UI-thread commit `:764-772`), effects wrapping (`:101,122-139,159`), and health
snapshots (`RefreshOutputHealth :1669` on a 1 s timer `:378`, scored by
`Playback/OutputLineHealthEvaluator.cs`).

**`IOutputRuntimeCatalog` does not exist** — the "catalog" is the concrete class, consumed by
`MediaPlayerViewModel.cs:33,522`, `CueShowSessionCoordinator.cs:31,166`, `SoundboardWorkspaceViewModel.cs:28`,
`CueCompositionRuntime.cs:28,196`, `IdleLogoSlateSession.cs:24`, `MainViewModel.cs:75,668`. Six
interface-extraction sites.

**Raw-terminal acquisition is net-new and is the hardest piece.** `PortAudioOutputRuntime.cs:24-25` keeps
`IAudioOutput? _output` private; `Start()` (`:51`) unconditionally wraps it in `SharedAudioOutput` (`:63`);
`AcquireForPlayback` (`:81`) only ever returns a lease. `SharedAudioOutput` has no exclusive/bypass mode
(`:13,92,101`), and its own doc (`:6-11`) states the fan-in mixer is "the only producer that ever submits
to the terminal output" because the PortAudio ring is single-producer — so raw mode must **guarantee the
mixer is not running** for that line, not merely add an API. Nothing in `OutputDefinitions.cs` carries an
acquisition-mode flag, so the mode joins the persisted contract and the mutual exclusion must be enforced
at `Start()`/`Acquire()`, not in the VM.

Blocking couplings to invert: (1) `: ViewModelBase`; (2) **owns its own dialogs** —
`using HaPlay.Views.Dialogs`, `ShowEdit*` `:1384-1434`, `Add*Async` `:1499-1655`, hand-built `Window`s at
`:509-551,1446`; (3) **reaches into app lifetime** — `TryGetOwnerWindow()` `:792` via
`Application.Current.ApplicationLifetime`, used at `:486,848-849,1335` (a second app head breaks this;
inject an owner-window provider); (4) **`MediaPlayerViewModel` in the public surface** —
`ActivePlayersProbe :279` is typed `Func<IReadOnlyList<MediaPlayerViewModel>>`; (5) `HaPlay.Resources.Strings`
(1429 lines / 4246-line resx) at `:352-353,513-542,1096` and throughout `OutputLineViewModel.cs:356-391`;
(6) **`MediaRuntime`/`RuntimeModules` are `internal static` app singletons** (`MediaRuntime.cs:20`,
`RuntimeModules.cs:10`) used at `:31,129,146,344-346,1242-1244` — a shared library cannot depend on them;
(7) `OutputLineViewModel` is both the runtime dictionary key and holds a back-pointer to the manager
(`:11`, commands `:424-427`) and `using Avalonia` — so model and VM are one object and the dictionaries
must be re-keyed by `Guid`; (8) `LocalVideoPreviewRuntime.cs:142,151,389,399` takes the manager and
manipulates `Avalonia.Controls.Window` directly — irreducibly UI-side, belongs in a `HaOutput.Ui`.

Verdict: the ownership/lease/reconfigure engine is extractable with moderate effort once (2)(3)(4)(5)(6)
are inverted; the dialog/add/edit half and health-string formatting stay app-side.

### 7.2 `HaSource` — PARTIAL, and the model half is genuinely Avalonia-free

`UI/HaPlay/Models/PlaylistItem.cs` (326 lines) declares the JSON contract at `:13-22` —
`[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]` with 8 derived types over the abstract base,
stable `Guid Id` at `:28`. **Confirmed Avalonia-free**: zero hits in `PlaylistItem.cs`,
`HaPlayPlaybackHelpers.cs`, `TextSourceSpecMapper.cs`, `HaPlayShowMapper.cs`. Helpers exist
(`Playback/HaPlayPlaybackHelpers.cs`: `BuildNDIInputUri :50`, `BuildPortAudioInputUri :71`, `BuildMMDUri :93`,
`BuildYouTubeUri :126`, `TryGetPreparedYouTubeAssetPath :116`, `StartBackgroundPhysicsBake :24`,
`IsAudioCapableOutput :15`; `Playback/TextSourceSpecMapper.cs` is a clean pure mapper), as do the six
dialogs.

Blockers: visibility (`HaPlayPlaybackHelpers :11`, `TextSourceSpecMapper :12`, `YouTubeRuntime :12` are all
`internal static`; `InternalsVisibleTo` currently covers only tests/desktop, `Properties/AssemblyInfo.cs:3-5`);
**`CueSubtitleSelection` lives in the cue model** — `FilePlaylistItem.Subtitles` (`PlaylistItem.cs:53`)
references `CueList.cs:517`, so either that record moves into HaSource or the split cuts through
`CueList.cs` (the one hard model entanglement); the **global alias**
`global using PlaylistItem = HaPlay.Models.PlaylistItem;` (`GlobalUsings.cs:6`) exists specifically to
disambiguate from `S.Media.Session`'s own `PlaylistItem` and **must be replicated in HaCue2 and HaSource or
the framework type silently binds**; blast radius is 66 files (25 in tests), mostly namespace churn.

### 7.3 Architecture tests — the rules are real but **`UI/` is invisible to them**

`MediaFramework/Test/S.Media.Arch.Tests/ArchitectureTests.cs` has a genuine rule table (`Allowed :17-84`)
enforced by `ProjectReferencesAreDownwardAndAllowed :131` and completeness-checked by
`EveryFrameworkProjectIsRegisteredInTheRules :120` — but the scope limiter `FrameworkDirs :87-89` lists only
`Media/Control/Interop/Audio/MIDI/NDI/OSC/Subtitles/Visualizer` and `FrameworkProjects :99-109` walks only
`<root>/MediaFramework/<sub>`. **`UI/` is never enumerated**, so `HaPlay` (which references 25 framework
projects) is under zero layering enforcement today. The only checks that see UI projects are
`EverySolutionProjectExistsOnDisk :198` and `NoSolutionProjectIsGitignored :215` — which *will* catch a new
`HaCue2.Desktop.csproj` added to the sln but not `git add`ed.

The plan's rule ("register each new library in the arch tests") therefore requires **first adding a UI scope
at all**: a `UiProjects(root)` walker plus a `UiAllowed` map for `HaOutput`, `HaSource`, `HaControl.Input`,
`HaCue2.Core`, `HaCue2`, `HaCue2.Desktop`, `HaPlay*`, `HaViz.*`. Note the map must allow out-of-tree names
(`HaPlay.csproj` references three `External/Classic.Avalonia` projects; there is precedent —
`YoutubeExplode` at `:64`). **Decision D10: `UI/**/*.Tests` are exempt**, the same way the existing map
already exempts `Tools/` and `Test/` — test projects legitimately reach across layers, so enforcing them
would add churn without catching real violations. Cost is near zero: the arch test project has no project
references and is pure XML parsing.

### 7.4 Build, publish and CI

- **Solution registration** needs three blocks per project: the `Project(...)` entry, the per-config rows
  (`MFPlayer.sln:976-987` for HaPlay), and a `NestedProjects` mapping into the `UI` folder GUID (`:1543`).
- **`Directory.Build.props`** enforces `net10.0`, `Nullable enable`, `AllowUnsafeBlocks`, and
  **`IsAotCompatible=true` for every project** (test/tool projects must explicitly opt out, as
  `HaViz.Core.Tests` and `S.Media.Arch.Tests` do).
- **`Directory.Build.targets`** enforces **`TreatWarningsAsErrors=true` everywhere** unless
  `SkipWarningsAsErrors=true`, and auto-marks anything outside `MediaFramework/` non-packable — so a new
  `UI/HaCue2*` needs no packaging action.
- **Central package management** (`Directory.Packages.props`, with transitive pinning): new projects use
  versionless `PackageReference`; any *new* package needs a `PackageVersion` entry.
- **NativeAOT head**: mirror `HaPlay.Desktop.csproj` — `OutputType=WinExe`, `PublishAot=true`, and
  critically **`AssemblyName` must differ from the library** (HaPlay uses `HaPlayer`, HaViz uses `HaVizBox`)
  because `HaCue2.dll` would collide and `avares://HaCue2/...` targets the library;
  `WarningsNotAsErrors=IL2026;IL2104;IL3053` if Dock.Avalonia is used; `ApplicationManifest`;
  `ConcurrentGarbageCollection` + `ServerGarbageCollection` for the realtime audio path; the SDL3-CS
  RID handling and the win-x64-only `FFmpeg.GPL` reference. `HaViz.Desktop.csproj` is the closest
  "second head" template.
- **CI (`.github/workflows/build.yml`, 908 lines)**: solution build (`:110`) and tests (`:143`) pick a new
  project up automatically, but **`HaPlay AOT publish` is a gating step (`:253`)** with no HaCue2
  equivalent — without one, an AOT regression ships silently. Launch smokes (`:263,275`) are
  `continue-on-error` and driven by `HAPLAY_SMOKE=1`/`HAPLAY_SETTINGS_PATH`; a HaCue2 equivalent needs its
  own env var and self-exit hook. The artifact job's native-manifest checks (`:668,673,681`) and the
  **gating** "launch uploaded app + enumerate backends" (`:686,702`, grepping for `MediaRuntime ready` and
  `HAPLAY_SMOKE: first frame rendered`) take a publish dir argument and are reusable, but
  `.github/native-manifest/{linux-x64,win-x64}.txt` currently describes the HaPlay native set.
  **Branch filter — fixed (D11).** `on: push: branches` was `[master, 'next-*']`, which did not match
  `cue-separation`, so pushes to the extraction branch got CI only via pull request. Now
  `[master, 'next-*', 'cue-*']`, following the existing `next-*` convention so future extraction branches
  are covered without another edit.

### 7.5 Storage paths — the shared-cache half is already right

`Models/HaPlayStoragePaths.cs` (36 lines): `LocalAppRoot :16-19` (`HAPLAY_CACHE_ROOT` override, else
`<LocalApplicationData>/HaPlay`, with an AOT-safe fallback chain `:21-34`), `RecoveryRoot :23`.

Four collision sites for a second app, all under that root: `app-settings.json`
(`Models/AppSettings.cs:99`, env override `:97`, `FilePathOverride :95`), `recent-projects.json`
(`MainViewModel.cs:1835`), `recovery/{sessionId}` (`Services/SessionRecoveryService.cs:108,463,525`),
`unsaved-scripts/{guid}` (`ControlWorkspaceViewModel.cs:491`).

**The plan's "one shared media cache" is already true by accident**: `YouTubePreparer.DefaultCacheRoot` is
`<LocalApplicationData>/mfplayer/youtube-cache` (`S.Media.Source.YouTube/YouTubePreparer.cs:46-47`) and MMD
bake is `<LocalApplicationData>/mfplayer/mmd-bake` (`S.Media.Source.MMD/MMDBakedPhysics.cs:287`) — both under
`mfplayer/`, not `HaPlay/`.

Two consequences: (1) **`HAPLAY_CACHE_ROOT` does not sandbox the media caches** — both read
`SpecialFolder.LocalApplicationData` directly and never consult `HaPlayStoragePaths`, so
`TestCacheSandbox` (`UI/HaPlay.Tests/TestCacheSandbox.cs:16-20`) redirects settings/recovery/scripts but
**not** those caches, and tests touch the real user cache; a `HaCue2.Tests` inherits the hole.
(2) Nothing is parameterized by app — `HaPlayStoragePaths` is a static class with hardcoded `"HaPlay"`
(`:19`) and `"HaPlay-user"` (`:33`), and env-var names are hardcoded in three places; CI also hardcodes
`HAPLAY_SETTINGS_PATH` (`build.yml:270,281,691,705`).

### 7.6 Test infrastructure — copy it wholesale, and mind the hang

`UI/HaPlay.Tests` carries ~1023 lines of harness a `HaCue2.Tests` must replicate. The critical file is
**`HeadlessSessionBootstrap.cs`** (48 lines): `[assembly: Xunit.TestFramework("HaPlay.Tests.HeadlessSessionFramework", "HaPlay.Tests")]`
(`:5`) installs a custom xunit framework whose constructor warms `HeadlessUnitTestSession` **before any test
runs**. The documented rationale (`:9-27`): Avalonia binds `Dispatcher.UIThread` to the *first* thread that
touches it, process-wide; if a plain VM test ran first, UIThread binds to an xunit worker and the session's
first `Dispatch` kills its own loop — the whole run hangs with zero tests, and **test order decides whether
it happens**. A new test assembly that copies the csproj but not this file will reproduce that hang. The same
constructor sets `HAPLAY_DISABLE_RECOVERY_TIMER=1` (`:41`) because every `MainViewModel` a test constructs
starts a never-stopped 2 s recovery `DispatcherTimer`.

Also required: `AvaloniaHeadlessTestApp.cs` (`[assembly: AvaloniaTestApplication]` +
`DisableTestParallelization`), `HeadlessDispatchExtensions.cs` (214), `DispatcherOwnershipGuard.cs` (128),
`HeadlessDispatchLintTests.cs` (327 — a lint that *enforces* correct dispatch usage),
`RawStringLiteralLintTests.cs`, `TimingFactAttribute`, `FFmpegNativeFactAttribute`, `TestCacheSandbox`.

**Known fragility to budget for** (documented at `build.yml:113-140`): a 2026-07-02 hang canceled at ~6 min;
`--blame-hang --blame-hang-timeout 4m`; `-m:1` + `RunConfiguration.MaxCpuCount=1` to serialize **assemblies**
because concurrent composition/transport pumps CPU-starve timing tests on 2–4-core runners;
`VSTEST_CONNECTION_TIMEOUT=300`; and a two-attempt whole-invocation retry emitting
`::warning title=Tests passed only on retry::`. **A second headless-Avalonia test assembly makes
cross-assembly contention worse** — HaCue2.Tests must also disable parallelization, and adding it is a real
risk to the existing serialization budget.

---

## 8. Plan statements that need correcting

| # | Plan says | Reality |
|---|---|---|
| 1 | MIDI/OSC input device ownership lives in `ControlWorkspaceViewModel` (`:505`) | The device layer is already in `S.Control` (`ControlInputSession` etc.). Only ~120 lines of lifecycle glue, two duplicate learn flows, trigger matching and the endpoint/probe types are app-side (§5.1). |
| 2 | Phase 3 builds `AudioPatchBay` etc. | Already built, tested and recoverable at `bdf27ffd` (§0). Phase 3 is recover → review → rebase. |
| 3 | "Quarantine and hot-swap a wedged terminal without interrupting other lines" | True for non-master lines *after* the archive's fix (deferred pump dispose moved off the run loop). A wedged **pacing master** still faults and permanently kills the router — no watchdog exists (§1.3). |
| 4 | "One `AudioRouter` for the whole project" | True only of the bay. Each cue voice still carries its own (`MediaPlayer.cs:874`) unless opened with `IncludeAudioRouter: false`, which forfeits the audio-mastered `MediaClock` (§1.4). |
| 5 | "P+R passes, not P×R" | Holds only when a pair has ≥ `MinFusedMatrixCells = 4` live cells (`FusedMix.cs:24`); sparse patches degrade to per-cell passes (§1.2). |
| 6 | "Live patch updates … never interrupt an active cue" | True for routes/gains/outputs; **false** for anything routed through `ApplyMatrix` at UI rate (allocates a mix-plan rebuild on the router thread, `Matrix.cs:108`) and for the pacing master (§1.2, §1.3). |
| 7 | "`SharedAudioOutput` is just a fan-in to replace" | It also supplies device keepalive (silence source + `FlushOutputsOnNaturalEof=false`), the per-client playback clock, and downstream-latency accounting. Replacing it means re-implementing four things (§1.2). |
| 8 | Register item 5: multi-list transport, each list remembers its position | Standby is not a framework concept at all, and **`LoadDocumentCoreAsync` tears down every group on every reload**, resetting all cursors *and stopping all playback in all lists* — while the app reloads on a 300 ms debounce after edits (§2.1). This is the largest uncosted item. |
| 9 | Register item 22: "a per-output test pattern ships in v1" | No per-output pattern exists; today's is composition-wide and lights up every bound output (§4.4). |
| 10 | Register item 21: visualizer decoupling | Correct and cheap (the composition flag is dead code) — but the framework enforces **one visualizer per composition** and surface layers always sort above frame layers, so N simultaneous visualizer cues is new work (§4.2). |
| 11 | Register item 23: per-output idle image as a fallback | Exists only for local SDL/Avalonia outputs and is **suppressed the entire time a cue list holds the line** — i.e. unavailable during a show. Needs to move into the composition pump (§4.3). |
| 12 | Register item 21: "a composition owns exactly size, frame rate, idle image" | There is a real, wired fifth property today — composition-level `VideoFx`/`ShowComposition.OutputMapping`. Dropping it is right, and retiring `_compositionMappingStage` re-enables the GPU integrated multi-output warp (§4.1). |
| 13 | Register item 15: "audition enters the bay as a monitoring input" | Not achievable without change: preview bypasses `_audioOutputFactory` entirely and hardcodes 2 channels (`VoicePlayer.cs:182`). The archive fixes the audio half; the **video** half of the audition rig does not exist at all (§4.5). This also answers plan open question 17 concretely: today it is 2, fixed. |
| 14 | Register item 17: "one integer for the whole group" is a limitation to fix | That is an app-model statement. The framework's crossfade duration *and curve* are already per-fire arguments and the pre-end window is already per-clip — per-transition overrides need **zero** framework change (§3.3). |
| 15 | Register item 16: "the same curve control everywhere a curve is picked" | Three current curve uses are hardcoded `EqualPower`, not picked, and the loop-crossfade curve has no document representation at all (§3.1). |
| 16 | Plan invariant: deleting a referenced logical channel offers cancel/remove/rebind | Superseded by register item 11 (automatic cleanup, one undoable command) — already reconciled in the plan, noted here for completeness. |
| 17 | "Register each new library in the architecture tests" | The arch tests **cannot see `UI/` at all** today; a UI scope must be added first (§7.3). |
| 18 | Preflight/Project status is new | Half-right. A real document validator already exists (`ShowDocumentValidator`) and covers every dead-reference class; what is missing is the *environment-aware* half (missing media, absent devices, clock master) — and **all of those probes already exist**, including the device-matching logic, buried in the open path (§6.6). |
| 19 | Register item 27: "the events panel tails the MS logging pipeline" | The premise is sound — MEL is used uniformly through one factory — but **no in-memory sink exists** and a runtime level switch cannot copy the file logger (its `MinimumLevel` is `init`-only and baked per category); use a volatile field (§6.4). |
| 20 | Diagnostics counters are "all existing framework counters" | True of the pump counters, but the session query **discards most of them** (returns only enqueued+dropped per device), and epoch / advancing / submit-to-output latency are snapshotted nowhere. Three of the mockup's columns have no data source (§6.2). |
| 21 | The plan's `ClipAudioOutputRuntime` "delete or seed" question | Settled: confirmed dead, and **not** a usable seed — its topology is the `P×R` form being replaced. The archive already deletes it (§1.2). |

---

## 9. Consequences for the plan's phases

Nothing here changes the plan's *shape*; it changes sequencing and reveals four items that were not costed.

**Do first, before any phase work:** tag `bdf27ffd` (§0). It is the only copy of ~3300 lines of tested
framework work and it is on a reflog clock.

**Phase 0 (decisions, fixtures, characterization)** — mostly unchanged, but:
- the wide-matrix benchmark it calls for **already exists** in the archive (`WideMatrixBenchmarks.cs`,
  sweeping 8/16/32/64 plus a program-sum-at-maximums case); recover and run it rather than writing it.
- `ClipAudioOutputRuntime`'s fate is decided: delete (the archive already does).
- add one decision: **what happens when the pacing master wedges** (§1.3).

**Phase 2 (extract output runtime)** — add the eight named couplings (§7.1) as explicit sub-tasks, and note
that **raw-terminal acquisition is net-new work in `PortAudioOutputRuntime` + `SharedAudioOutput` + the
persisted definition**, not a refactor. Add the arch-test UI scope here (§7.3), since that is what makes the
new library boundaries enforceable. **Grown by D4:** `HaControl.Input` now carries the control-surface
feedback half as well as the input half (§5.1).

**Phase 3 (project audio patch)** — retitle from "implement" to **"recover, review, rebase"**. The exit
criteria stay; the work is validating the archived implementation against them, plus the open items in
§1.4. **Grown by D2:** add the clock-master watchdog with pre-emptive handoff (§1.3) — new work the
archived bay does not contain, in the router's most fragile area, so it wants its own design review and
its own tests. Also fold in the non-destructive load (§2.1) here, since both are `ShowSession`/router
changes and D1 makes that shared work rather than HaCue2 tax.

**Phase 4/5 — four newly-visible work items to schedule:**

1. ✅ **Non-destructive document load** (§2.1) — **LANDED.** `LoadDocumentAsync` gained an opt-in
   `preserveActiveGroups`. A transport group survives a reload only when **every** voice it holds (active
   and crossfade tails alike) still maps to a clip binding equal in every field; any other group is torn
   down exactly as before. Retained groups keep their voices *and* their `LastFiredNumber` GO cursor — the
   per-list playhead multi-list transport needs. The strictness is the safety property: being eager would
   leave a show playing something you just edited away, which is far worse than a restarted cue. Binding
   comparison uses record equality for the scalars (so fields added later are covered automatically instead
   of rotting out of a hand-written list) plus element-wise comparison of the five list members, which
   record equality compares by reference — and two separately deserialized documents never share instances.
2. ✅ **Audio metering** (§6.1) — **LANDED** as `ProgramBusMeter`, metered on the program sum.
3. **The video shortfalls** (§4.3, §4.4, §4.5) — per-output test pattern/Identify, composition-owned idle
   images, and the audition video surface. Each is modest; together they are a work package.
4. **The control shortfalls** (§5.2, §5.6) — continuous-controller→parameter bindings (with a
   parameter-target registry and rate coalescing) and per-endpoint configurable test messages.
5. **The diagnostics package** (§6.2–§6.5) — a non-summing terminal+lease telemetry query, a clock/latency
   snapshot, an in-memory log-ring provider with a volatile level switch, and a report serializer. Each is
   small and independent; the log sink in particular is nearly free given the pipeline is already uniform.
6. **The environment-aware status pass** (§6.6) — a second validator beside the pure document one, reusing
   the probes that already exist, and an upgrade of `Validate`'s return shape from bare strings to records
   carrying severity, subject id and a navigation target (rev-3 needs all three per row).

**Also scheduled (D3, confirmed v1):** the outbound OSC/MIDI automation runner (§3.2) — a third feature
wearing the timeline's UI, with its own rate / land-exactly / coalesce requirements, living beside the
action-endpoint sender rather than in `ShowSession`. Its three contract points (no undo on cue stop,
explicit Panic behaviour, sender-side placement) should be settled in design review before the lane model
is finalised, because they constrain the lane record's shape.

**Net effect of D2–D4 on sequencing:** Phase 2 gains the feedback extraction, Phase 3 gains the watchdog
*and* the non-destructive load, and the automation package gains a third lane kind. None of these is
architecturally risky alone, but Phase 3 is now carrying the bay recovery, the load-path change and a
fault-path watchdog simultaneously — the plan's own risk register warns about stacking structural changes
in one phase, so this is the phase to split if any needs splitting.

---

## 10. Session ownership — share, split, or fork `ShowSession`?

Owner question (2026-08-01): *"the current ShowSession model is very attached to HaPlay — would it make
sense to create something separate for HaCue2, keeping/duplicating parts that make sense in both, or
have a shared library? Both apps might drift apart, which becomes an issue when substantial parts of the
ShowSession model are shared."*

The instinct is right that something is wrong, but the evidence points at a **different fault line** than
app-vs-app.

### 10.1 What the code actually shows

**`ShowSession` is not HaPlay's — it is already a general playback-engine contract with two clients.**
HaPlay contains **two independent mappers** that compile *into* `ShowDocument`:

- `Playback/HaPlayShowMapper.cs` (567 lines) — cue lists → document.
- `Playback/MediaPlayerShowMapper.cs` (118 lines) — **`ToShowDocument(mediaPath, hasVideo, audioRoutes,
  canvas…)`**, i.e. the *deck* wrapping a single playlist item in a one-clip document, loaded at
  `MediaPlayerViewModel.ShowSession.cs:675`.

The deck's use of the session (`MediaPlayerViewModel.ShowSession.cs`, 1549 lines) is almost entirely
neutral: compositions (`AddCompositionOutputAsync`, `RemoveCompositionOutputAsync`,
`SetCompositionTestPatternAsync`, `SetCompositionVisualizerAsync`, `GetCompositionStats`), audio routing
(`ApplyActiveAudioRoutesAsync`, `RebuildActiveClipAudioOutputsAsync`), transport (`StopAsync`,
`SetPausedAsync`), metrics, and `LoadDocumentAsync`. It touches cue vocabulary only where the engine
forces it — `FireCueAsync` and `DefaultGroup` — because firing a cue is the only way to make the engine
play anything. **The deck does not want cues; it wants a playback engine and pays a one-cue tax to reach
it.**

**The soundboard does not consume `ShowSession` at all.** `grep ShowSession` over
`UI/HaPlay/ViewModels/Soundboard*.cs` returns **zero hits**. `SoundboardWorkspaceViewModel.cs:103` exposes
a `PlaySoundCallback` and nothing else; its only `S.Media.Session` dependency is the pure static helper
`SoundboardQuantization` (`SoundboardGrid.cs:75`, used at `:254`). It is the *coordinator* that wires
those callbacks onto the cue workspace's session (`CueShowSessionCoordinator.cs:703,721,727,731,733,1624`).
So "the soundboard depends on the cue player" is a **wiring decision, not a design one** — and the
session's voice API (`FireVoiceAsync`/`StopVoiceAsync`/`SetVoiceVolumeAsync`/`FadeVoiceAsync`/
`IsVoicePlayingAsync`/`GetVoiceProgress`, `ShowSession.cs:947-987`) is neutral polyphonic one-shot
playback with a host-chosen string key, not a soundboard concept.

**`ShowDocument` is barely cue-shaped.** Of its six members (`ShowDocument.cs:264-286`) — `Version`,
`Cues`, `Clips`, `Compositions`, `Routes`, `AudioOutputs` — **only `Cues` is a cue-player concept**, and
`ShowDocument.cs` defines *zero* cue types: `CueDefinition` (number, armed, enabled, pre/post-wait,
follow-on, stop targets, auto-continue, fault policy) lives in `CueGraph.cs:47-61`. The document's
cue-ness is concentrated in the `Cues` list plus the fact that `CueId` is the join key
(`ShowClipBinding.CueId:108`, route validation `ShowDocumentValidator.cs:165`, `StopTargetIds:143-145`).
**Rename `CueId` → `ClipId`, move `Cues` out, and the remainder is a generic playback graph** — which is
exactly what the deck already exploits by inventing a synthetic cue called `"player"`.

**Two dead files show the layer has been accumulating app-shaped debris**, both with **zero external
references**: `ClipAudioOutputRuntime.cs` (310 lines) and — newly found — `SoundboardGrid.cs` (267
lines, of which only the static `SoundboardQuantization` helper is live). Decoys shaped like
responsibilities the session was once expected to own.

**Sizing — the cue-shaped surface is small and already separated.** `S.Media.Session` is 11,772 lines;
`ShowSession` + its 9 partials ≈ 4,150. The genuinely cue-shaped part is `CueGraph.cs` (343, already its
own class), `CueFireOrchestrator.cs` (216), `GoAsync` + the cursor (~35 lines in `Transport.cs:178-209`),
the `AddCue` loop (`ShowSession.cs:504-517`), and one document member. **Roughly 3,900 of the 4,150
partial lines are app-neutral**, plus 2,051 more in `ClipCompositionRuntime`. A cue/neutral split cuts
cleanly through `Transport.cs` and ~15 lines of `ShowSession.cs`, and through *nothing* in the other
seven partials — because the existing partial seams are **concern seams, not app seams**
(`ShowSession.cs:49-70`).

**Coupling direction is clean and one-way.** `S.Media.Session.csproj` references only framework projects;
the only "HaPlay" tokens in the whole project are ~10 comment lines documenting intent. Both mappers live
app-side.

**`ShowDocument` on disk is write-only in production** — `ShowDocumentSidecar.cs:52-57` writes one
`<project>.show.<n>.json` per cue list on save, but **nothing in HaPlay reads it back**; the only
`FromJson` call sites are the **C ABI host** (`S.Media.Interop/NativeApi.cs:187`) and
`Tools/SessionSmoke`. That matters for divergence: the sidecar is an interchange/export artifact whose
real consumer is an external ABI, so document changes are an external-compat question, not just an
internal refactor.

### 10.2 Assessing the three options

**(c) Fork — give HaCue2 its own session, let HaPlay keep the old one. Reject.** The overlap is not the
cue semantics (~600–900 lines, already separated) but the engine (large, subtle, concurrency- and
clock-critical). Forking duplicates `CommitClipAsync`, the voice model, the stop-claim protocol, the level
bus, composition lifetime, device acquisition and the visualizer service — roughly 10,000 lines whose
shape *is* their bug history (the claim/supersession rules at `ShowSession.Stops.cs:143-155` and
`VoicePlayer.cs:580-646` are the same hard-won argument written twice). **None of that is cue-specific,
so none of it would ever legitimately diverge** — you would simply maintain two copies. It would also
fork `ClipCompositionRuntime` (2,051 lines), which both apps need identically. The drift this "solves" is
cheap; the drift it creates is expensive.

**(a) Keep one shared `ShowSession` unchanged. Reject as-is.** It leaves the deck paying a cue tax, keeps
soundboard voices inside the cue session (which the plan already wants out), and leaves HaCue2's
evolution — per-list cursors, automation lanes, curve specs — constrained by a type the deck also
depends on.

**(b) Shared core + per-app semantics. Recommended — but cut it engine-vs-cue-semantics, not
HaPlay-vs-HaCue2.**

- **The engine stays one shared framework thing** and neither app forks it: compositions, clips,
  transport voices, standby pre-roll, audio/video attach, routing, clocks, taps, output leases,
  diagnostics. This is where the value and the risk both live.
- **The cue-semantics layer is HaCue2's domain**: `CueGraph`, follow-ons, the GO cursor, standby, and the
  cue-shaped parts of the document. HaPlay's deck already demonstrates the engine is drivable without it.
- **`VoicePlayer`'s voice API leaves the cue session** (as the plan already says) — it is neutral
  one-shot playback and can sit on the engine or beside it; either way HaCue2 should not inherit
  soundboard responsibilities.
- **Delete both decoys** (`ClipAudioOutputRuntime`, `SoundboardGrid`) so nobody reads them as evidence
  that the framework owns those concerns.

### 10.3 Managing the drift — the pattern is already proven in-tree

The drift worry is legitimate and does not require duplication to answer — but the current machinery is
thinner than it looks and has one real trap.

**What exists:** `ShowDocument.Version` (`:265`) plus `ShowDocumentValidator.SupportedVersion = 1`
(`:23`) checked for **exact equality** (`:31-32`). No migration, no forward compatibility, no capability
flags, and the JSON context is `internal` (`ShowDocument.cs:292`) so a host cannot extend serialization
from outside the assembly. `LoadDocumentCoreAsync:439-446` null-coalesces missing arrays, which gives
*additive backward* compat for free — an old document loads into a newer build. There is no reverse.

**The rule that follows:**

> **Add HaCue2-only capability as additive, nullable members, and never bump `Version` unless the meaning
> of an existing member changes.**

This is exactly what the archived attempt did: `ShowClipBinding.LogicalSends` is nullable, takes
precedence *only* when the session has an `IShowProgramAudioTarget`, falls back to the v1 direct-route
adapter otherwise, and ships with **no version bump** — with validation restricted to cell sanity because
(in its own comment) whether a `LogicalChannelId` exists is "a PROJECT question the session cannot answer
from the document alone". The document carries data, the *project* owns references, the engine ignores
what it has no target for.

**The trap — enum extension is NOT safely additive.** Enums round-trip **numerically** in the sidecar, so
adding a `FadeCurve` variant (or any new enum member) produces JSON that older builds and — critically —
**the C ABI host silently mis-read** as a different, valid value. Since the sidecar's only real reader is
that external ABI (§10.1), this is the one category that genuinely needs either a version bump or a
nullable side-channel. For custom fade curves specifically, that argues for the nullable `CustomCurve`
companion field recommended in §3.1 rather than extending the enum.

**Recommended before either app diverges:** replace the hard `==` with `MinSupported`/`Current` and an
additive-tolerant load. Three of the four foreseeable HaCue2 document changes (logical sends, automation
lanes, per-list data) are additive; only the enum/semantic ones are not. Without tolerant loading, each
additive change still risks forcing a lockstep release across HaPlay, HaCue2 and the ABI.

### 10.4 What to do, and when

**Now (during the extraction, while it is nearly free):**

1. **Delete the two dead files** (keep the live `SoundboardQuantization` helper).
2. **Extract `VoicePlayer`'s soundboard half** — the cheapest and most obviously correct cut. Of its 647
   lines, ~430 are voices and ~105 are preview; the voice half touches **no document, no cue, no transport
   group, no composition** (`BuildVoiceSpec`, `ShowSession.Taps.cs:318-334`, takes a raw path + device id).
   It shares only five infrastructure things with the cue path — the serial dispatcher,
   `ClipStandbyEngine` (`VoicePlayer.cs:148,254`), the `SoundingSourceRegistry` level/stop bus (`:328`,
   enumerated by `ShowSession.Stops.cs:70,81`), the single completion tick
   (`ShowSession.Completion.cs:73`), and the device cache — and the stop-claim protocol is *already*
   extracted into a shared type (`SoundingStopClaim.cs`, deliberately: `VoicePlayer.cs:63-68`). Give it
   `(dispatcher, standby, soundingRegistry, deviceCache)` instead of `ShowSession` and the session's eight
   voice methods (`ShowSession.cs:947-987`, all one-line delegations) fall away. **This alone removes the
   "soundboard depends on the cue player" liability that prompted the question.**
3. Keep every HaCue2 document addition additive and nullable, per §10.3 — and treat enum extension as a
   breaking change.
4. **Fix the version handling** (`MinSupported`/`Current`, additive-tolerant load) *before* divergence
   starts, since the sidecar has an external ABI consumer.
5. Make the timecode contract transport-neutral (§5.4a) — same "rename while nothing depends on it"
   argument.

**During the session work (Phase 3, alongside the non-destructive load of §2.1):** introduce the explicit
engine/cue-semantics seam:

- **`ShowSession` core, cue-free** — `LoadDocumentAsync`, `PlayClipAsync`/`CommitClipAsync`,
  transport-by-clip-id, seek/pause/stop/level, compositions, live edits, taps/visualizers, queries.
  Rename the join key `CueId` → `ClipId`; the deck already treats it as an opaque string. A rename, not a
  redesign.
- **Cue layer on top, in its own type** — `CueGraph` + `CueFireOrchestrator` + `GoAsync`/cursor + the
  `AddCue` loop become a cue runner that *drives* the core (~600 lines, along seams that already exist).
  `ControlShowActions.cs` binds to the cue layer; `NativeApi.cs` keeps the composed pair.
- **Split the `ShowDocument` record, keep one serialization envelope** — `Cues` moves next to the cue
  layer; `Clips`/`Compositions`/`Routes`/`AudioOutputs` stay core; one JSON envelope so the sidecar and
  the C ABI do not fork.
- **Do the non-destructive load once, in the core.** It is the only genuinely shared semantic change,
  **both apps want it** (HaPlay needs editing-while-playing too), and forking before fixing it means
  fixing it twice.

**Explicitly not now:** a full `ShowSession` decomposition. The plan's own risk register warns against
doing the app split and the routing rewrite simultaneously, and the same applies here — with the patch bay
recovery (§0) and the load-path change (§2.1) both landing in the same phase, a third structural rewrite
of the same files is how a rollback happens. Phase 7 already reserves "split the `ShowSession` facade
further … only where ownership is now proven"; that is the right home for the remainder.

### 10.5 The counter-argument, and why it does not apply — ANSWERED (D1)

The counter-argument was: if HaPlay were heading for retirement or long-term freeze, (c) becomes
defensible, because forking is tolerable when only one fork stays alive.

**Owner decision D1: HaPlay keeps evolving alongside HaCue2.** That closes the question in favour of the
shared engine — and it *raises* the bar on the seam rather than lowering it. Two consequences follow that
would not matter under a retirement plan:

1. **The seam has to be real, not notional.** With both apps changing, "shared engine" only works if the
   cue layer is genuinely liftable — otherwise every HaCue2 cue-semantics change lands in a class the deck
   loads documents through, and the coupling reasserts itself. The §10.4 steps (rename `CueId` → `ClipId`,
   extract the cue runner, split the document record) stop being tidiness and become the deliverable.
2. **Version tolerance is required, not optional.** Two actively-developed apps plus an external C ABI
   consumer sharing one document format cannot run on a hard `Version ==` check without forcing lockstep
   releases. §10.3's `MinSupported`/`Current` change moves from "recommended" to "do it before divergence
   starts".

The corollary worth stating plainly: **the engine now has two first-class customers, so engine changes
need to be justified against both.** The non-destructive load (§2.1) is the model case — HaPlay wants
editing-while-playing just as much as HaCue2 wants multi-list transport, so it is shared work, not
HaCue2 tax.

---

## Implementation progress (updated 2026-08-02)

Rows marked ✅ in the register below are in the tree. Counting only the register rows that represent
*work* (the ~40 rows that were ABSENT, PARTIAL or archived-but-not-recovered — the ~20 EXISTS rows
needed nothing), the state is:

| Area | Work rows | Landed | Remaining |
|---|---|---|---|
| Audio (framework) | 11 | **10** | raw-terminal acquisition — the remaining half is app-side (§7.1) |
| Video | 9 | **8** | GL-backend surfacing in a host UI (app-side) |
| Control | 9 | **4** | all three remaining are app-side: external-input gate, per-endpoint test message, session lifecycle glue + learn |
| Session | 6 | **6** | — |
| Automation | 4 | **2** | group-level lanes (RESOLVED: flatten in the mapper, no framework work) |
| Diagnostics | 3 | **3** | — |
| Fades | 3 | **1** | — the other two were re-scoped into the landed curve work |
| Build | 4 | **3** | second AOT head + CI gates (app-side) |
| Everything else (status, remote, app-support) | ~8 | 0 | all Phase-2 app-side |

**Hardening pass (commit `0d2ac4a2` "Gap fixup", 2026-08-02).** 34 framework files, ~+1,250 lines (about
half tests), no app-side changes. It did not open or close register ROWS - it fixed correctness inside rows
already marked landed:

- **`AudioPatchBay`** — terminal replacement is now staged then swapped, so a factory/format failure leaves
  the old terminal flowing instead of tearing it down; the bay owns clock policy explicitly and suppresses
  the router's generic adaptive hook (auto-promotion on removal could silently make a rate-adapted secondary
  the pace master while the bay reported none); adaptive-rate wrapping is role-aware (master direct at the
  project rate, secondaries wrapped) and re-staged on master promotion; monitor inputs follow the stable
  terminal id across a swap.
- **`AudioRouter`** — `AddOutput` overload to suppress the generic adaptive hook, `RemoveOutput` with
  cancellation.
- **`OutboundRampRunner`** — multi-point ramps (`OutboundRampPoint`) and `WaitForPendingSendAsync`.
- **`ShowDocumentValidator`** — envelope validation for BOTH lanes (negative times, unsorted points,
  non-finite levels, volume outside 0..+12 dB). The opacity lane shipped without it.
- **`LinearTimecodeGenerator`** — rejects an invalid authored label at construction and at `Seek`.

**Framework tranche: COMPLETE (2026-08-02).** Every register row that lands inside `MediaFramework/` is
done. Everything still open is Phase-2 app-side work in `UI/HaPlay/` — verified by locating each remaining
row's files rather than by reading its label: the external-input gate is three services under
`UI/HaPlay/Services/`, the per-endpoint test message is `ActionEndpoint*` under `UI/HaPlay/`, and
raw-terminal acquisition is `PortAudioOutputRuntime` under `UI/HaPlay/OutputPreview/`.

So roughly **39 of ~41 work items**, but that understates the weight: the recovered bay is the single
largest piece of framework work in the plan (~3,300 lines with ~1,100 lines of tests), and with the
non-destructive load now landed alongside it, **Phase 3's major framework items are complete** —
recovery, the clock-master watchdog, per-logical-output metering, and the load path. What remains in
the register is spread across video, control, automation, diagnostics and build.

Everything landed is verified: the solution builds 0 warnings / 0 errors with `TreatWarningsAsErrors`
on repo-wide, and every framework suite passes. **`HaPlay.Tests` reports 27 failures out of 1014, all
pre-existing** — verified by stashing the entire change set and re-running on a clean tree, which
produced the identical 27. They are UI layout/interaction tests (`CuePlayerLayoutBoundsTests` and
friends), several named `DEFECT_*`, so some appear to be deliberately-failing known-defect tests.

## Appendix — gap register

| Area | Capability | Status | Where |
|---|---|---|---|
| Audio | `AudioPatchBay`, program bus, producer leases, monitor input | ✅ **LANDED** (recovered from the tag) | §0, §1 |
| Audio | Wedged non-master terminal quarantine/hot-swap | ✅ **LANDED** (recovered) | §1.3 |
| Audio | Wedged **pacing master** survival | ✅ **LANDED** — `ClockMasterWatchdog` + `PromoteClockMaster` (D2) | §1.3 |
| Audio | Resampler factory injection | EXISTS (`IMediaRegistry.CreateResamplingOutput`) | §1.1 |
| Audio | Marker to stop a resampled terminal becoming clock master | ✅ **LANDED** — `IRateAdaptedOutput`; auto-promotion refuses it | §1.2 |
| Audio | Raw-terminal (exclusive) line acquisition | **ABSENT — APP-SIDE** (`UI/HaPlay/OutputPreview/PortAudioOutputRuntime.cs` never exposes the raw terminal) | §1.4, §7.1 |
| Audio | Per-lease input health counters on the router | ✅ **LANDED** — `ProducerDiagnostics` rows (buffered / overflow / underrun / latency / epoch) | §1.4, §6.2 |
| Audio | Output pump counters / health | EXISTS | §1.1, §6.2 |
| Audio | Level metering (peak/RMS) | ✅ **LANDED** — `ProgramBusMeter`, non-destructive, frame-based decay | §6.1 |
| Audio | Per-logical-output summing node to meter at | ✅ **LANDED** — metered inside `ProgramBusSource.ReadInto` | §6.1 |
| Audio | Clock epoch / advancing / latency in telemetry | ✅ **LANDED** — in `AudioPatchBay.SnapshotDiagnostics()` | §6.2 |
| Audio | Terminal state vocabulary | ✅ **LANDED** — `TerminalState` (master/open/behind/quarantined). `absent` deliberately excluded: presence is a host fact the bay cannot know | §6.2 |
| Audio | Lease rows exposed | ✅ **LANDED** — all leases feed the one program bus, so they are listed beside terminals rather than nested under one | §6.2 |
| Session | N list-scoped transport groups + per-group GO cursor | EXISTS | §2.1 |
| Session | Standby / next-cue pointer | ✅ **LANDED** — GO cursors moved onto the session (survive a preserving reload, playing or not) + public `GetStandbyCueAsync`/`SetStandbyCueAsync` per group | §2.1 |
| Session | **Non-destructive document load** | ✅ **LANDED** — `preserveActiveGroups` retains unchanged groups + their GO cursors | §2.1 |
| Session | Per-cue `Disabled` | EXISTS in framework, ABSENT in app model | §2.2 |
| Fades | Centralised curve math | EXISTS (`FadeCurves`) | §3.1 |
| Fades | Custom (point-list) curves as data | ✅ **LANDED** — `CustomFadeCurve` + `FadeShape`, additive nullable document fields (no enum extension) | §3.1 |
| Fades | Per-transition playlist crossfade | EXISTS (per-fire args) — app-only work | §3.3 |
| Automation | Volume envelope | EXISTS | §3.2 |
| Automation | Layer-opacity lane | ✅ **LANDED** — `VisualLevel` (authored x fade x automation) on both slot kinds + `ShowClipBinding.OpacityEnvelope` + `StartOpacityLaneRunner` | §3.2 |
| Automation | Outbound OSC/MIDI ramps | ✅ **LANDED** — `OutboundRampRunner` (explicit rate, lands on final value incl. on interrupt, coalesces) | §3.2 |
| Automation | Group-level lanes | No document home (flatten in the mapper) | §3.2 |
| Video | Per-output mapping (warp vs clean) | **EXISTS, fully wired** | §4.1 |
| Video | Visualizer off compositions | EXISTS (flag is dead code) | §4.2 |
| Video | N visualizer cues per composition | ✅ **LANDED** (D5) — slots keyed by (composition, visualizerId); one surface per source preserved | §4.2 |
| Video | Visualizer z-order freely orderable | ✅ **LANDED** — one `(LayerIndex, Sequence)` order across frame+surface layers; `CompositorSurfaceLayer.DrawAfterFrameLayers` interleaves in the GL compositor | §4.2 |
| Video | Idle image usable during a show | ✅ **LANDED** — composition idle (+ per-output fallback), shown by the pump whenever the canvas is empty | §4.3 |
| Video | Per-output test pattern / Identify | ✅ **LANDED** — `SetOutputTestPatternAsync`, substituted upstream of that output's mapping so the grid is warped | §4.4 |
| Video | Audition video surface | ✅ **LANDED (composed)** — `AuditionCompositionSpec` + enable/disable/attach/detach on `ShowSession`; preview places onto it | §4.5 |
| Video | Per-output telemetry | ✅ **LANDED** — `ClipCompositionOutputStats` rows (submitted / refused / failures / queue depth / mapped) | §6.3 |
| Video | Composition fps field | ✅ **LANDED** — `TargetFramesPerSecond`; achieved fps stays a caller-side delta | §6.3 |
| Video | GPU/GL backend identity | ✅ **LANDED** — `GraphicsDeviceIdentity` (vendor/renderer/version/GLSL/GLES/max-texture), captured at GL compositor + projectM renderer init | §6.3 |
| Diagnostics | MEL pipeline, single factory | EXISTS and consistent | §6.4 |
| Diagnostics | In-memory subscribable log sink + level switch | ✅ **LANDED** — `LogRingProvider` (structured entries, volatile level, drop count, EntryCaptured) | §6.4 |
| Diagnostics | "Copy report" serialization | ✅ **LANDED** — `AudioPatchBayReport.Render`, invariant-formatted plain text | §6.5 |
| Status | Document validator (dead references) | ✅ **LANDED** — `ShowValidationIssue` (severity + subject kind/id for navigation); `ThrowIfInvalid` blocks on Errors only | §6.6 |
| Status | Missing-media / absent-device checks | ABSENT — but every probe already exists, incl. device matching | §6.6 |
| Control | Device layer (open/monitor/resolve/persist) | EXISTS in `S.Control` | §5.1 |
| Control | Session lifecycle glue + learn + matching | app-side; lift-and-rehome | §5.1 |
| Control | cc → parameter bindings | ✅ **LANDED** — `ParameterRegistry` + `ContinuousBinding` (soft takeover) + `CoalescingParameterWriter` | §5.2 |
| Control | Single external-input gate | **ABSENT — APP-SIDE** (all three gates live in `UI/HaPlay/Services/`) | §5.3 |
| Control | MTC decode/chase | EXISTS | §5.4 |
| Control | Audio input capture (LTC prerequisite) | EXISTS (`PortAudioInput`, `padev:` scheme) | §5.4a |
| Control | LTC decoder | ✅ **LANDED** — `LinearTimecodeDecoder`, biphase-mark, polarity/amplitude independent | §5.4a |
| Control | Transport-neutral chase contract | ✅ **LANDED** — `MidiTimecodeChaseClock.FeedFrame(value)` ingests whole frames from any transport | §5.4a |
| Control | LTC generation | ✅ **LANDED** — `LinearTimecodeGenerator`, pull-based, exact-rational frame length; round-trips through the decoder at all 4 rates | §5.4a |
| Remote | Route table / self-documentation / counters | ABSENT (hand-written switch) | §5.5 |
| Remote | `POST /lists/{id}/go` | **PARTIAL** — list-scoped cue addressing landed (`GET /lists`, `/lists/{list}/cues/{cue}/go\|stop`). The BARE `/lists/{id}/go` still needs per-list standby in the VM (10 `StandbyCueNode` write sites to mirror) — a feature, not a route | §5.5 |
| Endpoints | Per-endpoint configurable test message | **ABSENT — APP-SIDE** (`ActionEndpoint*` are all `UI/HaPlay/`) | §5.6 |
| App support | `HaOutput` engine | EXISTS, 8 couplings to invert | §7.1 |
| App support | `IOutputRuntimeCatalog` | ABSENT (6 extraction sites) | §7.1 |
| App support | `HaSource` model | EXISTS, Avalonia-free; `CueSubtitleSelection` entangled | §7.2 |
| Build | Arch-test coverage of `UI/` | ✅ **LANDED** — `UiAllowed` map + 3 tests, incl. "no app references another app" | §7.3 |
| Build | Second AOT head + CI gates | Template exists; HaCue2 steps ABSENT | §7.4 |
| Build | Per-app settings/recovery roots | ✅ **LANDED** — `HaPlayStoragePaths.AppName` (root + `{APP}_CACHE_ROOT` derived; late change throws) | §7.5 |
| Build | One shared media cache | ✅ **LANDED** — `S.Media.Core.MediaCachePaths` (`MFPLAYER_CACHE_ROOT`); both cache sites now redirectable, and the test sandbox sets it | §7.5 |
| Test | Headless Avalonia harness | EXISTS (~1023 lines) — must be copied, incl. the anti-hang bootstrap | §7.6 |
| Session | `ShowDocument` as a shared engine contract | EXISTS — two mappers already target it (cue + deck); only 1 of 6 members is cue-shaped | §10.1 |
| Session | Engine / cue-semantics seam | ✅ **LANDED** — `CueId`→`ClipId` (wire pinned), cue-free core + `PlayClipAsync`, cue-runner lift (`CueRunner` owns the graph via `ICueRunnerHost`, arch-guarded), validator split (`CueListValidator`). Record split deferred: the envelope must stay one type | §10.2 |
| Session | Soundboard→cue-session coupling | Wiring only — the soundboard VM never references `ShowSession` | §10.1 |
| Session | `VoicePlayer` voice/preview split | ✅ **LANDED** — `SoundboardVoicePlayer` + `CuePreviewPlayer` on narrow `ISessionVoiceHost`/`ISessionPreviewHost`; arch-test guards the seam | §10.4 |
| Session | Dead code in the session layer | ✅ **LANDED** — both deleted; `SoundboardQuantization` split out | §10.1 |
| Session | Additive-nullable document evolution | EXISTS as a proven precedent (`LogicalSends`, no version bump) | §10.3 |
| Session | Tolerant document versioning | ✅ **LANDED** — `MinimumSupportedVersion`..`CurrentVersion`, tolerant below / closed above | §10.3 |
