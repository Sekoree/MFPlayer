using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Bridges existing <see cref="IVideoSink"/> to the new <see cref="IVideoFrameEndpoint"/> contract.
/// Conversion is performed at the endpoint boundary when needed.
/// </summary>
public sealed class VideoSinkEndpointAdapter : IVideoFrameEndpoint, IVideoSinkFormatCapabilities
{
    private readonly IVideoSink _sink;
    private readonly IPixelFormatConverter _converter;
    private readonly IReadOnlyList<PixelFormat> _supported;
    private readonly bool _ownsConverter;
    private bool _disposed;
    private long _passthroughFrames;
    private long _convertedFrames;
    private long _droppedFrames;

    public string Name => _sink.Name;
    public bool IsRunning => _sink.IsRunning;
    public IReadOnlyList<PixelFormat> SupportedPixelFormats => _supported;
    public IReadOnlyList<PixelFormat> PreferredPixelFormats => _supported;
    public bool PreferRawFramePassthrough => true;

    public VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        PassthroughFrames: Interlocked.Read(ref _passthroughFrames),
        ConvertedFrames: Interlocked.Read(ref _convertedFrames),
        DroppedFrames: Interlocked.Read(ref _droppedFrames),
        QueueDepth: 0,
        QueueDrops: 0);

    public VideoSinkEndpointAdapter(
        IVideoSink sink,
        IReadOnlyList<PixelFormat>? supportedPixelFormats = null,
        IPixelFormatConverter? converter = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _supported = supportedPixelFormats is { Count: > 0 }
            ? supportedPixelFormats
            : [PixelFormat.Rgba32];

        _converter = converter ?? new BasicPixelFormatConverter();
        _ownsConverter = converter == null;
    }

    public Task StartAsync(CancellationToken ct = default) => _sink.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _sink.StopAsync(ct);

    public void WriteFrame(in VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_sink.IsRunning)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        var dstFormat = ResolveFormat(frame.PixelFormat);
        if (dstFormat == frame.PixelFormat)
        {
            Interlocked.Increment(ref _passthroughFrames);
            _sink.ReceiveFrame(frame);
            return;
        }

        var converted = _converter.Convert(frame, dstFormat);
        Interlocked.Increment(ref _convertedFrames);
        try
        {
            _sink.ReceiveFrame(converted);
        }
        finally
        {
            if (!ReferenceEquals(frame.MemoryOwner, converted.MemoryOwner))
                converted.MemoryOwner?.Dispose();
        }
    }

    private PixelFormat ResolveFormat(PixelFormat source)
    {
        foreach (var format in _supported)
            if (format == source)
                return format;

        return _supported[0];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsConverter)
            _converter.Dispose();
    }
}

