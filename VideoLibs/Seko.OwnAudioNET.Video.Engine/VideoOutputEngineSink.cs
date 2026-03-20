using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Adapts an <see cref="IVideoOutputEngine"/> to the <see cref="IVideoOutput"/> contract expected by VideoMixer.
/// </summary>
public sealed class VideoOutputEngineSink : IVideoOutput
{
    private readonly IVideoOutputEngine _engine;
    private readonly bool _ownsEngine;
    private bool _disposed;

    public VideoOutputEngineSink(IVideoOutputEngine engine, bool ownsEngine = false)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public IVideoSource? Source { get; private set; }

    public bool IsAttached => Source != null;

    public bool AttachSource(IVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        Source = source;
        return true;
    }

    public void DetachSource()
    {
        ThrowIfDisposed();
        Source = null;
    }

    public bool PushFrame(VideoFrame frame, double masterTimestamp)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        return _engine.PushFrame(frame, masterTimestamp);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Source = null;

        if (_ownsEngine)
            _engine.Dispose();

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoOutputEngineSink));
    }
}

