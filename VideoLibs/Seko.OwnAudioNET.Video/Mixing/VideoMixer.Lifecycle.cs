namespace Seko.OwnAudioNET.Video.Mixing;

public sealed partial class VideoMixer
{
    public void Start()
    {
        ThrowIfDisposed();
        _engine.Start();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _engine.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        _engine.Stop();
    }

    public void Seek(double positionInSeconds)
    {
        ThrowIfDisposed();
        _engine.Seek(positionInSeconds);
    }

    public void Seek(double positionInSeconds, bool safeSeek)
    {
        ThrowIfDisposed();
        _engine.Seek(positionInSeconds, safeSeek);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _engine.SourceError -= OnEngineSourceError;

        try
        {
            ClearOutputs();
            ClearSources();
        }
        finally
        {
            if (_ownsEngine)
                _engine.Dispose();

            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoMixer));
    }
}

