namespace S.Media.OpenGL.SDL3;

/// <summary>
/// A single key-value pair pushed to the SDL3 HUD overlay.
/// Replaces the deleted <c>S.Media.Core.Diagnostics.DebugInfo</c>.
/// </summary>
public readonly record struct HudEntry(string Key, object? Value);

