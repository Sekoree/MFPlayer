# Cue Player enhancements

Status: findings + designs, 2026-07-27. References are against commit `c56f0619`
(`test-enhancements`, since merged to master — references apply to current master as-is).
Companion docs: `CuePlayer-Timeline-Editor.md` (group timeline + volume automation),
`HaViz-Clocking-And-Headless-Audio.md`, `General-Review-Findings-2026-07-27.md`.

Current model recap (all in `UI/HaPlay/Models/CueList.cs`): cue kinds group / media / action /
comment / jump / visualizer; trigger modes Manual / AutoFollow / AutoContinue; group fire modes
FirstCueOnly / FireAllSimultaneously / ArmedList (ArmedList is declared but has **no execution
branch** — `BuildTriggerPlan` only special-cases FireAllSimultaneously,
`CuePlayerViewModel.cs:2281-2306`). "Random" today = `JumpCueNode.RandomTarget`.

---

## 1. Configurable stop fade (requested)

Today: `ShowSession.SoftStopFadeDuration = 750 ms`, `private static readonly`
(`S.Media.Session/ShowSession.cs:58`). It is the fallback for operator Stop/Panic/per-cue Stop
when the clip has no `FadeOut` (`:2020`), the `FadeOutAndStop` natural end fallback (`:1543`),
and — with **no override possible at all** — the visualizer fade (`:2113`). The curve is always
linear (`FadeRamp.LevelDown`), step 25 ms. HaPlay's Stop button passes nothing
(`CuePlayerViewModel.Transport.cs:254` → `session.StopAllAsync()`).

Design:

1. **Framework**: make it an instance option — `ShowSessionOptions.DefaultStopFade`
   (default 750 ms for compat) plus explicit overloads `StopAsync(groupId, fade: TimeSpan?)` /
   `StopAllAsync(TimeSpan? fade)`, `null` = session default. Give
   `FadeOutAndRemoveVisualizersAsync` a duration parameter (callers: operator stop → the stop
   fade; visualizer Stop cue → that cue's `TransitionSeconds`, which the operator already
   configures for preset cross-fades and would expect to apply here).
2. **HaPlay**: per-cue-list setting `StopFadeMs` in `CueList` (surfaced in
   `CueListSettingsDialog`), with a global default in app settings. `Panic` should stay
   hard-cut or very short (say fixed 100 ms) — panic means *now*; make that a second setting
   `PanicFadeMs` defaulting to 0.
3. Precedence (unchanged in spirit): per-cue `FadeOutMs` > cue-list `StopFadeMs` > app default.
4. **Fade curves** while touching this: add `FadeCurve { Linear, EqualPower, Exponential, SCurve }`
   to `FadeRamp` (pure functions next to `LevelDown/LevelUp`), persist per cue
   (`FadeInCurve`/`FadeOutCurve`, default Linear so old files load unchanged) and as the
   stop-fade curve setting. Linear *gain* fades sound like they "fall off a cliff" at the end;
   Exponential (dB-linear) or EqualPower is what operators expect from a desk.

Test gap to close with it (from the audit): nothing asserts the stop-fade duration or ramp
shape today — add a fake-clock test around `StopGroupsCoreAsync` when making it configurable.

---

## 2. Fade as a cue type with multiple targets (requested)

Rather than only bettering the implicit fades, add a first-class **`FadeCueNode`** (QLab's
"Fade cue" precedent). Firing it ramps its targets over a duration; it occupies the chain like a
visualizer cue (instant, with optional `DurationMs`-style chain hold).

Model sketch:

```csharp
public sealed record FadeCueNode : CueNode
{
    public List<Guid> TargetCueIds { get; init; } = new();   // media cues and/or groups
    public bool TargetAllPlaying { get; init; }              // ignore list, fade everything active
    public double TargetLevelDb { get; init; } = double.NegativeInfinity; // -inf = to silence
    public int DurationMs { get; init; } = 3000;
    public FadeCurve Curve { get; init; } = FadeCurve.Linear;
    public bool StopWhenSilent { get; init; } = true;        // release clip at -inf, else keep running
    public bool AlsoFadeVideoOpacity { get; init; } = true;  // ramp layer opacity in step
}
```

Why it fits the architecture well: the whole ramp machinery already exists —
`FadeRamp.Start` + `TransportGroup.ApplyFadeLevel(player, routeTargets, startScale,
startLayerOpacities, level)` is exactly "set this clip's audio+video to level L now", already
identity-guarded against clip replacement mid-fade (`ShowSession.cs:2056-2087`). What's missing
is only (a) fading to a **non-zero target level** (levels today always ramp 1→0 or 0→1) and
(b) a public per-clip entry point, e.g. `ShowSession.FadeClipAsync(cueId, targetScale,
duration, curve, stopWhenSilent)`. A fade *up* (e.g. from a previous fade-down, or a cue started
at reduced level) falls out of the same call.

Execution wiring copies the visualizer-cue pattern: `CuePlayerViewModel.ExecuteCueAsync` case →
host `FadeCueExecutor` in `CueShowSessionCoordinator` → session calls per target. Targets
resolve by stable cue id exactly like Jump targets (reuse the number/range parsing UI,
`SelectedJumpTargetsText` infra). A Fade cue with `TargetAllPlaying + StopWhenSilent + -inf`
**is** the configurable "fade out everything and stop" — it subsumes §1's stop button behavior
(the Stop button becomes "fire an implicit all-target fade cue with the list's StopFadeMs").

Level bookkeeping detail: today's fades capture `ActiveAudioScale` at claim time and multiply.
A fade cue needs the group to track a persistent "current level" per clip so consecutive fades
compose (fade to −10 dB, later fade to −inf starts from −10). Add `TransportGroup.ClipLevel`
(default 1) that `ApplyFadeLevel` writes through, instead of treating every fade as 1→x.

---

## 3. Playlist group (requested)

Today shuffle/looping is faked with Jump cues (`RandomTarget` + cycle guards). A dedicated
**playlist mode on groups** expresses the intent directly:

```csharp
public enum CueGroupFireMode { FirstCueOnly, FireAllSimultaneously, ArmedList, Playlist }

// on CueGroupNode, used when FireMode == Playlist:
public sealed record CuePlaylistOptions
{
    public bool Shuffle { get; init; }
    public bool AvoidImmediateRepeat { get; init; } = true;  // reshuffled-pass boundary too
    public int LoopCount { get; init; } = 1;                 // 0 = infinite, N = passes
    public int? PlayCount { get; init; }                     // play only N items per pass (subset)
    public int CrossfadeMs { get; init; }                    // 0 = butt splice (see note)
    public bool ReshuffleEachPass { get; init; } = true;
    public CuePlaylistEndBehavior EndBehavior { get; init; } // Stop, AdvancePastGroup, Hold
}
```

Runtime design — transport layer, like Jump cues (no ShowDocument change needed):

- Firing the group (GO/standby resolves into it) starts a **playlist run**: session-only state
  `{ groupId → remaining pass count, shuffle bag (unpicked child ids), history }` held in the
  VM beside `_lastRandomJumpTargetIds` (same "session state, not project data" philosophy,
  `CuePlayerViewModel.Transport.cs:154-156`).
- On each child's natural end (`OnMediaCueNaturallyEndedAsync`,
  `CuePlayerViewModel.cs:2350`), if the ended cue's containing group is in an active playlist
  run, pick the next child from the bag (shuffle) or in order, refill the bag on pass
  boundary, decrement `LoopCount` when a pass completes, honor `PlayCount`, then fire via the
  existing `GoCore`-style internal path. Shuffle picking should use a **bag** (draw without
  replacement), not per-step `Random.Next` — bags guarantee "every song once per pass", which
  is what operators mean by shuffle.
- Standby display: show "Playlist 3/12 · pass 1/2" in the Now-Playing group row
  (`ActiveGroupViewModel` already aggregates chains; playlist is a third aggregate mode).
- Manual GO while a playlist runs = skip to next pick (natural and useful); Stop ends the run.
- `CrossfadeMs > 0` requires two simultaneously-active clips in one transport group, which
  `TransportGroup` (single `Active`) doesn't support today — deliver playlist v1 with butt
  splice + the per-cue FadeIn/FadeOut overlap illusion (fade-out of A runs while B fades in
  only if B is fired before A releases, i.e. fire-on-fade-start), and treat true crossfade as
  the follow-up framework feature (see §6 "Deck-style dual voice").

Decide `ArmedList`'s fate in the same change: it's persisted, selectable, and does nothing —
either implement (armed = each GO fires the next child, no auto-advance; that is nearly
`Playlist` with `Manual` advance) or fold it into Playlist options and migrate the enum value.

---

## 4. Timed triggers (requested)

Nothing in HaPlay or the framework fires on wall-clock time today (audit checked; the only
schedule-shaped primitives are `SoundboardGrid.TryCreateScheduledFire`, which computes a
quantized `When` that **no caller dispatches**, and the Control side's
`ControlPeriodicOSCSendManager.IsDue(utcNow)` pattern).

Model — a schedule is an *additional* trigger on a cue, not a replacement for `TriggerMode`
(a scheduled cue can still be fired manually):

```csharp
// optional on CueNode; null = no schedule (old files unchanged)
public sealed record CueSchedule
{
    public CueScheduleKind Kind { get; init; }        // TimeOfDay, DateTime, Recurring
    public TimeOnly? TimeOfDay { get; init; }         // TimeOfDay + Recurring
    public DateTimeOffset? At { get; init; }          // DateTime (one-shot)
    public DaysOfWeekMask Days { get; init; }         // Recurring
    public int GraceMs { get; init; } = 5000;         // late-fire window (see misfire policy)
    public bool Enabled { get; init; } = true;
}
```

Runtime — one central `CueSchedulerService` in HaPlay (not per-cue timers):

- A single `DispatcherTimer` at ~250 ms computes `next due` across all armed schedules and
  compares against `DateTimeOffset.Now` (local time — shows are local-time creatures; store
  `TimeOfDay` as local, one-shot `At` as `DateTimeOffset` so it survives timezone moves).
- **Armed gate**: schedules only fire while (a) the project's "schedule armed" master toggle is
  on and (b) `IsCueEditMode` is off. An operator editing at 14:59 must not have Q50 fire into
  the room. Surface armed state prominently (transport row chip + the cue row shows a clock
  badge with countdown).
- **Misfire policy**: on tick, anything due within `[due, due+GraceMs]` fires; older than grace
  is skipped and logged (app sleep/suspend recovery — never "catch up" a stack of missed
  fires). This is the classic scheduler mistake worth designing out from day one.
- Firing path: the exact per-cue path GO uses for operator selections
  (`FireOperatorSelectedCueAsync`) so pre-waits, group semantics, jump resolution and
  Now-Playing all behave identically; schedule fires also clear `_immediateJumpChain` like an
  operator GO.
- DST note: recurring TimeOfDay resolves against local wall time each day; a skipped/duplicated
  hour follows the OS clock (document it, don't fight it).

Future extension hook (already half-declared in the framework): `TimecodeSyncKind { Mtc, Ltc,
Smpte }` exists in `TriggerBindingSet.cs:33` with no implementation — the scheduler service is
the natural place for an eventual MTC/LTC chase source ("fire at timecode hh:mm:ss:ff").

---

## 5. Kill remaining default-audio-device dependence in HaPlay (requested)

The cue routing core is clean by design — the mapper's explicit-silence contract ("never an
implicit default device", `HaPlayShowMapper.cs:211,282`) is the right model. The leaks are at
the edges:

| # | Leak | Where | Fix |
|---|---|---|---|
| 1 | Cue **preview** picker defaults to "Default device" (null id) and preselects it | `CuePlayerViewModel.cs:101-113` → `VoicePlayer.cs:135` | Persist the preview device per project; default the picker to the first *configured cue output line's* device when one exists, "Default device" only as last resort. Grey-noise case: preview on a show machine should never surprise the house PA. |
| 2 | Soundboard tile on a non-PortAudio line resolves `device = null` → `VoicePlayer` falls back to session default device | `CueShowSessionCoordinator.cs:645-648`, `VoicePlayer.cs:255` | Treat "line is not a PortAudio output" as a config error surfaced on the tile (red badge + toast), not a silent reroute to default. |
| 3 | `ShowSession` captures `_outputDeviceId = default device` **once at construction**, never refreshed | `ShowSession.cs:260-263` | Re-resolve lazily at use, or subscribe to device-list changes; a USB interface plugged after app start currently never becomes the fallback. Better: make the fallback opt-in (`ShowSessionOptions.AllowDefaultDeviceFallback = false` for HaPlay). |
| 4 | `PortAudioOutputRuntime.MatchBackendDevice` last-resort returns the **system default** when id- and name-match fail | `UI/HaPlay/OutputPreview/PortAudioOutputRuntime.cs:229` | A configured line silently landing on the wrong physical output is worse than failing: throw like `ResolveCurrentOutputDevice` does (`:295-298`) and let the existing `RebindMissingOutputsDialog` handle it. |
| 5 | Blank-name PortAudio *input* descriptors resolve to default input | `PortAudioCaptureDecoderProvider.cs:136,179` | Acceptable for ad-hoc capture; just persist the resolved name back so the choice is stable. |

(1)–(4) are all "show plays out of the laptop speakers" bugs waiting for a gig. None are hard;
#3 and #4 are the ones with silent-failure character.

---

## 6. Additional enhancement ideas (unrequested, from the audit)

**Per-cue master volume + dB-mean bug.** Cues have no master level; per-route `GainDb` is the
only control, and the mapper collapses a line's routes to **one gain = linear of the *mean of
the dBs*** (`HaPlayShowMapper.cs:279`). Two routes at 0 dB and −60 dB become one −30 dB gain
for both — almost certainly not intended. Fix: per-route gains in the `ChannelMap` matrix
(scale each matrix cell instead of one line gain). Then add `MediaCueNode.LevelDb` as a master
multiplier — it's also the natural anchor for fade cues (§2) and envelopes (timeline doc).

**Live-editable stop-fade "panic slider".** With §1/§2 in place, a master fader UI over
`TargetAllPlaying` fade infrastructure gives a manual "bring the show down by hand" control —
operators love a physical-feel master out (bindable to MIDI via the existing control layer).

**MIDI/OSC/hotkey triggers per cue.** `TriggerBindingSet` (`S.Media.Session`) already models
MIDI/OSC/Keyboard trigger sources with retrigger policies, and HaPlay has full MIDI/OSC I/O in
the Control workspace — but cues can only be fired by GO/REST today (`RemoteApiDispatcher.cs:137`
covers transport, not individual cues). Wire "fire cue by id" into the remote API and the
control mapping layer; the cue drawer gets a "Triggers" tab (schedule from §4 + MIDI/OSC/hotkey).

**Group-boundary crossfade / dual-voice groups.** True crossfade between consecutive cues
(playlist §3, and AutoFollow chains generally) needs two live clips per transport group. The
deck player already runs A/B voices; porting that shape into `TransportGroup` (an `Outgoing`
slot next to `Active` that only exists during a fade window) unlocks playlist crossfade,
"fire next early" DJ-style takeovers, and seamless loop-with-crossfade for static/ambient cues.
Biggest framework item here; worth its own design doc when prioritized.

**`.haplaycues` files have no version guard.** `CueList.Schema` (`"HaPlayCueList/v3"`) is
written but never validated on load, unlike every other document type (`.haplayproj`,
`.haplaycuelists`, `.haplaycomps`, `.haplayboards` all range-check). A future v4 file will
half-deserialize into v3 defaults instead of failing closed. One `if` in `CueListIO`
(`ProjectIO.cs:102-122`).

**JSON context omissions.** `CueListJsonContext` (`CueList.cs:598-626`) doesn't list
`JumpCueNode`, `VisualizerCueNode`, `CueChromaKey`, `CueColorAdjust` in `[JsonSerializable]`
(they ride in via `[JsonDerivedType]` polymorphism today — tests pass — but the omission is
fragile under AOT trimming and inconsistent with the listed siblings); `HaPlayProject.cs`'s
context has the same gap. Cheap consistency fix.

**Pre-wait visibility.** `PreWaitMs` runs invisibly (50 ms slices in `WaitUntilDelayAsync`).
Show a countdown on the Now-Playing row / standby badge during pre-wait — operators need to
see *why* nothing is sounding yet.

**Test gaps worth closing** (from the audit): stop-fade duration/curve assertions; ArmedList
(whatever its fate); behavior when a configured audio output device is missing (the throw
path and the rebind dialog); scheduler misfire policy once §4 exists.

---

## Suggested build order

1. §1 configurable stop fade + curves (small, framework + settings plumbing, user-visible).
2. §5 device-dependence fixes #2/#4 (silent-reroute bugs), then #1/#3.
3. §2 FadeCueNode (mostly reuses ramp infra; delivers the multi-target fade).
4. §3 Playlist group (transport-layer, no framework change in v1).
5. §4 Timed triggers (new service; independent of the above).
6. Timeline editor (`CuePlayer-Timeline-Editor.md`) — builds on §1 curves and the per-route
   gain fix.
7. Dual-voice crossfade (framework feature, unlocks playlist crossfade).

---

## Implementation status (2026-07-28)

**Done (commit `e2def532` + the post-review fix round):** §1 stop fade + curves end to end
(instance `ShowSession.DefaultStopFade`, Stop/StopAll fade+curve overloads, per-cue curves,
`CueList.StopFadeMs`/`StopFadeCurve`, `AppSettings.StopFadeMs`/`PanicFadeMs` — now editable in
the Project workspace's "Cue transport" box; explicit zero fade hard-cuts on BOTH Stop entry
points). §2 FadeCueNode fully wired (`FadeClipAsync`, `ClipLevel` composition; the fade-in ramp
now holds the clip-fade slot so a Fade cue preempts it instead of fighting/destroying its
level). §3 playlist groups incl. ArmedList-as-GO-advance (post-review: `AvoidImmediateRepeat`
is shuffle-only; nested playlist picks consume recursively and a completed inner run routes to
the enclosing run — nesting no longer stalls). §4 scheduler (post-review: effective grace is
floored at 2× the sweep period so `GraceMs = 0` means "next tick", not "never"). §5 leaks
#1–#4 fixed (#5: HaPlay's Add-input dialog already persists concrete device names; blank-name
descriptors only occur hand-authored, where default-follow is arguably intended — dropped).
§6: per-route gain fix DONE (differing-gain routes now map to per-cell `MatrixCells`; the
dB-mean collapse only survives as the uniform-gain fast path), `.haplaycues` version guard
DONE (fails closed on `HaPlayCueList/v4+`), JSON contexts complete (incl. `CueAutomationPoint`).

**Overlap semantics fix worth knowing about:** Timeline/FireAllSimultaneously plan steps now
fire each media cue in its OWN runtime transport group at every delay (not just same-delay
batches) — previously a later lane's fire replaced the earlier lane's clip in the shared
authored group, so lanes couldn't actually overlap.

**Still open:** §6 `MediaCueNode.LevelDb` per-cue master (wants the route-matrix anchor, now in
place), pre-wait countdown visibility, MIDI/OSC/hotkey per-cue triggers, panic slider,
dual-voice crossfade (§3 `CrossfadeMs` still deliberately unimplemented). Scheduler remains
scoped to the SELECTED cue list (documented in code; schedules in other lists never fire) —
surface or widen next round. Timeline canvas renders blocks at `TimelineStartMs` while the
audible start is `+PreWaitMs`, and `TimelineStartMs` has no numeric drawer field yet.
