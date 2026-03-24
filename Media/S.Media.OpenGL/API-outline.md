# S.Media.OpenGL API Outline

Source of truth: `Media/S.Media.Core/PLAN.smedia-architecture.md`.

## Planned Files, Types, and API Shape

### `OpenGLVideoEngine.cs`
- `sealed class OpenGLVideoEngine : IVideoEngine, IDisposable`
- Planned API:
  - `int AddOutput(IVideoOutput output)`
  - `int RemoveOutput(IVideoOutput output)`
  - `int RemoveOutput(Guid outputId)`
  - `int ClearOutputs()`
  - `IReadOnlyList<IVideoOutput> Outputs { get; }`
  - `int SetActiveOutput(Guid outputId)`
  - `Guid? ActiveOutputId { get; }`
  - `int PushFrame(VideoFrame frame, TimeSpan presentationTime)`
  - `int CreateCloneOutput(Guid parentOutputId, in OpenGLCloneOptions options, out IVideoOutput? cloneOutput)`
  - `int AttachCloneOutput(Guid parentOutputId, Guid cloneOutputId)`
  - `int DetachCloneOutput(Guid parentOutputId, Guid cloneOutputId)`

### `OpenGLVideoOutput.cs`
- `sealed class OpenGLVideoOutput : IVideoOutput, IDisposable`
- Planned API:
  - `int Initialize(VideoOutputConfig config)`
  - `int Start()`
  - `int Stop()`
  - `int PushFrame(VideoFrame frame, TimeSpan presentationTime)`
  - `int Resize(int width, int height)`
  - `Guid Id { get; }`
  - `bool IsClone { get; }`
  - `Guid? CloneParentOutputId { get; }`
  - `int CreateClone(in OpenGLCloneOptions options, out OpenGLVideoOutput? cloneOutput)`
  - `int AttachClone(OpenGLVideoOutput cloneOutput, in OpenGLCloneOptions options)`
  - `int DetachClone(Guid cloneOutputId)`
  - `IReadOnlyList<Guid> CloneOutputIds { get; }`
  - `long LastPresentedFrameGeneration { get; }`
  - `OpenGLSurfaceMetadata Surface { get; }`

### `Output/OpenGLCloneOptions.cs`
- `sealed record OpenGLCloneOptions`
- Planned API:
  - `OpenGLCloneMode Mode { get; init; }`
  - `bool AutoResizeToParent { get; init; }`
  - `bool ShareParentColorPipeline { get; init; }`
  - `bool FailIfContextSharingUnavailable { get; init; }`
  - `OpenGLHUDCloneMode HudMode { get; init; }`
  - `int? MaxCloneDepth { get; init; } // null uses engine default`
  - `OpenGLClonePixelFormatPolicy PixelFormatPolicy { get; init; }`

### `Output/OpenGLClonePolicyOptions.cs`
- `sealed record OpenGLClonePolicyOptions`
- Planned API:
  - `int MaxCloneDepth { get; init; } // default: 4`
  - `bool RejectSelfAttach { get; init; }`
  - `bool RejectCycles { get; init; }`
  - `OpenGLClonePixelFormatPolicy DefaultPixelFormatPolicy { get; init; }`
  - `bool AllowAttachWhileRunning { get; init; }`
  - `int AttachPauseBudgetFrames { get; init; } // default: 1, performance-first`
  - `bool WarnOnPauseBudgetExceeded { get; init; } // default: true`

### `Output/OpenGLClonePixelFormatPolicy.cs`
- `enum OpenGLClonePixelFormatPolicy`
- Planned API:
  - `RequireCompatibleFastPath = 0`
  - `AllowGpuConversion = 1`

### `Output/OpenGLSurfaceMetadata.cs`
- `readonly record struct OpenGLSurfaceMetadata`
- Planned API:
  - `int SurfaceWidth { get; }`
  - `int SurfaceHeight { get; }`
  - `int RenderWidth { get; }`
  - `int RenderHeight { get; }`
  - `VideoPixelFormat PixelFormat { get; }`
  - `int PlaneCount { get; }`
  - `IReadOnlyList<int> PlaneStrides { get; }`
  - `long LastPresentedFrameGeneration { get; }`

### `Output/OpenGLCloneMode.cs`
- `enum OpenGLCloneMode`
- Planned API:
  - `SharedTexture = 0`
  - `SharedFboBlit = 1`
  - `CopyFallback = 2`

### `Output/OpenGLHUDCloneMode.cs`
- `enum OpenGLHUDCloneMode`
- Planned API:
  - `Independent = 0`
  - `InheritParent = 1`

### `Upload/UploadPlan.cs`
- `readonly record struct UploadPlan`
- Planned API:
  - `VideoPixelFormat PixelFormat { get; }`
  - `OpenGLCloneMode PreferredPath { get; }`
  - `bool RequiresGpuConversion { get; }`

### `Upload/OpenGLUploadPlanner.cs`
- `sealed class OpenGLUploadPlanner`
- Planned API:
  - `int UpdateCapabilities(in OpenGLCapabilitySnapshot capabilities)`
  - `UploadPlan CreatePlan(VideoFrame frame)`
  - `bool Supports(VideoPixelFormat pixelFormat)`

### `Upload/OpenGLTextureUploader.cs`
- `sealed class OpenGLTextureUploader`
- Planned API:
  - `int Upload(VideoFrame frame, UploadPlan plan)`
  - `int Reset()`
  - `long LastUploadGeneration { get; }`

### `Conversion/YuvToRgbaConverter.cs`
- `sealed class YuvToRgbaConverter`
- Planned API:
  - `int Convert(VideoFrame source, Span<byte> rgbaDestination, out int bytesWritten)`

### `Diagnostics/OpenGLOutputDiagnostics.cs`
- `readonly record struct OpenGLOutputDiagnostics`
- Planned API:
  - `long FramesPresented { get; }`
  - `long FramesDropped { get; }`
  - `long FramesCloned { get; }`
  - `double LastUploadMs { get; }`
  - `double LastPresentMs { get; }`
  - `OpenGLSurfaceMetadata Surface { get; }`

### `Diagnostics/OpenGLCloneGraphChangedEventArgs.cs`
- `sealed class OpenGLCloneGraphChangedEventArgs : EventArgs`
- Planned API:
  - `Guid ParentOutputId { get; }`
  - `Guid CloneOutputId { get; }`
  - `OpenGLCloneGraphChangeKind ChangeKind { get; }`

### `Diagnostics/OpenGLCloneGraphChangeKind.cs`
- `enum OpenGLCloneGraphChangeKind`
- Planned API:
  - `Attached = 0`
  - `Detached = 1`
  - `Destroyed = 2`

### `Diagnostics/OpenGLDiagnosticsSnapshotEventArgs.cs`
- `sealed class OpenGLDiagnosticsSnapshotEventArgs : EventArgs`
- Planned API:
  - `Guid OutputId { get; }`
  - `OpenGLOutputDiagnostics Snapshot { get; }`

### `Diagnostics/OpenGLDiagnosticsEvents.cs`
- `sealed class OpenGLDiagnosticsEvents`
- Planned API:
  - `event EventHandler<OpenGLSurfaceMetadata>? SurfaceChanged`
  - `event EventHandler<OpenGLCloneGraphChangedEventArgs>? CloneGraphChanged`
  - `event EventHandler<OpenGLDiagnosticsSnapshotEventArgs>? DiagnosticsUpdated`

### `Diagnostics/OpenGLCapabilitySnapshot.cs`
- `readonly record struct OpenGLCapabilitySnapshot`
- Planned API:
  - `bool SupportsTextureSharing { get; }`
  - `bool SupportsFboBlit { get; }`
  - `int MaxTextureSize { get; }`
  - `bool SupportsPersistentMappedBuffers { get; }`

## Notes
- Keep decode/session logic out of this project.
- Migration implementation matrix/source mapping: `Media/S.Media.OpenGL/opengl-migration-plan.md`.
- Error-code range/chunk ownership is defined by `MediaErrorAllocations` in `Media/S.Media.Core/Errors/MediaErrorAllocations.cs` and tracked in `Media/S.Media.Core/error-codes.md`.
- For detach/clone operations, OpenGL-specific clone failure codes remain authoritative when available; Core fallback `MixerDetachStepFailed` (`3000`) applies only when no more specific owned code exists in orchestration paths.
- Clone graph mutation ownership is engine-first (`OpenGLVideoEngine` is canonical for cross-output attach/detach); output-level clone methods are local convenience wrappers that delegate to engine policy.
- Parent output performs decode/upload path once; clones must not trigger additional decode or upload work.
- Clone success rule: a clone presents the same committed frame generation as its parent.
- `Stop()` is idempotent and returns `MediaResult.Success` when already stopped.
- `PushFrame` and render-path logging are trace-level only on hot paths.
- Clone attach/detach must be thread-safe; GL mutations execute on the owning render thread.
- Clone attach while running is supported; implementation may pause render for up to `AttachPauseBudgetFrames` and then continue best-effort with warning if exceeded.
- Parent disposal destroys all children clones deterministically.
- Clone/attach/detach failures must always return defined non-zero error codes (no silent fallback).
- Attaching a clone already attached to another parent must fail with a defined error code.
- Clone graphs must be acyclic; cycle detection is required on attach/create paths.
- Self-attach is explicitly rejected with a dedicated error code.
- `DetachClone` on a non-child target returns `OpenGLCloneNotAttached`.
- Failure atomicity: failed clone attach/detach/remove paths must not partially mutate clone-graph registration state.
- Effective max clone depth is configurable; attach/create beyond depth limit must fail with a defined error code.
- Default max clone depth is `4` unless overridden by policy/options.
- Pixel-format compatibility is performance-first by default (`RequireCompatibleFastPath`); incompatible parent/child formats fail with a defined error code.
- `AllowGpuConversion` is only permitted when a shared-context GPU fast path is available; copy-fallback conversion is not used by default.
- In-frame HUD should be clone-independent by default (`OpenGLHUDCloneMode.Independent`).
- Diagnostics/surface metadata refresh is on-change (new committed frame generation, clone graph changes, resize, pixel-format change, HUD state change).
- Disposal order is deterministic: stop ingress -> detach clone graph -> stop render loop -> release GL/HUD resources on owning context -> unregister output/engine links -> clear diagnostics state.
- Callback/event dispatch policy is fixed in this phase (no module-level callback-dispatch configuration surface).
- Future evolution note: if callback latency becomes a verified issue, add a minimal dispatcher later without breaking diagnostics/clone-graph event ordering or teardown-fence guarantees.

## Initial OpenGL Clone Error Code Picks (`4400-4499`)
- `4400`: `OpenGLCloneParentNotFound`
- `4401`: `OpenGLCloneAlreadyAttached`
- `4402`: `OpenGLCloneNotAttached`
- `4403`: `OpenGLCloneContextShareUnavailable`
- `4404`: `OpenGLCloneCreationFailed`
- `4405`: `OpenGLCloneAttachFailed`
- `4406`: `OpenGLCloneDetachFailed`
- `4407`: `OpenGLCloneParentDisposed`
- `4408`: `OpenGLCloneChildDestroyed`
- `4409`: `OpenGLCloneCycleDetected`
- `4410`: `OpenGLCloneChildAlreadyAttached`
- `4411`: `OpenGLCloneSelfAttachRejected`
- `4412`: `OpenGLCloneMaxDepthExceeded`
- `4413`: `OpenGLClonePixelFormatIncompatible`
- `4414`: `OpenGLCloneParentNotInitialized`

## OpenGL Contract Test Matrix (Minimum)
- Lifecycle idempotency: repeated `Stop()` returns `MediaResult.Success` for already-stopped outputs/engine paths.
- Clone graph safety: self-attach, cycle creation, and already-attached child paths fail deterministically with defined OpenGL clone codes.
- Detach semantics: detach on non-child returns `OpenGLCloneNotAttached`; failed detach/attach paths do not partially mutate clone-graph registration state.
- Running attach behavior: attach-while-running respects `AttachPauseBudgetFrames` policy and continues best-effort with warning when budget is exceeded.
- Generation parity: parent and clones present identical committed frame generation for successful clone-present paths.
- Teardown fence: no clone/diagnostics events are emitted after successful stop/dispose completion.

