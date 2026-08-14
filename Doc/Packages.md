# NuGet package surface

The framework is organized as purpose-led **entry packages** plus independently installable leaf
packages. Start with an entry package; reach for leaves only when you need one directly.

## Availability — read this first

**These packages are not published to nuget.org** (or any public feed). Consuming them currently
means working from this repository: project references inside the solution, or `dotnet pack` into
a local feed. A public restore is additionally blocked by design: the package graph pins
`FFmpeg.AutoGen 9.0.0`, a repo-built binding (upstream has no FFmpeg-master release) served from
the local `./packages` source declared in `nuget.config` — a consumer without this checkout has no
source for it. Public publishing is deliberately out of scope for now; if that changes, the
FFmpeg.AutoGen fork must first move to an owned package ID or be internalized (2026-08-14 review,
F-01).

## Entry / meta packages (start here)

| Package | Purpose |
|---|---|
| `S.Media` | Default playback runtime: core, timing, routing, players, FFmpeg decode, PortAudio + miniaudio backends |
| `S.Media.Core` | Minimal contracts/frames/diagnostics SDK for backend or source authors (dependency-light) |
| `S.Media.Show` | Cue/show/session + composition runtime (adds Session, Compositor, Subtitles, Source.Text) |
| `S.Media.Encoding` | Recording, muxing and network-stream output |
| `S.Media.Presentation.SDL3` | Standalone/windowed SDL3 presentation |
| `S.Media.Present.Avalonia` | Embedding video into Avalonia applications |
| `S.Media.Control` | Show control, scripting, MIDI and OSC |
| `S.Abi` | Load inbound native C-ABI plugins into a managed host |
| `S.Media.Interop` | Publish the framework as the outbound NativeAOT `s_media_player` C ABI |
| `S.Media.Full` | Batteries-included framework feature set (everything a full HaPlay-class app uses; **framework only**, no executable) |

## Feature modules (install individually when needed)

`S.Media.Audio.PortAudio`, `S.Media.Audio.MiniAudio`, `S.Media.NDI`, `S.Media.Subtitles`,
`S.Media.Visualizer.ProjectM`, `S.Media.Source.MMD`, `S.Media.Source.YouTube`,
`S.Media.Source.Text`, `S.Media.Stream.Http`, `S.Media.Compositor`, `S.Media.Gpu`,
`S.Media.Routing`, `S.Media.Session`, `S.Media.Players`, `S.Media.Time`, `OSCLib`, `S.Control`.

## Low-level binding / transitive packages (not starting points)

`PALib`, `MALib`, `PMLib`, `NDILib`, `LibAssLib`, `ProjectMLib`, `S.Media.FFmpeg.Common`,
`S.Control.Abstractions`, `S.Media.Decode.FFmpeg`, `S.Media.Encode.FFmpeg`,
`S.Media.Present.SDL3`, `S.Media.Present.SDL3.Compositor`. They exist as packages because the
packages above depend on them and some public APIs expose their contracts; their READMEs point
back to the recommended entry package.

## Stability and native prerequisites

- Every package ships a README (enforced by the pack gate in `Directory.Build.targets`) and XML
  documentation for IntelliSense.
- Native prerequisites are listed per package README and in the shared
  [native dependency matrix](Native-Dependencies.md).
- All libraries build with the trim/NativeAOT analyzers as errors — see [NativeAOT.md](NativeAOT.md).
- The modular leaf graph is unchanged and stays restorable: nothing was merged or hidden to build
  the entry surface.
