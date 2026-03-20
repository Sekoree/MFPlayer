# NDI Receive Pipeline

This guide documents the current NDI receive path used by `Test/NdiVideoSend` and the reusable adapters in `VideoLibs/Seko.OwnAudioNET.Video.NDI`.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## Main components

- `NdiReceiver` + `NdiFrameSync` (`NDI/NdiLib`)
  - discovers/connects and provides frame-sync pull APIs.
- `NdiAudioStreamSource`
  - OwnAudio `BaseAudioSource` implementation backed by frame-sync audio pull and ring buffering.
- `NdiVideoStreamDecoder`
  - `IVideoDecoder` adapter backed by frame-sync video pull.
- `NdiExternalTimelineClock`
  - resolves NDI timestamp/timecode into a monotonic playback timeline and compensates buffered-audio latency.
- `NdiReceiveTuningProfile`
  - preset policy (`Stable`, `Balanced`, `LowLatency`) for audio buffering + clock smoothing knobs.

## Runtime graph in `NdiVideoSend`

1. Discover first available NDI source and connect receiver.
2. Create shared `NdiFrameSync` and synchronization lock.
3. Build timeline clock from profile presets.
4. Probe NDI audio format, initialize `NativeAudioEngine`, create `AudioMixer`.
5. Create `VideoTransportEngine` in audio-led mode.
6. Create `VideoMixer` + `AudioVideoMixer`.
7. Add `NdiAudioStreamSource` and `VideoStreamSource` (using `NdiVideoStreamDecoder`).
8. Add one SDL output, bind output to source, start playback.

## Tuning profiles

`NdiReceiveTuningProfile` presets map to:

- `NdiAudioStreamSourceOptions`
  - ring capacity multiplier
  - capture high-watermark ratio
  - capture sleep interval
  - capture request size
- `NdiExternalTimelineClockOptions`
  - fallback frame duration
  - latency smoothing factor
  - minimum video forward-advance ratio

CLI accepts optional profile token:

- `stable`
- `balanced` (default)
- `lowlatency`

Unknown tokens are ignored with a warning.

## Diagnostics exposed by `NdiVideoSend`

- audio: ring fill, underruns, read/capture deltas, source state
- video: decode/present/render fps deltas, dropped frames delta, queue depth
- sync: current frame PTS vs mixer clock drift
- HUD: toggle with `H`

## Run commands

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoSend/NdiVideoSend.csproj" -c Release
```

Run with timeout only:

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoSend/NdiVideoSend.csproj" -c Release -- 15
```

Run with profile:

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoSend/NdiVideoSend.csproj" -c Release -- 15 lowlatency
```

## Multiplexing note

`VideoMixer` now accepts a single primary output sink. To fan out one decoded stream to multiple outputs, route through:

- `MultiplexVideoOutputEngine`
- wrapped by `VideoOutputEngineSink`
- then register that sink as the single `VideoMixer` output.

For audio, `MultiplexAudioEngine` provides the equivalent `IAudioEngine` fan-out wrapper.

For outbound sender setup, see `Doc/ndi-send.md`.

