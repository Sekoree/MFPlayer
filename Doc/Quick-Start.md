# Quick Start

This guide shows quick setup paths for both `MediaPlayer` and `AVRouter`.

## 0) Fastest path (`MediaPlayer`)

```csharp
using var player = new MediaPlayer();
player.AddEndpoint(audioOutput);
player.AddEndpoint(videoOutput);
await player.OpenAsync("media.mp4");
await player.PlayAsync();
```

For playback events and extra output/sink fan-out helpers, see `MediaPlayer-Guide.md`.
For a full main-output + NDI fan-out example, see
`MediaPlayer-Guide.md#end-to-end-main-output--ndi-fan-out`.

## 1) Audio Playback (PortAudio)

```csharp
using var output = new PortAudioOutput();
output.Open(device, requestedFormat, framesPerBuffer: 512);

using var router = new AVRouter();
var outputId = router.RegisterEndpoint(output);
var channelId = router.RegisterAudioInput(audioChannel);
router.CreateRoute(channelId, outputId,
    new AudioRouteOptions { ChannelMap = ChannelRouteMap.Identity(output.HardwareFormat.Channels) });

decoder.Start();
await router.StartAsync();
await output.StartAsync();
```

## 2) Video Playback (SDL3)

```csharp
using var videoOutput = new SDL3VideoOutput();
videoOutput.Open("MFPlayer", 1280, 720, videoChannel.SourceFormat);

using var router = new AVRouter();
var outputId = router.RegisterEndpoint(videoOutput);
var channelId = router.RegisterVideoInput(videoChannel);
router.CreateRoute(channelId, outputId, new VideoRouteOptions());

decoder.Start();
await router.StartAsync();
await videoOutput.StartAsync();
```

## 3) Add a Secondary Sink (fan-out)

```csharp
// Any IAudioEndpoint / IVideoEndpoint / IAVEndpoint can be registered as a sink.
var ndiId = router.RegisterEndpoint(ndiSink);
router.CreateRoute(channelId, ndiId,
    new AudioRouteOptions { ChannelMap = ChannelRouteMap.Identity(2) });
router.CreateRoute(videoChannelId, ndiId, new VideoRouteOptions());
```

## 4) Loading from a Stream

Instead of a file path, you can open media from any `System.IO.Stream`:

```csharp
// From a MemoryStream, FileStream, HTTP response stream, etc.
using var stream = File.OpenRead("media.mp4");
using var decoder = FFmpegDecoder.Open(stream);

// Or with options and leaveOpen control:
using var decoder = FFmpegDecoder.Open(stream,
    new FFmpegDecoderOptions { EnableVideo = true },
    leaveOpen: true);  // caller retains ownership of the stream
```

Seekable streams enable full seek support; non-seekable streams (e.g. network pipes)
allow forward-only playback.

## 5) Shutdown Order

- Stop outputs/sinks first.
- Stop decoder.
- Dispose sinks, outputs, and router.

Example:

```csharp
await videoOutput.StopAsync();
await output.StopAsync();

decoder.Dispose();
await router.DisposeAsync();
```

## Build/Run Samples

```bash
dotnet build /home/seko/RiderProjects/MFPlayer/MFPlayer.sln -v minimal
dotnet run --project /home/seko/RiderProjects/MFPlayer/Test/MFPlayer.SimplePlayer/MFPlayer.SimplePlayer.csproj
dotnet run --project /home/seko/RiderProjects/MFPlayer/Test/MFPlayer.VideoPlayer/MFPlayer.VideoPlayer.csproj
```

