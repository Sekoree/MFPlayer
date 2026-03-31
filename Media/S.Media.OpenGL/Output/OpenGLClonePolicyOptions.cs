namespace S.Media.OpenGL.Output;

public sealed record OpenGLClonePolicyOptions
{
    public int MaxCloneDepth { get; init; } = 4;

    /// <summary>
    /// When <see langword="true"/> (default), attempting to attach an output as a clone of
    /// itself returns <see cref="S.Media.Core.Errors.MediaErrorCode.OpenGLCloneSelfAttachRejected"/>.
    /// </summary>
    public bool RejectSelfAttach { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), the engine checks for cycles before attaching a
    /// clone and returns <see cref="S.Media.Core.Errors.MediaErrorCode.OpenGLCloneCycleDetected"/>
    /// if attaching would form a loop.
    /// </summary>
    public bool RejectCycles { get; init; } = true;

    internal OpenGLClonePixelFormatPolicy DefaultPixelFormatPolicy { get; init; } = OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath;

    /// <summary>
    /// When <see langword="true"/> (default), clones may be attached while the parent output
    /// is running. Set to <see langword="false"/> to require the parent to be stopped before
    /// any clone attachment.
    /// </summary>
    public bool AllowAttachWhileRunning { get; init; } = true;

    // TODO(B2): AttachPauseBudgetFrames and WarnOnPauseBudgetExceeded reserved for the
    //           shared-context pause-during-attach path. Restore when B2 is implemented.

    public OpenGLClonePolicyOptions Normalize()
    {
        return this with
        {
            MaxCloneDepth = Math.Max(1, MaxCloneDepth),
        };
    }
}
