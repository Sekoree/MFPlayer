# Structural refactor plan — the deliberate leftovers (2026-07-29)

Status: plan, agreed with the owner 2026-07-29. Everything in `Next-Round-Plan-2026-07-28.md`
(workstreams A–F) is implemented; the post-implementation reviews are closed. What remains are
items that were consciously NOT done because a correct fix needs a structural change rather than a
patch. They are not five independent defects — they collapse into **four root causes** plus a
hygiene set. Companion docs: `Dual-Voice-Crossfade-Design.md`, `CuePlayer-Timeline-Editor.md`,
`General-Review-Findings-2026-07-27.md`.

Order of work (rationale in each section): **C + E → D → B → A**. Cheapest and least risky first;
the big one (A) lands on top of the level bus (B) that makes it tractable.

---

## A. A transport group owns ONE clip, with a tail bolted beside it

**Root cause.** `TransportGroup` has `Active` plus the `Outgoing` slot added for crossfade, each
guarded ad hoc (`TryClaimOutgoingForStop`, one-outgoing-max, per-apply identity checks). The tail
is a second-class citizen, so everything that treats voices uniformly has to special-case it.

**Symptoms this explains** (all currently open, all recorded in the crossfade review):
- a commit-path exception between `ReplaceActiveAsync` and `StartOutgoingFadeRamp` orphans the
  tail — Outgoing set, no ramp, frozen level, released `Active`;
- `StopCueAsync` matches only `Active?.Spec.Id`, so cue X cannot be stopped while X is the tail;
- the incoming clip's natural end, and a fade cue's `stopWhenSilent`, funnel into
  `ReplaceAsync(null)` which hard-releases a still-fading tail — audible click;
- `StopAllAsync` filters `group.Active is not null`, so a hypothetical tail-only group would keep
  sounding through Panic (safe today only by an implicit, undocumented invariant);
- the tail is not addressable at all (by design, but the design was forced by the slot).

**Shape.** A group owns a **list of voices**. Each voice carries its own small state machine
(arming → active → releasing) and its own level/fade state; `Active` becomes "the voice transport
commands target". Stop / seek / fade / cue-stop then iterate voices uniformly, and the
special-casing disappears. N-way overlap falls out — "a triple overlap is a DJ-mixer feature" was a
scoping decision, not an architectural one.

**Cost/risk.** The largest, and it is the heart of playback. Mitigated by the crossfade / stop /
loop / master-trim suites built during the 2026-07 rounds — that is the safety net that makes it
tractable. Do it LAST, after B has already centralised level composition.

**DONE 2026-07-29.** `TransportGroup` owns a `List<TransportVoice>`; `ActiveVoice` is nothing more than "the
voice transport commands target". A `TransportVoice` carries its clip, everything that clip owns (outputs,
route targets, layers, timeline claims, subtitles), its own `SoundingLevel`, its own claims (fade-out,
clip-fade slot, clip-work, release ramp) and a four-state machine **Arming → Active → Releasing → Retired**.
`Outgoing`, `TryClaimOutgoingForStop`, `ApplyOutgoingFadeLevel`, `ReleaseOutgoingAsync`, `BeginOutgoingRamp`,
`OutgoingCurrentScale`, `IsOutgoingStopClaimed`, `TryBeginFadeOut`, `ReplaceAsync(clip, …)` and the
`GroupFade`/`OutgoingStopFade` pair are all **gone**; `StopGroupsCoreAsync` became `StopVoicesCoreAsync` and
every stop, fade, trim and teardown path claims and ramps VOICES.

All five symptoms are fixed, and each has a regression test in `S.Media.Session.Tests/TransportVoiceTests` —
all six were **verified failing on the pre-refactor tree** before the refactor started:
1. the release ramp is armed **inside** the handoff (`TransportGroup.StartReleaseRamp`, wired at group
   creation), so the window in which a voice sits Releasing with no ramp does not exist. Reaching the old
   state needs a fault exactly there, so there is one narrow internal seam, `ShowSession.PostCommitFault`,
   raised on the dispatcher immediately after a commit;
2. `StopCueAsync` selects the cue's VOICES across groups, so cue X stops whether it is the active clip or the
   tail — and stopping one leaves the other playing;
3. `ReleaseActiveVoiceAsync` releases the active voice ONLY. The natural end, the EOF-stall end, the natural
   fade-out's completion and a fade cue's `stopWhenSilent` all funnel through it, so none of them hard-cuts a
   still-fading tail;
4. tail-only groups are covered by construction — every voice is its own program source, so Panic reaches one
   with no group-level filter at all (B's `Active is not null || Outgoing is not null` self-filter went with
   the group registration; the per-voice stop hook self-filters on `State != Retired`);
5. the tail is addressable: it registers as `cue:<group>:<cue>`, shows up in `GetSoundingSourcesAsync` with
   its own role and composed level, is reachable by `StopCueAsync`, and rides the fader through its own hook.

**The tricky part was the tail's frozen level.** The slot design froze `BeforeMaster` at handoff and had a
second ramp (`ApplyOutgoingFadeLevel`) multiply the live trim back in — correct, but only because a comment
said so. Under the voice model the tail simply *keeps its own `SoundingLevel`*: nothing writes its `Fade` or
`Envelope` again except the release ramp, which multiplies the **fade** component, and the trim keeps riding
through `Effective`. So the trim cannot be captured into a ramp start any more, `ApplyOutgoingFadeLevel`
collapses into the ordinary `ApplyFadeLevel`, and `SoundingLevel.BeforeMaster` was deleted as dead — its rule
now lives on `Fade` ("the ONLY component a ramp may capture as its start"), where every ramp in the session
already reads it. The stop path collapsed the same way: claiming a tail mid-ramp is just capturing the level
and opacities it has NOW, exactly like claiming an active clip mid fade cue.

Per-voice bus registration replaced B's per-group one. That removed the idle-group problem rather than
re-solving it: a voice stamps the live master trim when it is constructed, so there is no idle registration to
keep in sync and no stale level on the next fire. It also answers B's warning about two concurrent
`StopGroupsCoreAsync` calls racing a group's fade claim — the claim is now `TransportVoice.TryClaimFadeOut`,
one interlocked claim per voice: one stop wins the ramp, the other still releases, and release is idempotent
(`State == Retired` short-circuits), so they cannot fight. Three assertions in `SoundingBusTests` changed
(`group:main` → `cue:main:c`, and "the group is not sounding" → "the voice left the bus", matching the line
above it for soundboard voices): they encoded the group-shaped registration, not a rule.

N-way overlap is structural — the list holds N and every path iterates it. `MaxReleasingVoices = 1` is a named
policy constant with the scoping rationale on it, enforced by hard-releasing the OLDEST tails beyond the cap,
so raising it is a one-line change rather than a redesign.

**Do not loosen:** (1) the release ramp must stay armed inside the handoff — the moment it becomes a separate
statement, symptom 1 is back and `PostCommitFault` is the only thing that would notice; (2) a ramp start is
`Fade`, never `Effective` — the master-trim crossfade tests are what catch it; (3) `Retired` is the identity
guard for every deferred write, and `ReleaseAsync` sets it BEFORE any teardown so a racing ramp step is
already a no-op; (4) a voice leaves the bus before its player goes away (`VoiceRetired` in
`ReleaseVoiceCoreAsync`); (5) releasing the active voice must never reach a tail — that is symptom 3, and its
test is the only thing standing between a clean handoff and an audible click; (6) transport commands (seek,
pause, end monitor, fade cues, the live route/placement edits) target `ActiveVoice` only — a tail is
fire-and-forget by design, and `StopCueAsync` is the one path that addresses it.

---

## B. Three independent audio-level and stop domains

**Root cause (verified in code).** `SetMasterTrimAsync` walks `_groups.Values` only.
`VoicePlayer` writes its own route gains independently of `ShowSession`'s. `StopAllAsync` /
`StopCueAsync` filter `_groups.Values`. So "the session" has three level authorities that never
consult each other.

**Symptoms.** The master/panic fader does not touch soundboard voices or previews — pull it to
silence and a soundboard stinger still plays at full level. `StopAllAsync` leaves voices running
(pre-existing). Level composition can drift per domain because each writes gains its own way.

**Shape.** A session-level registry of *sounding sources* (transport voices, soundboard voices,
previews, taps) with ONE composition point — `master × source × fade × envelope` — and ONE
enumeration used by trim, stop-all and panic. Deliberately **additive**: register the existing
paths first, then delete per-domain gain writes one at a time, so each step is separately testable.

**Scope — DECIDED with the owner 2026-07-29:**
- The master fader and Panic cover **program audio only**: transport cues **and soundboard voices**.
- **Preview / audition is excluded.** It is the operator's monitoring path; ducking it when the show
  is pulled down would deafen or confuse the person driving. Preview keeps its own level.
- **Stop-all and Panic have the SAME reach** — whatever the fader covers, both stop. One rule to
  remember under pressure beats an expressive-but-forgettable split.

So the registry needs a per-source *classification* (program vs monitoring), not just a flat list:
`trim`, `stop-all` and `panic` all enumerate program sources; preview registers as monitoring and is
skipped by all three. That classification is the single thing the whole design hangs on — get it in
the registration API from the start rather than bolting a bool on later.

**Cost/risk.** Medium, well-bounded if done additively. Do it BEFORE A.

**DONE 2026-07-29.** `SoundingSourceRegistry` is the bus: every sounding source registers with a
classification and `SetMasterTrimAsync` / `StopAllAsync` both drive its ONE `ProgramSources()`
enumeration. The classification is **not a bool** - it is which method you call. `RegisterProgram`
demands the trim hook and the stop hook at the call site; `RegisterMonitoring` takes neither, so a
monitoring source physically *cannot* be levelled or stopped by the bus. Preview and audio taps
register monitoring (a tap is an analysis feed - the fader must not duck a visualizer's reaction),
transport groups and soundboard voices register program. `SoundingLevel` is the single composition -
`master × source × fade × envelope` - and `ShowSession.GetSoundingSourcesAsync()` makes the whole bus
observable (label, role, is-sounding, composed level), which is what lets a test prove monitoring is
registered *and* excluded rather than merely absent.

Migration was additive in the order the plan asked: register all four paths → move trim onto the
enumeration → move stop-all onto it → delete the per-domain writes. Deleted: `VoicePlayer`'s three
independent gain writes (attach gain, `SetVoiceVolumeAsync`, the fade-ramp step) now all go through
one `ApplyVoiceLevel`. That alone fixed a live defect nobody had filed - the fade ramp wrote its 1→0
level straight onto the route, so fading a tile playing at 0.4 first **snapped it up to full**. Kept:
`TransportGroup.ApplyAudioScale` stays THE transport composition point (it already was one), only
re-expressed over `SoundingLevel`; `StopCueAsync` deliberately still walks the groups only - it
targets a cue id, and voices are keyed by tile, not cue.

Two tricky parts. First, **trim and stop want different filters**: trim must never miss a source that is
about to sound (or a trim set while nothing plays is lost and the next fire starts stale), stop must not
claim/ramp something with nothing in it. Splitting the enumeration would have recreated the very thing this
removes, so one unfiltered list serves both and each hook self-filters on the dispatcher. Registration is
per **VOICE** (`CommitVoiceAsync`), not per group, so the stop hook's filter is simply
`voice.State != Retired` - which is also what closes the tail-only-group hole Panic used to leave sounding,
since a tail is an entry in its own right. Trim inheritance is then not the enumeration's problem at all: a
voice **stamps the live trim when it is constructed** (`new TransportVoice(armed, binding, MasterTrim)`) and
a soundboard voice does the same at fire time, so there is no idle registration to keep in sync and nothing
starts at a stale level. Second, the freeze: `SoundingLevel` now *names* the constraint the earlier review
found the hard way - a crossfade handoff must freeze `fade × envelope`, never `Effective`, because the
outgoing ramp multiplies the live trim on every step. Also closed: Panic's other half,
`CancelPendingVoiceOpens` - a stinger still *loading* when Panic is hit is not on the bus yet and would
otherwise start playing immediately afterwards (the voice analogue of `CancelActiveFire`).

Nine new `SoundingBusTests` plus a new crossfade case in `MasterTrimTests`. The suite was validated as
a **negative control**: neutering the voice trim and stop hooks fails 6 of the 9, and the 3 that keep
passing are the ones testing other mechanisms. HaPlay needed no change - its fader and Panic already
funnel through `SetMasterTrimAsync` / `StopAllAsync`, and the soundboard's 200 ms progress poll already
reconciles tiles whose voice disappeared, so a Panic-stopped tile resets itself.

**Post-review fixes 2026-07-30** (each with a regression test that was verified failing first):
1. a voice's stop claim was a permanent one-shot, so **Panic after a Stop-with-fade was a silent no-op for
   soundboard voices** - the cue cut, the stinger played on for the rest of the show fade, and
   `StopAllAsync` returned anyway. `VoiceHandle.TryClaimStop(deadline)` now lets a stop that lands EARLIER
   supersede an in-flight one (cancelling its ramp and taking over levels + release), while one that lands
   later awaits `VoiceHandle.Released` - so *every* caller returns only once the voice is genuinely gone;
2. the fire path was the last route-gain write outside the composition: it attached at the authored gain
   and folded the trim in only *after* the commit (which awaits the displaced clip's teardown), so a GO
   under a lowered fader put **full-level program audio on the device** for that window. Routes now attach
   at `voice.EffectiveAudioLevel` (0 for a fade-in) and the post-commit pass is value-identical;
3. `StartVoiceFadeOut`'s completion released the voice even when a bus stop had claimed it mid-ramp - a
   click at the tile fade's level *and* a dead stop ramp. It now repeats the claim check exactly as
   `StartVoiceReleaseRamp` does;
4. `ApplyActiveAudioMatrixAsync` wrote the caller's cells raw and left `RouteTargets` describing the old
   route; it now installs them as that output's matrix **route target** through `ApplyAudioScale`;
5. bus labels are unique **per voice** (`cue:<group>:<cue>#<n>`): a loop crossfade overlaps a cue with
   itself, so a cue-shaped label put two identical entries on the bus and silently disabled the
   duplicate-label check;
6. a failed stop's `ShowPlaybackAlert.CueId` carries the registration's `SubjectId` (the cue id / tile id)
   instead of the bus label, so the host can resolve a name; the label still names the entry in the message.

**Do not loosen:** (1) preview and taps stay `Monitoring` - giving them hooks is how the fader starts
deafening the operator; (2) a handoff freezes `fade × envelope`, never `Effective`; (3) the "is anything
sounding" test belongs in the stop hook, never in the trim enumeration; (4) `ReleaseVoiceAsync`
unregisters BEFORE the player goes away - a lingering registration is a write to a dead player, and the
tests assert exactly one entry per label, where a label identifies a **voice** (a cue may legitimately have
two, so per-cue labels would defeat the check); (5) stop-all and Panic must keep sharing `StopAllAsync` -
the moment Panic gets its own path the two reaches drift; (6) a stop hook's returned task must complete
only when its source is actually released - "stopped means stopped" is what makes Panic trustworthy.

---

## C. Shared timeline geometry clamps, so drawing and hit-testing cannot disagree

**Root cause (verified in code).** `TimelineMath.EnvelopePointCenter` clamps X to `block.Right`,
and `EnvelopePointHit` consumes that same clamped function. Drawing and picking therefore share a
*presentation* decision baked into *shared* math. Unclamping the renderer alone desyncs picking;
duplicating the geometry canvas-side desyncs them permanently. That is exactly why the keyframe
issue was left open.

**Symptoms.** Envelope keyframes authored past a trimmed-in right edge stack on the edge and only
one of them can ever be hit. Same root shape, elsewhere in `TimelineCanvas`: marker labels have no
`MaxTextWidth` and draw past the measured extent; the last ruler tick label overruns; the drag
readout clamps against the canvas extent rather than the scrolled viewport (partially fixed —
readout and ruler are done, markers are done; the keyframe case is the one still open).

**Shape.** Split the math in two:
1. a pure **projection** — model (clip ms, dB) → position, *never clamped*, returning the position
   plus an out-of-range flag;
2. a thin **view adapter** that decides presentation from that flag.

Hit-testing consumes the same projection, so the two can never diverge again.

**Presentation policy for out-of-range keyframes** (decided; revisit if it feels wrong in use):
points beyond the trimmed end still *influence the audible curve* (the sampler interpolates toward
them and flat-extrapolates the last one), so they must not be silently hidden. Therefore: draw the
**curve** across the whole block unchanged; draw **dots only for in-range points**; represent the
out-of-range ones as a single edge indicator carrying a count, which is itself a hit target (so
they can be selected/removed). Hit-testing then only ever sees unambiguous in-range dots.

**Cost/risk.** Small-to-medium, **low risk** — pure functions with existing unit tests
(`TimelineEnvelopeMathTests`). Best value per unit of risk: do it FIRST.

**DONE 2026-07-29.** `TimelineMath` now exposes `ProjectEnvelopePoint` / `ProjectEnvelope` /
`HitTestEnvelope` over `EnvelopePointProjection`, `EnvelopeEdgeIndicator` and `EnvelopeOverlay`;
`EnvelopePointCenter` and `EnvelopePointHit` are gone, and the canvas computes no geometry of its
own. The clamp is removed on BOTH sides (the old `Math.Max(0, TimeMs)` too). Out-of-range points
collapse to one counted chevron badge per edge whose representative is the **innermost** of them —
the only one `EnvelopeClampDragTime` can pull back into range, so dragging it works and repeated
Delete peels the run off from the inside out. The test that pinned the clamp was replaced
deliberately (it encoded the defect). The invariant is now proven property-style over 120 generated
envelopes: every dot the renderer draws is hit-testable at its own centre, and a hit is always
either an in-range dot or the representative of a badge that was actually drawn.
Only **BeyondEnd** is reachable from the editor (every edit path clamps to `[0, EffectiveDurationMs]`
and a left trim raises `StartOffsetMs` without rebasing times); `BeforeStart` is still projected and
indicated because an externally edited project file can carry a negative `TimeMs` and the runtime
sampler honours it.

---

## D. Clocks make consumers INFER epoch boundaries

**Root cause.** `MediaClock`'s fold treats any advancing regression as a new epoch, resting on
"masters are monotonic within one segment" — which `IPlaybackClock` never promises, and which
`NDIIngestPlaybackClock` historically violated (fixed, but the contract is still unstated).

**Symptom (suspected, unresolved).** A transient dip that later recovers is folded as a new epoch
and the recovery then double-counts as a forward jump. The hold branch is safe; the advancing
branch is not, and no test covers a master that returns to its old epoch.

**Shape.** Have clocks report **`(epochId, elapsed)`** rather than elapsed alone. Consumers compare
ids instead of inferring a boundary from a regression. This **subsumes `TimebaseGeneration`** and
turns `SharedAudioOutput`'s terminal-re-anchor recovery from a high-water-mark heuristic into an
equality check. Alternative if the interface change is unwanted: make monotonicity an explicit,
asserted contract of `IPlaybackClock` and make the fold conservative (fold only on a definitive
regression, hold otherwise).

**Cost/risk.** Small interface change, medium mechanical ripple, low risk — and it retires a class
of bug rather than one instance. Do it after C, before B.

**DONE 2026-07-29.** `IPlaybackClock` now offers `long EpochId` plus `ClockReading Read()` — one atomic
`(EpochId, Elapsed, IsAdvancing)` struct — both as **default** members, so the ~30 implementers and test
doubles compiled untouched and only the clocks that actually re-anchor override them. Ids come from
`PlaybackEpoch.Next()` and are process-wide unique (`0` = `PlaybackEpoch.Single`, "never re-anchors"), so
two clocks can never compare equal by accident and a composite can forward a leaf id safely. The
monotonic contract is now **stated**: monotonic *within* an epoch, free to restart across a bump. The
default `Read()` composes the members, which is correct only while the epoch is constant — anything that
bumps MUST override it, and wrappers MUST forward it or they silently report `Single` over a device that
re-anchors (`ResamplingAudioOutput`, `AudioEffects`, `MeteringAudioOutput` all forward).

The tricky part was **where the fold triggers**. `MediaClock`'s old branch folded on any advancing
regression, and the doc's suspected symptom is real: a master reading 12 s, dipping to 11 s, then
recovering to 12 s reported **3 s** of position where only 2 s had ever played — the dip re-anchored at the
floor and the recovery was counted twice. That is pinned as
`MasterDipsAndRecoversWithinOneEpoch_DoesNotDoubleCountTheRecovery` (written first: **failed 3 s vs 2 s**,
passes after). The fold now consults **only** the id, never `IsAdvancing`, so it also re-anchors on the
first observation of a new epoch instead of waiting for the master to advance — which is why
`FlushLandingAfterClockSeek` now reads `target + 50 ms` instead of `target`: those 50 ms are real playback
of the new segment that the old timing threw away. `AdvancingRegression_FoldsContinuouslyIntoNewEpoch`
became `MasterReportsNewEpoch_FoldsContinuouslyIntoIt` — both were the only tests changed, and both
encoded the retired inference rather than a contract. Within one id a regression is now **held** at the
high-water (which is why a torn read is benign: the next read fixes it, and holding never invents time).

`TimebaseGeneration` is gone: `MediaClock.PositionEpoch` (+ `IPlayhead.PositionEpoch` / `ReadPosition()`,
also defaults) is the same concept in the same vocabulary, and `MediaPlayer`'s EOF clamp compares epochs.
`MediaPlayer.Position` now reads the position BEFORE the epoch, so a seek landing mid-read drops the clamp
instead of applying it to a position the seek already invalidated. `SharedAudioOutput.ClientInput` recovers
on `terminal.EpochId != captured`, not on "elapsed went backwards"; a regression *without* an announced
epoch is a broken terminal and is clamped to the high-water. DAC-lead smoothing and its monotonic
guarantee are untouched. Bump points wired: PortAudio/miniaudio Start + Flush + device-loss latch,
`NullClockedAudioOutput` Start/Flush, `VideoPtsClock` BeginSession/Seek, `NDIIngestPlaybackClock`
AttachReceiver/Seek, `CompositePlaybackClock` on `(winner, winner epoch)` change *including* the neutral
idle state, client Flush/attach. `MonotonicWallClock` and `TransportTimeline` keep the default: they are
continuous by construction.

**Do not loosen:** (1) the fold must never consult `IsAdvancing` again — that is the inference, and the
dip test is what catches its return; (2) any clock that bumps must override `Read()` — a bumping clock
with the default composition hands out torn pairs, and a wrapper that forwards `ElapsedSinceStart` but not
`Read()` erases its device's epochs silently; (3) the client clock's own epoch must NOT change on a
terminal re-anchor — staying continuous across it is the whole point of the recovery. Both backends'
Start/Flush/device-loss bumps ran against real hardware on this box (no skips).

**Post-review fixes 2026-07-30** (each with a regression test verified failing first):

1. **`MediaClock` handed out the reserved `PlaybackEpoch.Single`.** `_positionEpoch` was a plain `long`, so
   it defaulted to 0 — the id §D reserves for "never re-anchors" and promises never to hand out. Every
   never-yet-seeked playhead therefore compared *equal* to every other one. Now seeded from
   `PlaybackEpoch.Next()`. `MediaPlayer`'s EOF-clamp doc claim was false *because of* this and is now true.
2. **`CompositePlaybackClock.Read()` was not atomic.** Candidate sweep, epoch resolution and blend each took
   a different lock (or none), so two concurrent readers each paired a fresh id with the other's winner —
   and a mastered `MediaClock` re-anchors per new id, discarding inter-read accrual. One gate now.
3. **`EpochId` was a side-effecting read.** Reading a member ran all of `Read()`, mutating blend state:
   *looking* at the epoch started the hand-off cross-fade at the instant of the observation. (The live
   example was `AvPlaybackCoordinator`'s debug log — enabling `LogLevel.Debug` changed playback.) Blend
   state now commits only for `Read()`; members are projections of one coherent snapshot.
4. **`IsAdvancing` diverged from `Read().IsAdvancing`** when a terminal threw. Also fixed rather than
   documented: a device paused 30 s and *then* lost jumped to wall-time-since-attach, because the high-water
   only clamps regressions. The fallback is now spliced onto the raw high-water once per outage.
5. **The composite did not enforce its own monotonic contract** (S6) and **`VideoPtsClock.Resume()` could
   rewind within one epoch** (S2). Both now carry an epoch high-water.
6. **`SessionClock` now infers the re-anchor** (S1 — the unrealised payoff of this section). The epoch
   plumbing had been added to `PlayheadPlaybackClock` and `SessionClock` called *neither* `EpochId` nor
   `Read()`, so it was dead code and group master time stayed monotonic only via four manual
   `MarkDiscontinuity` calls — the "three local patches instead of one mechanism" shape this section exists
   to retire, still present after the round that claimed to retire it. `Now` reads through `Read()` and
   rebases on an id change, preserving the group time already reported (published by CAS so concurrent
   readers converge on one anchor). **The `ShowSession` calls are no longer load-bearing for monotonicity**
   — a missed one can no longer rewind group time — but they are not redundant: they still bump the
   timeline generation and re-anchor the source correlation, and they pin `Now` to the exact instant the
   caller captured *before* the jump, which the automatic path can only approximate from the last value a
   reader happened to observe. They degrade from "the mechanism" to "a precision refinement".

**Test-quality fixes from the same review:** the Start-path epoch branch had *no* discriminating test —
both candidates passed under the retired drift-sign heuristic. The new one (paused at 3 s, master
re-anchors, new segment plays past the paused reading) fails under it while both originals still pass.
Three device-gated cases in `AudioOutputClockEpochTests` early-`return`ed and reported **Passed** with no
hardware; they are now `[SkippableFact]` (`Assert.Skip` does not exist in xunit 2.9.3 — it silently
resolves to `AsyncEnumerable.Skip`), verified to report `[SKIP]`.

**Known residual epoch gaps** (none load-bearing today, all named so they are not rediscovered):
`ClockedNativeAudioOutput` cannot express an epoch — the native ABI has no channel for one, so a plugin
backend's re-anchor is invisible; that is a contract hole to close when the ABI next changes.
`TransportTimeline` reports `Single` and is currently *truthful* (monotonic in production), so it is naming
debt, not a bug. `OutputSyncGroup` reads `IsAdvancing` and `ElapsedSinceStart` as two separate calls — a
torn pair across an epoch boundary would compute a bogus phase error and apply a wrong ppm correction; it
has no production call sites yet, so fix it before wiring genlock, not after.

---

## E. Hygiene that stops recurrence (not refactors, but do them with C)

1. **Ban raw `HeadlessUnitTestSession.Dispatch`.** The 21 vacuous-assertion sites fixed in the
   2026-07-29 sweep were ONE shape — a returned `Task` that is easy to drop, silently throwing
   away every assertion in the body. Funnel all UI-thread test work through one sanctioned helper
   and add a lint test that fails when the raw API appears elsewhere (the `RawStringLiteralLint`
   precedent).
2. **Fix the raw-literal lint's own blind spot.** Its regex is `\b(Text|Content|Header|…)` which
   does not match `PlaceholderText`, so some user-facing literals escape it.
3. **`CueTriggerService.WouldAccept(record)`.** The always-on-input latency fix had to mirror the
   service's private `GateOpen` + MIDI-learn logic inside `MainViewModel` to keep the cheap filter
   on the I/O thread. Two copies of one rule WILL drift; expose it as a cheap thread-safe predicate
   on the service and have the VM call it.

**DONE 2026-07-29.**
1. `HeadlessDispatchLintTests` lints the DEFECT rather than banning the API (a wrapper can be
   misused just as easily, and 34 files would have needed churning). It blanks comments and string
   literals, then walks BACK from each `.Dispatch(` over the receiver chain — crossing balanced
   `()`/`[]`/`<>` and whole identifiers, stopping at keywords — to see what really precedes the
   expression. It catches: bare statement, `_ =` discard, expression-bodied **void** member (`=>`
   only consumes when the member returns the Task), and the `Dispatch(async …)` overload trap.
   Validated **both ways**: a probe file with all four broken shapes trips it with four distinct
   messages, the four correct shapes stay silent, and the real tree reports 0. Getting there took
   three wrong cuts — `=>` counted as consumption, the `>` of `=>` parsed as a generic close
   bracket, and `await` eaten as an identifier — each of which made the lint pass vacuously, which
   is precisely the failure it exists to prevent. Don't loosen it without re-running the probe.
2. Raw-literal lint blind spot fixed: `\b` cannot match between two word characters, so
   `PlaceholderText="…"` had NEVER been scanned. Correcting the regex surfaced **57** pre-existing
   literals (260 → 317); re-baselined with that rationale, ratchet rule intact.
3. `CueTriggerService.WouldAccept` now owns the arm/edit-mode rule and the learn exception;
   `MainViewModel` asks instead of restating it.

---

## Deliberately NOT doing (and why)

These are unbuilt features or sound scoping calls, not debt. Wiring them without a driving
requirement adds maintained surface area for no benefit:

- **Genlock domains** (`OutputSyncGroup` / `VideoPresentSyncGroup` / `LiveTimelineDriver`) — dormant
  until multi-output frame-lock is a real requirement.
- **`CompositePlaybackClock` / `SetMasterChain`** — stays test/lab machinery.
- **LTC / SMPTE audio timecode** — a bounded feature needing an audio-input chase decoder; MTC
  covers the common rig. Do it when a rig needs it.
- **Reverse (descending) MTC chase** — resyncs instead of decoding backwards; genuinely rare.
- **Cross-list standby / pre-roll** — selected-list-only is a real decoder-budget constraint.
- **`SoundboardGrid.TryCreateScheduledFire`** — HaPlay's soundboard now has its own quantized
  launch and shares only the boundary math; the framework primitive stays for framework hosts.
  Prune only on owner sign-off.

---

## Order

| Step | Work | Why here |
|---|---|---|
| 1 ✅ | **C** geometry projection split + **E** hygiene | Cheap, low risk, immediate payoff; E stops the vacuous-test shape recurring |
| 2 ✅ | **D** epoch identity on clocks | Small change, removes an inference class, simplifies B and A |
| 3 ✅ | **B** level / stop bus | Fixes an operator-visible gap (panic fader misses soundboard); centralises what A needs |
| 4 ✅ | **A** voices as a list | Biggest and riskiest — lands on B, behind the crossfade suites |

**All five steps are done.** What follows are the residuals the reviews named but left, worked separately.

---

## Follow-ups 2026-07-30 (after A–E all landed)

Each fix below was written test-first and confirmed failing on the pre-fix tree.

1. **Two concurrent stops on one voice: the loser cut the winner's ramp.** `TryClaimFadeOut()` was a
   permanent one-shot with no deadline, and `StopVoicesCoreAsync` released **every** voice it selected,
   including one whose claim it had lost — so a second Stop landing during a 5 s stop fade released the voice
   on the spot, chopping the ramp off at whatever level it had reached (measured: **0.95**). Double-tapping
   Stop, or Stop-on-a-group then Stop-all, is ordinary on a control surface, so this clicked in normal use.
   Fixed by porting the discipline the soundboard voices already had (`VoicePlayer.TryClaimStop`): the claim is
   held by whichever stop lands silent FIRST — an earlier deadline supersedes and takes over the levels and the
   release from where the incumbent got to, a later one becomes a waiter (`AwaitVoiceReleaseAsync`, bounded by
   its own deadline + 1 s so a fault in the owner cannot make Panic hang). The stop ramp is now cancellable
   per-voice (it ran on `CancellationToken.None`, so nothing could supersede it), and the natural end-of-clip
   fade honours the claim on **both** halves — its ramp stops writing, and it no longer releases the voice out
   from under a stop, which would also have reported an operator stop as `ClipNaturallyEnded`.
   Tests: `TransportVoiceTests.ASecondStopMidRamp_TakesOverTheFade_…` (30 s vs 1 s fades, so *taking over*
   and *waiting for the incumbent* are ~28 s apart — both end in silence, so a level assertion alone cannot
   tell them apart) and `PanicDuringAStopFade_StillCutsTheVoice_…` (a guard, not a discriminator: Panic already
   worked, and must keep working — it is the case that still SHOULD cut).
2. **A cue whose volume envelope starts below unity burst at full level.** `StartEnvelopeRunner` starts at the
   end of the commit, so the routes attached at unity and the device saw **0.8 against a 0.19 ceiling** — a 4×
   burst on a cue authored quiet precisely because it must not be loud. The envelope component is now seeded
   from `VolumeEnvelopes.Sample(binding.VolumeEnvelope, binding.StartOffset)` before anything attaches. Same
   class as the master-fader burst; the composition has to be authoritative *at* attach, not just after it.
   Test: `MasterTrimTests.GoWithAnEnvelopeStartingBelowUnity_NeverPutsAFullLevelBufferOnTheDevice`.
3. **The "different thread owns it" test flake — reproduced and diagnosed, not yet fixed.** See
   `HaPlay.Tests/DispatcherOwnershipGuard.cs`. It had resisted 17 clean runs; an opt-in
   (`HAPLAY_DISPATCHER_GUARD=1`) post-test dispatcher probe reproduces it on demand with one signature, and
   established two things: the offending access is **asynchronous**, so blaming the test that just finished
   cannot identify it; and listener handlers are not the whole story — two of the three listener tests never
   drained (the earlier round fixed only the one that motivated the drain) and fixing both did not reduce
   failures. The remaining suspect is what `HAPLAY_DISABLE_RECOVERY_TIMER` already point-fixes: tests build
   view models and never tear them down, and there are ~47 `DispatcherTimer` sites across them. **The
   structural fix is view-model teardown in tests** — a bigger job, deliberately not started here. The guard is
   opt-in because the probe is not passive: it builds an isolated app per test, which multiplies the very
   unbind window it detects (6/6 clean without it, 4-of-8 failing with it).
4. **An overlap group flattened a nested PLAYLIST group into all of its items** (reported from
   `testproject.haplayproj`: a fire-all group over `[visualizer, playlist[A, B]]` started both songs at
   once). Both overlap branches of `BuildTriggerPlan` expanded a nested group through
   `EnumerateFireableCueOrder`, which recurses into groups and returns only leaf cues — so the nested
   group's own fire mode was discarded. For Playlist/ArmedList that is not just "fires extra cues": those
   modes own run state, so it fired items the run never armed **and** left the run unarmed, which is the
   second half of the bug — `ConsumePlaylistPick` ran only when GO targeted the playlist group itself, so
   the item's natural end was swallowed by `FindPlaylistRunForEndedCue` and the list could never advance.
   Both overlap branches now share one `AppendOverlapLane` expander (they had drifted: sim mode dropped a
   nested group's own pre-wait, Timeline applied it), a nested playlist/armed-list group fires through its
   own plan, and `BuildTriggerPlan` reports the nested runs so both GO paths arm them. The inner step's
   `Independent` flag is preserved on purpose — a playlist's picks must replace each other in one shared
   transport group, which marking them independent would break. *Known limitation:* two playlist lanes
   under one overlap parent therefore share that group and would replace each other; that needs a runtime
   group per LANE rather than per cue. A nested *plain* group still flattens — it holds no run state, so
   that is a scoping choice, and `TimelineGroupTests` pins it. Tests: `PlaylistGroupTests`
   `BuildTriggerPlan_OverlapGroup_FiresANestedPlaylistsARMEDPICK_NotAllOfItsItems` (both overlap modes) and
   `Go_OnAnOverlapGroup_ArmsTheNestedPlaylistRun_SoItStillAutoAdvances`.
5. **Four drawer tabs clipped instead of scrolling.** Group, Action, Comment and Cue preview had a bare
   panel as their tab root rather than a `ScrollViewer`, so with the drawer short (its splitter allows
   88 px, the cap is 440 px) content simply fell outside. Measured at 1024×600 with a Playlist group
   selected: the Group tab's ~250 px of options laid out at `(11,456 1002x161)` — bottom edge y=617 in a
   600 px window, with the End-behavior row off-screen. All four are wrapped now, and
   `CuePlayerLayoutBoundsTests.CueDrawer_EveryTab_KeepsItsContentInsideTheDrawerVertically` walks the tab
   strip per cue kind at three sizes, so a newly added tab is covered without editing the test.

6. **A fire-all group's non-media lanes were serialized behind the media batch's UI round-trip.** The
   remaining half of the `testproject.haplayproj` report: with the plan fixed, `OutGroup` still did not start
   its Visualizer lane. `DispatchCueGroupExecution` awaited `SetStatusMessageOnUiAsync` — a marshal to the UI
   thread — *between* the coordinated media batch and the individual dispatch of every non-media cue. During
   a GO the UI thread is often busy (decoders opening, surfaces building), so a visualizer/action/fade lane
   started late; and if the marshal never completed, the outer `catch` swallowed it and the lane never
   started at all. The status message is a notification, not a prerequisite, so it is now published *after*
   the lanes are away. Media still goes first on purpose — a visualizer attaches to a composition's GL
   surface host, which may not exist until a clip plays on it. Test:
   `PlaylistGroupTests.Go_OnAnOverlapGroup_AlsoFiresTheNonMediaLanes_WithTheHostsGroupExecutorWired`, which
   wires the host's `MediaCueGroupExecutor` — the branch only production reached, which is why every existing
   test (they set `MediaCueExecutor` alone) took the per-step path and could never see this.
7. **The placement canvas followed an output WINDOW's aspect instead of the composition's.** Placements are
   normalized against the composition, so drawing the canvas at the window's aspect drew every rectangle at
   the wrong shape and position — and resizing the output window visibly moved rectangles nobody had touched.
   `PlacementCanvasAspect` is now always the composition's own resolution, and the window-resize subscription
   that existed only to re-raise it is gone. `CuePlayerCompositionCanvasTests` asserted the old behaviour
   deliberately; all of it is inverted, with the reasoning recorded in the class summary.
9. **A silently-succeeding lane in a batch skipped ApplyCueExecutionResult entirely.** The last of the
   `testproject.haplayproj` reports: with the ordering fixed the Visualizer *played*, but its indicator never
   showed it running. `DispatchCueGroupExecution` applied a non-media lane's result **only when the executor
   returned a message** — so the normal case, success (null), skipped the one method that creates a
   visualizer's Now Playing row (`StartVisualizerRow` → `OnCueStarted`) *and* advances an instant cue's
   Auto-Follow chain. Both were therefore silently dead for every lane inside a same-delay batch, while the
   same cue fired on its own went through `DispatchCueExecution`, which always applies — which is exactly why
   it only ever misbehaved inside a group. Now applied unconditionally. Test asserts the ROW, not the
   executor call, since "plays but shows nothing" is indistinguishable from "never fired" to the operator.
   Note for future UI-effect tests: the row is created by a pool-thread continuation marshalled to the UI
   thread, and an awaiting test body does not pump the headless dispatcher — the test pumps explicitly
   (`Dispatcher.UIThread.RunJobs()`), the same pattern `DrainListenerHandlers` uses. Without that the
   assertion can only time out no matter what the production code does.
13. **…and one hop further: `ToModel` flattened the rectangle on its way to the compositor.** With the
   resolver fixed, right/bottom worked and left/top still "stopped at the canvas edge".
   `CueVideoPlacementViewModel.ToModel` — the conversion the compositor is actually handed — clamped
   DestX/DestY/DestWidth/DestHeight to **[0,1]**. A positive offset passed through untouched, which is
   precisely why the two directions behaved differently and why each earlier round looked half-fixed: the
   negative was erased one hop after every layer I had verified. Now the same `NormalizedRectRange` as the
   rest of the chain, so oversize (fill-and-crop) survives too.
   **The habit this cost:** five rounds, five correct layers, because each time I verified the layer I had
   just changed instead of following the VALUE to its consumer. The test now walks the whole chain in one
   assertion — `SetDestRect` → `ToModel` → `FromModel` → `HaPlayShowMapper.ToShowVideoPlacement` — including
   the top-left→bottom-left Y flip, so a clamp introduced at any hop fails here. For anything with several
   representations, test the chain, not the links.

12. **THE actual out-of-bounds defect: the compositor shrank an off-canvas layer instead of clipping it.**
   `PlacementResolver.Resolve` opened with `destRect.Clamped()`. One line, and it produced every symptom
   reported over three rounds: a full-canvas layer dragged into the corner became a *smaller destination*
   that the fit then scaled the whole source down into (measured: scale 0.75 where 1.5 is correct — exactly
   halved, "the whole frame, just smaller"); dragging right trimmed the image on BOTH sides, because the
   overflow trim is centred — correct for absorbing a fit mismatch, wrong for an edge; and a negative offset
   was erased outright, so left/up "did nothing and filled the composition". Edges are now ordered but NOT
   clamped, and the placed image is clipped against the canvas *after* the fit, one-sided, converting the
   overhang into a source-crop inset on the edge that actually hangs over. The authored scale survives: the
   layer is cut off, not squeezed. Wholly-off-canvas collapses the crop.
   **Why this took three attempts:** every layer above the compositor was fixed and tested first — canvas
   drag, snap math, view-model range, the drawer's two-way editors — and each fix was real, so each round
   looked like progress while the visible behaviour barely changed. The resolver had **no tests at all**, and
   it is the only place the authored rectangle becomes pixels. New `PlacementResolverOutOfBoundsTests` (all
   five failed before, with the numbers above) pins per-edge clipping and keeps the two properties that were
   already right: a wholly-inside layer is untouched, and `Cover`'s trim stays centred so split-screen layers
   still cannot spill onto each other. All four placement paths - plain, VideoFx/mapped, media layer, surface
   layer - share the resolver, so one fix covers them.

11. **…and then it was still broken, by the DRAWER's numeric editors.** The out-of-bounds work above was
   correct at every layer that had a test — canvas, snap math, view model — and still did not work, because
   the drawer's `NumericUpDown` editors are `Minimum="0" Maximum="1"` and **two-way bound**. A NumericUpDown
   coerces its Value into its own range and writes the coerced number BACK through the binding, so a drag to
   a negative DestX was undone the instant the binding round-tripped: "moving up or left does nothing". Same
   defect on `PixelX`/`PixelY` in the output-layout dialog. Ranges now match `NormalizedRectRange`, and
   `PlacementEditorsInTheDrawer_MustNotClampInsideTheModelsRange` lints the markup for it (a source lint, the
   idiom already used for raw literals and `Dispatch` — the property is a fact about the markup and holds
   whether or not the tab is realized). Verified by restoring the old ranges: it names all four offenders.
   **The lesson worth keeping:** three correct layers with passing tests, defeated by a fourth that nothing
   tested. A two-way editor's range is not input validation, it is a coercion applied to whatever the model
   reports — so it has to agree with the model's own range, and the mismatch is invisible from either side.
   The new `CompositionPlacementCanvasDragTests` drives the control with real pointer input (`MouseDown` /
   `MouseMove` / `MouseUp`) rather than calling the math, which is the layer that was missing all along; it
   pins that a body drag changes position ONLY, in all four directions, and reaches negative coordinates.
   *Not reproduced:* the "dragging right/down resizes it" half. A body drag provably does not resize (that is
   the test above), so the likely cause is grabbing the resize HANDLE, which for a full-canvas layer sits in
   the composition's bottom-right corner — exactly where one reaches to push it out that way. Both canvases
   now set the cursor on hover (`SizeAll` over a body, `BottomRightCorner` over the handle) so the gesture is
   never a guess.

10. **Out-of-bounds dragging was correct but invisible, which made it read as broken.** Reported as "right and
   down just resizes it, left and up does nothing". Nothing was ever resized: the composition filled the whole
   editor control, so the part of a moved box outside it was clipped at the control edge and the remaining
   visible sliver simply looked narrower; and at `x=0` an edge sits ON a snap guide, so small drags were
   legitimately pulled back. Both editors now reserve a **work-area margin** (13% per side) so the overhang has
   somewhere to be drawn, **ghost** the outside portion at 35% (it is authored but will never be rendered),
   clip boxes to the control rather than the canvas, and draw the canvas outline ON TOP of every box so the
   reference edge is never hidden. `PlacementSnapMathTests` pins the invariant the visuals were
   misrepresenting — a move never changes size — and that a drag past the snap threshold does escape a guide
   rather than being pinned to it.

8. **Placements and output sections can now leave their canvas, and snap to its edges.** Both editors clamped
   position to `[0, 1-size]`, which forbade a lower third sliding in from off-canvas, a source oversized to
   fill-and-crop, and a composition letterboxed off-centre in a mismatched output. The range now runs from
   "exactly fully before the canvas" to "exactly fully after it" — finite, so nothing can be pushed anywhere
   it cannot be dragged back from — and lives in `Models/NormalizedRectRange` because it is a MODEL rule: a
   typed or deserialized rectangle must land in the same range as a dragged one, which a clamp in the canvas
   could not guarantee. Snapping (edges + centre, a 7 px *screen-space* pull so it feels identical on any
   canvas size, Ctrl to override for one drag) is the interaction half and lives with the canvases in
   `Views/Controls/PlacementSnapMath`, unit-tested in `PlacementSnapMathTests`. Both editors have a "Snap to
   edges" toggle, on by default: centring is how letterboxing is authored, and dead centre is one exact value
   in a continuum. Boxes are clipped to the editor control so an off-canvas placement is visible without
   painting over the form.