namespace S.Media.OpenGL.Output;

public sealed record OpenGLClonePolicyOptions
{
    public int MaxCloneDepth { get; init; } = 4;

    internal bool RejectSelfAttach { get; init; } = true;

    internal bool RejectCycles { get; init; } = true;

    internal OpenGLClonePixelFormatPolicy DefaultPixelFormatPolicy { get; init; } = OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath;

    internal bool AllowAttachWhileRunning { get; init; } = true;

    internal int AttachPauseBudgetFrames { get; init; } = 1;

    internal bool WarnOnPauseBudgetExceeded { get; init; } = true;

    public OpenGLClonePolicyOptions Normalize()
    {
        return this with
        {
            MaxCloneDepth = Math.Max(1, MaxCloneDepth),
            AttachPauseBudgetFrames = Math.Max(0, AttachPauseBudgetFrames),
        };
    }
}
