using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Output;

public sealed class AvaloniaVideoOutput : OpenGLWrapperVideoOutput
{
    private readonly Lock _gate = new();
    private Action<VideoFrame>? _frameCallback;

    // Keep existing public constructor for backwards compatibility, but prefer Create().
    internal AvaloniaVideoOutput(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
        : base(output, engine, isClone)
    {
    }

    public AvaloniaVideoOutput() : this(new OpenGLVideoOutput(), new OpenGLVideoEngine(), isClone: false)
    {
    }

    /// <summary>
    /// Creates a new <see cref="AvaloniaVideoOutput"/> without throwing on failure.
    /// Preferred over the public constructor for code that follows the framework's
    /// integer-return-code convention.
    /// </summary>
    public static int Create(out AvaloniaVideoOutput? result)
    {
        result = null;
        var output = new OpenGLVideoOutput();
        var engine = new OpenGLVideoEngine();
        var add = engine.AddOutput(output);
        if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
        {
            output.Dispose();
            engine.Dispose();
            return add;
        }

        result = new AvaloniaVideoOutput(output, engine, isClone: false);
        return MediaResult.Success;
    }

    /// <summary>The underlying <see cref="OpenGLVideoOutput"/> managed by this view.</summary>
    public OpenGLVideoOutput InternalOutput => Output;

    /// <summary>
    /// Binds a callback that will be invoked with each successfully pushed
    /// <see cref="VideoFrame"/>. Use this to forward frames from the mixer into an
    /// <c>AvaloniaOpenGLHostControl</c>:
    /// <code>avOut.BindFrameCallback(control.PushFrame);</code>
    /// </summary>
    public void BindFrameCallback(Action<VideoFrame>? callback)
    {
        lock (_gate)
        {
            _frameCallback = callback;
        }
    }

    public override int PushFrame(VideoFrame frame)
    {
        var r = Output.PushFrame(frame);
        if (r == MediaResult.Success)
        {
            Action<VideoFrame>? cb;
            lock (_gate) { cb = _frameCallback; }
            cb?.Invoke(frame);
        }

        return r;
    }

    public override int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        var r = Output.PushFrame(frame, presentationTime);
        if (r == MediaResult.Success)
        {
            Action<VideoFrame>? cb;
            lock (_gate) { cb = _frameCallback; }
            cb?.Invoke(frame);
        }

        return r;
    }

    public int CreateClone(in AvaloniaCloneOptions options, out AvaloniaVideoOutput? cloneOutput)
    {
        cloneOutput = null;

        lock (_gate)
        {
            if (!Output.IsRunning && Output.State == VideoOutputState.Stopped)
            {
                // allow creation even when stopped (policy allows it by default)
            }

            var create = CreateCloneCore(ToOpenGlCloneOptions(options), out var glClone);
            if (create != MediaResult.Success || glClone is null)
                return create != MediaResult.Success ? create : (int)MediaErrorCode.OpenGLCloneCreationFailed;

            cloneOutput = new AvaloniaVideoOutput(glClone, Engine, isClone: true);
            return MediaResult.Success;
        }
    }

    public int AttachClone(AvaloniaVideoOutput? cloneOutput, in AvaloniaCloneOptions options)
    {
        if (cloneOutput is null)
            return (int)MediaErrorCode.MediaInvalidArgument;

        lock (_gate)
        {
            var add = Engine.AddOutput(cloneOutput.Output);
            if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
                return add;

            // B7 fix: forward options to the engine rather than discarding them.
            var attach = Engine.AttachCloneOutput(Id, cloneOutput.Id, ToOpenGlCloneOptions(options));
            if (attach == MediaResult.Success)
                cloneOutput.IsClone = true;
            return attach;
        }
    }

    public int DetachClone(Guid cloneOutputId) => Engine.DetachCloneOutput(Id, cloneOutputId);

    protected override void OnBeforeDispose()
    {
        lock (_gate)
        {
            _frameCallback = null;
        }
    }

    private static OpenGLCloneOptions ToOpenGlCloneOptions(in AvaloniaCloneOptions options) =>
        new()
        {
            Mode = options.CloneMode,
            AutoResizeToParent = options.AutoTrackParentSize,
            HudMode = options.HudMode,
            MaxCloneDepth = options.MaxCloneDepth,
        };
}
