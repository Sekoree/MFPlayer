using System.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class NullClockedAudioOutputTests
{
    private const int SampleRate = 48000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void ConsumesSubmittedSamples_AtRealTimeRate()
    {
        // 1 s of queued audio so a loaded runner oversleeping the 300 ms window cannot drain the queue.
        using var output = new NullClockedAudioOutput(Stereo, capacityFrames: SampleRate);
        output.Submit(new float[SampleRate * Stereo.Channels]);

        var sw = Stopwatch.StartNew();
        output.Start();
        Thread.Sleep(300);
        var consumed = output.ConsumedSamples;
        var elapsed = output.ElapsedSinceStart;
        sw.Stop();

        // Consumption is computed from absolute timestamps, so it tracks the ACTUAL elapsed window
        // exactly; ±10% absorbs the skew between the stopwatch reads and the counter reads.
        var expected = sw.Elapsed.TotalSeconds * SampleRate;
        Assert.InRange(consumed, expected * 0.9, expected * 1.1);
        Assert.InRange(elapsed.TotalSeconds, sw.Elapsed.TotalSeconds * 0.9, sw.Elapsed.TotalSeconds * 1.1);
    }

    [Fact]
    public void ClockFreezes_WhenTheQueueUnderruns()
    {
        using var output = new NullClockedAudioOutput(Stereo);
        output.Start();

        // Nothing submitted: wall time passes but no samples are consumed.
        Thread.Sleep(50);
        Assert.Equal(TimeSpan.Zero, output.ElapsedSinceStart);

        // 100 ms of audio, then sleep well past it: the clock stops exactly at the submitted amount
        // (underrun silence never advances it), which is deterministic - no timing tolerance needed.
        output.Submit(new float[4800 * Stereo.Channels]);
        Thread.Sleep(250);
        Assert.Equal(0.1, output.ElapsedSinceStart.TotalSeconds, 3);
        Assert.Equal(4800, output.ConsumedSamples);
    }

    [Fact]
    public void WaitForCapacity_PacesTheProducer()
    {
        using var output = new NullClockedAudioOutput(Stereo)
        {
            TargetQueueSamples = 960, // two 10 ms chunks of headroom
        };
        output.Start();

        var chunk = new float[480 * Stereo.Channels];
        var produced = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 300)
        {
            Assert.True(output.WaitForCapacity(480, CancellationToken.None));
            output.Submit(chunk);
            produced++;
        }
        sw.Stop();

        // Steady state is one 10 ms chunk per 10 ms of wall time (+2 chunks of initial headroom).
        // Scale against the actual elapsed window like the router clocking tests do.
        var expected = sw.Elapsed.TotalMilliseconds / 10.0;
        Assert.InRange(produced, expected * 0.5, expected * 1.5 + 4);
        // Pacing must never let the queue exceed target + one chunk.
        Assert.InRange(output.QueuedSamples, 0, 960 + 480);
    }

    [Fact]
    public void ElapsedSinceStart_IsMonotonic_AndReanchorsOnFlush()
    {
        using var output = new NullClockedAudioOutput(Stereo, capacityFrames: SampleRate);
        output.Submit(new float[SampleRate * Stereo.Channels]);
        output.Start();

        var previous = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 60)
        {
            var now = output.ElapsedSinceStart;
            Assert.True(now >= previous, $"clock went backwards: {previous} -> {now}");
            previous = now;
        }
        Assert.True(previous > TimeSpan.Zero, "clock never advanced with a full queue");

        output.Flush();
        Assert.Equal(TimeSpan.Zero, output.ElapsedSinceStart);
        // The flush emptied the queue, so the clock stays at zero until new samples arrive.
        Thread.Sleep(20);
        Assert.Equal(TimeSpan.Zero, output.ElapsedSinceStart);

        output.Submit(new float[4800 * Stereo.Channels]);
        Thread.Sleep(30);
        Assert.True(output.ElapsedSinceStart > TimeSpan.Zero, "clock did not resume after post-flush submit");
    }

    [Fact]
    public void IsAdvancing_TracksTheStartStopLifecycle()
    {
        var output = new NullClockedAudioOutput(Stereo);
        Assert.False(output.IsAdvancing);

        output.Start();
        Assert.True(output.IsAdvancing);

        output.Stop();
        Assert.False(output.IsAdvancing);

        output.Start();
        Assert.True(output.IsAdvancing);

        output.Dispose();
        Assert.False(output.IsAdvancing);
        Assert.Throws<ObjectDisposedException>(() => output.Submit(new float[Stereo.Channels]));
    }

    [Fact]
    public void WaitForCapacity_BeforeStart_ReportsReady_SoPrefillCanFill()
    {
        using var output = new NullClockedAudioOutput(Stereo);
        Assert.True(output.WaitForCapacity(480, CancellationToken.None));
        output.Submit(new float[480 * Stereo.Channels]);
        Assert.Equal(480, output.QueuedSamples); // nothing drains before Start
    }
}
