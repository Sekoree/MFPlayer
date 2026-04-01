using System.Buffers;
using NDILib;
using S.Media.Core.Video;

namespace S.Media.NDI.Input;

/// <summary>
/// Manual-polling coordinator: calls <c>NDIlib_recv_capture_v3</c> and demuxes audio/video
/// into separate, bounded queues. Prefer <see cref="NDIFrameSyncCoordinator"/> for live-playback;
/// this implementation is retained for recording workflows and as a fallback.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> Concurrent calls to <see cref="TryReadVideo"/> and <see cref="TryReadAudio"/>
/// from separate threads are safe — a <see cref="SemaphoreSlim"/> serializes access to the native
/// capture call. However, because each <c>NDIlib_recv_capture_v3</c> invocation returns either an
/// audio <em>or</em> a video frame (not both), simultaneous polling from two threads means one
/// thread's capture call may "steal" a frame intended for the other media type. The stolen frame
/// is enqueued in its correct typed queue and will be returned on the next call, but the caller
/// that triggered the capture will see a timeout for its own media type. For best results when
/// using this coordinator for simultaneous audio+video, poll from a single thread or use
/// <see cref="NDIFrameSyncCoordinator"/> which handles this internally.
/// </remarks>
internal sealed class NDICaptureCoordinator : INDICaptureCoordinator
{
    // Semaphore ensures only one thread calls the native capture API at a time (Issue 5.5).
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private readonly Lock _gate = new();
    private readonly NDIReceiver _receiver;
    private readonly Queue<CapturedVideoFrame> _videoQueue = new();
    private readonly Queue<CapturedAudioBlock> _audioQueue = new();
    private readonly int _maxBufferedVideoFrames;
    private readonly int _maxBufferedAudioBlocks;

    public NDICaptureCoordinator(NDIReceiver receiver, int maxVideoFrames = 8, int maxAudioBlocks = 16)
    {
        _receiver = receiver;
        _maxBufferedVideoFrames = Math.Max(1, maxVideoFrames);
        _maxBufferedAudioBlocks = Math.Max(1, maxAudioBlocks);
    }

    public bool TryReadVideo(uint timeoutMs, out CapturedVideoFrame frame)
    {
        lock (_gate)
        {
            if (_videoQueue.Count > 0)
            {
                frame = _videoQueue.Dequeue();
                return true;
            }
        }

        CaptureOnce(timeoutMs);

        lock (_gate)
        {
            if (_videoQueue.Count > 0)
            {
                frame = _videoQueue.Dequeue();
                return true;
            }
        }

        frame = default;
        return false;
    }

    public bool TryReadAudio(uint timeoutMs, out CapturedAudioBlock frame)
    {
        lock (_gate)
        {
            if (_audioQueue.Count > 0)
            {
                frame = _audioQueue.Dequeue();
                return true;
            }
        }

        CaptureOnce(timeoutMs);

        lock (_gate)
        {
            if (_audioQueue.Count > 0)
            {
                frame = _audioQueue.Dequeue();
                return true;
            }
        }

        frame = default;
        return false;
    }

    public void Dispose() => _captureSemaphore.Dispose();

    private unsafe void CaptureOnce(uint timeoutMs)
    {
        // Non-blocking tryacquire (Issue 5.5): if another thread (e.g. the audio reader) is
        // already inside the native capture call, skip — do not queue a second concurrent call.
        if (!_captureSemaphore.Wait(0))
            return;

        try
        {
            using var capture = _receiver.CaptureScoped(timeoutMs);
            if (capture.FrameType == NdiFrameType.Video)
            {
                var width = Math.Max(1, capture.Video.Xres);
                var height = Math.Max(1, capture.Video.Yres);
                var stride = Math.Max(1, capture.Video.LineStrideInBytes);
                if (capture.Video.PData == nint.Zero)
                    return;

                var validLength = checked(width * height * 4);
                var rented = ArrayPool<byte>.Shared.Rent(validLength);
                if (!NDIVideoPixelConverter.TryCopyPacked32(capture.Video.PData, stride, capture.Video.FourCC, width, height, rented, validLength, out var outputFormat, out var conversionPath))
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    return;
                }

                var mapped = new CapturedVideoFrame(
                    Rgba: rented,
                    ValidLength: validLength,
                    Width: width,
                    Height: height,
                    Timestamp100Ns: capture.Video.Timestamp,
                    IncomingPixelFormat: capture.Video.FourCC.ToString(),
                    OutputPixelFormat: outputFormat,
                    ConversionPath: conversionPath,
                    IsPooled: true);

                lock (_gate)
                {
                    while (_videoQueue.Count >= _maxBufferedVideoFrames)
                    {
                        var dropped = _videoQueue.Dequeue();
                        if (dropped.IsPooled)
                            ArrayPool<byte>.Shared.Return(dropped.Rgba);
                    }
                    _videoQueue.Enqueue(mapped);
                }
                return;
            }

            if (capture.FrameType == NdiFrameType.Audio)
            {
                var noChannels = Math.Max(1, capture.Audio.NoChannels);
                var noSamples = Math.Max(0, capture.Audio.NoSamples);
                if (noSamples == 0 || capture.Audio.PData == nint.Zero)
                    return;

                var channelStrideBytes = capture.Audio.ChannelStrideInBytes > 0
                    ? capture.Audio.ChannelStrideInBytes
                    : noSamples * sizeof(float);

                var sampleCount = checked(noSamples * noChannels);
                var rented = ArrayPool<float>.Shared.Rent(sampleCount);
                var interleaved = rented.AsSpan(0, sampleCount);
                var basePtr = (byte*)capture.Audio.PData;
                for (var s = 0; s < noSamples; s++)
                    for (var c = 0; c < noChannels; c++)
                    {
                        var channelPtr = (float*)(basePtr + (c * channelStrideBytes));
                        interleaved[(s * noChannels) + c] = channelPtr[s];
                    }

                var mapped = new CapturedAudioBlock(
                    InterleavedSamples: rented,
                    SampleCount: sampleCount,
                    Frames: noSamples,
                    Channels: noChannels,
                    SampleRate: Math.Max(1, capture.Audio.SampleRate),
                    Timestamp100Ns: capture.Audio.Timestamp,
                    IsPooled: true);

                lock (_gate)
                {
                    while (_audioQueue.Count >= _maxBufferedAudioBlocks)
                    {
                        var dropped = _audioQueue.Dequeue();
                        if (dropped.IsPooled)
                            ArrayPool<float>.Shared.Return(dropped.InterleavedSamples);
                    }
                    _audioQueue.Enqueue(mapped);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            // Capture is best-effort — transient network / decode errors are non-fatal.
        }
        finally
        {
            _captureSemaphore.Release();
        }
    }
}

internal readonly record struct CapturedVideoFrame(
    byte[] Rgba,
    int ValidLength,
    int Width,
    int Height,
    long Timestamp100Ns,
    string IncomingPixelFormat,
    VideoPixelFormat OutputPixelFormat,
    string ConversionPath,
    bool IsPooled);

internal readonly record struct CapturedAudioBlock(
    float[] InterleavedSamples,
    int SampleCount,
    int Frames,
    int Channels,
    int SampleRate,
    long Timestamp100Ns,
    bool IsPooled);
