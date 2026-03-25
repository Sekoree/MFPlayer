# Project Status Notes

Last updated: 2026-03-25

This document gives per-project stage notes plus the key migration considerations applied across the repo.

## Per-Project Status (Solution Scope)

### Audio

- `Audio/PALib/PALib.csproj` - `Implemented`
  - PortAudio interop surface is active and used by `S.Media.PortAudio`.
  - Stage focus: stable native binding behavior.
- `Audio/PALib.Tests/PALib.Tests.csproj` - `Validation`
  - Binding safety and interop behavior checks.
- `Audio/PALib.Smoke/PALib.Smoke.csproj` - `Validation`
  - Manual runtime probing and environment bring-up checks.

### S.Media Core and Backends

- `Media/S.Media.Core/S.Media.Core.csproj` - `In Progress`
  - Contract baseline for error codes, media interfaces, and shared behavior.
- `Media/S.Media.Core.Tests/S.Media.Core.Tests.csproj` - `Validation`
  - Contract and regression coverage.
- `Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj` - `In Progress`
  - Decode/demux/resample/convert path is implemented with native-attempt + fallback behavior.
- `Media/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj` - `Validation`
  - Includes opt-in heavy-path test coverage.
- `Media/S.Media.PortAudio/S.Media.PortAudio.csproj` - `In Progress`
  - Engine/output/input implemented; blocking push semantics and default-device behavior (`-1`) are active.
- `Media/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj` - `Validation`
  - Lifecycle, host API, and device/default selection behavior.
- `Media/S.Media.MIDI/S.Media.MIDI.csproj` - `In Progress`
  - Engine and source/output contracts are in place.
- `Media/S.Media.MIDI.Tests/S.Media.MIDI.Tests.csproj` - `Validation`
  - Lifecycle and behavior regression checks.
- `Media/S.Media.NDI/S.Media.NDI.csproj` - `In Progress`
  - Source/output/runtime scaffolding and diagnostics aggregation implemented.
- `Media/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj` - `Validation`
  - Runtime/options/lifecycle contract tests.
- `Media/S.Media.OpenGL/S.Media.OpenGL.csproj` - `In Progress`
  - Engine/output clone graph and diagnostics behaviors are implemented.
- `Media/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj` - `Validation`
  - Clone graph and adapter contract tests.
- `Media/S.Media.OpenGL.Avalonia/S.Media.OpenGL.Avalonia.csproj` - `In Progress`
  - Avalonia adapter integration path.
- `Media/S.Media.OpenGL.SDL3/S.Media.OpenGL.SDL3.csproj` - `In Progress`
  - SDL3 adapter/embed path.

### App Projects

- `MFPlayer/MFPlayer.csproj` - `In Progress`
  - Main app shell; integration continues while migration proceeds.
- `MFPlayer.Desktop/MFPlayer.Desktop.csproj` - `In Progress`
  - Desktop host/bootstrap project.

### Foundation/Interop Libraries

- `PMLib/PMLib.csproj` - `Implemented`
  - MIDI support/interoperability layer.
- `NDI/NdiLib/NdiLib.csproj` - `Implemented`
  - NDI interop layer used by `S.Media.NDI`.
- `NDI/NdiLib.Smoke/NdiLib.Smoke.csproj` - `Validation`
  - NDI runtime smoke checks.
- `OSC/OSCLib/OSCLib.csproj` - `Implemented`
  - OSC library functionality is active.
- `OSC/OSCLib.Tests/OSCLib.Tests.csproj` - `Validation`
  - OSC regression tests.
- `OSC/OSCLib.Smoke/OSCLib.Smoke.csproj` - `Validation`
  - Manual OSC smoke checks.

### Bring-up and Scenario Tests

- `Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj` - `Validation`
  - Direct decoder -> output path used for first-audio bring-up.
- `Test/AudioEx/AudioEx.csproj` - `Validation`
  - Full A/V stress scenario harness.
- `Test/NdiVideoReceive/NdiVideoReceive.csproj` - `Validation`
  - NDI receive scenario harness.
- `Test/VideoTest/VideoTest.csproj` - `Validation`
  - Video rendering scenario harness.

### Legacy Migration References

- `VideoLibs/Seko.OwnAudioNET.Video/Seko.OwnAudioNET.Video.csproj` - `Legacy-Migration`
- `VideoLibs/Seko.OwnAudioNET.Video.Engine/Seko.OwnAudioNET.Video.Engine.csproj` - `Legacy-Migration`
- `VideoLibs/Seko.OwnAudioNET.Video.NDI/Seko.OwnAudioNET.Video.NDI.csproj` - `Legacy-Migration`
- `VideoLibs/Seko.OwnAudioNET.Video.Avalonia/Seko.OwnAudioNET.Video.Avalonia.csproj` - `Legacy-Migration`
- `VideoLibs/Seko.OwnAudioNET.Video.SDL3/Seko.OwnAudioNET.Video.SDL3.csproj` - `Legacy-Migration`

## Consolidated Considerations Applied Across Projects

- Migration strategy is side-by-side: build `S.Media.*` first, remove legacy after behavior parity.
- `IMediaPlayer` contract is inheritance-based (`IMediaPlayer : IAudioVideoMixer`).
- IDs for media sources/outputs are implementation-generated GUIDs and process-lifetime unique.
- Deterministic error contracts are required (`0` success, non-zero owned failures).
- Validation precedence is explicit (for example disposed/invalid frame checks before running-state checks where required).
- Clock model uses one field with mode selection (`External`, `AudioLed`, `VideoLed`, `Hybrid`) and invalid combos must fail with config errors.
- `VideoFrame` invariants are strict for pixel format, plane data, stride/length consistency.
- Pixel format negotiation prefers compatible/preferred formats and then closest fallback where supported.
- OpenGL clone policy ownership is centralized in `OpenGLVideoEngine`; adapters project state and do not own decode/session behavior.
- PortAudio behavior follows explicit default-device semantics (`CreateOutputByIndex(-1)`), host API filtering, and blocking output push semantics.
- Linux host API convenience aliases for `pulse`/`pulseaudio` are normalized while preserving deterministic config validation.
- NDI engine initialization and diagnostics startup are treated failure-atomically; lifecycle paths are idempotent/deterministic.
- Acronym naming is standardized (`NDI`, `MIDI`) and docs are kept in sync with implementation.

## Related Docs

- `Doc/project-implementation-stages.md`
- `Doc/refactor-considerations-log.md`
- `Media/IMPLEMENTATION-STATUS.md`

## Folder-Level Status Files

- `Audio/PROJECT-STATUS.md`
- `Media/PROJECT-STATUS.md`
- `Media/S.Media.Core/PROJECT-STATUS.md`
- `Media/S.Media.FFmpeg/PROJECT-STATUS.md`
- `Media/S.Media.PortAudio/PROJECT-STATUS.md`
- `Media/S.Media.MIDI/PROJECT-STATUS.md`
- `Media/S.Media.NDI/PROJECT-STATUS.md`
- `Media/S.Media.OpenGL/PROJECT-STATUS.md`
- `Media/S.Media.OpenGL.Avalonia/PROJECT-STATUS.md`
- `Media/S.Media.OpenGL.SDL3/PROJECT-STATUS.md`
- `NDI/PROJECT-STATUS.md`
- `OSC/PROJECT-STATUS.md`
- `Test/PROJECT-STATUS.md`
- `VideoLibs/PROJECT-STATUS.md`

