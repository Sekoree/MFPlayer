# OpenGL Migration Plan (`S.Media.OpenGL*`)

Source references:
- `Media/S.Media.OpenGL/API-outline.md`
- `Media/S.Media.OpenGL.Avalonia/API-outline.md`
- `Media/S.Media.OpenGL.SDL3/API-outline.md`
- `Media/S.Media.Core/PLAN.smedia-architecture.md`
- `Media/S.Media.Core/error-codes.md`

Metadata:
- Last updated: `2026-03-24`
- Status legend source: `Media/S.Media.Core/PLAN.smedia-architecture.md` (`Shared Wording Template` section)

## Scope

This plan hard-cuts performant OpenGL runtime pieces from legacy `VideoLibs/Seko.OwnAudioNET.*` projects into:
- `Media/S.Media.OpenGL`
- `Media/S.Media.OpenGL.Avalonia`
- `Media/S.Media.OpenGL.SDL3`

Goals:
- Keep proven high-throughput upload and shader paths.
- Preserve deterministic clone-graph behavior.
- Keep adapter layers UI-only (no decode/session logic).
- Move to int-first operational contracts (`0` success, non-zero failure).

Out of scope:
- compatibility wrappers/shims for legacy public types
- dual-path runtime (legacy + new)
- moving broadcast engines in this OpenGL pass (`BroadcastAudioEngine`, `BroadcastVideoEngine`)

## Adaptation Policy (Hard Rule)

- Legacy classes/files are reference material only.
- Do not move legacy class files or preserve legacy class identities in `S.Media.OpenGL*`.
- Adapt proven high-performance implementations into the new contracts/naming/layout.
- Keep migration focused on behavior and performance parity, not class/file carryover.

## Locked Runtime Semantics

- Clone graph safety: reject cycles, reject self-attach, reject child-already-attached.
- `DetachClone` on non-child returns `OpenGLCloneNotAttached`.
- Parent disposal deterministically destroys all child clones.
- Clone attach while running is allowed with pause budget (`AttachPauseBudgetFrames`, default `1`).
- Pixel-format policy is performance-first by default (`RequireCompatibleFastPath`).
- `AllowGpuConversion` is allowed only with a valid shared-context GPU fast path.
- HUD mode default is clone-independent.
- Diagnostics refresh is on-change, not per-frame spam.
- Teardown order is deterministic: stop ingress -> detach clone graph -> stop render loop -> release GL/HUD resources -> unregister links -> clear diagnostics state.

## Legacy to Target Migration Matrix

### Core OpenGL runtime

| Legacy source | Target | Action | Keep/perf note |
| --- | --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGLVideoEngine.cs` | `Media/S.Media.OpenGL/OpenGLVideoEngine.cs` | Refactor | Keep engine-first clone graph authority |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlUploadPlanner.cs` | `Media/S.Media.OpenGL/Upload/OpenGLUploadPlanner.cs` | Adapt | Preserve fast-path planning heuristics |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlTextureUploadOrchestrator.cs` | `Media/S.Media.OpenGL/Upload/OpenGLTextureUploader.cs` | Refactor | Preserve low-copy upload orchestration |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlYuvConverter.cs` | `Media/S.Media.OpenGL/Conversion/YuvToRgbaConverter.cs` | Adapt | Keep conversion kernels and stride handling |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoFramePacking.cs` | `Media/S.Media.OpenGL` internal helpers | Internalize | Reuse for plane packing, keep non-public |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlShaders.cs` | `Media/S.Media.OpenGL` internal shader helpers | Internalize | Preserve shader source/cache approach |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlConstants.cs` | `Media/S.Media.OpenGL` internal constants | Internalize | Namespace/contracts update only |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/OpenGL/VideoGlGeometry.cs` | `Media/S.Media.OpenGL` internal geometry helpers | Internalize | Keep vertex/index setup path |

### Avalonia adapter

| Legacy source | Target | Action | Keep/perf note |
| --- | --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.cs` | `Media/S.Media.OpenGL.Avalonia/Output/AvaloniaVideoOutput.cs` | Refactor/split | Keep output binding + clone APIs |
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.Uploads.cs` | `Media/S.Media.OpenGL` internals | Internalize | Avoid duplicate uploader path in adapter |
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/VideoGL.HUD.cs` | `Media/S.Media.OpenGL.Avalonia/Diagnostics/MediaHudOverlay.cs` | Refactor | Keep HUD rendering behavior |

### SDL3 adapter

| Legacy source | Target | Action | Keep/perf note |
| --- | --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.cs` | `Media/S.Media.OpenGL.SDL3/SDL3VideoView.cs` | Refactor/split | Keep render loop + lifecycle discipline |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.Shaders.cs` | `Media/S.Media.OpenGL.SDL3/SDL3ShaderPipeline.cs` | Adapt | Preserve shader path and draw pipeline |

## Migration Status Tracker

`Status` values use the shared legend (`Planned`, `In Progress`, `Done`, `Blocked`).

| Area | Target path | Status | Notes |
| --- | --- | --- | --- |
| Core OpenGL runtime | `Media/S.Media.OpenGL/*` | Planned | Adapt proven upload/clone internals; no class moves |
| Avalonia adapter | `Media/S.Media.OpenGL.Avalonia/*` | Planned | Keep adapter UI-only with shared core runtime |
| SDL3 adapter | `Media/S.Media.OpenGL.SDL3/*` | Planned | Preserve threaded render and embedding behavior |
| Clone error contracts | `Media/S.Media.OpenGL/API-outline.md` | Planned | Keep specific OpenGL codes; fallback policy unchanged |
| Validation gates | `Media/S.Media.OpenGL*` test matrix | Planned | Focus on clone graph determinism and perf parity |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.Packing.cs` | `Media/S.Media.OpenGL` internals | Internalize | Share core packing logic |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/VideoSDL.HUD.cs` | `Media/S.Media.OpenGL.SDL3/SDL3HudRenderer.cs` | Refactor | Keep adapter-local HUD rendering |

## Public vs Internal Split

Public (`S.Media.OpenGL*`):
- `OpenGLVideoEngine`, `OpenGLVideoOutput`, clone options/policy options, diagnostics contracts
- `AvaloniaOpenGLHostControl`, `AvaloniaVideoOutput`
- `SDL3VideoView`, `SDL3ShaderPipeline`, `SDL3HudRenderer`

Internal-only:
- GL proc/delegate loaders
- shader sources and compile cache internals
- frame packers and upload scratch buffers
- backend-specific HUD text rasterization internals

## Implementation Sequence

1. Adapt OpenGL internals (planner/uploader/converter/packing/shader helpers) into `S.Media.OpenGL` as internal types.
2. Implement `OpenGLVideoOutput` clone operations and deterministic clone error paths.
3. Implement `OpenGLVideoEngine` orchestration (`Add/Remove/Clear`, active output, clone attach/detach routing).
4. Port Avalonia adapter types and bind to `OpenGLVideoOutput` without duplicate upload logic.
5. Port SDL3 adapter types and preserve embedded/hosted window behavior.
6. Validate clone graph + diagnostics contracts in stress scenarios.

## Required Validation Gates

- One parent with 3+ clones uses one decode/upload path (no duplicate upload work).
- Attach while running respects pause budget and emits warning on exceed.
- Self-attach, cycle attach, and already-attached child return defined non-zero codes.
- Parent disposal destroys all children deterministically.
- Surface metadata updates on resize, pixel-format change, clone graph changes, and new committed frame generation.
- Adapter handle lifetime and descriptor semantics remain deterministic (`SDL3` embedded mode).

## SDL3 Extraction Checklist (`VideoSDL.cs`)

Preserve these proven behaviors while refactoring into `SDL3VideoView` + helpers:
- dedicated render thread and deterministic stop/join
- swap-interval policy mapping (vsync/off)
- planner-driven upload path (`glTexSubImage2D` + row-length aware packing where supported)
- frame-generation gating to avoid redundant full uploads
- last-frame redraw path when no new frame arrives
- HUD diagnostics toggles/counters
- source-to-output callback/queue flow with deterministic teardown
- hosted/embedded window handle support and stable descriptor tokens

## Ownership and Error Rules

- Adapter and OpenGL clone/detach operations must propagate the most specific owned failure code.
- `MixerDetachStepFailed` (`3000`) is fallback-only for orchestration paths when no specific owned code applies.
- Failure atomicity is required: failed attach/detach/remove/replace operations must not partially mutate registration state.
- Teardown completion is an event fence (no user-visible callbacks after successful stop/dispose completion).
