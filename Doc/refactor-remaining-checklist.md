# Refactor Remaining Checklist

Last updated: 2026-03-26

This checklist tracks concrete remaining migration work after the latest doc/status sync.

## Harness and App Migration

- [x] Port `Test/VideoStress` runtime logic from legacy `VideoTest` internals to `S.Media.*` APIs.
- [x] Replace legacy project references in `Test/AudioEx/AudioEx.csproj` with `S.Media.*` equivalents.
- [x] Replace legacy project references in `Test/NdiVideoReceive/NdiVideoReceive.csproj` with `S.Media.*` equivalents.
- [ ] Decide retirement point for `Test/VideoTest/VideoTest.csproj` after `VideoStress` reaches parity.
- [x] Create and wire canonical `Test/VideoStress/VideoStress.csproj` harness scaffold on `S.Media.*` references.

## Legacy Dependency Removal

- [ ] Remove `OwnAudioSharp.Basic` usage from active (non-legacy) projects.
- [ ] Remove `ManagedBass`/`ManagedBass.Mix` from active migration targets if no longer required.
- [ ] Remove legacy `VideoLibs/Seko.OwnAudioNET.*` projects from `MFPlayer.sln` once parity gates pass.
- [ ] Remove legacy package entries from `Directory.Packages.props` after code-path cutover is complete.

## Plan and Governance Sync

- [ ] Run an explicit pass over `Media/S.Media.Core/implementation-readiness-checklist.md` and mark each gate as pass/fail.
- [ ] Add owner/date values to `Media/S.Media.Core/implementation-execution-schedule.md` tracker rows.
- [x] Keep migration status trackers in `Media/S.Media.FFmpeg/ffmpeg-migration-plan.md` and `Media/S.Media.OpenGL/opengl-migration-plan.md` aligned with implementation PRs.
- [x] Keep `Doc/hard-cut-sweep.md` as source-of-truth for cutover state (planned vs complete actions).

## Verification

- [x] Continue using one-command solution verification: `dotnet test MFPlayer.sln --logger "console;verbosity=minimal"`.
- [x] Add a periodic grep check for legacy references in active modules/harnesses (`Media`, `MFPlayer`, `Test`).

## Latest Pre-Removal Validation Snapshot

- Date: `2026-03-26`
- `RUN_HEAVY_FFMPEG_TESTS=1 dotnet test MFPlayer.sln --logger "console;verbosity=minimal"`
  - `PALib.Tests` passed `2`
  - `OSCLib.Tests` passed `18`
  - `S.Media.Core.Tests` passed `49`
  - `S.Media.FFmpeg.Tests` passed `97` (heavy opted in; no skips in this run)
  - `S.Media.PortAudio.Tests` passed `19`
  - `S.Media.MIDI.Tests` passed `14`
  - `S.Media.NDI.Tests` passed `15`
  - `S.Media.OpenGL.Tests` passed `38`
- `dotnet build Test/VideoStress/VideoStress.csproj --no-restore` succeeded (`0` errors).
- `dotnet run --project Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj -- --list-devices` succeeded and enumerated host APIs/devices.
- `dotnet build Test/AudioEx/AudioEx.csproj --no-restore` succeeded (`0` errors).
- `dotnet build Test/NdiVideoReceive/NdiVideoReceive.csproj --no-restore` succeeded (`0` errors).
- `dotnet run --project Test/AudioEx/AudioEx.csproj -- --list-devices` succeeded (host APIs/output devices enumerated).
- `dotnet run --project Test/NdiVideoReceive/NdiVideoReceive.csproj -- --list-sources --discover-seconds 1` succeeded (runtime initialized; no sources found in current environment).
- `dotnet run --project Test/NdiVideoReceive/NdiVideoReceive.csproj -- --discover-seconds 1 --preview-seconds 2` succeeded (source discovered, SDL3 preview loop pushed frames).
- `dotnet test MFPlayer.sln --logger "console;verbosity=minimal"` re-run after VideoStress runtime parity + FFmpeg test stabilization updates succeeded.
- `rg -n -g '*.{cs,csproj}' -e 'Seko\.OwnAudioNET' -e 'OwnAudio' Media MFPlayer Test`
  - No legacy hits in `Media/*` or `MFPlayer/*`.
  - Remaining hits are isolated to `Test/VideoTest` (legacy migration reference harness).

