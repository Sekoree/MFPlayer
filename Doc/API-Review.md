# S.Media.* API Consistency Review

> **Date:** March 28, 2026
> **Scope:** All projects under `Media/S.Media.*`, plus native wrapper libraries (`PALib`, `NDILib`, `PMLib`)
> **Branch note:** Experimental API-breaking release branch. All recommendations may break existing call-sites and are intended to be resolved before the next stable release.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Core Interfaces — Audio & Video](#2-core-interfaces--audio--video)
3. [Mixer (AudioVideoMixer)](#3-mixer-audiovideomixer)
4. [S.Media.FFmpeg](#4-smediaffmpeg)
5. [S.Media.PortAudio](#5-smediaportaudio)
6. [S.Media.NDI](#6-smedianidi)
7. [S.Media.OpenGL & SDL3](#7-smediaopengl--sdl3)
8. [S.Media.OpenGL.Avalonia](#8-smediaopenglavalonia)
9. [S.Media.MIDI](#9-smediamidi)
10. [Error Code System](#10-error-code-system)
11. [Native Wrapper Libraries](#11-native-wrapper-libraries)
12. [Cross-Cutting Concerns](#12-cross-cutting-concerns)
13. [OSC Library](#13-osc-library-osclib)
14. [Test Programs](#14-test-programs)
15. [Prioritised Fix Roadmap](#15-prioritised-fix-roadmap)

---

## 1. Executive Summary

The framework has a solid structural foundation: clear source/output split, integer error-code returns, `IDisposable` everywhere, and a coherent per-source `Guid` identity model. However, several layers of historical debt have accumulated:

- **Drift-correction scaffolding** was added to `AudioVideoMixerDebugInfo` and `AudioVideoMixerConfig` but never implemented. Eight always-zero fields litter every diagnostic snapshot and actively mislead developers reading console output.
- `AudioVideoMixer` has a **confusing two-step start protocol** (`Start()` + `StartPlayback(config)`) with subtly different semantics that callers consistently get wrong.
- `NDIVideoOutput` **pushes audio via `PushAudio()`** but only implements `IVideoOutput` — the audio capability is invisible to all interface-based consumers, and the mixer cannot route audio to it. The NDI send test works around this with a hand-rolled A/V loop.
- **`AudioRoutingRule.Gain`** is declared and documented but **never applied** in the mix loop, making the entire per-route gain system silently non-functional.
- `IVideoOutput.Start()` and `IAudioOutput.Start()` take type-specific config objects, yet `IAudioOutput` additionally imposes a full **device-management API** (`SetOutputDevice*`, `AudioDeviceChanged`) on every audio output — making NDI and other non-device sinks unnecessarily complex to implement.
- `OpenGLVideoEngine.AddOutput(IVideoOutput)` **silently rejects** any non-`OpenGLVideoOutput` at runtime, violating the Liskov Substitution Principle.
- `VideoPresenterSyncPolicy` hard-codes `audioLedMaxWaitMs = 50.0`, **ignoring** `VideoPresenterSyncPolicyOptions.MaxWait` entirely.
- The three native wrapper libraries (**PALib**, **NDILib**, **PMLib**) have inconsistent logging depth, visibility conventions, platform resolution strategies, and error-signalling approaches.

---

## 2. Core Interfaces — Audio & Video

### 2.1 `IAudioOutput` — device-management bloat

**Problem:** Every class implementing `IAudioOutput` must expose `SetOutputDevice(AudioDeviceId)`, `SetOutputDeviceByName(string)`, `SetOutputDeviceByIndex(int)`, `AudioDeviceChanged`, and `Device` — concepts that only apply to physical audio playback devices. `NDIVideoOutput` (which also pushes audio), and any hypothetical network or file audio sink, cannot meaningfully implement these.

**Recommendation:** Split into two interfaces:

```csharp
/// Minimal capability required to receive pushed audio frames.
public interface IAudioSink : IDisposable
{
    Guid Id { get; }
    AudioOutputState State { get; }
    int Start(AudioOutputConfig config);
    int Stop();
    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex);
    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount);
}

/// Extended interface for physical device-backed outputs.
public interface IAudioOutput : IAudioSink
{
    AudioDeviceInfo Device { get; }
    int SetOutputDevice(AudioDeviceId deviceId);
    int SetOutputDeviceByName(string deviceName);
    int SetOutputDeviceByIndex(int deviceIndex);
    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}
```

`IAudioVideoMixer` would accept `IAudioSink` instead of `IAudioOutput`. `PortAudioOutput` continues to implement the full `IAudioOutput`. `NDIVideoOutput` implements `IAudioSink`. This is a **breaking change** but the cleanest path.

---

### 2.2 `IVideoOutput` — asymmetry with `IAudioOutput`

**Problem:** `IVideoOutput` has no `State` property and no `VideoOutputState`-equivalent, while `IAudioOutput` does. For parity and diagnostic purposes, outputs should be queryable without casting.

**Recommendation:** Add `VideoOutputState` enum and `State` property to `IVideoOutput`:

```csharp
public enum VideoOutputState { Stopped = 0, Running = 1 }

public interface IVideoOutput : IDisposable
{
    Guid Id { get; }
    VideoOutputState State { get; }  // ADD
    int Start(VideoOutputConfig config);
    int Stop();
    int PushFrame(VideoFrame frame);
    int PushFrame(VideoFrame frame, TimeSpan presentationTime);
}
```

`IVideoOutput.PushFrame` blocking behaviour should also be documented in the XML doc — some implementations (NDI with `ClockVideo=true`, PortAudio via `Pa_WriteStream`) are synchronously blocking; others (`SDL3VideoView`) are non-blocking queue-and-return. The mixer should be able to discover this without reading source.

---

### 2.3 `IAudioSource` vs `IVideoSource` — seek asymmetry

**Problem:** `IVideoSource` has `SeekToFrame(long)`, `SeekToFrame(long, out long, out long?)`, `CurrentFrameIndex`, `CurrentDecodeFrameIndex`, and `TotalFrameCount`. `IAudioSource` only has `Seek(double)` with no frame-index seeking. The original OwnAudio reference `IAudioSource` has `Duration` and `IsEndOfStream`; the current `IAudioSource` has `DurationSeconds` (NaN when unknown) but `IsEndOfStream` is absent — callers have no reliable way to detect end-of-file without inspecting the return code of `ReadSamples`.

**Recommendation:**
- Add `bool IsEndOfStream { get; }` to `IAudioSource`.
- Add `long? TotalSampleCount { get; }` to `IAudioSource` for symmetry with `IVideoSource.TotalFrameCount`.
- Consider removing the two-`out`-parameter overload of `SeekToFrame` from `IVideoSource` — callers can query `CurrentFrameIndex` and `TotalFrameCount` directly after seeking.

---

### 2.4 `IVideoSource.StreamInfo` is missing

**Problem:** `IAudioSource` exposes `AudioStreamInfo StreamInfo { get; }`, but `IVideoSource` has no `StreamInfo` property. Stream metadata (codec, width, height, frame rate) is available on concrete types only.

**Recommendation:** Add `VideoStreamInfo StreamInfo { get; }` to `IVideoSource`.

---

### 2.5 `IAudioSource.SourceId` vs `IVideoOutput.Id` — naming inconsistency

**Problem:** Sources use `SourceId` (both `IAudioSource` and `IVideoSource`). Outputs use `Id` (both interfaces). The OwnAudio reference uses `Id` everywhere.

**Recommendation:** Standardise on `Id` throughout. Rename `IAudioSource.SourceId` → `IAudioSource.Id` and `IVideoSource.SourceId` → `IVideoSource.Id`. Update all mixers and routing rules that key on `SourceId`.

---

### 2.6 `IAudioOutput.PushFrame` signature overhead for non-routing consumers

**Problem:** `IAudioOutput.PushFrame` requires a `ReadOnlySpan<int> sourceChannelByOutputIndex` route map on every call. This is noise for the common case where channels are correctly mapped. For NDI audio output, the route map is irrelevant — NDI sends all channels verbatim.

**Recommendation:** Add a convenience overload `int PushFrame(in AudioFrame frame)` that uses an identity route map derived from `frame.SourceChannelCount`. Keep the full overload for the mixer's internal use.

---

## 3. Mixer (`AudioVideoMixer`)

### 3.1 Two-step start protocol is confusing

**Problem:** The mixer has:
- `Start()` — transitions state machine to `Running`, starts the `IMediaClock`.
- `StartPlayback(AudioVideoMixerConfig)` — starts audio/video pump threads, internally calls `Start()` if not already running.

In practice `StartPlayback` always does the right thing alone, but the `Start`/`Stop`/`Pause`/`Resume` lifecycle is also independently usable. The interface `IAudioVideoMixer` exposes both sets, leading to confusion about which to call.

**Recommendation:** Remove the public `Start()`/`Stop()`/`Pause()`/`Resume()` from `IAudioVideoMixer`. Keep them `protected` or `internal` on `AudioVideoMixer`. The user-visible lifecycle should be `StartPlayback` / `StopPlayback` / `PausePlayback` / `ResumePlayback`.

---

### 3.2 `TickVideoPresentation()` is a dead no-op

**Problem:** `IAudioVideoMixer` declares `TimeSpan TickVideoPresentation()` with the summary "No-op – kept for interface compatibility." The implementation returns `TimeSpan.Zero` unconditionally. In `AVMixerTest`, this is called in the main loop:

```csharp
var tickDelay = mixer.TickVideoPresentation(); // always TimeSpan.Zero
var sleepMs = tickDelay <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(...);
Thread.Sleep(sleepMs); // always sleeps exactly 1ms — a 1kHz busy loop
```

The main thread busy-loops at 1 ms intervals doing nothing useful, while presentation is handled by the mixer's internal threads.

**Recommendation:** Remove `TickVideoPresentation()` from `IAudioVideoMixer` and `AudioVideoMixer`. Rewrite the test main loop to wait on a cancellation signal instead.

---

### 3.3 `AudioRoutingRule.Gain` is declared but never applied

**Problem:** `AudioRoutingRule` has a `Gain` field (`float Gain = 1.0f`) and documentation says it allows "per-route volume control". However, the `AudioVideoMixer.AudioPumpLoop` never reads `Gain` — the entire per-route gain system is silently non-functional. Users who configure gain rules will see no effect.

**Recommendation:**
1. **Short term:** Add `[Obsolete]` or a prominent XML doc warning to `AudioRoutingRule.Gain`, or remove it entirely.
2. **Long term:** Implement per-source, per-output gain in the audio mix loop.

---

### 3.4 `ISupportsAdvancedRouting` is not part of `IAudioVideoMixer`

**Problem:** `ISupportsAdvancedRouting` is implemented by `AudioVideoMixer` but not included in `IAudioVideoMixer`. Callers must downcast: `((ISupportsAdvancedRouting)mixer).AddAudioRoutingRule(...)`. Routing is a fundamental mixer capability.

**Recommendation:** Extend `IAudioVideoMixer` to include `ISupportsAdvancedRouting`:

```csharp
public interface IAudioVideoMixer : ISupportsAdvancedRouting
{
    // ...existing members...
}
```

---

### 3.5 Ghost drift-correction fields in `AudioVideoMixerDebugInfo`

**Problem:** Eight fields in `AudioVideoMixerDebugInfo` are permanently zero: `DriftMs`, `CorrectionSignalMs`, `CorrectionStepMs`, `CorrectionOffsetMs`, `CorrectionResyncCount`, `LeadMinMs`, `LeadAvgMs`, `LeadMaxMs`. They are remnants of a drift-correction system that was removed or never completed. `AVMixerTest` actively prints them, misleading developers:

```csharp
$"drift={d.DriftMs:F1}ms corr={d.CorrectionSignalMs:F1}ms"
// always prints: "drift=0.0ms corr=0.0ms"
```

**Recommendation:** Remove all 8 ghost fields. The `VideoWorker*` fields are actually populated and should stay. Proposed clean record:

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
    int  VideoWorkerMaxQueueDepth);
```

---

### 3.6 `AudioVideoMixerConfig` — dual routing systems and partial mutability

**Problem:** `AudioVideoMixerConfig` carries a flat global `RouteMap` with factory helpers (`ForStereo()`, `ForSourceToStereo()`, `ForPassthrough()`). The `AudioRoutingRule` system in `ISupportsAdvancedRouting` is completely separate and the two don't interact. Additionally, `OutputSampleRate` has a mutable `set` accessor while most other config properties use `init`, making `AudioVideoMixerConfig` partially mutable post-construction in practice.

**Recommendation:**
- Separate concerns into `AudioPumpConfig { SourceChannelCount, RouteMap, OutputSampleRate, AudioReadFrames }` and `VideoPumpConfig { VideoDecodeQueueCapacity, ... }`.
- Make all properties `init`-only; update callers to use `with` for mutation.

---

### 3.7 Single active video source — poor multi-source story

**Problem:** The mixer supports multiple `IVideoSource` registrations but dispatches only the "active" one (`_activeVideoSourceId`). `VideoRoutingRule` maps source→output, but the decode loop reads only from the single active source. Routing output A to source 1 and output B to source 2 is impossible.

**Recommendation:** Document the limitation explicitly on `IAudioVideoMixer.AddVideoSource` and `SetActiveVideoSource`. Long term, the video decode loop should drive per-source decode queues keyed by routing rules.

---

### 3.8 `GetAudioSourcesSnapshot()` allocates on every mix iteration

**Problem:** `AudioPumpLoop` calls `GetAudioSourcesSnapshot()` on every iteration, allocating a new `List<>` approximately 1024 times per second. The method also acquires `_gate` each time.

**Recommendation:** Cache the snapshot and invalidate it with a version counter:

```csharp
var lastVersion = -1;
List<(IAudioSource, double)> srcs = [];

while (!ct.IsCancellationRequested) {
    if (_audioSourcesVersion != lastVersion) {
        srcs = GetAudioSourcesSnapshot();
        lastVersion = _audioSourcesVersion;
    }
    // use srcs...
}
```

Increment `_audioSourcesVersion` in `AddAudioSource` / `RemoveAudioSource`.

---

### 3.9 `VideoPresenterSyncPolicy` ignores `MaxWait`

**Problem:** `VideoPresenterSyncPolicyOptions` has a `MaxWait` field (default 2 ms), but the `AudioLed` branch uses a hard-coded `const double audioLedMaxWaitMs = 50.0`. The `Realtime` path also caps at 50 ms inline. The options record exists but is only partially respected.

**Recommendation:**

```csharp
var maxWaitMs = options.MaxWait > TimeSpan.Zero
    ? options.MaxWait.TotalMilliseconds
    : 50.0;
```

Also make `VideoPresenterSyncPolicyOptions` `public` (currently `internal`) and expose it via `AudioVideoMixerConfig.PresenterSyncOptions`.

---

### 3.10 `MediaPlayer` introduces a separate `_playerGate` lock

**Problem:** `MediaPlayer` has `_playerGate` alongside `AudioVideoMixer._gate`. `MediaPlayer.Play()` acquires `_playerGate` then calls `AddAudioSource` / `AddVideoSource` which acquire `_gate`. If any code holds `_gate` and calls into `MediaPlayer`, a deadlock results.

**Recommendation:** Remove `_playerGate`; expose `_gate` as `protected` from `AudioVideoMixer` and use it directly in `MediaPlayer`, or ensure the base class is never called while `_playerGate` is held.

---

### 3.11 `IMediaPlayer.Play(IMediaItem)` source-binding gap

**Problem:** `MediaPlayer.Play(IMediaItem)` only attaches sources when the item implements `IMediaPlaybackSourceBinding`. A plain `IMediaItem` triggers `StartPlayback` with no sources attached, silently producing silence/no-video. The interface contract does not document this distinction.

**Recommendation:** Either require `IMediaItem` to implement `IMediaPlaybackSourceBinding`, or return an error code when it cannot:

```csharp
if (media is not IMediaPlaybackSourceBinding)
    return (int)MediaErrorCode.MediaInvalidArgument;
```

---

## 4. S.Media.FFmpeg

### 4.1 Silent stub frames when no session exists

**Problem:** `FFVideoSource.ReadFrame()` returns a hardcoded 2×2 RGBA placeholder when `_sharedDemuxSession` is null; `FFAudioSource.ReadSamples()` returns `Success` with `framesRead = 0`. These stubs exist for standalone test construction but mask misconfiguration in production.

**Recommendation:** Remove the stub paths. A source without a session should return a dedicated error code (`FFmpegSessionNotAttached`). For test isolation, the existing `FFAudioSource(double durationSeconds)` constructor (which explicitly produces silence) is sufficient.

---

### 4.2 `FFAudioSource` does not advance position when session is absent

**Problem:** When `_sharedDemuxSession` is null, `ReadSamples` returns `Success` with 0 frames and never advances `_positionSeconds`. The mixer advances its timeline regardless, creating A/V desync. This is a downstream consequence of §4.1.

---

### 4.3 `FFmpegOpenOptions.EnableExternalClockCorrection` is a dead flag

**Problem:** `FFmpegOpenOptions` has `EnableExternalClockCorrection` but no code reads or uses it. It is another remnant of the removed drift-correction system.

**Recommendation:** Remove from `FFmpegOpenOptions`.

---

### 4.4 `FFVideoSource.SeekToFrame` converts via frame-rate heuristics

**Problem:** `SeekToFrame(long frameIndex)` converts to seconds via `targetSeconds = frameIndex / fps` where `fps` defaults to 30 if unknown. This is a lossy conversion — VFR content accumulates error on long seeks. After seeking, `_currentFrameIndex` is set to `frameIndex` but the actual decode position may differ.

**Recommendation:** Expose native frame-accurate seek through `FFSharedDemuxSession`. Until then, return `MediaErrorCode.MediaSourceNonSeekable` when `StreamInfo.FrameRate` is not known, and document the heuristic prominently.

---

### 4.5 `FFVideoSource` and `FFAudioSource` seek coordination is fragile

**Problem:** Both sources call `_sharedDemuxSession.Seek()` independently. Seeking via one source may flush decode buffers shared by both. The session's internal seek-lock semantics are opaque to callers.

**Recommendation:** Provide `FFMediaItem.Seek(double)` as the canonical seek point and coordinate internally inside `FFSharedDemuxSession`.

---

### 4.6 `FFAudioChannelMappingPolicy` has unused `OutputChannelCountOverride`

**Problem:** `FFAudioSourceOptions.OutputChannelCountOverride` is declared but `FFAudioSource.TryGetEffectiveChannelMap()` does not read it.

**Recommendation:** Implement `OutputChannelCountOverride` in `TryGetEffectiveChannelMap`, or remove it.

---

### 4.7 `FFMediaItem` — dual API surface creates nullability traps

**Problem:** `FFMediaItem` exposes both a concrete shortcut (`FFAudioSource? AudioSource`) and the interface-typed list (`IReadOnlyList<IAudioSource> PlaybackAudioSources`). `AudioSource` can be null when `PlaybackAudioSources` is non-empty (composite constructor path). Callers who check `media.AudioSource is null` silently miss sources.

**Recommendation:** Mark `AudioSource` and `VideoSource` with XML doc warnings, or restrict them to the URI-open path only. For the composite constructor path, expose only `PlaybackAudioSources` / `PlaybackVideoSources`.

---

### 4.8 `FFMediaItem.Open()` throws — inconsistent with the rest of the framework

**Problem:** `FFMediaItem.Open(uri)` throws `DecodingException`. This is the only framework type that throws from its factory. The rest of the framework uses integer return codes consistently.

**Recommendation:** Make the constructor `internal`. Expose a static factory:

```csharp
public static int Create(FFmpegOpenOptions options, out FFMediaItem? item)
```

`FFMediaItem.TryOpen` exists but is less discoverable.

---

## 5. S.Media.PortAudio

### 5.1 `PortAudioEngine` emits fake fallback devices before `Initialize()`

**Problem:** The constructor creates two fake output devices (`"Default Output"` / `"Monitor Output"`) and a fake `"fallback"` host API before `Initialize()` is called. Callers enumerating devices on an uninitialized engine see phantom devices.

**Recommendation:** Return an empty list from `GetOutputDevices()` / `GetInputDevices()` when `State == AudioEngineState.Uninitialized`.

---

### 5.2 `PortAudioOutput` blocks indefinitely on `Pa_WriteStream` retries

**Problem:** The write loop retries on `paTimedOut` or `paOutputUnderflowed` with a 1 ms sleep but no overall timeout. If the native stream is stuck (device removed during write), the audio pump thread blocks indefinitely.

**Recommendation:** Add a configurable per-push timeout (e.g. `AudioEngineConfig.WriteTimeoutMs`). After timeout, return `MediaErrorCode.PortAudioPushFailed`.

---

### 5.3 `IAudioEngine.CreateOutput*` `out` parameter — inconsistency with `NDIEngine`

**Problem:** `IAudioEngine.CreateOutput(AudioDeviceId, out IAudioOutput? output)` returns the output via `out IAudioOutput?`. `NDIEngine.CreateOutput` uses the same pattern but returns `out NDIVideoOutput?` (a concrete type). The `out` parameter type is inconsistent across projects.

**Recommendation:** Adopt one consistent approach — either tuple return or `out` parameter — applied uniformly. Document it as the "S.Media factory convention".

---

### 5.4 `AudioEngineConfig` stale copies per `PortAudioOutput`

**Problem:** `AudioEngineConfig` is stored on `PortAudioEngine.Config` and copied into every `PortAudioOutput` constructor. If the engine is re-initialized with new config, existing outputs retain the old one.

**Recommendation:** Either have `PortAudioOutput` hold a reference to the engine's live config, or explicitly document that existing outputs are not affected by re-initialization.

---

## 6. S.Media.NDI

### 6.1 `NDIVideoOutput` implements `IVideoOutput` only — should also implement `IAudioSink`

**Problem:** `NDIVideoOutput` has a fully functional `PushAudio(in AudioFrame, TimeSpan)` method, internal audio staging buffer, `_audioPushSuccesses`/`_audioPushFailures` counters, and `NDIOutputOptions.EnableAudio`. But it only implements `IVideoOutput`. The result:
- `AudioVideoMixer` cannot route audio to it (requires `IAudioOutput` / `IAudioSink`).
- `PushAudio` is invisible to any interface-based consumer.
- The signature `PushAudio(in AudioFrame, TimeSpan)` doesn't match `IAudioOutput.PushFrame(in AudioFrame, ReadOnlySpan<int>, int)`.

The `NDISendTest` works around this with a hand-rolled A/V loop (see §14.3), confirming this is an active blocker.

**Recommendation (aligned with §2.1):**
1. Introduce `IAudioSink` (§2.1).
2. Change `NDIVideoOutput` to implement both `IVideoOutput` and `IAudioSink`.
3. Rename `PushAudio` → `PushFrame` to match `IAudioSink.PushFrame`.
4. Add `IAudioSink.Start(AudioOutputConfig)` delegating to the shared internal start.

```csharp
public sealed class NDIVideoOutput : IVideoOutput, IAudioSink
{
    // IVideoOutput
    public int Start(VideoOutputConfig config) { ... }
    public int PushFrame(VideoFrame frame) { ... }
    public int PushFrame(VideoFrame frame, TimeSpan pts) { ... }

    // IAudioSink
    int IAudioSink.Start(AudioOutputConfig config) => _running ? MediaResult.Success : StartInternal();
    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap) { ... }
}
```

---

### 6.2 `NDIVideoOutput.Start()` no-arg overload is non-standard

**Problem:** `public int Start()` calls `Start(new VideoOutputConfig())` and is not part of `IVideoOutput`. Since `NDIOutputOptions.ClockVideo`/`ClockAudio` are set at construction time, the config passed here is inert. No other output exposes a no-arg `Start()`.

**Recommendation:** Remove the no-arg `Start()`.

---

### 6.3 `NDIVideoOutput.PushFrame` holds `_gate` during `NDISender.SendVideo()` native call

**Problem:** `PushFrame(VideoFrame, TimeSpan)` holds `_gate` throughout `PushFrameCore`, which calls `NDISender.SendVideo()` — a native call. With `NDIOutputOptions.ClockVideo = true`, `SendVideo` blocks internally for a full frame interval (~33 ms at 30 fps). During this time `_gate` is held, blocking `Stop()`, `Dispose()`, and all diagnostic reads for the duration of every frame.

**Recommendation:** Capture the sender reference under the lock, then release before the native call:

```csharp
public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
{
    NDISender? sender;
    lock (_gate)
    {
        if (_disposed || !_running) { _videoPushFailures++; return ErrorCode; }
        sender = _sender;
    }
    // native call outside lock:
    return PushFrameCore(frame, presentationTime, sender);
}
```

Apply the same pattern to `PushAudio` / `PushAudioCore`.

---

### 6.4 `_stagingBuffer` and `_audioStagingBuffer` grow but never shrink

**Problem:** `EnsureStagingBuffer` only reallocates when the required size exceeds the current length. If a 1080p frame is pushed once, the staging buffer stays at 1080p permanently even if 480p frames follow. For variable-resolution sources, `ArrayPool<byte>` would be more GC-friendly.

---

### 6.5 `NDIIntegrationOptions.RequireAudioPathOnStart` duplicated in `NDIOutputOptions`

**Problem:** Both option types have `RequireAudioPathOnStart`. `NDIEngine.CreateOutput` does not propagate the engine-level flag to the output, making the engine-level version inert.

**Recommendation:** Remove `RequireAudioPathOnStart` from `NDIIntegrationOptions`; it belongs only in `NDIOutputOptions`.

---

### 6.6 `NDIVideoSource` / `NDIAudioSource` double coordinator bug in public constructors

**Problem:** The public constructors `NDIVideoSource(NDIMediaItem, NDISourceOptions)` and `NDIAudioSource(NDIMediaItem, NDISourceOptions)` each create a **new** `NDICaptureCoordinator`. Creating both from the same `NDIMediaItem` via the public constructors results in two independent coordinators, doubling the capture rate and breaking A/V frame correlation.

The internal constructors (used by `NDIMediaItem.CreateAudioSource`) correctly share the coordinator. The public constructors do not.

**Recommendation:** Mark the public constructors `internal`, or fix them to reuse the item's existing coordinator:

```csharp
public NDIVideoSource(NDIMediaItem mediaItem, NDISourceOptions options)
    : this(mediaItem, options, mediaItem.CaptureCoordinator)
{
}
```

---

### 6.7 `NDIVideoOutput` ignores `VideoOutputConfig` passed to `Start()`

**Problem:** `NDIVideoOutput.Start(VideoOutputConfig config)` validates the config but ignores all of `BackpressureMode`, `QueueCapacity`, `PresentationMode`, etc. NDI frame pacing is governed by `NDIOutputOptions.ClockVideo`, not by the framework's `VideoOutputConfig`.

**Recommendation:** Document explicitly that `VideoOutputConfig` is ignored by NDI output:

```csharp
// NDI frame pacing is controlled by NDIOutputOptions.ClockVideo; VideoOutputConfig is intentionally ignored.
_ = config;
```

---

### 6.8 `NDIEngine` coordinator tracking is opaque to callers

**Problem:** The `NDICaptureCoordinator` shared between audio/video sources from the same receiver is hidden inside a private dictionary. Callers must trust that coordinator sharing works — it only does if they use the factory methods, not the public constructors.

**Recommendation:** Expose a `CreateMediaItem(NDIReceiver, NDISourceOptions, out NDIMediaItem?)` factory on `NDIEngine`. The `NDIMediaItem` is then passed to `CreateAudioSource`/`CreateVideoSource`, making the receiver↔coordinator relationship transparent.

---

## 7. S.Media.OpenGL & SDL3

### 7.1 `OpenGLVideoEngine.AddOutput(IVideoOutput)` silently rejects non-GL outputs

**Problem:**

```csharp
public int AddOutput(IVideoOutput output)
{
    if (output is not OpenGLVideoOutput glOutput)
        return (int)MediaErrorCode.MediaInvalidArgument;
    // ...
}
```

Callers receive a generic `MediaInvalidArgument` with no indication of why.

**Recommendation:** Change the parameter type to `OpenGLVideoOutput`:

```csharp
public int AddOutput(OpenGLVideoOutput output) { ... }
```

---

### 7.2 `OpenGLVideoOutput` clone graph is managed on the output itself

**Problem:** The clone graph (`IsClone`, `CloneParentOutputId`, `CloneOutputIds`) is managed on the output, leaking engine-level concerns into the output type. `OpenGLVideoEngine` also manages a separate clone graph, creating two registries for the same concept.

**Recommendation:** Move clone graph management entirely into `OpenGLVideoEngine`. `OpenGLVideoOutput` should not know about parent/child IDs.

---

### 7.3 `VideoOutputPresentationMode.VSync` has no implementation

**Problem:** `VideoOutputConfig.PresentationMode` has a `VSync = 3` value that no output class handles. An unimplemented `VSync` silently falls through to `Unlimited` behaviour, potentially causing tearing.

**Recommendation:** Implement VSync gating or remove the enum value until it is ready.

---

### 7.4 Timeline anchor logic duplicated between mixer and `OpenGLVideoOutput`

**Problem:** `OpenGLVideoOutput` has its own `_hasTimelineAnchor`, `_anchorPtsSeconds`, `_anchorTicks`, and related fields to normalise presentation timestamps. The mixer's `VideoPresenterSyncPolicy` also handles timestamp monotonicity. When both are active, timestamps are normalised twice.

**Recommendation:** Centralise timestamp normalisation in the mixer. `IVideoOutput.PushFrame(VideoFrame, TimeSpan)` should always receive a normalised, clock-relative timestamp.

---

### 7.5 `SDL3VideoView` re-implements clone management internally

**Problem:** `SDL3VideoView` has its own `Dictionary<Guid, SDL3VideoView> _clones` parallel to `OpenGLVideoEngine`'s clone graph. If a `SDL3VideoView` is added to an `OpenGLVideoEngine`, two independent registries can diverge.

**Recommendation:** `SDL3VideoView` should delegate clone management entirely to the `OpenGLVideoEngine` it wraps. The internal `_clones` dictionary should be removed.

---

### 7.6 `SDL3VideoView` wraps `OpenGLVideoOutput` adding a triple-layer dispatch

**Problem:** `SDL3VideoView` implements `IVideoOutput` by internally constructing an `OpenGLVideoOutput` and `OpenGLVideoEngine`. The `PushFrame` path is:

```
Caller → SDL3VideoView (IVideoOutput)
              └→ OpenGLVideoOutput (IVideoOutput)
                       └→ OpenGLVideoEngine (manages outputs)
```

Three layers of queue/dispatch per frame push. The internal `OpenGLVideoOutput` is inaccessible, so its diagnostics are invisible.

**Recommendation:** `SDL3VideoView` should own the GL context and upload logic directly, removing the internal `OpenGLVideoOutput` wrapper. Or, expose the inner `OpenGLVideoOutput` as a public diagnostic property.

---

### 7.7 `SDL3VideoView.PushFrame` is non-blocking but `IVideoOutput` does not document this contract

**Problem:** Some `IVideoOutput` implementations are synchronously blocking (NDI with `ClockVideo=true`, PortAudio via `Pa_WriteStream`). `SDL3VideoView.PushFrame` is non-blocking — it enqueues and returns immediately. The mixer will see misleadingly fast returns masking rendering bottlenecks.

**Recommendation:** Add `bool IsNonBlocking { get; }` to `IVideoOutput`, or document blocking semantics in the XML doc, so the mixer can adapt its scheduling accordingly.

---

## 8. S.Media.OpenGL.Avalonia

### 8.1 `AvaloniaVideoOutput` throws in constructor on engine registration failure

**Problem:** The `AvaloniaVideoOutput` constructor throws `InvalidOperationException` if `_engine.AddOutput(_output)` fails:

```csharp
var add = _engine.AddOutput(_output);
if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
    throw new InvalidOperationException($"Failed to register output. Code={add}.");
```

This is inconsistent with the framework's integer error code convention.

**Recommendation:** Expose a static factory method returning an error code:

```csharp
public static int Create(out AvaloniaVideoOutput? output) { ... }
```

---

### 8.2 `AvaloniaVideoOutput` and `SDL3VideoView` independently duplicate the engine-wrapping pattern

Both wrap `OpenGLVideoOutput` + `OpenGLVideoEngine` and both suffer from the clone graph duplication described in §7.2 and §7.5. They should share a common base adapter class rather than independently duplicating the wrapping logic.

---

## 9. S.Media.MIDI

### 9.1 `MIDIEngine` has no `IMediaEngine` interface

**Problem:** `PortAudioEngine` implements `IAudioEngine`. `NDIEngine` and `MIDIEngine` follow a recognisable `Initialize/Terminate/Create*` pattern but implement no interface. This prevents dependency injection, mocking, and consistent lifecycle management.

**Recommendation:** Define a minimal engine interface:

```csharp
public interface IMediaEngine : IDisposable
{
    bool IsInitialized { get; }
    int Terminate();
}
```

---

### 9.2 `MIDIInput` and `MIDIOutput` have no common `IMIDIDevice` interface

**Problem:** Both share properties (`Device`, `IsOpen`) and methods (`Open()`, `Close()`, `Dispose()`). There is no shared interface.

**Recommendation:**

```csharp
public interface IMIDIDevice : IDisposable
{
    MIDIDeviceInfo Device { get; }
    bool IsOpen { get; }
    int Open();
    int Close();
}
```

---

### 9.3 MIDI has no entry in the `S.Media.Core` interface hierarchy

**Problem:** `PortAudioInput` is an `IAudioSource`. `MIDIInput` uses `Open()` / `Close()` instead of `Start()` / `Stop()`. MIDI events have no counterpart in the core interface model, preventing MIDI from being composed into A/V pipelines.

**Recommendation:** Add a lightweight `IMidiEventSource` (or `IEventSource<MidiMessage>`) to `S.Media.Core` so MIDI feeds can be composed similarly to audio/video sources.

---

## 10. Error Code System

### 10.1 `MediaErrorArea` lacks entries for PortAudio, OpenGL, SDL3, MIDI

**Problem:** `ResolveArea()` maps PortAudio, OpenGL, SDL3, and MIDI ranges to `OutputRender` or `GenericCommon` without distinct enum values. Diagnostics code cannot distinguish "PortAudio error" from "OpenGL error" using `ResolveArea`.

**Recommendation:**

```csharp
public enum MediaErrorArea
{
    Unknown      = 0,
    GenericCommon = 1,
    Playback     = 2,
    Decoding     = 3,
    Mixing       = 4,
    OutputRender = 5,  // keep as generic fallback
    NDI          = 6,
    PortAudio    = 7,  // ADD
    OpenGL       = 8,  // ADD
    MIDI         = 9,  // ADD
    SDL3         = 10, // ADD
}
```

---

### 10.2 `MediaResult.Success` and `MediaErrorCode.Success = 0` coexist

**Problem:** Two definitions of success exist side by side. `MediaErrorCode.Success = 0` on an error-code enum is semantically odd. Comparisons like `result == (int)MediaErrorCode.Success` vs. `result == MediaResult.Success` are equivalent but confusing.

**Recommendation:** Remove `MediaErrorCode.Success = 0`. All error codes should be non-zero failures. `MediaResult.Success = 0` is sufficient for comparisons.

---

### 10.3 NDI read-rejection codes mapped to wrong semantic

**Problem:** `ErrorCodeRanges.ResolveSharedSemantic` maps `NDIAudioReadRejected` and `NDIVideoReadRejected` to `MediaConcurrentOperationViolation`. NDI read rejection isn't necessarily concurrent — it can also mean the source is stopped.

**Recommendation:** Return `MediaErrorCode.MediaSourceNotRunning` (add this) when the source is stopped. Reserve `NDIAudioReadRejected`/`NDIVideoReadRejected` for the concurrent-read case only.

---

### 10.4 `Stop()` on disposed objects returns `Success` — inconsistent with `Start()`

**Problem:** `Stop()` returns `MediaResult.Success` when the object is disposed. But `Start()` on a disposed object returns a domain-specific error. Callers cannot distinguish "object was disposed" from "operation failed".

**Recommendation:** Add `MediaErrorCode.MediaObjectDisposed`. Both `Start()` and `Stop()` should return it on a disposed object.

---

## 11. Native Wrapper Libraries

This section covers the three native P/Invoke wrappers: **PALib** (PortAudio), **NDILib** (NDI SDK), and **PMLib** (PortMidi). Each is a separate project in the solution that `S.Media.*` libraries depend on.

---

### 11.1 PALib (PortAudio)

#### 11.1.1 `Native.cs` is `public` — inconsistent with NDILib

PALib exposes all P/Invoke bindings directly through `public static partial class Native`. Consumers can call `PALib.Native.Pa_WriteStream` directly. NDILib's `Native.cs` is `internal`, hidden behind typed wrappers (`NDIFinder`, `NDIReceiver`, `NDISender`). PMLib's `Native.cs` is also `public`. No consistent policy exists.

**Recommendation:** Make `PALib.Native` `internal` and expose a typed wrapper layer (analogous to NDILib's wrappers) as the public surface. This hides raw P/Invoke behind a managed abstraction and allows logging/error handling to be added at the boundary layer.

---

#### 11.1.2 `PALibLogging.TraceCall` boxes value types on every call regardless of log level

`TraceCall` signature:

```csharp
public static void TraceCall(ILogger logger, string method, params (string Name, object? Value)[] args)
{
    if (!logger.IsEnabled(LogLevel.Trace)) return;
    // ...
}
```

The `params (string, object?)[]` array is heap-allocated **before** the method body checks `IsEnabled`. Any value-type argument (e.g. `PaError`, `int`) is boxed into `object?` at each call site regardless of whether trace logging is enabled. The identical issue exists in `NDILibLogging.TraceCall` and `PMLibLogging.TraceCall` — all three share the same pattern.

**Recommendation:** Use an `IsEnabled` guard at the call site, or source-generated `LoggerMessage.Define`:

```csharp
// Zero-allocation when trace is disabled:
if (Logger.IsEnabled(LogLevel.Trace))
    Logger.LogTrace("{Method}()", nameof(Pa_Initialize));

// Or source-generated:
[LoggerMessage(Level = LogLevel.Trace, Message = "Pa_Initialize()")]
private static partial void LogPaInitialize(ILogger logger);
```

---

#### 11.1.3 `Pa_OpenStream` and `Pa_IsFormatSupported` use heap allocation for optional structs

Both methods allocate two `Marshal.AllocHGlobal` blocks per call — even when both parameters are `null`:

```csharp
// Always allocates 2 × SizeOf<PaStreamParameters>() bytes on the heap:
var inPtr  = Marshal.AllocHGlobal(Marshal.SizeOf<PaStreamParameters>());
var outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaStreamParameters>());
```

**Recommendation:** Use `unsafe` fixed pinning to avoid heap allocation:

```csharp
public static PaError Pa_IsFormatSupported(
    PaStreamParameters? inputParameters,
    PaStreamParameters? outputParameters,
    double sampleRate)
{
    PaStreamParameters inParam  = inputParameters  ?? default;
    PaStreamParameters outParam = outputParameters ?? default;
    unsafe
    {
        nint pIn  = inputParameters.HasValue  ? (nint)(&inParam)  : nint.Zero;
        nint pOut = outputParameters.HasValue ? (nint)(&outParam) : nint.Zero;
        return Pa_IsFormatSupported_Import(pIn, pOut, sampleRate);
    }
}
```

---

#### 11.1.4 `PortAudioLibraryResolver` must be called manually — no automatic registration

`PortAudioLibraryResolver.Register()` must be called before any `Native.*` use. A call to any native method before `Register()` falls back to the OS default loader. There is no enforcement of this ordering.

**Recommendation:** Register automatically via `[ModuleInitializer]`:

```csharp
internal static class PALibModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() => PortAudioLibraryResolver.Register();
}
```

---

#### 11.1.5 `Pa_Sleep` is exposed publicly

`public static void Pa_Sleep(nint msec)` wraps PortAudio's internal sleep. PortAudio's sleep is intended for use inside C callback contexts. Exposing it in the public `Native` class encourages its use in managed code where `Thread.Sleep` or `Task.Delay` are universally preferable.

**Recommendation:** Remove from the public API surface, or mark `[EditorBrowsable(EditorBrowsableState.Never)]` with an XML doc warning.

---

#### 11.1.6 Tracing is non-uniformly applied

`Pa_GetHostApiCount`, `Pa_GetDefaultHostApi`, `Pa_GetDeviceCount`, `Pa_GetStreamTime`, `Pa_GetStreamCpuLoad`, and stream start/stop methods have no `TraceCall` instrumentation, while `Pa_Initialize`, `Pa_Terminate`, `Pa_OpenStream`, and `Pa_GetHostApiInfo` do. The selection appears ad-hoc.

**Recommendation:** Adopt a clear rule: trace all lifecycle calls (`Initialize`, `Terminate`, `Open*`, `Close*`, `Start*`, `Stop*`); log failures at Debug for all stream I/O calls; omit tracing for pure-query calls (`GetDeviceCount`, `GetStreamTime`).

---

### 11.2 NDILib (NDI SDK)

#### 11.2.1 Hard-coded `"libndi.so.6"` — Linux-only

```csharp
private const string LibraryName = "libndi.so.6";
```

The NDI SDK ships under different names per platform: `Processing.NDI.Lib.x64.dll` on Windows, `libndi.dylib` on macOS, `libndi.so.6` on Linux. The current hard-code makes `NDILib` fail to load on Windows and macOS. Unlike PALib, there is no `NativeLibrary.SetDllImportResolver` equivalent.

**Recommendation:** Add an `NDILibraryResolver` registered via `[ModuleInitializer]`:

```csharp
private static nint Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
{
    var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new[] { "Processing.NDI.Lib.x64", "Processing.NDI.Lib.x86" }
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new[] { "libndi", "libndi.dylib" }
            : new[] { "libndi.so.6", "libndi" };
    foreach (var name in candidates)
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
            return handle;
    return nint.Zero;
}
```

---

#### 11.2.2 `Native.cs` is `internal` — inconsistent with PALib and PMLib

`NDILib.Native` is `internal` while `PALib.Native` and `PMLib.Native` are both `public`. The NDILib approach is architecturally better (see §11.1.1), but the inconsistency across the three wrappers is confusing.

---

#### 11.2.3 `NDIRuntimeScope`, `NDIFinder`, `NDISender`, `NDIFrameSync` all throw on creation failure

All four constructors throw `InvalidOperationException` when the native create call returns a null pointer:

```csharp
_instance = Native.NDIlib_send_create(create);
if (_instance == nint.Zero)
    throw new InvalidOperationException("Failed to create NDI sender instance.");
```

This is inconsistent with the S.Media.* framework convention of integer return codes. `using var scope = new NDIRuntimeScope()` requires a surrounding try/catch, whereas `PortAudioEngine.Initialize()` returns a checkable code.

**Recommendation:** Replace throwing constructors with factory methods:

```csharp
public static int Create(string? name, bool clockVideo, bool clockAudio, out NDISender? sender)
{
    sender = null;
    var ptr = Native.NDIlib_send_create(create);
    if (ptr == nint.Zero) return (int)NDIErrorCode.NDISenderCreateFailed;
    sender = new NDISender(ptr);
    return MediaResult.Success;
}
```

---

#### 11.2.4 `NDICaptureScope` has no default case for `None`/`Error`/`StatusChange`

If `NDIReceiver.Capture()` returns `NdiFrameType.None` (timeout), `Error`, or `StatusChange`, the scope's `Dispose()` correctly skips freeing, but no error is surfaced. The caller must check `scope.FrameType` before accessing frame data — but this requirement is not documented.

**Recommendation:** Add explicit XML documentation on `NDICaptureScope` stating which `FrameType` values indicate "no frame was captured" and that `Video`/`Audio`/`Metadata` should only be accessed when `FrameType` matches.

---

#### 11.2.5 No failure logging on `NDIlib_recv_capture_v3` — hot-path blind spot

PALib logs `Pa_ReadStream` and `Pa_WriteStream` failures at Debug level. NDILib has no equivalent for `NDIlib_recv_capture_v3`, the hot-path capture call. Capture errors (`NdiFrameType.Error`) will silently manifest as "no frame captured" in `NDICaptureCoordinator`.

**Recommendation:** Add a Debug-level log in `NDIReceiver.Capture()` when `frameType == NdiFrameType.Error`.

---

### 11.3 PMLib (PortMidi)

#### 11.3.1 `Native.cs` has no call tracing

PALib traces key native calls in `Native.cs`. PMLib's `Native.cs` has no tracing at all. `PMLibLogging` exists and is used in the `MIDIDevice` base class, but only at the high-level wrapper layer, not the native call layer.

**Recommendation:** Add Debug-level failure logging to `Pm_Read`, `Pm_Write`, and `Pm_Poll` (in `Native.cs` or in the `MIDIInputDevice`/`MIDIOutputDevice` callers), mirroring PALib's approach.

---

#### 11.3.2 No `PortMidiLibraryResolver`

PMLib relies on the platform default loader for `"portmidi"`. The `Native.cs` XML doc documents the expected library names correctly, but there is no `NativeLibrary.SetDllImportResolver` for version probing. On platforms shipping `libportmidi.so.2`, loading fails silently.

**Recommendation:** Add a `PortMidiLibraryResolver` matching PALib's pattern, registered via `[ModuleInitializer]`.

---

#### 11.3.3 `PMUtil.GetAllDevices()` uses deferred `IEnumerable` with `yield`

```csharp
public static IEnumerable<(int Id, PmDeviceInfo Info)> GetAllDevices()
{
    int count = Native.Pm_CountDevices();
    for (int i = 0; i < count; i++)
    {
        var ptr = Native.Pm_GetDeviceInfo(i);
        if (ptr != nint.Zero)
            yield return (i, Marshal.PtrToStructure<PmDeviceInfo>(ptr));
    }
}
```

If `Native.Pm_Terminate()` is called while the caller is enumerating, subsequent `Pm_GetDeviceInfo` calls in the generator will access freed memory.

**Recommendation:** Materialise immediately:

```csharp
public static IReadOnlyList<(int Id, PmDeviceInfo Info)> GetAllDevices()
{
    int count = Native.Pm_CountDevices();
    var results = new List<(int, PmDeviceInfo)>(count);
    for (int i = 0; i < count; i++)
    {
        var ptr = Native.Pm_GetDeviceInfo(i);
        if (ptr != nint.Zero)
            results.Add((i, Marshal.PtrToStructure<PmDeviceInfo>(ptr)));
    }
    return results;
}
```

---

#### 11.3.4 `MIDIDevice` constructor eagerly copies device info — good defensive pattern

`MIDIDevice(int deviceId)` eagerly copies the device name and interface strings at construction time "so they remain valid after `Pm_Terminate`". This is the correct defensive pattern and should be the model for future device-info caching in PALib and NDILib wrappers.

---

### 11.4 Cross-Wrapper Logging Consistency

All three logging support classes (`PALibLogging`, `NDILibLogging`, `PMLibLogging`) share an identical structure: static `ILoggerFactory`, `Configure(ILoggerFactory?)`, `GetLogger(string)`, and `TraceCall(ILogger, string, params (string, object?)[])`. Despite structural similarity, their instrumentation depth diverges:

| | PALib | NDILib | PMLib |
|---|---|---|---|
| Logging class | `PALibLogging` | `NDILibLogging` | `PMLibLogging` |
| Tracing at `Native.cs` layer | ✅ Key lifecycle calls | ❌ None | ❌ None |
| Failure logging in `Native.cs` | ✅ `Pa_Read/WriteStream` | ❌ None | ❌ None |
| Logging at wrapper layer | ✅ | ✅ Debug on create/dispose | ✅ `MIDIDevice` only |
| `Configure()` must be called manually | ✅ | ✅ | ✅ |
| `TraceCall` boxes value types | ⚠️ Yes | ⚠️ Yes | ⚠️ Yes |

**Recommendations:**
1. Extract a single `MediaLibLogging` helper into a shared source-linked file or small shared project, eliminating the three near-identical implementations.
2. Replace `params (string, object?)[]` with the `IsEnabled` guard pattern (§11.1.2).
3. Add a single bootstrap entry point so callers don't need three separate `Configure` calls:

```csharp
public static class MediaNativeLogging
{
    public static void Configure(ILoggerFactory? factory)
    {
        PALibLogging.Configure(factory);
        NDILibLogging.Configure(factory);
        PMLibLogging.Configure(factory);
    }
}
```

---

## 12. Cross-Cutting Concerns

### 12.1 Two NDILib implementations in the same solution

**Problem:** The solution has `/NDI/NdiLib/` and `/NDI/NDILib/` directories. `S.Media.NDI` references `NDILib`. `NdiLib` contains only `bin/` and `obj/` subdirectories — no source files. The empty shell remains confusing and risks future drift.

**Recommendation:** Delete the empty `NdiLib` directory.

---

### 12.2 `MediaMetadataSnapshot.AdditionalMetadata` is untyped `Dictionary<string, string>`

**Problem:** Common metadata (title, artist, album, duration, stream count) is not addressable in a type-safe way. `IDynamicMetadata` only exposes an event, not a current-value query.

**Recommendation:**
- Add well-known fields to `MediaMetadataSnapshot`: `Title`, `Artist`, `Album`, `Year` as nullable strings.
- Add `MediaMetadataSnapshot? GetMetadata()` to `IDynamicMetadata` for polling current metadata without waiting for an event.

---

### 12.3 `AudioFrame` is `readonly record struct` but `VideoFrame` is a `class` with ref-counting

**Problem:** The asymmetry is intentional but creates an inconsistency in the A/V mix loop. `AudioFrame` is passed by `in` ref. `VideoFrame` must call `AddRef()` and `Dispose()` explicitly — the `AddRef` / `_refCount` pattern is non-standard in C# and easy to misuse (forgetting `AddRef` before enqueuing into a worker is a silent correctness bug).

**Recommendation:** Evaluate `IMemoryOwner<byte>` for `VideoFrame` pixel planes to remove manual reference counting. Alternatively, adopt a `VideoFrameRef` wrapper enforcing single-owner semantics. At minimum, document the `AddRef` contract prominently on `VideoFrame`.

---

### 12.4 Mixed error-handling strategies — constructors throw, factories return codes

**Problem:** `FFMediaItem.Open()` throws `DecodingException`. `AvaloniaVideoOutput` throws `InvalidOperationException`. `NDIRuntimeScope`, `NDIFinder`, `NDISender`, `NDIFrameSync` throw `InvalidOperationException`. `PortAudioEngine.Initialize()` returns an error code. Callers must both catch and check.

**Recommendation:** Standardise on integer error codes everywhere. Make all throwing constructors `internal` and expose static factory methods returning error codes (see §4.8 and §11.2.3 for examples).

---

### 12.5 `AudioVideoMixer.Seek()` restarts playback threads unconditionally

**Problem:** `Seek()` calls `StopPlaybackThreads()` then `StartPlaybackThreads()` if playback was active. The audio pump thread join has a 4-second timeout. A full thread teardown-and-restart on every seek creates an audible dropout and makes UI scrubbing unusable.

**Recommendation:** Send a seek command through a `Channel<double> _seekChannel` that the pump threads consume asynchronously. The audio pump can drain its mix buffer and re-position without a full restart.

---

### 12.6 Engine-level output enumeration is inconsistent across projects

**Problem:** `IAudioEngine.Outputs` returns `IReadOnlyList<IAudioOutput>` and `PortAudioEngine` implements it. `NDIEngine` stores `List<NDIVideoOutput> _outputs` but exposes no `Outputs` property.

**Recommendation:** Add an `Outputs` property to `NDIEngine` (or to the proposed `IMediaEngine` from §9.1).

---

### 12.7 `VideoPresenterSyncPolicyOptions` is `internal`

**Problem:** `VideoPresenterSyncPolicyOptions` controls stale-frame thresholds, early-frame tolerance, and min/max waits. It is `internal`. The options partially overlapping with `AudioVideoMixerConfig` do not provide full coverage.

**Recommendation:** Make `VideoPresenterSyncPolicyOptions` public and expose it via `AudioVideoMixerConfig.PresenterSyncOptions`.

---

## 13. OSC Library (`OSCLib`)

### 13.1 `OSCLib` uses MEL; S.Media.* does not

`OSCClient` accepts `ILogger<OSCClient>?` and uses `NullLogger` as a default. No `S.Media.*` project uses `Microsoft.Extensions.Logging`. Note that PALib/NDILib/PMLib do use MEL — so if the native wrappers unify under MEL (§11.4), this inconsistency within `S.Media.*` itself becomes the remaining gap.

**Recommendation:** If MEL is adopted uniformly across `S.Media.*`, standardise `OSCLib` as well. If not, remove MEL from `OSCLib` and use the same diagnostic event pattern as `S.Media.*`.

---

### 13.2 `OSCClient`/`OSCServer` have no `IMediaEngine` lifecycle pattern

Unlike `PortAudioEngine`, `NDIEngine`, and `MIDIEngine`, `OSCClient` and `OSCServer` are directly constructed and disposed. This is the more idiomatic .NET approach for network clients. No change needed unless OSC is to be composed into a managed media pipeline.

---

### 13.3 `OSCPacketCodec` allocates `byte[]` on every send

OSC packets are encoded to a new `byte[]` per send. `OSCArgs` uses boxed `object` values. For high-frequency control messages (A/V automation at 100 Hz+), this generates GC pressure. Minor for typical OSC use cases but worth noting for sync-critical A/V automation.

---

## 14. Test Programs

### 14.1 `AVMixerTest` prints always-zero drift fields

```csharp
$"drift={d.DriftMs:F1}ms corr={d.CorrectionSignalMs:F1}ms"
// always prints: "drift=0.0ms corr=0.0ms"
```

Both fields are permanently zero (ghost fields from §3.5). This is in active test code, actively misleading developers reading the console.

---

### 14.2 `AVMixerTest` calls `TickVideoPresentation()` in a 1 kHz busy loop

```csharp
TestHelpers.RunWithDeadline(a.Seconds, () =>
{
    var tickDelay = mixer.TickVideoPresentation(); // always TimeSpan.Zero
    var sleepMs = tickDelay <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(...);
    Thread.Sleep(sleepMs); // always 1ms
    return true;
}, ...);
```

`TickVideoPresentation()` always returns `TimeSpan.Zero` (§3.2), so this loop does nothing useful at 1000 iterations per second. The presentation is handled entirely by the mixer's internal threads.

---

### 14.3 `NDISendTest` hand-rolls A/V coordination — confirms §6.1

The NDI send test manually reads audio frames, calls `ndiOutput.PushAudio(...)`, reads video frames, and calls `ndiOutput.PushFrame(...)` in a tight hand-rolled loop because the mixer cannot use `NDIVideoOutput` as an audio output. This is the clearest in-production demonstration of the `IAudioSink` gap. Once §6.1 is resolved, this test's bespoke loop can be replaced by a standard `AudioVideoMixer` setup.

---

## 15. Prioritised Fix Roadmap

### Phase 1 — Low-risk, no behavioural change

| # | Issue | Action |
|---|-------|--------|
| P1.1 | §3.5 Ghost drift fields | Remove 8 zero fields from `AudioVideoMixerDebugInfo` |
| P1.2 | §3.2 `TickVideoPresentation` no-op | Delete from interface and implementation; rewrite test loop |
| P1.3 | §4.3 Dead `EnableExternalClockCorrection` | Remove from `FFmpegOpenOptions` |
| P1.4 | §4.6 Unused `OutputChannelCountOverride` | Implement or remove |
| P1.5 | §10.2 `MediaErrorCode.Success` | Remove `Success = 0` from the enum |
| P1.6 | §12.1 Empty `NdiLib` directory | Delete |
| P1.7 | §6.2 No-arg `NDIVideoOutput.Start()` | Remove |
| P1.8 | §3.9 `MaxWait` ignored in sync policy | Replace hardcoded 50 ms with `options.MaxWait` |
| P1.9 | §6.3 `PushFrame` holds lock during native send | Capture sender ref before lock; release before native call |
| P1.10 | §3.6 `OutputSampleRate` mutable setter | Make all `AudioVideoMixerConfig` properties `init`-only |
| P1.11 | §6.5 `RequireAudioPathOnStart` duplication | Remove from `NDIIntegrationOptions` |
| P1.12 | §11.1.5 `Pa_Sleep` public exposure | Remove from public API or mark `[EditorBrowsable(Never)]` |
| P1.13 | §11.1.6 Non-uniform tracing | Adopt clear tracing rule; add failure logs to NDILib capture |
| P1.14 | §11.3.3 `PMUtil.GetAllDevices()` deferred enum | Materialise to `IReadOnlyList<>` |
| P1.15 | §11.4 Three separate `Configure()` calls | Add `MediaNativeLogging.Configure(factory)` bootstrap |
| P1.16 | §11.1.4 `PortAudioLibraryResolver` not automatic | Register via `[ModuleInitializer]` |

### Phase 2 — Interface corrections (breaking changes, call-site updates required)

| # | Issue | Action |
|---|-------|--------|
| P2.1 | §2.5 `SourceId` → `Id` | Rename on both source interfaces |
| P2.2 | §2.4 `IVideoSource.StreamInfo` | Add to `IVideoSource` |
| P2.3 | §2.3 `IAudioSource.IsEndOfStream` | Add `IsEndOfStream` and `TotalSampleCount` to `IAudioSource` |
| P2.4 | §3.4 Routing in `IAudioVideoMixer` | Merge `ISupportsAdvancedRouting` into `IAudioVideoMixer` |
| P2.5 | §7.1 OpenGL `AddOutput` type | Change parameter to `OpenGLVideoOutput` |
| P2.6 | §2.2 `IVideoOutput.State` | Add `VideoOutputState` enum and `State` to `IVideoOutput` |
| P2.7 | §3.11 `IMediaPlayer.Play` source gap | Change parameter to `IMediaPlaybackSourceBinding` |
| P2.8 | §4.7 `FFMediaItem` nullability traps | Add XML doc warnings; remove concrete shortcuts for composite path |
| P2.9 | §7.5 `SDL3VideoView` duplicate clone dict | Remove internal `_clones`; delegate to `OpenGLVideoEngine` |
| P2.10 | §10.1 `MediaErrorArea` missing entries | Add `PortAudio`, `OpenGL`, `MIDI`, `SDL3` enum values |
| P2.11 | §10.4 `Stop()` on disposed objects | Add `MediaObjectDisposed` error code; return from both `Start` and `Stop` |
| P2.12 | §9.1 `IMediaEngine` interface | Define and apply across all engines |
| P2.13 | §9.2 `IMIDIDevice` interface | Define shared MIDI device interface |

### Phase 3 — Architecture refactoring (large-scope, most impactful)

| # | Issue | Action |
|---|-------|--------|
| P3.1 | §2.1 `IAudioSink` split | Introduce `IAudioSink`; update all audio outputs and the mixer |
| P3.2 | §6.1 `NDIVideoOutput` audio compliance | Implement `IAudioSink`; rename `PushAudio` → `PushFrame` |
| P3.3 | §3.1 Two-step start protocol | Consolidate `Start`/`StartPlayback` lifecycle |
| P3.4 | §3.6 `AudioVideoMixerConfig` concern split | Split into `AudioPumpConfig` + `VideoPumpConfig` |
| P3.5 | §3.7 Multi-source video decode | Per-source decode queues keyed by routing rules |
| P3.6 | §3.3 `AudioRoutingRule.Gain` | Implement gain in mix loop, or remove the field |
| P3.7 | §12.5 Seek thread restart | Async seek via `Channel<double>` command |
| P3.8 | §4.5 Seek coordination in FFmpeg | Move to `FFSharedDemuxSession.Seek()` |
| P3.9 | §6.8 NDI coordinator transparency | Add `CreateMediaItem` factory to `NDIEngine` |
| P3.10 | §12.4 Mixed error-handling strategies | Standardise: all public factories return int codes |
| P3.11 | §12.3 `VideoFrame` ref counting | Evaluate `IMemoryOwner<byte>` / `VideoFrameRef` |
| P3.12 | §4.8 `FFMediaItem.Open()` throws | Static factory with error code; constructor `internal` |
| P3.13 | §7.6 `SDL3VideoView` triple-layer dispatch | Own GL context directly; remove internal `OpenGLVideoOutput` |
| P3.14 | §13.1 OSC/S.Media logging unification | Standardise on MEL or remove MEL from OSCLib |
| P3.15 | §11.1.1 PALib `Native.cs` visibility | Make `internal`; expose typed wrapper layer |
| P3.16 | §11.2.1 NDILib Linux-only binding | Add `NDILibraryResolver` with cross-platform name probing |
| P3.17 | §11.2.3 NDI wrapper constructors throw | Replace with factory methods returning error codes |
| P3.18 | §11.1.2 `TraceCall` boxing | Replace `params (string, object?)[]` with source-generated logging |
| P3.19 | §11.3.2 No `PortMidiLibraryResolver` | Add resolver with cross-platform name probing |
| P3.20 | §11.1.3 `Pa_OpenStream` heap allocation | Replace `Marshal.AllocHGlobal` with `unsafe fixed` pinning |
| P3.21 | §8.1 `AvaloniaVideoOutput` throws | Replace constructor throw with static factory |

---

*Revision 3 — Full document rewrite. All prior "Additional Findings" sections integrated into their respective project sections. Added §11 (Native Wrappers — PALib, NDILib, PMLib), §8 (OpenGL.Avalonia), §11.4 (cross-wrapper logging table). Roadmap consolidated and de-duplicated. Total distinct issues documented: 70+.*

