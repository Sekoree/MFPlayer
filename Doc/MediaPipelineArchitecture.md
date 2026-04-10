# MFPlayer — Media Pipeline Architecture

> **Status:** Design finalised / Ready for implementation  
> **Last updated:** 2026-04-07 (Q13–Q19 resolved; per-output/sink routing implemented)

---

## 1. Overview

The pipeline is split into three concern layers:

```
┌─────────────────────────────────────────────────────────────┐
│           Application / Decoder Layer                        │
│  (pushes frames, or is called by pull callback)             │
├─────────────────────────────────────────────────────────────┤
│  S.Media.Core          — interfaces, types, abstractions    │
│  S.Media.PortAudio     — IAudioEngine/IAudioOutput via PALib│
│  S.Media.FFmpeg        — decoder channels + SwrResampler    │
├─────────────────────────────────────────────────────────────┤
│  PALib / JackLib / NDILib  — native P/Invoke wrappers        │
└─────────────────────────────────────────────────────────────┘
```

### Project map

| Project | Location | Role |
|---|---|---|
| `PALib` | `Audio/PALib/` | PortAudio P/Invoke (existing) |
| `JackLib` | `Audio/JackLib/` | JACK2 P/Invoke (new; port/connection management) |
| `NDILib` | `NDI/NDILib/` | NDI SDK P/Invoke (existing) |
| `S.Media.Core` | `Media/S.Media.Core/` | All interfaces & value types |
| `S.Media.PortAudio` | `Audio/S.Media.PortAudio/` | PortAudio implementation of Core |
| `S.Media.FFmpeg` | `Media/S.Media.FFmpeg/` | FFmpeg decoding + libswresample |
| `S.Media.NDI` | `NDI/S.Media.NDI/` | NDI receive + send pipeline |

---

## 2. Mix-thread hot path (per channel, per tick)

```
IAudioChannel.FillBuffer(srcSpan, srcFormat)
  │
  ├─► IAudioResampler.Resample(srcRate → leaderRate)   // rate-only; keeps srcChannels
  │
  ├─► ApplyChannelVolume(gain)                          // scalar multiply
  │
  ├─► ChannelRouteMap.Scatter → leader mix buffer       // fan-out / cross-patch
  │
  └─► ChannelRouteMap.Scatter → sink[0..N] mix buffers  // per-sink independent mixes
                                                         // (explicit RouteTo or ChannelFallback)
After all channels:
  ├─► ApplyMasterVolume() on all buffers (leader + sinks)
  ├─► Write leader mix buffer → IAudioOutput (PortAudio RT callback)
  └─► Write each sink mix buffer → IAudioSink.ReceiveBuffer() [in-line, no hop]
```

---

## 3. S.Media.Core

### 3.1 Clock — `S.Media.Core/Clock/`

#### `IMediaClock` (interface, already created)

```csharp
public interface IMediaClock
{
    TimeSpan Position   { get; }
    double   SampleRate { get; }
    bool     IsRunning  { get; }

    /// Raised on every internal tick (period ≈ one output buffer).
    event Action<TimeSpan> Tick;

    void Start();
    void Stop();
    void Reset();
}
```

#### `MediaClockBase` (abstract)

- Manages `Tick` subscription list and fires it at the correct cadence.
- Concrete subclasses only need to supply the current time value.

#### `HardwareClock : MediaClockBase`

- Constructor: `HardwareClock(Func<double> secondsProvider, double sampleRate)`
- `secondsProvider` is injected by the output layer:
  - PortAudio → `() => Native.Pa_GetStreamTime(stream)`
  - NDI → `() => lastFrameTimestamp / 1e7` (100 ns ticks → seconds)
- **Fallback:** if `secondsProvider()` returns `≤ 0` the clock seamlessly continues
  from an internal `Stopwatch` started at the last valid hardware sample, then
  re-syncs when the provider returns a valid value again.

#### `StopwatchClock : MediaClockBase`

- Pure software clock. `Stopwatch`-backed.
- Default when no hardware source is available (offline render, unit tests,
  pure-software NDI).

---

### 3.2 Media types — `S.Media.Core/Media/`

```csharp
public enum SampleType { Float32, Int16, Int24, Int32 }
public enum PixelFormat { Bgra32, Rgba32, Nv12, Yuv420p, Uyvy422, Yuv422p10 }

public readonly record struct AudioFormat(
    int        SampleRate,
    int        Channels,
    SampleType SampleType = SampleType.Float32);

public readonly record struct VideoFormat(
    int         Width,
    int         Height,
    PixelFormat PixelFormat,
    int         FrameRateNumerator,
    int         FrameRateDenominator);

// Carried through the pipeline per decoded video frame
public readonly record struct VideoFrame(
    int                  Width,
    int                  Height,
    PixelFormat          PixelFormat,
    ReadOnlyMemory<byte> Data,
    TimeSpan             Pts);
```

**Internal canonical audio format:** `Float32` interleaved.  
All sources must produce (or be resampled to) `Float32`. Format conversion is
only permitted at the I/O boundary.

---

### 3.3 Base media interfaces — `S.Media.Core/Media/`

```csharp
/// Base for all outputs (audio or video).
public interface IMediaOutput : IDisposable
{
    IMediaClock Clock { get; }
    bool        IsRunning { get; }
    Task        StartAsync(CancellationToken ct = default);
    Task        StopAsync(CancellationToken ct = default);
}

/// Base for all source channels. TFrame = AudioFrame | VideoFrame.
public interface IMediaChannel<TFrame> : IDisposable
{
    Guid   Id     { get; }
    bool   IsOpen { get; }

    /// Pull: output asks for the next TFrame block.
    int FillBuffer(Span<TFrame> dest, int frameCount);

    /// Seek support (if the source allows it).
    bool CanSeek { get; }
    void Seek(TimeSpan position);
}
```

---

### 3.4 Audio interfaces — `S.Media.Core/Audio/`

#### `IAudioEngine`

```csharp
public interface IAudioEngine : IDisposable
{
    bool IsInitialized { get; }
    void Initialize();
    void Terminate();

    IReadOnlyList<AudioHostApiInfo> GetHostApis();
    IReadOnlyList<AudioDeviceInfo>  GetDevices();
    AudioDeviceInfo?                GetDefaultOutputDevice();
    AudioDeviceInfo?                GetDefaultInputDevice();
}
```

#### `IAudioOutput : IMediaOutput`

```csharp
public interface IAudioOutput : IMediaOutput
{
    AudioFormat HardwareFormat { get; }  // Fixed once opened; set by hardware
    IAudioMixer Mixer          { get; }

    void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer);

    /// Replaces the mixer reference used by the RT callback.
    /// Optional advanced hook for wrappers/composite outputs.
    void OverrideRtMixer(IAudioMixer mixer);
}
```

#### `IAudioChannel : IMediaChannel<float>`

Source format is **independent** from the hardware output format.

```csharp
public interface IAudioChannel : IMediaChannel<float>
{
    AudioFormat SourceFormat  { get; }  // Native format of this source
    float       Volume        { get; set; }
    TimeSpan    Position      { get; }

    // --- Push mode (decoder-driven, back-pressured) ---
    int   BufferDepth         { get; }  // Configurable ring-buffer capacity in frames
    int   BufferAvailable     { get; }
    ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default);
    bool  TryWrite(ReadOnlySpan<float> frames);   // Non-blocking; returns false when full

    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
}
```

#### `IAudioResampler`

```csharp
/// Performs sample-rate conversion only; channel count is preserved.
public interface IAudioResampler : IDisposable
{
    /// Returns number of output frames written.
    int Resample(
        ReadOnlySpan<float> input,
        Span<float>         output,
        AudioFormat         inputFormat,
        int                 outputSampleRate);
}
```

Two implementations are provided:

| Class | Location | Quality | Notes |
|---|---|---|---|
| `LinearResampler` | `S.Media.Core` | Good / fast | Linear interpolation; zero external dependencies; **used automatically** when no resampler is supplied to `AddChannel` and rates differ |
| `SwrResampler` | `S.Media.FFmpeg` | High (sinc) | Wraps libswresample; inject explicitly when linear quality is insufficient |

#### `IAudioMixer`

```csharp
public interface IAudioMixer : IDisposable
{
    IAudioOutput Output       { get; }
    float        MasterVolume { get; set; }
    int          ChannelCount { get; }

    /// Peak level per output channel, updated each mix tick. Length == HardwareFormat.Channels.
    IReadOnlyList<float> PeakLevels { get; }

    /// resampler defaults to LinearResampler when null and source/output rates differ.
    void AddChannel(
        IAudioChannel    channel,
        ChannelRouteMap  routeMap,
        IAudioResampler? resampler = null);


    void RemoveChannel(Guid channelId);

    // Called from PortAudio RT callback — zero allocation required
    void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat);
}
```

---

### 3.5 Channel routing — `S.Media.Core/Audio/Routing/`

#### `ChannelRoute`

```csharp
public readonly record struct ChannelRoute(
    int   SrcChannel,
    int   DstChannel,
    float Gain = 1.0f);
```

#### `ChannelRouteMap`

Immutable. Allows:
- **Pass-through** `src[0]→dst[0], src[1]→dst[1]`
- **Fan-out** `src[0]→dst[0], src[0]→dst[2]` (one source to multiple destinations)
- **Fan-in** `src[0]→dst[0], src[1]→dst[0]` (multiple sources into one destination)
- **Cross-patch** any combination with per-route gain

```csharp
public sealed class ChannelRouteMap
{
    public IReadOnlyList<ChannelRoute> Routes { get; }

    // Fluent builder
    public sealed class Builder
    {
        public Builder Route(int src, int dst, float gain = 1.0f);
        public ChannelRouteMap Build();
    }

    // Convenience factories
    public static ChannelRouteMap Identity(int channelCount);
    // e.g. stereo → ch0+ch2 (L) and ch1+ch3 (R) of a 4-ch output
    public static ChannelRouteMap StereoFanTo(int dstL1, int dstL2, int dstR1, int dstR2);
    // e.g. stereo → ch0+ch1 (L) and ch2+ch3 (R) of a 4-ch output
    public static ChannelRouteMap StereoExpandTo(int baseChannel);
}
```

**Internal representation for the hot path:** at `AddChannel` time the mixer
pre-bakes a `(int dstCh, float gain)[][]` lookup table indexed by `srcCh` so the
scatter loop is a simple indexed array walk with no dictionary lookup or LINQ.

---

### 3.6 `LinearResampler` — `S.Media.Core/Audio/`

Built-in fallback resampler used automatically by the mixer when `resampler: null`
is passed to `AddChannel` and the source sample rate differs from the output.

- **Algorithm:** linear interpolation (suitable for most playback use-cases).
- **Cross-buffer continuity:** unconsumed tail frames from the end of each call are
  saved internally and prepended to the next call's input, so the read-head is
  seamless across `Resample()` call boundaries. Typical tail size: 1–3 frames.
- **No external dependencies.**
- For higher quality (e.g. professional audio, extreme rate ratios) inject a
  `SwrResampler` from `S.Media.FFmpeg` explicitly.

---

## 4. S.Media.PortAudio

**Location:** `Audio/S.Media.PortAudio/`  
**References:** `PALib` (internal access via existing `InternalsVisibleTo`)

### Key types

| Type | Implements | Notes |
|---|---|---|
| `PortAudioEngine` | `IAudioEngine` | Wraps `Pa_Initialize` / `Pa_Terminate`; builds device list from `PaHostApiInfo` + `PaDeviceInfo` |
| `PortAudioOutput` | `IAudioOutput` | Opens PA stream in **callback mode**; callback calls `IAudioMixer.FillOutputBuffer` directly (zero allocation in RT path); callback safely handles oversized host blocks by chunking |
| `PortAudioClock` | `HardwareClock` | Constructed with `() => Native.Pa_GetStreamTime(stream)` |
| `PortAudioSink` | `IAudioSink` | PA **blocking-write** mode; write thread; 8-buffer pool |

### Stream callback contract

- Called on PortAudio's RT thread — **no allocations, no locks, no blocking**.
- Calls `mixer.FillOutputBuffer(outputBuffer, frameCount, hardwareFormat)`.
- Mixer must pre-allocate all working buffers at `Start()` time.

---

## 5. S.Media.FFmpeg

**Location:** `Media/S.Media.FFmpeg/`  
**NuGet:** `FFmpeg.AutoGen` v8.0.0 (already in `Directory.Packages.props`)

### Key types

| Type | Implements | Notes |
|---|---|---|
| `FFmpegDecoder` | — | Opens file/URL; drives `AVFormatContext`, demuxes packets to audio/video queues |
| `FFmpegAudioChannel` | `IAudioChannel` | Decodes to `AV_SAMPLE_FMT_FLT` (interleaved float) on background thread; fills ring buffer |
| `FFmpegVideoChannel` | `IMediaChannel<VideoFrame>` | Decodes video frames; converts to target `PixelFormat` |
| `SwrResampler` | `IAudioResampler` | Wraps `swr_alloc_set_opts2` / `swr_convert`; stateful, one per channel-registration; high-quality alternative to `LinearResampler` |

### `FFmpegAudioChannel` design

- **Background decoder thread**: decodes and converts packets → `float[]` frames,
  writes to a `BoundedChannel<AudioFrame>` (capacity = `BufferDepth`, default 8 frames).
- **Pull path**: `FillBuffer` reads from the bounded channel; blocks with
  `CancellationToken` if the buffer is momentarily empty.
- **Push path**: `WriteAsync` / `TryWrite` write directly to the same bounded channel;
  `BoundedChannelFullMode.Wait` enforces back-pressure naturally.
- **Seek**: calls `avformat_seek_file`, flushes codec context, clears the bounded
  channel, resets internal PTS tracking.

### `SwrResampler` lifetime

Each `AddChannel()` call with an explicit `SwrResampler` binds it to that channel slot.
The mixer owns the resampler and disposes it with `RemoveChannel()`.
The `swr_context` delay line is flushed on seek.

---

## 6. S.Media.NDI

**Location:** `NDI/S.Media.NDI/` ✅ implemented

| Type | Implements | Notes |
|---|---|---|
| `NdiClock` | `MediaClockBase` | Stopwatch interpolation between NDI frame timestamps; `UpdateFromFrame(long)` |
| `NdiAudioChannel` | `IAudioChannel` | Background capture via `NDIFrameSync.CaptureAudio`; FLTP→interleaved; pre-allocated `ConcurrentQueue<float[]>` pool; manual DropOldest returns buffers to pool |
| `NdiVideoChannel` | `IMediaChannel<VideoFrame>` | Background capture via `NDIFrameSync.CaptureVideo`; `FreeVideo` called immediately after pixel copy |
| `NdiAudioSink` | `IAudioSink` | Interleaved→planar on write thread; 8-buffer pool; optional `IAudioResampler` (auto-creates `LinearResampler` on rate mismatch) |
| `NdiSource` | — | Lifecycle wrapper: `Open(source, options)` creates receiver + frame-sync + channels; `Start()` starts clock + capture threads; `Dispose()` tears down in order |

NDI audio/video channels slot into the same `IAudioMixer` + `ChannelRouteMap`
pipeline unchanged. The `NdiClock` can either drive the output clock directly, or
slave to an existing `PortAudioClock` for A/V sync.

NDI **input** source enumeration is handled by `NDIFinder` (from `NDILib`) — it is
separate from `IAudioEngine`, which models hardware devices. `NdiSource.Open` accepts
a `NdiDiscoveredSource` found by the application via `NDIFinder`.

---

## 7. Threading model

| Thread | Owner | Constraint |
|---|---|---|
| PortAudio RT callback | OS audio subsystem | **No alloc, no lock, no blocking** |
| Mix thread | `IAudioMixer` | High priority; pre-allocated buffers; driven by output clock tick |
| Decoder thread(s) | `FFmpegAudioChannel` / `FFmpegVideoChannel` | Normal/below-normal priority; fills ring buffer ahead of time |
| App / UI thread | Caller | Calls `AddChannel`, `WriteAsync`, `Seek` etc. |

---

## 8. Resolved design decisions

1. **Resampler default** — `LinearResampler` (linear interpolation, zero dependencies)
   lives in `S.Media.Core` and is used automatically by the mixer when `resampler: null`
   is passed to `AddChannel` and the source/output sample rates differ. For higher
   quality, pass an explicit `SwrResampler` (from `S.Media.FFmpeg`) instead.
   `PassthroughResampler` / throw-on-mismatch is **not** used.

2. **Video output** — `IVideoOutput : IMediaOutput` and concrete display backends
   (`SDLVideoOutput`, `AvaloniaVideoOutput`) are out of scope for the initial
   implementation. The `IMediaOutput` / `IMediaChannel<TFrame>` base is already
   shaped to accommodate them without breaking changes.

3. **Peak metering** — `IAudioMixer` exposes `IReadOnlyList<float> PeakLevels`
   with one entry per **output channel** (length == `HardwareFormat.Channels`).
   Never assume stereo; callers index by channel number.

4. **Resampler factory** — removed. `AddChannel` has a single optional `IAudioResampler?`
   parameter; it defaults to `LinearResampler` on mismatch. If the caller opens the
   output first (so the hardware rate is known), they can construct a `SwrResampler`
   directly and pass it in.

---

## 9. JackLib — `Audio/JackLib/`

**Native library:** `libjack.so.0` (JACK2 on Linux; `libjack.dylib` on macOS)

JackLib is a thin, self-contained P/Invoke wrapper around the JACK2 C API,
analogous to `PALib` for PortAudio. Its primary role is **port and connection
management** after a PortAudio/JACK stream has been opened — specifically:

- Enumerating physical (hardware) output ports
- Connecting our client's output ports to `system:playback_N`
- Querying port metadata (name, flags, connection count)

### Key types

| Type | Access | Notes |
|---|---|---|
| `Native` | `internal` | Raw P/Invoke (libjack); `InternalsVisibleTo S.Media.PortAudio` |
| `JackClient` | `public` | RAII wrapper: `Open()`, `Activate()`, `Dispose()`; manages callback delegate lifetimes |
| `JackPortFlags` | `public enum` | `IsInput`, `IsOutput`, `IsPhysical`, `IsTerminal`, `CanMonitor` |
| `JackOptions` | `public enum` | `NullOption`, `NoStartServer`, `UseExactName`, … |
| `JackStatus` | `public enum` | `Failure`, `ServerStarted`, `NameNotUnique`, … |
| `JackPortType` | `public static` | String constants: `DefaultAudio`, `DefaultMidi` |

### JACK autoconnect workflow

```
1. Open PortAudio JACK stream (N channels).
2. After Pa_StartStream(), create JackClient("my_app", JackOptions.NoStartServer).
3. Query system playback:   GetPorts(flags: IsInput | IsPhysical)
4. AutoConnectToPhysicalOutputs(ourPorts) — pairs ports 1→1, 2→2, etc.
5. Dispose JackClient when the stream closes.
```

---

## 10. Resolved design decisions (Q13–Q15)

5. **`IAudioOutput` vs `IAudioSink` — keep separate (Q13 resolved)**  
   `IAudioOutput` is clock-owning and RT-callback-driven.  
   `IAudioSink` is clock-following and push-based (receives buffers from `AggregateOutput`).  
   One `IAudioOutput` (leader) + N `IAudioSink` instances fan-out audio to multiple
   devices/targets. Merging them would conflate RT and blocking-write threading contracts.

6. **Channel count control (Q14 resolved)**  
   `AudioDeviceInfo` already exposes `MaxOutputChannels` and `MaxInputChannels`
   (populated from `PaDeviceInfo`). Helper methods `ClampOutputChannels(int)` and
   `ClampInputChannels(int)` guard against out-of-range requests.  
   `Open()` honours `requestedFormat.Channels` as-requested; callers should clamp first.  
   JACK via PA reports its configured port limit as `MaxOutputChannels` — no special
   JACK-specific code needed for channel count control.

7. **JACK port management (Q15 resolved)**  
   - **Flexible port count:** controlled via `requestedFormat.Channels` + `ClampOutputChannels`.
   - **Autoconnect to hardware:** implemented in `JackLib.JackClient.AutoConnectToPhysicalOutputs()`.
     Best-effort: if `libjack` is absent or PA host API is not JACK, `JackClient`
     construction throws and the caller continues without autoconnect.
   - **JackLib project:** `Audio/JackLib/` — thin P/Invoke wrapper analogous to `PALib`.

---

## 11. Per-output / per-sink channel routing ✅ Implemented

### Architecture

```
IAudioChannel A ──┐  routeMap per target:
IAudioChannel B ──┤→ AudioMixer ──→ [target 0 mix buffer] → IAudioOutput (leader)
IAudioChannel C ──┘               ├─ [target 1 mix buffer] → Sink 1 (its own channels)
                                  └─ [target 2 mix buffer] → Sink 2 (its own channels)
```

Each RT tick:
1. **Pull** each channel's `FillBuffer` exactly **once** → `slot.ResampleBuf`
2. **Resample** once (source rate → leader rate)
3. **Scatter** into **N+1 independent mix buffers** — one per registered target — using
   that target's per-channel `ChannelRouteMap` (or `ChannelFallback` policy if no route is set)
4. **Write** leader mix buffer → hardware via `PortAudio` RT callback
5. **Write** each sink's mix buffer → `sink.ReceiveBuffer(sinkBuf, ...)` directly from RT path

### API

```csharp
// AudioMixer is now decoupled from IAudioOutput — constructed with just the format:
var mixer = new AudioMixer(new AudioFormat(48000, 2));                    // default Silent
var mixer = new AudioMixer(new AudioFormat(48000, 2), ChannelFallback.Broadcast);

// VirtualAudioOutput: hardware-free clock master for pure-sink scenarios:
var virtualOut = new VirtualAudioOutput(new AudioFormat(48000, 2), framesPerBuffer: 512);
var agg        = new AggregateOutput(virtualOut);

// Register sinks as routing targets:
agg.AddSink(portAudioSink);   // channels = 0 → use leader channel count
agg.AddSink(ndiAudioSink);

// Route A exclusively to the PortAudio sink, B exclusively to NDI — shared clock:
agg.Mixer.AddChannel(channelA, ChannelRouteMap.Silence());  // silent on leader mix
agg.Mixer.AddChannel(channelB, ChannelRouteMap.Silence());
agg.Mixer.RouteTo(channelA.Id, portAudioSink, ChannelRouteMap.Identity(2));
agg.Mixer.RouteTo(channelB.Id, ndiAudioSink,  ChannelRouteMap.Identity(2));

// Dynamic re-routing at runtime (no re-add required):
agg.Mixer.UnrouteTo(channelA.Id, portAudioSink);
agg.Mixer.RouteTo  (channelA.Id, ndiAudioSink, ChannelRouteMap.Identity(2));

// Remove sink entirely (routes cleaned up automatically):
agg.Mixer.UnregisterSink(ndiAudioSink);
```

### Components implemented

| Component | Implementation |
|---|---|
| `Audio/ChannelFallback.cs` | `Silent` / `Broadcast` enum |
| `IAudioMixer` | `LeaderFormat` (was `Output`), `DefaultFallback`, `RouteTo`, `UnrouteTo`, `RegisterSink`, `UnregisterSink` |
| `AudioMixer` constructor | `AudioMixer(AudioFormat leaderFormat, ChannelFallback)` — no longer holds an `IAudioOutput` back-reference |
| `AudioMixer` nested types | `SinkTarget` (per-sink mix buffer), `SinkRoute` (baked scatter table), `ChannelSlot` (volatile copy-on-write `SinkRoute[]`) |
| `AudioMixer.FillOutputBuffer` | Pull-once → resample → scatter into leader buffer → scatter into N sink buffers → distribute |
| `Audio/VirtualAudioOutput.cs` | Hardware-free `IAudioOutput`; `StopwatchClock` timer loop; enables pure-sink routing (A→C, B→D) with a shared clock and no physical device |
| `Audio/Routing/ChannelRouteMap.Silence()` | Empty route map — channel contributes nothing to leader mix; use when routing exclusively via `RouteTo` |
| `AggregateOutput` | Lifecycle/orchestration helper for leader + sinks; stores pre-open sink registrations (including per-sink channels) and replays them on `Open` |

### Resolved design decisions (Q16–Q19)

| # | Question | Resolution |
|---|---|---|
| Q16 | Default fallback | `ChannelFallback.Silent` — channels are silent on sinks unless `RouteTo` is called. Configurable at `AudioMixer` construction. |
| Q17 | API shape | **Option C** — `RouteTo` / `UnrouteTo` as separate post-`AddChannel` calls. Enables dynamic re-routing without re-adding the channel. |
| Q18 | Channel data sharing | Fixed: `FillBuffer` called once per tick; `ResampleBuf` reused for all scatter passes. |
| Q19 | Per-sink sample rate | Sinks receive audio at leader sample rate. Sinks needing a different rate apply their own internal resampler (e.g. `NdiAudioSink` already does this). |

