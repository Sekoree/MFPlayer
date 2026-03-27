# Hard-Cut Sweep — Completed

This document tracked the migration from `Seko.OwnAudioNET.*` / `OwnAudio` to the `S.Media.*` framework.

## Status: ✅ Complete (2026-03-27)

- All `Media/*` implementation code uses the `S.Media.*` runtime exclusively.
- Legacy `VideoLibs/Seko.OwnAudioNET.*` projects have been removed from the solution and filesystem.
- All test harnesses (`AudioEx`, `NDIVideoReceive`, `VideoStress`, etc.) reference only `S.Media.*` modules.
- No `OwnAudio` references remain in active source code or project files.

## Validation

```fish
rg -n -g '*.{cs,csproj}' 'Seko\.OwnAudioNET|OwnAudio' Media Test Audio NDI MIDI OSC
```
