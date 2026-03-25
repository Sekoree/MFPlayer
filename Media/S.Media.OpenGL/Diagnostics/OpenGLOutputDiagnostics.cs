using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Diagnostics;

public readonly record struct OpenGLOutputDiagnostics(
    long FramesPresented,
    long FramesDropped,
    long FramesCloned,
    double LastUploadMs,
    double LastPresentMs,
    OpenGLSurfaceMetadata Surface);

