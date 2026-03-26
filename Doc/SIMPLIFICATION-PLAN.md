# S.Media API Simplification Plan

> **Master tracking file** — this is the single source of truth for the ongoing simplification work.
> Older per-module migration plans (`ffmpeg-migration-plan.md`, `opengl-migration-plan.md`, etc.) cover the original hard-cut migration and are still valid for reference but are **not** the active work plan.

Last updated: 2026-03-26 (All 7 steps complete ✅)

## Goal

Simplify the S.Media consumer API by internalizing drift/sync internals, removing orphaned sub-mixers, lifting output management into `IAudioVideoMixer`, fixing the non-functional Avalonia GL renderer, removing FFmpeg placeholder fallbacks, implementing stub events, and flattening NDI/OpenGL configs — all while preserving the `engine → source → mixer → output` architecture.

## Implementation Order

`1 → 2 → 3 → 5 → 6 → 4 → 7`

---

### Step 1 — Internalize drift & absorb runtime into mixer ✅ DONE

- [x] Made `AudioVideoMixerRuntime` and `AudioVideoMixerRuntimeOptions` internal
- [x] Created public `AudioVideoMixerConfig` with only consumer fields + `ToRuntimeOptions()` bridge
- [x] Hardcoded 6 drift params as internal defaults
- [x] Renamed `AudioVideoMixerRuntimeSnapshot` → `AudioVideoMixerDebugInfo`
- [x] Made `VideoPresenterSyncPolicy`, `VideoPresenterSyncPolicyOptions`, `VideoPresenterSyncDecision` all internal
- [x] Added `InternalsVisibleTo` for test project in `S.Media.Core.csproj`
- [x] Updated `NdiVideoReceive/Program.cs` (removed 6 drift CLI options, uses new `AudioVideoMixerConfig`)
- [x] Added `StartPlayback(AudioVideoMixerConfig)`, `StopPlayback()`, `TickVideoPresentation()`, `GetDebugInfo()` to `AudioVideoMixer`

### Step 2 — Remove orphaned sub-mixers, lift output management ✅ DONE

- [x] Removed `IAudioMixer AudioMixer` and `IVideoMixer VideoMixer` from `IAudioVideoMixer`
- [x] Removed orphaned sub-mixer instance creation from `AudioVideoMixer`
- [x] Lifted `AddAudioOutput`/`RemoveAudioOutput`/`AddVideoOutput`/`RemoveVideoOutput` + collections into `IAudioVideoMixer`
- [x] Simplified `IMediaPlayer` to just `Play(IMediaItem)` + output forwarding
- [x] Updated `FakeMixer` in `MediaPlayerCompositionTests.cs`
- [x] Updated `NdiVideoReceive/Program.cs` (removed orphaned sub-mixer calls)

### Step 3 — Implement stub events in all mixers ✅ DONE

- [x] Replaced empty `add { } remove { }` event stubs with real backing delegates
- [x] `AudioMixer`: `SourceError` + `DropoutDetected` with `internal Raise*()` helpers
- [x] `VideoMixer`: `SourceError` with `internal RaiseSourceError()` helper
- [x] `AudioVideoMixer`: `AudioSourceError` + `VideoSourceError` with `internal Raise*()` helpers

### Step 4 — Fix Avalonia OpenGL rendering ✅ DONE

- [x] Created `AvaloniaGLRenderer` internal class with full shader/texture/upload/draw pipeline
- [x] Ported dual-profile shaders (OpenGL 3.3 core + ES 3.0) for RGBA and multi-format YUV→RGB
- [x] Supports all 11 VideoPixelFormat values including 10-bit formats via GL_R16/GL_RG16
- [x] Wired renderer into `AvaloniaOpenGLHostControl.OnOpenGlInit/OnOpenGlRender/OnOpenGlDeinit`
- [x] Added `PushFrame(VideoFrame)`, `KeepAspectRatio` property
- [x] Removed placeholder `_ = fb; _ = gl;` pattern
- [x] Build & tests: 0 failures, 257 passed

### Step 5 — Remove FFmpeg placeholder fallbacks ✅ DONE

- [x] Removed `Placeholder*` constants and `CreateSynthetic*` methods from `FFPacketReader`, `FFAudioDecoder`, `FFVideoDecoder`
- [x] Changed fallback paths to return proper `MediaErrorCode` instead of synthetic data
- [x] Updated `FFPixelConverter` — return error instead of placeholder phase; removed `ResolveFallbackMappedFormat`
- [x] Updated `FFResampler` — return error instead of placeholder phase
- [x] Rewrote `FFDecoderInternalsTests.cs` (7 tests → expect errors)
- [x] Updated `FFMediaItemTests.cs` and `FFSharedDecodeContextTests.cs` (renamed tests)
- [x] Updated `FFPixelConverterTests.cs` (placeholder tests → expect errors; inferred format test)
- [x] Updated `FFResamplerTests.cs` (3 tests → expect errors)
- [x] Updated session-backed source tests (`FFVideoSourceTests`, `FFAudioSourceTests`, `FFSharedDemuxSessionTests`)
- [x] Updated integration tests (`FFPortAudioIntegrationTests`, `MediaPlayerPortAudioIntegrationTests`)
- [x] Build & run full test suite: 0 failures, 257 passed, 4 skipped

### Step 6 — Flatten NDI config, simplify OpenGL clone options, rename diagnostics ✅ DONE

- [x] `NDISourceOptions`: replaced 5 nullable `*Override` fields with concrete defaults, removed `Resolve*()` methods
- [x] Removed `EnableExternalClockCorrection` from `NDIIntegrationOptions`
- [x] `NDIDiagnosticsOptions`: made `MaxReadPauseForDiagnostics` and `PublishSnapshotsOnRequestOnly` internal
- [x] Split `NDIVideoDiagnostics` → `NDIVideoSourceDebugInfo` + `NDIVideoOutputDebugInfo`
- [x] `OpenGLClonePolicyOptions`: kept only `MaxCloneDepth` public; made 6 others internal
- [x] `OpenGLCloneOptions`: made `AutoResizeToParent`, `ShareParentColorPipeline`, `FailIfContextSharingUnavailable` internal
- [x] Renamed `OpenGLOutputDiagnostics` → `OpenGLOutputDebugInfo`
- [x] Renamed `AvaloniaOutputDiagnostics` → `AvaloniaOutputDebugInfo`
- [x] Renamed `SDL3OutputDiagnostics` → `SDL3OutputDebugInfo`
- [x] Added XML doc to `AudioOutputConfig` explaining forward-compat placeholder
- [x] Added `InternalsVisibleTo` for NDI, OpenGL, Avalonia, SDL3 test/adapter projects
- [x] Updated all affected tests: NDIEngineAndOptionsTests, NDISourceAndMediaItemTests, OpenGLDiagnosticsContractsTests
- [x] Updated NdiVideoReceive test app
- [x] Build & run: 0 failures, 257 passed, 4 skipped

### Step 7 — Convenience factories, SDL3 stub, tests & docs ✅ DONE

- [x] Add `FFMediaItem.TryOpen(string uri, out FFMediaItem? item)` + throwing `Open(string uri)` + `Open(Stream stream)` factories
- [x] Add `FFMediaItem.TryOpen(Stream?, out FFMediaItem?)` factory
- [x] Implement `SDL3ShaderPipeline` Upload/Draw — full GL rendering for embedded use-case (all 11 pixel formats)
- [x] Update all affected tests across test projects (7 new factory tests)
- [x] Update `IMPLEMENTATION-STATUS.md`, all `PROJECT-STATUS.md` files, and `Doc/` markdown

---

## Design Decisions

| Decision | Choice | Rationale |
| --- | --- | --- |
| `FFMediaItem.Open()` without native FFmpeg | Return error code (`TryOpen` pattern) | Consistent with int-first API; throwing `Open()` added as convenience |
| FFmpeg placeholders | Removed entirely | Not kept for testability; tests now assert error codes |
| Avalonia rendering | Port old `VideoGL.cs` shader/upload logic | Proven path, handles all 11 `VideoPixelFormat` values |
| `AudioOutputConfig` empty class | Keep with XML doc comment | Forward-compat placeholder for future audio output config |
| Heavy pixel formats | Preserve native multi-plane passthrough (YUV420P, NV12, P010LE) | Conversion-free path for performance-critical formats |

## Files Modified (cumulative)

### S.Media.Core
- `Mixing/AudioVideoMixerRuntimeOptions.cs` — made internal
- `Mixing/AudioVideoMixerConfig.cs` — **NEW** public consumer config
- `Mixing/AudioVideoMixerRuntimeSnapshot.cs` — renamed record to `AudioVideoMixerDebugInfo`
- `Mixing/AudioVideoMixerRuntime.cs` — made internal
- `Mixing/VideoPresenterSyncPolicy.cs` — made internal
- `Mixing/VideoPresenterSyncPolicyOptions.cs` — made internal
- `Mixing/VideoPresenterSyncDecision.cs` — made internal
- `Mixing/IAudioVideoMixer.cs` — removed sub-mixer props, added output management
- `Mixing/AudioVideoMixer.cs` — new lifecycle methods, real events, output lists
- `Mixing/AudioMixer.cs` — real events with Raise helpers
- `Mixing/VideoMixer.cs` — real events with Raise helpers
- `Playback/IMediaPlayer.cs` — simplified
- `Playback/MediaPlayer.cs` — delegates to mixer
- `S.Media.Core.csproj` — InternalsVisibleTo

### S.Media.FFmpeg
- `Decoders/Internal/FFPacketReader.cs` — removed placeholders
- `Decoders/Internal/FFAudioDecoder.cs` — removed placeholders
- `Decoders/Internal/FFVideoDecoder.cs` — removed placeholders
- `Decoders/Internal/FFPixelConverter.cs` — removed placeholder phase + fallback method
- `Decoders/Internal/FFResampler.cs` — removed placeholder phase

### Tests
- `S.Media.Core.Tests/MediaPlayerCompositionTests.cs` — updated FakeMixer
- `S.Media.FFmpeg.Tests/FFDecoderInternalsTests.cs` — rewritten for error expectations
- `S.Media.FFmpeg.Tests/FFMediaItemTests.cs` — renamed test
- `S.Media.FFmpeg.Tests/FFSharedDecodeContextTests.cs` — renamed test
- `S.Media.FFmpeg.Tests/FFPixelConverterTests.cs` — updated placeholder tests

### Test Apps
- `Test/NdiVideoReceive/Program.cs` — simplified to new API, flat NDI source options, VideoSource debug info

### S.Media.NDI (Step 6)
- `Config/NDISourceOptions.cs` — replaced 5 nullable Override fields with concrete defaults, removed Resolve methods
- `Config/NDIIntegrationOptions.cs` — removed EnableExternalClockCorrection
- `Diagnostics/NDIDiagnosticsOptions.cs` — made 2 fields internal
- `Diagnostics/NDIVideoDiagnostics.cs` — split into NDIVideoSourceDebugInfo + NDIVideoOutputDebugInfo
- `Diagnostics/NDIEngineDiagnostics.cs` — updated to use split types
- `Runtime/NDIEngine.cs` — simplified source creation (no Resolve calls), split diagnostics
- `Input/NDIVideoSource.cs` — flat field names
- `Input/NDIAudioSource.cs` — flat field names
- `Output/NDIVideoOutput.cs` — NDIVideoOutputDebugInfo
- `S.Media.NDI.csproj` — InternalsVisibleTo

### S.Media.OpenGL (Step 6)
- `Output/OpenGLClonePolicyOptions.cs` — 6 fields made internal
- `Output/OpenGLCloneOptions.cs` — 3 fields made internal
- `Diagnostics/OpenGLOutputDiagnostics.cs` — renamed to OpenGLOutputDebugInfo
- `Diagnostics/OpenGLDiagnosticsSnapshotEventArgs.cs` — uses OpenGLOutputDebugInfo
- `Diagnostics/OpenGLDiagnosticsEvents.cs` — uses OpenGLOutputDebugInfo
- `OpenGLVideoOutput.cs` — uses OpenGLOutputDebugInfo
- `S.Media.OpenGL.csproj` — InternalsVisibleTo

### S.Media.OpenGL.Avalonia (Step 6)
- `Diagnostics/AvaloniaOutputDiagnostics.cs` — renamed to AvaloniaOutputDebugInfo

### S.Media.OpenGL.SDL3 (Step 6)
- `Diagnostics/SDL3OutputDiagnostics.cs` — renamed to SDL3OutputDebugInfo

### S.Media.Core (Step 6)
- `Audio/AudioOutputConfig.cs` — added XML doc comment

### S.Media.FFmpeg (Step 7)
- `Media/FFMediaItem.cs` — added `Open(uri)`, `Open(stream)`, `TryOpen(uri)`, `TryOpen(stream)` static factories

### S.Media.OpenGL.SDL3 (Step 7)
- `SDL3ShaderPipeline.cs` — full GL rendering: shader programs, VAO/VBO, texture management, RGBA/YUV upload, draw

### Tests (Step 7)
- `S.Media.FFmpeg.Tests/FFMediaItemTests.cs` — 7 new convenience factory tests

