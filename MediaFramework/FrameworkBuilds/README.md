# Framework build output

This folder is populated by **`MediaFramework/Scripts/build-framework.sh`** (Linux/macOS) or **`MediaFramework/Scripts/build-framework.ps1`** (Windows).

After a successful run you will have:

- **`net10.0/`** — All MFPlayer framework assemblies (for example `S.Media.Core.dll`, `S.Media.Playback.dll`), transitive managed dependencies (for example `FFmpeg.AutoGen.dll`, Avalonia when using `S.Media.Avalonia`), and a **`runtimes/`** tree for native assets (for example SDL3).

The meta project **`MFPlayer.Framework.Publish`** strips its own `MFPlayer.Framework.Publish.dll` from the drop so consumers only see real libraries.

## Default git behaviour

`MediaFramework/FrameworkBuilds/` contents are **gitignored** except this README so the repository stays source-only. Remove or adjust the `MediaFramework/FrameworkBuilds/` entry in the root `.gitignore` if you want to version prebuilt drops.

## Documentation

- How to reference these binaries: **`Doc/Libraries/Consuming-Framework-Builds.md`**
- Library catalogue: **`Doc/Libraries/Framework-Libraries-Reference.md`**
