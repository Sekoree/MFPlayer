using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Video-only mixer that provides OwnAudio-style source/output management over <see cref="IVideoTransportEngine"/>.
/// </summary>
public sealed class VideoMixer : IVideoMixer
{
    private readonly IVideoTransportEngine _engine;
    private readonly bool _ownsEngine;
    private bool _disposed;

    public VideoMixer(VideoTransportEngineConfig? config = null)
        : this(new VideoTransportEngine(config), ownsEngine: true)
    {
    }

    public VideoMixer(IVideoTransportEngine engine, bool ownsEngine = false)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
        _engine.SourceError += OnEngineSourceError;
    }

    public VideoTransportEngineConfig Config => _engine.Config;

    public IVideoClock Clock => _engine.Clock;

    public double Position => _engine.Position;

    public bool IsRunning => _engine.IsRunning;

    public int SourceCount => _engine.SourceCount;

    public int OutputCount => _engine.OutputCount;

    public event EventHandler<VideoErrorEventArgs>? SourceError;

    public bool AddSource(IVideoSource source)
    {
        ThrowIfDisposed();
        return _engine.AddVideoSource(source);
    }

    public bool RemoveSource(IVideoSource source)
    {
        ThrowIfDisposed();
        return _engine.RemoveVideoSource(source);
    }

    public bool RemoveSource(Guid sourceId)
    {
        ThrowIfDisposed();
        return _engine.RemoveVideoSource(sourceId);
    }

    public IVideoSource[] GetSources()
    {
        ThrowIfDisposed();
        return _engine.GetVideoSources();
    }

    public void ClearSources()
    {
        ThrowIfDisposed();
        _engine.ClearVideoSources();
    }

    public bool AddOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        return _engine.AddVideoOutput(output);
    }

    public bool RemoveOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        return _engine.RemoveVideoOutput(output);
    }

    public bool RemoveOutput(Guid outputId)
    {
        ThrowIfDisposed();
        return _engine.RemoveVideoOutput(outputId);
    }

    public IVideoOutput[] GetOutputs()
    {
        ThrowIfDisposed();
        return _engine.GetVideoOutputs();
    }

    public void ClearOutputs()
    {
        ThrowIfDisposed();
        _engine.ClearVideoOutputs();
    }

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

        if (_ownsEngine)
            _engine.Dispose();

        _disposed = true;
    }

    private void OnEngineSourceError(object? sender, VideoErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoMixer));
    }
}

