using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Unified push endpoint contract for audio buffers.
/// Implemented by sink/output adapters during API unification.
/// </summary>
public interface IAudioBufferEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Pushes one interleaved audio buffer into the endpoint.
    /// </summary>
    void WriteBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format);
}
