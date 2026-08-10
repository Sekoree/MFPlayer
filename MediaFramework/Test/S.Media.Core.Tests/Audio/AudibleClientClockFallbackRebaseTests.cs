using S.Media.Routing;
using S.Media.Time;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// The first-terminal-outage splice in <see cref="AudibleClientClock"/>. The fallback stopwatch runs
/// from ATTACH and counts every second the device spent idle/paused, so the splice must re-baseline
/// it onto the terminal domain's high-water before any reader consumes the stopwatch domain.
/// </summary>
/// <remarks>
/// The concurrency test pins the publication ORDER of that transition. The original code claimed the
/// "rebased" flag with an Interlocked.Exchange BEFORE writing the spliced epoch under the gate, so at
/// the first outage a second concurrent reader could see the flag already set, skip the gate, and
/// compute elapsed from the STALE pre-splice epoch: wall-time-since-attach, potentially minutes ahead
/// of real playback. That one reading flowed through the monotonic audible high-water and PERMANENTLY
/// pinned the clock in the future (the high-water only ever rises). The fixed ordering writes the
/// epoch first and publishes the flag last, both under the gate; the
/// <c>FallbackSpliceUnderGateForTest</c> seam holds the first faller inside the transition so the
/// interleaving that used to leak the stale epoch is exercised deterministically.
/// </remarks>
public sealed class AudibleClientClockFallbackRebaseTests
{
    [Fact]
    public void FirstOutage_SplicesTheFallbackOntoTheHighWater_NotWallTimeSinceAttach()
    {
        var terminal = new FailableTerminalClock();
        var clock = new AudibleClientClock(terminal, static () => 0);

        terminal.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(100.0, clock.ElapsedSinceStart.TotalMilliseconds, 1); // high-water = 100 ms

        // Let the attach-anchored stopwatch run far past the device: the whole point of the splice is
        // that this wall time must NOT surface when the terminal drops.
        Thread.Sleep(400);
        terminal.FailFromNow();

        var elapsed = clock.ElapsedSinceStart;
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(300),
            $"fallback read {elapsed.TotalMilliseconds:0}ms - the splice adopted wall-since-attach instead of the 100ms high-water");
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100), "the splice must resume from the high-water, not below it");
    }

    [Fact]
    public async Task FirstOutage_ConcurrentReader_CannotObserveThePreSpliceEpoch()
    {
        var terminal = new FailableTerminalClock();
        var clock = new AudibleClientClock(terminal, static () => 0);

        terminal.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(100.0, clock.ElapsedSinceStart.TotalMilliseconds, 1);

        // Make the stale-epoch reading unmistakable: wall-since-attach >= 400 ms vs a 100 ms high-water.
        Thread.Sleep(400);
        terminal.FailFromNow();

        using var seamEntered = new ManualResetEventSlim();
        using var releaseSplice = new ManualResetEventSlim();
        clock.FallbackSpliceUnderGateForTest = () =>
        {
            seamEntered.Set();
            // Hold the FIRST faller inside the outage transition (under the gate, epoch not yet
            // written). With flag-first publication this was exactly the window in which reader B
            // returned wall-since-attach from the stale epoch.
            Assert.True(releaseSplice.Wait(TimeSpan.FromSeconds(5)), "test seam was never released");
        };

        var readerA = Task.Run(() => clock.ElapsedSinceStart);
        Assert.True(seamEntered.Wait(TimeSpan.FromSeconds(5)), "reader A never reached the splice");

        var readerB = Task.Run(() => clock.ElapsedSinceStart);
        // Give B every chance to (incorrectly) race past the transition before A completes it.
        Thread.Sleep(100);
        releaseSplice.Set();

        var elapsedA = await readerA.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsedB = await readerB.WaitAsync(TimeSpan.FromSeconds(5));
        clock.FallbackSpliceUnderGateForTest = null;

        // Both readers must report the spliced domain (high-water + a few ms of wall progression),
        // never wall-since-attach. Bug shape: reader B >= 500 ms, and every later read pinned there.
        Assert.True(
            elapsedA < TimeSpan.FromMilliseconds(300),
            $"reader A saw {elapsedA.TotalMilliseconds:0}ms - stale epoch leaked");
        Assert.True(
            elapsedB < TimeSpan.FromMilliseconds(300),
            $"reader B saw {elapsedB.TotalMilliseconds:0}ms - stale epoch leaked through the flag/epoch race");

        // And the audible high-water was not ratcheted by a phantom reading: post-outage reads stay
        // anchored on the splice, advancing at wall rate from the 100 ms resume point.
        var settled = clock.ElapsedSinceStart;
        Assert.True(
            settled < TimeSpan.FromMilliseconds(350),
            $"post-outage read {settled.TotalMilliseconds:0}ms - a stale reading permanently ratcheted the clock");
    }

    [Fact]
    public void TerminalRecovery_ClearsTheSplice_SoALaterOutageRebasesAgain()
    {
        var terminal = new FailableTerminalClock();
        var clock = new AudibleClientClock(terminal, static () => 0);

        terminal.Advance(TimeSpan.FromMilliseconds(100));
        _ = clock.ElapsedSinceStart;

        terminal.FailFromNow();
        _ = clock.ElapsedSinceStart; // outage 1: splices

        terminal.Recover();
        terminal.Advance(TimeSpan.FromMilliseconds(200)); // device catches up past the high-water
        Assert.Equal(300.0, clock.ElapsedSinceStart.TotalMilliseconds, 0);

        Thread.Sleep(300); // idle wall time the second splice must NOT surface either
        terminal.FailFromNow();
        var second = clock.ElapsedSinceStart;
        Assert.True(
            second < TimeSpan.FromMilliseconds(450),
            $"second outage read {second.TotalMilliseconds:0}ms - the splice was not re-derived for the new outage");
        Assert.True(second >= TimeSpan.FromMilliseconds(300));
    }

    /// <summary>A terminal clock the test can drive and break: reads throw once
    /// <see cref="FailFromNow"/> is called - the shape of a device torn down mid-read.</summary>
    private sealed class FailableTerminalClock : IPlaybackClock
    {
        private long _elapsedTicks;
        private volatile bool _failing;

        public TimeSpan ElapsedSinceStart =>
            _failing ? throw new ObjectDisposedException(nameof(FailableTerminalClock)) : new(Volatile.Read(ref _elapsedTicks));

        public long EpochId => 1;
        public bool IsAdvancing => !_failing;

        public ClockReading Read() =>
            _failing
                ? throw new ObjectDisposedException(nameof(FailableTerminalClock))
                : new ClockReading(EpochId, new TimeSpan(Volatile.Read(ref _elapsedTicks)), true);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);
        public void FailFromNow() => _failing = true;
        public void Recover() => _failing = false;
    }
}
