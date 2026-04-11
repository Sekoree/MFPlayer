using S.Media.Core.Media;

namespace S.Media.Core.Video.Endpoints;

/// <summary>
/// Adapts an <see cref="IVideoFrameEndpoint"/> to <see cref="IVideoSink"/> so
/// endpoint-based routes can be attached directly to <see cref="IVideoMixer"/>.
/// </summary>
public sealed class VideoEndpointSinkAdapter : IVideoSink, IVideoSinkFormatCapabilities
{
    private readonly IVideoFrameEndpoint _endpoint;

    public string Name => _endpoint.Name;
    public bool IsRunning => _endpoint.IsRunning;
    public IReadOnlyList<PixelFormat> PreferredPixelFormats => _endpoint.SupportedPixelFormats;

    public VideoEndpointSinkAdapter(IVideoFrameEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public Task StartAsync(CancellationToken ct = default) => _endpoint.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _endpoint.StopAsync(ct);

    public void ReceiveFrame(in VideoFrame frame) => _endpoint.WriteFrame(frame);

    public void Dispose() => _endpoint.Dispose();
}

