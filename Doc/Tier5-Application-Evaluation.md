# Tier 5 Application Evaluation

## Scope

This document evaluates what additional framework parts are needed to build three
product-level apps on top of MFPlayer while preserving current advanced routing:

- multiple simultaneous audio endpoints
- multiple simultaneous video endpoints/surfaces
- per-route options (gain, delay, live/scheduled behavior, channel maps)
- mixed local + NDI fan-out

Target apps:

1. MediaPlayer
2. CuePlayer (including OSC + MIDI command send)
3. Soundboard

---

## Current Baseline (Already Strong)

MFPlayer already provides the hard low-level pieces:

- decoder/channel model (`FFmpegDecoder`, audio/video channels)
- explicit graph routing (`AVRouter`) with multi-input/multi-endpoint fan-out
- endpoint abstraction for local/NDI/render surfaces
- route-level controls (format/channel map/gain/time offset/live mode)
- clock model and diagnostics

This means the remaining work is mostly **transport/session orchestration** and
**show-control domain features**, not rebuilding playback primitives.

---

## Gaps By App

## 1) MediaPlayer

Needed additions beyond current framework:

- playlist/session model (`PlaylistItem`, metadata, in/out points, trim)
- transport queueing (next-item pre-open, gapless handoff)
- seek-complete semantics (`SeekAsync` that resolves on first post-seek present)
- state persistence (session restore with routes/endpoints)
- UX-facing event stream (buffering/preload/route-health)

Estimated additional framework work: **medium** (about 4-6 weeks).

## 2) CuePlayer (with OSC + MIDI send)

Needed additions:

- timeline/cue graph (`ITimeline`, `Cue`, `CueAction`, cue groups/stacks)
- deterministic transport scheduler (play-at time / follow actions / hold points)
- action engine (media actions + non-media actions)
- protocol adapters:
  - OSC output (UDP transport, address templates, arg encoding)
  - MIDI output (note/CC/program/MSC abstraction, device routing)
- cue-state machine (armed/running/completed/failed/skipped)
- audit/trace log suitable for show operation

Estimated additional framework work: **high** (about 8-12 weeks).

## 3) Soundboard

Needed additions:

- clip-bank model (pads, banks/pages, tags, colors, hotkeys)
- polyphonic trigger engine (many concurrent short clips)
- per-pad routing profile (which outputs/endpoints each pad targets)
- retrigger policies (restart, overlap, choke groups, one-shot/loop)
- latency-focused preloading (decode warm cache for hot pads)
- master ducking/sidechain rules (optional but common requirement)

Estimated additional framework work: **medium-high** (about 6-9 weeks).

---

## Shared New Framework Modules

To avoid building three separate vertical stacks, these shared modules cover all apps:

1. `Timeline/Transport` core
- `ITimeline`, `TimelineItem`, transport state, pre-roll scheduler.
- Closes checklist Tier 5 (`7.1`, `7.2`, `7.3` foundations).

2. `Session Model`
- serializable graph/session format:
  - sources/assets
  - endpoints
  - routes
  - per-app state (playlist, cues, pad banks)

3. `Action Bus`
- generic action dispatch (`Play`, `Pause`, `Stop`, `Seek`, `SetRouteGain`, etc.)
- extension point for OSC/MIDI sends.

4. `Protocol Adapters`
- OSC sender service
- MIDI sender service
- deterministic dispatch timestamps tied to router/transport clock.

5. `Preload/Cache Layer`
- decoder warm-up pool
- next-item pre-open for gapless transitions
- short-clip cache path for soundboard bursts.

6. `Operational Diagnostics`
- route/cue/action correlation IDs
- timeline trace and error classification for operator tooling.

---

## Practical Build Order

Recommended order to minimize churn and keep Tier 6 performance work compatible:

1. Land Timeline Core (`7.1`) + pre-open/gapless base (`7.2`).
2. Add playlist/crossfade automation (`7.3`) on top of route gain automation.
3. Add action bus + OSC/MIDI adapters (enables CuePlayer).
4. Add soundboard trigger engine using the same action bus + preload cache.
5. Add multi-NDI-source cueing/mixing (`7.4`) once discovery/registry is validated in ops scenarios.

---

## Effort Summary

If done as a unified framework track:

- Tier 5 core modules: **~8-10 weeks**
- App-specific layers:
  - MediaPlayer app layer: **~2-3 weeks**
  - CuePlayer app layer (UI/workflow over cue graph): **~3-5 weeks**
  - Soundboard app layer (UI/workflow over trigger engine): **~2-4 weeks**

Total for all three, sequentially with reuse: **~15-22 weeks**.

Parallel teams can reduce calendar time significantly if they share the core modules above.

---

## Key Design Constraint (Keep)

Do not collapse app logic into `AVRouter`.

`AVRouter` should remain the composable routing/mixing engine.
Timeline, cueing, OSC/MIDI, playlist, and soundboard semantics should live in a
higher orchestration layer (facade/services), so advanced routing remains reusable
across all app types without transport-specific coupling.
