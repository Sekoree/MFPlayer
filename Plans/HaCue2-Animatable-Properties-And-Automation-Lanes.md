# HaCue2 animatable properties and automation lanes

Status: adopted; phases 1-3 implemented, phase 4 framework rack/audio/native path implemented with app plugin discovery remaining, phase 5 new authoring complete

Date: 2026-08-12

Code inspected: `d72ef7e7` (`next-experiment`)

Implementation baseline: `37174447` (`Better effects part 1`)

Implementation update (2026-08-12): phase 1's model, migration, runtime lowering, and primary editor
are implemented with absolute-time/native-unit tracks, volume and placement-opacity lowering, a
scrollable single-cue lane, and lazy cached waveform context trimmed into cue time. Automation cues
and OSC/MIDI tracks now use a shared pause-aware, seekable cue clock, including rehearsal offsets,
refire handover, normal-completion final values, and explicit freeze-versus-land-final interruption
policy. Group audio trim and video opacity are exposed as automation-cue targets and compose through
dedicated modifier slots rather than overwriting child automation or fades. Placement transforms and
stable-instance chroma-key/colour-adjust parameters now use the same catalog and composition slots for
media, text, and visualizer cues. Visualizer lanes run against their surface layers on the shared clock,
including seek and timeline-rehearsal offsets.

Correction/continuation pass (2026-08-12, working tree after `9020312a`): curve laws now have one
direction-independent segment meaning in the editor, session and outbound runner, and compiled volume
envelopes remain in dB until after interpolation. This required show-document schema 2 while retaining
schema-1 linear-envelope reading. Automation-cue writes now use instance-captured controller slots with
latest-owner arbitration; cue-owned automation continues underneath and is revealed on restore. The
same contract covers media, text and visualizer placements, group modifiers, transform/effect parameters,
refire handover and terminal outbound sends.

Effect-rack continuation pass (2026-08-12, working tree after `aad6c1a3`): HaCue project schema 3 now
stores ordered, bypassable video-effect instances with stable IDs, type-local scalar values and retained
opaque plugin configuration. Schema-2 chroma/colour settings migrate without changing instance identity
or render order. Catalog-driven lowering carries the rack into show-document schema 3, and the session
resolves each instance through the injected bus registry with built-in fallback. Automation addresses
arbitrary descriptor parameters by effect-instance ID and parameter ID; duplication, validation, live
controller ownership, visualizer layers and the rack authoring controls all use that same identity.

Cue-local managed audio inserts now follow the same model. The built-in smoothed gain insert publishes
dB metadata, compiles into each clip route/program input, and accepts cue-time or automation-cue values
through `IAutomatableAudioBusEffect`. Each routed copy receives identical writes, effect wrappers preserve
device clock/stat capabilities and borrowed-output ownership, and a controller claim reveals the latest
cue-owned value when released. Focused tests cover migration/order/bypass, registry config overlay,
generic video parameters, audio factory/configuration, live audio automation and controller restoration.
The bus registry and append-only native ABI now also publish effect descriptors without constructing a
live processing instance. New native effects can receive clamped control-thread values plus a smoothing
duration, while pre-extension vtables remain runtime-compatible and simply expose no authoring metadata.
The remaining phase-4 boundary is application-level discovery: HaCue2 does not yet load external plugin
registrations into its insertion menus/property catalog. This is not required by the built-in rack.

The editor now uses a gesture-local draft and immutable viewport transform, commits one undo step on
release, cancels on Escape, offers millisecond snapping/Alt bypass/Shift constraint/grid-aware nudging,
click-drag creation, a visible ruler, and per-segment **Edit curve shape…**. Automation cues can select a
specific placement on multi-placement targets. Unknown property IDs open read-only and remain runnable
warnings; migration results are visible in Project Status. New authoring commands use stable property IDs
instead of menu ordinals. The managed effect foundation now publishes scalar video authoring metadata and
an optional real-time-safe audio parameter interface with effect-owned smoothing, proven by the built-in
gain effect. The native ABI extension is append-only and covered by both new-parameter and old-vtable
gcc fixtures.

Focused coverage now pins long-cue viewport/waveform projection, pause/seek/stop and rehearsal timing,
automation-cue natural completion, outbound reposition/coalescing, group modifier composition,
group-target authoring, placement transforms, stable effect-instance duplication/lowering, and
visualizer surface automation. The broader Core, editor, engine, session and native-ABI suites pass,
along with the native plugin smoke executable and a clean HaPlay build.

This supersedes the `EffectLaneKind`/shared whole-curve-editor direction in
`Plans/HaCue-Feature-Ideas.md` and the automation-lane portions of
`Plans/HaCue2-Framework-Gap-Analysis.md`. Their runtime ownership and single-authority composition
constraints still apply.

## Recommendation

Replace `EffectLaneKind` with property-targeted, absolute-time automation tracks, and give those tracks
a dedicated, scrollable cue editor. Keep a **curve** as a small, normalized shape which eases one fade
or one segment; do not use the curve-shape dialog as the editor for a 30-minute cue.

The long-term vocabulary should be:

- an **animatable property** is a stable, typed numeric target such as cue volume, one placement's
  opacity, or an effect instance's contrast;
- an **automation track** is keyframes over cue time which drive one animatable property;
- an **automation cue** is an ordinary cue whose tracks drive properties on other sounding cues,
  groups, buses, or external endpoints for an authored duration;
- an **effect** is a processor such as chroma key, colour adjustment, EQ, or compression. Its numeric
  parameters may be animatable, but the processor and its automation are not the same object; and
- a **curve shape** is an interpolation function used by a fade or between two keyframes. It has no
  media timeline of its own.

This keeps the good part of the existing implementation—one interpolation evaluator and the runtime's
single-authority level composition—without forcing every timed edit through a normalized square.

## Why this needs an architectural change

The recent timeline and curve work has fixed real defects, but the remaining friction is not one more
pointer-handler bug. The document model and the editor both describe time as a fraction of the entire
cue, then reuse the fade-curve editor because a fade curve also happens to use values from zero to one.
That similarity is mathematical, not semantic.

### What exists now

`MediaCueNode`, `TextCueNode`, `VisualizerCueNode`, and `GroupCueNode` each carry a list of
`EffectLane`. A lane has:

- one of four enum kinds: volume, opacity, OSC ramp, or MIDI ramp;
- normalized `LanePoint.X` and `LanePoint.Y` values in `0..1`;
- no stable keyframe identity;
- an optional endpoint and address, embedded in the same record as internal media automation; and
- at most one lane of each enum kind.

`TimelinePresentation` projects each lane into the timeline group. The inline editor is laid over the
owning cue's visible span, while `CurveEditorWindow` displays the same normalized point list in a full
rectangle. Supplying the window with a cue duration only changes the exact field from a percentage to a
formatted time; it does not give the window a time ruler, viewport, playhead, waveform, scrolling, or
zoom.

Compilation handles volume and opacity by converting normalized X positions into
`ShowEnvelopePoint.Time`. The session then samples them against clip time. OSC and MIDI are run by the
separate host-side `OutboundEffects` clock.

There are several good foundations worth preserving:

- volume composes through the existing source/fade/envelope/master chain rather than overwriting a
  fade;
- opacity has an equivalent authored/fade/automation composition in `VisualLevel`;
- session envelope sampling follows clip time and therefore understands seek and loop positions;
- interpolation laws and custom Bézier shapes are centralized; and
- the journal already knows how to make a drag one undo step.

### Concrete causes of the current editing behaviour

The implementation contains several mechanisms which directly explain why long-cue editing feels
unreliable or imprecise:

1. `CurveCanvas.NudgeStep` is `0.01`, so one horizontal arrow nudge means one percent of the whole cue.
   On a 30-minute cue that is 18 seconds, regardless of zoom or the operator's intended precision.
2. `CurveEdits.HasPointNear` also uses a one-percent X threshold. On that same cue, a new keyframe is
   refused when another point is within 18 seconds, even if the viewport makes them look far apart.
3. Adding a point is a double-click-only canvas gesture. There is no explicit “add keyframe at
   playhead” route in the dedicated editor.
4. Pointer capture remembers `_draggedIndex`. Every motion writes and normalizes the whole list, and
   normalization sorts by X. If the dragged point crosses a neighbour, the index can now identify a
   different point on the next motion. Timeline selection is also stored as point indices.
5. Each pointer motion journals a whole-list replacement and refreshes the point/shape projection.
   Recent changes correctly preserve the lane control itself during a drag, but identity is still an
   array position in a collection being replaced and sorted.
6. A lane is required to retain at least two points. “Delete what I selected” can therefore be refused
   for a rule needed by custom fade shapes, even though an empty or one-key automation track has a
   perfectly useful meaning.
7. Normalized X positions stretch when the played duration changes. A keyframe placed on a meaningful
   moment in a long recording moves when the out-point or discovered duration changes.

The result is a control which behaves reasonably for a two-second easing curve and poorly as a media
timeline. More hit-testing tweaks will not change that mismatch.

### The current “effect” abstraction also loses information

The lane enum is already too coarse for the features it is meant to grow into:

- one opacity lane affects every placement of a cue; it cannot name one of several placements;
- one lane per kind prevents two OSC addresses or two MIDI controllers on the same cue;
- child-versus-group precedence is keyed only by kind, not by an actual target;
- group lanes are copied down and restarted over each child's duration instead of following one group
  clock;
- internal lanes are sampled by the media session, while outbound lanes use a `Stopwatch`; pause and
  seek therefore do not share one timing contract;
- outbound compilation currently replaces each authored `CurveToNext` with linear; and
- timeline rehearsal starts an outbound ramp over the *remaining duration*, which restarts its shape
  rather than sampling the shape at the seek position.

The inspector also contains enum-order mappings and a status sentence saying outbound lanes are not
sent even though the engine now sends them. That small drift is a useful warning: adding more enum
members will spread more target-specific switches across model, validator, compiler, engine, and UI.

Finally, the repository's actual media effects are a different subsystem. Video layer effects have
descriptors and packed values; audio bus effects are created from opaque configuration and expose a
real-time `Process` contract. Neither is represented by `EffectLaneKind`. Treating all of these as an
“effect lane” makes it harder, not easier, to add automatable effect parameters later.

## Design goals

The replacement should satisfy the following contracts:

1. Editing at 27:15 in a 45-minute file must be as precise and stable as editing at 00:02.
2. A keyframe has stable identity through reordering, multi-selection, undo, and pointer capture.
3. Stored time is meaningful time, not a percentage of whatever duration was last measured.
4. The UI lists properties that the selected cue and its concrete sub-objects can actually animate.
5. Every property has explicit units, limits, default value, composition rule, and runtime owner.
6. Automation, fades, authored values, live edits, master trim, and group modifiers never become
   competing writers to the same final value.
7. Seek, pause, resume, loop, rehearsal-from-playhead, refire, stop, and panic have deterministic
   automation behaviour.
8. Multiple placements, effect instances, and outbound endpoints are addressed by stable IDs.
9. Automation can work on an indefinite/live cue; media probing must improve the editor but must not
   decide whether the authored track reaches the engine.
10. Old projects migrate without changing their effective volume or opacity.

This proposal deliberately does **not** require reflection over every C# property, a plugin ABI change
in the first phase, or immediate removal of `FadeCueNode` and `PatchCueNode`.

## Separate shape space from time space

This is the central design decision.

### Curve-shape editor

A curve shape maps normalized progress to normalized progress. A square is correct for it:

```text
input progress 0..1  ->  curve/easing  ->  output progress 0..1
```

Use this editor for:

- fade in/out shape;
- stop, panic, patch, and playlist-crossfade shape;
- project curve presets; and
- the outgoing interpolation of one automation segment.

Rename the window and its labels to **Curve shape** so it never implies that its X axis is an entire
piece of media. It does not need a transport or waveform.

### Automation editor

An automation track maps real cue time to a value in the property's native unit:

```text
cue time 00:00:00.000 .. 00:45:00.000  ->  volume -inf..+12 dB
cue time 00:00:00.000 .. 00:45:00.000  ->  placement opacity 0..100 %
```

This editor needs a viewport, horizontal navigation, a ruler, playhead, keyframe lanes, and media
context. It may reuse the curve evaluator for each segment, but it should not reuse a control whose
coordinate system is the whole curve.

## Document model

The exact names can change, but the persisted concepts should have this shape:

```csharp
public sealed record AutomationTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AutomationTargetRef Target { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public List<AutomationKeyframe> Keyframes { get; set; } = [];
}

public sealed record AutomationTargetRef
{
    // Stable catalog key, never a CLR property name.
    public string PropertyId { get; set; } = "";

    // Null means the owning cue itself. Otherwise this names a placement, effect instance,
    // endpoint binding, bus, or another concrete target object.
    public Guid? ObjectId { get; set; }
}

public sealed record AutomationKeyframe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long TimeMs { get; set; }
    public double Value { get; set; }

    // Describes the segment from this key to the next. Hold is a real step; otherwise Curve
    // is evaluated in normalized segment space.
    public bool Hold { get; set; }
    public CurveSpec Curve { get; set; } = CurveSpec.Linear();
}
```

Important details:

- `TimeMs` is cue-relative, absolute time after the cue's trim-in. `long` avoids baking today's
  duration limits into the format.
- keyframe IDs are persisted. Selection, dragging, clipboard identity, diagnostics, and journal
  commands use the ID, never the sorted-list index;
- values are stored in the property's native authoring unit, not normalized Y;
- a curve remains normalized *inside the segment*, where reuse of `CurveSpec` and project presets is
  correct;
- zero keyframes means an inert track and draws the authored base as a dashed line;
- one keyframe is a valid constant automation value; and
- before the first keyframe and after the last keyframe, the nearest keyframe is held. The editor shows
  those horizontal continuations rather than leaving extrapolation implicit.

The owner cue should expose `List<AutomationTrack> AutomationTracks`. A track on a media, text, or
visualizer cue follows that cue's clock. An indefinite cue remains automatable: the viewport initially
extends past the last keyframe and the last value holds until stop.

### Stable sub-object IDs

`LayerPlacement` currently has no ID. Add one before placement properties become targets. Effect
instances also need IDs. Composition/layer number, list position, display name, or property path is not
a safe identity: all can change while the same authored object remains.

An opacity target then means:

```text
owner cue Q12
object 8de0... (the placement on Main / layer 2)
property video.placement.opacity
```

The UI may offer “all placements”, but that should be an authoring macro which creates/links explicit
tracks, not a magic target whose membership silently changes when a new placement is added.

## Animatable-property catalog

Do not implement “animatable” as an attribute on arbitrary model setters. Attributes cannot express
which placement is being addressed, whether a media file actually has audio, how a value composes with
fades, which thread owns its runtime setter, or whether a plugin exposes a real-time-safe update.
Reflection also works against the project's NativeAOT discipline.

Use an explicit catalog. A descriptor is code, not project data; the project stores only its stable ID
and target object ID.

```csharp
public sealed record AutomationPropertyDescriptor(
    string Id,
    string DisplayName,
    AutomationValueSpec Value,
    AutomationTargetKind TargetKind,
    AutomationDomain Domain,
    AutomationComposition Composition,
    string Group,
    bool SupportsCueOwnedTrack = true,
    bool SupportsAutomationCue = true);

public sealed record AutomationValueSpec(
    double Minimum,
    double Maximum,
    double Default,
    string Unit,
    AutomationScale Scale);
```

The catalog needs two halves:

- **Core discovery** answers which descriptors and concrete target references apply to a cue. The
  inspector and editor use this and never switch on enum ordinal.
- **Runtime binding** maps a resolved target to a compiler binding or live actuator. It owns thread
  affinity, update cadence, smoothing, and failure reporting.

Unknown property IDs must load as unresolved/inert tracks and appear in Project Status. They must not
be discarded. This lets a project survive opening on a machine without an optional effect/plugin.

### Recommended first property set

| Property ID | Concrete target | Unit/range | Automated value | Other contributors |
|---|---|---|---|---|
| `cue.audio.volume` | media cue | dB, silence floor..+12 | the cue's volume at time T | per-send gain, fade, master trim |
| `video.placement.opacity` | one placement ID | 0..100 % | that placement's authored opacity at T | fade factor |
| `group.audio.trim` | group runtime | dB, attenuation..0 | additive trim over active descendants | child volume, fades, master |
| `group.video.opacity` | group runtime | 0..100 % | multiplicative factor over descendants | placement opacity, child automation, fades |

Start with the first two. Group targets should be a later, explicit feature; they must not be implemented
by copying one lane into every child and restarting it on each child clock.

After the runtime has property-specific composition slots, add:

- placement X, Y, width, height, rotation, and crop;
- brightness and contrast;
- chroma similarity, smoothness, and spill suppression;
- visualizer parameters which have a stable runtime setter; and
- effect-instance parameters from the effect catalog described below.

Each new descriptor is accepted only when its composition and runtime-update contracts are defined. A
number being present in the document is not by itself a reason to expose it.

## One authority for each effective value

Property-targeted automation must not mean calling the ordinary model setter 40 times per second. The
model holds authored data. A runtime slot evaluates it alongside other contributors.

For cue volume, the desired composition is conceptually:

```text
send gain
  x dB(animated cue volume, or static LevelDb when there is no active track)
  x group trim
  x cue/stop/crossfade level
  x master trim
```

For placement opacity:

```text
(animated placement opacity, or static placement opacity)
  x group opacity
  x cue/stop/crossfade level
```

This evolves the existing `SoundingLevel` and `VisualLevel` pattern; it does not bypass them.
Position-like properties have no fade multiplier, but still need distinct authored and automated slots
so a live base edit cannot race a timeline sampler writing the same field.

Using native values makes the UI honest. A volume keyframe stores and displays `-12 dB`, not `0.251`.
An opacity keyframe stores and displays `50 %`, not a generic Y value whose meaning depends on an enum.

## Automation cues

Name the new cue kind `AutomationCueNode`, not `EffectCueNode`. “Effect cue” becomes ambiguous as soon
as HaCue2 gains an EQ or another actual processor.

```csharp
public sealed record AutomationCueNode : CueNode
{
    public int DurationMs { get; set; } = 1000;
    public List<TargetedAutomationTrack> Tracks { get; set; } = [];
    public AutomationCompletion Completion { get; set; } = AutomationCompletion.HoldFinal;
}

public sealed record TargetedAutomationTrack
{
    public Guid TargetCueId { get; set; }
    public AutomationTrack Track { get; set; } = new();
}
```

Recommended firing semantics:

- resolve and capture the targeted active cue instances when the automation cue fires;
- a missing/non-sounding internal target is a reported no-op, not a delayed surprise when it later
  starts;
- refiring the same automation cue preempts its previous run and samples from the new run's authored
  start;
- pause freezes the run and resume continues it;
- a timeline seek samples the track at the sought automation-cue time immediately;
- `HoldFinal` is the default for a control value; `RestoreBase` is explicit; and stopping targeted
  media remains a separate option/verb rather than an accidental side effect of reaching zero.

`FadeCueNode` should remain in the first migration. It is a high-level show verb with useful targeting
and “stop when silent” semantics. It may later compile internally through the same evaluator/actuator,
but operators should not have to draw two keys for the common “fade this cue out and stop it” action.
The same reasoning keeps `PatchCueNode`: a persistent state recall is not time-domain property
automation merely because it can have a transition duration.

## Effects and their animatable parameters

Long-term effect authoring should use an ordered rack of instances:

```csharp
public sealed record CueEffectInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EffectTypeId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<EffectParameterValue> Parameters { get; set; } = [];
}

public sealed record EffectParameterValue
{
    public string ParameterId { get; set; } = "";
    public double Value { get; set; }
}
```

An effect descriptor must publish stable parameter IDs plus display name, type/component count, range,
default, unit/scale, and whether it supports live automation. A track targets the effect instance ID and
one parameter ID. That gives “animate contrast on this colour effect” a durable address.

At adoption the framework needed additional contracts before arbitrary plugin parameters could be animated:

- video descriptors needed authoring metadata and a safe immutable instance-swap path;
- audio effects needed parameter descriptors, a real-time-safe setter and clear smoothing ownership; and
- the native ABI needed an append-only mirror only after proving that managed contract.

Those framework contracts are now implemented. What remains is for HaCue2's application composition root
to load external registrations and inject their descriptors into the authoring catalog.

Therefore phase one should wrap the built-in placement/chroma/colour fields in descriptors. The effect
rack and plugin parameter contract are a later layer over the same automation target system, not a
prerequisite for fixing volume and opacity editing.

## Automation editor UX

Open the same editor from:

- an **AUTOMATION** tab on any cue with applicable properties;
- the diamond/automation button beside a static property in Audio, Video, or an effect rack;
- an automation cue; and
- an automation row inside a group timeline.

For a single media cue, the main surface should resemble the trimming editor:

```text
Q12  Long ambience                         27:14.820 / 45:03.200
[ play/pause ] [ add key ] [ fit ] [ zoom - / + ] [ snap: 100 ms ]

time      27:00       27:10       27:20       27:30       27:40
waveform  ~~~~__~~~~~~~~~~~~__~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
volume    -6 dB ----o---------o\____o-------------------------
opacity   100 % -----------------------o____/o-----------------

[overview/minimap==================| visible window |=========]
selected key: 27:15.240   -12.0 dB   segment: s-curve
```

Required behaviour:

- horizontal zoom and pan change the viewport, never the document values;
- an always-visible scrollbar/overview makes long-cue navigation obvious;
- waveform context is shown for audio properties; video tracks can later add thumbnail/preview
  context;
- the playhead can be scrubbed and typed, and **Add keyframe at playhead** is a first-class button and
  shortcut;
- clicking/dragging an empty lane creates a key at the pointer and begins dragging it; double-click can
  remain as an alternate gesture, not the only discoverable route;
- hit radii are pixels, time snapping is milliseconds/frames, and value snapping uses the property's
  units. No interaction threshold is a fraction of the entire cue;
- Alt temporarily bypasses time/value snap; Shift constrains a drag to one axis;
- arrow nudge follows the selected grid or a small time unit at the current zoom, not one percent;
- dragging off the canvas does **not** delete. Delete/Backspace, a context command, and a visible remove
  button are sufficient and do not turn a lost capture into data loss;
- exact time and native-unit value fields stay beside/below the lane; and
- selecting a segment opens its interpolation picker, while **Edit curve shape…** opens the small
  normalized curve-shape window for that segment only.

In the inspector, an automated property should show both its base and its playhead value. The static
control must not silently look authoritative while a track is overriding it. A small state such as
`Base -6.0 dB · automated now -12.0 dB` is enough.

The existing group timeline can host compact projections of these same tracks, but the single-cue
automation editor is the primary detailed surface. A long cue must not be placed inside a Timeline
group merely to get a usable volume editor.

## Stable edit sessions

The editor should make a local draft for the duration of a pointer gesture:

1. on press, capture the keyframe ID, the initial sorted keys, and an immutable viewport transform;
2. on move, update that key by ID in the local draft and redraw only the affected track;
3. optionally publish a throttled latest-only preview to the live runtime;
4. on release/capture loss, normalize and validate once, then journal one replacement command; and
5. on cancel/Escape, discard the draft.

Sorting is a presentation/evaluation concern, not identity. A keyframe can cross its neighbour without
the pointer being transferred to a different key. Multi-selection stores IDs. Tangents/segment data are
attached to the outgoing keyframe ID.

This also avoids sending the shell's full document-change/recompile path on every pointer motion. In the
first implementation, authored edits may continue to apply on the next fire/pass; the editor's local
audition supplies feedback. If live edit of a sounding track is added, swap one immutable track snapshot
at gesture boundaries or through a latest-only preview channel—never reload the show for every pixel.

## Timing contract

All actuators consume the same pure evaluator:

```text
Sample(sorted keyframes, cueTime) -> property value
```

The clock comes from the owning runtime:

- a cue-owned internal track samples the session's clip/group transport time;
- an automation cue samples the cue executor's pausable/seekable timeline clock;
- an outbound actuator uses that sampled cue time and adds rate limiting; it does not invent a second
  `Stopwatch` timebase;
- a loop resets cue time and therefore replays cue-owned automation per pass; and
- a seek seeds every automated value at the target time before audio/video becomes visible.

Absolute cue time has an intentional trim rule: changing trim-in/out does not stretch keyframes. They
remain at the same time after cue start. Keys past a shortened out-point remain in the document and are
shown as out-of-range, with explicit commands to delete them or scale/rebase the track. Silent automatic
rescaling is what makes carefully placed long-form edits drift.

A later “lock automation to source time” mode may be useful for media markers, but it should be an
explicit time base and conversion command, not inferred during a trim.

### Outbound-specific rules

OSC/MIDI are actuator targets, not special property enum members. Their descriptors/configuration also
carry endpoint, address/controller, native range, and a default/max send rate.

They retain three extra rules:

- intermediate sends are rate-limited and coalesced; no backlog of stale fader positions;
- normal completion explicitly lands on the sampled final value; and
- stop/panic behaviour is declared per track or project policy. Recommended default: stop freezes at
  the value sampled at the interruption, while normal completion lands on the last key. “Land final on
  panic” is too surprising for lighting/machinery to be implicit.

Multiple outbound tracks to the same protocol are valid because identity is target, not `OscRamp` or
`MidiRamp` kind.

## Layering and runtime ownership

Keep the existing library boundaries:

```text
HaCue2.Core
  document records, property IDs/descriptors, validation, migration, pure evaluator

HaCue2 UI
  property discovery presentation, automation editor, gesture drafts, journal commands

HaCue2.Engine
  cue executor, automation-cue lifetime, outbound actuators, runtime target resolution

S.Media.Session / compositor / routing
  clip-time internal actuators and the single composed value for audio/video properties
```

`HaCue2.Core` must not reference Avalonia or an app runtime. `S.Media.Session` must not know OSC/MIDI or
HaCue2 cue-node types. The compiler maps known cue-owned properties to the session document; the host
runs endpoint targets beside the existing action sender.

For compatibility, the compiler can initially lower new `cue.audio.volume` and
`video.placement.opacity` tracks into the existing `VolumeEnvelope` and `OpacityEnvelope` fields. A
future generic `ShowAutomationBinding` is justified when a third internal framework property lands, not
before. Keep the old fields as document/ABI shims during that transition.

## Project migration and compatibility

This change should bump the HaCue2 project schema to 2. Although new fields are additive in JSON shape,
an older build which ignores them will play a show without its automation and can then resave it. That
is a semantic misread, so the current “closed above” policy should refuse it rather than silently losing
show behaviour.

Migration from schema 1 should be explicit and tested:

1. Generate a stable ID for every migrated keyframe.
2. For a cue volume lane, convert each linear factor to an absolute cue-volume value:
   `LevelDb + 20*log10(Y)`, using the silence floor for zero.
3. For opacity, create one track per existing placement and convert each point to
   `placement.Opacity * Y`.
4. Convert X to `TimeMs` against the cue's played duration. If no honest duration is available, retain
   the legacy normalized lane as a compatibility shim until probing resolves it; do not guess and move
   the effect to arbitrary moments.
5. Preserve authored segment curves and Bézier shapes.
6. Convert OSC/MIDI endpoint/address data into explicit outbound target bindings. Preserve the authored
   curves even though the current runner plays them linearly.
7. Preserve current child-overrides-group behaviour for legacy group lanes. The safest conversion is to
   materialize the effective legacy lane on each affected descendant once its duration is known. New
   group automation uses the explicit group clock and never inherits by enum kind.
8. When both a new track and a legacy lane address the same property, the new track wins. They must never
   both contribute.

Perform conversion through a versioned migration service, not scattered serializer callbacks. Before
the first schema-2 save, keep the ordinary backup/recovery copy and show a migration summary in Project
Status.

## Delivery plan

### Phase 0 — contain the current editor

If the existing model must ship before replacement:

- track the dragged point by temporary/stable identity rather than index;
- stop sorting/rebuilding the document list on every motion;
- replace percentage-based add/nudge thresholds with viewport pixel/time thresholds;
- add an explicit keyframe-at-playhead command; and
- remove drag-off-to-delete.

These are worthwhile safety changes, but they do not make the normalized curve window a long-form
automation editor.

### Phase 1 — new model and single-cue editor

- add `AutomationTrack`, stable `AutomationKeyframe` IDs, absolute `TimeMs`, and schema migration;
- add the explicit descriptor catalog with volume and per-placement opacity;
- build the scrollable/zoomable single-cue editor with ruler, playhead, waveform, exact fields, and one
  gesture draft per undo step;
- lower those tracks into the existing framework envelopes; and
- make unknown/live-duration cues work without guessing a total length.

This phase directly addresses the reported problem and is the recommended first milestone.

### Phase 2 — runtime generalization

- introduce shared evaluation snapshots and runtime target bindings;
- make volume/opacity seek, pause, loop, and rehearsal tests target the common timing contract;
- add explicit group trim/opacity targets; and
- add placement transform properties only after each has a composed authored/automation runtime slot.

### Phase 3 — automation cues and outbound targets

- add `AutomationCueNode` and its completion policies;
- move OSC/MIDI tracks to target descriptors and the common cue clock;
- preserve curve shapes, send-rate/coalescing, seek offsets, and explicit stop/panic behaviour; and
- allow multiple endpoints/controllers per cue.

### Phase 4 — effect rack and parameter automation

- **Done:** schema-3 stable video/audio effect instances, ordered rack values, bypass and legacy migration.
- **Done:** ordered video-rack authoring UI, descriptor-based built-in discovery, generic lowering and
  registry resolution while retaining unknown plugin configuration.
- **Done:** every built-in chroma/colour authoring parameter is catalogued and addressable by stable
  effect-instance/parameter IDs rather than the original five-property enum.
- **Done:** cue-local gain inserts, managed real-time parameter publication/smoothing, compiler/session
  actuation, automation-cue control ownership, duplication retargeting and validation.
- **Remaining:** let injected managed/native plugin registrations publish authoring descriptors to
  HaCue2's insertion menus and property catalog instead of only the framework bus registry.
- **Done:** the native audio-effect ABI appends factory descriptors and a live setter, normalizes nested
  vtables, and retains compatibility with plugins whose struct sizes end at the original fields.

### Phase 5 — remove legacy authoring

- **Done:** UI/helpers no longer create `EffectLane`; `DuckMath` and samples write native-time tracks.
- **Done:** property creation is keyed by stable property ID rather than enum/menu position.
- **Retained intentionally:** schema-1 model, validator, migration, badges and curve-target readers remain
  compatibility shims for the supported project window.
- **Remaining cleanup:** rename internal presentation members which still say `EffectLane` even though
  they project `AutomationTrack`, once compatibility-facing names can be removed without churn.

## Acceptance tests

The feature is not complete until these behaviours are covered:

### Editor

- On a 45-minute cue, add two keys 100 ms apart at 27:15, drag and nudge them without jumps.
- Drag a key across two neighbours; the same key remains captured and one undo restores the gesture.
- Lose pointer capture/window focus mid-drag; no later hover edits the document and the journal closes.
- Add, move, copy, paste, and delete zero/one/many selected keys by mouse and keyboard.
- Zoom/pan while idle, then edit against the exact viewport transform; no full-cue percentage leaks into
  hit testing or snapping.
- Resize/undock the editor without losing selection or changing authored times.
- Screen-reader names and keyboard alternatives cover every custom-lane operation.

### Timing/runtime

- Seeking to the middle of a segment seeds the correct audio, opacity, and outbound value immediately.
- Pause freezes automation; resume continues without a jump; a loop restarts cue-owned automation.
- Rehearsal from a group playhead samples elapsed automation rather than restarting over the remaining
  duration.
- An indefinite/live cue runs absolute-time automation and holds its last key until stopped.
- Shortening a cue does not rescale its keys; out-of-range keys are reported and preserved.

### Composition

- Static cue volume, per-send gain, automation, group trim, fade cue, stop fade, and master trim compose
  exactly once.
- One placement's opacity track does not alter another placement of the same decoded cue.
- A live base edit cannot snap an in-flight fade or automation value to full.
- Missing target/effect/plugin leaves an inert, visible, preflight-reported track.

### Migration

- Schema-1 **opacity** fixtures have the same effective sampled values before and after conversion at
  start, every key, every segment midpoint, and end.
- Schema-1 **volume** fixtures match exactly at start, every key, and end. Segment interiors deliberately
  do **not** match: schema 1 interpolated the linear gain factor, whereas cue volume is now authored,
  stored and interpolated in dB, converting to gain only after interpolation. A legacy `(0,0)→(1,1)` lane
  on a 0 dB cue sampled `0.5` at its midpoint and now samples `10^(−30/20) ≈ 0.0316`.

  This divergence was **adopted deliberately on 2026-08-12** rather than shimmed. One value domain for cue
  volume everywhere — editor, compiler, session, automation cue — is worth more than bit-identical replay
  of legacy interiors, and a dB-domain fade from silence is the perceptually better curve. The cost is
  real and must not be rediscovered as a bug: a schema-1 show with a volume lane sounds different through
  the middle of that lane, most audibly on fades from silence. `AShortLegacyVolumeLaneIsInterpolatedInDb`
  pins the new values so the change stays intentional.
- Unknown-duration legacy lanes remain intact until duration resolution; no guessed conversion is saved.
- Legacy group precedence and outbound endpoint/address data survive migration.
- Schema-2 projects are refused by schema-1 builds instead of opening without automation.

### Outbound

- Every segment law is honoured.
- Sends stay within the configured rate, slow endpoints coalesce, and normal completion lands exactly.
- Seek, pause, refire, stop, and panic each follow the declared policy.

## Recommended decisions to adopt now

1. Use **automation**, not **effect lane**, for timed property changes.
2. Store keyframe time in absolute cue milliseconds and values in native property units.
3. Persist keyframe, placement, and effect-instance IDs.
4. Keep the normalized curve dialog for easing shapes only.
5. Make property applicability catalog-driven; do not grow `EffectLaneKind` or use CLR reflection.
6. Start with cue volume and per-placement opacity, lowering to the framework paths that already compose
   correctly.
7. Build the single-cue, trimming-style automation editor before adding more target kinds.
8. Add automation cues only after cue-owned tracks share a proven seek/pause/loop evaluator.
9. Treat effect processors as instances whose parameters may opt into automation.
10. Bump the project schema because an old build ignoring automation would play the wrong show.

That sequence fixes the immediate editing problem without throwing away the runtime work already done,
and leaves HaCue2 with an extensible effect/automation model instead of a larger enum and a more crowded
square.
