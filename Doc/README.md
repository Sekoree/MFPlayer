# MFPlayer Docs

This folder contains current documentation for both `MediaPlayer` and `AVRouter` API surfaces.

## Read This First

1. `Quick-Start.md` - fast setup path for audio, video, and fan-out.
2. `MediaPlayer-Guide.md` - high-level playback API, events, and fan-out examples (including NDI).
3. `Usage-Guide.md` - day-to-day API usage patterns.
4. `Clone-Sinks.md` - parent-owned clone endpoint usage for Avalonia and SDL3.
5. `AVMixer-Refactor-Plan.md` - historical refactor plan that led to today's `AVRouter` endpoint-based routing model.

## Current Architecture (Short)

- `MediaPlayer` is the high-level playback convenience entry point.
- `AVRouter` is the explicit routing/orchestration entry point.
- `AudioMixer` and `VideoMixer` are internal implementation details.
- Every destination is an **endpoint** (`IMediaEndpoint`) attached and routed through `AVRouter`. "Output" and "sink" are legacy class-name suffixes; the public vocabulary is *endpoint*.
- Clone endpoints are created by parent endpoints (for example `CreateCloneSink(...)`) and owned by the parent lifecycle.

## Layering (§0.1 framing decision)

MFPlayer exposes two tiers of public surface:

1. **`AVRouter` — the framework.** A composable input/output routing graph:
   multiple audio/video inputs, multiple endpoints, explicit `CreateRoute` /
   `RemoveRoute`, per-route options, clock priority resolution. `AVRouter`
   stays minimal and does **not** acquire "player" ergonomics. Use it for
   multi-source mixing, timeline playback, clone fan-out, custom transport.

2. **`S.Media.FFmpeg.MediaPlayer` — the facade.** A thin single-file /
   single-source convenience layer built on `AVRouter`. Ergonomic helpers such
   as `OpenAndPlayAsync`, `WaitForCompletionAsync`, drain handling and
   (future) `MediaPlayerBuilder` live here, **not** on `AVRouter`. Reach for
   `MediaPlayer` when you want "play one file, then exit"; reach for
   `AVRouter` when you need anything more.

See `API-Implementation-Review.md` §"Layering" for the rationale and
`Implementation-Checklist.md` §0.1 for the audit trail.

## Obsoletion Policy (§0.4.3 framing decision)

When a public class is renamed (for example `SDL3VideoOutput` →
`SDL3VideoEndpoint`, `NDIAVSink` → `NDIAVEndpoint`):

1. The old name is kept for **one** release as a public type-forwarder:
   ```csharp
   [Obsolete("Renamed to FooEndpoint. This type-forwarder will be removed in the next release.", error: false)]
   public sealed class FooOld : FooEndpoint { }
   ```
   A dedicated `*Legacy.cs` file holds the forwarder (see
   `NDIAVSinkLegacy.cs`, `SDL3VideoOutputLegacy.cs`,
   `AvaloniaOpenGlVideoOutputLegacy.cs`).
2. In the release **after**, the forwarder is either promoted to
   `error: true` for one more release or deleted outright (implementer's
   choice based on user-facing impact).
3. Clone endpoints with internal constructors obtainable only via a parent
   `Create*` method **do not** require forwarders — `var`-typed / implicit
   callers migrate transparently; explicit type annotations are rare.
4. **Exception:** the PortAudio `PortAudioOutput` / `PortAudioSink` types
   were deleted outright in an earlier pass, predating this policy. Their
   callers were migrated inline. This is the one grandfathered case and is
   not the template going forward.

## Sample Apps

- `Test/MFPlayer.SimplePlayer` - audio playback.
- `Test/MFPlayer.NDIPlayer` - audio-focused NDI receive sample with latency presets.
- `Test/MFPlayer.NDIAutoPlayer` - auto-discovery + auto-reconnect NDI A/V sample with latency presets.
- `Test/MFPlayer.VideoPlayer` - video playback with optional NDI endpoint.
- `Test/MFPlayer.MultiOutputPlayer` - one audio source to multiple endpoints.
- `Test/MFPlayer.AvaloniaVideoPlayer` - embedded Avalonia video output.
