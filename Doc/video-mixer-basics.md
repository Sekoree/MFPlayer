# VideoMixer Basics (Video-Only)

Use `VideoMixer` when you only need video transport/routing and no audio-driven master clock.

## Main pieces

- `FFVideoDecoder`: decodes compressed video stream.
- `VideoStreamSource`: clock-aware video source and queueing.
- internal playback engine (managed by `VideoMixer`).
- `VideoMixer`: source registration and active-source selection.
- `IVideoOutput`: output sink (`VideoSDL`, `VideoGL`, etc.).

## Minimal pipeline

```csharp
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Sources;

var inputFile = "/path/to/video.mov";

var decoderOptions = new FFVideoDecoderOptions
{
    EnableHardwareDecoding = true,
    UseDedicatedDecodeThread = true,
    QueueCapacity = 30
};

using var decoder = new FFVideoDecoder(inputFile, decoderOptions);
using var videoSource = new VideoStreamSource(decoder, ownsDecoder: false);

var transportConfig = new VideoEngineConfig
{
    ClockSyncMode = VideoClockSyncMode.VideoOnly,
    PresentationSyncMode = VideoPresentationSyncMode.PreferVSync
}.CloneNormalized();

using var renderEngine = new OpenGLVideoEngine();
using var videoMixer = new VideoMixer(renderEngine, config: transportConfig);

if (!videoMixer.AddSource(videoSource))
    throw new InvalidOperationException("Failed to add video source.");

IVideoOutput output = CreateOutputSomehow();
if (!renderEngine.AddOutput(output))
    throw new InvalidOperationException("Failed to add video output.");

if (renderEngine is ISupportsOutputSwitching switching && !switching.SetVideoOutput(output))
    throw new InvalidOperationException("Failed to select video output.");

if (!videoMixer.SetActiveSource(videoSource))
    throw new InvalidOperationException("Failed to set active source.");

videoMixer.Start();

// ... playback loop / UI lifetime ...

videoMixer.Pause();
videoMixer.Seek(5.0);
videoMixer.Start();

// On shutdown
videoMixer.RemoveSource(videoSource);
```

`VideoMixer` uses an attached render engine. For fan-out to multiple real outputs, attach a broadcast engine.

## Multi-output fan-out with a broadcast engine

```csharp
using Seko.OwnAudioNET.Video.Engine;

var outputA = CreateOutputA();
var outputB = CreateOutputB();

var renderEngine = new BroadcastVideoEngine();
renderEngine.AddOutput(outputA);
renderEngine.AddOutput(outputB);

using var videoMixer = new VideoMixer(renderEngine, config: transportConfig);

if (!videoMixer.SetActiveSource(videoSource))
    throw new InvalidOperationException("Failed to set active source.");
```

## Notes

- `VideoStreamSource.StartOffset` shifts source position on the timeline.
- Use `videoMixer.Seek(seconds, safeSeek: true)` when you want pause/resume-safe seek behavior from the transport layer.
- For multi-view rendering, use one mixer-bound primary output and mirror in UI/output layer where supported.

## Alternative no-audio sample: direct decoder -> output engine

If you do not need transport routing (or seek orchestration), this is the smallest video-only path:

```csharp
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.SDL3;

using var decoder = new FFVideoDecoder("/path/to/video.mov", new FFVideoDecoderOptions
{
    EnableHardwareDecoding = true
});

using var output = new VideoSDL
{
    KeepAspectRatio = true,
    EnableHudOverlay = false
};

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

If you need source routing, pause/resume/seek semantics, and output binding, use the `VideoMixer` pipeline from the main section above.

## Alternative no-audio sample: direct decoder -> Avalonia `VideoGL`

If you already have an Avalonia app, you can push decoded frames directly to a `VideoGL` control.

```csharp
using Seko.OwnAudioNET.Video.Avalonia;
using Seko.OwnAudioNET.Video.Decoders;

using var decoder = new FFVideoDecoder("/path/to/video.mov", new FFVideoDecoderOptions
{
    EnableHardwareDecoding = true
});

var videoView = new VideoGL
{
    KeepAspectRatio = true,
    EnableHudOverlay = true,
    PresentationSyncMode = VideoPresentationSyncMode.PreferVSync
};

// In Avalonia, place this control in your UI, for example:
// VideoHost.Content = videoView;

while (decoder.TryDecodeNextFrame(out var frame, out var error))
{
    try
    {
        // Push directly to the control. Use frame PTS as the timestamp hint.
        videoView.PushFrame(frame, frame.PtsSeconds);
    }
    finally
    {
        frame.Dispose();
    }
}

if (!string.IsNullOrWhiteSpace(error))
    Console.WriteLine($"Decode stopped: {error}");
```

Optional HUD updates for live diagnostics:

```csharp
videoView.UpdateFormatInfo(
    sourcePixelFormat: "yuv422p10le",
    outputPixelFormat: "yuv422p10le",
    videoFps: 60);

videoView.UpdateHudDiagnostics(
    queueDepth: 12,
    uploadMsPerFrame: 0,
    avDriftMs: -4.5,
    isHardwareDecoding: true,
    droppedFrames: 0);
```

This is the smallest Avalonia path. If you need timeline control (`Start`/`Pause`/`Seek`) and source/output routing, use the `VideoMixer` approach from the main section.

## Quick compare: SDL3 vs Avalonia direct path

| Topic | SDL3 direct path (`VideoSDL` + `VideoEngine`) | Avalonia direct path (`VideoGL.PushFrame`) |
| --- | --- | --- |
| Best use case | Minimal standalone debug player | Integrating video into an existing Avalonia UI |
| Window lifecycle | Managed directly by `VideoSDL.Initialize/Start/Stop` | Managed by Avalonia app/window lifecycle |
| Rendering control | Immediate output sink model | Control-based model (push frames to UI control) |
| Multi-view support | Usually one output per window | Easy mirror/view composition in Avalonia layouts |
| Input handling | SDL key callbacks/events | Avalonia key/input events |
| Typical complexity | Lower for quick smoke/debug tools | Lower for desktop UI apps already using Avalonia |

## Which path should I choose?

- Pick **direct decoder -> output engine** when:
  - you want the smallest possible path from decode to pixels,
  - you do not need source routing, shared timelines, or coordinated seek behavior.
- Pick **video-only `VideoMixer`** when:
  - you need start/pause/seek orchestration,
  - you want output/source binding and a cleaner path toward later A/V sync integration.

## Common pitfalls and fixes

- Black output window:
  - verify decoder is actually producing frames (`TryDecodeNextFrame` loop entered).
  - ensure output was started (`VideoSDL.Start()` for SDL path).
- Choppy playback in direct push mode:
  - direct push has no shared transport pacing by default; consider the `VideoMixer` path for timeline-driven pacing.
- No hardware decode engaged:
  - set `EnableHardwareDecoding = true` and verify platform/device supports codec + pixel format.
- Format mismatch assumptions:
  - do not assume output pixel format equals source format; inspect stream/output diagnostics in your app.

