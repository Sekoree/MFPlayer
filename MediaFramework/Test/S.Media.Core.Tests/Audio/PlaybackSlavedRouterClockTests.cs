using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class PlaybackSlavedRouterClockTests
{
    [Fact]
    public void WaitForNextChunk_AdvancesWithIngestElapsed()
    {
        var master = new FakeIngestClock(TimeSpan.FromMilliseconds(20));
        var clock = new PlaybackSlavedRouterClock(master, 48_000, 480);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.True(master.ElapsedSinceStart >= TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task WaitForNextChunk_MasterStartsNewEpoch_DropsOldDeadline()
    {
        var master = new FakeIngestClock(TimeSpan.FromMilliseconds(20));
        var clock = new PlaybackSlavedRouterClock(master, 48_000, 480);
        Assert.True(clock.WaitForNextChunk(CancellationToken.None)); // next deadline: 20 ms

        master.Reanchor(TimeSpan.Zero);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var wait = Task.Run(() => clock.WaitForNextChunk(cts.Token));

        await Task.Delay(20, cts.Token);
        Assert.False(wait.IsCompleted); // a re-anchor alone must not synthesize a chunk

        master.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(await wait.WaitAsync(cts.Token));
    }

    private sealed class FakeIngestClock(TimeSpan elapsed) : IPlaybackClock
    {
        private long _epochId = PlaybackEpoch.Next();

        public TimeSpan ElapsedSinceStart { get; private set; } = elapsed;
        public bool IsAdvancing => true;
        public long EpochId => _epochId;
        public ClockReading Read() => new(_epochId, ElapsedSinceStart, IsAdvancing);

        public void Advance(TimeSpan delta) => ElapsedSinceStart += delta;

        public void Reanchor(TimeSpan elapsed)
        {
            ElapsedSinceStart = elapsed;
            _epochId = PlaybackEpoch.Next();
        }
    }
}
