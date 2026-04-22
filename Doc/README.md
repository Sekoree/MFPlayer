# MFPlayer Docs

This folder contains current documentation for both `MediaPlayer` and `AVRouter` API surfaces.

## Read This First

1. `Quick-Start.md` - fast setup path for audio, video, and fan-out.
2. `MediaPlayer-Guide.md` - high-level playback API, events, and fan-out examples (including NDI).
3. `Usage-Guide.md` - day-to-day API usage patterns.
4. `Clone-Sinks.md` - parent-owned clone sink usage for Avalonia and SDL3.
5. `AVMixer-Refactor-Plan.md` - historical refactor plan that led to today's `AVRouter` endpoint-based routing model.

## Current Architecture (Short)

- `MediaPlayer` is the high-level playback convenience entry point.
- `AVRouter` is the explicit routing/orchestration entry point.
- `AudioMixer` and `VideoMixer` are internal implementation details.
- Outputs and sinks are endpoints (`IMediaEndpoint`) that are attached and routed through `AVRouter`.
- Clone sinks are created by parent outputs (for example `CreateCloneSink(...)`) and owned by the parent lifecycle.

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

## Sample Apps

- `Test/MFPlayer.SimplePlayer` - audio playback.
- `Test/MFPlayer.NDIPlayer` - audio-focused NDI receive sample with latency presets.
- `Test/MFPlayer.NDIAutoPlayer` - auto-discovery + auto-reconnect NDI A/V sample with latency presets.
- `Test/MFPlayer.VideoPlayer` - video playback with optional NDI sink.
- `Test/MFPlayer.MultiOutputPlayer` - one audio source to multiple outputs.
- `Test/MFPlayer.AvaloniaVideoPlayer` - embedded Avalonia video output.
