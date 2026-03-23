# NDI Send (Sink/Engine)

This guide covers the outbound NDI path in `VideoLibs/Seko.OwnAudioNET.Video.NDI`.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## Main types

- `NDIVideoEngine`
  - combined sender with video sink and audio send API.
- `NDIEngineConfig`
  - sender/timeline/audio format options.
- `NDIVideoOutput`
  - `IVideoOutput` sink that can be attached to `VideoEngine` or `VideoMixer` output routing.
- `INDIAudioOutputEngine`
  - span-based interleaved float audio sender.

## Option 1: direct send API (quickest)

```csharp
using Seko.OwnAudioNET.Video.NDI;

using var ndi = new NDIVideoEngine(new NDIEngineConfig
{
    SenderName = "MFPlayer Demo",
    AudioSampleRate = 48000,
    AudioChannels = 2,
    RgbaSendFormat = NDIVideoRgbaSendFormat.Auto,
    UseIncomingVideoTimestamps = false
});

ndi.Start();

// RGBA frame payload must be width * height * 4 bytes (or include stride bytes).
var ok = ndi.SendVideoRgba(rgbaFrame, width, height, strideBytes: width * 4, timestampSeconds: 0.0);

// Interleaved float PCM: LRLR... for stereo.
ndi.AudioEngine.Send(interleavedSamples);

var listeners = ndi.GetConnectionCount();
Console.WriteLine($"NDI listeners: {listeners}");

ndi.Stop();
```

## Option 2: route through `VideoEngine`

Use this when you already have a push-based video pipeline and want NDI as an output sink.

```csharp
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.NDI;

using var ndi = new NDIVideoEngine(new NDIEngineConfig { SenderName = "MFPlayer Engine" });
ndi.Start();

// For each decoded frame:
ndi.PushFrame(frame, masterTimestampSeconds);
```

### Optional: add a transcode adapter before NDI output

Use `VideoTranscodeEngine` when you want outbound constraints (resolution, pixel format, FPS cap)
without changing decoder output behavior.

```csharp
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.NDI;

using var ndi = new NDIVideoEngine(new NDIEngineConfig { SenderName = "MFPlayer Strict" });
ndi.Start();

// Strict preset defaults to 1920x1080 @ 60.0 with preserve-aspect resize.
var transcodeConfig = NDITranscodePresets.CreateStrictSenderPreset();

// Overloads can override resolution and cap FPS (arbitrary positive values allowed).
// var transcodeConfig = NDITranscodePresets.CreateStrictSenderPreset(1280, 720, NDITranscodePresets.SafeFps30);

using var transcodeEngine = new VideoTranscodeEngine(ndi, transcodeConfig);

// Push decoded frames to the transcode adapter.
transcodeEngine.PushFrame(frame, masterTimestampSeconds);

var diag = transcodeEngine.GetDiagnosticsSnapshot();
Console.WriteLine($"CPU fallback used: {diag.UsedCpuFallbackEver}, count={diag.CpuFallbackCount}, last={diag.LastBackendUsed}");
```

## Option 3: use as `IVideoOutput` in mixer routing

`NDIVideoOutput` is exposed via `NDIVideoEngine.VideoOutput` and can be attached to a render engine used by `VideoMixer`.

```csharp
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.NDI;

using var ndi = new NDIVideoEngine(new NDIEngineConfig { SenderName = "MFPlayer Mixer" });
ndi.Start();

// Assume playbackEngine + videoMixer + videoSource already exist.
using var renderEngine = new OpenGLVideoEngine();
using var videoMixer = new VideoMixer(playbackEngine, renderEngine);

if (!renderEngine.AddOutput(ndi.VideoOutput))
    throw new InvalidOperationException("Failed to add NDI output sink.");

if (renderEngine is ISupportsOutputSwitching switching && !switching.SetVideoOutput(ndi.VideoOutput))
    throw new InvalidOperationException("Failed to select NDI output sink.");

if (!videoMixer.SetActiveSource(videoSource))
    throw new InvalidOperationException("Failed to set active video source.");
```

## Timeline and sync notes

- Default behavior uses internal timeline progression from audio/video send path.
- You can provide external timeline authority via `NDIEngineConfig.ExternalClock`.
- `UseIncomingVideoTimestamps = true` makes outgoing video timecode follow incoming `masterTimestamp` passed into `PushFrame`/`SendVideoRgba`.

## Format notes

- `SendVideoRgba` expects RGBA byte layout.
- `SendFormat = Auto` negotiates native NDI FourCC from incoming `VideoFrame.PixelFormat`.
- The NDI adapter now runs in strict mode: no implicit pixel conversion is performed.
- `SendVideoRgba` requires `SendFormat` to be `Auto` or `Rgba`.
- Alpha can be dropped when forcing RGB-only targets (`Uyva -> Uyvy`, `Pa16 -> P216`), and emits a one-time warning per stream key.
- `INDIAudioOutputEngine.Send` expects interleaved `float` PCM.
- `NDITranscodePresets` exposes safe cap constants:
  - `23.976`, `24.0`, `25.0`, `29.97`, `30.0`, `50.0`, `59.94`, `60.0`.

## Run a quick smoke sender

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/NDI/NdiLib.Smoke/NdiLib.Smoke.csproj" -c Release
```

## Related files

- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NDIVideoEngine.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NDIEngineConfig.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NDIVideoOutput.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/INDIAudioOutputEngine.cs`

