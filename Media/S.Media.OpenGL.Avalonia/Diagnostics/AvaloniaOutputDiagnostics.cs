using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Diagnostics;

public readonly record struct AvaloniaOutputDebugInfo(
    long FramesPresented,
    long FramesCloned,
    bool IsCloneActive,
    OpenGLSurfaceMetadata Surface);
