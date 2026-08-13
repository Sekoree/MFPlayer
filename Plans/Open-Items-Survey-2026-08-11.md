# What is actually still open - survey, 2026-08-11

Asked: *how many planned features in the planning docs are still open?*

Answer: **far fewer than the docs say.** The two documents that track open work
(`HaCue2-Framework-Gap-Analysis.md`, dated 2026-08-01 with edits to 08-03, and
`HaCue2-vs-HaPlay-CuePlayer-Parity.md`, dated 08-04) between them list **13 open subsystems and 24
missing parity items**. Checked against the tree today, **almost all of them have landed** - the docs
were never updated as the work went in.

Method: every item below was verified by grep against `UI/HaCue2*`, `.github/workflows/build.yml` and
`MFPlayer.sln`, not taken from the doc's own status column.

---

## 1. Gap analysis, "Still open, and what each is waiting for" (2026-08-03)

### The 7 "subsystems absent" - 7 of 7 now exist

| Item | Status today | Evidence |
|---|---|---|
| Preview / audition rig | **landed** | `ShowHost.Audition.cs`, `CuePreviewPlayer`, 20 files |
| External input (MIDI/OSC/hotkeys) | **landed** | `TriggerInputs.cs`, `TriggerBindingSet`, `TriggerMatching.cs` |
| …schedules | **landed** | `TriggerClocks.cs:155` walks `TriggerInputKind.Schedule` |
| …MTC chase | **landed** | `TimecodeChase.cs`, `MidiTimecodeChaseClock` |
| Remote API server | **landed** | `RemoteApiServer.cs`, `RemoteApiRoutes.cs` |
| Log ring → Diagnostics tail | **landed** | `LogRingProvider`, `LogTailTests` |
| Visualizer cues | **landed** | `VisualizerCueNode`, `ProjectVisualizers.cs`, 16 files |
| Video output kinds NDI / record / stream | **landed** | all four `VideoOutputKind` values handled in `ProjectVideoOutputs.cs:180-223` and `ProjectRecorders.cs:384` |
| Effect-lane editor UI | **landed** | `TimelineSheet.axaml`, `InspectorPane.axaml`, 12 files |

### The 6 "smaller, deferred" - 6 of 6 now exist

Idle images (`IdleFrames.cs`, `ShowHost.IdleFrames.cs` - so the still-image decoder that "blocked" it
was solved), test pattern + Identify (`IdentifyPattern.cs`), MIDI-out for action cues (`MidiOut.cs`),
the project override ledger (`SettingsViewModel`, covered by `OverrideLedgerTests`), media-cache
management (`HaCue2.Machine.MediaCache`, wired into settings and the YouTube runtime), and the hotkeys
grid.

### The 4 "build" items - 3 of 4 done

| Item | Status |
|---|---|
| HaCue2 AOT-publish gate | **landed** - `build.yml:300` |
| Fixture gating `hacue2-check` in CI | **landed** - `build.yml:312-337`, asserts the deliberate exit 1 |
| `HaCue2.Tests` in the workflow | **landed** - CI runs `dotnet test MFPlayer.sln`, and both HaCue2 test projects are in the solution |
| HaCue2 **launch smoke** | **still open** - the only launch gate in CI is HaPlay's |

---

## 2. Parity audit (2026-08-04) - the headline gap is closed

Its sharpest finding was "Video placement - **HaPlay well ahead**, 6 model fields HaCue2 cannot express
at all". All six now exist on `LayerPlacement` (`UI/HaCue2.Core/Model/Cues.cs`):

`CropLeft/Top/Right/Bottom` (:514) · `RotationDegrees` (:527) · `ChromaKey` + `ChromaKeyEnabled`
(:549) · `ColorAdjust` + `ColorAdjustEnabled` (:554) · per-layer warp mapping · and `LayerFit` (:667)
now carries all six fit modes (`Contain, Cover, Stretch, Center, FillWidth, FillHeight`), closing the
"3 fit modes missing" row.

The doc's other 18 "no" rows were not individually re-verified - given the hit rate above, **assume
that document is stale rather than authoritative** and re-audit before planning from it.

---

## 3. What is genuinely still open

Six things - but an owner decision on 2026-08-11 closes two of them outright.

### Owner decision (2026-08-11): HaPlay's cue player STAYS

> HaPlay's cue player is kept deliberately, as a simpler variant beside HaCue2. It is a product, not
> a migration leftover.

That is not a deferral, it resolves two items:

| # | Item | Resolution |
|---|---|---|
| 1 | **Phase 6 of the extraction** - 46 cue-named files still in `UI/HaPlay` | **CLOSED.** Phase 6 existed to dismantle HaPlay's cue player after extracting it. If that player is a shipped product, those files are the product and there is nothing to dismantle. |
| 2 | **`HaOutput` extraction** - the 8 couplings | **CLOSED.** The 2026-08-03 note deferred this at a stated cost: "a merge later **if both apps are to share one engine**". They are not. HaCue2.Engine's own 450-line `ProjectVideoOutputs` and HaPlay's `OutputManagement` stay separate on purpose. |

The one consequence worth writing down: the two apps' output paths are now permanently divergent by
choice, so an output-layer fix has two homes. That is acceptable only while HaPlay's player stays the
*simpler* one - the moment it starts chasing HaCue2's output features, revisit this.

### Still open (4)

| # | Item | Kind | Worth doing? |
|---|---|---|---|
| 3 | **HaCue2 launch smoke in CI** - the AOT gate publishes the binary but never starts it; HaPlay has a launch gate, the flagship does not | build | **yes, cheap** |
| 4 | **Multi-output genlock not wired** (`OutputSyncGroup`, `VideoPresentSyncGroup`, `SyncPresentVideoOutput`) | feature | **not yet** - speculative until a venue actually needs a stitched wall; `Doc/HaPlay-MultiOutput-Sync.md` itself concludes Option A suffices for every current case |
| 5 | **P7 live-ingest not wired** (`SourceTimeline`, `LiveTimelineDriver`, `SourceSyncGroup`) | feature | **not yet** - same shape; wire it when a real NDI A/V desync is observed, not before |
| 6 | **MiniAudio JACK GC hazard** | defect | **a warning, not a rewrite** - see below |

### On item 6, corrected

I first rated this higher than the evidence supports. Checking properly:

- miniaudio **is** a first-class user-selectable backend (`AppSettings.AudioBackend`), documented as
  "the answer when a box's PortAudio build is the thing that is broken" - so it is reachable by a real
  operator on a real machine, and reached exactly when they are already troubleshooting.
- **But** MALib builds no native code (`MALib.csproj`: "no native build step … hosts deploy the vanilla
  miniaudio shared library"), and vanilla miniaudio on Linux tries PulseAudio, then ALSA, then JACK. On
  a PipeWire box `pipewire-pulse` answers first, so the JACK backend - the only one with the hazard - is
  a last resort that is unlikely to be selected.

So: low probability, invisible failure mode. The proportionate fix is to **detect the JACK backend at
device open and warn** (~15 lines), turning a mysterious xrun into a message. The blocking-write rework
is disproportionate, and the C shim from §9c of the timing review is definitely not warranted without a
measurement showing the hazard actually firing.

### Not on the original list, but ahead of items 4–6 for anyone who cares about cue timing

`AudioPatchBay.TerminalClockProxy` takes the shared bay gate on **every clock read**, and its three
property members each take it separately (`AudioPatchBay.cs:766-785`). Every sounding voice's clock
chain runs through one of these. The throughput cost is probably small and is **unmeasured** - the
argument for fixing it is not speed but coupling: a patch edit, which takes the same gate, can briefly
block every voice's clock read. `MasterClockProxy` next to it is already lock-free over a volatile
field, and `ClipCompositionRuntime`'s `_acquiredSnapshot` is the immutable-snapshot pattern to copy.

Plus, from `Timing-Simplification-Review-2026-08-11.md` §8/§9d and never actioned:
`CpuVideoCompositor` clears 8.3 MB per frame unconditionally (CPU fallback path only); `MediaClock.Stop`
is still literally `Pause`.

### `CueRunner._fireLock` - examined 2026-08-11, deliberately NOT changed

The complaint is real: one process-global semaphore serialises fires across all cue lists, so a GO on
list A holds it through A's media open (hundreds of milliseconds for a cold file) while a GO on list B
waits. Scoping it per list would decouple them.

It is not landing with the other three items, for two reasons:

1. **`FireCuesAsync` legitimately spans authored groups** - its own doc says it fires each cue on that
   cue's `CueDefinition.GroupId`. Per-list scoping therefore needs ordered multi-acquire (sort the keys,
   take them in order) to stay deadlock-free. That is correct-able but it is not a small change.
2. **It changes when two things may happen at once, on the fire path.** The other three items in this
   round were contained and independently verifiable; this one alters the concurrency model of the most
   safety-critical path in a live show, and a subtle regression means two cues firing together that
   should not have, mid-performance.

Landing it in the same batch as a large body of timing work that has only ever been verified against
test suites is the wrong sequencing. Do it as its own change, with its own validation, after the
real-show run.

Note also that the worst case is already smaller than it looks: `FireCuesIndependentScheduledAsync`
releases the lock as soon as its batch is prepared, so timeline events do not hold it while waiting for
their edge.

### Two numbers that need re-taking rather than trusting

The gap analysis's "21 inert controls, 56 hardcoded fields" (as of 08-03), and the parity doc's
remaining 18 "no" rows.

---

## 4. The real finding

The planning docs are **not a reliable open-items list**. Two of them assert 37 open items between
them; the tree says roughly 6. That is not a documentation nit - it is the reason this question needed
a code survey to answer, and anyone planning the next phase from those documents would be planning work
that is already done.

Recommendation: mark both documents **superseded by this survey** at the top, and stop maintaining
status inside long analysis prose. Status belongs in one short list that is cheap to re-verify; the
analyses are worth keeping for their *reasoning*, which is still good, not for their verdicts.
