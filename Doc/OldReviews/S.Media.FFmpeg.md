# S.Media.FFmpeg — Issues & Fix Guide

> **Scope:** `S.Media.FFmpeg` — `FFmpegMediaItem`, `FFmpegAudioSource`, `FFmpegVideoSource`, `FFSharedDemuxSession`, options types
> **Cross-references:** See `API-Review.md` §4 for the full analysis.
> **Last reviewed:** March 31, 2026 (Pass 4 — all fixes implemented)

---

## Progress Checklist

| # | Issue | Status |
|---|-------|--------|
| 1.1 | `EnableExternalClockCorrection` dead flag removed | ✅ Fixed |
| 1.2 | `OutputChannelCountOverride` applied in `TryGetEffectiveChannelMap` | ✅ Fixed |
| 2.1 | `FFmpegMediaItem.Create()` factory; `Open()`/`TryOpen()` `[Obsolete]` | ✅ Fixed |
| 3.1 | `FFmpegVideoSource` stub frame when no session | ✅ Fixed (blocked by N10 validator) |
| 3.2 | `FFmpegAudioSource` returns Success+0 when no session | ✅ Fixed (blocked by N10 validator) |
| 4.1 | `SeekToFrame` 30 fps hard fallback | ✅ Fixed — returns `MediaSourceNonSeekable` when fps unknown |
| 4.2 | Coordinated seek via `FFmpegMediaItem.Seek()` | ✅ Fixed |
| 5.1 | Full channel-mapping policy implemented | ✅ Fixed |
| 6.1 | `AudioSource`/`VideoSource` nullability trap XML docs | ✅ Fixed |
| 6.2 | `Open()`/`TryOpen()` obsoleted | ✅ Fixed |
| 7.1 | `FF*` / `FFmpeg*` prefix inconsistency — public types renamed to `FFmpeg*` | ✅ Fixed |
| 7.2 | `FFSharedDecodeContext` make `internal` | ✅ Fixed |
| 7.3 | `FFStreamDescriptor` make `internal` | ✅ Fixed |
| N1 | Network URI support (`rtsp://`, `http://`) — `File.Exists` gate removed | ✅ Fixed |
| N2 | `FFPacketReader.ReadNextPacket()` dead code | ✅ Fixed — removed |
| N3 | No-arg decoder/pipeline overloads dead code | ✅ Fixed — removed |
| N4 | `FFResampler` never calls `swr_convert` | ✅ Fixed — backend removed, documented as limitation |
| N5 | Per-packet `av_packet_alloc` in hot path | ✅ Fixed — pre-allocated per backend |
| N6 | Seek does not flush decoder codec buffers | ✅ Fixed — `avcodec_flush_buffers` called post-seek |
| N7 | `WorkerLoop` busy-polls at ~200 Hz | ✅ Fixed — `WaitOne(20)`; queue depth raised to 4 |
| N8 | Video `EndOfStream` 98% heuristic is fragile | ✅ Fixed — trusts session return code |
| N9 | `FFPixelConverter` hardcodes RGBA32 target | ✅ Fixed — configurable via `FFmpegDecodeOptions.PreferredOutputPixelFormat` |
| N10 | `UseSharedDecodeContext = false` silently creates non-functional sources | ✅ Fixed — validator rejects it |
| N11 | `InputFormatHint` is dead configuration surface | ✅ Fixed — passed to `av_find_input_format` + `avformat_open_input` |
| P4-1 | Debug oscillating `nativeSampleValue` in `FFPacketReader.ReadAudioPacket` | ✅ Fixed — replaced with `0f` |
| P4-2 | Double-seek in `FFmpegMediaItem.Seek` — session seeked N+1 times for N sources | ✅ Fixed — `NotifySeek()` skips session re-seek |
| P4-3 | `_pendingAudioChunk` not cleared on `Seek` — stale audio post-seek | ✅ Fixed — cleared alongside queues |
| P4-4 | `FFmpegAudioSource.Seek()` doesn't reset `EndOfStream` → `Running` | ✅ Fixed — parity with `FFmpegVideoSource.Seek()` |
| P4-5 | Dead `_ = sessionFrame.Has*` reads in `FFmpegVideoSource.ReadFrame` | ✅ Fixed — removed |
| P4-6 | `EnableHardwareDecode`, `LowLatencyMode`, `UseDedicatedDecodeThread` unimplemented | ✅ Documented — XML remarks flag them as reserved/not-yet-wired |
| P4-7 | `_nextAudioPresentationTime` / `_nextVideoPresentationTime` dead fields | ✅ Fixed — removed |
| P4-8 | `FFmpegAudioSource.Start()` uses bizarre `switch { _ => }` expression pattern | ✅ Fixed — explicit `if + return` |

---

## Table of Contents

1. [Dead Code & Configuration Flags](#1-dead-code--configuration-flags)
2. [Error Handling & Factory Pattern](#2-error-handling--factory-pattern)
3. [Source Behaviour Without a Session](#3-source-behaviour-without-a-session)
4. [Seeking](#4-seeking)
5. [Audio Channel Mapping](#5-audio-channel-mapping)
6. [API Surface (`FFmpegMediaItem`)](#6-api-surface-ffmpegmediaitem)
7. [Naming & Consolidation](#7-naming--consolidation)
8. [Decode Pipeline Issues (Pass 3)](#8-decode-pipeline-issues-pass-3)
9. [Configuration & Dead Surface (Pass 3)](#9-configuration--dead-surface-pass-3)
10. [Pass 4 — Bugs, Dead Code & Style](#10-pass-4--bugs-dead-code--style)

---

## 1. Dead Code & Configuration Flags

### Issue 1.1 — `FFmpegOpenOptions.EnableExternalClockCorrection` is never read ✅ Fixed

**Resolution:** Property removed from `FFmpegOpenOptions`. No call-site migration required.

---

### Issue 1.2 — `FFmpegAudioSourceOptions.OutputChannelCountOverride` is never applied ✅ Fixed

**Resolution:** `TryGetEffectiveChannelMap` now reads `OutputChannelCountOverride` and applies it to cap the output channel count across all mapping policies.

---

## 2. Error Handling & Factory Pattern

### Issue 2.1 — `FFmpegMediaItem.Open()` throws `DecodingException` ✅ Fixed

**Resolution:**
- `FFmpegMediaItem.Create(string uri, out FFmpegMediaItem? item)` — primary factory, returns int, never throws.
- `FFmpegMediaItem.Create(FFmpegOpenOptions options, out FFmpegMediaItem? item)` — full-options variant.
- `FFmpegMediaItem.Open(string)` and `FFmpegMediaItem.TryOpen(string, out)` marked `[Obsolete]` pointing to `Create`.

---

## 3. Source Behaviour Without a Session

### Issue 3.1 — `FFmpegVideoSource` stub frame when no session ✅ Fixed

**Resolution:** `FFmpegConfigValidator` now rejects `UseSharedDecodeContext = false` (§N10). `FFmpegMediaItem` can no longer construct sources with a null session via the public API. The standalone `new FFmpegVideoSource()` path remains intentional (returns 2×2 placeholder for test/stub use).

---

### Issue 3.2 — `FFmpegAudioSource` returns Success+0 when no session ✅ Fixed

**Resolution:** Same as §3.1 — `UseSharedDecodeContext = false` is now rejected by the validator.

---

## 4. Seeking

### Issue 4.1 — `SeekToFrame` 30 fps hard fallback ✅ Fixed

**Before:** Hard fallback to 30 fps when frame rate was unknown, silently producing wrong seek positions.

**Now:** Returns `MediaErrorCode.MediaSourceNonSeekable` when frame rate is genuinely unknown (neither `StreamInfo.FrameRate` nor `_observedNativeFrameRate` from prior reads is available):

```csharp
var fps = StreamInfo.FrameRate ?? _observedNativeFrameRate;
if (fps is not > 0 || !double.IsFinite(fps.Value))
    return (int)MediaErrorCode.MediaSourceNonSeekable;
var targetSeconds = frameIndex / fps.Value;
```

---

### Issue 4.2 — Seeking via `FFmpegAudioSource` and `FFmpegVideoSource` independently is fragile ✅ Fixed

**Resolution:** `FFmpegMediaItem.Seek(double positionSeconds)` is the canonical seek point for shared-session items.

---

## 5. Audio Channel Mapping

### Issue 5.1 — Channel mapping options partially implemented ✅ Fixed

All four policies (`PreserveSourceLayout`, `ApplyExplicitRouteMap`, `DownmixToStereo`, `DownmixToMono`) are implemented. `OutputChannelCountOverride` is respected.

---

## 6. API Surface (`FFmpegMediaItem`)

### Issue 6.1 — `AudioSource` / `VideoSource` properties create nullability traps ✅ Fixed

**Resolution:** Both properties carry XML doc warnings.

---

### Issue 6.2 — `FFmpegMediaItem.Open()` is inconsistently discoverable ✅ Fixed

**Resolution:** `Open()` and `TryOpen()` marked `[Obsolete]`. `Create()` is the primary path.

---

## 7. Naming & Consolidation

### Issue 7.1 — `FFMediaItem`, `FFAudioSource`, `FFVideoSource` — standardise on `FFmpeg` prefix ✅ Fixed

**Before:** The public API had a split personality — configuration types (`FFmpegOpenOptions`, `FFmpegDecodeOptions`, `FFmpegConfigValidator`, `FFmpegRuntime`) used the full `FFmpeg` prefix while the primary consumer-facing types used the shorter `FF` prefix:

| Old name | New name |
|---|---|
| `FFMediaItem` | `FFmpegMediaItem` |
| `FFAudioSource` | `FFmpegAudioSource` |
| `FFVideoSource` | `FFmpegVideoSource` |
| `FFAudioSourceOptions` | `FFmpegAudioSourceOptions` |
| `FFAudioChannelMap` | `FFmpegAudioChannelMap` |
| `FFAudioChannelMappingPolicy` | `FFmpegAudioChannelMappingPolicy` |

**Considerations:**
- **Breaking change** — all six types are `sealed` so no compat-shim subclasses are possible. C# does not support `[Obsolete]` on `using` type aliases. A clean rename with no forwarding stubs was the only viable path.
- **Internal types untouched** — `FFPacketReader`, `FFAudioDecoder`, `FFVideoDecoder`, `FFSharedDemuxSession`, `FFSharedDecodeContext`, `FFStreamDescriptor` and all other `internal` types keep their short `FF*` names; the naming rule applies only to the public API surface.
- **Source files renamed** — `FFAudioSource.cs` → `FFmpegAudioSource.cs`, `FFMediaItem.cs` → `FFmpegMediaItem.cs`, etc. for all six types and their matching test files.

**Resolution:** All 28 affected files across `S.Media.FFmpeg`, `S.Media.FFmpeg.Tests`, `S.Media.Core.Tests`, and all `Test/` programs were updated in a single pass. All projects build with 0 errors.

---

### Issue 7.2 — `FFSharedDecodeContext` should be `internal` ✅ Fixed

`FFSharedDecodeContext` is now `internal sealed class`. Accessible to the test assembly via `[assembly: InternalsVisibleTo("S.Media.FFmpeg.Tests")]`.

---

### Issue 7.3 — `FFStreamDescriptor` should be `internal` ✅ Fixed

`FFStreamDescriptor` is now `internal readonly record struct`. Same `InternalsVisibleTo` coverage applies.

---

## 8. Decode Pipeline Issues (Pass 3)

### Issue N1 — Network URI support ✅ Fixed

`ResolveInputPath` (formerly `ResolveLocalPath`) no longer gates on `File.Exists` for non-local URIs. `rtsp://`, `http://`, and `https://` URIs are now passed directly to `avformat_open_input`. Local `file://` paths still have a `File.Exists` pre-check for a cleaner error message.

---

### Issue N2 — `FFPacketReader.ReadNextPacket()` dead code ✅ Fixed

Method removed.

---

### Issue N3 — No-arg decoder/pipeline overloads dead code ✅ Fixed

Removed `Decode()` from `FFAudioDecoder` and `FFVideoDecoder`, `Resample()` from `FFResampler`, and `Convert()` from `FFPixelConverter`.

---

### Issue N4 — `FFResampler` never calls `swr_convert` ✅ Fixed (documented limitation)

`FFNativeResamplerBackend` has been removed. The resampler is now a documented direct pass-through. Format conversion (S16/S32/DBL → FLT) is already performed upstream in `FFNativeAudioDecoderBackend.ExtractSamples`. A comment in `FFResampler` notes that sample-rate conversion is not yet implemented and a `SwrContext`-based path should be added when needed.

---

### Issue N5 — Per-packet `av_packet_alloc` in hot path ✅ Fixed

`FFNativeAudioDecoderBackend` and `FFNativeVideoDecoderBackend` now pre-allocate one `AVPacket*` per instance in `TryEnsureInitialized`. `TryDecode` reuses it via `av_packet_unref` + `av_grow_packet` instead of allocating on every call.

---

### Issue N6 — Seek does not flush decoder codec buffers ✅ Fixed

`FlushCodecBuffers()` added to `FFAudioDecoder`, `FFVideoDecoder`, `FFNativeAudioDecoderBackend`, and `FFNativeVideoDecoderBackend`. `FFSharedDemuxSession.Seek` calls both flush methods (inside `_pipelineGate`) after the packet reader seek succeeds.

---

### Issue N7 — `WorkerLoop` busy-polls at ~200 Hz ✅ Fixed

- `_workerSignal.WaitOne(5)` → `WaitOne(20)` (50 Hz idle rate).
- Default `MaxQueuedPackets` and `MaxQueuedFrames` raised from 1 → 4.

---

### Issue N8 — Video `EndOfStream` 98% heuristic ✅ Fixed

Replaced the `_positionSeconds >= DurationSeconds * 0.98` guard with a direct trust of the session return code:

```csharp
if (code != MediaResult.Success)
{
    lock (_gate)
    {
        if (State == VideoSourceState.Running)
            State = VideoSourceState.EndOfStream;
    }
    frame = null!;
    return code;
}
```

---

### Issue N9 — `FFPixelConverter` hardcodes RGBA32 target ✅ Fixed

`FFmpegDecodeOptions.PreferredOutputPixelFormat` (`VideoPixelFormat?`) added. Threaded through `FFSharedDemuxSession.Open` → `FFPixelConverter.Initialize(preferredOutputPixelFormat)`. `TryNativeConvert` uses `FFNativeFormatMapper.MapToNativePixelFormat` to select the sws_scale target. `TryExecuteScale` uses `av_image_get_linesize` for correct stride computation (replaces the hardcoded `_width * 4`). Only packed single-plane formats (Rgba32, Bgra32) are accepted as sws_scale targets; multi-plane formats continue to use the native pass-through path.

---

## 9. Configuration & Dead Surface (Pass 3)

### Issue N10 — `UseSharedDecodeContext = false` silently creates non-functional sources ✅ Fixed

`FFmpegConfigValidator.Validate` now returns `FFmpegInvalidConfig` when `UseSharedDecodeContext = false`. A code comment documents that this flag should be removed or given a real implementation.

---

### Issue N11 — `InputFormatHint` is dead configuration surface ✅ Fixed

`FFNativeFileDemux.TryOpen` now calls `av_find_input_format(openOptions.InputFormatHint)` and passes the resulting `AVInputFormat*` as the forced format argument to `avformat_open_input`. The validator restriction that previously blocked `InputFormatHint` for URI inputs has been removed.

---

## 10. Pass 4 — Bugs, Dead Code & Style

### Issue P4-1 — Debug oscillating `nativeSampleValue` in `FFPacketReader.ReadAudioPacket` ✅ Fixed

**Before:**
```csharp
var nativeSampleValue = (float)((_nextAudioPacketIndex % 16) / 16d);
```
A debug-era ramp wave was being injected into the `SampleValue` field of every audio packet (values cycling 0 → 0.9375 and repeating every 16 packets). This field is used as a fill value in `FFSharedDemuxSession.ReadAudioSamples` when decoded samples don't fully cover the destination buffer, so any partial read would produce audible artifacts.

**Fix:** Replaced with `SampleValue: 0f`, matching the existing `ReadVideoPacket` behaviour.

---

### Issue P4-2 — Double-seek in `FFmpegMediaItem.Seek` ✅ Fixed

**Root cause:** `FFmpegMediaItem.Seek()` correctly called `_sharedDemuxSession.Seek()` once. It then looped over `_playbackAudioSources` and `_playbackVideoSources` calling `src.Seek(positionSeconds)` — but `FFmpegAudioSource.Seek()` and `FFmpegVideoSource.Seek()` each also forwarded to `_sharedDemuxSession.Seek()`. Result: with one audio + one video source, the session was seeked three times total, flushing decoder buffers and clearing the freshly-populated queues on every extra call.

**Fix:** Added `internal void NotifySeek(double positionSeconds)` to both `FFmpegAudioSource` and `FFmpegVideoSource`. `NotifySeek` only updates `_positionSeconds` and resets `EndOfStream` → `Running` state — it does not touch the session. `FFmpegMediaItem.Seek()` now calls `NotifySeek()` instead of `Seek()` on each source after the single session seek. Direct per-source `Seek()` calls from user code continue to trigger a full session seek as expected.

---

### Issue P4-3 — `_pendingAudioChunk` not cleared on `Seek` ✅ Fixed

**Root cause:** `FFSharedDemuxSession.Seek()` cleared `_audioQueue` and `_videoQueue` but left `_pendingAudioChunk` intact. `_pendingAudioChunk` holds the leftover tail of a partially-consumed audio chunk and is consumed as the very first data on the next `ReadAudioSamples` call — meaning audio from before the seek would be delivered as the first samples after the seek, causing an audible time-skip artefact.

**Fix:** Added `_pendingAudioChunk = null;` inside the `_gate` lock in `Seek()`, alongside the existing queue clears.

---

### Issue P4-4 — `FFmpegAudioSource.Seek()` does not reset `EndOfStream` state ✅ Fixed

**Root cause:** `FFmpegVideoSource.Seek()` had explicit code to transition `State` from `EndOfStream` back to `Running` when a seek is performed (added during the N8 fix). `FFmpegAudioSource.Seek()` was missing the equivalent, so seeking an audio source that had reached end-of-stream left it permanently stuck in `EndOfStream`, silently producing no further samples.

**Fix:** Added the same `if (State == AudioSourceState.EndOfStream) State = AudioSourceState.Running;` guard to `FFmpegAudioSource.Seek()`. Also added it to the new `FFmpegAudioSource.NotifySeek()` for the `FFmpegMediaItem` coordinated-seek path.

---

### Issue P4-5 — Dead `_ = sessionFrame.Has*` reads in `FFmpegVideoSource.ReadFrame` ✅ Fixed

```csharp
// Before — dead reads, no effect on any state
_ = sessionFrame.HasNativeTimingMetadata;
_ = sessionFrame.HasNativePixelMetadata;
```
These two lines computed boolean properties on `FFSessionVideoFrame` and discarded the results. They had no effect on observable behaviour and appeared to be debug/tracing stubs that were never completed.

**Fix:** Both lines removed.

---

### Issue P4-6 — `EnableHardwareDecode`, `LowLatencyMode`, `UseDedicatedDecodeThread` unimplemented ✅ Documented

Three `public` properties of `FFmpegDecodeOptions` have no wiring in the decode pipeline:
- `EnableHardwareDecode` — hardware-accelerated contexts (VAAPI, DXVA2, VideoToolbox) not yet implemented
- `LowLatencyMode` — B-frame reorder buffer suppression not yet implemented
- `UseDedicatedDecodeThread` — demux/decode thread split not yet implemented

`DecodeThreadCount` is validated and normalised (clamped to CPU count) but the clamped value is not passed to `avcodec_open2`.

These are part of the public record API so cannot be removed without a breaking change. **Fix:** Added XML `<remarks>` to each property clearly stating they are reserved for future implementation and have no current effect. The fields remain in place for forward-compatible binary compatibility.

---

### Issue P4-7 — Dead `_nextAudioPresentationTime` / `_nextVideoPresentationTime` fields ✅ Fixed

Both fields were initialised in `FFPacketReader.Initialize()` and updated in `Seek()` but were never read by `ReadAudioPacket()`, `ReadVideoPacket()`, or any other method. They were presumably placeholders for a synthetic presentation-time generation path that was never completed.

**Fix:** Both fields and their assignments in `Initialize()` and `Seek()` removed.

---

### Issue P4-8 — `FFmpegAudioSource.Start()` uses bizarre `switch { _ => }` expression ✅ Fixed

**Before:**
```csharp
return _disposed
    ? (int)MediaErrorCode.MediaInvalidArgument
    : (State = AudioSourceState.Running) switch { _ => MediaResult.Success };
```
The `switch { _ => }` idiom was used purely to convert the assignment expression into a value-returning expression. This obscures intent and is flagged by most static analysers.

**Fix:** Replaced with a straightforward `if` guard + explicit `return`:
```csharp
if (_disposed) return (int)MediaErrorCode.MediaInvalidArgument;
State = AudioSourceState.Running;
return MediaResult.Success;
```
