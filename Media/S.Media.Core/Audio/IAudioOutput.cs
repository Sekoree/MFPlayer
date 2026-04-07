using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// An audio output device stream. Owns the hardware clock and an <see cref="IAudioMixer"/>.
/// Open the stream first, then add channels to the mixer.
/// </summary>
public interface IAudioOutput : IMediaOutput
{
    /// <summary>
    /// The exact format negotiated with the hardware after <see cref="Open"/> completes.
    /// Fixed for the lifetime of the stream.
    /// </summary>
    AudioFormat HardwareFormat { get; }

    /// <summary>The mixer attached to this output. Available after <see cref="Open"/>.</summary>
    IAudioMixer Mixer { get; }

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
    void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0);

    /// <summary>
    /// Replaces the <see cref="IAudioMixer"/> invoked by the RT callback.
    /// Called by <see cref="AggregateOutput"/> to intercept the fill path so it can
    /// distribute audio to additional sinks after the primary buffer is filled.
    /// Not intended for direct use by application code.
    /// </summary>
    void OverrideRtMixer(IAudioMixer mixer);
}
