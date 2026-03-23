# OpenGL Migration Plan (`S.Media.OpenGL*`)

Source references:
- `Media/S.Media.OpenGL/API-outline.md`
- `Media/S.Media.OpenGL.Avalonia/API-outline.md`
- `Media/S.Media.OpenGL.SDL3/API-outline.md`
- `Media/S.Media.Core/PLAN.smedia-architecture.md`

## Scope

This document maps legacy OpenGL-related code from:
- `VideoLibs/Seko.OwnAudioNET.Video.Engine`
- `VideoLibs/Seko.OwnAudioNET.Video.Avalonia`
- `VideoLibs/Seko.OwnAudioNET.Video.SDL3`

into:
- `Media/S.Media.OpenGL`
- `Media/S.Media.OpenGL.Avalonia`
- `Media/S.Media.OpenGL.SDL3`

No compatibility wrappers. Hard cut only.

## Locked Runtime Semantics

- Clone sync: clones present on parent committed frame generation boundaries.
- Clone creation/attach while running: allowed, with configurable pause window.
  - Policy default: `AttachPauseBudgetFrames = 1` (performance-first), best-effort with warning on exceed.
- Clone graph rules:
  - no cycles
  - explicit self-attach rejection
  - reject child already attached to another parent
  - `DetachClone` non-child => `OpenGLCloneNotAttached`
  - parent disposal destroys all child clones deterministically
- Pixel format authority: parent output is authoritative.
  - Default policy: `RequireCompatibleFastPath`
  - Incompatible parent/child formats => explicit error code (`OpenGLClonePixelFormatIncompatible`)
  - `AllowGpuConversion` only when shared-context GPU fast path is available; no copy-fallback conversion by default.
- HUD behavior: clone HUD is independent by default.
- Diagnostics refresh: on-change only (no per-frame spam).
- SDL embedding-handle contract:
  - handle lifetime is state-bound (valid after `Initialize*`, invalid after `Dispose`)
  - thread-affinity is platform-specific; unsafe cross-thread queries return defined non-zero error code
  - descriptor tokens are stable and explicit (`x11-window`, `wayland-surface`, `win32-hwnd`, `cocoa-nsview`)
  - embedded parent host loss returns defined non-zero error code + deterministic teardown
- Disposal order:
  1. stop ingress
  2. detach clone graph
  3. stop render loop
  4. release GL/HUD resources on owning context
  5. unregister output/engine links
  6. clear diagnostics state

## File Migration Matrix

### Core OpenGL engine

| Legacy Source | Target | Action | Notes |
|---|---|---|---|
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGLVideoEngine.cs` | `Media/S.Media.OpenGL/OpenGLVideoEngine.cs` | Refactor | Add clone graph orchestration + int error codes |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlUploadPlanner.cs` | `Media/S.Media.OpenGL/Upload/OpenGLUploadPlanner.cs` | Move+rename | Keep fast-path planning logic |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlTextureUploadOrchestrator.cs` | `Media/S.Media.OpenGL/Upload/OpenGLTextureUploader.cs` | Refactor | Keep high-throughput uploader internals |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlYuvConverter.cs` | `Media/S.Media.OpenGL/Conversion/YuvToRgbaConverter.cs` | Move+rename | Preserve conversion kernels |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoFramePacking.cs` | `Media/S.Media.OpenGL/*` internal helpers | Internalize/split | Keep packing private, wire to `OpenGLSurfaceMetadata` |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlShaders.cs` | `Media/S.Media.OpenGL/*` internal shader helpers | Internalize | Preserve shader source/cache strategy |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlConstants.cs` | `Media/S.Media.OpenGL/*` internal constants | Internalize | Namespace update only |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlGeometry.cs` | `Media/S.Media.OpenGL/*` internal geometry helpers | Internalize | Namespace update only |

### Avalonia extension

| Legacy Source | Target | Action | Notes |
|---|---|---|---|
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.cs` | `Media/S.Media.OpenGL.Avalonia/Output/AvaloniaVideoOutput.cs` | Refactor/split | Keep output binding + clone APIs |
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.Uploads.cs` | `Media/S.Media.OpenGL` uploader internals | Internalize | Reuse shared uploader, avoid duplicate upload paths |
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.HUD.cs` | `Media/S.Media.OpenGL.Avalonia/Diagnostics/MediaHudOverlay.cs` | Refactor | Keep HUD draw path, default independent clone state |

### SDL3 extension

| Legacy Source | Target | Action | Notes |
|---|---|---|---|
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.cs` | `Media/S.Media.OpenGL.SDL3/SDL3VideoView.cs` | Refactor/split | Keep window/output lifecycle |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.Shaders.cs` | `Media/S.Media.OpenGL.SDL3/SDL3ShaderPipeline.cs` | Move+rename | Preserve shader performance path |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.Packing.cs` | `Media/S.Media.OpenGL` internal packing helpers | Internalize | Prefer shared core packers |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.HUD.cs` | `Media/S.Media.OpenGL.SDL3/SDL3HudRenderer.cs` | Refactor | Keep HUD rendering separate from core |

## Public vs Internal Split

Public (`S.Media.OpenGL*`):
- engine/output/clone options/policies
- diagnostics snapshot contracts
- host adapter entry points (`AvaloniaVideoOutput`, `SDL3VideoView`)

Internal:
- shader source registries and compile caches
- GL proc-loading, handles, and context utility shims
- frame packers, plane upload orchestration internals
- backend-specific HUD rasterization details

## Avalonia Control Shape

Preferred design:
- Primary binding path: `AvaloniaOpenGLHostControl : OpenGLControlBase` + constructor-injected `OpenGLVideoOutput`
- Adapter dependency policy: base `Avalonia` NuGet only.

Rationale:
- minimizes abstraction overhead
- keeps GL lifecycle close to actual GL host control
- still allows ergonomic UI composition where desired

## Implementation Sequence

1. Move shared OpenGL upload/shader/packing internals into `S.Media.OpenGL` and compile standalone.
2. Implement `OpenGLVideoOutput` + clone graph policy enforcement and error paths.
3. Implement `OpenGLVideoEngine` orchestration and output registry.
4. Port Avalonia adapter (`AvaloniaVideoOutput`, `AvaloniaOpenGLHostControl`).
5. Port SDL3 adapter (`SDL3VideoView`, `SDL3ShaderPipeline`, `SDL3HudRenderer`).
6. Verify with clone scenarios (1 parent + 3 clones) and diagnostics-on-change behavior.

## SDL3 Control Extraction (`VideoSDL.cs`)

Preserve and adapt these proven pieces from `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.cs`:
- **Threaded render loop**: dedicated render thread, event pump, swap loop, graceful stop/join.
- **Presentation sync awareness**: swap-interval policy mapping (`None` vs VSync modes).
- **Fast upload path**: planner-driven upload with `glTexSubImage2D` + `glPixelStorei` row-length usage when available.
- **Strided upload metrics**: counters for strided plane/frame uploads to diagnose copy overhead.
- **Frame-version gate**: submit latest frame by generation/version; avoid redundant full upload for unchanged version.
- **Last-frame redraw path**: draw previously uploaded frame when no new frame arrives.
- **HUD toggles and diagnostics**: on-demand HUD with format/FPS/queue/upload/drift counters.
- **Source attachment pattern**: source `FrameReadyFast` callback path to output push queue.
- **Embedded/hosted window support**: platform-handle based embedded initialization and descriptor mapping.
- **Deterministic teardown**: stop loop, release queued frames, destroy GL context/window/resources in order.

Move these as internals where possible (not public API):
- GL delegate loading tables
- shader source strings and compile helpers
- texture state scratch buffers and packing utilities
- HUD text rasterization details

## Validation Checklist

- Parent + clones share one decode/upload path.
- Clone attach while running stays within pause budget.
- Self-attach, cycle attach, and already-attached child paths return defined error codes.
- Parent disposal destroys all clone children.
- Surface metadata updates on: resize, pixel format change, clone graph change, new committed frame generation.
- HUD is independent per clone by default.

