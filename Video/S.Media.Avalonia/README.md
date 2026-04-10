# S.Media.Avalonia

Avalonia OpenGL video output for MFPlayer.

## What it provides

- `AvaloniaOpenGlVideoOutput` control based on `OpenGlControlBase`
- `IVideoOutput` compatible API (`Open`, `StartAsync`, `StopAsync`, `Mixer`, `Clock`)
- Reuses `VideoMixer` and `VideoPtsClock`
- Lightweight diagnostics via `GetDiagnosticsSnapshot()`

## Basic usage

1. Place `AvaloniaOpenGlVideoOutput` in your Avalonia visual tree.
2. Call `Open(...)` once.
3. Add an `IVideoChannel` to `Mixer` and set it active.
4. Start decoder and call `StartAsync()`.

