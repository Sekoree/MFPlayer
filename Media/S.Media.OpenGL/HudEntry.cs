namespace S.Media.OpenGL;

/// <summary>
/// A single key-value diagnostic pair pushed to a HUD overlay renderer.
/// Used by both <c>SDL3VideoView.UpdateHud</c> and <c>AvaloniaOpenGLHostControl.UpdateHud</c>.
/// Replaces the deleted <c>S.Media.Core.Diagnostics.DebugInfo</c>.
/// </summary>
public readonly record struct HudEntry(string Key, object? Value);

