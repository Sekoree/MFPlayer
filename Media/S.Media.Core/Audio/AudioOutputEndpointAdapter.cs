using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Bridges existing <see cref="IAudioOutput"/> to the unified <see cref="IAudioBufferEndpoint"/> contract
/// by injecting an internal channel into the output's mixer.
/// </summary>
public sealed class AudioOutputEndpointAdapter : IAudioBufferEndpoint
{
    private readonly IAudioOutput _output;
    private readonly AudioChannel _channel;
    private readonly AudioFormat _format;
    private readonly IAudioResampler _resampler;
    private readonly bool _ownsResampler;
    private bool _disposed;

    public string Name { get; }
    public bool IsRunning => _output.IsRunning;

    public AudioOutputEndpointAdapter(
        IAudioOutput output,
        string? name = null,
        IAudioResampler? resampler = null,
        int bufferDepth = 8)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _format = output.HardwareFormat;
        Name = name ?? "AudioOutputEndpoint";

        _channel = new AudioChannel(_format, bufferDepth: Math.Max(1, bufferDepth));
        _output.Mixer.AddChannel(_channel, ChannelRouteMap.Identity(_format.Channels));

        _resampler = resampler ?? new LinearResampler();
        _ownsResampler = resampler == null;
    }

    public Task StartAsync(CancellationToken ct = default) => _output.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _output.StopAsync(ct);

    public void WriteBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int srcSamples = Math.Min(buffer.Length, frameCount * Math.Max(1, format.Channels));
        if (srcSamples <= 0)
            return;

        if (format.SampleRate == _format.SampleRate && format.Channels == _format.Channels)
        {
            _channel.TryWrite(buffer[..srcSamples]);
            return;
        }

        int outFrames = format.SampleRate == _format.SampleRate
            ? frameCount
            : (int)Math.Round((double)frameCount * _format.SampleRate / format.SampleRate);
        if (outFrames <= 0)
            return;

        int outSamples = outFrames * _format.Channels;
        var tmp = new float[outSamples];

        _resampler.Resample(buffer[..srcSamples], tmp, format, _format.SampleRate);
        _channel.TryWrite(tmp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _output.Mixer.RemoveChannel(_channel.Id);
        _channel.Dispose();
        if (_ownsResampler)
            _resampler.Dispose();
    }
}

