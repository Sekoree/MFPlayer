using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Output;

public sealed record AvaloniaCloneOptions
{
    public OpenGLCloneMode CloneMode { get; init; } = OpenGLCloneMode.SharedTexture;

    public bool AutoTrackParentSize { get; init; } = true;

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;

    public bool FailIfParentDisposed { get; init; } = true;

    public int? MaxCloneDepth { get; init; }
}

