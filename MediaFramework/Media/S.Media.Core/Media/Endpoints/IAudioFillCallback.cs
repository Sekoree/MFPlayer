using S.Media.Core.Audio;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Implemented by the graph. Set on <see cref="IPullAudioEndpoint"/> at registration time.
/// The endpoint calls this from its RT callback.
/// </summary>
public interface IAudioFillCallback
{
    /// <summary>
    /// Fills <paramref name="dest"/> with mixed audio for the pull endpoint.
    /// Called from the endpoint's RT callback — MUST NOT allocate, lock, or block.
    /// </summary>
    void Fill(Span<float> dest, int frameCount, AudioFormat endpointFormat);
}

