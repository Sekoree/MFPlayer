# FFmpeg Migration Plan (`S.Media.FFmpeg`)

Source references:
- `Media/S.Media.FFmpeg/API-outline.md`
- `Media/S.Media.Core/API-outline.md`
- `Media/S.Media.Core/PLAN.smedia-architecture.md`
- `Media/S.Media.Core/error-codes.md`

Metadata:
- Last updated: `2026-03-24`
- Status legend source: `Media/S.Media.Core/PLAN.smedia-architecture.md` (`Shared Wording Template` section)

## Scope

This plan migrates proven FFmpeg decoding behavior from legacy `VideoLibs/Seko.OwnAudioNET.Video` internals into:
- `Media/S.Media.FFmpeg`

Goals:
- Keep proven decode throughput and A/V sync behavior.
- Preserve deterministic int-first contracts (`0` success, non-zero failure).
- Preserve ownership/lifetime guarantees and bounded-queue behavior.
- Keep `S.Media.FFmpeg` public API aligned to new source/media-item model.

Out of scope:
- class-by-class file moves from legacy projects
- legacy public type compatibility wrappers
- reintroducing legacy session types as public API

## Adaptation Policy (Hard Rule)

- Legacy classes are reference material only.
- Do not move legacy class files or keep legacy class identities in the new module.
- Reuse proven implementation strategies and algorithms, but adapt into `S.Media.*` contracts and naming.
- Public surface must remain the planned new types (`FFAudioSource`, `FFVideoSource`, `FFMediaItem`, options/contracts in current outlines).
- Any copied logic must be normalized to current ownership/error/teardown rules before merge.

## Locked Runtime Semantics

- Source-first public model: `FFAudioSource`, `FFVideoSource`, `FFMediaItem`.
- Runtime bootstrap is consumer-owned via `FFmpeg.AutoGen` setup APIs.
- Single-reader per source instance; same-instance concurrent read returns `FFmpegConcurrentReadViolation` (`2014`).
- Invalid open/config combos return `FFmpegInvalidConfig` (`2010`) with no partial-open side effects.
- Seek rules: invalid seek returns non-zero, no clamping, no state mutation.
- Duration rules: known finite media => finite non-negative; unknown/live => `double.NaN`.
- Teardown fence: no post-dispose callbacks/events.

## Legacy to Target Adaptation Matrix

### Decoder and demux internals

| Legacy source | Target | Action | Adaptation note |
| --- | --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFAudioDecoder.cs` | `Media/S.Media.FFmpeg/Decoders/Internal/FFAudioDecoder.cs` | Adapt | Preserve decode loop/perf behavior under new error/lifetime contracts |
| `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFVideoDecoder.cs` | `Media/S.Media.FFmpeg/Decoders/Internal/FFVideoDecoder.cs` | Adapt | Preserve frame decode throughput and queue discipline |
| `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFVideoDecoder.PixelFormat.cs` | `Media/S.Media.FFmpeg/Decoders/Internal/FFPixelConverter.cs` | Adapt/split | Preserve pixel-format conversion behavior with new type boundaries |
| `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFSharedDemuxSession.cs` | `Media/S.Media.FFmpeg/Decoders/Internal/FFSharedDemuxSession.cs` | Adapt | Keep synchronized A/V demux strategy and deterministic teardown |
| `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFSharedDemuxSessionOptions.cs` | `Media/S.Media.FFmpeg/Config/FFmpegOpenOptions.cs`, `Media/S.Media.FFmpeg/Config/FFmpegDecodeOptions.cs` | Re-map | Fold options into new split config model |
| `VideoLibs/Seko.OwnAudioNET.Video/Decoders/FFVideoDecoderOptions.cs` | `Media/S.Media.FFmpeg/Config/FFmpegDecodeOptions.cs` | Re-map | Keep decode-thread and queue settings with documented clamping |

### Stream metadata and probing

| Legacy source | Target | Action | Adaptation note |
| --- | --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video/Probing/MediaStreamCatalog.cs` | `Media/S.Media.FFmpeg/Runtime/FFStreamDescriptor.cs` + `Media/S.Media.Core/Media/*StreamInfo.cs` | Adapt/split | Keep stream discovery behavior, expose typed Core info contracts |
| `VideoLibs/Seko.OwnAudioNET.Video/Probing/MediaStreamInfoEntry.cs` | `Media/S.Media.FFmpeg/Runtime/FFStreamDescriptor.cs` | Adapt | Normalize metadata shape to current descriptor contracts |
| `VideoLibs/Seko.OwnAudioNET.Video/Probing/MediaStreamKind.cs` | `Media/S.Media.Core` stream typing contracts | Re-map | Avoid legacy enum leakage into new public FFmpeg surface |

### Source behavior references

| Legacy source | Target | Action | Adaptation note |
| --- | --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video/Sources/AudioStreamSource.cs` | `Media/S.Media.FFmpeg/Sources/FFAudioSource.cs` | Adapt | Preserve read/seek behavior using current `IAudioSource` contracts |
| `VideoLibs/Seko.OwnAudioNET.Video/Sources/VideoStreamSource.cs` | `Media/S.Media.FFmpeg/Sources/FFVideoSource.cs` | Adapt | Preserve frame read/seek behavior using current `IVideoSource` contracts |
| `VideoLibs/Seko.OwnAudioNET.Video/Sources/VideoStreamSourceOptions.cs` | `Media/S.Media.FFmpeg/Config/*` | Re-map | Fold relevant options into new FFmpeg option surfaces |
| `VideoLibs/Seko.OwnAudioNET.Video/Sources/BaseVideoSource.cs` | `Media/S.Media.FFmpeg/Sources/*` internals | Adapt/split | Keep shared mechanics without legacy base-class carryover |

## Migration Status Tracker

`Status` values use the shared legend (`Planned`, `In Progress`, `Done`, `Blocked`).

| Area | Target path | Status | Notes |
| --- | --- | --- | --- |
| Decoder internals | `Media/S.Media.FFmpeg/Decoders/Internal/*` | Planned | Adapt proven loops/session behavior; no class moves |
| Source wrappers | `Media/S.Media.FFmpeg/Sources/*` | Planned | Align to `IAudioSource` / `IVideoSource` contracts |
| Config mapping | `Media/S.Media.FFmpeg/Config/*` | In Progress | Decode-thread validation and queue/thread normalization are now deterministic |
| Media item construction | `Media/S.Media.FFmpeg/Media/FFMediaItem.cs` | Planned | Preserve ownership and stream-open semantics |
| Contract tests | `Media/S.Media.FFmpeg` test matrix | Planned | Validate `2010`, `2014`, ownership, and teardown fence |

## Public vs Internal Split

Public (`S.Media.FFmpeg`):
- `FFAudioSource`, `FFVideoSource`, `FFMediaItem`
- `FFmpegOpenOptions`, `FFmpegDecodeOptions`, `FFAudioSourceOptions`, mapping types

Internal-only:
- demux/decode workers and native interop plumbing
- packet reader, resampler, pixel conversion internals
- queueing/session coordination details

## Implementation Sequence

1. Adapt core decoder internals and shared demux session into `Decoders/Internal`.
2. Wire config translation (`FFmpegOpenOptions`, `FFmpegDecodeOptions`) and deterministic validation.
3. Implement source wrappers (`FFAudioSource`, `FFVideoSource`) against Core contracts.
4. Implement `FFMediaItem` construction paths (including stream-based paths).
5. Validate seek/ownership/concurrency contracts and teardown fences.
6. Run performance regression checks against representative heavy codecs/containers.

## Validation Gates

- Invalid-config matrix cases return deterministic `2010` with no partial-open state.
- Same-instance concurrent reads return `2014` and map to semantic `950`.
- Stream ownership contract respects `LeaveInputStreamOpen`.
- Caller buffer/frame ownership is preserved (no retained caller memory).
- Duration semantics match contract (`double.NaN` for unknown/live).
- Decode-thread/queue settings apply deterministic clamping and behavior.
- Heavy-file stress path is opt-in (`SMEDIA_RUN_HEAVY_STRESS=1`) to keep default test runs stable while enabling local 4k60 throughput checks.

## Ownership and Error Rules

- Prefer specific FFmpeg module codes when available; only fall back to generic orchestration codes when no specific owned code applies.
- Failures must be atomic: no partially-open handles/workers on failed open/start/seek/dispose paths.
- Secondary failures during multi-step teardown are diagnostics-only and do not override the primary deterministic return code.

