# Test Harness Project Status

Last updated: 2026-03-25

## Scope

- `Test/AudioEx/AudioEx.csproj`
- `Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj`
- `Test/NdiVideoReceive/NdiVideoReceive.csproj`
- `Test/VideoTest/VideoTest.csproj`

## Current Stage

- All projects in this folder: Validation

## Notes

- `FirstAudioPlayback.Smoke` is the active first-audio bring-up harness (direct decoder -> output).
- `AudioEx` and `VideoTest` are scenario/stress harnesses used for runtime behavior validation.
- These projects are intentionally practical and may evolve quickly with backend changes.

## Related Docs

- `Test/FirstAudioPlayback.Smoke/README.md`
- `Doc/audioex-setup.md`
- `Doc/videotest-setup.md`
- `Doc/project-implementation-stages.md`
