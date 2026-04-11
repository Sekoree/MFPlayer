# Video Acceleration Plan (2026-04-10)

Goal:
- Improve real-world playback for heavy sources (4K60 ProRes and similar) across weaker hardware.
- Reduce CPU conversion overhead and unnecessary memory movement.
- Keep API ergonomics simple: hardware decode should work automatically without required manual device config.

## Progress Checklist

### Phase 0 - Baseline Stabilization (done)
- [x] Fix frame ownership lifetime bug in `AvaloniaOpenGlVideoOutput`.
- [x] Avoid redundant texture uploads when the frame is unchanged.
- [x] Add bounded render catch-up skips.
- [x] Make stale-frame dropping adaptive to output frame rate.
- [x] Add diagnostics counters and test-app visibility (`up`, `reuse`, `catchup`).

### Phase 1 - Automatic Hardware Decode (in progress)
- [x] Add auto hardware decode preference in `FFmpegDecoderOptions` (`PreferHardwareDecoding`).
- [x] Auto-probe device types by OS and FFmpeg runtime capabilities.
- [x] Keep `HardwareDeviceType` as optional advanced override only.
- [x] Add `--sw` test-app escape hatch for deterministic software-only testing.
- [x] Add decoder diagnostics snapshot to expose active hw backend and whether hw accel is active per stream.

### Phase 2 - Multi-Sink Format Efficiency (in progress)
- [x] Add sink format preference interface (`IVideoSinkFormatPreference`).
- [x] Make `VideoMixer` honor sink preferred format.
- [x] Update `NDIVideoSink` to accept preferred `Bgra32`/`Rgba32` and map to matching NDI FourCC.
- [x] Add capability negotiation for multiple acceptable formats (primary + fallback list).
- [x] Add per-sink conversion counters (requested format hit/miss).

### Phase 3 - CPU Conversion Backend (libyuv path) (planned)
- [~] Add optional libyuv runtime integration and wrappers (initial baseline in `S.Media.Core.Video.LibYuvRuntime`).
- [~] Implement fast-path converters: `NV12->RGBA/BGRA`, `I420->RGBA/BGRA`, `UYVY->RGBA/BGRA`.
- [~] Add runtime availability detection and fallback to current managed/basic converter.
- [~] Add benchmark harness comparing current conversion path vs libyuv.
  - [x] Added lightweight harness project: `Test/S.Media.Core.Benchmarks`.
  - [x] Added managed-vs-libyuv comparison mode in harness output.

### Phase 4 - GPU YUV Shader Path (planned)
- [~] Add YUV texture upload path in renderer (start with `NV12`).
- [~] Add shader conversion for `NV12` -> RGB in renderer.
- [~] Extend to `YUV420P` and `YUV422P10`.
- [~] Add pixel-format routing policy: prefer shader-compatible formats for local outputs.

### Phase 5 - Zero-Copy / Advanced Interop (large undertaking)
- [ ] Investigate hw-frame interop path (platform-specific) to avoid CPU round-trip.
- [ ] Validate synchronization and fallback behavior.
- [ ] Add platform matrix docs and runtime fallback logic.
  - [x] Added initial scaffold doc: `Doc/Phase5-PlatformMatrix-RuntimeFallback.md`.

### Phase 6 - API Simplification and Unification (in progress)
- [x] Extract shared GL shader + fullscreen quad resources for SDL3 and Avalonia renderers.
- [x] Introduce `IAVMixer` / `AVMixer` facade that composes existing `AudioMixer` + `VideoMixer`.
- [x] Extend `AVMixer` with optional unified A/V clock policy and route groups.
- [~] Shift remaining mixer-side format conversion toward endpoint-side conversion.
- [x] Add endpoint abstraction track (push/pull) to reduce Output/Sink coupling.
- [x] Add video output cloning support (main + monitor preview use cases).
- [x] Add user-facing endpoint presets (`Safe`, `Balanced`, `LowLatency`) with NDI-first defaults.
- [~] Gradually migrate sample apps to consume `IAVMixer` while keeping old mixers intact.

## Execution Notes

- Keep each phase shippable independently.
- Preserve strict fallback behavior (no hard dependency on optional native components).
- Validate each phase with:
  - `dotnet build` for touched projects,
  - targeted unit tests,
  - playback diagnostics in `MFPlayer.AvaloniaVideoPlayer`.

## Tracking Log

- 2026-04-10: Phase 0 completed.
- 2026-04-10: Phase 1 auto-probe and default hardware decode behavior enabled.
- 2026-04-10: Phase 2 sink preferred-format routing baseline completed.
- 2026-04-10: Phase 3 started with libyuv runtime baseline; BGRA<->RGBA swizzle can use libyuv `ARGBShuffle` when available.
- 2026-04-10: Added decoder diagnostics snapshot (`FFmpegDecoder.GetDiagnosticsSnapshot`) and startup diagnostics output in Avalonia test app.
- 2026-04-10: Added libyuv conversion paths for `NV12`/`YUV420P`/`UYVY` to `RGBA`/`BGRA` with managed fallback.
- 2026-04-10: Added initial SDL3 NV12 shader rendering path and NV12 leader output selection.
- 2026-04-10: Completed sink format capability negotiation and sink conversion hit/miss diagnostics in `VideoMixer`.
- 2026-04-10: Added conversion benchmark harness project (`Test/S.Media.Core.Benchmarks`) for quick throughput comparisons.
- 2026-04-10: Added SDL3 per-format presentation diagnostics counters (`Bgra`, `Rgba`, `Nv12`, `Yuv420p`, `Other`).
- 2026-04-10: Added shared GL shader resources (`GlShaderSources`) reused by SDL3 and Avalonia renderers.
- 2026-04-10: Added `IAVMixer` / `AVMixer` composition facade and core tests.
- 2026-04-10: Plan updated with mixer architecture direction: format-agnostic core, endpoint-side conversion, endpoint unification, cloning, and presets.
- 2026-04-10: Added output/sink endpoint adapters (audio/video) and pull-source adapters.
- 2026-04-10: Added NDI audio preset parity (`Safe`/`Balanced`/`LowLatency`) with bounded pending queue behavior.
- 2026-04-10: Added `VideoOutputEndpointAdapter` for explicit output-side endpoint bridging.
- 2026-04-10: Added `AVMixer` route-group helpers for one-liner multi-sink routing.
- 2026-04-10: Added endpoint diagnostics snapshot contract (`IVideoSink.GetDiagnosticsSnapshot`) and wired endpoint telemetry into player diagnostics output.
- 2026-04-10: Added `VideoSinkEndpointAdapter` diagnostics counters (`Passthrough`/`Converted`/`Dropped`).
- 2026-04-10: Migrated `MFPlayer.VideoMultiOutputPlayer` to `AVMixer` routing and NDI preset selection with endpoint telemetry in stats output.
- 2026-04-10: Added converter-level libyuv diagnostics (`available/attempt/success/fallback`) to player stats outputs.
- 2026-04-10: Migrated `MFPlayer.AvaloniaVideoPlayer` to `AVMixer` video routing facade.
- 2026-04-10: Added `LocalVideoOutputRoutingPolicy` and integrated SDL3 output leader-format selection to prefer shader-compatible source formats.
- 2026-04-10: Added converter runtime toggle for libyuv vs managed fallback and benchmark two-pass comparison mode.
- 2026-04-10: Added `Yuv422p10` scaffolding in local routing policy and SDL3 diagnostics with explicit fallback policy (`supportsYuv422p10: false`) until shader support lands.
- 2026-04-10: Added raw-format passthrough marker (`PreferRawFramePassthrough`) so endpoint adapters can move one more conversion step out of `VideoMixer` and handle it at endpoint boundary.
- 2026-04-10: Added SDL3 `Yuv422p10` shader path (planar 16-bit integer texture upload + shader conversion) and enabled leader routing support (`supportsYuv422p10: true`).
- 2026-04-10: Split mixer passthrough diagnostics into `SameFormatPassthrough` and `RawMarkerPassthrough` and updated player stats output.
- 2026-04-10: Added deterministic `YUV422P10` shader regression probes (`GlShaderSourcesTests`) to validate neutral gray, luma monotonicity, and shifted 16-bit unpack behavior.
- 2026-04-11: Added full/limited range normalization toggle for `YUV422P10` shader path (`SDL3VideoOutput.Yuv422p10LimitedRange`) and sample-app runtime prompt in `MFPlayer.VideoPlayer`.
- 2026-04-11: Expanded `GlShaderSourcesTests` with limited-range black/white-point checks and shader contract assertion for `uLimitedRange`.
- 2026-04-11: Added `YUV422P10` BT.601/BT.709 matrix toggle (`SDL3VideoOutput.Yuv422p10UseBt709Matrix`) and runtime sample-app prompt.
- 2026-04-11: Expanded `GlShaderSourcesTests` with shader contract assertion for `uColorMatrix` and deterministic BT.601-vs-BT.709 output-difference checks.
- 2026-04-11: Added `YUV422P10` matrix mode selection (`SDL3VideoOutput.Yuv422p10ColorMatrix`: `Auto`/`Bt601`/`Bt709`) with resolution-based auto heuristic and sample-app `auto/601/709` prompt.
- 2026-04-11: Added decoder metadata hint path (`IVideoColorMatrixHint`) and FFmpeg color-space mapping so sample-app matrix default can follow source metadata when available.
- 2026-04-11: Extended FFmpeg metadata hint mapping with source color-range (`AVCOL_RANGE_MPEG/JPEG`) so sample-app `YUV422P10` range default can follow decoder metadata when available.
- 2026-04-11: Kept `YUV422P10` range selection end-to-end as enum (`Auto`/`Full`/`Limited`) in sample-app flow (no early bool collapse) while preserving bool compatibility APIs.
- 2026-04-11: Extended shared YUV shader controls (`YuvColorRange`/`YuvColorMatrix`) to `NV12` and `YUV420P` paths in SDL3 renderer, not just `YUV422P10`.
- 2026-04-11: Added runtime YUV policy diagnostics line in `MFPlayer.VideoPlayer` (`requested -> resolved`, with decoder hint values).
- 2026-04-11: Added Phase 5 scaffold doc (`Doc/Phase5-PlatformMatrix-RuntimeFallback.md`) for platform matrix and fallback policy tracking.
- 2026-04-11: Added matching YUV policy hint/resolution diagnostics output in `MFPlayer.AvaloniaVideoPlayer` (with explicit `cpu-convert-to-rgba` path note).
- 2026-04-11: Added shared `YuvAutoPolicy` helper in `S.Media.Core.Video` and switched SDL3 renderer + sample apps to use one common auto-resolution policy path.

