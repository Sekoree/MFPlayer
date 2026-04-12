# Usage Guide (AVMixer API)

## Core Model

- Add channels to `AVMixer`.
- Attach outputs to `AVMixer` (`AttachAudioOutput`, `AttachVideoOutput`).
- Register sinks/endpoints and route channels explicitly.

## Common Operations

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

