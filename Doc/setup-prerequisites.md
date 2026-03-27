# Setup and Prerequisites

This project uses FFmpeg-backed decoding, PortAudio for audio transport, and OpenGL (via SDL3 or Avalonia) for video output.

## 1) Required projects

Core libraries:

- `Media/S.Media.Core` — interfaces, error codes, mixer, clock, player
- `Media/S.Media.FFmpeg` — FFmpeg-backed media decoding
- `Media/S.Media.PortAudio` — PortAudio audio engine and output
- `Media/S.Media.OpenGL` — OpenGL video output engine

Output adapters (pick one or both):

- `Media/S.Media.OpenGL.SDL3` — SDL3 standalone video window
- `Media/S.Media.OpenGL.Avalonia` — Avalonia embedded GL control

Native wrappers:

- `Audio/PALib` — PortAudio P/Invoke bindings
- `NDI/NDILib` — NDI SDK P/Invoke bindings
- `MIDI/PMLib` — PortMidi P/Invoke bindings

Sample apps:

- `Test/SimpleAudioTest`
- `Test/SimpleVideoTest`
- `Test/MediaPlayerTest`
- `Test/AVMixerTest`
- `Test/NDIVideoReceive`

## 2) FFmpeg runtime

The code uses `FFmpeg.AutoGen` and expects FFmpeg shared libraries to be discoverable.

You can set:

- `FFMPEG_ROOT` to a folder containing FFmpeg shared libs

Examples in this repo also probe common Linux paths (`/lib`, `/usr/lib`, `/usr/local/lib`, `/usr/lib/x86_64-linux-gnu`).

## 3) Build commands

```fish
dotnet build MFPlayer.sln -c Release
```

Or build individual test apps:

```fish
dotnet build Test/SimpleAudioTest/SimpleAudioTest.csproj -c Release
dotnet build Test/MediaPlayerTest/MediaPlayerTest.csproj -c Release
dotnet build Test/NDIVideoReceive/NDIVideoReceive.csproj -c Release
```

## 4) Runtime toggles used by sample apps

- `SMEDIA_TEST_INPUT`
  - Path to a media file used by test apps (also accepted via `--input <path>`)
- `FFMPEG_ROOT`
  - optional FFmpeg root override
- `AUDIOEX_USE_SHARED_DEMUX`
  - `0` disables shared demux in `AudioEx`
  - unset or non-zero enables shared demux

## 5) Common troubleshooting

- No video/audio stream found:
  - confirm the file has decodable streams and correct stream index selection.
- FFmpeg load issues:
  - set `FFMPEG_ROOT` and verify shared libraries are present.
- Clock mismatch symptoms:
  - use `AudioVideoSyncMode.Synced` (default) for A/V playback.
  - drift correction is handled automatically by the mixer.
