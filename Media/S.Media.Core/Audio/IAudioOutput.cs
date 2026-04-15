using System.ComponentModel;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// An audio output device stream. Owns the hardware clock.
/// Routing is managed externally — wire channels through <see cref="Mixing.IAVMixer"/>.
/// </summary>
public interface IAudioOutput : IMediaOutput
{
    /// <summary>
    /// The exact format negotiated with the hardware after <see cref="Open"/> completes.
    /// Fixed for the lifetime of the stream.
    /// </summary>
    AudioFormat HardwareFormat { get; }

    /// <summary>
    /// Opens the hardware stream.
    /// </summary>
    /// <param name="device">Target device (from <see cref="IAudioEngine.GetDevices"/>).</param>
    /// <param name="requestedFormat">
    /// Desired format. The implementation may negotiate a different sample rate or channel
    /// count; read <see cref="HardwareFormat"/> after opening to get the actual values.
    /// </param>
    /// <param name="framesPerBuffer">
    /// Requested hardware buffer size in frames. Pass 0 to let the driver choose.
    /// </param>
    /// <param name="suggestedLatency">
    /// Suggested output latency in seconds. When &gt; 0, passed to the driver directly.
    /// When ≤ 0 (default), derived from <paramref name="framesPerBuffer"/> or device default.
    /// </param>
    void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0, double suggestedLatency = 0);

    /// <summary>
    /// Replaces the <see cref="IAudioMixer"/> invoked by the RT callback.
    /// Called by <see cref="AggregateOutput"/> to intercept the fill path.
    /// Not intended for direct use by application code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    void OverrideRtMixer(IAudioMixer mixer);
}
