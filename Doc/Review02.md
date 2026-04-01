# MFPlayer Framework - Review 02

> Date: 2026-04-01
> Scope: API and implementation alignment across `S.Media.*`, `PALib`, `PMLib`, `NDILib`, and `OSCLib`.
> Status: In progress (incremental updates).

## Method

- Verify API consistency across core interfaces and adapter libraries.
- Check lifecycle symmetry (`Initialize`/`Start`/`Stop`/`Dispose`) and error-code semantics.
- Spot correctness bugs, behavioural mismatches, and avoidable perf costs.
- Compare wrapper/runtime patterns in `PALib`, `PMLib`, `NDILib` for alignment.
- Cross-check with tests where available.

## Findings (rolling)

> Severity legend: Critical, High, Medium, Low

### 1) Critical - `AVMixer.StopPlayback()` does not transition mixer state to `Stopped`

- **Evidence:** `Media/S.Media.Core/Mixing/AVMixer.cs:533` calls only `StopPlaybackThreads()` and returns success. State transition logic exists in `Stop()` (`Media/S.Media.Core/Mixing/AVMixer.cs:603-609`), but `StopPlayback()` never calls it.
- **Amplifier:** EOS path in audio pump calls `StopPlayback()` (`Media/S.Media.Core/Mixing/AVMixer.cs:853-856`, `Media/S.Media.Core/Mixing/AVMixer.cs:916-919`), so natural end-of-stream can leave `State`/`IsRunning` stale.
- **Impact:** lifecycle contract drift; callers can observe `Running` after explicit stop or EOS stop.
- **Recommendation:** call `Stop()` as part of `StopPlayback()` (or in `StopPlaybackThreads()`), and avoid self-stop from pump thread (signal cancellation and exit loop).

### 2) High - EOS path calls `StopPlayback()` from inside pump thread

- **Evidence:** `AudioPumpLoop` invokes `_ = StopPlayback()` on EOS (`Media/S.Media.Core/Mixing/AVMixer.cs:855`, `Media/S.Media.Core/Mixing/AVMixer.cs:918`), and `StopPlaybackThreads()` joins `_audioPumpThread` (`Media/S.Media.Core/Mixing/AVMixer.cs:677`).
- **Impact:** self-join code smell and shutdown path complexity; makes stop semantics harder to reason about and test.
- **Recommendation:** set cancellation/request-stop and `break`; let external stop/join own thread lifecycle.

### 3) High - `FFmpeg*Source` disposed-state error codes are inconsistent with framework contract

- **Evidence:** disposed checks return `MediaInvalidArgument` in `FFmpegAudioSource` (`Media/S.Media.FFmpeg/Sources/FFmpegAudioSource.cs:89-90`, `Media/S.Media.FFmpeg/Sources/FFmpegAudioSource.cs:121-124`) and `FFmpegVideoSource` (`Media/S.Media.FFmpeg/Sources/FFmpegVideoSource.cs:95-97`, `Media/S.Media.FFmpeg/Sources/FFmpegVideoSource.cs:151-155`).
- **Contrast:** core and NDI components commonly use `MediaObjectDisposed`.
- **Impact:** inconsistent caller handling and harder cross-library error handling.
- **Recommendation:** return `MediaObjectDisposed` for disposed object operations across FFmpeg sources.

### 4) Medium - `FFmpegDecodeOptions` exposes non-functional knobs

- **Evidence:** options are documented as reserved in `Media/S.Media.FFmpeg/Config/FFmpegDecodeOptions.cs` (`EnableHardwareDecode`, `LowLatencyMode`, `UseDedicatedDecodeThread`, `DecodeThreadCount`). In session open, only queue capacities and preferred pixel format are consumed (`Media/S.Media.FFmpeg/Decoders/Internal/FFSharedDemuxSession.cs:82-83`, `:145-147`), and decode thread count is only validated (`:43-46`).
- **Impact:** API appears richer than implementation; user intent silently ignored.
- **Recommendation:** either wire options through now (codec context/worker strategy), or move them behind experimental/internal API until functional.

### 5) Medium - `NDIEngine` lifecycle contract diverges from shared engine abstraction

- **Evidence:** `NDIEngine` is `IDisposable` only (`Media/S.Media.NDI/Runtime/NDIEngine.cs:11`) while shared contract is `IMediaEngine` (`Media/S.Media.Core/Runtime/IMediaEngine.cs`). `MIDIEngine` already implements `IMediaEngine`.
- **Impact:** uneven engine composition and DI ergonomics across `S.Media.*` libraries.
- **Recommendation:** implement `IMediaEngine` on `NDIEngine` (already has `IsInitialized` + `Terminate`).

### 6) Medium - `AVMixer` hot-path uses LINQ/list allocations in update pruning and EOS checks

- **Evidence:** EOS checks use `srcs.All(...)` (`Media/S.Media.Core/Mixing/AVMixer.cs:853`, `:916`); stale key pruning uses `.Where(...).ToList()` (`Media/S.Media.Core/Mixing/AVMixer.cs:755-758`, `:770-771`).
- **Impact:** avoidable allocations in long-running realtime loops.
- **Recommendation:** replace with manual loops and two-pass remove lists backed by pooled buffers or reusable lists.

### 7) Medium - `AVMixerConfig.ResamplerFactory` signature cannot express channel-count transforms

- **Evidence:** signature is `Func<int, int, IAudioResampler>` with docs `(sourceSampleRate, targetSampleRate)` (`Media/S.Media.Core/Mixing/AVMixerConfig.cs:121-131`), and mixer uses only those two args (`Media/S.Media.Core/Mixing/AVMixer.cs:813-815`).
- **Impact:** resampler creation cannot adapt when source/output channel counts differ; hidden coupling to `SourceChannelCount`.
- **Recommendation:** extend delegate to include channel counts or pass a single context struct.

### 8) Medium - `PortAudioInput.ReadSamples` applies gain internally, which can double-apply volume in mixer pipelines

- **Evidence:** capture path multiplies by `Volume` (`Media/S.Media.PortAudio/Input/PortAudioInput.cs:183-185`), while mixer also applies per-source `src.Volume` during mixing (`Media/S.Media.Core/Mixing/AVMixer.cs:829`, `:838`).
- **Impact:** effective gain may become `Volume^2` for common mixer usage.
- **Recommendation:** centralize gain at mixer/consumer layer; avoid source-side gain in `ReadSamples` (or clearly document differentiated semantics).

### 9) Low - `PortAudioEngine.Initialize` uses the same error code for double-init and native init failure

- **Evidence:** both disposed and already-initialized paths return `PortAudioInitializeFailed` (`Media/S.Media.PortAudio/Engine/PortAudioEngine.cs:85-94`).
- **Impact:** callers cannot distinguish misuse vs environment/runtime failure.
- **Recommendation:** return a distinct code for lifecycle misuse (e.g. concurrent operation/state violation).

### 10) Medium - OSC option `IgnoreTimeTagScheduling` is documented but not used in server behavior

- **Evidence:** option exists in `OSCServerOptions` docs (`OSC/OSCLib/OSCOptions.cs:104`), but `OSCServer.DispatchPacketAsync` always dispatches immediately and never branches on option (`OSC/OSCLib/OSCServer.cs:139-156`).
- **Impact:** API contract mismatch; users cannot rely on option semantics.
- **Recommendation:** either implement scheduling branch or remove/rename option to reflect current behavior explicitly.

### 11) Test gap - no focused `AVMixer` stop-state regression coverage found

- **Evidence:** quick search in `Media/S.Media.Core.Tests` found no `StopPlayback`/`IsRunning` assertions.
- **Impact:** critical lifecycle regressions (items #1/#2) are easy to reintroduce.
- **Recommendation:** add deterministic tests for:
  - `StopPlayback()` => `State == Stopped`, `IsRunning == false`
  - EOS auto-stop => same state guarantees
  - stop called from pump context does not self-join or deadlock

### 12) High - `OSCServer` oversize `Throw` policy can terminate the receive loop

- **Evidence:** oversize handling throws in `HandleOversizePacket` when policy is `Throw` (`OSC/OSCLib/OSCServer.cs:161-163`). That call is outside the receive try/catch that handles socket errors (`OSC/OSCLib/OSCServer.cs:95-111`), so exception escapes `ReceiveLoopAsync`.
- **Impact:** a single oversized datagram can fault the server loop task and stop further packet processing.
- **Recommendation:** catch/log oversize exceptions inside loop and keep server alive, or redefine `Throw` as per-packet callback/error event rather than loop-fatal behavior.

### 13) Medium - `OSCServerOptions.IgnoreTimeTagScheduling` is currently a no-op

- **Evidence:** option is documented in `OSC/OSCLib/OSCOptions.cs:104`, but `DispatchPacketAsync` always dispatches immediately (`OSC/OSCLib/OSCServer.cs:139-156`) with no option branch.
- **Impact:** API indicates behavior control that does not exist.
- **Recommendation:** either implement timetag-aware scheduling when option is false or remove/rename the option to avoid misleading contract.

### 14) Medium - `NDIEngine.CreateAudioSource/CreateVideoSource` do not validate `receiver` nullability

- **Evidence:** `CreateMediaItem` uses `ArgumentNullException.ThrowIfNull(receiver)` (`Media/S.Media.NDI/Runtime/NDIEngine.cs:116`), but `CreateAudioSource`/`CreateVideoSource` do not (`Media/S.Media.NDI/Runtime/NDIEngine.cs:131-183`).
- **Impact:** null receiver can flow into coordinator dictionary path and fail with less actionable exceptions.
- **Recommendation:** add `ArgumentNullException.ThrowIfNull(receiver)` for both factory methods to align behavior with `CreateMediaItem`.

### 15) Medium - `PMUtil.ChannelMask` docs specify 0-15, but implementation does not enforce range

- **Evidence:** docs state channel range 0-15 (`MIDI/PMLib/PMUtil.cs:170-171`), implementation is raw shift `1 << channel` (`MIDI/PMLib/PMUtil.cs:180`).
- **Impact:** out-of-range channels produce undefined/incorrect masks silently (including sign-bit behavior for large shifts).
- **Recommendation:** validate range and throw `ArgumentOutOfRangeException` (or add `TryChannelMask`).

### 16) Low - wrapper lifecycle alignment is good across native resolver modules

- **Evidence:** `PALib`, `PMLib`, and `NDILib` each install DllImport resolvers via module initializers (`Audio/PALib/Runtime/PALibModuleInit.cs`, `MIDI/PMLib/Runtime/PMLibModuleInit.cs`, `NDI/NDILib/Runtime/NDILibModuleInit.cs`) with idempotent `Install(...)` implementations.
- **Positive impact:** consistent bootstrap pattern reduces first-call native load failures and improves cross-library predictability.
- **Recommendation:** keep this as the baseline pattern for additional native-backed libraries.

### 17) Low - `MIDIEngine` fallback behavior is robust but should be explicitly documented as "initialized without native runtime"

- **Evidence:** `Initialize` sets `IsInitialized = true` regardless of native availability, then seeds synthetic devices when native init fails (`Media/S.Media.MIDI/Runtime/MIDIEngine.cs:38-42`, `:229-233`).
- **Impact:** behavior is valid but surprising if callers equate `IsInitialized` with native backend presence.
- **Recommendation:** document this explicitly and optionally expose a `NativeBackendAvailable` property.

### 18) High - `SDL3VideoView` does not implement `VideoOutputBackpressureMode.Wait`

- **Evidence:** standalone enqueue path only handles `DropOldest`; any other mode returns `VideoOutputBackpressureQueueFull` (`Media/S.Media.OpenGL.SDL3/SDL3VideoView.cs:1625-1637`). `Wait` exists in core enum (`Media/S.Media.Core/Video/VideoOutputBackpressureMode.cs:3-8`).
- **Impact:** API contract mismatch; callers selecting `Wait` do not get wait semantics.
- **Recommendation:** implement bounded wait using `BackpressureTimeout`/`BackpressureWaitFrameMultiplier`, or reject unsupported mode at `Start()` validation.

### 19) Medium - `SDL3VideoView` has known macOS event-thread violation in render loop

- **Evidence:** explicit TODO: SDL events should be pumped on main thread on macOS, but current code pumps in render thread (`Media/S.Media.OpenGL.SDL3/SDL3VideoView.cs:1611-1613`).
- **Impact:** platform instability/risk on macOS standalone mode.
- **Recommendation:** route event pumping to main thread (or use platform-specific dispatch abstraction) before claiming macOS parity.

### 20) Medium - `OpenGLVideoEngine.PushFrame` allocates via LINQ on every frame push

- **Evidence:** clone list materialization uses `Select`/`Where`/`ToArray` (`Media/S.Media.OpenGL/OpenGLVideoEngine.cs:194-199`) on hot path.
- **Impact:** avoidable allocations/GC pressure during high-FPS output.
- **Recommendation:** replace with manual loop into reusable buffer/list.

### 21) Medium - `MIDIReconnectOptions.DisconnectGracePeriod` is dead configuration

- **Evidence:** option exists and is normalized (`Media/S.Media.MIDI/Config/MIDIReconnectOptions.cs:7`, `:20`) but no usage in `MIDIInput`/`MIDIOutput` reconnect paths.
- **Impact:** misleading API knob with no behavioural effect.
- **Recommendation:** either implement grace handling in disconnect/reconnect flow or remove/deprecate property until active.

### 22) Medium - `MIDIInput` event handler exceptions can kill polling thread silently

- **Evidence:** `MessageReceived` invoked inline inside polling loop without try/catch (`Media/S.Media.MIDI/Input/MIDIInput.cs:161-163`).
- **Impact:** one consumer exception can terminate receive loop unexpectedly without controlled status transition.
- **Recommendation:** wrap handler invocation, surface error via status event/logging, continue polling unless fatal.

### 23) Low - `IMIDIDevice.StatusChanged` docs mention states that do not exist

- **Evidence:** docs mention `closing` and `error` states (`Media/S.Media.MIDI/Types/IMIDIDevice.cs:30`), but enum has only `Closed/Opening/Open/Disconnected/Reconnecting/ReconnectFailed` (`Media/S.Media.MIDI/Types/MIDIConnectionStatus.cs:3-11`).
- **Impact:** documentation drift and consumer confusion.
- **Recommendation:** align docs with actual enum, or add missing states if intended.

### 24) Test gap - no coverage found for SDL3 backpressure `Wait` semantics or MIDI callback-fault resilience

- **Evidence:** OpenGL adapter tests do not exercise `BackpressureMode.Wait` (`Media/S.Media.OpenGL.Tests/SDL3AdapterTests.cs`), and MIDI tests do not appear to assert callback exception resilience.
- **Impact:** behavioural regressions in queue pressure and reconnect/error handling can ship unnoticed.
- **Recommendation:** add tests for:
  - SDL3 queue-full behavior in `DropOldest` vs `Wait`
  - MIDI input poll loop resilience when `MessageReceived` handler throws

### 25) Medium - `NDIRuntime` lifetime wrapper is not reference-counted across multiple scopes

- **Evidence:** each `NDIRuntime.Create(...)` calls `NDIlib_initialize` and returns an independent wrapper; each instance `Dispose()` unconditionally calls `NDIlib_destroy` (`NDI/NDILib/NDIRuntime.cs:44-63`).
- **Reference context:** SDK notes in `Reference/NDI/include/Processing.NDI.Lib.h:110-120` describe initialize/destroy as global lifetime hints.
- **Impact:** if multiple runtime scopes are created, disposing one can tear down NDI globally while other wrappers still exist.
- **Recommendation:** guard with static ref-count (first create initializes, last dispose destroys), or make `Create` singleton-returning.

### 26) Medium - FFmpeg tests do not cover disposed-object error semantics

- **Evidence:** `Media/S.Media.FFmpeg.Tests/FFmpegAudioSourceTests.cs` and `Media/S.Media.FFmpeg.Tests/FFmpegVideoSourceTests.cs` cover seek/read/concurrency paths but include no assertions for disposed operations.
- **Impact:** current disposed-code inconsistency in FFmpeg sources can regress unnoticed.
- **Recommendation:** add explicit tests for `Start/Stop/Read/Seek` after `Dispose` and assert unified `MediaObjectDisposed` behavior.

### 27) Medium - `NDIEngine` null-receiver guards are not tested (and currently missing in factory methods)

- **Evidence:** NDI tests in `Media/S.Media.NDI.Tests/NDIEngineAndOptionsTests.cs` and `Media/S.Media.NDI.Tests/NDISourceAndMediaItemTests.cs` do not assert null-receiver argument handling for `CreateAudioSource` / `CreateVideoSource`.
- **Impact:** argument-contract drift can surface as runtime faults instead of deterministic argument exceptions.
- **Recommendation:** add tests for null receiver inputs and align factory methods to `CreateMediaItem` guard pattern.

### 28) Low - Test apps ignore some push return codes in hot loops

- **Evidence:** `Test/NDISendTest/Program.cs:136` calls `ndiAudioSink.PushFrame(...)` without checking return code.
- **Impact:** failures in audio send path can be silent during diagnostics.
- **Recommendation:** capture and report non-success codes (with semantic mapping) similarly to video push path.

### 29) Medium - Public stop/lifecycle methods block, but blocking behavior is not consistently documented

- **Evidence:** stop paths join threads with timeouts (e.g., `AVMixer` joins up to 4s in `Media/S.Media.Core/Mixing/AVMixer.cs:677-679`; MIDI input joins poll thread in `Media/S.Media.MIDI/Input/MIDIInput.cs:105`).
- **Reference contrast:** `Reference/OwnAudio/Source/Engine/AudioEngineWrapper.cs` explicitly documents blocking `Stop()` and provides `StopAsync()` guidance.
- **Impact:** UI callers may freeze unexpectedly when calling stop/dispose on UI thread.
- **Recommendation:** add explicit blocking remarks to public lifecycle APIs and consider async stop variants where practical.

### 30) Low - Reflection-heavy tests for NDI internals are brittle and tie refactors to private field names

- **Evidence:** `Media/S.Media.NDI.Tests/NDISourceAndMediaItemTests.cs` accesses private members like `_audioRing`, `_videoJitterQueue`, and private methods via reflection (`:228-294`).
- **Impact:** internal cleanup/refactors can cause test churn without behavioral regression.
- **Recommendation:** prefer behavior-level assertions through public diagnostics/contracts; keep minimal reflection only where no contract surface exists.

### 31) Test gap - no targeted parity tests found for `NDIRuntime` multi-instance lifecycle

- **Evidence:** existing NDI tests focus on `NDIEngine` and source options; no coverage found for multiple `NDIRuntime.Create` scopes interacting.
- **Impact:** global runtime teardown races can remain hidden.
- **Recommendation:** add tests for nested/multiple runtime scopes (create A/create B/dispose A/use B) to define expected behavior explicitly.
