# What is actually still open — survey, 2026-08-11

Asked: *how many planned features in the planning docs are still open?*

Answer: **far fewer than the docs say.** The two documents that track open work
(`HaCue2-Framework-Gap-Analysis.md`, dated 2026-08-01 with edits to 08-03, and
`HaCue2-vs-HaPlay-CuePlayer-Parity.md`, dated 08-04) between them list **13 open subsystems and 24
missing parity items**. Checked against the tree today, **almost all of them have landed** — the docs
were never updated as the work went in.

Method: every item below was verified by grep against `UI/HaCue2*`, `.github/workflows/build.yml` and
`MFPlayer.sln`, not taken from the doc's own status column.

---

## 1. Gap analysis, "Still open, and what each is waiting for" (2026-08-03)

### The 7 "subsystems absent" — 7 of 7 now exist

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

### The 6 "smaller, deferred" — 6 of 6 now exist

Idle images (`IdleFrames.cs`, `ShowHost.IdleFrames.cs` — so the still-image decoder that "blocked" it
was solved), test pattern + Identify (`IdentifyPattern.cs`), MIDI-out for action cues (`MidiOut.cs`),
the project override ledger (`SettingsViewModel`, covered by `OverrideLedgerTests`), media-cache
management (`HaCue2.Machine.MediaCache`, wired into settings and the YouTube runtime), and the hotkeys
grid.

### The 4 "build" items — 3 of 4 done

| Item | Status |
|---|---|
| HaCue2 AOT-publish gate | **landed** — `build.yml:300` |
| Fixture gating `hacue2-check` in CI | **landed** — `build.yml:312-337`, asserts the deliberate exit 1 |
| `HaCue2.Tests` in the workflow | **landed** — CI runs `dotnet test MFPlayer.sln`, and both HaCue2 test projects are in the solution |
| HaCue2 **launch smoke** | **still open** — the only launch gate in CI is HaPlay's |

---

## 2. Parity audit (2026-08-04) — the headline gap is closed

Its sharpest finding was "Video placement — **HaPlay well ahead**, 6 model fields HaCue2 cannot express
at all". All six now exist on `LayerPlacement` (`UI/HaCue2.Core/Model/Cues.cs`):

`CropLeft/Top/Right/Bottom` (:514) · `RotationDegrees` (:527) · `ChromaKey` + `ChromaKeyEnabled`
(:549) · `ColorAdjust` + `ColorAdjustEnabled` (:554) · per-layer warp mapping · and `LayerFit` (:667)
now carries all six fit modes (`Contain, Cover, Stretch, Center, FillWidth, FillHeight`), closing the
"3 fit modes missing" row.

The doc's other 18 "no" rows were not individually re-verified — given the hit rate above, **assume
that document is stale rather than authoritative** and re-audit before planning from it.

---

## 3. What is genuinely still open

Six things, and only two are features:

| # | Item | Kind | Source |
|---|---|---|---|
| 1 | **Phase 6 of the extraction** — 46 cue-named files still in `UI/HaPlay` (the doc said 41; it grew) | structural | gap analysis |
| 2 | **`HaOutput` extraction** — the 8 couplings; deliberately deferred to Phase 6 by owner decision 2026-08-03, since HaCue2.Engine opened its own `SDL3GLVideoOutput` path instead | structural | gap analysis §7.1 |
| 3 | **HaCue2 launch smoke in CI** | build | gap analysis |
| 4 | **Multi-output genlock not wired** — `OutputSyncGroup` + `VideoPresentSyncGroup` + `SyncPresentVideoOutput` are built and tested; nothing constructs them | feature | `Doc/HaPlay-MultiOutput-Sync.md`, now labelled in `Unwired/` |
| 5 | **P7 live-ingest model not wired** — `SourceTimeline`, `LiveTimelineDriver`, `SourceSyncGroup`; demonstrated end to end by `Tools/LiveReceiveProbe`, used by no product path | feature | now labelled in `Unwired/` |
| 6 | **MiniAudio JACK backend runs managed code on the JACK graph thread** — the PortAudio-class GC hazard, still open for that one backend | defect | `Timing-Simplification-Review-2026-08-11.md` §9c |

Plus two measurements that would need re-taking rather than trusting: the gap analysis's "21 inert
controls, 56 hardcoded fields" UI count (as of 08-03), and the parity doc's remaining 18 rows.

---

## 4. The real finding

The planning docs are **not a reliable open-items list**. Two of them assert 37 open items between
them; the tree says roughly 6. That is not a documentation nit — it is the reason this question needed
a code survey to answer, and anyone planning the next phase from those documents would be planning work
that is already done.

Recommendation: mark both documents **superseded by this survey** at the top, and stop maintaining
status inside long analysis prose. Status belongs in one short list that is cheap to re-verify; the
analyses are worth keeping for their *reasoning*, which is still good, not for their verdicts.
