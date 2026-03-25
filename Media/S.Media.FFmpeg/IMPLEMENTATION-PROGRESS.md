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
- Validator coverage expanded for additional invalid-config matrix cases:
  - negative stream indices
  - URI + stream-only option contradictions (`InputFormatHint`, `LeaveInputStreamOpen=false`)

### 2.5) Concurrency and Locking Contract Alignment

- `FFSharedDemuxSession` no longer performs packet/decode/convert work under the shared state lock.
- Decode pipeline work is serialized via dedicated pipeline lock while queue/state transitions remain under the state lock.
- Source-level same-instance concurrent read rejection is enforced:
  - audio/video read APIs now return `FFmpegConcurrentReadViolation` (`2014`) on concurrent read attempts.

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
- Shared demux session now emits descriptor-refresh snapshots on open/seek for metadata synchronization.

### 8) Media Item Semantics Alignment

- `FFMediaItem` now propagates effective seekability to created sources (`InputStream.CanSeek` aware).
- Known stream durations now flow to `DurationSeconds`; unknown/unbounded remains `double.NaN`.
- Baseline metadata snapshot support is now active:
  - `FFMediaItem` implements `IDynamicMetadata`
  - metadata is initialized from open/source context and stream descriptors
  - metadata publication respects teardown-fence behavior after dispose.
- Dynamic metadata refresh path is now wired from shared-session descriptor refresh events into `FFMediaItem` metadata snapshots.
- Duplicate metadata snapshots (same key/value content) are suppressed to avoid redundant callback churn.

### 9) Plane-Aware Conversion Policy

- `FFPixelConverter` now applies an explicit plane-aware policy:
  - mapped multi-plane native formats (`YUV*`, `NV12`, `P010`) preserve native plane payloads when required planes are present
  - RGBA conversion is no longer forced for these cases in the placeholder/native-attempt bridge path
- Additional deterministic tests now assert YUV420/NV12 multi-plane preservation behavior.

### 9.5) Incomplete Multi-Plane Normalization Guard

- `FFPixelConverter` now normalizes incomplete mapped multi-plane payloads to `Rgba32` fallback shape.
- Incomplete `YUV420P` / `NV12` / `P010` mapped payloads no longer leak as invalid multi-plane frame contracts.
- Fallback `Rgba32` output now enforces deterministic plane layout (`Plane0` + stride, no secondary planes).

## Test Coverage Status

Implemented tests now cover:

- config/validation semantics
- shared context lifecycle and descriptor override
- shared demux lifecycle/seek behavior
- decoder internals (native-attempt fallback behavior)
- resampler and pixel-converter fallback + metadata snapshot behavior
- source-level session-fed read/seek behavior
- source-level concurrent-read rejection (`2014`)
- shared-session contention paths (`Read` vs `Seek`, `Read` vs `Close`) without deadlock
- metadata baseline + teardown-fence behavior
- dynamic metadata refresh callback behavior
- duplicate metadata snapshot suppression behavior
- explicit plane-aware multi-plane conversion preservation behavior
- incomplete multi-plane mapped payload normalization to deterministic `Rgba32` fallback shape
- FFmpeg->PortAudio baseline push-path integration behavior
- heavy metadata seek-churn cadence guard (opt-in heavy path)
- heavy opt-in integration scaffolds

## Latest Verified Test Results

Most recent verified runs:

- `dotnet test Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore`
  - total: 95
  - succeeded: 91
  - failed: 0
  - skipped: 4 (heavy tests, when not opted in)

- `RUN_HEAVY_FFMPEG_TESTS=1 dotnet test Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore --filter FullyQualifiedName~Heavy`
  - total: 3
  - succeeded: 3
  - failed: 0
  - skipped: 0

## Remaining Work (High-Level)

- continue enriching true multi-plane behavior in frame materialization where needed
- add deeper payload-content assertions for native multi-plane scenarios (not only shape/stride)
- tighten production native data flow (reduce synthetic payload use on successful native decode/convert paths)
- expand heavy-asset validation for descriptor refresh cadence and metadata update ordering under seek churn
