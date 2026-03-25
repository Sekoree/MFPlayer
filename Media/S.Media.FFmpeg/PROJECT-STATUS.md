# S.Media.FFmpeg Project Status

Last updated: 2026-03-25

## Scope

- `Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj`
- `Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj`

## Current Stage

- Module: In Progress
- Tests: Validation

## Implemented Highlights

- Shared demux session path (`FFSharedDemuxSession`) is active.
- Decode/resample/convert pipeline is implemented with native-attempt and deterministic fallback behavior.
- Heavy-path tests exist and remain opt-in for default sweeps.

## Current Considerations

- Preserve strict native decode semantics (including non-frame EAGAIN behavior).
- Keep sample/frame count shaping deterministic through decode and resample stages.
- Continue parity checks against migration references where behavior is still being tuned.

## Related Docs

- `Media/S.Media.FFmpeg/API-outline.md`
- `Media/S.Media.FFmpeg/IMPLEMENTATION-PROGRESS.md`
- `Media/S.Media.FFmpeg/ffmpeg-migration-plan.md`
- `Doc/refactor-considerations-log.md`
