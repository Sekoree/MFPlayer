using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Output;

public sealed class AvaloniaVideoOutput : IVideoOutput
{
    private readonly Lock _gate = new();
    private readonly OpenGLVideoOutput _output;
    private readonly OpenGLVideoEngine _engine;
    private bool _disposed;

    internal AvaloniaVideoOutput(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
    {
        _output = output;
        _engine = engine;
        IsClone = isClone;

        var add = _engine.AddOutput(_output);
        if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
        {
            throw new InvalidOperationException($"Failed to register output in OpenGL engine. Code={add}.");
        }
    }

    public AvaloniaVideoOutput() : this(new OpenGLVideoOutput(), new OpenGLVideoEngine(), isClone: false)
    {
    }

    public Guid Id => _output.Id;

    public bool IsClone { get; }

    public Guid? CloneParentOutputId => _output.CloneParentOutputId;

    public OpenGLVideoOutput Output => _output;

    public int Start(VideoOutputConfig config) => _output.Start(config);

    public int Stop() => _output.Stop();

    public int PushFrame(VideoFrame frame) => _output.PushFrame(frame);

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime) => _output.PushFrame(frame, presentationTime);

    public int CreateClone(in AvaloniaCloneOptions options, out AvaloniaVideoOutput? cloneOutput)
    {
        cloneOutput = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            var create = _engine.CreateCloneOutput(Id, ToOpenGlCloneOptions(options), out var cloneBaseOutput);
            if (create != MediaResult.Success || cloneBaseOutput is not OpenGLVideoOutput glClone)
            {
                return create != MediaResult.Success ? create : (int)MediaErrorCode.OpenGLCloneCreationFailed;
            }

            cloneOutput = new AvaloniaVideoOutput(glClone, _engine, isClone: true);
            return MediaResult.Success;
        }
    }

    public int AttachClone(AvaloniaVideoOutput? cloneOutput, in AvaloniaCloneOptions options)
    {
        if (cloneOutput is null)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            var add = _engine.AddOutput(cloneOutput.Output);
            if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
            {
                return add;
            }

            return _engine.AttachCloneOutput(Id, cloneOutput.Id);
        }
    }

    public int DetachClone(Guid cloneOutputId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            var detach = _engine.DetachCloneOutput(Id, cloneOutputId);
            if (detach != MediaResult.Success)
            {
                return detach;
            }

            return MediaResult.Success;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = _engine.RemoveOutput(Id);
        if (!IsClone)
        {
            _engine.Dispose();
        }

        _output.Dispose();
    }

    private static OpenGLCloneOptions ToOpenGlCloneOptions(in AvaloniaCloneOptions options)
    {
        return new OpenGLCloneOptions
        {
            Mode = options.CloneMode,
            AutoResizeToParent = options.AutoTrackParentSize,
            HudMode = options.HudMode,
            MaxCloneDepth = options.MaxCloneDepth,
        };
    }
}
