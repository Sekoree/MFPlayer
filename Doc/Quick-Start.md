# Quick Start

This guide shows the minimum wiring for playback using `AVMixer`.

## 1) Audio Playback (PortAudio)

```csharp
using var output = new PortAudioOutput();
output.Open(device, requestedFormat, framesPerBuffer: 512);

using var avMixer = new AVMixer(output.HardwareFormat);
avMixer.AttachAudioOutput(output);

avMixer.AddAudioChannel(audioChannel, ChannelRouteMap.Identity(output.HardwareFormat.Channels));

decoder.Start();
await output.StartAsync();
```

## 2) Video Playback (SDL3)

```csharp
using var videoOutput = new SDL3VideoOutput();
videoOutput.Open("MFPlayer", 1280, 720, videoChannel.SourceFormat);

using var avMixer = new AVMixer(new AudioFormat(48000, 2), videoOutput.OutputFormat);
avMixer.AttachVideoOutput(videoOutput);

avMixer.AddVideoChannel(videoChannel);

decoder.Start();
await videoOutput.StartAsync();
```

## 3) Add a Secondary Sink (fan-out)

```csharp
avMixer.RegisterAudioSink(ndiSink, channels: 2);
avMixer.RouteAudioChannelToSink(audioChannel.Id, ndiSink, ChannelRouteMap.Identity(2));

avMixer.RegisterVideoSink(ndiSink);
avMixer.RouteVideoChannelToSink(videoChannel.Id, ndiSink);
```

## 4) Shutdown Order

- Stop outputs/sinks first.
- Stop decoder.
- Dispose sinks, outputs, and mixer.

Example:

```csharp
await videoOutput.StopAsync();
await output.StopAsync();

decoder.Dispose();
avMixer.Dispose();
```

## Build/Run Samples

```bash
dotnet build /home/seko/RiderProjects/MFPlayer/MFPlayer.sln -v minimal
dotnet run --project /home/seko/RiderProjects/MFPlayer/Test/MFPlayer.SimplePlayer/MFPlayer.SimplePlayer.csproj
dotnet run --project /home/seko/RiderProjects/MFPlayer/Test/MFPlayer.VideoPlayer/MFPlayer.VideoPlayer.csproj
```

