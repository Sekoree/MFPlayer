# Video Mixer Evolution Plan (2026-04-10)

Goal:
Evolve current mixers into an end-user friendly, format-agnostic, many-to-many AV router while preserving compatibility.

Target outcomes:
- Mixer core is not tied to fixed audio/video output formats.
- Conversion happens at endpoint boundaries (output/sink adapters), not inside mixer core.
- Routing and timing are decoupled from concrete output classes.
- Simple decoder -> endpoint flows remain trivial.
- Advanced many-to-many routing and multi-clock glue is available via `AVMixer`.

## Design Principles

1. Keep mixer core format-agnostic and endpoint-agnostic.
2. Move conversion/resampling/pixel adaptation to endpoint side.
3. Preserve old APIs during migration; add new APIs beside them.
4. Keep simple workflows first-class with sensible defaults.
5. Build in small phases with measurable diagnostics.

## Updated Architecture Direction

### 1) Core routing

- `VideoMixer` and `AudioMixer` evolve toward pure routing + pacing + buffering.
- `AVMixer` becomes the orchestration layer for many-to-many audio/video routing.
- Mixer does not need `Output`/`Sink` objects as hard dependencies.

### 2) Endpoint model (migration)

- Introduce endpoint adapters that can be used by both outputs and sinks.
- Keep `Output` and `Sink` for now, but converge behavior toward a common endpoint contract.
- Candidate unified model:
  - push mode: endpoint accepts frames/buffers,
  - pull mode: endpoint requests frames/buffers via delegate.

### 3) Conversion boundary

- Endpoint decides whether incoming media format is directly supported.
- If unsupported, endpoint performs conversion using available converters/shaders/libyuv/resamplers.
- Mixer should prefer route format passthrough when possible.

### 4) Cloning and fan-out

- Add output cloning for video: one decoded channel can feed multiple render surfaces.
- Example: main Avalonia OGL control + monitoring preview control without extra decode.

### 5) Presets and defaults

- Add end-user profiles for outputs/sinks: `Safe`, `Balanced`, `LowLatency`.
- Start with NDI-oriented defaults (buffer sizing, queue depth, drop policy, pacing).
- Keep automatic hardware decode enabled by default.

## Phased Checklist

### Phase A - Foundation (completed/ongoing)
- [x] Basic multi-target video routing.
- [x] Sink format preferences and capabilities.
- [x] Initial `IAVMixer`/`AVMixer` facade.

### Phase B - Mixer decoupling (next)
- [ ] Remove format assumptions from mixer internals where possible.
- [ ] Move remaining conversion logic from mixer into endpoint adapters.
- [ ] Keep compatibility shims for existing `IVideoOutput.Mixer` / `IAudioOutput.Mixer` usage.

### Phase C - Endpoint unification
- [x] Introduce common endpoint abstraction (push + optional pull delegate).
- [x] Implement adapters for existing `Output` and `Sink` APIs.
- [ ] Decide long-term API: keep both concepts or converge on endpoint type.

### Phase D - AV router power features
- [~] Expand `AVMixer` into full many-to-many router (audio + video + clock links).
- [x] Add explicit clock-master policies (audio/video/external).
- [x] Add route groups for simple decoder->endpoint one-liners.

### Phase E - Cloning and profiles
- [x] Add video output cloning primitives and sample app usage.
- [x] Add `Safe` / `Balanced` / `LowLatency` endpoint presets (NDI first).
- [x] Add diagnostics snapshots for route format passthrough vs conversion.

## Acceptance Criteria for Next Milestone

1. Build passes for `S.Media.Core`, `S.Media.SDL3`, `S.Media.Avalonia`, and test projects.
2. One sample demonstrates `AVMixer` in simple decoder->endpoint flow. (Implemented in `MFPlayer.VideoPlayer`)
3. Mixer diagnostics clearly separate routing from conversion statistics. (Implemented via `VideoMixer.DiagnosticsSnapshot.SameFormatPassthrough` / `RawMarkerPassthrough` / `Converted`)
4. At least one endpoint preset set is implemented and documented.

