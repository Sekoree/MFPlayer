# projectM integration

This binding and the projectM library produced by [`scripts/build-projectm.sh`](../../../scripts/build-projectm.sh)
are modified/integrated for MFPlayer's desktop apps; they are not an unmodified upstream projectM build.
The source baseline is projectM 4.1.6, licensed under LGPL-2.1, and remains dynamically linked.

The build works from a disposable copy of the vendored source and applies (or verifies) the
auditable changes in [`scripts/patches/`](../../../scripts/patches/):

- `projectm-render-to-bound-fbo.patch` directs the final render pass into the framebuffer selected
  by the embedding compositor.
- `projectm-null-safe-texture-descriptor.patch` prevents missing random-texture descriptors from
  causing a native null-pointer crash.
- `projectm-hlsl-strtod-bounds.patch` prevents the preset shader tokenizer from reading beyond a
  bounded, non-NUL-terminated numeric token.

The build script also installs pinned Milkdrop preset and texture packs under
`External/projectm/<rid>/`. The managed visualizer supplies the companion texture directory to
projectM before loading a preset. HaCue2, HaPlay and HaViz import one shared deployment target which
places this complete tree under the application output; the runtime resolver prefers it over a system
installation because the patches are part of the compositor contract.
