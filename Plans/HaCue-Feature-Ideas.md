# HaCue feature ideas

Status: backlog — **not** first-release scope  
Date: 2026-07-30  
Companion to: `Plans/HaCue-Extraction-And-Project-Audio-Patch-Plan.md`

Enhancements the HaCue split makes possible or worthwhile, with what exists today, what it would take,
and what it depends on. Four items from this list were promoted into the extraction plan's first release
(provisional decision 11) and are only summarised here; everything else is unscheduled.

Every "does not exist" below was checked against the code on 2026-07-30, branch
`next-fix-enhance-round`. The cue player is more complete than a feature list suggests — it already has
cue numbers and labels, pre-wait, notes, colour tags, auto-follow/auto-continue, jump cues with
random-target picking, fade cues with target lists and fade-everything, wall-clock schedules, MTC chase,
per-cue MIDI/OSC/hotkey triggers, timeline groups with authored offsets, playlist and armed-list group
modes, compositions with output mapping, waveform extraction, projectM cue layers, preview/audition,
crash recovery, autosave, and a remote HTTP API. Proposals that duplicate any of that are excluded.

It also already has more automation than a feature list suggests, which reshapes several ideas below:
`TimelineEditorWindow` is a real per-group lane editor with a snap grid, a live playhead fed from the
200 ms cue-progress samples, and **draggable volume-envelope keyframes** drawn over each media lane
(`CueAutomationPoint` — clip-relative times with a curve to the next point, rendered and hit-tested by
`TimelineCanvas.RenderEnvelope`). There is even a "Duck under…" authoring helper
(`TimelineDuckMath`) that detects overlapping voice-over lanes and splices a dip into the bed's
envelope as ordinary keyframes. So audio level automation is **done**; what is missing is narrower and
more specific than "add automation".

## Promoted to first release

Specified in the extraction plan; listed here only so this document reads as the whole picture.

| Idea | Plan section |
|---|---|
| Edit-command undo journal | "Editing model: an undo journal, decided now" |
| Show mode as the launch state | "Show mode is a state, not a checkbox" |
| Patch snapshots + patch cues | "Target HaCue project model" (`PatchCueNode`) |
| Preflight / relink / consolidate | "Preflight, relink, and consolidate" |

## Tier 1 — high value, well-bounded

### Automation lanes — for other internal parameters, and for outbound OSC/MIDI

The envelope machinery exists and works; it is simply wired to exactly one destination. `MediaCueNode`
carries a `VolumeEnvelope` of `CueAutomationPoint`, the timeline canvas draws and edits it, and
`ShowSession.StartEnvelopeRunner` rides it into the voice's level composition. Nothing else in the
document can be automated:

- `CueVideoPlacement.Opacity` is a single static `double`, and a fade cue's `AlsoFadeVideoOpacity` ramps
  opacity only for the duration of that fade. So "take this video to 50 % opacity at these points, then
  ramp back to 100 % over four seconds" cannot be authored at all, while the audio equivalent can.
- An **action cue cannot change a value over time either.** `ActionCueNode` is four fields — `ActionKind`
  (`OSCOut` | `MIDIOut`), `EndpointId`, `AddressOrMessage`, and `List<string> Arguments` — so it fires one
  message with static arguments, once. `FadeCueNode` is the only kind carrying a duration and a curve, and
  its targets are *cue ids*: it fades audio level and video opacity of playing cues and cannot be aimed at
  an OSC address or a MIDI controller.

The control side cannot cover for this, deliberately. Control scripts have `osc` and `midi` send APIs but
**no timing primitives**; `ControlScriptApiLibrary`'s time module says so outright — *"Recurring/delayed
execution is provided by the declarative Periodic trigger … so scripts never spin loops; this library is
the time-reading surface for debounce/elapsed/timestamp logic."* `ControlPeriodicOSCSendConfig` and
`ControlDeviceTaskProfile` do recurring sends, but with a fixed address, fixed arguments and an interval —
a keep-alive (the default name is `/xremote`, the X32 subscription refresh), not a ramp. Nothing in
`S.Control` interpolates; the only interpolation in that tree is MTC position. And a cue cannot invoke a
script at all, since `CueActionKind` has no script kind — the only route is loopback through HaPlay's own
OSC listener, which still hits the no-timer wall.

**Today's workaround, so the cost of not having this is concrete:** N action cues in a Timeline group at
authored `TimelineStartMs` offsets, each sending one static value. A two-second ramp at 25 Hz is 50 cues.

The unifying observation is that **an action cue with a duration is parameter automation with an external
destination** — same keyframes, same curves, same editor, different actuator. So generalise the keyframe
list into a keyed lane and let the target be internal *or* outbound:

```csharp
public sealed record CueAutomationLane
{
    public CueAutomationTarget Target { get; set; }   // Volume, LayerOpacity, ChromaThreshold,
                                                      // OscArgument, MidiControlChange, …
    public Guid? TargetId { get; set; }               // placement / layer / send, or the action endpoint
    public string? Address { get; set; }              // OSC address, or CC number for MIDI
    public int SendRateHz { get; set; } = 25;         // outbound lanes only — see below
    public List<CueAutomationPoint> Points { get; set; } = [];
}
```

Worth keeping honest about the cost: this is **three features wearing one UI**, and only the editor is
shared.

| Lane kind | Actuates | Rate | Failure mode |
|---|---|---|---|
| Audio level | Router gain slots, via the existing envelope runner | Per audio chunk | Already handled |
| Video parameter | Compositor, via the placement spec | Per composited frame | A late frame holds a stale value |
| Outbound OSC / MIDI | Action endpoint sender | `SendRateHz`, explicit | Flooding, or a ramp that never lands |

Two requirements belong to outbound lanes specifically, and neither is optional:

- **An explicit send rate, not a per-frame or per-chunk one.** Nobody wants a lighting desk taking 60
  messages a second per lane. 25 Hz is a sane default and the author should be able to lower it. Rate is a
  property of the lane because a fader ramp and a colour-temperature ramp reasonably differ.
- **The ramp must land exactly on the final keyframe value.** Emitting at a fixed rate means the last tick
  generally falls short of the end of the ramp, which leaves a desk holding 0.7482 instead of 0.75 —
  invisible in rehearsal and wrong for the rest of the show. The runner sends the terminal value explicitly
  when the lane ends, including when the cue is stopped or the show panics mid-ramp. Alongside that,
  coalesce: if an endpoint is slow or unreachable, drop intermediate values rather than queueing a backlog
  of stale ones, and still send the final value when it recovers.

Sensible build order: **layer opacity** first, because the plumbing to set it live already exists (fade
cues do it) and the operator payoff is immediate; then **outbound OSC**, which needs no new runtime path
beyond a timer and the existing sender; then MIDI CC, chroma-key threshold, brightness/contrast, placement
rect and visualizer parameters, all of which are the same shape once the lane model is in.

Constraint carried from the extraction plan: an automation lane must feed the documented composition
chain, never become a second authority over the same value. Volume automation already obeys this — the
envelope multiplies the fade level rather than replacing it — and any new internal lane has to do the
same. Outbound lanes are exempt from that rule for the simple reason that they compose nothing: they are
the authority for a value that lives in another system entirely, which is also why they must not be undone
on cue stop the way an internal parameter is.

Depends on: nothing structural for the model. The video half depends on the compositor exposing the
parameter as live-settable per placement; the outbound half depends only on the action-endpoint sender
already used by action cues.

### Reach the envelope editor from a cue that is not in a timeline group

`TimelineEditorWindowViewModel` is constructed per group, and its lanes are `Group.Children`. The only
way to see or edit an envelope today is therefore to put the cue inside a Timeline-mode group.

That misses the most common case for level automation: **one long file whose sections differ in level**.
That is a single cue, usually sitting in an ordinary list, and it already has a `VolumeEnvelope` field
with no reachable editor.

Allow opening the same editor on a single media cue — one lane, drawn on the clip's own axis rather than
the group's plan epoch. The canvas already renders exactly this shape for one block, so the work is an
entry point, a time-base switch, and hiding the lane-arrangement affordances that only make sense for a
group. Pairs naturally with waveform markers: authoring level changes against a waveform is much easier
than against an empty block.

Depends on: nothing. This is the cheapest large win in this document.

### A real crossfade editor for playlist groups

The runtime already performs proper dual-voice crossfades — `CuePlaylistOptions.CrossfadeMs` makes a
playlist fire its next item early through the crossfade replacement path, and `LoopCrossfadeMs` does the
same at a loop wrap, so both voices genuinely overlap and both stay routed.

What is missing is entirely in the authoring layer. `CrossfadeMs` is **one integer for the whole group**:
every transition gets the same length, there is no choice of curve per side, and nothing shows the
overlap. Tuning a 20-song playlist means editing one number that is wrong for most of the transitions.

Give a playlist group a horizontal strip of its items end to end, with a draggable overlap handle
between neighbours and an out-curve / in-curve per overlap. Persist as an optional per-item override
that falls back to the group's existing `CrossfadeMs`, so old files are unchanged and the group default
stays meaningful.

Two things the editor should show, because they are the failures people hit: an overlap longer than the
outgoing item's remaining tail (the plan's crossfade-trim gotcha), and an item whose trim leaves less
material than the requested fade.

Depends on: nothing in the runtime — this is a view over behaviour that already works.

### Post-wait

`CueNode.PreWaitMs` exists (`UI/HaPlay/Models/CueList.cs:250`); there is no post-wait, so "wait 3 s
after this cue completes, then auto-continue" cannot be expressed. Auto-follow currently fires at the
boundary with no authored gap.

Add `PostWaitMs` to `CueNode`, honoured by the auto-follow/auto-continue boundary. Watch the interaction
with `EndTargetCueId` and with a group's fire mode: the wait belongs to the *completing* cue, and a
timeline group's authored offsets already express spacing, so post-wait should be ignored (or rejected at
validation) inside `Timeline` groups rather than silently double-counted.

Depends on: nothing. Cheap.

### Loudness normalisation

No LUFS or loudness analysis exists anywhere in the repo. A show assembled from forty differently
mastered files is levelled by hand, per cue, every time.

Analyse integrated loudness on media add or on demand, store the measured value on the cue, and offer
"normalise selection to target LUFS" writing per-cue `LevelDb`. Two properties matter: the measurement is
cached against (path, stream, trim window) so it is not recomputed per fire, and the applied gain is a
plain authored level the operator can see and override — never a hidden runtime multiplier. Trim-aware
measurement is the interesting part, since a cue's audible content is its trimmed window, not the file.

Depends on: a decode pass off the playback path. Fits `WaveformExtractor`'s existing shape closely enough
that it may share its plumbing.

### Waveform markers and trim-from-marker

`WaveformExtractor` produces peaks only. (`CuePoint` in `CueList.cs:220` is an unrelated
mapping-mesh type — do not reuse the name.) There is no way to mark a point in a file and set trim,
fade or envelope times from it.

Store markers per media cue (clip-relative times, so they survive trim edits the way the volume envelope
already does), draw them on the waveform and the timeline, snap trim/fade handles to them, and allow
jump-to-marker while playing. Naming them makes the notes column much more useful.

Depends on: nothing structural. Journaled edits, so best after the undo journal.

### Cue-sheet export

`ExportCompositionsAsync` exists (`CuePlayerViewModel.Persistence.cs:63`); there is no cue-sheet
export. The stage manager's paperwork is currently a screenshot.

Export the loaded list(s) as CSV and Markdown: number, label, type, target/media, trigger mode,
pre/post-wait, duration, level, notes, colour tag, disabled state. Straightforward once the document is
the source of truth rather than the view models.

Depends on: nothing.

## Tier 2 — high value, larger

### Tracking backup / hot standby

The single feature that most separates a show-critical cue player from a capable one, and the one that
only makes sense *because* HaCue is standalone: nobody will run a second full HaPlay to back up cues.

A backup instance opens the same project and follows the primary — standby pointer, fire events,
transport state, arm state — staying silent until takeover, then continuing from the primary's position.
The existing remote API already carries the verbs (`go`, `pause`, `resume`, `stop`, `panic`, `arm`,
`disarm` — `UI/HaPlay/Remote/RemoteApiDispatcher.cs:151-347`); what is missing is a state-mirror
channel, a project-identity check so a backup cannot follow a divergent document, and a takeover
handshake with a defined answer for "primary came back".

The hard parts are the ones that always sink this feature if left late: what happens mid-cue (does the
backup pre-roll every armed cue, or accept a gap on takeover?), how divergent edits are refused rather
than merged, and how the operator can tell at a glance which machine is live.

Depends on: the remote API and the audio bay being settled. Realistically Phase 7 or later.

### Headless show runner

`ShowDocument` sidecars already exist and the extraction plan versions them; the bay must already work
with no audio device via `NullClockedAudioOutput`. HaPlay's entry point parses only diagnostic switches
(`UI/HaPlay.Desktop/Program.cs`), so there is no headless mode today.

Run a compiled show with no UI, driven by schedule, timecode, OSC/MIDI, or the remote API — for
installations, museums, and render boxes. Most of it falls out of Phase 3 and of making preflight
headless; the new work is a lifecycle without a window and an honest answer about which endpoints a
generic host can open (the extraction plan's ShowDocument-v2 question).

Depends on: Phase 3, plus the endpoint-resolution decision in "ShowDocument evolution".

### Scope the cue list to a group (group as root)

The cue tree shows everything in the loaded list(s). That is fine for 80 cues and unusable for the shape
this application invites: a playlist of 20 songs where each song is a group carrying 20–30 attached
lighting, projection and patch cues is 400–600 rows, and the operator is working inside one song at a
time.

Let any cue list **or any group** be selected as the current root, so the tree shows only that subtree
with a breadcrumb back out. Presented as a second tab beside the active-cue list — the panel is already
there, and "which part of the show am I looking at" belongs next to "what is playing".

The root scope is **view state, machine-local**, never document state. It must not travel in the project
and must not survive as a saved layout that hides half a show from the next operator.

Three interactions to settle before building it, because getting them wrong is worse than not having the
feature:

- **GO ignores the scope.** The scope is a view filter, not a transport boundary. An operator who has
  drilled into song 7 and presses GO must still fire the show's next cue.
- **The active list ignores the scope.** Cues sounding outside the current root have to stay visible, or
  scoping becomes a way to lose track of running audio.
- **Standby may leave the scope.** Auto-follow across a group boundary has to move standby out of the
  current root; the view should follow it (or say clearly that it has moved), never silently strand it.

Depends on: nothing. It is a filter over the existing tree plus a breadcrumb, and it makes the
per-list-playhead question easier to reason about by giving "current scope" a name in the UI first.

### Independent per-list playheads

`StandbyCueNode` is a single property on `CuePlayerViewModel` — one playhead across merged lists.

**Decided for now: stay single.** The scenario that argues against it is real, though, and worth writing
down rather than rediscovering: two screens each driven by a different list or group, output running
independently, needing their own GO. Sound and video called separately by two operators is the same shape.

The cheap insurance is a model decision rather than a UI one. **Store standby per cue list, with exactly
one list marked active**, instead of a single global pointer. The UI stays single-playhead — one standby
highlight, one GO, one transport row — but going multi later becomes a view change rather than a model
migration, and the "group as root" scope above already gives the UI a notion of which part of the show is
current.

If it is ever built out: hotkeys and triggers grow a list scope, the remote API's `go` grows a target,
`CueTriggerService`'s arm gate becomes per-list, and the transport row becomes one strip per active list.
That is the part not to pay for now.

### Timecode generation (MTC / LTC out)

`CueTimecodeChaseService` decodes incoming MTC; nothing generates it. HaCue can follow another system's
clock but cannot be the master, which is often the more useful role for the machine holding the media.

Generate MTC (and optionally LTC on an audio output — the logical patch makes that a natural
destination: an LTC generator patched to a logical channel like any other source). The clock question is
already answered by the plan: the program clock is the bay master's audible clock, so generated timecode
should be derived from it rather than from wall time, or the timecode and the audio will drift apart.

Depends on: the bay's clock policy. LTC-on-a-logical-channel depends on the patch.

### MIDI Show Control

Only raw NoteOn/CC/PC in and raw MIDI out exist (`CueTriggerService`, `CueActionKind`). MSC is the
standard for talking to lighting desks and other playback systems, and "GO cue 12 in list 3" as a
first-class message is much more robust than agreeing on a note number.

Both directions: respond to inbound MSC GO/STOP/RESUME/LOAD addressed to a configured device id, and
emit MSC from action cues. Pairs naturally with per-list playheads, since MSC addresses cue *lists*.

Depends on: `HaControl.Input` (the shared MIDI layer the extraction plan extracts in Phase 2).

## Tier 3 — worth doing, lower urgency

### Controller feedback out

The X32/XTouch layer work lives in HaPlay's Control workspace, which the extraction plan leaves in
HaPlay. HaCue would therefore ship with trigger *input* but no LED, scribble-strip, or fader feedback —
a control surface that fires cues but cannot show which one is standing by.

Either extract the feedback half alongside `HaControl.Input`, or accept input-only for the first release
and say so explicitly. This is worth an early decision even if the work is late, because "my surface
went dark" is a support question, not a feature request.

### Per-cue and per-bus audio effects

The extraction plan deliberately excludes logical-bus insert effects from the first patch
implementation, and physical output effects already exist after the patch. Per-cue EQ/filter/dynamics and
per-logical-channel inserts are the natural follow-up, in that order — the framework has an
`AudioEffectBus` already.

Keep the plan's rule: effects must not become a second gain authority. Anything that changes level has to
compose into the documented chain, not beside it.

### Delay, polarity, and solo on the patch

Open question 10 in the extraction plan already recommends deferring these. Per-cell or per-logical-channel
delay is the one operators ask for first (speaker alignment); polarity inversion is trivial once cells
carry more than gain/mute; solo should be a non-persisted editor audition function, never document state.

Depends on: the patch being stable, and delay in particular interacting with the clock's reported latency.

### Multi-audio-stream / stem cues

Provisional decision 4 routes the channels of *one* selected audio stream. Decoding several container
audio streams at once (language stems, split music/effects beds) is a separate playback feature and a
larger one — it changes what a "source channel" means in the N×V editor.

### Media inventory view

A project-wide list of every referenced media file with size, duration, format, resolve status, and which
cues use it. Mostly a different presentation of what preflight and reference counting already compute, so
it is cheap once those exist — and it is where relink and consolidate most naturally live in the UI.

## Explicitly not proposed

- **A soundboard / cart wall in HaCue.** The extraction plan keeps the soundboard in HaPlay, and
  duplicating it would recreate the coupling the split exists to remove. If HaCue needs instant-fire
  buttons, they should fire *cues* — the existing per-cue hotkeys and triggers already do this, and a
  cue-native "hit list" panel over them would be a UI, not a second playback engine.
- **Reviving `VirtualAudioChannels`.** Dead by design; the extraction plan is explicit that the new patch
  must not reactivate it.
- **Undo of transport actions.** Undo means "un-edit my document", never "un-play my show". Stated in the
  plan's journal section for exactly this reason.
