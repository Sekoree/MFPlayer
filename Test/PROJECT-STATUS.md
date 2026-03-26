# Test Harness Project Status

Last updated: 2026-03-26

## Scope

- `Test/AudioEx/AudioEx.csproj`
- `Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj`
- `Test/NdiVideoReceive/NdiVideoReceive.csproj`
- `Test/VideoStress/VideoStress.csproj`
- `Test/VideoTest/VideoTest.csproj` (legacy harness kept as migration reference)

## Current Stage

- `AudioEx`: In Progress
- `FirstAudioPlayback.Smoke`: Validation
- `NdiVideoReceive`: In Progress
- `VideoStress`: In Progress
- `VideoTest`: Legacy-Migration

## Notes

- `FirstAudioPlayback.Smoke` is the active first-audio bring-up harness (direct decoder -> output).
- `VideoStress` is now the canonical Avalonia video stress harness path and references `S.Media.*` modules.
- `AudioEx` now uses `S.Media.FFmpeg` + `S.Media.PortAudio` for direct audio decode/output stress loops.
- `NdiVideoReceive` now uses `S.Media.NDI` for discovery + source read smoke validation.
- `VideoTest` remains in-tree as legacy migration reference until runtime feature parity is ported.
- These projects are intentionally practical and may evolve quickly with backend changes.

## Related Docs

- `Test/FirstAudioPlayback.Smoke/README.md`
- `Doc/audioex-setup.md`
- `Doc/videotest-setup.md`
- `Doc/project-implementation-stages.md`
