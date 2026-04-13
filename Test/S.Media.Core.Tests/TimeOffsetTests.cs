using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Tests for per-channel time offsets on audio and video mixers,
/// and A/V drift monitoring via <see cref="AVMixer"/>.
/// </summary>
public sealed class TimeOffsetTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static AudioFormat Mono48k => new(48000, 1);
    private static VideoFormat FmtRgba30 => new(640, 360, PixelFormat.Rgba32, 30, 1);

    /// <summary>Channel that always returns a constant value when pulled, counting frames pulled.</summary>
    private sealed class ConstantChannel : IAudioChannel
    {
        private readonly float _value;
        private long _framesPulled;
        public ConstantChannel(AudioFormat fmt, float value = 1f) { SourceFormat = fmt; _value = value; }
        public Guid        Id           { get; } = Guid.NewGuid();
        public AudioFormat SourceFormat { get; }
        public bool        IsOpen       => true;
        public bool        CanSeek      => false;
        public float       Volume       { get; set; } = 1.0f;
        public int         BufferDepth  => 8;
        public int         BufferAvailable => int.MaxValue;
        public TimeSpan    Position     => TimeSpan.Zero;
        public long        FramesPulled => Interlocked.Read(ref _framesPulled);
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            dest[..dest.Length].Fill(_value);
            Interlocked.Add(ref _framesPulled, frameCount);
            return frameCount;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class QueueVideoChannel : IVideoChannel
    {
        private readonly Queue<VideoFrame> _frames = new();
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; }
        public TimeSpan Position { get; private set; }

        public QueueVideoChannel(VideoFormat format) => SourceFormat = format;

        public void Enqueue(VideoFrame frame) => _frames.Enqueue(frame);

        public int BufferDepth => 64;
        public int BufferAvailable => _frames.Count;
        public event EventHandler? EndOfStream { add { } remove { } }

        public int FillBuffer(Span<VideoFrame> dest, int frameCount)
        {
            if (frameCount <= 0 || _frames.Count == 0) return 0;
            var frame = _frames.Dequeue();
            dest[0] = frame;
            Position = frame.Pts;
            return 1;
        }

        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class SpyVideoSink : IVideoSink
    {
        public string Name => nameof(SpyVideoSink);
        public bool IsRunning { get; set; } = true;
        public int Calls { get; private set; }
        public VideoFrame? LastFrame { get; private set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ReceiveFrame(in VideoFrame frame)
        {
            Calls++;
            LastFrame = frame;
        }

        public void Dispose() { }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Video time offset tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void VideoMixer_SetChannelTimeOffset_RoundTrips()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);
        mixer.AddChannel(ch);

        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.FromMilliseconds(200));
        Assert.Equal(TimeSpan.FromMilliseconds(200), mixer.GetChannelTimeOffset(ch.Id));

        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, mixer.GetChannelTimeOffset(ch.Id));
    }

    [Fact]
    public void VideoMixer_SetChannelTimeOffset_UnregisteredChannel_Throws()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        Assert.Throws<InvalidOperationException>(
            () => mixer.SetChannelTimeOffset(Guid.NewGuid(), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void VideoMixer_PositiveOffset_DelaysPresentation()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);

        // Enqueue a frame at PTS=0 (normalized) and another at PTS=500ms
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(100)));
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(600)));

        mixer.AddChannel(ch);
        mixer.RouteChannelToPrimaryOutput(ch.Id);

        // Set a 200ms delay offset — this shifts the effective clock backwards by 200ms,
        // so frames that would normally be due at clock=T are now due at clock=T+200ms.
        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.FromMilliseconds(200));

        // Bootstrap: first frame always presented immediately.
        var f1 = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(0));
        Assert.True(f1.HasValue);

        // At clock=400ms, without offset the 500ms-PTS frame would be held.
        // With 200ms offset, effective clock = 400-200 = 200ms, so the 500ms frame 
        // should still be held.
        var f2 = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(400));
        Assert.True(f2.HasValue);
        // The second frame (PTS=500ms) should still be held, not yet presented.
        Assert.True(mixer.HeldFrameCount > 0);
    }

    [Fact]
    public void VideoMixer_NegativeOffset_AdvancesPresentation()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);

        // Frame at PTS=0 and PTS=500ms (after normalization)
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(100)));
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(600)));

        mixer.AddChannel(ch);
        mixer.RouteChannelToPrimaryOutput(ch.Id);

        // Negative offset: advance by 200ms. Effective clock = clock + 200ms.
        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.FromMilliseconds(-200));

        // Bootstrap: first frame always presented immediately.
        var f1 = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(0));
        Assert.True(f1.HasValue);

        // At clock=350ms, effective = 350+200 = 550ms, which is past the 500ms PTS.
        // The second frame should be presented.
        var f2 = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(350));
        Assert.True(f2.HasValue);
        Assert.Equal(TimeSpan.FromMilliseconds(500), f2.Value.Pts);
    }

    [Fact]
    public void VideoMixer_Offset_AppliesPerSinkChannel()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var chLeader = new QueueVideoChannel(FmtRgba30);
        var chSink = new QueueVideoChannel(FmtRgba30);

        chLeader.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));
        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.RouteChannelToPrimaryOutput(chLeader.Id);

        var sink = new SpyVideoSink();
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        // Offset only the sink channel, not the leader
        mixer.SetChannelTimeOffset(chSink.Id, TimeSpan.FromMilliseconds(500));

        // At clock=20ms both should bootstrap (first frame always presented immediately).
        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        // The sink should still get its first frame (bootstrap rule).
        Assert.True(sink.LastFrame.HasValue);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Audio time offset tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void AudioMixer_SetChannelTimeOffset_RoundTrips()
    {
        using var mixer = new AudioMixer(Mono48k);
        var ch = new ConstantChannel(Mono48k, 1f);
        mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.FromMilliseconds(200));
        Assert.Equal(TimeSpan.FromMilliseconds(200), mixer.GetChannelTimeOffset(ch.Id));

        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, mixer.GetChannelTimeOffset(ch.Id));
    }

    [Fact]
    public void AudioMixer_SetChannelTimeOffset_UnregisteredChannel_Throws()
    {
        using var mixer = new AudioMixer(Mono48k);
        Assert.Throws<InvalidOperationException>(
            () => mixer.SetChannelTimeOffset(Guid.NewGuid(), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void AudioMixer_PositiveOffset_InsertsSilence()
    {
        using var mixer = new AudioMixer(Mono48k);
        var ch = new ConstantChannel(Mono48k, 0.8f);
        mixer.AddChannel(ch, ChannelRouteMap.Identity(1));
        mixer.PrepareBuffers(256);

        // Set a 200ms delay = 9600 frames of silence at 48kHz.
        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.FromMilliseconds(200));

        float[] dest = new float[256];

        // First fill: should output silence (channel is delayed).
        mixer.FillOutputBuffer(dest, 256, Mono48k);
        Assert.All(dest, s => Assert.Equal(0f, s));

        // After enough fills to exhaust the delay, channel data should appear.
        // 9600 frames / 256 = 37.5 buffers. After 38 buffers the delay is consumed.
        for (int i = 0; i < 38; i++)
            mixer.FillOutputBuffer(dest, 256, Mono48k);

        // Now one more fill should produce the channel's audio.
        mixer.FillOutputBuffer(dest, 256, Mono48k);
        Assert.Contains(dest, s => Math.Abs(s - 0.8f) < 0.01f);
    }

    [Fact]
    public void AudioMixer_NegativeOffset_DiscardsFrames()
    {
        using var mixer = new AudioMixer(Mono48k);
        var ch = new ConstantChannel(Mono48k, 0.5f);
        mixer.AddChannel(ch, ChannelRouteMap.Identity(1));
        mixer.PrepareBuffers(256);

        // Set a -50ms advance = 2400 frames to discard at 48kHz.
        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.FromMilliseconds(-50));

        float[] dest = new float[256];

        // First several fills: the channel is pulled but discarded (advance).
        // 2400 / 256 = ~9.4, so after ~10 buffers the advance is consumed.
        for (int i = 0; i < 10; i++)
            mixer.FillOutputBuffer(dest, 256, Mono48k);

        // After the advance is consumed, normal audio should appear.
        mixer.FillOutputBuffer(dest, 256, Mono48k);
        Assert.Contains(dest, s => Math.Abs(s - 0.5f) < 0.01f);

        // The channel should have been pulled more frames than output
        // (discarded frames + normal frames).
        Assert.True(ch.FramesPulled > 256);
    }

    [Fact]
    public void AudioMixer_ZeroOffset_NoEffect()
    {
        using var mixer = new AudioMixer(Mono48k);
        var ch = new ConstantChannel(Mono48k, 0.7f);
        mixer.AddChannel(ch, ChannelRouteMap.Identity(1));
        mixer.PrepareBuffers(256);

        mixer.SetChannelTimeOffset(ch.Id, TimeSpan.Zero);

        float[] dest = new float[256];
        mixer.FillOutputBuffer(dest, 256, Mono48k);

        // Immediately produces channel audio.
        Assert.All(dest, s => Assert.Equal(0.7f, s, precision: 5));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AVMixer time offset forwarding & drift monitoring tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void AVMixer_SetTimeOffsets_ForwardsToSubMixers()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), FmtRgba30);

        var audioCh = new DummyAudioChannel();
        var videoCh = new DummyVideoChannel();

        av.AddAudioChannel(audioCh, ChannelRouteMap.Identity(2));
        av.AddVideoChannel(videoCh);

        av.SetAudioChannelTimeOffset(audioCh.Id, TimeSpan.FromMilliseconds(100));
        av.SetVideoChannelTimeOffset(videoCh.Id, TimeSpan.FromMilliseconds(-50));

        Assert.Equal(TimeSpan.FromMilliseconds(100), av.GetAudioChannelTimeOffset(audioCh.Id));
        Assert.Equal(TimeSpan.FromMilliseconds(-50), av.GetVideoChannelTimeOffset(videoCh.Id));
    }

    [Fact]
    public void AVMixer_GetAvDrift_ReturnsPositionDifference()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), FmtRgba30);

        var audioCh = new PositionTrackingAudioChannel(TimeSpan.FromMilliseconds(500));
        var videoCh = new PositionTrackingVideoChannel(TimeSpan.FromMilliseconds(300));

        av.AddAudioChannel(audioCh, ChannelRouteMap.Identity(2));
        av.AddVideoChannel(videoCh);

        var drift = av.GetAvDrift(audioCh.Id, videoCh.Id);
        // audio(500ms) - video(300ms) = +200ms → audio is ahead
        Assert.Equal(TimeSpan.FromMilliseconds(200), drift);
    }

    [Fact]
    public void AVMixer_GetAvDrift_UnregisteredChannel_Throws()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), FmtRgba30);

        Assert.Throws<InvalidOperationException>(() => av.GetAvDrift(Guid.NewGuid(), Guid.NewGuid()));
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private sealed class DummyAudioChannel : IAudioChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public AudioFormat SourceFormat { get; } = new(48000, 2);
        public float Volume { get; set; } = 1f;
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }
        public int FillBuffer(Span<float> dest, int frameCount) { dest.Clear(); return frameCount; }
        public void Seek(TimeSpan position) { }
        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Dispose() { }
    }

    private sealed class DummyVideoChannel : IVideoChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; } = new(640, 360, PixelFormat.Rgba32, 30, 1);
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler? EndOfStream { add { } remove { } }
        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class PositionTrackingAudioChannel : IAudioChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public AudioFormat SourceFormat { get; } = new(48000, 2);
        public float Volume { get; set; } = 1f;
        public TimeSpan Position { get; }
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }

        public PositionTrackingAudioChannel(TimeSpan position) => Position = position;

        public int FillBuffer(Span<float> dest, int frameCount) { dest.Clear(); return frameCount; }
        public void Seek(TimeSpan position) { }
        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Dispose() { }
    }

    private sealed class PositionTrackingVideoChannel : IVideoChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; } = new(640, 360, PixelFormat.Rgba32, 30, 1);
        public TimeSpan Position { get; }
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler? EndOfStream { add { } remove { } }

        public PositionTrackingVideoChannel(TimeSpan position) => Position = position;

        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }
}

