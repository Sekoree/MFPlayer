# S.Media.OpenGL.SDL3 API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `SDL3VideoView.cs`
- `sealed class SDL3VideoView : IVideoOutput, IDisposable`
- Planned API:
  - `int Initialize(SDL3VideoViewOptions options)`
  - `int InitializeEmbedded(nint parentHandle, int width, int height)`
  - `int PushFrame(VideoFrame frame, TimeSpan presentationTime)`
  - `int Start()`
  - `int Stop()`
  - `int Resize(int width, int height)`
  - `nint GetPlatformWindowHandle()`
  - `string GetPlatformHandleDescriptor()`
  - `int TryGetPlatformWindowHandle(out nint handle)`
  - `int TryGetPlatformHandleDescriptor(out string descriptor)`
  - `Guid Id { get; }`
  - `bool IsClone { get; }`
  - `Guid? CloneParentOutputId { get; }`
  - `int CreateClone(in SDL3CloneOptions options, out SDL3VideoView? cloneView)`
  - `int AttachClone(SDL3VideoView cloneView, in SDL3CloneOptions options)`
  - `int DetachClone(Guid cloneViewId)`

### `SDL3HudRenderer.cs`
- `sealed class SDL3HudRenderer`
- Planned API:
  - `int Update(DebugInfo debugInfo)`
  - `int Render()`

### `SDL3ShaderPipeline.cs`
- `sealed class SDL3ShaderPipeline : IDisposable`
- Planned API:
  - `int EnsureInitialized()`
  - `int Upload(VideoFrame frame)`
  - `int Draw()`

### `SDL3CloneOptions.cs`
- `sealed record SDL3CloneOptions`
- Planned API:
  - `OpenGLCloneMode CloneMode { get; init; }`
  - `bool AutoTrackParentSize { get; init; }`
  - `OpenGLHUDCloneMode HudMode { get; init; }`
  - `bool FailIfParentWindowClosed { get; init; }`
  - `int? MaxCloneDepth { get; init; } // null uses OpenGL engine/output policy`

### `Diagnostics/SDL3OutputDiagnostics.cs`
- `readonly record struct SDL3OutputDiagnostics`
- Planned API:
  - `long FramesPresented { get; }`
  - `long FramesCloned { get; }`
  - `long FramesDropped { get; }`
  - `double LastPresentMs { get; }`
  - `OpenGLSurfaceMetadata Surface { get; }`

## Notes
- Keep SDL3-specific rendering concerns contained in this project.
- Migration implementation matrix/source mapping: `Media/S.Media.OpenGL/opengl-migration-plan.md`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Media/S.Media.Core/error-codes.md`.
- For adapter detach/clone operations, propagate specific OpenGL/SDL3 failure codes; use Core fallback `MixerDetachStepFailed` (`3000`) only when no more specific owned code exists in orchestration paths.
- Clone views should present parent frame generations without duplicating decode/upload work.
- SDL3 window/context lifecycle changes must destroy child clones deterministically with non-zero error codes.
- Attach clone operations must fail when the child is already attached to another parent.
- Clone cycle creation must be rejected via OpenGL clone cycle-detection error paths.
- In-frame HUD state should be clone-independent by default; inheritance is opt-in via clone options.
- Embedding support is first-class: expose native window handle + descriptor APIs for host framework integration.
- Platform window-handle access is state-bound: valid only after successful `Initialize*` and before `Dispose`.
- Thread-affinity requirements are platform-specific; where unrestricted access is unsafe, return a defined non-zero error code.
- Handle descriptor must use stable, fixed platform tokens (for example `x11-window`, `wayland-surface`, `win32-hwnd`, `cocoa-nsview`) so hosts can route interop correctly.
- Safest descriptor contract: return only fixed known tokens; unknown/unsupported descriptor paths return non-zero error code.
- If embedded parent host is destroyed unexpectedly, return a defined non-zero error code and perform deterministic teardown.
- Failure atomicity: failed adapter attach/detach/embed lifecycle paths must not partially mutate clone/output registration state.
- Callback/event dispatch policy is fixed in this phase (no adapter-level callback-dispatch configuration surface).
- Future evolution note: if callback latency becomes a verified issue, add a minimal dispatcher later without breaking adapter event ordering or teardown-fence guarantees.

## Initial SDL3 Embedding Error Code Picks (`4460-4479`)
- `4460`: `SDL3EmbedNotInitialized`
- `4461`: `SDL3EmbedInvalidParentHandle`
- `4462`: `SDL3EmbedParentLost`
- `4463`: `SDL3EmbedHandleUnavailable`
- `4464`: `SDL3EmbedDescriptorUnavailable`
- `4465`: `SDL3EmbedThreadAffinityViolation`
- `4466`: `SDL3EmbedUnsupportedDescriptor`
- `4467`: `SDL3EmbedInitializeFailed`
- `4468`: `SDL3EmbedTeardownFailed`

## SDL3 Contract Test Matrix (Minimum)
- Embed lifecycle: `InitializeEmbedded(...)` validates handle/descriptor paths deterministically with defined SDL3 embed codes.
- Handle access contract: handle/descriptor getters are state-bound and fail deterministically outside valid lifetime windows.
- Clone adapter behavior: attach/detach delegates to OpenGL clone policy and surfaces deterministic clone error codes.
- Parent-loss behavior: unexpected embedded parent loss returns deterministic non-zero code and triggers deterministic teardown.
- Failure atomicity: failed attach/detach/embed operations do not partially mutate clone/output registration state.
- Teardown fence: no adapter-visible callbacks/events after successful `Stop()`/`Dispose()` completion.

