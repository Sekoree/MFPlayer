# Next round plan — remaining items (2026-07-28)

Decisions taken with the owner on 2026-07-28. Everything earlier in the Ideas docs is
implemented; this doc plans the leftover pool. Workstreams A–F below are IN SCOPE; the
"parked" list at the end is the explicit out-of-scope record.

## Status (2026-07-29)

| Item | State |
|---|---|
| **B** always-on MIDI/OSC input | Done (`ControlInputSession`) — review found lifetime/signature defects, fixed in the follow-up round |
| **C1** SharedAudioOutput DAC-lead compensation | Done — review found the estimator collapsed to the *minimum* observed lead; reworked in the follow-up round |
| **C2** HaViz NDI audio submit ring | Done (`NdiAudioSubmitRing`) — review found the ring stayed pinned full after one overflow; fixed |
| **D1** MTC chase → scheduler | Done — pure `MidiTimecodeDecoder` + `MidiTimecodeChaseClock` in S.Control (quarter-frame assembly, full-frame locates, wall-time interpolation, stall freeze, jump/relocate generations); `CueTimecodeChaseService` decodes on the MIDI I/O thread off B's `ControlMonitorRecord` seam (never a dispatcher post per quarter-frame) and stays OFF until some loaded list carries a `CueScheduleKind.Timecode` schedule; `CueSchedulerService` fires on chase crossings with the wall path's exact semantics. LTC stays parked |
| **D2** NDI ingest clock | Done — opt-in via `NDISourceOptions.PaceRouterFromIngestClock` / `ndi://…?ingestClock=1` + dialog checkbox; default off (a slaved router produces *nothing* while ingest stalls — that is genlock, hence opt-in) |
| **D3** VideoPtsClock | Done — opt-in via `MediaPlayerOpenOptions.MasterVideoOnlyClockFromPts`, default **off** deliberately: pinning the clock to an early-presented PTS shortens the wall interval to the next frame and compounds on decode-ahead file sources (~1.32× speed at 30 fps, ~1.9× at 60 fps). Recommended for live/ingest-paced video-only sources, which cannot run away |
| **E** soundboard quantized launch | Done — see the premise correction below |
| **F1** fire blip at lowered trim | Done |
| **F2** HaViz line-in device persistence | Done |
| **F3** NullClockedAudioOutput consumer | Done — HaViz desktop attaches it as the router's pacing primary + MediaClock master (sample-accurate, drift-free, no hardware). Attached whether or not local monitoring is on, so toggling the monitor never changes the show-critical feed's pacing source |
| **A** cross-list merged session | Done — `HaPlayShowMapper.ToShowDocument(lists)` concatenates every loaded list into the one `ShowSession` document with list-scoped runtime transport groups; schedules/triggers/remote fire any list through a headless per-list run that never moves the visible GO/standby transport. Standby/pre-roll stays selected-list-only; contested output lines dedupe selected-list-first through `ReconcileCueOutputTopologyAsync` |

**E premise correction:** the plan assumed HaPlay's soundboard runs on the framework
`SoundboardGrid`. It does not — HaPlay has its own `SoundboardWorkspaceViewModel` tile grid and
fires through `VoicePlayer`, so "dispatch `SoundboardGrid.TryCreateScheduledFire`" was not
implementable as written. Instead the boundary math moved into a shared, pure
`SoundboardQuantization` (`NextBoundary` / `BeatsToQuantum`) that BOTH the framework primitive
and HaPlay now call, and the feature landed in the workspace VM: board-global BPM +
`LaunchQuantizeBeats` (Ableton-style, one setting per board rather than per tile), tap arms and
the workspace's single pump fires on the next boundary, a second tap disarms, Stop-all drops
armed launches, and stopping a *playing* tile stays immediate (quantization is a launch grid).
Boundaries are multiples of the quantum from the workspace's own transport origin — there is no
external tempo sync (Ableton Link / MTC chase remain parked / D1).

## A. Cross-list scheduling — ONE MERGED SESSION (decision)

Today `CueShowSessionCoordinator` maps only the SELECTED cue list into the single
`ShowSession`; schedules/triggers/remote warn about other lists. Decision: map **all loaded
cue lists into the one session document** with list-scoped identity, so any cue in any list
is fireable by schedules, triggers, and the remote API — while the visible transport
(GO/standby/Now-Playing tree) still follows the selected list only.

Design points:
- Cue/group/composition ids are already GUIDs (globally unique across lists) — the mapper
  (`HaPlayShowMapper`) gains a multi-list entry that concatenates per-list documents; runtime
  transport-group ids get a list-id prefix so two lists can't collide on authored group names.
- The coordinator's rebuild path (`ReloadCueGraph` debounce) rebuilds on edits to ANY list,
  not just the selected one; standby/pre-roll windows remain selected-list-only (pre-rolling
  every list would eat decoders).
- `CueSchedulerService`/`CueTriggerService` enumerate armed schedules/triggers across ALL
  lists; the fire path resolves the owning list's VM context (fire-by-id already exists —
  `FireTriggeredCueSafeAsync`; it must look up the cue across lists and use that list's
  runtime state e.g. playlist runs, without touching the selected-list UI transport).
- Now-Playing: cues fired from a non-selected list appear in the Now-Playing panel with a
  list-name prefix (they play in the same session; hiding them would be worse).
- Remove the "other lists have schedules" arm warning; replace with a count of armed items
  across lists in the tooltip.
- Risk: per-list output-line leases — video output bindings from two lists to the same line
  must dedupe through the existing lease reconciliation (`ReconcileCueOutputTopologyAsync`).

## B. Always-on MIDI/OSC trigger input (decision: do the S.Control change)

Today cue triggers tap the Control workspace's monitor sink, so MIDI/OSC triggers flow only
while Control is ARMED. Decision: move input ownership so device input runs whenever devices
are configured, independent of the mapping engine's arm state.

- S.Control: expose a public input event at the `ControlEventQueue`/device-session layer
  (the seam the trigger-round agent identified as cleanest); input sessions get their own
  lifetime (created when devices are configured/enabled) with the mapping engine subscribing
  on arm rather than owning the devices.
- HaPlay: `CueTriggerService` subscribes to the new event; the monitor-sink tap remains only
  as a fallback path until removal. The "Control must be armed" tooltip text goes away.
- Guard: device open failures surface as toasts (existing pattern); disarming Control must
  NOT close devices that trigger bindings still need — ref-count the two consumers.

## C. Latency pass

1. **SharedAudioOutput client clock DAC-lead compensation** (~100 ms constant): subtract the
  client-bus + pump + terminal-ring latency from `ClientInput.ElapsedSinceStart`, mirroring
  `PortAudioOutput`'s negotiated-latency subtraction. Sources: terminal output's own
  reported latency (PortAudio has it; miniaudio now has a period estimate) + measured ring
  depths. Keep it monotonic (ease-in like the PortAudio quadratic start fix; never step
  backwards on recomputation). Test with the fake-terminal harness from the client-clock round.
2. **HaViz NDI audio submit ring** (P3 leftover): `VizNdiEngine.SubmitPcm` can block a
  decoder thread on the SDK audio clock — move NDI audio submission behind a small SPSC ring
  + dedicated sender thread (drop-oldest on overflow, stats counter).

## D. Timecode & sync wiring

1. **MTC chase → scheduler** (depends on B's input seam): decode MIDI Time Code
  quarter-frames from the always-on MIDI input into a chase clock; `CueSchedule` gains
  `Kind.Timecode` with an `hh:mm:ss:ff` target + frame-rate; `CueSchedulerService` fires on
  chase-clock crossing (same grace/once semantics). LTC (audio timecode) stays parked — it
  needs an audio-input decode path; the enum value exists for later.

  **D1 implementation notes.** The B seam carried MTC as-is, so no new framework event was
  needed: `ControlMIDIMessagePayload.FromMIDIMessage` already maps a quarter-frame to
  `ControlMIDIMessageType.MIDITimeCode` with the DATA byte in `MIDIValue`, and a full-frame
  locate to `SysEx` whose bytes ride `RawBytes`. One caveat: `RawBytes` is gated by
  `ControlMonitorOptions.IncludeRawBytes` (default on), so with raw bytes switched off in the
  monitor settings quarter-frame chase still works and only full-frame LOCATES go undetected.
  Decoding happens on the PortMIDI poll thread inside `MainViewModel.OnControlInputObserved`
  (before the trigger path's dispatcher post — 100 msg/s at 25 fps must never become 100
  dispatcher work items/s), and MTC records are swallowed there so they never reach the trigger
  sweep. The decoder anchors each assembled timecode on the timestamp of *piece 0* rather than
  adding the customary "+2 frames" fudge: the value is inherently 2 frames stale when it can
  first be read, and anchoring on the instant it actually describes lets the clock interpolate
  that lag away. Stall (100 ms of silence) freezes the clock instead of free-wheeling, and every
  discontinuity — first lock, relocate, full-frame locate, resume after a stall — opens a new
  chase *generation* whose start position becomes the scheduler's baseline, which is what makes
  "no burst after a locate" and "a rewind re-arms the pass" fall out for free.
2. **NDI ingest clock wiring**: `NDISource` playback opts into `SlaveToIngest(source.IngestClock)`
  (router pacing from sender timecode) behind an opt-in flag on the NDI input cue/deck options
  (default off — wallclock behavior unchanged). The monotonicity fix from the review round is
  already in.
3. **VideoPtsClock**: wire `videoOnlyMaster` for video-only playback in
  `MediaPlayer`/`ClipStandbyEngine` open paths (video-only clips master to PTS instead of wall
  time). Low risk; tests exist for the clock itself.

## E. Soundboard quantized launch (decision: wire it)

Dispatch `SoundboardGrid.TryCreateScheduledFire`: tiles with `Quantize` set fire on the next
quantum boundary instead of immediately. The soundboard VM computes `When` via the existing
primitive, holds the tile in a "pending" visual state (pulse), and fires through the existing
voice path at the boundary (reuse the scheduler-service timing discipline — one timer, not
per-tile). Tempo source: the grid's existing quantize/tempo fields; if none exists beyond the
primitive, add BPM + quantum to the soundboard settings UI.

## F. Polish

1. **Fire blip at lowered trim**: apply the master-trim/fade-in factor to route gains inside
  the commit BEFORE the first Submit (the documented ms-scale full-gain blip in
  `CommitClipAsync`).
2. **HaViz line-in input-device persistence**: persist the capture device name in HaViz
  desktop settings; restore on start, ignore if gone.
3. **NullClockedAudioOutput production consumer**: HaViz desktop uses it when local
  monitoring is off (replacing the discarding sink + wallclock combination with the
  sample-accurate null output as the router's clocked pacer), and/or a `--headless-render`
  smoke path. Keep behavior identical when monitoring is on.

## Order & dependencies

1. B (always-on input) and C (latency pass) first — independent of each other, B unblocks D1.
2. A (merged session) next — biggest; solo so nothing races the coordinator/mapper.
3. D (timecode & sync) after B; D2/D3 can ride alongside E.
4. E (soundboard quantize) and F (polish) — independent, anywhere after A for E (session
  stability), F anytime.

## Parked (explicit, with reasons)

- **LTC/SMPTE audio timecode decode** — needs an audio-input chase decoder; MTC covers the
  common rig first.
- **Genlock domains** (`OutputSyncGroup`/`VideoPresentSyncGroup`/`LiveTimelineDriver`) —
  dormant until multi-output frame-lock is a real requirement.
- **`CompositePlaybackClock`/`SetMasterChain`** — stays test/lab machinery.
- **Outgoing crossfade tail addressability** — by design (fire-and-forget tail).
- **Doc hygiene note**: CuePlayer-Enhancements' "Still open" line predates the continuation
  round — panic slider, loop-with-crossfade, duck presets, and the TimelineStartMs field are
  DONE; corrected alongside this plan.

## Cross-list + MTC follow-up (2026-07-29)

A second adversarial review of **A** (cross-list merged session) and **D1** (MTC chase) recorded
seven defects as too large / needing a semantics call for that pass. All are fixed; each carries a
regression test that fails on the unfixed code.

**A — cross-list merged session**

1. `EnsureCueShowSessionCurrentAsync` (the fire-path flush of pending edits) had no `HasActiveCues`
   guard, unlike the debounce tick and `WarmUpcomingForPreRollAsync`. Since the flush is a full
   `LoadDocumentAsync` over the ONE merged document, an edit in list C tore down list A's playing
   cue at the next fire — impossible pre-merge, where only the selected list could dirty the graph.
   Now deferred with the same shape: the graph stays dirty, the debounce is re-armed, and the fire
   runs against the loaded document.
2. A `_cueGroupByCueId` miss fell back to `ShowSession.DefaultGroup` ("main"), a group **no** cue is
   on in the merged document (every cue is list-scoped). The per-group end monitor then declared the
   cue ended after the ~3 s warmup grace while it was still audible. A miss is now an error: logged,
   surfaced on the status strip (`CueTransportGroupUnknownFormat`), no group substituted, and no
   end-monitor entry created — the row still appears so the operator can stop it. Seek/seek-many skip
   an unresolvable cue rather than addressing "main".
3. `AdvanceAutoFollowAfterInstantCueAsync` and `ResolveFadeCueTargetsOnUi` were selected-list-scoped:
   a foreign Action/Visualizer/Fade cue dropped its Auto-Follow chain, and a foreign Fade resolved no
   explicit targets. Both now resolve in the OWNING list (`EnumerateFireableCueOrderFor` /
   `EnumerateAllCueNodesFor`) and a foreign chain continues through `GoForeignListAsync`, mirroring
   the media natural-end path — never through `GoCore`, which would move the visible standby.
4. Foreign pre-waits gated on `IsTransportPaused` (the VISIBLE list's pause), so pausing the list the
   operator happened to be looking at froze every other list's scheduled pre-rolls. **Semantics
   decided:** a pre-wait is a scheduling delay, not playback, and Pause is a visible-transport
   affordance (it needs `CurrentCueNode`). `RunTriggerPlanAsync`/`WaitUntilDelayAsync` now take the
   run's own pause probe; a headless cross-list run passes its own state (alive until its list fires
   again or Stop/Panic cancels it). Session-wide clip pause (`SetAllPausedAsync`) is unchanged.
5. A list rename never re-stamped live Now-Playing rows. `CuePlayerViewModel` now watches every
   loaded list's `Name` and re-runs `RefreshNowPlayingListNames`.

**D1 — MTC**

6. `MidiTimecodeDecoder` produced corrupt labels after a dropout of exactly a whole multiple of 8
   quarter-frames: the stream resumes on the piece index the assembler expects, splicing pieces 0..3
   (frame + second nibbles) of one window onto pieces 4..7 (minute + hour nibbles) of a later one.
   Inside a minute that is harmless — the low half already IS the label the window started on, and
   the update is stamped with that window's piece 0 — but across a minute/hour rollover the merged
   label is a full minute wrong, and it was reported as a relocate.
   **Rule chosen** (a time bound cannot separate an 8-message dropout from a poll thread starved
   mid-window, so the check is arithmetic, not temporal): because a splice keeps the low half of the
   window it started on, the corrupt label always matches the wall-time prediction's `ss:ff` exactly
   (±1 frame of arrival jitter) while its `hh:mm` sits somewhere ahead. An assembled label with that
   fingerprint is never reported and never moves the anchor; it is held and believed only if the NEXT
   independent assembly lands where it predicts — which a genuine relocate does within one timecode
   (~83 ms of added latency in the rare case a relocate trips the fingerprint) and a splice never
   does. The test is on the label FIELDS, so 29.97 drop-frame (a labelled minute is 1798 or 1800
   frames) needs no special case. Residual, accepted and documented: a splice in the very first
   assembly after a reset has no anchor to be inconsistent with; it self-corrects on the next
   assembly with a new generation, so it costs one 2-frame window rather than a persistent error.
7. `CueSchedulerService` chase-path mirror gaps:
   - (a) The pre-baseline retire used a strict `targetSeconds < _chaseBaselineSeconds`, so a target
     exactly ON the generation start stayed live and a locate landing exactly on a cue fired it. Now
     `<=`, mirroring the wall path's `due <= _armedBaselineWall`. (The review stated this one
     inverted — the strict `<` was on the chase side, the inclusive `<=` on the wall side; the
     mismatch itself reproduced.)
   - (b) `_handledTimecode` was cleared WHOLESALE on any generation change, weakening "fires exactly
     once" to "fires once per run" — and generations bump on every dropout. Since the chase position
     is a wall-time extrapolation leading the last decoded label by up to ~4 frames, a dropout right
     after a crossing re-baselined just BEHIND the target that had already fired and fired it again.
     It is now pruned like the wall path's `_handled`: a target re-arms only when the new baseline is
     further behind it than the chase's own re-anchor slack (4 frames), so a real rewind still
     replays the pass while a re-anchor cannot. The set also keys the target's seconds (targets from
     cues at different frame rates are not comparable by frame number) and is bounded by dropping
     entries whose cue no longer carries a timecode schedule.
   - (c) Chase lateness is measured in the chase's domain against a real-millisecond grace floor.
     **Kept, documented, with evidence**: the chase clock is wall-time interpolation anchored on
     labels, and the decoder classifies any label more than 2 frames off the wall prediction as a
     JUMP — which opens a new generation and re-baselines the sweep. A run therefore cannot
     accumulate more than ~2 frames of skew between the two domains, far inside the 500 ms floor.
     Pinned by `ChaseClock_SenderRunningFasterThanRealTime_OpensANewGenerationEveryAssembly`.

**Harness note (not caused by this round).** `HaPlay.Tests` carries a pre-existing intermittent
failure of `RemoteApiDispatcherTests.UnknownEndpoint_Returns404_AndBadMethod405`: the session's
per-test isolated `Application` setup throws *"The calling thread cannot access this object because a
different thread owns it"* out of `AvaloniaHeadlessPlatform.Initialize` → `DefaultRenderLoop.Add` →
`Dispatcher.VerifyAccess`, i.e. `Dispatcher.UIThread` got bound to a stray pool thread between two
session dispatches — the same class `HeadlessSessionBootstrap` documents and warms against. It is
timing/order driven, not content driven: adding **six no-op `[Fact]`s** to the *unmodified*
`CrossListSessionTests` reproduces it (1 failure in 5 runs) exactly as this round's real tests do
(~2 in 5), while the untouched set passed 6/6. Disabling the 5 s status-message auto-clear
continuation (one obvious source of stray `Dispatcher.UIThread` touches) does **not** fix it, so
there is more than one straggler. Left as-is: it needs a harness fix, not a product one.
