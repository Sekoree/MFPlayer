using SDL3;

namespace S.Media.OpenGL.SDL3;

public sealed record SDL3VideoViewOptions
{
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public string PreferredDescriptor { get; init; } = "x11-window";

    public string WindowTitle { get; init; } = "MFPlayer SDL3 Preview";

    public SDL.WindowFlags WindowFlags { get; init; } = SDL.WindowFlags.Resizable;

    public bool ShowOnInitialize { get; init; } = true;

    public bool BringToFrontOnShow { get; init; } = true;

    public bool PreserveAspectRatio { get; init; } = true;
}
