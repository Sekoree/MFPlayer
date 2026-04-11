using S.Media.Core.Audio;
using S.Media.Core.Audio.Endpoints;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AudioEndpointAdapterTests
{
    private sealed class SpyAudioSink : IAudioSink
    {
        public string Name => nameof(SpyAudioSink);
        public bool IsRunning { get; set; }
        public int Calls { get; private set; }
        public AudioFormat? LastFormat { get; private set; }
        public int LastFrameCount { get; private set; }

        public Task StartAsync(CancellationToken ct = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
        {
            Calls++;
            LastFormat = sourceFormat;
            LastFrameCount = frameCount;
        }

        public void Dispose() { }
    }

    private sealed class StubClock : S.Media.Core.Clock.IMediaClock
    {
        public TimeSpan Position => TimeSpan.Zero;
        public double SampleRate => 48000;
        public bool IsRunning => true;
        public event Action<TimeSpan>? Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Reset() { }
    }

    private sealed class StubAudioOutput : IAudioOutput
    {
        public AudioFormat HardwareFormat { get; }
        public IAudioMixer Mixer { get; }
        public S.Media.Core.Clock.IMediaClock Clock { get; } = new StubClock();
        public bool IsRunning => true;

        public StubAudioOutput(AudioFormat format)
        {
            HardwareFormat = format;
            Mixer = new AudioMixer(format, ChannelFallback.Silent);
        }

        public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0) { }
        public void OverrideRtMixer(IAudioMixer mixer) { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => Mixer.Dispose();
    }

    [Fact]
    public async Task AudioSinkEndpointAdapter_ForwardsBuffer()
    {
        var sink = new SpyAudioSink();
        using var adapter = new AudioSinkEndpointAdapter(sink);

        await adapter.StartAsync();
        adapter.WriteBuffer(new float[8], frameCount: 4, new AudioFormat(48000, 2));

        Assert.Equal(1, sink.Calls);
        Assert.Equal(4, sink.LastFrameCount);
        Assert.True(sink.LastFormat.HasValue);
        Assert.Equal(48000, sink.LastFormat.Value.SampleRate);
    }

    [Fact]
    public void AudioOutputEndpointAdapter_InsertsChannelIntoOutputMixer()
    {
        using var output = new StubAudioOutput(new AudioFormat(48000, 2));
        using var adapter = new AudioOutputEndpointAdapter(output, output.Mixer);

        adapter.WriteBuffer(new float[] { 0.5f, -0.5f, 0.25f, -0.25f }, frameCount: 2, new AudioFormat(48000, 2));

        var dest = new float[4];
        output.Mixer.FillOutputBuffer(dest, frameCount: 2, output.HardwareFormat);

        Assert.NotEqual(0f, dest[0]);
        Assert.NotEqual(0f, dest[1]);
    }
}

