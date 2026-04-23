# MediaPlayer Guide

`MediaPlayer` is the highest-level playback API in this repository.
Use it when you want a simple open/play/pause/stop flow and optional fan-out to extra endpoints.

## Basic audio playback

```csharp
using S.Media.FFmpeg;
using S.Media.PortAudio;

using var output = new PortAudioOutput();
output.Open(device, requestedFormat, framesPerBuffer: 512);

using var player = new MediaPlayer();
player.AddEndpoint(output);
await player.OpenAsync("music.mp3");
await player.PlayAsync();

// ...
await player.StopAsync();
```

## Basic audio + video playback

```csharp
using var player = new MediaPlayer();
player.AddEndpoint(audioOutput);
player.AddEndpoint(videoOutput);
await player.OpenAsync("movie.mp4");
await player.PlayAsync();
```

## Playback events

`MediaPlayer` now exposes convenience events:

- `PlaybackStateChanged` for transport state transitions.
- `PlaybackCompleted` for completion reason (`SourceEnded`, `StoppedByUser`, `ReplacedByOpen`, `Faulted`).
- `PlaybackFailed` with stage + exception for recovery/telemetry.

Example:

```csharp
player.PlaybackStateChanged += (_, e) =>
    Console.WriteLine($"state: {e.Previous} -> {e.Current}");

player.PlaybackCompleted += (_, e) =>
    Console.WriteLine($"completed: {e.Reason}");

player.PlaybackFailed += (_, e) =>
    Console.WriteLine($"failed in {e.Stage}: {e.Exception.Message}");
```

## Add extra endpoints (fan-out)

Use `AddEndpoint(...)` / `RemoveEndpoint(...)` for runtime fan-out.

### Add an audio endpoint

```csharp
player.AddEndpoint(audioSink);
```

### Add a video endpoint

```csharp
player.AddEndpoint(videoSink);
```

### Add A/V endpoint

```csharp
player.AddEndpoint(avEndpoint);
```

### Remove fan-out targets

```csharp
player.RemoveEndpoint(audioSink);
player.RemoveEndpoint(videoSink);
player.RemoveEndpoint(avEndpoint);
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

using var ndiSink = new NDIAVEndpoint(sender, new NDIAVSinkOptions
{
    Name = "MFPlayer NDI Sink",
    Preset = NDIEndpointPreset.Balanced,
    VideoTargetFormat = videoOutput.OutputFormat,
    AudioTargetFormat = new S.Media.Core.Audio.AudioFormat(48000, 2)
});

using var player = new MediaPlayer();
player.AddEndpoint(audioOutput);
player.AddEndpoint(videoOutput);
await player.OpenAsync("movie.mp4");

// Fan out decoded A/V from the same player session.
player.AddEndpoint(ndiSink);

await ndiSink.StartAsync();
await player.PlayAsync();

// ... playback ...

await player.StopAsync();
player.RemoveEndpoint(ndiSink);
await ndiSink.StopAsync();
```

For a full interactive sample with optional NDI enablement and diagnostics, see
`Test/MFPlayer.VideoPlayer/Program.cs`.

## Access router directly

```csharp
if (player.Router is { } router)
{
    // Full IAVRouter API is available.
    var id = router.RegisterEndpoint(audioSink);
}
```

## Notes

- Open endpoints before creating `MediaPlayer`.
- `MediaPlayer` does not own externally created endpoints.
- For fully custom multi-channel routing workflows, use `AVRouter` directly.

## Clock selection (§1.4b)

The router owns a priority-ranked clock registry. Every clock-capable endpoint
is auto-registered when you call `RegisterEndpoint` (or add it via the
`MediaPlayer` facade), at the priority exposed by
`IClockCapableEndpoint.DefaultPriority`:

| Endpoint kind                             | Default priority        |
|-------------------------------------------|-------------------------|
| Local hardware output (PortAudio, SDL3)   | `ClockPriority.Hardware`|
| Network receive clock (NDI receive, PTP)  | `ClockPriority.External`|
| Virtual / stopwatch                       | `ClockPriority.Internal`|

The resolver picks the highest-priority clock currently registered. Concrete
scenarios:

- **Default PA-only playback.** `RegisterEndpoint(paEndpoint)` ⇒ the
  `PortAudioClock` wins at `Hardware`. No extra code needed.
- **PA playback + NDI send slaved to a PTP genlock source.** Register your PTP
  clock with `router.RegisterClock(ptpClock, ClockPriority.External)` — it
  outranks the PA clock for this session. Remove it later with
  `router.UnregisterClock(ptpClock)` and the resolver *automatically* falls
  back to the PA clock with no re-plumbing.
- **Hard override for a single session.** `router.SetClock(otherClock)`
  registers at `ClockPriority.Override` and outranks every other entry.
  Clearing it restores the priority-based resolution.
- **NDI send endpoints do not provide a clock today.** `NDIClock` is a
  *receive*-side type derived from sender-stamped timestamps. When NDI is on
  the send side, the natural choices are the PA clock (default) or a
  PTP/`SetClock` override. A future NDI sender can opt in by implementing
  `IClockCapableEndpoint`.

> **Known rough edge:** the router's internal tick cadence is currently
> decoupled from `Clock.Position`. That is tracked under checklist §4.9
> (`ActiveClockChanged` event), §5.5 (auto cadence derivation) and §6.7
> (per-axis tick cadence) and does not affect *which* clock is chosen — only
> how frequently the resolver is re-evaluated.
