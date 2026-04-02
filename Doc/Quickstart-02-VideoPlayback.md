# Quickstart 02 — Video Playback

> FFmpeg decoder → SDL3 window

This guide shows the minimal code to decode a video file with FFmpeg and render it in an SDL3 OpenGL window.

---

## Prerequisites

| Package | Purpose |
|---------|---------|
| `S.Media.FFmpeg` | FFmpeg-based decoding |
| `S.Media.OpenGL.SDL3` | SDL3/OpenGL video output |
| `S.Media.Core` | Shared types (`VideoFrame`, `MediaResult`, etc.) |

Native libraries **FFmpeg** and **SDL3** must be loadable at runtime.

---

## Full Example

```csharp
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using S.Media.OpenGL.SDL3;
using SDL3;

// 1. Ensure FFmpeg native libraries are loaded.
FFmpegRuntime.EnsureInitialized();

// 2. Open a media file — video only, no audio.
using var media = new FFmpegMediaItem(new FFmpegOpenOptions
{
    InputUri       = "file:///path/to/video.mp4",
    OpenAudio      = false,
    OpenVideo      = true,
    UseSharedDecodeContext = true,
});

// 3. Get the video source and start decoding.
var source = media.VideoSource
    ?? throw new InvalidOperationException("No video stream found.");
source.Start();

// 4. Create and initialize an SDL3 window with OpenGL rendering.
var view = new SDL3VideoView();
view.Initialize(new SDL3VideoViewOptions
{
    Width             = 1280,
    Height            = 720,
    WindowTitle       = "Video Playback",
    WindowFlags       = SDL.WindowFlags.Resizable,
    ShowOnInitialize  = true,
    PreserveAspectRatio = true,
});

// SourceTimestamp mode: the render thread paces frames at the native frame rate.
view.Start(new VideoOutputConfig
{
    PresentationMode                = VideoOutputPresentationMode.SourceTimestamp,
    TimestampMode                   = VideoTimestampMode.RebaseOnDiscontinuity,
    TimestampDiscontinuityThreshold = TimeSpan.FromMilliseconds(50),
    StaleFrameDropThreshold         = TimeSpan.FromMilliseconds(200),
    MaxSchedulingWait               = TimeSpan.FromMilliseconds(33),
});

// 5. Read decoded frames and push them to the view.
while (true)
{
    var readCode = source.ReadFrame(out var frame);
    if (readCode != MediaResult.Success)
        break; // end of stream or error

    using (frame)
    {
        view.PushFrame(frame, frame.PresentationTime);
    }
}

// 6. Clean up.
view.Stop();
source.Stop();
view.Dispose();
```

---

## Key Concepts

### Video-Only Open Options

When you only need video, set `OpenAudio = false` to skip audio stream discovery and decoder allocation. This saves memory and startup time.

This example uses the **throwing constructor** (`new FFmpegMediaItem(...)`) since it's the simplest for video-only. For error-code semantics, use `FFmpegMediaItem.Create(options, out item)` instead.

### Presentation Modes

| Mode | Behaviour |
|------|-----------|
| `SourceTimestamp` | Render thread paces frames using the embedded PTS. Best for file playback at native frame rate. |
| `Unlimited` | Frames are rendered as fast as they arrive. Useful for benchmarking or live sources without timestamps. |

### `VideoFrame` Lifetime

`ReadFrame()` returns a `VideoFrame` that holds pixel data (potentially pool-backed). Always `using (frame)` or call `frame.Dispose()` after pushing. The view takes a copy during `PushFrame()`.

### `PushFrame` Is Non-Blocking

The SDL3 view owns its own render thread. `PushFrame()` enqueues the frame and returns immediately — the read loop is not blocked by vsync or rendering.

---

## See Also

- `Test/SimpleVideoTest/Program.cs` — the working test app this guide is based on
- `Test/TestShared/TestHelpers.cs` — `InitVideoView()` helper used by all test apps

