# S.Media.NDI — Issues & Fix Guide

> **Scope:** `S.Media.NDI` — `NDIEngine`, `NDIVideoOutput`, `NDIVideoSource`, `NDIAudioSource`, `NDIMediaItem`, `NDICaptureCoordinator`, options types
> **Cross-references:** See `API-Review.md` §6 and `NDILib.md` for the underlying native wrapper issues.

---

## Table of Contents

1. [Audio Output Gap (`IAudioSink`)](#1-audio-output-gap-iaudiosin k)
2. [`NDIVideoOutput` Implementation Issues](#2-ndivideoutput-implementation-issues)
3. [Source Construction & Coordinator Bug](#3-source-construction--coordinator-bug)
4. [Engine & Options Cleanup](#4-engine--options-cleanup)

---

## 1. Audio Output Gap (`IAudioSink`)

### Issue 1.1 — `NDIVideoOutput` cannot be used as an audio output by the mixer

`NDIVideoOutput` has a fully functional `PushAudio(in AudioFrame frame, TimeSpan pts)` method, audio staging buffer, push counters, and `NDIOutputOptions.EnableAudio`. But it only implements `IVideoOutput`. The `AudioVideoMixer` cannot route audio to it, and `PushAudio` is invisible to any interface-based consumer. `NDISendTest` works around this by hand-rolling a bespoke A/V loop:

```csharp
// NDISendTest — forced to bypass the mixer entirely:
while (running)
{
    var audio = audioSource.ReadSamples(...);
    ndiOutput.PushAudio(audio, pts);
    var video = videoSource.ReadFrame(...);
    ndiOutput.PushFrame(video, pts);
}
```

**Fix:** Introduce `IAudioSink` in `S.Media.Core` (see `S.Media.Core.md` §1.1) and implement it on `NDIVideoOutput`:

```csharp
public sealed class NDIVideoOutput : IVideoOutput, IAudioSink
{
    // ── IVideoOutput ─────────────────────────────────────────────────────────
    public int Start(VideoOutputConfig config) { ... }
    public int PushFrame(VideoFrame frame) => PushFrame(frame, TimeSpan.Zero);
    public int PushFrame(VideoFrame frame, TimeSpan presentationTime) { ... }

    // ── IAudioSink ───────────────────────────────────────────────────────────
    int IAudioSink.Start(AudioOutputConfig config)
    {
        // If already running (started via IVideoOutput.Start), accept this as success.
        // Otherwise start with defaults.
        return _running ? MediaResult.Success : StartInternal(default, config);
    }

    int IAudioSink.Stop() => Stop();

    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap)
        => PushAudioInternal(frame);   // NDI sends all channels verbatim; routeMap is ignored

    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap, int sourceChannelCount)
        => PushAudioInternal(frame);
}
```

Rename `PushAudio` to the private `PushAudioInternal` and remove the public `PushAudio(in AudioFrame, TimeSpan)` method (the `TimeSpan` parameter is not meaningful for the `IAudioSink` interface; NDI's audio clock is controlled separately via `ClockAudio`).

**Migration:** Once `IAudioVideoMixer.AddAudioOutput` accepts `IAudioSink`:

```csharp
// NDISendTest simplifies to:
mixer.AddVideoOutput(ndiOutput);
mixer.AddAudioOutput(ndiOutput);   // NDIVideoOutput as IAudioSink
mixer.StartPlayback(config);
```

**Consideration — Unified `Start`:**
NDI is inherently an A/V mux. Starting as a video output and as an audio sink are logically the same operation. The internal `Start()` should be idempotent:

```csharp
private int StartInternal(VideoOutputConfig? videoConfig, AudioOutputConfig? audioConfig)
{
    lock (_gate)
    {
        if (_running) return MediaResult.Success;  // already started — fine
        // ...create NDISender, set _running = true...
    }
}
```

---

### Issue 1.2 — `PushAudio` signature mismatch with any interface

The existing `public int PushAudio(in AudioFrame frame, TimeSpan presentationTime)` does not match `IAudioOutput.PushFrame(in AudioFrame, ReadOnlySpan<int>)`. The `TimeSpan` parameter is not used by `IAudioSink` consumers. The route map is not used by NDI (all channels sent verbatim).

**Fix:** Keep `PushAudio` as an internal implementation detail. The public-facing path is via `IAudioSink.PushFrame`. If the `TimeSpan` parameter is needed for precise NDI timestamping, handle it internally from the frame's own timestamp data.

---

## 2. `NDIVideoOutput` Implementation Issues

### Issue 2.1 — `_gate` held during `NDISender.SendVideo()` native call

With `NDIOutputOptions.ClockVideo = true`, `NDISender.SendVideo()` blocks for a full frame interval (~33 ms at 30 fps) while the NDI SDK's internal clock waits. During this time `_gate` is held, blocking `Stop()`, `Dispose()`, and all diagnostic reads.

**Fix:** Capture the sender reference under the lock, then release the lock before the native call:

```csharp
public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
{
    if (frame.ValidateForPush() is var validation and not MediaResult.Success)
        return validation;

    NDISender? sender;
    bool running;
    lock (_gate)
    {
        if (_disposed) return (int)MediaErrorCode.NDIOutputPushVideoFailed;
        sender = _sender;
        running = _running;
    }

    if (!running || sender is null)
        return (int)MediaErrorCode.NDIOutputPushVideoFailed;

    // Native call is now OUTSIDE the lock:
    var result = PushFrameCore(frame, presentationTime, sender);

    lock (_gate) { /* update counters */ }
    return result;
}
```

Apply the same pattern to the audio push path.

**Consideration:** After releasing `_gate`, `_sender` could theoretically be set to null by a concurrent `Stop()`. Guard against this:

```csharp
// PushFrameCore should accept the captured sender and not re-read _sender:
private int PushFrameCore(VideoFrame frame, TimeSpan pts, NDISender sender) { ... }
```

`sender` is a captured reference. Even if `Stop()` sets `_sender = null` concurrently, the captured reference remains valid until `Dispose()` calls `sender.Dispose()` — which only happens after `_sender = null` and `_running = false` are already set. Add a null check before any call on `sender` to be safe.

---

### Issue 2.2 — No-arg `Start()` overload is non-standard

`public int Start()` calls `Start(new VideoOutputConfig())`. It is not on `IVideoOutput`. Since `NDIOutputOptions.ClockVideo`/`ClockAudio` are set at construction time, the config is inert. No other output exposes a no-arg `Start()`.

**Fix:** Remove `public int Start()`. Callers should use `Start(new VideoOutputConfig())` explicitly or the `IAudioSink.Start(AudioOutputConfig)` path.

---

### Issue 2.3 — `Start()` validates and then ignores `VideoOutputConfig`

`NDIVideoOutput.Start(VideoOutputConfig config)` calls `config.Validate(...)` but ignores all of `BackpressureMode`, `QueueCapacity`, `PresentationMode`, etc. NDI frame pacing is governed by `NDIOutputOptions.ClockVideo`, not by the framework's `VideoOutputConfig`.

**Fix:** Explicitly discard the config and document why:

```csharp
public int Start(VideoOutputConfig config)
{
    // NDI frame pacing is governed by NDIOutputOptions.ClockVideo / ClockAudio set at
    // construction time. VideoOutputConfig backpressure settings are not applicable to
    // a network output and are intentionally ignored.
    _ = config;

    return StartInternal(null, null);
}
```

---

### Issue 2.4 — `_stagingBuffer` and `_audioStagingBuffer` grow but never shrink

`EnsureStagingBuffer` only reallocates when the required size exceeds the current length. A one-time 1080p push permanently retains the 1080p buffer even if all subsequent pushes are 480p.

**Fix:** Use `ArrayPool<byte>` for staging buffers:

```csharp
private byte[]? _stagingBuffer;
private int _stagingBufferSize;

private byte[] EnsureStagingBuffer(int requiredSize)
{
    if (_stagingBuffer is not null && _stagingBufferSize >= requiredSize)
        return _stagingBuffer;

    if (_stagingBuffer is not null)
        ArrayPool<byte>.Shared.Return(_stagingBuffer);

    _stagingBuffer = ArrayPool<byte>.Shared.Rent(requiredSize);
    _stagingBufferSize = requiredSize;
    return _stagingBuffer;
}
```

Return the buffer in `Dispose()`:

```csharp
public void Dispose()
{
    // ...existing disposal...
    if (_stagingBuffer is not null)
    {
        ArrayPool<byte>.Shared.Return(_stagingBuffer);
        _stagingBuffer = null;
    }
}
```

**Consideration:** `ArrayPool<byte>.Rent(n)` may return a buffer larger than `n`. Track the actual used size separately from the buffer length.

---

## 3. Source Construction & Coordinator Bug

### Issue 3.1 — Public constructors create independent `NDICaptureCoordinator` instances

`NDIVideoSource(NDIMediaItem, NDISourceOptions)` and `NDIAudioSource(NDIMediaItem, NDISourceOptions)` — the public constructors — each create a **new** `NDICaptureCoordinator` for the same `NDIReceiver`. This means:

1. Two separate `NDIReceiver.CaptureScoped()` calls are issued per frame interval, doubling NDI bandwidth consumption.
2. Audio and video frames are captured from independent capture calls — they will not be correlated. A video frame and an audio frame from the "same" NDI call will come from different capture slots.

The internal constructors (used by `NDIMediaItem.CreateAudioSource` / `CreateVideoSource`) correctly share a single coordinator.

**Fix:** Mark the public constructors `internal` so callers must use the factory path:

```csharp
// NDIVideoSource.cs:
internal NDIVideoSource(NDIMediaItem mediaItem, NDISourceOptions options)
    : this(mediaItem, options, mediaItem.CaptureCoordinator)
{
}
```

Alternatively, fix the public constructors to share the coordinator:

```csharp
// Requires making NDIMediaItem.CaptureCoordinator accessible:
public NDIVideoSource(NDIMediaItem mediaItem, NDISourceOptions options)
    : this(mediaItem, options, mediaItem.GetOrCreateSharedCoordinator())
{
}
```

**Correct usage pattern (via engine factory):**

```csharp
int r = engine.CreateMediaItem(receiver, sourceOptions, out var item);
item.CreateVideoSource(out var videoSrc);
item.CreateAudioSource(out var audioSrc);
// Both sources share the same NDICaptureCoordinator — correct A/V correlation.
```

---

## 4. Engine & Options Cleanup

### Issue 4.1 — `NDIIntegrationOptions.RequireAudioPathOnStart` duplicated in `NDIOutputOptions`

`RequireAudioPathOnStart` exists in both option types. `NDIEngine.CreateOutput` does not propagate the engine-level flag to the output options. The engine-level setting is therefore inert.

**Fix:** Remove from `NDIIntegrationOptions`:

```csharp
public sealed class NDIIntegrationOptions
{
    // DELETE:
    // public bool RequireAudioPathOnStart { get; init; }

    // KEEP in NDIOutputOptions only.
}
```

---

### Issue 4.2 — `NDIEngine` coordinator tracking is opaque

The `NDICaptureCoordinator` shared between audio and video sources from the same receiver is hidden inside a private dictionary. Callers who use the public source constructors (before fixing Issue 3.1) get silently broken behaviour.

**Fix:** Expose a `CreateMediaItem` factory on `NDIEngine`:

```csharp
public sealed class NDIEngine
{
    // ADD:
    public int CreateMediaItem(
        string sourceName,
        NDISourceOptions options,
        out NDIMediaItem? item)
    {
        item = null;
        var receiver = /* find or create receiver for sourceName */;
        if (receiver is null)
            return (int)MediaErrorCode.NDISourceNotFound;

        item = new NDIMediaItem(receiver, options);
        _mediaItems.Add(item);
        return MediaResult.Success;
    }
}
```

This makes the coordinator-sharing relationship explicit and removes the hidden dictionary pattern.

---

### Consideration — NDI A/V Sync

NDI's SDK has built-in A/V sync mechanisms:
- `ClockVideo = true` causes `SendVideo` to block until the next frame boundary, providing pacing.
- `ClockAudio = true` causes `SendAudio` to block similarly.

When both are `true`, the SDK handles A/V synchronisation internally. Do NOT attempt to manually sync by calling audio push and video push in a fixed ratio — the SDK handles this. Enabling both clocks while also applying `VideoPresenterSyncPolicy` in the mixer will result in double-pacing. If using `NDIVideoOutput` as a mixer output (post §1.1 fix), set `ClockVideo = false` and `ClockAudio = false` and let the mixer control the pace.

---

### Consideration — NDI Library Resolver

`S.Media.NDI` depends on `NDILib`, which hard-codes `"libndi.so.6"` — a Linux-only name. On Windows and macOS this will fail to load silently. See `NDILib.md` §1.1 for the cross-platform resolver fix. `S.Media.NDI` should trigger that resolver registration via `[ModuleInitializer]` on assembly load.

