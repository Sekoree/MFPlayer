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
    private QueuedAudioChunk? _pendingAudioChunk;

    private Thread? _workerThread;
    private bool _isOpen;
    private bool _running;
    private int _audioQueueCapacity;
    private int _videoQueueCapacity;
    private bool _disposed;

    // ── I.2: Dedicated decode thread state ──────────────────────────────────────
    private bool _useDedicatedDecodeThread;
    private int _packetQueueCapacity;
    private Thread? _demuxThread;
    private Thread? _decodeThread;
    private readonly AutoResetEvent _demuxSignal = new(false);
    private readonly AutoResetEvent _decodeSignal = new(false);
    private readonly Queue<DemuxedPacket> _audioPacketQueue = new();
    private readonly Queue<DemuxedPacket> _videoPacketQueue = new();

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
            _packetQueueCapacity = normalizedDecodeOptions.MaxQueuedPackets;
            _useDedicatedDecodeThread = normalizedDecodeOptions.UseDedicatedDecodeThread;

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

            // After native stream resolution, clear streams that don't actually exist
            {
                if (hasAudio && nativeAudioStream is null)
                {
                    hasAudio = false;
                    _context.ClearStream(audio: true);
                }

                if (hasVideo && nativeVideoStream is null)
                {
                    hasVideo = false;
                    _context.ClearStream(audio: false);
                }
            }

            if (hasAudio)
            {
                var audioInit = _audioDecoder.Initialize(normalizedDecodeOptions);
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
                var videoInit = _videoDecoder.Initialize(normalizedDecodeOptions);
                if (videoInit != MediaResult.Success)
                {
                    _context.Close();
                    return videoInit;
                }

                // N9: pass the caller's preferred output pixel format to the converter.
                var convertInit = _pixelConverter.Initialize(normalizedDecodeOptions.PreferredOutputPixelFormat);
                if (convertInit != MediaResult.Success)
                {
                    _context.Close();
                    return convertInit;
                }
            }

            _audioQueue.Clear();
            _videoQueue.Clear();
            _pendingAudioChunk = null;

            _isOpen = true;
            _running = true;

            // I.2: Choose between single-thread (legacy) and dual-thread (demux + decode) modes.
            if (_useDedicatedDecodeThread)
            {
                _audioPacketQueue.Clear();
                _videoPacketQueue.Clear();

                _demuxThread = new Thread(DemuxLoop)
                {
                    IsBackground = true,
                    Name = "S.Media.FFmpeg.SharedDemuxSession.Demux",
                };
                _decodeThread = new Thread(DecodeLoop)
                {
                    IsBackground = true,
                    Name = "S.Media.FFmpeg.SharedDemuxSession.Decode",
                };
                _demuxThread.Start();
                _decodeThread.Start();
            }
            else
            {
                _workerThread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "S.Media.FFmpeg.SharedDemuxSession",
                };
                _workerThread.Start();
            }

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
        Thread? demux;
        Thread? decode;

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
            _pendingAudioChunk = null;
            _audioPacketQueue.Clear();
            _videoPacketQueue.Clear();
            _workerSignal.Set();
            _demuxSignal.Set();
            _decodeSignal.Set();

            thread = _workerThread;
            _workerThread = null;
            demux = _demuxThread;
            _demuxThread = null;
            decode = _decodeThread;
            _decodeThread = null;
        }

        thread?.Join();
        demux?.Join();
        decode?.Join();
        return _context.Close();
    }

    public int ReadAudioSamples(Span<float> destination, int requestedFrameCount, int channelCount, out int framesRead, out TimeSpan chunkPresentationTime)
    {
        framesRead = 0;
        chunkPresentationTime = TimeSpan.Zero;

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

            if (_pendingAudioChunk.HasValue)
            {
                chunk = _pendingAudioChunk.Value;
                _pendingAudioChunk = null;
                hasChunk = true;
            }
            else if (_audioQueue.Count > 0)
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

        var writableFrames = destination.Length / channelCount;
        if (writableFrames <= 0)
        {
            return MediaResult.Success;
        }

        framesRead = Math.Min(requestedFrameCount, Math.Min(chunk.FrameCount, writableFrames));
        chunkPresentationTime = chunk.PresentationTime;
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

        var remainingFrames = chunk.FrameCount - framesRead;

        lock (_gate)
        {
            if (_disposed || !_isOpen || _context.AudioStream is null)
            {
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            if (remainingFrames > 0)
            {
                var sampleOffset = Math.Max(0, Math.Min(chunk.Samples.Length, sampleCount));
                var sampleRate = _context.AudioStream?.SampleRate.GetValueOrDefault(48_000) ?? 48_000;
                var consumedDuration = TimeSpan.FromSeconds((double)framesRead / Math.Max(1, sampleRate));
                _pendingAudioChunk = new QueuedAudioChunk(
                    chunk.Generation,
                    remainingFrames,
                    chunk.SampleValue,
                    chunk.Samples[sampleOffset..],
                    chunk.PresentationTime + consumedDuration);
            }

            _workerSignal.Set();
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

            // N6: flush stale B-frame / reference state from both codec contexts so the
            // first frames after the seek are clean.
            _audioDecoder.FlushCodecBuffers();
            _videoDecoder.FlushCodecBuffers();

            lock (_gate)
            {
                if (_disposed || !_isOpen)
                {
                    return (int)MediaErrorCode.FFmpegSeekFailed;
                }

                _audioQueue.Clear();
                _videoQueue.Clear();
                _pendingAudioChunk = null;
                // I.2: flush packet queues on seek
                _audioPacketQueue.Clear();
                _videoPacketQueue.Clear();

                publishDescriptors = _context.AudioStream is not null || _context.VideoStream is not null;
                _workerSignal.Set();
                _demuxSignal.Set();
                _decodeSignal.Set();
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
        _demuxSignal.Dispose();
        _decodeSignal.Dispose();
        _packetReader.Dispose();
        _audioDecoder.Dispose();
        _videoDecoder.Dispose();
        _resampler.Dispose();
        _pixelConverter.Dispose();
        _context.Dispose();
    }

    // ── Single-thread mode (UseDedicatedDecodeThread = false) ────────────────────

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
                // N7: 5 ms wait caused ~200 Hz busy-polling when both queues were full.
                // 20 ms (50 Hz) is ample for a 4-frame / 4-packet queue.
                _workerSignal.WaitOne(20);
                continue;
            }

            Thread.Yield();
        }
    }

    // ── Dual-thread mode (UseDedicatedDecodeThread = true) ──────────────────────

    /// <summary>
    /// I.2: Demux thread — reads packets from the container and enqueues them
    /// into bounded per-stream packet queues. Runs ahead of decode so the decoder
    /// always has packets ready.
    /// </summary>
    private void DemuxLoop()
    {
        while (true)
        {
            bool needAudio, needVideo;

            lock (_gate)
            {
                if (_disposed || !_running || !_isOpen)
                    return;

                needAudio = _context.AudioStream is not null && _audioPacketQueue.Count < _packetQueueCapacity;
                needVideo = _context.VideoStream is not null && _videoPacketQueue.Count < _packetQueueCapacity;
            }

            var produced = false;

            if (needAudio)
            {
                var code = _packetReader.ReadAudioPacket(out var packet);
                if (code == MediaResult.Success)
                {
                    lock (_gate)
                    {
                        if (!_disposed && _running && _isOpen && _audioPacketQueue.Count < _packetQueueCapacity)
                        {
                            _audioPacketQueue.Enqueue(new DemuxedPacket(packet, true));
                            produced = true;
                            _decodeSignal.Set();
                        }
                    }
                }
            }

            if (needVideo)
            {
                var code = _packetReader.ReadVideoPacket(out var packet);
                if (code == MediaResult.Success)
                {
                    lock (_gate)
                    {
                        if (!_disposed && _running && _isOpen && _videoPacketQueue.Count < _packetQueueCapacity)
                        {
                            _videoPacketQueue.Enqueue(new DemuxedPacket(packet, false));
                            produced = true;
                            _decodeSignal.Set();
                        }
                    }
                }
            }

            if (!produced)
            {
                _demuxSignal.WaitOne(20);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>
    /// I.2: Decode thread — drains the packet queues, decodes + converts, and
    /// enqueues finished audio chunks / video frames for consumer threads.
    /// </summary>
    private void DecodeLoop()
    {
        while (true)
        {
            bool wantAudio, wantVideo;
            FFPacket? audioPacket = null, videoPacket = null;

            lock (_gate)
            {
                if (_disposed || !_running || !_isOpen)
                    return;

                // Dequeue packets when the frame queues have room.
                wantAudio = _context.AudioStream is not null
                            && _audioQueue.Count < _audioQueueCapacity
                            && _audioPacketQueue.Count > 0;
                wantVideo = _context.VideoStream is not null
                            && _videoQueue.Count < _videoQueueCapacity
                            && _videoPacketQueue.Count > 0;

                if (wantAudio)
                    audioPacket = _audioPacketQueue.Dequeue().Packet;
                if (wantVideo)
                    videoPacket = _videoPacketQueue.Dequeue().Packet;
            }

            var produced = false;

            if (audioPacket is not null)
            {
                if (TryDecodeAudioPacket(audioPacket.Value, out var chunk))
                {
                    lock (_gate)
                    {
                        if (!_disposed && _running && _isOpen && _audioQueue.Count < _audioQueueCapacity)
                        {
                            _audioQueue.Enqueue(chunk);
                            produced = true;
                            _demuxSignal.Set(); // room freed in packet queue
                        }
                    }
                }
                else
                {
                    // Signal demux that we consumed the packet regardless.
                    _demuxSignal.Set();
                }
            }

            if (videoPacket is not null)
            {
                if (TryDecodeVideoPacket(videoPacket.Value, out var frame))
                {
                    lock (_gate)
                    {
                        if (!_disposed && _running && _isOpen && _videoQueue.Count < _videoQueueCapacity)
                        {
                            _videoQueue.Enqueue(frame);
                            produced = true;
                            _demuxSignal.Set();
                        }
                    }
                }
                else
                {
                    _demuxSignal.Set();
                }
            }

            if (!produced)
            {
                _decodeSignal.WaitOne(20);
            }
            else
            {
                _workerSignal.Set(); // wake consumers
                Thread.Yield();
            }
        }
    }

    /// <summary>I.2: Decodes a single audio packet into a <see cref="QueuedAudioChunk"/>.</summary>
    private bool TryDecodeAudioPacket(FFPacket packet, out QueuedAudioChunk chunk)
    {
        lock (_pipelineGate)
        {
            chunk = default;

            var decodeCode = _audioDecoder.Decode(packet, out var decoded);
            if (decodeCode != MediaResult.Success || decoded.FrameCount <= 0)
                return false;

            var resampleCode = _resampler.Resample(decoded, out var resampled);
            if (resampleCode != MediaResult.Success || resampled.FrameCount <= 0)
                return false;

            chunk = new QueuedAudioChunk(resampled.Generation, resampled.FrameCount, resampled.SampleValue, resampled.Samples, resampled.PresentationTime);
            return true;
        }
    }

    /// <summary>I.2: Decodes a single video packet into a <see cref="QueuedVideoFrame"/>.</summary>
    private bool TryDecodeVideoPacket(FFPacket packet, out QueuedVideoFrame frame)
    {
        lock (_pipelineGate)
        {
            frame = default;

            var decodeCode = _videoDecoder.Decode(packet, out var decoded);
            if (decodeCode == (int)MediaErrorCode.FFmpegVideoDecodeNeedMoreData)
                return false;
            if (decodeCode != MediaResult.Success)
                return false;

            var convertCode = _pixelConverter.Convert(decoded, out var converted);
            if (convertCode != MediaResult.Success)
                return false;

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

    /// <summary>I.2: Holds a demuxed packet and its stream type for the inter-thread packet queue.</summary>
    private readonly record struct DemuxedPacket(FFPacket Packet, bool IsAudio);

    private bool TryCreateQueuedAudioChunk(out QueuedAudioChunk chunk)
    {
        lock (_pipelineGate)
        {
            chunk = default;

            // Native audio decode may require several packets before yielding a frame.
            const int maxPacketAttempts = 12;
            for (var attempt = 0; attempt < maxPacketAttempts; attempt++)
            {
                var packetCode = _packetReader.ReadAudioPacket(out var packet);
                if (packetCode != MediaResult.Success)
                {
                    return false;
                }

                var decodeCode = _audioDecoder.Decode(packet, out var decoded);
                if (decodeCode != MediaResult.Success)
                {
                    // Individual packet decode failure — skip and try next packet.
                    continue;
                }

                if (decoded.FrameCount <= 0)
                {
                    continue;
                }

                var resampleCode = _resampler.Resample(decoded, out var resampled);
                if (resampleCode != MediaResult.Success)
                {
                    // Resample failure for this chunk — skip and try next packet.
                    continue;
                }

                if (resampled.FrameCount <= 0)
                {
                    continue;
                }

                chunk = new QueuedAudioChunk(resampled.Generation, resampled.FrameCount, resampled.SampleValue, resampled.Samples, resampled.PresentationTime);
                return true;
            }

            return false;
        }
    }

    private bool TryCreateQueuedVideoFrame(out QueuedVideoFrame frame)
    {
        lock (_pipelineGate)
        {
            frame = default;

            // Video codecs (H.264, H.265, etc.) typically need several packets before
            // producing the first frame. Loop feeding packets until a frame is produced
            // or an unrecoverable error occurs.
            const int maxPacketsBeforeFrame = 256;
            const int maxConsecutiveDecodeErrors = 3;
            var consecutiveErrors = 0;
            for (var attempt = 0; attempt < maxPacketsBeforeFrame; attempt++)
            {
                var packetCode = _packetReader.ReadVideoPacket(out var packet);
                if (packetCode != MediaResult.Success)
                {
                    return false;
                }

                var decodeCode = _videoDecoder.Decode(packet, out var decoded);
                if (decodeCode == (int)MediaErrorCode.FFmpegVideoDecodeNeedMoreData)
                {
                    // Decoder buffered the packet but needs more before it can output a frame.
                    consecutiveErrors = 0;
                    continue;
                }

                if (decodeCode != MediaResult.Success)
                {
                    // Individual packet decode failure (e.g. corrupt H.264 reference frames).
                    // Flush immediately after each failure to clear corrupt reference state
                    // and prevent native heap corruption from accumulating.
                    _videoDecoder.FlushCodecBuffers();
                    consecutiveErrors++;
                    if (consecutiveErrors >= maxConsecutiveDecodeErrors)
                    {
                        return false;
                    }
                    continue;
                }

                consecutiveErrors = 0;

                var convertCode = _pixelConverter.Convert(decoded, out var converted);
                if (convertCode != MediaResult.Success)
                {
                    // Pixel conversion failure for this frame — skip and try next packet.
                    continue;
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

            return false;
        }
    }

    private readonly record struct QueuedAudioChunk(long Generation, int FrameCount, float SampleValue, ReadOnlyMemory<float> Samples, TimeSpan PresentationTime);

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
