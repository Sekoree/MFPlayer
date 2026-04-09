using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Concrete implementation of <see cref="IVideoMixer"/>.
/// Manages video channels and pulls frames from the active one.
/// Single-channel presentation in v1 (no compositing / layering).
/// Backend-agnostic — usable with SDL3, Avalonia, NDI, or any other output.
/// </summary>
public sealed class VideoMixer : IVideoMixer
{
    private readonly object _lock = new();
    private IVideoChannel[] _channels = [];
    private volatile IVideoChannel? _activeChannel;
    private VideoFrame? _lastFrame;
    private bool _disposed;

    public VideoMixer(VideoFormat outputFormat)
    {
        OutputFormat = outputFormat;
    }

    /// <inheritdoc/>
    public VideoFormat OutputFormat { get; }

    /// <inheritdoc/>
    public int ChannelCount
    {
        get
        {
            lock (_lock) return _channels.Length;
        }
    }

    /// <inheritdoc/>
    public IVideoChannel? ActiveChannel => _activeChannel;

    /// <inheritdoc/>
    public void AddChannel(IVideoChannel channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channel);

        lock (_lock)
        {
            var old = _channels;
            var neo = new IVideoChannel[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = channel;
            _channels = neo;

            // Auto-activate the first channel added.
            _activeChannel ??= channel;
        }
    }

    /// <inheritdoc/>
    public void RemoveChannel(Guid channelId)
    {
        lock (_lock)
        {
            var old = _channels;
            int idx = -1;
            for (int i = 0; i < old.Length; i++)
            {
                if (old[i].Id == channelId) { idx = i; break; }
            }
            if (idx < 0) return;

            // If removing the active channel, clear it.
            if (_activeChannel?.Id == channelId)
                _activeChannel = null;

            var neo = new IVideoChannel[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
                if (i != idx) neo[j++] = old[i];
            _channels = neo;
        }
    }

    /// <inheritdoc/>
    public void SetActiveChannel(Guid? channelId)
    {
        if (channelId is null)
        {
            _activeChannel = null;
            return;
        }

        lock (_lock)
        {
            foreach (var ch in _channels)
            {
                if (ch.Id == channelId.Value)
                {
                    _activeChannel = ch;
                    return;
                }
            }
        }
    }

    // Pre-allocated single-frame buffer to avoid per-call allocation.
    private readonly VideoFrame[] _pullBuffer = new VideoFrame[1];

    /// <inheritdoc/>
    public VideoFrame? PresentNextFrame()
    {
        var channel = _activeChannel;
        if (channel is null) return null;

        // Pull one frame from the active channel.
        int got = channel.FillBuffer(_pullBuffer, 1);

        if (got > 0)
        {
            // Dispose the previous frame's memory owner (return ArrayPool rental).
            _lastFrame?.MemoryOwner?.Dispose();
            _lastFrame = _pullBuffer[0];
            return _pullBuffer[0];
        }

        // No new frame available — re-display the last frame (hold / no flicker).
        return _lastFrame;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lastFrame?.MemoryOwner?.Dispose();
        _lastFrame = null;
    }
}

