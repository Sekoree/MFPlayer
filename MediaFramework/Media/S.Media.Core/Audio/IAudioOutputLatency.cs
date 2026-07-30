namespace S.Media.Core.Audio;

/// <summary>
/// Optional <see cref="IAudioOutput"/> capability: how long audio handed to
/// <see cref="IAudioOutput.Submit"/> right now takes to become audible (the output's own queued
/// backlog plus the device buffering behind it).
/// </summary>
/// <remarks>
/// Distinct from an <c>IPlaybackClock</c>, which reports where the output already <em>is</em>: a
/// fan-in owner that clocks its clients off the terminal's playback clock cannot see how far the
/// samples it just submitted still are from the speaker, and that transit delay is exactly the bias
/// its per-client clocks have to subtract (<c>SharedAudioOutput</c>).
/// </remarks>
public interface IAudioOutputLatency
{
    /// <summary>
    /// Submit-to-speaker delay, or <see cref="TimeSpan.Zero"/> when unknown. Read from clock hot
    /// paths: implementations must be allocation-free, must not block on device lifecycle work and
    /// must not throw.
    /// </summary>
    TimeSpan SubmitToOutputLatency { get; }
}

/// <summary>
/// Reads <see cref="IAudioOutputLatency"/> off an arbitrary output, and the reason every
/// <see cref="IAudioOutput"/> DECORATOR implements the interface unconditionally instead of only when its
/// inner output does.
/// </summary>
/// <remarks>
/// <para>The decorators (<c>MeteringAudioOutput</c>, <c>ResamplingAudioOutput</c>, <c>AudioEffectOutput</c>)
/// preserve their inner output's capabilities through a matrix of hand-written subclasses, because a
/// consumer tests for those capabilities to DECIDE BEHAVIOUR: <c>IClockedOutput</c> selects the pacing
/// path and <c>IPlaybackClock</c> selects master-clock promotion, so claiming either over an inner that
/// lacks it changes what the router does. Each new conditional capability doubles that matrix - which is
/// exactly how this one came to be forwarded by none of them (2026-07-30 review §2).</para>
/// <para>This capability does not need to be conditional. Its own contract already defines
/// <see cref="TimeSpan.Zero"/> as "unknown", and the only consumer adds the value solely when it is
/// positive, so "implemented, reporting Zero" and "not implemented" are indistinguishable to every caller.
/// A decorator can therefore always implement it and delegate through this helper - no new subclasses, no
/// growth in the matrix, and the capability stops disappearing at the first wrapper.</para>
/// <para>The try/catch is deliberate even though implementations promise not to throw: this is read from
/// clock hot paths whose own contract is never to throw, and a terminal output disposed mid-read is a real
/// case. Degrading to "unknown" is always safe.</para>
/// </remarks>
public static class AudioOutputLatency
{
    /// <summary>The output's submit-to-speaker delay, or <see cref="TimeSpan.Zero"/> when it reports none
    /// (or cannot be read). Never throws.</summary>
    public static TimeSpan Of(IAudioOutput? output)
    {
        if (output is not IAudioOutputLatency reporting)
            return TimeSpan.Zero;
        try
        {
            var latency = reporting.SubmitToOutputLatency;
            return latency > TimeSpan.Zero ? latency : TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}
