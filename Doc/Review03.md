# MFPlayer Framework — Review 03

> **Date:** 2026-04-01
> **Scope:** Deep-dive into all libraries — API alignment, implementation correctness, performance, and simplification opportunities.
> **Status:** Complete.

*This review focuses on findings **NOT** already covered in Review01 or Review02. Cross-references to earlier findings use `R1§` / `R2#` notation.*

---

## Table of Contents

1. [NDIEngine & MIDIEngine — Dispose Race Window](#1-ndiengine--midiengine--dispose-race-window)
2. [NDIEngine Factory Methods — Untracked Intermediate Objects](#2-ndiengine-factory-methods--untracked-intermediate-objects)
3. [NDI Source Diagnostics — _framesDropped Inflation on Stop-State Reads](#3-ndi-source-diagnostics--framesdropped-inflation-on-stop-state-reads)
4. [AVMixer — GetActiveVideoSource LINQ in Video Hot Path](#4-avmixer--getactivevideosource-linq-in-video-hot-path)
5. [AVMixer — AddAudioSource/AddVideoSource LINQ Duplicate Check](#5-avmixer--addaudiosourceaddvideosource-linq-duplicate-check)
6. [AVMixer — OutputWorker Thread.Sleep(1) Busy-Wait](#6-avmixer--outputworker-threadsleep1-busy-wait)
7. [OpenGLVideoOutput — BuildSurfaceMetadata Allocates List per Frame](#7-openglvideooutput--buildsurfacemetadata-allocates-list-per-frame)
8. [OpenGLVideoOutput — Thread.Sleep Resolution for Presentation Timing](#8-openglvideooutput--threadsleep-resolution-for-presentation-timing)
9. [OSCPacketCodec — BuildTypeTagString Called Twice During Encode](#9-oscpacketcodec--buildtypetagstring-called-twice-during-encode)
10. [OSCClientOptions.DecodeOptions — Dead Configuration](#10-oscclientoptionsdecodeoptions--dead-configuration)
11. [PMLib/Native.cs — No Trace Logging Unlike PALib](#11-pmlibnativecs--no-trace-logging-unlike-palib)
12. [MediaPlayer._activeMedia — Potentially Dead Field](#12-mediaplayer_activemedia--potentially-dead-field)
13. [FFmpegMediaItem.ComputeMetadataSignature — Allocating LINQ on Every Metadata Update](#13-ffmpegmediaitemcomputemetadatasignature--allocating-linq-on-every-metadata-update)
14. [SDL3VideoView — Monolithic File with Manual GL Delegate Loading](#14-sdl3videoview--monolithic-file-with-manual-gl-delegate-loading)
15. [Missing Device Monitoring Compared to OwnAudio Reference](#15-missing-device-monitoring-compared-to-ownaudio-reference)
16. [MediaPlayer.DetachCurrentMediaSources — Redundant Snapshot + One-by-One Removal](#16-mediaplayerdetachcurrentmediasources--redundant-snapshot--one-by-one-removal)
17. [AVMixer Snapshot Helpers Allocate New Lists Every Call](#17-avmixer-snapshot-helpers-allocate-new-lists-every-call)
18. [NDIEngine.CreateAudioSource/CreateVideoSource — Missing Null-Receiver Guard (Amplifier)](#18-ndiengineCreateaudiosourcecreatevideosource--missing-null-receiver-guard-amplifier)
19. [OSCServer.StartAsync — Linked CancellationToken Semantics](#19-oscserverstartasync--linked-cancellationtoken-semantics)

---

## 1. NDIEngine & MIDIEngine — Dispose Race Window

**Severity:** High  
**Files:** `Media/S.Media.NDI/Runtime/NDIEngine.cs:229–253`, `Media/S.Media.MIDI/Runtime/MIDIEngine.cs:168–184`

Both engines use a two-lock Dispose pattern where `_disposed` is set in a **second** lock acquisition, after calling `Terminate()` between locks:

```csharp
// NDIEngine.Dispose()
lock (_gate) { if (_disposed) return; /* signal diagnostics thread */ }
diagnosticsThread?.Join(...);
_ = Terminate();                          // <── gap here
lock (_gate) { _disposed = true; ... }    // <── only now is _disposed set
```

```csharp
// MIDIEngine.Dispose()
lock (_gate) { if (_disposed) return; }
_ = Terminate();                          // <── gap here
lock (_gate) { _disposed = true; }        // <── only now is _disposed set
```

**Problem:** Between the first lock release and `_disposed = true` in the second lock, another thread can enter factory methods like `Initialize()`, `CreateAudioSource()`, `CreateMediaItem()`, or `CreateOutput()` — all of which check `_disposed` under `_gate` and will see `false`. This creates a window where:

- A new source/output is created against an engine that is mid-teardown.
- `Terminate()` runs concurrently with factory methods, potentially tearing down native resources while a new source is being constructed.

**Recommendation:** Collapse into a single lock acquisition that sets `_disposed = true` **before** calling `Terminate()`:

```csharp
// NDIEngine — single-phase Dispose
public void Dispose()
{
    Thread? diagnosticsThread;
    lock (_gate)
    {
        if (_disposed) return;
        _disposed = true;                    // ← close the gate immediately
        diagnosticsThread = _diagnosticsThread;
        _diagnosticsRunning = false;
        _diagnosticsThread = null;
        _diagnosticsStopSignal.Set();
    }
    diagnosticsThread?.Join(TimeSpan.FromSeconds(1));
    _ = Terminate();
    lock (_gate) { DiagnosticsUpdated = null; }
    _diagnosticsStopSignal.Dispose();
}
```

The same pattern applies to `MIDIEngine.Dispose()`.

---

## 2. NDIEngine Factory Methods — Untracked Intermediate Objects

**Severity:** Medium  
**File:** `Media/S.Media.NDI/Runtime/NDIEngine.cs:131–183`

`CreateAudioSource` and `CreateVideoSource` both create an intermediate `NDIMediaItem` on lines 147 and 175:

```csharp
var item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
_ = item.CreateAudioSource(normalized, out source);
```

This `NDIMediaItem` is:
- Never stored in any tracked collection.
- Never disposed by the engine.
- Not returned to the caller.

If `NDIMediaItem` holds any internal state or native resources beyond the coordinator (which is shared), this is a resource leak. Even if `NDIMediaItem` is lightweight today, it establishes a pattern where future additions to `NDIMediaItem` (e.g., metadata cache, event subscriptions) would silently leak.

**Recommendation:** Either:
1. Track the intermediate `NDIMediaItem` and dispose it when the engine terminates/disposes.
2. Add a direct factory path on the coordinator/source that doesn't require an `NDIMediaItem` intermediary.
3. Verify and document that `NDIMediaItem` is disposable but has no resources to release in this usage.

---

## 3. NDI Source Diagnostics — _framesDropped Inflation on Stop-State Reads

**Severity:** Medium  
**Files:** `Media/S.Media.NDI/Input/NDIAudioSource.cs:~116–125`, `Media/S.Media.NDI/Input/NDIVideoSource.cs:~153–161`

> *Note: R1§6.3 identified this for `NDIVideoSource` specifically. This finding extends it to `NDIAudioSource` and provides a concrete recommendation.*

Both sources increment `_framesDropped` when the source is not in `Running` state:

```csharp
if (State != AudioSourceState.Running)
{
    lock (_gate) { _framesDropped++; }
    return (int)MediaErrorCode.MediaSourceNotRunning;
}
```

During the normal lifecycle (mixer polls after `StopPlayback` is called but before threads fully quiesce), `_framesDropped` accumulates reads that were simply rejected due to the source being stopped. This conflates "actual data loss during active capture" with "expected rejection of reads on a stopped source".

**Impact:** Diagnostics reports and monitoring dashboards will show inflated frame-drop counts, making it impossible to distinguish real data loss from normal lifecycle behavior.

**Recommendation:** Introduce a separate `_rejectedReads` counter for non-running state rejections. Reserve `_framesDropped` exclusively for frames lost during active capture (queue overflow, stale discard, jitter buffer reset, etc.):

```csharp
if (State != AudioSourceState.Running)
{
    Interlocked.Increment(ref _rejectedReads);   // new: separate counter
    return (int)MediaErrorCode.MediaSourceNotRunning;
}
```

Expose `_rejectedReads` through the diagnostics snapshot for visibility.

---

## 4. AVMixer — GetActiveVideoSource LINQ in Video Hot Path

**Severity:** Medium  
**File:** `Media/S.Media.Core/Mixing/AVMixer.cs:1142–1148`

```csharp
private IVideoSource? GetActiveVideoSource()
{
    lock (_gate)
    {
        var id = _activeVideoSourceId;
        return id is null ? null : _videoSources.FirstOrDefault(s => s.Id == id);
    }
}
```

This method is called on **every iteration** of both `VideoDecodeLoop` and `VideoPresentLoop`. `FirstOrDefault` with a lambda closure allocates a delegate object on every call. For 60 fps video, this generates ~120 allocations/second.

> *Distinct from R1§3.3 and R2#6 which cover audio-pump LINQ (source pruning and EOS checks).*

**Recommendation:** Replace with a manual loop:

```csharp
private IVideoSource? GetActiveVideoSource()
{
    lock (_gate)
    {
        var id = _activeVideoSourceId;
        if (id is null) return null;
        foreach (var s in _videoSources)
            if (s.Id == id) return s;
        return null;
    }
}
```

Alternatively, cache the resolved `IVideoSource` reference alongside `_activeVideoSourceId` and invalidate when `SetActiveVideoSource` or source-list mutations occur (dirty-flag pattern already used for audio sources).

---

## 5. AVMixer — AddAudioSource/AddVideoSource LINQ Duplicate Check

**Severity:** Low  
**File:** `Media/S.Media.Core/Mixing/AVMixer.cs:367`

```csharp
if (_audioSources.Any(x => x.Source.Id == source.Id))
    return (int)MediaErrorCode.MixerSourceIdCollision;
```

`Any()` with a closure allocates on every `AddAudioSource` call. While this is a management-path method (not hot-path), it's inconsistent with the manual loops used in `RemoveAudioSource` (line 396) and `SetAudioSourceStartOffset` (line 380) for the same list.

**Recommendation:** Use a manual loop for consistency with sibling methods:

```csharp
foreach (var (existing, _) in _audioSources)
    if (existing.Id == source.Id)
        return (int)MediaErrorCode.MixerSourceIdCollision;
```

---

## 6. AVMixer — OutputWorker Thread.Sleep(1) Busy-Wait

**Severity:** High  
**File:** `Media/S.Media.Core/Mixing/AVMixer.cs:71`

```csharp
if (!has) { Thread.Sleep(1); continue; }
```

When the `OutputWorker` queue is empty, the worker thread uses `Thread.Sleep(1)` as a polling interval. On Windows without `timeBeginPeriod(1)`, this sleeps ~15 ms, adding uncontrollable latency to frame delivery. On Linux, it is closer to 1 ms but still represents a polling pattern.

For a per-video-output worker thread (one per `IVideoOutput`), this means:
- N video outputs → N threads busy-waiting when no frames are available.
- On Windows, each frame can have up to ~15 ms additional latency before the worker dequeues it.

**Recommendation:** Replace with a signaling mechanism. A `ManualResetEventSlim` (or `AutoResetEvent`) set on `Enqueue` and waited-on in the worker loop eliminates busy-waiting and provides immediate wake-up:

```csharp
private readonly ManualResetEventSlim _signal = new(false);

internal void Enqueue(VideoFrame frame, TimeSpan pts)
{
    // ... existing enqueue logic ...
    _signal.Set();
}

private void WorkerLoop()
{
    while (!_stop)
    {
        (VideoFrame Frame, TimeSpan Pts) item;
        bool has;
        lock (_qLock) { has = _queue.TryDequeue(out item); }
        if (!has) { _signal.Wait(16); _signal.Reset(); continue; }
        // ... existing push logic ...
    }
}
```

---

## 7. OpenGLVideoOutput — BuildSurfaceMetadata Allocates List per Frame

**Severity:** Low  
**File:** `Media/S.Media.OpenGL/OpenGLVideoOutput.cs:431–461`

```csharp
private static OpenGLSurfaceMetadata BuildSurfaceMetadata(VideoFrame frame, long generation)
{
    var strides = new List<int>(4);
    if (frame.Plane0.Length > 0) strides.Add(frame.Plane0Stride);
    // ... Plane1–3 ...
    return new OpenGLSurfaceMetadata(..., PlaneStrides: strides, ...);
}
```

A new `List<int>` is allocated on every frame push. Since the maximum plane count is 4 (a compile-time constant), this can be replaced with a fixed-size `int[]` or an `ImmutableArray<int>`:

```csharp
Span<int> buf = stackalloc int[4];
int count = 0;
if (frame.Plane0.Length > 0) buf[count++] = frame.Plane0Stride;
// ...
var strides = buf[..count].ToArray(); // single small array allocation
```

If `OpenGLSurfaceMetadata.PlaneStrides` is changed from `List<int>` to `IReadOnlyList<int>` or `int[]`, the allocation can be further reduced.

---

## 8. OpenGLVideoOutput — Thread.Sleep Resolution for Presentation Timing

**Severity:** Medium  
**File:** `Media/S.Media.OpenGL/OpenGLVideoOutput.cs:162–164`

```csharp
if (delay > TimeSpan.Zero)
{
    Thread.Sleep(delay);
}
```

> *R1§7.1 identified this as blocking the mixer's present thread. This finding focuses on the **timer resolution** aspect, which is a separate problem.*

`Thread.Sleep` has a resolution of approximately 15.6 ms on Windows (the default timer interrupt period). For 60 fps video (16.67 ms per frame), a requested sleep of e.g. 5 ms can easily overshoot to 15+ ms, causing frame presentation jitter or missed vsync windows. At higher frame rates (120 fps = 8.33 ms/frame), the sleep is longer than the entire frame interval.

**Recommendation:** For sub-frame-interval timing:
1. Use a spin-then-sleep hybrid: spin for the last 2–3 ms, sleep for longer intervals.
2. Use `Thread.SpinWait` with a high-resolution clock check for the final portion.
3. On Windows, consider `timeBeginPeriod(1)` (via P/Invoke or documented prerequisite) for 1 ms sleep resolution.
4. Consider using `ManualResetEventSlim.Wait(delay)` which can have better resolution characteristics.

---

## 9. OSCPacketCodec — BuildTypeTagString Called Twice During Encode

**Severity:** Low  
**File:** `OSC/OSCLib/OSCPacketCodec.cs:395–410, 271, 400`

During encoding, `BuildTypeTagString` is called once in `EstimatePacketSize` (line 400) to compute the buffer size, and again in `EncodeMessage` (line 271) to write the actual bytes:

```csharp
// EstimatePacketSize (line 400):
var size = PaddedStringByteCount(message.Address) 
         + PaddedStringByteCount(BuildTypeTagString(message.Arguments));

// EncodeMessage (line 271):
var tags = BuildTypeTagString(message.Arguments);
```

`BuildTypeTagString` creates a `StringBuilder`, iterates all arguments, and builds a type tag string. For messages with many arguments, this is wasted work.

**Recommendation:** Either:
1. Cache the type tag string on the `OSCMessage` (compute once, reuse).
2. Estimate size without building the string (type tag length = argument count + 1 for the `,` prefix, padded to 4-byte boundary).
3. Accept the double-build as acceptable for typical small OSC messages and add a comment.

Option (2) is the simplest and most efficient:

```csharp
private static int EstimateTypeTagSize(int argumentCount) =>
    PadTo4(argumentCount + 2); // ',' + tags + '\0'
```

---

## 10. OSCClientOptions.DecodeOptions — Dead Configuration

**Severity:** Low  
**File:** `OSC/OSCLib/OSCOptions.cs:129–132`

```csharp
/// <summary>
/// Reserved for symmetry with server-side decode settings.
/// </summary>
public OSCDecodeOptions DecodeOptions { get; init; } = new();
```

`OSCClient` never decodes received packets — it only encodes and sends. `DecodeOptions` exists "for symmetry" but is not read by any code path. Users who configure it will have no effect on behavior.

**Recommendation:** Either:
1. Remove it and add it back when client-side decode is actually needed.
2. Mark it `[Obsolete("Reserved for future use. Not currently consumed.")]`.

---

## 11. PMLib/Native.cs — No Trace Logging Unlike PALib

**Severity:** Low  
**Files:** `MIDI/PMLib/Native.cs`, `Audio/PALib/Native.cs`

`PALib/Native.cs` wraps every PortAudio P/Invoke call with trace-level logging:

```csharp
// PALib pattern:
internal static PaError Pa_Initialize()
{
    var result = Pa_Initialize_Import();
    Logger?.LogTrace("Pa_Initialize() => {Result}", result);
    return result;
}
```

`PMLib/Native.cs` uses raw `[LibraryImport]` with no logging layer:

```csharp
// PMLib pattern:
[LibraryImport(LibraryName)]
internal static partial PmError Pm_Initialize();
```

This inconsistency means:
- PortAudio native calls can be traced at runtime for debugging.
- PortMidi native calls are invisible — if a native call silently fails, there is no managed-side record.

**Recommendation:** Add logging wrappers to `PMLib/Native.cs` following the same pattern as `PALib/Native.cs`. Use `PMLibLogging.Logger` (which already exists in the library's runtime setup).

---

## 12. MediaPlayer._activeMedia — Potentially Dead Field

**Severity:** Low  
**File:** `Media/S.Media.Core/Playback/MediaPlayer.cs:14, 41, 88`

```csharp
private IMediaItem? _activeMedia;  // line 14

// Written in Play():
lock (Gate) { _activeMedia = media as IMediaItem; }  // line 41

// Cleared in DetachCurrentMediaSources():
_activeMedia = null;  // line 88
```

`_activeMedia` is written and cleared but **never read** anywhere in the current codebase. There is no property, method, or event that exposes it.

**Impact:** Dead code that creates the impression that the active media item is tracked for some purpose — misleading for future maintainers.

**Recommendation:** Either:
1. Expose it as a read-only property (`public IMediaItem? ActiveMedia { get { lock (Gate) return _activeMedia; } }`) if consumers need it.
2. Remove the field entirely if there is no planned use.

---

## 13. FFmpegMediaItem.ComputeMetadataSignature — Allocating LINQ on Every Metadata Update

**Severity:** Low  
**File:** `Media/S.Media.FFmpeg/Media/FFmpegMediaItem.cs:525–534`

```csharp
private static string? ComputeMetadataSignature(MediaMetadataSnapshot? metadata)
{
    if (metadata is null) return null;
    var ordered = metadata.AdditionalMetadata.OrderBy(kvp => kvp.Key, StringComparer.Ordinal);
    return string.Join("|", ordered.Select(kvp => $"{kvp.Key}={kvp.Value}"));
}
```

`OrderBy`, `Select`, `string.Join`, and interpolated string formatting all allocate on every metadata update. This runs each time `OnStreamDescriptorsRefreshed` fires (after demux header re-reads, seeks, format changes).

While metadata updates are infrequent, the function computes a signature solely to detect changes — a hash would be cheaper:

**Recommendation:** Use a simple hash-based comparison instead of string building:

```csharp
private static int ComputeMetadataHash(MediaMetadataSnapshot? metadata)
{
    if (metadata is null) return 0;
    var hash = new HashCode();
    foreach (var kvp in metadata.AdditionalMetadata.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        hash.Add(kvp.Key, StringComparer.Ordinal);
        hash.Add(kvp.Value, StringComparer.Ordinal);
    }
    return hash.ToHashCode();
}
```

This still allocates for `OrderBy` but avoids the string concatenation. For a fully allocation-free path, sort the dictionary keys into a reusable list.

---

## 14. SDL3VideoView — Monolithic File with Manual GL Delegate Loading

**Severity:** Low (architecture/maintainability)  
**File:** `Media/S.Media.OpenGL.SDL3/SDL3VideoView.cs` (~1669 lines)

This file contains:
- 50+ manually declared GL delegate types (`delegate* unmanaged<...>`)
- A standalone render thread with frame queue management
- Shader compilation and pipeline setup
- HUD text rendering
- Input event handling
- Full SDL3 window lifecycle management

All in a single 1669-line file.

**Recommendation:**
1. **Extract GL delegate loading** into a shared `GLLoader` or `GLFunctions` struct/class that can be reused by other OpenGL backends (e.g., Avalonia integration).
2. **Extract HUD rendering** into a separate `SDL3HudRenderer` class.
3. **Extract shader management** into a `ShaderPipeline` class.
4. Consider whether a source-generator-based approach (e.g., `[GlImport("glCreateShader")]`) could reduce the manual delegate boilerplate.

---

## 15. Missing Device Monitoring Compared to OwnAudio Reference

**Severity:** Medium (feature gap)  
**Files:** `Reference/OwnAudio/OwnAudioEngine/Ownaudio.Core/IAudioEngine.cs`, `Media/S.Media.PortAudio/Engine/PortAudioEngine.cs`

The OwnAudio reference architecture provides explicit device monitoring control:

```csharp
// OwnAudio IAudioEngine:
int PauseDeviceMonitoring();
int ResumeDeviceMonitoring();
event DeviceListChangedHandler? DeviceListChanged;
```

MFPlayer's `PortAudioEngine` has device-change detection via `AudioDeviceChanged` event and `RefreshNativeDevices()`, but lacks:
- **Pause/Resume device monitoring**: No way to temporarily suppress device-change callbacks during sensitive operations (e.g., mid-seek, during configuration changes).
- **Explicit monitoring lifecycle**: Device monitoring starts implicitly — there's no way to control when the engine polls for changes.

**Recommendation:** Add `PauseDeviceMonitoring()` / `ResumeDeviceMonitoring()` to `IAudioEngine` (or as an opt-in `IDeviceMonitor` interface) for parity with the reference architecture. This is especially valuable for live-performance scenarios where device-change notifications during playback can trigger unwanted device switches.

---

## 16. MediaPlayer.DetachCurrentMediaSources — Redundant Snapshot + One-by-One Removal

**Severity:** Low (simplification)  
**File:** `Media/S.Media.Core/Playback/MediaPlayer.cs:48–92`

```csharp
private int DetachCurrentMediaSources()
{
    // Step 1: Snapshot lists under lock
    lock (Gate) {
        audioToRemove = [.. _attachedAudioSources];
        videoToRemove = [.. _attachedVideoSources];
    }
    // Step 2: Remove one-by-one (each takes the lock again)
    foreach (var source in audioToRemove) RemoveAudioSource(source);
    foreach (var source in videoToRemove) RemoveVideoSource(source);
    // Step 3: Clear tracked lists (takes lock again)
    lock (Gate) {
        _attachedAudioSources.Clear();
        _attachedVideoSources.Clear();
        _activeMedia = null;
    }
}
```

This pattern:
1. Allocates two snapshot lists (Step 1).
2. Takes the lock N+M+1 additional times (once per `RemoveAudioSource`/`RemoveVideoSource` + final clear).
3. Clears `_attachedAudioSources` **after** removal — but removal already mutates the underlying mixer lists, so this clear is only cleaning the tracking list.

**Recommendation:** Since `MediaPlayer` owns the tracking lists, a single-lock bulk operation would be simpler:

```csharp
private int DetachCurrentMediaSources()
{
    List<IAudioSource> audioToRemove;
    List<IVideoSource> videoToRemove;
    lock (Gate) {
        audioToRemove = [.. _attachedAudioSources];
        videoToRemove = [.. _attachedVideoSources];
        _attachedAudioSources.Clear();
        _attachedVideoSources.Clear();
        _activeMedia = null;
    }
    // Bulk removal from mixer (still required for mixer's internal lists):
    var firstError = MediaResult.Success;
    foreach (var s in audioToRemove) { /* ... */ }
    foreach (var s in videoToRemove) { /* ... */ }
    return firstError;
}
```

---

## 17. AVMixer Snapshot Helpers Allocate New Lists Every Call

**Severity:** Low  
**File:** `Media/S.Media.Core/Mixing/AVMixer.cs:1151–1155`

```csharp
private List<(IAudioSource Source, double StartOffset)> GetAudioSourcesSnapshot()
{ lock (_gate) { return [.. _audioSources]; } }

private List<IVideoSource> GetVideoSourcesSnapshot()
{ lock (_gate) { return [.. _videoSources]; } }
```

These are called on dirty-flag cache refreshes inside the audio pump and video loops. Each call allocates a new `List<T>` via the collection expression spread. The dirty-flag pattern means this doesn't happen every frame, but during source-list changes it creates unnecessary GC pressure.

**Recommendation:** Return arrays instead of lists (collection expression `[.. source]` into a local array), or use a reusable buffer:

```csharp
private (IAudioSource Source, double StartOffset)[] GetAudioSourcesSnapshot()
{ lock (_gate) { return [.. _audioSources]; } }
```

Arrays are slightly cheaper than lists for read-only snapshots (no internal `_size` bookkeeping, smaller object header).

---

## 18. NDIEngine.CreateAudioSource/CreateVideoSource — Missing Null-Receiver Guard (Amplifier)

**Severity:** Medium  
**File:** `Media/S.Media.NDI/Runtime/NDIEngine.cs:131, 159`

> *R2#14 identified the missing guard. This finding amplifies with an additional observation.*

Beyond the missing `ArgumentNullException.ThrowIfNull(receiver)` (already noted in R2#14), both methods pass `receiver` into `GetOrCreateCaptureCoordinatorLocked(receiver)` before any null check:

```csharp
public int CreateAudioSource(NDIReceiver receiver, ...)
{
    source = null;
    lock (_gate)
    {
        if (_disposed || !IsInitialized) return ...;
        var normalized = sourceOptions.Normalize();
        var optionsValidation = normalized.Validate();
        if (optionsValidation != MediaResult.Success) return optionsValidation;
        var coordinator = GetOrCreateCaptureCoordinatorLocked(receiver); // ← null receiver used here
        var item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
        // ...
    }
}
```

If `receiver` is null, the failure will manifest inside `GetOrCreateCaptureCoordinatorLocked` as a `NullReferenceException` — inside the lock, potentially leaving `_coordinators` in an inconsistent state.

**Recommendation:** Add `ArgumentNullException.ThrowIfNull(receiver)` as the **first line** of both methods, **before** `lock (_gate)`, matching the pattern in `CreateMediaItem` (line 116).

---

## 19. OSCServer.StartAsync — Linked CancellationToken Semantics

**Severity:** Low  
**File:** `OSC/OSCLib/OSCServer.cs`

`StartAsync` creates a linked `CancellationTokenSource` from the caller's token. If the caller passes a scoped token (e.g., from an ASP.NET request), cancelling that token also stops the server. This is correct for "run until I say stop" patterns, but surprising for "start the server and let it run independently" patterns.

**Impact:** An application that starts the OSC server from a request handler and passes the request's `CancellationToken` will inadvertently stop the server when the request completes.

**Recommendation:** Document this clearly in the XML doc:

```xml
/// <param name="cancellationToken">
/// Cancelling this token stops the server. Pass <see cref="CancellationToken.None"/>
/// if the server should run independently until <see cref="StopAsync"/> is called.
/// </param>
```

---

## Prioritised Summary

### P1 — High (Fix Soon)

| # | Finding | File(s) |
|---|---------|---------|
| 1 | NDIEngine/MIDIEngine Dispose race window — `_disposed` set too late | `NDIEngine.cs`, `MIDIEngine.cs` |
| 6 | OutputWorker `Thread.Sleep(1)` busy-wait — latency + CPU waste | `AVMixer.cs` |

### P2 — Medium (Should Fix)

| # | Finding | File(s) |
|---|---------|---------|
| 2 | NDIEngine factory methods create untracked intermediate `NDIMediaItem` | `NDIEngine.cs` |
| 3 | NDI `_framesDropped` inflated by stop-state reads | `NDIAudioSource.cs`, `NDIVideoSource.cs` |
| 4 | `GetActiveVideoSource()` LINQ in video hot path | `AVMixer.cs` |
| 8 | `Thread.Sleep` resolution for presentation timing (~15 ms on Windows) | `OpenGLVideoOutput.cs` |
| 15 | Missing device monitoring (Pause/Resume) vs OwnAudio reference | `PortAudioEngine.cs` |
| 18 | Missing null-receiver guard can corrupt coordinator state under lock | `NDIEngine.cs` |

### P3 — Low (Nice to Have)

| # | Finding | File(s) |
|---|---------|---------|
| 5 | `AddAudioSource` uses LINQ `.Any()` (inconsistent with sibling methods) | `AVMixer.cs` |
| 7 | `BuildSurfaceMetadata` allocates `List<int>` per frame | `OpenGLVideoOutput.cs` |
| 9 | `BuildTypeTagString` called twice during OSC encode | `OSCPacketCodec.cs` |
| 10 | `OSCClientOptions.DecodeOptions` is dead configuration | `OSCOptions.cs` |
| 11 | PMLib lacks trace logging (inconsistent with PALib) | `PMLib/Native.cs` |
| 12 | `MediaPlayer._activeMedia` written but never read | `MediaPlayer.cs` |
| 13 | `ComputeMetadataSignature` uses allocating LINQ | `FFmpegMediaItem.cs` |
| 14 | SDL3VideoView is 1669 lines — extract GL loader, HUD, shaders | `SDL3VideoView.cs` |
| 16 | DetachCurrentMediaSources redundant snapshot pattern | `MediaPlayer.cs` |
| 17 | Snapshot helpers allocate new lists on every dirty-flag refresh | `AVMixer.cs` |
| 19 | OSCServer linked CancellationToken semantics undocumented | `OSCServer.cs` |

---

*Review03.md — Deep-dive pass complete 2026-04-01.*
