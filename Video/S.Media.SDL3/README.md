# S.Media.SDL3

SDL3 + OpenGL video output and clone sink for `S.Media.Core`.

- `SDL3VideoOutput` — hardware-timed `IVideoEndpoint` + `IClockCapableEndpoint` that
  owns an SDL3 window and drives an OpenGL renderer on its own render thread.
  Advertises a `VideoPtsClock` so the router can slave its tick to presentation time.
- `SDL3VideoCloneSink` — lightweight `IVideoEndpoint` that opens a secondary SDL3
  window and copies inbound frames into its own ArrayPool buffer (the router owns
  the source frame's `MemoryOwner`).
- `GLRenderer` — shader-based pixel-format converter (BGRA/RGBA/NV12/I420/UYVY422/
  10-bit YUV) with BT.601/709/2020 matrix selection and scaling filter options.

