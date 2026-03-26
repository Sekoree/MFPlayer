# NDI Dropped-Frame Findings and Remediation Plan

This document captures observed dropped-frame risks in the current `S.Media.NDI` receive path and a concrete plan to fix them.

Scope:

- `Media/S.Media.NDI/*` receive pipeline (`NDIAudioSource`, `NDIVideoSource`, `NDIEngine`)
- `Test/NdiVideoReceive/Program.cs` preview harness behavior
- Relevant NDI wrappers in `NDI/NdiLib/NdiWrappers.cs`

## Current-State Findings

## 1) Shared receiver is consumed independently by audio and video readers (highest risk)

- Audio path calls `NdiReceiver.CaptureScoped(...)` in `Media/S.Media.NDI/Input/NDIAudioSource.cs` (`ReadSamples`).
- Video path calls `NdiReceiver.CaptureScoped(...)` in `Media/S.Media.NDI/Input/NDIVideoSource.cs` (`ReadFrame`).
- `CaptureScoped` frees whichever frame type it gets when disposed (`NDI/NdiLib/NdiWrappers.cs`), so one consumer can pull and discard frames intended for the other.
- Effect: avoidable no-frame cases, bursty underruns, and frame starvation under load.

## 2) End-to-end pacing can miss frame budget

- Audio read timeout: 8ms (`NDIAudioSource`).
- Video read timeout: 16ms (`NDIVideoSource`).
- Preview loop also sleeps 16ms every iteration (`Test/NdiVideoReceive/Program.cs`).
- Worst-case loop timing can exceed real-time budget for 60fps and increase late drops.

## 3) Video jitter dequeue policy can hold available frames

- `TryDequeueBufferedFrame` only dequeues when queue depth is at least `_videoJitterBufferFrames`.
- If depth drops below threshold, reads report unavailable even with buffered frames present.
- Effect: unnecessary fallback/drop behavior after transient network jitter.

## 4) Queue overflow policy handling is incomplete

- `NDIQueueOverflowPolicy` defines `DropOldest`, `DropNewest`, `RejectIncoming`.
- Overflow handling in `EnqueueCapturedFrame` distinguishes only `RejectIncoming`; otherwise it drops oldest.
- Effect: `DropNewest` setting has no unique runtime behavior.

## 5) Per-frame fallback cache allocation creates GC pressure

- `CacheFallbackFrame` allocates a new `byte[]` snapshot for each captured/presented frame.
- At 1080p60 this can produce heavy allocation churn.
- Effect: GC pauses can increase jitter and downstream drops.

## 6) Video timeline progression is fixed to 60fps

- `NDIVideoSource` sets `PositionSeconds = CurrentFrameIndex / 60.0`.
- Source frame rate and timestamps are not used for timeline progression.
- Effect: A/V gating in preview loop can treat valid frames as early/late and drop more than necessary.

## 7) Drop diagnostics are too coarse

- `FramesDropped` is aggregated across different causes (rejected read, overflow, fallback unavailable, etc.).
- No per-cause counters make tuning difficult.

## Remediation Plan (Phased)

## Phase 0 - Instrumentation first (short, low risk)

Goal: measure real bottlenecks before changing behavior.

Changes:

- Extend diagnostics with drop-reason counters:
  - read rejected
  - queue overflow oldest/newest/reject incoming
  - timeout/no frame
  - fallback unavailable
  - sync late-drop (harness level)
- Add moving averages or p95 for read latency (`LastReadMs` successor metrics).
- Log queue depth high-water marks and underrun streaks.

Deliverables:

- `NDIAudioDiagnostics` and `NDIVideoDiagnostics` expanded.
- `NDIEngine` snapshot aggregates new counters.
- `Test/NdiVideoReceive` periodic status includes new metrics.

Acceptance:

- Drop causes are attributable without code stepping.

## Phase 1 - Fix ingestion architecture (highest impact)

Goal: prevent cross-stream frame consumption loss.

Changes:

- Move to single ingest owner per receiver:
  - Option A: dedicated ingest worker using `NdiReceiver.CaptureScoped`, demux to audio/video queues.
  - Option B: adopt `NdiFrameSync` for receive pull and keep shared lock around frame-sync calls.
- `NDIAudioSource` and `NDIVideoSource` become queue/ring consumers, not direct receiver pollers.

Deliverables:

- Receiver ingest service owned by `NDIEngine` (or a per-item coordinator).
- Audio/video sources subscribe to buffered outputs.

Acceptance:

- Under steady input, no starvation due to one stream draining the other.
- `NDIVideoFallbackUnavailable` incidence materially reduced.

## Phase 2 - Jitter buffer and overflow semantics cleanup

Goal: improve continuity during bursts while preserving low-latency options.

Changes:

- Change video dequeue logic to:
  - require initial priming once,
  - then allow dequeue when queue has any frame.
- Implement real `DropNewest` behavior.
- Make max queue depth and priming policy explicit in options.
- Add tests for all overflow policies and priming transitions.

Acceptance:

- Fewer fallback events during short jitter spikes.
- Option behavior matches enum names and tests.

## Phase 3 - Allocation and copy-path optimization

Goal: reduce GC and copy overhead in hot paths.

Changes:

- Replace fallback snapshot per-frame allocations with pooled/reused buffer.
- Reuse conversion buffers where possible (video + audio interleave scratch).
- Audit lock scope around copy work; keep lock only for ring/queue metadata updates.

Acceptance:

- Lower allocation rate and fewer gen-2 pauses during preview.
- Stable frame pacing under sustained 1080p60 runs.

## Phase 4 - Timeline and pacing correctness

Goal: reduce policy-caused late drops.

Changes:

- Use source timestamps/frame-rate metadata for video presentation timeline.
- Remove fixed 60fps progression assumption.
- Replace unconditional `Thread.Sleep(16)` in preview harness with deadline-based pacing.

Acceptance:

- Lower `lateDrop` counts at equivalent latency settings.
- Better audio/video drift stability across sources with different frame rates.

## Verification and Benchmark Plan

## Baseline scenarios

- Scenario A: local high-quality source, stable network.
- Scenario B: moderate jitter (simulated congestion).
- Scenario C: sustained CPU pressure on receiver host.

## Metrics to capture

- Video: captured, dropped by reason, fallback presented, queue depth min/max.
- Audio: frames captured, underruns, dropped by reason.
- Sync: average drift and peak drift.
- Performance: CPU usage, allocation rate, GC pause time, read latency p95.

## Test strategy

- Unit tests (`Media/S.Media.NDI.Tests`):
  - overflow-policy semantics
  - jitter prime/dequeue transitions
  - diagnostics counter increments per reason
  - timeline position monotonicity and source-rate correctness
- Harness tests (`Test/NdiVideoReceive`):
  - 5-10 minute soak with periodic metrics snapshots
  - parameter sweeps for jitter and sync thresholds

## Rollout Sequence

1. Land diagnostics changes first (Phase 0).
2. Land single-ingest architecture behind feature flag/config gate (Phase 1).
3. Enable new ingestion path in harness and compare baseline metrics.
4. Land jitter/overflow semantics (Phase 2) and rerun benchmarks.
5. Land allocation/copy optimizations (Phase 3).
6. Land timeline and pacing corrections (Phase 4).
7. Promote defaults only after benchmark pass criteria are met.

## Suggested initial work items (next sprint)

- Add per-cause drop counters and expose in `NDIEngine` snapshot.
- Implement and test `DropNewest` behavior.
- Adjust dequeue logic to avoid holding available frames after initial priming.
- Prototype single-ingest coordinator and run A/B benchmark against current path.

