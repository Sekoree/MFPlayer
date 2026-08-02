namespace S.Media.Core.Audio;

/// <summary>
/// Marker: this output's audio passes through a sample-rate converter, so its timing is not its own.
/// </summary>
/// <remarks>
/// <para>
/// Such a wrapper reports the latency of the device beneath it and <b>not</b> the converter's internal
/// buffered/filter delay, so a clock read through it is silently early. That is harmless for a follower
/// - drift correction handles it - but disqualifying for the terminal a router paces from: the whole
/// programme would be timed against a clock that is wrong by an amount nothing accounts for, which shows
/// up as A/V drift with no obvious cause.
/// </para>
/// <para>
/// This is the reason a clock master must open natively at the project mix rate. That rule was already
/// enforced where a bay attaches a master explicitly; this marker closes the other door, where a router
/// AUTO-PROMOTES the first clocked output it sees and a resampling wrapper looks like a perfectly good
/// candidate because it implements the clock interfaces faithfully.
/// </para>
/// <para>Distinct from <see cref="IAdaptiveRateWrappedOutput"/>, which marks the drift-correcting
/// wrapper: that one resamples by tiny ratios to track a master, this one converts between two fixed
/// rates. Both are unfit to pace, for the same underlying reason.</para>
/// </remarks>
public interface IRateAdaptedOutput;
