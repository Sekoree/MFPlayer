using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Tests for <see cref="AggregateOutput"/>: sink fan-out, add/remove sinks at runtime,
/// and mixer delegation.
/// </summary>
public sealed class AggregateOutputTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────

    private static AudioFormat Mono48K => new(48000, 1);

    private sealed class FakeLeaderOutput : IAudioOutput
    {
        private readonly AudioFormat _fmt;
        private AudioMixer? _mixer;

        public FakeLeaderOutput(AudioFormat fmt)
        {
            _fmt  = fmt;
            Clock = new StopwatchClock(fmt.SampleRate);
        }

        // Mixer is set externally because AggregateOutput replaces it.
        public AudioFormat HardwareFormat => _fmt;
        public IAudioMixer Mixer          => _mixer ??= new AudioMixer(_fmt);
        public IMediaClock Clock          { get; }
        public bool        IsRunning      => false;

        public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer) { }
        public void OverrideRtMixer(IAudioMixer mixer) { } // no-op for tests
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void Dispose() => _mixer?.Dispose();
    }

    /// <summary>Captures all ReceiveBuffer calls for assertion.</summary>
    private sealed class SpySink : IAudioSink
    {
        public string Name       => "Spy";
        public bool   IsRunning  { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public readonly List<(int frameCount, AudioFormat format)> Calls = new();
        public float[]? LastBuffer;

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCalls++;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalls++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
        {
            Calls.Add((frameCount, sourceFormat));
            LastBuffer = buffer.ToArray();
        }

        public void Dispose() { }
    }

    private static AggregateOutput MakeAggregate()
    {
        var leader = new FakeLeaderOutput(Mono48K);
        _ = leader.Mixer;
        return new AggregateOutput(leader);
    }

    // ── AddSink / Sinks count ──────────────────────────────────────────────

    [Fact]
    public void AddSink_AppearsinSinks()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var sink = new SpySink();
            agg.AddSink(sink);

            Assert.Single(agg.Sinks);
            Assert.Same(sink, agg.Sinks[0]);
        }
    }

    [Fact]
    public void AddSink_Multiple_AllAppear()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var s1 = new SpySink();
            var s2 = new SpySink();
            agg.AddSink(s1);
            agg.AddSink(s2);

            Assert.Equal(2, agg.Sinks.Count);
        }
    }

    [Fact]
    public async Task AddSink_Duplicate_Ignored()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var sink = new SpySink();
            agg.AddSink(sink);
            agg.AddSink(sink);

            Assert.Single(agg.Sinks);

            await agg.StartAsync();
            await agg.StopAsync();

            Assert.Equal(1, sink.StartCalls);
            Assert.Equal(1, sink.StopCalls);
        }
    }

    // ── RemoveSink ────────────────────────────────────────────────────────

    [Fact]
    public void RemoveSink_RemovesSinkFromList()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var sink = new SpySink();
            agg.AddSink(sink);
            agg.RemoveSink(sink);

            Assert.Empty(agg.Sinks);
        }
    }

    [Fact]
    public void RemoveSink_NonExistent_DoesNotThrow()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            agg.RemoveSink(new SpySink()); // no-op
        }
    }

    [Fact]
    public void RemoveSink_RemovesCorrectSink_WhenMultiplePresent()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var s1 = new SpySink();
            var s2 = new SpySink();
            agg.AddSink(s1);
            agg.AddSink(s2);

            agg.RemoveSink(s1);

            Assert.Single(agg.Sinks);
            Assert.Same(s2, agg.Sinks[0]);
        }
    }

    // ── Fan-out via FillOutputBuffer ──────────────────────────────────────

    [Fact]
    public async Task FillOutputBuffer_DistributesToRunningSink()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var spy = new SpySink();
            agg.AddSink(spy);
            await spy.StartAsync();

            // Fill via the aggregate mixer
            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            Assert.Single(spy.Calls);
            Assert.Equal(4, spy.Calls[0].frameCount);
            Assert.Equal(Mono48K, spy.Calls[0].format);
        }
    }

    [Fact]
    public void FillOutputBuffer_DoesNotDistributeToStoppedSink()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var spy = new SpySink(); // IsRunning = false (not started)
            agg.AddSink(spy);

            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            Assert.Empty(spy.Calls);
        }
    }

    [Fact]
    public async Task FillOutputBuffer_DistributesToAllRunningSinks()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var s1 = new SpySink();
            var s2 = new SpySink();
            agg.AddSink(s1);
            agg.AddSink(s2);
            await s1.StartAsync();
            await s2.StartAsync();

            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            Assert.Single(s1.Calls);
            Assert.Single(s2.Calls);
        }
    }

    [Fact]
    public async Task FillOutputBuffer_SinkReceivesCorrectBuffer()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            // Add a constant-value channel so the mix buffer is non-zero.
            var ch = new ConstantSourceChannel(Mono48K, 0.75f);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var spy = new SpySink();
            agg.AddSink(spy);
            await spy.StartAsync();

            // With Silent default, the channel must be explicitly routed to the sink.
            agg.Mixer.RouteTo(ch.Id, spy, ChannelRouteMap.Identity(1));

            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            Assert.NotNull(spy.LastBuffer);
            Assert.All(spy.LastBuffer!, s => Assert.Equal(0.75f, s, precision: 5));
        }
    }

    // ── Sink removed at runtime ───────────────────────────────────────────

    [Fact]
    public async Task RemoveSink_AtRuntime_NoLongerReceivesBuffers()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var spy = new SpySink();
            agg.AddSink(spy);
            await spy.StartAsync();

            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K); // call 1

            agg.RemoveSink(spy);
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K); // call 2 — spy should not receive

            Assert.Single(spy.Calls); // only the first call
        }
    }

    // ── Mixer delegation ──────────────────────────────────────────────────

    [Fact]
    public void Mixer_ChannelCount_DelegatestoInner()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            Assert.Equal(0, agg.Mixer.ChannelCount);
            var ch = new ConstantSourceChannel(Mono48K);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));
            Assert.Equal(1, agg.Mixer.ChannelCount);
        }
    }

    [Fact]
    public void HardwareFormat_DelegatestoLeader()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            Assert.Equal(Mono48K, agg.HardwareFormat);
        }
    }

    // ── ChannelFallback.Silent (default) ─────────────────────────────────

    [Fact]
    public async Task Silent_SinkReceivesZeroBuffer_WhenNoRouteConfigured()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var ch = new ConstantSourceChannel(Mono48K);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var spy = new SpySink();
            agg.AddSink(spy);
            await spy.StartAsync();

            // No RouteTo call — spy gets a silent buffer.
            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            Assert.Single(spy.Calls); // still called
            Assert.All(spy.LastBuffer!, s => Assert.Equal(0f, s, precision: 5)); // but silent
        }
    }

    // ── RouteTo / UnrouteTo ───────────────────────────────────────────────

    [Fact]
    public async Task RouteTo_SinkReceivesChannelData()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var ch = new ConstantSourceChannel(Mono48K, 0.5f);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var spy = new SpySink();
            agg.AddSink(spy);
            agg.Mixer.RouteTo(ch.Id, spy, ChannelRouteMap.Identity(1));
            await spy.StartAsync();

            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            Assert.All(spy.LastBuffer!, s => Assert.Equal(0.5f, s, precision: 5));
        }
    }

    [Fact]
    public async Task UnrouteTo_SinkReceivesSilenceAfterRemoval()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var ch = new ConstantSourceChannel(Mono48K);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var spy = new SpySink();
            agg.AddSink(spy);
            agg.Mixer.RouteTo(ch.Id, spy, ChannelRouteMap.Identity(1));
            await spy.StartAsync();

            float[] dest = new float[4];

            // First fill — routed: spy should see 1.0 f samples.
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);
            Assert.All(spy.LastBuffer!, s => Assert.Equal(1.0f, s, precision: 5));

            // Remove route — spy should see zeros.
            agg.Mixer.UnrouteTo(ch.Id, spy);
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);
            Assert.All(spy.LastBuffer!, s => Assert.Equal(0f, s, precision: 5));
        }
    }

    [Fact]
    public async Task RouteTo_TwoSinks_IndependentMixes()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var ch = new ConstantSourceChannel(Mono48K, 0.8f);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var spy1 = new SpySink();
            var spy2 = new SpySink();
            agg.AddSink(spy1);
            agg.AddSink(spy2);

            // Route channel to spy1 only
            agg.Mixer.RouteTo(ch.Id, spy1, ChannelRouteMap.Identity(1));
            await spy1.StartAsync();
            await spy2.StartAsync();

            float[] dest = new float[4];
            agg.Mixer.FillOutputBuffer(dest, 4, Mono48K);

            // spy1 gets channel data; spy2 gets silence (no route, Silent fallback)
            Assert.All(spy1.LastBuffer!, s => Assert.Equal(0.8f, s, precision: 5));
            Assert.All(spy2.LastBuffer!, s => Assert.Equal(0f, s, precision: 5));
        }
    }

    // ── ChannelFallback.Broadcast ─────────────────────────────────────────

    [Fact]
    public async Task Broadcast_SinkReceivesLeaderMix_WithoutExplicitRoute()
    {
        var leader = new FakeLeaderOutput(Mono48K);
        var agg    = new AggregateOutput(leader); // default = Silent on inner mixer...

        // But we test AudioMixer directly here with Broadcast.
        using var mixer = new AudioMixer(Mono48K, ChannelFallback.Broadcast);
        using (agg)
        {
            var ch = new ConstantSourceChannel(Mono48K, 0.6f);
            mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var spy = new SpySink();
            mixer.RegisterSink(spy, 1);
            await spy.StartAsync();

            float[] dest = new float[4];
            mixer.FillOutputBuffer(dest, 4, Mono48K);

            // No explicit RouteTo needed — Broadcast copies leader route to sinks.
            Assert.All(spy.LastBuffer!, s => Assert.Equal(0.6f, s, precision: 5));
        }
    }

    // ── RouteTo on unregistered sink throws ───────────────────────────────

    [Fact]
    public void RouteTo_UnregisteredSink_Throws()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            var ch = new ConstantSourceChannel(Mono48K);
            agg.Mixer.AddChannel(ch, ChannelRouteMap.Identity(1));

            var unregisteredSink = new SpySink();
            Assert.Throws<InvalidOperationException>(
                () => agg.Mixer.RouteTo(ch.Id, unregisteredSink, ChannelRouteMap.Identity(1)));
        }
    }

    // ── DefaultFallback property ──────────────────────────────────────────

    [Fact]
    public void AudioMixer_DefaultFallback_IsSilentByDefault()
    {
        var agg = MakeAggregate();
        using (agg)
        {
            Assert.Equal(ChannelFallback.Silent, agg.Mixer.DefaultFallback);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed class ConstantSourceChannel : IAudioChannel
    {
        private readonly float _value;
        public ConstantSourceChannel(AudioFormat fmt, float value = 1f) { SourceFormat = fmt; _value = value; }
        public Guid        Id              { get; } = Guid.NewGuid();
        public AudioFormat SourceFormat    { get; }
        public bool        IsOpen         => true;
        public bool        CanSeek        => false;
        public float       Volume         { get; set; } = 1.0f;
        public int         BufferDepth    => 8;
        public int         BufferAvailable => int.MaxValue;
        public TimeSpan    Position        => TimeSpan.Zero;

        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
        {
            add { }
            remove { }
        }

        public int FillBuffer(Span<float> dest, int frameCount) { dest.Fill(_value); return frameCount; }
        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }
}

