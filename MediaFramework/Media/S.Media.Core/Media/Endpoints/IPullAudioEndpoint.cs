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
    /// <remarks>
    /// <b>Swap semantics (§3.51 / CH5):</b> setting this property MUST be a volatile
    /// write, and setting it to <see langword="null"/> MUST ensure that any
    /// in-flight invocation of <see cref="IAudioFillCallback.Fill"/> has returned
    /// before the setter returns (a bounded spin-wait is sufficient; the router
    /// relies on this to safely tear down the associated route metadata
    /// immediately afterwards).
    /// </remarks>
    IAudioFillCallback? FillCallback { get; set; }

    /// <summary>
    /// The audio format of the hardware stream.
    /// The graph reads this to know what format to produce.
    /// </summary>
    /// <remarks>
    /// <b>Frozen-after-Open (§3.52 / CH6):</b> this value MUST NOT change after the
    /// endpoint has been opened / handed to the router. The router caches
    /// it at <c>RegisterEndpoint</c> time to size scratch buffers.
    /// </remarks>
    AudioFormat EndpointFormat { get; }

    /// <summary>
    /// The number of frames per hardware callback buffer.
    /// The graph reads this to size scratch buffers.
    /// </summary>
    /// <remarks>
    /// <b>Frozen-after-Open (§3.52 / CH6):</b> this value MUST NOT change after the
    /// endpoint has been opened / handed to the router.
    /// </remarks>
    int FramesPerBuffer { get; }
}

