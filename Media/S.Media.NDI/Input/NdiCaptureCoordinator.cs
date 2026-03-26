using System.Buffers;
using NdiLib;
using S.Media.Core.Video;

namespace S.Media.NDI.Input;


internal sealed class NdiCaptureCoordinator
{
    private const int MaxBufferedVideoFrames = 8;
    private const int MaxBufferedAudioBlocks = 16;

    private readonly Lock _gate = new();
    private readonly NdiReceiver _receiver;
    private readonly Queue<CapturedVideoFrame> _videoQueue = new();
    private readonly Queue<CapturedAudioBlock> _audioQueue = new();

    public NdiCaptureCoordinator(NdiReceiver receiver)
    {
        _receiver = receiver;
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

    private unsafe void CaptureOnce(uint timeoutMs)
    {
        try
        {
            using var capture = _receiver.CaptureScoped(timeoutMs);
            if (capture.FrameType == NdiFrameType.Video)
            {
                var width = Math.Max(1, capture.Video.Xres);
                var height = Math.Max(1, capture.Video.Yres);
                var stride = Math.Max(1, capture.Video.LineStrideInBytes);
                if (capture.Video.PData == nint.Zero)
                {
                    return;
                }

                var validLength = checked(width * height * 4);
                var rented = ArrayPool<byte>.Shared.Rent(validLength);
                if (!TryCopyPacked32(capture.Video.PData, stride, capture.Video.FourCC, width, height, rented, validLength, out var outputFormat, out var conversionPath))
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
                    while (_videoQueue.Count >= MaxBufferedVideoFrames)
                    {
                        var dropped = _videoQueue.Dequeue();
                        if (dropped.IsPooled)
                        {
                            ArrayPool<byte>.Shared.Return(dropped.Rgba);
                        }
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
                {
                    return;
                }

                var channelStrideBytes = capture.Audio.ChannelStrideInBytes > 0
                    ? capture.Audio.ChannelStrideInBytes
                    : noSamples * sizeof(float);

                var sampleCount = checked(noSamples * noChannels);
                var rented = ArrayPool<float>.Shared.Rent(sampleCount);
                var interleaved = rented.AsSpan(0, sampleCount);
                var basePtr = (byte*)capture.Audio.PData;
                for (var s = 0; s < noSamples; s++)
                {
                    for (var c = 0; c < noChannels; c++)
                    {
                        var channelPtr = (float*)(basePtr + (c * channelStrideBytes));
                        interleaved[(s * noChannels) + c] = channelPtr[s];
                    }
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
                    while (_audioQueue.Count >= MaxBufferedAudioBlocks)
                    {
                        var dropped = _audioQueue.Dequeue();
                        if (dropped.IsPooled)
                        {
                            ArrayPool<float>.Shared.Return(dropped.InterleavedSamples);
                        }
                    }

                    _audioQueue.Enqueue(mapped);
                }
            }
        }
        catch
        {
            // Capture is best-effort by contract in this phase.
        }
    }

    private static unsafe bool TryCopyPacked32(
        nint sourcePtr,
        int sourceStride,
        NdiFourCCVideoType sourceFormat,
        int width,
        int height,
        byte[] destination,
        int destinationLength,
        out VideoPixelFormat outputFormat,
        out string conversionPath)
    {
        switch (sourceFormat)
        {
            case NdiFourCCVideoType.Rgba:
                outputFormat = VideoPixelFormat.Rgba32;
                conversionPath = "passthrough-rgba";
                break;
            case NdiFourCCVideoType.Rgbx:
                outputFormat = VideoPixelFormat.Rgba32;
                conversionPath = "passthrough-rgbx";
                break;
            case NdiFourCCVideoType.Bgra:
                outputFormat = VideoPixelFormat.Bgra32;
                conversionPath = "passthrough-bgra";
                break;
            case NdiFourCCVideoType.Bgrx:
                outputFormat = VideoPixelFormat.Bgra32;
                conversionPath = "passthrough-bgrx";
                break;
            default:
                outputFormat = VideoPixelFormat.Unknown;
                conversionPath = "unsupported-source-format";
                return false;
        }

        var destinationStride = width * 4;
        var pixelsPerRow = Math.Min(width, Math.Max(0, sourceStride / 4));
        var copyBytesPerRow = pixelsPerRow * 4;
        if (destinationLength < destinationStride * height)
        {
            outputFormat = VideoPixelFormat.Unknown;
            conversionPath = "destination-too-small";
            return false;
        }

        if (copyBytesPerRow == destinationStride)
        {
            fixed (byte* destinationBase = destination)
            {
                Buffer.MemoryCopy((void*)sourcePtr, destinationBase, destinationLength, destinationStride * height);
            }

            return true;
        }

        fixed (byte* destinationBase = destination)
        {
            for (var y = 0; y < height; y++)
            {
                var sourceRow = (byte*)sourcePtr + (y * sourceStride);
                var destinationRow = destinationBase + (y * destinationStride);
                if (copyBytesPerRow < destinationStride)
                {
                    new Span<byte>(destinationRow, destinationStride).Clear();
                }

                if (copyBytesPerRow > 0)
                {
                    Buffer.MemoryCopy(sourceRow, destinationRow, destinationStride, copyBytesPerRow);
                }
            }
        }

        return true;
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

