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
- [`ndi-send.md`](ndi-send.md)
  - NDI send pipeline (`NDIVideoEngine`, sink routing, direct audio/video send APIs).

## Recommended read order

1. Setup and prerequisites
2. AudioEx setup
3. VideoTest setup
4. Video mixer basics
5. Audio/video mixer (audio-led)
6. Multiplexer patterns (audio/video fan-out)
7. NDI receive setup and tuning
8. NDI send setup and sink usage
9. OwnAudio interop notes and output snippets

## Where to look for runnable references

- `Test/AudioEx/Program.cs`
- `Test/VideoTest/MainWindow.axaml.cs`
- `Test/NdiVideoReceive/Program.cs`

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

