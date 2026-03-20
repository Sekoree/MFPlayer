# Setup and Prerequisites

This project uses FFmpeg-backed decoding with video output adapters and OwnAudio for audio transport/mixing.

## 1) Required projects

Core libraries:

- `VideoLibs/Seko.OwnAudioNET.Video`
- `VideoLibs/Seko.OwnAudioNET.Video.Engine`

Output libraries (pick one or both):

- `VideoLibs/Seko.OwnAudioNET.Video.SDL3`
- `VideoLibs/Seko.OwnAudioNET.Video.Avalonia`

Sample apps:

- `Test/AudioEx`
- `Test/VideoTest`
- `Test/NdiVideoSend`

## 2) FFmpeg runtime

The code uses `FFmpeg.AutoGen` and expects FFmpeg shared libraries to be discoverable.

You can set:

- `FFMPEG_ROOT` to a folder containing FFmpeg shared libs

Examples in this repo also probe common Linux paths (`/lib`, `/usr/lib`, `/usr/local/lib`, `/usr/lib/x86_64-linux-gnu`).

## 3) Build commands

```fish
dotnet build "/home/seko/RiderProjects/MFPlayer/VideoLibs/Seko.OwnAudioNET.Video/Seko.OwnAudioNET.Video.csproj" -c Release
dotnet build "/home/seko/RiderProjects/MFPlayer/VideoLibs/Seko.OwnAudioNET.Video.Engine/Seko.OwnAudioNET.Video.Engine.csproj" -c Release
dotnet build "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release
dotnet build "/home/seko/RiderProjects/MFPlayer/Test/VideoTest/VideoTest.csproj" -c Release
dotnet build "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoSend/NdiVideoSend.csproj" -c Release
```

## 4) Runtime toggles used by sample apps

- `AUDIOEX_USE_SHARED_DEMUX`
  - `0` disables shared demux in `AudioEx`
  - unset or non-zero enables shared demux
- `VIDEOTEST_USE_SHARED_DEMUX`
  - `0` disables shared demux in `VideoTest`
  - unset or non-zero enables shared demux
- `FFMPEG_ROOT`
  - optional FFmpeg root override
- `AUDIOEX_VIDEO_THREADS`
  - optional explicit video decoder thread count override for `AudioEx`
- `VIDEOTEST_VIDEO_THREADS`
  - optional explicit video decoder thread count override for `VideoTest`

## 5) Common troubleshooting

- No video/audio stream found:
  - confirm the file has decodable streams and correct stream index selection.
- FFmpeg load issues:
  - set `FFMPEG_ROOT` and verify shared libraries are present.
- Clock mismatch symptoms:
  - prefer `VideoTransportClockSyncMode.AudioLed` for A/V playback.
  - review `AudioVideoDriftCorrectionConfig` values if drift correction is too weak/aggressive.

