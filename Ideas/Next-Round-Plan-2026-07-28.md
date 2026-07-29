# Next round plan — remaining items (2026-07-28)

Decisions taken with the owner on 2026-07-28. Everything earlier in the Ideas docs is
implemented; this doc plans the leftover pool. Workstreams A–F below are IN SCOPE; the
"parked" list at the end is the explicit out-of-scope record.

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
