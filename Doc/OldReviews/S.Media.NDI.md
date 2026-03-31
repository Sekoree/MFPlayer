# S.Media.NDI — Issues & Fix Guide

> **Scope:** `S.Media.NDI` — `NDIEngine`, `NDIVideoOutput`, `NDIVideoSource`, `NDIAudioSource`, `NDIMediaItem`, `NDICaptureCoordinator`, options types
> **Cross-references:** See `API-Review.md` §6 and `NDILib.md` for the underlying native wrapper issues.
> **Checklist:** See `S.Media.NDI-Checklist.md` for a consolidated status tracker across all passes.

---

## Review History

| Pass | Date | Reviewer | Notes |
|---|---|---|---|
| 1st | 2026-03-31 | — | Initial review — 8 issues identified (§1–§4) |
| 2nd | 2026-03-31 | — | Follow-up pass — 3 issues fixed; 5 still outstanding; 18 new issues added (§5) |
| 3rd | 2026-03-31 | — | All 28 numbered issues fixed; §5.A architectural refactor completed — `INDICaptureCoordinator` interface + `NDIFrameSyncCoordinator` |

---

## Table of Contents

1. [Audio Output Gap (`IAudioSink`)](#1-audio-output-gap-iaudiosin-k)
2. [`NDIVideoOutput` Implementation Issues](#2-ndivideoutput-implementation-issues)
3. [Source Construction & Coordinator Bug](#3-source-construction--coordinator-bug)
4. [Engine & Options Cleanup](#4-engine--options-cleanup)
5. [Second-Pass Findings](#5-second-pass-findings)
   - [5.1 Bugs & Correctness](#51-bugs--correctness)
   - [5.2 Dead / Unenforced Configuration](#52-dead--unenforced-configuration)
   - [5.3 API Gaps & Missing NDI Features](#53-api-gaps--missing-ndi-features)
   - [5.4 Performance & Minor](#54-performance--minor)
   - [5.5 Architectural Consideration — Replace NDICaptureCoordinator with NDIFrameSync](#55-architectural-consideration--replace-ndicapturecoordinator-with-ndiframesync)

---

## 1. Audio Output Gap (`IAudioSink`)

### Issue 1.1 — `NDIVideoOutput` cannot be used as an audio output by the mixer ✅ Fixed (2nd pass)

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

### Issue 1.2 — `PushAudio` signature mismatch with any interface ❌ Outstanding

The existing `public int PushAudio(in AudioFrame frame, TimeSpan presentationTime)` does not match `IAudioOutput.PushFrame(in AudioFrame, ReadOnlySpan<int>)`. The `TimeSpan` parameter is not used by `IAudioSink` consumers. The route map is not used by NDI (all channels sent verbatim).

**Fix:** Keep `PushAudio` as an internal implementation detail. The public-facing path is via `IAudioSink.PushFrame`. If the `TimeSpan` parameter is needed for precise NDI timestamping, handle it internally from the frame's own timestamp data.

---

## 2. `NDIVideoOutput` Implementation Issues

### Issue 2.1 — `_gate` held during `NDISender.SendVideo()` native call ✅ Fixed (2nd pass)

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

### Issue 2.2 — No-arg `Start()` overload is non-standard ✅ Fixed (2nd pass)

`public int Start()` calls `Start(new VideoOutputConfig())`. It is not on `IVideoOutput`. Since `NDIOutputOptions.ClockVideo`/`ClockAudio` are set at construction time, the config is inert. No other output exposes a no-arg `Start()`.

**Fix:** Remove `public int Start()`. Callers should use `Start(new VideoOutputConfig())` explicitly or the `IAudioSink.Start(AudioOutputConfig)` path.

---

### Issue 2.3 — `Start()` validates and then ignores `VideoOutputConfig` ❌ Outstanding

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

### Issue 2.4 — `_stagingBuffer` and `_audioStagingBuffer` grow but never shrink ❌ Outstanding

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

### Issue 3.1 — Public constructors create independent `NDICaptureCoordinator` instances ❌ Outstanding

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

### Issue 4.1 — `NDIIntegrationOptions.RequireAudioPathOnStart` duplicated in `NDIOutputOptions` ❌ Outstanding

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

### Issue 4.2 — `NDIEngine` coordinator tracking is opaque ❌ Outstanding

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

---

## 5. Second-Pass Findings

> Second pass performed 2026-03-31. Issues §5.x are all **new** — they were not present in the first review.

---

### 5.1 Bugs & Correctness

#### Issue 5.1 — `NDIQueueOverflowPolicy.DropNewest` and `RejectIncoming` are identical

**File:** `Input/NDIVideoSource.cs` → `EnqueueCapturedFrame()`

Both enum branches in `EnqueueCapturedFrame` do the same thing: return the rented array to the pool, increment `_framesDropped`, and return:

```csharp
if (_queueOverflowPolicy == NDIQueueOverflowPolicy.RejectIncoming)
{
    ArrayPool<byte>.Shared.Return(rgba);
    _framesDropped++;
    return;
}

if (_queueOverflowPolicy == NDIQueueOverflowPolicy.DropNewest)
{
    ArrayPool<byte>.Shared.Return(rgba);
    _framesDropped++;
    return;   // identical to RejectIncoming!
}
```

`DropNewest` should dequeue and discard the most-recently enqueued item from the jitter buffer to make room, then enqueue the incoming frame. As written the enum has three values but only two distinct behaviours.

**Fix:**

```csharp
case NDIQueueOverflowPolicy.RejectIncoming:
    ArrayPool<byte>.Shared.Return(rgba);
    _framesDropped++;
    return;

case NDIQueueOverflowPolicy.DropNewest:
{
    // Discard the frame most recently added to the queue (tail) and replace with incoming.
    var dropped = _videoJitterQueue.Dequeue(); // Queue<T> is FIFO; "newest" means the last enqueued.
    // For true tail-drop, consider switching to a Deque or LinkedList.
    if (dropped.IsPooled) ArrayPool<byte>.Shared.Return(dropped.Rgba);
    _framesDropped++;
    // fall through to enqueue the incoming frame
    break;
}
```

Note: `Queue<T>` is FIFO, so `Dequeue()` removes the *oldest* item. True `DropNewest` (tail-drop) requires a `LinkedList<T>` or `ArrayDeque`. Until that change, `DropNewest` and `DropOldest` are semantically equivalent on `Queue<T>`.

---

#### Issue 5.2 — `NDIVideoOutput.Start()` returns wrong error code when disposed

**File:** `Output/NDIVideoOutput.cs`

Inside `Start(VideoOutputConfig config)`, the disposed guard returns `NDIOutputPushVideoFailed` instead of `MediaObjectDisposed`:

```csharp
lock (_gate)
{
    if (_disposed)
        return (int)MediaErrorCode.NDIOutputPushVideoFailed;   // ← wrong
```

Every other method on `NDIVideoOutput` (e.g. `PushFrame`, `PushAudioInternal`) correctly returns `MediaObjectDisposed` when disposed.

**Fix:**

```csharp
if (_disposed)
    return (int)MediaErrorCode.MediaObjectDisposed;
```

---

#### Issue 5.3 — `IAudioSink.Start()` misleads when `EnableAudio = false`

**File:** `Output/NDIVideoOutput.cs` → `IAudioSink.Start(AudioOutputConfig)`

If the output was already started via `IVideoOutput.Start()` with `Options.EnableAudio = false`, `IAudioSink.Start()` returns `MediaResult.Success` immediately because `_running` is `true`. The mixer considers the sink started and routes audio to it. Every subsequent `IAudioSink.PushFrame()` silently returns `NDIOutputAudioStreamDisabled`. The mixer may log a failure but the user gets no clear early error.

**Fix:** Validate `EnableAudio` before accepting the `IAudioSink.Start()` call:

```csharp
int IAudioSink.Start(AudioOutputConfig config)
{
    lock (_gate)
    {
        if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
        if (!Options.EnableAudio) return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
        if (_running) return MediaResult.Success;
    }
    return Start(new VideoOutputConfig());
}
```

---

#### Issue 5.4 — `NDIEngine.CreateAudioSource`/`CreateVideoSource` return wrong error code on options validation failure

**File:** `Runtime/NDIEngine.cs`

Both factory methods return `NDIReceiverCreateFailed` when `optionsValidation` fails:

```csharp
var code = item.CreateAudioSource(effective, out source);
if (code != MediaResult.Success || source is null)
{
    return (int)MediaErrorCode.NDIReceiverCreateFailed;   // ← wrong; options failed, not the receiver
}
```

A validation error (`NDIInvalidSourceOptions`, `NDIInvalidQueueOverflowPolicyOverride`, etc.) is swallowed and replaced with an unrelated code.

**Fix:** Propagate the inner error code directly:

```csharp
var optionsValidation = normalized.Validate();
if (optionsValidation != MediaResult.Success)
    return optionsValidation;   // preserve the specific validation error

var code = item.CreateAudioSource(effective, out source);
if (code != MediaResult.Success || source is null)
    return (int)MediaErrorCode.NDIReceiverCreateFailed;
```

---

#### Issue 5.5 — `NDICaptureCoordinator.CaptureOnce()` is not serialized — concurrent native calls possible

**File:** `Input/NDICaptureCoordinator.cs`

`NDIVideoSource.ReadFrame()` and `NDIAudioSource.ReadSamples()` each guard against concurrent calls on *themselves* via `Interlocked.CompareExchange(ref _readInProgress, ...)`. But they share a single `NDICaptureCoordinator`. If both sources find empty queues simultaneously (the normal case with a mixed A/V stream) they will each call `CaptureOnce()` concurrently, resulting in two simultaneous calls to `_receiver.CaptureScoped(timeoutMs)` on the same native handle. `NDIlib_recv_capture_v3` is not documented as thread-safe.

**Fix:** Serialize `CaptureOnce` within the coordinator using its own lock or a dedicated `SemaphoreSlim(1, 1)` for the capture call:

```csharp
private readonly SemaphoreSlim _captureSemaphore = new(1, 1);

private unsafe void CaptureOnce(uint timeoutMs)
{
    if (!_captureSemaphore.Wait(0))
        return;   // another thread is already capturing; skip this cycle
    try
    {
        // ... existing body ...
    }
    catch { /* best-effort */ }
    finally
    {
        _captureSemaphore.Release();
    }
}
```

Using `Wait(0)` (non-blocking tryacquire) is preferable to a blocking wait so that neither the audio nor video reader stalls waiting for the other's capture.

---

#### Issue 5.6 — `NDICaptureCoordinator.CaptureOnce()` swallows all exceptions including fatal ones

**File:** `Input/NDICaptureCoordinator.cs`

The bare `catch {}` swallows `OutOfMemoryException`, `AccessViolationException`, `ThreadAbortException`, and `StackOverflowException`:

```csharp
catch
{
    // Capture is best-effort by contract in this phase.
}
```

**Fix:** Catch only expected exceptions or at least use `catch (Exception ex)` with a filter that rethrows critical failures:

```csharp
catch (Exception ex) when (ex is not OutOfMemoryException
                               and not AccessViolationException)
{
    // capture is best-effort — log and continue
}
```

---

#### Issue 5.7 — `NDIVideoSource` uses `DateTime.UtcNow` for fallback timeout

**File:** `Input/NDIVideoSource.cs` → `TryGetFallbackFrame()`

The fallback timeout check compares `nowUtc - _lastFrameCapturedUtc > FallbackTimeout` where both sides are `DateTime`. `DateTime.UtcNow` is not monotonic and is susceptible to NTP jumps or system clock adjustments, which could cause the fallback to expire immediately or never expire.

**Fix:** Store the last-frame timestamp as a `long` from `Stopwatch.GetTimestamp()` and compare elapsed ticks:

```csharp
private long _lastFrameCapturedTimestamp; // Stopwatch ticks

// In CacheFallbackFrame:
_lastFrameCapturedTimestamp = Stopwatch.GetTimestamp();

// In TryGetFallbackFrame:
var elapsed = Stopwatch.GetElapsedTime(_lastFrameCapturedTimestamp);
if (_videoFallbackMode == NDIVideoFallbackMode.PresentLastFrameUntilTimeout
    && elapsed > FallbackTimeout)
{
    // ... return false
}
```

Remove `_lastFrameCapturedUtc` and `CapturedAtUtc` from `BufferedVideoFrame`.

---

#### Issue 5.8 — Jitter-buffer priming returns `NDIVideoFallbackUnavailable` — indistinguishable from "no signal"

**File:** `Input/NDIVideoSource.cs`

While the jitter buffer is priming (`_videoJitterQueue.Count < _videoJitterBufferFrames`), frames are being received from the network and enqueued, but `TryDequeueBufferedFrame` returns `false`, leaving `capturedFrame = false`. The code then falls through to return `NDIVideoFallbackUnavailable` — the same code returned when no NDI signal is present. Callers cannot distinguish "receiving but buffering" from "no signal".

**Fix:** Add a dedicated error code (e.g. `NDIVideoBuffering`) or expose a `IsBuffering` property, and return it from the priming path:

```csharp
// In NDIVideoSource.ReadFrame(), after TryReadVideo succeeds but TryDequeueBufferedFrame returns false:
if (!_videoJitterPrimed)
    return (int)MediaErrorCode.NDIVideoBuffering;   // new code in MediaErrorCode
```

---

### 5.2 Dead / Unenforced Configuration

#### Issue 5.9 — `NDILimitsOptions` queue-depth properties are never enforced

**File:** `Config/NDILimitsOptions.cs`, `Input/NDICaptureCoordinator.cs`

`NDILimitsOptions` exposes:
- `MaxPendingAudioFrames` (default 8)
- `MaxPendingVideoFrames` (default 8)
- `MaxChildrenPerParent` (default 4)

None of these are read by `NDICaptureCoordinator`, which uses its own hardcoded constants:

```csharp
private const int MaxBufferedVideoFrames = 8;
private const int MaxBufferedAudioBlocks = 16;
```

`MaxChildrenPerParent` is not checked anywhere in `NDIEngine`. Users who configure these limits get no effect.

**Fix:** Pass the effective limits to `NDICaptureCoordinator` at construction time:

```csharp
internal sealed class NDICaptureCoordinator
{
    private readonly int _maxBufferedVideoFrames;
    private readonly int _maxBufferedAudioBlocks;

    public NDICaptureCoordinator(NDIReceiver receiver, NDILimitsOptions limits)
    {
        _receiver = receiver;
        _maxBufferedVideoFrames = limits.MaxPendingVideoFrames;
        _maxBufferedAudioBlocks = limits.MaxPendingAudioFrames;
    }
}
```

Enforce `MaxChildrenPerParent` in `NDIEngine.CreateAudioSource`/`CreateVideoSource` by counting existing sources per coordinator.

---

#### Issue 5.10 — `NDIOutputOptions.ValidateCapabilitiesOnStart` is dead config

**File:** `Config/NDIOutputOptions.cs`, `Output/NDIVideoOutput.cs`

The flag `ValidateCapabilitiesOnStart` (defaults to `true`) is never read anywhere in `NDIVideoOutput.Start()` or `NDIEngine.CreateOutput()`. It silently does nothing.

**Fix:** Either wire it to a meaningful validation step (e.g. checking that `NDISender.GetConnectionCount()` is available) or remove the property.

---

#### Issue 5.11 — `NDIEngineDiagnostics.ClockDriftMs` is a static formula, not a measurement

**File:** `Runtime/NDIEngine.cs` → `BuildDiagnosticsSnapshotLocked()`

```csharp
ClockDriftMs: _diagnosticsOptions.DiagnosticsTickInterval.TotalMilliseconds /
              _limitsOptions.MaxPendingVideoFrames,
```

This computes a constant from two config values — it never changes at runtime and does not measure any actual drift. The name strongly implies a live measurement.

**Fix:** Either populate it with real measured drift (e.g. difference between expected and actual `DiagnosticsLoop` tick interval using `Stopwatch`) or rename it to something accurate like `DiagnosticsTickBudgetMs` and document that it is a config-derived hint, not a measurement.

---

#### Issue 5.12 — `NDIExternalTimelineClock` is not integrated with any capture path

**File:** `Clock/NDIExternalTimelineClock.cs`

`NDIExternalTimelineClock` implements `IMediaClock` and exposes `OnAudioFrame(long timecode100ns, ...)` and `ResolveVideoPtsSeconds(long, long, double)`. However:

- `OnAudioFrame` has zero call-sites in `S.Media.NDI`.
- `ResolveVideoPtsSeconds` has zero call-sites.
- `NDICaptureCoordinator` and `NDIVideoSource` do their own timestamp handling independently.
- `NDIEngine` never creates or exposes a clock.

The clock is effectively dead code in the current library.

**Fix options:**
1. Wire `NDIExternalTimelineClock` to the capture pipeline: call `OnAudioFrame` from `NDICaptureCoordinator.CaptureOnce()` when an audio frame is received, and use `ResolveVideoPtsSeconds` in `NDIVideoSource` for PTS resolution.
2. If this clock is not intended to be used yet, move it out of the public API into an `internal` class or annotate it `[Obsolete]` until it is wired up.

---

### 5.3 API Gaps & Missing NDI Features

#### Issue 5.13 — `NDIVideoOutput.PushFrame()` does not check `Options.EnableVideo`

**File:** `Output/NDIVideoOutput.cs`

`NDIOutputOptions.EnableVideo` defaults to `true`, but it is never checked in `PushFrame`. An output created with `EnableVideo = false` and `EnableAudio = true` will still send any video frame pushed to it. There is no symmetry with the `EnableAudio` guard in `PushAudioInternal`:

```csharp
if (!Options.EnableAudio)
{
    Interlocked.Increment(ref _audioPushFailures);
    return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
}
```

**Fix:** Add the matching guard in `PushFrame`:

```csharp
NDISender? sender;
lock (_gate)
{
    if (_disposed || !_running || _sender is null)  { /* ... */ }
    if (!Options.EnableVideo)
    {
        Interlocked.Increment(ref _videoPushFailures);
        return (int)MediaErrorCode.NDIInvalidOutputOptions;
    }
    sender = _sender;
}
```

---

#### Issue 5.14 — Tally support absent

**File:** `Output/NDIVideoOutput.cs`

`NDISender.GetTally()` is available in `NDILib` and returns on-program / on-preview status from connected receivers. This is an important NDI feature for broadcast workflows (e.g. tally lights). `NDIVideoOutput` exposes no tally API.

**Fix:** Add a `GetTally()` method and optionally a `TallyChanged` event:

```csharp
public bool GetTally(out bool onProgram, out bool onPreview)
{
    NDISender? sender;
    lock (_gate) { sender = _sender; }
    if (sender is null) { onProgram = false; onPreview = false; return false; }
    var ok = sender.GetTally(out var tally);
    onProgram = tally.OnProgram != 0;
    onPreview = tally.OnPreview != 0;
    return ok;
}
```

---

#### Issue 5.15 — Connection count not exposed

**File:** `Output/NDIVideoOutput.cs`

`NDISender.GetConnectionCount()` is available but not plumbed through to `NDIVideoOutput`. Callers have no way to check whether anyone is receiving the stream, which is a common "is anything listening?" health check.

**Fix:**

```csharp
public int GetConnectionCount(uint timeoutMs = 0)
{
    NDISender? sender;
    lock (_gate) { sender = _sender; }
    return sender?.GetConnectionCount(timeoutMs) ?? 0;
}
```

---

#### Issue 5.16 — `NDIMediaItem` hardcodes stream metadata

**File:** `Media/NDIMediaItem.cs`

Both constructors hardcode stream info:

```csharp
AudioStreams = [new AudioStreamInfo { Codec = "NDI", SampleRate = 48_000, ChannelCount = 2 }];
VideoStreams = [new VideoStreamInfo { Codec = "NDI", Width = 1920, Height = 1080, FrameRate = 60 }];
```

A 4K/25fps source, a 720p/50fps source, and a 1080p/59.94fps source all present as `1920×1080@60`. Downstream components (e.g. the mixer's video pipeline) that use `IMediaItem.VideoStreams` for buffer sizing or format negotiation will size incorrectly.

`NDIVideoSource.StreamInfo` *does* update dynamically once frames start arriving, but `NDIMediaItem.VideoStreams` never reflects actual source parameters.

**Fix options:**
1. Set initial `VideoStreams`/`AudioStreams` to `null`/empty and populate them from the first captured frame via `NDIVideoSource.StreamInfo`.
2. Accept initial width/height/fps/sampleRate/channels as constructor parameters so callers who know the source format can provide them.

---

#### Issue 5.17 — `Timecode` and `Timestamp` set to the same value in all send paths

**File:** `Output/NDIVideoOutput.cs` → `PushFrameCore()`, `PushAudioCore()`

```csharp
var ndiFrame = new NdiVideoFrameV2
{
    Timecode  = timecode,   // ← both set to presentationTime.Ticks
    Timestamp = timecode,
    // ...
};
```

Per the NDI SDK spec:
- `Timecode` is a SMPTE-style embedded timecode. Pass `NdiConstants.TimecodeSynthesize` (= `long.MaxValue`) to have the SDK generate it automatically.
- `Timestamp` is the UTC capture time in 100 ns units (wall-clock, not PTS).

Setting both to `presentationTime.Ticks` (which is a relative PTS, not a wall-clock time) gives receivers incorrect SMPTE timecodes and incorrect capture timestamps.

**Fix:**

```csharp
var ndiFrame = new NdiVideoFrameV2
{
    Timecode  = NdiConstants.TimecodeSynthesize,   // let SDK generate SMPTE TC
    Timestamp = presentationTime.Ticks,            // relative PTS — acceptable for Timestamp
    // ...
};
```

If true SMPTE TC generation is needed, maintain a running TC counter from the first frame.

---

### 5.4 Performance & Minor

#### Issue 5.18 — `NDIMediaItem.PlaybackAudioSources` and `PlaybackVideoSources` allocate on every access

**File:** `Media/NDIMediaItem.cs`

```csharp
public IReadOnlyList<IAudioSource> PlaybackAudioSources => _playbackAudioSources.ToArray();
public IReadOnlyList<IVideoSource> PlaybackVideoSources => _playbackVideoSources.ToArray();
```

`.ToArray()` allocates a new array on every property read, even if nothing has changed.

**Fix:**

```csharp
public IReadOnlyList<IAudioSource> PlaybackAudioSources => _playbackAudioSources.AsReadOnly();
public IReadOnlyList<IVideoSource> PlaybackVideoSources => _playbackVideoSources.AsReadOnly();
```

`List<T>.AsReadOnly()` returns a thin wrapper with no allocation per call.

---

#### Issue 5.19 — `_lastPushMs` written outside lock with plain assignment

**File:** `Output/NDIVideoOutput.cs`

Both the video and audio push paths write:

```csharp
_lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
```

outside `_gate` as a plain `double` assignment. On 32-bit targets `double` writes are not atomic. The value is only read in `Diagnostics` (under `_gate`), which creates a torn-read risk.

**Fix:** Either guard the write inside `_gate`, or if the diagnostic is performance-sensitive, use `Interlocked` via a `long` reinterpreted as `double`:

```csharp
// Simple fix — write under the gate that already guards the counters:
lock (_gate)
{
    if (result == MediaResult.Success) _videoPushSuccesses++;
    else _videoPushFailures++;
    _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
```

---

### 5.5 Architectural — Replace `NDICaptureCoordinator` with `NDIFrameSync` ✅ Fixed (3rd pass)

`NDICaptureCoordinator` manually reimplements capabilities that already exist in `NDILib.NDIFrameSync`:

| Feature | `NDICaptureCoordinator` | `NDIFrameSync` |
|---|---|---|
| A/V demux | ✅ manual `if FrameType == Video` | ✅ separate `CaptureVideo` / `CaptureAudio` |
| Audio resampling | ❌ none — fixed sample rate | ✅ SDK handles it |
| Pull-mode (always returns immediately) | ❌ blocks for `timeoutMs` | ✅ returns silence / last frame |
| Thread safety | ❌ race (Issue 5.5) | ✅ SDK-managed |
| Jitter buffering | ✅ manual queue | ✅ SDK-managed |

**Implementation:**

1. Added `INDICaptureCoordinator` interface (`Input/INDICaptureCoordinator.cs`) — shared contract for all coordinator types:
   ```csharp
   internal interface INDICaptureCoordinator : IDisposable
   {
       bool TryReadVideo(uint timeoutMs, out CapturedVideoFrame frame);
       bool TryReadAudio(uint timeoutMs, out CapturedAudioBlock frame);
   }
   ```

2. Extracted pixel-format conversion to `NDIVideoPixelConverter` (`Input/NDIVideoPixelConverter.cs`) — shared by both implementations.

3. Added `NDIFrameSyncCoordinator` (`Input/NDIFrameSyncCoordinator.cs`) — wraps `NDILib.NDIFrameSync`:
   - `TryReadVideo`: calls `frameSync.CaptureVideo()`; returns `false` when `Xres == 0` (no signal).
   - `TryReadAudio`: probes current incoming format, checks `AudioQueueDepth()`, then pulls up to 2 048 samples at the native rate via `frameSync.CaptureAudio()`.
   - Thread-safe without any additional locks — the SDK manages its own internal receive thread.

4. `NDICaptureCoordinator` now implements `INDICaptureCoordinator` (retained for recording workflows and as runtime fallback).

5. `NDIMediaItem` and `NDIEngine.GetOrCreateCaptureCoordinatorLocked` prefer `NDIFrameSyncCoordinator`; fall back to `NDICaptureCoordinator` silently if framesync creation fails (e.g. older NDI runtime).

6. `NDIVideoSource` and `NDIAudioSource` field types changed from `NDICaptureCoordinator?` to `INDICaptureCoordinator?` — no logic changes required; both coordinators conform to the same `TryReadVideo`/`TryReadAudio` contract.


