# Tier 5 Framework Capability Plan (Lean Baseline)

Last updated: **2026-04-25**

Related split plan: [`Project-Modularization-Plan.md`](./Project-Modularization-Plan.md).

## Intent

This plan now tracks the **lean baseline**:

- a robust media playback framework,
- a fast-start `MediaPlayer` facade for common workflows,
- full control via `AVRouter` for advanced and multi-source scenarios.

`MediaPlayer` is intentionally **not** the place for playlist/session/workflow ownership.
Those belong to consuming apps/helper libraries.

---

## Hard Boundary Rules

- Framework must provide reusable runtime primitives (routing, clocks, playback lifecycle, diagnostics).
- Framework must not own product workflows (playlist/cue stacks, show control state, session persistence).
- Framework must not encode UI concerns.
- Optional protocol glue remains leaf-level and should not bloat core packages.
- If a feature mostly answers operator workflow, keep it outside framework core.

---

## Baseline Scope (Keep)

The current baseline focuses on:

- `Media/S.Media.Core/*`
  - `AVRouter`, route controls, endpoint abstractions, diagnostics.
- `Media/S.Media.Playback/*` (lean)
  - `MediaPlayer`, `MediaPlayerBuilder`, lifecycle/events, seek semantics, external input wiring.
- `NDI/S.Media.NDI.Playback/*` (optional)
  - playback-specific NDI builder glue as a leaf integration.

### MediaPlayer rule

- `MediaPlayer` = quick start for open/play/pause/stop and basic endpoint fan-out.
- No playlist model, no cue graph, no soundboard domain inside `MediaPlayer`.
- For complex transport/orchestration, use `IAVRouter` directly.

---

## De-scoped From Baseline

The following were removed from this branch to restore baseline focus:

- `Media/S.Media.Composition/*`
- `OSC/S.Media.Composition.OSC/*`
- `MIDI/S.Media.Composition.MIDI/*`
- `Test/S.Media.Composition.Tests/*`
- `Test/S.Media.Composition.Protocol.Tests/*`
- `Media/S.Media.Playback/Playlist/*`
- `Test/S.Media.FFmpeg.Tests/DecoderPreopenCoordinatorTests.cs`
- `Test/S.Media.FFmpeg.Tests/GaplessIntegrationTests.cs`
- `Test/S.Media.FFmpeg.Tests/RouteGainAutomationTests.cs`

Also removed from `MFPlayer.sln`:

- `S.Media.Composition`
- `S.Media.Composition.OSC`
- `S.Media.Composition.MIDI`
- `S.Media.Composition.Tests`
- `S.Media.Composition.Protocol.Tests`

---

## Current Capability Snapshot

Status legend:

- `Ready` = production-capable in baseline
- `Deferred` = intentionally app/helper-layer

| Capability | Status | Notes |
|---|---|---|
| Multi-input, multi-endpoint routing | Ready | `AVRouter` supports explicit routing/mixing |
| Route runtime controls (`SetRouteEnabled`, `SetRouteGain`) | Ready | Per-route control retained in core |
| Clock registry + active clock resolution | Ready | Router-level clock priority model |
| Playback lifecycle/events | Ready | `MediaPlayer` + `MediaPlayerBuilder` |
| External live input wiring | Ready | Builder supports external audio/video inputs |
| Diagnostics snapshot + stream | Ready | Router diagnostics retained |
| Playlist/cue/soundboard domain runtime | Deferred | Out of framework baseline |
| Session persistence/migration | Deferred | App/helper responsibility |
| OSC/MIDI action runtime | Deferred | Optional adapter track, not baseline |

---

## Execution Focus (Near Term)

1. Harden playback/router reliability and test coverage.
2. Keep `MediaPlayer` small and predictable.
3. Improve docs/samples for:
   - quick-start playback,
   - advanced `AVRouter` orchestration,
   - soundboard-like app-layer composition patterns.
4. Reintroduce higher-level domains only behind a separate explicit scope decision.

---

## Optional Future Track (Not Baseline)

If/when product needs require it, cue/soundboard/protocol orchestration can return as
separate optional modules with strict dependency boundaries and app-layer ownership of
workflow/state models.
