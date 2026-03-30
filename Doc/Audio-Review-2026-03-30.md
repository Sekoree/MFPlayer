# Audio Stack Review — March 30, 2026

> **Scope:** `PALib`, `S.Media.PortAudio` (Engine, Input, Output), `S.Media.Core` audio layer
> (`AudioFrame`, `AudioResampler`, `AudioMixUtils`, `AudioRouteMapValidator`, config types,
> `AVMixer` audio path)
>
> **Format:** Each issue has a severity tag — **[HIGH]**, **[MEDIUM]**, **[LOW]**, **[INFO]** —
> followed by the affected file(s), a description of the problem, and the recommended fix.
> Items that duplicate an already-documented finding in `S.Media.PortAudio.md` or `API-Review.md`
> cross-reference the existing entry rather than repeating the full analysis.

---

## Table of Contents

1. [PALib — Native P/Invoke Layer](#1-palib--native-pinvoke-layer)
2. [S.Media.PortAudio.Input — PortAudioInput](#2-smediaportaudioinput--portaudioinput)
3. [S.Media.PortAudio.Output — PortAudioOutput](#3-smediaportaudiooutput--portaudiooutput)
4. [S.Media.PortAudio.Engine — PortAudioEngine](#4-smediaportaudioengine--portaudioengine)
5. [S.Media.Core — Audio Interfaces & Config Types](#5-smediacore--audio-interfaces--config-types)
6. [S.Media.Core — AudioResampler](#6-smediacore--audioresampler)
7. [S.Media.Core — AVMixer Audio Path](#7-smediacore--avmixer-audio-path)
8. [Summary Table](#8-summary-table)

---

## 1. PALib — Native P/Invoke Layer

### Issue A.1 — `Native.cs` static logger is captured before `PALibLogging.Configure()` can run **[LOW]**

**File:** `Audio/PALib/Native.cs`

```csharp
// Current:
private static readonly ILogger Logger = PALibLogging.GetLogger("PALib.Core");
```

`PALibLogging.GetLogger` is called at type-initialization time — the first moment any
`Native.*` method is referenced. If the application calls `PALibLogging.Configure(factory)`
*after* PALib is first used (e.g. after `Pa_Initialize()`), the `Logger` field still holds
the `NullLogger` captured at type initialization. The reconfiguration is silently ignored
for the core `Native` class.

**Fix:** Make `Logger` a property that reads from `PALibLogging` on every access, or change
`PALibLogging.GetLogger` to return a logger that forwards to the current factory (a
`ForwardingLogger` wrapper pattern):

```csharp
// Option A — late-bound property (one virtual dispatch per log-check):
private static ILogger Logger => PALibLogging.GetLogger("PALib.Core");

// Option B — PALibLogging caches a ForwardingLogger that re-reads _factory on each call:
// (no change to Native.cs call sites)
```

Option A is the simplest single-line fix. Option B is more efficient if `GetLogger` is
called frequently (the current `IsEnabled` guard already minimises the cost).

---

### Issue A.2 — `Pa_Sleep` is public **[LOW]**

**File:** `Audio/PALib/Native.cs`

`Pa_Sleep(nint msec)` is exposed as a `public` method. This is a raw PortAudio utility that
callers should never need — `Thread.Sleep` and `Task.Delay` are the correct .NET primitives.
See also: `API-Review.md §11.1.5 / P1.12` (open).

**Fix:**
```csharp
[EditorBrowsable(EditorBrowsableState.Never)]
[Obsolete("Use Thread.Sleep or Task.Delay. Pa_Sleep is a PortAudio-internal utility.")]
public static void Pa_Sleep(nint msec) => Pa_Sleep_Import(msec);
```

Or remove from the public surface entirely (it is `internal` from the caller's perspective
since no managed `S.Media.PortAudio` code calls it).

---

### Issue A.3 — `PaStructs`, `PaEnums`, `PaConstants` are public **[LOW]**

**Files:** `Audio/PALib/Types/Core/PaStructs.cs`, `PaEnums.cs`, `PaConstants.cs`

All low-level PortAudio types are `public`, leaking the P/Invoke ABI into any assembly that
references PALib. Types like `PaStreamParameters`, `PaError`, `PaSampleFormat` etc. are
implementation details of `S.Media.PortAudio` — they should not appear in callers' IntelliSense.
See also: `API-Review.md §11.1.1 / P3.15` (open).

**Fix:** Change visibility to `internal` across all three files. Add a typed managed wrapper
layer in `S.Media.PortAudio` if callers need access to any of these values (e.g. expose
`PortAudioError` as a managed record).

---

### Issue A.4 — `Pa_StopStream` used in all cleanup paths instead of `Pa_AbortStream` **[MEDIUM]**

**Files:** `S.Media.PortAudio/Output/PortAudioOutput.cs`,
`S.Media.PortAudio/Input/PortAudioInput.cs` (`CloseNativeStreamIfOpen`)

`Pa_StopStream` drains the output ring buffer and waits for all pending frames to be
delivered to the hardware before returning — it is a *blocking* call. Both
`PortAudioOutput.CloseNativeStreamIfOpen` and `PortAudioInput.CloseNativeStreamIfOpen` call
it unconditionally, including from `Dispose()`, `Stop()`, `ApplyDeviceChange()`, and the
engine's `Terminate()`. On a hot-unplugged or stalled device, `Pa_StopStream` can stall the
calling thread for tens of milliseconds (or indefinitely if the driver is misbehaving).

`Pa_AbortStream` terminates the stream immediately, discarding buffered frames, and is the
correct choice for any *teardown* scenario.

**Fix:**
```csharp
private void CloseNativeStreamIfOpen()
{
    if (_stream == nint.Zero) { _nativeStreaming = false; return; }
    try
    {
        _ = Native.Pa_AbortStream(_stream);   // immediate, no drain
        _ = Native.Pa_CloseStream(_stream);
    }
    catch { /* best-effort */ }
    finally { _stream = nint.Zero; _nativeStreaming = false; }
}
```

If a graceful drain is ever desired (e.g. deliberate `Stop()` by the user vs emergency
`Dispose()`), add a `bool drain` parameter to `CloseNativeStreamIfOpen` and call
`Pa_StopStream` only when `drain = true`.

---

## 2. S.Media.PortAudio.Input — PortAudioInput

### Issue B.1 — `Start()` silently ignores new config when already running **[LOW]**

**File:** `S.Media.PortAudio/Input/PortAudioInput.cs`

```csharp
if (State == AudioSourceState.Running && _nativeStreaming && _stream != nint.Zero)
    return MediaResult.Success;  // ← new config is discarded
```

Calling `input.Start(new AudioInputConfig { SampleRate = 44_100 })` on an already-running
input at 48 kHz silently returns `Success` while keeping the 48 kHz stream. There is no
indication to the caller that the config was ignored.

**Fix:** Compare the incoming config to `Config` and restart the stream if they differ:

```csharp
if (State == AudioSourceState.Running && _nativeStreaming && _stream != nint.Zero)
{
    if (config.SampleRate      == Config.SampleRate &&
        config.ChannelCount    == Config.ChannelCount &&
        config.FramesPerBuffer == Config.FramesPerBuffer)
        return MediaResult.Success;   // truly identical — no-op is correct
    // Config differs — close and reopen
    CloseNativeStreamIfOpen();
}
Config = config;
```

---

### Issue B.2 — Default-stream path doesn't clamp channel count to device capability **[LOW]**

**File:** `S.Media.PortAudio/Input/PortAudioInput.cs`, `TryStartNativeStream`

When `Device.Id` is not a `pa:N` ID (i.e. the device is a fallback/default), the code
calls `Pa_OpenDefaultStream` with `numInputChannels: Config.ChannelCount` directly. If the
default device only supports 2 channels but `Config.ChannelCount = 8`, PortAudio returns
`paInvalidChannelCount`, which the caller sees as `PortAudioStreamOpenFailed` with no
diagnostic about the real cause.

The `Pa_OpenStream` path (specific device) correctly clamps:
```csharp
var effectiveCh = Math.Clamp(Config.ChannelCount, 1, Math.Max(1, deviceInfo.Value.maxInputChannels));
```

**Fix:** Query `Pa_GetDeviceInfo(Pa_GetDefaultInputDevice())` before `Pa_OpenDefaultStream`
and apply the same clamping:

```csharp
var defaultDeviceIndex = Native.Pa_GetDefaultInputDevice();
var defaultDeviceInfo  = Native.Pa_GetDeviceInfo(defaultDeviceIndex);
var maxCh = defaultDeviceInfo?.maxInputChannels ?? Config.ChannelCount;
var effectiveCh = Math.Clamp(Config.ChannelCount, 1, Math.Max(1, maxCh));
// ... use effectiveCh in Pa_OpenDefaultStream
```

---

### Issue B.3 — `ApplyDeviceChange` leaves `Device` pointing to new device on restart failure **[LOW]**

**File:** `S.Media.PortAudio/Input/PortAudioInput.cs`

```csharp
lock (_gate)
{
    previous = Device;
    Device = newDevice;           // ← assigned unconditionally
    if (_nativeStreaming) { CloseNativeStreamIfOpen(); restartResult = TryStartNativeStream(); }
}
if (restartResult != MediaResult.Success) return (int)MediaErrorCode.PortAudioDeviceSwitchFailed;
```

When `TryStartNativeStream()` fails, the method correctly returns `PortAudioDeviceSwitchFailed`
— but `Device` has already been updated to `newDevice`. The object is now in an inconsistent
state: `Device` says the new device, but the stream is closed. Subsequent calls to `Device`
report the wrong device.

The same issue exists in `PortAudioOutput.ApplyDeviceChange`.

**Fix:** Roll back `Device` on failure:
```csharp
lock (_gate)
{
    previous = Device;
    Device = newDevice;
    if (_nativeStreaming)
    {
        CloseNativeStreamIfOpen();
        restartResult = TryStartNativeStream();
        if (restartResult != MediaResult.Success)
            Device = previous;   // roll back
    }
}
```

---

### Issue B.4 — `ReadSamples` acquires `_gate` lock for a diagnostic `PositionSeconds` update **[LOW]**

**File:** `S.Media.PortAudio/Input/PortAudioInput.cs`

```csharp
lock (_gate) { PositionSeconds += framesRead / (double)config.SampleRate; }
```

This lock acquisition is inside the hot read path — called by the AVMixer audio pump
~47 times/second. `PositionSeconds` is a diagnostic value (elapsed live time); it does not
need consistency with any other locked state.

**Fix:** Use `Interlocked`-based double accumulation:
```csharp
// Replace auto-property with a backing field:
private long _positionSamplesBits;   // reinterpret as double via BitConverter

// In ReadSamples, after the native read:
var prevBits = Interlocked.Read(ref _positionSamplesBits);
var prevSecs = BitConverter.Int64BitsToDouble(prevBits);
Interlocked.Exchange(ref _positionSamplesBits,
    BitConverter.DoubleToInt64Bits(prevSecs + framesRead / (double)config.SampleRate));

public double PositionSeconds =>
    BitConverter.Int64BitsToDouble(Interlocked.Read(ref _positionSamplesBits));
```

Or simply declare `PositionSeconds` as a `double` field with `volatile` semantics via
`Volatile.Write` — the property is informational, not a synchronization primitive.

---

## 3. S.Media.PortAudio.Output — PortAudioOutput

### Issue C.1 — `EnsureResampler` is not thread-safe against concurrent `Start()` **[MEDIUM]**

**File:** `S.Media.PortAudio/Output/PortAudioOutput.cs`

`TryWriteNativeFrame` calls `EnsureResampler` without holding `_gate`. Meanwhile, `Start()`
holds `_gate` and calls `_resampler?.Dispose(); _resampler = null`. If `PushFrame` on
thread A calls `EnsureResampler` at the same time that `Start()` on thread B disposes and
nulls `_resampler`, thread A can obtain a disposed resampler and call `Resample()` on it.

Note: `PushFrame`'s fast-reject checks `State != Running` before entering `TryWriteNativeFrame`,
and `Start()` sets state under `_gate`. But there is no memory fence between the
`State = Running` write in `Start()` and the `_resampler = null` write (they are both under
`_gate`, so sequentially consistent within the lock, but `PushFrame` reads `State` without
the lock via volatile read). A thread that sees `State == Running` can still race with the
`_resampler = null` write.

**Fix:** Access `_resampler` only under `_gate` by capturing a local reference inside the
lock and using it outside:

```csharp
// Alternatively, since EnsureResampler is idempotent, make _resampler volatile or guard it:
private volatile AudioResampler? _resampler;
// ... and in EnsureResampler, use Interlocked.CompareExchange for the assignment.
```

Or, simpler: accept that `_resampler` is owned by the `Start`/`Stop` lifecycle and
document that callers must not call `PushFrame` while `Start`/`Stop` is in progress. This
matches the existing behavioral contract (`PushFrame` rejects `State != Running`) but
requires the architecture to be documented.

---

### Issue C.2 — Write-loop `Thread.Sleep(1)` can add 15 ms latency on Windows **[INFO]**

**File:** `S.Media.PortAudio/Output/PortAudioOutput.cs`, `TryWriteNativeFrame`

```csharp
if (write == PaError.paTimedOut || write == PaError.paOutputUnderflowed)
{
    Thread.Sleep(1);   // ← up to 15 ms on Windows with default timer resolution
    continue;
}
```

On Windows, the default system timer resolution is ~15.6 ms. `Thread.Sleep(1)` frequently
sleeps for 15 ms, which exceeds the entire buffer duration for a 256-frame / 48 kHz stream
(~5.3 ms). This turns one retry into a guaranteed underrun on every underflow event on
Windows without `timeBeginPeriod(1)`.

**Fix:** Use `Thread.SpinWait(50)` for the first few retries (hot spin), then fall back to
`Thread.Sleep(0)` (yield) before accepting `Thread.Sleep(1)`. Alternatively, raising the
platform timer resolution via `timeBeginPeriod(1)` at the engine level is the idiomatic
Windows audio solution.

---

## 4. S.Media.PortAudio.Engine — PortAudioEngine

### Issue D.1 — `RemoveInput`/`RemoveOutput` return `PortAudioDeviceNotFound` for "not tracked" **[LOW]**

**File:** `S.Media.PortAudio/Engine/PortAudioEngine.cs`

```csharp
public int RemoveOutput(IAudioOutput output)
{
    lock (_gate)
    {
        if (!_outputs.Remove(output))
            return (int)MediaErrorCode.PortAudioDeviceNotFound;   // ← wrong code
    }
    output.Dispose();
    return MediaResult.Success;
}
```

`PortAudioDeviceNotFound` means "the device ID does not exist in the enumeration". Here the
meaning is "this output object is not in our tracked list". These are semantically different
— a caller checking for `PortAudioDeviceNotFound` after `RemoveOutput` might incorrectly
think the device is gone rather than that the object was never tracked.

**Fix:** Return `MediaInvalidArgument` or a new `MediaObjectNotFound` error code:
```csharp
return (int)MediaErrorCode.MediaInvalidArgument;
```

---

### Issue D.2 — `TryInitializeNativeRuntimeAndRefreshDevices` reports success when `Pa_Initialize` fails with no preferred API **[MEDIUM]**

**File:** `S.Media.PortAudio/Engine/PortAudioEngine.cs`

```csharp
var init = Native.Pa_Initialize();
if (init != PaError.paNoError)
{
    _nativeInitialized = false;
    return (string.IsNullOrWhiteSpace(Config.PreferredHostApi), nativeFailed: true);
    //      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    //      ok = true when no API preference — Initialize() reports Success!
}
```

When `Pa_Initialize()` fails (e.g. PortAudio library present but initialisation error) and
no `PreferredHostApi` is set, this returns `(ok: true, nativeFailed: true)`. The calling
`Initialize()` method interprets `ok: true` as a success and transitions the engine to
`Initialized` — but native audio is completely non-functional. The engine silently falls
through to phantom fallback devices with no error returned to the caller.

This is distinct from a `DllNotFoundException` (library absent) where the fallback-device
behaviour is intentional. A successful library load followed by a failed `Pa_Initialize` is
a genuine error that should be reported.

**Fix:** Distinguish between "DLL absent" (graceful fallback OK) and "DLL present but init
failed" (return `PortAudioInitializeFailed`):

```csharp
var init = Native.Pa_Initialize();
if (init != PaError.paNoError)
{
    _nativeInitialized = false;
    // Pa_Initialize failed even though the DLL loaded — always an error.
    return (ok: false, nativeFailed: true);
}
```

Graceful fallback to phantom devices should only apply in the `DllNotFoundException` /
`EntryPointNotFoundException` / `TypeInitializationException` catch blocks.

---

### Issue D.3 — `RefreshDevices` returns `PortAudioNotInitialized` when native is unavailable but engine is initialized **[LOW]**

**File:** `S.Media.PortAudio/Engine/PortAudioEngine.cs`

```csharp
if (!_nativeInitialized)
    return (int)MediaErrorCode.PortAudioNotInitialized;
```

The engine IS initialized (fallback mode) — but `RefreshDevices()` returns a code that
implies the engine was never initialized. A caller that checks `IsInitialized` before
calling `RefreshDevices` will be confused by the response.

**Fix:** Return a dedicated code:
```csharp
if (!_nativeInitialized)
    return (int)MediaErrorCode.PortAudioStreamOpenFailed;  // "native unavailable"
    // Or add: MediaErrorCode.PortAudioNativeUnavailable
```

---

## 5. S.Media.Core — Audio Interfaces & Config Types

### Issue E.1 — `AudioFrame.Layout` is declared but never validated **[MEDIUM]**

**File:** `S.Media.Core/Audio/AudioFrame.cs`,
`S.Media.PortAudio/Output/PortAudioOutput.cs`,
`S.Media.Core/Mixing/AVMixer.cs`

`AudioFrame` carries an `AudioFrameLayout` field (`Interleaved` / `Planar`) but:

1. `PortAudioOutput.TryWriteNativeFrame` always processes samples as interleaved.
2. `AVMixer.AudioPumpLoop` always produces interleaved frames and always passes them as
   interleaved to sinks.
3. No consumer ever checks `frame.Layout`.

A planar `AudioFrame` passed to any sink will be **silently misinterpreted** as interleaved,
producing channel-cross-contaminated output with no error.

**Options:**

- **Option A (short-term):** Add a guard at each sink's entry point:
  ```csharp
  if (frame.Layout != AudioFrameLayout.Interleaved)
      return (int)MediaErrorCode.MediaInvalidArgument;
  ```
  This at least surfaces the bug immediately rather than producing corrupted audio.

- **Option B (long-term):** Implement deinterleaving in `TryWriteNativeFrame` and
  `AVMixer.AudioPumpLoop`, or remove `Planar` from `AudioFrameLayout` if the project has
  no planar source.

---

### Issue E.2 — `AVMixerConfig.RouteMap` is a mutable `int[]` **[LOW]**

**File:** `S.Media.Core/Mixing/AVMixerConfig.cs`

```csharp
public int[] RouteMap { get; init; } = [0, 1];
```

`init` prevents reassignment of the property, but the array contents can be mutated after
the config is passed to `StartPlayback`. The audio pump captures `config.RouteMap` at start
and uses it for the entire session — a race-mutation would corrupt the live routing.

**Fix:**
```csharp
public IReadOnlyList<int> RouteMap { get; init; } = [0, 1];
```

The pump already reads it as an index sequence; no call sites need changing if they use
collection initializers. The `config.RouteMap?.Length > 0` check in `AudioPumpLoop` works
on any `IReadOnlyList<int>`.

---

### Issue E.3 — `AudioDeviceInfo` lacks hardware capability fields **[INFO]**

**File:** `S.Media.Core/Audio/AudioDeviceInfo.cs`

```csharp
public readonly record struct AudioDeviceInfo(
    AudioDeviceId Id, string Name,
    string? HostApi = null,
    bool IsDefaultInput = false,
    bool IsDefaultOutput = false,
    bool IsFallback = false);
```

Missing: `MaxInputChannels`, `MaxOutputChannels`, `DefaultSampleRate`. Without them, a
caller cannot validate an `AudioInputConfig` or `AudioOutputConfig` against the chosen
device without making additional engine calls. `PortAudioOutput.TryOpenSelectedDeviceStream`
already queries `Pa_GetDeviceInfo` internally and clamps — but this is invisible to callers.

**Fix:** Add optional nullable fields populated during device enumeration:
```csharp
public readonly record struct AudioDeviceInfo(
    AudioDeviceId Id, string Name,
    string? HostApi = null,
    bool IsDefaultInput = false,
    bool IsDefaultOutput = false,
    bool IsFallback = false,
    int? MaxInputChannels = null,
    int? MaxOutputChannels = null,
    double? DefaultSampleRate = null);
```

Populate from `PaDeviceInfo` in `PortAudioEngine.RefreshNativeDevices`.

---

### Issue E.4 — `IAudioInput` lacks a `ReadSamples` declaration **[INFO]**

**File:** `S.Media.Core/Audio/IAudioInput.cs`, `IAudioSource.cs`

`ReadSamples` is defined on `IAudioSource` — `IAudioInput` extends `IAudioSource` so it
inherits `ReadSamples`. However, `IAudioInput`'s XML doc does not mention `ReadSamples`
at all, and the method carries no `AudioInputConfig` context. A reader of `IAudioInput` alone
cannot tell what the span layout of the returned data will be (channel count, sample rate)
without reading `IAudioSource`. A short cross-reference note would help.

**Fix:** Add a `<remarks>` section to `IAudioInput` noting that `ReadSamples` returns
interleaved float32 at `Config.SampleRate` with `Config.ChannelCount` channels.

---

## 6. S.Media.Core — AudioResampler

### Issue F.1 — `ResampleLinear` has chunk-boundary discontinuities **[LOW]**

**File:** `S.Media.Core/Audio/AudioResampler.cs`

The sinc resampler correctly maintains a ring buffer of the last `SincKernelHalfSize` frames
from the previous call, enabling seamless interpolation across chunk boundaries. The linear
resampler only carries over `_fractionalPosition` — it does not store the last sample of
chunk N for interpolation with the first sample of chunk N+1.

When upsampling (ratio < 1), the interpolation formula `s0 + t*(s1 - s0)` uses `s1 =
source[1]` of the *current* chunk at the start of the chunk — but if the fractional position
carry-over means the first output sample should be interpolated between the *last* sample of
the previous chunk and the *first* sample of the current chunk, `s0` is wrong (it reads the
first sample of the new chunk instead of the last sample of the previous chunk).

This produces a very small click or DC step at every chunk boundary when upsampling with
linear mode.

**Fix:** Store the last `sourceChannelCount` samples of each call in a per-channel
`float[] _linearHistory` buffer (just one frame × channel count), and use them as `s0` when
`_fractionalPosition < 0` at the start of the next call.

---

### Issue F.2 — `ResampleSinc` normalizes weights per output sample — expensive **[LOW]**

**File:** `S.Media.Core/Audio/AudioResampler.cs`

```csharp
destination[dstBase + ch] = weightSum > 0 ? (float)(sum / weightSum) : 0f;
```

The weight-sum normalization is computed for every output sample × every channel. For a
standard windowed-sinc filter with fixed β and kernel size, the kernel weights for a given
sub-sample offset `t` are fully deterministic and independent of the signal. Pre-computing
a table of kernel weights for, e.g., 256 sub-sample phases would reduce the inner loop to
pure multiply-add with no division.

For the current use case (48 kHz → 44.1 kHz conversion of a 1024-frame batch with stereo
audio), the cost is ~2 × 1024 × 64 kernel evaluations = 131 072 `WindowedSinc` calls per
batch — each involving a `Math.Sin`, `Math.Sqrt`, and Bessel series. This is the single
hottest code path in the resampler.

**Fix (medium effort):** Pre-compute a `float[256, SincKernelSize]` table of kernel weights
indexed by sub-sample phase (quantized to 256 levels). The `WindowedSinc` / `KaiserWindow`
/ `BesselI0` calls drop to zero in the hot path.

**Fix (low effort, 2× speedup):** At minimum, cache the `BesselI0(KaiserBeta)` denominator
— it is constant and currently recomputed on every `KaiserWindow` call:

```csharp
// In the class (constructor or static):
private static readonly double BesselI0Beta = BesselI0(KaiserBeta);

// In KaiserWindow:
return BesselI0(arg) / BesselI0Beta;   // denominator no longer recomputed
```

---

### Issue F.3 — `AudioResampler` constructor throws rather than returning error codes **[LOW]**

**File:** `S.Media.Core/Audio/AudioResampler.cs`

```csharp
public AudioResampler(int sourceSampleRate, ...)
{
    if (sourceSampleRate <= 0) throw new ArgumentOutOfRangeException(...);
    ...
}
```

Per the project's error-handling convention (all public factories return `int` codes), the
constructor should be replaced with a static factory:

```csharp
public static int Create(int sourceSampleRate, ..., out AudioResampler? resampler)
{
    resampler = null;
    if (sourceSampleRate <= 0) return (int)MediaErrorCode.MediaInvalidArgument;
    ...
    resampler = new AudioResampler(...);
    return MediaResult.Success;
}
// Make constructor internal.
```

Currently `PortAudioOutput.EnsureResampler` wraps the constructor in `try/catch`, which
masks the validation error silently. `AVMixerConfig.ResamplerFactory` delegates also call
the constructor through user-supplied factories — they have no standardised way to signal
failure other than exceptions.

---

### Issue F.4 — `Dispose()` is a no-op beyond setting a flag **[INFO]**

**File:** `S.Media.Core/Audio/AudioResampler.cs`

```csharp
public void Dispose() { _disposed = true; }
```

The ring buffer (`_ringBuffer = new float[SincKernelSize * targetChannelCount]`) and the
`_fractionalPosition` / `_ringWritePos` state fields are never cleared. If the GC collects
the resampler while it is still referenced by a `Dictionary` entry (e.g. in `AudioPumpLoop`
or `PortAudioOutput._resampler`), there is no problem — but if `Dispose` is meant to signal
"this object is finished", zeroing the ring buffer would make any use-after-dispose
detectable. This is a low-severity cosmetic issue.

---

## 7. S.Media.Core — AVMixer Audio Path

### Issue G.1 — `GetAudioOutputsSnapshot()` allocates on every audio pump iteration **[HIGH]**

**File:** `S.Media.Core/Mixing/AVMixer.cs`, `AudioPumpLoop`

```csharp
// Called inside the tight audio pump loop, ~47 times/second at 48kHz/1024 frames:
var outputs = GetAudioOutputsSnapshot();   // allocates List<IAudioSink> + lock every call

private List<IAudioSink> GetAudioOutputsSnapshot()
{ lock (_gate) { return [.. _audioOutputs]; } }
```

The video output path was already fixed (issue N7) with a dirty-flag + cached array pattern
to avoid per-frame allocations. The audio output list is not. This produces at minimum one
`List<IAudioSink>` heap allocation per audio batch (~47/sec), which amounts to ~47 KB of
GC pressure per second (assuming a 4-output list).

**Fix:** Apply exactly the same caching pattern used for `_videoOutputsCache`:

```csharp
// Add fields:
private volatile bool _audioOutputsNeedsUpdate = true;
private IAudioSink[] _audioOutputsCache = [];

// In AddAudioOutput / RemoveAudioOutput:
_audioOutputsNeedsUpdate = true;

// At the top of AudioPumpLoop (before the while loop):
IAudioSink[] audioOutputsCache = [];

// Inside the loop, before using outputs:
if (_audioOutputsNeedsUpdate)
{
    lock (_gate) { audioOutputsCache = [.. _audioOutputs]; }
    _audioOutputsNeedsUpdate = false;
}
```

This eliminates the per-frame allocation and lock on the hot path.

---

### Issue G.2 — Audio routing rules re-read under `_gate` on every pump iteration **[HIGH]**

**File:** `S.Media.Core/Mixing/AVMixer.cs`, `AudioPumpLoop`

```csharp
// Inside the tight pump loop, every iteration:
AudioRoutingRule[]? rules = null;
lock (_gate)
{
    if (_audioRoutingRules.Count > 0)
        rules = [.. _audioRoutingRules];   // allocation + lock every frame
}
```

This is the same antipattern that was fixed for video routing rules (`_videoRoutingRulesCache`
/ `_videoRoutingRulesNeedsUpdate`). The audio routing rule list snapshot is taken under the
`_gate` lock on every pump cycle — ~47 times/second. Since routing rules rarely change
(typically set once at `StartPlayback`), this is pure overhead.

**Fix:**

```csharp
// Add fields (mirror the video counterparts):
private volatile bool _audioRoutingRulesNeedsUpdate = true;
private AudioRoutingRule[] _audioRoutingRulesCache = [];

// In AddAudioRoutingRule / RemoveAudioRoutingRule / ClearAudioRoutingRules:
_audioRoutingRulesNeedsUpdate = true;

// In AudioPumpLoop (local cache, refreshed via dirty flag):
AudioRoutingRule[]? rulesCache = null;
// Inside the loop:
if (_audioRoutingRulesNeedsUpdate)
{
    lock (_gate)
    {
        _audioRoutingRulesCache = _audioRoutingRules.Count > 0
            ? [.. _audioRoutingRules]
            : [];
    }
    _audioRoutingRulesNeedsUpdate = false;
}
var rules = _audioRoutingRulesCache.Length > 0 ? _audioRoutingRulesCache : (AudioRoutingRule[]?)null;
```

---

### Issue G.3 — `resampledBuf` in `AudioPumpLoop` uses heap allocation **[MEDIUM]**

**File:** `S.Media.Core/Mixing/AVMixer.cs`, `AudioPumpLoop`

```csharp
if (resampledBuf is null || resampledBuf.Length < needed)
    resampledBuf = new float[needed];   // heap allocation when estimate grows
```

`PortAudioOutput.TryWriteNativeFrame` already uses `ArrayPool<float>.Shared` for its
intermediate buffers. The mixer's audio pump should do the same for `resampledBuf` to
avoid LOH allocation when upsampling to large batches, and to benefit from the pool across
iterations.

**Fix:**
```csharp
// Replace the 'resampledBuf' local with ArrayPool rent/return per batch:
float[]? resampledRented = null;
try
{
    var needed = resampler.EstimateOutputFrameCount(fr) * sourceChannels;
    resampledRented = ArrayPool<float>.Shared.Rent(needed);
    var outFrames = resampler.Resample(source, fr, resampledRented.AsSpan(0, needed));
    // ... use resampledRented ...
}
finally
{
    if (resampledRented != null)
        ArrayPool<float>.Shared.Return(resampledRented, clearArray: false);
}
```

---

### Issue G.4 — `sourceBufs` / `outputBufs` dictionaries in routing path accumulate stale entries **[LOW]**

**File:** `S.Media.Core/Mixing/AVMixer.cs`, `AudioPumpLoop` routing path

```csharp
var sourceBufs   = new Dictionary<Guid, float[]>();
var outputBufs   = new Dictionary<Guid, float[]>();
```

These dictionaries are sized and populated as sources/outputs are encountered. When a source
or output is removed via `RemoveAudioSource` / `RemoveAudioOutput`, its entry in these
dictionaries is never cleaned up. In a long-running session with many dynamic add/remove
cycles, these dictionaries grow without bound.

**Fix:** After refreshing the audio sources snapshot (`if (_audioSourcesNeedsUpdate)`),
remove any `sourceBufs` entries whose `Guid` is no longer in `srcs`:

```csharp
if (_audioSourcesNeedsUpdate)
{
    srcs = GetAudioSourcesSnapshot().ToArray();
    _audioSourcesNeedsUpdate = false;

    // Prune stale source buffers.
    var activeIds = new HashSet<Guid>(srcs.Length);
    foreach (var (src, _) in srcs) activeIds.Add(src.Id);
    foreach (var key in sourceBufs.Keys.Where(k => !activeIds.Contains(k)).ToList())
        sourceBufs.Remove(key);

    // Same for resamplers.
    foreach (var key in resamplers.Keys.Where(k => !activeIds.Contains(k)).ToList())
    { resamplers[key].Dispose(); resamplers.Remove(key); }
}
```

Apply the same cleanup for `outputBufs` when the audio outputs snapshot is refreshed (see
Issue G.1).

---

### Issue G.5 — `AudioPumpLoop` sampleRate initialization doesn't handle multi-source case **[LOW]**

**File:** `S.Media.Core/Mixing/AVMixer.cs`, `AudioPumpLoop`

```csharp
else if (srcs.Length > 0 && srcs[0].Source.StreamInfo.SampleRate.GetValueOrDefault(0) > 0)
    sampleRate = srcs[0].Source.StreamInfo.SampleRate!.Value;
else
    sampleRate = 48_000;
```

The sample rate is resolved from `srcs[0]` only. If a second source with a different sample
rate is added (and no `ResamplerFactory` is set), it will be mixed at the wrong rate with no
warning. The resampler is only engaged when `config.ResamplerFactory != null` — so in the
common case where no resampler is configured, sources with mismatched rates are silently
mixed incorrectly.

**Fix (minimum):** Log a warning when a source's `StreamInfo.SampleRate` differs from
`sampleRate` and `config.ResamplerFactory == null`:

```csharp
var srcRate = src.StreamInfo.SampleRate.GetValueOrDefault(0);
if (srcRate > 0 && srcRate != sampleRate && config.ResamplerFactory == null)
    // warn: "Source {src.Id} sample rate {srcRate} differs from mix rate {sampleRate}. No resampler configured."
```

---

### Issue G.6 — `AudioSourceState` inconsistency: mixer checks `State == AudioSourceState.Running` but never checks `EndOfStream` **[LOW]**

**File:** `S.Media.Core/Mixing/AVMixer.cs`, `AudioPumpLoop`

```csharp
foreach (var (src, offset) in srcs)
{
    if (src.State != AudioSourceState.Running) continue;
    ...
}
```

`AudioSourceState` has (at minimum) `Stopped` and `Running`. If `EndOfStream` was added to
`AudioSourceState` (analogous to the `VideoSourceState.EndOfStream` added as N12), the mixer
already handles the video case by sleeping 50 ms — but the audio path only skips non-running
sources without any distinction. When all audio sources reach `EndOfStream`, the mixer will
hit the `!anyRead` path and spin at 1 ms sleep indefinitely instead of stopping playback or
raising an event.

**Fix:** Add end-of-stream detection to the audio pump:
```csharp
// After the source loop:
if (!anyRead && srcs.All(s => s.Source.State == AudioSourceState.EndOfStream))
{
    // All audio sources exhausted — stop playback or raise an event.
    _ = StopPlayback();
    break;
}
```

---

## 8. Summary Table

| ID | Severity | Component | Summary |
|----|----------|-----------|---------|
| G.1 | **HIGH** | AVMixer | `GetAudioOutputsSnapshot()` allocates on every audio pump iteration |
| G.2 | **HIGH** | AVMixer | Audio routing rules re-read under lock on every pump iteration |
| A.4 | **MEDIUM** | PALib / PortAudio{Input,Output} | `Pa_StopStream` (blocking/draining) used in all cleanup paths; should be `Pa_AbortStream` |
| C.1 | **MEDIUM** | PortAudioOutput | `EnsureResampler` not thread-safe against concurrent `Start()` |
| D.2 | **MEDIUM** | PortAudioEngine | `Pa_Initialize` failure + no preferred API silently reports `Initialize()` success |
| E.1 | **MEDIUM** | AudioFrame / all sinks | `AudioFrame.Layout` (Interleaved/Planar) declared but never validated |
| G.3 | **MEDIUM** | AVMixer | `resampledBuf` uses heap allocation; should use `ArrayPool<float>` |
| A.1 | LOW | PALib | Static `Logger` in `Native.cs` is captured before `Configure()` runs |
| A.2 | LOW | PALib | `Pa_Sleep` is public |
| A.3 | LOW | PALib | `PaStructs`, `PaEnums`, `PaConstants` are public (API-Review P3.15) |
| B.1 | LOW | PortAudioInput | `Start()` silently ignores new config when already running |
| B.2 | LOW | PortAudioInput | `Pa_OpenDefaultStream` path doesn't clamp channel count to device capability |
| B.3 | LOW | PortAudioInput + Output | `Device` property not rolled back on `ApplyDeviceChange` restart failure |
| B.4 | LOW | PortAudioInput | `ReadSamples` acquires `_gate` lock to update diagnostic `PositionSeconds` |
| D.1 | LOW | PortAudioEngine | `RemoveInput`/`RemoveOutput` return `PortAudioDeviceNotFound` for wrong reason |
| D.3 | LOW | PortAudioEngine | `RefreshDevices` returns `PortAudioNotInitialized` when engine IS initialized (fallback mode) |
| E.2 | LOW | AVMixerConfig | `RouteMap` is mutable `int[]`; should be `IReadOnlyList<int>` |
| F.1 | LOW | AudioResampler | `ResampleLinear` has chunk-boundary discontinuity (no ring-buffer carry-over) |
| F.2 | LOW | AudioResampler | `BesselI0(KaiserBeta)` recomputed on every kernel evaluation; should be cached |
| F.3 | LOW | AudioResampler | Constructor throws instead of returning error codes |
| G.4 | LOW | AVMixer | `sourceBufs`/`outputBufs` routing path dictionaries accumulate stale entries |
| G.5 | LOW | AVMixer | Sample rate resolved from first source only; no warning for mismatched multi-source rates |
| G.6 | LOW | AVMixer | Audio pump doesn't handle `EndOfStream` for audio sources (vs. video which does) |
| C.2 | INFO | PortAudioOutput | `Thread.Sleep(1)` on underflow can sleep 15 ms on Windows |
| E.3 | INFO | AudioDeviceInfo | Missing `MaxInputChannels`, `MaxOutputChannels`, `DefaultSampleRate` capability fields |
| E.4 | INFO | IAudioInput | No XML doc cross-reference linking `IAudioInput` to `ReadSamples` semantics |
| F.4 | INFO | AudioResampler | `Dispose()` sets flag only; ring buffer not cleared |

---

### Recommended Fix Order

**Immediate (correctness / data-loss risk):**
1. G.1 + G.2 — audio output list and routing-rule cache (mirrors the existing N7/video fix; one change, big impact on GC pressure)
2. D.2 — silent `Initialize()` success on `Pa_Initialize` failure (misleads callers about audio availability)
3. E.1 — `AudioFrame.Layout` guard at sink entry points (prevents silent audio corruption)
4. A.4 — `Pa_AbortStream` in cleanup paths (prevents stalls on hot-unplug)

**Short-term (reliability):**
5. C.1 — `EnsureResampler` thread-safety
6. B.3 — `Device` rollback on restart failure
7. G.3 — `ArrayPool` for `resampledBuf`
8. G.4 + G.6 — stale routing dicts + EndOfStream audio handling

**Housekeeping:**
9. B.1, B.2, B.4, D.1, D.3, E.2, F.1–F.4, G.5 — all LOW/INFO items as bandwidth allows.

