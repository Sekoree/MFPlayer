using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Diagnostics;

/// <summary>
/// Per-output diagnostics snapshot for an <see cref="OpenGLVideoOutput"/>.
/// Kept for backwards compatibility — new code should use
/// <see cref="VideoOutputDiagnosticsSnapshot"/> directly.
/// </summary>
public readonly record struct OpenGLOutputDebugInfo(
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
