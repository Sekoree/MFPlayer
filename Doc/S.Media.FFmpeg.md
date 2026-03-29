# S.Media.FFmpeg — Issues & Fix Guide

> **Scope:** `S.Media.FFmpeg` — `FFMediaItem`, `FFAudioSource`, `FFVideoSource`, `FFSharedDemuxSession`, options types
> **Cross-references:** See `API-Review.md` §4 for the full analysis.

---

## Table of Contents

1. [Dead Code & Configuration Flags](#1-dead-code--configuration-flags)
2. [Error Handling & Factory Pattern](#2-error-handling--factory-pattern)
3. [Source Behaviour Without a Session](#3-source-behaviour-without-a-session)
4. [Seeking](#4-seeking)
5. [Audio Channel Mapping](#5-audio-channel-mapping)
6. [API Surface (`FFMediaItem`)](#6-api-surface-ffmediaitem)
7. [Naming & Consolidation](#7-naming--consolidation)

---

## 1. Dead Code & Configuration Flags

### Issue 1.1 — `FFmpegOpenOptions.EnableExternalClockCorrection` is never read

This flag exists in `FFmpegOpenOptions` but no code in `FFSharedDemuxSession` or anywhere else reads or branches on it. It is a remnant of the removed drift-correction system.

**Fix:** Remove the property entirely:

```csharp
public sealed class FFmpegOpenOptions
{
    // DELETE:
    // public bool EnableExternalClockCorrection { get; init; }
}
```

**Migration:** Any call sites that set this property should simply remove the assignment. It had no effect.

---

### Issue 1.2 — `FFAudioSourceOptions.OutputChannelCountOverride` is never applied

Declared in `FFAudioSourceOptions`, but `FFAudioSource.TryGetEffectiveChannelMap()` only reads `MappingPolicy` and `ExplicitChannelMap`. `OutputChannelCountOverride` is silently ignored.

**Fix (implement):**

```csharp
private bool TryGetEffectiveChannelMap(out int[]? channelMap, out int outputChannelCount)
{
    outputChannelCount = _options.OutputChannelCountOverride > 0
        ? _options.OutputChannelCountOverride
        : _streamInfo.ChannelCount;

    // ...rest of mapping logic uses outputChannelCount...
}
```

**Fix (remove):** If this feature is not planned, remove `OutputChannelCountOverride` from the options type to avoid confusion.

---

## 2. Error Handling & Factory Pattern

### Issue 2.1 — `FFMediaItem.Open()` throws `DecodingException`

`FFMediaItem.Open(uri)` is the only framework factory that throws. Every other factory uses integer return codes. This forces callers to use try/catch in addition to checking return codes elsewhere.

**Fix:** Make the constructor `internal` and expose a static factory:

```csharp
public sealed class FFMediaItem : IMediaItem, IMediaPlaybackSourceBinding, IDisposable
{
    // Make constructors internal:
    internal FFMediaItem(...) { ... }

    // Static factories — return int, not throw:
    public static int Create(string uri, out FFMediaItem? item)
        => Create(new FFmpegOpenOptions { Uri = uri }, out item);

    public static int Create(FFmpegOpenOptions options, out FFMediaItem? item)
    {
        item = null;
        try
        {
            var session = FFSharedDemuxSession.Open(options);
            item = new FFMediaItem(session, options);
            return MediaResult.Success;
        }
        catch (DecodingException ex)
        {
            return ex.ErrorCode;
        }
    }
}
```

`FFMediaItem.TryOpen` (the existing non-throwing alternative) can be deprecated in favour of `Create`.

**Consideration:** Callers using `using var media = FFMediaItem.Open(uri)` in a try/catch must be updated to the new pattern. Update `AVMixerTest` and other test programs accordingly.

---

## 3. Source Behaviour Without a Session

### Issue 3.1 — `FFVideoSource` returns a stub frame when `_sharedDemuxSession` is null

`ReadFrame()` returns a hardcoded 2×2 RGBA placeholder when no session is set. This masks misconfiguration.

### Issue 3.2 — `FFAudioSource` returns `Success` + 0 frames when session is absent

`ReadSamples()` returns `Success` with `framesRead = 0` and never advances `_positionSeconds`. The mixer treats this as "no data yet" rather than "misconfigured", advancing its own timeline while the audio source stays frozen. This creates A/V desync.

**Fix for both:** Remove the stub paths. Return a dedicated error code instead:

```csharp
// Add to MediaErrorCode (or FFmpegErrorCode):
FFmpegSessionNotAttached = ...,

// In FFVideoSource.ReadFrame():
if (_sharedDemuxSession is null)
    return (int)FFmpegErrorCode.FFmpegSessionNotAttached;

// In FFAudioSource.ReadSamples():
if (_sharedDemuxSession is null)
{
    framesRead = 0;
    return (int)FFmpegErrorCode.FFmpegSessionNotAttached;
}
```

**Consideration:** The existing `FFAudioSource(double durationSeconds)` constructor explicitly documents that it produces silence. This is the correct standalone test path — keep it. The zero-session stub in the main constructor is the bug.

---

## 4. Seeking

### Issue 4.1 — `FFVideoSource.SeekToFrame` uses frame-rate heuristics

```csharp
var targetSeconds = frameIndex / (fps > 0 ? fps : 30.0);
```

For VFR content or when frame rate is unknown (defaults to 30 fps), this produces an incorrect seek position. `_currentFrameIndex` is set to `frameIndex` but the actual decode position may differ.

**Fix (short term):** Return an error when frame rate is unknown:

```csharp
public int SeekToFrame(long frameIndex)
{
    if (StreamInfo.FrameRate is not > 0)
        return (int)MediaErrorCode.MediaSourceNonSeekable;

    var targetSeconds = frameIndex / StreamInfo.FrameRate;
    return Seek(targetSeconds);
}
```

**Fix (long term):** Expose a native frame-accurate seek through `FFSharedDemuxSession` using `AVSEEK_FLAG_FRAME` (if supported by the container).

---

### Issue 4.2 — Seeking via `FFAudioSource` and `FFVideoSource` independently is fragile

Both sources call `_sharedDemuxSession.Seek()` independently. Seeking via one may flush shared decode buffers used by the other, depending on the session's internal lock semantics.

**Fix:** Provide a unified seek point on `FFMediaItem`:

```csharp
public sealed class FFMediaItem
{
    // Single canonical seek:
    public int Seek(double positionSeconds)
        => _sharedDemuxSession?.Seek(positionSeconds)
           ?? (int)FFmpegErrorCode.FFmpegSessionNotAttached;
}
```

Have `FFSharedDemuxSession.Seek()` coordinate the audio and video decoder streams internally (drain both queues, seek once, signal both sources). The individual `FFAudioSource.Seek` and `FFVideoSource.Seek` can then delegate to the item:

```csharp
// In FFAudioSource:
public int Seek(double positionSeconds) => _mediaItem.Seek(positionSeconds);
```

**Consideration:** `AudioVideoMixer.Seek()` currently calls `Seek(double)` on each source independently in a loop. After this fix, it should detect when two sources share the same `FFMediaItem` and only call `FFMediaItem.Seek()` once.

---

## 5. Audio Channel Mapping

### Issue 5.1 — Channel mapping options partially implemented

`FFAudioSourceOptions` has `MappingPolicy`, `ExplicitChannelMap`, and `OutputChannelCountOverride`. Only `MappingPolicy` and `ExplicitChannelMap` are applied in `TryGetEffectiveChannelMap`. `OutputChannelCountOverride` is ignored (see §1.2).

**Recommended complete implementation:**

```csharp
private bool TryGetEffectiveChannelMap(out ReadOnlySpan<int> channelMap, out int outputChannelCount)
{
    // 1. Determine output channel count
    outputChannelCount = _options.OutputChannelCountOverride > 0
        ? _options.OutputChannelCountOverride
        : _streamInfo.ChannelCount;

    // 2. Apply mapping policy
    switch (_options.MappingPolicy)
    {
        case FFAudioChannelMappingPolicy.Passthrough:
            channelMap = BuildIdentityMap(_streamInfo.ChannelCount);
            return true;

        case FFAudioChannelMappingPolicy.Explicit:
            if (_options.ExplicitChannelMap is not { Length: > 0 })
                goto default;
            channelMap = _options.ExplicitChannelMap;
            return true;

        default:
            channelMap = default;
            return false;
    }
}
```

---

## 6. API Surface (`FFMediaItem`)

### Issue 6.1 — `AudioSource` / `VideoSource` properties create nullability traps

`FFMediaItem` exposes:
- `FFAudioSource? AudioSource` — null when constructed from an `IReadOnlyList<IAudioSource>` that has no `FFAudioSource`.
- `IReadOnlyList<IAudioSource> PlaybackAudioSources` — can be non-empty when `AudioSource` is null.

A caller checking `media.AudioSource is null` may silently miss sources.

**Fix (minimal):** Add XML doc warnings:

```csharp
/// <summary>
/// The primary FFmpeg audio source, or <see langword="null"/> if this item was constructed
/// from an external source list. Use <see cref="PlaybackAudioSources"/> for the authoritative list.
/// </summary>
/// <remarks>
/// <b>Warning:</b> Do not use this as a null-check for "has audio". Check
/// <c>PlaybackAudioSources.Count > 0</c> instead.
/// </remarks>
public FFAudioSource? AudioSource { get; }
```

**Fix (API clean-up):** Restrict `AudioSource` and `VideoSource` to the URI-open path. The composite constructor should only expose `PlaybackAudioSources` / `PlaybackVideoSources`:

```csharp
// Composite constructor: hide concrete-typed shortcuts
public FFMediaItem(IReadOnlyList<IAudioSource> audioSources, IReadOnlyList<IVideoSource> videoSources)
{
    _audioSources = audioSources;
    _videoSources = videoSources;
    // Do NOT set AudioSource / VideoSource — leave them null
}
```

---

### Issue 6.2 — `FFMediaItem.Open()` is inconsistently discoverable

`Open(string uri)` is the prominent factory; `TryOpen` is the non-throwing alternative. With the factory pattern fix from §2.1 (`Create`), this hierarchy becomes:

```
FFMediaItem.Create(options, out item)  — primary: returns int, never throws
FFMediaItem.Open(uri)                  — [Obsolete] convenience: throws DecodingException
FFMediaItem.TryOpen(uri, out item)     — [Obsolete] non-throwing shortcut: returns bool
```

Mark `Open` and `TryOpen` as `[Obsolete]` pointing to `Create`.

---

## 7. Naming & Consolidation

> See `Naming-and-Consolidation.md` for the full cross-project analysis.

### 7.1 `FFMediaItem`, `FFAudioSource`, `FFVideoSource` — standardise on `FFmpeg` prefix

The `S.Media.FFmpeg` project uses two prefixes inconsistently: user-facing config types use `FFmpeg` (`FFmpegOpenOptions`, `FFmpegDecodeOptions`) while source/media types use the short `FF` (`FFMediaItem`, `FFAudioSource`, `FFVideoSource`). A caller new to the library can't predict which prefix to type.

**Proposed renames:**

| Current | Proposed |
|---|---|
| `FFMediaItem` | `FFmpegMediaItem` |
| `FFAudioSource` | `FFmpegAudioSource` |
| `FFVideoSource` | `FFmpegVideoSource` |
| `FFAudioChannelMap` | `FFmpegAudioChannelMap` |
| `FFAudioSourceOptions` | `FFmpegAudioSourceOptions` |

---

### 7.2 `FFSharedDecodeContext` → `FFmpegDecodeSession`

`FFSharedDecodeContext` holds the format context, manages `RefCount`, coordinates open/close, and stores `ResolvedDecodeOptions`. It is a session manager, not a lightweight context. The word "Context" understates this. Rename to `FFmpegDecodeSession` to match the `FFmpeg` prefix convention and better describe what it does. Also see §1.6 for making it `internal`.

---

### 7.3 `FFStreamDescriptor` → make `internal`

`FFStreamDescriptor` is public but duplicates `AudioStreamInfo` / `VideoStreamInfo` from `S.Media.Core`. It should be `internal` FFmpeg plumbing. Expose stream info to consumers via `AudioStreamInfo` / `VideoStreamInfo` only. See `Naming-and-Consolidation.md` §1.6.
