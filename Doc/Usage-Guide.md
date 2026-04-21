# Usage Guide

## Choose Your API Level

- Use `MediaPlayer` for fast open/play/pause/stop workflows and optional fan-out.
- Use `AVRouter` when you need full explicit input/endpoint routing control.
- See `MediaPlayer-Guide.md` for complete `MediaPlayer` examples and events.

## Core Model (AVRouter)

- Register audio / video *inputs* (channels) with `router.RegisterInput(channel)`.
- Register *endpoints* (outputs, sinks, `IAVEndpoint`s) with `router.RegisterEndpoint(ep)`.
- Wire them up with `router.CreateRoute(inputId, endpointId, options)` using
  `AudioRouteOptions` / `VideoRouteOptions` as appropriate.
- The router owns its internal clock by default; a `IClockCapableEndpoint` (e.g. a
  hardware audio output) is auto-discovered and promoted to master when registered.

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
var audioInputId = router.RegisterInput(audioChannel);
var videoInputId = router.RegisterInput(videoChannel);

router.UnregisterInput(audioInputId);
router.UnregisterInput(videoInputId);
```

### Register endpoints

```csharp
var audioEpId = router.RegisterEndpoint(audioOutput);   // IAudioEndpoint
var videoEpId = router.RegisterEndpoint(videoOutput);   // IVideoEndpoint
var avEpId    = router.RegisterEndpoint(ndiSink);       // IAVEndpoint
```

### Route/unroute

```csharp
var audioRoute = router.CreateRoute(audioInputId, audioEpId,
    new AudioRouteOptions { ChannelMap = routeMap });
var videoRoute = router.CreateRoute(videoInputId, videoEpId, new VideoRouteOptions());

router.RemoveRoute(audioRoute);
router.RemoveRoute(videoRoute);
```

### Endpoint adapters

The router treats `IAudioEndpoint`, `IVideoEndpoint`, and combined `IAVEndpoint`
uniformly — hardware outputs, fan-out sinks, and clone sinks all go through the
same `RegisterEndpoint` / `CreateRoute` API.

## Audio Routing Notes

- Explicit routes produce deterministic fan-out.
- Use `ChannelRouteMap.Identity(n)` for direct channel mapping.
- For mono-to-stereo or downmix behavior, build a custom `ChannelRouteMap`.

## Video Routing Notes

- The router pushes frames to all video endpoints at the internal tick cadence.
- A pull endpoint (e.g. SDL3 render window) consumes via an injected fill callback.
- Keep endpoint `ReceiveFrame(...)` non-blocking.

## Lifecycle Guidance

- Open outputs first, then register inputs/endpoints/routes, then start.
- During shutdown: stop endpoints, remove routes if needed, then dispose.
- Disposing `AVRouter` drains internal queues and disposes auto-created resources
  (e.g. resamplers it owns). Caller-owned endpoints and channels must still be
  disposed separately.

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

