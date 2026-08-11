namespace S.Media.Time;

/// <summary>
/// Capability seam for a playback clock that knows the audio-pipeline depth currently between
/// production and the speaker. The genlock start policy uses it to defer a silent (video-only)
/// voice's start by that depth, so its first frame lands when the sounding programme becomes audible
/// instead of leading it by the whole audio path (measured ~150–500 ms).
/// </summary>
/// <remarks>
/// This exists so the policy never type-sniffs a concrete clock: any wrapper or decorator over a
/// clock with this capability MUST implement it too and forward <see cref="CurrentPipelineLead"/> -
/// otherwise the deferral silently disappears and the picture-leads-audio defect returns with
/// nothing logged. Same rule as forwarding <see cref="IPlaybackClock.Read"/> and
/// <see cref="ClockReading.EpochId"/>: a wrapper that hides a capability of the clock below it
/// reintroduces the bug that capability fixed.
/// </remarks>
public interface IPipelineLeadClock : IPlaybackClock
{
    /// <summary>
    /// The RAW measured audio path depth between production and the speaker - not the low-passed
    /// value the clock itself subtracts. Zero when unknown (e.g. torn down mid-read).
    /// </summary>
    TimeSpan CurrentPipelineLead { get; }
}
