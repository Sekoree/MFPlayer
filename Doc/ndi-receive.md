# NDI Receive Pipeline

This guide documents the current NDI receive path used by `Test/NdiVideoReceive` and the reusable adapters in `VideoLibs/Seko.OwnAudioNET.Video.NDI`.

Prerequisite: see `Doc/setup-prerequisites.md` first.

## Main components

- `NdiReceiver` + `NdiFrameSync` (`NDI/NdiLib`)
  - discovers/connects and provides frame-sync pull APIs.
  - receiver defaults to `NdiRecvColorFormat.Fastest` for native low-cost frame formats.
- `NDIAudioStreamSource`
  - OwnAudio `BaseAudioSource` implementation backed by frame-sync audio pull and ring buffering.
- `NDIVideoStreamDecoder`
  - `IVideoDecoder` adapter backed by frame-sync video pull.
- `NDIExternalTimelineClock`
  - resolves NDI timestamp/timecode into a monotonic playback timeline and compensates buffered-audio latency.
- `NDIReceiveTuningProfile`
  - preset policy (`Stable`, `Balanced`, `LowLatency`) for audio buffering + clock smoothing knobs.

## Runtime graph in `NdiVideoReceive`

1. Discover first available NDI source and connect receiver.
2. Create shared `NdiFrameSync` and synchronization lock.
3. Build timeline clock from profile presets.
4. Probe NDI audio format, initialize `NativeAudioEngine`, create `AudioMixer`.
5. Create render engine + `VideoMixer` in audio-led mode.
6. Create `AudioVideoMixer`.
7. Add `NDIAudioStreamSource` and `VideoStreamSource` (using `NDIVideoStreamDecoder`).
8. Add one SDL output to render engine, set active source, start playback.

## Tuning profiles

`NDIReceiveTuningProfile` presets map to:

- `NDIAudioStreamSourceOptions`
  - ring capacity multiplier
  - capture high-watermark ratio
  - capture sleep interval
  - capture request size
- `NDIExternalTimelineClockOptions`
  - fallback frame duration
  - latency smoothing factor
  - minimum video forward-advance ratio

CLI accepts optional profile token:

- `stable`
- `balanced` (default)
- `lowlatency`

Unknown tokens are ignored with a warning.

## Diagnostics exposed by `NdiVideoReceive`

- audio: ring fill, underruns, read/capture deltas, source state
- video: decode/present/render fps deltas, dropped frames delta, queue depth
- sync: current frame PTS vs mixer clock drift
- HUD: toggle with `H`

## Run commands

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoReceive/NdiVideoReceive.csproj" -c Release
```

Run with timeout only:

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoReceive/NdiVideoReceive.csproj" -c Release -- 15
```

Run with profile:

```fish
dotnet run --project "/home/seko/RiderProjects/MFPlayer/Test/NdiVideoReceive/NdiVideoReceive.csproj" -c Release -- 15 lowlatency
```

## Multiplexing note

`VideoMixer` now uses one attached render engine. To fan out one decoded stream to multiple outputs, use:

- `BroadcastVideoEngine` as the mixer render engine.

For audio, `BroadcastAudioEngine` provides the equivalent `IAudioEngine` fan-out wrapper.

For outbound sender setup, see `Doc/ndi-send.md`.

## Strict-format note

- `NDIVideoStreamDecoder` now forwards native NDI pixel formats into `VideoFrame`.
- Unsupported `FourCC` values are rejected (no implicit conversion in the NDI adapter).
- Use `VideoTranscodeEngine` when you explicitly need format conversion between NDI and render/output paths.

