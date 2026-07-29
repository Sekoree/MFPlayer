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
