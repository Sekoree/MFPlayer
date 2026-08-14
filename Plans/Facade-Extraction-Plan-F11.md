# Facade extraction plan — review F-11 (proposal)

2026-08-14. Review F-11 found behavioral ownership concentrated in a few very large coordinators;
the second pass softened it correctly: the partial-class splits are better-seamed than raw line
counts suggest, so extraction goes **by state owner, never by size**, and only where a boundary
buys testability or removes an implicit-sequencing hazard. This plan names the owners, the order,
and — just as deliberately — what stays put.

## Invariants every extraction must keep

1. **One dispatcher, one transaction boundary.** ShowSession's serial dispatcher and HaCue2's
   journal remain the only write paths. A facade routes through them; it never grows its own.
2. **One state owner per facade**, with explicit disposal and a contract test. No service-per-method.
3. **Architecture-test enforcement**: each new seam gets a rule in `ArchitectureTests` (the
   CueRunner seam's `TheCueRunnerDrivesTheEngineThroughItsHostInterfaceOnly` is the pattern).
4. **Extract when touched.** No big-bang restructuring commit; each extraction rides the next real
   feature/fix that forces the file open, so review effort lands where behavior is already changing.

## The map

### ShowSession (framework, 7.8k lines over 12 partials) — mostly done, two seams left

The fire path already lives behind `CueRunner`/`ICueRunnerHost` (the model extraction), audition
already has `ISessionPreviewHost`, the visualizer half is `ShowSessionVisualizerService`, and
metadata is `ShowSessionMetadataPublisher`. Remaining owners worth a boundary:

- **Output leases** (`Transport`/`TransportVoices` partials): the acquired-output bookkeeping —
  acquire, per-lease mapping stage, retirement on live mapping updates — is one lifecycle that
  several partials touch. Extract an `OutputLeaseCoordinator` owned by the session, driven from
  the dispatcher, contract-tested against the existing live-reconfiguration tests.
- **Live edits / control ownership** (`LiveEdits` partial): the arbitration of who may write a
  live parameter (operator vs automation vs controller) is a policy, not session plumbing.
  Extract when the next controller feature opens the file.

Explicitly **not** extracted: `Queries`/`Levels`/`Stops`/`Completion` — read models and small
verb sets that already have one owner each; a facade would only add indirection.

### HaCue2 `InspectorViewModel` (3.9k) — the first real extraction

> **Status 2026-08-14: the Audio pane exemplar is DONE.** `AudioPaneEditor` (send matrix, presets,
> route readout, live pushes) sits behind `IInspectorEditorContext`; the inspector shrank ~290
> lines; `EditorRegressionTests` exercise the behavior through the new boundary and
> `InspectorEditorBoundaryTests` pins it (the inspector may not build `SetCueSendCommand` or push
> live sends itself).
>
> **Status update, same day:** `CueEditPlumbing` is extracted (both `EditEach` overloads + the
> field parsers; 30 call sites rewired), and SEVEN more editors landed on it: `FadePaneEditor`,
> `JumpPaneEditor`, `ActionPaneEditor`, `PatchPaneEditor`, `VisualizerPaneEditor`,
> `GroupPaneEditor`, `TextPaneEditor`. The inspector is down **3,935 → ~2,600 lines**, all
> per-kind panes are out, and the full headless suite passes through the new boundaries.
>
> **Finding that stops the mechanical run here:** the Video pane and the automation block are ONE
> coupled cluster - a video field notifies `PlacementHeaders` (automation region), and every
> automation-lane projection (`CanAdd*Lane`, `EffectLanes`, target placements) derives from the
> video pane's `SelectedPlacement`. Extracting Video alone would need cross-object notification
> plumbing and still leave the coupling. The right next unit is **Video + media tracks + curve
> pickers + automation as one `PlacementAndAutomationEditor` (~1,500 lines)**, done as its own
> focused pass. What then remains on the inspector is exactly the plan's target: the General
> fields, the tab router, and the editor construction.

Its own region comments are the seams, and they are per-editor, exactly what the review proposed
("split inspector behavior by feature editor session/tab"):

| Region today | Becomes | Owner lifetime |
|---|---|---|
| Audio pane + send presets | `AudioPaneEditor` | selected cue |
| Video pane + dest rect + crop + effect rack | `VideoPaneEditor` | selected cue |
| Per-kind panes (text / FADE / JUMP / ACTION / PATCH / VISUALIZER) | one small editor class each | selected cue |
| Media tracks + curve pickers + automation properties | `AutomationPaneEditor` | selected cue |
| Editable fields + edit plumbing | stays: the router + the journal seam | inspector |

`InspectorViewModel` shrinks to a router: it resolves the selection, constructs the right editor
session over the shared journal-command seam ("edit plumbing" stays the ONE write path), and
disposes it on selection change — which is also the fix for the class of bug where a stale pane
writes to the wrong cue. Each editor gets its own test file; today a pane's behavior is only
reachable through the 3.9k composite.

### HaCue2 `CuesViewModel` (3.5k)

Two owners are extractable without disturbing the transport surface:
- **Active-panel ticker** (already diff-based after N-05): its own class with the 4 Hz loop,
  testable with a fake clock.
- **Tree projection** (document → rows, badges, scope filters): a pure projection today computed
  in-place; extracting it makes the "cue tree shows X" tests direct instead of via the whole VM.
GO/fire/panic stay on the view model — they are THE surface, not an implementation detail.

### HaPlay `MediaPlayerViewModel` (2.4k + feature partials) — leave it

The partials already split by feature with clear seams (`Transport`/`Playlist`/`ShowSession`/
`Configuration`), and the second review explicitly softened this target. Revisit only if a
concrete change starts crossing partial boundaries.

### HaPlay `CueShowSessionCoordinator` (2.0k)

One candidate seam: the **poll loops** (session stats/state polling) vs the **lease + reload
wiring**. The loops are lifecycle-simple and test-awkward inside the coordinator; extract a
`SessionPollService` when next touched. The lease/reload half IS the coordinator — stays.

## Order

1. `InspectorViewModel` per-editor sessions (highest churn, clearest seams, real stale-pane hazard).
2. `CuesViewModel` ticker + tree projection.
3. `ShowSession` output-lease coordinator.
4. Everything else opportunistically, per invariant 4.

Each step lands as: new class + contract tests + arch rule + the region deleted from the
coordinator — never a forwarding layer left behind.
