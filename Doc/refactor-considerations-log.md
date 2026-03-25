# Refactor Considerations Log

Last updated: 2026-03-25

This log consolidates major decisions and implementation considerations agreed during the migration from legacy OwnAudio/video extensions to `S.Media.*`.

## Cross-Project Contract Decisions

- Migration model is additive-first: implement `S.Media.*`, keep legacy side-by-side during migration, remove legacy when parity is complete.
- `IMediaPlayer` inherits from `IAudioVideoMixer` (`IMediaPlayer : IAudioVideoMixer`).
- Source IDs are implementation-generated GUIDs and should be unique for process lifetime.
- Return-code contract remains deterministic: `0` success, non-zero failure code.
- Error precedence is explicit and validated in tests (for example disposed-frame errors are not masked by running-state errors).
- Invalid configuration must return owned invalid-config codes, never silent coercion for nonsensical combinations.
- Concurrency policy uses modern `System.Threading.Lock` where applicable.

## Clock and Mixer Semantics

- Single clock field with a `ClockType` mode model.
- Supported modes include `External`, `AudioLed`, `VideoLed`, and `Hybrid`.
- Invalid mixer/clock combinations must fail with config errors (not implicit fallback).

## Video Contracts

- `VideoFrame` invariants are strict:
  - pixel format and plane data consistency enforced,
  - stride/plane-length validation enforced,
  - disposed-frame push validation preserved.
- Video frame pooling is required, with idempotent cross-thread-safe dispose behavior.
- Pixel-format handling supports preferred/closest format fallback strategy where compatible.
- Clone graph ownership/policy is centralized in `OpenGLVideoEngine`; adapters project/forward, they do not own decode/session policy.

## Output Backpressure and Queueing

- Backpressure defaults prioritize deterministic behavior.
- Default queue overflow policy is `DropOldest` unless explicitly overridden.
- Queue capacity must be at least `1`; invalid values return config error.
- Validation happens before state checks in push paths where precedence requires it.

## FFmpeg Pipeline Decisions

- Native path is attempted first with deterministic fallback behavior.
- Decode `EAGAIN` must not be treated as successful decoded frame.
- Resample/payload frame count shaping is guarded to avoid sample/frame mismatch.
- Shared demux session handles multi-attempt packet/decode loops for native decode paths.
- Unknown native pixel formats use preferred/closest mapping fallback instead of immediate rejection where possible.

## PortAudio / Audio Runtime Decisions

- Engine exposes host APIs and default input/output device discovery.
- `CreateOutputByIndex(-1)` and device index `-1` semantics target discovered default output.
- Host API selection supports normalized aliases (for Linux, `pulse` / `pulseaudio` convenience handling).
- Output push semantics are blocking by contract in current implementation.
- Device/host failures return explicit owned PortAudio codes; no silent success for non-started streams.

## NDI and Diagnostics Decisions

- NDI output push follows frame-validation precedence (disposed/invalid frame first).
- Runtime initialization is failure-atomic where diagnostics thread startup fails.
- Diagnostics snapshot aggregation is part of engine-level behavior and is validated via tests.

## Naming, Style, and General Hygiene

- Acronyms are consistently uppercase (for example `NDI`, `MIDI`).
- API outlines/docs are maintained alongside implementation changes.
- Contract tests are updated in lockstep with behavior changes.

## Current Residual Focus Areas

- Continue parity checks where legacy behavior is still used as a migration reference.
- Keep status/docs synchronized as modules move from in-progress to fully migrated.
- Final legacy removal should happen only after contract and behavior parity gates pass.
