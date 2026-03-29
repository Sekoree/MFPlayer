# S.Media.OpenGL — Issues & Fix Guide

> **Scope:** `S.Media.OpenGL`, `S.Media.OpenGL.SDL3`, `S.Media.OpenGL.Avalonia`
> **Cross-references:** See `API-Review.md` §§7, 8 for the full analysis.

---

## Table of Contents

1. [OpenGL Engine & Output](#1-opengl-engine--output)
2. [Clone Graph Management](#2-clone-graph-management)
3. [SDL3 (`SDL3VideoView`)](#3-sdl3-sdl3videoview)
4. [Avalonia (`AvaloniaVideoOutput`)](#4-avalonia-avaoniavideoutput)
5. [Common Concerns](#5-common-concerns)

---

## 1. OpenGL Engine & Output

### Issue 1.1 — `OpenGLVideoEngine.AddOutput(IVideoOutput)` silently rejects non-GL outputs

```csharp
public int AddOutput(IVideoOutput output)
{
    if (output is not OpenGLVideoOutput glOutput)
        return (int)MediaErrorCode.MediaInvalidArgument;
    // ...
}
```

Callers get a generic `MediaInvalidArgument` with no indication of why. This violates the Liskov Substitution Principle — accepting `IVideoOutput` implies acceptance of any video output.

**Fix:** Change the parameter type to the concrete type:

```csharp
public int AddOutput(OpenGLVideoOutput output)
{
    ArgumentNullException.ThrowIfNull(output);
    // ...
}
```

**Migration:** Any call site passing a non-`OpenGLVideoOutput` will now get a compile error rather than a silent runtime failure.

**Alternatively**, if the `IVideoOutput` parameter must remain for interface compliance reasons, return a better error:

```csharp
if (output is not OpenGLVideoOutput glOutput)
    return (int)MediaErrorCode.OpenGLInvalidOutputType;  // ADD this error code
```

---

### Issue 1.2 — `VideoOutputPresentationMode.VSync` has no implementation

`VideoOutputConfig.PresentationMode` has `VSync = 3`. No output class handles it. An unimplemented VSync value silently falls through to `Unlimited` behaviour, which can cause tearing.

**Fix option A — Remove until implemented:**

```csharp
public enum VideoOutputPresentationMode
{
    Unlimited = 0,
    Throttled = 1,
    AudioLed  = 2,
    // VSync = 3  -- REMOVED, reserved for future use
}
```

**Fix option B — Implement VSync gating:**

A VSync-gated push waits for the next vertical blank before submitting the frame to the GPU. In OpenGL, this is typically achieved by calling `SwapBuffers` with VSync enabled (via `wglSwapIntervalEXT` / `glXSwapIntervalEXT` / `SDL_GL_SetSwapInterval`). The frame should be uploaded to the texture and the swap triggered in the render thread, blocking the caller until the swap completes.

```csharp
// In OpenGLVideoOutput, when PresentationMode == VSync:
// 1. Upload texture
// 2. Signal render thread to do SwapBuffers with interval=1
// 3. Wait for render thread to confirm swap
// 4. Return
```

This requires render-thread synchronisation. If not implemented in this release, choose option A.

---

### Issue 1.3 — Timeline anchor logic duplicated in `OpenGLVideoOutput`

`OpenGLVideoOutput` has its own `_hasTimelineAnchor`, `_anchorPtsSeconds`, `_anchorTicks`, `_lastNormalizedPtsSeconds`, and `_lastPresentTicks` fields to normalise presentation timestamps. The mixer's `VideoPresenterSyncPolicy` also handles timestamp monotonicity. When both are active, timestamps are normalised twice.

**Fix:** Centralise timestamp normalisation in the mixer. `IVideoOutput.PushFrame(VideoFrame, TimeSpan)` should always receive a normalised, monotonically increasing, clock-relative timestamp from the mixer. `OpenGLVideoOutput` should then remove its own rebase fields entirely:

```csharp
// REMOVE from OpenGLVideoOutput:
// private bool _hasTimelineAnchor;
// private double _anchorPtsSeconds;
// private long _anchorTicks;
// private double _lastNormalizedPtsSeconds;
// private long _lastPresentTicks;

// In PushFrame — receive already-normalised PTS from mixer:
public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
{
    // presentationTime is now guaranteed monotonically increasing by the mixer
    _pendingFrame = (frame, presentationTime);
    // ...
}
```

**Consideration:** This requires the mixer to always call `PushFrame(frame, TimeSpan)` (the two-arg overload) and never `PushFrame(frame)`. Ensure the mixer's `VideoWorker` always provides a valid PTS.

---

## 2. Clone Graph Management

### Issue 2.1 — Clone graph is managed on `OpenGLVideoOutput` itself

`OpenGLVideoOutput` carries `IsClone`, `CloneParentOutputId`, `CloneOutputIds`, and related logic. These are engine-level topology concerns that leak into the output object.

**Fix:** Move the clone graph entirely into `OpenGLVideoEngine`:

```csharp
// Engine holds the graph:
private readonly Dictionary<Guid, List<Guid>> _cloneGraph = new();
// Key: parent output Id → Value: list of clone output Ids

// Remove from OpenGLVideoOutput:
// public bool IsClone { get; }
// public Guid? CloneParentOutputId { get; }
// public IReadOnlyList<Guid> CloneOutputIds { get; }
```

The engine exposes graph queries:

```csharp
public IReadOnlyList<Guid> GetCloneIds(Guid parentId);
public Guid? GetParentId(Guid cloneId);
```

**Consideration:** `SDL3VideoView` and `AvaloniaVideoOutput` both expose `IsClone` and `CloneParentOutputId` as public properties derived from their inner `OpenGLVideoOutput`. After this fix, they should query the engine instead.

---

## 3. SDL3 (`SDL3VideoView`)

### Issue 3.1 — `SDL3VideoView` maintains its own clone dictionary

`SDL3VideoView` has `Dictionary<Guid, SDL3VideoView> _clones` parallel to `OpenGLVideoEngine`'s clone graph. If a `SDL3VideoView` is added to an `OpenGLVideoEngine`, two independent registries can diverge.

**Fix:** Remove `_clones` from `SDL3VideoView`. Delegate all clone management to the `OpenGLVideoEngine` it wraps:

```csharp
public int CreateClone(SDL3CloneOptions options, out SDL3VideoView? clone)
{
    var r = _engine.CreateCloneOutput(Id, ToGlOptions(options), out var glClone);
    if (r != MediaResult.Success || glClone is null) { clone = null; return r; }

    clone = new SDL3VideoView(glClone, _engine, isClone: true);
    return MediaResult.Success;
    // SDL3VideoView no longer adds to its own _clones dict
}
```

---

### Issue 3.2 — `SDL3VideoView` wraps `OpenGLVideoOutput` with a triple-layer dispatch

The `PushFrame` path currently goes through three layers:

```
Caller → SDL3VideoView → internal queue → render thread → OpenGLVideoOutput → OpenGLVideoEngine
```

The internal `OpenGLVideoOutput` is inaccessible for diagnostics, and the triple-layer approach adds unnecessary indirection.

**Recommended fix:** `SDL3VideoView` should directly own the GL context and texture-upload logic. The internal `OpenGLVideoOutput` wrapper should be removed.

```
// Target architecture:
Caller → SDL3VideoView (enqueue frame to SDL render thread)
              └→ SDL render thread: bind SDL window GL context, upload texture, draw, swap
```

`SDL3VideoView` can call `OpenGLVideoEngine` directly for texture management (if shader / texture logic is shared), without going through `OpenGLVideoOutput`.

**Consideration:** This is a significant refactor. An interim fix is to expose the internal `OpenGLVideoOutput` as a public diagnostic property:

```csharp
public OpenGLVideoOutput InternalOutput => _output;
```

---

### Issue 3.3 — `SDL3VideoView.PushFrame` is non-blocking but `IVideoOutput` doesn't say so

`SDL3VideoView.PushFrame` enqueues and returns immediately; `NDIVideoOutput` with `ClockVideo=true` blocks for ~33 ms. The mixer makes no distinction.

**Fix:** Add a `bool IsNonBlocking` property (or document this in XML):

```csharp
public interface IVideoOutput
{
    // ADD:
    /// <summary>
    /// <see langword="true"/> if <see cref="PushFrame(VideoFrame, TimeSpan)"/> returns before
    /// the frame is presented; <see langword="false"/> if it blocks until presentation or pacing.
    /// The mixer uses this to determine whether to wait for acknowledgement before sending the next frame.
    /// </summary>
    bool IsNonBlocking { get; }
}
```

`SDL3VideoView` returns `true`. `NDIVideoOutput` returns `ClockVideo` (blocks when clocked). `OpenGLVideoOutput` returns `false` (blocks on texture upload + present).

---

## 4. Avalonia (`AvaloniaVideoOutput`)

### Issue 4.1 — Constructor throws on engine registration failure

```csharp
var add = _engine.AddOutput(_output);
if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
    throw new InvalidOperationException($"Failed to register output. Code={add}.");
```

This is inconsistent with the framework's integer return code convention.

**Fix:** Expose a static factory:

```csharp
public sealed class AvaloniaVideoOutput : IVideoOutput
{
    private AvaloniaVideoOutput(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
    {
        // purely internal — no throwing
        _output = output;
        _engine = engine;
        IsClone = isClone;
    }

    public static int Create(out AvaloniaVideoOutput? result)
    {
        result = null;
        var output = new OpenGLVideoOutput();
        var engine = new OpenGLVideoEngine();
        var add = engine.AddOutput(output);
        if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
            return add;
        result = new AvaloniaVideoOutput(output, engine, isClone: false);
        return MediaResult.Success;
    }
}
```

---

### Issue 4.2 — `AvaloniaVideoOutput` and `SDL3VideoView` duplicate the engine-wrapping pattern

Both implement `IVideoOutput` by wrapping `OpenGLVideoOutput` + `OpenGLVideoEngine`. Both implement `CreateClone` / `AttachClone` / `DetachClone` with nearly identical code. This is ~200 lines duplicated across two types.

**Fix:** Extract a shared abstract base:

```csharp
// In S.Media.OpenGL:
public abstract class OpenGLWrapperVideoOutput : IVideoOutput
{
    protected readonly OpenGLVideoOutput Output;
    protected readonly OpenGLVideoEngine Engine;

    protected OpenGLWrapperVideoOutput(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
    {
        Output = output;
        Engine = engine;
        IsClone = isClone;
        Engine.AddOutput(Output);
    }

    public Guid Id => Output.Id;
    public bool IsClone { get; }
    public int Start(VideoOutputConfig config) => Output.Start(config);
    public int Stop() => Output.Stop();
    public int PushFrame(VideoFrame frame) => Output.PushFrame(frame);
    public int PushFrame(VideoFrame frame, TimeSpan pts) => Output.PushFrame(frame, pts);

    protected int CreateCloneCore(OpenGLCloneOptions options, out OpenGLVideoOutput? cloneOutput)
        => Engine.CreateCloneOutput(Id, options, out cloneOutput);

    public void Dispose()
    {
        Engine.RemoveOutput(Id);
        if (!IsClone) Engine.Dispose();
        Output.Dispose();
    }
}

// AvaloniaVideoOutput and SDL3VideoView extend this, adding only their UI-specific code
```

---

## 5. Common Concerns

### Thread Affinity

OpenGL contexts are thread-affine — all GL calls must happen on the thread that created the context. `SDL3VideoView` creates the context on the SDL render thread. `AvaloniaVideoOutput` creates it on the Avalonia UI thread (via the `IRenderSession`). Neither of these contexts can be used from the mixer's pump threads.

This is why both wrappers enqueue frames to a render thread rather than calling GL directly in `PushFrame`. This is correct. The implication is that:

- `PushFrame` is always non-blocking (queue-and-return).
- Frame drop can occur silently if the render queue is full.

**Recommendation:** Expose a `DroppedFrameCount` diagnostic counter on both output types, and log or event-raise when drops occur.

---

### Clone Pixel Format Requirements

Clones must use the same pixel format as the parent. `OpenGLVideoEngine` enforces this and returns `OpenGLClonePixelFormatIncompatible` if mismatched. Document this requirement prominently on `CreateClone` / `AttachClone`:

```csharp
/// <remarks>
/// The clone output <b>must</b> receive frames in the same pixel format as the parent.
/// Mismatched formats will cause <see cref="MediaErrorCode.OpenGLClonePixelFormatIncompatible"/>.
/// </remarks>
```

---

### MaxCloneDepth Enforcement

`OpenGLCloneOptions.MaxCloneDepth` limits how deep the clone hierarchy can go. A clone of a clone of a clone will be rejected at `MaxCloneDepth = 2`. Ensure this limit is documented and surfaced in a clear error code (not `MediaInvalidArgument`). Consider adding `OpenGLCloneDepthExceeded` to the error code system.

