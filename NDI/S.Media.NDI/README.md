# S.Media.NDI

NDI source / sink adapters for `S.Media.Core`.

- `NDISource` / `NDIVideoChannel` / `NDIAudioChannel` — receive an NDI stream as a
  pull-mode `IAudioChannel` / `IVideoChannel` with an `NDIClock` slaved to sender
  timestamps.  Falls back to a synthetic monotonic clock (re-origined per
  real↔synthetic transition) when the sender provides undefined timestamps.
- `NDIAVSink` — an `IAVEndpoint` that fans a pipeline's audio+video out through a
  single NDI sender with a shared `NDIAvTimingContext` for aligned timecodes.
  Separate `_videoSendLock` / `_audioSendLock` allow concurrent RGBA sends without
  blocking audio (per NDI SDK §13: "frames may be sent … at any time, off any
  thread, and in any order").
- Endpoint / latency presets (`NDIEndpointPreset`, `NDILatencyPreset`) tune pool
  counts, pending-frame caps and poll cadence from a single knob.

Low-level interop lives in the sibling `NDILib` project.

