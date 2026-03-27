# MFPlayer Docs

This folder contains setup guides, architecture docs, and status tracking for the S.Media framework.

## Guides

- [`setup-prerequisites.md`](setup-prerequisites.md)
  - What to install, what to build, and environment variables.
- [`audioex-setup.md`](audioex-setup.md)
  - How the AudioEx stress-test harness is wired.
- [`ndi-dropped-frame-remediation-plan.md`](ndi-dropped-frame-remediation-plan.md)
  - Findings and phased remediation plan for dropped frames in the `S.Media.NDI` receive pipeline.

## Architecture & Refactoring

- [`project-implementation-stages.md`](project-implementation-stages.md)
  - Snapshot table for every project in `MFPlayer.sln` and its current implementation stage.
- [`project-status-notes.md`](project-status-notes.md)
  - Per-project stage notes plus consolidated migration considerations.
- [`refactor-considerations-log.md`](refactor-considerations-log.md)
  - Consolidated log of contract decisions and migration considerations agreed during the `S.Media.*` refactor.
- [`hard-cut-sweep.md`](hard-cut-sweep.md)
  - Migration sweep status for legacy `OwnAudio` dependency removal (completed).

### Folder Status Files

- [`Audio/PROJECT-STATUS.md`](../Audio/PROJECT-STATUS.md)
- [`Media/PROJECT-STATUS.md`](../Media/PROJECT-STATUS.md)
- [`NDI/PROJECT-STATUS.md`](../NDI/PROJECT-STATUS.md)
- [`OSC/PROJECT-STATUS.md`](../OSC/PROJECT-STATUS.md)
- [`Test/PROJECT-STATUS.md`](../Test/PROJECT-STATUS.md)

## Where to look for runnable references

- `Test/SimpleAudioTest/Program.cs` — basic FFmpeg → PortAudio playback
- `Test/SimpleVideoTest/Program.cs` — basic FFmpeg → SDL3 video playback
- `Test/MediaPlayerTest/Program.cs` — full A/V playback via MediaPlayer
- `Test/AVMixerTest/Program.cs` — A/V playback via AudioVideoMixer directly
- `Test/NDIVideoReceive/Program.cs` — NDI receive with AV mixer + SDL3
