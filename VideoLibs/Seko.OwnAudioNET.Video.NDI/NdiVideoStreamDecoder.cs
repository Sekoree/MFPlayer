using NdiLib;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Decoders;

namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Video decoder adapter that pulls RGBA frames from NDI frame-sync.
/// </summary>
public sealed class NdiVideoStreamDecoder : IVideoDecoder
{
    private readonly NdiFrameSync _frameSync;
    private readonly INdiExternalTimelineClock _timelineClock;
    private readonly Lock? _frameSyncLock;

    private VideoStreamInfo _streamInfo;
    private byte[] _bgraScratch = Array.Empty<byte>();
    private double _frameDurationSeconds;
    private bool _disposed;

    public NdiVideoStreamDecoder(NdiFrameSync frameSync, INdiExternalTimelineClock timelineClock, Lock? frameSyncLock = null)
    {
        _frameSync = frameSync ?? throw new ArgumentNullException(nameof(frameSync));
        _timelineClock = timelineClock ?? throw new ArgumentNullException(nameof(timelineClock));
        _frameSyncLock = frameSyncLock;

        // Start with unknown stream metadata; first valid frame updates it.
        _streamInfo = new VideoStreamInfo(0, 0, 0, TimeSpan.Zero, VideoPixelFormat.Rgba32);
        ProbeInitialVideoFormat();
    }

    public VideoStreamInfo StreamInfo => _streamInfo;

    public bool IsEndOfStream => false;

    public bool IsHardwareDecoding => false;

    public event Action<VideoStreamInfo>? StreamInfoChanged;

    public bool TryDecodeNextFrame(out VideoFrame frame, out string? error)
    {
        ThrowIfDisposed();

        if (_frameSyncLock != null)
        {
            lock (_frameSyncLock)
                return TryDecodeNextFrameCore(out frame, out error);
        }

        return TryDecodeNextFrameCore(out frame, out error);
    }

    private bool TryDecodeNextFrameCore(out VideoFrame frame, out string? error)
    {
        ThrowIfDisposed();

        _frameSync.CaptureVideo(out var video);
        try
        {
            if (video.PData == nint.Zero || video.Xres <= 0 || video.Yres <= 0)
            {
                frame = default!;
                error = "No video frame available yet.";
                return false;
            }

            var stride = video.LineStrideInBytes > 0 ? video.LineStrideInBytes : video.Xres * 4;
            var byteCount = stride * video.Yres;
            if (byteCount <= 0)
            {
                frame = default!;
                error = "Invalid video frame size.";
                return false;
            }

            unsafe
            {
                var src = new ReadOnlySpan<byte>((void*)video.PData, byteCount);
                if (video.FourCC is NdiFourCCVideoType.Bgra or NdiFourCCVideoType.Bgrx)
                {
                    EnsureBgraScratch(byteCount);
                    ConvertBgraToRgba(src, _bgraScratch.AsSpan(0, byteCount));
                    src = _bgraScratch.AsSpan(0, byteCount);
                }
                else if (video.FourCC is not (NdiFourCCVideoType.Rgba or NdiFourCCVideoType.Rgbx))
                {
                    frame = default!;
                    error = $"Unsupported NDI pixel format: {video.FourCC}";
                    return false;
                }

                var ptsSeconds = ResolveTimestampSeconds(video);
                frame = VideoFrame.CreateExternalRgba32(src, video.Xres, video.Yres, stride, ptsSeconds);
            }

            UpdateStreamInfo(video);
            error = null;
            return true;
        }
        finally
        {
            _frameSync.FreeVideo(video);
        }
    }

    public bool TrySeek(TimeSpan position, out string error)
    {
        ThrowIfDisposed();
        error = string.Empty;
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ProbeInitialVideoFormat()
    {
        if (_frameSyncLock != null)
        {
            lock (_frameSyncLock)
            {
                ProbeInitialVideoFormatCore();
                return;
            }
        }

        ProbeInitialVideoFormatCore();
    }

    private void ProbeInitialVideoFormatCore()
    {
        _frameSync.CaptureVideo(out var video);
        try
        {
            if (video.PData == nint.Zero || video.Xres <= 0 || video.Yres <= 0)
                return;

            var frameRate = ResolveFrameRate(video);
            _streamInfo = new VideoStreamInfo(video.Xres, video.Yres, frameRate, TimeSpan.Zero, VideoPixelFormat.Rgba32);
        }
        finally
        {
            _frameSync.FreeVideo(video);
        }
    }

    private void UpdateStreamInfo(in NdiVideoFrameV2 video)
    {
        var frameRate = ResolveFrameRate(video);
        if (frameRate > 0)
            _frameDurationSeconds = 1.0 / frameRate;

        if (_streamInfo.Width == video.Xres && _streamInfo.Height == video.Yres && Math.Abs(_streamInfo.FrameRate - frameRate) < 0.001)
            return;

        _streamInfo = new VideoStreamInfo(video.Xres, video.Yres, frameRate, TimeSpan.Zero, VideoPixelFormat.Rgba32);
        StreamInfoChanged?.Invoke(_streamInfo);
    }

    private static double ResolveFrameRate(in NdiVideoFrameV2 video)
    {
        if (video.FrameRateN > 0 && video.FrameRateD > 0)
            return video.FrameRateN / (double)video.FrameRateD;

        return 0;
    }

    private double ResolveTimestampSeconds(in NdiVideoFrameV2 video)
    {
        return _timelineClock.ResolveVideoPtsSeconds(video.Timestamp, video.Timecode, _frameDurationSeconds);
    }

    private void EnsureBgraScratch(int length)
    {
        if (_bgraScratch.Length >= length)
            return;

        _bgraScratch = new byte[length];
    }

    private static void ConvertBgraToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i + 0];
            rgba[i + 3] = bgra[i + 3];
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NdiVideoStreamDecoder));
    }
}

