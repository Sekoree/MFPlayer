# Dual-voice transport groups (true crossfade)

Status: IMPLEMENTED, 2026-07-28 (order steps 1–3 fully, loop-with-crossfade included).
`TransportGroup.Outgoing` + `ReplaceAsync(crossfade:)` + `FireCueAsync(cueId, crossfade, curve)`;
the pre-end fire rides `ShowClipBinding.PreEndNotify` → `ShowSession.ClipApproachingEnd` (the end
monitor's one-shot pre-end offset, no second timer); HaPlay maps `CuePlaylistOptions.CrossfadeMs`
(now persisted AND implemented, group-tab field) onto it and advances early through
`CuePlayerViewModel.MediaCueCrossfadeExecutor` (EqualPower). The cross is audio AND video: the
incoming voice's layers attach black and `StartFadeIn` ramps them 0→authored (a plain per-cue
FadeIn on a placed clip gets the same audio+video ramp - the earlier audio-only fade-in was an
omission, not a choice; only the fade CUE keeps an explicit video opt-out), and the outgoing
tail's opacities ramp down with its audio. Loop-with-crossfade landed as
`ShowClipBinding.LoopCrossfade` / `MediaCueNode.LoopCrossfadeMs` (General-tab field next to
Loop): inside the window the end monitor re-fires the SAME binding as a fresh incoming voice via
`PlayClipAsync(crossfade:)` (bypassing the cue graph, so a wrap never re-triggers auto-follow;
EqualPower; a failed re-open falls back to the butt-splice seek wrap). Tests:
`S.Media.Session.Tests/CrossfadeTests.cs` (incl. opacity + loop-overlap sections),
`HaPlay.Tests/PlaylistGroupTests.cs` (crossfade section). Original design below.

## Post-implementation review fixes (2026-07-29)

A review of the shipped code found the crossfade's **video half was broken**: the outgoing clip
keeps its layers *and* its transport-timeline claim, but `ReplaceAsync` re-points that same
timeline at the INCOMING clip's playhead, so the composition pump judged the tail's frames as
far-future (`ChooseMasterAlignedFrame` rejects `PTS > masterPts + canvasPeriod`) and fell back to
the last frame forever — the tail faded out as a **frozen still**. Both headline uses hit it
(playlist crossfade: A at 3:12 vs B at 0; loop-with-crossfade likewise). The shipped opacity test
could not see it, because the layer *was* composited every tick at the right opacity — with a
stale picture.

Fixed by detaching the outgoing layers from master alignment at handoff
(`ClipCompositionRuntime.IPlacedClipLayer.DetachFromMasterAlignment()` → `SlotKeepPolicy.Latest`
on the outgoing slots only). That seam needs **zero** change to `S.Media.Compositor`, is
per-layer (the Active clip keeps master alignment on the same canvas), and `Latest` is what an
unmastered composition already uses — the tail is fire-and-forget and its player is already
clock-paced. The regression test asserts the tail's **presented frame PTS advances** across the
window (the recorder now captures `LayerSample(Opacity, FramePts)`).

Also fixed in the same pass: live per-cue route re-apply and hot output rebuild both wrote route
gains WITHOUT the master trim/envelope (a cue could jump to 5× the fader's level, or to unity,
and stay there), and `ShowSession.MasterTrim` was a non-volatile cross-thread read.

### GPU surface path (closed, 2026-07-29)

The gap this section used to record: the GPU surface path (`SurfaceLayerSlot`, NXT-10) has the same
problem in a different shape — a surface *renders* at the composite's stamped master time rather
than selecting from a frame queue, so a crossfade tail's surface rendered at the incoming clip's
time (the tail's model posed at 0:00 while its audio played out at 3:12).

Fixed with a **per-surface render clock**, additive at every hop and null on every ordinary
placement (which then reaches the compositor byte for byte as before):

`ShowSession.ReplaceAsync` handoff captures the outgoing clip's own `Player.PlayClock` once and
passes it to `IPlacedClipLayer.DetachFromMasterAlignment(Func<TimeSpan>? ownClipTime)` →
`SurfaceLayerSlot` stores it as `VideoCompositorSource.SurfaceSlot.RenderTimeSource` (a lock-free
plain property, like `Slot.KeepPolicy`) → the mixer samples it once per composite, outside every
lock, into the new `CompositorSurfaceLayer.RenderTime` → `GlVideoCompositor.CompositeWithSurfaces`
renders that surface at `RenderTime ?? presentationTime` (both the direct and the
effects/mapping-intermediate paths). `LayerSlot` ignores the clock — its frames carry their own
PTS, so latest-wins already tracks the outgoing player. `IVideoCompositorLayerSurface.Render`'s
signature is **unchanged** (only what it is handed moves), so external/native surface implementers
are unaffected; the C ABI's `master_ticks` doc now says a layer may be given its own clock. A
render clock that throws (its player is being torn down under the composite thread) degrades to the
master time instead of faulting the pump.

It IS testable: `S.Media.Compositor.Tests/SurfaceRenderTimeGlTests` drives a fake surface that
records the `masterTime` it is asked to render at, on a real SDL3/GL context
(`Skip.IfNot(SDL3GLVideoCompositor.TryProbe)`), so the GL half is pinned on real GL; the mixer half
is pure CPU (`SurfaceLayerTests`), and `S.Media.Session.Tests/CrossfadeSurfaceTests` runs the whole
crossfade end-to-end against a CPU compositor wearing the surface-host capability. All three fail
when the fix is reverted. (GL test classes now share one xunit collection — two SDL/GL contexts
live at once took the test host down.)

The framework feature the enhancement round repeatedly deferred:
`CuePlaylistOptions.CrossfadeMs` (persisted, unimplemented), group-boundary crossfade for
AutoFollow chains, "fire next early" takeovers, and seamless loop-with-crossfade all need **two
simultaneously-live clips in one transport group**, which `TransportGroup` (single `Active`
slot) cannot express. Companion docs: `CuePlayer-Enhancements.md` §3/§6,
`CuePlayer-Timeline-Editor.md`.

## Why not "just fire two runtime groups"

The timeline/sim-mode overlap fix (2026-07-28) fires lanes into independent runtime groups —
fine when clips are *unrelated*. A crossfade is a *replacement* with an overlap window: the
outgoing and incoming clip share the group's identity (standby, Now-Playing row, seek/pause
target, natural-end routing, stop semantics). Splitting them across two groups leaks all of
that bookkeeping to the host (HaPlay would have to reimplement replacement, which is exactly
what `TransportGroup.ReplaceAsync` owns today).

## Model: an `Outgoing` slot that exists only during the fade window

> **SUPERSEDED 2026-07-29** by workstream A of `Structural-Refactor-Plan-2026-07-29.md`: a group now owns a
> LIST of voices, each with its own state machine (`Arming → Active → Releasing → Retired`), its own level
> state and its own level/stop-bus registration. `Outgoing` is gone; the "one outgoing max" rule below is
> now the named policy constant `TransportGroup.MaxReleasingVoices = 1`. Everything the sections below
> specify about BEHAVIOUR still holds (window, implied fade-in, video opacity crossing, transport targeting
> the incoming clip, open failure = no fade, loop-with-crossfade); only the slot shape changed. Two rules
> got STRONGER: a stop now claims each voice individually rather than the group's two slots, and releasing
> the active clip no longer touches a tail.

```
TransportGroup
  Active   : IArmedClip?      // unchanged - THE clip; all transport targets it
  Outgoing : IArmedClip?      // non-null only mid-crossfade; released when its ramp ends
```

- `ReplaceAsync(...)` gains an optional `crossfade: (TimeSpan duration, FadeCurve curve)?`.
  Null (default) = today's behavior byte-for-byte: old Active released before the new clip
  commits. Non-null = the old Active moves to `Outgoing`, keeps its outputs/routes/layers, and
  a `FadeRamp` ramps it 1→0 (via the existing `ApplyFadeLevel` path, identity-guarded on
  `Outgoing` instead of `Active`) while the new Active fades 0→1 through the normal
  `StartFadeIn` machinery (which already claims the clip-fade slot). At ramp end (or on any
  Stop/Panic/next Replace) the Outgoing clip releases through the same teardown ReplaceAsync
  uses today.
- **One outgoing max.** A second replacement during a fade hard-releases the current Outgoing
  first (a triple overlap is a DJ-mixer feature, not a cue-player feature). This keeps resource
  bounds and the mental model simple.
- Transport semantics while a fade runs: pause/seek/position/end-monitor target `Active` only;
  `Outgoing` is fire-and-forget audio+video tail. Stop claims BOTH (the stop fade must take the
  outgoing tail down too, not just Active).

## Resource + routing notes

- The outgoing clip keeps its OWN router outputs (they were attached to its player); no
  route-sharing needed. Device contention is nil for shared-output lines (`SharedAudioOutput`
  mixes clients) and already-handled for exclusive lines (two clips on one PortAudio device
  already occurs across groups today).
- Composition layers: `Outgoing` keeps its `PlacedLayer`s; the incoming clip adds its own on
  top; the outgoing fade ramps layer opacity via the existing per-layer start-opacity capture.
  Layer z-order: incoming above outgoing (the new content wins visually).
- Prepared clips (`ClipStandbyEngine`) make the incoming open cheap; the crossfade window
  should not begin until the incoming clip actually started (open failure = no fade, old clip
  keeps playing — fail loud via the fire status).

## Level composition

The outgoing ramp is a *fade-out claim* (`TryBeginFadeOut` on the outgoing identity), so it
composes with everything the way stop fades do today (envelope/master level continue to
multiply through `ApplyAudioScale`). No new mixer state: `Outgoing`'s levels are frozen-config
(routes + `ClipLevel` at handoff), only the ramp scalar moves.

## API + wiring (top-down)

1. `ShowSession.FireCueAsync(cueId, crossfade: TimeSpan?, curve)` overload → threads into
   `CommitClipAsync` → `ReplaceAsync(crossfade: ...)`.
2. HaPlay: playlist auto-advance passes `CuePlaylistOptions.CrossfadeMs` (fire the next pick
   when the current item is `CrossfadeMs` FROM its natural end — the end-monitor already knows
   the out-point; add a "pre-end notify" offset to `StartEndMonitor` rather than a second
   timer). GO-on-playing-group ("fire next early") uses the same overload with the list's
   crossfade setting.
3. Loop-with-crossfade (ambient beds): `ClipEndBehavior.Loop` + crossfade = re-fire the same
   binding into the group with the overlap window. Same mechanism, zero extra model.

## Test plan

Session-level with the existing fake-clock/recording harness: replace-with-crossfade keeps two
clips audible for the window and releases the outgoing at ramp end; Stop mid-fade takes both
down on one stop clock; a second replace mid-fade hard-releases the first outgoing; open
failure of the incoming leaves the old Active untouched; layer opacities cross correctly.
HaPlay-level: playlist advance timing (pre-end fire at `CrossfadeMs`), butt-splice fallback
when `CrossfadeMs == 0` unchanged.

## Suggested order

1. `TransportGroup.Outgoing` + `ReplaceAsync(crossfade:)` + session tests (framework core).
2. `FireCueAsync` overload + playlist pre-end fire (delivers §3 `CrossfadeMs`).
3. GO-early takeover + loop-with-crossfade (both reuse 1+2).
