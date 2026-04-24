using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Compat shim that implements <see cref="IVideoSubscription"/> on top of the legacy
/// <c>IMediaChannel&lt;VideoFrame&gt;.FillBuffer</c> pull API. Used as the default
/// implementation for <see cref="IVideoChannel.Subscribe"/> on channels that don't
/// provide native fan-out (e.g. <c>NDIVideoChannel</c>).
///
/// <para>
/// At most one <see cref="FillBufferSubscription"/> should be alive per channel at a
/// time — the underlying <c>FillBuffer</c> ring is shared, so multiple shims would
/// race for frames (exactly the bug that native fan-out fixes in
/// <c>FFmpegVideoChannel</c>).
/// </para>
/// </summary>
internal sealed class FillBufferSubscription : IVideoSubscription
{
    private readonly IVideoChannel _channel;
    private readonly VideoSubscriptionOptions _options;
    private bool _disposed;
    // §3.48 / CH1 — single-reader reentrancy guard (Debug builds only).
    private int _fillBufferActive;

    public FillBufferSubscription(IVideoChannel channel, VideoSubscriptionOptions options)
    {
        _channel = channel;
        _options = options;
        // Forward underrun events from the channel so per-subscription consumers can observe them.
        _channel.BufferUnderrun += OnChannelUnderrun;
    }

    public int Capacity    => _options.Capacity;
    public int Count       => _channel.BufferAvailable;
    public bool IsCompleted => _disposed;

    /// <summary>§2.8 — forwarded from the underlying channel on the channel's publisher thread.</summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    public int FillBuffer(Span<VideoFrame> dest, int frameCount)
    {
        // §3.48 / CH1 — assert single-reader invariant in debug builds.
        Debug.Assert(Interlocked.Exchange(ref _fillBufferActive, 1) == 0,
            "FillBufferSubscription.FillBuffer called concurrently — the contract requires single-threaded pull.");
        try
        {
            return _disposed ? 0 : _channel.FillBuffer(dest, frameCount);
        }
        finally
        {
            Interlocked.Exchange(ref _fillBufferActive, 0);
        }
    }

    private readonly VideoFrame[] _oneFrame = new VideoFrame[1];

    public bool TryRead(out VideoFrame frame)
    {
        frame = default;
        if (_disposed) return false;
        _oneFrame[0] = default;
        int got = _channel.FillBuffer(_oneFrame, 1);
        if (got == 0) return false;
        frame = _oneFrame[0];
        return true;
    }

    private void OnChannelUnderrun(object? sender, BufferUnderrunEventArgs e)
        => BufferUnderrun?.Invoke(sender, e);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.BufferUnderrun -= OnChannelUnderrun;
    }
}

