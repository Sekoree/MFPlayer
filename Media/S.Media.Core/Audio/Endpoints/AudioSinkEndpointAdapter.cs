using S.Media.Core.Media;

namespace S.Media.Core.Audio.Endpoints;

/// <summary>
/// Bridges existing <see cref="IAudioSink"/> to the unified <see cref="IAudioBufferEndpoint"/> contract.
/// </summary>
public sealed class AudioSinkEndpointAdapter : IAudioBufferEndpoint
{
    private readonly IAudioSink _sink;

    public string Name => _sink.Name;
    public bool IsRunning => _sink.IsRunning;

    public AudioSinkEndpointAdapter(IAudioSink sink)
        => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public Task StartAsync(CancellationToken ct = default) => _sink.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _sink.StopAsync(ct);

    public void WriteBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format)
        => _sink.ReceiveBuffer(buffer, frameCount, format);

    public void Dispose() => _sink.Dispose();
}


