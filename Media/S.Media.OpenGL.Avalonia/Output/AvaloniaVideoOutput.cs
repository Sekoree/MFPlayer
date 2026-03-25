using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.OpenGL.Avalonia.Output;

public sealed class AvaloniaVideoOutput : IVideoOutput
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, AvaloniaVideoOutput> _clones = [];
    private readonly OpenGLVideoOutput _output;
    private bool _disposed;

    internal AvaloniaVideoOutput(OpenGLVideoOutput output, bool isClone, Guid? parentId)
    {
        _output = output;
        IsClone = isClone;
        CloneParentOutputId = parentId;
    }

    public AvaloniaVideoOutput() : this(new OpenGLVideoOutput(), isClone: false, parentId: null)
    {
    }

    public Guid Id => _output.Id;

    public bool IsClone { get; }

    public Guid? CloneParentOutputId { get; private set; }

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

            cloneOutput = new AvaloniaVideoOutput(new OpenGLVideoOutput(), isClone: true, parentId: Id);
            _clones[cloneOutput.Id] = cloneOutput;
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

            if (cloneOutput.Id == Id)
            {
                return (int)MediaErrorCode.OpenGLCloneSelfAttachRejected;
            }

            if (cloneOutput.CloneParentOutputId.HasValue && cloneOutput.CloneParentOutputId.Value != Id)
            {
                return (int)MediaErrorCode.OpenGLCloneChildAlreadyAttached;
            }

            if (_clones.ContainsKey(cloneOutput.Id))
            {
                return (int)MediaErrorCode.OpenGLCloneAlreadyAttached;
            }

            cloneOutput.CloneParentOutputId = Id;
            _clones[cloneOutput.Id] = cloneOutput;
            return MediaResult.Success;
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

            if (!_clones.Remove(cloneOutputId, out var cloneOutput))
            {
                return (int)MediaErrorCode.OpenGLCloneNotAttached;
            }

            cloneOutput.CloneParentOutputId = null;
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
            foreach (var clone in _clones.Values)
            {
                clone.CloneParentOutputId = null;
            }

            _clones.Clear();
        }

        _output.Dispose();
    }
}

