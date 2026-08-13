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
projectM before loading a preset. HaCue2, HaPlay and HaViz import one shared deployment target
([`ProjectMDesktopDeployment.targets`](ProjectMDesktopDeployment.targets)) which places this complete
tree under the application output; the runtime resolver prefers it over a system installation because
the patches are part of the compositor contract.

That target also *builds* the tree on demand: publishing a desktop head for the host RID with nothing
staged runs `scripts/build-projectm.sh` itself, so a fresh clone can `dotnet publish` without a
separate setup step (a few minutes, once - it needs cmake, a C++17 compiler, OpenGL headers and glm).
A plain `dotnet build` never triggers it and keeps degrading to "no visualizer". Escape hatches:
`-p:SkipProjectMNativeBuild=true` skips the build *and* the publish gate, `-p:MfpProjectMAutoBuild=false`
keeps the gate but leaves the build to you. A cross-RID publish cannot auto-build (the script is bash
and builds for the host), so `External/projectm/<target-rid>/` has to be produced on a machine of that
RID and copied in.
