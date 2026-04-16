# MFPlayer Docs

This folder contains current documentation for both `MediaPlayer` and `AVMixer` API surfaces.

## Read This First

1. `Quick-Start.md` - fast setup path for audio, video, and fan-out.
2. `MediaPlayer-Guide.md` - high-level playback API, events, and fan-out examples (including NDI).
3. `Usage-Guide.md` - day-to-day API usage patterns.
4. `Clone-Sinks.md` - parent-owned clone sink usage for Avalonia and SDL3.
5. `AVMixer-Refactor-Plan.md` - architecture plan to decouple `AVMixer` from outputs (Phase 1) and timeline scheduling (Phase 2 outline).

## Current Architecture (Short)

- `MediaPlayer` is the high-level playback convenience entry point.
- `AVMixer` is the explicit routing/orchestration entry point.
- `AudioMixer` and `VideoMixer` are internal implementation details.
- Outputs and sinks are endpoints (`IMediaEndpoint`) that are attached and routed through `AVMixer`.
- Clone sinks are created by parent outputs (for example `CreateCloneSink(...)`) and owned by the parent lifecycle.

## Sample Apps

- `Test/MFPlayer.SimplePlayer` - audio playback.
- `Test/MFPlayer.NDIPlayer` - audio-focused NDI receive sample with latency presets.
- `Test/MFPlayer.NDIAutoPlayer` - auto-discovery + auto-reconnect NDI A/V sample with latency presets.
- `Test/MFPlayer.VideoPlayer` - video playback with optional NDI sink.
- `Test/MFPlayer.MultiOutputPlayer` - one audio source to multiple outputs.
- `Test/MFPlayer.AvaloniaVideoPlayer` - embedded Avalonia video output.
