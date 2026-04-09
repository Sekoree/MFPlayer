# MFPlayer — Video Playback Architecture

> **Status:** Design finalised / Ready for implementation  
> **Last updated:** 2026-04-09 (V4–V5, V7 resolved; IVideoMixer → IVideoMixer; PTS clock; NDI clarified)  
> **Prerequisite reading:** `MediaPipelineArchitecture.md` (audio pipeline, existing types)

---

## 1. Goals & Non-Goals

### Goals

- Add video playback to the framework **without touching or coupling to the audio pipeline**.
- Mirror the audio architecture (interfaces, naming, patterns) where it makes sense.
- SDL3 + OpenGL as the concrete video output/display backend.
- Input pixel formats converted to **BGRA32** before GPU upload — no custom shaders beyond a
  minimal passthrough vertex+fragment pair.
- Reuse the existing `VideoFrame`, `VideoFormat`, `PixelFormat` types from `S.Media.Core`.
- Reuse the existing `FFmpegVideoChannel` (already decodes to `VideoFrame`) as the primary source.

### Non-Goals (for this first iteration)

- A/V sync (audio and video are fully independent pipelines for now).
- Video mixing / compositing / overlays / transitions.
- Fancy shaders, colour-space transforms on GPU, HDR tone-mapping.
- Video sinks / fan-out (the `IVideoSink` interface is shaped but not implemented).
- Video recording / encoding.
- Fullscreen / multi-monitor / resize handling beyond basic window creation.

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  Application / Decoder Layer                                     │
│  FFmpegDecoder → FFmpegVideoChannel (already exists)            │
│  NdiSource     → NdiVideoChannel    (already exists)            │
├─────────────────────────────────────────────────────────────────┤
│  S.Media.Core/Video/     — IVideoOutput, IVideoChannel,         │
│                             IVideoMixer (interfaces + types) │
├─────────────────────────────────────────────────────────────────┤
│  S.Media.SDL3            — SDL3VideoOutput (SDL3 window + GL)   │
│                             SDL3VideoMixer                   │
├─────────────────────────────────────────────────────────────────┤
│  SDL3-CS / SDL3-CS.Native — P/Invoke wrapper (already in deps)  │
└─────────────────────────────────────────────────────────────────┘
```

### Audio ↔ Video analogy map

| Audio concept | Video equivalent | Notes |
|---|---|---|
| `IAudioEngine` | *(none for v1)* | No device enumeration needed; SDL3 creates the window |
| `IAudioOutput : IMediaOutput` | `IVideoOutput : IMediaOutput` | Owns window + GL context + render-loop clock |
| `IAudioMixer` | `IVideoMixer` | Much simpler: presents one channel at a time (no mixing/compositing in v1) |
| `IAudioChannel : IMediaChannel<float>` | `IVideoChannel : IMediaChannel<VideoFrame>` | Thin sub-interface adding `VideoFormat` + `Position` |
| `IAudioSink` | `IVideoSink` *(interface only, no impl in v1)* | Shaped for future NDI send, recording, etc. |
| `IAudioResampler` | `IPixelFormatConverter` | CPU-side pixel format conversion (any → BGRA32) |
| `AudioFormat` | `VideoFormat` | Already exists |
| `float` (PCM sample) | `VideoFrame` | Already exists |
| `ChannelRouteMap` | *(none)* | No channel routing for video |
| `AggregateOutput` | *(not in v1)* | No fan-out for video yet |

---

## 3. Project Map (new projects)

| Project | Location | Role |
|---|---|---|
| `S.Media.Core` (existing) | `Media/S.Media.Core/` | New files in `Video/` subfolder: interfaces + base types |
| `S.Media.SDL3` (new) | `Video/S.Media.SDL3/` | Concrete SDL3+OpenGL video output |
| `S.Media.FFmpeg` (existing) | `Media/S.Media.FFmpeg/` | `FFmpegVideoChannel` already exists — no changes needed |

---

## 4. S.Media.Core — Video Interfaces

All new files go in `S.Media.Core/Video/`.

### 4.1 `IVideoChannel` — `Video/IVideoChannel.cs`

Thin sub-interface over the existing `IMediaChannel<VideoFrame>`:

```csharp
namespace S.Media.Core.Video;

/// <summary>
/// A single video source that feeds into an <see cref="IVideoMixer"/>.
/// Analogous to <see cref="Audio.IAudioChannel"/> but for video frames.
/// </summary>
public interface IVideoChannel : IMediaChannel<VideoFrame>
{
    /// <summary>The native format of this video source.</summary>
    VideoFormat SourceFormat { get; }

    /// <summary>Current playback position (derived from the last presented frame's PTS).</summary>
    TimeSpan Position { get; }
}
```

**Why a sub-interface?** `FFmpegVideoChannel` already implements `IMediaChannel<VideoFrame>`.
Adding `IVideoChannel` is a compatible extension — `FFmpegVideoChannel` just needs to also
expose `SourceFormat` (it already has `Format`) and `Position`. This mirrors how `IAudioChannel`
extends `IMediaChannel<float>`.

### 4.2 `IVideoOutput` — `Video/IVideoOutput.cs`

```csharp
namespace S.Media.Core.Video;

/// <summary>
/// A video output display surface. Owns a window/render context and a clock.
/// Analogous to <see cref="Audio.IAudioOutput"/> for the video pipeline.
/// </summary>
public interface IVideoOutput : IMediaOutput
{
    /// <summary>Format describing the current output surface (resolution, pixel format, frame rate).</summary>
    VideoFormat OutputFormat { get; }

    /// <summary>The video mixer that manages channels and drives frame presentation.</summary>
    IVideoMixer Mixer { get; }

    /// <summary>
    /// Opens the output surface (creates a window / render context).
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="format">Requested output format (pixel format, frame rate hint).</param>
    void Open(string title, int width, int height, VideoFormat format);
}
```

### 4.3 `IVideoMixer` — `Video/IVideoMixer.cs`

The video equivalent of `IAudioMixer`, but much simpler for v1 — no compositing.

```csharp
namespace S.Media.Core.Video;

/// <summary>
/// Manages video channels and presents the active channel's frames to the output.
/// In v1 this is single-channel (no compositing / layering).
/// Analogous to <see cref="Audio.IAudioMixer"/> in structure.
/// </summary>
public interface IVideoMixer : IDisposable
{
    /// <summary>The format of the output surface.</summary>
    VideoFormat OutputFormat { get; }

    /// <summary>Number of channels currently registered.</summary>
    int ChannelCount { get; }

    /// <summary>
    /// The channel currently being presented. Null if no channel is active.
    /// Only one channel is rendered at a time in v1.
    /// </summary>
    IVideoChannel? ActiveChannel { get; }

    /// <summary>Registers a video channel.</summary>
    void AddChannel(IVideoChannel channel);

    /// <summary>Removes a previously registered channel by its Id.</summary>
    void RemoveChannel(Guid channelId);

    /// <summary>
    /// Sets which registered channel is actively being rendered.
    /// Pass null to show a blank/black frame.
    /// </summary>
    void SetActiveChannel(Guid? channelId);

    /// <summary>
    /// Called by the render loop to pull the next frame from the active channel
    /// and present it. Returns the frame that was presented, or null if no frame
    /// was available.
    /// </summary>
    VideoFrame? PresentNextFrame();
}
```

### 4.4 `IPixelFormatConverter` — `Video/IPixelFormatConverter.cs`

```csharp
namespace S.Media.Core.Video;

/// <summary>
/// Converts video frame pixel data from one format to another.
/// Analogous to <see cref="Audio.IAudioResampler"/> for the video pipeline.
/// </summary>
public interface IPixelFormatConverter : IDisposable
{
    /// <summary>
    /// Converts pixel data from <paramref name="source"/>'s pixel format
    /// to <paramref name="dstFormat"/>.
    /// </summary>
    /// <returns>A new VideoFrame with the converted data (caller disposes MemoryOwner).</returns>
    VideoFrame Convert(VideoFrame source, PixelFormat dstFormat);
}
```

Two implementations planned:

| Class | Location | Notes |
|---|---|---|
| `SwsPixelConverter` | `S.Media.FFmpeg` | Wraps `sws_scale`; high quality; stateful (caches `SwsContext`) |
| *(not needed for v1)* | `S.Media.Core` | No built-in fallback converter — FFmpeg already converts at decode time |

**In practice for v1:** `FFmpegVideoChannel` already converts frames to BGRA32 at decode time
via its `TargetPixelFormat` parameter (default `PixelFormat.Bgra32`). The output receives
pre-converted frames — no extra converter needed in the hot path. `IPixelFormatConverter` is
shaped for future use (e.g. when NdiVideoChannel delivers UYVY422 and the output needs BGRA32).

### 4.5 `IVideoSink` — `Video/IVideoSink.cs` *(interface only, no implementation in v1)*

```csharp
namespace S.Media.Core.Video;

/// <summary>
/// A secondary video destination that receives copies of presented frames.
/// Analogous to <see cref="Audio.IAudioSink"/> for the video pipeline.
/// Not implemented in v1 — interface shaped for future NDI send / recording.
/// </summary>
public interface IVideoSink : IDisposable
{
    string Name      { get; }
    bool   IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Receives a presented frame. Implementations must be non-blocking
    /// (copy the data and return immediately).
    /// </summary>
    void ReceiveFrame(in VideoFrame frame);
}
```

---

## 5. S.Media.SDL3 — Concrete Video Output

**Location:** `Video/S.Media.SDL3/`  
**NuGet:** `SDL3-CS` + `SDL3-CS.Native` (already in `Directory.Packages.props`)  
**References:** `S.Media.Core`

### 5.1 Project structure

```
Video/S.Media.SDL3/
    S.Media.SDL3.csproj
    SDL3VideoOutput.cs       — IVideoOutput implementation
    SDL3VideoMixer.cs    — IVideoMixer implementation
    GLRenderer.cs            — OpenGL texture upload + fullscreen quad
```

### 5.2 `S.Media.SDL3.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SDL3-CS" />
    <PackageReference Include="SDL3-CS.Native" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Media\S.Media.Core\S.Media.Core.csproj" />
  </ItemGroup>
</Project>
```

### 5.3 `SDL3VideoOutput` — main lifecycle type

```csharp
/// <summary>
/// SDL3 + OpenGL backed video output.
/// Creates an SDL3 window with an OpenGL context and runs a vsync-driven render loop.
/// Analogous to PortAudioOutput for audio.
/// </summary>
public sealed class SDL3VideoOutput : IVideoOutput
```

**Responsibilities:**
- `Open(title, w, h, format)` → `SDL_Init(SDL_INIT_VIDEO)` → `SDL_CreateWindow` with
  `SDL_WINDOW_OPENGL` flag → `SDL_GL_CreateContext` → create `GLRenderer` → create
  `SDL3VideoMixer`.
- `StartAsync()` → start the render loop thread → set `IsRunning = true`.
- `StopAsync()` → signal render loop to stop → join thread → `IsRunning = false`.
- **Clock:** uses `VideoPtsClock` (PTS-driven; see §11.1). Updated from each presented frame's
  PTS; Stopwatch interpolation between frames. The render loop calls `clock.UpdateFromFrame(pts)`
  after each presented frame and fires `Clock.Tick`.
- `Dispose()` → destroy GL context → `SDL_DestroyWindow` → `SDL_Quit`.

### 5.4 Render loop

```
Render loop (dedicated thread, normal priority):
    while (!cancelled):
        SDL_PollEvent()           // drain events (close, resize, key)
        frame = mixer.PresentNextFrame()
        if frame.HasValue:
            glRenderer.UploadAndDraw(frame.Value)
            clock.UpdateFromFrame(frame.Value.Pts)  // PTS-driven clock
            frame.Value.MemoryOwner?.Dispose()       // return ArrayPool rental
        else:
            glRenderer.DrawBlack()               // no frame available — show black
        SDL_GL_SwapWindow()                       // vsync
        clock.FireTick()
```

**Key design points:**
- The render loop runs on a **dedicated thread** (like the PortAudio RT callback, but with
  relaxed constraints — allocations are acceptable on the render thread).
- Frame pacing is controlled by **vsync** (`SDL_GL_SetSwapInterval(1)`). No manual timer
  needed in v1.
- The mixer pulls one frame per loop iteration from the active channel's ring buffer.
  If no frame is ready, the previous frame stays on screen (no flicker).
- `VideoFrame.MemoryOwner` is disposed after GPU upload to return the `ArrayPool<byte>` rental.

### 5.5 `SDL3VideoMixer`

```csharp
/// <summary>
/// Manages video channels and pulls frames from the active one.
/// Single-channel presentation (no compositing in v1).
/// </summary>
public sealed class SDL3VideoMixer : IVideoMixer
```

**Internal state:**
- `List<IVideoChannel> _channels` — registered channels.
- `IVideoChannel? _activeChannel` — the channel currently being presented.
- `VideoFrame? _lastFrame` — the most recent frame (held for re-display if no new frame arrives).

**`PresentNextFrame()` logic:**
```
1. If _activeChannel is null → return null
2. Call _activeChannel.FillBuffer(dest, 1)
3. If got 1 frame → dispose _lastFrame.MemoryOwner, store new frame as _lastFrame, return it
4. If got 0 frames → return _lastFrame (repeat last frame — hold, no flicker)
```

### 5.6 `GLRenderer` — OpenGL plumbing

```csharp
/// <summary>
/// Minimal OpenGL renderer: uploads a BGRA32 texture and draws a fullscreen quad.
/// No custom shaders beyond a trivial passthrough pair.
/// </summary>
internal sealed class GLRenderer : IDisposable
```

**GL setup (on Open):**
1. `glGenTextures(1, &_texture)` — single 2D texture.
2. `glTexParameteri` — `GL_NEAREST` filtering (no interpolation for now), `GL_CLAMP_TO_EDGE`.
3. Create a minimal **passthrough shader program**:
   - **Vertex shader:** transforms a fullscreen triangle/quad from NDC coordinates.
   - **Fragment shader:** samples the texture and outputs the colour as-is.
4. Create a fullscreen quad VAO (two triangles covering `[-1,1]×[-1,1]` in NDC, with
   UV `[0,1]×[0,1]`).
5. `glViewport(0, 0, w, h)`.

**Per-frame `UploadAndDraw(VideoFrame frame)`:**
1. `glBindTexture(GL_TEXTURE_2D, _texture)`.
2. `glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_BGRA, GL_UNSIGNED_BYTE, dataPtr)`
   — uploads BGRA32 pixel data.
   - On subsequent frames with the same resolution: use `glTexSubImage2D` to avoid
     re-allocating the GPU texture (fast path).
3. `glUseProgram(_program)`.
4. `glBindVertexArray(_quadVao)` → `glDrawArrays(GL_TRIANGLES, 0, 6)`.

**"No special shaders" interpretation:** The vertex + fragment shaders are the absolute minimum
needed for modern OpenGL (core profile). There is no colour-space conversion, no tone-mapping,
no effects — just `texture(tex, uv)` → `fragColor`.

**Passthrough shaders (embedded as string constants):**

```glsl
// Vertex
#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;
void main() {
    gl_Position = vec4(aPos, 0.0, 1.0);
    vUV = aUV;
}

// Fragment
#version 330 core
in vec2 vUV;
out vec4 fragColor;
uniform sampler2D uTexture;
void main() {
    fragColor = texture(uTexture, vUV);
}
```

### 5.7 GL function loading

SDL3 provides `SDL_GL_GetProcAddress` for loading OpenGL function pointers. `GLRenderer` will
use this to load the ~20 GL functions needed (no dependency on a large GL binding library):

- `glGenTextures`, `glDeleteTextures`, `glBindTexture`, `glTexImage2D`, `glTexSubImage2D`,
  `glTexParameteri`
- `glCreateShader`, `glShaderSource`, `glCompileShader`, `glCreateProgram`, `glAttachShader`,
  `glLinkProgram`, `glUseProgram`, `glDeleteShader`, `glDeleteProgram`, `glGetUniformLocation`,
  `glUniform1i`
- `glGenVertexArrays`, `glDeleteVertexArrays`, `glBindVertexArray`, `glGenBuffers`,
  `glDeleteBuffers`, `glBindBuffer`, `glBufferData`, `glEnableVertexAttribArray`,
  `glVertexAttribPointer`, `glDrawArrays`
- `glViewport`, `glClear`, `glClearColor`

These can be loaded once at `Open()` time into `delegate*` fields (or regular delegates).

**Alternative:** If the manual loading becomes unwieldy, use a lightweight generated GL
binding (e.g. `Silk.NET.OpenGL` or a small hand-rolled generator). But for ~30 functions
it's manageable with raw `SDL_GL_GetProcAddress`.

---

## 6. Threading Model

| Thread | Owner | Constraint |
|---|---|---|
| Render loop | `SDL3VideoOutput` | Dedicated thread; owns GL context; vsync-paced; allocations OK |
| Decoder thread(s) | `FFmpegVideoChannel` | Background; fills `BoundedChannel<VideoFrame>` ring |
| App / UI thread | Caller | Calls `AddChannel`, `SetActiveChannel`, `Open`, `Start` etc. |

Video has **no RT constraint** like audio. The render thread may allocate, lock, and block
within reason. The only performance-sensitive operation is the `glTexImage2D` upload — this
is bounded by GPU transfer bandwidth and typically completes in <1 ms for 1080p BGRA32.

---

## 7. Frame Lifecycle & Memory Management

```
FFmpegVideoChannel.DecodeLoop():
  ├─ Decode AVFrame
  ├─ sws_scale → BGRA32 into ArrayPool<byte> rental
  ├─ Wrap as VideoFrame { Data, MemoryOwner = ArrayPoolOwner }
  └─ Write to BoundedChannel<VideoFrame> (back-pressure)

SDL3VideoMixer.PresentNextFrame():
  ├─ activeChannel.FillBuffer(dest, 1) — pulls from BoundedChannel
  ├─ If got frame: dispose previous _lastFrame.MemoryOwner
  ├─ Store as _lastFrame
  └─ Return frame to render loop

GLRenderer.UploadAndDraw(frame):
  ├─ Pin frame.Data → glTexImage2D (GPU upload)
  └─ Draw fullscreen quad

Render loop (after upload):
  └─ frame.MemoryOwner?.Dispose() — returns byte[] to ArrayPool
```

**Key invariant:** Each `VideoFrame`'s `MemoryOwner` is disposed exactly once, by the render
loop, after the GPU upload is complete. The mixer holds a reference to the *last* frame
for re-display but only disposes it when a *new* frame replaces it.

---

## 8. FFmpegVideoChannel Modifications

Minimal changes to the existing `FFmpegVideoChannel`:

| Change | Details |
|---|---|
| Implement `IVideoChannel` | Add `: IVideoChannel` to class declaration. `SourceFormat` is already exposed as `Format`. Add `Position` property (tracked from last decoded frame PTS). |
| *(Optional)* expose `BufferAvailable` | For diagnostics — count of frames in the ring buffer. Not strictly required for v1. |

`FFmpegVideoChannel` already:
- Decodes to `VideoFrame` with `TargetPixelFormat = Bgra32` (default).
- Uses `BoundedChannel<VideoFrame>` as a ring buffer.
- Returns `ArrayPool<byte>` rentals via `VideoFrame.MemoryOwner`.
- Implements `IMediaChannel<VideoFrame>.FillBuffer` (non-blocking pull).

No changes to `FFmpegDecoder` are needed.

---

## 9. Implementation Plan — Step by Step

### Phase 1: Core interfaces (`S.Media.Core/Video/`)

| # | File | Type | Effort |
|---|---|---|---|
| 1.1 | `Video/IVideoChannel.cs` | Interface | Small |
| 1.2 | `Video/IVideoOutput.cs` | Interface | Small |
| 1.3 | `Video/IVideoMixer.cs` | Interface | Small |
| 1.4 | `Video/IPixelFormatConverter.cs` | Interface | Small |
| 1.5 | `Video/IVideoSink.cs` | Interface (no impl) | Small |

### Phase 2: SDL3 project scaffolding

| # | Task | Effort |
|---|---|---|
| 2.1 | Create `Video/S.Media.SDL3/S.Media.SDL3.csproj` | Small |
| 2.2 | Add project to `MFPlayer.sln` (under `Video` solution folder) | Small |

### Phase 3: GLRenderer

| # | Task | Effort |
|---|---|---|
| 3.1 | `GLRenderer` — GL function loading via `SDL_GL_GetProcAddress` | Medium |
| 3.2 | `GLRenderer` — shader compilation + fullscreen quad VAO setup | Medium |
| 3.3 | `GLRenderer` — `UploadAndDraw(VideoFrame)` + `DrawBlack()` | Small |
| 3.4 | `GLRenderer.Dispose()` — cleanup GL resources | Small |

### Phase 4: SDL3VideoMixer

| # | Task | Effort |
|---|---|---|
| 4.1 | Channel registration (`AddChannel`, `RemoveChannel`) | Small |
| 4.2 | `SetActiveChannel` + `PresentNextFrame` logic | Small |

### Phase 5: SDL3VideoOutput

| # | Task | Effort |
|---|---|---|
| 5.1 | `Open()` — SDL3 init, window creation, GL context, GLRenderer init | Medium |
| 5.2 | `StartAsync()` / `StopAsync()` — render loop thread management | Small |
| 5.3 | Render loop — event pump, present, swap, clock tick | Medium |
| 5.4 | `Dispose()` — tear down in correct order | Small |

### Phase 6: Wire up FFmpegVideoChannel + NdiVideoChannel + VideoPtsClock

| # | Task | Effort |
|---|---|---|
| 6.1 | Add `IVideoChannel` implementation to `FFmpegVideoChannel` | Small |
| 6.2 | Add `Position` tracking to `FFmpegVideoChannel` | Small |
| 6.3 | Add `IVideoChannel` implementation to `NdiVideoChannel` | Small |
| 6.4 | Add `Position` tracking to `NdiVideoChannel` | Small |
| 6.5 | Implement `VideoPtsClock : MediaClockBase` | Small |

### Phase 7: Integration test app

| # | Task | Effort |
|---|---|---|
| 7.1 | Create `Test/MFPlayer.VideoPlayer/` console app | Small |
| 7.2 | Open a video file → FFmpegDecoder → FFmpegVideoChannel → SDL3VideoOutput → display | Medium |

### Phase 8: Unit tests

| # | Task | Effort |
|---|---|---|
| 8.1 | `SDL3VideoMixer` tests (channel add/remove, active channel, frame pull) | Medium |
| 8.2 | `IVideoChannel` adapter tests for `FFmpegVideoChannel` | Small |

---

## 10. Dependency Graph

```
S.Media.Core          (existing — add Video/ interfaces)
    ↑
S.Media.FFmpeg        (existing — FFmpegVideoChannel implements IVideoChannel)
    ↑
S.Media.SDL3          (new — refs S.Media.Core only; NOT S.Media.FFmpeg)
    ↑
MFPlayer.VideoPlayer  (test app — refs S.Media.SDL3 + S.Media.FFmpeg)
```

`S.Media.SDL3` depends only on `S.Media.Core` (interfaces + types) and `SDL3-CS`. It does
**not** reference `S.Media.FFmpeg` — the video channel is injected as `IVideoChannel`.

Audio projects (`S.Media.PortAudio`, `PALib`, `JackLib`) are **completely untouched**.

---

## 11. Resolved Design Questions (V1–V7)

| # | Question | Resolution |
|---|---|---|
| V1 | **Frame pacing beyond vsync** — Should the mixer drop frames if the decoder is faster than vsync, or let the ring buffer back-pressure the decoder? | Let `BoundedChannel` back-pressure naturally. Mixer pulls 1 frame per vsync tick. If the decoder is faster, it blocks on the full ring. If slower, the last frame is re-displayed. This matches the audio model (pull-driven). |
| V2 | **Window resize** — Should the GL viewport auto-resize on window resize events? | Yes, handle `SDL_EVENT_WINDOW_RESIZED` in the event pump and call `glViewport`. Texture resolution stays at the video's native resolution; GL stretches via the fullscreen quad. |
| V3 | **Pixel format at the GL boundary** — BGRA32 vs RGBA32? | Use `GL_BGRA` with `GL_UNSIGNED_BYTE`. BGRA32 is FFmpeg's default conversion target and avoids an extra channel-swap. Most GPUs handle `GL_BGRA` natively. |
| V4 | **Multiple windows** — Should one `SDL3VideoOutput` support multiple windows? | **One output = one window.** For multi-window, create multiple `SDL3VideoOutput` instances. However, `IVideoChannel.FillBuffer` *consumes* frames from the ring buffer, so feeding the same channel to two outputs causes them to race for frames. A future **frame-cloning / fan-out layer** (analogous to `AggregateOutput` for audio) will be needed for true multi-output. For now, a second output requires a second decoder or a manual frame-copy step. |
| V5 | **Clock source** — StopwatchClock vs frame-PTS-driven clock? | **PTS-based clock from the start.** `VideoPtsClock : MediaClockBase` tracks position from each presented frame's PTS. Uses a `Stopwatch` for inter-frame interpolation (same pattern as `NdiClock.UpdateFromFrame`). This keeps the clock accurate to the source material's timeline and future-proofs for A/V sync and NDI clock integration. See §11.1 below. |
| V6 | **GL loading approach** — Manual `SDL_GL_GetProcAddress` vs Silk.NET or similar? | Start with manual loading (~30 functions). If it becomes unwieldy during implementation, switch to a lightweight binding. |
| V7 | **NdiVideoChannel** — Should it also implement `IVideoChannel` in this phase? | **Yes.** Same small adapter as `FFmpegVideoChannel` — add `: IVideoChannel`, expose `SourceFormat` (from existing `Format`) and `Position` (from last frame PTS). Included in Phase 6 of the implementation plan. |

### 11.1 `VideoPtsClock` — PTS-driven video clock

**Location:** `S.Media.Core/Clock/VideoPtsClock.cs`

```csharp
/// <summary>
/// Video clock driven by presented frame PTS values.
/// Uses Stopwatch interpolation between frames (same pattern as NdiClock).
/// </summary>
public sealed class VideoPtsClock : MediaClockBase
```

**Design:**
- `UpdateFromFrame(TimeSpan pts)` — called by the render loop after each presented frame.
  Records the PTS and resets the internal `Stopwatch` for inter-frame interpolation.
- `Position` returns `lastPts + stopwatch.Elapsed` between frames.
- If no frame has been presented yet, position is `TimeSpan.Zero`.
- `Reset()` clears PTS and stopwatch.

This mirrors `NdiClock.UpdateFromFrame(long ndiTimestamp)` and ensures that if NDI is used as a
video source in the future, the clock can either:
1. Be driven by `NdiVideoChannel` frame timestamps directly, or
2. Slave to the existing `NdiClock` (shared with audio) for A/V sync.

### 11.2 `IVideoEngine` — not needed

`IAudioEngine` exists to enumerate **hardware audio devices** (PortAudio's device list). Video
has no equivalent hardware device enumeration need:

- **SDL3:** window creation is direct — no device discovery required.
- **NDI:** source discovery is handled by `NDIFinder` (from `NDILib`), which is already
  separate from `IAudioEngine`. `NdiSource.Open` accepts a discovered source directly.
  There is no need for a video-specific engine interface for NDI.

If a future backend requires device enumeration (e.g. capture cards), an `IVideoEngine` can
be added at that point without breaking the existing design.

---

## 12. Sample Usage (Target API)

```csharp
// Decode
FFmpegLoader.EnsureLoaded();
var decoder = FFmpegDecoder.Open("video.mp4");
var videoChannel = decoder.VideoChannels[0]; // FFmpegVideoChannel : IVideoChannel

// Video output
var videoOutput = new SDL3VideoOutput();
videoOutput.Open("MFPlayer", 1280, 720,
    new VideoFormat(1280, 720, PixelFormat.Bgra32, 30, 1));

// Wire up
videoOutput.Mixer.AddChannel(videoChannel);
videoOutput.Mixer.SetActiveChannel(videoChannel.Id);

// Go
decoder.Start();
await videoOutput.StartAsync();

// ... playback ...

await videoOutput.StopAsync();
decoder.Dispose();
videoOutput.Dispose();
```

---

## 13. File Inventory (all new / modified files)

### New files

| File | Project | Type |
|---|---|---|
| `Media/S.Media.Core/Video/IVideoChannel.cs` | S.Media.Core | Interface |
| `Media/S.Media.Core/Video/IVideoOutput.cs` | S.Media.Core | Interface |
| `Media/S.Media.Core/Video/IVideoMixer.cs` | S.Media.Core | Interface |
| `Media/S.Media.Core/Video/IPixelFormatConverter.cs` | S.Media.Core | Interface |
| `Media/S.Media.Core/Video/IVideoSink.cs` | S.Media.Core | Interface |
| `Media/S.Media.Core/Clock/VideoPtsClock.cs` | S.Media.Core | Class |
| `Video/S.Media.SDL3/S.Media.SDL3.csproj` | S.Media.SDL3 | Project |
| `Video/S.Media.SDL3/SDL3VideoOutput.cs` | S.Media.SDL3 | Class |
| `Video/S.Media.SDL3/SDL3VideoMixer.cs` | S.Media.SDL3 | Class |
| `Video/S.Media.SDL3/GLRenderer.cs` | S.Media.SDL3 | Class |
| `Test/MFPlayer.VideoPlayer/MFPlayer.VideoPlayer.csproj` | Test app | Project |
| `Test/MFPlayer.VideoPlayer/Program.cs` | Test app | Entry point |

### Modified files

| File | Change |
|---|---|
| `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs` | Implement `IVideoChannel`; add `Position` property |
| `NDI/S.Media.NDI/NDIVideoChannel.cs` | Implement `IVideoChannel`; add `SourceFormat` + `Position` properties |
| `MFPlayer.sln` | Add new projects to solution |
| `Directory.Packages.props` | *(no changes — SDL3-CS already listed)* |

