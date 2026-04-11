# Phase 5 - Platform Matrix and Runtime Fallback Scaffold

Purpose:
- Track hw-frame interop candidates by platform.
- Define runtime probe/fallback behavior before deep implementation work.

## Platform Matrix (Draft)

| Platform | Preferred Interop Path | Secondary Path | Current State | Key Blockers |
|---|---|---|---|---|
| Linux | VAAPI hw-frames -> GL texture import | VAAPI decode + CPU upload | Investigating | EGL/DMABUF wiring and sync model |
| Windows | D3D11VA hw-frames -> shared texture interop | D3D11VA decode + CPU upload | Investigating | D3D11/OpenGL bridging strategy |
| macOS | VideoToolbox hw-frames -> Metal/GL path | VideoToolbox decode + CPU upload | Investigating | Cross-backend renderer interop abstraction |

## Runtime Fallback Policy (Draft)

Probe order (high level):
1. Try platform-native hw-frame interop path.
2. If unavailable, use hardware decode with CPU conversion/upload.
3. If hardware decode unavailable or unstable, fallback to software decode.

Behavioral rules:
- Any interop init failure must downgrade path, never fail playback startup by default.
- Emit diagnostics that include selected path and fallback reason.
- Preserve deterministic opt-out (`--sw`) for reproducible testing.

## Telemetry Fields (Initial)

- `interop.path` (e.g., `vaapi-dmabuf`, `d3d11-shared`, `vtb-cpu`)
- `interop.enabled` (`true`/`false`)
- `interop.fallbackReason` (short code/string)
- `interop.initAttempts`
- `interop.initFailures`

## Validation Checklist (Draft)

- `dotnet build` on touched projects
- Targeted unit/integration tests for probe/fallback logic
- Manual playback validation on each platform target with diagnostics capture

