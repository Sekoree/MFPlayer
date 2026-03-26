# S.Media Implementation Execution Schedule

Source references:
- `Media/S.Media.Core/PLAN.smedia-architecture.md`
- `Media/S.Media.Core/implementation-readiness-checklist.md`
- `Media/S.Media.Core/error-codes.md`

## Scheduling Model

This schedule is dependency-first (no fixed dates). A milestone starts only when its entry gates pass.

Gate labels:
- `Entry`: required before module implementation starts.
- `Exit`: required before module is marked complete.
- `Blocker`: hard stop that must be resolved before continuing.

## Milestone Overview

| Milestone | Modules | Parallel lanes | Primary dependency |
| --- | --- | --- | --- |
| M0 | Core (contracts + errors + diagnostics) | None | None |
| M1 | Core (mixers/player behavior + contract tests) | None | M0 |
| M2 | PortAudio, FFmpeg, MIDI | 3 lanes (`Audio`, `Decoding`, `MIDI`) | M1 |
| M3 | OpenGL | None | M1 + FFmpeg read path available |
| M4 | OpenGL.Avalonia, OpenGL.SDL3 | 2 lanes (`Avalonia`, `SDL3`) | M3 |
| M5 | NDI | 1 lane (`NDI Input/Clock` then `NDI Output`) | M1 (+ M3 for output interop) |
| M6 | Cross-module integration + stabilization | 1 lane (`Integration`) | M2 + M4 + M5 |

## Module Execution Cards

### `Media/S.Media.Core`

- Entry gates:
  - `implementation-readiness-checklist.md` Core section is `Go`.
  - Error allocation/source-of-truth alignment verified with `Media/S.Media.Core/error-codes.md`.
- Deliverables:
  - `MediaErrorAllocations`, `ErrorCodeRanges.ResolveSharedSemantic`, diagnostics key definitions.
  - Finalized lifecycle/idempotency/failure-atomicity/teardown-fence contracts.
  - Mixer detach policy contract support (`3000` fallback + secondary diagnostics semantics).
- Exit gates:
  - Core contract tests for lifecycle + shared semantic mapping + seek + detach precedence pass.
  - No unresolved Core blockers for downstream modules.

### `Media/S.Media.PortAudio`

- Entry gates:
  - Core audio contracts are stable (`IAudioEngine`, `IAudioOutput`, `IAudioSource`).
- Deliverables:
  - Engine lifecycle/discovery/output creation aligned to Core interfaces.
  - Non-timeout input pull path, deterministic mapping validation, device-switch semantics.
  - PortAudio contract test matrix implementation.
- Exit gates:
  - Lifecycle idempotency/event ordering/teardown fence tests pass.
  - Route/push validation and overflow/underflow behaviors validated.

### `Media/S.Media.FFmpeg`

- Entry gates:
  - Core source/media contracts stable.
  - Adaptation policy enforced (`implementation adaptation only`, no legacy class moves).
- Deliverables:
  - Internal decoder/demux adaptation (`FFAudioDecoder`, `FFVideoDecoder`, shared session).
  - Deterministic open/config validation (`2010`) and concurrency path (`2014 -> 950`).
  - Stream ownership + seek + duration semantics (`double.NaN` for unknown/live).
- Exit gates:
  - FFmpeg contract matrix implemented and passing.
  - No partial-open failures on invalid config paths.

### `Media/S.Media.MIDI`

- Entry gates:
  - Core event/lifecycle contracts stable.
- Deliverables:
  - Engine/input/output with deterministic lifecycle and status transitions.
  - Reconnect behavior controlled by `ReconnectMode` (single source of truth).
  - Synchronous/no-queue callback model and event-lifetime fences.
- Exit gates:
  - MIDI contract matrix implemented and passing (status ordering, reconnect stop conditions, teardown fence).

### `Media/S.Media.OpenGL`

- Entry gates:
  - Core video contracts stable.
  - FFmpeg video read path available for integration scenarios.
- Deliverables:
  - Clone-graph policy implementation (no cycles/self-attach, deterministic detach behavior).
  - Performance-first upload/clone pipeline adaptation (no legacy class moves).
  - OpenGL error code and failure-atomicity behavior.
- Exit gates:
  - OpenGL contract matrix implemented and passing.
  - Clone generation parity and teardown fence validated.

### `Media/S.Media.OpenGL.Avalonia`

- Entry gates:
  - OpenGL core clone/output behavior stable.
- Deliverables:
  - UI-only adapter controls/outputs with delegated clone policy.
  - Adapter error propagation and deterministic lifecycle behavior.
- Exit gates:
  - Avalonia contract matrix implemented and passing.

### `Media/S.Media.OpenGL.SDL3`

- Entry gates:
  - OpenGL core clone/output behavior stable.
- Deliverables:
  - SDL3 output/embed adapter with deterministic handle/descriptor contracts.
  - Parent-loss handling and teardown behavior.
- Exit gates:
  - SDL3 contract matrix implemented and passing.

### `Media/S.Media.NDI`

- Entry gates:
  - Core clock/error contracts stable.
- Deliverables:
  - Live source behavior (`DurationSeconds = double.NaN`) and queue/fallback precedence.
  - External clock contract with `4211` no-fallback semantics.
  - Input paths first; output interop after OpenGL gate passes.
- Exit gates:
  - NDI contract matrix implemented and passing.
  - External-clock unavailability and diagnostics-thread behavior validated.

## Integration and Stabilization (M6)

- Cross-module integration scenarios:
  - FFmpeg -> Audio (`PortAudio`) path.
  - FFmpeg -> Video (`OpenGL`) path.
  - FFmpeg -> A/V mixer path.
  - NDI live input + external clock behavior.
  - OpenGL clone fan-out with adapter outputs.
- Required global checks:
  - Shared semantic mapping sanity (`ResolveSharedSemantic`).
  - Teardown fences respected across all modules.
  - Failure-atomicity checks for start/stop/seek/attach/detach/remove paths.

## Execution Tracking Template

Use one row per module implementation wave.

| Module | Milestone | Owner | Entry gate date | Exit gate date | Status (`Planned/In Progress/Done/Blocked`) | Active blocker |
| --- | --- | --- | --- | --- | --- | --- |
| `S.Media.Core` | M0-M1 | `...` | `...` | `...` | `In Progress` | `Backlog checklist still needs explicit gate check-off pass` |
| `S.Media.PortAudio` | M2 | `...` | `...` | `...` | `In Progress` | `Cross-module harness migration still ongoing` |
| `S.Media.FFmpeg` | M2 | `...` | `...` | `...` | `In Progress` | `Heavy-path/perf parity tuning remains` |
| `S.Media.MIDI` | M2 | `...` | `...` | `...` | `In Progress` | `No hard blocker currently tracked` |
| `S.Media.OpenGL` | M3 | `...` | `...` | `...` | `In Progress` | `Remaining perf parity validation gates` |
| `S.Media.OpenGL.Avalonia` | M4 | `...` | `...` | `...` | `In Progress` | `Legacy harness parity still being ported` |
| `S.Media.OpenGL.SDL3` | M4 | `...` | `...` | `...` | `In Progress` | `Legacy harness parity still being ported` |
| `S.Media.NDI` | M5 | `...` | `...` | `...` | `In Progress` | `Legacy harness parity + broader integration pass pending` |

