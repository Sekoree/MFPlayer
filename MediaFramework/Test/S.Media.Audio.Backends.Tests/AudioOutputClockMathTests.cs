using System.Diagnostics;
using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Audio.Backends.Tests;

/// <summary>
/// Pure clock math behind the backend playback clocks (review findings: miniaudio's raw sample-count
/// clock advanced in period stair-steps with no latency subtraction; PortAudio's audible clamp held 0
/// for the first outputLatency and then jumped). The device-independent pieces are internal statics so
/// the contracts - clamping, monotonicity, continuity at the startup-window edge - are testable without
/// hardware.
/// </summary>
public sealed class AudioOutputClockMathTests
{
    private const double Tolerance = 1e-9;

    // ---- miniaudio: wall-clock interpolation between data callbacks ----------------------------

    [Fact]
    public void MiniAudio_Interpolation_IsZeroBeforeTheFirstCallback() =>
        Assert.Equal(0, MiniAudioOutput.ComputeCallbackInterpolationSeconds(
            lastCallbackTimestamp: 0, nowTimestamp: Stopwatch.GetTimestamp(), periodSeconds: 0.010));

    [Fact]
    public void MiniAudio_Interpolation_IsZeroWithoutAKnownPeriod() =>
        Assert.Equal(0, MiniAudioOutput.ComputeCallbackInterpolationSeconds(
            lastCallbackTimestamp: 1, nowTimestamp: 2, periodSeconds: 0));

    [Fact]
    public void MiniAudio_Interpolation_NeverGoesNegativeOnClockSkew()
    {
        var now = Stopwatch.GetTimestamp();
        Assert.Equal(0, MiniAudioOutput.ComputeCallbackInterpolationSeconds(
            lastCallbackTimestamp: now + Stopwatch.Frequency, nowTimestamp: now, periodSeconds: 0.010));
    }

    [Fact]
    public void MiniAudio_Interpolation_TracksWallTimeWithinAPeriod()
    {
        var last = Stopwatch.GetTimestamp();
        var now = last + (long)(0.004 * Stopwatch.Frequency); // 4 ms into a 10 ms period
        var interpolated = MiniAudioOutput.ComputeCallbackInterpolationSeconds(last, now, periodSeconds: 0.010);
        Assert.InRange(interpolated, 0.004 - 1e-6, 0.004 + 1e-6);
    }

    [Fact]
    public void MiniAudio_Interpolation_ClampsToOnePeriod_SoItCannotOvertakeTheNextCallback()
    {
        var last = Stopwatch.GetTimestamp();
        var now = last + (long)(0.5 * Stopwatch.Frequency); // callbacks stalled for 500 ms
        Assert.Equal(0.010, MiniAudioOutput.ComputeCallbackInterpolationSeconds(last, now, periodSeconds: 0.010), Tolerance);
    }

    [Fact]
    public void MiniAudio_FreshOutput_ReportsZeroElapsed()
    {
        using var output = new MiniAudioOutput(new AudioFormat(48_000, 2));
        Assert.Equal(TimeSpan.Zero, output.ElapsedSinceStart);
        Assert.False(output.IsAdvancing);
    }

    // ---- PortAudio: startup ease-in for the audible-position latency subtraction ---------------

    [Fact]
    public void PortAudio_Audible_IsZeroAtOrBeforeSegmentStart()
    {
        Assert.Equal(0, PortAudioOutput.ComputeAudibleSeconds(0, 0.090));
        Assert.Equal(0, PortAudioOutput.ComputeAudibleSeconds(-1, 0.090));
    }

    [Fact]
    public void PortAudio_Audible_IsIdentityWithoutLatency() =>
        Assert.Equal(1.234, PortAudioOutput.ComputeAudibleSeconds(1.234, 0), Tolerance);

    [Fact]
    public void PortAudio_Audible_SubtractsFullLatencyInSteadyState() =>
        Assert.Equal(1.0 - 0.090, PortAudioOutput.ComputeAudibleSeconds(1.0, 0.090), Tolerance);

    [Fact]
    public void PortAudio_Audible_IsExactlyContinuousAtTheWindowEdge()
    {
        const double latency = 0.090;
        var edge = 2 * latency;
        // Quadratic ease-in meets elapsed - latency at the edge with matching value...
        Assert.Equal(edge - latency, PortAudioOutput.ComputeAudibleSeconds(edge, latency), Tolerance);
        // ...and no step just below it.
        var justBelow = PortAudioOutput.ComputeAudibleSeconds(edge - 1e-6, latency);
        Assert.InRange((edge - latency) - justBelow, 0, 1e-5);
    }

    [Fact]
    public void PortAudio_Audible_IsMonotonicAndAdvancesInsideTheStartupWindow()
    {
        const double latency = 0.090;
        var previous = 0.0;
        for (var elapsed = 0.0; elapsed <= 4 * latency; elapsed += latency / 64)
        {
            var audible = PortAudioOutput.ComputeAudibleSeconds(elapsed, latency);
            Assert.True(audible >= previous, $"non-monotonic at elapsed={elapsed}: {audible} < {previous}");
            previous = audible;
        }

        // The old clamp reported 0 for the whole first latency window; the ramp must actually move.
        Assert.True(PortAudioOutput.ComputeAudibleSeconds(latency / 2, latency) > 0,
            "audible position must advance during the startup window instead of holding zero");
    }

    [Fact]
    public void PortAudio_Audible_StaysBetweenTheAudibleAndConsumedPositions()
    {
        const double latency = 0.090;
        for (var elapsed = 0.0; elapsed <= 4 * latency; elapsed += latency / 32)
        {
            var audible = PortAudioOutput.ComputeAudibleSeconds(elapsed, latency);
            Assert.InRange(audible, Math.Max(0, elapsed - latency) - Tolerance, elapsed + Tolerance);
        }
    }
}
