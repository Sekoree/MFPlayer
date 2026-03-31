namespace S.Media.OpenGL.Output;

public sealed record OpenGLCloneOptions
{
    public OpenGLCloneMode Mode { get; init; } = OpenGLCloneMode.CopyFallback;

    /// <summary>
    /// When <see langword="true"/> (default), the clone automatically tracks the parent
    /// output's surface dimensions.
    /// </summary>
    public bool AutoResizeToParent { get; init; } = true;

    // TODO(B2): ShareParentColorPipeline and FailIfContextSharingUnavailable reserved for
    //           the shared-GL-context path. Restore as public properties when B2 is implemented.

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;

    public int? MaxCloneDepth { get; init; }

    public OpenGLClonePixelFormatPolicy PixelFormatPolicy { get; init; } = OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath;
}
