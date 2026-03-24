# S.Media.OpenGL.Avalonia API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `Controls/AvaloniaOpenGLHostControl.cs`
- `sealed class AvaloniaOpenGLHostControl : OpenGLControlBase`
- Planned API:
  - `AvaloniaOpenGLHostControl(OpenGLVideoOutput output)`
  - `OpenGLVideoOutput Output { get; }`
  - `int ReplaceOutput(OpenGLVideoOutput output)`
  - `OpenGLSurfaceMetadata Surface { get; }`

### `Output/AvaloniaVideoOutput.cs`
- `sealed class AvaloniaVideoOutput : OpenGLVideoOutput`
- Planned API:
  - `int CreateClone(in AvaloniaCloneOptions options, out AvaloniaVideoOutput? cloneOutput)`
  - `int AttachClone(AvaloniaVideoOutput cloneOutput, in AvaloniaCloneOptions options)`
  - `int DetachClone(Guid cloneOutputId)`
  - `bool IsClone { get; }`
  - `Guid? CloneParentOutputId { get; }`

### `Output/AvaloniaCloneOptions.cs`
- `sealed record AvaloniaCloneOptions`
- Planned API:
  - `OpenGLCloneMode CloneMode { get; init; }`
  - `bool AutoTrackParentSize { get; init; }`
  - `OpenGLHUDCloneMode HudMode { get; init; }`
  - `bool FailIfParentDisposed { get; init; }`
  - `int? MaxCloneDepth { get; init; } // null uses OpenGL engine/output policy`

### `Diagnostics/MediaHudOverlay.cs`
- `sealed class MediaHudOverlay`
- Planned API:
  - `void Update(DebugInfo debugInfo)`
  - `void Render(DrawingContext context, Rect bounds)`

### `Diagnostics/AvaloniaOutputDiagnostics.cs`
- `readonly record struct AvaloniaOutputDiagnostics`
- Planned API:
  - `long FramesPresented { get; }`
  - `long FramesCloned { get; }`
  - `bool IsCloneActive { get; }`
  - `OpenGLSurfaceMetadata Surface { get; }`

## Notes
- This project stays a UI adapter layer only.
- Migration implementation matrix/source mapping: `Media/S.Media.OpenGL/opengl-migration-plan.md`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Media/S.Media.Core/error-codes.md`.
- For adapter detach/clone operations, propagate specific OpenGL clone failure codes; use Core fallback `MixerDetachStepFailed` (`3000`) only when no more specific owned code exists in orchestration paths.
- `OpenGLControlBase` is a required dependency for the canonical Avalonia host path.
- Dependency policy: base `Avalonia` NuGet package only for this adapter surface.
- Clone controls/outputs must reflect the parent surface and frame generation without duplicating decode/upload work.
- Parent output ownership remains in `S.Media.OpenGL`; Avalonia adapters project UI lifecycle onto that output.
- Parent disposal destroys all clone controls/outputs derived from that parent output.
- Attach clone operations must fail when the child is already attached to another parent.
- Clone cycle creation must be rejected via OpenGL clone cycle-detection error paths.
- In-frame HUD state should be clone-independent by default; inheritance is opt-in via clone options.
- Preferred host path is direct `OpenGLControlBase` usage (`AvaloniaOpenGLHostControl`) with constructor-injected output.
- Failure atomicity: failed adapter attach/detach/replace-output paths must not partially mutate adapter-visible clone/output state.
- Callback/event dispatch policy is fixed in this phase (no adapter-level callback-dispatch configuration surface).
- Future evolution note: if callback latency becomes a verified issue, add a minimal dispatcher later without breaking adapter event ordering or teardown-fence guarantees.

## Avalonia Adapter Error Code Policy
- Adapter clone/detach failures reuse canonical OpenGL clone codes from `S.Media.OpenGL` in this phase.

## Avalonia Contract Test Matrix (Minimum)
- Control/output replacement: `ReplaceOutput(...)` succeeds/fails deterministically and preserves adapter state on failure.
- Clone adapter behavior: adapter attach/detach delegates to OpenGL clone policy and surfaces the same deterministic clone error codes.
- Parent lifecycle projection: parent disposal deterministically invalidates derived clone controls/outputs.
- Failure atomicity: failed adapter attach/detach/replace paths do not partially mutate adapter-visible state.
- Teardown fence: no adapter-level callbacks/events after successful control/output disposal completion.

