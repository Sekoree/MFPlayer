# AudioEx Setup

This guide explains how `Test/AudioEx` is wired and how to run it.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## What AudioEx does

`AudioEx` is a stress-test harness that performs direct FFmpeg → PortAudio decode/output loops with live diagnostics.

Current setup in `Test/AudioEx/Program.cs`:

- Opens media via `FFMediaItem` and creates `FFAudioSource` / `FFVideoSource`
- Creates a `PortAudioEngine` and `PortAudioOutput` for audio playback
- Runs decode→push loops with error tracking and semantic code reporting

## Shared demux behavior

AudioEx supports both modes:

- shared demux per media item (default)
- separate decode sessions (fallback)

Environment toggle:

- `AUDIOEX_USE_SHARED_DEMUX=0` -> disable shared demux

## Run commands

```fish
dotnet run --project Test/AudioEx/AudioEx.csproj -c Release -- "/path/to/file.mov"
```

Disable shared demux for A/B testing:

```fish
env AUDIOEX_USE_SHARED_DEMUX=0 dotnet run --project Test/AudioEx/AudioEx.csproj -c Release -- "/path/to/file.mov"
```

## Key files

- `Test/AudioEx/Program.cs`
- `Media/S.Media.FFmpeg/Media/FFMediaItem.cs`
- `Media/S.Media.FFmpeg/Sources/FFAudioSource.cs`
- `Media/S.Media.PortAudio/Output/PortAudioOutput.cs`
