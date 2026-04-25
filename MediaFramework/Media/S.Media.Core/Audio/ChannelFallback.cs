namespace S.Media.Core.Audio;

/// <summary>
/// Controls what happens when an <see cref="IAudioChannel"/> is added to an
/// <see cref="IAudioMixer"/> but has no explicit route configured for a registered
/// <see cref="IAudioSink"/> via <see cref="IAudioMixer.RouteTo"/>.
/// </summary>
public enum ChannelFallback
{
    /// <summary>
    /// The channel produces silence on any output/sink that does not have an explicit
    /// route. Use this for strict output isolation (e.g. a cue mix must not bleed
    /// into the main mix unless explicitly routed).
    /// This is the default for <see cref="Mixing.AudioMixer"/>.
    /// </summary>
    Silent,

    /// <summary>
    /// The channel's leader route map is re-used for any output/sink that does not
    /// have an explicit route. Equivalent to the pre-multi-output behaviour where all
    /// sinks received an identical copy of the fully-mixed leader buffer.
    /// </summary>
    Broadcast,
}

