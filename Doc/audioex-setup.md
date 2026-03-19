# AudioEx Setup

This guide explains how `Test/AudioEx` is wired and how to run it.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## What AudioEx does

`AudioEx` is an SDL3-based A/V debug player that builds one shared mixer pipeline and prints live diagnostics.

Current setup in `Test/AudioEx/Program.cs`:

- Creates an OwnAudio engine (`AudioPlaybackEngineFactory.CreateEngine`) and `AudioMixer`
- Creates video transport (`VideoTransportEngine`) in audio-led mode
- Wraps both with `AudioVideoMixer`
- Enables drift correction via `AudioVideoDriftCorrectionConfig`
- Creates one `VideoSDL` output and binds it to the active `FFVideoSource`
- Supports playlist-style sequential playback by assigning cumulative `StartOffset` values to each audio/video source pair

## Pipeline architecture

Per app instance:

1. Parse inputs and validate files
2. Probe each file for first audio+video stream (`MediaStreamCatalog`)
3. Build shared mixer stack:
   - `AudioMixer`
   - `MasterClockVideoClockAdapter`
   - `VideoTransportEngine` (`ClockSyncMode = AudioLed`)
   - `VideoMixer`
   - `AudioVideoMixer`
4. For each media item:
   - create `FFVideoDecoder` + `FFAudioDecoder`
   - create `FFVideoSource` + `FFAudioSource`
   - assign `StartOffset` on both sources
   - add sources to `AudioVideoMixer`
5. Bind `VideoSDL` to current active source, start playback, switch source binding as timeline crosses item boundaries

## Shared demux behavior

AudioEx supports both modes:

- shared demux per media item (default)
- separate decode sessions (fallback)

Environment toggle:

- `AUDIOEX_USE_SHARED_DEMUX=0` -> disable shared demux

## Controls (while SDL window is focused)

- `Space`: play/pause
- `Left` / `Right`: seek -/+ 5s
- `Home` / `End`: seek start/end of full playlist timeline
- `F11`: toggle fullscreen
- `H`: toggle HUD

## Runtime diagnostics

AudioEx prints live line stats including:

- timeline position (`tl=`)
- render/source FPS
- frame counts (`pres`, `dec`, `drop`, `q`)
- upload metrics (`up`, `upP`, `strF`, `strP`)
- drift values (`v-m`, `v-a`) and correction (`corr`)

## Run commands

```bash
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release
```

Playlist run:

```bash
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release -- "/path/to/file1.mov" "/path/to/file2.mov"
```

Disable shared demux for A/B testing:

```bash
env AUDIOEX_USE_SHARED_DEMUX=0 dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release -- "/path/to/file1.mov" "/path/to/file2.mov"
```

## Key files

- `Test/AudioEx/Program.cs`
- `VideoLibs/Seko.OwnAudioNET.Video/Mixing/AudioVideoMixer.cs`
- `VideoLibs/Seko.OwnAudioNET.Video/Sources/FFVideoSource.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.cs`

