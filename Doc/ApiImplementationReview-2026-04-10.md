# MFPlayer API and Implementation Review (2026-04-10)

Scope:
- Core video API and scheduling path (`S.Media.Core`, `S.Media.FFmpeg`, `S.Media.Avalonia`, SDL reference path)
- Avalonia test app playback behavior for heavy content (4K60 ProRes `yuv422p10le`)
- API ergonomics, correctness, and simplification opportunities

## Findings (ordered by severity)

### 1) High - Frame ownership bug in Avalonia output (fixed)

- File: `Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs`
- Issue: The render loop disposed `frame.Value.MemoryOwner` after each draw.
- Why this is problematic:
  - `VideoMixer` intentionally retains the current frame as `last` and may return it across multiple render ticks.
  - Disposing in the output invalidates pooled frame memory while the mixer still references that frame.
  - This can cause visible instability/corruption and extra pool churn.
- Fix applied:
  - Removed output-side ownership disposal.
  - Mixer remains the owner of leader frame lifecycle (dispose on replacement/reset).

### 2) High - Redundant texture uploads in Avalonia render loop (fixed)

- Files: `Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs`, `Video/S.Media.Avalonia/AvaloniaGlRenderer.cs`
- Issue: Every render call re-uploaded full frame texture, even when the mixer returned the same held frame.
- Impact:
  - Extremely expensive at 4K (`~33 MB` per RGBA frame upload).
  - On weaker hardware this can dominate frame time and worsen slow playback/stutter.
- Fix applied:
  - Added renderer `DrawLastTexture(...)` path.
  - Output now detects unchanged frame payload (`width/height/pts/data`) and reuses existing texture instead of re-uploading.
  - Added diagnostics counters (`TextureUploads`, `TextureReuseDraws`) to validate behavior.

### 3) Medium - Mixer performed avoidable format round-trips on leader path (fixed)

- File: `Media/S.Media.Core/Video/VideoMixer.cs`
- Issue: Pull path normalized to RGBA first, then converted again to output format for non-RGBA outputs.
- Impact:
  - For BGRA leader output with BGRA decode frames, this did unnecessary conversion work and allocations.
- Fix applied:
  - Conversion is now direct `raw -> outputPixelFormat` when needed.
  - If source already matches output format, no conversion is performed.
- Validation:
  - Added regression test: `PresentNextFrame_LeaderBgraOutput_DoesNotRoundTripConvert` in `Test/S.Media.Core.Tests/VideoMixerTests.cs`.

### 4) Medium - Stale-frame drop policy too permissive for high-FPS workloads (partially fixed)

- File: `Media/S.Media.Core/Video/VideoMixer.cs`
- Issue: Previous fixed stale threshold (`90ms`) allows several outdated frames at 60fps before catch-up.
- Impact:
  - In heavy decode/render scenarios, visible lag accumulates before frame dropping engages.
- Fix applied:
  - Drop threshold is now adaptive to output frame rate (roughly 2 frame intervals, floor 30ms).
  - Improves catch-up behavior at high frame rates (e.g., 4K60).

## API usability notes

- `IVideoOutput.Open(...)` includes `title` even for embedded controls (`AvaloniaOpenGlVideoOutput` ignores it).
  - Suggested simplification (non-breaking path): keep current method, add optional backend-specific overloads/helpers in concrete outputs to reduce confusion.
- `IVideoMixer.PresentNextFrame(...)` currently conflates two concerns:
  - pacing decision, and
  - frame acquisition/conversion.
  - Suggested future refactor: extract configurable pacing policy (lead/drop tolerances) into a dedicated options/policy object.

## 4K60 ProRes (`yuv422p10le`) performance recommendations

Implemented now:
1. Reuse uploaded textures when frame is unchanged.
2. Adaptive stale-frame dropping to recover from lag earlier.
3. Reduced mixer conversion work on non-RGBA leader outputs.
4. Added bounded per-render catch-up skipping in Avalonia output when frames are significantly behind clock.
5. Added CLI tuning options in `MFPlayer.AvaloniaVideoPlayer`:
   - `--hw=<deviceType>`
   - `--sw` (force software decode)
   - `--threads=<n>`
   - `--video-buffer=<n>`
   - `--catchup-lag-ms=<n>`
   - `--max-catchup-pulls=<n>`
6. Enabled FFmpeg hardware decode probing by default (`vaapi`/`d3d11va`/`videotoolbox` fallback by platform).
7. Added sink pixel-format preference support so sinks can request non-RGBA formats when supported.
8. Added FFmpeg decoder diagnostics snapshot API with active hardware backend and per-stream hw-accel state.
9. Added libyuv-backed fast-path CPU conversions for `NV12`, `YUV420P`, and `UYVY` to `RGBA`/`BGRA` with fallback.
10. Added initial SDL3 NV12 shader rendering path (dual-texture Y/UV upload + shader conversion).
11. Extended SDL3 shader rendering to `YUV420P` (three-plane upload + shader conversion).
12. Added lightweight conversion benchmark harness project (`Test/S.Media.Core.Benchmarks`).
13. Simplified renderer maintenance by extracting shared OpenGL shader and quad resources (`GlShaderSources`).
14. Added `IAVMixer` / `AVMixer` composition facade to unify audio+video mixer usage without breaking existing APIs.
15. Added SDL3 `YUV422P10` shader path (planar 16-bit upload + shader conversion).
16. Split mixer routing diagnostics into `SameFormatPassthrough` and `RawMarkerPassthrough`.
17. Added raw passthrough marker (`PreferRawFramePassthrough`) so endpoint adapters can own conversion boundary.
18. Added `YUV422P10` full/limited range normalization toggle (`SDL3VideoOutput.Yuv422p10LimitedRange`) with runtime sample-app prompt for clip-by-clip validation.
19. Added `YUV422P10` BT.601/BT.709 matrix toggle (`SDL3VideoOutput.Yuv422p10UseBt709Matrix`) with deterministic shader-source regression checks.

Recommended next (not yet implemented):
1. Add optional decoder-side frame skipping policy under sustained lag
   - Example: skip non-key video frames when queue age exceeds a threshold.
2. Add hardware decode path selection in test app args
   - e.g. `--hw=vaapi` on Linux where available.
3. Add dynamic output scaling in test app
   - render at reduced internal resolution on weak devices while preserving window size.
4. Validate 10-bit color range/matrix handling for `YUV422P10` shader path against reference clips (BT.709/BT.2020).

## What was changed in this pass

- `Media/S.Media.Core/Video/VideoMixer.cs`
  - Adaptive stale drop threshold.
  - Direct conversion to requested output format (removed unnecessary intermediate conversions).
- `Video/S.Media.Avalonia/AvaloniaGlRenderer.cs`
  - Added `DrawLastTexture(...)` draw-only code path.
- `Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs`
  - Removed incorrect frame owner disposal.
  - Added frame-identity based upload reuse.
  - Added upload/reuse diagnostics counters.
  - Added bounded catch-up pulls per render with diagnostics (`CatchupSkips`).
- `Test/MFPlayer.AvaloniaVideoPlayer/MainWindow.cs`
  - Extended diagnostics logging with upload/reuse counters.
  - Added command-line performance tuning options for decode/output behavior.
- `Media/S.Media.FFmpeg/FFmpegDecoder.cs`
  - Added auto hardware decode probing by default with platform-specific device preference order.
  - Added diagnostics snapshot API to report selected hw backend and per-video-channel acceleration.
- `Media/S.Media.Core/Video/LibYuvRuntime.cs`
  - Added dynamic libyuv loading and conversion wrappers (`ARGBShuffle`, `NV12To*`, `I420To*`, `UYVYTo*`).
- `Media/S.Media.Core/Video/BasicPixelFormatConverter.cs`
  - Added libyuv fast paths for `NV12`/`YUV420P`/`UYVY` to `RGBA`/`BGRA` with deterministic fallback.
- `Video/S.Media.SDL3/GLRenderer.cs`
  - Added NV12 shader pipeline with separate Y/UV textures.
  - Added YUV420P shader pipeline with separate Y/U/V textures.
  - Added YUV422P10 shader pipeline with 16-bit planar uploads.
  - Added full vs limited range normalization path controlled by shader uniform (`uLimitedRange`).
- `Video/S.Media.SDL3/SDL3VideoOutput.cs`
  - Preserves NV12/YUV420P/YUV422P10 leader output formats to allow shader conversion paths.
  - Exposes `Yuv422p10LimitedRange` toggle propagated to renderer.
- `Test/S.Media.Core.Benchmarks/*`
  - Added standalone conversion throughput harness for quick local perf checks.
- `Media/S.Media.Core/Video/GlShaderSources.cs`
  - Added shared shader/geometry resources reused by SDL3 and Avalonia renderers.
- `Media/S.Media.Core/Mixing/IAVMixer.cs`, `Media/S.Media.Core/Mixing/AVMixer.cs`
  - Added AV facade that composes existing audio and video mixers for incremental migration.
- `Media/S.Media.Core/Video/IVideoSinkFormatPreference.cs`
  - Added optional sink capability interface for preferred pixel format routing.
- `Media/S.Media.Core/Video/VideoMixer.cs`
  - Updated sink path to honor optional sink preferred pixel format.
  - Added raw marker passthrough route and split route diagnostics (`SameFormatPassthrough` / `RawMarkerPassthrough`).
- `NDI/S.Media.NDI/NDIVideoSink.cs`
  - Implemented sink preference interface and BGRA/RGBA ingest support for reduced swizzle overhead.
- `Test/S.Media.Core.Tests/VideoMixerTests.cs`
  - Added regression test for no unnecessary BGRA round-trip conversion.
- `Test/S.Media.Core.Tests/BasicPixelFormatConverterTests.cs`
  - Added fallback-path tests for `NV12`/`YUV420P`/`UYVY` with libyuv disabled (deterministic black output).
  - Added guard-path tests for same-format passthrough, unsupported destination exception, and dispose safety.

