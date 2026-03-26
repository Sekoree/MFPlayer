# Hard-Cut Sweep Baseline

This document tracks the migration sweep for removing active `Seko.OwnAudioNET.*` and `OwnAudio` usage.

## Baseline Snapshot (2026-03-26)

- `Media/*` implementation code: no direct `Seko.OwnAudioNET.*` or `OwnAudio` references found in `*.cs` / `*.csproj`.
- `Media/S.Media.Core/PLAN.smedia-architecture.md` intentionally contains migration references to legacy names.
- Legacy project references are still intentionally present in selected harnesses and solution entries during side-by-side migration.
- `MFPlayer.sln` currently includes both legacy `VideoLibs/Seko.OwnAudioNET.*` projects and new `S.Media.*` projects.

## Outstanding Hard-Cut Actions

1. Port remaining legacy harnesses (`AudioEx`, `NdiVideoReceive`, `VideoTest`) to `S.Media.*` runtime surfaces.
2. Keep `VideoStress` as canonical Avalonia stress harness and complete parity port from `VideoTest`.
3. Remove legacy `VideoLibs/Seko.OwnAudioNET.*` projects from `MFPlayer.sln` once parity gates pass.
4. Remove legacy package usage from active harnesses and central package props after cutover.
5. Rename remaining setup docs when harness cutover completes:
   - `Doc/audioex-setup.md` -> `Doc/mediadebug-setup.md`
   - `Doc/videotest-setup.md` -> `Doc/videostress-setup.md`

## Validation Commands

```fish
rg -n -g '*.{cs,csproj}' 'Seko\.OwnAudioNET|OwnAudio' Media Test
rg -n "Seko\.OwnAudioNET\.Video" MFPlayer.sln
rg -n "AudioEx\.csproj|VideoTest\.csproj|NdiVideoReceive\.csproj" MFPlayer.sln
```
