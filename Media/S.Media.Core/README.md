# S.Media.Core

Core routing, mixing, clocks and endpoint contracts for MFPlayer. Source-agnostic and
codec-agnostic — other projects (`S.Media.FFmpeg`, `S.Media.NDI`, `S.Media.SDL3`,
`S.Media.PortAudio`, `S.Media.Avalonia`) plug in as channels or endpoints.

Key types:

- `AVRouter` / `IAVRouter` — registers audio/video **inputs** and **endpoints**, wires
  them up with `CreateRoute`, and drives push endpoints from its own clock tick.
- `IAudioEndpoint` / `IVideoEndpoint` / `IAVEndpoint` — unified push contracts for
  outputs and fan-out sinks.
- `IMediaChannel<T>` + `IAudioChannel` / `IVideoChannel` — pull contracts for decoded
  sources, with explicit partial-fill semantics.
- `MediaClockBase` / `StopwatchClock` / `VideoPtsClock` — clock implementations with a
  priority-tiered auto-selection policy.

See `Doc/Quick-Start.md` and `Doc/Usage-Guide.md` for usage examples and
`Doc/AVMixer-Refactor-Plan.md` for the architecture history.

