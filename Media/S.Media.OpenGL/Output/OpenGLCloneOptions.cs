namespace S.Media.OpenGL.Output;

public sealed record OpenGLCloneOptions
{
    public OpenGLCloneMode Mode { get; init; } = OpenGLCloneMode.SharedTexture;

    public bool AutoResizeToParent { get; init; } = true;

    public bool ShareParentColorPipeline { get; init; } = true;

    public bool FailIfContextSharingUnavailable { get; init; } = true;

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;

    public int? MaxCloneDepth { get; init; }

    public OpenGLClonePixelFormatPolicy PixelFormatPolicy { get; init; } = OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath;
}

