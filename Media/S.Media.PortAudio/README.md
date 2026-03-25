# S.Media.PortAudio

Contract-first PortAudio backend for `S.Media.Core` audio engine/output APIs.

## Current Scope

- `Engine/PortAudioEngine.cs`
  - lifecycle (`Initialize`, `Start`, `Stop`, `Terminate`)
  - output/input device discovery stubs
  - output creation and tracking
- `Output/PortAudioOutput.cs`
  - lifecycle (`Start`, `Stop`)
  - device switch methods (`SetOutputDevice*`)
  - per-push dense route-map validation
- `Input/PortAudioInput.cs`
  - source-style pull input scaffold (`IAudioSource`)
  - live semantics (`DurationSeconds = double.NaN`, `Seek => MediaSourceNonSeekable`)
- `Diagnostics/PortAudioLogAdapter.cs`
  - `Microsoft.Extensions.Logging` adapter helper

## Notes

- Current implementation is deterministic contract scaffolding; native streaming integration follows in next iterations.
- Native runtime/device discovery is attempted through PALib (`Pa_Initialize`, device catalog), with deterministic synthetic fallback when native runtime is unavailable.
- Output/input paths attempt native blocking streams (`Pa_OpenDefaultStream` + read/write), then fall back to deterministic synthetic behavior if native open/start/read/write is unavailable.
- Return-code contract is int-first (`0` success, non-zero failure).
- Lifecycle idempotency is preserved for `Stop()` and `Terminate()`.

