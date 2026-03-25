using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Diagnostics;

public readonly record struct AvaloniaOutputDiagnostics(
    long FramesPresented,
    long FramesCloned,
    bool IsCloneActive,
    OpenGLSurfaceMetadata Surface);

