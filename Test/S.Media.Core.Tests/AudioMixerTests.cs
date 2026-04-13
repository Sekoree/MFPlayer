using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Tests for <see cref="AudioMixer"/>: channel management, mix hot path,
/// volume, routing, peak metering, and <see cref="AudioMixer.PrepareBuffers"/>.
/// </summary>
public sealed class AudioMixerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static AudioFormat Stereo48k => new(48000, 2);
    private static AudioFormat Mono48k   => new(48000, 1);

    /// <summary>Minimal fake output — no hardware required.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private readonly AudioFormat _fmt;
        public FakeOutput(AudioFormat fmt)
        {
            _fmt   = fmt;
            Clock  = new StopwatchClock(fmt.SampleRate);
            Mixer  = new AudioMixer(_fmt);
        }

        public AudioFormat HardwareFormat => _fmt;
        public IAudioMixer Mixer          { get; }
        public IMediaClock Clock          { get; }
        public bool        IsRunning      => false;

        public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0) { }
        public void OverrideRtMixer(IAudioMixer mixer) { } // no-op for tests
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>Channel that always returns a constant value when pulled.</summary>
    private sealed class ConstantChannel : IAudioChannel
    {
        private readonly float _value;
        public ConstantChannel(AudioFormat fmt, float value = 1f) { SourceFormat = fmt; _value = value; }
        public Guid        Id           { get; } = Guid.NewGuid();
        public AudioFormat SourceFormat { get; }
        public bool        IsOpen       => true;
        public bool        CanSeek      => false;
        public float       Volume       { get; set; } = 1.0f;
        public int         BufferDepth  => 8;
        public int         BufferAvailable => int.MaxValue;
        public TimeSpan    Position     => TimeSpan.Zero;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
        {
            add { }
            remove { }
        }
        public event EventHandler? EndOfStream { add { } remove { } }

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            dest.Fill(_value);
            return frameCount;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private static (FakeOutput output, AudioMixer mixer) MakeMixer(AudioFormat fmt)
    {
        var output = new FakeOutput(fmt);
        return (output, (AudioMixer)output.Mixer);
    }

    private sealed class CapturingSink : IAudioSink
    {
        public string Name => nameof(CapturingSink);
        public bool IsRunning => true;
        public AudioFormat? LastSourceFormat { get; private set; }
        public int Calls { get; private set; }

        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
        {
            Calls++;
            LastSourceFormat = sourceFormat;
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    // ── AddChannel / FillOutputBuffer ────────────────────────────────────

    [Fact]
    public void FillOutputBuffer_NoChannels_OutputsAllZeros()
    {
        var (_, mixer) = MakeMixer(Stereo48k);
        using (mixer)
        {
            float[] dest = new float[8];
            mixer.FillOutputBuffer(dest, 4, Stereo48k);
            Assert.All(dest, s => Assert.Equal(0f, s));
        }
    }

    [Fact]
    public void FillOutputBuffer_SingleMonoChannel_RoutedToStereo()
    {
        var (_, mixer) = MakeMixer(Stereo48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Mono48k, 1f);
            // Route mono src[0] → dst[0] (left) only
            var route = new ChannelRouteMap.Builder().Route(0, 0).Build();
            mixer.AddChannel(ch, route);

            float[] dest = new float[8]; // 4 stereo frames
            mixer.FillOutputBuffer(dest, 4, Stereo48k);

            // Ch 0 (left) should be 1f, ch 1 (right) should be 0f
            for (int f = 0; f < 4; f++)
            {
                Assert.Equal(1f, dest[f * 2],     precision: 5);
                Assert.Equal(0f, dest[f * 2 + 1], precision: 5);
            }
        }
    }

    [Fact]
    public void FillOutputBuffer_TwoChannelsSameRate_SumsCorrectly()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            var ch1 = new ConstantChannel(Mono48k, 0.3f);
            var ch2 = new ConstantChannel(Mono48k, 0.4f);
            var route = ChannelRouteMap.Identity(1);
            mixer.AddChannel(ch1, route);
            mixer.AddChannel(ch2, route);

            float[] dest = new float[4]; // 4 mono frames
            mixer.FillOutputBuffer(dest, 4, Mono48k);

            Assert.All(dest, s => Assert.Equal(0.7f, s, precision: 5));
        }
    }

    // ── ChannelCount ──────────────────────────────────────────────────────

    [Fact]
    public void ChannelCount_ReflectsAddAndRemove()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            Assert.Equal(0, mixer.ChannelCount);
            var ch = new ConstantChannel(Mono48k);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));
            Assert.Equal(1, mixer.ChannelCount);
            mixer.RemoveChannel(ch.Id);
            Assert.Equal(0, mixer.ChannelCount);
        }
    }

    // ── RemoveChannel ─────────────────────────────────────────────────────

    [Fact]
    public void RemoveChannel_StopsChannelFromBeingPulled()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Mono48k, 1f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            mixer.RemoveChannel(ch.Id);

            float[] dest = new float[4];
            mixer.FillOutputBuffer(dest, 4, Mono48k);
            Assert.All(dest, s => Assert.Equal(0f, s));
        }
    }

    [Fact]
    public void RemoveChannel_NonExistent_DoesNotThrow()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            mixer.RemoveChannel(Guid.NewGuid()); // no-op
        }
    }

    // ── MasterVolume ──────────────────────────────────────────────────────

    [Fact]
    public void MasterVolume_ScalesOutput()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Mono48k, 1f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));
            mixer.MasterVolume = 0.5f;

            float[] dest = new float[4];
            mixer.FillOutputBuffer(dest, 4, Mono48k);

            Assert.All(dest, s => Assert.Equal(0.5f, s, precision: 5));
        }
    }

    [Fact]
    public void MasterVolume_ClampedToZeroMinimum()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            mixer.MasterVolume = -1f;
            Assert.Equal(0f, mixer.MasterVolume);
        }
    }

    // ── Channel volume ─────────────────────────────────────────────────────

    [Fact]
    public void ChannelVolume_ScalesChannelOutput()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Mono48k, 1f);
            ch.Volume = 0.25f;
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            float[] dest = new float[4];
            mixer.FillOutputBuffer(dest, 4, Mono48k);

            Assert.All(dest, s => Assert.Equal(0.25f, s, precision: 5));
        }
    }

    // ── Peak levels ──────────────────────────────────────────────────────

    [Fact]
    public void PeakLevels_UpdatedAfterFill()
    {
        var (_, mixer) = MakeMixer(Stereo48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Stereo48k, 0.6f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(2));

            float[] dest = new float[8];
            mixer.FillOutputBuffer(dest, 4, Stereo48k);

            Assert.Equal(2, mixer.PeakLevels.Count);
            Assert.Equal(0.6f, mixer.PeakLevels[0], precision: 5);
            Assert.Equal(0.6f, mixer.PeakLevels[1], precision: 5);
        }
    }

    [Fact]
    public void PeakLevels_ZeroWhenNoChannels()
    {
        var (_, mixer) = MakeMixer(Stereo48k);
        using (mixer)
        {
            float[] dest = new float[8];
            mixer.FillOutputBuffer(dest, 4, Stereo48k);

            // PeakLevels may be empty before first fill or have zeros after.
            if (mixer.PeakLevels.Count > 0)
                Assert.All(mixer.PeakLevels, p => Assert.Equal(0f, p));
        }
    }

    // ── Null resampler (same-rate) ────────────────────────────────────────

    [Fact]
    public void AddChannel_SameRate_NoResamplerAllocated_OutputCorrect()
    {
        // When source rate == output rate, the mixer should not create a LinearResampler
        // and must produce correct output via direct copy.
        var (_, mixer) = MakeMixer(Stereo48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Stereo48k, 0.8f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(2)); // no resampler passed

            float[] dest = new float[8];
            mixer.FillOutputBuffer(dest, 4, Stereo48k);
            Assert.All(dest, s => Assert.Equal(0.8f, s, precision: 5));
        }
    }

    // ── PrepareBuffers ────────────────────────────────────────────────────

    [Fact]
    public void PrepareBuffers_AllowsFillOutputBuffer_WithNoFallbackAlloc()
    {
        // PrepareBuffers pre-allocates everything; FillOutputBuffer should not throw
        // and must produce correct results.
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            var ch = new ConstantChannel(Mono48k, 1f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            mixer.PrepareBuffers(256);

            float[] dest = new float[256];
            mixer.FillOutputBuffer(dest, 256, Mono48k);

            Assert.All(dest, s => Assert.Equal(1f, s, precision: 5));
        }
    }

    [Fact]
    public void PrepareBuffers_AfterAddChannel_AllocatesSlotBuffers()
    {
        // AddChannel after PrepareBuffers should immediately pre-allocate.
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            mixer.PrepareBuffers(512);

            var ch = new ConstantChannel(Mono48k, 0.5f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            float[] dest = new float[512];
            mixer.FillOutputBuffer(dest, 512, Mono48k);
            Assert.All(dest, s => Assert.Equal(0.5f, s, precision: 5));
        }
    }

    [Fact]
    public void FillOutputBuffer_BufferLargerThanPrepared_IncrementsLeaderCapacityMisses()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            mixer.PrepareBuffers(64);
            float[] dest = new float[2048];

            mixer.FillOutputBuffer(dest, 2048, Mono48k);

            Assert.Equal(1, mixer.RtLeaderCapacityMisses);
            Assert.All(dest, s => Assert.Equal(0f, s));
        }
    }

    [Fact]
    public void FillOutputBuffer_RegisteredSink_ReceivesCachedLeaderFormat()
    {
        var (_, mixer) = MakeMixer(Stereo48k);
        using (mixer)
        {
            var sink = new CapturingSink();
            mixer.RegisterSink(sink, channels: 1);

            var ch = new ConstantChannel(Stereo48k, 0.4f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(2));
            mixer.PrepareBuffers(32);

            float[] dest = new float[64];
            mixer.FillOutputBuffer(dest, 32, Stereo48k);

            Assert.Equal(1, sink.Calls);
            Assert.True(sink.LastSourceFormat.HasValue);
            var sinkFormat = sink.LastSourceFormat.Value;
            Assert.Equal(Stereo48k.SampleRate, sinkFormat.SampleRate);
            Assert.Equal(1, sinkFormat.Channels);
        }
    }

    // ── Cross-rate resampling ─────────────────────────────────────────────

    [Fact]
    public void AddChannel_DifferentRate_AutoCreatesResampler_OutputNotSilent()
    {
        // Source at 44100 Hz, output at 48000 Hz — mixer auto-creates LinearResampler.
        var (_, mixer) = MakeMixer(Mono48k);
        using (mixer)
        {
            var ch = new ConstantChannel(new AudioFormat(44100, 1), 0.5f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            float[] dest = new float[256];
            mixer.FillOutputBuffer(dest, 256, Mono48k);

            // Just verify output is not all zeros (basic sanity check).
            Assert.Contains(dest, s => s != 0f);
        }
    }

    // ── Dispose guard ─────────────────────────────────────────────────────

    [Fact]
    public void AddChannel_AfterDispose_Throws()
    {
        var (_, mixer) = MakeMixer(Mono48k);
        mixer.Dispose();

        var ch = new ConstantChannel(Mono48k);
        Assert.Throws<ObjectDisposedException>(
            () => mixer.AddChannel(ch, ChannelRouteMap.Identity(1)));
    }
}

