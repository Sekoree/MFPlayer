# HaCue2 in-depth product, runtime, UI/UX, and framework audit

**Audit date:** 2026-08-05  
**Branch / revision:** `cue-separation` / `0f9f393f` (`Some QoL fixes`)  
**Scope:** `UI/HaCue2*`, the retained HaPlay cue player, and the S.Media paths HaCue2 uses  
**Outcome:** original analysis plus implementation/verification appendix; see the final section

## Executive verdict

HaCue2 is no longer a thin or obviously unfinished extraction. Its document model, undoable editing, patching, cue execution, control ingress, output mapping, recording, status surfaces, and tests form a coherent standalone application. The shell launched successfully, the audited projects built without warnings, 2,162 tests passed, both supplied 4K60 ProRes files decoded without a stall, and the multi-output timing smoke stayed well inside its frame budget.

It is nevertheless **not ready to be treated as feature-complete for a live show**. The most important problems are integration gaps at the application composition root, not missing framework capabilities:

1. HaCue2 always constructs the framework's CPU compositor. It never injects HaPlay's working SDL3/OpenGL compositor factory.
2. HaCue2 never supplies the framework subtitle-overlay factory, so subtitle choices can be authored but do not render.
3. The documented Stop/Panic shortcuts do not exist; both `Esc` and `Ctrl+Esc` currently perform Standby Here.
4. “Open locked” is not applied, and the partial lock that does exist can allow a consolidate operation to copy files while rejecting the matching document change.
5. Playlist pass count is ignored for Hold and Next List end policies.
6. Keyboard trigger sources are serialized and even included in the sample, but no keyboard events ever reach the trigger engine.
7. An external `transport · go` command always targets the first cue list, not the operator's current list.

The first compositor issue has a wide blast radius: it makes output mesh warp affine-only, causes projectM visualizers to be refused for lack of a GL surface host, and leaves all video composition on the CPU. A clean synthetic GL smoke on this machine demonstrates that this is missing wiring rather than an unavailable graphics path.

The settings and diagnostics screens also expose a meaningful collection of switches and fields that persist but are never consumed, fields that appear editable but are never saved, and commands whose label overstates what they reset. Those are product trust problems in an operator application even where they do not crash.

## What was examined

- HaCue2 Core, Machine, Engine, UI, Desktop, Check, Seed, and both test projects.
- Every top-level HaCue2 view/window and its call sites.
- The complete cue execution path from a shell gesture or external trigger through `CueExecutor`, `ShowHost`, `ShowCompiler`, and `ShowSession`.
- Project/application settings, persistence, recovery, locking, preflight/status, diagnostics, cache, logging, remote control, recording, and output configuration.
- HaCue2 mapping, placement, effects, subtitles, tracks, playlist, jump, fade, visualizer, action, patch, text, live-input, and transport authoring.
- Retained HaPlay cue-player behavior and its current model/runtime, rather than relying only on the older separation plans.
- S.Media session, compositor, player, subtitle, output, meter, projectM, and smoke/benchmark tooling relevant to HaCue2.
- A real desktop launch and the supplied 4K60 ProRes media under `/home/sekoree/Videos/`.

The earlier documents in `Plans/` were useful hypotheses, but this report rechecked current code because several gaps recorded on 2026-08-04 have already been closed.

## Verification record

### Build and automated tests

| Check | Result |
|---|---:|
| `HaCue2.Desktop` clean build | 0 warnings, 0 errors |
| `HaCue2.Core.Tests` | 657 passed |
| `HaCue2.Tests` | 305 passed |
| `S.Media.Session.Tests` | 349 passed |
| `S.Media.Compositor.Tests` | 54 passed |
| `S.Media.Players.Tests` | 16 passed, 1 skipped |
| `S.Media.Core.Tests` | 781 passed, 2 skipped |
| **Audited total** | **2,162 passed, 3 skipped** |

The skipped framework tests were pre-existing environment/fixture skips, not failures introduced by HaCue2.

The passing count is encouraging but also illustrates the central coverage gap: no current HaCue2 test exercises its real `ShowHost.CreateAsync` composition root and asserts the selected compositor, subtitle factory, mesh output, or visualizer surface attachment.

### Graphics and application launch

- The framework `CompositorSmoke` passed centre/corner pixel checks in both normal and pipelined GL modes.
- HaCue2 Desktop launched with an isolated `HACUE2_DATA_ROOT`, opened the sample project, and rendered the complete shell.
- The live shell is visually cohesive and appropriately transport-centric, but text and chrome are very dense. At roughly 2,140 × 1,380 client pixels, the fixed-width inspector already truncates labels and tab headings. The declared 1,080 × 700 minimum is unlikely to provide a good editing experience.
- PortAudio enumeration emitted repeated native ALSA diagnostics. This did not prevent launch, but it makes console/log output noisy and may disguise useful diagnostics.

### Heavy-media evidence

| File | Relevant probe facts | Playback result |
|---|---|---|
| `shootingstar_0611_1.mov` | 38,112,979,333 bytes; ProRes HQ; 3840 × 2106; 10-bit 4:2:2; 60 fps; about 942 Mb/s; 323.7 s | 598 frames over the 10-second smoke window; PTS advanced to 10.150 s; no stall |
| `おねがいダーリン_0611.mov` | 8,752,251,230 bytes; ProRes HQ; 3840 × 2106; 10-bit 4:2:2; 60 fps; about 780 Mb/s; 89.8 s | 600 frames over the 10-second smoke window; PTS advanced to 10.150 s; no stall |

The headless multi-output smoke on the larger file delivered 601 frames with no reported drops or misses. Cross-output skew had a 0.01 ms median, 0.02 ms p95, and 0.12 ms maximum against a 20 ms budget.

A full framework `SessionSmoke` using the larger source also passed decode, ShowSession, CPU composition at 1,280 × 720 / 24 fps, subtitle setup, output, live placement changes, seeks, pause/resume, trim/loop, and fades. It reported:

- steady A/V sync median -19 ms, p95 absolute 29 ms, jitter 10 ms;
- no paused-clock advance, and a normal resume;
- about 89 KiB/s allocation in its measured window, with no Gen 0 collection;
- 125.2 seconds of process user CPU over 33.0 seconds wall for the entire smoke invocation.

That last number is not a compositor-only benchmark, but it is a useful warning: even the reduced 720p24 CPU path kept several cores busy. Direct ProRes decode and frame fan-out look healthy; the missing GL factory is the first performance problem to solve before drawing conclusions about 4K60 show capacity.

## Priority register

| ID | Priority | Area | Finding |
|---|---|---|---|
| HAC-001 | High | Video runtime | HaCue2 always uses the CPU compositor; GL mapping and surface-host features are unavailable |
| HAC-002 | High | Subtitles | Subtitle selections compile but no overlay factory is supplied, so they never render |
| HAC-003 | High | Operator safety | Shortcut reference is false; Stop/Panic keyboard actions are absent |
| HAC-004 | High | Project safety | Open Locked is ignored and consolidate can have side effects under the partial lock |
| HAC-005 | High | Playlist | Pass count only works when At End is Loop |
| HAC-006 | High | Triggers | Keyboard trigger input is a dead source |
| HAC-007 | High | Triggers/transport | External generic GO operates on the first list, not the current list |
| HAC-008 | High | Coverage | Tests do not exercise the production composition root, permitting HAC-001/002 to pass unnoticed |
| HAC-009 | Medium | Settings | Multiple application settings persist but have no runtime consumer |
| HAC-010 | Medium | Cache | Cache root/budgets/thumbnail claims do not describe or control the real caches |
| HAC-011 | Medium | Status | Video outputs become Unknown and are then summarized as “outputs ok” |
| HAC-012 | Medium | Diagnostics | Reset Counters clears messages, not the displayed counters or clip state |
| HAC-013 | Medium | Mapping UX | Mesh authoring is blind numeric/nudge editing and calibration is transient/incomplete |
| HAC-014 | Medium | HaPlay parity | Several intentional-looking old workflows still have no HaCue2 equivalent |
| HAC-015 | Medium | Accessibility | Custom controls and icon buttons have almost no automation semantics |
| HAC-016 | Medium | Responsive UX | Fixed dense inspector and minimum window size do not degrade gracefully |
| HAC-017 | Medium | Settings/logging | Several editable paths do not save; logging changes are presented as live but are startup-only |
| HAC-018 | Low | Diagnostics/noise | Native audio enumeration produces repeated ALSA warnings |
| HAC-019 | Low | Maintenance | Small duplication and stale benchmark commentary remain |

## Detailed findings

### HAC-001 — the production session never selects the GL compositor

`UI/HaCue2.Engine/ShowHost.cs` creates the session as:

```csharp
new ShowSession(registry, backend, programAudioTarget: target)
```

No `compositorFactory` is passed. `ShowSession` therefore uses `ClipCompositionRuntime.CreateDefaultCompositor`, which creates `CpuVideoCompositor`.

HaPlay's two current session creation paths explicitly pass `CueCompositionRuntime.CreateShowSessionCompositor`. That factory probes SDL3/OpenGL, returns `SDL3GLVideoCompositor` with the correct BGRA and owner-thread-disposal policy, and retains a CPU fallback when GL is genuinely unavailable.

Consequences in HaCue2:

- Section source/destination rectangles and affine placement render, but an authored output `Mesh` cannot warp in the CPU compositor. The framework warning says mesh warp requires GL and falls back to affine placement.
- `ProjectVisualizers.FireAsync` calls `SetCompositionVisualizerAsync`, which requires an `IVideoCompositorSurfaceHost`. The CPU compositor is not one, so a projectM cue is refused with “it has no GL surface.” The UI, model, native context factory, and runtime wrapper all exist, but the advertised feature cannot reach a compatible surface.
- Composition remains CPU-based even on a machine where the framework's GL pixel smoke passes.
- UI copy currently says mesh is a “GL output path only” feature without revealing that HaCue2 never chooses that path.

This should not be fixed by copying another private factory into HaCue2. Move/generalize the working factory into an S.Media or shared application-host component, then let both applications inject it. One shared composition root should own:

- GL probe and CPU fallback policy;
- BGRA conversion policy;
- owner-thread disposal;
- subtitle overlay creation;
- optional metadata probing; and
- runtime capability reporting to the UI.

Acceptance tests should assert both paths: GL is selected when the probe succeeds; CPU fallback is selected and visibly reported when it does not. A pixel-level mesh test should prove that a non-affine control point changes the rendered output.

### HAC-002 — subtitles are authorable but not playable

HaCue2 has embedded/sidecar subtitle selection, styling fields, serialization, probe data, inspector UI, validation, and tests of document editing. `ShowCompiler` carries those selections into the framework binding.

However, `ShowHost` supplies no `subtitleFactory` to `ShowSession`. The framework only creates overlays when that delegate is non-null. HaPlay supplies:

```csharp
(path, streamIndex, w, h) =>
    SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex)
```

The result is a polished authoring dead end: a user can spend time selecting and styling subtitles that will never appear.

Fix this in the same shared composition-root work as HAC-001. Add integration coverage for one sidecar and one embedded stream, including seek and loop behavior. Editing-only tests are insufficient.

### HAC-003 — the hotkey screen and actual safety keys disagree

The Settings window labels its hotkey section WIP, but it still presents a concrete operator reference:

- Stop: `Esc`
- Panic: `Ctrl+Esc`

`ShellWindow.OnKeyDown` handles any `Key.Escape`, without checking modifiers, by calling `StandbyHere()`. Therefore both displayed gestures move standby. Neither stops nor panics.

The File menu also displays `Ctrl+N` and `Ctrl+O`, but only Save/Save As are explicitly handled. Avalonia's `MenuItem.InputGesture` displays gesture text; it does not implement the command itself. N and O are consequently display-only.

This is high priority because an operator may learn the shown emergency gesture and discover the mismatch during a show. Until editable profiles exist, install one authoritative keymap with command bindings and tests. Conflict resolution, focus-in-text-box rules, and whether Panic may ever be intercepted must be explicit. Do not show a gesture until it is executable.

### HAC-004 — locking is incomplete and can produce partial consolidate results

`ProjectSettings.OpenLocked` is only edited and serialized. Project opening never applies it to `ProjectJournal.IsReadOnly`, so “Open locked” does nothing.

The manual lock only makes the journal reject commands. It does not propagate a common read-only state through Audio, Video, Targets, Settings, and every project-side operation. Controls can remain enabled, accept input, and then appear to do nothing when their journal command is refused.

There is also an unsafe side-effect boundary. Project Status keeps Consolidate enabled whenever a journal exists. Consolidation copies media into the project store before submitting the document-path rewrite. Under `IsReadOnly`, the copy can happen and the command can be rejected, leaving duplicated files while the project still points at the originals.

Use one shell-level `CanAuthor = Journal is { IsReadOnly: false }` contract for all authoring surfaces. Guard external side effects before starting work, not merely at `journal.Do`. The journal should also return an explicit refusal result so UI commands cannot silently pretend success.

Required tests:

- Open Locked is applied on normal open and recovery open.
- All editing panes and settings expose read-only state consistently.
- Consolidate, relink, imports, cache-affecting project actions, and target sends cannot mutate external state while locked.
- Unlock restores authoring without reconstructing unrelated runtime state.

### HAC-005 — playlist passes contradict their own model and UI

`GroupCueNode.LoopCount` says it is the number of passes, while `AtEnd` says what happens after the final pass. The inspector reinforces that separation. The example “play this twice and then hold” is even written into the model and executor comments.

`CueExecutor.FinishPlaylistAsync`, however, switches on `AtEnd` first. It evaluates `LoopCount` and starts another pass only inside `AtListEnd.Loop`. Hold and Next List remove the run after the first pass, regardless of `LoopCount`.

The executor should first decide whether another pass remains. Only after the final pass should it apply Hold, Next List, or the chosen terminal behavior. Add tests for finite and infinite passes across every end policy, shuffle stability, repeat avoidance, and crossfade final-edge handling.

### HAC-006 — Keyboard trigger input is a serialized dead end

`TriggerInputKind.Keyboard` exists, the sample project creates a Keyboard source, and presentation copy describes it as always available. `TriggerInputs.Open` deliberately excludes it. The only live ingress is MIDI/OSC monitor records plus clock handling, and `ShellWindow` never feeds key gestures into `TriggerInputs`.

The editor also has no corresponding “add keyboard source” path. Existing/sample bindings are therefore inert.

Implement keyboard gestures through the same authoritative key router recommended for HAC-003, including modifier normalization, repeat suppression, learn mode, focus policy, and conflicts with transport shortcuts. If that is not planned soon, stop creating/showing Keyboard sources so projects cannot contain a control surface that will never fire.

### HAC-007 — generic external GO fires the wrong list

Learn mode exposes `transport · go`. `ShowHost.Triggers.TransportAsync("go")` iterates project cue lists and breaks after the first. It has no connection to `CuesViewModel`'s currently selected/scoped list.

On a multi-list show, a MIDI or OSC GO can therefore fire list 1 while the operator is looking at a different list. That is a dangerous split-brain between UI transport and hardware transport.

Either synchronize one active-list ID into the engine or make bindings explicitly list-specific. The generic command should use exactly the same transport service as the Space/GO button. Test current-list changes, deleted lists, project reload, and simultaneous UI/external GO.

### HAC-008 — the test pyramid stops below the real composition root

The test suite is broad and generally well organized. It validates serialization, patch math, cue execution decisions, shell binding, view rendering, mapping commands, settings persistence, control flow, and many framework primitives. What it does not do is construct HaCue2's production `ShowHost` and verify its optional framework dependencies.

That is how a CPU-only compositor, null subtitle factory, and unusable projectM attachment coexist with 2,162 passing tests.

Add a small `HaCue2.ShowSmoke` or equivalent integration fixture that:

1. loads a minimal `.hacue2proj` through the real application composition root;
2. asserts `BackendName`/capabilities after GL probing;
3. renders a non-affine mesh to pixels;
4. renders a sidecar subtitle after seek;
5. attaches a visualizer surface when projectM is available, with an explicit optional skip otherwise;
6. reloads a document and proves active output/surface ownership remains valid; and
7. prints frame latency, drops, allocation, and CPU data for a supplied media path.

Keep CPU fallback coverage too. The point is not “GL always”; it is “the selected backend matches the machine and the UI tells the truth.”

### HAC-009 / HAC-017 — settings that persist without controlling behavior

The application settings view writes many values successfully, but the runtime never consumes these:

- `RememberInspectorTab` — inspector state is remembered unconditionally per cue kind.
- `RememberTimelineDock`.
- `FlatActiveList`.
- `OpenDrawerOnLaunch`.
- `MeterBallistics` — framework meter code currently uses fixed behavior.
- `PeakHoldMs`.
- `ClipReset` — there is no wired click/three-second policy.
- `RunStatusChecksOnOpen` — status is constructed and surfaced regardless.

Other settings are only partly connected:

- Application `StopFadeMs` persists, but existing projects use project `StopFadeMs`, and new-project creation does not seed from the application value. It currently has no effective consumer.
- Application Panic Fade is copied into the host at engine startup. Editing it in a running app is presented as immediate but does not update `MachinePanicFadeMs`; the project override is live.
- `FileLogLevel`, retention, and crash-dump settings save, but logging/crash services are constructed once. The screen generally promises immediate application-scope behavior and does not mark these as restart-required.
- `AppSettingsStore.Save` returns success/failure, but the settings writer ignores the result, making persistence failure silent.
- Double-GO Guard parses digits from the text. A human-friendly entry such as `0.25 s` becomes `25 ms`, not `250 ms`.

Three displayed text boxes have no write handler at all:

- Cache root;
- Log directory; and
- Recovery location.

Cache and log directory look editable but only modify a view-model field. Recovery files are intentionally placed beside the project, so Recovery Location should probably be explanatory/read-only unless that architecture changes.

Every setting should be placed in one of four explicit categories: applied live, applied to new projects, applied after restart, or removed. Add a binding-to-consumer test table; persistence-only tests do not prove a setting works.

### HAC-010 — the cache page describes a system that is not implemented

The default Cache Root label says “shared framework cache,” while HaCue2's `MediaCache` defaults to `<HaCue2 data root>/cache`. YouTube media uses the separate framework `MediaCachePaths.For("youtube-cache")`. Editing Cache Root does not persist, so it controls neither location.

Waveform and Thumbnail Budget strings persist but have no parser, enforcement, accounting, or eviction. The clear/report path assumes a `thumbnails` directory, but no corresponding HaCue2 thumbnail producer was found.

Choose one of two honest designs:

- Introduce a shared cache policy service with canonical root, categories, byte-count parsing, LRU/age eviction, reporting, and safe clear operations; or
- Remove the budget/category controls until the feature exists, and show the actual independent paths that can be opened or cleared.

YouTube downloads, waveform data, thumbnails, and temporary probe artifacts should not silently escape a limit that claims to be global.

### HAC-011 — output preflight can report Unknown as OK

When audio devices are available the shell uses `MachineEnvironment`. That environment deliberately reports every video output as Unknown. The richer runtime absence information exists separately in `ShowHost.Problems`/runtime output state, not in Project Status.

The status token only treats Failed and Warning as bad, so a collection of Unknown video checks can produce a green “outputs ok” summary. `RunStatusChecksOnOpen` does not change this behavior.

Build a composite environment:

- filesystem/media checks from the project checker;
- machine audio/device checks from Machine;
- live video-output/screen acquisition and backend capability checks from Engine; and
- an explicit neutral “not checked” token state that can never be rendered as green OK.

This is also the right place to report CPU fallback, unavailable mesh warp, or projectM surface support before GO.

### HAC-012 — Reset Counters does not reset counters

Diagnostics labels a command **RESET COUNTERS**. It clears the host problem list and in-memory log ring. It does not reset bay counters, meter clip state, or the displayed “since show started” baseline.

Either relabel it **CLEAR PROBLEMS AND LOG**, or add a real reset/baseline API that includes throughput/drop counters and `ProgramBusMeter.ResetClip`. The latter can become the common implementation for the currently inert Clip Reset setting.

### HAC-013 — mapping authoring is capable but still too indirect

HaCue2's mapping editor has improved substantially and should not be described as merely “dumbed down.” It now provides:

- separate source and destination canvases;
- exact numeric source fractions and destination pixels;
- an even-grid splitter;
- add, copy, reorder, replace, and undoable section edits;
- independent 2–16 row/column mesh sizes;
- reset, selected-point addressing, numeric offsets, and arrow nudges; and
- output identify/test actions.

The remaining usability problem is the mesh itself. Selecting `rN cN` in a combo box and typing/nudging `dx/dy` is effectively blind. There are no visible curved grid lines or draggable handles.

HaPlay's retained `MappingEditorDialog` already implements a better interaction model: tessellated curved grid lines, handles, pointer capture, coordinate transforms, and direct drag. Generalize that as a reusable mapping control rather than letting the two apps diverge again. Keep HaCue2's exact numeric editor beside it for precision and keyboard access.

HaPlay also provides a persistent output calibration grid. HaCue2's four-second Identify output is mainly a blue/name slate. It lacks orientation corner colors, section boundaries, warp-grid visibility, and a stay-on-until-closed mode. Generalize the existing HaPlay test pattern and ensure it is always cleared on close/project replacement.

This UX work comes after HAC-001: today the authored mesh is not rendered by HaCue2's actual compositor.

### HAC-014 — remaining HaPlay parity gaps

The following are still present in the current retained HaPlay player and have no equivalent behavior in HaCue2:

- **Playlist `PlayCount`:** play only N selected items per pass rather than every child.
- **First Cue Only group mode:** fire just the first enabled child.
- **Armed List group mode:** GO advances children while natural end holds.
- **Per-media end target:** fire a specific target when that media cue ends. HaCue2's separate Jump cues can author some chains but are not a direct end-edge equivalent.
- **Selective visualizer feed:** `FeedAll`, `FeedCueIds`, and per-media `SendToVisualizer`. HaCue2's visualizer is attached to compositions but does not expose equivalent source selection.
- **Editable hotkey/profile system:** HaCue2's screen is explicitly WIP, and the static fallback is incorrect as described in HAC-003.

HaCue2 does have a richer general Jump cue in other respects: conditions, visit count, random target selection, and fire-on-arrival are carried over or improved. Parity should therefore be evaluated by workflow rather than by copying every field.

Features that are now correctly present and should not be reopened as gaps include pre-roll, upcoming/active aggregation, crop/rotation/fit/effects, track pickers, subtitles in the document model, end behaviors, loop crossfade, color tags, image/text/live inputs, composition feeds, output sections, and seek-lock handling. Subtitle runtime rendering remains broken specifically because of HAC-002.

MMD cues were deliberately experimental and excluded. Their absence is not a defect.

### HAC-015 — accessibility needs a deliberate pass

Across all 37 HaCue2 AXAML files (including 20 files under `Views/`), no explicit `AutomationProperties.Name` was found. There are many tooltips, but tooltips do not provide a reliable screen-reader name and are poor on touch.

The largest risks are:

- icon-only layout and transport-adjacent buttons;
- custom placement, curve, matrix, and mapping canvases;
- tiny status tokens whose color carries meaning;
- keyboard reachability and focus indication in dense inspector sections; and
- live updates that are visually obvious but not announced.

Add stable automation names/help text to icon controls, semantic peers for custom canvases, keyboard alternatives for every pointer operation, and automated focus/automation-tree checks. Test with large text/high contrast as well as a screen reader.

### HAC-016 — density and resizing need product-level rules

The shell has a good booth-like information hierarchy: cue list and GO remain primary, active state is close at hand, and project/machine scopes are understandable. The same density becomes fragile at smaller sizes:

- the inspector is fixed around 316 px and already truncates labels at a large desktop size;
- some segmented tabs compress to ambiguous fragments;
- matrix headers and numeric fields are small and low contrast;
- the 1,080 × 700 minimum leaves little room for list, drawer, timeline, and inspector together; and
- there is no touch/relaxed-density mode.

Make the inspector resizable and collapsible, establish minimum useful widths per pane, allow long forms to scroll without shrinking controls below target size, and add screenshot/layout tests at minimum, laptop, 1440p, and scaled/high-DPI sizes. A compact booth mode and a relaxed authoring mode would serve different phases better than one fixed density.

### HAC-018 / HAC-019 — smaller cleanup items

- Audio enumeration produced repeated ALSA diagnostics on launch. Enumerate once where practical and route/suppress expected native probe noise without suppressing genuine device failures.
- `TargetsViewModel.SendTestAsync` assigns `probe.Address = parts[0]` twice. This is harmless duplication.
- `TessellateBenchmarks` commentary says tessellation occurs per frame, while the current GL implementation caches layer and output mesh buffers. Update the benchmark description so it measures the current concern.

## Reachability and forgotten-screen audit

All top-level files under `UI/HaCue2/Views/*.axaml` have construction, navigation, host, or dialog call sites. No wholly unreachable window was found. The single-shell structure avoids a collection of orphaned secondary windows.

The dead ends are subtler than unreachable AXAML:

- Hotkeys is visibly WIP and its displayed bindings are not implemented.
- Keyboard trigger sources can be saved but never receive keyboard events.
- Subtitle controls edit a document feature that the runtime cannot render.
- Visualizer controls feed a runtime that the selected CPU compositor refuses.
- Cache/log/recovery path boxes look editable but do not persist.
- Several appearance and meter switches persist but have no consumer.
- Open Locked persists but does not affect project opening.
- Reset Counters does not perform its labelled operation.

Those should be tracked as product completeness bugs, not dismissed because the view itself is reachable.

## Architecture assessment

### Strong parts worth preserving

- The Core/Machine/Engine/UI split is clear and substantially more standalone than the old embedded player.
- `ProjectJournal` gives authoring a centralized command/undo seam.
- `CueExecutor` keeps transport decisions behind a testable host abstraction instead of teaching the framework every application cue type.
- Snapshot compilation and debounced reload preserve active groups and compatible composition state.
- Output synchronization is designed to be idempotent, and output definitions are dynamic rather than startup-only.
- One decoded source can fan out through extra placements instead of decoding once per output.
- Patch gains, cue sends, voice fades, and master trim have a clear composition model.
- Control input, clocks, outbound actions, recorders, and optional projectM support are isolated services.
- Application and project scopes are visibly distinguished in settings.
- Remote control remains closed by default and preserves the existing optional-token/closed-LAN policy.
- The status checker is mostly pure Core logic and has a headless companion.

### Where simplification will help

The main simplification is to stop assembling optional ShowSession behavior separately in each UI application. A shared `ShowSessionHostOptions`/factory should be the one place that connects the framework's production compositor, subtitles, metadata, capabilities, and fallback reporting. HaPlay and HaCue2 can still choose policy, but should not silently omit whole subsystems through optional constructor arguments.

Settings need a similar contract. Each property should declare its consumer, activation mode, validation, and scope. A generated or hand-maintained settings registry can drive both the UI and binding tests, eliminating the current “saved therefore implemented” assumption.

Finally, an application-level transport service should be authoritative for GO/Stop/Panic/Standby and current-list scope. UI keys, buttons, MIDI, OSC, remote control, and future keyboard triggers should call it rather than reimplementing list selection or safety semantics.

## Recommended implementation order

### Phase 1 — make rendered/runtime behavior truthful

1. Extract and inject the shared GL/CPU compositor factory.
2. Inject the subtitle overlay factory.
3. Add the production `ShowHost` integration smoke before changing other video UX.
4. Surface compositor/capability/fallback state in preflight and Diagnostics.
5. Verify mesh warp, projectM attachment, subtitles, document reload, and output lease replacement on the real app path.

### Phase 2 — close operator-safety faults

1. Introduce one transport command service and authoritative keymap.
2. Correct Stop/Panic and File menu gestures; add key-routing tests.
3. Route external GO through the current list.
4. Implement Keyboard trigger ingress through the same key router, or remove the dead source.
5. Correct playlist pass/end ordering and add the missing behavior matrix.

### Phase 3 — make lock, settings, and status honest

1. Apply Open Locked and propagate a single `CanAuthor` policy.
2. Guard consolidate/relink/import side effects before they start.
3. Classify every setting as live/new-project/restart/remove and wire or remove it.
4. Replace the phantom cache budgets with a real shared cache policy or a factual path/clear screen.
5. Merge machine and live-runtime status; never summarize Unknown as OK.
6. Rename or implement Reset Counters.

### Phase 4 — finish authoring parity and UX

1. Generalize HaPlay's draggable mesh overlay and persistent calibration pattern.
2. Decide which remaining parity workflows belong in standalone HaCue2: Play Count, Armed List, First Cue Only, per-media end target, and selective visualizer feed.
3. Add responsive inspector/density behavior and minimum-size screenshot tests.
4. Complete the automation/accessibility pass.

### Phase 5 — performance qualification

After GL wiring is fixed, create a repeatable HaCue-specific qualification matrix using the supplied ProRes files:

- direct decode at native 4K60;
- one decode feeding 1, 2, and 4 compositions/outputs;
- affine placement versus warped mesh;
- subtitle on/off;
- visualizer on a second composition;
- live mapping edit and output reconfiguration during playback;
- seek, loop boundary, crossfade, stop fade, and panic;
- JIT and published/NativeAOT builds where supported.

Record presented/dropped frames, decode latency, compositor latency, cross-output skew, process CPU, GPU utilization, working set, allocations, and recovery after a deliberate output loss. The existing `VideoPlaybackSmoke`, `MultiOutputSmoke`, `SessionSmoke`, and compositor smoke are good lower-level building blocks; the missing piece is an executable that goes through HaCue2's real host.

## Boundaries and remaining manual verification

This machine provided a valid desktop/GL environment and heavy media, but not a full show rig. The audit did not claim physical verification of:

- a projector or LED processor on the final glass;
- multiple physical screens and hot-plug/disconnect behavior;
- real MIDI hardware, OSC peer, LTC/MTC source, NDI sender/receiver, or recorder destination;
- projectM native-library availability and preset packs;
- real audio interfaces with multi-channel patching; or
- packaged NativeAOT launch behavior.

Those are appropriate acceptance checks after the high-priority composition-root and transport fixes. They do not weaken the confirmed code-path findings above: the current constructor cannot select GL or create subtitle overlays on any rig.

## Release recommendation

Do not call HaCue2 feature-complete or use it as the only operator path for a production show until HAC-001 through HAC-008 are resolved and exercised through the real application host. A preview/authoring build is reasonable if the UI clearly marks GL mesh, visualizer, subtitles, keyboard controls, and locking limitations.

Once those integration seams are fixed, the underlying design is strong enough that the remaining work is mostly product honesty, parity decisions, and operator polish—not another extraction rewrite.

## Reproduction command reference

Run these from the repository root. The smoke projects should be built in Release first when using `--no-build`.

```bash
dotnet build UI/HaCue2.Desktop/HaCue2.Desktop.csproj --no-restore --nologo -m:1
dotnet test UI/HaCue2.Core.Tests/HaCue2.Core.Tests.csproj --no-restore --nologo -m:1
dotnet test UI/HaCue2.Tests/HaCue2.Tests.csproj --no-restore --nologo -m:1

dotnet test MediaFramework/Test/S.Media.Session.Tests/S.Media.Session.Tests.csproj --no-restore --nologo -m:1
dotnet test MediaFramework/Test/S.Media.Compositor.Tests/S.Media.Compositor.Tests.csproj --no-restore --nologo -m:1
dotnet test MediaFramework/Test/S.Media.Players.Tests/S.Media.Players.Tests.csproj --no-restore --nologo -m:1
dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore --nologo -m:1

dotnet run --project MediaFramework/Tools/CompositorSmoke/CompositorSmoke.csproj -c Release
dotnet run --project MediaFramework/Tools/VideoPlaybackSmoke/VideoPlaybackSmoke.csproj -c Release -- \
  /home/sekoree/Videos/shootingstar_0611_1.mov 10
dotnet run --project MediaFramework/Tools/MultiOutputSmoke/MultiOutputSmoke.csproj -c Release -- \
  /home/sekoree/Videos/shootingstar_0611_1.mov 10 --headless
dotnet run --project MediaFramework/Tools/SessionSmoke/SessionSmoke.csproj -c Release -- \
  /home/sekoree/Videos/shootingstar_0611_1.mov \
  /home/sekoree/Videos/shootingstar_0611_1.mov
```

---

# Independent verification pass

**Verified:** 2026-08-05, same day as the audit  
**Revision:** `cue-separation` / `0f9f393f` — unchanged since the audit; the working tree carries only this
document and `CLAUDE.md` as untracked files, so no remediation had landed between the two passes.  
**Method:** each finding re-checked against current source; the build and every audited test project
re-run. The desktop launch, heavy-media smokes, and GL smoke were *not* re-run.

**Outcome: 18 of 19 findings confirmed as written. One (HAC-019, first bullet) is incorrect. Three
findings are correct in substance but need a nuance recorded below.**

## Verification record, reproduced

| Check | Audit | This pass |
|---|---|---|
| `HaCue2.Desktop` build | 0 warnings, 0 errors | ✅ 0 warnings, 0 errors |
| `HaCue2.Core.Tests` | 657 passed | ✅ 657 passed |
| `HaCue2.Tests` | 305 passed | ✅ 305 passed |
| `S.Media.Session.Tests` | 349 passed | ✅ 349 passed |
| `S.Media.Compositor.Tests` | 54 passed | ✅ 54 passed |
| `S.Media.Players.Tests` | 16 passed, 1 skipped | ✅ 16 passed, 1 skipped |
| `S.Media.Core.Tests` | 781 passed, 2 skipped | ✅ 781 passed, 2 skipped |
| **Total** | **2,162 passed, 3 skipped** | ✅ **2,162 passed, 3 skipped** |

## Confirmed findings, with the evidence that confirms them

| ID | Verdict | Evidence located this pass |
|---|---|---|
| HAC-001 | Confirmed | `ShowHost.cs:396` constructs `new ShowSession(registry, backend, programAudioTarget: target)` — no `compositorFactory`, so `ClipCompositionRuntime.cs:155` falls to `CreateDefaultCompositor` → `CpuVideoCompositor`. HaPlay passes `CueCompositionRuntime.CreateShowSessionCompositor` at `CueShowSessionCoordinator.cs:224` and `MediaPlayerViewModel.ShowSession.cs:504`. Both consequences verified end to end: the mesh→affine fallback warning is `ClipCompositionRuntime.cs:1783`, and `VideoCompositorSource.SupportsSurfaceLayers` (`:126`) is `_compositor is IVideoCompositorSurfaceHost`, which `SetCompositionVisualizerAsync` (`ShowSession.Taps.cs:165`) requires — producing the exact “it has no GL surface” refusal at `ProjectVisualizers.cs:130`. The “GL output path only” tooltip is `VideoView.axaml:728`. |
| HAC-002 | Confirmed | No `subtitleFactory` at the same call site. `ShowSession.cs:1126` gates overlay creation on `_subtitleFactory is { }`. The authoring half does work: `ShowCompiler.cs:277,343` compiles the selections into the binding. HaPlay supplies `SubtitleOverlayFactory.FromFileDeferred` at both its session sites. |
| HAC-003 | Confirmed | `AuxiliaryViewModels.cs:1185-1186` lists Stop `Esc` and Panic `Ctrl+Esc`; `ShellWindow.axaml.cs:77` is `case Key.Escape:` with no modifier test, calling `StandbyHere()`. `OnKeyDown` handles Ctrl+S and Ctrl+Shift+S only; `Ctrl+N`/`Ctrl+O` at `ShellWindow.axaml:38-39` are `InputGesture` display text. |
| HAC-004 | Confirmed | `OpenLocked` has three references total — the model plus the settings view-model’s read/write; nothing applies it to `ProjectJournal.IsReadOnly`. `MediaEdits.Consolidate` calls `store.Copy(...)` *before* `journal.Do(...)`, and `ProjectJournal.Do` (`:97`) returns silently under `IsReadOnly` and is `void`, so there is no refusal result to observe. The button is `IsEnabled="{Binding CanEdit}"` = `_journal is not null`. |
| HAC-005 | Confirmed | `CueExecutor.FinishPlaylistAsync:250` switches on `AtEnd` first; `LoopCount` is read only inside `case AtListEnd.Loop` (`:260`). `NextList` (`:275`) and the `default` Hold arm (`:282`) remove the run after the first pass. |
| HAC-006 | Confirmed | `TriggerInputs.Opens()` (`:151`) is `MidiIn or OscIn`. `SampleProject.cs:364` creates the Keyboard source; `VideoAndTargetPresentation.cs:242` shows it as `"always"`; `Dialogs.AddTriggerInput` offers MIDI/OSC/Schedule/Timecode only. No UI call site feeds key gestures into `Triggers`. |
| HAC-007 | Confirmed | `ShowHost.Triggers.cs:110-114` is `foreach (list in _project.CueLists) { GoAsync(list); break; }`. The UI path uses `CuesViewModel.ScopedList` (`GoCoreAsync:329`). The engine holds no active-list state — grep for `ActiveList`/`CurrentList` in `HaCue2.Engine` returns nothing. |
| HAC-008 | Confirmed | Neither test project references `ShowHost` at all (only the csproj’s project reference matches). `BackendName` has no HaCue2 consumer. Note: the production entry point is `ShowHost.StartAsync`, not `CreateAsync` as this document writes. |
| HAC-009 | Confirmed | All eight listed settings resolve to settings-view-model read/write plus an AXAML binding, with no runtime consumer. The live-apply hook `ShellViewModel.ApplyApplicationSettings` (`:482`, wired at `ShellWindow.axaml.cs:405`) applies only DoubleGoGuard, ConfirmStopAll and remote settings — `MachinePanicFadeMs` is assigned once at `ShellViewModel.cs:230`. `ProjectFiles.Create` seeds fade in/out, auto-renumber, click-standby and mix rate but **not** `StopFadeMs`. `WriteApp` (`:607`) discards `AppSettingsStore.Save`’s bool. `ParseSettingNumber` (`:101`) keeps ASCII digits only, so `0.25 s` → 25 ms. |
| HAC-010 | Confirmed | The VM label is `"(shared framework cache)"` (`:581`) while `MediaCache.RootFor` (`:30`) defaults to `StoragePaths.Root/cache`; YouTube uses the framework’s `MediaCachePaths`. `WaveformBudget`/`ThumbnailBudget` have no parser, accounting or eviction anywhere. `Clear("thumbnails")` targets a folder no HaCue2 code writes. |
| HAC-011 | Confirmed with nuance | See “Nuances” below. |
| HAC-012 | Confirmed | `ResetCounters` (`:1699`) is `ClearProblems()` + `Ring.Clear()` + `Refresh()`. `ProgramBusMeter.ResetClip` exists in the framework with no HaCue2 caller. |
| HAC-013 | Confirmed | Mesh editing is a point combo box plus dx/dy `NumericUpDown` and four nudge buttons (`VideoView.axaml:744-768`); mesh sizes are 2–16 as described. HaPlay’s `MappingEditorDialog.axaml.cs` has draggable `_meshHandles` with pointer capture and `WarpMeshTessellator.Evaluate` curve drawing, plus the persistent `MappingTestPattern`. HaCue2’s Identify is 4 s (`EngineRuntime.cs:105`) and draws a flat blue field, a name, and an edge border — the border is worth crediting, but there are no corner colours, section boundaries, warp grid or stay-on mode. |
| HAC-014 | Confirmed | `GroupFireMode` is `AllTogether/Playlist/Timeline` only. `PlayCount`, `EndTargetCueId`, `FeedAll`, `FeedCueIds` and `SendToVisualizer` exist in `UI/HaPlay/Models/CueList.cs` and have zero hits across HaCue2 Core/Engine/UI. |
| HAC-015 | Confirmed | 37 AXAML files, 20 under `Views/`, zero `AutomationProperties` occurrences. |
| HAC-016 | Confirmed with nuance | `ShellWindow.axaml:10` is `MinWidth="1080" MinHeight="700"`; the inspector column is `CuesView.axaml:132` `ColumnDefinitions="*,4,316"`. See “Nuances”. |
| HAC-017 | Confirmed | Cache root, log directory and recovery location are plain `TextBox` bindings to view-model fields with no `On…Changed` write-back — note the pane already uses `Classes="ro"` for the read-only “In use” field, so the read-only convention exists and simply was not applied. `FileLogProvider` reads `FileLogLevel`/`LogRetention` at construction and `CrashReports` is built conditionally at startup; unlike the audio-backend field, neither is marked restart-required. |
| HAC-018 | Not re-verified | Runtime observation. Statically, `AudioDevices` is constructed once (`HaCue2.Desktop/Program.cs:35`), so any repetition originates inside the native probe rather than in app-level re-enumeration. |
| HAC-019 | Split | Second bullet confirmed: `TessellateBenchmarks.cs:9-11` still describes per-frame re-tessellation while `GlVideoCompositor.DrawLayerMesh` (`:1907`) calls `GetOrCreateLayerMeshBuffers(mesh)`. First bullet is incorrect — see below. |

The reachability section was also re-checked: all 20 views under `Views/` have a real construction or
embedding site (`ClipEditorWindow` and `SubtitlePickerWindow` from `InspectorPane.axaml.cs`,
`TimelineWindow` from `TimelineSheet.axaml.cs`, `InspectorPane`/`ScopePane` inside `CuesView.axaml`).

## Correction

**HAC-019, first bullet, is wrong.** `TargetsViewModel.SendTestAsync` does *not* assign
`probe.Address = parts[0]` twice. There is exactly one such assignment (`TargetsViewModel.cs:555`),
preceded by the object initializer setting `Address` from `endpoint.TestMessage`. Splitting the field
and re-assigning the head is deliberate normalization, not duplication. There is nothing to clean up
here, and this item should be struck rather than scheduled.

## Nuances on otherwise-correct findings

**HAC-011 — the neutral state already exists in Core; the defect is one level up.**
`CheckOutcome.NotChecked` is implemented, `Summarise` (`ProjectStatus.cs:435`) returns it when every
output went unchecked, and the Project Status row renders “not checked” with a neutral gel
(`AuxiliaryViewModels.cs:1549,1558`). So the recommendation to “add an explicit neutral not-checked
token state” is already satisfied in the checker. The real fault is narrower and still real:
`ShellViewModel.OutputSummary` (`:873`) tests only for `Failed` and `Warning`, so a `NotChecked`
video-outputs row falls through to the green **“● outputs ok”** shell token. Fix the summary’s
tri-state handling rather than rebuilding the outcome enum. The composite-environment
recommendation stands unchanged: `MachineEnvironment.VideoOutput` returns `Unknown` unconditionally
(`:34`) and the `RuntimeEnvironment` that *does* know `AbsentVideoOutputs` is selected only when no
audio backend exists (`ShellViewModel.cs:61`).

**HAC-016 — the inspector is already resizable.** `CuesView.axaml:390` places a `GridSplitter` in the
4 px column and the panel carries `MinWidth="240"`, so 316 px is the starting width rather than a
fixed one. The collapsible pane, per-pane minimum widths, density modes and minimum-size screenshot
tests are all still outstanding.

**HAC-008 — method name.** The production composition root to cover is `ShowHost.StartAsync`
(two overloads, `ShowHost.cs:360` and `:372`), not `CreateAsync`.

## Effect on the release recommendation

None. HAC-001 through HAC-008 are each confirmed against current source, so the recommendation to
withhold “feature-complete” status until they are fixed and exercised through the real application
host stands as written.

---

# Implementation and verification update

**Implemented:** 2026-08-05  
**Decision record:** carry over the remaining useful HaPlay workflows, explicitly excluding the
experimental MMD cue; make YouTube part of HaCue2's real cache; use one `Esc` for Stop and a second
`Esc` within 700 ms for Panic.  
**Status:** every confirmed code finding in this audit has been remediated. The physical-rig acceptance
items listed below remain acceptance work, not known code defects.

This section supersedes the original release recommendation for the reviewed findings. It does not
erase the original evidence: the audit and independent pass above remain the before-state, while this
is the implementation and after-state.

## Remediation ledger

| ID | Status | Implemented result |
|---|---|---|
| HAC-001 | Fixed | Added a shared SDL3 desktop composition selector in `S.Media.Present.SDL3.Compositor`. HaCue2 and HaPlay now use the same OpenGL probe and explicit CPU fallback policy. `ShowHost.StartAsync` supplies the factory, and composition telemetry/Diagnostics reports the actual backend. GL provides warp mesh and surface-layer capabilities; CPU fallback reports their absence instead of silently claiming them. `HACUE2_COMPOSITOR` (falling back to `S_MEDIA_COMPOSITOR`) permits an explicit `cpu`, `gl`, or `gpu` policy for diagnosis. |
| HAC-002 | Fixed | HaCue2's production session now injects `SubtitleOverlayFactory.FromFileDeferred`. The integration test constructs a real subtitle overlay, and `SessionSmoke` rendered the subtitle layer successfully. |
| HAC-003 | Fixed | Replaced the divergent shell switch with one normalized command map. File/New/Open/Save/Save As, transport, preview, output drawer, undo and redo all route through persisted profiles. Per the final decision, plain `Esc` stops the selected/oldest active cue; a second plain `Esc` within 700 ms panics. The safety sequence is fixed and cannot be removed by an edited profile. |
| HAC-004 | Fixed | `OpenLocked` is applied when opening a project and drives the journal's read-only state. A single `CanAuthor` policy disables authoring surfaces without disabling runtime transport. Consolidate and relink refuse before filesystem side effects begin. Lock regression tests cover both operations. |
| HAC-005 | Fixed | Playlist pass count is evaluated before the terminal Hold/Next List policy. `PlayCount` limits items per pass independently of `LoopCount`; shuffle and boundary-repeat behavior are retained. Added the complete pass/end behavior matrix. |
| HAC-006 | Fixed | Keyboard sources are offered by the trigger-input dialog and receive normalized shell keyboard signals. Learn sees the same ingress. Trigger bindings can opt into firing while a text editor has focus; ordinary transport/editor typing guards remain in force. |
| HAC-007 | Fixed | The engine now owns an active cue-list id. Shell scope changes synchronize it, and external/remote GO resolves through the same current-list transport path as the button. A production-host integration test proves a second-list external GO does not fire the first list. |
| HAC-008 | Fixed | Added tests through the real `ShowHost.StartAsync` production root for compositor selection/fallback, subtitle factory construction and current-list external GO. Backend identity is now observable in composition runtime stats rather than existing only as an unused lower-level property. |
| HAC-009 | Fixed | Connected the previously inert appearance, meter, inspector, drawer, current-list and transport defaults; new projects now inherit the application Stop fade. Meter PPM/VU ballistics, peak hold and click/automatic clip reset are live. Settings use decimal-aware duration parsing, surface save failures, and apply live changes to a running host where safe. Restart-only logging/backend/cache-root changes are labelled as such. The obsolete timeline-dock setting was removed. |
| HAC-010 | Fixed | Introduced one truthful HaCue2 cache root for waveform peaks and prepared YouTube assets. Both have parsed byte budgets and LRU enforcement at their write paths; Settings measures actual disk use, clears the selected derived data, and reports bytes really freed. YouTube's authoring preparer and playback module share the same configured cache and default 20 GB budget. Phantom probe/thumbnail disk caches and controls were removed because those data are in-memory/drawn and have no disk producer. |
| HAC-011 | Fixed | The shell now preserves Core's neutral `NotChecked` outcome (`○ outputs not checked`) instead of falling through to green. Project checks combine machine discovery with live runtime output absence, so an opened output failure is not masked by a present audio backend. The launch screenshot exercised the neutral state. |
| HAC-012 | Fixed | Diagnostics now distinguishes clearing the problem/clip state from resetting runtime counters, and the actual program-bus clip latch is connected to the manual reset. The former misleading `RESET COUNTERS` label was removed. |
| HAC-013 | Fixed | Added a direct `WarpMeshCanvas` which draws the same Catmull-Rom surface as `WarpMeshTessellator`, supports draggable handles, pointer capture, arrow/Shift+arrow operation, exact numeric editing and one undo group per drag. Mapping calibration can remain on until explicitly cleared and survives a compatible document reload. Its pattern adds a 10×10 grid, coloured corners, overscan border, output name and authored source-section boundaries. |
| HAC-014 | Fixed to approved scope | Added First Cue Only and Armed List modes, independent items-per-pass, per-media end targets, and selective projectM feed by cue plus per-media opt-in. Armed List advances exactly one child per operator GO and owns child Follow/end-target behavior. Manual cue stop and Stop All/Panic clear sequence state so the next GO cannot deadlock on a missing natural-end callback. MMD remains deliberately excluded. |
| HAC-015 | Fixed in code; physical assistive-tech pass remains | Added stable automation names/help for primary shell, transport, active rows and custom patch/placement/curve/warp controls. The warp editor is keyboard operable and numeric alternatives remain available for custom canvases. Automated accessibility-tree and focusability tests cover the high-risk controls. A real screen-reader/high-contrast session is retained as physical acceptance below. |
| HAC-016 | Fixed | Lowered the shell's practical minimum to 900×640, made the inspector explicitly collapsible, and auto-collapses it below laptop width while preserving a manual restore. The existing splitter remains usable. Compact/normal/relaxed density and font-scale resources apply live; automated layout tests exercise the 960 px collapse boundary. |
| HAC-017 | Fixed | Cache and log paths persist immediately and report a failed save. Their active-session notes state when restart is required. Recovery remains a factual read-only location rather than a fake editable field. File log level/directory/retention and crash-report changes are labelled restart-only because their services are constructed at startup. |
| HAC-018 | Fixed | Added an optional combined audio-device snapshot capability. PortAudio holds one native discovery lifetime while collecting output and input lists, eliminating the duplicate initialization/probe cycle without suppressing genuine failures. PortAudio and audio-backend conformance suites pass. One expected ALSA diagnostic block remains on this Linux machine because ALSA itself probes unavailable compatibility PCMs. |
| HAC-019 | Resolved | Updated the tessellation benchmark commentary to match cached GL mesh buffers. The independent pass's correction stands: the claimed duplicate `TargetsViewModel` assignment never existed, so no source change was made for that false item. |

## Additional simplifications and hardening completed

- The shared compositor policy replaces two application-specific factory implementations rather than
  adding a third one.
- Hotkey profiles (`Cue standard` and `Laptop`) and overrides are real machine settings; the former WIP
  screen is now an editable binding table with conflict/reserved-safety feedback.
- Visualizer feed validation and project-reference tracking now catch deleted/selective targets before
  show time.
- HaCue2, HaPlay and HaViz now import one projectM deployment policy. IDE output and direct publishes
  carry the repository-patched 4.1.6 runtime with the pinned 552-preset and 68-texture packs under
  `External/projectm/<rid>`. The resolver deliberately prefers that application bundle over a system
  projectM, while retaining `MFP_PROJECTM_LIB` as the highest-priority diagnostic/development override.
  A Linux publish fails if this payload is absent instead of silently shipping an unpatched fallback.
  Blank HaCue visualizer packs now select the bundled pack rather than projectM's idle preset.
- Output calibration ownership lives in `ShowHost`, so output reload/replacement does not strand a
  test frame or silently discard a calibration session.
- Disabled status checks remain neutral; health summaries cannot convert “not examined” into “OK”.
- A stale benchmark statement, stale cache descriptions, and cache UI for nonexistent producers were
  removed during the final truthfulness pass.

## Verification after implementation

### Builds, tests and publish

| Check | Result |
|---|---|
| Clean `HaCue2.Desktop` Debug build | ✅ 0 warnings, 0 errors |
| `HaCue2.Core.Tests` | ✅ 686 passed |
| `HaCue2.Tests` | ✅ 317 passed |
| `S.Media.Session.Tests` | ✅ 349 passed |
| `S.Media.Compositor.Tests` | ✅ 54 passed |
| `S.Media.Players.Tests` | ✅ 16 passed, 1 intentional skip |
| `S.Media.Core.Tests` | ✅ 781 passed, 2 intentional skips |
| `S.Media.PortAudio.Tests` | ✅ 21 passed |
| `S.Media.Audio.Backends.Tests` | ✅ 45 passed |
| `S.Media.Source.YouTube.Tests` | ✅ 34 passed, 2 opt-in live-network skips |
| `S.Media.Visualizer.ProjectM.Tests` | ✅ 19 passed |
| `S.Media.Arch.Tests` | ✅ 30 passed |
| **Extended total** | ✅ **2,352 passed, 5 skipped** |
| Linux x64 NativeAOT publish | ✅ native-code generation and link completed |
| JIT desktop launch, isolated data root | ✅ start screen opened and remained responsive |
| NativeAOT desktop launch, isolated data root | ✅ native ELF start screen opened and remained responsive |

The RID-specific NativeAOT graph required an explicit `linux-x64` restore before the no-restore
publish. The default graph was restored afterward, and the final no-restore Debug build and both full
HaCue2 suites passed again. This avoids a false success/failure caused by switching Debug, Release and
AOT asset graphs.

### Real GL/session/media qualification

| Exercise | Result |
|---|---|
| `CompositorSmoke` | ✅ SDL/OpenGL composite, pipeline and readback correct |
| `ProjectMGlSmoke`, app-bundle resolution | ✅ patched projectM 4.1.6 loaded from the deployed HaCue layout without an environment override; 60/60 exact compositor-path frames rendered, final frame 60,000 lit pixels (26.0%) |
| `ProjectMGlSmoke`, pinned full pack | ✅ patched projectM 4.1.6 loaded from HaCue2's Debug output; 120/120 frames rendered through the 552-preset/68-texture bundle, final frame 29,376 lit pixels (12.8%); captured frame inspected and visibly non-black |
| `shootingstar_0611_1.mov` direct playback | ✅ ProRes 3840×2106, 10-bit 4:2:2, 60 fps; 602 frames presented through 10.150 s |
| `おねがいダーリン_0611.mov` direct playback | ✅ ProRes 3840×2106, 10-bit 4:2:2, 60 fps; 603 frames presented through 10.150 s |
| Two-output headless fan-out, `shootingstar_0611_1.mov` | ✅ 601 frames; 0 late drops, 0 misses; skew median 0.01 ms, p95 0.04 ms, max 0.68 ms |
| `SessionSmoke` with both 4K60 sources | ✅ two cues/layers, subtitle rendering, live placement, seek, pause/resume, trim, loop and fade all completed |
| Session steady-state allocation | ✅ 132 KiB over 1.5 s (88 KiB/s), 0 Gen0 collections |

The desktop and NativeAOT launches were captured from the active window. The JIT capture also visibly
confirmed the corrected neutral machine-video state (`not checked yet`) on a fresh isolated profile.

## Remaining acceptance boundaries

No known code finding from this audit is left open. The following still require the relevant physical
equipment or human assistive-technology session and should remain on the release acceptance checklist:

- projector/LED-processor final-glass inspection, EDID changes and multi-screen hot-plug;
- real multichannel audio interfaces, MIDI devices, OSC peer, LTC/MTC and NDI endpoints;
- live output loss/reacquisition while a calibrated multi-output show is running;
- screen-reader, high-contrast and large-text use by a human operator; and
- a complete dress-rehearsal pass on the packaged target-machine artifact.

Subject to those rig checks, the original HAC-001-through-HAC-008 release block is cleared by the
implementation and production-root verification above.

---

# YouTube background preparation and cache recovery follow-up

**Implemented:** 2026-08-05
**Status:** complete

YouTube cue authoring no longer holds its dialog open for the full download/remux. After the manifest
and stream selection are resolved, HaCue2 writes the cue immediately with a concrete, portable source
URI, closes the authoring window, and hands preparation to one application-wide background queue. The
queue is deliberately serialized by default to limit network, disk and remux contention while a show
is otherwise in use. Duplicate requests coalesce, progress remains visible in the shell status bar,
failures remain retryable, and application shutdown cancels outstanding work without exposing partial
assets to playback. GO retains the reliable-mode contract: it never downloads and opens only an
atomically committed local asset.

Project Status now has a machine-local **YouTube cache** check. A cue is reported as ready,
downloading, missing, failed or not checked; missing/downloading/failed payloads remain preflight
errors because those cues cannot fire yet. **Download missing** queues every non-ready source and
continues after the Project Status window closes. This covers a cache cleared between launches and a
project copied from another machine. Old unpinned “best” source URIs are rewritten through the project
journal to the concrete streams returned by the repair, preserving deterministic offline playback and
undo. Clearing the YouTube cache from Settings notifies the open shell immediately and is refused while
a background writer is active.

Generated YouTube captions are now machine-derived at compile time from the portable source URI.
Absolute caption paths left by older projects are neither relinked nor consolidated and cannot make a
moved project point back at another machine's cache. This exception is limited to URIs that explicitly
request a YouTube caption language; a normal user-added external subtitle sidecar on a YouTube cue
continues to participate in media operations and playback.

## Follow-up verification

| Check | Result |
|---|---|
| `HaCue2.Desktop` Debug build | ✅ 0 warnings, 0 errors |
| `HaCue2.Core.Tests` | ✅ 693 passed |
| `HaCue2.Tests` | ✅ 319 passed |
| `S.Media.Source.YouTube.Tests` | ✅ 37 passed, 2 opt-in live-network skips |
| Updated extended total | ✅ **2,364 passed, 5 skipped** |
| Isolated JIT desktop launch | ✅ sample shell remained running for the smoke interval |

The added regressions prove that cue creation returns while the transfer is still running, identical
requests share one job, deleting a committed `.mkv` changes readiness to missing and creates a new
repair task, persistent stream legs are reused for the repair, every cache state reaches Project
Status with the correct severity, the Project Status fix repairs and journal-pins a moved cue, and a
prepared caption path comes from this machine rather than an old serialized cache location.
