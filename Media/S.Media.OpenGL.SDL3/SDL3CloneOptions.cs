using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.SDL3;

public sealed record SDL3CloneOptions
{
    public OpenGLCloneMode CloneMode { get; init; } = OpenGLCloneMode.CopyFallback;

    public bool AutoTrackParentSize { get; init; } = true;

    public OpenGLHUDCloneMode HudMode { get; init; } = OpenGLHUDCloneMode.Independent;

    /// <inheritdoc cref="AvaloniaCloneOptions.FailIfParentDisposed"/>
    [Obsolete("FailIfParentWindowClosed is not yet implemented and is silently ignored. It will be wired in a future release.")]
    public bool FailIfParentWindowClosed { get; init; } = true;

    public int? MaxCloneDepth { get; init; }
}
