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
