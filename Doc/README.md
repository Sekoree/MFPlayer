# MFPlayer Video Docs

This folder contains practical setup and usage guides for the video layer in `VideoLibs/*` and its integration with OwnAudio mixers/clocks.

## Recent Changes

- mixer API cleanup:
  - removed legacy output-binding methods from mixer interfaces.
  - use `SetActiveSource(...)` / `SetActiveVideoSource(...)` to select rendered video source.
- engine-first model:
  - `VideoMixer` now owns playback internals and uses a render `IVideoEngine`.
  - fan-out is handled by `BroadcastVideoEngine` as the attached render engine.
- multiplexing support:
  - added `BroadcastAudioEngine` for one-to-many audio engine send.
  - added `BroadcastVideoEngine` for one-to-many video output routing.
- NDI receive stack:
  - added `NDIAudioStreamSource`, `NDIVideoStreamDecoder`, and `NDIExternalTimelineClock`.
  - added receive tuning presets via `NDIReceiveTuningProfile`.
- NDI send stack docs:
  - documented `NDIVideoEngine` usage for direct send, engine routing, and mixer sink routing.
- source API cleanup:
  - removed obsolete convenience constructors from `AudioStreamSource` and `VideoStreamSource`.
  - decoder-first construction is now the canonical path.

## Guides

- [`project-implementation-stages.md`](project-implementation-stages.md)
  - Snapshot table for every project in `MFPlayer.sln` and its current implementation stage.
- [`project-status-notes.md`](project-status-notes.md)
  - Per-project stage notes plus consolidated migration considerations.
- [`refactor-considerations-log.md`](refactor-considerations-log.md)
  - Consolidated log of contract decisions and migration considerations agreed during the `S.Media.*` refactor.

### Folder Status Files

- [`Audio/PROJECT-STATUS.md`](../Audio/PROJECT-STATUS.md)
- [`Media/PROJECT-STATUS.md`](../Media/PROJECT-STATUS.md)
- [`Media/S.Media.Core/PROJECT-STATUS.md`](../Media/S.Media.Core/PROJECT-STATUS.md)
- [`Media/S.Media.FFmpeg/PROJECT-STATUS.md`](../Media/S.Media.FFmpeg/PROJECT-STATUS.md)
- [`Media/S.Media.PortAudio/PROJECT-STATUS.md`](../Media/S.Media.PortAudio/PROJECT-STATUS.md)
- [`Media/S.Media.MIDI/PROJECT-STATUS.md`](../Media/S.Media.MIDI/PROJECT-STATUS.md)
- [`Media/S.Media.NDI/PROJECT-STATUS.md`](../Media/S.Media.NDI/PROJECT-STATUS.md)
- [`Media/S.Media.OpenGL/PROJECT-STATUS.md`](../Media/S.Media.OpenGL/PROJECT-STATUS.md)
- [`Media/S.Media.OpenGL.Avalonia/PROJECT-STATUS.md`](../Media/S.Media.OpenGL.Avalonia/PROJECT-STATUS.md)
- [`Media/S.Media.OpenGL.SDL3/PROJECT-STATUS.md`](../Media/S.Media.OpenGL.SDL3/PROJECT-STATUS.md)
- [`NDI/PROJECT-STATUS.md`](../NDI/PROJECT-STATUS.md)
- [`OSC/PROJECT-STATUS.md`](../OSC/PROJECT-STATUS.md)
- [`Test/PROJECT-STATUS.md`](../Test/PROJECT-STATUS.md)
- [`VideoLibs/PROJECT-STATUS.md`](../VideoLibs/PROJECT-STATUS.md)

- [`setup-prerequisites.md`](setup-prerequisites.md)
  - What to install, what to build, and environment variables.
- [`audioex-setup.md`](audioex-setup.md)
  - How the SDL3 AudioEx test player is wired (playlist, offsets, controls, counters, burst summaries).
- [`videotest-setup.md`](videotest-setup.md)
  - How the Avalonia VideoTest app is wired (4 mirrored views, controls, HUD, counters, burst summaries).
- [`video-mixer-basics.md`](video-mixer-basics.md)
  - Minimal video-only pipeline with `VideoMixer`.
- [`audio-video-mixer.md`](audio-video-mixer.md)
  - Audio-led A/V playback with `AudioVideoMixer`, drift correction, and a tuning table.
- [`interop-ownaudio.md`](interop-ownaudio.md)
  - How video classes map to base OwnAudio classes, plus SDL3/Avalonia and no-audio output snippets.
- [`multiplexers.md`](multiplexers.md)
  - How to fan out one audio/video stream to multiple engines/outputs, including local+NDI recipes.
- [`ndi-receive.md`](ndi-receive.md)
  - NDI receive pipeline (`NDIAudioStreamSource`, `NDIVideoStreamDecoder`, external timeline clock, tuning profiles).
- [`ndi-dropped-frame-remediation-plan.md`](ndi-dropped-frame-remediation-plan.md)
  - Findings and phased remediation plan for dropped frames in the `S.Media.NDI` receive pipeline.
- [`ndi-send.md`](ndi-send.md)
  - NDI send pipeline (`NDIVideoEngine`, sink routing, direct audio/video send APIs).
- [`hard-cut-sweep.md`](hard-cut-sweep.md)
  - Migration sweep status for legacy `Seko.OwnAudioNET.*` / `OwnAudio` dependency removal.
- [`Media/S.Media.FFmpeg/ffmpeg-migration-plan.md`](../Media/S.Media.FFmpeg/ffmpeg-migration-plan.md)
  - Legacy-to-target adaptation plan for FFmpeg decoding internals into `S.Media.FFmpeg` (implementation adaptation only, no class moves).
- [`Media/S.Media.OpenGL/opengl-migration-plan.md`](../Media/S.Media.OpenGL/opengl-migration-plan.md)
  - Legacy-to-target migration matrix for moving performant OpenGL runtime and adapter code into `S.Media.OpenGL*`.

## Recommended read order

1. Setup and prerequisites
2. Project implementation stages snapshot
3. Project status notes
4. Refactor considerations log
5. AudioEx setup
6. VideoTest setup
7. Video mixer basics
8. Audio/video mixer (audio-led)
9. Multiplexer patterns (audio/video fan-out)
10. NDI receive setup and tuning
11. NDI dropped-frame findings and remediation plan
12. NDI send setup and sink usage
13. OwnAudio interop notes and output snippets
14. FFmpeg migration plan (implementation adaptation matrix)
15. OpenGL migration plan (legacy-to-target implementation matrix)

## Where to look for runnable references

- `Test/AudioEx/Program.cs` (current active test-app entrypoint during migration)
- `Archive/Legacy/AudioEx/Program.cs` (archived legacy reference)
- `Archive/Legacy/VideoTest/MainWindow.axaml.cs` (archived legacy reference)
- `Archive/Legacy/NdiVideoReceive/Program.cs` (archived legacy reference)

Note: legacy references are archived for migration history and are no longer part of the active solution graph.

## Diagnostics Counter Legend

Use this legend for the live diagnostics lines in `AudioEx` and `VideoTest`:

- audio hard-sync counters:
  - `a_hseek`: hard-sync seek attempts
  - `a_hsup`: hard-sync seeks suppressed during the post-seek suppression window
  - `a_hfail`: hard-sync seek failures
- video hard-resync counters:
  - `v_rseek`: hard-resync attempts
  - `v_rok`: hard-resync successes
  - `v_rfail`: hard-resync failures
  - `v_rsup`: drift-correction ticks suppressed during the post-seek suppression window
- burst summary:
  - `[Burst10s]`: aggregated 10-second totals plus drift ranges (`v-m`, `v-a`)
