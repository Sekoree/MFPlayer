using S.Control;
using S.Media.Session;
using Xunit;

namespace S.Control.Tests;

/// <summary>
/// Outbound OSC/MIDI value ramps (HaCue2 decision D3). The three properties tested here are the ones the
/// feature exists to guarantee, and each is a field failure if it is wrong: an explicit send rate, landing
/// exactly on the final value, and coalescing rather than queueing.
/// </summary>
public class OutboundRampRunnerTests
{
    private static List<OutboundRampPoint> Ramp(double from, double to, double seconds) =>
        [new(TimeSpan.Zero, from), new(TimeSpan.FromSeconds(seconds), to)];

    [Fact]
    public void InterpolatesBetweenKeyframes()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 1), sent.Add, sendRateHz: 10);

        runner.Advance(TimeSpan.Zero);
        runner.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0d, sent[0], 3);
        Assert.Equal(0.5d, sent[^1], 3);
    }

    [Fact]
    public void RespectsTheSendRate_RatherThanEmittingPerCall()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 10), sent.Add, sendRateHz: 25);

        // A caller ticking far faster than the rate must not flood the desk: 25 Hz means 40 ms apart.
        for (var ms = 0; ms < 200; ms += 1)
            runner.Advance(TimeSpan.FromMilliseconds(ms));

        Assert.InRange(sent.Count, 4, 7);
    }

    [Fact]
    public void LandsExactlyOnTheFinalValue_WhenItCompletes()
    {
        var sent = new List<double>();
        // A rate that does not divide the duration: the last tick necessarily falls short of the end.
        var runner = new OutboundRampRunner(Ramp(0, 0.75, 1), sent.Add, sendRateHz: 7);

        for (var ms = 0; ms <= 1000; ms += 10)
            runner.Advance(TimeSpan.FromMilliseconds(ms));

        // Leaving a desk holding 0.7482 instead of 0.75 is invisible in rehearsal and wrong for the rest
        // of the show, so the terminal value is sent explicitly.
        Assert.True(runner.IsFinished);
        Assert.Equal(0.75d, sent[^1], 10);
    }

    [Fact]
    public void Interrupt_StillSendsTheFinalValue()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 10), sent.Add, sendRateHz: 25);
        runner.Advance(TimeSpan.Zero);
        runner.Advance(TimeSpan.FromMilliseconds(200));

        // The cue stopped, or the show panicked, a fifth of the way in.
        runner.Interrupt();

        // An outbound value is NOT undone by stopping the cue - it belongs to another system, and the
        // opposite rule (used for internal lanes) would strand the desk mid-fade.
        Assert.True(runner.IsFinished);
        Assert.Equal(1d, sent[^1], 10);
    }

    [Fact]
    public void Freeze_EndsAtTheSampledInterruptionValue()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 10), sent.Add, sendRateHz: 25);

        runner.Freeze(TimeSpan.FromSeconds(2.5));

        Assert.True(runner.IsFinished);
        Assert.Equal(0.25d, sent[^1], 10);
    }

    [Fact]
    public void Reposition_SeedsTheSoughtValueAndAllowsACompletedRampToRunAgain()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 10), sent.Add, sendRateHz: 25);
        runner.Advance(TimeSpan.FromSeconds(10));
        Assert.True(runner.IsFinished);

        runner.Reposition(TimeSpan.FromSeconds(4));

        Assert.False(runner.IsFinished);
        Assert.Equal(0.4d, sent[^1], 10);
        runner.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0.5d, sent[^1], 10);
    }

    [Fact]
    public void Interrupt_IsIdempotent()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 1), sent.Add);

        runner.Interrupt();
        var afterFirst = sent.Count;
        runner.Interrupt();
        runner.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(afterFirst, sent.Count);
    }

    [Fact]
    public void CoalescesRatherThanQueueing_WhenTheCallerIsLate()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0, 1, 10), sent.Add, sendRateHz: 25);

        runner.Advance(TimeSpan.Zero);
        // The host stalled for two seconds - fifty intervals' worth. A queueing design would now replay
        // fifty stale values the show has already moved past.
        runner.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2, sent.Count);
        Assert.Equal(0.2d, sent[^1], 3);
    }

    [Fact]
    public void ASlowOrFailingEndpoint_DoesNotKillTheRamp()
    {
        var attempts = 0;
        var runner = new OutboundRampRunner(
            Ramp(0, 1, 1),
            _ => { attempts++; throw new TimeoutException("desk unreachable"); },
            sendRateHz: 10);

        for (var ms = 0; ms <= 1000; ms += 50)
            runner.Advance(TimeSpan.FromMilliseconds(ms));

        // It keeps trying, and still attempts the terminal value - which is how a recovering endpoint
        // ends up holding the right number instead of whatever it caught mid-fade.
        Assert.False(runner.IsFinished); // terminal delivery never succeeded, so completion cannot be claimed
        Assert.True(attempts > 1, "a failing send aborted the ramp");
    }

    [Fact]
    public void FailedTerminalSend_IsRetriedUntilItActuallyLands()
    {
        var attempts = 0;
        var runner = new OutboundRampRunner(Ramp(0, 1, 1), value =>
        {
            Assert.Equal(1, value, 10);
            if (attempts++ == 0)
                throw new TimeoutException("one transient failure");
        });

        runner.Advance(TimeSpan.FromSeconds(1));
        Assert.False(runner.IsFinished);

        runner.Advance(TimeSpan.FromSeconds(2));
        Assert.True(runner.IsFinished);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AsyncSender_HoldsOneInFlightAndOnlyTheNewestPendingValue()
    {
        var firstMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<double>();
        var calls = 0;
        var runner = new OutboundRampRunner(
            Ramp(0, 1, 10),
            async (value, _) =>
            {
                lock (sent) sent.Add(value);
                if (Interlocked.Increment(ref calls) == 1)
                    await firstMayFinish.Task;
            },
            sendRateHz: 100);

        runner.Advance(TimeSpan.Zero);
        await Task.Delay(25); // first send is in flight
        runner.Advance(TimeSpan.FromSeconds(1));
        runner.Advance(TimeSpan.FromSeconds(2));
        runner.Advance(TimeSpan.FromSeconds(3));
        firstMayFinish.SetResult();
        await runner.WaitForPendingSendAsync();

        lock (sent)
        {
            Assert.Equal(2, sent.Count);
            Assert.Equal(0, sent[0], 5);
            Assert.Equal(0.3, sent[1], 3);
        }
    }

    [Fact]
    public void SegmentsUseTheirAuthoredCurve()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(
            [new OutboundRampPoint(TimeSpan.Zero, 0, FadeCurve.Exponential),
             new OutboundRampPoint(TimeSpan.FromSeconds(1), 1)],
            sent.Add);

        runner.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0.125, sent[^1], 3);
    }

    [Fact]
    public void DecreasingSegmentsDoNotReverseTheirAuthoredCurve()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(
            [new OutboundRampPoint(TimeSpan.Zero, 1, FadeCurve.Exponential),
             new OutboundRampPoint(TimeSpan.FromSeconds(1), 0)],
            sent.Add);

        runner.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0.875, sent[^1], 3);
    }

    [Fact]
    public void HoldsTheLastValue_PastTheEndOfTheRamp()
    {
        var sent = new List<double>();
        var runner = new OutboundRampRunner(Ramp(0.25, 0.9, 1), sent.Add, sendRateHz: 10);

        runner.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0.9d, sent[^1], 10);
        Assert.False(runner.Advance(TimeSpan.FromSeconds(6)), "a finished ramp kept sending");
    }

    [Fact]
    public void MultiSegmentRamps_FollowEachSegment()
    {
        var sent = new List<double>();
        List<OutboundRampPoint> points =
        [
            new(TimeSpan.Zero, 0),
            new(TimeSpan.FromSeconds(1), 1),
            new(TimeSpan.FromSeconds(2), 0.5),
        ];
        var runner = new OutboundRampRunner(points, sent.Add, sendRateHz: 20);

        runner.Advance(TimeSpan.FromMilliseconds(500));
        var midFirst = sent[^1];
        runner.Advance(TimeSpan.FromMilliseconds(1500));
        var midSecond = sent[^1];

        Assert.Equal(0.5d, midFirst, 2);
        Assert.Equal(0.75d, midSecond, 2);
    }

    [Fact]
    public void RejectsUnsortedOrEmptyKeyframes_AndNonPositiveRates()
    {
        Assert.Throws<ArgumentException>(() => new OutboundRampRunner([], _ => { }));
        Assert.Throws<ArgumentException>(() => new OutboundRampRunner(
            [new(TimeSpan.FromSeconds(1), 0), new(TimeSpan.Zero, 1)], _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboundRampRunner(
            Ramp(0, 1, 1), _ => { }, sendRateHz: 0));
    }
}
