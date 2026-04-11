using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Adapts an <see cref="IAudioBufferEndpoint"/> to <see cref="IAudioSink"/> so
/// endpoint-based routes can be registered directly with <see cref="IAudioMixer"/>.
/// Inverse of <see cref="AudioSinkEndpointAdapter"/>.
/// </summary>
public sealed class AudioEndpointSinkAdapter : IAudioSink
{
    private readonly IAudioBufferEndpoint _endpoint;

    public string Name => _endpoint.Name;
    public bool IsRunning => _endpoint.IsRunning;

    public AudioEndpointSinkAdapter(IAudioBufferEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public Task StartAsync(CancellationToken ct = default) => _endpoint.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _endpoint.StopAsync(ct);

    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
        => _endpoint.WriteBuffer(buffer, frameCount, sourceFormat);

    public void Dispose() => _endpoint.Dispose();
}

