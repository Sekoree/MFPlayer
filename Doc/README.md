# MFPlayer Video Docs

This folder contains practical setup and usage guides for the video layer in `VideoLibs/*` and its integration with OwnAudio mixers/clocks.

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

## Recommended read order

1. Setup and prerequisites
2. AudioEx setup
3. VideoTest setup
4. Video mixer basics
5. Audio/video mixer (audio-led)
6. OwnAudio interop notes and output snippets

## Where to look for runnable references

- `Test/AudioEx/Program.cs`
- `Test/VideoTest/MainWindow.axaml.cs`

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

