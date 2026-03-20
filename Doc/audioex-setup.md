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
- Creates one `VideoSDL` output and binds it to the active `VideoStreamSource`
- Keeps one mixer-bound output sink (mirroring/fan-out should happen downstream)
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
   - create `VideoStreamSource` + `AudioStreamSource`
   - assign `StartOffset` on both sources
   - add sources to `AudioVideoMixer`
5. Bind `VideoSDL` to current active source, start playback, switch source binding as timeline crosses item boundaries

## Shared demux behavior

AudioEx supports both modes:

- shared demux per media item (default)
- separate decode sessions (fallback)

Environment toggle:

- `AUDIOEX_USE_SHARED_DEMUX=0` -> disable shared demux

Shared demux API now supports both:

- `FFSharedDemuxSession.OpenFile(...)`
- `FFSharedDemuxSession.OpenStream(stream, leaveOpen: true, ...)`

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

Additional sync diagnostics:

- audio hard-sync counters:
  - `a_hseek` (hard-sync seek attempts)
  - `a_hsup` (hard-sync seeks suppressed during the post-seek suppression window)
  - `a_hfail` (hard-sync seek failures)
- video hard-resync counters:
  - `v_rseek` (hard-resync attempts)
  - `v_rok` (hard-resync successes)
  - `v_rfail` (hard-resync failures)
  - `v_rsup` (drift-correction ticks suppressed during the post-seek suppression window)

Every ~10 seconds AudioEx also prints a `[Burst10s]` summary line:

- `[Burst10s] ...` with aggregated counter totals and drift ranges (`v-m`, `v-a`).

## Threading overrides

- `AUDIOEX_VIDEO_THREADS`
  - optional explicit decoder thread count override.
  - useful for heavy mezzanine codecs (for example 4K60 ProRes 422/10-bit).

## Run commands

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release
```

Playlist run:

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release -- "/path/to/file1.mov" "/path/to/file2.mov"
```

Disable shared demux for A/B testing:

```fish
env AUDIOEX_USE_SHARED_DEMUX=0 dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release -- "/path/to/file1.mov" "/path/to/file2.mov"
```

Override decoder thread count:

```fish
env AUDIOEX_VIDEO_THREADS=6 dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/AudioEx/AudioEx.csproj" -c Release -- "/path/to/file1.mov" "/path/to/file2.mov"
```

## Key files

- `Test/AudioEx/Program.cs`
- `VideoLibs/Seko.OwnAudioNET.Video/Mixing/AudioVideoMixer.cs`
- `VideoLibs/Seko.OwnAudioNET.Video/Sources/VideoStreamSource.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.cs`

