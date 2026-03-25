# S.Media Implementation Status

Last updated: 2026-03-25

This snapshot summarizes the current implementation state across `S.Media.*` projects.

## Current Module Status

| Module | Status | Notes |
| --- | --- | --- |
| `S.Media.Core` | In Progress | Core contracts, mixers/player surfaces, debug/error allocation policy, and shared semantic mapping are in place and actively used by module tests. |
| `S.Media.FFmpeg` | In Progress | Source/media-item/shared-session pipeline and deterministic native-attempt fallbacks are implemented; heavy test paths remain opt-in. |
| `S.Media.PortAudio` | In Progress | Engine/input/output slices are implemented with lifecycle/idempotency/integration tests passing. |
| `S.Media.MIDI` | In Progress | Engine/input/output/reconnect/status events implemented with deterministic lifecycle and validation contracts. |
| `S.Media.NDI` | In Progress | Engine/source/output scaffolding, diagnostics thread/snapshots, source/output diagnostics aggregation, and option precedence contracts are implemented. |
| `S.Media.OpenGL` | In Progress | Engine/output clone graph behavior, diagnostics events, upload/conversion helpers, and contract tests are implemented. |
| `S.Media.OpenGL.Avalonia` | In Progress | `OpenGlControlBase` host control, adapter output/clone options, and HUD overlay contract path are implemented. |
| `S.Media.OpenGL.SDL3` | In Progress | Video view/embed contracts, shader/hud adapter flow, clone path, and embedding error-code contracts are implemented. |

## Test Baseline (Single Command)

Most recent full-run command:

```fish
cd /home/sekoree/RiderProjects/MFPlayer
dotnet test MFPlayer.sln --logger "console;verbosity=minimal" | cat
```

Latest observed results from that one-command run:

- `PALib.Tests`: Passed `2`, Failed `0`, Skipped `0`
- `OSCLib.Tests`: Passed `18`, Failed `0`, Skipped `0`
- `S.Media.Core.Tests`: Passed `47`, Failed `0`, Skipped `0`
- `S.Media.FFmpeg.Tests`: Passed `91`, Failed `0`, Skipped `4` (heavy opt-in tests)
- `S.Media.PortAudio.Tests`: Passed `15`, Failed `0`, Skipped `0`
- `S.Media.MIDI.Tests`: Passed `14`, Failed `0`, Skipped `0`
- `S.Media.NDI.Tests`: Passed `15`, Failed `0`, Skipped `0`
- `S.Media.OpenGL.Tests`: Passed `35`, Failed `0`, Skipped `0`

## Notes

- The architecture direction in `Media/S.Media.Core/PLAN.smedia-architecture.md` is being followed, with strict project-aligned namespaces and direct-contract APIs.
- OpenGL adapter direction is aligned to legacy-style host/HUD lifecycle behavior (`VideoGL`/`VideoSDL`) while staying adapter-only (no decode/session ownership in adapters).
- FFmpeg heavy-path tests remain intentionally opt-in and are skipped in default full-solution runs.

