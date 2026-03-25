using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Runtime;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFSharedDemuxSession : IDisposable
{
    private readonly Lock _pipelineGate = new();
    private readonly FFSharedDecodeContext _context = new();
    private readonly FFPacketReader _packetReader = new();
    private readonly FFAudioDecoder _audioDecoder = new();
    private readonly FFVideoDecoder _videoDecoder = new();
    private readonly FFResampler _resampler = new();
    private readonly FFPixelConverter _pixelConverter = new();
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _workerSignal = new(false);
    private readonly Queue<QueuedAudioChunk> _audioQueue = new();
    private readonly Queue<QueuedVideoFrame> _videoQueue = new();

    private Thread? _workerThread;
    private bool _isOpen;
    private bool _running;
    private int _audioQueueCapacity;
    private int _videoQueueCapacity;
    private bool _disposed;

    internal event EventHandler<FFStreamDescriptorSnapshot>? StreamDescriptorsRefreshed;

    public FFStreamDescriptor? AudioStream => _context.AudioStream;

    public FFStreamDescriptor? VideoStream => _context.VideoStream;

    public FFmpegDecodeOptions ResolvedDecodeOptions => _context.ResolvedDecodeOptions;

    public int Open(FFmpegOpenOptions openOptions, FFmpegDecodeOptions decodeOptions)
    {
        ArgumentNullException.ThrowIfNull(openOptions);
        ArgumentNullException.ThrowIfNull(decodeOptions);

        if (decodeOptions.DecodeThreadCount < 0)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        var normalizedDecodeOptions = decodeOptions.Normalize();

        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegSharedContextDisposed;
        }

        var validationCode = FFmpegConfigValidator.Validate(openOptions, normalizedDecodeOptions);
        if (validationCode != MediaResult.Success)
        {
            return validationCode;
        }

        var publishDescriptors = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.FFmpegSharedContextDisposed;
            }

            if (_isOpen)
            {
                _workerSignal.Set();
                return MediaResult.Success;
            }

            var openCode = _context.Open(openOptions, normalizedDecodeOptions);
            if (openCode != MediaResult.Success)
            {
                return openCode;
            }

            _audioQueueCapacity = normalizedDecodeOptions.MaxQueuedPackets;
            _videoQueueCapacity = normalizedDecodeOptions.MaxQueuedFrames;

            var hasAudio = _context.AudioStream is not null;
            var hasVideo = _context.VideoStream is not null;
            var readerInit = _packetReader.Initialize(
                hasAudio,
                hasVideo,
                openOptions,
                _context.AudioStream?.StreamIndex,
                _context.VideoStream?.StreamIndex);
            if (readerInit != MediaResult.Success)
            {
                _context.Close();
                return readerInit;
            }

            if (_packetReader.TryGetNativeStreamDescriptors(out var nativeAudioStream, out var nativeVideoStream))
            {
                publishDescriptors = _context.ApplyResolvedStreamDescriptors(nativeAudioStream, nativeVideoStream);
            }

            if (hasAudio)
            {
                var audioInit = _audioDecoder.Initialize();
                if (audioInit != MediaResult.Success)
                {
                    _context.Close();
                    return audioInit;
                }

                var resampleInit = _resampler.Initialize();
                if (resampleInit != MediaResult.Success)
                {
                    _context.Close();
                    return resampleInit;
                }
            }

            if (hasVideo)
            {
                var videoInit = _videoDecoder.Initialize();
                if (videoInit != MediaResult.Success)
                {
                    _context.Close();
                    return videoInit;
                }

                var convertInit = _pixelConverter.Initialize();
                if (convertInit != MediaResult.Success)
                {
                    _context.Close();
                    return convertInit;
                }
            }

            _audioQueue.Clear();
            _videoQueue.Clear();

            _isOpen = true;
            _running = true;
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "S.Media.FFmpeg.SharedDemuxSession",
            };
            _workerThread.Start();

            publishDescriptors = publishDescriptors || _context.AudioStream is not null || _context.VideoStream is not null;
        }

        if (publishDescriptors)
        {
            PublishStreamDescriptors();
        }

        return MediaResult.Success;
    }

    public int Close()
    {
        Thread? thread;

        lock (_gate)
        {
            if (_disposed || !_isOpen)
            {
                return _context.Close();
            }

            _running = false;
            _isOpen = false;
            _audioQueue.Clear();
            _videoQueue.Clear();
            _workerSignal.Set();

            thread = _workerThread;
            _workerThread = null;
        }

        thread?.Join();
        return _context.Close();
    }

    public int ReadAudioSamples(Span<float> destination, int requestedFrameCount, int channelCount, out int framesRead)
    {
        framesRead = 0;

        if (requestedFrameCount <= 0)
        {
            return MediaResult.Success;
        }

        if (channelCount <= 0)
        {
            channelCount = 2;
        }

        QueuedAudioChunk chunk;
        var hasChunk = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.FFmpegSharedContextDisposed;
            }

            if (!_isOpen || _context.AudioStream is null)
            {
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            if (_audioQueue.Count > 0)
            {
                chunk = _audioQueue.Dequeue();
                hasChunk = true;
            }
            else
            {
                chunk = default;
            }
        }

        if (!hasChunk && !TryCreateQueuedAudioChunk(out chunk))
        {
            return (int)MediaErrorCode.FFmpegReadFailed;
        }

        lock (_gate)
        {
            if (_disposed || !_isOpen || _context.AudioStream is null)
            {
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            _workerSignal.Set();
        }

        var writableFrames = destination.Length / channelCount;
        if (writableFrames <= 0)
        {
            return MediaResult.Success;
        }

        framesRead = Math.Min(requestedFrameCount, Math.Min(chunk.FrameCount, writableFrames));
        var sampleCount = framesRead * channelCount;
        var sourceSamples = chunk.Samples.Span;
        var copyCount = Math.Min(sampleCount, sourceSamples.Length);
        if (copyCount > 0)
        {
            sourceSamples[..copyCount].CopyTo(destination[..copyCount]);
        }

        if (copyCount < sampleCount)
        {
            destination[copyCount..sampleCount].Fill(chunk.SampleValue);
        }

        return MediaResult.Success;
    }

    public int ReadVideoFrame(out FFSessionVideoFrame frame)
    {
        QueuedVideoFrame queuedFrame;
        var hasFrame = false;

        lock (_gate)
        {
            if (_disposed)
            {
                frame = default;
                return (int)MediaErrorCode.FFmpegSharedContextDisposed;
            }

            if (!_isOpen || _context.VideoStream is null)
            {
                frame = default;
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            if (_videoQueue.Count > 0)
            {
                queuedFrame = _videoQueue.Dequeue();
                hasFrame = true;
            }
            else
            {
                queuedFrame = default;
            }
        }

        if (!hasFrame && !TryCreateQueuedVideoFrame(out queuedFrame))
        {
            frame = default;
            return (int)MediaErrorCode.FFmpegReadFailed;
        }

        lock (_gate)
        {
            if (_disposed || !_isOpen || _context.VideoStream is null)
            {
                frame = default;
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            _workerSignal.Set();
        }

        frame = new FFSessionVideoFrame(
            queuedFrame.FrameIndex,
            queuedFrame.PresentationTime,
            queuedFrame.IsKeyFrame,
            queuedFrame.Width,
            queuedFrame.Height,
            queuedFrame.Plane0,
            queuedFrame.Plane0Stride,
            queuedFrame.Plane1,
            queuedFrame.Plane1Stride,
            queuedFrame.Plane2,
            queuedFrame.Plane2Stride,
            queuedFrame.MappedPixelFormat,
            queuedFrame.NativeTimeBaseNumerator,
            queuedFrame.NativeTimeBaseDenominator,
            queuedFrame.NativeFrameRateNumerator,
            queuedFrame.NativeFrameRateDenominator,
            queuedFrame.NativePixelFormat);
        return MediaResult.Success;
    }

    public int Seek(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var publishDescriptors = false;

        lock (_pipelineGate)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return (int)MediaErrorCode.FFmpegSharedContextDisposed;
                }

                if (!_isOpen)
                {
                    return (int)MediaErrorCode.FFmpegSeekFailed;
                }
            }

            var seekCode = _packetReader.Seek(positionSeconds);
            if (seekCode != MediaResult.Success)
            {
                return seekCode;
            }

            lock (_gate)
            {
                if (_disposed || !_isOpen)
                {
                    return (int)MediaErrorCode.FFmpegSeekFailed;
                }

                _audioQueue.Clear();
                _videoQueue.Clear();

                publishDescriptors = _context.AudioStream is not null || _context.VideoStream is not null;
                _workerSignal.Set();
            }
        }

        if (publishDescriptors)
        {
            PublishStreamDescriptors();
        }

        return MediaResult.Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _workerSignal.Dispose();
        _packetReader.Dispose();
        _audioDecoder.Dispose();
        _videoDecoder.Dispose();
        _resampler.Dispose();
        _pixelConverter.Dispose();
        _context.Dispose();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            var requestAudio = false;
            var requestVideo = false;

            lock (_gate)
            {
                if (_disposed || !_running || !_isOpen)
                {
                    return;
                }

                requestAudio = _context.AudioStream is not null && _audioQueue.Count < _audioQueueCapacity;
                requestVideo = _context.VideoStream is not null && _videoQueue.Count < _videoQueueCapacity;
            }

            var produced = false;

            if (requestAudio && TryCreateQueuedAudioChunk(out var audioChunk))
            {
                lock (_gate)
                {
                    if (!_disposed && _running && _isOpen && _audioQueue.Count < _audioQueueCapacity)
                    {
                        _audioQueue.Enqueue(audioChunk);
                        produced = true;
                    }
                }
            }

            if (requestVideo && TryCreateQueuedVideoFrame(out var videoFrame))
            {
                lock (_gate)
                {
                    if (!_disposed && _running && _isOpen && _videoQueue.Count < _videoQueueCapacity)
                    {
                        _videoQueue.Enqueue(videoFrame);
                        produced = true;
                    }
                }
            }

            if (!produced)
            {
                _workerSignal.WaitOne(5);
                continue;
            }

            Thread.Yield();
        }
    }

    private bool TryCreateQueuedAudioChunk(out QueuedAudioChunk chunk)
    {
        lock (_pipelineGate)
        {
            chunk = default;

            var packetCode = _packetReader.ReadAudioPacket(out var packet);
            if (packetCode != MediaResult.Success)
            {
                return false;
            }

            var decodeCode = _audioDecoder.Decode(packet, out var decoded);
            if (decodeCode != MediaResult.Success)
            {
                return false;
            }

            var resampleCode = _resampler.Resample(decoded, out var resampled);
            if (resampleCode != MediaResult.Success)
            {
                return false;
            }

            chunk = new QueuedAudioChunk(resampled.Generation, resampled.FrameCount, resampled.SampleValue, resampled.Samples);
            return true;
        }
    }

    private bool TryCreateQueuedVideoFrame(out QueuedVideoFrame frame)
    {
        lock (_pipelineGate)
        {
            frame = default;

            var packetCode = _packetReader.ReadVideoPacket(out var packet);
            if (packetCode != MediaResult.Success)
            {
                return false;
            }

            var decodeCode = _videoDecoder.Decode(packet, out var decoded);
            if (decodeCode != MediaResult.Success)
            {
                return false;
            }

            var convertCode = _pixelConverter.Convert(decoded, out var converted);
            if (convertCode != MediaResult.Success)
            {
                return false;
            }

            frame = new QueuedVideoFrame(
                converted.Generation,
                converted.FrameIndex,
                converted.PresentationTime,
                converted.IsKeyFrame,
                converted.Width,
                converted.Height,
                converted.Plane0,
                converted.Plane0Stride,
                converted.Plane1,
                converted.Plane1Stride,
                converted.Plane2,
                converted.Plane2Stride,
                converted.MappedPixelFormat,
                converted.NativeTimeBaseNumerator,
                converted.NativeTimeBaseDenominator,
                converted.NativeFrameRateNumerator,
                converted.NativeFrameRateDenominator,
                converted.NativePixelFormat);
            return true;
        }
    }

    private readonly record struct QueuedAudioChunk(long Generation, int FrameCount, float SampleValue, ReadOnlyMemory<float> Samples);

    private readonly record struct QueuedVideoFrame(
        long Generation,
        long FrameIndex,
        TimeSpan PresentationTime,
        bool IsKeyFrame,
        int Width,
        int Height,
        ReadOnlyMemory<byte> Plane0,
        int Plane0Stride,
        ReadOnlyMemory<byte> Plane1,
        int Plane1Stride,
        ReadOnlyMemory<byte> Plane2,
        int Plane2Stride,
        VideoPixelFormat MappedPixelFormat,
        int? NativeTimeBaseNumerator,
        int? NativeTimeBaseDenominator,
        int? NativeFrameRateNumerator,
        int? NativeFrameRateDenominator,
        int? NativePixelFormat);

    private void PublishStreamDescriptors()
    {
        FFStreamDescriptor? audio;
        FFStreamDescriptor? video;

        lock (_gate)
        {
            if (_disposed || !_isOpen)
            {
                return;
            }

            audio = _context.AudioStream;
            video = _context.VideoStream;
        }

        StreamDescriptorsRefreshed?.Invoke(this, new FFStreamDescriptorSnapshot(audio, video));
    }
}

internal readonly record struct FFStreamDescriptorSnapshot(FFStreamDescriptor? AudioStream, FFStreamDescriptor? VideoStream);

internal readonly struct FFSessionVideoFrame
{
    public FFSessionVideoFrame(
        long frameIndex,
        TimeSpan presentationTime,
        bool isKeyFrame,
        int width,
        int height,
        ReadOnlyMemory<byte> plane0,
        int plane0Stride,
        ReadOnlyMemory<byte> plane1,
        int plane1Stride,
        ReadOnlyMemory<byte> plane2,
        int plane2Stride,
        VideoPixelFormat pixelFormat,
        int? nativeTimeBaseNumerator,
        int? nativeTimeBaseDenominator,
        int? nativeFrameRateNumerator,
        int? nativeFrameRateDenominator,
        int? nativePixelFormat)
    {
        FrameIndex = frameIndex;
        PresentationTime = presentationTime;
        IsKeyFrame = isKeyFrame;
        Width = width;
        Height = height;
        Plane0 = plane0;
        Plane0Stride = plane0Stride;
        Plane1 = plane1;
        Plane1Stride = plane1Stride;
        Plane2 = plane2;
        Plane2Stride = plane2Stride;
        PixelFormat = pixelFormat;
        NativeTimeBaseNumerator = nativeTimeBaseNumerator;
        NativeTimeBaseDenominator = nativeTimeBaseDenominator;
        NativeFrameRateNumerator = nativeFrameRateNumerator;
        NativeFrameRateDenominator = nativeFrameRateDenominator;
        NativePixelFormat = nativePixelFormat;
    }

    public long FrameIndex { get; }

    public TimeSpan PresentationTime { get; }

    public bool IsKeyFrame { get; }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Plane0 { get; }

    public int Plane0Stride { get; }

    public ReadOnlyMemory<byte> Plane1 { get; }

    public int Plane1Stride { get; }

    public ReadOnlyMemory<byte> Plane2 { get; }

    public int Plane2Stride { get; }

    public VideoPixelFormat PixelFormat { get; }

    public int? NativeTimeBaseNumerator { get; }

    public int? NativeTimeBaseDenominator { get; }

    public int? NativeFrameRateNumerator { get; }

    public int? NativeFrameRateDenominator { get; }

    public int? NativePixelFormat { get; }

    public bool HasNativeTimingMetadata =>
        NativeTimeBaseNumerator.HasValue || NativeTimeBaseDenominator.HasValue ||
        NativeFrameRateNumerator.HasValue || NativeFrameRateDenominator.HasValue;

    public bool HasNativePixelMetadata => NativePixelFormat.HasValue;

    public double? TryGetNativeFrameRate()
    {
        if (!NativeFrameRateNumerator.HasValue || !NativeFrameRateDenominator.HasValue || NativeFrameRateDenominator.Value <= 0)
        {
            return null;
        }

        return NativeFrameRateNumerator.Value / (double)NativeFrameRateDenominator.Value;
    }
}

