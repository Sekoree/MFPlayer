using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.SDL3;

public sealed record SDL3CloneOptions
{
    public OpenGLCloneMode CloneMode { get; init; } = OpenGLCloneMode.CopyFallback;

    public bool AutoTrackParentSize { get; init; } = true;

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;


    public int? MaxCloneDepth { get; init; }
}
