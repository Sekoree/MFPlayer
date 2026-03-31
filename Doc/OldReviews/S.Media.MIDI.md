# S.Media.MIDI — Issues & Fix Guide

> **Scope:** `S.Media.MIDI` — `MIDIEngine`, `MIDIInput`, `MIDIOutput`, config types
> **Cross-references:** See `API-Review.md` §9 and `PMLib.md` for the underlying native wrapper issues.

---

## Table of Contents

1. [Interface Hierarchy](#1-interface-hierarchy)
2. [Engine Lifecycle](#2-engine-lifecycle)
3. [Composability with S.Media Pipelines](#3-composability-with-smedia-pipelines)
4. [Considerations](#4-considerations)

---

## 1. Interface Hierarchy

### Issue 1.1 — `MIDIInput` and `MIDIOutput` have no common interface

Both share properties (`Device`, `IsOpen`) and methods (`Open()`, `Close()`, `Dispose()`). There is no shared interface, preventing unified device management.

**Fix:** Define `IMIDIDevice` in `S.Media.Core` (or `S.Media.MIDI`):

```csharp
// S.Media.MIDI/Interfaces/IMIDIDevice.cs
public interface IMIDIDevice : IDisposable
{
    /// <summary>The device descriptor this instance is bound to.</summary>
    MIDIDeviceInfo Device { get; }

    /// <summary><see langword="true"/> when the device stream is open.</summary>
    bool IsOpen { get; }

    /// <summary>Opens the device stream. Returns <see cref="MediaResult.Success"/> on success.</summary>
    int Open();

    /// <summary>Closes the device stream.</summary>
    int Close();
}
```

Have both `MIDIInput` and `MIDIOutput` implement `IMIDIDevice`.

**Migration:** Code that enumerates or manages MIDI devices can use `IMIDIDevice` instead of switching on concrete type.

---

### Issue 1.2 — `MIDIInput.Start()` / `Stop()` absent — inconsistent with `IAudioSource`

`PortAudioInput` (an `IAudioSource`) has `Start()` and `Stop()`. `MIDIInput` uses `Open()` and `Close()`. This inconsistency complicates generic lifecycle code that handles both audio and MIDI inputs.

**Context:** MIDI is event-driven rather than sample-driven, so it cannot implement `IAudioSource.ReadSamples`. However, the `Open` / `Close` lifecycle could be aliased to `Start` / `Stop` for uniformity.

**Fix:** Add `Start()` and `Stop()` methods to `IMIDIDevice` (or to `IMIDIInput` specifically) as aliases:

```csharp
public interface IMIDIDevice : IDisposable
{
    // ...existing...
    int Start();    // alias for Open — starts event delivery
    int Stop();     // alias for Close — stops event delivery
}
```

In `MIDIInput`:
```csharp
public int Start() => Open();
public int Stop()  => Close();
```

This doesn't replace `Open`/`Close` — it just provides a consistent lifecycle method name for code that treats all inputs uniformly.

---

## 2. Engine Lifecycle

### Issue 2.1 — `MIDIEngine` has no `IMediaEngine` interface

`PortAudioEngine` implements `IAudioEngine`. `MIDIEngine` has no interface. This prevents dependency injection, mocking, and consistent engine management.

**Fix:** Define `IMediaEngine` in `S.Media.Core` (see `S.Media.Core.md` §5 / `API-Review.md` §9.1):

```csharp
public interface IMediaEngine : IDisposable
{
    bool IsInitialized { get; }
    int Terminate();
}
```

```csharp
public sealed class MIDIEngine : IMediaEngine
{
    public bool IsInitialized { get; private set; }

    public int Initialize()
    {
        var err = PMLib.Native.Pm_Initialize();
        if (err != PmError.NoError)
            return (int)MediaErrorCode.MIDIInitializationFailed;
        IsInitialized = true;
        return MediaResult.Success;
    }

    public int Terminate()
    {
        if (!IsInitialized) return MediaResult.Success;
        PMLib.Native.Pm_Terminate();
        IsInitialized = false;
        return MediaResult.Success;
    }

    public void Dispose() => Terminate();
}
```

---

## 3. Composability with S.Media Pipelines

### Issue 3.1 — MIDI has no entry in the `S.Media.Core` interface hierarchy

MIDI events have no counterpart in the `IAudioSource` / `IVideoSource` model. `MIDIInput` cannot be added to an `AudioVideoMixer`, and MIDI-driven automation (tempo sync, parameter control) cannot be expressed as a first-class pipeline element.

**Fix:** Add a lightweight event-source interface to `S.Media.Core`:

```csharp
// S.Media.Core/MIDI/IMidiEventSource.cs
public interface IMidiEventSource : IDisposable
{
    Guid Id { get; }
    bool IsRunning { get; }
    int Start();
    int Stop();
    event EventHandler<MidiMessage>? MessageReceived;
}
```

`MIDIInput` can implement `IMidiEventSource`:

```csharp
public sealed class MIDIInput : IMidiEventSource
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool IsRunning => IsOpen;

    public int Start() => Open();
    public int Stop()  => Close();

    // Forward PortMidi poll events to MessageReceived
    public event EventHandler<MidiMessage>? MessageReceived;
}
```

**Consideration:** `MidiMessage` should be defined in `S.Media.Core` (or a shared `S.Media.MIDI.Core` project) to avoid a hard dependency from `S.Media.Core` on `S.Media.MIDI`.

A thin abstraction like:

```csharp
// S.Media.Core/MIDI/MidiMessage.cs
public readonly record struct MidiMessage(
    int Status,
    int Data1,
    int Data2,
    long TimestampMs);
```

keeps `S.Media.Core` decoupled from PortMidi specifics.

---

## 4. Considerations

### PortMidi Initialisation Order

PortMidi requires `Pm_Initialize()` to be called before any other function and `Pm_Terminate()` after all streams are closed. `MIDIEngine.Initialize()` and `MIDIEngine.Terminate()` manage this. Ensure that:

- Only one `MIDIEngine` instance is active at a time (global PortMidi state).
- All `MIDIInput` and `MIDIOutput` instances are closed before calling `Terminate()`. The engine should track open devices and close them in `Terminate()` / `Dispose()`.

```csharp
public int Terminate()
{
    foreach (var device in _openDevices.ToList())
        device.Close();
    _openDevices.Clear();

    PMLib.Native.Pm_Terminate();
    IsInitialized = false;
    return MediaResult.Success;
}
```

---

### Device Enumeration Timing

PortMidi scans for devices at `Pm_Initialize()` time. Devices added after initialisation are not visible. If hot-plug detection is required, `Pm_Terminate()` + `Pm_Initialize()` is needed to rescan. This is a PortMidi limitation — document it clearly on `MIDIEngine.GetDevices()`.

---

### `PMLib.Native.cs` Has No Call Tracing

Unlike PALib, PMLib's `Native.cs` has no trace logging. Open/close failures will only appear if the `PMLibLogging` wrapper layer logs them. See `PMLib.md` §1.1 for the fix. As a minimum, ensure `MIDIEngine.Initialize()` and `MIDIInput.Open()` log failures at `Warning` level through `PMLibLogging`.

---

### Thread Safety for `MessageReceived` Event

`MIDIInput` raises `MessageReceived` on the polling background thread (1 ms poll interval by default). Handlers must be thread-safe. If handlers interact with UI components, they must marshal to the UI thread. Document this prominently on the event:

```csharp
/// <summary>
/// Raised on the PortMidi polling thread when a MIDI message is received.
/// <para>
/// <b>Threading:</b> Handlers run on a background thread.
/// Marshal to the UI thread if needed (e.g. <c>Dispatcher.Invoke</c> / <c>SynchronizationContext</c>).
/// </para>
/// </summary>
public event EventHandler<MidiMessage>? MessageReceived;
```

