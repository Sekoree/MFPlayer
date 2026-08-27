# Full-repo review — 2026-08-25

Scope: everything, per owner request, with three named concerns: (1) clocks feel weird from the
UI perspective, (2) UI latency (input and output) should come down without compromises,
(3) audio and video outputs are designed as "two different worlds" where NDI wants one
audio-or-audio+video feed. Five parallel subsystem reviews + live testing on this box
(NDI source `SEKO-S1MAX (OBS PGM)` running).

## What was tested and verified healthy

- **Build**: full `MFPlayer.NoAndroid.slnf` builds clean.
- **All test suites green** (first time HaPlay.Tests is fully green — the 27 historical
  failures are gone): HaCue2.Core 810 · HaCue2 488 · HaPlay 1034 · HaRemote 9 · HaViz.Core 22 ·
  S.Media.Core 844 · Session 425 · Players 33 · NDI 40 · Audio.Backends 37 · Compositor 71 ·
  Decode.FFmpeg 53 · Arch all green.
- **Live NDI receive** (`LiveReceiveProbe` vs OBS PGM): 1080p60 held at 60.1 fps over 20 s,
  schedule lead bounded (mean 11.4 ms, range −9.8…14.4 ms), 1 re-anchor, audio 48 k/2ch.
- **NDI A/V correlation** (`NDIAVCorrelationProbe`): the shared-receiver fix is live — one
  `ndi://` open yields ONE receiver for A+V (independent opens anchored ~40–53 ms apart).
- **NDI send** (`NDILoopbackSmoke`): send → discover → receive round trip OK, 482 frames @1080p.
- **`hacue2-check`**: both real projects (`ManyItemTest`, `TestCue`) validate clean.
- **2026-08-14 judder fix chain confirmed complete** (NDI-11): first-frame wait before clock
  construction, start-edge re-anchor, VideoPlayer latch-heal + standing-latency trim, all pinned
  by `VideoPlayerLiveLatchHealTests`.

Tooling nit found while testing: `NDIAVCorrelationProbe` has a race (checks `TryGetVideoFormat`
right after `IngestClock.IsAdvancing` instead of `WaitForStreams`) and its FAIL message prints
the registry condition even when the direct-combined check is what failed. One rerun passed.

---

## Priority shortlist (owner-facing synthesis)

1. **C4** — GO→audible is ~200–350 ms, ≥80 % of it a fixed ~170 ms device-ring target +
   PortAudio high-latency FIFO that HaCue2 neither tunes nor exposes. Single biggest latency
   lever; HaPlay already ships proven ~65 ms sizing.
2. **C1** — Active-panel clocks extrapolate from a timestamp taken *after* variable
   cross-dispatcher awaits → visible ms-digit jitter and lag spikes exactly during GO. This is
   the concrete mechanism behind "clocks feel weird".
3. **NDI-01** — the linked A/V carrier's "shared egress timeline" never applies to the audio
   half; A and V timecode bases are unrelated and drift apart across edits.
4. **B1 + C2ᵖ** — a new "video + audio" NDI output plays silence (line created unpatched,
   needs matrix + audio restart in another tab) while the UI shows green "open".
5. **NDI-02 / B4 / F6** — duplicate NDI sender names are unvalidated → two unsynchronized
   writers on one native sender (torn frames / native use-after-free potential).
6. **F3** — wrap-crossfade fire-failure restore can clobber a consumed natural-end marker and
   stall a playlist group at its last item.
7. **NDI-04** — every `ndi://` open serializes behind one global lock while blocking on the
   network (up to ~8 s) — a GO-latency hazard when firing NDI cues.
8. **S-01** — STJ source-gen init-prop gotcha never migrated across HaPlay's serialized models
   (~130 risky initializers; older/hand-edited files → null collections, Guid.Empty ids).

---

## 1 · Clocks & UI latency (concern #1 and #2)

How it works today (verified): engine→UI is polled — `EngineRuntime` polls
`ShowHost.SnapshotAsync()` at 250 ms with an immediate re-poll on `SoundingChanged`;
`ActivePanelTicker` (50 ms DispatcherTimer) extrapolates each row's clock from its poll stamp.
`ShowHost.SnapshotAsync` subtracts `AudibleLatency` so Active-panel readouts are speaker/glass
truth, and the remote API uses the same snapshot — the *domain* design is genuinely good.
The defects are in stamping, cadence, and invalidation:

- **C1 (major)** — Poll stamp is taken on the UI thread after per-cue-list dispatcher
  round-trips (`ShowSession.cs:244-245`, loop `ShowHost.Transport.cs:634-643`, stamp
  `CuePresentation.cs:170`). Extrapolated clock = position(T0) + (now − Tstamp); the δ varies
  poll-to-poll (ms digits step back/forth at each 250 ms correction) and grows to whole
  fire/reload durations when the session dispatcher is busy — i.e. during GO.
  **Fix**: take `Stopwatch.GetTimestamp()` inside `ShowSession.Snapshot()` and carry it through
  `TransportSnapshot` → row; collapse the N standby queries into one `InvokeAsync` or a
  published lock-free view (an 8-list show currently pays 32 dispatcher round-trips/s).
- **C2 (major)** — Pause is fire-and-forget against the stale polled flag
  (`CuesViewModel.cs:835`): double-press within ~250 ms re-sends "pause"; clocks creep ahead
  then snap back on every pause. **Fix**: await + immediate poll signal; optionally flip
  `IsPaused` optimistically for the ticker.
- **C3 (major)** — The timeline sheet's playhead never follows a running group
  (`_playheadMs` only written by operator clicks, `CuesViewModel.cs:3191-3245`). Add an opt-in
  follow mode fed from the group snapshot (audible domain), suspended while dragging.
- **C4 (major, the latency lever)** — `ProjectPatchBay.cs:146` opens device lines with no
  options → `PortAudioOutput` defaults: `TargetQueueSamples = 8192` (~170 ms @48 k) + native
  `defaultHighOutputLatency` FIFO (~20–100 ms). The pump keeps the ring full, so a fired cue's
  first sample lands behind all of it; genlocked video GO waits on the same depth
  (`VoiceStartPolicy.cs:201`), and bay gain changes (fader→heard) ride the same queue.
  HaPlay's `PortAudioLiveMonitoring` already proves `max(rate/15, 3 chunks)` ≈ 65 ms @48 k
  dropout-free on this box.
  **Fix**: per-project (or per-line) output-latency setting in Settings → Audio —
  Safe (current ~170 ms) / Balanced (~100 ms) / Low (~65 ms) — mapped to `AudioBackendOptions`;
  do not silently change the default.

  GO→audible budget (warm cue, 48 k device line, from code evidence):

  | Stage | Cost |
  |---|---|
  | KeyDown → GoAsync (no debounce) | <1 ms |
  | Standby read + cursor advance (2 dispatcher turns) | 1–5 ms |
  | Arm (warm via pre-roll) | ~0 |
  | Stage commit + prepare | ms … tens of ms (video ≤ ~250 ms) |
  | Next 10 ms mix chunk (`ChunkSamples=480`) | 0–10 ms |
  | **Device ring target queue (8192 fr)** | **~170 ms** |
  | **PortAudio native FIFO (high latency hint)** | **~20–100 ms** |
  | **Total** | **~200–350 ms** |

- **C5 (minor)** — Seeks and loop wraps don't raise the immediate-poll signal → stale clock up
  to 250 ms then snap; loop clocks overshoot the wrap. Raise it from `SeekCueAsync`/
  `SeekCuesAsync`/`SetPausedAsync`.
- **C6 (minor)** — Meters rebuilt only in the 250 ms poll (4 Hz, steppy, misses transients);
  PPM/VU is a value pick, no ballistics. Bay reads are lock-free → a dedicated 20–30 Hz
  meter-only timer is cheap; add exponential decay in `ProgramMeterPresenter`.
- **C7 (minor)** — Wall-clock/timecode trigger pass runs on a 250 ms timer
  (`TriggerClocks.cs:31,64`) → externally scheduled cues fire up to 250 ms late. Shorten to
  ~50 ms or schedule the next known edge precisely.
- **C8 (minor)** — `ShowHost.SnapshotAsync` does linear `FindCue` per sounding cue inside
  `_gate` (`ShowHost.Transport.cs:610`), 4×/s; use the one-pass dictionary pattern already used
  elsewhere.

Preserve while fixing: immediate poll on fire, in-place row reconciliation, one audible domain
across Active panel + remote API, off-dispatcher open/prepare, per-voice crossfade playheads.

## 2 · Audio/video "two worlds" (concern #3)

Verdict: the instinct is right and locatable. The engine layer (`NdiSenderHub` + leases)
correctly models one NDI feed as one carrier, and the NDI *input* cue dialog is the good
counter-example (one cue, A/V toggles, latency probe, genlock, feedback-loop guard). The
document model + configuration UI keep one A/V feed as two records in two tabs, joined at
runtime by a *name string* while the UI joins them by *Guid links*, with `NdiCarriesAudio` as a
third source of truth — and nothing validates their coherence.

- **B1 (critical UX)** — `AddVideoOutput` "video + audio" creates the audio line with zero
  patch cells (`Dialogs.cs:1012-1030`); `ProjectPatchBay.Open` skips unpatched lines; opening
  needs an audio restart. Three undirected steps in another tab; meanwhile the Video row reads
  "NDI · video+audio" + green live. Seed default cells (Main L/R) or post-add "patch & restart
  now?" prompt; surface `NeedsAudioRestart` outside the Audio tab.
- **B2 (major)** — "Apply & restart audio" restarts the whole engine
  (`ShellViewModel.cs:503-515`): projector windows close/reopen, NDI video legs drop. Add a
  bay-only rebuild seam in ShowHost, or at minimum honest button copy.
- **B3 (major)** — No `EditAudioLine` dialog exists. An audio-only NDI feed (the common NDI
  case) can't change channel count/sender name/rate after creation; workaround is
  delete + recreate + re-patch.
- **B4 (major)** — No adopt/link path; carries-audio always creates a fresh line
  (`Dialogs.cs:1156-1172`); name collisions silently join existing senders (see NDI-02).
- **B5 (moderate)** — Rename asymmetry: video-side edit renames twin + hint; audio-side rename
  renames only the line — tabs disagree and the on-wire name doesn't change.
- **B6 (moderate)** — Downgrade asymmetry: video tab can drop the audio half cleanly; audio tab
  only offers `RemoveAudioLine`, which cascades to deleting the whole video output.
- **B7 (moderate)** — Renaming a live carrier splits it on the network: video leg reopens under
  the new name within 300 ms, audio keeps the old sender until engine restart.
- **C1ᵖ (bug)** — `VideoAndTargetPresentation.cs:403` hardcodes "NDI · video+audio" for every
  NDI output regardless of `NdiCarriesAudio`.
- **C2ᵖ (bug)** — `AudioPresentation.cs:245-263`: a line with zero patch cells reads green
  "open" while closed and silent — the exact trap the third state was added to fix.
- **C3ᵖ (nit)** — Neither row cross-references its twin (no A/V link column/chip).

**Target model (sketch)**: one explicit `CarrierName` (on-wire identity) stored once; one
carrier editor reachable from both tabs with Carries: audio / video / audio+video (so audio-only
feeds exist first-class and can grow a video half); validator rules (carrier-name collisions,
dangling links, `NdiAudioChannels` ≠ line channels, `NdiCarriesAudio` ≠ link present); bay-only
restart; Kind label derived from actual carriage + link chips on both rows. Migration is
contained to `Dialogs`, the two presentations, and a load-time Guid-link → CarrierName mapping.

## 3 · NDI subsystem (engine level)

- **NDI-01 (major)** — Linked carrier's shared egress timeline never applies to audio: the bay
  pumps `float[]` → `NDIAudioOutput.Submit(span)` which stamps timecode from a private sample
  counter; only the unused `Submit(in AudioFrame)` overload consults
  `NDIEgressPresentationTimeline`. Video re-anchors on every edit re-sync
  (`NDIVideoSender.cs:191` `Reset()`), audio counts from bay start and resets on APPLY — bases
  unrelated, drifting across edits. Fix: bay NDI terminal derives PTS from the bus clock and
  uses the frame overload (or adapter); stop resetting the shared timeline while audio is live;
  or drop both halves to `TimecodeSynthesize` and delete the unmet claim.
- **NDI-02 (major)** — Duplicate names: hub hands unlimited leases per name; two video outputs
  → two threads on one `NDIVideoSender` whose ping-pong staging is unsynchronized
  (`NDIVideoSender.cs:74-98,525-535`) → torn frames / `NativeMemory.Free` on an SDK-in-flight
  buffer; two audio lines → shared `_packedBuffer` + `Realloc` race (`NDIAudioOutput.cs:132-144`).
  Fix: role-aware acquisition (one video-half + one audio-half claim per name) + validator rule.
- **NDI-04 (moderate)** — `SharedNDISourceCache.AddEntryLocked` runs the open callback under the
  global `_gate`; the callback blocks up to 5 s find + 3 s stream-wait → all NDI opens (any
  name) serialize and stall behind one absent source. Keyed pending-entry / per-name semaphore.
- **NDI-05 (minor)** — Video/audio open pairing via managed-thread-id marker (2 s TTL) can
  mis-pair unrelated opens on a reused pool thread; pair via explicit token in `OpenAsync`.
- **NDI-03 (minor, latent)** — `NDIRecvListener/NDISendListener.GetEvents` free the native
  events before returning structs holding their string pointers (use-after-free on read); no
  production callers today. Marshal before free like `GetReceivers`.
- **NDI-06 (minor)** — standalone receivers log faults only `#if DEBUG`.
- **NDI-07 (minor)** — all received video hardcoded full-range BT.709
  (`NDIVideoFrameUnpack.cs:22-26`); limited-range hardware senders wash out; offer `range=` URI
  override or honor metadata.
- **NDI-08 (minor)** — UYVA alpha silently discarded (keyed graphics composite opaque);
  one-time log.
- **NDI-09 (cleanup)** — `NDIVideoReceiver`/`NDIAudioReceiver` are dead standalone paths with a
  `Bandwidth = Lowest` trap default; delete or align before revival.
- **NDI-10 (minor)** — `Configure` refuses odd-height P216/Pa16 needlessly (4:2:2 needs even
  width only).
- **NDI-12 (minor)** — wire-format fallback silent: UYVY choice silently unconverted when
  `CanConvert` fails; "Bgra" branch never forces conversion.

## 4 · Core playback / engine correctness

Commit 97141b6a's mechanisms held up (live first-frame wait failure paths, pause-stops-heal,
heal/trim thresholds vs queue cap 4, AddLayer foreign-domain fix intact, snapshot contract).

- **F3 (race)** — `TryCrossfadeWrapAsync` (`CueExecutor.cs:1087-1105`, also 1073) publishes the
  next pass then awaits `FireAsync` outside `_stateGate`; if the closer's natural end consumes
  the `CrossfadedFrom` marker meanwhile and the fire fails, the restore writes the old run back
  with the edge already spent — no advance, no end policy, group hangs at the last item.
  Fix: CAS-style restore (only if the stored run is still the published one) + route the
  consumed-marker case through `FinishPlaylistAsync`.
- **F4 (edge)** — one-item playlist with crossfade ≥ item duration degrades to butt-cuts on
  alternating passes (pre-end edge swallowed while the marker is unconsumed). Clamp effective
  wrap crossfade below item duration.
- **F2 (minor)** — live layer `TimeSelectionOffset = −VideoPlayheadOffset − 2×SourceFramePeriod`
  uncapped → a 1 fps live source gets a 2 s standing latency (file-side lead is capped 20/40 ms
  for exactly this). Cap ~100 ms.
- **F5 (minor)** — patch-bay open log claims `clock master 'NAME'` when the named master is an
  NDI line or failed to open — no terminal took the role AND the automatic election is skipped;
  bay free-runs (the pop-every-2 s mode) while the log says otherwise. Track whether the master
  actually took the role; run the election as fallback.
- **F1 (latent)** — `NdiFeedVideoOutput` doesn't forward `IVideoOutputCooperativeAbort` from the
  inner `NDIVideoSender` — the wrapper-must-forward gotcha class; moot today
  (`disposeInner:false`) but a future owning pump silently loses prompt teardown.
- **F6** — validator gap for NDI names / link coherence (same as B4/NDI-02).

## 5 · Satellite apps & support layers

Layer is in unusually good shape; layering rules hold; no HaCue-v1 remnants.

- **S-01 (major, latent)** — STJ source-gen init-prop gotcha unmigrated across HaPlay's
  serialized models: `ControlGraphConfig` (39), `CueList.cs` (64, incl. base
  `CueNode.Id init = Guid.NewGuid()`), `PlaylistItem` (27), `MediaPlayerConfig` (19),
  `Soundboard` (11), `HaPlayProject` (9), `OutputDefinitions`, `ActionEndpoint`,
  `RecoverySessionInfo`. ~130 non-CLR-default initializers: absent JSON field → null
  collections (NRE), `Guid.Empty` id collisions, zeroed scales/ports/`InstructionLimit`.
  Current-app round-trips mask it; old/hand-edited/external files trigger it. Mechanical
  migration to `set`, mirroring the HaCue2 rule (`AppSettings.cs:35-39` documents it).
- **S-02 (minor)** — `OutboundRampRunner.Freeze` retry path re-emits authored `FinalValue`
  instead of the frozen value after a failed freeze send
  (`OutboundRampRunner.cs:188-196,205-213`).
- **S-03 (minor)** — `LineInCapture` shared `_stopRequested` revives a timed-out old pump on
  restart → two pumps feed the sink (`LineInCapture.cs:60,100-107`). Per-generation stop flag.
- **S-04 (minor)** — `VizNdiEngine.Dispose` disposes the CTS after a timed-out join → wedged
  pump throws `ObjectDisposedException` → spurious `Faulted` on a disposed engine
  (`VizNdiEngine.cs:354-361`). Leak the CTS on timeout (matches native-leak policy).
- **S-05 (nit)** — HaViz "output is BLACK" shown before first frame (`LastFrameLuma <= 0`
  includes the −1 sentinel, `MainViewModel.cs:249`).
- **S-06 (nit)** — `DownmixToStereo` doc says averaging, code takes ch 0/1 verbatim
  (`VizNdiEngine.cs:217-235`).
- **Observation** — HaCue2's `RemoteApiServer` covers transport verbs only; no remote surface
  for output health / standby-state / cue-name lookup.

## Suggested sequencing

1. **Latency + clocks sprint**: C4 (latency presets), C1 (stamp placement + standby batching),
   C2/C5 (immediate-poll signals), C6 (meter timer). All local, low-risk, directly answer the
   named concerns.
2. **Carrier unification sprint**: NDI-01 (audio PTS via bus clock; stop mid-live resets),
   NDI-02 + validator rules (F6/B4), then the UI: carrier editor (B1/B3/B4), presentation fixes
   (C1ᵖ/C2ᵖ/C3ᵖ), rename symmetry (B5/B7), bay-only restart (B2).
3. **Correctness batch**: F3, F4, F5, F2, NDI-04, NDI-05, S-02..S-04.
4. **Hygiene batch**: S-01 migration, NDI-03/06..10/12, F1, S-05/S-06, probe race fix,
   C7/C8, remote API surface.
