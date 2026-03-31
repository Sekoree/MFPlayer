using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Output;

public sealed record AvaloniaCloneOptions
{
    public OpenGLCloneMode CloneMode { get; init; } = OpenGLCloneMode.CopyFallback;

    public bool AutoTrackParentSize { get; init; } = true;

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;

    /// <summary>
    /// Reserved: not yet implemented. Setting this value has no effect.
    /// Will be wired in a future release when parent-lifetime tracking is active.
    /// </summary>
    [Obsolete("FailIfParentDisposed is not yet implemented and is silently ignored. It will be wired in a future release.")]
    public bool FailIfParentDisposed { get; init; } = true;

    public int? MaxCloneDepth { get; init; }
}
