# Usage Guide

## Choose Your API Level

- Use `MediaPlayer` for fast open/play/pause/stop workflows and optional fan-out.
- Use `AVMixer` when you need full explicit channel/sink routing control.
- See `MediaPlayer-Guide.md` for complete `MediaPlayer` examples and events.

## Core Model (AVMixer)

- Add channels to `AVMixer`.
- Attach outputs to `AVMixer` (`AttachAudioOutput`, `AttachVideoOutput`).
- Register sinks/endpoints and route channels explicitly.

## Common Operations

### NDI receive latency tuning

Use `NDISourceOptions.QueueBufferDepth` with `NDILatencyPreset` for end-user-friendly buffering defaults:

```csharp
var options = new NDISourceOptions
{
	QueueBufferDepth = NDILatencyPreset.Balanced, // Safe(12), Balanced(8), LowLatency(4)
	LowLatency = false
};
```

- `QueueBufferDepth` applies to both audio and video rings.
- `QueueBufferDepth` can use built-in presets or a custom value via `NDILatencyPreset.FromQueueDepth(...)`.
- `AudioBufferDepth` / `VideoBufferDepth` remain available as advanced overrides.
- `LowLatency = true` enables faster polling and tighter capture sleeps (lower latency, higher CPU).

### Add/Remove channels

```csharp
avMixer.AddAudioChannel(audioChannel, routeMap);
avMixer.AddVideoChannel(videoChannel);

avMixer.RemoveAudioChannel(audioChannel.Id);
avMixer.RemoveVideoChannel(videoChannel.Id);
```

### Register sinks

```csharp
avMixer.RegisterAudioSink(audioSink, channels: 2);
avMixer.RegisterVideoSink(videoSink);
```

### Route/unroute

```csharp
avMixer.RouteAudioChannelToSink(audioChannel.Id, audioSink, routeMap);
avMixer.UnrouteAudioChannelFromSink(audioChannel.Id, audioSink);

avMixer.RouteVideoChannelToSink(videoChannel.Id, videoSink);
avMixer.UnrouteVideoChannelFromSink(videoSink);
```

### Endpoint adapters

If you already have endpoint-style targets:

```csharp
avMixer.RegisterAudioEndpoint(audioEndpoint, channels: 2);
avMixer.RegisterVideoEndpoint(videoEndpoint);

avMixer.RouteVideoChannelToEndpoint(videoChannel.Id, videoEndpoint);
```

## Audio Routing Notes

- Explicit sink routes are recommended for deterministic fan-out.
- Use `ChannelRouteMap.Identity(n)` for direct channel mapping.
- For mono-to-stereo or downmix behavior, build a custom `ChannelRouteMap`.

## Video Routing Notes

- Primary output render loop pulls via the attached presentation mixer.
- Sink fan-out gets copies on the same clock domain.
- Keep sink `ReceiveFrame(...)` non-blocking.

## Lifecycle Guidance

- Open outputs first, then attach/register/route, then start.
- During shutdown: stop endpoints, unroute if needed, then dispose.
- Disposing `AVMixer` cleans internal mixer resources it owns.

## MediaPlayer Quick Reference

```csharp
using var player = new MediaPlayer(audioOutput, videoOutput);
await player.OpenAsync("file.mp4");
await player.PlayAsync();

player.AddAudioSink(extraAudioSink, channels: 2);
player.AddVideoSink(extraVideoSink);

await player.PauseAsync();
player.Seek(TimeSpan.FromSeconds(30));
await player.PlayAsync();
await player.StopAsync();
```

Events:

- `PlaybackStateChanged`
- `PlaybackCompleted`
- `PlaybackFailed`
- `PlaybackEnded` (compatibility signal)

