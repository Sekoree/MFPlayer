using System.Collections.Concurrent;
using S.Media.Core.Video;
using S.Media.Time;
using Xunit;

namespace S.Media.Players.Tests;

/// <summary>
/// The live self-heal (2026-08-15 NDI judder root cause). A live timeline that runs ahead of the
/// playhead latches: the full queue blocks the reader, the receiver drops its oldest frames, every
/// drop jumps the incoming PTS further ahead, and playback settles at a fraction of the source
/// rate - a freeze/catch-up judder cycle. It latches whenever the playhead falls behind real time
/// for a moment (the audio ring prefill at a fire steps the clock to -ringDepth, a pause, a device
/// stall). The player must detect the pinned queue and re-anchor the source at the playhead.
/// </summary>
public sealed class VideoPlayerLiveLatchHealTests
{
    [Fact]
    public void APinnedAheadOfPlayheadQueueIsDrainedAndReanchored()
    {
        // Frames perpetually half a second ahead of a playhead parked at zero - the latched state.
        var source = new AheadLiveSource(firstPts: TimeSpan.FromMilliseconds(500));
        var output = new CollectingOutput();
        var clock = new ManualClock { CurrentPosition = TimeSpan.Zero };
        using var player = new VideoPlayer(source, output, clock, queueCapacity: 4);

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => player.QueuedFrameCount == 4, TimeSpan.FromSeconds(2)));

        // First tick observes the pinned queue and arms the heal window; nothing presents.
        clock.FireVideoTick();
        Assert.Empty(output.PresentationTimes);
        Assert.Empty(source.Rebases);

        // Past the persistence window the latch is certain: the queue is dropped and the source
        // re-anchored at the playhead.
        Thread.Sleep(400);
        clock.FireVideoTick();

        Assert.Equal(TimeSpan.Zero, Assert.Single(source.Rebases));

        // In production the playhead then RUNS; re-anchored frames (and the one stale frame the
        // decode thread carried across the drain) all come due and flow.
        clock.CurrentPosition = TimeSpan.FromMilliseconds(700);
        Assert.True(SpinWait.SpinUntil(() => player.QueuedFrameCount > 0, TimeSpan.FromSeconds(2)));
        clock.FireVideoTick();

        Assert.NotEmpty(output.PresentationTimes);
    }

    [Fact]
    public void AFullQueueOfDueFramesIsNotAHeal()
    {
        // Queue full but every frame due (playhead ahead of them): one burst drains it - healing
        // here would drop frames that are about to present.
        var source = new AheadLiveSource(firstPts: TimeSpan.Zero, frameCount: 4);
        var output = new CollectingOutput();
        var clock = new ManualClock { CurrentPosition = TimeSpan.FromMilliseconds(100) };
        using var player = new VideoPlayer(source, output, clock, queueCapacity: 4);

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => player.QueuedFrameCount == 4, TimeSpan.FromSeconds(2)));

        clock.FireVideoTick();
        Thread.Sleep(400);
        clock.FireVideoTick();

        Assert.Empty(source.Rebases);
        Assert.Equal(4, output.PresentationTimes.Count);
    }

    /// <summary>An endless live source whose PTS timeline starts wherever the test parks it; a
    /// rebase re-anchors the next frame at the handed-in playhead, like the NDI receiver does.</summary>
    private sealed class AheadLiveSource(TimeSpan firstPts, int frameCount = int.MaxValue) : ILiveVideoSource
    {
        private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1000d / 60);
        private readonly object _gate = new();
        private TimeSpan _nextPts = firstPts;
        private int _produced;

        public ConcurrentQueue<TimeSpan> Rebases { get; } = new();

        public VideoFormat Format { get; private set; } = new(2, 2, PixelFormat.Bgra32, new Rational(60, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => Volatile.Read(ref _produced) >= frameCount;

        public void SelectOutputFormat(PixelFormat format) => Format = Format with { PixelFormat = format };

        public void RebaseToLatest(TimeSpan playClockNow)
        {
            Rebases.Enqueue(playClockNow);
            lock (_gate)
                _nextPts = playClockNow;
        }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            if (Interlocked.Increment(ref _produced) > frameCount)
            {
                frame = null!;
                return false;
            }

            TimeSpan pts;
            lock (_gate)
            {
                pts = _nextPts;
                _nextPts += Period;
            }

            frame = new VideoFrame(pts, Format, [new byte[16]], [8]);
            return true;
        }
    }

    private sealed class CollectingOutput : IVideoOutput
    {
        public ConcurrentQueue<TimeSpan> PresentationTimes { get; } = new();
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [];
        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            PresentationTimes.Enqueue(frame.PresentationTime);
            frame.Dispose();
        }
    }

    private sealed class ManualClock : IMediaClock
    {
        public TimeSpan CurrentPosition { get; set; }
        public bool IsRunning { get; private set; } = true;
        public double PlaybackRate => 1d;

        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler? VideoTick;

        public void FireVideoTick() => VideoTick?.Invoke(this, EventArgs.Empty);
        public void Start() => IsRunning = true;
        public void Pause(CancellationToken cancellationToken = default) => IsRunning = false;
        public void Reset() => Seek(TimeSpan.Zero);
        public void SetMaster(IPlaybackClock? master) { }

        public void Seek(TimeSpan position)
        {
            CurrentPosition = position;
            PositionChanged?.Invoke(this, position);
        }
    }
}
