using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// A secondary destination that receives copies of the master mixed buffer.
/// Used to fan-out audio to additional targets (e.g. a second hardware device,
/// an NDI sender, a file recorder).
/// </summary>
public interface IAudioSink : IMediaEndpoint
{
    /// <summary>
    /// Called once per mixed buffer, from the leader's RT callback thread.
    /// Implementations MUST be non-blocking — copy to a ring buffer and return immediately.
    /// Format conversion (resampling, channel routing) is the sink's own responsibility.
    /// </summary>
    void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat);
}
