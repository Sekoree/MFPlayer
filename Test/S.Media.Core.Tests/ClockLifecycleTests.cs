using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Covers the Tier 1 clock fixes from Implementation-Checklist:
///   - §3.31a / C6: <see cref="StopwatchClock.Reset"/> stops the base tick timer.
///   - §3.31 / C5: <see cref="HardwareClock"/> debounces exit from fallback.
///   - §3.30 / C1: <see cref="MediaClockBase"/> tick callback after Dispose is a no-op.
/// </summary>
public sealed class ClockLifecycleTests
{
    [Fact]
    public void StopwatchClock_ResetAfterStart_StopsTickTimer()
    {
        // Short cadence so we'd see several ticks in 120 ms if the timer were
        // left armed after Reset.
        using var clock = new StopwatchClock(TimeSpan.FromMilliseconds(10));
        int tickCount = 0;
        clock.Tick += _ => Interlocked.Increment(ref tickCount);

        clock.Start();
        Thread.Sleep(40);
        int countBeforeReset = Volatile.Read(ref tickCount);
        Assert.True(countBeforeReset >= 2,
            $"Expected at least 2 ticks while running, got {countBeforeReset}.");

        clock.Reset(); // §3.31a: must stop the base timer too.
        Thread.Sleep(120);
        int countAfterReset = Volatile.Read(ref tickCount);

        // A small race is possible if a tick was already in flight when Reset
        // ran, so allow +1 but not a string of continued ticks.
        Assert.InRange(countAfterReset - countBeforeReset, 0, 1);
    }

    [Fact]
    public void HardwareClock_SingleFlakyValidRead_DoesNotLeaveFallback()
    {
        // Simulate a driver that returns 0 (invalid) most of the time but
        // occasionally returns a spuriously-valid reading. Without §3.31
        // debouncing, each isolated valid reading would snap Position from
        // (fallback-interpolated) down to (raw hw value), visible as time
        // travelling backwards.
        int call = 0;
        double[] sequence =
        [
            0.0,     // invalid → enter fallback
            0.0,     // invalid
            0.123,   // first valid — must NOT exit fallback alone
            0.0,     // invalid again — confirm still in fallback
            0.0,
        ];

        double Provider()
        {
            int i = Interlocked.Increment(ref call) - 1;
            return i < sequence.Length ? sequence[i] : 0.0;
        }

        using var clock = new HardwareClock(Provider, sampleRate: 48000);

        var p1 = clock.Position; // enters fallback
        Thread.Sleep(15);
        var p2 = clock.Position; // still invalid
        Thread.Sleep(15);
        var p3 = clock.Position; // flaky valid — debounced, must stay on fallback curve
        Thread.Sleep(15);
        var p4 = clock.Position; // back to invalid, fallback continues

        // Fallback stopwatch-driven position must be monotonic across the flaky read.
        Assert.True(p1 <= p2, $"p1={p1} p2={p2}");
        Assert.True(p2 <= p3, $"p2={p2} p3={p3}");
        Assert.True(p3 <= p4, $"p3={p3} p4={p4}");
    }

    [Fact]
    public void MediaClockBase_DisposeDuringRunning_SuppressesFurtherTicks()
    {
        var clock = new StopwatchClock(TimeSpan.FromMilliseconds(5));
        int tickCount = 0;
        clock.Tick += _ => Interlocked.Increment(ref tickCount);

        clock.Start();
        Thread.Sleep(30);
        int before = Volatile.Read(ref tickCount);

        clock.Dispose();
        Thread.Sleep(50);
        int after = Volatile.Read(ref tickCount);

        // At most one in-flight tick may slip through between Dispose and the
        // _disposed observation; anything more implies OnTimerTick is still
        // invoking handlers post-dispose (§3.30 / C1).
        Assert.InRange(after - before, 0, 1);
    }
}

