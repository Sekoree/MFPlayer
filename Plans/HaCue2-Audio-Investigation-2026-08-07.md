# HaCue2 Audio Investigation — 2026-08-07

Investigation of the reported HaCue2 problems: audio stutter / mini-dropouts / pops, no audio
after firing the "everything" group, A/V sync doubts, the active cue list reordering itself and
appearing late, the inspector jumping back to the General tab during multi-edit, and the patch
matrix clipping physical output names.

Evidence base: the session log `~/.local/share/HaCue2/logs/hacue2-20260807-063454-13851.log`
(06:34–06:42, the failing session with `NewestTest.hacue2proj`), source reading across
`S.Media.Routing` / `S.Media.Session` / `HaCue2.*`, and three purpose-built probes that drive the
**real** project, device, and patch-bay code outside the app (see "Reproduction probes" at the end).

---

## 1. The audio killer: grant leak → permanent voice death  *(root cause, proven)*

### Symptom

Firing GO on the group opens 13 players (11 stems + 2 `.mov`s). Within seconds, voice audio
routers die one by one with:

```
ERR AudioRouter RunLoop: WaitForNextChunk failed without cancellation ·
    InvalidOperationException: AudioRouter pacing clock failed (primary output faulted or lost); run loop stopping.
```

~12 of these in the log after the first GO (06:38:49–06:39:03), and again ~11 after the second GO
(06:40:02–06:40:10). A faulted voice router never restarts — that voice is **silent for the rest of
the show**. This is the "hit go and no audio gets through" symptom.

### Mechanism

The voice's clip router is paced by its `ProgramBusProducer` lease (the `_program` output is the
pacing primary). Pacing works on a *grant* system introduced in commit `2535a4f4`:

1. `WaitForCapacity` takes a grant of `chunkSamples × channels` floats
   (`MediaFramework/Media/S.Media.Routing/Audio/ProgramBusSource.cs:426-478`).
2. The grant is released **only** when the audio arrives in `Submit`
   (`ProgramBusSource.cs:337`).
3. Between the two, the chunk travels through the router's output pump. Every drop path in
   `OutputPump.Commit` — backpressure cap expired (1 s), pool exhausted, ready-queue eviction
   (`MediaFramework/Media/S.Media.Routing/Audio/AudioRouter.OutputPump.cs:140-186`) — discards the
   chunk **without releasing its grant**. That credit leaks *forever*; only `Flush` (pause/seek)
   resets `_grantedFloats`.
4. The pacing target is half the ring = 8192 floats; one chunk = 960 floats. After **8 leaked
   chunks**, `TryTakeGrant` can never succeed again.
5. Terminal detail: a ring that drains to empty never signals waiters — `MixInto` only signals
   when it actually read data (`ProgramBusSource.cs:517-519`). So the wedged router sits in a
   signal-less 5 s wait (`ProgramBusSource.cs:452`), `WaitForCapacity` returns `false`,
   `OutputSlavedRouterClock` forwards the failure
   (`MediaFramework/Media/S.Media.Routing/Audio/OutputSlavedRouterClock.cs:75`), and the run loop
   treats it as a fatal pacing failure (`AudioRouter.cs:1616-1636`). Voice dead.

### Proof

A probe against the real `ProgramBusSource` (healthy grant/submit/read loop, then simulated
drops — grants taken, never submitted):

```
healthy: buffered=0 frames
wedged after 8 leaked grants (dropped chunks)
ring fully drained after 0 bus reads; buffered=0
WaitForCapacity on EMPTY ring: REFUSED after 5.00s
```

The log's timing matches to the decisecond: first voice submit ~06:38:44.3, first faults at
06:38:49.49 (5.2 s later).

### What causes the drops in the app

The same log shows whole-process stalls right at GO: two different pump drainers simultaneously
spent 233 ms inside `ProgramBusProducer.Submit` (a plain ring write), and a decoder call took
891 ms. 13 simultaneous cold opens + the 1080p60 12-bit YUVA 4:4:4 ProRes + UI work stall the
drainer past the 1 s backpressure cap → `Commit` drops → grant leaks. **Each drop is also a 10 ms
content skip — that is the audible pop/mini-dropout.** Pops accumulate until the voice wedges
completely.

Crucially, the engine stack **without the Avalonia shell is clean** on this machine: the real
project + real PortAudio device + full group GO — headless, with real SDL video windows, and even
after simulating the session's 14 live-edit reloads — runs with all 11 producers pinned at
half-ring, zero drops, zero underruns, zero faults, negligible GC. The graph is correct; app-side
stalls trigger the drops, and the grant leak turns a transient stall into permanent silence.

### Recommended fixes (importance order)

1. **Release the grant when a chunk is dropped** anywhere in the pump path, or make
   `ProgramBusProducer` self-heal: on the 5 s timeout, reconcile `_grantedFloats` against reality
   (ring empty + nothing in flight ⇒ credit is stale), log, and retry instead of returning false.
2. **A timed-out capacity wait must not permanently fault a voice router.** Degrade (drop,
   reanchor) and recover; a live cue player should never turn a 5-second hiccup into "silent until
   re-fired".
3. Add a regression test shaped like the probe — the new `ProgramBusPacingTests` cover healthy
   pacing and flush but not the drop path.

---

## 2. Why the "Some sync fixes" commit felt inert: no clock master in the project

`NewestTest.hacue2proj` has `"clockMasterLineId": null`. Everything in commit `2535a4f4` —
video-only voices genlocked to `bay.MasterClock`, producer clocks rebased to the master terminal —
only engages when a terminal **is** the clock master. With none, the bay free-runs on a wall clock
and voice clocks ride the wall-clock fallback: the old drifting behavior, unchanged.

- **Operator action**: set "Local output" as the clock master (Audio → Patch). It opens natively
  at 48 kHz via PortAudio's JACK host API (PipeWire shim), so it is eligible
  (`AudioPatchBay.ValidateRate` requires native mix rate for a master).
- **App changes worth making**:
  - Default `clockMasterLineId` to the first eligible local line when a project is created (or
    flag "no clock master" in project status). Right now it is an invisible foot-gun.
  - `ProjectPatchBay.Open` failures go only to the status bar and are never logged
    (`UI/HaCue2.Engine/ProjectPatchBay.cs:129-133`) — this session's log looked device-less.
    Log them too.

Verified on this machine: the hint `"Ryzen HD Audio Controller Analog Stereo"` resolves to
PortAudio device id 11 (JACK host API) and opens fine at 48 kHz stereo; a bay probe drained 6 s of
paced audio with zero underruns.

---

## 3. Video / UI slowness on GO

In the failing session the composition pump ran 2–30× over budget (up to 526 ms per 16.7 ms tick,
`slotOverflow` climbing to 776) and video decode waited seconds for queue slots. But the **same
composition on the same machine keeps up fine** when driven without the Avalonia shell — the
projector/compositor path is not inherently too slow; the app process is stalling it.

- Video is *not* rendered on the UI thread (SDL windows render on their own threads), so
  "separating video from the UI" is not the fix. Reducing the UI-thread storm and fixing §1 is.
- Known UI churn: `CuesViewModel.Tick()` clears and re-adds both ObservableCollections wholesale
  4×/s (`UI/HaCue2/ViewModels/CuesViewModel.cs:1408-1424`, `1656-1676`), resetting the
  ItemsControls each tick.
- If slowness persists after those fixes, profile the running app next.

---

## 4. Active cue list

### 4a. Reorders itself with playback position

`CuePresentation.Active` sorts by `state.Elapsed`
(`UI/HaCue2/Presentation/CuePresentation.cs:128`). Since the transport rework, `Elapsed` is the
**playhead position** (`UI/HaCue2.Engine/ShowHost.Transport.cs:368-378`), not run time — so loop
wraps, seeks, pauses, and playlist advances shuffle rows. The engine already stamps the fire time
(`Sounding.StartedTicks`, `ShowHost.Transport.cs:27`, stamped at `:283`) and throws it away.

**Fix (three edits, one construction site):**
1. Add `long StartedTicks` to `ActiveCueState` (`UI/HaCue2.Engine/ShowHost.cs:30-31`).
2. Pass `entry.Value.StartedTicks` in `SnapshotAsync` (`ShowHost.Transport.cs:371-378` — the only
   `new ActiveCueState(...)` in the codebase).
3. Sort with `OrderBy(state => state.StartedTicks)` (earliest first = newest last, the documented
   intent), optionally `.ThenBy(state => state.CueId)` for batch-fire ties.

Checked: the bare-STOP fallback (`CuesViewModel.cs:446`) still gets the right cue; the remote API
and Sample runtime never serialize `ActiveCueState`, so nothing else needs touching.

### 4b. Appears very late

Two layers:
1. **Engine**: a cue only enters the active set after `await _session.FireCueAsync(...)` completes
   the *entire* media open/commit (`UI/HaCue2.Engine/ShowHost.Execution.cs:110-121`; fires also
   serialize behind `CueRunner._fireLock`). Cold/heavy files show nothing for seconds, and there
   is no "starting…" placeholder.
2. **UI**: `EngineRuntime` polls at 250 ms on `DispatcherPriority.Background`
   (`UI/HaCue2/Session/EngineRuntime.cs:31, 66`) — the lowest priority, deferred whenever the UI
   thread is busy; the `_polling` gate skips ticks while a snapshot is in flight, and
   `SnapshotAsync` awaits per-cue-list standby queries across the busy session dispatcher.

**Fix**: mark the cue sounding at fire start (and `Forget` it on a failed status); and/or trigger
an immediate out-of-band `Poll()` when a fire lands plus raise the timer priority above
`Background`. Layer 1 is the piece that makes it feel "very late".

---

## 5. Inspector: multi-edit "add send" springs back to the General tab

Chain: adding a send raises `ProjectJournal.Changed` synchronously → `ShellViewModel.Refresh` →
`CuesViewModel.Rebuild` clears the TreeDataGrid selection → `Inspector.Show([])`. The tab memory
in `InspectorViewModel.Show` only records the open tab when the outgoing selection is **single** —
the `SelectionCount <= 1` guard (`UI/HaCue2/ViewModels/InspectorViewModel.cs:120`). With a
multi-selection "AUDIO" is never remembered, so when `RestoreSelection` re-shows the cues, reload
falls back to General (`InspectorViewModel.cs:155-168`). Single-selection edits work only because
that guard passes.

**Fix**: remember the tab for multi-selections too — minimally delete `&& SelectionCount <= 1`, or
remember per-kind for every kind in the selection. Recall is already guarded by
`Tabs.Contains(remembered)`, so remembering a tab a kind lacks is harmless.

---

## 6. Audio → Patch: physical output name clipped

The matrix row header is a fixed-width Border: `RowHeaderWidth="118"` at
`UI/HaCue2/Views/AudioView.axaml:203`, consumed by
`UI/HaCue2/Controls/MatrixView.axaml:40-43` via the `MatrixRowHeaderWidth` resource, with no
trimming (styles at `Themes/Chrome.axaml:551-566`). Headers are `"{line.Name} · Out {n}"`
(`UI/HaCue2/Presentation/AudioPresentation.cs:102`) — real device names far exceed 118 px.

**Fix**: treat `RowHeaderWidth` as a minimum — when `Rows` changes, measure the longest header
(`FormattedText` with the `mxhead` mono font + its 5+5 padding and 1 px border) in
`MatrixView.axaml.cs` and publish `max(RowHeaderWidth, measured)` into the resource. The top-left
corner spacer shares the resource, so the whole column widens together and the pane's horizontal
ScrollViewer absorbs the width. (Cheap stopgap: `TextTrimming="CharacterEllipsis"` + a tooltip.)

---

## Reproduction probes

Session-scoped scratch projects (not part of the repo; recreate from this description if gone):

- **BayProbe** — enumerates PortAudio devices, resolves the project's device hint, opens the
  output at 48 kHz stereo, attaches it to an `AudioPatchBay` with no clock master (like the
  project), and paces a producer through `WaitForCapacity`. Result: device opens (JACK host API,
  id 11), 6 s drains cleanly. Its `--grantleak` mode is the §1 proof.
- **ShowRepro** — loads `NewestTest.hacue2proj`, starts a real `ShowHost` with `PortAudioBackend`
  (headless or `--video` with real SDL windows, `--reloads N` to simulate live-edit reloads),
  fires GO on the list, and prints bay/producer diagnostics + GC stats per second. Result: clean
  in every configuration tried (25–30 s runs).

Location at time of writing:
`/tmp/claude-1000/-home-sekoree-RiderProjects-MFPlayer/086810be-6423-42ac-8f96-5f179befeae2/scratchpad/`

---

## Implementation status (same day)

All of the below are implemented and tested; suites S.Media.Core.Tests (786), S.Media.Session.Tests
(359), HaCue2.Core.Tests (704) and HaCue2.Tests (381) all pass, and the ShowRepro probe stays clean
against the fixed framework.

- §1 grant leak: `IGrantPacedOutput` drop reporting from `OutputPump.RecordDrop`/`AbandonQueue` +
  one-per-episode stale-credit write-off in `WaitForCapacity` (dead-bus still fails visibly).
  Regression tests in `ProgramBusPacingTests`.
- §2: bay failures reported to the Problems surface and logged; open/failure summary logged with
  the clock-master state; new "Audio clock" ProjectStatus warning (flags `NewestTest.hacue2proj`).
  Setting the clock master in the project remains an operator action, per the codebase's
  "a real decision, not a default" policy.
- §4a: `ActiveCueState.StartedTicks` + fire-order sort; `ActiveOrderTests`.
- §4b: sounding at fire start with `ConfirmSounding`/`Forget`, `ShowHost.SoundingChanged` →
  immediate UI poll, poll timer at `DispatcherPriority.Input`.
- §5: tab memory written for every kind in a multi-selection; regression test verified to fail on
  the old guard.
- §6: `MatrixView` measures the longest row header and widens the shared resource;
  `MatrixHeaderWidthTests`.

## Round 2 — live-app profiling (same day, after the first fixes)

Reported: audio fixed structurally but still popping every ~2 s; video took ~a minute to react
after GO; UI intermittently frozen. Profiled by launching the real app with the remote API and
firing GO over HTTP; per-thread CPU + `dotnet-dump` stacks + KWin window scripting.

Findings and fixes:

1. **Pops every ~2 s = masterless bay drift** — `PortAudioOutput: ring full - dropped …` every
   couple of seconds. With no clock master the bay free-runs on the wall clock; the device
   consumes on its own crystal; `ProjectPatchBay` built the bay with no resampler/adaptive-rate
   factories, so nothing absorbed the drift (and the adaptive wrapper couldn't have: its bias
   source is pump drops, and PortAudio drops inside its own ring without ever backing the pump
   up). **Fix**: when the document names no clock master, the first local line whose device opened
   at the mix rate is promoted automatically (logged; ProjectStatus still warns so the operator
   makes it explicit), and the registry's resampler + adaptive-rate factories are now wired into
   the bay for foreign-rate/multi-line rigs. Verified live: terminal state `AdvancingMaster`,
   0 ring-full over multi-minute runs — and the A/V genlock chain from commit 2535a4f4 is now
   actually engaged by default.
2. **Batch fire aborting ("slot id 'slot_3' is already registered")** — `VideoCompositorSource`
   default slot ids were `slot_{count+1}`, which reissues a live id after any removal. On a
   composition with layer churn this aborted `FireCuesIndependentAsync`, fell back to one-at-a-time
   fires, and in one observed run silently skipped a cue entirely. **Fix**: monotonic ordinals for
   slot and surface-slot default ids + regression tests (`CompositorSlotIdTests`).
3. **Remote API returned `{"error":"the request could not be completed"}` although cues fired** —
   the slot-collision exception escaping `GoAsync` after partial success. The generic 500 now
   carries the underlying message.
4. **The catastrophic 2.9 fps / frozen-UI session could not be reproduced outside the IDE.** The
   identical binary, launched normally on the same machine with the same project, runs the full
   group GO with both ProRes videos compositing at speed and a responsive UI (verified twice, with
   screenshots, ~23 % total CPU). The failing sessions were launched from Rider — with ~200 threads
   spawning at GO (FFmpeg decoder pools) and heavy JIT, a debug run pays debugger round-trips for
   every thread start and first-chance event, which matches "first GO after launch is horrific"
   and "UI responds every few minutes". Recommendation: test performance with Run (not Debug), or
   a Release build; treat in-IDE Debug behaviour as non-representative for this workload.

All suites re-run green after round 2 (Core 786 + new, Session 359, Compositor 56, HaCue2.Core
704, HaCue2 381).

## Round 3 — sync + frame-dip measurement (same day)

Reported after round 2: much better, but "buffer issues", frame-rate dips, and "cues not quite in
sync". Measured with an instrumented headless/windowed probe printing per-cue playhead offsets and
composition stats every second.

Findings:

1. **The sync spread is real and start-time, not drift**: ~230 ms headless, ~525–595 ms with real
   video windows, rock-constant while playing. Per-cue tables show the scatter follows **commit
   order**, which varies run to run — whichever voices commit first lead the rest.
   **Root cause**: `FireCuesIndependentAsync`'s barrier holds all voices until every clip is
   *armed*, but the **commits run serially on the session dispatcher and each commit both does the
   slow start work and starts the transport**. A video commit includes
   `TryPresentBufferedFrameForSync` (up to 250 ms) and buffered-frame waits, so every voice queued
   behind it starts late by that much. Headless (instant presents) the stagger is ~20 ms; with
   windows it is hundreds of ms, in random per-run order.
   **Fix required (not yet implemented — needs a design decision)**: two-phase start. Phase A per
   voice on the dispatcher: commit/attach/prefill/present-sync WITHOUT starting clocks; second
   barrier; phase B: start every voice's router+clock in one tight pass. Trade-off to decide: a
   group GO would wait for its slowest sibling's full prep before *anything* sounds (~0.5–1.5 s on
   this show) in exchange for sample-tight starts. `IArmedClip.Start` / `AvPlaybackCoordinator.Play`
   / `ShowSession.CommitClipAsync` all participate.
2. **Silent-video voices anchored early** (fixed): a genlocked video-only voice's `MediaClock`
   anchors to the already-advancing show clock at `Start()`, while a sounding voice's producer
   clock holds at zero until its first sample is audible — one audio-pipeline depth (~200 ms)
   later. The picture therefore led the programme by that depth, rate-locked. Fixed:
   `AudibleClientClock.CurrentPipelineLead` + `MediaClock.DeferStart` + the coordinator defers the
   silent voice's start by the measured depth, capped at 300 ms (the go-storm transient reads high).
   Fully effective once commits start on one edge (item 1); already correct for single mid-show
   video fires.
3. **Frame-rate dips**: composition holds 60 fps average but accumulates ~1 pump overrun/second
   with windows (occasional slot-overflow frames). Visible as a once-a-second hiccup on 60 fps
   content. Not diagnosed further yet; candidates: occasional >16.7 ms 10/12-bit layer upload or
   conversion. Follow-up item.

## Round 4 — two-phase synchronized group start (implemented)

The serial-commit stagger from Round 3 is fixed structurally:

- `AvPlaybackCoordinator.Play` is split into `PreparePlay(...) → Action`: the slow half (prefill,
  hardware, decode spin-up, video buffer wait, sync present) runs first, and the returned action is
  the start edge (router + clock start). `Play` = `PreparePlay()()`, so single fires are unchanged.
  Threaded through `IAvPlaybackSession`/`MediaPlaybackSession`, `MediaPlayer.PreparePlay`, and
  `IArmedClip.PrepareStart` (default interface implementation keeps hosts that never split working;
  a prepared clip is marked started so an aborted commit disposes it rather than returning a
  half-started player to warm standby).
- `ShowSession.PlayClipAsync`/`CommitClipAsync` gained a second barrier (`waitForStartEdge`): group
  voices commit + fully prepare behind barrier one, then start their clocks together behind barrier
  two. `CueRunner.FireCuesIndependentAsync` owns both barriers with the same
  cannot-strand-the-batch signaling as before. The silent-video deferral now measures the pipeline
  depth at the start edge, not at prepare.

Measured effect (windowed, full group, three repeat runs): stem scatter dropped from 340–560 ms
(per-run-random) to 0–180 ms, quantized in ~80 ms steps; one run hit 5 ms across 9 stems. All
suites green (786/17/359/381/704).

### Remaining gap and the finishing design (next round)

The residual scatter has one cause: at the start edge every voice's audio still races down its own
decode → producer-ring → bus pipeline, and that race is scheduler-dependent (the ~80 ms grid is the
voice-router pump depth). The videos' deferral in turn reads a lead measurement whose value depends
on how full the OTHER voices' rings happen to be when its starter runs. Per-voice timing heuristics
cannot close this.

**Design — pre-roll with a bus hold**: give `ProgramBusProducer` a Hold/Release state. During
PREPARE each voice starts its router with its producer HELD: the ring pre-fills with the clip's
first samples but `MixInto` skips held producers (no reads, no underruns, clock frozen). At the
start edge, release every sibling's producer — they all join the SAME bus mix chunk, which is
sample-tight by construction, and the producer clocks anchor on that first read. The video
deferral's edge measurement then also becomes stable (rings are deterministically full).
Care points: `WaitForCapacity` must not time out/fault while its producer is held (the 5 s
starvation timeout would fire if a cold sibling delays the edge); release must wake the waiter; and
the composition's slot-overflow growth seen in some runs (0 → ~150 per 11 s, run-dependent) should
be re-measured after pre-roll, since it correlates with the video-vs-programme offset.

## Round 5 — grant cap + bus-hold pre-roll (implemented)

Reported after round 4: dropouts every second (diagnostics: 960–27,840 underrun floats per lease,
zero overflow, terminal clean, 262 ms lease latency) and audio/video offset.

1. **Micro-underruns — outstanding-grant cap.** Granted-but-unsubmitted audio counts against the
   half-ring pacing target, so every chunk in the voice pump's queue was a chunk the ring was
   allowed to be short: at full pump depth the ring's share fell below one mix chunk and bus reads
   substituted a few ms of silence whenever drainer scheduling lagged (≈1/s under app load; never
   in idle probes). Outstanding grants are now capped at two chunks (`TryTakeGrant`), grants are
   retired AFTER the ring write and retire wakes the waiter (`Submit`/`ReleaseGrant`) — the ring
   keeps a floor of target−2 chunks (~65 ms minimum) with unchanged throughput.
2. **Bus-hold pre-roll** (`IPreRollableOutput` on `ProgramBusProducer`): during PREPARE each group
   voice's router runs with its producer held — the ring pre-fills with the clip's first samples,
   `MixInto` skips it (no reads, no underrun counts), and `WaitForCapacity` never concludes
   starvation while held. The start edge releases every sibling together; they join the same (or
   adjacent) mix chunk. `EndPreRoll` re-anchors the producer clock (it is time-based and advanced
   through the hold; without the re-anchor every position would lead its audible content by the
   hold length). Wired in `AvPlaybackCoordinator.PreparePlay` via the router's pacing primary;
   single fires take the same path with an immediate release (≤1 chunk of hold, harmless).
   `MaxStartDeferral` raised to 500 ms (master pacing keeps the device ring full, so a legitimate
   pipeline depth is ~345 ms).

Measured (windowed, full group, three runs): stems within 2–4 ms of each other, run-stable;
both videos within ~20 ms of each other at a consistent ~110–175 ms behind the stem clocks; slot
overflows 0 (previously grew run-dependently); pump overruns startup-only. Note the stem positions
carry a systematic ~85 ms late-reading bias (full pre-rolled ring in the lead term), so the
physical A/V offset is likely ~half the clock-readout gap. If video reads late by eye, the next
knob is the silent-voice deferral (subtracting the deepest-ring component moves video ~85 ms
earlier); a user-facing A/V offset trim is the standard endgame for projector rigs.

All suites green: Core 789, Session 359, Players 16, HaCue2 381, HaCue2.Core 704.

## Suggested work order

1. Framework: fix the grant leak + make capacity-wait timeout non-fatal, with a regression test (§1).
2. Project/app: set a clock master in the test project; default + surface it in the app; log
   `ProjectPatchBay` failures (§2).
3. App: stable active-list ordering by `StartedTicks` (§4a).
4. App: sounding-at-fire-start + immediate poll for the active list (§4b).
5. App: inspector tab memory for multi-edit (§5); patch matrix header width (§6).
6. Re-test the full group GO in the app; if UI/video sluggishness remains, profile the running app
   (start with `CuesViewModel.Tick` collection churn, §3).
