# VideoStress (S.Media)

Canonical Avalonia stress harness for the ongoing migration to `S.Media.*`.

## Current State

- Uses new module references (`S.Media.Core`, `S.Media.FFmpeg`, `S.Media.OpenGL`, `S.Media.OpenGL.Avalonia`, `S.Media.PortAudio`).
- Provides bootstrap and input-path resolution (`arg[0]`, `VIDEOSTRESS_INPUT`, legacy fallback `VIDEOTEST_INPUT`).
- Runs a functional FFmpeg video read loop into four Avalonia OpenGL host views.
- Includes basic runtime controls: play/pause, seek, HUD toggle, and close hotkeys.

## Quick Run

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/VideoStress/VideoStress.csproj -- /path/to/media.mp4
```

## Build

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet build Test/VideoStress/VideoStress.csproj --no-restore
```

## Controls

- `Space`: play/pause loop
- `Left` / `Right`: seek -5s / +5s
- `Home` / `End`: seek to start/end
- `H`: toggle HUD overlay mode on all views
- `Esc`: close app

