using S.Media.Routing;
using HaPlay.Playback;
using S.Media.Core.Audio;
using S.Media.Time;
using S.Media.Decode.FFmpeg.Audio;
using Xunit;

namespace HaPlay.Tests;

public sealed class MeteringAudioOutputTests
{
    [Fact]
    public void Wrap_PreservesClockedPlaybackOutput()
    {
        var inner = new ClockedPlaybackOutput(new AudioFormat(48_000, 2));
        var meter = MeteringAudioOutput.Wrap(inner);

        var clocked = Assert.IsAssignableFrom<IClockedOutput>(meter);
        var playback = Assert.IsAssignableFrom<IPlaybackClock>(meter);
        var flushable = Assert.IsAssignableFrom<IFlushableOutput>(meter);

        Assert.True(clocked.WaitForCapacity(480, CancellationToken.None));
        Assert.Equal(480, inner.LastWaitSamples);
        Assert.Equal(TimeSpan.FromSeconds(7), playback.ElapsedSinceStart);
        flushable.Flush();
        Assert.True(inner.Flushed);
    }

    [Fact]
    public void Wrap_DoesNotPromoteNonClockedOutput()
    {
        var meter = MeteringAudioOutput.Wrap(new PlainOutput(new AudioFormat(48_000, 2)));

        Assert.IsNotAssignableFrom<IClockedOutput>(meter);
        Assert.IsNotAssignableFrom<IPlaybackClock>(meter);
        Assert.IsAssignableFrom<IFlushableOutput>(meter);
    }

    [Fact]
    public void WrappedClockedOutput_BecomesAudioRouterPrimaryAndClockMaster()
    {
        using var clock = new MediaClock();
        using var router = new AudioRouter(48_000, 480);
        router.AttachMasterClock(clock);

        var meter = MeteringAudioOutput.Wrap(new ClockedPlaybackOutput(new AudioFormat(48_000, 2)));
        var id = router.AddOutput(meter);

        Assert.Equal(id, router.PrimaryOutputId);
        Assert.Same(meter, clock.Master);
    }

    [Fact]
    public void WrappedResampledClockedOutput_BecomesAudioRouterPrimaryAndClockMaster()
    {
        using var clock = new MediaClock();
        using var router = new AudioRouter(44_100, 441);
        router.AttachMasterClock(clock);

        var inner = new ClockedPlaybackOutput(new AudioFormat(48_000, 2));
        var resampled = ResamplingAudioOutput.Wrap(inner, new AudioFormat(44_100, 2));
        var meter = MeteringAudioOutput.Wrap(resampled);
        var id = router.AddOutput(meter);

        Assert.Equal(id, router.PrimaryOutputId);
        Assert.Same(meter, clock.Master);
        Assert.True(((IClockedOutput)meter).WaitForCapacity(441, CancellationToken.None));
        Assert.Equal(482, inner.LastWaitSamples);
    }

    /// <summary>
    /// The production deck chain is lease → effects → meter → resample (see
    /// <c>MediaPlayerViewModel.ShowSession.BuildDeckAudioLease</c>), and every one of those wrappers used to
    /// swallow <see cref="IAudioOutputLatency"/> - the capability was implemented by the backends and
    /// visible to nobody above them (2026-07-30 review §2). It is now implemented unconditionally on each
    /// wrapper's base, so it survives an arbitrary stack of them.
    /// </summary>
    [Fact]
    public void Latency_SurvivesTheWholeDecoratorChain()
    {
        var inner = new ClockedPlaybackOutput(new AudioFormat(48_000, 2))
        {
            SubmitToOutputLatency = TimeSpan.FromMilliseconds(37),
        };
        var effects = AudioEffectOutput.Wrap(inner, []);
        var meter = MeteringAudioOutput.Wrap(effects);
        var resampled = ResamplingAudioOutput.Wrap(meter, new AudioFormat(44_100, 2));

        foreach (var stage in new IAudioOutput[] { effects, meter, resampled })
        {
            var reporting = Assert.IsAssignableFrom<IAudioOutputLatency>(stage);
            Assert.Equal(TimeSpan.FromMilliseconds(37), reporting.SubmitToOutputLatency);
        }
    }

    /// <summary>
    /// Unlike the clock/clocked faces, this one is safe to claim over an inner that lacks it: the interface
    /// defines <see cref="TimeSpan.Zero"/> as "unknown" and the only consumer adds it solely when positive,
    /// so "implemented, reporting Zero" and "not implemented" are indistinguishable. That is what keeps it
    /// OFF the conditional capability matrix - see <see cref="AudioOutputLatency"/>.
    /// </summary>
    [Fact]
    public void Latency_OverAnOutputThatReportsNone_IsZeroRatherThanAbsent()
    {
        var meter = MeteringAudioOutput.Wrap(new PlainOutput(new AudioFormat(48_000, 2)));

        var reporting = Assert.IsAssignableFrom<IAudioOutputLatency>(meter);
        Assert.Equal(TimeSpan.Zero, reporting.SubmitToOutputLatency);
        // The capabilities that DO change behaviour must still be absent - claiming those would send the
        // router down the device-pacing path over an output that cannot pace.
        Assert.IsNotAssignableFrom<IClockedOutput>(meter);
        Assert.IsNotAssignableFrom<IPlaybackClock>(meter);
    }

    [Fact]
    public void Latency_FromAThrowingOutput_DegradesToUnknown()
    {
        // Implementations promise not to throw; the helper still survives one, because it is read from
        // clock hot paths whose own contract is never to throw and a device disposed mid-read is real.
        var meter = MeteringAudioOutput.Wrap(new ThrowingLatencyOutput(new AudioFormat(48_000, 2)));

        Assert.Equal(TimeSpan.Zero, ((IAudioOutputLatency)meter).SubmitToOutputLatency);
    }

    private sealed class PlainOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ThrowingLatencyOutput(AudioFormat format) : IAudioOutput, IAudioOutputLatency
    {
        public AudioFormat Format { get; } = format;

        public TimeSpan SubmitToOutputLatency => throw new ObjectDisposedException(nameof(ThrowingLatencyOutput));

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ClockedPlaybackOutput(AudioFormat format) :
        IAudioOutput,
        IClockedOutput,
        IPlaybackClock,
        IFlushableOutput,
        IAudioOutputLatency
    {
        public AudioFormat Format { get; } = format;

        public int LastWaitSamples { get; private set; }

        public bool Flushed { get; private set; }

        public TimeSpan SubmitToOutputLatency { get; init; }

        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(7);

        public bool IsAdvancing => true;

        public void Submit(ReadOnlySpan<float> packedSamples) { }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            LastWaitSamples = chunkSamples;
            return !token.IsCancellationRequested;
        }

        public void Flush() => Flushed = true;
    }
}
