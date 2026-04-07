# MFPlayer — Media Pipeline Architecture

> **Status:** Design finalised / Ready for implementation  
> **Last updated:** 2026-04-07

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
│  PALib / NDILib        — existing native P/Invoke wrappers  │
└─────────────────────────────────────────────────────────────┘
```

### Project map

| Project | Location | Role |
|---|---|---|
| `PALib` | `Audio/PALib/` | PortAudio P/Invoke (existing) |
| `NDILib` | `NDI/NDILib/` | NDI SDK P/Invoke (existing) |
| `S.Media.Core` | `Media/S.Media.Core/` | All interfaces & value types |
| `S.Media.PortAudio` | `Audio/S.Media.PortAudio/` | PortAudio implementation of Core |
| `S.Media.FFmpeg` | `Media/S.Media.FFmpeg/` | FFmpeg decoding + libswresample |

---

## 2. Mix-thread hot path (per channel, per tick)

```
IAudioChannel.FillBuffer(srcSpan, srcFormat)
  │
  ├─► IAudioResampler.Resample(srcRate → dstRate)   // rate-only; keeps srcChannels
  │
  ├─► ApplyChannelVolume(gain)                       // scalar multiply
  │
  ├─► ChannelRouteMap.Scatter(srcCh → dstCh[], gain) // fan-out / cross-patch
  │
  └─► Sum into output buffer (dstChannels × dstFrames)

After all channels:
  └─► ApplyMasterVolume()
  └─► Write to IAudioOutput (PortAudio RT callback)
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
public enum PixelFormat { Bgra32, Nv12, Yuv420p, Uyvy422 }

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
}
```

#### `IAudioOutput : IMediaOutput`

```csharp
public interface IAudioOutput : IMediaOutput
{
    AudioFormat HardwareFormat { get; }  // Fixed once opened; set by hardware
    IAudioMixer Mixer          { get; }

    void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer);
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
| `PortAudioOutput` | `IAudioOutput` | Opens PA stream in **callback mode**; callback calls `IAudioMixer.FillOutputBuffer` directly (zero allocation in RT path) |
| `PortAudioClock` | `HardwareClock` | Constructed with `() => Native.Pa_GetStreamTime(stream)` |

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

## 6. Future: S.Media.NDI

**Location:** `NDI/S.Media.NDI/` (planned, not yet scoped)

| Type | Implements | Notes |
|---|---|---|
| `NdiClock` | `MediaClockBase` | `TimeSpan.FromTicks(ndiTimestamp / 100)` (100 ns → `TimeSpan` ticks) |
| `NdiAudioChannel` | `IAudioChannel` | Pulls from `NDIFrameSync.CaptureAudio`; FrameSync handles time-base correction |
| `NdiVideoChannel` | `IMediaChannel<VideoFrame>` | Pulls from `NDIFrameSync.CaptureVideo` |

NDI audio/video channels slot into the same `IAudioMixer` + `ChannelRouteMap`
pipeline unchanged. The `NdiClock` can either drive the output clock directly, or
slave to an existing `PortAudioClock` for A/V sync.

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

