# S.Media.Core — Issues & Fix Guide

> **Scope:** `S.Media.Core` — interfaces, mixer, clock, playback, error codes
> **Cross-references:** See `API-Review.md` §§2, 3, 10, 12 for the full analysis.
> **Last updated:** March 29, 2026 — Second-pass audit completed; §11 (status + new findings) added.

---

## Table of Contents

1. [Core Interfaces — Audio](#1-core-interfaces--audio)
2. [Core Interfaces — Video](#2-core-interfaces--video)
3. [Mixer (`AudioVideoMixer`)](#3-mixer-audiovideomixer)
4. [Playback (`MediaPlayer`)](#4-playback-mediaplayer)
5. [Error Code System](#5-error-code-system)
6. [Cross-Cutting Data Types](#6-cross-cutting-data-types)
7. [Naming & Consolidation](#7-naming--consolidation)
8. [Implementation Audit — New Findings (March 2026)](#8-implementation-audit--new-findings-march-2026)
9. [v1 Baseline — Keep / Simplify / Scrap](#9-v1-baseline--keep--simplify--scrap)
10. [OwnAudio Reference Analysis & Design Decisions](#10-ownaudio-reference-analysis--design-decisions)
11. [Second-Pass Audit — March 29, 2026](#11-second-pass-audit--march-29-2026)

---

## 1. Core Interfaces — Audio

### Issue 1.1 — `IAudioOutput` forces device-management on non-device sinks ✅ DONE

`IAudioOutput` requires every implementor to expose `SetOutputDevice`, `SetOutputDeviceByName`, `SetOutputDeviceByIndex`, `Device`, and `AudioDeviceChanged`. This is appropriate for `PortAudioOutput` but makes `NDIVideoOutput` (a network A/V mux) and any future file or network audio sink impossible to implement cleanly.

**Fix:** Split into two interfaces:

```csharp
// S.Media.Core/Audio/IAudioSink.cs
public interface IAudioSink : IDisposable
{
    Guid Id { get; }
    AudioOutputState State { get; }
    int Start(AudioOutputConfig config);
    int Stop();
    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex);
    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount);
}

// S.Media.Core/Audio/IAudioOutput.cs  (extend IAudioSink)
public interface IAudioOutput : IAudioSink
{
    AudioDeviceInfo Device { get; }
    int SetOutputDevice(AudioDeviceId deviceId);
    int SetOutputDeviceByName(string deviceName);
    int SetOutputDeviceByIndex(int deviceIndex);
    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}
```

**Migration:**
- Change `IAudioVideoMixer.AddAudioOutput(IAudioOutput)` → `AddAudioOutput(IAudioSink)`.
- `PortAudioOutput` continues to implement `IAudioOutput` (no change).
- `NDIVideoOutput` now implements `IAudioSink` (see `S.Media.NDI.md`).
- All existing call sites that pass `PortAudioOutput` still compile because `IAudioOutput : IAudioSink`.

**Considerations:**
- Any code that downcasts `IAudioSink` to `IAudioOutput` to call device-management APIs will need a null-check guard: `if (sink is IAudioOutput ao) ao.SetOutputDevice(...)`.
- `AudioOutputState` is already defined and can stay as-is.

---

### Issue 1.2 — `IAudioSource.SourceId` naming inconsistency ✅ DONE

Sources use `SourceId`; outputs use `Id`. The OwnAudio reference uses `Id` everywhere.

**Fix:** Rename in the interface:

```csharp
public interface IAudioSource
{
    Guid Id { get; }  // was SourceId
    // ...
}
public interface IVideoSource
{
    Guid Id { get; }  // was SourceId
    // ...
}
```

**Migration:** Global rename of all `SourceId` references on the interface and in the mixer, routing rules, and tests. Routing rule structs (`AudioRoutingRule.SourceId`, `VideoRoutingRule.SourceId`) must also be renamed.

---

### Issue 1.3 — `IAudioSource` is missing end-of-stream detection and sample count ✅ DONE

Callers have no way to detect end-of-file except by inspecting `ReadSamples` return codes.

**Fix:** Add `EndOfStream` to the `AudioSourceState` enum (see §10.11), and add total sample count:

```csharp
public enum AudioSourceState
{
    Stopped     = 0,
    Running     = 1,
    EndOfStream = 2,  // ADD: source produced all available data
}

public interface IAudioSource
{
    // ADD:
    long? TotalSampleCount { get; }   // null when unknown (live sources)
    // NOTE: IsEndOfStream() is now State == AudioSourceState.EndOfStream
    // ...existing...
}
```

**Considerations:**
- `FFAudioSource` should set `State = EndOfStream` when the demux session reports EOF.
- For live sources (`NDIAudioSource`, `PortAudioInput`), `State` never reaches `EndOfStream`.
- `TotalSampleCount` can return `null` for live or unknown-duration sources.
- The mixer can check `srcs.All(s => s.State == AudioSourceState.EndOfStream)` to auto-stop.

---

### Issue 1.4 — `IAudioOutput.PushFrame` route-map noise ✅ DONE

Every push requires a `ReadOnlySpan<int> sourceChannelByOutputIndex` even when channels are identity-mapped.

**Fix:** Add a convenience overload to `IAudioSink`:

```csharp
public interface IAudioSink
{
    // ADD convenience overload — identity route map:
    int PushFrame(in AudioFrame frame);
    // ...existing overloads kept...
}
```

Implement in the base/default implementation as:
```csharp
public int PushFrame(in AudioFrame frame)
{
    // build identity map: [0, 1, 2, ..., frame.ChannelCount - 1]
    Span<int> identity = stackalloc int[frame.ChannelCount];
    for (int i = 0; i < identity.Length; i++) identity[i] = i;
    return PushFrame(frame, identity, frame.ChannelCount);
}
```

---

## 2. Core Interfaces — Video

### Issue 2.1 — `IVideoSource.StreamInfo` is missing ✅ DONE

`IAudioSource` exposes `AudioStreamInfo StreamInfo { get; }`. `IVideoSource` has nothing equivalent. Codec, resolution, and frame rate are only accessible on concrete types.

**Fix:**

```csharp
public interface IVideoSource
{
    // ADD:
    VideoStreamInfo StreamInfo { get; }
    // ...existing...
}
```

All concrete implementations (`FFVideoSource`, `NDIVideoSource`) already have this data internally — it is just not surfaced through the interface.

---

### Issue 2.2 — `IVideoOutput` missing `State` property ✅ DONE

`IAudioOutput` / `IAudioSink` have `AudioOutputState State { get; }`. `IVideoOutput` has no equivalent. Callers must downcast to concrete types for state inspection.

**Fix:**

```csharp
public enum VideoOutputState { Stopped = 0, Running = 1 }

public interface IVideoOutput : IDisposable
{
    Guid Id { get; }
    VideoOutputState State { get; }   // ADD
    int Start(VideoOutputConfig config);
    int Stop();
    int PushFrame(VideoFrame frame);
    int PushFrame(VideoFrame frame, TimeSpan presentationTime);
}
```

---

### Issue 2.3 — `IVideoOutput.PushFrame` blocking contract is undocumented ❌ NOT DONE

Some implementations (`NDIVideoOutput` with `ClockVideo=true`) block for a full frame interval. Others (`SDL3VideoView`) are non-blocking. The mixer assumes synchronous completion.

**Fix:** Document the contract in the XML doc:

```csharp
/// <param name="frame">The frame to push.</param>
/// <remarks>
/// <b>Blocking behaviour varies by implementation:</b>
/// - <c>NDIVideoOutput</c> with <c>ClockVideo=true</c>: blocks until NDI's internal clock releases (~1 frame interval).
/// - <c>SDL3VideoView</c>: non-blocking; enqueues and returns immediately.
/// Callers such as the mixer must account for this when scheduling frame delivery.
/// Implementations should document their blocking semantics.
/// </remarks>
```

**Consideration:** A future `bool IsNonBlocking { get; }` property on `IVideoOutput` would allow the mixer to adjust scheduling automatically. For now, XML documentation is the minimum viable fix.

---

### Issue 2.4 — `IVideoSource` `SeekToFrame` out-parameter overload is awkward ✅ DONE

`SeekToFrame(long frameIndex, out long actualFrameIndex, out long? keyFrameIndex)` is clunky. Callers can query `CurrentFrameIndex` and `TotalFrameCount` directly after seeking.

**Fix:** Remove the two-`out` overload from the interface. Keep it `internal` on `FFVideoSource` if the implementation needs it.

---

## 3. Mixer (`AudioVideoMixer`)

### Issue 3.1 — Two-step start protocol ⚠️ PARTIAL — removed from interface, still `public` on concrete class

`Start()` and `StartPlayback(config)` both exist on `IAudioVideoMixer`. `Start()` only starts the clock; `StartPlayback` starts clock + pump threads. Callers inconsistently call both or just one.

**Fix:**
- Remove `Start()`, `Stop()`, `Pause()`, `Resume()` from `IAudioVideoMixer`.
- Keep them as `protected` on `AudioVideoMixer` for subclass use.
- The user-facing lifecycle on the interface should be: `StartPlayback` / `StopPlayback` / `PausePlayback` / `ResumePlayback`.

```csharp
public interface IAudioVideoMixer : ISupportsAdvancedRouting, IDisposable
{
    // REMOVE: Start(), Stop(), Pause(), Resume()
    // KEEP:
    int StartPlayback(AudioVideoMixerConfig config);
    int StopPlayback();
    int PausePlayback();
    int ResumePlayback();
    // ...
}
```

**Migration:** Any caller that calls `mixer.Start()` directly must switch to `mixer.StartPlayback(config)`.

---

### Issue 3.2 — `TickVideoPresentation()` is a dead no-op ⚠️ PARTIAL — marked `[Obsolete]`, not deleted

Always returns `TimeSpan.Zero`. Causes a 1 kHz busy-loop in `AVMixerTest`.

**Fix:** Delete from `IAudioVideoMixer` and `AudioVideoMixer`:

```csharp
// DELETE from interface:
// TimeSpan TickVideoPresentation();
```

Update `AVMixerTest` main loop:

```csharp
// BEFORE (busy-loops):
while (!cts.IsCancellationRequested)
{
    mixer.TickVideoPresentation();
    Thread.Sleep(1);
}

// AFTER:
cts.Token.WaitHandle.WaitOne();
// or: await Task.Delay(Timeout.Infinite, cts.Token);
```

---

### Issue 3.3 — `AudioRoutingRule.Gain` declared but never applied ✅ DONE

The gain field exists in the struct but the `AudioPumpLoop` never reads it. Silent no-op.

**Fix (short term):** Remove `Gain` from `AudioRoutingRule` or mark it obsolete:

```csharp
public struct AudioRoutingRule
{
    public Guid SourceId { get; init; }
    public Guid OutputId { get; init; }
    // EITHER remove Gain entirely, OR:
    [Obsolete("Gain is not yet applied in the mix loop. Set to 1.0f and do not rely on this field.")]
    public float Gain { get; init; }
}
```

**Fix (long term):** Apply gain during the mix phase in `AudioPumpLoop`:

```csharp
// In the mix accumulation loop, per source:
foreach (var rule in _audioRoutingRules.Where(r => r.SourceId == src.Id && r.OutputId == output.Id))
{
    // apply rule.Gain to the samples for this source→output pair
    for (int i = 0; i < samplesThisChannel; i++)
        mixBuffer[i] += samples[i] * rule.Gain;
}
```

---

### Issue 3.4 — `ISupportsAdvancedRouting` not on `IAudioVideoMixer` ✅ DONE

Callers must downcast to use `AddAudioRoutingRule` / `AddVideoRoutingRule`. Routing is a core mixer capability.

**Fix:**

```csharp
public interface IAudioVideoMixer : ISupportsAdvancedRouting, IDisposable
{
    // ...existing members unchanged...
}
```

No implementation changes needed — `AudioVideoMixer` already implements both.

---

### Issue 3.5 — Ghost drift-correction fields in `AudioVideoMixerDebugInfo` ✅ DONE

Eight fields are permanently zero: `DriftMs`, `CorrectionSignalMs`, `CorrectionStepMs`, `CorrectionOffsetMs`, `CorrectionResyncCount`, `LeadMinMs`, `LeadAvgMs`, `LeadMaxMs`.

**Fix:** Delete them from the record. The populated fields are:

```csharp
public readonly record struct AudioVideoMixerDebugInfo(
    long VideoPushed,
    long VideoPushFailures,
    long VideoNoFrame,
    long VideoLateDrops,
    long VideoQueueTrimDrops,
    long VideoCoalescedDrops,
    int  VideoQueueDepth,
    long AudioPushFailures,
    long AudioReadFailures,
    long AudioEmptyReads,
    long AudioPushedFrames,
    long VideoWorkerEnqueueDrops,
    long VideoWorkerStaleDrops,
    long VideoWorkerPushFailures,
    int  VideoWorkerQueueDepth,
    int  VideoWorkerMaxQueueDepth
);
```

**Migration:** Any code printing/displaying the removed fields (e.g. `AVMixerTest`) must remove those references.

---

### Issue 3.6 — `AudioVideoMixerConfig` — dual routing systems and partial mutability ✅ DONE

**Two routing systems co-exist without interacting:**
- The flat `RouteMap` in `AudioVideoMixerConfig` (used by `ForStereo`, `ForSourceToStereo`, etc.)
- `AudioRoutingRule` in `ISupportsAdvancedRouting`

Neither talks to the other. A caller who sets both gets undefined mixing behaviour.

**Additionally,** `OutputSampleRate` has a mutable `set` while all other properties use `init`.

**Fix:**

```csharp
// Step 1: make all properties init-only
public sealed class AudioVideoMixerConfig
{
    public int SourceChannelCount { get; init; }
    public int[]? RouteMap { get; init; }
    public int OutputSampleRate { get; init; }   // was mutable set
    // ...all others already init...
}

// Step 2: document the two routing systems clearly:
// RouteMap: applied globally at the output dispatch stage (all sources, all outputs).
// AudioRoutingRule: per-source-per-output filtering. NOT yet gain-applied (see Issue 3.3).
// They are independent; AudioRoutingRule filtering occurs AFTER RouteMap remapping.
```

**Consideration:** The long-term fix is to unify the two systems, but that is a larger refactor. For now, make them composable by documenting the order of application and ensuring both are applied in the mix loop.

---

### Issue 3.7 — `GetAudioSourcesSnapshot()` allocates on every mix iteration ✅ DONE

Called inside `AudioPumpLoop` ~1024 times/sec. Allocates a new `List<>` each time.

**Fix:** Cache with a version counter:

```csharp
// In AudioVideoMixer:
private volatile int _audioSourcesVersion;

// In AddAudioSource / RemoveAudioSource:
Interlocked.Increment(ref _audioSourcesVersion);

// In AudioPumpLoop:
var lastVersion = -1;
List<(IAudioSource src, double gain)> srcs = new();

while (!ct.IsCancellationRequested)
{
    var v = _audioSourcesVersion;
    if (v != lastVersion)
    {
        srcs = GetAudioSourcesSnapshot();
        lastVersion = v;
    }
    // use srcs...
}
```

---

### Issue 3.8 — `VideoPresenterSyncPolicy` ignores `VideoPresenterSyncPolicyOptions.MaxWait` ⚠️ PARTIAL — MaxWait now read; options type still `internal` and not exposed via `AVMixerConfig`

The `AudioLed` and `Realtime` branches both use a hard-coded `50.0` ms cap.

**Fix:** In `VideoPresenterSyncPolicy.SelectNextFrame` (both branches):

```csharp
// BEFORE:
const double audioLedMaxWaitMs = 50.0;

// AFTER:
double audioLedMaxWaitMs = _options.MaxWait > TimeSpan.Zero
    ? _options.MaxWait.TotalMilliseconds
    : 50.0;
```

Also make `VideoPresenterSyncPolicyOptions` `public` and expose it through `AudioVideoMixerConfig`:

```csharp
public sealed class AudioVideoMixerConfig
{
    // ADD:
    public VideoPresenterSyncPolicyOptions? PresenterSyncOptions { get; init; }
}
```

---

### Issue 3.9 — `AudioVideoMixer.Seek()` restarts playback threads ✅ DONE

Every seek does a full thread stop + restart (with a 4-second join timeout), causing audible dropouts.

**Fix (long term):** Use a command channel:

```csharp
private readonly Channel<double> _seekChannel = Channel.CreateBounded<double>(
    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

public int Seek(double positionSeconds)
{
    _seekChannel.Writer.TryWrite(positionSeconds);
    return MediaResult.Success;
}

// In AudioPumpLoop, at the top of each iteration:
if (_seekChannel.Reader.TryRead(out var seekPos))
{
    // drain mix buffer, seek all sources, reset timeline
    ExecuteSeek(seekPos);
}
```

**Consideration:** The seek must flush any partially-filled audio mix buffers and reset the A/V sync state. The `VideoWorker` queue should also be drained on seek to avoid presenting stale frames.

---

## 4. Playback (`MediaPlayer`)

### Issue 4.1 — `_playerGate` vs `_gate` deadlock risk ❌ NOT DONE

`MediaPlayer` uses `_playerGate` while calling `AddAudioSource` / `AddVideoSource` which acquire `_gate`. Holding `_gate` and calling into `MediaPlayer` would deadlock.

**Fix:** Expose `_gate` as `protected` from `AudioVideoMixer`:

```csharp
// In AudioVideoMixer:
protected Lock Gate => _gate;  // or: protected readonly Lock _sharedGate = _gate;
```

Remove `_playerGate` from `MediaPlayer` and use `Gate` directly, or restructure `MediaPlayer` to not hold any lock while calling base-class methods.

---

### Issue 4.2 — `IMediaPlayer.Play(IMediaItem)` silently produces no audio/video ✅ DONE

`Play(IMediaItem)` only binds sources if the item implements `IMediaPlaybackSourceBinding`. A plain `IMediaItem` starts playback with no sources attached.

**Fix:**

```csharp
// Option A — enforce at the interface level:
public interface IMediaPlayer
{
    int Play(IMediaPlaybackSourceBinding media);  // was IMediaItem
    // ...
}

// Option B — return error code if item cannot provide sources:
public int Play(IMediaItem media)
{
    if (media is not IMediaPlaybackSourceBinding binding)
        return (int)MediaErrorCode.MediaInvalidArgument;
    // ...attach sources from binding...
}
```

---

## 5. Error Code System

### Issue 5.1 — `MediaErrorArea` missing entries for PortAudio, OpenGL, SDL3, MIDI ❌ NOT DONE

`ResolveArea()` maps these to generic `OutputRender` or `GenericCommon`.

**Fix:**

```csharp
public enum MediaErrorArea
{
    Unknown       = 0,
    GenericCommon = 1,
    Playback      = 2,
    Decoding      = 3,
    Mixing        = 4,
    OutputRender  = 5,
    NDI           = 6,
    PortAudio     = 7,   // ADD
    OpenGL        = 8,   // ADD
    MIDI          = 9,   // ADD
    SDL3          = 10,  // ADD
}
```

Update `ErrorCodeRanges.ResolveArea()` to map the existing numeric ranges to the new enum values.

---

### Issue 5.2 — `MediaErrorCode.Success = 0` should not exist ✅ DONE — was never present in the enum

An error-code enum should not have a `Success` value. `MediaResult.Success = 0` already fills this role.

**Fix:** Remove from the enum:

```csharp
public enum MediaErrorCode
{
    // REMOVE: Success = 0,
    MediaInvalidArgument = 1,
    // ...rest unchanged...
}
```

**Migration:** Replace all `(int)MediaErrorCode.Success` comparisons with `MediaResult.Success`.

---

### Issue 5.3 — `Stop()` on disposed objects returns `Success` ✅ DONE

**Fix:** Add a new error code and return it from both `Start()` and `Stop()` when disposed:

```csharp
// In MediaErrorCode enum — ADD:
MediaObjectDisposed = ...,   // choose next available code in GenericCommon range

// In implementations:
public int Stop()
{
    if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
    // ...
}
```

---

### Issue 5.4 — NDI read-rejection codes mapped to wrong semantic ❌ NOT DONE

`NDIAudioReadRejected` and `NDIVideoReadRejected` are mapped to `MediaConcurrentOperationViolation`. Read rejection also occurs when the source is stopped.

**Fix:** Add `MediaSourceNotRunning` to the error code enum:

```csharp
MediaSourceNotRunning = ...,  // in GenericCommon or a new area
```

Return it from `NDIVideoSource` / `NDIAudioSource` when `_running == false`, and return `NDI*ReadRejected` only when the concurrent-read flag is set.

---

## 6. Cross-Cutting Data Types

### Issue 6.1 — `VideoFrame` manual ref-counting is fragile ❌ NOT DONE

`VideoFrame` uses `AddRef()` / `_refCount` for pooling. Forgetting `AddRef()` before enqueuing to a worker is a silent correctness bug.

**Fix options (choose one):**
1. **`IMemoryOwner<byte>` planes:** Replace raw pooled arrays with `IMemoryOwner<byte>` for each plane. Disposal transfers ownership cleanly.
2. **`VideoFrameRef` wrapper:** A ref-counted wrapper with a finalizer safety net, similar to `System.Buffers.MemoryPool<T>`.
3. **`ReadOnlySequence<byte>` planes:** For zero-copy paths.

**Minimum viable fix:** Add a `[MustCallAddRefBeforeEnqueue]` attribute (custom annotation) and document the contract prominently in the XML doc.

**Consideration:** The `VideoFrame` class is a hot path. Any solution must not introduce per-frame heap allocations. `IMemoryOwner<byte>` has zero overhead when the backing array is pool-rented.

---

### Issue 6.2 — `MediaMetadataSnapshot.AdditionalMetadata` is untyped ❌ NOT DONE

Common fields (title, artist, album) are accessible only via string key lookup.

**Fix:**

```csharp
public sealed record MediaMetadataSnapshot
{
    // ADD well-known fields:
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Year { get; init; }

    // KEEP for arbitrary metadata:
    public IReadOnlyDictionary<string, string> AdditionalMetadata { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
```

Also add polling to `IDynamicMetadata`:

```csharp
public interface IDynamicMetadata
{
    MediaMetadataSnapshot? GetMetadata();        // ADD
    event EventHandler<MediaMetadataSnapshot>? MetadataChanged;
}
```

---

## 7. Naming & Consolidation

> For the full cross-project naming analysis see `Naming-and-Consolidation.md`.

### 7.1 `AudioVideoMixer*` prefix — shorten to `AV` ✅ DONE

Every mixer-related public type carries the eight-character prefix `AudioVideo`. This makes code verbose with no readability gain — the A/V domain is already obvious from the namespace and interface names.

| Current | Proposed |
|---|---|
| `AudioVideoMixer` | `AVMixer` |
| `IAudioVideoMixer` | `IAVMixer` |
| `AudioVideoMixerConfig` | `AVMixerConfig` |
| `AudioVideoMixerState` | `AVMixerState` |
| `AudioVideoMixerDebugInfo` | `AVMixerDebugInfo` |
| `AudioVideoMixerStateChangedEventArgs` | `AVMixerStateChangedEventArgs` |
| `AudioVideoSyncMode` | `AVSyncMode` |

---

### 7.2 `MixerClockTypeRules` — remove the class ⚠️ PARTIAL — marked `[Obsolete]`, not deleted

This is an 18-line class with a single method used only inside `AudioVideoMixerConfig` and `AudioVideoMixer`. It adds no encapsulation or testability over a private static method.

**Fix:** Move `ValidateClockType` into `AVMixerConfig` as `private static` and delete `MixerClockTypeRules.cs`.

---

### 7.3 `AudioSourceErrorEventArgs` + `VideoSourceErrorEventArgs` — unify ❌ NOT DONE

Both are structurally identical (three identical properties). Replace with a single `MediaSourceErrorEventArgs`. See `Naming-and-Consolidation.md` §1.3.

---

### 7.4 `VideoOutputTimestampMonotonicMode` → `VideoTimestampMode` ❌ NOT DONE

Four-word type name with "Monotonic" embedded. The three enum values (`Passthrough`, `ClampForward`, `RebaseOnDiscontinuity`) are self-explanatory. Rename the type and the `VideoOutputConfig` property:

```csharp
// BEFORE:
public VideoOutputTimestampMonotonicMode TimestampMonotonicMode { get; init; }

// AFTER:
public VideoTimestampMode TimestampMode { get; init; }
```

---

### 7.5 `VideoPresenterSyncPolicy` / `VideoPresenterSyncPolicyOptions` — rename and make public ❌ NOT DONE

These are currently `internal`. When made public (see §3.8), the `Presenter` word is redundant:

| Current | Proposed |
|---|---|
| `VideoPresenterSyncPolicy` | `VideoSyncPolicy` |
| `VideoPresenterSyncPolicyOptions` | `VideoSyncOptions` |

---

### 7.6 `VideoPresentationHostPolicy` → `VideoDispatchPolicy` ❌ NOT DONE

"Host policy" sounds like OS or UI hosting. The actual meaning is "how the mixer dispatches video frames to outputs". Better name:

```csharp
public enum VideoDispatchPolicy
{
    DirectThread    = 0,   // was DirectPresenterThread
    BackgroundWorker = 1,  // was ManagedBackground
}
```

---

### 7.7 `ISupportsAdvancedRouting` — rename or fold ⚠️ PARTIAL — folded into `IAVMixer`, not renamed

If routing is merged into `IAVMixer` (§3.4), this interface disappears. If kept separate, rename to remove the "Advanced" implication:

```csharp
public interface IMixerRouting { ... }   // preferred
```

---

### 7.8 `MixerSourceDetachOptions` → `SourceDetachOptions` ✅ DONE — type marked `[Obsolete]`; replaced by optional bool params

The `Mixer` prefix is not needed — detach options are specific to the source, not the mixer. If replaced with optional bool parameters on `RemoveAudioSource` (see `Naming-and-Consolidation.md` §2.11), the type disappears entirely.

---

### 7.9 `MediaErrorAllocations` int shortcuts — remove ❌ NOT DONE — ~30 redundant `int` properties still present

`MediaErrorAllocations` has ~30 `public static int` properties that are trivial casts of `MediaErrorCode` enum values. These are redundant — callers should cast the enum directly. Only the `ErrorCodeAllocationRange` properties should remain. See `Naming-and-Consolidation.md` §1.4.

---

### 7.10 File name mismatch: `AudioVideoMixerRuntimeSnapshot.cs` → `AudioVideoMixerDebugInfo.cs` ❌ NOT DONE — file still named `AudioVideoMixerRuntimeSnapshot.cs`, contains `AVMixerDiagnostics`

The file is named `AudioVideoMixerRuntimeSnapshot.cs` but contains `AudioVideoMixerDebugInfo`. Rename the file to match the type.

---

## 8. Implementation Audit — New Findings (March 2026)

Cross-referencing the documented issues against the actual implementation revealed the following additional problems.

---

### 8.1 — `AudioPumpLoop` completely ignores `AudioRoutingRule` — **CONFIRMED BUG** ✅ DONE

Issue §3.3 documents `Gain` being unread, but the situation is worse: **no audio routing rules are consulted at all** during mixing. The audio pump mixes all sources into all outputs unconditionally. `AudioRoutingRule.SourceId` and `AudioRoutingRule.OutputId` have zero effect on audio. Only video routing (`VideoRoutingRule`) is applied — in `PushFrameToOutputs`.

**Fix:** Either apply audio routing rules in `AudioPumpLoop` (per-source-per-output filtering), or remove `AudioRoutingRule` from the v1 API and document that audio is always mixed to all outputs.

---

### 8.2 — `GetAudioSourcesSnapshot()` called on every mix iteration — **HOT-PATH ALLOCATION** ✅ DONE

`GetAudioSourcesSnapshot()` is called inside the `while (!ct.IsCancellationRequested)` loop in `AudioPumpLoop`, allocating a new `List<>` every ~21 ms (48 kHz / 1024 frames). The existing issue §3.7 describes this; it was not implemented.

**Fix implemented:** Version-counter pattern — snapshot is taken once and only refreshed when `_audioSourcesVersion` changes (incremented by `AddAudioSource` / `RemoveAudioSource`).

---

### 8.3 — `VideoPresenterSyncPolicy.MaxWait` ignored in `AudioLed` and `Realtime` paths — **BUG** ✅ DONE

Both branches hard-code `const double audioLedMaxWaitMs = 50.0` and `50.0` respectively, ignoring `options.MaxWait`. Only the `Synced` path correctly uses `options.MaxWait`.

**Fix implemented:** Both `AudioLed` and `Realtime` paths now read `options.MaxWait.TotalMilliseconds` with a `50.0` fallback when `MaxWait <= 0`.

---

### 8.4 — `AudioVideoMixerConfig.SyncMode` and `OutputSampleRate` have mutable `set` — **API INCONSISTENCY** ✅ DONE

All other `AudioVideoMixerConfig` properties use `init`. `SyncMode` and `OutputSampleRate` use mutable `set`, allowing post-construction mutation that is never observed by the running mixer.

**Fix implemented:** Both changed to `init`.

---

### 8.5 — `IVideoSource.StreamInfo` missing — **API GAP** ✅ DONE

`IAudioSource` has `AudioStreamInfo StreamInfo`. `IVideoSource` has no equivalent. Resolution, frame rate, and duration are inaccessible through the interface.

**Fix implemented:** Added with a default implementation (`=> default`) to avoid breaking existing stubs.

---

### 8.6 — `IVideoOutput.State` missing — **API GAP** ✅ DONE

`IAudioOutput.State` exists; `IVideoOutput` has no equivalent. Callers cannot inspect output state without downcasting.

**Fix implemented:** Added `VideoOutputState State { get; }` with default `=> VideoOutputState.Stopped`. New `VideoOutputState` enum: `Stopped = 0`, `Running = 1`.

---

### 8.7 — `SeekToFrame(long, out long, out long?)` 3-param overload is dead weight ✅ DONE

The 3-out-parameter overload of `SeekToFrame` is not called by any consumer code. `FFVideoSource` implements it by delegating to the 1-parameter version. No caller has ever used the out parameters.

**Fix implemented:** Removed from `IVideoSource`. Concrete implementations may keep their overloads.

---

### 8.8 — `Diagnostics/` folder is dead weight ❌ NOT DONE — `DebugInfo.cs`, `DebugKeys.cs`, `DebugValueKind.cs` still present and unreferenced

`DebugInfo`, `DebugKeys`, and `DebugValueKind` exist in `S.Media.Core/Diagnostics/` but are not used anywhere — not by the mixer, not by any consumer library. They represent an unused abstraction layer.

**v1 recommendation:** Remove. If structured debug reporting is needed in a future version it should be designed as part of a real diagnostic pipeline, not a generic untyped `object Value` bag.

---

### 8.9 — `AudioVideoMixer.AudioSources` property allocates via LINQ on every read ✅ DONE — uses `ConvertAll`

```csharp
public IReadOnlyList<IAudioSource> AudioSources
{ get { lock (_gate) return _audioSources.Select(x => x.Source).ToList(); } }
```

`Select(...).ToList()` inside a lock allocates on every property read. Low frequency, but unnecessary.

**Fix:** Project to a read-only list without LINQ: `_audioSources.ConvertAll(x => x.Source)`.

---

### 8.10 — `IAudioVideoMixer` and `ISupportsAdvancedRouting` are separate interfaces — **FRAGMENTATION** ✅ DONE

Callers must downcast `IAudioVideoMixer` to `ISupportsAdvancedRouting` to access routing. Since every mixer implementation must support both, there is no reason to keep them separate.

**Fix implemented:** `IAudioVideoMixer` now extends `ISupportsAdvancedRouting`.

---

### 8.11 — `Start()/Stop()/Pause()/Resume()` on `IAudioVideoMixer` duplicate `StartPlayback()`/`StopPlayback()` ⚠️ PARTIAL — removed from `IAVMixer`; still `public` (not `protected`) on `AVMixer`

The two-step protocol (`Start()` to start the clock, then `StartPlayback()` to start threads) is confusing. `Start()` alone produces a running mixer with no audio/video pumps. External callers do not need this distinction.

**Fix implemented:** `Start()`, `Stop()`, `Pause()`, `Resume()` removed from `IAudioVideoMixer`. Added `PausePlayback()` and `ResumePlayback()`. The concrete `AudioVideoMixer` class retains all four methods for internal use and subclass access.

---

### 8.12 — `IAudioSink` does not exist — `IAudioOutput` bundles device management with frame pushing ✅ DONE — `IAudioSink` created; `AddAudioOutput` accepts `IAudioSink`

Confirmed §1.1. Currently `NDIVideoOutput` cannot implement `IAudioOutput` cleanly because it has no concept of audio devices.

**Fix implemented (partial):** `IAudioSink` interface created with the non-device members (`Id`, `State`, `Start`, `Stop`, `PushFrame`). `IAudioOutput` now extends `IAudioSink`. `AddAudioOutput` still accepts `IAudioOutput` for now; it will be changed to `IAudioSink` once `NDIVideoOutput` is updated to implement `IAudioSink` directly.

---

## 9. v1 Baseline — What to Keep, Simplify, or Scrap

This section captures the pragmatic v1 decisions. All items marked **SCRAP** should be removed before the v1 release unless a concrete use case emerges.

| Feature / Type | Decision | Reason |
|---|---|---|
| `TickVideoPresentation()` | **SCRAP** | Dead no-op. Removed. |
| `AudioVideoMixerDebugInfo` ghost fields (DriftMs, CorrectionSignalMs, etc.) | **SCRAP** | Always zero. Removed. |
| `MediaErrorCode.Success = 0` | **SCRAP** | `MediaResult.Success = 0` already fills this role. Removed. |
| `MediaErrorAllocations` int shortcuts | **SCRAP** | Redundant casts of enum values. Callers cast directly. |
| `SeekToFrame(long, out long, out long?)` on `IVideoSource` | **SCRAP** | Never called. Removed from interface. |
| `AudioRoutingRule` ignored in `AudioPumpLoop` | **FIX** | Confirmed bug (§8.1). Per-output routing path with Gain now implemented. Fast path preserved when no rules. |
| `MixerSourceDetachOptions` | **DONE** | Replaced with optional `bool stopOnDetach, bool disposeOnDetach` params on `RemoveAudioSource`/`RemoveVideoSource`. Type marked `[Obsolete]`. |
| `AudioVideoMixer*` naming (8-char prefix) | **DONE** | Renamed to `AV*`: `AVMixer`, `IAVMixer`, `AVMixerConfig`, `AVMixerState`, `AVMixerDiagnostics`, `AVMixerStateChangedEventArgs`, `AVSyncMode`. |
| `IAudioSource.SourceId` / `IVideoSource.SourceId` | **DONE** | Renamed to `Id` on both interfaces and all implementations. |
| `Seek()` restarts threads | **DONE** | Replaced with `Channel<double>` command. AudioPumpLoop drains it in-loop — no thread stop/restart. Idle path still executes synchronously. |

---

## 10. OwnAudio Reference Analysis & Design Decisions

The OwnAudio library (`Reference/OwnAudio/`) was the predecessor audio engine used for playback. Its source is split across `Source/` (main C# library) and `OwnAudioEngine/` (native PortAudio binding). The following sections document findings from comparing it against S.Media.Core and record explicit design decisions.

---

### 10.1 — `IVideoSink` — Not Needed

The user raised the question: if we introduce `IAudioSink` to separate frame-pushing from device management, should there also be an `IVideoSink`?

**Answer: No.** `IAudioSink` was necessary because `IAudioOutput` bundles device management methods (`SetOutputDevice`, `SetOutputDeviceByName`, `SetOutputDeviceByIndex`, `Device`, `AudioDeviceChanged`) with the frame-pushing contract, making network/file audio sinks impossible to implement cleanly.

`IVideoOutput` already has only the clean sink contract:

```csharp
public interface IVideoOutput : IDisposable
{
    Guid Id { get; }
    int Start(VideoOutputConfig config);
    int Stop();
    int PushFrame(VideoFrame frame);
    int PushFrame(VideoFrame frame, TimeSpan presentationTime);
}
```

There is no video-device-management layer equivalent to PortAudio device selection. All current and planned video outputs (SDL3, OpenGL, NDI) are pure sinks. **`IVideoOutput` IS the video sink interface — no split needed.**

If a future implementation required hardware video-output device selection (e.g., a capture-card output), it would extend `IVideoOutput` with device management, mirroring the `IAudioSink`→`IAudioOutput` split at that time.

---

### 10.2 — No Drift Correction for v1 — Explicit Design Decision

OwnAudio's `FileSource.Synchronization.cs` implements a Three-Zone drift correction system using SoundTouch tempo adjustment:

- **Green Zone** (< 20 ms drift): No correction.
- **Yellow Zone** (20–100 ms): *Soft sync* — a lock-free volatile write of a tempo adjustment (`_pendingSoftSyncTempoAdjustment`) picked up by the decoder thread.
- **Red Zone** (> 100 ms): Hard seek, buffer skip, or predictive seek.

This is ~300 lines of synchronization logic, depends on SoundTouch (a native library) for tempo manipulation, and introduces subtle multi-thread communication patterns (lock-free volatile writes between the mixer thread and per-source decoder threads).

**Decision for v1: No drift correction.** Sources are expected to produce data at the correct rate. The mixer's responsibility is to pull frames from sources and push them to outputs; it does not compensate for source clock drift. This is sound because:

1. `FFAudioSource`/`FFVideoSource` (file playback) are deterministic — no drift is possible.
2. NDI sources carry their own embedded timecode; the receiver handles synchronisation internally.
3. PortAudio input captures at the hardware sample rate, which is already the clock reference.

If drift correction becomes necessary in a future version, it should live in a **per-source policy** (e.g., `IAudioSource.ResyncTo(long samplePosition)` or an `IAudioClockFollower` interface), NOT in the central mixer loop. OwnAudio itself learned this lesson: its earlier global-resync approach caused "thundering herd" CPU spikes when 20+ tracks all sought simultaneously, and was reverted in favour of per-source Three-Zone correction.

> The existing §8.3 bug (MaxWait hard-coded in VideoPresenterSyncPolicy) is a correctness fix, not drift correction, and should still be applied.

---

### 10.3 — Thread Priority Not Set on Pump Threads — **BUG** ✅ DONE

OwnAudio sets all audio-critical threads to `ThreadPriority.Highest` and video threads to `AboveNormal`. S.Media.Core's `StartPlaybackThreads` creates threads without setting priority — they default to `Normal`. Under system load, `Normal` threads can be pre-empted for 10–30 ms, causing audible audio glitches.

**Fix:** Set thread priority in `StartPlaybackThreads`:

```csharp
_audioPumpThread = new Thread(() => AudioPumpLoop(ct))
{
    Name = "AudioVideoMixer.AudioPump",
    IsBackground = true,
    Priority = ThreadPriority.Highest       // ADD
};

_videoDecodeThread = new Thread(() => VideoDecodeLoop(ct))
{
    Name = "AudioVideoMixer.VideoDecode",
    IsBackground = true,
    Priority = ThreadPriority.AboveNormal   // ADD
};

_videoPresentThread = new Thread(() => VideoPresentLoop(ct))
{
    Name = "AudioVideoMixer.VideoPresent",
    IsBackground = true,
    Priority = ThreadPriority.AboveNormal   // ADD
};
```

**Note:** `ThreadPriority.Highest` is safe on .NET for background audio threads because they spend most of their time blocked on `PushFrame` (which blocks on the hardware buffer), and the GC will still stop them for collection pauses. Avoid `Highest` on long-running CPU-bound threads.

---

### 10.4 — Per-Source `Volume` Missing from `IAudioSource` — **FEATURE GAP** ✅ DONE

OwnAudio's `IAudioSource` has `float Volume { get; set; }` (0.0–1.0). It is applied in the mix thread before accumulation. S.Media.Core's `IAudioSource` has no per-source volume. Callers cannot adjust individual source levels without wrapping the source.

**Fix:** Add to `IAudioSource`:

```csharp
public interface IAudioSource
{
    // ADD:
    /// <summary>Per-source volume multiplier. 0.0 = silent, 1.0 = unity. Default: 1.0.</summary>
    float Volume { get; set; }
    // ...existing...
}
```

Apply in `AudioPumpLoop` before accumulation:

```csharp
var vol = src.Volume;
if (Math.Abs(vol - 1.0f) < 0.001f)
    for (var i = 0; i < fr * sourceChannels; i++) mixBuf[i] += tempBuf[i];
else
    for (var i = 0; i < fr * sourceChannels; i++) mixBuf[i] += tempBuf[i] * vol;
```

The fast path (vol ≈ 1.0) avoids a multiply per sample. Concrete implementations should default `Volume = 1.0f`.

---

### 10.5 — `MasterVolume` Missing from `IAudioVideoMixer` — **FEATURE GAP** ✅ DONE

OwnAudio's `AudioMixer` has `float MasterVolume { get; set; }` applied to the final mixed buffer after accumulation. S.Media.Core has no master output volume — all outputs receive the raw mixed signal.

**Fix:** Add to `IAudioVideoMixer`:

```csharp
/// <summary>Master output volume applied post-mix. 0.0 = silent, 1.0 = unity. Default: 1.0.</summary>
float MasterVolume { get; set; }
```

Apply in `AudioPumpLoop` after accumulation, before the output push (fast path skips multiply at unity):

```csharp
var masterVol = _masterVolume;
if (Math.Abs(masterVol - 1.0f) > 0.001f)
    for (var i = 0; i < active; i++) mixBuf[i] *= masterVol;
```

The clamp (`Math.Clamp(mixBuf[i], -1f, 1f)`) should still run after master volume — do not remove it.

---

### 10.6 — SIMD Not Used in Mix Accumulation and Clamp — **PERFORMANCE** ✅ DONE

OwnAudio's `AudioMixer.Processing.cs` uses `System.Numerics.Vector<float>` SIMD for `MixIntoBuffer` and `ApplyMasterVolume`, processing 4–8 floats per instruction on AVX2 hardware.

S.Media.Core's `AudioPumpLoop` uses scalar loops:

```csharp
for (var i = 0; i < fr * sourceChannels; i++) mixBuf[i] += tempBuf[i];   // scalar
for (var i = 0; i < active; i++) mixBuf[i] = Math.Clamp(mixBuf[i], -1f, 1f); // scalar
```

At 48 kHz / 1024 frames / 2 channels = 2048 samples per batch with 4 simultaneous sources, this is ~32 768 FLOP per mix cycle. SIMD gives a 4–8× throughput improvement on the accumulation inner loop.

**Fix:** Extract a `AudioMixUtils` static helper (same file, or `Audio/AudioMixUtils.cs`):

```csharp
internal static class AudioMixUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MixInto(float[] dst, float[] src, int count, float gain = 1.0f)
    {
        int i = 0, simd = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && count >= simd)
        {
            var gVec = new Vector<float>(gain);
            int end = count - count % simd;
            for (; i < end; i += simd)
                (new Vector<float>(dst, i) + new Vector<float>(src, i) * gVec).CopyTo(dst, i);
        }
        for (; i < count; i++) dst[i] += src[i] * gain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clamp(float[] buf, int count)
    {
        int i = 0, simd = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && count >= simd)
        {
            var mn = new Vector<float>(-1f);
            var mx = new Vector<float>( 1f);
            int end = count - count % simd;
            for (; i < end; i += simd)
                Vector.Clamp(new Vector<float>(buf, i), mn, mx).CopyTo(buf, i);
        }
        for (; i < count; i++) buf[i] = Math.Clamp(buf[i], -1f, 1f);
    }
}
```

Replace scalar loops in `AudioPumpLoop`:

```csharp
AudioMixUtils.MixInto(mixBuf, tempBuf, fr * sourceChannels, src.Volume);
// ...after all sources:
AudioMixUtils.Clamp(mixBuf, active);
```

**Note:** `gain` is passed directly so §10.4 (per-source volume) and SIMD are implemented in one step.

---

### 10.7 — `AudioPumpLoop` Advances Timeline on Empty Read — **BUG** ✅ DONE

Current code when all sources return no data:

```csharp
if (!anyRead)
{
    Interlocked.Increment(ref _audioEmptyReads);
    Thread.Sleep(1);
    timelineSamples += framesPerBatch;      // ← INCORRECT
    UpdateAudioClock(timelineSamples, sampleRate);
    continue;
}
```

Advancing `timelineSamples` without producing any audio causes the A/V clock to jump forward even though silence was not sent to any output. Video frames are then presented too early relative to actual audio output. This is especially problematic at the beginning of playback before sources have started, and at end-of-stream when sources are exhausted.

OwnAudio's mix thread only advances the clock _after_ the engine receives data:

```csharp
_engine.Send(mixBuffer);              // audio delivered
_masterClock.Advance(_bufferSizeInFrames); // ONLY advance after delivery
```

**Fix:** Do not advance `timelineSamples` on empty reads. Only sleep and retry:

```csharp
if (!anyRead)
{
    Interlocked.Increment(ref _audioEmptyReads);
    Thread.Sleep(1);
    continue;   // ← do NOT advance timeline
}
```

The timeline should advance only in the success path, after `UpdateAudioClock(timelineSamples += framesProduced, sampleRate)`.

---

### 10.8 — `AudioPumpLoop` Calls `ReadSamples` on Stopped Sources — **BUG** ✅ DONE

OwnAudio's mix thread checks `source.State != AudioState.Playing` and skips sources that are not actively playing. S.Media.Core's `AudioPumpLoop` calls `ReadSamples` unconditionally regardless of `src.State`. A stopped source may return garbage, error codes, or stale data.

**Fix:** Add a state gate in `AudioPumpLoop`:

```csharp
foreach (var (src, offset) in srcs)
{
    if (src.State != AudioSourceState.Running) continue;    // ADD
    if (timelineSeconds < offset) continue;
    // ...ReadSamples...
}
```

This also ensures sources at their start offset that have not yet reached their cue point are skipped cleanly.

---

### 10.9 — `AudioResampler`: Keep in Core, Add FFmpeg-backed Alternative — **DESIGN DECISION** ⚠️ PARTIAL — `IAudioResampler` interface created; `AVMixerConfig.ResamplerFactory` hook not added

S.Media.Core contains a pure-C# windowed-sinc + linear resampler (`Audio/AudioResampler.cs`, 487 lines). OwnAudio uses SoundTouch for pitch/tempo changes but not for plain sample-rate conversion.

When `S.Media.FFmpeg` is available, libswresample (`swr_alloc`, `swr_convert`) is a better choice:
- Handles all standard rates (8 kHz to 384 kHz) correctly with high-quality polyphase filters.
- Handles stereo and multichannel layout conversions natively.
- Is actively maintained and fuzz-tested as part of the FFmpeg test suite.
- Zero additional native dependency cost (already linked via `libavutil`/`libswresample`).

**Decision:**

1. `S.Media.Core` **keeps `AudioResampler`** as the pure-C# fallback — use cases where `S.Media.FFmpeg` is not available (e.g., custom audio pipelines without file decoding).

2. Extract a new interface **`IAudioResampler`** in `S.Media.Core/Audio/`:

   ```csharp
   // S.Media.Core/Audio/IAudioResampler.cs
   public interface IAudioResampler : IDisposable
   {
       int SourceSampleRate { get; }
       int TargetSampleRate { get; }
       int Resample(ReadOnlySpan<float> source, int inputFrameCount, Span<float> destination);
       int EstimateOutputFrameCount(int inputFrameCount);
       void Reset();
   }
   ```

   `AudioResampler` implements `IAudioResampler`.

3. `S.Media.FFmpeg` adds **`FFAudioResampler : IAudioResampler`** backed by `swr_alloc` / `swr_convert`. `FFAudioSource` uses `FFAudioResampler` internally instead of `AudioResampler`.

4. The mixer's `AudioPumpLoop` should accept an optional `IAudioResampler` from `AudioVideoMixerConfig` when resampling is needed (e.g., heterogeneous source sample rates):

   ```csharp
   public sealed class AudioVideoMixerConfig
   {
       // ADD (optional, defaults to null = no resampling):
       public Func<int, int, IAudioResampler>? ResamplerFactory { get; init; }
   }
   ```

   When `null`, the mixer assumes all sources match `OutputSampleRate`. When set, the factory is called once per source on first read if its `StreamInfo.SampleRate` differs from `OutputSampleRate`.

---

### 10.10 — Cached Sources Array: Use Volatile Flag, Not Version Counter ✅ DONE

§3.7 / §8.2 recommend a version counter for `GetAudioSourcesSnapshot()`. OwnAudio uses a simpler `volatile bool _sourcesArrayNeedsUpdate` flag — identical semantics, one fewer counter variable.

**Recommendation:** Use the OwnAudio flag pattern in `AudioPumpLoop`:

```csharp
// In AudioVideoMixer — ADD fields:
private volatile bool _audioSourcesNeedsUpdate = true;
private IAudioSource[] _audioSourcesCache = [];
private double[] _audioSourceOffsetCache = [];

// In AddAudioSource / RemoveAudioSource:
_audioSourcesNeedsUpdate = true;

// In AudioPumpLoop — replace GetAudioSourcesSnapshot() loop:
if (_audioSourcesNeedsUpdate)
{
    lock (_gate)
    {
        _audioSourcesCache      = _audioSources.ConvertAll(x => x.Source).ToArray();
        _audioSourceOffsetCache = _audioSources.ConvertAll(x => x.StartOffset).ToArray();
        _audioSourcesNeedsUpdate = false;
    }
}
var srcsSnap    = _audioSourcesCache;
var offsetsSnap = _audioSourceOffsetCache;
```

This replaces `GetAudioSourcesSnapshot()` which allocates a new `List<>` on every iteration. In steady state (no sources added/removed) the snapshot path is never executed.

---

### 10.11 — `IAudioSource` State Enum: Add `EndOfStream` ✅ DONE

OwnAudio's `AudioState` enum has four values: `Stopped`, `Paused`, `Playing`, `EndOfStream`. S.Media.Core's `AudioSourceState` only has `Stopped` and `Running`. There is no way to distinguish "stopped because asked to stop" from "stopped because the file ended".

The mixer's `AudioPumpLoop` cannot distinguish end-of-file from a source error, and has no way to know when all sources are exhausted.

**Fix:** Add `EndOfStream` to `AudioSourceState`:

```csharp
public enum AudioSourceState
{
    Stopped    = 0,
    Running    = 1,
    EndOfStream = 2,  // ADD: source produced all available data
}
```

`FFAudioSource` sets `State = EndOfStream` when the demux session reports EOF. The mixer can then detect when all sources are in `EndOfStream` and auto-stop playback or raise a `PlaybackComplete` event.

This also resolves §1.3 (`IsEndOfStream` property) — it is redundant with `State == EndOfStream`. Only add the enum value, not the property.

---

### 10.12 — `AudioVideoMixer.AudioSources` LINQ vs ConvertAll — **Hot Fix** ✅ DONE

Already documented in §8.9. OwnAudio confirms: source lists are iterated heavily in the mix thread — any allocation inside a lock is undesirable. The concrete fix:

```csharp
// BEFORE (allocates via LINQ):
public IReadOnlyList<IAudioSource> AudioSources
{ get { lock (_gate) return _audioSources.Select(x => x.Source).ToList(); } }

// AFTER (allocation still exists but no LINQ overhead):
public IReadOnlyList<IAudioSource> AudioSources
{ get { lock (_gate) return _audioSources.ConvertAll(x => x.Source); } }
```

`List<T>.ConvertAll` is marginally faster than `Select().ToList()` because it pre-sizes the result list and avoids LINQ enumerator overhead. Combined with §10.10, the `AudioSources` property becomes a thin snapshot for external callers while the mix thread uses the cached array.

---

### Summary — New Issues Added in §10

| # | Finding | Category | Priority | Status |
|---|---|---|---|---|
| 10.1 | `IVideoSink` not needed | Design decision | Decided | ✅ |
| 10.2 | No drift correction for v1 | Design decision | Decided | ✅ |
| 10.3 | Thread priority not set on pump threads | Bug | High | ✅ |
| 10.4 | Per-source `Volume` missing from `IAudioSource` | Feature gap | Medium | ✅ |
| 10.5 | `MasterVolume` missing from `IAudioVideoMixer` | Feature gap | Medium | ✅ |
| 10.6 | SIMD not used in mix accumulation and clamp | Performance | Medium | ✅ |
| 10.7 | Timeline advances on empty read | Bug | High | ✅ |
| 10.8 | `ReadSamples` called on stopped sources | Bug | High | ✅ |
| 10.9 | `IAudioResampler` abstraction + `AVMixerConfig.ResamplerFactory` | Design decision | Medium | ⚠️ interface only |
| 10.10 | Cached sources: volatile flag pattern | Performance | Low | ✅ |
| 10.11 | `AudioSourceState` missing `EndOfStream` | Feature gap | Medium | ✅ |
| 10.12 | LINQ in `AudioSources` (see §8.9) | Performance | Low | ✅ |

---

## 11. Second-Pass Audit — March 29, 2026

Cross-referencing all prior documented issues against the actual implementation after the fix pass.

**Legend:** ✅ Done · ⚠️ Partial · ❌ Not done · 🆕 New finding

---

### 11.1 — Full Status Table

| Issue | Title | Status |
|---|---|---|
| 1.1 | `IAudioSink` / `IAudioOutput` split | ✅ |
| 1.2 | `SourceId` → `Id` on `IAudioSource` / `IVideoSource` | ✅ |
| 1.3 | `EndOfStream` state + `TotalSampleCount` | ✅ |
| 1.4 | `PushFrame(in AudioFrame)` identity overload on `IAudioSink` | ✅ |
| 2.1 | `IVideoSource.StreamInfo` | ✅ |
| 2.2 | `IVideoOutput.State` / `VideoOutputState` enum | ✅ |
| 2.3 | `IVideoOutput.PushFrame` blocking contract documented | ❌ |
| 2.4 | `SeekToFrame(long, out, out)` removed from interface | ✅ |
| 3.1 | `Start/Stop/Pause/Resume` removed from `IAVMixer`; `protected` on `AVMixer` | ✅ |
| 3.2 | `TickVideoPresentation()` deleted | ⚠️ Marked `[Obsolete]`, not deleted |
| 3.3 | `Gain` applied in mix loop | ✅ |
| 3.4 | `IAVMixer : ISupportsAdvancedRouting` | ✅ |
| 3.5 | Ghost drift-correction fields removed from diagnostics | ✅ |
| 3.6 | `OutputSampleRate` / `SyncMode` → `init`-only | ✅ |
| 3.7 | Volatile flag for source snapshot cache | ✅ |
| 3.8 | `VideoPresenterSyncPolicy.MaxWait` used in AudioLed/Realtime | ✅ |
| 3.8 | `VideoPresenterSyncPolicyOptions` public + in `AVMixerConfig` | ❌ |
| 3.9 | `Seek()` via `Channel<double>`, no thread restart | ✅ |
| 4.1 | `_playerGate` deadlock risk resolved | ❌ |
| 4.2 | `Play(IMediaItem)` returns error when item has no sources | ✅ |
| 5.1 | `MediaErrorArea` — PortAudio / OpenGL / SDL3 / MIDI entries | ❌ |
| 5.2 | `MediaErrorCode.Success = 0` removed | ✅ (was never present) |
| 5.3 | `MediaObjectDisposed` error code; `Stop()` on disposed | ✅ |
| 5.4 | `MediaSourceNotRunning`; NDI ReadRejected semantic corrected | ❌ |
| 6.1 | `VideoFrame` ref-counting fragility annotated | ❌ |
| 6.2 | `MediaMetadataSnapshot` well-known fields + `IDynamicMetadata.GetMetadata()` | ❌ |
| 7.1 | `AudioVideo*` → `AV*` rename | ✅ |
| 7.2 | `MixerClockTypeRules` deleted | ⚠️ Marked `[Obsolete]`, not deleted |
| 7.3 | `AudioSourceErrorEventArgs` / `VideoSourceErrorEventArgs` unified | ❌ |
| 7.4 | `VideoOutputTimestampMonotonicMode` → `VideoTimestampMode` | ❌ |
| 7.5 | `VideoPresenterSyncPolicy/Options` renamed + made public | ❌ |
| 7.6 | `VideoPresentationHostPolicy` → `VideoDispatchPolicy` | ❌ |
| 7.7 | `ISupportsAdvancedRouting` renamed / folded | ⚠️ Folded into `IAVMixer`, not renamed |
| 7.8 | `MixerSourceDetachOptions` marked obsolete | ✅ |
| 7.9 | `MediaErrorAllocations` int shortcuts removed | ❌ |
| 7.10 | File `AudioVideoMixerRuntimeSnapshot.cs` renamed to `AVMixerDiagnostics.cs` | ❌ |
| 8.1 | Audio routing rules consulted in pump loop | ✅ |
| 8.2 | Hot-path snapshot allocation fix | ✅ |
| 8.3 | `MaxWait` fix in sync policy | ✅ |
| 8.4 | `SyncMode` / `OutputSampleRate` init-only | ✅ |
| 8.5 | `IVideoSource.StreamInfo` | ✅ |
| 8.6 | `IVideoOutput.State` | ✅ |
| 8.7 | `SeekToFrame` 3-param removed | ✅ |
| 8.8 | `Diagnostics/` folder removed | ❌ |
| 8.9 | `AudioSources` LINQ → `ConvertAll` | ✅ |
| 8.10 | `IAVMixer : ISupportsAdvancedRouting` | ✅ |
| 8.11 | `Start/Stop/Pause/Resume` off `IAVMixer`; `protected` on `AVMixer` | ✅ |
| 8.12 | `IAudioSink` created; `AddAudioOutput` accepts `IAudioSink` | ✅ |
| 10.3 | Thread priorities set (`Highest` / `AboveNormal`) | ✅ |
| 10.4 | Per-source `Volume` on `IAudioSource` | ✅ |
| 10.5 | `MasterVolume` on `IAVMixer` | ✅ |
| 10.6 | SIMD `AudioMixUtils` | ✅ |
| 10.7 | Empty-read does not advance timeline | ✅ |
| 10.8 | State gate skips stopped sources | ✅ |
| 10.9 | `IAudioResampler` interface created | ✅ |
| 10.9 | `AVMixerConfig.ResamplerFactory` hook | ❌ |
| 10.10 | Volatile flag for source snapshot | ✅ |
| 10.11 | `AudioSourceState.EndOfStream` | ✅ |
| 10.12 | `AudioSources` ConvertAll | ✅ |

**Score after March 29 fixes: 45 ✅ / 4 ⚠️ / 12 ❌ out of 65 tracked items (including N1–N4).**
> Items fixed today: §3.1/§8.11 (`Start/Stop` protected), N1 (per-channel routing), N2 (video threads always start), §4.2 (`Play` guard), §5.3 (`MediaObjectDisposed`).

---

### 11.2 — Open Items (detail)

#### 11.2-A — `IVideoOutput.PushFrame` blocking contract undocumented (§2.3)

No XML documentation was added to `IVideoOutput.PushFrame`. NDI (`ClockVideo=true`) blocks for ~1 frame interval; SDL3 returns immediately. The mixer's `VideoPresentLoop` does not account for this. The minimum fix is the XML doc block specified in §2.3.

---

#### 11.2-B — `Start/Stop/Pause/Resume` still `public` on `AVMixer` (§3.1 / §8.11) ✅ DONE — March 29, 2026

All four methods changed to `protected` in `AVMixer`. Direct callers in `AVMixerTest` and `NDIReceiveTest` updated to use `StartPlayback` / `StopPlayback` instead. 95/95 unit tests pass.

---

#### 11.2-C — `TickVideoPresentation()` not deleted (§3.2)

Marked `[Obsolete]` but still present in `AVMixer`. The document decision (§9 table) was **SCRAP** — remove entirely. Delete the method.

---

#### 11.2-D — `VideoPresenterSyncPolicyOptions` internal, not wired into `AVMixerConfig` (§3.8)

`VideoPresenterSyncPolicyOptions` and `VideoPresenterSyncPolicy` are both `internal`. Callers cannot override sync-policy parameters (stale threshold, early tolerance, max wait). Two steps required:

1. Make `VideoPresenterSyncPolicyOptions` `public` (rename to `VideoSyncOptions` per §7.5 when doing §7 naming pass).
2. Add to `AVMixerConfig`:

```csharp
/// <summary>
/// Overrides the video presenter sync-policy options used by the mixer's presentation loop.
/// When <see langword="null"/> the mixer uses its built-in defaults (stale = 200 ms, maxWait = 50 ms).
/// </summary>
public VideoSyncOptions? PresenterSyncOptions { get; init; }
```

3. In `StartPlaybackThreads`, use `config.PresenterSyncOptions ?? VideoSyncOptions.Default` when constructing the policy.

---

#### 11.2-E — `_playerGate` deadlock risk in `MediaPlayer` (§4.1)

`MediaPlayer` holds `_playerGate` while calling `RemoveAudioSource` / `AddAudioSource`, which acquire `AVMixer._gate`. Any code that holds `_gate` and calls into `MediaPlayer` will deadlock. Fix: expose `Gate` as `protected` on `AVMixer`, remove `_playerGate` from `MediaPlayer`, and use the shared lock directly:

```csharp
// In AVMixer:
protected Lock Gate => _gate;

// In MediaPlayer — replace _playerGate usages with Gate
```

---

#### 11.2-F — `Play(IMediaItem)` silently starts with no sources (§4.2) ✅ DONE — March 29, 2026

Added early guard at the top of `MediaPlayer.Play`: if `media is not IMediaPlaybackSourceBinding` the method returns `MediaInvalidArgument` immediately. Method body simplified since `binding` is now guaranteed non-null after the guard. Existing test `Play_StartsPlayback` (which asserted the old buggy success) was renamed and updated to assert the correct rejection behaviour.

---

#### 11.2-G — `MediaErrorArea` missing four entries (§5.1)

`MediaErrorArea` only has 7 values. `ResolveArea()` maps PortAudio (4300–4399), OpenGL (4400–4499), SDL3 (4460–4468), and MIDI (900–949) all to `OutputRender` or `Unknown`. Add:

```csharp
public enum MediaErrorArea
{
    // ...existing...
    PortAudio     = 7,
    OpenGL        = 8,
    MIDI          = 9,
    SDL3          = 10,
}
```

Update `ResolveArea()` with range checks for each new area. Note that SDL3 (4460–4468) is a sub-range inside the OutputRender band (4000–4999), so it must be checked **before** the generic 4000–4999 catch:

```csharp
if (code >= 900  && code <= 949)  return MediaErrorArea.MIDI;
if (code >= 4300 && code <= 4399) return MediaErrorArea.PortAudio;
if (code >= 4400 && code <= 4499) return MediaErrorArea.OpenGL;
if (code >= 4460 && code <= 4468) return MediaErrorArea.SDL3;  // checked before OpenGL block
if (code >= 4000 && code <= 4999) return MediaErrorArea.OutputRender;
```

---

#### 11.2-H — `Stop()` on disposed objects returns `Success` (§5.3) ✅ DONE — March 29, 2026

Added `MediaObjectDisposed = 10` to `MediaErrorCode` in the GenericCommon range (0–999). All `_disposed` guards in `AVMixer` (`Start`, `Pause`, `Resume`, `Stop`, `AddAudioSource`, `AddVideoSource`, `StartPlayback`) now return `MediaObjectDisposed` instead of the previous incorrect returns (`MediaInvalidArgument` or `MediaResult.Success`). The disposed check is also split out from the state check in `Pause`/`Resume`/`Stop` so both conditions return their own correct code.

---

#### 11.2-I — NDI `ReadRejected` wrongly mapped to `ConcurrentOperationViolation` (§5.4)

`ErrorCodeRanges.ResolveSharedSemantic` maps `NDIAudioReadRejected` and `NDIVideoReadRejected` to `MediaConcurrentOperationViolation`. Read rejection fires both for concurrent reads **and** when the source is stopped. Add `MediaSourceNotRunning` and split the mapping:

```csharp
// In MediaErrorCode — add in GenericCommon range:
MediaSourceNotRunning = 11,

// In ResolveSharedSemantic:
// NDI*ReadRejected is ambiguous — callers should check the source State
// to distinguish stopped vs concurrent-read. No remapping applied.
// Remove the two NDI lines from the switch.
```

Return `MediaSourceNotRunning` from `NDIAudioSource` / `NDIVideoSource` when `_running == false`, and reserve `NDI*ReadRejected` for the concurrent-read flag only.

---

#### 11.2-J — `VideoFrame.AddRef()` has no safety annotation (§6.1)

Manual ref-counting remains. The minimum viable fix is a prominent XML doc contract on `AddRef()` and `Dispose()`, and a `[MustCallAddRefBeforeEnqueue]` Roslyn annotation attribute:

```csharp
/// <summary>
/// Increments the reference count so this frame stays alive past the original owner's
/// <see cref="Dispose"/> call.
/// <para>
/// <b>Ownership rule:</b> every code path that stores or enqueues a <see cref="VideoFrame"/>
/// across a scope boundary MUST call <c>AddRef()</c> before storing, and MUST call
/// <c>Dispose()</c> when done. Failing to call <c>AddRef()</c> before enqueue is a
/// silent use-after-free bug.
/// </para>
/// </summary>
```

Long-term: migrate plane storage to `IMemoryOwner<byte>` per §6.1 Option 1.

---

#### 11.2-K — `MediaMetadataSnapshot` missing well-known fields; `IDynamicMetadata` missing polling (§6.2)

`MediaMetadataSnapshot` only has `UpdatedAtUtc` and an untyped dictionary. `IDynamicMetadata` has only an event, no polling method. Apply the fix from §6.2:

```csharp
public sealed record MediaMetadataSnapshot
{
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string? Title  { get; init; }
    public string? Artist { get; init; }
    public string? Album  { get; init; }
    public string? Year   { get; init; }
    public ReadOnlyDictionary<string, string> AdditionalMetadata { get; init; } = ...;
}

public interface IDynamicMetadata
{
    MediaMetadataSnapshot? GetMetadata();   // ADD polling
    event EventHandler<MediaMetadataSnapshot>? MetadataChanged;  // rename from MetadataUpdated
}
```

---

#### 11.2-L — Naming cleanup backlog (§7.3, §7.4, §7.5, §7.6, §7.9, §7.10, §8.8)

These are all low-risk rename / delete operations with no behavioural impact:

| Item | Action |
|---|---|
| `AudioSourceErrorEventArgs` + `VideoSourceErrorEventArgs` | Merge into `MediaSourceErrorEventArgs` |
| `VideoOutputTimestampMonotonicMode` | Rename to `VideoTimestampMode`; update `VideoOutputConfig` and `AVMixerConfig` properties |
| `VideoPresenterSyncPolicy` / `VideoPresenterSyncPolicyOptions` | Rename to `VideoSyncPolicy` / `VideoSyncOptions`; make `public` |
| `VideoPresentationHostPolicy` | Rename to `VideoDispatchPolicy`; rename values `DirectPresenterThread` → `DirectThread`, `ManagedBackground` → `BackgroundWorker` |
| `MediaErrorAllocations` int shortcut properties | Delete all ~30 `public static int` properties; keep `ErrorCodeAllocationRange` properties and `All` |
| `AudioVideoMixerRuntimeSnapshot.cs` | Rename file to `AVMixerDiagnostics.cs` |
| `Diagnostics/DebugInfo.cs`, `DebugKeys.cs`, `DebugValueKind.cs` | Delete files |
| `MixerClockTypeRules.cs` | Delete file (already `[Obsolete]`) |

---

#### 11.2-M — `AVMixerConfig.ResamplerFactory` not added (§10.9)

`IAudioResampler` interface exists but `AVMixerConfig` has no hook for injecting a resampler factory. Without it, the mixer silently mixes sources at mismatched sample rates. Add:

```csharp
public sealed class AVMixerConfig
{
    /// <summary>
    /// Optional factory for creating per-source resamplers when a source's sample rate
    /// differs from <see cref="OutputSampleRate"/>.
    /// Parameters: (sourceSampleRate, targetSampleRate) → <see cref="IAudioResampler"/>.
    /// When <see langword="null"/>, the mixer assumes all sources already match <see cref="OutputSampleRate"/>.
    /// </summary>
    public Func<int, int, IAudioResampler>? ResamplerFactory { get; init; }
}
```

`AudioPumpLoop` should check `src.StreamInfo.SampleRate != sampleRate` on first read and call `config.ResamplerFactory` to obtain and cache a resampler per source.

---

### 11.3 — New Findings

#### 🆕 N1 — `AudioRoutingRule.SourceChannel` / `OutputChannel` defined but never consulted — **BUG** ✅ DONE — March 29, 2026

**Implemented:** The routing path in `AudioPumpLoop` was rewritten to do proper per-channel mixing:
- A `Dictionary<Guid, float[]> outputBufs` was added to hold per-output mix buffers.
- For each output, the output channel count is computed as `max(rule.OutputChannel) + 1` across all rules targeting that output.
- All rules for a `(SourceId, OutputId)` pair are now applied (not just the first), each calling `AudioMixUtils.MixChannel(srcChannel → dstChannel, gain)`.
- The resulting per-output buffer is pushed via the identity `PushFrame(in frame)` overload.
- `AudioMixUtils.MixChannel` was added as a scalar per-channel interleaved helper.

```csharp
public readonly record struct AudioRoutingRule(
    Guid SourceId,
    int  SourceChannel,   // ← never read in AudioPumpLoop
    Guid OutputId,
    int  OutputChannel,   // ← never read in AudioPumpLoop
    float Gain = 1.0f);
```

The routing path in `AudioPumpLoop` matches rules only by `r.SourceId == src.Id && r.OutputId == output.Id`, then calls `AudioMixUtils.MixInto(mixBuf, sbuf, fr * sourceChannels, src.Volume * match.Gain)`. `SourceChannel` and `OutputChannel` are entirely ignored.

This is the same class of bug as the original §3.3 (`Gain` never applied), now for channel selectivity. A caller who sets `SourceChannel = 1` expecting only channel 1 of a stereo source to be routed will instead get all channels mixed together.

**Fix options:**
1. **Implement channel-level routing** in `AudioPumpLoop` — apply the source and output channel fields to select specific channels from `sbuf` into `mixBuf`.
2. **Remove the fields** from `AudioRoutingRule` until channel-level routing is properly implemented, to avoid the false API promise.

Priority: **High** (silent data-correctness bug).

---

#### 🆕 N2 — Video pump threads not started for dynamically added video sources — **BUG** ✅ DONE — March 29, 2026

**Implemented:** The `if (GetVideoSourcesSnapshot().Count > 0)` guard was removed from `StartPlaybackThreads`. `VideoDecodeLoop` and `VideoPresentLoop` now always start with `StartPlayback`. Both loops handle an empty source list gracefully (`GetActiveVideoSource()` returns `null` → `Thread.Sleep(2)`).

---

#### 🆕 N3 — `AVMixerConfig.ForPassthrough` uses `Enumerable.Range` (LINQ) — **Minor**

Inconsistent with the codebase's LINQ-removal effort:

```csharp
RouteMap = Enumerable.Range(0, count).ToArray(),
```

Low priority — this is a config-time factory, not a hot path. Replace with an explicit array fill for consistency:

```csharp
var map = new int[count];
for (var i = 0; i < count; i++) map[i] = i;
RouteMap = map,
```

Priority: **Low**.

---

#### 🆕 N4 — `IDynamicMetadata` event name inconsistency — **Minor**

The current `IDynamicMetadata` uses `MetadataUpdated`. The document (§6.2) proposed `MetadataChanged` to match standard .NET "value changed" event naming conventions (e.g. `PropertyChanged`, `StateChanged`, `CollectionChanged`). Resolve when applying §6.2 / §11.2-K.

Priority: **Low** (trivially fixed alongside §11.2-K).

---

### 11.4 — Remaining Fix Plan (post March 29 session)

**Already resolved this session:** N1 (channel routing), N2 (video threads), §11.2-B (protected lifecycle).  
**Remaining open:** 14 ❌ + 3 ⚠️ items organised below by risk and effort.

---

#### Pass 1 — Correctness ✅ COMPLETE

All Pass 1 items were resolved on March 29, 2026.

---

##### P1-1: `Play(IMediaItem)` guard — §11.2-F / §4.2 ✅ DONE

**File:** `Media/S.Media.Core/Playback/MediaPlayer.cs`

Added `if (media is not IMediaPlaybackSourceBinding binding) return (int)MediaErrorCode.MediaInvalidArgument;` as the first check in `Play`. Test updated accordingly.

---

##### P1-2: `MediaObjectDisposed` error code — §11.2-H / §5.3 ✅ DONE

**Files:** `Media/S.Media.Core/Errors/MediaErrorCode.cs`, `Media/S.Media.Core/Mixing/AudioVideoMixer.cs`

Added `MediaObjectDisposed = 10` to the enum. All `_disposed` guards in `AVMixer` updated: `Start`, `Pause`, `Resume`, `Stop`, `AddAudioSource`, `AddVideoSource`, `StartPlayback` now all return `MediaObjectDisposed`. Disposed check separated from state check in `Pause`/`Resume`/`Stop`.

---

#### Pass 2 — API Completeness (6 items, medium risk)

---

##### P2-1: `VideoPresenterSyncPolicyOptions` public + `AVMixerConfig.PresenterSyncOptions` — §11.2-D / §3.8

**Problem:** Callers cannot override sync-policy parameters (stale threshold, early tolerance, max wait). Both types are `internal`.

**Files:**
- `Media/S.Media.Core/Mixing/VideoPresenterSyncPolicyOptions.cs` — change `internal` → `public`  
  *(Rename to `VideoSyncOptions` in the Pass 3 naming sweep — keep old name for now to isolate this change.)*
- `Media/S.Media.Core/Mixing/VideoPresenterSyncPolicy.cs` — change `internal` → `public`
- `Media/S.Media.Core/Mixing/AudioVideoMixerConfig.cs` — add:
  ```csharp
  /// <summary>
  /// Overrides the video presenter sync-policy options.
  /// When <see langword="null"/> (default), the mixer uses built-in defaults (stale = 200 ms, maxWait = 50 ms).
  /// </summary>
  public VideoPresenterSyncPolicyOptions? PresenterSyncOptions { get; init; }
  ```
- `Media/S.Media.Core/Mixing/AudioVideoMixer.cs` — in `VideoPresentLoop`, replace the hard-coded `new VideoPresenterSyncPolicyOptions(...)` with:
  ```csharp
  var policyOptions = config.PresenterSyncOptions ?? new VideoPresenterSyncPolicyOptions(
      StaleFrameDropThreshold: config.OutputStaleFrameThreshold,
      FrameEarlyTolerance: TimeSpan.FromMilliseconds(2),
      MinDelay: TimeSpan.FromMilliseconds(1),
      MaxWait: TimeSpan.FromMilliseconds(50));
  ```

---

##### P2-2: `_playerGate` deadlock — §11.2-E / §4.1

**Problem:** `MediaPlayer` holds `_playerGate` while calling `AddAudioSource`/`RemoveAudioSource`, which acquire `AVMixer._gate`. Potential deadlock if any code holds `_gate` and calls into `MediaPlayer`.

**Files:**
- `Media/S.Media.Core/Mixing/AudioVideoMixer.cs` — expose gate as `protected`:
  ```csharp
  protected Lock Gate => _gate;
  ```
- `Media/S.Media.Core/Playback/MediaPlayer.cs` — remove `_playerGate` field entirely; replace all `lock (_playerGate)` usages with `lock (Gate)`

---

##### P2-3: `MediaErrorArea` new entries + `ResolveArea()` — §11.2-G / §5.1

**Problem:** PortAudio (4300–4399), OpenGL (4400–4499), SDL3 (4460–4468), MIDI (900–949) all map to `OutputRender` or `Unknown`.

**Files:**
- `Media/S.Media.Core/Errors/MediaErrorArea.cs` — add four values:
  ```csharp
  PortAudio = 7,
  OpenGL    = 8,
  MIDI      = 9,
  SDL3      = 10,
  ```
- `Media/S.Media.Core/Errors/ErrorCodeRanges.cs` — add range checks inside `ResolveArea()` **before** the existing 4000–4999 catch-all.  
  ⚠️ SDL3 (4460–4468) is a sub-range of OpenGL (4400–4499), which is itself inside OutputRender (4000–4999). Check order must be: SDL3 → OpenGL → PortAudio → OutputRender:
  ```csharp
  if (code >= 900  && code <= 949)  return MediaErrorArea.MIDI;
  if (code >= 4300 && code <= 4399) return MediaErrorArea.PortAudio;
  if (code >= 4460 && code <= 4468) return MediaErrorArea.SDL3;   // before OpenGL
  if (code >= 4400 && code <= 4499) return MediaErrorArea.OpenGL;
  if (code >= 4000 && code <= 4999) return MediaErrorArea.OutputRender;
  ```

---

##### P2-4: `MediaSourceNotRunning` + NDI read-rejection semantics — §11.2-I / §5.4

**Problem:** `NDIAudioReadRejected` and `NDIVideoReadRejected` are mapped to `MediaConcurrentOperationViolation` in `ResolveSharedSemantic`. Both codes fire when the source is merely stopped, not just during a concurrent read.

**Files:**
- `Media/S.Media.Core/Errors/MediaErrorCode.cs` — add `MediaSourceNotRunning = 11` in GenericCommon range
- `Media/S.Media.Core/Errors/ErrorCodeRanges.cs` — remove the two NDI lines from `ResolveSharedSemantic`. Add a comment explaining that callers should check `source.State` to distinguish stopped vs. concurrent-read
- `Media/S.Media.NDI/` — NDI source implementations: return `MediaSourceNotRunning` when `_running == false`; return `NDI*ReadRejected` only when the concurrent-read guard is set  
  *(This last part is in S.Media.NDI, not S.Media.Core, and can be done in a dedicated NDI pass.)*

---

##### P2-5: XML doc for `IVideoOutput.PushFrame` — §11.2-A / §2.3

**Problem:** No documentation on whether `PushFrame` blocks. NDI blocks for ~1 frame interval; SDL3 does not.

**File:** `Media/S.Media.Core/Video/IVideoOutput.cs` — add XML doc to both `PushFrame` overloads:
```csharp
/// <remarks>
/// <b>Blocking behaviour varies by implementation:</b><br/>
/// - <c>NDIVideoOutput</c> with <c>ClockVideo=true</c>: blocks for ~1 frame interval.<br/>
/// - <c>SDL3VideoView</c>: non-blocking; returns immediately.<br/>
/// Callers scheduling frame delivery (e.g. the mixer's presentation loop) must account for this.
/// </remarks>
```

---

##### P2-6: `AVMixerConfig.ResamplerFactory` — §11.2-M / §10.9

**Problem:** `IAudioResampler` exists but the mixer has no hook to use it. Sources with a different sample rate than `OutputSampleRate` are silently mixed at the wrong rate.

**Files:**
- `Media/S.Media.Core/Mixing/AudioVideoMixerConfig.cs` — add:
  ```csharp
  /// <summary>
  /// Optional factory for per-source resamplers when a source's sample rate differs from
  /// <see cref="OutputSampleRate"/>. Parameters: (sourceSampleRate, targetSampleRate).
  /// When <see langword="null"/>, the mixer assumes all sources already match <see cref="OutputSampleRate"/>.
  /// </summary>
  public Func<int, int, IAudioResampler>? ResamplerFactory { get; init; }
  ```
- `Media/S.Media.Core/Mixing/AudioVideoMixer.cs` — in `AudioPumpLoop` fast path, after determining `sampleRate`, add a per-source resampler cache:
  ```csharp
  var resamplers = new Dictionary<Guid, IAudioResampler>();
  ```
  On first read for each source, if `src.StreamInfo.SampleRate != sampleRate` and `config.ResamplerFactory != null`, create and cache a resampler. Apply it to `tempBuf` before accumulation. Dispose all resamplers when the loop exits.

---

#### Pass 3 — Naming & Cleanup (all low-risk, can be done in one sweep)

---

##### P3-1: Delete `TickVideoPresentation()` — §11.2-C / §3.2

**File:** `Media/S.Media.Core/Mixing/AudioVideoMixer.cs`  
**Action:** Delete the `[Obsolete]` method entirely. One line removed.

---

##### P3-2: `VideoFrame.AddRef()` ownership annotation — §11.2-J / §6.1

**File:** `Media/S.Media.Core/Video/VideoFrame.cs`  
**Action:** Add prominent XML doc block to `AddRef()` and `Dispose()` spelling out the ownership contract. No behaviour change.

---

##### P3-3: `MediaMetadataSnapshot` well-known fields + `IDynamicMetadata.GetMetadata()` — §11.2-K / §6.2

**Files:**
- `Media/S.Media.Core/Media/MediaMetadataSnapshot.cs` — add `Title`, `Artist`, `Album`, `Year` string properties
- `Media/S.Media.Core/Media/IDynamicMetadata.cs` — add `MediaMetadataSnapshot? GetMetadata()` polling method; rename `MetadataUpdated` → `MetadataChanged` (N4)

---

##### P3-4: Naming sweep (all mechanical renames) — §11.2-L / §7.x

All items below can be done in a single coordinated pass. Each rename requires updating all usages across `S.Media.Core`, `S.Media.Core.Tests`, and any library that references these types.

| Current name | New name | Files affected |
|---|---|---|
| `AudioSourceErrorEventArgs` + `VideoSourceErrorEventArgs` | `MediaSourceErrorEventArgs` | `IAVMixer.cs`, `AudioVideoMixer.cs`, all NDI/PA/FFmpeg sources that raise these events |
| `VideoOutputTimestampMonotonicMode` | `VideoTimestampMode` | `VideoOutputConfig.cs`, `AVMixerConfig.cs`, `VideoPresenterSyncPolicy.cs`, all output implementations |
| `VideoPresenterSyncPolicyOptions` | `VideoSyncOptions` | `VideoPresenterSyncPolicy.cs`, `AudioVideoMixer.cs`, `AVMixerConfig.cs` (after P2-1) |
| `VideoPresenterSyncPolicy` | `VideoSyncPolicy` | `AudioVideoMixer.cs` |
| `VideoPresentationHostPolicy` (+ values) | `VideoDispatchPolicy` (`DirectThread`, `BackgroundWorker`) | `AVMixerConfig.cs`, `AudioVideoMixer.cs`, all test/smoke files that use it |
| `ISupportsAdvancedRouting` | `IMixerRouting` (optional — low value, folded interface) | `IAVMixer.cs`, `ISupportsAdvancedRouting.cs`, `AudioVideoMixer.cs`, tests |

**Files to delete:**
- `Media/S.Media.Core/Diagnostics/DebugInfo.cs`
- `Media/S.Media.Core/Diagnostics/DebugKeys.cs`
- `Media/S.Media.Core/Diagnostics/DebugValueKind.cs`
- `Media/S.Media.Core/Mixing/MixerClockTypeRules.cs` (already `[Obsolete]`)

**Files to rename:**
- `Media/S.Media.Core/Mixing/AudioVideoMixerRuntimeSnapshot.cs` → `AVMixerDiagnostics.cs`

**`MediaErrorAllocations.cs` — remove ~30 int shortcuts:**
Delete all `public static int Xxx => (int)MediaErrorCode.Xxx` properties. Keep only the `ErrorCodeAllocationRange` properties and `All`. Update any callers to cast the enum directly.

---

##### P3-5: `AVMixerConfig.ForPassthrough` LINQ — N3

**File:** `Media/S.Media.Core/Mixing/AudioVideoMixerConfig.cs`  
**Action:** Replace `Enumerable.Range(0, count).ToArray()` with an explicit array fill. One-liner change, can be folded into any other edit of this file.

---

### 11.5 — Remaining Open Items Quick-Reference

**Pass 1 complete.** 11 items remain across Pass 2 and Pass 3.

| ID | Title | Pass | Files |
|---|---|---|---|
| P2-1 | `VideoPresenterSyncPolicyOptions` public + `AVMixerConfig.PresenterSyncOptions` | 2 | `VideoPresenterSyncPolicyOptions.cs`, `VideoPresenterSyncPolicy.cs`, `AVMixerConfig.cs`, `AudioVideoMixer.cs` |
| P2-2 | `_playerGate` deadlock | 2 | `AudioVideoMixer.cs`, `MediaPlayer.cs` |
| P2-3 | `MediaErrorArea` entries + `ResolveArea()` | 2 | `MediaErrorArea.cs`, `ErrorCodeRanges.cs` |
| P2-4 | `MediaSourceNotRunning` + NDI semantic | 2 | `MediaErrorCode.cs`, `ErrorCodeRanges.cs`, NDI sources |
| P2-5 | `IVideoOutput.PushFrame` XML doc | 2 | `IVideoOutput.cs` |
| P2-6 | `AVMixerConfig.ResamplerFactory` | 2 | `AVMixerConfig.cs`, `AudioVideoMixer.cs` |
| P3-1 | Delete `TickVideoPresentation()` | 3 | `AudioVideoMixer.cs` |
| P3-2 | `VideoFrame.AddRef()` annotation | 3 | `VideoFrame.cs` |
| P3-3 | `MediaMetadataSnapshot` fields + `IDynamicMetadata` | 3 | `MediaMetadataSnapshot.cs`, `IDynamicMetadata.cs` |
| P3-4 | Full naming sweep | 3 | Many — see §11.4 |
| P3-5 | `ForPassthrough` LINQ | 3 | `AVMixerConfig.cs` |

