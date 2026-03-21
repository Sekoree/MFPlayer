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

using var engine = ndi.CreateVideoEngine();

// For each decoded frame:
engine.PushFrame(frame, masterTimestampSeconds);
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
- If `RgbaSendFormat = Bgra`, conversion is performed before submit.
- `INDIAudioOutputEngine.Send` expects interleaved `float` PCM.

## Run a quick smoke sender

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/NDI/NdiLib.Smoke/NdiLib.Smoke.csproj" -c Release
```

## Related files

- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NDIVideoEngine.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NDIEngineConfig.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NDIVideoOutput.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/INDIAudioOutputEngine.cs`

