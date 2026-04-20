using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Optional capability on audio endpoints: the endpoint can be driven
/// by a pull callback instead of or in addition to push-based <see cref="IAudioEndpoint.ReceiveBuffer"/>.
/// Hardware audio outputs (PortAudio) implement this because their RT
/// callback pulls audio rather than having it pushed.
/// </summary>
public interface IPullAudioEndpoint : IAudioEndpoint
{
    /// <summary>
    /// The graph sets this when the endpoint is registered.
    /// The endpoint calls it from its RT callback to pull audio.
    /// </summary>
    IAudioFillCallback? FillCallback { get; set; }

    /// <summary>
    /// The audio format of the hardware stream.
    /// The graph reads this to know what format to produce.
    /// </summary>
    AudioFormat EndpointFormat { get; }

    /// <summary>
    /// The number of frames per hardware callback buffer.
    /// The graph reads this to size scratch buffers.
    /// </summary>
    int FramesPerBuffer { get; }
}

