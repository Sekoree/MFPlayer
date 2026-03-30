# S.Media.PortAudio — Issues & Fix Guide

> **Scope:** `S.Media.PortAudio` — `PortAudioEngine`, `PortAudioOutput`, `PortAudioInput`, config types
> **Cross-references:** See `API-Review.md` §5 and `PALib.md` for the underlying native wrapper issues.

---

## Table of Contents

1. [Engine Lifecycle & Device Enumeration](#1-engine-lifecycle--device-enumeration)
2. [Output Reliability](#2-output-reliability)
3. [API & Factory Consistency](#3-api--factory-consistency)
4. [Configuration Lifecycle](#4-configuration-lifecycle)
5. [PortAudioInput — Missing Interface & Mixer Integration](#5-portaudioinput--missing-interface--mixer-integration)
6. [Implementation Quality & Robustness Issues](#6-implementation-quality--robustness-issues)
7. [Summary of Recommended Changes](#7-summary-of-recommended-changes-by-priority)

---

## 1. Engine Lifecycle & Device Enumeration

### Issue 1.1 — Phantom devices are created before `Initialize()`

`PortAudioEngine` constructor creates two fake output devices (`"Default Output"` / `"Monitor Output"`) and a fake `"fallback"` host API before `Initialize()` is called. Callers enumerating devices on an uninitialized engine see phantom entries that do not correspond to real hardware.

**Why this happens:** The fallback devices were introduced as a graceful-degradation sentinel — if PortAudio fails to initialize, at least a no-op device exists. But the mechanism is always-on rather than opt-in.

**Fix:**

```csharp
public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
{
    if (State == AudioEngineState.Uninitialized)
        return Array.Empty<AudioDeviceInfo>();

    return _outputDevices;
}

public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
{
    if (State == AudioEngineState.Uninitialized)
        return Array.Empty<AudioDeviceInfo>();

    return _inputDevices;
}
```

Remove the phantom device construction from the constructor entirely. If a "default output" sentinel is needed for graceful degradation, add an explicit opt-in:

```csharp
// Only present after Initialize() if real enumeration returned nothing:
public bool UseFallbackDeviceOnEnumerationFailure { get; init; } = false;
```

---

## 2. Output Reliability

### Issue 2.1 — `PortAudioOutput` blocks indefinitely on `Pa_WriteStream` retries

The write loop in `TryWriteNativeFrame` retries on `paOutputUnderflowed` or `paTimedOut` with a 1 ms sleep between attempts. There is no overall timeout. If the native stream is stuck (e.g. device hot-unplugged mid-write), the audio pump thread blocks indefinitely — the mixer's audio pump never progresses, eventually hanging the whole pipeline.

**Fix:** Add a configurable write timeout to `AudioEngineConfig`:

```csharp
public sealed class AudioEngineConfig
{
    // ADD:
    /// <summary>
    /// Maximum total time to retry a single <c>Pa_WriteStream</c> call before giving up.
    /// Default: 500 ms. Set to <see cref="TimeSpan.Zero"/> to retry indefinitely (not recommended).
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}
```

Apply in the write loop:

```csharp
private int TryWriteNativeFrame(nint buffer, nuint frameCount)
{
    var deadline = Stopwatch.GetTimestamp()
        + (long)(_config.WriteTimeout.TotalSeconds * Stopwatch.Frequency);

    while (true)
    {
        var err = Native.Pa_WriteStream(_stream, buffer, frameCount);
        if (err == PaError.paNoError) return MediaResult.Success;

        bool isRetryable = err is PaError.paOutputUnderflowed or PaError.paTimedOut;
        bool timedOut = _config.WriteTimeout > TimeSpan.Zero
            && Stopwatch.GetTimestamp() >= deadline;

        if (!isRetryable || timedOut)
            return (int)MediaErrorCode.PortAudioPushFailed;

        Thread.Sleep(1);
    }
}
```

**Consideration:** The timeout value should be tuned to at least 2× the audio buffer duration (e.g. for a 256-frame buffer at 48 kHz ≈ 5 ms, a 50–500 ms timeout is reasonable). Log the timeout at Warning level so it surfaces in production diagnostics.

---

## 3. API & Factory Consistency

### Issue 3.1 — `IAudioEngine.CreateOutput` uses `out IAudioOutput?` but `NDIEngine.CreateOutput` uses `out NDIVideoOutput?`

The `out` parameter type is inconsistent: one returns the interface type, the other returns a concrete type. This complicates generic engine management code.

**Fix:** Standardise the `out` parameter to always return the interface type:

```csharp
// NDIEngine — change:
public int CreateOutput(NDIOutputOptions options, out IAudioSink? output)
// instead of: out NDIVideoOutput? output
```

Or, if concrete type access is needed:

```csharp
// Provide both:
public int CreateOutput(NDIOutputOptions options, out NDIVideoOutput? output) { ... }

// Adapter for interface-based code:
public int CreateOutput(NDIOutputOptions options, out IAudioSink? output)
{
    var r = CreateOutput(options, out NDIVideoOutput? concrete);
    output = concrete;
    return r;
}
```

**Consideration:** Choose one policy and apply it to all engines. The recommended policy is: factory `out` parameters always use the most specific useful type (`IAudioOutput` for `PortAudioEngine`, `NDIVideoOutput` for `NDIEngine`), and document the return type clearly.

---

### Issue 3.2 — No `IMediaEngine` interface unifying all engines

`PortAudioEngine` implements `IAudioEngine`. `NDIEngine` has no interface. This prevents dependency injection and consistent lifecycle management.

**Fix:** Define in `S.Media.Core`:

```csharp
public interface IMediaEngine : IDisposable
{
    bool IsInitialized { get; }
    int Terminate();
}
```

Have all engines implement it: `PortAudioEngine : IAudioEngine, IMediaEngine`, `NDIEngine : IMediaEngine`, `MIDIEngine : IMediaEngine`.

---

## 4. Configuration Lifecycle

### Issue 4.1 — `AudioEngineConfig` is snapshot-copied into each `PortAudioOutput`

`PortAudioEngine` stores `AudioEngineConfig` as `Config`. When it calls `CreateOutput(...)`, the config is passed by value into `PortAudioOutput._config`. If the engine is re-initialized (or config conceptually "updated"), existing outputs retain stale values.

**Fix option A — Immutable snapshot (document it):**

```csharp
// In PortAudioOutput XML doc:
/// <remarks>
/// This output captures a snapshot of <see cref="AudioEngineConfig"/> at creation time.
/// Re-initializing the engine does not update the config of existing outputs.
/// Recreate outputs after re-initialization if config changes are needed.
/// </remarks>
```

**Fix option B — Live reference via the engine:**

```csharp
// PortAudioOutput holds a reference to the engine instead of a config copy:
private readonly PortAudioEngine _engine;
private AudioEngineConfig Config => _engine.Config;
```

Option A is simpler and safe for most use cases. Option B is appropriate if runtime config changes (e.g. adjusting buffer size on the fly) are required.

---

### Consideration — `PortAudioLibraryResolver` must be called before first use

`S.Media.PortAudio` depends on `PALib`, which requires `PortAudioLibraryResolver.Register()` to be called before any native call is made. If the resolver is not called, the OS default loader is used — which may find an incompatible library version or fail silently.

`S.Media.PortAudio` should call `PortAudioLibraryResolver.Register()` from its own `[ModuleInitializer]`:

```csharp
// In S.Media.PortAudio (assembly-level):
internal static class PortAudioMediaModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => PALib.Runtime.PortAudioLibraryResolver.Register();
}
```

This ensures registration happens automatically when `S.Media.PortAudio` is first loaded, without requiring any explicit call from consumer code. See `PALib.md` §1.4 for details.

---

## 5. PortAudioInput — Missing Interface & Mixer Integration

### Issue 5.1 — `PortAudioInput` implements `IAudioSource` but there is no `IAudioInput` interface

`PortAudioInput` is a microphone/line-in capture source. It correctly implements `IAudioSource`, so it can be used in the mixer pipeline. However:
- There is no `IAudioInput` interface to abstract over different capture backends.
- The input-specific configuration (`AudioInputConfig`) lives in `S.Media.PortAudio.Input` — not in `S.Media.Core` — so no other package can refer to it without a direct dependency on `S.Media.PortAudio`.
- `IAudioEngine` has `GetInputDevices()` and `GetDefaultInputDevice()` but no `CreateInput(...)` factory — inputs must be created directly as `new PortAudioInput()`, bypassing the engine entirely.

**Fix — add `IAudioInput` to `S.Media.Core`:**

```csharp
// S.Media.Core/Audio/IAudioInput.cs
public interface IAudioInput : IAudioSource
{
    /// <summary>Current capture configuration.</summary>
    AudioInputConfig Config { get; }

    /// <summary>Starts capture with the given configuration.</summary>
    int Start(AudioInputConfig config);
}
```

Move `AudioInputConfig` (currently `S.Media.PortAudio.Input.AudioInputConfig`) to `S.Media.Core.Audio`:

```csharp
// S.Media.Core/Audio/AudioInputConfig.cs
public sealed record AudioInputConfig
{
    public int SampleRate    { get; init; } = 48_000;
    public int ChannelCount  { get; init; } = 2;
    public AudioDeviceId? Device { get; init; }   // null = default input device
}
```

**Fix — add `CreateInput` to `IAudioEngine`:**

```csharp
public interface IAudioEngine : IDisposable
{
    // ...existing...

    int CreateInput(AudioDeviceId deviceId, out IAudioInput? input);
    int CreateInputByName(string deviceName, out IAudioInput? input);
    IReadOnlyList<IAudioInput> Inputs { get; }
}
```

This makes microphone input a first-class part of the engine and mixer pipeline, consistent with how audio outputs are managed.

---

### Issue 5.2 — `PortAudioInput` has no device-selection API

`PortAudioInput` captures from the system default input device only. There is no way to specify a device (by `AudioDeviceId`, name, or index) equivalent to `IAudioOutput.SetOutputDevice`. Switching devices requires disposing and re-creating the input.

**Fix:** Mirror the output device-selection pattern:

```csharp
public sealed class PortAudioInput : IAudioInput
{
    // ADD:
    public int SetInputDevice(AudioDeviceId deviceId) { ... }
    public int SetInputDeviceByName(string deviceName) { ... }
    public int SetInputDeviceByIndex(int deviceIndex) { ... }

    public event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}
```

---

### Issue 5.3 — No hot-plug detection for input devices

When a USB microphone is unplugged, `PortAudioInput` will silently fail on the next `ReadSamples` call, returning a PortAudio error code. There is no `AudioDeviceChanged` event equivalent for inputs (only `IAudioOutput` has it).

**Fix:** Add disconnect detection in the PortAudio stream callback (`paDeviceUnavailable` / `paAbort`) and surface it via `AudioDeviceChanged` on `IAudioInput`.

This mirrors the fix described for output in `PALib.md` §5.1 and follows the same reconnect + fallback pattern.

---

### Consideration — `PortAudioInput` as a Mixer Source

`PortAudioInput` correctly implements `IAudioSource` via `Start()`, `Stop()`, `ReadSamples()`. This means it can already be added to an `AudioVideoMixer` as a live audio source — for example, for a live monitoring / passthrough use case:

```csharp
using var mic = new PortAudioInput();
mic.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 2 });

mixer.AddAudioSource(mic);
mixer.StartPlayback(AudioVideoMixerConfig.ForStereo());
```

However, `PortAudioInput.DurationSeconds` returns `double.NaN` and `Seek()` returns a "not seekable" error — which is correct but callers should guard against it. Document this on `IAudioSource.Seek`:

```csharp
/// <summary>
/// Seeks to the specified position.
/// Returns <see cref="MediaErrorCode.MediaSourceNonSeekable"/> for live capture sources
/// such as <c>PortAudioInput</c>.
/// </summary>
int Seek(double positionSeconds);
```

---

## 6. Implementation Quality & Robustness Issues

### Issue 6.1 — `PortAudioOutput.TryWriteNativeFrame` blocks indefinitely on stream stall

The write loop in `PortAudioOutput.TryWriteNativeFrame` (lines 448–478) retries on `paTimedOut` or `paOutputUnderflowed` with only a 1 ms `Thread.Sleep`. There is no overall timeout. If the hardware device is hot-unplugged mid-write or the native stream becomes stuck, the mixer's audio pump thread blocks indefinitely, hanging the entire pipeline.

**Current code (problematic):**
```csharp
while (framesRemaining > 0)
{
    if (_disposed || State != AudioOutputState.Running || _stream == nint.Zero)
    {
        return (int)MediaErrorCode.PortAudioPushFailed;
    }

    var writableFrames = Math.Min(framesRemaining, Math.Max(1, _nativeFramesPerBuffer));
    var sampleOffset = frameOffset * _nativeChannelCount;
    var writePtr = ptr + sampleOffset;
    var write = Native.Pa_WriteStream(_stream, (nint)writePtr, (nuint)writableFrames);
    
    if (write == PaError.paNoError) { /* success */ }
    if (write == PaError.paTimedOut || write == PaError.paOutputUnderflowed)
    {
        Thread.Sleep(1);  // ← No timeout, will spin forever
        continue;
    }
    // ...error cases
}
```

**Fix:** Add a configurable timeout to `AudioEngineConfig`:

```csharp
public sealed class AudioEngineConfig
{
    // ADD:
    /// <summary>
    /// Maximum time to retry a single <c>Pa_WriteStream</c> call on transient backpressure.
    /// Default: 500 ms. Set to <see cref="TimeSpan.Zero"/> to retry indefinitely (not recommended).
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}
```

Apply in the write loop:

```csharp
private int TryWriteNativeFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap, int sourceChannelCount)
{
    // ... channel routing/resampling logic ...
    
    fixed (float* ptr = rented)
    {
        var framesRemaining = effectiveFrameCount;
        var frameOffset = 0;
        var deadline = Stopwatch.GetTimestamp() 
            + (long)(_config.WriteTimeout.TotalSeconds * Stopwatch.Frequency);

        while (framesRemaining > 0)
        {
            // existing state checks
            if (_disposed || State != AudioOutputState.Running || _stream == nint.Zero)
            {
                return (int)MediaErrorCode.PortAudioPushFailed;
            }

            var writableFrames = Math.Min(framesRemaining, Math.Max(1, _nativeFramesPerBuffer));
            var sampleOffset = frameOffset * _nativeChannelCount;
            var writePtr = ptr + sampleOffset;
            var write = Native.Pa_WriteStream(_stream, (nint)writePtr, (nuint)writableFrames);
            
            if (write == PaError.paNoError) { /* ... */ continue; }

            bool isRetryable = write is PaError.paTimedOut or PaError.paOutputUnderflowed;
            bool deadlineExceeded = _config.WriteTimeout > TimeSpan.Zero 
                && Stopwatch.GetTimestamp() >= deadline;

            if (!isRetryable || deadlineExceeded)
            {
                // Log timeout event at Warning level for production diagnostics
                if (deadlineExceeded && isRetryable)
                {
                    // Emit diagnostic event or log
                }
                return (int)MediaErrorCode.PortAudioPushFailed;
            }

            Thread.Sleep(1);
        }
        return MediaResult.Success;
    }
}
```

**Tuning guidance:** For a buffer of 256 frames at 48 kHz ≈ 5.3 ms duration, a timeout of 50–500 ms is reasonable (ensuring at least 10× buffer duration). Log timeout events to help diagnose stuck hardware or driver issues in production.

---

### Issue 6.2 — `PortAudioInput` has hardcoded 256-frame buffer size

`PortAudioInput.TryStartNativeStream()` (lines 213–234) opens the input stream with a hardcoded `framesPerBuffer: 256`. This does not respect the engine configuration and cannot be changed at runtime.

**Current code:**
```csharp
var open = Native.Pa_OpenDefaultStream(
    out _stream,
    numInputChannels: Config.ChannelCount,
    numOutputChannels: 0,
    sampleFormat: PaSampleFormat.paFloat32,
    sampleRate: Config.SampleRate,
    framesPerBuffer: 256,  // ← HARDCODED
    streamCallback: (delegate* unmanaged[Cdecl]<...>)0,
    userData: nint.Zero);
```

**Fix:** Accept `framesPerBuffer` from `AudioEngineConfig` and pass it through:

```csharp
// In PortAudioInput constructor:
private readonly int _nativeFramesPerBuffer;

public PortAudioInput(int framesPerBuffer = 256)
{
    _nativeFramesPerBuffer = Math.Max(1, framesPerBuffer);
    // ...
}

// In TryStartNativeStream:
var open = Native.Pa_OpenDefaultStream(
    out _stream,
    numInputChannels: Config.ChannelCount,
    numOutputChannels: 0,
    sampleFormat: PaSampleFormat.paFloat32,
    sampleRate: Config.SampleRate,
    framesPerBuffer: _nativeFramesPerBuffer,  // ← Now configurable
    streamCallback: (delegate* unmanaged[Cdecl]<...>)0,
    userData: nint.Zero);
```

Or, better, add to `AudioEngineConfig` and have the engine pass it:

```csharp
public sealed class AudioEngineConfig
{
    public int FramesPerBuffer { get; init; } = 256;
    // ...
}

// When creating input:
public int CreateInput(AudioDeviceId deviceId, out IAudioInput? input)
{
    // ...
    input = new PortAudioInput(
        deviceProvider: () => _inputDevices,
        framesPerBuffer: Config.FramesPerBuffer,
        sampleRate: Config.SampleRate);
    // ...
}
```

---

### Issue 6.3 — `PortAudioInput` generates fake test samples when stream is not initialized

When the native stream fails to start, `PortAudioInput.ReadSamples()` (lines 108–145) falls back to generating synthetic test samples at lines 144–158:

```csharp
// Fallback: generate synthetic test samples
var sampleCount = framesRead * config.ChannelCount;
for (var i = 0; i < sampleCount; i++)
{
    destination[i] = ((_sampleCursor + i) % 64) / 64f;  // ← Sawtooth wave pattern
}
```

This is extremely dangerous in production: if the native stream fails silently, the mixer will output a sawtooth tone instead of audio or silence, misleading developers and end-users. This violates the "fail fast" principle.

**Fix:** Remove the synthetic fallback entirely. Return an error instead:

```csharp
public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
{
    framesRead = 0;
    
    if (requestedFrameCount <= 0)
        return MediaResult.Success;

    lock (_gate)
    {
        if (_disposed || State != AudioSourceState.Running)
            return (int)MediaErrorCode.PortAudioInputReadFailed;

        if (!_nativeStreaming || _stream == nint.Zero)
            return (int)MediaErrorCode.PortAudioStreamStartFailed;  // ← Fail fast, no synthesis
        
        config = Config;
    }

    // ... proceed with native read only ...
    
    fixed (float* ptr = destination)
    {
        var read = Native.Pa_ReadStream(_stream, (nint)ptr, (nuint)framesRead);
        if (read == PaError.paNoError)
        {
            // ... success path ...
        }
        // ... error handling ...
    }
}
```

If you need a "soft" fallback for testing or debugging, make it opt-in via a separate testing-only class (`PortAudioInputWithFallback` or similar), not the production path.

---

### Issue 6.4 — No validation of `AudioEngineConfig` values in `PortAudioEngine.Initialize`

`PortAudioEngine.Initialize()` (lines 61–81) does perform basic range checks:

```csharp
if (config.SampleRate <= 0 || config.OutputChannelCount <= 0 || config.FramesPerBuffer <= 0)
{
    return (int)MediaErrorCode.PortAudioInvalidConfig;
}
```

However, there is no upper-bound validation. Pathological values like `SampleRate = int.MaxValue` or `FramesPerBuffer = 1_000_000_000` will be accepted, then fail silently in native calls or allocate excessive memory.

**Fix:** Add upper bounds:

```csharp
private static bool IsValidAudioEngineConfig(AudioEngineConfig config)
{
    const int MaxSampleRate = 192_000;
    const int MaxChannelCount = 128;
    const int MaxFramesPerBuffer = 65_536;

    return config.SampleRate > 0 && config.SampleRate <= MaxSampleRate
        && config.OutputChannelCount > 0 && config.OutputChannelCount <= MaxChannelCount
        && config.FramesPerBuffer > 0 && config.FramesPerBuffer <= MaxFramesPerBuffer;
}

public int Initialize(AudioEngineConfig config)
{
    ArgumentNullException.ThrowIfNull(config);

    lock (_gate)
    {
        if (_disposed)
            return (int)MediaErrorCode.PortAudioInitializeFailed;

        if (!IsValidAudioEngineConfig(config))
            return (int)MediaErrorCode.PortAudioInvalidConfig;

        // ... rest of Initialize ...
    }
}
```

---

### Issue 6.5 — Exception handling inconsistency across native operations

`PortAudioEngine.TryInitializeNativeRuntimeAndRefreshDevices()` (lines 364–382) and `PortAudioOutput.TryStartNativeStream()` (lines 303–350) both catch `DllNotFoundException`, `EntryPointNotFoundException`, and `TypeInitializationException`. However, `PortAudioInput.TryStartNativeStream()` (lines 213–234) catches the same exceptions but does not return a value — it just silently sets `_nativeStreaming = false`.

This inconsistency makes debugging harder: engine failures surface as readable error codes, but input failures are silent.

**Fix:** Standardize exception handling and propagate errors consistently:

```csharp
// In PortAudioInput:
private int TryStartNativeStream()
{
    if (_nativeStreaming)
        return MediaResult.Success;

    try
    {
        var open = Native.Pa_OpenDefaultStream(
            out _stream,
            numInputChannels: Config.ChannelCount,
            numOutputChannels: 0,
            sampleFormat: PaSampleFormat.paFloat32,
            sampleRate: Config.SampleRate,
            framesPerBuffer: _nativeFramesPerBuffer,
            streamCallback: (delegate* unmanaged[Cdecl]<...>)0,
            userData: nint.Zero);

        if (open != PaError.paNoError)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioStreamOpenFailed;
        }

        var start = Native.Pa_StartStream(_stream);
        if (start != PaError.paNoError)
        {
            Native.Pa_CloseStream(_stream);
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioStreamStartFailed;
        }

        _nativeStreaming = true;
        return MediaResult.Success;
    }
    catch (DllNotFoundException)
    {
        _stream = nint.Zero;
        _nativeStreaming = false;
        return (int)MediaErrorCode.PortAudioStreamOpenFailed;
    }
    catch (EntryPointNotFoundException)
    {
        _stream = nint.Zero;
        _nativeStreaming = false;
        return (int)MediaErrorCode.PortAudioStreamOpenFailed;
    }
    catch (TypeInitializationException)
    {
        _stream = nint.Zero;
        _nativeStreaming = false;
        return (int)MediaErrorCode.PortAudioStreamOpenFailed;
    }
}
```

Change the `Start()` method to capture the error:

```csharp
public int Start(AudioInputConfig config)
{
    ArgumentNullException.ThrowIfNull(config);

    lock (_gate)
    {
        if (_disposed)
            return (int)MediaErrorCode.PortAudioInputStartFailed;

        if (config.SampleRate <= 0 || config.ChannelCount <= 0)
            return (int)MediaErrorCode.PortAudioInvalidConfig;

        Config = config;
        State = AudioSourceState.Running;
        var streamResult = TryStartNativeStream();  // ← Capture result
        return streamResult;  // ← Propagate to caller
    }
}
```

---

### Issue 6.6 — Phantom devices returned before engine initialization

`PortAudioEngine` constructor (lines 24–49) pre-populates `_outputDevices` and `_inputDevices` with phantom fallback entries (`"Default Output"`, `"Monitor Output"`, `"Default Input"`) before `Initialize()` is called. The `PhantomDeviceState.Uninitialized` check in `GetOutputDevices()` is **not** implemented in the actual code.

**Actual current behavior:**
```csharp
public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
{
    lock (_gate)
    {
        return _outputDevices.ToArray();  // ← Always returns fallback devices, even if uninitialized
    }
}
```

Callers can enumerate phantom devices on an uninitialized engine, creating confusing UIs ("Default Output" device appears but is non-functional).

**Fix:** Return empty list until engine is initialized:

```csharp
public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
{
    lock (_gate)
    {
        if (State == AudioEngineState.Uninitialized)
            return Array.Empty<AudioDeviceInfo>();
        return _outputDevices.ToArray();
    }
}

public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
{
    lock (_gate)
    {
        if (State == AudioEngineState.Uninitialized)
            return Array.Empty<AudioDeviceInfo>();
        return _inputDevices.ToArray();
    }
}
```

If a "fallback device" is needed for graceful degradation when enumeration fails, add an opt-in property to `AudioEngineConfig`:

```csharp
public sealed class AudioEngineConfig
{
    /// <summary>
    /// If true, a synthetic "Default Output" device is added when real enumeration returns zero devices.
    /// Default: false. Enable for graceful degradation in minimal environments.
    /// </summary>
    public bool UseFallbackDeviceOnEnumerationFailure { get; init; } = false;
}
```

Then in `RefreshNativeDevices()`:

```csharp
if (discoveredOutputs.Count == 0 && Config.UseFallbackDeviceOnEnumerationFailure)
{
    discoveredOutputs.Add(
        new AudioDeviceInfo(
            new AudioDeviceId("fallback-output"),
            "Default Output (Fallback)",
            HostApi: "fallback"));
}
```

---

### Issue 6.7 — `PortAudioOutput` snapshot-captures `AudioEngineConfig`

`PortAudioEngine.CreateOutput()` passes `Config` by value into `PortAudioOutput` constructor:

```csharp
output = new PortAudioOutput(device.Value, () => _outputDevices, Config, () => _defaultOutputDevice);
```

`PortAudioOutput` stores it as `_config`:

```csharp
private readonly AudioEngineConfig _config;

public PortAudioOutput(
    AudioDeviceInfo device,
    Func<IReadOnlyList<AudioDeviceInfo>> deviceProvider,
    AudioEngineConfig config,
    Func<AudioDeviceInfo?>? defaultOutputProvider = null)
{
    // ...
    _config = config;  // ← Snapshot captured
}
```

If the engine is re-initialized with different settings (e.g., higher sample rate), existing outputs retain stale config. Subsequent `PushFrame` calls use outdated sample rates and buffer sizes.

**Fix option A — document the snapshot behavior:**

```csharp
// In PortAudioOutput XML doc:
/// <remarks>
/// This output captures a snapshot of <see cref="AudioEngineConfig"/> at creation time.
/// Re-initializing the engine does not update outputs' configs. Recreate outputs after
/// re-initialization if config changes are needed.
/// </remarks>
public sealed unsafe class PortAudioOutput : IAudioOutput { ... }
```

**Fix option B — live reference via the engine (preferred if re-init is common):**

```csharp
// PortAudioOutput holds reference to engine:
private readonly PortAudioEngine _engine;
private AudioEngineConfig Config => _engine.Config;  // ← Always current

public PortAudioOutput(
    AudioDeviceInfo device,
    Func<IReadOnlyList<AudioDeviceInfo>> deviceProvider,
    PortAudioEngine engine,
    Func<AudioDeviceInfo?>? defaultOutputProvider = null)
{
    _engine = engine;
    // ...
}
```

Option A is simpler and safer for most use cases (re-init is rare). Option B is appropriate if runtime config changes (dynamic sample rate, buffer size adjustments) are part of the design.

---

### Issue 6.8 — No logging / diagnostics framework in S.Media.PortAudio

`PortAudioLogAdapter.cs` exists but is not wired up. No events or callbacks are raised when:
- Phantom devices are returned from an uninitialized engine
- Native stream initialization fails
- A write timeout occurs on output
- A device is hot-unplugged

This makes debugging production failures very difficult. Errors are only observable through return codes passed back to the caller, with no central logging.

**Fix:** Wire up the logging adapter:

```csharp
// In PortAudioEngine:
private static ILogger<PortAudioEngine>? _logger;

public static void ConfigureLogging(ILogger<PortAudioEngine> logger)
{
    _logger = logger;
    PALib.Runtime.PortAudioLibraryResolver.ConfigureLogging(logger);
}

// In TryInitializeNativeRuntimeAndRefreshDevices():
if (!discoveryOk)
{
    _logger?.LogWarning("PortAudio device enumeration failed; falling back to no devices.");
}

// In TryStartNativeStream():
if (open != PaError.paNoError)
{
    _logger?.LogError("Pa_OpenStream failed with code {ErrorCode}", open);
}
```

Add a public event for lifecycle changes:

```csharp
public event EventHandler<PortAudioDiagnosticEventArgs>? DiagnosticEvent;

// Raise on key events:
DiagnosticEvent?.Invoke(this, new PortAudioDiagnosticEventArgs(
    DiagnosticLevel.Warning,
    "Write timeout on output stream — device may be stuck or unplugged"));
```

---

## 7. Summary of Recommended Changes (by priority)

| Issue | Severity | Effort | Impact |
|-------|----------|--------|--------|
| 6.3 — Synthetic fallback samples on stream failure | **CRITICAL** | Low | Prevents silent data corruption |
| 6.1 — Write timeout on blocked stream | **HIGH** | Medium | Prevents audio pump deadlock |
| 6.2 — Hardcoded input buffer size | **MEDIUM** | Low | Improves flexibility / consistency |
| 6.4 — No upper-bound validation | **MEDIUM** | Low | Prevents pathological configs |
| 6.6 — Phantom devices on uninitialized engine | **MEDIUM** | Low | Improves API clarity |
| 5.1–5.3 — `IAudioInput` interface + device selection | **MEDIUM** | High | Completes input API story |
| 6.7 — Config snapshot vs. live reference | **LOW** | Medium | Clarifies lifecycle semantics |
| 6.5 — Exception handling inconsistency | **LOW** | Low | Improves observability |
| 6.8 — No logging framework | **LOW** | Medium | Improves diagnostics |

---

*See also `API-Review.md` §5, `S.Media.Core.md` §1.1–1.2, and `PALib.md` for the full analysis of related issues in the native wrapper layer.*
