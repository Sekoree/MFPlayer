# S.Media.PortAudio API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

This module is a concrete backend for Core audio engine contracts, with an OwnAudio-like push sink model and explicit per-channel routing support.

### `Output/PortAudioOutput.cs`
- `sealed class PortAudioOutput : IAudioOutput, IDisposable`
- Planned API:
  - `int Start(AudioOutputConfig config)`
  - `int Stop()`
  - `int SetOutputDevice(AudioDeviceId deviceId)`
  - `int SetOutputDeviceByName(string deviceName)`
  - `int SetOutputDeviceByIndex(int deviceIndex)`
  - `event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged`
  - `int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex)`
  - `int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount)`
  - `AudioDeviceInfo Device { get; }`
  - `AudioOutputState State { get; }`

### `Engine/PortAudioEngine.cs`
- `sealed class PortAudioEngine : IAudioEngine, IDisposable`
- Planned API:
  - `PortAudioEngine()`
  - `AudioEngineState State { get; }`
  - `AudioEngineConfig Config { get; }`
  - `bool IsInitialized { get; }`
  - `int Initialize(AudioEngineConfig config)`
  - `int Start()`
  - `int Stop()`
  - `int Terminate()`
  - `IReadOnlyList<AudioDeviceInfo> GetOutputDevices()`
  - `IReadOnlyList<AudioDeviceInfo> GetInputDevices()`
  - `int CreateOutput(AudioDeviceId deviceId, out IAudioOutput? output)`
  - `int CreateOutputByName(string deviceName, out IAudioOutput? output)`
  - `int CreateOutputByIndex(int deviceIndex, out IAudioOutput? output)`
  - `IReadOnlyList<IAudioOutput> Outputs { get; }`
  - `event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged`

### `Input/PortAudioInput.cs`
- `sealed class PortAudioInput : IAudioSource, IDisposable`
- Planned API:
  - `int Start(AudioInputConfig config)`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)`
  - `int Seek(double positionSeconds) // returns MediaSourceNonSeekable for live input`
  - `double PositionSeconds { get; }`
  - `double DurationSeconds { get; } // live input returns double.NaN`
  - `AudioSourceState State { get; }`

### `Diagnostics/PortAudioLogAdapter.cs`
- `sealed class PortAudioLogAdapter`
- Planned API:
  - `PortAudioLogAdapter(ILogger logger)`
  - `void LogInfo(string message)`
  - `void LogWarning(string message)`
  - `void LogError(string message, Exception? exception = null)`

## Routing Behavior Notes
- Every push carries an explicit dense output-indexed source-channel map; there is no static engine route-map state.
- Default caller strategy in higher layers should favor mono/separated channels until final output mapping.
- Mapping validation is strict and deterministic: invalid map returns non-zero error code, with no partial push.
- `PushFrame(...)` call-shape/map validation follows Core generic-audio precedence (`4200`, `4201`, `4203`, `4210`); `PortAudioRouteMapInvalid` (`4308`) is reserved for backend/runtime mapping faults.
- One-to-many fan-out is supported directly by repeated source indices in the per-push map.
- `-1` map entries render silence on the corresponding output channel.
- Interleaved multi-channel input is supported; source channels can be mapped to any output channels (including reordering and sparse mapping).
- `0` is success, all non-zero values are failure codes.
- PortAudio owned errors use reserved subrange `4300-4399` from the output/render pool.
- `Stop()` is idempotent; calling stop on an already stopped engine/output/input returns `MediaResult.Success`.
- `Terminate()` auto-stops active outputs/inputs and returns `MediaResult.Success` when already terminated.

## Input Read Safety Contract
- `ReadSamples(...)` never writes past `destination` bounds.
- If captured frames are fewer than requested, remaining destination samples are zero-filled.
- `framesRead` reports captured (non-silence) frames; return code remains `MediaResult.Success` unless backend failure occurs.
- `requestedFrameCount <= 0` returns `MediaResult.Success` with `framesRead = 0`.

## Notes
- Backend is `Audio/PALib`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Media/S.Media.Core/error-codes.md`.
- For Core mixer detach/remove/clear orchestration, PortAudio-specific failure codes remain authoritative when available; Core fallback `MixerDetachStepFailed` (`3000`) applies only when no more specific owned code exists.
- Logging is `Microsoft.Extensions.Logging`-based and non-static.
- Hot-path logging is trace-only for per-frame operations; higher levels use aggregated/sampled reporting.
- Contracts like `IAudioEngine`, `AudioEngineConfig`, `AudioFrame`, and dense channel-map push semantics are owned by `S.Media.Core`.
- Device-selection APIs intentionally mirror OwnAudio-style flows (`Get*Devices`, `Set*DeviceByName`, `Set*DeviceByIndex`) while keeping typed device-id support.
- Engine owns discovery/lifecycle and created-output tracking; output instances own device switching APIs.
- On failure, include backend-native detail where available (`PaError`, host error text, operation context, correlation id).
- `PortAudioRuntime` and `PortAudioDeviceCatalog` responsibilities are intentionally folded into `PortAudioEngine`.
- `PortAudioOutput` represents a concrete device-bound output endpoint created by `PortAudioEngine`.
- `PortAudioInput` follows source-style pull semantics to stay close to OwnAudio source/decoder workflows.
- `PortAudioInput` read behavior is strict non-timeout pull semantics in this phase; timeout-based read APIs are intentionally not exposed.
- `PortAudioOutput` instances created by `PortAudioEngine` are caller-owned and caller-disposed.
- Callback/event dispatch policy is fixed in this phase (no module-level callback-dispatch configuration surface).
- Future evolution note: if callback latency becomes a verified issue, add a minimal dispatcher later without breaking event ordering or teardown-fence guarantees.
- Failure atomicity: failed stop/terminate/device-switch paths must not leave partially mutated output or engine state.

## Initial PortAudio Error Code Picks (`4300-4399`)
- `4300`: `PortAudioNotInitialized`
- `4301`: `PortAudioInitializeFailed`
- `4302`: `PortAudioTerminateFailed`
- `4303`: `PortAudioInvalidConfig`
- `4304`: `PortAudioStreamOpenFailed`
- `4305`: `PortAudioStreamStartFailed`
- `4306`: `PortAudioStreamStopFailed`
- `4307`: `PortAudioPushFailed`
- `4308`: `PortAudioRouteMapInvalid`
- `4309`: `PortAudioDeviceNotFound`
- `4310`: `PortAudioDeviceSwitchFailed`
- `4311`: `PortAudioReadFailed`
- `4312`: `PortAudioUnderflow`
- `4313`: `PortAudioOverflow`
- `4314`: `PortAudioHostError`
- `4315`: `PortAudioInputStartFailed`
- `4316`: `PortAudioInputStopFailed`
- `4317`: `PortAudioInputReadFailed`

## PortAudio Contract Test Matrix (Minimum)
- Lifecycle idempotency: repeated `Stop()`/`Terminate()` returns `MediaResult.Success`.
- Event ordering: `StateChanged` is FIFO per engine instance; `AudioDeviceChanged` is FIFO per output instance and reflects committed device state.
- Event teardown fence: no engine/output/input user-visible callbacks after successful `Stop()`/`Terminate()`/`Dispose()` completion.
- Input read contract: partial/zero-fill semantics remain deterministic under sustained load.
- Overflow behavior: underflow/overflow paths surface deterministic outcomes (`PortAudioUnderflow`/`PortAudioOverflow`) without unbounded buffering.

