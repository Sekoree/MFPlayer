# Cue Player: group timeline editor + volume automation

Status: design, 2026-07-27. Builds on `CuePlayer-Enhancements.md` (§1 fade curves, §6 per-route
gain fix). References against commit `c56f0619` (`test-enhancements`, since merged to
master — references apply to current master as-is).

## Problem

Fade-in/out and start/end trim are today four numeric boxes on the General tab
(`CuePlayerView.axaml:244-274`), and "when things happen inside a group" is expressible only as
`PreWaitMs` per cue plus the group fire mode. There is no way to *see* a group's arrangement in
time, no way to say "voice-over starts 12 s into the music bed at −6 dB while the bed ducks",
and no volume-over-time control at all (audio level is per-route `GainDb` only).

## What already exists to build on (important — the runtime is closer than it looks)

- `CueGroupFireMode.FireAllSimultaneously` already produces a **delay-sorted plan**: every
  fireable descendant with `groupPreWait + cue.PreWaitMs`, executed by `RunTriggerPlanAsync`
  batching equal delays and awaiting `WaitUntilDelayAsync`
  (`CuePlayerViewModel.cs:2281-2306,2662`, `.Transport.cs`). A timeline group is *exactly this
  plan* with authored start times instead of pre-waits.
- `WaitUntilDelayAsync` is already pause-aware (shifts its epoch while transport is paused,
  `CuePlayerViewModel.cs:3088`), so a paused timeline resumes correctly for free.
- Fades/trims already map per clip to the session (`ShowClipBinding.FadeIn/FadeOut/
  StartOffset/EndOffset`); the timeline editor is largely a *new view over existing fields*.
- `FadeRamp` provides the stepped-application loop (25 ms) that envelope playback can reuse;
  `TransportGroup.ApplyFadeLevel` applies a level to a clip's audio routes + layer opacities
  identity-guarded (`ShowSession.cs:2056-2087`).
- `WaveformControl` already exists (cue Preview tab) — reusable for waveform-in-block
  rendering, which is what makes a timeline editor actually usable.
- The group Now-Playing row already aggregates chain timelines (`ActiveGroupViewModel`), and
  progress arrives via the 200 ms coordinator poll.
- Measured cost of envelope evaluation is nil: 21.5 ns per piecewise-linear eval (20
  keyframes, binary search); at one eval per 512-frame block that is ~2 µs per audio-second
  per cue, and even per-sample evaluation would only be ~0.1% of a core per cue. Perf is a
  non-issue at any plausible show size.

## Model

```csharp
// CueGroupNode gains:
public enum CueGroupFireMode { FirstCueOnly, FireAllSimultaneously, ArmedList, Timeline }

// CueNode gains (meaningful inside a Timeline group; default 0 so old files load unchanged):
public int TimelineStartMs { get; init; }

// MediaCueNode gains:
public IReadOnlyList<CueAutomationPoint> VolumeEnvelope { get; init; } = [];

public sealed record CueAutomationPoint
{
    public int TimeMs { get; init; }          // CLIP-relative (post-StartOffset), see below
    public double LevelDb { get; init; }      // -60..+12, -inf allowed
    public FadeCurve CurveToNext { get; init; } = FadeCurve.Linear;
}
```

Decisions worth pinning:

- **Timeline time base = the group's plan epoch** (the moment the group fires), the same
  epoch `RunTriggerPlanAsync` uses. Start times are offsets on it. Pause shifts the epoch
  exactly as pre-waits do today.
- **Envelope time base = clip position** (what the progress poll already reports per cue),
  *not* group time. Clip-relative envelopes survive seeks, loops (envelope restarts per
  pass), and reuse of the cue outside the group. QLab behaves this way and operators expect
  it.
- **Level composition**: effective clip gain =
  `routeGain × cueMaster (future) × envelope(clipPos) × fadeRamp × stopFade`. Envelope and
  fades multiply; FadeIn/FadeOut stay the quick-access "handles" and the envelope is the
  detailed curve. (Alternative — compile fades *into* the envelope — rejected: it breaks the
  simple-cue editing path and multi-edit propagation.)
- `TimelineStartMs` lives on `CueNode`, so Action / Jump / Visualizer / (future) Fade cues can
  sit on the timeline as zero-length event markers — "at 00:12 send OSC", "at 01:30 fade the
  bed to −10 dB" — which composes beautifully with `FadeCueNode` from the enhancements doc.

## Runtime

**Phase A — start times (no framework change).** `BuildTriggerPlan` gets a `Timeline` branch:
children ordered by `TimelineStartMs + PreWaitMs` into the existing plan structure. Everything
downstream (batching, pause, Now-Playing rows, cancellation) already works. Group aggregate
row: reuse the summed-chain projection in `ActiveGroupViewModel.RecomputeAggregate` with the
authored span (`max(child start + child duration)`).

**Phase B — volume envelopes (session change).** New session API:
`ShowSession.SetClipEnvelope(cueId, IReadOnlyList<(TimeSpan, float, FadeCurve)>)`, applied by a
per-clip automation runner: a `FadeRamp.Start`-style loop (25 ms step is fine, ~1.5 dB max
step at typical slopes; use 10 ms if audible zipper noise ever shows on steep ramps) that
samples the envelope at the clip's current position (`group.Timeline.GetSnapshot()`) and
writes through `ApplyFadeLevel`'s route-target path. It must *compose* with, not fight, fades:
give `TransportGroup` one small level-mixer struct (`envelopeLevel`, `fadeLevel`,
`stopLevel` — product applied in one place) instead of today's each-fade-multiplies-capture
approach; that is the same refactor the Fade cue (enhancements §2) needs, so do it once.
Envelope data rides into the session via `ShowClipBinding` next to FadeIn/FadeOut
(mapper: `HaPlayShowMapper.MapClip`).

## UI

**Placement:** a dedicated resizable panel — recommend a separate window like
`ScriptEditorWindow` (the drawer's 440 px max height is too tight for lanes + envelope
editing), opened per group via a "Timeline…" button on the Group tab; read-only mini-strip of
the same content can later embed in the drawer.

**Layout:**

```
[toolbar: zoom fit/±, snap toggle, grid 1s/500ms/100ms, envelope-visibility toggle]
─────────────────────────────── time ruler (mm:ss) ────────────────────────────
Lane: 1.1 Music bed      ▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉
                          ◢ fade-in            envelope polyline           ◣
Lane: 1.2 Voice-over               ▉▉▉▉▉▉▉▉▉▉▉▉
Lane: 1.3 OSC lights up  ◆ (event marker at 00:04)
──────────────────────────────── ▲ playhead ───────────────────────────────────
```

- One lane per child, tree order. Block left edge = `TimelineStartMs` (drag to move, snaps to
  grid/other edges/playhead); block width = effective duration (probed duration − trims);
  **edge-drag = Start/EndOffset trim**; corner fade handles = FadeIn/FadeOut (drag length),
  right-click handle → curve choice.
- Waveform drawn inside media blocks via the existing `WaveformControl` pipeline (peak cache
  per source, computed off-thread like the Preview tab does).
- Envelope: overlay polyline per lane (toggle); click adds a point, drag moves (dB readout
  tooltip), del removes, right-click sets segment curve. Points clamp to the trimmed range.
- Zero-length cues (Action/Jump/Visualizer/Fade) render as diamond markers on their lane.
- Live playhead: position from the existing 200 ms `OnCueProgress` feed, interpolated by a
  ~60 ms UI timer between polls (matches the scrubber behavior; no new session traffic).
- Editing is gated by `IsCueEditMode` exactly like row drag-reorder today; during a show the
  window becomes a live view (playhead + levels) instead of an editor.
- All edits go through the existing VM properties (`StartOffsetMs`, `FadeInMs`, …) so
  multi-edit propagation, undo/duplicate/clipboard and persistence round-trips need no new
  code paths; `TimelineStartMs` and `VolumeEnvelope` are the only new persisted fields.

**Implementation notes:** custom `Control` with manual hit-testing (the
`CompositionPlacementCanvas` drag/resize pattern already in-tree is the template —
`CuePlayerView.axaml:616`); virtualize only if lanes > ~50; AOT-safe bindings like the tree
grid (`AotBinding.*`).

## Persistence & compat

- New fields default-safe (`TimelineStartMs = 0`, empty envelope, existing fire modes
  untouched) — old `.haplaycues` load unchanged; a Timeline group saved and opened in an old
  build degrades to first-cue-only fire (acceptable; note in release notes).
- Add the new records to `CueListJsonContext` / `HaPlayProject` context **including** the
  currently-missing `JumpCueNode`/`VisualizerCueNode` entries (enhancements doc §6).
- Copy/paste: envelope + start time travel with the cue via `CueClipboardDocument`
  automatically once they're model fields.

## Test plan

VM-level (headless, existing harness): Timeline plan ordering/delays incl. pause-shift;
trim/fade handle edits round-trip to model; envelope JSON round-trip; envelope+fade
composition math (pure function — test the level mixer directly); old-file load defaults.
Session-level: automation runner applies expected levels against a fake clock; envelope
survives seek (clip-relative); stop fade preempts envelope. UI: hit-test math for
block/handle/point picking as pure functions.

## Phasing

1. **A**: Timeline fire mode + start times + window with lanes/drag/trim/fade handles (no
   envelope, no waveforms). Immediately useful; zero framework change.
2. **B**: Volume envelopes (session level-mixer + automation runner) — shared groundwork with
   `FadeCueNode`.
3. **C**: Waveforms in blocks, snapping polish, event markers for non-media cues.
4. **D**: Duck presets ("sidechain-lite": authoring helper that writes an envelope dip into
   the bed for the duration of an overlapping voice-over lane — pure editor sugar, no runtime).
