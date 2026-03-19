# Video Layer Interop With OwnAudio

This page explains how the video classes fit into the existing OwnAudio architecture.

## Concept mapping

- OwnAudio `MasterClock`
  - shared timeline authority for synchronized playback.
- `MasterClockVideoClockAdapter`
  - adapts OwnAudio `MasterClock` to `IVideoClock` for video transport/source APIs.
- OwnAudio `AudioMixer`
  - hosts `IAudioSource` instances and drives the master clock.
- `FFAudioSource`
  - OwnAudio `BaseAudioSource` implementation backed by FFmpeg decode.
- `FFVideoSource`
  - video-side clock-aware source backed by FFmpeg decode queue.
- `VideoMixer`
  - registers video sources/outputs and controls shared video transport.
- `AudioVideoMixer`
  - combines `AudioMixer` and `VideoMixer` into one A/V-facing API.

## Typical integration pattern

1. Create/start OwnAudio engine.
2. Create `AudioMixer`.
3. Create `VideoTransportEngine` using `MasterClockVideoClockAdapter(audioMixer.MasterClock)`.
4. Configure `ClockSyncMode = AudioLed`.
5. Create `VideoMixer` around that transport.
6. Create `AudioVideoMixer` to manage both sides together.
7. Add `FFAudioSource` and `FFVideoSource`, then call `Start()`.

## Minimal integration skeleton

```csharp
var audioMixer = new AudioMixer(audioEngine, bufferSize);
var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
var videoTransport = new VideoTransportEngine(videoClock, new VideoTransportEngineConfig
{
    ClockSyncMode = VideoTransportClockSyncMode.AudioLed
}, ownsClock: false);
var videoMixer = new VideoMixer(videoTransport, ownsEngine: true);

var avMixer = new AudioVideoMixer(audioMixer, videoMixer);
```

## Tiny output snippets

These snippets assume you already created and started an `AudioVideoMixer` pipeline (`avMixer`), and already added a `videoSource`.

### SDL3 output (`VideoSDL`)

```csharp
using Seko.OwnAudioNET.Video.SDL3;

var sdlOutput = new VideoSDL
{
    KeepAspectRatio = true,
    EnableHudOverlay = false
};

if (!sdlOutput.Initialize(1280, 720, "MFPlayer", out var glError))
    throw new InvalidOperationException($"VideoSDL init failed: {glError}");

sdlOutput.Start();

if (!avMixer.AddVideoOutput(sdlOutput))
    throw new InvalidOperationException("Failed to add SDL output.");
if (!avMixer.BindVideoOutputToSource(sdlOutput, videoSource))
    throw new InvalidOperationException("Failed to bind SDL output to video source.");

avMixer.Start();

// ... run app loop ...

avMixer.RemoveVideoOutput(sdlOutput);
sdlOutput.Stop();
sdlOutput.Dispose();
```

### Avalonia output (`VideoGL`) with mirrors

```csharp
using Seko.OwnAudioNET.Video.Avalonia;

var primaryView = new VideoGL
{
    KeepAspectRatio = true,
    PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
};

if (!avMixer.AddVideoOutput(primaryView))
    throw new InvalidOperationException("Failed to add primary VideoGL output.");
if (!avMixer.BindVideoOutputToSource(primaryView, videoSource))
    throw new InvalidOperationException("Failed to bind primary VideoGL output.");

// Optional UI mirrors (same rendered frame fan-out, no extra mixer outputs needed).
var mirror1 = VideoGL.CreateMirror(primaryView);
var mirror2 = VideoGL.CreateMirror(primaryView);

// In Avalonia, assign these to controls:
// VideoControl.Content = primaryView;
// VideoControl2.Content = mirror1;
// VideoControl3.Content = mirror2;

avMixer.Start();
```

## No-audio playback samples

If you do not want audio playback, you can run video-only in two common ways.

### 1) Direct decode loop: `FFVideoDecoder` -> `VideoEngine`

Use this for the most minimal push model (no transport/mixer timeline).

```csharp
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.SDL3;

using var decoder = new FFVideoDecoder("/path/to/video.mov", new FFVideoDecoderOptions
{
    EnableHardwareDecoding = true
});

using var output = new VideoSDL { KeepAspectRatio = true, EnableHudOverlay = false };
if (!output.Initialize(1280, 720, "VideoOnly", out var glError))
    throw new InvalidOperationException($"VideoSDL init failed: {glError}");

output.Start();

using var engine = new VideoEngine();
if (!engine.AddOutput(output))
    throw new InvalidOperationException("Failed to add output.");

while (decoder.TryDecodeNextFrame(out var frame, out var error))
{
    try
    {
        // Use frame PTS as timeline hint for output-side diagnostics/presentation.
        engine.PushFrame(frame, frame.PtsSeconds);
    }
    finally
    {
        frame.Dispose();
    }
}

if (!string.IsNullOrWhiteSpace(error))
    Console.WriteLine($"Decode stopped: {error}");

output.Stop();
```

### 2) Video-only transport: `FFVideoSource` + `VideoMixer`

Use this when you want seek/pause/start behavior and source/output routing, but still no audio.

```csharp
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.SDL3;
using Seko.OwnAudioNET.Video.Sources;

using var decoder = new FFVideoDecoder("/path/to/video.mov", new FFVideoDecoderOptions());
using var source = new FFVideoSource(decoder, ownsDecoder: false);

var config = new VideoTransportEngineConfig
{
    ClockSyncMode = VideoTransportClockSyncMode.VideoOnly,
    PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
}.CloneNormalized();

using var transport = new VideoTransportEngine(config);
using var mixer = new VideoMixer(transport, ownsEngine: false);
using var output = new VideoSDL { KeepAspectRatio = true, EnableHudOverlay = false };

if (!output.Initialize(1280, 720, "VideoOnly", out var glError))
    throw new InvalidOperationException($"VideoSDL init failed: {glError}");

output.Start();

if (!mixer.AddSource(source) || !mixer.AddOutput(output) || !mixer.BindOutputToSource(output, source))
    throw new InvalidOperationException("Failed to wire video-only mixer pipeline.");

mixer.Start();

// ... app loop ...

mixer.Pause();
mixer.Seek(5.0);
mixer.Start();
```

For a longer walkthrough of this second approach, see `Doc/video-mixer-basics.md`.

## Synchronization notes

- Keep audio as timeline authority for media with meaningful audio track.
- `FFAudioSource.AttachToClock(audioMixer.MasterClock)` is required in audio-led flows.
- `FFVideoSource.StartOffset` can intentionally delay/advance a source on the shared timeline.
- `AudioVideoMixer` drift correction performs micro timeline offset adjustments and hard-resync fallback for large drift.

## Error/event surface

- OwnAudio side:
  - `AudioMixer.SourceError`
- Video side:
  - `VideoMixer.SourceError`
  - per-source `FFVideoSource.Error`
- Combined in `AudioVideoMixer`:
  - `AudioSourceError`
  - `VideoSourceError`
  - `VideoOutputSourceChanged`

## Reference implementations in this workspace

- `Test/AudioEx/Program.cs`
- `Test/VideoTest/MainWindow.axaml.cs`

