# MediaPlayer Guide

`MediaPlayer` is the highest-level playback API in this repository.
Use it when you want a simple open/play/pause/stop flow and optional fan-out to extra sinks.

## Basic audio playback

```csharp
using S.Media.FFmpeg;
using S.Media.PortAudio;

using var output = new PortAudioOutput();
output.Open(device, requestedFormat, framesPerBuffer: 512);

using var player = new MediaPlayer(audioOutput: output);
await player.OpenAsync("music.mp3");
await player.PlayAsync();

// ...
await player.StopAsync();
```

## Basic audio + video playback

```csharp
using var player = new MediaPlayer(audioOutput, videoOutput);
await player.OpenAsync("movie.mp4");
await player.PlayAsync();
```

## Playback events

`MediaPlayer` now exposes convenience events:

- `PlaybackStateChanged` for transport state transitions.
- `PlaybackCompleted` for completion reason (`SourceEnded`, `StoppedByUser`, `ReplacedByOpen`, `Faulted`).
- `PlaybackFailed` with stage + exception for recovery/telemetry.
- `PlaybackEnded` remains for compatibility and maps to source end signaling.

Example:

```csharp
player.PlaybackStateChanged += (_, e) =>
    Console.WriteLine($"state: {e.Previous} -> {e.Current}");

player.PlaybackCompleted += (_, e) =>
    Console.WriteLine($"completed: {e.Reason}");

player.PlaybackFailed += (_, e) =>
    Console.WriteLine($"failed in {e.Stage}: {e.Exception.Message}");
```

## Add extra outputs/sinks (fan-out)

For advanced routing, either use `player.Mixer` directly, or use convenience methods.

### Add an audio sink

```csharp
// Auto-routes first decoded audio channel when available.
player.AddAudioSink(audioSink, channels: 2);

// Explicit route map variant.
player.AddAudioSink(audioSink, routeMap: ChannelRouteMap.Identity(2), channels: 2);
```

### Add a video sink

```csharp
player.AddVideoSink(videoSink);
```

### Add endpoint adapters

```csharp
player.AddAudioEndpoint(audioEndpoint, channels: 2);
player.AddVideoEndpoint(videoEndpoint);
```

### Remove fan-out targets

```csharp
player.RemoveAudioSink(audioSink);
player.RemoveVideoSink(videoSink);
player.RemoveAudioEndpoint(audioEndpoint);
player.RemoveVideoEndpoint(videoEndpoint);
```

### End-to-end: main output + NDI fan-out

```csharp
using NDILib;
using S.Media.FFmpeg;
using S.Media.NDI;

if (!NDIRuntime.IsSupportedCpu())
    throw new InvalidOperationException("NDI requires SSE4.2 support.");

NDIRuntime.Create(out var ndiRuntime);
using var runtime = ndiRuntime ?? throw new InvalidOperationException("Failed to init NDI runtime.");

NDISender.Create(out var ndiSender, "MFPlayer NDI", clockVideo: false, clockAudio: false);
using var sender = ndiSender ?? throw new InvalidOperationException("Failed to create NDI sender.");

using var ndiSink = new NDIAVSink(sender, new NDIAVSinkOptions
{
    Name = "MFPlayer NDI Sink",
    Preset = NDIEndpointPreset.Balanced,
    VideoTargetFormat = videoOutput.OutputFormat,
    AudioTargetFormat = new S.Media.Core.Audio.AudioFormat(48000, 2)
});

using var player = new MediaPlayer(audioOutput, videoOutput);
await player.OpenAsync("movie.mp4");

// Fan out decoded A/V from the same player session.
player.AddVideoSink(ndiSink);
player.AddAudioSink(ndiSink, channels: 2);

await ndiSink.StartAsync();
await player.PlayAsync();

// ... playback ...

await player.StopAsync();
player.RemoveAudioSink(ndiSink);
player.RemoveVideoSink(ndiSink);
await ndiSink.StopAsync();
```

For a full interactive sample with optional NDI enablement and diagnostics, see
`Test/MFPlayer.VideoPlayer/Program.cs`.

## Access mixer directly

```csharp
if (player.Mixer is { } mixer)
{
    // Full IAVMixer API is available.
    mixer.RegisterAudioSink(audioSink, channels: 2);
}
```

## Notes

- Open outputs before creating `MediaPlayer`.
- `MediaPlayer` does not own externally created outputs/sinks/endpoints.
- For fully custom multi-channel routing workflows, use `AVMixer` directly.

