# Audio/Video Multiplexers

This guide explains how to fan out one mixed/rendered stream to multiple destinations.

## Why broadcast engines exist

- `AudioMixer` targets one `IAudioEngine` instance.
- `VideoMixer` targets one attached render engine.

When you need one-to-many delivery, use the broadcast wrappers in `Seko.OwnAudioNET.Video.Engine`.

## Audio fan-out: `BroadcastAudioEngine`

`BroadcastAudioEngine` implements `IAudioEngine` and forwards `Send(...)` to multiple engines.

```csharp
using Ownaudio.Core;
using Ownaudio.Native;
using OwnaudioNET.Mixing;
using Seko.OwnAudioNET.Video.Engine;

var engineA = new NativeAudioEngine();
var engineB = new NativeAudioEngine();

var config = AudioConfig.Default;
engineA.Initialize(config);
engineB.Initialize(config);

using var muxAudioEngine = new BroadcastAudioEngine(engineA, engineB);
muxAudioEngine.Start();

using var mixer = new AudioMixer(muxAudioEngine, config.BufferSize);
// Add audio sources to mixer as usual.
```

Notes:

- All child engines should use matching sample rate/channels/buffer settings.
- Device-control methods are forwarded to all engines where appropriate.

## Video fan-out: `BroadcastVideoEngine`

`BroadcastVideoEngine` fans out pushed frames to many outputs and/or child video engines.

```csharp
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;

var outputA = CreateOutputA();
var outputB = CreateOutputB();

var muxVideoEngine = new BroadcastVideoEngine();
muxVideoEngine.AddOutput(outputA);
muxVideoEngine.AddOutput(outputB);

using var videoMixer = new VideoMixer(muxVideoEngine, config: transportConfig);
if (!videoMixer.SetActiveSource(videoSource))
    throw new InvalidOperationException("Failed to set active source.");
```

## With `AudioVideoMixer`

- Audio: create `AudioMixer` with `BroadcastAudioEngine`.
- Video: attach one `BroadcastVideoEngine` to `VideoMixer`.
- Then build `AudioVideoMixer` normally.

## Recipe 1: one source -> two local outputs

Use this when one decoded source should be shown in two render targets (for example two windows or two independent output sinks).

```csharp
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Sources;

// Assume these already exist:
// VideoMixer videoMixer;
// VideoStreamSource videoSource;

var outputA = CreateOutputA();
var outputB = CreateOutputB();

var muxVideoEngine = new BroadcastVideoEngine();
muxVideoEngine.AddOutput(outputA);
muxVideoEngine.AddOutput(outputB);

using var videoMixer = new VideoMixer(muxVideoEngine, config: transportConfig);

if (!videoMixer.SetActiveSource(videoSource))
    throw new InvalidOperationException("Failed to set active source.");

videoMixer.Start();
```

## Recipe 2: one source -> local preview + NDI output

Use this when you want a local monitor output and a network NDI sender at the same time.

```csharp
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.NDI;
using Seko.OwnAudioNET.Video.Sources;

// Assume these already exist:
// VideoMixer videoMixer;
// VideoStreamSource videoSource;

var localPreview = CreateLocalPreviewOutput(); // e.g. VideoSDL or VideoGL

using var ndi = new NDIVideoEngine(new NDIEngineConfig
{
    SenderName = "MFPlayer Multiplex",
    AudioSampleRate = 48000,
    AudioChannels = 2
});
ndi.Start();

var muxVideoEngine = new BroadcastVideoEngine();
muxVideoEngine.AddOutput(localPreview);
muxVideoEngine.AddOutput(ndi.VideoOutput);

using var videoMixer = new VideoMixer(muxVideoEngine, config: transportConfig);

if (!videoMixer.SetActiveSource(videoSource))
    throw new InvalidOperationException("Failed to set active source.");

videoMixer.Start();
```

If audio is part of the same pipeline, combine this with `BroadcastAudioEngine` so one `AudioMixer` can also fan out to multiple audio engines.

## Related files

- `VideoLibs/Seko.OwnAudioNET.Video.Engine/BroadcastAudioEngine.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.Engine/BroadcastVideoEngine.cs`

