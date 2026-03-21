# Video Layer Interop With OwnAudio

This page explains how the video classes fit into the existing OwnAudio architecture.

## Concept mapping

- OwnAudio `MasterClock`
  - shared timeline authority for synchronized playback.
- `MasterClockVideoClockAdapter`
  - adapts OwnAudio `MasterClock` to `IVideoClock` for video transport/source APIs.
- OwnAudio `AudioMixer`
  - hosts `IAudioSource` instances and drives the master clock.
- `AudioStreamSource`
  - OwnAudio `BaseAudioSource` implementation backed by FFmpeg decode.
- `VideoStreamSource`
  - video-side clock-aware source backed by FFmpeg decode queue.
- `VideoMixer`
  - registers video sources, selects one active source, and controls shared video playback timing.
- `AudioVideoMixer`
  - combines `AudioMixer` and `VideoMixer` into one A/V-facing API.

## Typical integration pattern

1. Create/start OwnAudio engine.
2. Create `AudioMixer`.
3. Configure `VideoEngineConfig` with `ClockSyncMode = AudioLed`.
4. Create a render engine (for example `OpenGLVideoEngine`).
5. Create `VideoMixer` with render engine + optional audio clock adapter.
6. Create `AudioVideoMixer` to manage both sides together.
7. Add `AudioStreamSource` and `VideoStreamSource`, then call `Start()`.

For output fan-out, attach a `BroadcastVideoEngine` as the mixer render engine.

## Minimal integration skeleton

```csharp
var audioMixer = new AudioMixer(audioEngine, bufferSize);
var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
var renderEngine = new OpenGLVideoEngine();
var videoMixer = new VideoMixer(renderEngine, videoClock, new VideoEngineConfig
{
    ClockSyncMode = VideoClockSyncMode.AudioLed
});

var avMixer = new AudioVideoMixer(audioMixer, videoMixer);
```

## Fan-out wrappers (audio/video)

- `BroadcastAudioEngine`
  - wraps multiple `IAudioEngine` instances so one `AudioMixer` send path can target many engines.
- `BroadcastVideoEngine`
  - fans out frames to multiple video outputs and/or child video output engines.

For end-to-end fan-out wiring examples, see `Doc/multiplexers.md`.

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

if (!renderEngine.AddOutput(sdlOutput))
    throw new InvalidOperationException("Failed to add SDL output.");
if (renderEngine is ISupportsOutputSwitching switching && !switching.SetVideoOutput(sdlOutput))
    throw new InvalidOperationException("Failed to select SDL output.");
if (!avMixer.SetActiveVideoSource(videoSource))
    throw new InvalidOperationException("Failed to set active video source.");

avMixer.Start();

// ... run app loop ...

sdlOutput.Stop();
sdlOutput.Dispose();
```

### Avalonia output (`VideoGL`) with mirrors

```csharp
using Seko.OwnAudioNET.Video.Avalonia;

var primaryView = new VideoGL
{
    KeepAspectRatio = true,
    PresentationSyncMode = VideoPresentationSyncMode.PreferVSync
};

if (!renderEngine.AddOutput(primaryView))
    throw new InvalidOperationException("Failed to add primary VideoGL output.");
if (renderEngine is ISupportsOutputSwitching switching && !switching.SetVideoOutput(primaryView))
    throw new InvalidOperationException("Failed to select primary VideoGL output.");
if (!avMixer.SetActiveVideoSource(videoSource))
    throw new InvalidOperationException("Failed to set active video source.");

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

### 1) Direct decode loop: `FFVideoDecoder` -> `OpenGLVideoEngine`

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

using var engine = new OpenGLVideoEngine();
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

### 2) Video-only transport: `VideoStreamSource` + `VideoMixer`

Use this when you want seek/pause/start behavior and source/output routing, but still no audio.

```csharp
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.SDL3;
using Seko.OwnAudioNET.Video.Sources;

using var decoder = new FFVideoDecoder("/path/to/video.mov", new FFVideoDecoderOptions());
using var source = new VideoStreamSource(decoder, ownsDecoder: false);

var config = new VideoEngineConfig
{
    ClockSyncMode = VideoClockSyncMode.VideoOnly,
    PresentationSyncMode = VideoPresentationSyncMode.PreferVSync
}.CloneNormalized();

using var render = new OpenGLVideoEngine();
using var mixer = new VideoMixer(render, config: config);
using var output = new VideoSDL { KeepAspectRatio = true, EnableHudOverlay = false };

if (!output.Initialize(1280, 720, "VideoOnly", out var glError))
    throw new InvalidOperationException($"VideoSDL init failed: {glError}");

output.Start();

if (!mixer.AddSource(source))
    throw new InvalidOperationException("Failed to add source.");
if (!render.AddOutput(output))
    throw new InvalidOperationException("Failed to add output.");
if (render is ISupportsOutputSwitching switching && !switching.SetVideoOutput(output))
    throw new InvalidOperationException("Failed to select output.");
if (!mixer.SetActiveSource(source))
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
- `AudioStreamSource.AttachToClock(audioMixer.MasterClock)` is required in audio-led flows.
- `VideoStreamSource.StartOffset` can intentionally delay/advance a source on the shared timeline.
- `AudioVideoMixer` drift correction performs micro timeline offset adjustments and hard-resync fallback for large drift.

## Error/event surface

- OwnAudio side:
  - `AudioMixer.SourceError`
- Video side:
  - `VideoMixer.SourceError`
  - per-source `VideoStreamSource.Error`
- Combined in `AudioVideoMixer`:
  - `AudioSourceError`
  - `VideoSourceError`
  - `ActiveVideoSourceChanged`

## Reference implementations in this workspace

- `Test/AudioEx/Program.cs`
- `Test/VideoTest/MainWindow.axaml.cs`

