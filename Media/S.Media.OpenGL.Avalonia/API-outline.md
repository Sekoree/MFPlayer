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
- `OpenGLControlBase` is a required dependency for the canonical Avalonia host path.
- Dependency policy: base `Avalonia` NuGet package only for this adapter surface.
- Clone controls/outputs must reflect the parent surface and frame generation without duplicating decode/upload work.
- Parent output ownership remains in `S.Media.OpenGL`; Avalonia adapters project UI lifecycle onto that output.
- Parent disposal destroys all clone controls/outputs derived from that parent output.
- Attach clone operations must fail when the child is already attached to another parent.
- Clone cycle creation must be rejected via OpenGL clone cycle-detection error paths.
- In-frame HUD state should be clone-independent by default; inheritance is opt-in via clone options.
- Preferred host path is direct `OpenGLControlBase` usage (`AvaloniaOpenGLHostControl`) with constructor-injected output.

