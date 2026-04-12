using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Unit tests for <see cref="DriftCorrector"/>.
/// Covers steady-state convergence, clamp behaviour, fractional accumulation,
/// and reset semantics.
/// </summary>
public sealed class DriftCorrectorTests
{
    // ── Basic behaviour ──────────────────────────────────────────────────

    [Fact]
    public void AtTarget_ReturnsNominalFrames()
    {
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test");

        // Queue exactly at target → no correction needed.
        int result = dc.CorrectFrameCount(512, currentQueueDepth: 3);

        Assert.Equal(512, result);
        Assert.InRange(dc.CorrectionRatio, 0.999, 1.001);
    }

    [Fact]
    public void QueueBelowTarget_ProducesMoreFrames_OverTime()
    {
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test");

        // Simulate queue consistently below target (hardware consuming faster than leader).
        long totalExtra = 0;
        for (int i = 0; i < 10_000; i++)
        {
            int frames = dc.CorrectFrameCount(512, currentQueueDepth: 0);
            totalExtra += frames - 512;
        }

        // Over 10 000 buffers, should have produced net extra frames.
        Assert.True(totalExtra > 0,
            $"Expected net extra frames when queue is below target, got {totalExtra}");
        Assert.True(dc.CorrectionRatio > 1.0,
            $"Expected ratio > 1.0 when queue is below target, got {dc.CorrectionRatio}");
    }

    [Fact]
    public void QueueAboveTarget_ProducesFewerFrames_OverTime()
    {
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test");

        // Simulate queue consistently above target (hardware consuming slower than leader).
        long totalDeficit = 0;
        for (int i = 0; i < 10_000; i++)
        {
            int frames = dc.CorrectFrameCount(512, currentQueueDepth: 8);
            totalDeficit += 512 - frames;
        }

        Assert.True(totalDeficit > 0,
            $"Expected net fewer frames when queue is above target, got deficit={totalDeficit}");
        Assert.True(dc.CorrectionRatio < 1.0,
            $"Expected ratio < 1.0 when queue is above target, got {dc.CorrectionRatio}");
    }

    // ── Clamping ─────────────────────────────────────────────────────────

    [Fact]
    public void CorrectionRatio_NeverExceedsMaxCorrection()
    {
        double maxCorrection = 0.005;
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test", maxCorrection: maxCorrection);

        // Drive the error extremely hard.
        for (int i = 0; i < 100_000; i++)
            dc.CorrectFrameCount(512, currentQueueDepth: 0);

        Assert.InRange(dc.CorrectionRatio, 1.0, 1.0 + maxCorrection + 1e-10);

        // Now the other direction.
        var dc2 = new DriftCorrector(targetDepth: 3, ownerName: "test", maxCorrection: maxCorrection);
        for (int i = 0; i < 100_000; i++)
            dc2.CorrectFrameCount(512, currentQueueDepth: 100);

        Assert.InRange(dc2.CorrectionRatio, 1.0 - maxCorrection - 1e-10, 1.0);
    }

    // ── Fractional accumulation ──────────────────────────────────────────

    [Fact]
    public void FractionalAccumulation_ProducesCorrectLongTermAverage()
    {
        // With a very small correction, individual buffers are mostly 512,
        // but over time the average should differ measurably.
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test", kp: 1e-3, ki: 0);

        long totalFrames = 0;
        int iterations = 50_000;
        for (int i = 0; i < iterations; i++)
        {
            totalFrames += dc.CorrectFrameCount(512, currentQueueDepth: 1);
        }

        // Queue below target (1 < 3): correction > 1.0 → more frames than 512 per buffer.
        double average = (double)totalFrames / iterations;
        Assert.True(average > 512.0,
            $"Expected average > 512.0 for below-target queue, got {average:F4}");
    }

    // ── Reset ────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsState()
    {
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test");

        // Accumulate some integral error.
        for (int i = 0; i < 5_000; i++)
            dc.CorrectFrameCount(512, currentQueueDepth: 0);

        Assert.True(dc.CorrectionRatio > 1.0);

        dc.Reset();

        Assert.Equal(1.0, dc.CorrectionRatio);
    }

    // ── Minimum frame count ──────────────────────────────────────────────

    [Fact]
    public void CorrectFrameCount_NeverReturnsLessThanOne()
    {
        var dc = new DriftCorrector(targetDepth: 0, ownerName: "test", maxCorrection: 0.999);

        // Even with extreme negative correction and tiny nominal, must return ≥ 1.
        for (int i = 0; i < 1_000; i++)
        {
            int result = dc.CorrectFrameCount(1, currentQueueDepth: 1000);
            Assert.True(result >= 1, $"Got {result} at iteration {i}");
        }
    }

    // ── TotalCalls ───────────────────────────────────────────────────────

    [Fact]
    public void TotalCalls_CountsInvocations()
    {
        var dc = new DriftCorrector(targetDepth: 3, ownerName: "test");

        for (int i = 0; i < 42; i++)
            dc.CorrectFrameCount(512, currentQueueDepth: 3);

        Assert.Equal(42, dc.TotalCalls);
    }
}

