# S.Media.FFmpeg Implementation Progress

Last updated: 2026-03-25

This note summarizes implementation progress completed so far in `Media/S.Media.FFmpeg` and `Media/S.Media.FFmpeg.Tests`.

## Progress Snapshot

- Public source/media-item surface is in place and usable:
  - `FFMediaItem`
  - `FFAudioSource`
  - `FFVideoSource`
- Shared session pipeline is wired end-to-end for placeholder + native-attempt paths:
  - `FFPacketReader` -> `FFAudioDecoder` / `FFVideoDecoder` -> `FFResampler` / `FFPixelConverter` -> `FFSharedDemuxSession` queues -> sources
- Deterministic fallback behavior is implemented across the stack when native paths are unavailable or fail.
- Heavy integration scaffold is implemented and opt-in only.

## Completed Areas

### 1) Shared Session and Queue Plumbing

- `FFSharedDemuxSession` now has:
  - bounded audio/video queues
  - worker loop lifecycle (`Open` / `Close` / `Dispose`)
  - deterministic seek/reset behavior
  - session-fed `ReadAudioSamples` and `ReadVideoFrame`
- `FFAudioSource` and `FFVideoSource` read through session when created from `FFMediaItem` shared-open path.

### 2) Config Validation and Determinism

- Decode-thread semantics:
  - negative decode thread count -> invalid config
  - high values clamp to logical CPU count
  - queue limits clamp to at least 1
- Stream/open option validation has deterministic failure behavior.

### 3) Native Demux Attempt with Safe Fallback

- `FFPacketReader` attempts native demux for file-based inputs via FFmpeg.
- Native packet metadata is threaded when available (codec/stream/timing fields).
- On runtime binding/API failures, demux cleanly falls back to placeholder mode.

### 4) Native Decode Attempt with Safe Fallback

- `FFAudioDecoder` and `FFVideoDecoder` attempt native decode (`avcodec_*`) when native packet metadata is present.
- One-way fallback latch behavior is implemented per component instance:
  - if native init/decode fails, component disables native attempt and continues deterministic placeholder behavior.

### 5) Native Conversion/Resample Attempt with Safe Fallback

- `FFResampler` has native-attempt backend (`swr_*`) and deterministic fallback.
- `FFPixelConverter` has native-attempt backend (`sws_*`) and deterministic fallback.
- Mapped pixel-format semantics are threaded to session/source frame materialization.

### 6) Payload Threading (Current State)

- Audio:
  - decoded sample payload is carried through decode/resample/session read path
  - session read copies payload into destination span, with deterministic fill fallback when needed
- Video:
  - decoded `Plane0` payload and stride are carried through decode/convert/session/source
  - multi-plane fields (`Plane1` / `Plane2` + strides) are threaded through decode/convert/session/source structures

### 7) Stream Descriptor Upgrades

- Shared decode context supports native descriptor override after open.
- Native demux stream descriptors can replace placeholder stream descriptors when available.

## Test Coverage Status

Implemented tests now cover:

- config/validation semantics
- shared context lifecycle and descriptor override
- shared demux lifecycle/seek behavior
- decoder internals (native-attempt fallback behavior)
- resampler and pixel-converter fallback + metadata snapshot behavior
- source-level session-fed read/seek behavior
- heavy opt-in integration scaffolds

## Latest Verified Test Results

Most recent verified runs:

- `dotnet test Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore`
  - total: 70
  - succeeded: 67
  - failed: 0
  - skipped: 3 (heavy tests, when not opted in)

- `RUN_HEAVY_FFMPEG_TESTS=1 dotnet test Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore --filter FullyQualifiedName~Heavy`
  - total: 3
  - succeeded: 3
  - failed: 0
  - skipped: 0

## Remaining Work (High-Level)

- finalize plane-aware native conversion output policy for non-RGBA native formats
- continue enriching true multi-plane behavior in frame materialization where needed
- add additional payload-content assertions for multi-plane native scenarios
- begin tightening toward production native data flow (less synthetic payload use in successful native paths)

