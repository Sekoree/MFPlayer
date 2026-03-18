using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Bridges a source's frame-ready callbacks into an output sink engine.
/// Keeps source attachment outside of the engine itself.
/// </summary>
public sealed class VideoSourceSinkBridge : IDisposable
{
    private readonly IVideoOutputEngine _engine;
    private readonly Action<VideoFrame, double> _frameHandler;

    private IVideoSource? _source;
    private bool _disposed;

    public VideoSourceSinkBridge(IVideoOutputEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _frameHandler = OnFrameReady;
    }

    public IVideoSource? Source => _source;

    public bool IsAttached => _source != null;

    public bool AttachSource(IVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (ReferenceEquals(_source, source))
            return true;

        DetachSource();
        source.FrameReadyFast += _frameHandler;
        _source = source;
        return true;
    }

    public void DetachSource()
    {
        ThrowIfDisposed();

        if (_source == null)
            return;

        _source.FrameReadyFast -= _frameHandler;
        _source = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_source != null)
        {
            _source.FrameReadyFast -= _frameHandler;
            _source = null;
        }

        _disposed = true;
    }

    private void OnFrameReady(VideoFrame frame, double masterTimestamp)
    {
        try
        {
            _engine.PushFrame(frame, masterTimestamp);
        }
        catch
        {
            // Best effort bridge path.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoSourceSinkBridge));
    }
}


