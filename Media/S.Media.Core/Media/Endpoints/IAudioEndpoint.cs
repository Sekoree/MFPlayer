using S.Media.Core.Media;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Receives audio buffers from the graph. Replaces <c>IAudioOutput</c>, <c>IAudioSink</c>,
/// and <c>IAudioBufferEndpoint</c> with a single unified push contract.
/// </summary>
public interface IAudioEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Called by the graph to deliver mixed/forwarded audio.
    /// Implementations MUST be non-blocking on the RT thread.
    /// </summary>
    void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format);

    /// <summary>
    /// Timestamped variant: delivers mixed audio together with the <b>stream-time PTS
    /// of the first sample</b> in <paramref name="buffer"/>.  Sinks that need to emit
    /// media timecodes (e.g. NDI, SMPTE ST 2110) should override this and ignore the
    /// simpler <see cref="ReceiveBuffer(ReadOnlySpan{float}, int, AudioFormat)"/> path.
    ///
    /// <para>
    /// The default implementation discards the PTS and forwards to
    /// <see cref="ReceiveBuffer(ReadOnlySpan{float}, int, AudioFormat)"/> so plain
    /// playback sinks continue to work unchanged.
    /// </para>
    /// </summary>
    /// <param name="buffer">Interleaved PCM data, <c>frameCount × format.Channels</c> samples.</param>
    /// <param name="frameCount">Number of frames in <paramref name="buffer"/>.</param>
    /// <param name="format">Audio format of <paramref name="buffer"/>.</param>
    /// <param name="sourcePts">
    /// Stream-time PTS of the first sample in <paramref name="buffer"/>, as reported by
    /// the upstream channel's <c>Position</c> at the moment of the read.  May be
    /// <see cref="TimeSpan.Zero"/> before the first successful decoder read.
    /// </param>
    void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts)
        => ReceiveBuffer(buffer, frameCount, format);
}



