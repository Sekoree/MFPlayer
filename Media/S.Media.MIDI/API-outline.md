# S.Media.MIDI API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

This module is a concrete backend over `MIDI/PMLib` with deterministic int-return lifecycle contracts and event-driven input delivery.

### `Runtime/MIDIEngine.cs`
- `sealed class MIDIEngine : IDisposable`
- Planned API:
  - `MIDIEngine()`
  - `int Initialize(MIDIReconnectOptions? reconnectOptions = null)`
  - `int Terminate()`
  - `bool IsInitialized { get; }`
  - `IReadOnlyList<MIDIDeviceInfo> GetInputs()`
  - `IReadOnlyList<MIDIDeviceInfo> GetOutputs()`
  - `MIDIDeviceInfo? GetDefaultInput()`
  - `MIDIDeviceInfo? GetDefaultOutput()`
  - `int CreateInput(MIDIDeviceInfo device, out MIDIInput? input)`
  - `int CreateOutput(MIDIDeviceInfo device, out MIDIOutput? output)`

### `Input/MIDIInput.cs`
- `sealed class MIDIInput : IDisposable`
- Planned API:
  - `MIDIDeviceInfo Device { get; }`
  - `MIDIReconnectOptions ReconnectOptions { get; }`
  - `int Open()`
  - `int Close()`
  - `bool IsOpen { get; }`
  - `event EventHandler<MIDIMessageEventArgs>? MessageReceived`
  - `event EventHandler<MIDIConnectionStatusEventArgs>? StatusChanged`

### `Config/MIDIReconnectOptions.cs`
- `sealed record MIDIReconnectOptions`
- Planned API:
  - `MIDIReconnectMode ReconnectMode { get; init; } // default: AutoReconnect`
  - `TimeSpan DisconnectGracePeriod { get; init; } // default: 500ms`
  - `TimeSpan ReconnectTimeout { get; init; } // default: 5s; <= 0 means no timeout limit (attempt limit still applies)`
  - `int MaxReconnectAttempts { get; init; } // default: 8, values < 1 clamp to 1`
  - `TimeSpan ReconnectAttemptDelay { get; init; } // default: 250ms`

### `Config/MIDIReconnectMode.cs`
- `enum MIDIReconnectMode`
- Planned API:
  - `AutoReconnect = 0`
  - `NoRecover = 1`

### `Events/MIDIMessageEventArgs.cs`
- `sealed class MIDIMessageEventArgs : EventArgs`
- Planned API:
  - `MIDIMessage Message { get; }`
  - `MIDIDeviceInfo SourceDevice { get; }`
  - `DateTimeOffset ReceivedAtUtc { get; }`
  - `long? BackendTimestamp { get; } // backend-native timestamp when available`

### `Events/MIDIConnectionStatusEventArgs.cs`
- `sealed class MIDIConnectionStatusEventArgs : EventArgs`
- Planned API:
  - `MIDIConnectionStatus Status { get; }`
  - `MIDIDeviceInfo Device { get; }`
  - `DateTimeOffset ChangedAtUtc { get; }`
  - `int? ErrorCode { get; } // populated for transition-to-failure states`

### `Types/MIDIConnectionStatus.cs`
- `enum MIDIConnectionStatus`
- Planned API:
  - `Closed = 0`
  - `Opening = 1`
  - `Open = 2`
  - `Disconnected = 3`
  - `Reconnecting = 4`
  - `ReconnectFailed = 5`

### `Output/MIDIOutput.cs`
- `sealed class MIDIOutput : IDisposable`
- Planned API:
  - `MIDIDeviceInfo Device { get; }`
  - `int Open()`
  - `int Close()`
  - `bool IsOpen { get; }`
  - `int Send(in MIDIMessage message)`
  - `event EventHandler<MIDIConnectionStatusEventArgs>? StatusChanged`

### `Diagnostics/MIDILogAdapter.cs`
- `sealed class MIDILogAdapter`
- Planned API:
  - `MIDILogAdapter(ILogger logger)`
  - `void LogInfo(string message)`
  - `void LogWarning(string message)`
  - `void LogError(string message, Exception? exception = null)`

## Notes
- Backend is `MIDI/PMLib`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Media/S.Media.Core/error-codes.md`.
- For Core mixer detach/remove/clear orchestration, MIDI-specific failure codes remain authoritative when available; Core fallback `MixerDetachStepFailed` (`3000`) applies only when no more specific owned code exists.
- Logging uses `Microsoft.Extensions.Logging` with non-static logger injection.
- `0` is success; all non-zero return values are failures.
- `MIDIEngine.Terminate()` auto-closes active inputs/outputs and returns `MediaResult.Success` when already terminated.
- Default device lookup is discovery-only (`GetDefaultInput()`/`GetDefaultOutput()`); creation remains explicit via `CreateInput(...)`/`CreateOutput(...)`.
- `MIDIInput.Close()` and `MIDIOutput.Close()` are idempotent and return `MediaResult.Success` when already closed.
- `IsOpen` is `true` only while the corresponding native input/output handle is active.
- Successful `Close()` and `MIDIEngine.Terminate()` transitions set `IsOpen` to `false`.
- `MIDIOutput.Send(...)` returns `MIDIOutputNotOpen` when `IsOpen == false`.
- Input delivery is event-driven; no timeout/poll read contract is defined for MIDI in this phase.
- Callback delivery is synchronous and serialized per instance (no configurable callback dispatcher in this phase).
- No internal callback queue exists in this phase.
- Message callbacks are no-drop while the instance is open; no internal callback-queue drop policy is applied in this phase.
- Status and message callbacks are emitted on backend/engine callback thread context; consumers dispatch to UI thread when needed.
- Future evolution note: if callback latency becomes a verified issue, introduce a minimal dispatcher in a later phase without breaking `MessageReceived`/`StatusChanged` ordering or teardown-fence guarantees.
- Status transitions are guaranteed for all internally observed connection-state changes on both input/output instances.
- Guaranteed status delivery preserves per-instance ordering and does not coalesce transitions.
- Failure atomicity is required: failed `Initialize()`/`Create*()`/`Open()` paths leave no partially-open state.
- Reconnect behavior is configurable: `ReconnectMode = AutoReconnect` attempts deterministic reopen of the same device using bounded retry/backoff from `MIDIReconnectOptions`; `ReconnectMode = NoRecover` performs no reconnect attempts and transitions to disconnected/failure state immediately.
- Reconnect mode is controlled only by `ReconnectMode` (single source of truth).
- Reconnect stop conditions are deterministic: recovery stops when either `MaxReconnectAttempts` is exhausted or `ReconnectTimeout` is reached; both converge to `MIDIReconnectFailed` (`920`).
- Reconnect attempts stop immediately on explicit `Close()`/`Terminate()`.
- Input/output status transitions are observable through `StatusChanged` on both `MIDIInput` and `MIDIOutput`.
- Same-instance concurrent operation misuse maps to shared semantic `MediaConcurrentOperationViolation` (`950`).
- Error split is explicit: `MIDIInvalidMessage` means pre-send validation failed (unsupported/invalid message shape); `MIDIOutputSendFailed` means message passed validation but backend rejected send.
- Failure atomicity: failed `Open()`/`Close()`/`Terminate()`/reconnect transitions must not leave partially-open handles or partially-updated connection state.

## Initial MIDI Error Code Picks (`900-949`)
- `900`: `MIDINotInitialized`
- `901`: `MIDIInitializeFailed`
- `902`: `MIDITerminateFailed`
- `903`: `MIDIDeviceEnumerationFailed`
- `904`: `MIDIDeviceNotFound`
- `905`: `MIDIInputOpenFailed`
- `906`: `MIDIInputCloseFailed`
- `907`: `MIDIOutputOpenFailed`
- `908`: `MIDIOutputCloseFailed`
- `909`: `MIDIOutputSendFailed`
- `910`: `MIDIOutputNotOpen`
- `911`: `MIDIInputNotOpen`
- `912`: `MIDIInvalidMessage`
- `913`: `MIDIInvalidConfig`
- `918`: `MIDIConcurrentOperationRejected`
- `918` maps to shared semantic `MediaConcurrentOperationViolation` (`950`) when rejection reason is same-instance concurrent misuse.
- `919`: `MIDIDeviceDisconnected`
- `920`: `MIDIReconnectFailed`
- `914-917`: reserved for future MIDI routing/graph policies.

## MIDI Contract Test Matrix (Minimum)
- Lifecycle idempotency: repeated `Close()`/`Terminate()` returns `MediaResult.Success`.
- Open-state correctness: `IsOpen` mirrors native-handle activity exactly.
- Send semantics: `Send(...)` returns `MIDIOutputNotOpen` when output is closed.
- Event lifetime safety: no `MessageReceived` callback after `Close()` or `Terminate()`.
- Event lifetime safety: no `StatusChanged` callback after `Close()` or `Terminate()`.
- Status event contract: status transitions are guaranteed and ordered per instance (`Opening` -> `Open`, `Open` -> `Disconnected` -> optional `Reconnecting` -> `Open` or `ReconnectFailed`).
- Status event durability: transitions are emitted as distinct events (no transition coalescing).
- Callback delivery behavior: synchronous no-drop callback delivery is preserved while open; no configurable dispatch/overflow mode is applied in this phase.
- Event payload contract: `MIDIMessageEventArgs` always includes source-device identity and receive timestamp; backend timestamp is populated when available.
- Failure atomicity: failed initialize/create/open paths leave no leaked handles or active callbacks.
- Reconnect policy: transient disconnects follow configured mode/retry/timeout rules, recover when possible, and return deterministic disconnect/reconnect codes when recovery fails.
- Error split: malformed/unsupported message uses `MIDIInvalidMessage`; backend send rejection uses `MIDIOutputSendFailed`.
- Shared-semantic mapping: `MIDIConcurrentOperationRejected` maps to `MediaConcurrentOperationViolation` (`950`) through `ResolveSharedSemantic`.

