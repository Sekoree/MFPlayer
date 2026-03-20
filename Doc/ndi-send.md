# NDI Send (Sink/Engine)

This guide covers the outbound NDI path in `VideoLibs/Seko.OwnAudioNET.Video.NDI`.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## Main types

- `NdiOutputEngine`
  - combined sender with video sink and audio send API.
- `NdiEngineConfig`
  - sender/timeline/audio format options.
- `NdiVideoOutput`
  - `IVideoOutput` sink that can be attached to `VideoEngine` or `VideoMixer` output routing.
- `INdiAudioOutputEngine`
  - span-based interleaved float audio sender.

## Option 1: direct send API (quickest)

```csharp
using Seko.OwnAudioNET.Video.NDI;

using var ndi = new NdiOutputEngine(new NdiEngineConfig
{
    SenderName = "MFPlayer Demo",
    AudioSampleRate = 48000,
    AudioChannels = 2,
    RgbaSendFormat = NdiVideoRgbaSendFormat.Auto,
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

using var ndi = new NdiOutputEngine(new NdiEngineConfig { SenderName = "MFPlayer Engine" });
ndi.Start();

using var engine = ndi.CreateVideoEngine();

// For each decoded frame:
engine.PushFrame(frame, masterTimestampSeconds);
```

## Option 3: use as `IVideoOutput` in mixer routing

`NdiVideoOutput` is exposed via `NdiOutputEngine.VideoOutput`, so it can be mixer-bound like any other output sink.

```csharp
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.NDI;

using var ndi = new NdiOutputEngine(new NdiEngineConfig { SenderName = "MFPlayer Mixer" });
ndi.Start();

// Assume videoMixer + videoSource already exist.
if (!videoMixer.AddOutput(ndi.VideoOutput))
    throw new InvalidOperationException("Failed to add NDI output sink.");

if (!videoMixer.BindOutputToSource(ndi.VideoOutput, videoSource))
    throw new InvalidOperationException("Failed to bind NDI output sink.");
```

## Timeline and sync notes

- Default behavior uses internal timeline progression from audio/video send path.
- You can provide external timeline authority via `NdiEngineConfig.ExternalClock`.
- `UseIncomingVideoTimestamps = true` makes outgoing video timecode follow incoming `masterTimestamp` passed into `PushFrame`/`SendVideoRgba`.

## Format notes

- `SendVideoRgba` expects RGBA byte layout.
- If `RgbaSendFormat = Bgra`, conversion is performed before submit.
- `INdiAudioOutputEngine.Send` expects interleaved `float` PCM.

## Run a quick smoke sender

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/NDI/NdiLib.Smoke/NdiLib.Smoke.csproj" -c Release
```

## Related files

- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NdiOutputEngine.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NdiEngineConfig.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/NdiVideoOutput.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/INdiAudioOutputEngine.cs`

