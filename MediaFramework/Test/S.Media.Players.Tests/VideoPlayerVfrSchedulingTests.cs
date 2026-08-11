using System.Collections.Concurrent;
using S.Media.Core.Video;
using S.Media.Time;
using Xunit;

namespace S.Media.Players.Tests;

public sealed class VideoPlayerVfrSchedulingTests
{
    [Fact]
    public void Scheduled_tick_forwards_every_due_vfr_timestamp_in_order()
    {
        var source = new TimestampSource(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(15),
            TimeSpan.FromMilliseconds(40));
        var output = new CollectingVideoOutput();
        var clock = new ManualMediaClock { CurrentPosition = TimeSpan.FromMilliseconds(20) };
        using var player = new VideoPlayer(source, output, clock, queueCapacity: 4);

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => player.QueuedFrameCount == 4, TimeSpan.FromSeconds(2)));

        clock.FireVideoTick();

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15)],
            output.PresentationTimes.ToArray());
        Assert.Equal(3, player.DisplayedCount);
        Assert.Equal(0, player.DroppedLate);
        Assert.Equal(1, player.QueuedFrameCount);
    }

    [Fact]
    public void Delivery_lead_emits_a_frame_before_its_due_time_without_changing_its_pts()
    {
        var source = new TimestampSource(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200));
        var output = new CollectingVideoOutput();
        // Playhead 92 ms + default 8 ms EarlyTolerance reaches exactly 100 ms: without a lead the
        // 100 ms frame sits on the decision boundary this feature exists to move away from.
        var clock = new ManualMediaClock { CurrentPosition = TimeSpan.FromMilliseconds(84) };
        using var player = new VideoPlayer(source, output, clock, queueCapacity: 4)
        {
            DeliveryLead = TimeSpan.FromMilliseconds(10),
        };

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => player.QueuedFrameCount == 2, TimeSpan.FromSeconds(2)));

        clock.FireVideoTick();

        // 84 + 8 (tolerance) + 10 (lead) = 102 ms window: the 100 ms frame is emitted a full 16 ms
        // before its PTS-due instant, keeping its authored timestamp; 200 ms stays queued.
        Assert.Equal([TimeSpan.FromMilliseconds(100)], output.PresentationTimes.ToArray());
        Assert.Equal(1, player.QueuedFrameCount);
        Assert.Equal(0, player.DroppedLate);
    }

    private sealed class TimestampSource(params TimeSpan[] timestamps) : IVideoSource
    {
        private static readonly VideoFormat SourceFormat =
            new(2, 2, PixelFormat.Bgra32, new Rational(24, 1));
        private int _next;

        public VideoFormat Format { get; private set; } = SourceFormat;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => Volatile.Read(ref _next) >= timestamps.Length;

        public void SelectOutputFormat(PixelFormat format) => Format = Format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _next) - 1;
            if ((uint)index >= (uint)timestamps.Length)
            {
                frame = null!;
                return false;
            }

            frame = new VideoFrame(timestamps[index], Format, [new byte[16]], [8]);
            return true;
        }
    }

    private sealed class CollectingVideoOutput : IVideoOutput
    {
        public ConcurrentQueue<TimeSpan> PresentationTimes { get; } = new();
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            PresentationTimes.Enqueue(frame.PresentationTime);
            frame.Dispose();
        }
    }

    private sealed class ManualMediaClock : IMediaClock
    {
        public TimeSpan CurrentPosition { get; set; }
        public bool IsRunning { get; private set; } = true;
        public double PlaybackRate => 1d;

        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler? VideoTick;

        public void FireVideoTick() => VideoTick?.Invoke(this, EventArgs.Empty);
        public void Start() => IsRunning = true;
        public void Stop(CancellationToken cancellationToken = default) => IsRunning = false;
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
