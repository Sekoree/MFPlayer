namespace S.Media.Core.Audio;

/// <summary>
/// Push-based audio consumer. Outputs include playback devices (PortAudio output)
/// and network senders (NDI). The router calls <see cref="Submit"/> with
/// samples already mapped into the output's channel layout.
/// </summary>
public interface IAudioOutput
{
    AudioFormat Format { get; }

    /// <summary>
    /// Submit packed (interleaved) float samples. <c>packedSamples.Length</c>
    /// must be a multiple of <see cref="Format"/>'s channel count. Outputs
    /// decide their own overflow behaviour (drop vs. block).
    /// </summary>
    void Submit(ReadOnlySpan<float> packedSamples);
}

/// <summary>
/// An output that wraps another (an effect insert, a resampler, a meter). Declaring the inner sink lets
/// <see cref="AudioOutputCapabilities"/> resolve a capability THROUGH the wrapper.
/// </summary>
/// <remarks>
/// This exists because "every wrapper must re-expose every capability face" is a rule that keeps being
/// broken as new faces are added - each miss is silent, and each has cost a real defect (the pacing-credit
/// leak that wedged a voice, the lost pre-roll alignment, the dropped pipeline-lead clock). A wrapper
/// cannot forget to implement an interface it does not have to implement: it declares what it wraps once,
/// and capability lookups walk the chain.
/// </remarks>
public interface IAudioOutputDecorator
{
    /// <summary>The sink this output delegates to. Never null.</summary>
    IAudioOutput InnerOutput { get; }
}

/// <summary>Resolves an optional capability face on an output, seeing through
/// <see cref="IAudioOutputDecorator"/> wrappers.</summary>
public static class AudioOutputCapabilities
{
    /// <summary>The nearest <typeparamref name="T"/> in the decorator chain, or null when nothing in it
    /// implements the face. Prefer this over a direct <c>as</c>/<c>is</c> test on any output that may have
    /// been wrapped - an insert chain in front of a device or a program bus is the normal case, not an
    /// exotic one.</summary>
    public static T? Find<T>(IAudioOutput? output) where T : class
    {
        // Bounded: a malformed decorator that returns itself (or a cycle) must not hang the router.
        for (var depth = 0; output is not null && depth < 32; depth++)
        {
            if (output is T match)
                return match;
            if (output is not IAudioOutputDecorator decorator || ReferenceEquals(decorator.InnerOutput, output))
                return null;
            output = decorator.InnerOutput;
        }

        return null;
    }
}
