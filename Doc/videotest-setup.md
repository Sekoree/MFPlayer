# VideoTest Setup

This guide explains how `Test/VideoTest` is wired and how to run it.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## What VideoTest does

`VideoTest` is an Avalonia stress test that renders one mixer-bound video output mirrored to 4 UI views.

Current setup in `Test/VideoTest/MainWindow.axaml.cs`:

- Creates OwnAudio engine + `AudioMixer`
- Builds audio-led video transport and wraps with `AudioVideoMixer`
- Creates one `FFVideoSource` and one `FFAudioSource`
- Binds one primary `VideoGL` output to the source
- Creates 3 additional UI mirrors (`VideoGL.CreateMirror(primary)`)
- Prints per-second diagnostics in title + console

## Important input note

At the moment, `VideoTest` uses a hardcoded media path in `OnOpened`:

- `var testFile = "/home/seko/Videos/shootingstar_0611_1.mov";`

There is still a console message mentioning arg/env selection, but the current code path is hardcoded.

## Pipeline architecture

1. `MainWindow.OnOpened` probes first video and first audio stream
2. Creates `FFVideoDecoder` + `FFAudioDecoder`
3. Creates `FFVideoSource` + `FFAudioSource`
4. Builds shared mixer stack:
   - `AudioMixer`
   - `MasterClockVideoClockAdapter`
   - `VideoTransportEngine` (`ClockSyncMode = AudioLed`)
   - `VideoMixer`
   - `AudioVideoMixer` with `AudioVideoDriftCorrectionConfig`
5. Adds audio/video sources and starts mixer
6. Routes one primary `VideoGL` output through mixer, mirrors to 4 controls

## Shared demux behavior

Environment toggle:

- `VIDEOTEST_USE_SHARED_DEMUX=0` -> disable shared demux
- unset/non-zero -> shared demux enabled

## Controls (main window)

- `Space`: play/pause
- `Left` / `Right`: seek -/+ 5s
- `Home` / `End`: seek start/end
- `F11`: toggle fullscreen
- `H`: toggle VideoGL HUD overlay (applies to all mirrored views)
- `Escape`: close

## Diagnostics

`VideoTest` reports:

- decode/present/drop deltas
- upload + strided upload counts and percentages
- hardware decode state and pixel format conversion
- drift (`v-m`, `v-a`) and correction offset (`corr`)
- per-view diagnostics for all 4 views

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

Every ~10 seconds VideoTest also prints a `[Burst10s]` summary line:

- `[Burst10s] ...` with aggregated counter totals and drift ranges.

## Threading override

- `VIDEOTEST_VIDEO_THREADS`
  - optional explicit decoder thread count override for heavy media.

## Run commands

```fish
dotnet run --project "/home/sekoree/RiderProjects/MFPlayer/Test/VideoTest/VideoTest.csproj" -c Release
```

Run with separate demux sessions:

```fish
env VIDEOTEST_USE_SHARED_DEMUX=0 dotnet run --project "/home/sekoree/RiderProjects/MFPlayer/Test/VideoTest/VideoTest.csproj" -c Release
```

Run with explicit decoder threads:

```fish
env VIDEOTEST_VIDEO_THREADS=6 dotnet run --project "/home/sekoree/RiderProjects/MFPlayer/Test/VideoTest/VideoTest.csproj" -c Release
```

## Key files

- `Test/VideoTest/MainWindow.axaml.cs`
- `Test/VideoTest/MainWindow.Diagnostics.cs`
- `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.cs`
- `VideoLibs/Seko.OwnAudioNET.Video/Mixing/AudioVideoMixer.cs`

