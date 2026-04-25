using S.Media.NDI;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class NDIClockTests
{
    [Fact]
    public void Position_IsMonotonic_DuringFrequentFrameUpdates()
    {
        var clock = new NDIClock();
        clock.Start();
        try
        {
            long baseTicks = TimeSpan.FromSeconds(10).Ticks;
            long frameStep = TimeSpan.FromMilliseconds(16.6667).Ticks;

            TimeSpan prev = TimeSpan.Zero;
            for (int i = 0; i < 5000; i++)
            {
                // Simulate incoming ~60 fps timestamps.
                long pts = baseTicks + (i / 5) * frameStep;
                clock.UpdateFromFrame(pts);

                var now = clock.Position;
                Assert.True(now >= prev, $"Clock regressed at iteration {i}: {now} < {prev}");
                prev = now;
            }
        }
        finally
        {
            clock.Stop();
            clock.Reset();
        }
    }

    [Fact]
    public void Position_DoesNotRegress_AcrossRapidReads()
    {
        var clock = new NDIClock();
        clock.Start();
        try
        {
            long start = TimeSpan.FromSeconds(5).Ticks;
            clock.UpdateFromFrame(start);

            TimeSpan prev = clock.Position;
            for (int i = 0; i < 10000; i++)
            {
                var now = clock.Position;
                Assert.True(now >= prev, $"Clock regressed at read {i}: {now} < {prev}");
                prev = now;
            }
        }
        finally
        {
            clock.Stop();
            clock.Reset();
        }
    }
}

