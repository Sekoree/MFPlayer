
namespace S.Media.Core.Audio;

/// <summary>
/// Minimal contract for any destination that accepts pushed audio frames.
/// Does not include device-selection APIs — use <see cref="IAudioOutput"/> for hardware outputs.
/// </summary>
public interface IAudioSink : IDisposable
{
    Guid Id { get; }

    AudioOutputState State { get; }

    int Start(AudioOutputConfig config);

    int Stop();

    /// <summary>
    /// Pushes an audio frame to this sink using the specified channel route-map.
    /// </summary>
    /// <remarks>
    /// <b>Buffer ownership:</b> The implementation must not hold a reference to
    /// <see cref="AudioFrame.Samples"/> beyond this call. The caller (typically the mixer)
    /// reuses the underlying buffer on the next pump iteration, so any data needed after
    /// <c>PushFrame</c> returns must be copied into an internal buffer.
    /// </remarks>
    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex);

    /// <inheritdoc cref="PushFrame(in AudioFrame, ReadOnlySpan{int})"/>
    /// <param name="frame">The audio frame to push.</param>
    /// <param name="sourceChannelByOutputIndex">Maps each output channel index to a source channel index.</param>
    /// <param name="sourceChannelCount">The number of channels in the source frame.</param>
    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount);

    /// <summary>
    /// Pushes <paramref name="frame"/> using an identity route-map (channel N → output N).
    /// Convenience overload; no allocation.
    /// </summary>
    int PushFrame(in AudioFrame frame)
    {
        // (10.7) Reject zero-channel frames rather than clamping to 1 (which would produce
        // a [0] route map and potentially read out-of-bounds from an empty Samples span).
        int ch = frame.SourceChannelCount;
        if (ch <= 0)
            return (int)S.Media.Core.Errors.MediaErrorCode.MediaInvalidArgument;
        Span<int> identity = stackalloc int[ch];
        for (int i = 0; i < ch; i++) identity[i] = i;
        return PushFrame(in frame, identity, ch);
    }
}

