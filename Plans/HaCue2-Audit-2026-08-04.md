# HaCue2 audit - 4 August 2026

## Scope

This review covered:

- the eight commits from `9db212ce` through `54e7e49e`;
- the complete HaCue2 path across `HaCue2.Core`, `HaCue2.Engine`, `HaCue2.Machine`, the Avalonia app,
  the desktop head, and both HaCue2 test projects;
- the session/framework behavior directly exercised by those changes;
- the cue-tree, transport, source-creation, timeline, locked-mode, and text-cue UI/UX paths.

The worktree was clean before the review. The starting baseline passed 619 core/engine tests and 234
headless UI tests.

## Commit-order assessment

The overall direction is sound. Most changes were made in sensible vertical slices: the clip editor
introduced its model and scanning support before its UI, engine/session corrections gained regression
tests, device/output work flowed from the framework abstraction into machine discovery and then the
pickers, and the broad cue-parity change updated model, compiler, inspector, presentation, and tests
together.

The weak point was the final two-commit text/source migration:

1. `1fba64bb` introduced Text cues as a complete app-rendered feature.
2. `54e7e49e` correctly simplified that design by moving rendering into `S.Media.Source.Text`, removed
   the app-side card cache, and added NDI/capture/YouTube source cue paths.
3. That replacement changed the Text cue from an app-side special case into an ordinary playable clip,
   but not every cross-cutting cue-kind switch was updated. Execution still treated Text as an instant
   no-op; validation, reference safety, outbound effects, duration aggregation, and several presentation
   paths still omitted it. Positive-duration cards were also compiled to freeze forever.

So the commits are ordered coherently at a feature level, but the last migration landed without a final
“new cue kind everywhere” integration pass. The fixes below complete that pass and add tests at the
boundaries that were missed.

## Implemented fixes

### Playback and runtime correctness

- Text cues now enter the real playback path. Timed cards stop naturally and can Follow; zero-duration
  cards retain the documented “hold until stopped” behavior.
- Text opacity envelopes, inherited group effects, layer ordering, outbound effect lanes, timeline
  durations, active-panel duration aggregation, and destinations now work consistently.
- A Follow chain configured to stop at a disabled cue no longer mistakes that cue for list-end and
  loops or advances into another list.
- Timeline rehearsal seeking now uses the compiler-produced cue-to-transport mapping. This fixes
  seeking children of non-timeline groups, for which re-deriving the group in the host produced a
  different transport id from the compiler.
- Engine reload compiles and loads the next document before replacing the host’s project and transport
  map. A bad edit therefore leaves the existing runtime state coherent. Reload failures are reported in
  the shell instead of escaping an `async void` callback.

### Data integrity and undo

- Duplicate is now a real deep copy. Sends, subtitles, placements, mapping sections, curves, effect
  lanes, patch levels, and other mutable children are no longer shared with the original.
- Every duplicated cue, effect lane, and mapping section gets a new id. Jump/Fade references that point
  inside a duplicated group are retargeted to the copied children; references outside the group remain
  external deliberately.
- Prepared YouTube subtitle changes are part of the same journal composite as the source edit. Choosing
  no subtitles now clears an old subtitle, and one Undo restores URI, label, duration, and subtitles.
- Locked mode now rejects authoring before file import or other pre-journal side effects, and authoring
  controls visibly disable while transport remains available.

### Validation and delete/reference safety

- Text cue effect-lane ids, endpoint references, placements, fade presets, timing, alignment, font size,
  outline width, and lane values are now validated and included in reference queries.
- Blank/whitespace cards are reported as unfinished rather than silently compiling to no clip.
- Media cue master gain, send source channels, and negative source/trim/fade/loop timing are validated
  before compilation.

### UI and UX

- The transport’s list readout is now an actual list picker. When scoped to a group, it names and
  controls the group’s owning list instead of silently falling back to the first list.
- Scope changes now refresh the list readout and group heading immediately.
- “Open timeline” is enabled only for a selected timeline group and cannot open stale content for an
  unrelated cue.
- Duplicate/Remove disable without a selection; authoring actions disable in locked mode; the enable/
  disable context action describes what it will actually do.
- Text rows show authored length (or `hold`), fade, composition, and effect badges; active rows show the
  destination composition; timelines classify Text as visual and use its real duration.
- NDI discovery runs off the UI thread, keeping GO, STOP, and active-cue controls responsive during the
  two-second discovery window.
- Superseded YouTube resolve/download operations can no longer clear the busy latch belonging to the
  newer operation. A prepared cue is not silently discarded if the project becomes locked or the cue is
  deleted while the download runs.

## Simplifications and optimizations

- Media and Text envelope compilation now share one duration-based helper rather than parallel logic.
- Runtime seeking consumes the compiler’s authoritative transport mapping instead of maintaining a
  second grouping algorithm.
- Duplicate uses the existing serialization snapshot boundary, keeping polymorphic deep-copy behavior
  aligned with save/reopen and runtime snapshots.
- Text presentation uses authored duration directly rather than waiting for a media probe that will
  never exist for an in-document card.
- NDI discovery is still on-demand, but its blocking native/network work no longer occupies the UI
  dispatcher.

## Verification

Final automated verification:

- `HaCue2.Core.Tests`: **626 passed**
- `HaCue2.Tests`: **240 passed**
- `S.Media.Session.Tests`: **349 passed**
- `S.Media.Arch.Tests`: **27 passed**
- Total: **1,242 passed, 0 failed**
- `HaCue2.Desktop` Debug build: **succeeded with 0 warnings and 0 errors**
- `git diff --check`: clean

The new tests cover Text execution/end behavior/envelopes/presentation, disabled Follow policy, Text
validation and references, deep duplicate isolation/internal retargeting, multi-list transport selection,
timeline opening, locked authoring, and source-subtitle undo.

## Manual checks still required on a show machine

The headless environment cannot meaningfully verify physical-device behavior. Before using these recent
features in a performance, rehearse:

- PortAudio/miniaudio device opening, clock selection, and the intended output patch;
- a real NDI sender’s discovery, audio/video selection, and loss/reconnect behavior;
- local capture-device ownership and format negotiation;
- YouTube resolution/download/remux with network access, followed by offline playback;
- SDL output windows on the actual multi-monitor/projector layout;
- MIDI/OSC input and output against the real desk/controllers.

No remaining code defect was found in the audited HaCue2 paths after the fixes and automated checks;
the items above are integration checks that require the target rig.
