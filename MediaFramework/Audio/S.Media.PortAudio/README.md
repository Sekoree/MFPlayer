# S.Media.PortAudio

PortAudio-backed audio output and sink for `S.Media.Core`.

- `PortAudioOutput` — hardware `IAudioEndpoint` + `IClockCapableEndpoint`.  Opens a
  device at a requested `AudioFormat`, exposes the negotiated `HardwareFormat`, and
  drives the router's push tick from a callback clock (`PortAudioClock`).
- `PortAudioSink` — lightweight `IAudioEndpoint` for fan-out to a second device.
  Uses `SinkBufferHelper.ComputeWriteFrames` for drift-corrected write sizing so
  the secondary stream cannot drift relative to the primary clock.
- `PortAudioEngine` — one-shot native library init/shutdown with device enumeration.

Low-level bindings live in the sibling `PALib` project.

