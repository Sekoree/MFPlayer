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
}

