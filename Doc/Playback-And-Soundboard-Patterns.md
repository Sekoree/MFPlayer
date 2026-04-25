# Playback and Soundboard Patterns

This guide shows practical app-layer patterns on top of the lean framework:

- quick media playback with `MediaPlayer`,
- soundboard-style multi-clip playback with `AVRouter`.

For the concrete implementation roadmap, see
[`Playback-Soundboard-Enablement-Plan.md`](./Playback-Soundboard-Enablement-Plan.md).

## 1) Quick start: `MediaPlayer`

Use `MediaPlayer` when you want "open one source and play it now".

```csharp
using S.Media.Playback;

using var player = MediaPlayer.Create()
    .WithAudioOutput(audioOutput)
    .WithAutoPreroll()
    .Build();

await player.OpenAsync("track.mp3");
await player.PlayAsync();
await player.WaitForCompletionAsync();
```

Key points:

- `MediaPlayer` is intentionally single-source per session.
- You can still fan out to multiple endpoints.
- Playlist/cue/session ownership is app-layer by design.

## 2) Soundboard-style pattern (available today)

For a basic soundboard (overlapping clips into one output), use one shared `AVRouter`
and create decoder inputs per triggered clip.

```csharp
using S.Media.Core.Routing;
using S.Media.FFmpeg;

var router = new AVRouter();
var endpointId = router.RegisterEndpoint(audioOutput);
await audioOutput.StartAsync();
await router.StartAsync();

var active = new Dictionary<Guid, (FFmpegDecoder Decoder, InputId Input, RouteId Route)>();

async Task<Guid> TriggerClipAsync(string filePath)
{
    var clipId = Guid.NewGuid();
    var decoder = FFmpegDecoder.Open(filePath);
    var channel = decoder.FirstAudioChannel
        ?? throw new InvalidOperationException("Clip has no audio channel.");

    var inputId = router.RegisterAudioInput(channel);
    var routeId = router.CreateRoute(inputId, endpointId);

    decoder.EndOfMedia += (_, _) =>
    {
        router.RemoveRoute(routeId);
        router.UnregisterInput(inputId);
        decoder.Dispose();
        active.Remove(clipId);
    };

    active[clipId] = (decoder, inputId, routeId);
    decoder.Start(); // Independent clip start; overlaps are allowed.
    return clipId;
}
```

Why this works:

- `AVRouter` supports multiple simultaneous audio inputs to one endpoint.
- Each triggered clip has its own decoder/input/route lifecycle.
- Overlap is natural because each clip is an independent source.

## 3) What is not fully provided yet

A production soundboard usually also needs:

- low-latency warm preload pools,
- deterministic retrigger policy (`Restart`, `IgnoreIfPlaying`, `ChokeGroupStop`),
- per-pad routing/gain profiles,
- ducking/sidechain policies,
- structured clip/session persistence.

Those are intentionally app/helper concerns in the current baseline and are not built
into `MediaPlayer`.

## 4) "Always ready" clips: what can be done now

Current options:

- **Pre-open decoder objects** before trigger to reduce file-open cost.
- **Register routes at trigger time** (or pre-register and keep disabled).
- **Start decoder on trigger** and remove route/input on `EndOfMedia`.
- Prefer input-side transport control via optional `ISeekableInput` capability (for compatible channels).
- If you only have `InputId`, use router helpers `TrySeekInput(...)` / `TryRewindInput(...)`.

Limitations:

- There is no dedicated framework preload-cache manager yet.
- There is no built-in soundboard trigger policy engine yet.
- Retriggering the same clip with precise low-latency behavior is app logic.

## 5) What to add if you want first-class soundboard runtime

If your app needs robust live-show semantics, add an app/helper module with:

1. `ClipHandle` abstraction (decoder lifecycle + route lifecycle + state).
2. Preload pool (warm-open N decoders, LRU eviction, memory budget guardrails).
3. Trigger policy layer (`Restart`, `Overlap`, `IgnoreIfPlaying`, choke groups).
4. Route profile presets (endpoint targets + gain + optional delay).
5. Telemetry (trigger-to-audible latency, underruns, dropped triggers).

This keeps framework core lean while still enabling production-grade soundboard apps.
