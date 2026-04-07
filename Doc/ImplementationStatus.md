# MFPlayer — Implementation Status

> **Last updated:** 2026-04-07

---

## Implemented ✅

### `S.Media.Core` (`Media/S.Media.Core/`)

#### Clock
| File | Type | Notes |
|---|---|---|
| `Clock/IMediaClock.cs` | Interface | Position, SampleRate, IsRunning, Tick, Start/Stop/Reset |
| `Clock/MediaClockBase.cs` | Abstract | Owns `System.Timers.Timer`; fires `Tick` off the RT thread |
| `Clock/HardwareClock.cs` | Class | `Func<double>` provider; Stopwatch fallback on ≤ 0 |
| `Clock/StopwatchClock.cs` | Class | Pure software clock for offline/test/NDI |

#### Media types
| File | Type | Notes |
|---|---|---|
| `Media/SampleType.cs` | Enum | Float32 / Int16 / Int24 / Int32 |
| `Media/PixelFormat.cs` | Enum | Bgra32, Nv12, Yuv420p, Uyvy422 |
| `Media/AudioFormat.cs` | Record struct | SampleRate, Channels, SampleType |
| `Media/VideoFormat.cs` | Record struct | Width, Height, PixelFormat, FrameRate rational |
| `Media/VideoFrame.cs` | Record struct | Width, Height, PixelFormat, Data, Pts |
| `Media/IMediaOutput.cs` | Interface | Clock, IsRunning, StartAsync, StopAsync |
| `Media/IMediaChannel.cs` | Interface | Id, IsOpen, FillBuffer, CanSeek, Seek |

#### Audio
| File | Type | Notes |
|---|---|---|
| `Audio/AudioDeviceInfo.cs` | Records | `AudioHostApiInfo`, `AudioDeviceInfo`, `HostApiType` enum |
| `Audio/IAudioEngine.cs` | Interface | Initialize/Terminate, GetHostApis/Devices |
| `Audio/IAudioOutput.cs` | Interface | HardwareFormat, Mixer, Open |
| `Audio/IAudioChannel.cs` | Interface | SourceFormat, Volume, Position, push/pull |
| `Audio/AudioChannel.cs` | Class | `BoundedChannel<float[]>` ring buffer; RT-safe pull |
| `Audio/BufferUnderrunEventArgs.cs` | EventArgs | Position + FramesDropped |
| `Audio/IAudioResampler.cs` | Interface | Resample, Reset |
| `Audio/LinearResampler.cs` | Class | Linear interp; stateful phase; zero dependencies |
| `Audio/IAudioMixer.cs` | Interface | AddChannel, RemoveChannel, FillOutputBuffer, PeakLevels |
| `Audio/IAudioSink.cs` | Interface | ReceiveBuffer (RT-safe); StartAsync/StopAsync |
| `Audio/AggregateOutput.cs` | Class | Leader + N sinks; clock from leader; RT distribution |
| `Audio/Routing/ChannelRoute.cs` | Record struct | SrcChannel, DstChannel, Gain |
| `Audio/Routing/ChannelRouteMap.cs` | Class | Fluent Builder; Identity/StereoFanTo/StereoExpandTo/DownmixToMono |

#### Mixing
| File | Type | Notes |
|---|---|---|
| `Mixing/AudioMixer.cs` | Class | Copy-on-write slot list; hot path: pull → resample → vol → scatter → peaks → copy |

#### Errors
| File | Type | Notes |
|---|---|---|
| `Errors/MediaException.cs` | Exception | Base pipeline exception |
| `Errors/AudioEngineException.cs` | Exception | Wraps native error codes |

---

### `S.Media.PortAudio` (`Audio/S.Media.PortAudio/`)

| File | Type | Notes |
|---|---|---|
| `PortAudioEngine.cs` | Class | `Pa_Initialize/Terminate`; maps PA structs to Core info records |
| `PortAudioClock.cs` | Class | `HardwareClock` subclass; `HandleRef` box set post-Open |
| `PortAudioOutput.cs` | Class | PA stream callback mode; RT callback → `AudioMixer.FillOutputBuffer` |
| `PortAudioSink.cs` | Class | `IAudioSink`; PA blocking-write stream; pool-buffered; background write thread |

---

### `S.Media.FFmpeg` (`Media/S.Media.FFmpeg/`)

| File | Type | Notes |
|---|---|---|
| `FFmpegLoader.cs` | Class | One-time native library init; sets `ffmpeg.RootPath` |
| `FFmpegDecoder.cs` | Class | `avformat_open_input`; demux thread routes packets to per-stream queues |
| `FFmpegAudioChannel.cs` | Class | `IAudioChannel`; background decode thread; SWR → Float32; bounded ring |
| `FFmpegVideoChannel.cs` | Class | `IMediaChannel<VideoFrame>`; background decode; SWS pixel conversion |
| `SwrResampler.cs` | Class | `IAudioResampler`; libswresample sinc; stateful; reinitialises on param change |

---

## Missing / TODO 🔲

### `S.Media.Core` — error types
| File | Priority | Notes |
|---|---|---|
| `Errors/BufferException.cs` | Low | Underrun/overflow detail |

### `S.Media.NDI` — NDI integration (`NDI/S.Media.NDI/`) 🔲

| File | Type | Priority | Notes |
|---|---|---|---|
| `S.Media.NDI.csproj` | Project | Medium | Ref: `NDILib`, `S.Media.Core` |
| `NdiClock.cs` | Class | Medium | `MediaClockBase`; converts 100 ns NDI timestamps |
| `NdiAudioChannel.cs` | Class | Medium | `IAudioChannel`; pulls from `NDIFrameSync.CaptureAudio` |
| `NdiVideoChannel.cs` | Class | Medium | `IMediaChannel<VideoFrame>`; pulls from `NDIFrameSync.CaptureVideo` |
| `NdiAudioSink.cs` | Class | Medium | `IAudioSink`; calls `NDISender.SendAudio` on background thread |

### Tests 🔲

| Project | Priority | Notes |
|---|---|---|
| `S.Media.Core.Tests` | High | AudioMixer, LinearResampler, ChannelRouteMap, AudioChannel unit tests |
| `S.Media.PortAudio.Tests` | Medium | Smoke tests (requires audio hardware or mock) |
| `S.Media.FFmpeg.Tests` | Medium | Decoder + channel tests against reference files |

#### Clock
| File | Type | Notes |
|---|---|---|
| `Clock/IMediaClock.cs` | Interface | Position, SampleRate, IsRunning, Tick, Start/Stop/Reset |
| `Clock/MediaClockBase.cs` | Abstract | Owns `System.Timers.Timer`; fires `Tick` off the RT thread |
| `Clock/HardwareClock.cs` | Class | `Func<double>` provider; Stopwatch fallback on ≤ 0 |
| `Clock/StopwatchClock.cs` | Class | Pure software clock for offline/test/NDI |

#### Media types
| File | Type | Notes |
|---|---|---|
| `Media/SampleType.cs` | Enum | Float32 / Int16 / Int24 / Int32 |
| `Media/PixelFormat.cs` | Enum | Bgra32, Nv12, Yuv420p, Uyvy422 |
| `Media/AudioFormat.cs` | Record struct | SampleRate, Channels, SampleType |
| `Media/VideoFormat.cs` | Record struct | Width, Height, PixelFormat, FrameRate rational |
| `Media/VideoFrame.cs` | Record struct | Width, Height, PixelFormat, Data, Pts |
| `Media/IMediaOutput.cs` | Interface | Clock, IsRunning, StartAsync, StopAsync |
| `Media/IMediaChannel.cs` | Interface | Id, IsOpen, FillBuffer, CanSeek, Seek |

#### Audio
| File | Type | Notes |
|---|---|---|
| `Audio/AudioDeviceInfo.cs` | Records | `AudioHostApiInfo`, `AudioDeviceInfo`, `HostApiType` enum |
| `Audio/IAudioEngine.cs` | Interface | Initialize/Terminate, GetHostApis/Devices |
| `Audio/IAudioOutput.cs` | Interface | HardwareFormat, Mixer, Open |
| `Audio/IAudioChannel.cs` | Interface | SourceFormat, Volume, Position, push/pull |
| `Audio/AudioChannel.cs` | Class | `BoundedChannel<float[]>` ring buffer; RT-safe pull |
| `Audio/BufferUnderrunEventArgs.cs` | EventArgs | Position + FramesDropped |
| `Audio/IAudioResampler.cs` | Interface | Resample, Reset |
| `Audio/LinearResampler.cs` | Class | Linear interp; stateful phase; zero dependencies |
| `Audio/IAudioMixer.cs` | Interface | AddChannel, RemoveChannel, FillOutputBuffer, PeakLevels |
| `Audio/Routing/ChannelRoute.cs` | Record struct | SrcChannel, DstChannel, Gain |
| `Audio/Routing/ChannelRouteMap.cs` | Class | Fluent Builder; Identity/StereoFanTo/StereoExpandTo/DownmixToMono; bakes RT lookup |

#### Mixing
| File | Type | Notes |
|---|---|---|
| `Mixing/AudioMixer.cs` | Class | Copy-on-write slot list; hot path: pull → resample → vol → scatter → peaks → copy |

---

### `S.Media.PortAudio` (`Audio/S.Media.PortAudio/`)

| File | Type | Notes |
|---|---|---|
| `PortAudioEngine.cs` | Class | `Pa_Initialize/Terminate`; maps PA structs to Core info records |
| `PortAudioClock.cs` | Class | `HardwareClock` subclass; `HandleRef` box set post-Open |
| `PortAudioOutput.cs` | Class | PA stream callback mode; RT callback → `AudioMixer.FillOutputBuffer` |

---

## Missing / TODO 🔲

### `S.Media.Core` — aggregate output & sink

| File | Type | Priority | Notes |
|---|---|---|---|
| `Audio/IAudioSink.cs` | Interface | **High** | Secondary output destination (NDI, file, second PA device); receives copies of the master mix |
| `Audio/AggregateOutput.cs` | Class | **High** | Wraps a leader `IAudioOutput`; distributes mixed buffer to additional `IAudioSink` instances; exposes the leader's clock |

### `S.Media.PortAudio` — write-mode secondary output

| File | Type | Priority | Notes |
|---|---|---|---|
| `PortAudioSink.cs` | Class | Medium | `IAudioSink` backed by PA write-mode stream; ring buffer decouples RT distribution from PA write |

### `S.Media.FFmpeg` — decoding (`Media/S.Media.FFmpeg/`) 🔲

| File | Type | Priority | Notes |
|---|---|---|---|
| `S.Media.FFmpeg.csproj` | Project | **High** | Ref: `FFmpeg.AutoGen` 8.0.0, `S.Media.Core` |
| `FFmpegDecoder.cs` | Class | **High** | `avformat_open_input`, demux loop, feeds audio/video queues |
| `FFmpegAudioChannel.cs` | Class | **High** | `IAudioChannel`; decodes to `AV_SAMPLE_FMT_FLT`; background thread + bounded ring |
| `FFmpegVideoChannel.cs` | Class | Medium | `IMediaChannel<VideoFrame>`; decodes + converts pixel format |
| `SwrResampler.cs` | Class | **High** | `IAudioResampler`; wraps libswresample sinc resampler |

### `S.Media.NDI` — NDI integration (`NDI/S.Media.NDI/`) 🔲

| File | Type | Priority | Notes |
|---|---|---|---|
| `S.Media.NDI.csproj` | Project | Medium | Ref: `NDILib`, `S.Media.Core` |
| `NdiClock.cs` | Class | Medium | `MediaClockBase`; converts 100 ns NDI timestamps |
| `NdiAudioChannel.cs` | Class | Medium | `IAudioChannel`; pulls from `NDIFrameSync.CaptureAudio` |
| `NdiVideoChannel.cs` | Class | Medium | `IMediaChannel<VideoFrame>`; pulls from `NDIFrameSync.CaptureVideo` |
| `NdiAudioSink.cs` | Class | Medium | `IAudioSink`; calls `NDISender.SendAudio` on background thread |

### `S.Media.Core` — error types 🔲

| File | Priority | Notes |
|---|---|---|
| `Errors/MediaException.cs` | Low | Base exception for pipeline errors |
| `Errors/AudioEngineException.cs` | Low | Wraps PA error codes |
| `Errors/BufferException.cs` | Low | Underrun/overflow detail |

### Tests 🔲

| Project | Priority | Notes |
|---|---|---|
| `S.Media.Core.Tests` | High | AudioMixer, LinearResampler, ChannelRouteMap, AudioChannel unit tests |
| `S.Media.PortAudio.Tests` | Medium | Smoke tests (requires audio hardware or mock) |
| `S.Media.FFmpeg.Tests` | Medium | Decoder + channel tests against reference files |

---

## Architecture note — multiple outputs

The current 1:1 mixer → output model has been extended with `AggregateOutput`:

```
                    ┌─────────────────────────────────┐
                    │        AggregateOutput          │
                    │  leader: IAudioOutput (PA)      │
                    │  clock:  leader.Clock           │
                    │                                 │
 AudioMixer.FillOutputBuffer()                        │
    → leader PA buffer (filled first)                │
    → IAudioSink[0] ← PortAudioSink (2nd device)    │
    → IAudioSink[1] ← NdiAudioSink                  │
    → IAudioSink[2] ← FileSink / recorder           │
                    └─────────────────────────────────┘
```

The clock leader drives all timing. Sinks use ring buffers to decouple the RT
distribution from their own write operations. Each sink owns an optional
`IAudioResampler` for format conversion if its target rate differs from the leader.

