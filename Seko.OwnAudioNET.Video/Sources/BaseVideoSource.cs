using Seko.OwnAudioNET.Video.Events;

namespace Seko.OwnAudioNET.Video.Sources;

/// <summary>
/// Base implementation for video sources, providing transport state, synchronization metadata,
/// and common event plumbing.
/// </summary>
public abstract class BaseVideoSource : IVideoSource
{
    private int _state = (int)VideoPlaybackState.Stopped;
    private long _samplePosition;
    private volatile string? _syncGroupId;
    private volatile bool _isSynchronized;
    private double _positionSeconds;
    private bool _disposed;

    /// <summary>Initializes a new base instance.</summary>
    protected BaseVideoSource()
    {
        Id = Guid.NewGuid();
    }

    /// <inheritdoc/>
    public Guid Id { get; }

    /// <inheritdoc/>
    public VideoPlaybackState State => (VideoPlaybackState)Volatile.Read(ref _state);

    /// <inheritdoc/>
    public abstract VideoStreamInfo StreamInfo { get; }

    /// <inheritdoc/>
    public virtual double Position => Volatile.Read(ref _positionSeconds);

    /// <inheritdoc/>
    public virtual double Duration => StreamInfo.Duration.TotalSeconds;

    /// <inheritdoc/>
    public abstract bool IsEndOfStream { get; }

    /// <inheritdoc/>
    public abstract bool IsHardwareDecoding { get; }

    /// <inheritdoc/>
    public abstract double StartOffset { get; set; }

    /// <inheritdoc/>
    public abstract bool IsAttachedToClock { get; }

    /// <inheritdoc/>
    public long SamplePosition
    {
        get => Interlocked.Read(ref _samplePosition);
        protected set => Interlocked.Exchange(ref _samplePosition, value);
    }

    /// <inheritdoc/>
    public string? SyncGroupId
    {
        get => _syncGroupId;
        set => _syncGroupId = value;
    }

    /// <inheritdoc/>
    public bool IsSynchronized
    {
        get => _isSynchronized;
        set => _isSynchronized = value;
    }

    /// <inheritdoc/>
    public event EventHandler<VideoPlaybackStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<VideoErrorEventArgs>? Error;

    /// <inheritdoc/>
    public event EventHandler<VideoFrameReadyEventArgs>? FrameReady;

    /// <inheritdoc/>
    public event Action<VideoFrame, double>? FrameReadyFast;

    /// <inheritdoc/>
    public event EventHandler<VideoStreamInfoChangedEventArgs>? StreamInfoChanged;

    /// <inheritdoc/>
    public abstract bool TryGetFrameAtTime(double masterTimestamp, out VideoFrame frame);

    /// <inheritdoc/>
    public abstract bool RequestNextFrame(out VideoFrame frame);

    /// <inheritdoc/>
    public abstract bool Seek(double positionInSeconds);

    /// <inheritdoc/>
    public abstract void AttachToClock(OwnaudioNET.Synchronization.MasterClock clock);

    /// <inheritdoc/>
    public abstract void DetachFromClock();

    /// <inheritdoc/>
    public abstract void ResyncTo(long samplePosition);

    /// <inheritdoc/>
    public virtual void Play()
    {
        ThrowIfDisposed();

        if (State is VideoPlaybackState.Stopped or VideoPlaybackState.Paused or VideoPlaybackState.EndOfStream)
            SetState(VideoPlaybackState.Playing);
    }

    /// <inheritdoc/>
    public virtual void Pause()
    {
        ThrowIfDisposed();

        if (State == VideoPlaybackState.Playing)
            SetState(VideoPlaybackState.Paused);
    }

    /// <inheritdoc/>
    public virtual void Stop()
    {
        ThrowIfDisposed();

        if (State == VideoPlaybackState.Stopped)
            return;

        Seek(0);
        SetPosition(0);
        SetSamplePosition(0);
        SetState(VideoPlaybackState.Stopped);
    }

    /// <summary>Updates the cached playback position.</summary>
    protected void SetPosition(double positionInSeconds)
    {
        Volatile.Write(ref _positionSeconds, positionInSeconds);
    }

    /// <summary>Sets the current sample position used by synchronization.</summary>
    protected void SetSamplePosition(long samplePosition)
    {
        Interlocked.Exchange(ref _samplePosition, samplePosition);
    }

    /// <summary>Advances the playback state and raises <see cref="StateChanged"/> when it changes.</summary>
    protected void SetState(VideoPlaybackState newState)
    {
        var oldState = (VideoPlaybackState)Interlocked.Exchange(ref _state, (int)newState);
        if (oldState != newState)
        {
            var handler = StateChanged;
            if (handler != null)
                handler.Invoke(this, new VideoPlaybackStateChangedEventArgs(oldState, newState));
        }
    }

    /// <summary>Raises the zero-allocation and allocating frame-ready events.</summary>
    protected void RaiseFrameReady(VideoFrame frame, double masterTimestamp)
    {
        FrameReadyFast?.Invoke(frame, masterTimestamp);

        var handler = FrameReady;
        if (handler != null)
            handler.Invoke(this, new VideoFrameReadyEventArgs(frame, masterTimestamp));
    }

    /// <summary>Raises the stream-info-changed event.</summary>
    protected void RaiseStreamInfoChanged(VideoStreamInfo streamInfo)
    {
        var handler = StreamInfoChanged;
        if (handler != null)
            handler.Invoke(this, new VideoStreamInfoChangedEventArgs(streamInfo));
    }

    /// <summary>Raises a video error and transitions the source into the error state.</summary>
    protected void RaiseError(string message, Exception? exception = null)
    {
        var handler = Error;
        if (handler != null)
            handler.Invoke(this, new VideoErrorEventArgs(message, exception));

        SetState(VideoPlaybackState.Error);
    }

    /// <summary>Throws if the source has already been disposed.</summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    /// <summary>Releases source resources.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            try
            {
                Stop();
            }
            catch
            {
                // Best-effort rewind during disposal.
            }
        }

        _disposed = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

