
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

    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex);

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

