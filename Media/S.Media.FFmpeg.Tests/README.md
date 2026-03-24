# S.Media.FFmpeg.Tests

`S.Media.FFmpeg.Tests` includes unit tests and an opt-in local stress scenario for heavy video files.

## Heavy Video Stress Test (Opt-In)

The `FFPlaybackStressTests` test is disabled by default. To enable it locally:

- Set `RUN_HEAVY_FFMPEG_TESTS=1` (preferred)
- Legacy compatibility: `SMEDIA_RUN_HEAVY_STRESS=1` also works
- Optionally set `SMEDIA_HEAVY_VIDEO_PATH` (default: `/home/seko/Videos/shootingstar_0611_1.mov`)

When opt-in is not enabled, or when the heavy asset path does not exist, the stress test is marked as **Skipped**.

Example:

```fish
set -x RUN_HEAVY_FFMPEG_TESTS 1
set -x SMEDIA_HEAVY_VIDEO_PATH /home/seko/Videos/shootingstar_0611_1.mov
dotnet test /home/seko/RiderProjects/MFPlayer/Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj
```

