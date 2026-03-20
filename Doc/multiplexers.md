# Audio/Video Multiplexers

This guide explains how to fan out one mixed/rendered stream to multiple destinations.

## Why multiplexers exist

- `AudioMixer` targets one `IAudioEngine` instance.
- `VideoMixer` targets one primary `IVideoOutput` sink.

When you need one-to-many delivery, use the multiplexer wrappers in `Seko.OwnAudioNET.Video.Engine`.

## Audio fan-out: `MultiplexAudioEngine`

`MultiplexAudioEngine` implements `IAudioEngine` and forwards `Send(...)` to multiple engines.

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

using var muxAudioEngine = new MultiplexAudioEngine(engineA, engineB);
muxAudioEngine.Start();

using var mixer = new AudioMixer(muxAudioEngine, config.BufferSize);
// Add audio sources to mixer as usual.
```

Notes:

- All child engines should use matching sample rate/channels/buffer settings.
- Device-control methods are forwarded to all engines where appropriate.

## Video fan-out: `MultiplexVideoOutputEngine`

`MultiplexVideoOutputEngine` fans out pushed frames to many outputs and/or child output engines.

Because `VideoMixer` expects one `IVideoOutput`, use `VideoOutputEngineSink` as adapter.

```csharp
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;

var outputA = CreateOutputA();
var outputB = CreateOutputB();

var muxVideoEngine = new MultiplexVideoOutputEngine();
muxVideoEngine.AddOutput(outputA);
muxVideoEngine.AddOutput(outputB);

using var muxSink = new VideoOutputEngineSink(muxVideoEngine, ownsEngine: true);

// videoMixer is your regular VideoMixer instance.
if (!videoMixer.AddOutput(muxSink))
    throw new InvalidOperationException("Failed to add multiplex video sink.");

if (!videoMixer.BindOutputToSource(muxSink, videoSource))
    throw new InvalidOperationException("Failed to bind source to multiplex sink.");
```

## With `AudioVideoMixer`

- Audio: create `AudioMixer` with `MultiplexAudioEngine`.
- Video: keep one mixer output (`VideoOutputEngineSink`) wrapping `MultiplexVideoOutputEngine`.
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

var muxVideoEngine = new MultiplexVideoOutputEngine();
muxVideoEngine.AddOutput(outputA);
muxVideoEngine.AddOutput(outputB);

using var muxSink = new VideoOutputEngineSink(muxVideoEngine, ownsEngine: true);

if (!videoMixer.AddOutput(muxSink))
    throw new InvalidOperationException("Failed to add multiplex sink to VideoMixer.");

if (!videoMixer.BindOutputToSource(muxSink, videoSource))
    throw new InvalidOperationException("Failed to bind video source to multiplex sink.");

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

using var ndi = new NdiOutputEngine(new NdiEngineConfig
{
    SenderName = "MFPlayer Multiplex",
    AudioSampleRate = 48000,
    AudioChannels = 2
});
ndi.Start();

var muxVideoEngine = new MultiplexVideoOutputEngine();
muxVideoEngine.AddOutput(localPreview);
muxVideoEngine.AddOutput(ndi.VideoOutput);

using var muxSink = new VideoOutputEngineSink(muxVideoEngine, ownsEngine: true);

if (!videoMixer.AddOutput(muxSink))
    throw new InvalidOperationException("Failed to add multiplex sink to VideoMixer.");

if (!videoMixer.BindOutputToSource(muxSink, videoSource))
    throw new InvalidOperationException("Failed to bind video source to multiplex sink.");

videoMixer.Start();
```

If audio is part of the same pipeline, combine this with `MultiplexAudioEngine` so one `AudioMixer` can also fan out to multiple audio engines.

## Related files

- `VideoLibs/Seko.OwnAudioNET.Video.Engine/MultiplexAudioEngine.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.Engine/MultiplexVideoOutputEngine.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.Engine/VideoOutputEngineSink.cs`

