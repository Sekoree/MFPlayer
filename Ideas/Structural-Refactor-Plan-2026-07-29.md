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
| 2 | **D** epoch identity on clocks | Small change, removes an inference class, simplifies B and A |
| 3 | **B** level / stop bus | Fixes an operator-visible gap (panic fader misses soundboard); centralises what A needs |
| 4 | **A** voices as a list | Biggest and riskiest — lands on B, behind the crossfade suites |
