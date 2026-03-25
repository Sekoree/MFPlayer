# S.Media.MIDI

Contract-first MIDI backend for `S.Media.Core` over `MIDI/PMLib`.

## Current Scope

- `Runtime/MIDIEngine.cs`
  - init/terminate lifecycle
  - device catalog discovery
  - synthetic fallback catalog when native runtime/devices are unavailable
- `Input/MIDIInput.cs`
  - open/close lifecycle
  - event-driven input delivery
  - status transitions (`Opening`, `Open`, `Closed`, `Disconnected`, `Reconnecting`, `ReconnectFailed`)
  - bounded native reconnect attempts for short-disconnect recovery
- `Output/MIDIOutput.cs`
  - open/close lifecycle
  - `Send(in MIDIMessage)` contract (`MIDIOutputNotOpen` when closed)
  - reconnect-aware disconnect handling during native send failures
- `Config/*`, `Types/*`, `Events/*`
  - reconnect options, message/status payload contracts

## Notes

- Current implementation is contract scaffolding with native-attempt behavior, reconnect policy hooks, and deterministic fallback paths.
- Return-code contract is int-first (`0` success, non-zero failure).
- `Close()` / `Terminate()` paths are idempotent.

