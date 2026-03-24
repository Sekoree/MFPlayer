# Hard-Cut Sweep Baseline

This document tracks the migration sweep for removing active `Seko.OwnAudioNET.*` and `OwnAudio` usage.

## Baseline Snapshot (2026-03-22)

- `Media/*` implementation code: no direct `Seko.OwnAudioNET.*` or `OwnAudio` references found in `*.cs` / `*.csproj`.
- `Media/S.Media.Core/PLAN.smedia-architecture.md` intentionally contains migration references to legacy names.
- `Test/AudioEx/*`: no direct `Seko.OwnAudioNET.*` or `OwnAudio` references found.
- Legacy-only references are now archived under:
  - `Archive/Legacy/AudioEx/*`
  - `Archive/Legacy/VideoTest/*`
  - `Archive/Legacy/NdiVideoReceive/*`
  - `Archive/Legacy/Seko.OwnAudioNET.*/*`
- `MFPlayer.sln`: legacy `VideoLibs/Seko.OwnAudioNET.*` and legacy test app projects removed from active solution graph.

## Outstanding Hard-Cut Actions

1. Source-tree archival completed via `Tools/hard-cut-archive.sh`.
2. Keep legacy projects out of `MFPlayer.sln` (completed in this baseline; prevent reintroduction).
3. Rename remaining setup docs:
   - `Doc/audioex-setup.md` -> `Doc/mediadebug-setup.md`
   - `Doc/videotest-setup.md` -> `Doc/videostress-setup.md`
4. Update docs index/read order to remove transition-era legacy entries after cutover validation.
5. Use `Tools/hard-cut-archive.sh` for staged, reversible source-tree archival when ready.

## Validation Commands

```fish
rg -n -g '*.{cs,csproj}' 'Seko\.OwnAudioNET|OwnAudio' Media Test/AudioEx
rg -n "Seko\.OwnAudioNET|OwnAudio" Archive/Legacy/AudioEx Archive/Legacy/VideoTest Archive/Legacy/NdiVideoReceive
rg -n "Seko\.OwnAudioNET\.Video" MFPlayer.sln
rg -n "AudioEx\.csproj|VideoTest\.csproj|NdiVideoReceive\.csproj" MFPlayer.sln
bash Tools/hard-cut-archive.sh
```

