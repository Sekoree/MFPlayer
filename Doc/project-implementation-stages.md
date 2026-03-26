# Project Implementation Stages

Last updated: 2026-03-26

This page tracks the current implementation stage for each project in `MFPlayer.sln`.

## Stage Legend

- `Implemented`: Core behavior is in place and currently used.
- `In Progress`: Active implementation/migration work is still ongoing.
- `Validation`: Test/smoke/harness project used to validate behavior.
- `Legacy-Migration`: Legacy or transitional project kept during migration.

## Audio

| Project | Stage | Notes |
| --- | --- | --- |
| `Audio/PALib/PALib.csproj` | Implemented | Native PortAudio binding layer used by `S.Media.PortAudio`. |
| `Audio/PALib.Tests/PALib.Tests.csproj` | Validation | Binding and interop contract tests. |
| `Audio/PALib.Smoke/PALib.Smoke.csproj` | Validation | Manual runtime verification harness. |

## Media Core + Backends

| Project | Stage | Notes |
| --- | --- | --- |
| `Media/S.Media.Core/S.Media.Core.csproj` | In Progress | Contract surface and shared error ranges are established and actively used. |
| `Media/S.Media.Core.Tests/S.Media.Core.Tests.csproj` | Validation | Contract and integration tests for core surfaces. |
| `Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj` | In Progress | Shared demux session, decode/resample/convert path implemented; heavy paths still being tuned. |
| `Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj` | Validation | Functional tests, including opt-in heavy-path coverage. |
| `Media/S.Media.PortAudio/S.Media.PortAudio.csproj` | In Progress | Engine/output/input active, blocking push semantics and host API selection implemented. |
| `Media/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj` | Validation | Lifecycle, default device, host API, routing contracts. |
| `Media/S.Media.MIDI/S.Media.MIDI.csproj` | In Progress | Engine/input/output and reconnect/status behavior implemented. |
| `Media/S.Media.MIDI.Tests/S.Media.MIDI.Tests.csproj` | Validation | Lifecycle and behavior tests. |
| `Media/S.Media.NDI/S.Media.NDI.csproj` | In Progress | Source/output/runtime scaffolding, diagnostics aggregation, and option precedence contracts implemented. |
| `Media/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj` | Validation | Runtime and option contract tests. |
| `Media/S.Media.OpenGL/S.Media.OpenGL.csproj` | In Progress | Clone graph and engine/output contract path implemented. |
| `Media/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj` | Validation | Clone graph, adapter, and error-code contract tests. |
| `Media/S.Media.OpenGL.Avalonia/S.Media.OpenGL.Avalonia.csproj` | In Progress | Avalonia adapter and host control integration path. |
| `Media/S.Media.OpenGL.SDL3/S.Media.OpenGL.SDL3.csproj` | In Progress | SDL3 adapter/embed path and clone integration. |

## App Layer

| Project | Stage | Notes |
| --- | --- | --- |
| `MFPlayer/MFPlayer.csproj` | In Progress | Main Avalonia app shell; migration integration still ongoing. |
| `MFPlayer.Desktop/MFPlayer.Desktop.csproj` | In Progress | Desktop startup/packaging host for `MFPlayer`. |

## Foundation Libraries (non-`S.Media.*`)

| Project | Stage | Notes |
| --- | --- | --- |
| `PMLib/PMLib.csproj` | Implemented | MIDI base interop/support library used during migration. |
| `NDI/NdiLib/NdiLib.csproj` | Implemented | NDI native interop layer used by `S.Media.NDI`. |
| `NDI/NdiLib.Smoke/NdiLib.Smoke.csproj` | Validation | Manual NDI binding/runtime checks. |
| `OSC/OSCLib/OSCLib.csproj` | Implemented | OSC library is functional and tested. |
| `OSC/OSCLib.Tests/OSCLib.Tests.csproj` | Validation | Automated OSC tests. |
| `OSC/OSCLib.Smoke/OSCLib.Smoke.csproj` | Validation | Manual OSC smoke checks. |

## Test and Bring-up Projects

| Project | Stage | Notes |
| --- | --- | --- |
| `Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj` | Validation | Direct decoder -> output smoke runner; current first-audio bring-up harness. |
| `Test/AudioEx/AudioEx.csproj` | In Progress | Migrated to `S.Media.FFmpeg` + `S.Media.PortAudio`; broader A/V parity still in progress. |
| `Test/NdiVideoReceive/NdiVideoReceive.csproj` | In Progress | Migrated to `S.Media.NDI` discovery/source-read smoke path; output/render parity still in progress. |
| `Test/VideoStress/VideoStress.csproj` | In Progress | Canonical Avalonia stress harness on `S.Media.*` references; runtime porting in progress. |
| `Test/VideoTest/VideoTest.csproj` | Legacy-Migration | Legacy Avalonia harness retained as behavior/reference source during port. |

## Legacy VideoLibs (migration source)

| Project | Stage | Notes |
| --- | --- | --- |
| `VideoLibs/Seko.OwnAudioNET.Video/Seko.OwnAudioNET.Video.csproj` | Legacy-Migration | Legacy implementation kept as adaptation reference. |
| `VideoLibs/Seko.OwnAudioNET.Video.Engine/Seko.OwnAudioNET.Video.Engine.csproj` | Legacy-Migration | Source reference for migration to `S.Media.*`. |
| `VideoLibs/Seko.OwnAudioNET.Video.NDI/Seko.OwnAudioNET.Video.NDI.csproj` | Legacy-Migration | Source reference for NDI behavior parity. |
| `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/Seko.OwnAudioNET.Video.Avalonia.csproj` | Legacy-Migration | Source reference for Avalonia adapter behavior. |
| `VideoLibs/Seko.OwnAudioNET.Video.SDL3/Seko.OwnAudioNET.Video.SDL3.csproj` | Legacy-Migration | Source reference for SDL3 adapter behavior. |

## Notes

- This is a practical stage snapshot, not a strict release-readiness gate.
- `Media/IMPLEMENTATION-STATUS.md` remains the compact execution/status snapshot.
- Legacy and reference projects remain intentionally visible until migration completion and cleanup.
