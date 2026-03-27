using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Diagnostics;

public readonly record struct OpenGLOutputDebugInfo(
    long FramesPresented,
    long FramesDropped,
    long FramesCloned,
    double LastUploadMs,
    double LastPresentMs,
    OpenGLSurfaceMetadata Surface);
