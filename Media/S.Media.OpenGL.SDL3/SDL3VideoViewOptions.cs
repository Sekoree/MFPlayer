namespace S.Media.OpenGL.SDL3;

public sealed record SDL3VideoViewOptions
{
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public string PreferredDescriptor { get; init; } = "x11-window";
}

