using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Diagnostics;

/// <summary>
/// Diagnostics snapshot for <see cref="Output.AvaloniaVideoOutput"/>.
/// Kept for backwards compatibility — new code should use
/// <see cref="VideoOutputDiagnosticsSnapshot"/> directly.
/// </summary>
public readonly record struct AvaloniaOutputDebugInfo(
    long FramesPresented,
    long FramesDropped,
    long FramesCloned,
    double LastUploadMs,
    double LastPresentMs,
    OpenGLSurfaceMetadata Surface)
{
    /// <summary>Converts to the unified snapshot type.</summary>
    public VideoOutputDiagnosticsSnapshot ToSnapshot() =>
        new(FramesPresented, FramesDropped, FramesCloned, LastUploadMs, LastPresentMs, Surface);
}
