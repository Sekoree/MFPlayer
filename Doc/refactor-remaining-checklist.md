# Refactor Remaining Checklist

Last updated: 2026-03-27

This checklist tracks concrete remaining migration work after the latest doc/status sync.

## Harness and App Migration

- [x] Port `Test/VideoStress` runtime logic from legacy `VideoTest` internals to `S.Media.*` APIs.
- [x] Replace legacy project references in `Test/AudioEx/AudioEx.csproj` with `S.Media.*` equivalents.
- [x] Replace legacy project references in `Test/NDIVideoReceive/NDIVideoReceive.csproj` with `S.Media.*` equivalents.
- [x] Retire `Test/VideoTest` — removed (VideoStress has parity).
- [x] Create and wire canonical `Test/VideoStress/VideoStress.csproj` harness scaffold on `S.Media.*` references.

## Legacy Dependency Removal

- [x] Remove `OwnAudioSharp.Basic` usage from active (non-legacy) projects.
- [x] Remove `ManagedBass`/`ManagedBass.Mix` from active migration targets.
- [x] Remove legacy `VideoLibs/Seko.OwnAudioNET.*` projects from `MFPlayer.sln` and filesystem.
- [ ] Remove legacy package entries from `Directory.Packages.props` after code-path cutover is complete.

## Plan and Governance Sync

- [ ] Run an explicit pass over `Media/S.Media.Core/implementation-readiness-checklist.md` and mark each gate as pass/fail.
- [ ] Add owner/date values to `Media/S.Media.Core/implementation-execution-schedule.md` tracker rows.
- [x] Keep migration status trackers in `Media/S.Media.FFmpeg/ffmpeg-migration-plan.md` and `Media/S.Media.OpenGL/opengl-migration-plan.md` aligned with implementation PRs.
- [x] Keep `Doc/hard-cut-sweep.md` as source-of-truth for cutover state (completed).

## Verification

- [x] Continue using one-command solution verification: `dotnet test MFPlayer.sln --logger "console;verbosity=minimal"`.
- [x] Add a periodic grep check for legacy references in active modules/harnesses (`Media`, `MFPlayer`, `Test`).

## Latest Validation Snapshot

- Date: `2026-03-27`
- `dotnet build MFPlayer.sln` — 0 warnings, 0 errors.
- `dotnet test MFPlayer.sln` — 259 passed, 4 skipped (heavy FFmpeg opt-in), 0 failed.
- `rg -n -g '*.{cs,csproj}' -e 'Seko\.OwnAudioNET' -e 'OwnAudio' Media MFPlayer Test` — no hits.
