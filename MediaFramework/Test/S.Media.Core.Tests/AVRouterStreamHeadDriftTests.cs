using System;
using System.Threading;
using System.Threading.Tasks;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Covers the settling-baseline behaviour of
/// <see cref="AVRouter.GetAvStreamHeadDrift"/>: it must defer locking in the
/// baseline until two consecutive samples agree on <c>(aTicks − vTicks)</c>
/// within ~50 ms, so a slowly-settling pipeline (audio output buffer pre-fill,
/// resampler warm-up) doesn't poison the long-running drift signal with a
/// startup-transient zero point.
/// </summary>
public sealed class AVRouterStreamHeadDriftTests
{
    [Fact]
    public void StreamHeadDrift_ReturnsZero_WhilePipelineStillSettling()
    {
        using var router = new AVRouter();
        var aCh = new MovableAudioChannel();
        var vCh = new MovableVideoChannel();
        var aId = router.RegisterAudioInput(aCh);
        var vId = router.RegisterVideoInput(vCh);

        // Sample 1: aPos − vPos = 100 ms (still settling, no prior sample).
        aCh.SetPositionMs(1000);
        vCh.SetPositionMs(900);
        Assert.Equal(TimeSpan.Zero, router.GetAvStreamHeadDrift(aId, vId));

        // Sample 2: now 200 ms apart — Δ = 100 ms, well above the 50 ms
        // settling tolerance; baseline must remain unset → still 0.
        aCh.SetPositionMs(2000);
        vCh.SetPositionMs(1800);
        Assert.Equal(TimeSpan.Zero, router.GetAvStreamHeadDrift(aId, vId));

        // Sample 3: 300 ms apart — Δ from sample 2 = 100 ms, still moving.
        aCh.SetPositionMs(3000);
        vCh.SetPositionMs(2700);
        Assert.Equal(TimeSpan.Zero, router.GetAvStreamHeadDrift(aId, vId));
    }

    [Fact]
    public void StreamHeadDrift_LocksInBaseline_OnceTwoSamplesAgree()
    {
        using var router = new AVRouter();
        var aCh = new MovableAudioChannel();
        var vCh = new MovableVideoChannel();
        var aId = router.RegisterAudioInput(aCh);
        var vId = router.RegisterVideoInput(vCh);

        // Settling phase.
        aCh.SetPositionMs(1000); vCh.SetPositionMs(900);
        Assert.Equal(TimeSpan.Zero, router.GetAvStreamHeadDrift(aId, vId));
        aCh.SetPositionMs(2000); vCh.SetPositionMs(1800);
        Assert.Equal(TimeSpan.Zero, router.GetAvStreamHeadDrift(aId, vId));

        // Pipeline now settled — two consecutive samples agree on a − v = 200 ms.
        aCh.SetPositionMs(3200); vCh.SetPositionMs(3000);
        Assert.Equal(TimeSpan.Zero, router.GetAvStreamHeadDrift(aId, vId));

        // Real drift afterwards: a − v moves to 250 ms ⇒ +50 ms relative.
        // First post-baseline sample seeds the EMA so we should see ~50 ms back.
        aCh.SetPositionMs(4250); vCh.SetPositionMs(4000);
        var drift = router.GetAvStreamHeadDrift(aId, vId);
        Assert.InRange(drift.TotalMilliseconds, 45, 55);
    }

    [Fact]
    public void StreamHeadDrift_ReportsRelativeDelta_AfterBaselineLocked()
    {
        using var router = new AVRouter();
        var aCh = new MovableAudioChannel();
        var vCh = new MovableVideoChannel();
        var aId = router.RegisterAudioInput(aCh);
        var vId = router.RegisterVideoInput(vCh);

        // Two stable samples to lock in the baseline at (a − v) = 200 ms.
        aCh.SetPositionMs(1000); vCh.SetPositionMs(800);
        router.GetAvStreamHeadDrift(aId, vId);
        aCh.SetPositionMs(2000); vCh.SetPositionMs(1800);
        router.GetAvStreamHeadDrift(aId, vId);
        aCh.SetPositionMs(3000); vCh.SetPositionMs(2800);
        router.GetAvStreamHeadDrift(aId, vId);

        // No drift relative to baseline — should converge to 0 even though the
        // absolute offset is 200 ms.
        for (int t = 4; t < 12; t++)
        {
            aCh.SetPositionMs(t * 1000);
            vCh.SetPositionMs(t * 1000 - 200);
            router.GetAvStreamHeadDrift(aId, vId);
        }
        var settled = router.GetAvStreamHeadDrift(aId, vId);
        Assert.InRange(settled.TotalMilliseconds, -2, 2);
    }

    // ── Test stubs (minimal IAudioChannel / IVideoChannel with mutable Position) ──

    private sealed class MovableAudioChannel : IAudioChannel
    {
        private long _positionTicks;
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public AudioFormat SourceFormat { get; } = new(48000, 2);
        public float Volume { get; set; } = 1f;
        public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }
        public int FillBuffer(Span<float> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
        public void SetPositionMs(double ms) => Volatile.Write(ref _positionTicks, TimeSpan.FromMilliseconds(ms).Ticks);
    }

    private sealed class MovableVideoChannel : IVideoChannel
    {
        private long _positionTicks;
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; } = new(1920, 1080, PixelFormat.Yuv420p, 30, 1);
        public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
        public TimeSpan NextExpectedPts => Position;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }
        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public IVideoSubscription Subscribe(VideoSubscriptionOptions options) => new NopSub();
        public void Dispose() { }
        public void SetPositionMs(double ms) => Volatile.Write(ref _positionTicks, TimeSpan.FromMilliseconds(ms).Ticks);

        private sealed class NopSub : IVideoSubscription
        {
            public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
            public bool TryRead(out VideoFrame frame) { frame = default; return false; }
            public int Count => 0;
            public int Capacity => 4;
            public bool IsCompleted => false;
            public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
            public void Dispose() { }
        }
    }
}
