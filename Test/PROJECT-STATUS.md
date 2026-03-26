# Test Harness Project Status

Last updated: 2026-03-26

## Scope — End-to-End S.Media Test Apps (NEW)

| # | App | What it tests |
|---|-----|---------------|
| 1 | `SimpleAudioTest` | FFMediaItem → PortAudio output (device picker) |
| 2 | `SimpleVideoTest` | FFMediaItem → SDL3 standalone window |
| 3 | `AudioMixerTest` | 2 audio files via AudioMixer with offset |
| 4 | `VideoMixerTest` | 2 video files via VideoMixer (source switching) |
| 5 | `MediaPlayerTest` | A/V via MediaPlayer + SDL3 + PortAudio |
| 6 | `AVMixerTest` | A/V via AudioVideoMixer directly + SDL3 + PortAudio |
| 7 | `MultiViewTest` | Avalonia 2×2 grid with 4 cloned GL outputs (stress) |
| 8 | `NDIReceiveTest` | NDI discovery → AVMixer + SDL3 + PortAudio |
| 9 | `NDISendTest` | Video file → NDI output |

All accept `--input <path>` or env `SMEDIA_TEST_INPUT`. See `Test/TEST-APPS-PLAN.md` for full details.

## Scope — Legacy / Migration Harnesses

- `Test/AudioEx/AudioEx.csproj`
- `Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj`
- `Test/NdiVideoReceive/NdiVideoReceive.csproj`
- `Test/VideoStress/VideoStress.csproj`
- `Test/VideoTest/VideoTest.csproj` (legacy harness kept as migration reference)

## Current Stage

**New apps**: All 9 done and building (0W/0E). 265 existing tests pass (4 skipped).

- `AudioEx`: In Progress
- `FirstAudioPlayback.Smoke`: Validation
- `NdiVideoReceive`: In Progress
- `VideoStress`: In Progress
- `VideoTest`: Legacy-Migration

## Notes

- The 9 new test apps exercise the full S.Media API surface end-to-end (audio, video, mixers, player, NDI).
- `MultiViewTest` is an Avalonia desktop app; all others are console apps.
- `FirstAudioPlayback.Smoke` is the active first-audio bring-up harness (direct decoder -> output).
- `VideoStress` is now the canonical Avalonia video stress harness path and references `S.Media.*` modules.
- `AudioEx` now uses `S.Media.FFmpeg` + `S.Media.PortAudio` for direct audio decode/output stress loops.
- `NdiVideoReceive` now uses `S.Media.NDI` for discovery + source read smoke validation.
- `VideoTest` remains in-tree as legacy migration reference until runtime feature parity is ported.
- These projects are intentionally practical and may evolve quickly with backend changes.

## Related Docs

- `Test/TEST-APPS-PLAN.md` — full plan for the 9 new test apps
- `Test/FirstAudioPlayback.Smoke/README.md`
- `Doc/audioex-setup.md`
- `Doc/videotest-setup.md`
- `Doc/project-implementation-stages.md`
