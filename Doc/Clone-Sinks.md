# Clone Sinks (Parent-Owned)

Clone sinks are secondary video targets created by a parent output.

## Why this model

- Enforces backend compatibility (pixel format, thread model, renderer assumptions).
- Keeps lifecycle safe: parent output can track and dispose all clones.
- Simplifies app code: no manual clone constructor wiring.

## Avalonia

```csharp
var clone = avaloniaOutput.CreateCloneSink("Preview");
await clone.StartAsync();

avMixer.RegisterVideoSink(clone);
avMixer.RouteVideoChannelToSink(videoChannel.Id, clone);
```

## SDL3

```csharp
var clone = sdlOutput.CreateCloneSink(title: "Program", width: 960, height: 540);
await clone.StartAsync();

avMixer.RegisterVideoSink(clone);
avMixer.RouteVideoChannelToSink(videoChannel.Id, clone);
```

## Ownership and disposal

- Treat clones as parent-owned.
- Stop clones before teardown when possible.
- Disposing the parent output disposes tracked clones.

## Practical tip

If a sink is only for temporary preview, unroute first, then stop and dispose to reduce render and copy work:

```csharp
avMixer.UnrouteVideoChannelFromSink(clone);
await clone.StopAsync();
clone.Dispose();
```

