using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.Core.Video.Endpoints;

/// <summary>
/// Bridges existing <see cref="IVideoOutput"/> to the unified <see cref="IVideoFrameEndpoint"/> contract
/// by injecting an internal channel into the output mixer.
/// </summary>
public sealed class VideoOutputEndpointAdapter : IVideoFrameEndpoint
{
    private sealed class EndpointVideoChannel : IVideoChannel
    {
        private readonly Queue<VideoFrame> _queue = new();
        private readonly Lock _gate = new();
        private readonly int _capacity;
        private long _positionTicks;

        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; }
        public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
        public int BufferDepth     => _capacity;
        public int BufferAvailable { get { lock (_gate) return _queue.Count; } }

#pragma warning disable CS0067  // event is never used — adapter has no upstream EOF source
        public event EventHandler? EndOfStream;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
#pragma warning restore CS0067

        public EndpointVideoChannel(VideoFormat sourceFormat, int capacity)
        {
            SourceFormat = sourceFormat;
            _capacity = Math.Max(1, capacity);
        }

        public void Enqueue(VideoFrame frame)
        {
            lock (_gate)
            {
                if (_queue.Count >= _capacity)
                {
                    var dropped = _queue.Dequeue();
                    dropped.MemoryOwner?.Dispose();
                }
                _queue.Enqueue(frame);
            }
        }

        public int FillBuffer(Span<VideoFrame> dest, int frameCount)
        {
            if (frameCount <= 0)
                return 0;

            VideoFrame frame;
            lock (_gate)
            {
                if (_queue.Count == 0)
                    return 0;
                frame = _queue.Dequeue();
            }

            dest[0] = frame;
            Volatile.Write(ref _positionTicks, frame.Pts.Ticks);
            return 1;
        }

        public void Seek(TimeSpan position) { }
        public void Dispose()
        {
            lock (_gate)
            {
                while (_queue.Count > 0)
                {
                    var frame = _queue.Dequeue();
                    frame.MemoryOwner?.Dispose();
                }
            }
        }
    }

    private readonly IVideoOutput _output;
    private readonly IVideoMixer _mixer;
    private readonly EndpointVideoChannel _channel;
    private readonly IPixelFormatConverter _converter;
    private readonly bool _ownsConverter;
    private bool _disposed;

    public string Name { get; }
    public bool IsRunning => _output.IsRunning;
    public IReadOnlyList<PixelFormat> SupportedPixelFormats { get; }

    /// <param name="output">The video output surface.</param>
    /// <param name="mixer">
    /// The mixer to inject the internal channel into — typically the video mixer
    /// managed by <see cref="Mixing.IAVMixer"/> and attached to <paramref name="output"/>.
    /// </param>
    public VideoOutputEndpointAdapter(
        IVideoOutput output,
        IVideoMixer mixer,
        string? name = null,
        IPixelFormatConverter? converter = null,
        int bufferDepth = 4)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _mixer  = mixer  ?? throw new ArgumentNullException(nameof(mixer));
        Name = name ?? "VideoOutputEndpoint";

        _converter = converter ?? new BasicPixelFormatConverter();
        _ownsConverter = converter == null;

        SupportedPixelFormats = [_output.OutputFormat.PixelFormat];
        _channel = new EndpointVideoChannel(_output.OutputFormat, bufferDepth);

        _mixer.AddChannel(_channel);
        _mixer.RouteChannelToPrimaryOutput(_channel.Id);
    }

    public Task StartAsync(CancellationToken ct = default) => _output.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _output.StopAsync(ct);

    public void WriteFrame(in VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dstFormat = _output.OutputFormat.PixelFormat;
        if (frame.PixelFormat == dstFormat)
        {
            _channel.Enqueue(frame);
            return;
        }

        var converted = _converter.Convert(frame, dstFormat);
        _channel.Enqueue(converted);

        // Channel now owns converted frame data; do not dispose converted owner here.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mixer.UnroutePrimaryOutput();
        _mixer.RemoveChannel(_channel.Id);
        _channel.Dispose();

        if (_ownsConverter)
            _converter.Dispose();
    }
}

