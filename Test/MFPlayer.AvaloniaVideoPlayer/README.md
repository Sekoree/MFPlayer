# MFPlayer.AvaloniaVideoPlayer

Minimal Avalonia sample app that plays one video stream using:

- `S.Media.FFmpeg` (`FFmpegDecoder`, video-only)
- `S.Media.Avalonia` (`AvaloniaOpenGlVideoOutput`)

## Run

Pass the media path as the first argument.

```bash
dotnet run --project /home/sekoree/RiderProjects/MFPlayer/Test/MFPlayer.AvaloniaVideoPlayer/MFPlayer.AvaloniaVideoPlayer.csproj -- "/path/to/video.mp4"
```

## Diagnostics

The app prints periodic `[vstats]` lines to console (about once per second) so you can spot slow rendering and drops.

- `fps`: presented frames per second vs expected stream fps
- `drop`: stale frames dropped by mixer pacing
- `held`: frames held waiting for target PTS
- `pull`: decoder pull hits/attempts
- `ex`: render exceptions in the interval
