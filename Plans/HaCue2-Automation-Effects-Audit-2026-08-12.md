# HaCue2 automation + effects - implementation audit

Date: 2026-08-12

**Status: fix pass applied the same day - see the Fix log at the end.** Every blocker and every High
finding below is now fixed with a regression test; the mediums listed as fixed there are done too. Items
not in the Fix log are still open.

Audited: `Plans/HaCue2-Animatable-Properties-And-Automation-Lanes.md` against `next-experiment`
at `aad6c1a3` **plus the uncommitted working tree** (the tree is part of the feature).

## Bottom line

The architecture landed and it is the right architecture. Property-targeted absolute-time tracks,
a stable-ID keyframe model, an explicit AOT-safe catalog, schema-3 gating, the effect rack with
stable instance identity, and an append-only native ABI are all genuinely implemented, not stubbed.
1,590 tests pass and the whole solution builds clean.

The defects are not architectural. They are **seams**: places where two correct subsystems meet and
one of them forgot the other's contract. Four of them are ship-stopping, and all four are invisible
to the current test suite.

## Verification performed

| Check | Result |
|---|---|
| `MFPlayer.sln` build | succeeded, 0 errors |
| HaCue2.Core.Tests | 777 / 777 |
| HaCue2.Tests (headless UI) | 407 / 407 |
| S.Media.Session.Tests | 398 / 398 |
| S.Abi.Tests | 8 / 8 |
| `AbiSmoke` native plugin smoke | exit 0 - registers `test.gain`, `test.gain.legacy`, `test.invert` |

The doc's claim that the suites pass and the native smoke runs is accurate.

## State of the code - read this before fixing anything

The feature is split across committed and uncommitted work. `37174447` → `9020312a` → `aad6c1a3`
landed phases 1–3 and the first rack pass (~8,900 lines). **~1,840 lines remain uncommitted**,
including three untracked, load-bearing files:

- `UI/HaCue2.Core/Model/LayerEffects.cs` - the effect rack model itself
- `UI/HaCue2.Core.Tests/LayerEffectRackTests.cs`
- `MediaFramework/Test/S.Media.Session.Tests/LayerEffectRackTests.cs`

plus uncommitted native-ABI changes (`mfp_plugin.h`, `NativeAudioBusEffect.cs`, `AbiNative.cs`) and
~970 lines of framework session changes. **The phase-4 rack exists nowhere in git history.** Commit
before starting the fixes below.

---

## Blockers

### B1 - the live volume fader is dead on every playing cue

`ShowCompiler.Envelope` (`UI/HaCue2.Core/Compile/ShowCompiler.cs:861`) always emits at least one
point - the static `LevelDb` - when a cue has no track. So `hasEnvelope`
(`MediaFramework/Media/S.Media.Session/ShowSession.cs:1638`) is true for *every* media cue with
routes, and `StartEnvelopeRunner` (`ShowSession.Levels.cs:75-97`) runs forever, writing
`Level.Envelope` every 25 ms via `voice.ApplyEnvelopeLevel(...)`.

`ApplyActiveVolumeAsync` (`ShowSession.LiveEdits.cs:163-170`) writes **the same slot**, with no
owner/claim guard - unlike `ApplyControllerEnvelope` (`ShowSession.TransportVoices.cs:490-506`),
which is claim-guarded.

Drag the inspector volume fader on a playing cue: the value applies, then the next runner tick
(≤25 ms) writes the compiled static level back. The fader does nothing. A reload can't rescue it
either - changing `LevelDb` changes `VolumeEnvelope`, so `SameClipBinding` (`ShowSession.cs:848`)
fails and the cue would restart.

This is the doc's own §"One authority" rule ("a live base edit cannot race a timeline sampler
writing the same field") unmet for `cue.audio.volume`. The video side gets it right -
`ClipCompositionRuntime.cs:3370` writes only `_level.Base`.

**Fix direction:** give the authored base its own slot on the audio side, exactly as video does, and
let the envelope runner own only the automation slot.

### B2 - one gain insert re-arms the silent-voice wedge regression

`AudioEffectOutput.Wrap` (`MediaFramework/Media/S.Media.Routing/AudioEffects.cs:55-72`) forwards
exactly `IClockedOutput` / `IPlaybackClock` / `IAudioOutputPlaybackStats`. `ProgramBusProducer`
(`Audio/ProgramBusSource.cs:309-317`) implements eight faces, including `IGrantPacedOutput`,
`IPreRollableOutput`, `IPipelineLeadClock`, `IFlushableOutput` and `IAudioOutputLatency`.

`ShowSession.cs:518-519` wraps the producer and hands the **wrapper** to `router.AddOutput`:

```csharp
output = ApplyAudioEffects(output, audioEffects);
router.AddOutput(output.Output, outputId);
```

Consequences:

- `AudioRouter.OutputPump.cs:93` - `output as IGrantPacedOutput` is now `null`, so the drop funnels
  at `:135` and `:243` never return pacing credit. The interface's own doc
  (`ProgramBusSource.cs:28-35`) names the outcome verbatim: *"the credit for that chunk leaks and the
  producer eventually refuses every further grant (the HaCue2 silent-voice regression: 8 leaked
  chunks wedge the voice, its router times out and faults)."*
- `VoiceStartPolicy.cs:152-156` - `is IPreRollableOutput` fails, so group-fire falls back to
  `PlainStart` and loses sibling-alignment pre-roll (0–180 ms of start scatter between stems).

Not an edge case: `ShowCompiler.cs:353` always emits `LogicalSends`, and HaCue2's `ShowHost.cs:481`
always sets `_programAudio`. This is the repo's known wrapper-forwarding bug class, and it also drops
`IAudioOutputLatency`, which the 2026-07-30 review made unconditional on decorators.

**Fix direction:** the wrapper must forward every face the inner output implements, not an enumerated
subset. Prefer a forwarding scheme that fails closed (assert on unknown faces) over another
hand-maintained list.

### B3 - legacy group-lane migration produces duplicate track IDs and bricks the project

`AutomationMigration.cs:184` sets `Id = tracks.Count == 0 ? lane.Id : Guid.NewGuid()`, while `Merge`
(`:310-316`) pushes the *same* `EffectLane` object into every descendant. Each child's first track
therefore reuses the group lane's Guid. `ProjectValidator.cs:63-67` registers track IDs in one
project-wide identity map, yielding `The id … is shared by a automationTrack and a automationTrack`
and `IsRunnable == false` (`:144`).

Reproduced: a group with one volume lane and two media children (both with `SourceDurationMs`)
migrates to two tracks sharing the lane's ID; the project will not run. This directly fails the doc's
"Legacy group precedence … survive migration" acceptance test.

### B4 - schema-1 volume migration changes what the show sounds like

`AutomationMigration.VolumeAt` (`:271-276`) maps `Y=0 → SilenceFloorDb (-60 dB)` and `Y=1 → baseDb`.
`FadeRamp.cs:158-168` interpolates `from.Level → to.Level` and only *then* converts dB→linear via
`Math.Pow(10, value/20)`.

A legacy `(0,0)→(1,1)` fade-in on a 0 dB cue:

| | midpoint value |
|---|---|
| schema 1 (linear factor interpolation) | **0.5** (−6 dB) |
| now (dB interpolation) | **0.0316** (−30 dB) |

A 24 dB error mid-fade, worst on fades from silence. Endpoints match exactly, which is why
`AutomationTests.cs:141-166` - which pins the *new* dB midpoint and never compares it to the legacy
value - passes.

**This is not a simple bug. It is two of the doc's own decisions colliding.** The correction pass
deliberately chose "compiled volume envelopes remain in dB until after interpolation", which is right
for new authoring. The migration acceptance criterion demands schema-1 fixtures sample identically
"at start, every key, **every segment midpoint**, and end". Both cannot hold for legacy lanes.

**Decision required - pick one:**

1. tag *migrated legacy* volume envelopes `ShowEnvelopeValueScale.Linear` so they keep their original
   shape, and let only newly authored tracks use dB; or
2. densify legacy segments with intermediate keys approximating the linear-domain curve; or
3. accept the change (fades from silence become perceptually smoother) and amend the acceptance
   criterion in the plan doc.

Option 1 is the smallest and most faithful. **Opacity is unaffected** - `FadeCurves.Interpolate` is
affine in value and opacity envelopes stay `Linear`, so per-placement migration does preserve
midpoints.

---

## High severity

- **H1 - Bézier migration emits duplicate keyframe times.** `AutomationMigration.cs:237-255` always
  subdivides into 32 steps and `At()` (`:268`) rounds to whole ms, so any segment under ~32 ms
  collapses. On a 400 ms cue this yields 12 × `duplicate or out-of-order times` validator Errors →
  not runnable. `ShowCompiler.cs:878` also silently drops those points.
- **H2 - a seek does not seed automated values.** `ShowSession.Transport.cs:291-306` seeks and
  immediately `player.Play()`; nothing re-samples the volume/opacity/transform/effect envelopes. The
  25 ms runners are the only correctors, so scrubbing across a long fade resumes at the pre-seek
  value for up to 25 ms (or a full-level burst the other way). The *fire* path seeds correctly
  (`ShowSession.cs:1408-1496`), so rehearsal is fine - only seek is missing it. Contradicts the
  Timing contract's "a seek seeds every automated value at the target time before audio/video becomes
  visible", and no test covers it.
- **H3 - a visualizer cue runs on two independent clocks.** `ShowHost.Execution.cs:349` creates clock
  A for the visualizer automation run; `:1060` creates a separate clock B for the same cue's outbound
  run. `SeekVisualizerAutomationRunAsync` (`:455-469`) seeks A, but `OutboundEffects.DriveAsync`
  (`:177-189`) then reads B's snapshot, sees a generation mismatch, and repositions back to B's
  un-sought position. The seek is reverted within 10 ms and the desk tracks the original timeline.
  Automation cues are correct here (`:151`, `:171` share one clock instance).
- **H4 - outbound completion lands on a key the show never reached.** `OutboundEffects.StartAsync`
  uses `duration` only as a `<= Zero` guard (`:42`); the runner's end is `points[^1].Time`
  (`OutboundRampRunner.cs:89`), and `Complete` emits `FinalValue = _points[^1].Value` (`:211`). A
  1,000 ms automation cue whose OSC track is keyed 0→0.0 and 5000→1.0 sends 0.0…0.2, then slams the
  desk to **1.0** on completion. A lighting/machinery surprise the doc explicitly wants avoided.
- **H5 - copy/paste re-introduces the exact defect the redesign was built to kill.**
  `ToClipboardKnot`/`FromClipboardKnot` (`AutomationEditorViewModel.cs:729-747`) normalize
  `TimeMs / DurationMs` on copy and multiply by the *destination's* `DurationMs` on paste. Copying
  keys spanning 500 ms out of a 45-minute cue into a 30-second cue collapses them to ~5.5 ms. Values
  renormalize through descriptor min/max too, so a dB→% paste maps proportionally. `Math.Clamp(…,0,1)`
  on X also collapses any key past `DurationMs` onto the cue end.
- **H6 - a refused keyframe add hijacks the previous selection.** `CurveCanvas.axaml.cs:197-213` sets
  the `_draggedIndex = int.MaxValue` sentinel *before* knowing whether the VM accepted the add;
  `AutomationEditorViewModel.cs:324` refuses via
  `keys.All(key => Math.Abs(key.TimeMs - timeMs) > 4)` and falls to `default: return` without touching
  selection. The next motion resolves the sentinel through `SelectedIndex()` - still the *old*
  selection - and drags that key to the cursor.

  More reachable than it looks: default snap is **100 ms** (`:201`), and editor-added keys are already
  grid-aligned, so pressing anywhere in the same snap cell gives `timeMs == key.TimeMs` exactly. The
  refusal band is the whole snap cell, not ±4 ms. At the high zoom this editor exists to support
  (~3 s viewport ≈ 33 px per cell) it is an easy target.

---

## Medium severity

**Audio effects**

- Built-in gain's "smoothing duration" is a fixed slew *rate*, not a duration
  (`AudioEffects.cs:288-292`): actual transition is `|Δlinear| × seconds`, so a 10 ms request for
  −6→−3 dB finishes in ~2 ms while 0→+12 dB takes ~30 ms - longer than the 25 ms runner interval.
- Every gain insert opens at unity and slews down (`:212-232`, `:266-271`): `_currentLinear` stays
  `1f` and `Configure` never snaps current→target, so a cue authored at −20 dB emits ~9 ms of
  near-unity audio at the head of every fire.
- `Clamp` turns mute into unity: `EffectParameterDescriptor.Clamp` (`:30-32`) returns `Default` for
  non-finite input, and gain's default is `0` dB - so `TrySetParameter("gainDb", −inf)` yields **full
  gain**, contradicting `AudioEffects.cs:238`. Session paths pre-filter, but this is a public contract
  failing in the wrong direction for audio.
- Static authored parameters never reach a native plugin via `set_parameter` - they are merged as
  top-level JSON keys into the opaque config blob (`ShowSession.cs:322-355`), assuming the config
  namespace equals the parameter-ID namespace, which the header explicitly disclaims. The repo's own
  fixture proves the hole (`test_plugin.c:349-352` ignores `config_json`). Only *automated* parameters
  survive.
- One bad descriptor permanently pins a plugin library: `AbiPluginHost.BindAudioEffects`
  (`:301-309`) acquires leases in a loop with no try/catch, and `NativeAudioEffectFactory`'s ctor can
  now throw (`NativeAudioBusEffect.cs:32-42`), abandoning leases `0..N-1` so `TryUnload` never fires.
  HaPlay swallows the exception (`MediaRuntime.cs:135-141`), so all of that plugin's effects silently
  vanish.

**Editor**

- The inline timeline lane did not come along for the ride. `TimelineSheet.axaml:210-216` omits
  `LogicalNudges` (so arrows still nudge 1 % of the cue), journals a whole-list write per pointer
  motion, leaves `RemoveWhenDraggedOffCanvas` at its `true` default, and never wires
  `GestureCancelled` (so Escape commits). Causes 1, 5 and 6 are fixed in the new window and still live
  on that surface. Its persistent selection is also still index-based
  (`CuesViewModel.cs:2174, 2539-2546`).
- Delete/Ctrl+C/V during a held drag desynchronizes canvas and view-model
  (`CurveCanvas.axaml.cs:341-372` never clears `_draggedIndex`), so the next motion grabs whichever
  key now occupies the stale index and journals a second undo step.
- A no-op gesture still journals an undo step and dirties the project (`EndGesture` `:381-390` writes
  unconditionally; `ProjectJournal.Do` has no before/after equality check).
- Ruler labels are drawn a fifth of the viewport left of the time they name - 5 labels at
  `index/4 × ViewLength` laid out in a 5-cell `UniformGrid`
  (`AutomationEditorViewModel.cs:807-812` vs `AutomationEditorWindow.axaml:114-118`); the ruler also
  spans the outer grid column while the plot is inset by border + padding + `Margin="6"`.

**Runtime**

- Controller release falls back to `0` rather than the descriptor default
  (`ShowSession.TransportVoices.cs:680-683`) - harmless for `gainDb`, wrong for any descriptor whose
  default is non-zero.
- A seek emits one stale outbound value and de-syncs the generation counter
  (`OutboundEffects.cs:177` reads the clock outside `run.Gate`, `:181` compares inside).
- Seeking an automation cue can throw `ObjectDisposedException` (`ShowHost.Execution.cs:471-488`
  touches `run.Cancellation.Token` after releasing `_automationRunGate`, while `DriveAutomationAsync`'s
  `finally` disposes it outside the lock).

---

## Design decisions needed (not bugs)

1. **B4's dB-vs-linear migration domain** - see above.
2. **`HoldFinal` claims are unreleasable.** After a `HoldFinal` automation cue ends, its owner ID
   stays in `_controllerOwners` (`TransportVoices.cs:688-693`) with `ControllerEnvelope` non-null.
   The cue-owned envelope *and* any live inspector edit are permanently shadowed, with no verb to
   release the claim short of stopping the cue, and no operator-visible indication that a dead
   controller owns the value.
3. **Nested group trims overwrite rather than compose.** `GroupAudioTrim`/`GroupVideoOpacity` write a
   single `Level.Modifier` / `Slot.ModifierLevel` under a fixed key (`"group:audio"` / `"group:video"`,
   `TransportVoices.cs:532,548`), while `Descendants` recurses (`ShowHost.Execution.cs:815-824`). An
   outer and an inner group trim target the same voice; latest-owner wins and one is discarded. Safe
   (no double-application) but not the additive/multiplicative semantics the property table describes.
4. **Older controller runs are permanently silenced by a newer one's release.** Run A claims, B
   preempts, B ends with `RestoreBase` → `ReleaseController` removes the key entirely
   (`:695-701`), so A's later writes all fail while A is still running. Reveal-on-release reveals the
   cue-owned value, never the next-latest controller.

---

## Confirmed correct

Worth recording, because a lot of hard things are right:

**Model / migration.** Schema-3 gating with closed-above refusal, implemented and tested
(`HaCueProject.cs:35`, `HaCueProjectFile.cs:211-214`, `ProjectFileTests.cs:361-371`). Unknown property
IDs load inert, warn as runnable, surface in Project Status, and survive a serialize→deserialize round
trip. Migration is idempotent and double-migration safe via the `SameTarget` guard, and a
hand-authored track correctly beats a legacy lane (doc rule 8). Unknown-duration lanes are retained,
not guessed. The `LevelDb + 20*log10(Y)` formula and per-placement `placement.Opacity * Y` conversion
are exactly as specified. Outbound endpoint/address/per-segment curve data survive, and migrated
OSC/MIDI tracks get `Interruption = LandFinal` to preserve schema-1 stop behaviour.

**dB-domain claim is true.** Only the volume envelope is tagged `Decibels`
(`ShowCompiler.cs:700`); `VolumeEnvelopes.Sample` interpolates stored dB and converts afterwards. No
volume path interpolates in linear-factor space. Opacity/transform/effect envelopes correctly stay
`Linear`.

**Evaluator semantics.** Hold-before-first / hold-after-last, true step on `Hold`, empty → authored,
one-key → constant, disabled → authored, descriptor clamping, deterministic `.ThenBy(key.Id)` order.

**Single-authority composition.** `Effective = Source × Fade × (ControllerEnvelope ?? Envelope) ×
Modifier × Master`, written in exactly one place (`TransportVoices.cs:306-323`). Automation-cue volume
*shadows* rather than overwrites, so releasing a claim reveals the cue-owned value. `LevelDb` appears
once. Every ramp captures `Level.Fade`, never `Effective`, which is what keeps master trim from
double-applying. Video mirrors it exactly (`ClipCompositionRuntime.cs:2540-2550`).

**One clock.** No `PeriodicTimer`, no private wall-clock *timebase* for positions. `AutomationRunClock`
is pure anchored arithmetic; pause is recorded at the transition, not sampled. Rehearsal offsets are
honoured on every entry point. `FadeRamp`'s internal `Stopwatch` is a step cadence only.
(Two residual seams: a `Stopwatch` fallback at `OutboundEffects.cs:83,87` that no production caller
reaches, and a wall-time `Task.Delay(10ms)` drive loop at `:193` that blocks virtual-clock testing.)

**Automation-cue lifetime.** Targets captured at fire; missing and non-sounding targets each reported
as no-ops; refire is a clean handover (previous run cancelled *and awaited*, including its
`RestoreBase` and outbound interrupt, before the new run starts).

**Outbound.** Rate limiting with genuine latest-only coalescing - one in-flight send, one newest
pending value, intermediates overwritten, no backlog possible; a failed terminal send is retained for
retry without overwriting a newer offer. **The "CurveToNext replaced with linear" defect is fixed**
(`OutboundEffects.cs:272`, `OutboundRampRunner.cs:239`), holds become step pairs, Béziers expand to 32
samples. `Freeze` is the default and panic ordering is correct.

**Native ABI is genuinely append-only.** Compiling old and new headers side by side, every pre-existing
field offset is byte-identical; only totals grew (`MfpAudioEffectVTable` 32→40,
`MfpAudioEffectFactoryVTable` 32→48). Short-vtable detection (`AbiPluginHost.NormalizeTable<T>`,
`:578-591`) validates `struct_size >= RequiredSize<T>()`, allocates zeroed, and copies
`min(sizeof(T), header->StructSize)` - so a legacy plugin's new trailing slots read as NULL and every
later read goes against the host-owned copy. Crucially, the *nested* `MfpAudioEffectVTable` is now
normalized before `Process`/`SetParameter` are dereferenced (`:490-492`); previously it was used raw.
Both gcc fixtures pin the pre-extension sizes via `offsetof` and pass.

**Marshalling.** Verified against gcc: `MfpEffectParameterDescriptor` size 56 with offsets
8/16/24/32/36/40/44/48, matching the managed `[Sequential]` mirror exactly. Descriptor strings copied
immediately with `PtrToStringUTF8`; outbound buffers explicitly NUL-terminated. All host callbacks are
`[UnmanagedCallersOnly]` statics taken via `&Method`, so nothing is GC-movable and no pinning is
needed. NativeAOT-clean under the repo-wide `IsAotCompatible=true`.

**Real-time safety.** Nothing samples automation on an audio callback. `GainAudioEffect.Process` is
allocation- and lock-free; `TrySetParameter` is reached only from the session dispatcher.

**Effect rack.** Instance IDs survive legacy migration unchanged, rack order is preserved into
`ShowVideoPlacement.Effects`, `ConfigJson` is retained for unknown plugin types, and duplication
renews every ID while retargeting tracks by old→new map, including cross-cue `Target.CueId` inside a
copied subtree. Authoring UI (ordered rows, add/remove/reorder/bypass) is wired to stable `Guid`s, and
removing an effect deletes its automation tracks in one composite.

**Phase 5 claim holds.** The only remaining `new EffectLane` / `new LanePoint` sites are
compatibility-only and exercised solely by their own tests. `InspectorViewModel.EffectLanes` is the
naming leftover the doc already lists as remaining cleanup - it projects `AutomationTrack`.

**Test honesty.** The known headless `Dispatch(async () => …)` vacuous-pass trap is documented and
avoided; no `async` lambda appears in the automation UI tests, and assertions check the *document*,
not the view-model echo.

**Phase-4 "Remaining" bullet is accurate.** `ShowHost.BuildEffectRegistry()`
(`UI/HaCue2.Engine/ShowHost.cs:573-587`) registers three built-in managed effects and nothing else;
grepping HaCue2 for `MediaPluginDirectory` / `AbiPluginHost` returns zero hits.

---

## Gaps the doc over-claims

- **Descriptor-without-instance is audio-only.** There is no descriptor overload for
  `AddLayerEffect` / `AddVideoEffect` / `AddVisualSource` / `AddGeometryEffect`
  (`BusRegistry.cs:41-58`); video authoring metadata still requires constructing an instance
  (`ClipCompositionRuntime.cs:3391`). The doc's unqualified claim is true for half the subsystem.
- **The new descriptor API has no production consumer** - `AudioEffectDescriptors` /
  `TryGetAudioEffectDescriptor` are referenced only from tests. Consistent with the "Remaining"
  bullet, but it is currently dead API.
- **The video-effect nested vtable is still not normalized** (`AbiPluginHost.cs:497-503` reads
  `videoEffect->EffectVTable->Process` straight from plugin memory). Harmless today because that
  vtable was not extended - and now the only asymmetric case, so it will be the next short-struct read
  the moment it gains a field.
- **The inspector never shows base vs automated value.** The doc requires
  `Base -6.0 dB · automated now -12.0 dB`; nothing renders it, and the static Level/Opacity boxes look
  authoritative while a track overrides them. No per-property "automate" diamond either - automation is
  reachable only through a separate tab. `EffectLaneRow.Detail` still reads *"empty - double-click the
  editor to add a key"*, naming the gesture the redesign replaced.
- **Out-of-range keys are preserved but unreachable and invisible.** `ViewMaxStartMs = DurationMs −
  ViewLengthMs` and `CursorMs` clamps to `DurationMs`, so nothing past the out-point can be scrolled
  to or selected; there is no indicator anywhere in HaCue2, and the group timeline silently clamps them
  to X=1. The doc requires them shown with explicit delete/rebase commands.
- **Accessibility is thin.** The lane itself is named, but none of the 11 toolbar/footer buttons or the
  effect-rack `↑ ↓ × ON/OFF` buttons carry `AutomationProperties.Name`; there is no keyboard binding
  for add-keyframe-at-playhead; Ctrl/Shift multi-select has no keyboard equivalent; `HoldToggled` is
  unwired in `AutomationEditorWindow`. No test touches the automation editor for accessibility.
- **The playhead cannot be typed** and no playhead line is drawn on the canvas - the doc's "scrubbed
  and typed" is half-met.
- **A second evaluator exists.** `DuckMath.Sample` (`DuckMath.cs:150-180`) shapes segments with
  `Curve.Law` only, ignoring `PresetId` and inline `Points`, so ducking under a Bézier segment reads a
  different level than `AutomationEvaluator` would. Contradicts "all actuators consume the same pure
  evaluator".
- **Stable placement IDs do not survive lowering.** `ShowPlacementEnvelope` and friends address a
  placement by `(CompositionId, LayerIndex)` (`ShowCompiler.cs:717-719, 750-753, 791-795`), not by
  `LayerPlacement.Id`, and nothing rejects two placements sharing composition+layer - so the doc's
  "one placement's opacity track does not alter another placement of the same decoded cue" is
  guaranteed only at the document layer.
- **Preset delete-safety misses automation keyframes.** `ProjectReferences.CurvesOf`
  (`:214-222`) covers fade/crossfade/patch curves only, so deleting a preset used by a keyframe reports
  zero references and then raises a validator Error after the fact.
- **Chroma `spill` default disagrees in three places** - `0` in `ChromaKeyVideoEffect.cs:48` vs `0.1`
  in `Cues.cs:700`, `Automation.cs:180-182` and `ShowCompiler.cs:604`. A rack-created chroma differs
  from a legacy-migrated one.
- **Migration side effects on every load and snapshot.** `Migrate` stamps `SchemaVersion = 3`
  unconditionally even when lanes remain unresolved, and `ProjectSnapshot.Copy` routes through
  `Deserialize`, so every runtime snapshot re-runs a full migration pass with `durations = null`.

---

## Performance notes

Not defects, but the wrong altitude for paths that run at 40 Hz per track:

- `AutomationEvaluator.Sample` (`Automation.cs:460-464`) runs `Where().OrderBy().ThenBy().ToList()`
  over the whole keyframe list on **every** sample - a 45-minute cue's thousands of keys re-sorted
  40×/s. It also calls `from.Curve.Resolve(project)`, which linearly scans `CurvePresets` and
  constructs a fresh `CustomFadeCurve` (re-validating every point) per sample, and throws/catches an
  `ArgumentException` per sample on a malformed inline curve. Contrast the session-side path, which is
  correct: pre-lowered, pre-sorted points with allocation-free binary search (`FadeRamp.cs:131-160`).
- `SampleAutomationAsync` calls `_project.FindCue` per track per tick - a full document flatten plus
  linear scan (`HaCueProject.cs:84,90`).
- Effect-parameter automation round-trips JSON 40×/s: each tick rebuilds the whole layer effect chain,
  allocating an `ArrayBufferWriter` + `Utf8JsonWriter`, parsing the config JSON, then re-parsing it in
  each built-in effect - or reconstructing a registry plugin instance
  (`ClipCompositionRuntime.cs:3232-3276`, `:3420-3454`).
- `NativeAudioBusEffect.TrySetParameter` allocates per call (LINQ closure + string concat + `byte[]`)
  where the managed equivalent allocates nothing.

---

## Suggested order of work

1. **Commit the working tree.** The rack exists nowhere in git.
2. B1, B2 - both are live-show failures, both are small, contained fixes.
3. B3, H1 - migration correctness; both make a migrated project refuse to run.
4. B4 - decide the domain question, then implement.
5. H2, H3, H4 - timing seams.
6. H5, H6 - editor correctness.
7. Add the tests that would have caught B1–B4 and H2–H4. **Every blocker above is invisible to the
   current 1,590-test suite**, which is the most useful signal in this audit: the suite tests the
   units well and the seams between them barely at all. In particular, no test drives real pointer or
   key events through `CurveCanvas`, which is exactly why H6 survived.

---

# Fix log - 2026-08-12

All four blockers, all six High findings, and five Mediums are fixed. Each carries a regression test.

## Blockers

**B1 - live volume fader.** Cue volume now lowers to two slots instead of one. `ShowClipBinding.VolumeDb`
carries the authored level; `VolumeEnvelope` is emitted **only** when a volume track exists
(`ShowCompiler.cs`), so an un-automated cue arms no envelope runner at all. `SoundingLevel` gained a
`Base` slot and `Envelope` became nullable, composing as
`Source × Fade × (ControllerEnvelope ?? Envelope ?? Base) × Modifier × Master` - replace-authored, matching
the descriptor. `ApplyActiveVolumeAsync` writes `Base` via the new `ApplyBaseLevel`; the runner still owns
`Envelope`. `ClipAudioLevels.BaseLevel` exposes the authored value so a host can show what automation is
shadowing. Tests: `AnUnautomatedCueLowersItsLevelToTheAuthoredSlotAndArmsNoEnvelopeRunner`,
`AnAutomatedCueStillCarriesItsAuthoredLevelBesideTheEnvelope`,
`ALiveVolumeEditIsNotRevertedByTheEnvelopeRunner`, `AutomationShadowsTheAuthoredLevelWithoutDestroyingIt`.

**B2 - dropped capability faces.** Rather than extend a subclass matrix that cannot scale to six faces,
capability lookup now sees *through* wrappers: new `IAudioOutputDecorator` (declares the inner sink) and
`AudioOutputCapabilities.Find<T>` (walks the chain, cycle-bounded). `AudioEffectOutput`,
`ResamplingAudioOutput` and `AdaptiveRateAudioOutput` declare it; `AudioRouter.OutputPump` and
`VoiceStartPolicy` resolve through it. This closes the whole recurring bug class, not one instance - a
wrapper cannot forget to re-expose an interface it never has to implement. Tests:
`AudioEffectOutput_Wrap_ResolvesCapabilitiesItDoesNotItselfImplement`,
`CapabilityLookupWalksNestedWrappersAndSurvivesACycle`.

**B3 - duplicate migrated track ids.** `Counts.ClaimTrackId` hands out ids, seeded with every id already
in the document, so a group lane merged into N descendants yields N distinct tracks and a hand-authored
track can never be collided with. Test: `AMergedGroupLaneGivesEveryDescendantItsOwnTrackId`.

**B4 - dB migration divergence. Decided: accepted, not shimmed.** One value domain for cue volume
everywhere beats bit-identical replay of legacy segment interiors. The plan's Migration acceptance
criterion now states the divergence explicitly, and `AShortLegacyVolumeLaneIsInterpolatedInDb` pins the new
values (endpoints exact, midpoint `10^(−30/20)`) so it stays intentional rather than drifting back.

## High

- **H1** Bézier subdivision is capped at one step per millisecond and an `Append` guard keeps times
  strictly increasing. Test: `AShortBezierSegmentMigratesToStrictlyIncreasingTimes`.
- **H2** Seeks seed automation before playback resumes. Each lane runner registers its own step body as a
  reseed action on the voice (`RegisterAutomationSeeder`), so seeding and the 25 ms loop are the same code
  and cannot drift. Both `SeekAsync` and `SeekManyAsync` call it, deriving clip time from the seek target
  (`ClipTimeOf`) because the timeline only re-anchors at `MarkDiscontinuity`. Test:
  `SeekingSeedsTheAutomatedVolumeBeforePlaybackResumes`.
- **H3** A visualizer's outbound run now shares the visualizer run's `AutomationRunClock`
  (`StartVisualizerAutomationAsync` returns it; `StartClockedOutboundAsync` takes `sharedClock`), so one
  seek moves one clock. It also inherits `keepAliveAfterEnd`, so a later backward seek can reopen a
  completed ramp.
- **H4** `OutboundEffects.Points` truncates at the run's duration and lands on the value sampled at that
  instant, so "land exactly on the final value" can no longer jump a desk to a key the show never reached.
- **H5** Automation copy/paste uses a new native-unit clipboard format (`HaCue2-Automation/1`) carrying
  absolute milliseconds and native values; the normalized knot formats stay readable. Test:
  `CopiedKeyframesKeepTheirMillisecondSpacingInAMuchShorterCue`.
- **H6** `CurveGesture.Accepted` reports whether an add really happened; the canvas takes capture only then,
  so a refused add is inert instead of dragging the previous selection. Test:
  `ARefusedAddIsNotReportedAsAccepted`.

## Medium

- The inline timeline lane took the dedicated editor's contract: `LogicalNudges`,
  `RemoveWhenDraggedOffCanvas="False"`, and `GestureCancelled` → `CuesViewModel.CancelGesture` (closes the
  composite and undoes it, since that surface applies per motion).
- `EffectParameterDescriptor.Clamp` resolves ±inf toward the nearest **bound**; only NaN falls back to the
  default. −inf dB is a mute again, not full gain.
- `GainAudioEffect.Configure` settles on its configured value instead of slewing from unity.
- Gain smoothing is a **duration**: the per-frame step is sized from the distance to the new target.
- `AbiPluginHost.BindAudioEffects` disposes already-taken leases if a later factory throws, and
  `NativeAudioEffectFactory.Dispose` no longer destroys the plugin-owned factory (double-destroy; it also
  meant a plugin registering any effect could never be unloaded).
- `MediaRuntime`'s stale "AddAudioEffect throws on a duplicate kind" comment now states the real last-wins
  rule.
- The inspector shows a note beside Level when a volume track owns it, so the static box no longer looks
  authoritative (`LevelIsAutomated` / `LevelAutomationNote`).

## Verification

Full solution builds clean. Suites after the pass: HaCue2.Core **782**, HaCue2 UI **409**,
S.Media.Session **401**, S.Media.Core **844** (2 skipped), S.Abi **8** - 0 failures. `AbiSmoke` exits 0.

Two **pre-existing** timing flakes surfaced under parallel load and pass repeatedly in isolation; neither
is related to this work: `TimelinePlayheadTests.FromTheTopIsExactlyWhatFiringTheGroupAlwaysDid`
(30.035 s vs 30 s) and `CrossfadeSurfaceTests.WithoutACrossfade_TheSurfaceFollowsTheNewClipsSourceCoordinate`
(−9.5 ms vs a `[0, 10s]` range). Both are worth tightening separately.

## Still open after pass 1

- The three design decisions - resolved in pass 2 below.
- The live half of the inspector's base-vs-automated readout ("automated now −12.0 dB") needs a playhead
  value from the engine; only the document-side half is wired.
- Out-of-range keyframes are still unreachable and unindicated in the editor.
- The inline lane still journals per pointer motion (the draft refactor); accessibility names on the
  toolbar/rack buttons; the ruler label offset; no-op gestures still journaling a step.
- Evaluator hot-path allocation, `FindCue` per tick, and per-tick effect JSON round-tripping.
- Video-side descriptor publication without an instance, and the un-normalized video-effect nested vtable.

---

# Fix log - pass 2 (2026-08-12)

Breaking changes were explicitly allowed for this branch, so the three parked design decisions were
resolved properly rather than shimmed.

## Design decisions - resolved

**Controller ownership is now a stack, not a single owner.** `TransportVoice` keeps an ordered list of
claims per slot, each remembering the last value its owner wrote, plus the writer needed to restore it (so
release paths never parse a slot key back into a composition/layer/parameter triple). Consequences:

- Releasing the newest claim **reveals the owner underneath** and restores its value. An older run that is
  still going gets its slot back instead of being permanently silenced.
- A non-top owner may now **withdraw** its own claim - a run that is ending must always be able to leave -
  and doing so changes nothing audible. (This flips one assertion in
  `LatestControllerClaimPreemptsOlderWritersAndRestoresSafely`, updated accordingly.)
- **`HoldFinal` gives up its claims but keeps the values** (`RelinquishController`), wired at natural
  completion via `RelinquishAutomationAsync`. A finished run no longer owns a slot forever. The operator
  also gets an escape hatch: `ReleaseControllerHoldsAsync` / `HasControllerHoldsAsync`.
- Releasing an audio-effect claim falls back to the **descriptor's declared default**, not a hard-coded 0.

Tests: `ReleasingTheNewestControllerRevealsTheOlderOneThatIsStillRunning`,
`RelinquishingAHoldKeepsTheValueButFreesTheSlot`.

**Nested group trims compose.** Each group owns its own contribution
(`SetAudioModifier`/`SetVideoModifier`, keyed by group id) and the effective modifier is their product;
claims still arbitrate between two runs driving the *same* group. `ApplyControllerAudioModifierAsync` /
`ApplyControllerVideoModifierAsync` and their Clear counterparts take a `sourceGroupId` - **a breaking
signature change**. Test: `NestedGroupTrimsMultiplyInsteadOfOverwritingEachOther`.

## Correctness

- **Seek-vs-dispose race fixed.** Both `AutomationRun` and `VisualizerAutomationRun` take a reader lease
  (`TryLease`/`ReleaseLease`/`Finish`); disposal waits for in-flight readers. A run completing between a
  seek's lookup and its first touch used to throw `ObjectDisposedException` at the operator.
- **One evaluator again.** `DuckMath.Sample` delegated to the new `AutomationCurve` instead of shaping
  segments with `Curve.Law` alone, so ducking no longer computes a restore level the show never played.
- **Preset delete-safety** now counts automation keyframe curves, so "what uses this preset?" stops
  reporting zero for a preset a track depends on.
- **Chroma spill default** aligned to `.1f` in the descriptor; a rack-created chroma now matches a
  migrated one.
- **Out-of-range keyframes are reported and removable.** A validator warning names the count per track
  (preserved, still runnable), and the editor shows it with an explicit **DELETE KEYS PAST THE END**
  command. Test: `KeysPastTheCueEndAreReportedAndRemovableByAnExplicitCommand`.
- **No-op gestures journal nothing** (`ChangesAnything`), so a refused add no longer dirties the project
  or leaves an Undo that does nothing. Test: `AGestureThatChangesNothingJournalsNothing`.

## Performance

`AutomationCurve` prepares a track once - keys filtered and sorted, every segment shape resolved, the
descriptor looked up - and samples allocation-free. Both 40 Hz drivers now build a plan at fire time
(`AutomationRun.Plan` / `VisualizerAutomationRun.Plan`) that also resolves each track's target cue once,
removing the per-tick `FindCue` document flatten. `AutomationEvaluator.Sample` remains as the one-shot
convenience for editors and tests. `NativeAudioBusEffect.TrySetParameter` pre-encodes its parameter names
and looks them up in a dictionary, so a control-thread write allocates nothing.

## ABI

- The **video-effect nested vtable is normalized** like the audio one, so the first field ever added to it
  cannot become an unchecked read past a legacy plugin's shorter struct.
- **New layout regression test** (`AbiStructLayoutTests`): generates a C probe against the real header,
  compiles it with gcc, and compares every `sizeof`/`offsetof` with the managed mirrors. It passes today -
  confirming the mirrors are correct - and will fail on the next hand-sync drift. `S.Abi` gained an
  `InternalsVisibleTo` for the test project.

## UI

Ruler ticks corrected (four ticks labelled at their own left edge, and the strip inset to match the plot
surface's border + padding + canvas margin - labels were up to 20 % of the viewport off), accessibility
names added to every automation-editor and effect-rack button, and the stale "double-click the editor to
add a key" inspector copy replaced.

## Verification (pass 2)

Full solution builds clean. HaCue2.Core **782**, HaCue2 UI **411**, S.Media.Core **844** (2 skipped),
S.Abi **9**, S.Media.Session **404** - 0 failures. `AbiSmoke` exits 0.

## Still open after pass 2

Resolved in pass 3 below, except the live inspector readout and the ABI display-name field.

---

# Fix log - pass 3 (2026-08-12)

## The inline timeline lane now drags in a draft

The last of the seven original defects on that surface. A continuous drag mutates a local keyframe list and
re-projects only its own lane (`ProjectAutomationDraft`); the document is written once, on release. Escape
simply discards the draft - nothing reached the document, so there is nothing to undo. One-shot edits
(add/remove) still write straight through, and `CancelGesture` undoes those. Previously every pointer
motion journalled a whole-list replacement and refreshed the entire timeline; a quiet composite kept it one
undo step, but the work and the churn were real. Test:
`TheInlineLaneDragsInADraftAndCommitsOnceOnRelease`.

## Placement collisions are rejected

Placement automation lowers addressed by `(composition, layer)` and the session fans each envelope to every
layer matching that pair, so two placements sharing one slot collide at runtime whatever their ids say -
one placement's opacity track would move the other. The validator now errors on it, making the design's
"one placement's opacity track does not alter another placement of the same decoded cue" a real guarantee
rather than a document-layer one.

## Automation writes short-circuit when nothing moved

The runner rewrites every parameter every 25 ms whether or not the value moved. `PlacementEffectAutomation.Set`
and `PlacementAutomation.Set` now report whether they changed anything, and the callers skip the work when
they did not. That removes, on a flat segment or a track sitting on its last key:

- the whole layer effect-chain rebuild - spec allocation, config JSON re-serialize **and** re-parse, and
  reconstruction of any registry plugin instance - 40×/s; and
- the placement recompose + re-apply, 40×/s per property per layer.

## Migration no longer runs on every deserialize

`Migrate` returns early when no cue holds a legacy lane and no earlier pass left one unresolved. This
matters because `ProjectSnapshot.Copy` is a serialize/deserialize round trip, so every runtime snapshot was
paying for a full migration walk plus a re-stamp of the migration summary. The guard tests the **lanes**,
not the stamped schema number - a project built in code carries the current schema by default and may still
have legacy lanes assigned onto it.

## Both flakes fixed - and one was not what it looked like

- `CrossfadeSurfaceTests` asserted an exact floor of zero on the first composite after a butt splice. The
  composite pump and the clock advance on different threads, so a few milliseconds either side of the
  splice is scheduling weather. Now carries one frame of tolerance, with the intent (follows the NEW clip's
  coordinate, not the outgoing one's) unchanged.
- `TimelinePlayheadTests` looked like a real defect - a *virtual* clock reporting 30.035 s where the
  schedule is exact. It is a harness artifact: `FakeCueHost` samples a clock **shared by every timeline
  branch** at whatever moment its continuation runs, so a concurrent branch's advance can land between the
  edge release and the read. Production reads a monotonic device clock and would jitter identically. Order
  and identity are still asserted exactly; only the recorded time carries tolerance.

## Verification (pass 3)

Full solution builds clean. HaCue2.Core **782**, HaCue2 UI **412**, S.Media.Core **844** (2 skipped),
S.Abi **9**, S.Media.Session **404** - 0 failures, and the Core suite passes 3/3 consecutive runs with the
former flake in it. `AbiSmoke` exits 0.

## Still open after pass 3

All three closed in pass 4 below.

---

# Fix log - pass 4 (2026-08-12)

The last three items. Nothing from the original audit remains open.

## The inspector shows base vs automated, live

The design's `base −6.0 dB · automated now −12.0 dB` now renders while a cue sounds. The path reuses the
existing 4 Hz engine poll rather than adding one:

`TransportSnapshot` gained `CueVolume`/`AuthoredVolume` (plain float reads off the active voice's level,
on the same lock-free terms as the position reads beside them, so a concurrently replaced voice yields a
stale value for one tick rather than throwing) → `ActiveCueState.AutomatedVolumeDb`, set only when
something is actually overriding the authored level → `ActiveCueRow` → `CuesViewModel.Tick` pushes the
selected cue's value into `InspectorViewModel.LiveAutomatedVolumeDb`, read off rows the tick had already
gathered. Off air the note falls back to naming the track and its range.

Writing the test caught a real defect in my own change: the live value **leaked across a selection
change**, so the outgoing cue's automated level briefly read as the incoming cue's. `Show()` now clears it.
Test: `TheInspectorSaysWhatAutomationIsDoingToTheLevel`.

## Video-side descriptor publication

`AudioEffectRegistrationDescriptor` is renamed `EffectRegistrationDescriptor` (**breaking**, one file) and
now serves both halves. `AddLayerEffect` gained the metadata overload, with `LayerEffectDescriptors` /
`TryGetLayerEffectDescriptor` mirroring the audio surface, and `VideoLayerEffectDescriptor` publishes
`AuthoringParameters` - its scalar parameters flattened out of the packed vector ones. The two built-in
layer effects register with their metadata, so the API is not dead on arrival.

The point is what it removes: an insertion menu can list an effect's parameters without constructing (and
configuring) the effect first. Test:
`BusRegistry_PublishesLayerEffectParametersWithoutCreatingAnInstance` asserts the factory is never invoked
while reading them, and that a metadata-free registration still keeps its kind.

## Native factories can publish a display name

`display_name` appended to `MfpAudioEffectFactoryVTable`, surfaced as
`NativeAudioEffectFactory.DisplayName`, used by `BindAudioEffects` with the kind as fallback - a menu
showed `test.gain` where it should show "Test Gain".

The append-only rule is now proven twice over rather than asserted: the layout test added in pass 2 was
already in place when this field landed and confirmed every pre-existing offset unchanged, and the gcc
fixture's `test.gain` sets `display_name` while `test.gain.legacy` keeps a `struct_size` ending before
`get_parameter_count`. Both still load, and the legacy one still publishes no metadata.

## Verification (pass 4)

Full solution builds clean. HaCue2.Core **782**, HaCue2 UI **413**, S.Media.Core **844** (2 skipped),
S.Abi **9**, S.Media.Session **406** - 0 failures. `AbiSmoke` exits 0.

## Still open

Nothing from the audit. Two notes for whoever picks this up next:

- The lock-free `TransportSnapshot` level reads are deliberately racy-by-one-tick, matching the position
  reads they sit beside. If a future consumer needs an exact value it must go through
  `GetClipAudioLevelsAsync`, not this snapshot.
- `EffectRegistrationDescriptor` is now shared by audio and layer effects. A third effect stage should
  reuse it rather than adding a third parallel record.
