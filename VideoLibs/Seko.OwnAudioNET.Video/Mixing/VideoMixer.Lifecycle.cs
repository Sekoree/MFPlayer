namespace Seko.OwnAudioNET.Video.Mixing;

public sealed partial class VideoMixer
{
    public void Start()
    {
        ThrowIfDisposed();
        _transport.Start();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _transport.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        _transport.Stop();
    }

    public void Seek(double positionInSeconds)
    {
        ThrowIfDisposed();
        _transport.Seek(positionInSeconds);
    }

    public void Seek(double positionInSeconds, bool safeSeek)
    {
        ThrowIfDisposed();
        _transport.Seek(positionInSeconds, safeSeek);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _transport.SourceError -= OnEngineSourceError;

        try
        {
            SetActiveSourceInternal(null, raiseEvent: false);
            ClearSources();
        }
        finally
        {
            _transport.Dispose();

            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoMixer));
    }
}

