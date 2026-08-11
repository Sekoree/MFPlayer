# S.Media.Time

Clocks and A/V synchronization for MFPlayer: monotonic clocks, session timelines, output sync groups and rate discipline.

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.

## `Unwired/`

Types under `Unwired/` are built and tested but reached by **no production code path**. They are the
foundations of planned features, not dead code, and each file's header names the design doc it belongs
to and what would connect it. The folder exists so that "planned, built, not yet connected" is
distinguishable from "load-bearing" at a glance - the only cost these types actually impose is that a
reader otherwise has to grep for callers to find out which they are.

Currently: `OutputSyncGroup`, `VideoPresentSyncGroup` (multi-output genlock, `Doc/HaPlay-MultiOutput-Sync.md`),
and `SourceTimeline`, `LiveTimelineDriver`, `SourceSyncGroup` (the P7 live-ingest model, demonstrated by
`Tools/LiveReceiveProbe`). Their companions live in `S.Media.Routing/Video/Unwired/` and
`S.Media.Core/Video/Unwired/`.
