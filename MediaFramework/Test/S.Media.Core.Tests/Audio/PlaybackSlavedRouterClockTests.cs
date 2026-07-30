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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = Task.Run(() => clock.WaitForNextChunk(cts.Token));

        // Wait until the polling thread has actually OBSERVED the re-anchored clock, rather than
        // sleeping and hoping it got scheduled. Ordering matters and is not decorative: the clock
        // requires one fresh chunk of progress measured from where it saw the new epoch, so if the
        // advance below landed first, the deadline would be set 10 ms AHEAD of a fake that never
        // advances again and the wait would hang until the timeout. (That was a real 1-in-4 flake,
        // not an implementation fault - in production the ingest master keeps moving.)
        Assert.True(master.WaitUntilReadAfterReanchor(TimeSpan.FromSeconds(5)), "wait thread never read the clock");
        Assert.False(wait.IsCompleted); // a re-anchor alone must not synthesize a chunk

        master.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(await wait.WaitAsync(cts.Token));
    }

    /// <summary>Test master. <see cref="Read"/> is taken under a lock and the mutators write under the
    /// same one, so the polling thread can never see a torn (epoch, elapsed) pair - the fake must not
    /// invent a failure mode the contract exists to forbid.</summary>
    private sealed class FakeIngestClock(TimeSpan elapsed) : IPlaybackClock
    {
        private readonly Lock _gate = new();
        private readonly ManualResetEventSlim _readAfterReanchor = new(false);
        private long _epochId = PlaybackEpoch.Next();
        private TimeSpan _elapsed = elapsed;

        public TimeSpan ElapsedSinceStart
        {
            get { lock (_gate) return _elapsed; }
        }

        public bool IsAdvancing => true;

        public long EpochId
        {
            get { lock (_gate) return _epochId; }
        }

        public ClockReading Read()
        {
            lock (_gate)
            {
                _readAfterReanchor.Set();
                return new ClockReading(_epochId, _elapsed, IsAdvancing);
            }
        }

        public void Advance(TimeSpan delta)
        {
            lock (_gate) _elapsed += delta;
        }

        public void Reanchor(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _elapsed = elapsed;
                _epochId = PlaybackEpoch.Next();
                _readAfterReanchor.Reset();
            }
        }

        /// <summary>Blocks until someone has read this clock since the last <see cref="Reanchor"/>.</summary>
        public bool WaitUntilReadAfterReanchor(TimeSpan timeout) => _readAfterReanchor.Wait(timeout);
    }
}
