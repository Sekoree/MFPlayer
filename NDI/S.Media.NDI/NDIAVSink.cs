using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.NDI;

/// <summary>
/// Consolidated NDI sink that can accept both audio and video and send them through one sender
/// with a shared A/V timing context.
/// </summary>
public sealed class NDIAVSink : IAudioSink, IVideoSink, IVideoSinkFormatCapabilities
{
    private readonly struct PendingVideo
    {
        public readonly byte[] Buffer;
        public readonly int Width;
        public readonly int Height;
        public readonly long PtsTicks;
        public readonly PixelFormat PixelFormat;
        public readonly int Bytes;

        public PendingVideo(byte[] buffer, int width, int height, long ptsTicks, PixelFormat pixelFormat, int bytes)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
            PtsTicks = ptsTicks;
            PixelFormat = pixelFormat;
            Bytes = bytes;
        }
    }

    private readonly struct PendingAudio
    {
        public readonly float[] Buffer;
        public readonly int Samples;
        public readonly long TimecodeTicks;

        public PendingAudio(float[] buffer, int samples, long timecodeTicks)
        {
            Buffer = buffer;
            Samples = samples;
            TimecodeTicks = timecodeTicks;
        }
    }

    private static readonly IReadOnlyList<PixelFormat> sBgraPrefs = [PixelFormat.Bgra32, PixelFormat.Rgba32];
    private static readonly IReadOnlyList<PixelFormat> sRgbaPrefs = [PixelFormat.Rgba32, PixelFormat.Bgra32];
    private static readonly IReadOnlyList<PixelFormat> sNv12Prefs = [PixelFormat.Nv12];
    private static readonly IReadOnlyList<PixelFormat> sUyvyPrefs = [PixelFormat.Uyvy422];
    private static readonly IReadOnlyList<PixelFormat> sI420Prefs = [PixelFormat.Yuv420p];

    private readonly NDISender _sender;
    private readonly NDIAvTimingContext _timing = new();
    private readonly Lock _sendLock = new();

    // Video path
    private readonly bool _hasVideo;
    private readonly VideoFormat _videoTargetFormat;
    private readonly ConcurrentQueue<byte[]> _videoPool = new();
    private readonly ConcurrentQueue<PendingVideo> _videoPending = new();
    private readonly SemaphoreSlim _videoPendingSignal = new(0);
    private readonly int _videoMaxPendingFrames;
    private int _videoPendingFrames;
    private readonly BasicPixelFormatConverter _videoConverter = new();
    private static readonly Lock sFfmpegLoadLock = new();
    private static int sFfmpegLoadState;
    private long _videoPoolMissDrops;
    private long _videoCapacityMissDrops;
    private long _videoFormatDrops;
    private long _videoQueueDrops;
    private long _videoPassthroughFrames;
    private long _videoConvertedFrames;
    private long _videoConversionDrops;

    // Audio path
    private readonly bool _hasAudio;
    private readonly AudioFormat _audioTargetFormat;
    private readonly int _audioFramesPerBuffer;
    private readonly int _audioMaxPendingBuffers;
    private readonly IAudioResampler? _audioResampler;
    private readonly bool _ownsAudioResampler;
    private readonly ConcurrentQueue<float[]> _audioPool = new();
    private readonly ConcurrentQueue<PendingAudio> _audioPending = new();
    private readonly SemaphoreSlim _audioPendingSignal = new(0);
    private int _audioPendingBuffers;
    private long _audioPoolMissDrops;
    private long _audioCapacityMissDrops;
    private long _audioQueueDrops;

    private Thread? _videoThread;
    private Thread? _audioThread;
    private CancellationTokenSource? _cts;
    private int _started;
    private bool _disposed;

    public string Name { get; }
    public bool IsRunning => Volatile.Read(ref _started) == 1;
    public bool HasAudio => _hasAudio;
    public bool HasVideo => _hasVideo;

    public IReadOnlyList<PixelFormat> PreferredPixelFormats => _videoTargetFormat.PixelFormat switch
    {
        PixelFormat.Bgra32 => sBgraPrefs,
        PixelFormat.Rgba32 => sRgbaPrefs,
        PixelFormat.Nv12 => sNv12Prefs,
        PixelFormat.Uyvy422 => sUyvyPrefs,
        PixelFormat.Yuv420p => sI420Prefs,
        _ => sRgbaPrefs,
    };

    public NDIAVSink(
        NDISender sender,
        VideoFormat? videoTargetFormat = null,
        AudioFormat? audioTargetFormat = null,
        NDIEndpointPreset preset = NDIEndpointPreset.Balanced,
        string? name = null,
        bool preferPerformanceOverQuality = false,
        int videoPoolCount = 0,
        int videoMaxPendingFrames = 0,
        int audioFramesPerBuffer = 1024,
        int audioPoolCount = 0,
        int audioMaxPendingBuffers = 0,
        IAudioResampler? audioResampler = null)
    {
        _sender = sender;
        Name = name ?? "NDIAVSink";

        _hasVideo = videoTargetFormat.HasValue;
        if (videoTargetFormat is { } v)
        {
            var fallbackPixelFormat = preferPerformanceOverQuality ? PixelFormat.Uyvy422 : PixelFormat.Rgba32;
            var px = v.PixelFormat is PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Nv12 or PixelFormat.Uyvy422 or PixelFormat.Yuv420p
                ? v.PixelFormat
                : fallbackPixelFormat;
            _videoTargetFormat = v with { PixelFormat = px };

            var videoPreset = NDIVideoPresetOptions.For(preset);
            if (videoPoolCount <= 0) videoPoolCount = videoPreset.PoolCount;
            if (videoMaxPendingFrames <= 0) videoMaxPendingFrames = videoPreset.MaxPendingFrames;
            _videoMaxPendingFrames = videoMaxPendingFrames;

            int w = _videoTargetFormat.Width > 0 ? _videoTargetFormat.Width : 1280;
            int h = _videoTargetFormat.Height > 0 ? _videoTargetFormat.Height : 720;
            int bytes = w * h * 4;
            for (int i = 0; i < Math.Max(1, videoPoolCount); i++)
                _videoPool.Enqueue(new byte[bytes]);
        }

        _hasAudio = audioTargetFormat.HasValue;
        if (audioTargetFormat is { } atf)
        {
            _audioTargetFormat = atf;
            var audioPreset = NDIAudioPresetOptions.For(preset);
            if (audioFramesPerBuffer <= 0) audioFramesPerBuffer = 512;
            if (audioPoolCount <= 0) audioPoolCount = audioPreset.PoolCount;
            if (audioMaxPendingBuffers <= 0) audioMaxPendingBuffers = audioPreset.MaxPendingBuffers;

            _audioFramesPerBuffer = audioFramesPerBuffer;
            _audioMaxPendingBuffers = audioMaxPendingBuffers;

            if (audioResampler == null)
            {
                _audioResampler = new LinearResampler();
                _ownsAudioResampler = true;
            }
            else
            {
                _audioResampler = audioResampler;
                _ownsAudioResampler = false;
            }

            int headroom = Math.Max(1, audioPreset.BufferHeadroomMultiplier);
            for (int i = 0; i < audioPoolCount; i++)
                _audioPool.Enqueue(new float[_audioFramesPerBuffer * _audioTargetFormat.Channels * headroom]);
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_hasVideo)
        {
            _videoThread = new Thread(VideoWriteLoop)
            {
                Name = $"{Name}.VideoThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _videoThread.Start();
        }

        if (_hasAudio)
        {
            _audioThread = new Thread(AudioWriteLoop)
            {
                Name = $"{Name}.AudioThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _audioThread.Start();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;

        _cts?.Cancel();

        await Task.Run(() => _videoThread?.Join(TimeSpan.FromSeconds(3)), ct).ConfigureAwait(false);
        await Task.Run(() => _audioThread?.Join(TimeSpan.FromSeconds(3)), ct).ConfigureAwait(false);
    }

    public void ReceiveFrame(in VideoFrame frame)
    {
        if (!_hasVideo || Volatile.Read(ref _started) == 0)
            return;

        if (Volatile.Read(ref _videoPendingFrames) >= _videoMaxPendingFrames)
        {
            Interlocked.Increment(ref _videoQueueDrops);
            return;
        }

        int bytes = frame.Data.Length;
        if (bytes <= 0)
        {
            Interlocked.Increment(ref _videoCapacityMissDrops);
            return;
        }

        if (!_videoPool.TryDequeue(out var dst))
        {
            Interlocked.Increment(ref _videoPoolMissDrops);
            return;
        }

        if (dst.Length < bytes)
        {
            _videoPool.Enqueue(dst);
            Interlocked.Increment(ref _videoCapacityMissDrops);
            return;
        }

        frame.Data.Span[..bytes].CopyTo(dst.AsSpan(0, bytes));
        _videoPending.Enqueue(new PendingVideo(dst, frame.Width, frame.Height, frame.Pts.Ticks, frame.PixelFormat, bytes));
        Interlocked.Increment(ref _videoPendingFrames);
        _videoPendingSignal.Release();
    }

    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
    {
        if (!_hasAudio || Volatile.Read(ref _started) == 0)
            return;

        int outCh = _audioTargetFormat.Channels;
        int requestedWriteFrames = sourceFormat.SampleRate == _audioTargetFormat.SampleRate
            ? frameCount
            : (int)Math.Round((double)frameCount * _audioTargetFormat.SampleRate / sourceFormat.SampleRate);
        int requestedWriteSamples = requestedWriteFrames * outCh;

        if (!_audioPool.TryDequeue(out var dest))
        {
            Interlocked.Increment(ref _audioPoolMissDrops);
            return;
        }

        if (dest.Length < requestedWriteSamples)
        {
            _audioPool.Enqueue(dest);
            Interlocked.Increment(ref _audioCapacityMissDrops);
            return;
        }

        int writtenSamples;
        if (_audioResampler != null && sourceFormat.SampleRate != _audioTargetFormat.SampleRate)
        {
            int writtenFrames = _audioResampler.Resample(buffer, dest.AsSpan(0, requestedWriteSamples), sourceFormat, _audioTargetFormat.SampleRate);
            writtenSamples = Math.Clamp(writtenFrames, 0, requestedWriteFrames) * outCh;
        }
        else
        {
            int copy = Math.Min(buffer.Length, requestedWriteSamples);
            buffer[..copy].CopyTo(dest.AsSpan(0, copy));
            if (copy < requestedWriteSamples)
                dest.AsSpan(copy, requestedWriteSamples - copy).Clear();
            writtenSamples = requestedWriteSamples;
        }

        if (writtenSamples <= 0)
        {
            _audioPool.Enqueue(dest);
            Interlocked.Increment(ref _audioCapacityMissDrops);
            return;
        }

        if (Volatile.Read(ref _audioPendingBuffers) >= _audioMaxPendingBuffers)
        {
            _audioPool.Enqueue(dest);
            Interlocked.Increment(ref _audioQueueDrops);
            return;
        }

        int writtenFramesForClock = writtenSamples / outCh;
        long startTicks = _timing.ReserveAudioTimecode(writtenFramesForClock, _audioTargetFormat.SampleRate);

        _audioPending.Enqueue(new PendingAudio(dest, writtenSamples, startTicks));
        Interlocked.Increment(ref _audioPendingBuffers);
        _audioPendingSignal.Release();
    }

    public VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        long queueDrops = Interlocked.Read(ref _videoQueueDrops);
        long dropped = Interlocked.Read(ref _videoPoolMissDrops)
                       + Interlocked.Read(ref _videoCapacityMissDrops)
                       + Interlocked.Read(ref _videoFormatDrops)
                       + queueDrops;

        return new VideoEndpointDiagnosticsSnapshot(
            PassthroughFrames: Interlocked.Read(ref _videoPassthroughFrames),
            ConvertedFrames: Interlocked.Read(ref _videoConvertedFrames),
            DroppedFrames: dropped,
            QueueDepth: Volatile.Read(ref _videoPendingFrames),
            QueueDrops: queueDrops);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _started, 0);

        _cts?.Cancel();
        _videoThread?.Join(TimeSpan.FromSeconds(2));
        _audioThread?.Join(TimeSpan.FromSeconds(2));
        _videoPendingSignal.Dispose();
        _audioPendingSignal.Dispose();
        _videoConverter.Dispose();
        if (_ownsAudioResampler) _audioResampler?.Dispose();
    }

    private static int VideoLineStride(PixelFormat fmt, int w) => fmt switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => w * 4,
        PixelFormat.Uyvy422 => w * 2,
        PixelFormat.Nv12 or PixelFormat.Yuv420p => w,
        _ => w * 4,
    };

    private static NDIFourCCVideoType ToFourCc(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Bgra32 => NDIFourCCVideoType.Bgra,
        PixelFormat.Rgba32 => NDIFourCCVideoType.Rgba,
        PixelFormat.Nv12 => NDIFourCCVideoType.Nv12,
        PixelFormat.Uyvy422 => NDIFourCCVideoType.Uyvy,
        PixelFormat.Yuv420p => NDIFourCCVideoType.I420,
        _ => NDIFourCCVideoType.Rgba,
    };

    private static bool EnsureFfmpegLoaded()
    {
        lock (sFfmpegLoadLock)
        {
            int state = sFfmpegLoadState;
            if (state == 1) return true;
            if (state == -1) return false;
            try
            {
                FFmpegLoader.EnsureLoaded();
                sFfmpegLoadState = 1;
                return true;
            }
            catch
            {
                sFfmpegLoadState = -1;
                return false;
            }
        }
    }

    private static bool TryConvertI210ToUyvyInPlace(byte[] buffer, int width, int height, int sourceBytes, out int outputBytes)
    {
        outputBytes = 0;
        if (width <= 0 || height <= 0) return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int required = ySize + (uvSize * 2);
        if (sourceBytes < required || buffer.Length < required) return false;

        int dstStride = width * 2;
        outputBytes = dstStride * height;
        if (buffer.Length < outputBytes) return false;

        static byte Narrow10To8(ushort v)
        {
            int n = ((v & 0x03FF) + 2) >> 2;
            if (n < 0) n = 0;
            if (n > 255) n = 255;
            return (byte)n;
        }

        for (int row = height - 1; row >= 0; row--)
        {
            int yRow = row * yStride;
            int uvRow = row * uvStride;
            int dstRow = row * dstStride;

            for (int x = width - 2; x >= 0; x -= 2)
            {
                int y0Off = yRow + (x * 2);
                int y1Off = y0Off + 2;
                int uvOff = uvRow + ((x >> 1) * 2);

                ushort y0V = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(y0Off, 2));
                ushort y1V = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(y1Off, 2));
                ushort uv = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(uvOff + ySize, 2));
                ushort vv = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(uvOff + ySize + uvSize, 2));

                int d = dstRow + (x * 2);
                buffer[d] = Narrow10To8(uv);
                buffer[d + 1] = Narrow10To8(y0V);
                buffer[d + 2] = Narrow10To8(vv);
                buffer[d + 3] = Narrow10To8(y1V);
            }
        }

        return true;
    }

    private static bool TryConvertI210ToRgbaManaged(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba)
    {
        if (width <= 0 || height <= 0) return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int srcRequired = ySize + (uvSize * 2);
        int dstRequired = width * height * 4;
        if (src.Length < srcRequired || dst.Length < dstRequired) return false;

        var yPlane = src[..ySize];
        var uPlane = src.Slice(ySize, uvSize);
        var vPlane = src.Slice(ySize + uvSize, uvSize);

        static byte Clamp(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 255f) return 255;
            return (byte)(v + 0.5f);
        }

        for (int y = 0; y < height; y++)
        {
            int yRow = y * yStride;
            int uvRow = y * uvStride;
            int dstRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int yOff = yRow + (x * 2);
                int uvOff = uvRow + ((x >> 1) * 2);

                int y10 = BinaryPrimitives.ReadUInt16LittleEndian(yPlane.Slice(yOff, 2)) & 0x03FF;
                int u10 = BinaryPrimitives.ReadUInt16LittleEndian(uPlane.Slice(uvOff, 2)) & 0x03FF;
                int v10 = BinaryPrimitives.ReadUInt16LittleEndian(vPlane.Slice(uvOff, 2)) & 0x03FF;

                float yf = y10 / 1023f;
                float uf = (u10 - 512f) / 512f;
                float vf = (v10 - 512f) / 512f;

                byte r = Clamp((yf + (1.5748f * vf)) * 255f);
                byte g = Clamp((yf - (0.1873f * uf) - (0.4681f * vf)) * 255f);
                byte b = Clamp((yf + (1.8556f * uf)) * 255f);

                int d = dstRow + (x * 4);
                if (dstRgba)
                {
                    dst[d] = r;
                    dst[d + 1] = g;
                    dst[d + 2] = b;
                    dst[d + 3] = 255;
                }
                else
                {
                    dst[d] = b;
                    dst[d + 1] = g;
                    dst[d + 2] = r;
                    dst[d + 3] = 255;
                }
            }
        }

        return true;
    }

    private static unsafe bool TryConvertI210ToRgbaFfmpeg(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int width,
        int height,
        bool dstRgba,
        ref SwsContext* sws)
    {
        if (width <= 0 || height <= 0) return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int srcRequired = ySize + (uvSize * 2);
        int dstRequired = width * height * 4;
        if (src.Length < srcRequired || dst.Length < dstRequired) return false;

        AVPixelFormat dstFmt = dstRgba ? AVPixelFormat.AV_PIX_FMT_RGBA : AVPixelFormat.AV_PIX_FMT_BGRA;
        sws = ffmpeg.sws_getCachedContext(
            sws,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
            width,
            height,
            dstFmt,
            2,
            null,
            null,
            null);

        if (sws == null) return false;

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            byte* y = pSrc;
            byte* u = pSrc + ySize;
            byte* v = pSrc + ySize + uvSize;

            byte*[] srcData = [y, u, v, null];
            int[] srcStride = [yStride, uvStride, uvStride, 0];
            byte*[] dstData = [pDst, null, null, null];
            int[] dstStride = [width * 4, 0, 0, 0];

            return ffmpeg.sws_scale(sws, srcData, srcStride, 0, height, dstData, dstStride) == height;
        }
    }

    private unsafe void VideoWriteLoop()
    {
        var token = _cts!.Token;
        int fpsNum = _videoTargetFormat.FrameRateNumerator > 0 ? _videoTargetFormat.FrameRateNumerator : 30000;
        int fpsDen = _videoTargetFormat.FrameRateDenominator > 0 ? _videoTargetFormat.FrameRateDenominator : 1001;
        SwsContext* ffmpegSws = null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                _videoPendingSignal.Wait(token);
            }
            catch (OperationCanceledException) { break; }

            while (_videoPending.TryDequeue(out var pf))
            {
                Interlocked.Decrement(ref _videoPendingFrames);

                try
                {
                    ReadOnlyMemory<byte> payload;
                    PixelFormat sendFormat;
                    IDisposable? tempOwner = null;
                    byte[]? scratchBuffer = null;

                    try
                    {
                        if (pf.PixelFormat == _videoTargetFormat.PixelFormat)
                        {
                            payload = pf.Buffer.AsMemory(0, pf.Bytes);
                            sendFormat = pf.PixelFormat;
                            Interlocked.Increment(ref _videoPassthroughFrames);
                        }
                        else if (pf.PixelFormat == PixelFormat.Yuv422p10 && _videoTargetFormat.PixelFormat == PixelFormat.Uyvy422)
                        {
                            if (!TryConvertI210ToUyvyInPlace(pf.Buffer, pf.Width, pf.Height, pf.Bytes, out int uyvyBytes))
                            {
                                Interlocked.Increment(ref _videoConversionDrops);
                                Interlocked.Increment(ref _videoFormatDrops);
                                continue;
                            }

                            payload = pf.Buffer.AsMemory(0, uyvyBytes);
                            sendFormat = PixelFormat.Uyvy422;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }
                        else if (pf.PixelFormat == PixelFormat.Yuv422p10
                                 && (_videoTargetFormat.PixelFormat == PixelFormat.Rgba32 || _videoTargetFormat.PixelFormat == PixelFormat.Bgra32))
                        {
                            int rgbaBytes = pf.Width * pf.Height * 4;
                            bool converted = false;

                            scratchBuffer = ArrayPool<byte>.Shared.Rent(rgbaBytes);

                            if (EnsureFfmpegLoaded())
                            {
                                converted = TryConvertI210ToRgbaFfmpeg(
                                    pf.Buffer.AsSpan(0, pf.Bytes),
                                    scratchBuffer.AsSpan(0, rgbaBytes),
                                    pf.Width,
                                    pf.Height,
                                    _videoTargetFormat.PixelFormat == PixelFormat.Rgba32,
                                    ref ffmpegSws);
                            }

                            if (!converted)
                            {
                                converted = TryConvertI210ToRgbaManaged(
                                    pf.Buffer.AsSpan(0, pf.Bytes),
                                    scratchBuffer.AsSpan(0, rgbaBytes),
                                    pf.Width,
                                    pf.Height,
                                    _videoTargetFormat.PixelFormat == PixelFormat.Rgba32);
                            }

                            if (!converted)
                            {
                                Interlocked.Increment(ref _videoConversionDrops);
                                Interlocked.Increment(ref _videoFormatDrops);
                                continue;
                            }

                            payload = scratchBuffer.AsMemory(0, rgbaBytes);
                            sendFormat = _videoTargetFormat.PixelFormat;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }
                        else
                        {
                            var srcFrame = new VideoFrame(
                                pf.Width,
                                pf.Height,
                                pf.PixelFormat,
                                pf.Buffer.AsMemory(0, pf.Bytes),
                                TimeSpan.FromTicks(pf.PtsTicks));

                            var converted = _videoConverter.Convert(srcFrame, _videoTargetFormat.PixelFormat);
                            payload = converted.Data;
                            sendFormat = converted.PixelFormat;
                            tempOwner = converted.MemoryOwner;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }

                        if (!MemoryMarshal.TryGetArray(payload, out var seg) || seg.Array == null)
                        {
                            Interlocked.Increment(ref _videoFormatDrops);
                            continue;
                        }

                        fixed (byte* p = &seg.Array[seg.Offset])
                        {
                            _timing.ObserveVideoPts(pf.PtsTicks);

                            var vf = new NDIVideoFrameV2
                            {
                                Xres = pf.Width,
                                Yres = pf.Height,
                                FourCC = ToFourCc(sendFormat),
                                FrameRateN = fpsNum,
                                FrameRateD = fpsDen,
                                PictureAspectRatio = pf.Height > 0 ? (float)pf.Width / pf.Height : 1f,
                                FrameFormatType = NDIFrameFormatType.Progressive,
                                Timecode = pf.PtsTicks,
                                PData = (nint)p,
                                LineStrideInBytes = VideoLineStride(sendFormat, pf.Width),
                                PMetadata = nint.Zero,
                                Timestamp = pf.PtsTicks
                            };

                            lock (_sendLock)
                                _sender.SendVideo(vf);
                        }
                    }
                    catch (NotSupportedException)
                    {
                        Interlocked.Increment(ref _videoConversionDrops);
                        Interlocked.Increment(ref _videoFormatDrops);
                    }
                    finally
                    {
                        tempOwner?.Dispose();
                        if (scratchBuffer != null)
                            ArrayPool<byte>.Shared.Return(scratchBuffer);
                    }
                }

                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                        Console.Error.WriteLine($"[{Name}] NDI video send exception: {ex.Message}");
                }
                finally
                {
                    _videoPool.Enqueue(pf.Buffer);
                }
            }
        }

        if (ffmpegSws != null)
            ffmpeg.sws_freeContext(ffmpegSws);
    }

    private unsafe void AudioWriteLoop()
    {
        var token = _cts!.Token;
        int channels = _audioTargetFormat.Channels;
        var planar = new float[_audioFramesPerBuffer * channels];

        while (!token.IsCancellationRequested)
        {
            try
            {
                _audioPendingSignal.Wait(token);
            }
            catch (OperationCanceledException) { break; }

            while (_audioPending.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _audioPendingBuffers);

                var interleaved = pending.Buffer;
                int sampleValues = pending.Samples;
                int samplesPerChannel = sampleValues / channels;
                int planarNeed = channels * samplesPerChannel;
                if (planar.Length < planarNeed)
                    planar = new float[planarNeed];

                for (int c = 0; c < channels; c++)
                    for (int s = 0; s < samplesPerChannel; s++)
                        planar[c * samplesPerChannel + s] = interleaved[s * channels + c];

                _audioPool.Enqueue(interleaved);

                fixed (float* pData = planar)
                {
                    var frame = new NDIAudioFrameV3
                    {
                        SampleRate = _audioTargetFormat.SampleRate,
                        NoChannels = channels,
                        NoSamples = samplesPerChannel,
                        FourCC = NDIFourCCAudioType.Fltp,
                        PData = (nint)pData,
                        ChannelStrideInBytes = samplesPerChannel * sizeof(float),
                        Timecode = pending.TimecodeTicks,
                        Timestamp = pending.TimecodeTicks
                    };

                    lock (_sendLock)
                        _sender.SendAudio(frame);
                }
            }
        }
    }
}
