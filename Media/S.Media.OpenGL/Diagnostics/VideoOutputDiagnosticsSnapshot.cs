using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Diagnostics;

/// <summary>
/// Unified diagnostics snapshot shared by all OpenGL output backends.
/// Replaces the three separate per-backend debug-info structs.
/// </summary>
public readonly record struct VideoOutputDiagnosticsSnapshot(
    /// <summary>Total frames successfully presented since <c>Start</c>.</summary>
    long FramesPresented,
    /// <summary>Total frames dropped (arrived too late or queue was full).</summary>
    long FramesDropped,
    /// <summary>Total frames forwarded to at least one clone output.</summary>
    long FramesCloned,
    /// <summary>
    /// Time spent uploading the last frame to GPU textures, in milliseconds.
    /// <c>0</c> if the backend does not instrument uploads.
    /// </summary>
    double LastUploadMs,
    /// <summary>
    /// Time elapsed between the frame arriving at <c>PushFrame</c> and the surface being
    /// committed, in milliseconds. <c>0</c> if not instrumented.
    /// </summary>
    double LastPresentMs,
    /// <summary>Current surface geometry and pixel format.</summary>
    OpenGLSurfaceMetadata Surface);

