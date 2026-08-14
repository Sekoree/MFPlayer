# Native dependency matrix

What each package needs at runtime, where the release artifact gets it, and how strictly the
binding checks it. "Gate" = the executable check in `scripts/check-native-versions.sh` +
`scripts/load-probe-native-manifest.sh` run against every release artifact (REL-01).

| Native | Pinned version | Needed by | Acceptance policy | Release source (linux-x64) | Gate |
|---|---|---|---|---|---|
| FFmpeg (libav*) | pinned master snapshot, avcodec-63 ABI (exact identity: [`.github/native-manifest/ffmpeg.lock`](../.github/native-manifest/ffmpeg.lock)) | Decode.FFmpeg, Encode.FFmpeg | soname/ABI major, verified from the shipped binary via `avcodec_version()` | dated BtbN autobuild (SHA-256 verified), bundled as fallback; matching system FFmpeg preferred. win-x64: `FFmpeg.GPL` NuGet, same nightly | version gate |
| PortAudio | 19.7.0 | Audio.PortAudio | stable soname (`libportaudio.so.2`) | apt binary | load-probe |
| miniaudio | **exactly 0.11.25** | Audio.MiniAudio (MALib) | exact `ma_version()` — hand-mirrored ABI; anything else is refused by the resolver | compiled from the pinned single header | version gate |
| PortMidi | 2.0.7 | Control (PMLib) | 2.x ABI (`libportmidi.so.2`) | built from the pinned source tag | load-probe |
| libass | **>= 0.17.5** | Subtitles (LibAssLib) | minimum secure version via `ass_library_version()` | built from the pinned 0.17.5 source archive (SHA-256 verified) | version gate |
| projectM | 4.1.6 + repo patches | Visualizer.ProjectM | exact version; GLES-only builds rejected; desktop-GL required | built by `scripts/build-projectm.sh` (pinned archive when `Reference/` absent), staged under `External/projectm/<rid>/` with presets + textures | version gate |
| SDL3 | 3.4.12 | Present.SDL3(.Compositor) | NuGet-shipped native (SDL3-CS) | NuGet | load-probe |
| NDI | v6 runtime | S.Media.NDI (NDILib) | major version; **optional** (proprietary licensing) | DistroAV helper (best-effort) / host install | none (optional) |
| Bullet (mmd_bullet shim) | Bullet 3.25, shim ABI 1 | Source.MMD | custom shim with explicit ABI marker | built from vendored source | load-probe |
| SkiaSharp/HarfBuzz | NuGet-pinned | HaPlay UI | NuGet-shipped natives | NuGet | load-probe (app launch) |

## Resolution order

First-party bindings resolve through `SystemFirstNativeLibraryResolver` (system → explicit paths →
app-local RID assets) **except**:

- **MALib (miniaudio)** probes the app-local exact build FIRST and version-gates every candidate —
  a mismatched system build would corrupt memory, not fail cleanly.
- **ProjectMLib** honours `MFP_PROJECTM_LIB` (path or directory), then the dev build under
  `External/projectm/<rid>/`, and rejects OpenGL-ES builds that would crash a desktop-GL context.

## Developer setup (Linux)

- FFmpeg: the bindings need the avcodec-63 ABI (an FFmpeg master snapshot — no stable release
  carries it yet), which distros do not ship. Run `scripts/fetch-ffmpeg.sh` once: it stages the
  exact locked build into `External/ffmpeg/<rid>/`, which the resolver probes automatically.
  PortAudio and libass usually come from your distro (libass must be 0.17.5+).
- miniaudio: compile the pinned header once — see the artifact workflow for the two-line build.
- projectM: run `scripts/build-projectm.sh` (uses `Reference/projectm-4.1.6` when present, else
  downloads the pinned archive) and export the `MFP_PROJECTM_LIB` line it prints. Publishing a
  desktop head (HaPlay/HaCue2/HaViz) for the host RID runs that script for you when nothing is
  staged under `External/projectm/<rid>/` — `dotnet publish` then takes the extra few minutes once,
  and needs cmake, a C++17 compiler, OpenGL headers and glm. `-p:SkipProjectMNativeBuild=true`
  publishes without it (compile gates only — a published app is expected to carry the patched
  runtime), and a cross-RID publish still needs the tree built on a machine of that RID.
- MMD physics: `MediaFramework/Native/mmd_bullet/build.sh`.
