# S.Media.PortAudio API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

This module is a concrete backend for Core audio engine contracts, with an OwnAudio-like push sink model and explicit per-channel routing support.

### `Output/PortAudioOutput.cs`
- `sealed class PortAudioOutput : IAudioOutput, IDisposable`
- Planned API:
  - `int Start(AudioOutputConfig config)`
  - `int Stop()`
  - `int PushFrame(in AudioFrame frame, in AudioChannelRouteMap routeMap)`
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
  - `int PushFrame(in AudioFrame frame, in AudioChannelRouteMap routeMap)`
  - `int SetOutputDevice(AudioDeviceId deviceId)`
  - `IReadOnlyList<AudioDeviceInfo> GetOutputDevices()`
  - `IReadOnlyList<AudioDeviceInfo> GetInputDevices()`
  - `int SetOutputDeviceByName(string deviceName)`
  - `int SetOutputDeviceByIndex(int deviceIndex)`
  - `int SetInputDeviceByName(string deviceName)`
  - `int SetInputDeviceByIndex(int deviceIndex)`
  - `AudioDeviceId? ActiveOutputDevice { get; }`
  - `int CreateOutput(AudioDeviceId deviceId, out PortAudioOutput? output)`
  - `int CreateOutputByName(string deviceName, out PortAudioOutput? output)`
  - `int CreateDefaultOutput(out PortAudioOutput? output)`
  - `event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged`
  - `event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged`

### `Input/PortAudioInput.cs`
- `sealed class PortAudioInput : IAudioSource, IDisposable`
- Planned API:
  - `int Start(AudioInputConfig config) // config includes optional LiveReadTimeoutOptions`
  - `int Stop()`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)`
  - `int ReadSamples(Span<float> destination, int requestedFrameCount, TimeSpan timeout, out int framesRead)`
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
- Every push carries an explicit `AudioChannelRouteMap`; there is no static engine route-map state.
- Default caller strategy in higher layers should favor mono/separated channels until final output mapping.
- Route-map validation is strict and deterministic: invalid map returns non-zero error code, with no partial push.
- Per-channel gain/mute is route-level metadata, not global engine state.
- One-to-many fan-out is supported directly in the engine route map.
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
- Timeout overload returns partial data with `MediaResult.Success` when any samples arrive before timeout.
- Timeout overload returns `MediaSourceReadTimeout` only when no samples arrive before timeout.
- `TimeSpan.Zero` timeout is non-blocking poll; negative timeout returns `MediaInvalidArgument`.

## Notes
- Backend is `Audio/PALib`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Doc/error-codes.md`.
- Logging is `Microsoft.Extensions.Logging`-based and non-static.
- Hot-path logging is trace-only for per-frame operations; higher levels use aggregated/sampled reporting.
- Contracts like `IAudioEngine`, `AudioEngineConfig`, `AudioFrame`, and `AudioChannelRouteMap` are owned by `S.Media.Core`.
- Device-selection methods intentionally mirror OwnAudio-style flows (`Get*Devices`, `Set*DeviceByName`, `Set*DeviceByIndex`) while keeping typed device-id support.
- On failure, include backend-native detail where available (`PaError`, host error text, operation context, correlation id).
- `PortAudioRuntime` and `PortAudioDeviceCatalog` responsibilities are intentionally folded into `PortAudioEngine`.
- `PortAudioOutput` represents a concrete device-bound output endpoint created by `PortAudioEngine`.
- `PortAudioInput` follows source-style pull semantics to stay close to OwnAudio source/decoder workflows.
- `PortAudioOutput` instances created by `PortAudioEngine` are caller-owned and caller-disposed.

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

