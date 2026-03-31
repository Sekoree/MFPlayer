namespace S.Media.OpenGL.Output;

public sealed record OpenGLCloneOptions
{
    public OpenGLCloneMode Mode { get; init; } = OpenGLCloneMode.SharedTexture;

    /// <summary>
    /// When <see langword="true"/> (default), the clone automatically tracks the parent
    /// output's surface dimensions.
    /// </summary>
    public bool AutoResizeToParent { get; init; } = true;

    // Reserved until shared-context path (CloneMode) is implemented (see Issue B2).
    internal bool ShareParentColorPipeline { get; init; } = true;

    internal bool FailIfContextSharingUnavailable { get; init; } = true;

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;

    public int? MaxCloneDepth { get; init; }

    public OpenGLClonePixelFormatPolicy PixelFormatPolicy { get; init; } = OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath;
}
