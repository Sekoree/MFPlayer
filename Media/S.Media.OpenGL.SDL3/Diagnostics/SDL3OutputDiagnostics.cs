using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.SDL3.Diagnostics;

public readonly record struct SDL3OutputDiagnostics(
    long FramesPresented,
    long FramesCloned,
    long FramesDropped,
    double LastPresentMs,
    OpenGLSurfaceMetadata Surface);

