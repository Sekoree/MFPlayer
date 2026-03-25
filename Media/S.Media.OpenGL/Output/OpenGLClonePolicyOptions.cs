namespace S.Media.OpenGL.Output;

public sealed record OpenGLClonePolicyOptions
{
    public int MaxCloneDepth { get; init; } = 4;

    public bool RejectSelfAttach { get; init; } = true;

    public bool RejectCycles { get; init; } = true;

    public OpenGLClonePixelFormatPolicy DefaultPixelFormatPolicy { get; init; } = OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath;

    public bool AllowAttachWhileRunning { get; init; } = true;

    public int AttachPauseBudgetFrames { get; init; } = 1;

    public bool WarnOnPauseBudgetExceeded { get; init; } = true;

    public OpenGLClonePolicyOptions Normalize()
    {
        return this with
        {
            MaxCloneDepth = Math.Max(1, MaxCloneDepth),
            AttachPauseBudgetFrames = Math.Max(0, AttachPauseBudgetFrames),
        };
    }
}

