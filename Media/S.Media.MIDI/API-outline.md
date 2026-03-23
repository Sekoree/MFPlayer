# S.Media.MIDI API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `MIDIRuntime.cs`
- `sealed class MIDIRuntime : IDisposable`
- Planned API:
  - `int Initialize()`
  - `int Terminate()`
  - `bool IsInitialized { get; }`

### `MIDIDeviceCatalog.cs`
- `sealed class MIDIDeviceCatalog`
- Planned API:
  - `IReadOnlyList<MIDIDeviceInfo> GetInputs()`
  - `IReadOnlyList<MIDIDeviceInfo> GetOutputs()`
  - `MIDIDeviceInfo? GetDefaultInput()`
  - `MIDIDeviceInfo? GetDefaultOutput()`

### `MIDIInput.cs`
- `sealed class MIDIInput : IDisposable`
- Planned API:
  - `int Open(MIDIDeviceInfo device)`
  - `int Close()`
  - `bool IsOpen { get; }`
  - `event EventHandler<MIDIMessageEventArgs>? MessageReceived`

### `MIDIOutput.cs`
- `sealed class MIDIOutput : IDisposable`
- Planned API:
  - `int Open(MIDIDeviceInfo device)`
  - `int Close()`
  - `bool IsOpen { get; }`
  - `int Send(MIDIMessage message)`

### `MIDIMessageRouter.cs`
- `sealed class MIDIMessageRouter`
- Planned API:
  - `int AddRoute(MIDIInput input, MIDIOutput output)`
  - `int RemoveRoute(MIDIInput input, MIDIOutput output)`
  - `IReadOnlyList<MIDIRoute> Routes { get; }`

## Notes
- Backend is `MIDI/PMLib`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Doc/error-codes.md`.
- Logging uses `Microsoft.Extensions.Logging` and follows non-static logger injection.
- `0` is success; all non-zero return values are failures.
- `MIDIRuntime.Terminate()` auto-closes active inputs/outputs and returns `MediaResult.Success` when already terminated.
- `MIDIInput.Close()` and `MIDIOutput.Close()` are idempotent and return `MediaResult.Success` when already closed.
- `IsOpen` is `true` only while the corresponding native input/output handle is active.
- Successful `Close()` and `MIDIRuntime.Terminate()` transitions set `IsOpen` to `false`.
- `MIDIOutput.Send(...)` returns `MIDIOutputNotOpen` when `IsOpen == false`.
- MIDI input is event-driven in this outline; no timeout/poll read contract is defined here.

## Initial MIDI Error Code Picks (`0-999`)
- `910`: `MIDIOutputNotOpen`

