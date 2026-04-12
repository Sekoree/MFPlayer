# MFPlayer Docs

This folder contains current documentation for the AVMixer-first API surface.

## Read This First

1. `Quick-Start.md` - fast setup path for audio, video, and fan-out.
2. `Usage-Guide.md` - day-to-day API usage patterns.
3. `Clone-Sinks.md` - parent-owned clone sink usage for Avalonia and SDL3.
4. `API-Implementation-Audit-2026-04-12.md` - implementation audit and optimization notes.

## Current Architecture (Short)

- `AVMixer` is the public orchestration entry point.
- `AudioMixer` and `VideoMixer` are internal implementation details.
- Outputs and sinks are endpoints (`IMediaEndpoint`) that are attached and routed through `AVMixer`.
- Clone sinks are created by parent outputs (for example `CreateCloneSink(...)`) and owned by the parent lifecycle.

## Sample Apps

- `Test/MFPlayer.SimplePlayer` - audio playback.
- `Test/MFPlayer.VideoPlayer` - video playback with optional NDI sink.
- `Test/MFPlayer.MultiOutputPlayer` - one audio source to multiple outputs.
- `Test/MFPlayer.AvaloniaVideoPlayer` - embedded Avalonia video output.

