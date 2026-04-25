# MFPlayer framework libraries

Reference documentation for every **library** project in this repository (as opposed to test/sample executables under `MediaFramework/Test/`).

| Document | Purpose |
|----------|---------|
| [Framework-Libraries-Reference.md](Framework-Libraries-Reference.md) | Full catalogue: roles, dependencies, key namespaces, native/runtime notes |
| [Consuming-Framework-Builds.md](Consuming-Framework-Builds.md) | Using **`MediaFramework/FrameworkBuilds/net10.0`** from another solution |

**Build a local drop:** from the repo root, run `MediaFramework/Scripts/build-framework.sh` (or `MediaFramework/Scripts/build-framework.ps1` on Windows). See `MediaFramework/FrameworkBuilds/README.md`.

**Solution integration:** all libraries are projects in `MFPlayer.sln`. The **`MediaFramework/Build/MFPlayer.Framework.Publish`** meta-project exists only to drive a unified `dotnet publish` output; it is not a runtime API surface.

**Application guides** (behaviour, not per-DLL reference): `Doc/Quick-Start.md`, `Doc/MediaPlayer-Guide.md`, `Doc/Usage-Guide.md`, `Doc/Clone-Sinks.md`, `Doc/Host-Application-Implementation-Checklist.md`.
