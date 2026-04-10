# Video Mixer Evolution Plan (2026-04-10)

Goal:
Bring the video pipeline closer to audio parity while keeping complexity low:
- multiple input channels,
- multiple outputs/sinks,
- one active input per target (no blending/compositing),
- basic pixel conversion (any -> `Rgba32` baseline),
- basic framerate/PTS pacing so playback speed is correct.

## Principles

1. Keep hot path simple and deterministic.
2. Avoid introducing heavy conversion stacks now.
3. Keep public API incremental and source-compatible where possible.
4. Build in phases with tests after each phase.

## Current baseline

- `VideoMixer` currently supports many channels but one global active channel.
- `SDL3VideoOutput` presents one stream.
- `IVideoSink` exists but had no routing orchestration in the mixer.
- `FFmpegVideoChannel` already outputs `Bgra32` by default.

## Phase A (implemented in-progress)

### A1. Mixer API extension

- Extend `IVideoMixer` with:
  - `RegisterSink(IVideoSink sink)`
  - `UnregisterSink(IVideoSink sink)`
  - `SetActiveChannelForSink(IVideoSink sink, Guid? channelId)`
  - `SinkCount`
  - `PresentNextFrame(TimeSpan clockPosition)` (clock-aware)

### A2. Multi-target routing model

- Keep one global leader target (for `IVideoOutput`).
- Add N sink targets, each with exactly one active channel.
- No blending: each target independently selects one channel.

### A3. Basic pacing

- Use `clockPosition` + frame `Pts`:
  - hold frame if early,
  - advance when due,
  - drop overly stale queued frame.

### A4. Basic conversion

- Convert all pulled frames to canonical `Rgba32`.
- Convert leader frame to `OutputFormat.PixelFormat` when needed.
- Keep sink output in `Rgba32` for now.
- For unsupported source formats in v1 converter, emit black RGBA frame to preserve timing.

## Phase B (next)

1. Add explicit metrics/counters in `VideoMixer`:
   - held frames,
   - dropped stale frames,
   - conversion fallbacks-to-black.
2. Add optional target policy knobs:
   - lead tolerance,
   - stale-drop threshold.
3. Add first sink implementation candidate (`NDIVideoSink` or test sink adapter).

## Phase C (audio/video alignment)

1. Define a shared A/V sync policy:
   - audio master by default,
   - optional video master for video-only scenarios.
2. Add an integration harness with FFmpeg audio+video channels:
   - assert drift bounds over time,
   - assert behavior across seeks.

## API shape summary

- Keep `IVideoOutput` unchanged for now.
- Keep `VideoMixer` as orchestration center for channel->target selection.
- Keep `IVideoSink.ReceiveFrame` non-blocking contract.

## Risks and mitigations

- Risk: conversion coverage is incomplete.
  - Mitigation: keep fallback-to-black explicit and measurable.
- Risk: memory ownership bugs across targets.
  - Mitigation: deterministic dispose rules in `VideoMixer` and tests around owner handoff.
- Risk: framerate jitter under vsync mismatch.
  - Mitigation: basic PTS pacing now; policy knobs in Phase B.

## Acceptance criteria for current phase

1. Build passes for `S.Media.Core` and `S.Media.SDL3`.
2. Existing video sample (`MFPlayer.VideoPlayer`) still runs with current FFmpeg output path.
3. New `VideoMixer` unit tests cover:
   - sink registration,
   - per-sink active channel selection,
   - pacing hold-until-due,
   - basic BGRA->RGBA conversion path.

