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
        public event EventHandler? AudioTick { add { } remove { } }
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
