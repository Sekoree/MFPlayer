# Playback + Soundboard Enablement Plan

Last updated: **2026-04-25**

Primary reference: [`Playback-And-Soundboard-Patterns.md`](./Playback-And-Soundboard-Patterns.md).

## Goal

Make soundboard-style and advanced playback workflows significantly easier to build
on top of the current framework, **without removing or regressing** existing
features (`MediaPlayer`, `AVRouter`, endpoint fan-out, NDI playback glue, current tests).

## Non-Goals

- No playlist/cue/session ownership inside `MediaPlayer`.
- No forced app architecture in framework core.
- No breaking changes to existing public APIs unless explicitly approved in a later phase.

## Compatibility Guardrails (Must Hold)

1. Existing `MediaPlayer` open/play/pause/stop flows keep behavior and event semantics.
2. Existing `AVRouter` routing APIs remain source-compatible.
3. Existing sample apps continue to build after each phase.
4. New helpers are additive and optional.
5. Every phase ships with regression tests against current behavior.

---

## Concrete Work Plan

### Phase 0 — Safety Net and Baseline (1 week)

- Capture baseline:
  - `dotnet build MFPlayer.sln`
  - current test pass snapshot for `S.Media.Core.Tests` and `S.Media.FFmpeg.Tests`
  - sample app smoke matrix (`SimplePlayer`, `MultiOutputPlayer`, `VideoPlayer`, `NDIPlayer`)
- Add API-compat snapshot checks for `S.Media.Core` and `S.Media.Playback`.
- Add docs CI check so examples compile (or are validated via snippet tests).

**Exit criteria**

- Baseline artifacts committed.
- No unknown test failures.

### Phase 1 — App-Layer Helper Package Skeleton (1 week)

Create a new optional package, app/helper focused:

- `Media/S.Media.AppToolkit` (name can be adjusted),
- references `S.Media.Core`, `S.Media.Playback`, `S.Media.FFmpeg`,
- contains no UI and no persistence schema.

Initial abstractions:

- `ClipId`, `ClipHandle`, `ClipState`,
- `IClipRuntime` (`PrepareAsync`, `TriggerAsync`, `StopAsync`, `UnloadAsync`),
- `IRouteProfileResolver` (maps clip -> endpoints/options).

**Exit criteria**

- Empty but compiling runtime skeleton.
- No dependency-cycle violations.

### Phase 2 — Clip Lifecycle Runtime (2 weeks)

Implement robust clip runtime for overlapping trigger playback:

- Per-trigger lifecycle state machine:
  - `Preparing`, `Ready`, `Playing`, `Stopping`, `Completed`, `Faulted`.
- Internal ownership model:
  - decoder instance,
  - router input registration,
  - route registration(s),
  - deterministic cleanup on completion/failure/cancel.
- Input transport capability:
  - add optional `ISeekableInput` capability (not on base `IMediaChannel`) and prefer per-input seek/rewind semantics for clip control,
  - expose router helpers (`TrySeekInput`, `TryRewindInput`) for `InputId`-based runtimes,
  - keep this additive and compatible with non-seekable live inputs.
- Structured events:
  - `ClipTriggered`, `ClipStarted`, `ClipCompleted`, `ClipFailed`.

**Exit criteria**

- A clip can be prepared, triggered, and cleaned up without resource leaks.
- Overlapping triggers to one endpoint are validated by tests.
- Seekable clip inputs can be rewound without affecting unrelated active inputs/routes.

### Phase 3 — Warm Preload Pool (2 weeks)

Add optional preload manager:

- Pre-open N decoders for fast trigger startup.
- Memory budget + LRU eviction.
- Max concurrent decode-open operations guard.
- Pool metrics:
  - hit rate, cold starts, preload latency, evictions.

**Exit criteria**

- Trigger latency reduced in benchmarks vs cold-open flow.
- Pool behaves deterministically under memory pressure.

### Phase 4 — Trigger Policy Engine (2 weeks)

Add deterministic retrigger behavior:

- `Overlap` (always start new instance),
- `Restart` (stop current and restart),
- `IgnoreIfPlaying`,
- `ChokeGroupStop` (stop conflicting group on new trigger).

Also add per-clip concurrency limits and debouncing windows.

**Exit criteria**

- Policy conformance tests cover race conditions and burst triggering.

### Phase 5 — Route/Gain Profiles and Ducking Hooks (2 weeks)

Add app-level routing presets:

- Clip routing profile:
  - target endpoint set,
  - initial route gain,
  - optional route delay/offset fields.
- Optional ducking coordinator hooks:
  - sidechain group,
  - attack/release envelopes.

Implementation note:

- Use existing `SetRouteGain` at runtime for non-destructive automation.
- Keep ducking orchestration in helper package, not in `MediaPlayer`.

**Exit criteria**

- One clip can route to multiple endpoints via profile.
- Gain automation and ducking hooks verified with integration tests.

### Phase 6 — Developer Experience and Samples (1 week)

- Add a dedicated sample app:
  - `Test/MFPlayer.SoundboardRuntimeSample` (or similar),
  - demonstrates preload + trigger policy + overlapping playback.
- Expand docs with copy-paste recipes and failure-mode handling.
- Add troubleshooting section (latency, underruns, CPU spikes).

**Exit criteria**

- Sample app demonstrates all target helper capabilities.
- Docs are consistent with shipped APIs.

---

## Test Strategy (Regression + New)

### Regression suites (must stay green)

- Existing `S.Media.Core.Tests`.
- Existing `S.Media.FFmpeg.Tests`.
- Existing playback sample app compile checks.

### New suites

- `ClipRuntimeTests`:
  - lifecycle transitions,
  - cleanup idempotency,
  - trigger while stopping.
- `PreloadPoolTests`:
  - budget enforcement,
  - LRU correctness,
  - warm/cold path metrics.
- `TriggerPolicyTests`:
  - overlap/restart/ignore/choke semantics.
- `RouteProfileTests`:
  - multi-endpoint mapping,
  - per-route gain initialization.
- `StressTests`:
  - burst trigger stability,
  - long-run leak checks.

---

## Rollout and Risk Control

- Release each phase behind additive APIs only.
- Keep old usage patterns documented until new helpers are stable.
- Maintain a "fallback path" recipe using raw `AVRouter` + `FFmpegDecoder` throughout rollout.
- Track metrics before/after each phase:
  - trigger-to-audible latency,
  - failure rate,
  - cleanup lag,
  - peak memory.

## Estimated Timeline

- Total: **9-11 weeks** sequential.
- With parallel tracks (runtime + samples/docs): **6-8 weeks**.

## Done Definition

This plan is complete when:

1. A developer can build a production-like soundboard flow using helper APIs with <200 lines of app orchestration code.
2. Existing `MediaPlayer` and `AVRouter` scenarios remain backward compatible and tested.
3. The helper layer remains optional and does not bloat framework core responsibilities.
