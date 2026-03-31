# S.Media.OpenGL — Issues & Fix Guide

> **Scope:** `S.Media.OpenGL`, `S.Media.OpenGL.SDL3`, `S.Media.OpenGL.Avalonia`
> **Cross-references:** See `API-Review.md` §§7, 8 for the full analysis.
> **Last reviewed:** 2026-03-31

---

## Master Checklist

| # | Area | Issue | Status |
|---|------|-------|--------|
| 1.1 | Engine | `AddOutput(IVideoOutput)` silently rejects non-GL outputs | ✅ Fixed — param changed to `OpenGLVideoOutput` |
| ~~1.2~~ | ~~Engine~~ | ~~`VSync` has no implementation~~ | ✅ Stale — VSync IS implemented (see §1.2) |
| 1.3 | Engine | Embedded path sleeps under `_gate` lock; per-output anchor double-paces with mixer | ✅ Fixed — `_output.PushFrame` moved outside lock |
| 2.1 | Clone | Clone topology lives on `OpenGLVideoOutput` itself | ✅ Fixed — full graph moved into `OpenGLVideoEngine._childrenByParent`; `GetCloneIds` / `GetParentId` / `IsClone` query methods added; corresponding fields/methods removed from `OpenGLVideoOutput` |
| 3.1 | SDL3 | `SDL3VideoView` maintains its own parallel clone dictionary | ✅ Fixed — `_clones` dictionary removed; all topology queries delegated to the engine |
| 3.2 | SDL3 | Triple-layer dispatch; internal output inaccessible for diagnostics | ✅ Fixed — `InternalOutput` diagnostic property added |
| 3.3 | SDL3 | `IVideoOutput` blocking contract undocumented (description corrected) | ✅ Fixed — `IVideoOutput.PushFrame` XML docs updated |
| 4.1 | Avalonia | Constructor throws on engine registration failure | ✅ Fixed — `Create()` static factories added to both backends |
| 4.2 | Both | Engine-wrapping pattern duplicated across both backends | ✅ Fixed — `OpenGLWrapperVideoOutput` abstract base class extracted into `S.Media.OpenGL`; both `AvaloniaVideoOutput` and `SDL3VideoView` now inherit it |
| **B1** | **Avalonia** | **`AvaloniaVideoOutput`→`AvaloniaOpenGLHostControl` pixel path broken** | ✅ Fixed — `BindFrameCallback` + frame forwarding in `PushFrame` |
| **B2** | **Clone** | **`OpenGLCloneMode` values stored but never enforced** | ✅ Fixed (Option A) — `SharedTexture` and `SharedFboBlit` marked `[Obsolete]`; `CopyFallback` is documented as the only active path |
| **B3** | **SDL3** | **Embedded `PushFrame` sleeps while holding `_gate` lock** | ✅ Fixed — see Issue 1.3 |
| **B4** | **Avalonia** | **`_lastFrame` never cleared after render — memory leak** | ✅ Fixed — `AddRef` on store; cleared + `Dispose` after render |
| **B5** | **Core GL** | **`Upload/` subsystem is orphaned dead code** | 🔴 Open |
| **B6** | **Shaders** | **YUV shaders assume full-range; most real video is limited-range** | 🔴 Open — needs `IsFullRange` in `VideoFrame` first |
| **B7** | **Clone** | **`AttachClone` ignores its `options` parameter in both backends** | ✅ Fixed — public engine overload added; both backends forward options |
| **B8** | **SDL3** | **Platform-handle default fallback wrong on macOS / Windows** | ✅ Fixed — `RuntimeInformation.IsOSPlatform` guards added |
| **B9** | **Diagnostics** | **Diagnostic structs inconsistent; timing metrics always zero** | ✅ Fixed — unified `VideoOutputDiagnosticsSnapshot` record added; `AvaloniaOutputDebugInfo` / `SDL3OutputDebugInfo` aligned (all fields present) with `ToSnapshot()` converters; upload and present timings measured via `Stopwatch` in both backends and wired through `OpenGLVideoOutput.UpdateTimings()` |
| **B10** | **Clone** | **Useful clone-policy settings hidden as `internal`** | ✅ Fixed — `RejectCycles`, `AllowAttachWhileRunning`, `AutoResizeToParent` made `public` |
| **C1** | **Core** | **`IVideoOutput.PushFrame` XML docs name concrete types** | ✅ Fixed — generic language + fully-qualified `cref` links |
| **C2** | **Core** | **`VideoOutputState` has no `Paused` value** | ✅ Fixed — `Paused = 2` added to `VideoOutputState` |
| **C3** | **Core** | **`BackpressureWaitFrameMultiplier`/`BackpressureTimeout` interaction undocumented** | ✅ Fixed — `<remarks>` added to both properties |
| **C4** | **Shaders** | **Shader source strings duplicated across three files** | 🔴 Open |

---

## Table of Contents

1. [OpenGL Engine & Output](#1-opengl-engine--output)
2. [Clone Graph Management](#2-clone-graph-management)
3. [SDL3 (`SDL3VideoView`)](#3-sdl3-sdl3videoview)
4. [Avalonia (`AvaloniaVideoOutput` / `AvaloniaOpenGLHostControl`)](#4-avalonia-avaoniavideoutput--avaloniaopengllhostcontrol)
5. [Shared Shader / Conversion Layer](#5-shared-shader--conversion-layer)
6. [Diagnostics](#6-diagnostics)
7. [S.Media.Core Adjustments](#7-smediacore-adjustments)
8. [Common Concerns](#8-common-concerns)

---

## 1. OpenGL Engine & Output

### Issue 1.1 — `OpenGLVideoEngine.AddOutput(IVideoOutput)` silently rejects non-GL outputs

**Status:** 🔴 Open

```csharp
public int AddOutput(IVideoOutput output)
{
    if (output is not OpenGLVideoOutput glOutput)
        return (int)MediaErrorCode.MediaInvalidArgument;
    // ...
}
```

Callers get a generic `MediaInvalidArgument` with no indication of why. Accepting `IVideoOutput`
but only working for `OpenGLVideoOutput` violates the intent of the interface parameter.

**Fix:** Change the parameter type to the concrete type:

```csharp
public int AddOutput(OpenGLVideoOutput output)
{
    ArgumentNullException.ThrowIfNull(output);
    // ...
}
```

Any call site passing a non-`OpenGLVideoOutput` now gets a compile error rather than a silent
runtime failure. Update `RemoveOutput(IVideoOutput)` similarly.

---

### ~~Issue 1.2 — `VideoOutputPresentationMode.VSync` has no implementation~~ — STALE / REMOVED

> **⚠️ This issue was incorrect and is removed from the active list.**
>
> `VSync` is fully implemented in two layers:
>
> - `OpenGLVideoOutput.ComputePresentationTimingLocked` does software frame-interval gating
>   when `PresentationMode == VSync`, using `_lastPresentTicks` and the configured
>   `VSyncRefreshRate` (defaults to 60 Hz).
> - `SDL3VideoView.RenderLoop` calls `SDL.GLSetSwapInterval(1)` for hardware VSync at the
>   buffer-swap level.
>
> The original draft also referenced enum values `Throttled` and `AudioLed` — **these do not
> exist**. The real values are `Unlimited = 0`, `SourceTimestamp = 1`, `MaxFps = 2`, `VSync = 3`.
>
> **Remaining gap (minor):** Hardware VSync via `GLSetSwapInterval` is only available on the
> standalone render thread. In embedded (Avalonia / parent-window) mode there is no buffer-swap
> to synchronise on, so only the software interval-gating path applies.

---

### Issue 1.3 — Embedded `PushFrame` sleeps under `_gate` lock; per-output anchor double-paces with mixer

**Status:** 🔴 Open

> **Note:** The original Issue 1.3 framed this as "timestamp anchor logic duplicated with the
> mixer". The duplication exists but is mostly benign in standalone mode. The concrete bug is in
> the embedded path.

**Bug — lock-while-sleeping:** In `SDL3VideoView.PushFrame` (embedded branch):

```csharp
lock (_gate)
{
    var push = _output.PushFrame(frame, presentationTime);  // may call Thread.Sleep
    ...
}
```

`OpenGLVideoOutput.PushFrame` releases its own internal lock before sleeping, but
`SDL3VideoView._gate` stays held for the full sleep duration. Any concurrent call to `Stop`,
`Resize`, `Dispose`, or `CreateClone` blocks until the sleep elapses.

**Fix:** Move the embedded `_output.PushFrame` call outside `_gate`:

```csharp
bool embedded;
lock (_gate) { embedded = _embedded; /* read other state */ }

if (embedded)
{
    // sleep (if any) happens here, _gate is NOT held
    var push = _output.PushFrame(frame, presentationTime);
    if (push != MediaResult.Success) return push;

    lock (_gate)
    {
        // commit generation tracking, pipeline draw
    }
    return MediaResult.Success;
}
```

**Double-pacing note:** When used with the mixer, the mixer's `VideoPresenterSyncPolicy` already
handles PTS scheduling before calling `PushFrame`. Setting `PresentationMode = Unlimited` on
`VideoOutputConfig` disables the per-output sleep, preventing double-pacing.

---

## 2. Clone Graph Management

### Issue 2.1 — Clone graph topology lives on `OpenGLVideoOutput`

**Status:** 🔴 Open

`OpenGLVideoOutput` carries `IsClone`, `CloneParentOutputId`, `CloneOutputIds`, and the internal
`AddClone` / `RemoveClone` mutation methods. These are engine-level topology concerns that belong
in `OpenGLVideoEngine`.

**Fix:** Move the full clone graph into `OpenGLVideoEngine`:

```csharp
// Engine already has _childToParent; add the reverse map:
private readonly Dictionary<Guid, List<Guid>> _childrenByParent = new();

// Remove from OpenGLVideoOutput:
// public bool IsClone { get; }
// public Guid? CloneParentOutputId { get; }
// public IReadOnlyList<Guid> CloneOutputIds { get; }
// internal int AddClone(Guid) / RemoveClone(Guid)

// Engine exposes graph queries:
public IReadOnlyList<Guid> GetCloneIds(Guid parentId);
public Guid? GetParentId(Guid cloneId);
public bool IsClone(Guid outputId);
```

`SDL3VideoView` and `AvaloniaVideoOutput` query the engine for `IsClone` / parent ID rather than
reading from the inner `OpenGLVideoOutput`.

---

### Issue B2 — `OpenGLCloneMode` values are stored but never enforced

**Status:** 🔴 Open

`OpenGLCloneMode.SharedTexture / SharedFboBlit / CopyFallback` is accepted by `OpenGLCloneOptions`
and by `OpenGLUploadPlanner.CreatePlan`, but `OpenGLVideoEngine.PushFrame` always calls
`clone.PresentClonedFrame(committedSurface)` regardless of mode. Only surface **metadata**
(width, height, format, generation) is forwarded to clones — no actual texture sharing, FBO
blit, or pixel copy takes place. `OpenGLUploadPlanner.preferredPath` is also computed but never
acted on.

**Fix options:**

- **Option A (honest API):** Mark the enum with `[Obsolete("Clone rendering paths are not yet implemented. CopyFallback is the only active path.")]` until the shared-context path is built.
- **Option B (implement):** Dispatch clones according to their registered mode after committing the parent frame. `CopyFallback` = current behaviour (forward metadata only). `SharedTexture` / `SharedFboBlit` require a shared GL context — implement as a follow-up once the context-sharing infrastructure exists.

---

### Issue B7 — `AttachClone` ignores its `options` parameter in both backends

**Status:** 🔴 Open — Bug

`AvaloniaVideoOutput.AttachClone` calls `_engine.AttachCloneOutput(Id, cloneOutput.Id)` without
forwarding `options`. `SDL3VideoView.AttachClone` explicitly discards them with `_ = options`.
`MaxCloneDepth`, `CloneMode`, and `HudMode` passed by the caller are silently thrown away.

**Fix:** Expose an `AttachCloneOutput` overload on `OpenGLVideoEngine` that accepts options
(the private `AttachCloneOutputCore` already supports this):

```csharp
public int AttachCloneOutput(Guid parentOutputId, Guid cloneOutputId, in OpenGLCloneOptions options)
    => AttachCloneOutputCore(parentOutputId, cloneOutputId, options);
```

Both wrappers forward their translated options:

```csharp
return _engine.AttachCloneOutput(Id, cloneOutput.Id, ToOpenGlCloneOptions(options));
```

---

### Issue B10 — Useful clone-policy settings hidden as `internal`

**Status:** 🟡 Open — Low priority

Several `OpenGLClonePolicyOptions` and `OpenGLCloneOptions` properties consumers would reasonably
want to configure are `internal`:

| Property | On | Rationale for exposing |
|---|---|---|
| `RejectCycles` | `OpenGLClonePolicyOptions` | Cycle detection is a correctness policy, not an implementation detail |
| `AllowAttachWhileRunning` | `OpenGLClonePolicyOptions` | Live attach is a valid use-case |
| `AutoResizeToParent` | `OpenGLCloneOptions` | Resize behaviour is consumer-observable |

**Fix:** Make `RejectCycles` and `AllowAttachWhileRunning` `public`. Keep
`FailIfContextSharingUnavailable` and `AttachPauseBudgetFrames` internal until the
texture-sharing path (B2) is implemented.

---

## 3. SDL3 (`SDL3VideoView`)

### Issue 3.1 — `SDL3VideoView` maintains its own parallel clone dictionary

**Status:** 🔴 Open

`SDL3VideoView._clones : Dictionary<Guid, SDL3VideoView>` runs in parallel with
`OpenGLVideoEngine._childToParent` and `OpenGLVideoOutput._cloneOutputIds`. Three registries
tracking the same graph can diverge.

**Fix:** Remove `_clones` from `SDL3VideoView`. Delegate all topology queries to the engine:

```csharp
public int CreateClone(in SDL3CloneOptions options, out SDL3VideoView? cloneView)
{
    cloneView = null;
    var r = _engine.CreateCloneOutput(Id, ToOpenGlCloneOptions(options), out var glClone);
    if (r != MediaResult.Success || glClone is not OpenGLVideoOutput gl)
        return r;

    cloneView = new SDL3VideoView(gl, _engine, isClone: true);
    // no longer adds to _clones; engine IS the registry
    return MediaResult.Success;
}
```

---

### Issue 3.2 — Triple-layer dispatch; internal output not accessible for diagnostics

**Status:** 🔴 Open

The `PushFrame` path:

```
Caller → SDL3VideoView → render queue → render thread
       → OpenGLVideoOutput (timing + metadata) → OpenGLVideoEngine (clone fan-out)
```

The inner `OpenGLVideoOutput` is private, making its diagnostics (`FramesPresented`,
`FramesDropped`, `Surface`) invisible to callers.

**Interim fix (low-effort):** Expose the internal output as a read-only diagnostic property:

```csharp
/// <summary>
/// The underlying <see cref="OpenGLVideoOutput"/> managed by this view.
/// For diagnostics only — do not call Start/Stop/PushFrame directly on this object.
/// </summary>
public OpenGLVideoOutput InternalOutput => _output;
```

**Long-term fix:** Once Issue B9 (unified diagnostics) is addressed, expose typed diagnostics
directly on `SDL3VideoView` without leaking the inner object.

---

### Issue 3.3 — `IVideoOutput` blocking contract undocumented

**Status:** 🟡 Partially open

> **Correction from original doc:** The original text said "`OpenGLVideoOutput` returns `false`
> (blocks on texture upload + present)." Both claims are wrong:
> 1. `OpenGLVideoOutput` does **no** texture upload — it only tracks surface metadata and does
>    optional `Thread.Sleep` scheduling.
> 2. In standalone SDL3 mode `PushFrame` is **non-blocking** (enqueue + return immediately).

Accurate per-type blocking contract:

| Type | `PushFrame` blocking behaviour |
|---|---|
| `SDL3VideoView` (standalone) | Non-blocking — enqueues to render-thread queue |
| `SDL3VideoView` (embedded) | Blocking — synchronous upload + draw on calling thread |
| `AvaloniaVideoOutput` | Non-blocking — updates surface metadata; rendering is async in Avalonia's render loop |
| `OpenGLVideoOutput` (raw) | May sleep in `SourceTimestamp` / `MaxFps` / `VSync` modes |

**Fix:** Add `bool IsNonBlocking` to `IVideoOutput` (or document via XML):

```csharp
/// <summary>
/// <see langword="true"/> if <see cref="PushFrame(VideoFrame, TimeSpan)"/> returns before
/// the frame is visually presented; <see langword="false"/> if the call may block for up to
/// one frame interval for pacing.
/// </summary>
bool IsNonBlocking { get; }
```

---

### Issue B3 — See §1.3

The embedded-path lock-while-sleeping bug is described in §1.3.

---

### Issue B8 — Platform-handle default fallback is Linux-only

**Status:** 🔴 Open

`SDL3VideoView.ResolveDefaultDescriptor()` falls back to `"x11-window"` when `WAYLAND_DISPLAY`
is unset — wrong on macOS (should be `"cocoa-nsview"`) and Windows (should be `"win32-hwnd"`).

**Fix:**

```csharp
private static string ResolveDefaultDescriptor()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return "win32-hwnd";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        return "cocoa-nsview";
    return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        ? "wayland-surface"
        : "x11-window";
}
```

---

## 4. Avalonia (`AvaloniaVideoOutput` / `AvaloniaOpenGLHostControl`)

### Issue B1 — `AvaloniaVideoOutput` → `AvaloniaOpenGLHostControl` pixel path is broken *(Critical)*

**Status:** 🔴 Open — Critical bug

When the mixer calls `AvaloniaVideoOutput.PushFrame(frame)` the call chain is:

```
mixer → AvaloniaVideoOutput.PushFrame(frame)
      → _output.PushFrame(frame)         ← updates Surface metadata (generation++)
      → diagnostics.PublishSurfaceChanged ← AvaloniaOpenGLHostControl does NOT subscribe to this
```

`AvaloniaOpenGLHostControl.OnOpenGlRender` renders `_lastFrame`. `_lastFrame` is set **only** by
`AvaloniaOpenGLHostControl.PushFrame(frame)` — a method never called by `AvaloniaVideoOutput`.

Result: the mixer delivers frames into `AvaloniaVideoOutput`, surface generation increments, the
control may schedule a render pass, but `_lastFrame` is `null` → nothing is displayed.

**Fix:** Give `AvaloniaVideoOutput` a reference to its bound control and forward the frame:

```csharp
public sealed class AvaloniaVideoOutput : IVideoOutput
{
    private AvaloniaOpenGLHostControl? _hostControl;

    public void BindHostControl(AvaloniaOpenGLHostControl control)
    {
        _hostControl = control;
    }

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        var r = _output.PushFrame(frame, presentationTime);
        if (r == MediaResult.Success)
            _hostControl?.PushFrame(frame);  // forward pixel data to Avalonia render loop
        return r;
    }
}
```

**Required setup change:**

```csharp
// BEFORE (broken):
var avOut = new AvaloniaVideoOutput();
var control = new AvaloniaOpenGLHostControl(avOut.Output);
mixer.AddVideoOutput(avOut);

// AFTER (working):
var avOut = new AvaloniaVideoOutput();
var control = new AvaloniaOpenGLHostControl(avOut.Output);
avOut.BindHostControl(control);   // ← new required step
mixer.AddVideoOutput(avOut);
```

---

### Issue 4.1 — Constructor throws on engine registration failure

**Status:** 🔴 Open

```csharp
var add = _engine.AddOutput(_output);
if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
    throw new InvalidOperationException($"Failed to register output. Code={add}.");
```

Inconsistent with the framework's integer return code convention.

**Fix:** Expose a static factory on both `AvaloniaVideoOutput` and `SDL3VideoView`:

```csharp
public sealed class AvaloniaVideoOutput : IVideoOutput
{
    private AvaloniaVideoOutput(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
    {
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

### Issue 4.2 — Engine-wrapping pattern duplicated across both backends

**Status:** 🔴 Open

`AvaloniaVideoOutput` and `SDL3VideoView` both wrap `OpenGLVideoOutput` + `OpenGLVideoEngine` and
both implement `CreateClone` / `AttachClone` / `DetachClone` with nearly identical code
(~200 lines duplicated).

**Fix:** Extract a shared abstract base in `S.Media.OpenGL`:

```csharp
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
    public VideoOutputState State => Output.State;
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
```

`AvaloniaVideoOutput` and `SDL3VideoView` extend this, adding only their UI-specific code.

---

### Issue B4 — `_lastFrame` never cleared in `AvaloniaOpenGLHostControl` — memory leak

**Status:** 🔴 Open — Bug

`AvaloniaOpenGLHostControl._lastFrame` is set on every `PushFrame` call but never reset after
rendering. A 4K BGRA frame is ~32 MB; the previous frame is retained indefinitely, preventing GC.

**Fix:** Clear the field after reading it in `OnOpenGlRender`:

```csharp
protected override void OnOpenGlRender(GlInterface gl, int fb)
{
    VideoFrame? frame;
    lock (_gate)
    {
        frame = _lastFrame;
        _lastFrame = null;   // ← release reference so the frame can be collected
        // ... read other state
    }

    if (frame != null)
    {
        // If VideoFrame uses ref-counting, call frame.AddRef() before clearing _lastFrame above,
        // then Dispose() here after rendering.
        _renderer.RenderFrame(gl, fb, frame, pixelWidth, pixelHeight, KeepAspectRatio);
    }
    // ...
}
```

---

## 5. Shared Shader / Conversion Layer

### Issue C4 — Shader source strings duplicated across three files

**Status:** 🔴 Open

Identical (or near-identical) GLSL shader strings appear in three places:

| File | Form |
|---|---|
| `SDL3VideoView` | Single-line packed string constants |
| `SDL3ShaderPipeline` | Named `private const int` GL constants + inline strings |
| `AvaloniaGLRenderer` | Proper multi-line raw string literals (best form, but separate) |

Any fix to the YUV color math (Issue B6 below) must be applied in all three places.

**Fix:** Create a single `GlslShaders` static class in `S.Media.OpenGL`:

```csharp
// S.Media.OpenGL/GlslShaders.cs
internal static class GlslShaders
{
    internal static string VertexCore { get; } = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main() { gl_Position = vec4(aPosition, 0.0, 1.0); vTexCoord = aTexCoord; }
        """;

    internal static string VertexEs { get; } = /* #version 300 es variant */;
    internal static string FragmentRgbaCore { get; } = /* ... */;
    internal static string FragmentRgbaEs { get; } = /* ... */;
    internal static string FragmentYuvCore { get; } = /* single source of truth for YUV math */;
    internal static string FragmentYuvEs { get; } = /* ... */;
}
```

`SDL3VideoView`, `SDL3ShaderPipeline`, and `AvaloniaGLRenderer` all reference this class.
`SDL3VideoView` should also move its remaining inline hex GL constants (`0x0DE1`, `0x8229`, etc.)
to named `const int` fields, matching the pattern already used in `SDL3ShaderPipeline`.

---

### Issue B6 — YUV shaders assume full-range luma; most real video is limited-range

**Status:** 🔴 Open — Quality Bug

The YUV → RGB formula in all three shader sources uses BT.709 coefficients applied directly to
raw texture samples:

```glsl
float y = texture(uTextureY, vTexCoord).r;   // raw sample, no range correction
float r = y + 1.5748 * v;
```

H.264 and H.265 content defaults to **limited-range** (TV range): luma encoded 16–235 rather
than 0–255. Without range expansion, black appears grey (~6%) and peak white is compressed.

**Fix:** Add a `uFullRange` uniform and apply range correction in all YUV shaders:

```glsl
uniform int uFullRange;  // 1 = full range (0..255), 0 = limited range (16..235)

void main()
{
    float y = texture(uTextureY, vTexCoord).r;
    float u = texture(uTextureU, vTexCoord).r - 0.5;
    float v = texture(uTextureV, vTexCoord).r - 0.5;

    if (uFullRange == 0) {
        y = (y - 16.0/255.0) * (255.0/219.0);
        u *= (255.0/224.0);
        v *= (255.0/224.0);
    }

    FragColor = vec4(yuvToRgb(y, u, v), 1.0);
}
```

`uFullRange` should be driven by frame metadata. **Required upstream change:** add
`bool IsFullRange` to `VideoFrame` (or `IPixelFormatData`) and map `AVFrame.color_range`
in `S.Media.FFmpeg` (`AVCOL_RANGE_JPEG` = full, `AVCOL_RANGE_MPEG` = limited).

---

### Issue B5 — `Upload/` subsystem is orphaned dead code

**Status:** 🔴 Open

`OpenGLTextureUploader`, `OpenGLUploadPlanner`, `UploadPlan`, and `YuvToRgbaConverter`
(`S.Media.OpenGL/Upload`, `S.Media.OpenGL/Conversion`) are **never called** by any active
rendering path:

- `SDL3VideoView` does inline texture upload via private GL delegates
- `SDL3ShaderPipeline` has its own complete upload path
- `AvaloniaGLRenderer` has its own complete upload path

Additionally `YuvToRgbaConverter` only handles `Rgba32`, `Bgra32`, `Yuv420P`, and `Nv12` —
missing 7 of the 11 `VideoPixelFormat` values.

**Decision needed:**

- **Option A (delete):** Remove `Upload/` and `Conversion/`. Retain `OpenGLCapabilitySnapshot`
  for future texture-sharing (B2). Add a `// TODO` comment linking to B2.
- **Option B (integrate):** Wire `OpenGLTextureUploader` into `SDL3ShaderPipeline` and
  `AvaloniaGLRenderer` as the canonical upload path, extend `YuvToRgbaConverter` to all 11
  formats, delete the duplicated inline logic.

---

## 6. Diagnostics

### Issue B9 — Diagnostic structs are inconsistent and timing metrics are always zero

**Status:** 🔴 Open

| Field | `OpenGLOutputDebugInfo` | `SDL3OutputDebugInfo` | `AvaloniaOutputDebugInfo` |
|---|---|---|---|
| `FramesPresented` | ✅ | ✅ | ✅ |
| `FramesDropped` | ✅ | ✅ | ❌ Missing |
| `FramesCloned` | ✅ | ✅ | ✅ |
| `LastUploadMs` | ✅ (always `0.0`) | ❌ Missing | ❌ Missing |
| `LastPresentMs` | ✅ (always `0.0`) | ✅ (always `0.0`) | ❌ Missing |
| `Surface` | ✅ | ✅ | ✅ |

`LastUploadMs` and `LastPresentMs` are hardcoded to `0` in `BuildDiagnosticsSnapshotLocked`
— the actual upload and present timings are never measured.

**Fix:**

1. Define a shared record in `S.Media.OpenGL`:

```csharp
public readonly record struct VideoOutputDiagnosticsSnapshot(
    long FramesPresented,
    long FramesDropped,
    long FramesCloned,
    double LastUploadMs,
    double LastPresentMs,
    OpenGLSurfaceMetadata Surface);
```

2. Wrap `Stopwatch.GetTimestamp()` around the actual upload and draw calls in `SDL3VideoView`,
   `SDL3ShaderPipeline`, and `AvaloniaGLRenderer`. Publish the measured deltas.

3. Consolidate `AvaloniaOutputDebugInfo` and `SDL3OutputDebugInfo` to use this common type.

---

## 7. S.Media.Core Adjustments

### Issue C1 — `IVideoOutput.PushFrame` XML docs name concrete implementation types

**Status:** 🟡 Open — Low priority

The XML doc comment on `IVideoOutput.PushFrame` references `NDIVideoOutput`, `SDL3VideoView`,
and `AvaloniaVideoOutput` by literal name. As implementations evolve the names will drift out
of sync with reality.

**Fix:** Use `<see cref="..."/>` for stable types; replace implementation specifics with
generic language:

```csharp
/// <summary>
/// Pushes a decoded video frame to this output for immediate or scheduled presentation.
/// </summary>
/// <remarks>
/// Implementations may block for up to one frame interval for presentation pacing
/// (e.g. when <see cref="VideoOutputConfig.PresentationMode"/> ≠
/// <see cref="VideoOutputPresentationMode.Unlimited"/>).
/// Use <see cref="AVMixerConfig.PresentationHostPolicy"/> with
/// <see cref="VideoDispatchPolicy.BackgroundWorker"/> to isolate blocking outputs
/// from the mixer's presentation thread.
/// </remarks>
int PushFrame(VideoFrame frame, TimeSpan presentationTime);
```

---

### Issue C2 — `VideoOutputState` has no `Paused` value

**Status:** 🟡 Open — Low priority

`VideoOutputState` has only `Stopped` and `Running`. A mixer that wants to temporarily freeze a
specific output (hold the last frame) has no contract for this state.

**Consideration:** Add `Paused = 2`. Outputs that do not support pausing return
`MediaErrorCode.MediaInvalidOperation` on a future `Pause()` call. Defer until the mixer
implements per-output pause control.

---

### Issue C3 — `BackpressureWaitFrameMultiplier` / `BackpressureTimeout` interaction undocumented

**Status:** 🟡 Open — Low priority

`VideoOutputConfig.BackpressureMode = Wait` requires either `BackpressureTimeout.HasValue` or a
frame duration from the caller (`hasEffectiveFrameDuration` in `Validate`). This contract is
enforced in `Validate` but is undocumented on the properties themselves.

**Fix:** Add `<remarks>` to both `BackpressureWaitFrameMultiplier` and `BackpressureTimeout`
describing the `Wait` mode dependency and the `Validate` enforcement.

---

## 8. Common Concerns

### Thread Affinity

OpenGL contexts are thread-affine. `SDL3VideoView` creates the context on the SDL render thread;
`AvaloniaVideoOutput` creates it on the Avalonia UI thread. Neither context can be used from the
mixer's pump threads. Both wrappers therefore enqueue frames to a render thread rather than
calling GL directly in `PushFrame`. **This design is correct.**

Implications:

- `PushFrame` is always non-blocking in standalone mode (queue-and-return).
- Frame drops can occur silently if the render queue is full.

**Recommendation:** Expose a `DroppedFrameCount` counter on both output types (see Issue B9)
and raise an event or log when drops occur.

---

### macOS SDL Event Pump

`SDL3VideoView.RenderLoop` contains the following acknowledged TODO:

```csharp
// TODO: on macOS SDL events must be pumped on the main thread.
//       For now, pump them here (fine on Linux X11/Wayland).
PumpSdlEvents();
```

On macOS, calling `SDL.PollEvent` from a background thread causes Cocoa run-loop assertion
failures or silent corruption. This must be resolved before any macOS deployment. Standard fix:
post a request to the main thread to pump events once per frame, or use a dedicated SDL
event-dispatch mechanism.

---

### Clone Pixel Format Requirements

Clones must use the same pixel format as the parent. `OpenGLVideoEngine` enforces this and
returns `OpenGLClonePixelFormatIncompatible` if mismatched. Document this requirement on
`CreateClone` / `AttachClone`:

```csharp
/// <remarks>
/// The clone output <b>must</b> receive frames in the same pixel format as the parent.
/// Mismatched formats will cause
/// <see cref="MediaErrorCode.OpenGLClonePixelFormatIncompatible"/>.
/// </remarks>
```

---

### MaxCloneDepth Enforcement

`OpenGLCloneOptions.MaxCloneDepth` limits clone hierarchy depth.
`MediaErrorCode.OpenGLCloneMaxDepthExceeded` (= 4412) is already defined and returned correctly
by the engine. ~~"Consider adding `OpenGLCloneDepthExceeded`"~~ — **this error code already
exists; no action needed.** Ensure it is documented on `CreateClone` and `AttachClone`.
