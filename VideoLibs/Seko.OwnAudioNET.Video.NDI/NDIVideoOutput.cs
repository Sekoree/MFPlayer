using System.Buffers;
using NdiLib;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// NDI-backed video sink that plugs into existing <see cref="IVideoOutput"/> routing.
/// </summary>
public sealed class NDIVideoOutput : IVideoOutput
{
    private readonly NDISenderSession _session;
    private readonly NDITimelineClock _timeline;
    private readonly NDIEngineConfig _config;
    private readonly Lock _syncLock = new();

    private byte[]? _bgraScratch;
    private bool _running;
    private bool _disposed;

    internal NDIVideoOutput(NDISenderSession session, NDITimelineClock timeline, NDIEngineConfig config)
    {
        _session = session;
        _timeline = timeline;
        _config = config;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public IVideoSource? Source { get; private set; }

    public bool IsAttached => Source != null;

    internal void Start()
    {
        lock (_syncLock)
            _running = true;
    }

    internal void Stop()
    {
        lock (_syncLock)
            _running = false;
    }

    public bool AttachSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(Source, source))
            return true;

        DetachSource();
        Source = source;
        return true;
    }

    public void DetachSource()
    {
        Source = null;
    }

    public bool PushFrame(VideoFrame frame, double masterTimestamp)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        lock (_syncLock)
        {
            if (!_running)
                return false;
        }

        if (frame.PixelFormat != VideoPixelFormat.Rgba32 || frame.PlaneCount < 1)
            return false;

        var planeData = frame.GetPlaneData(0);
        var planeLength = frame.GetPlaneLength(0);
        var stride = frame.GetPlaneStride(0);

        if (planeLength <= 0 || planeData.Length < planeLength)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0)
            return false;

        var sendAsBgra = _config.RgbaSendFormat == NDIVideoRgbaSendFormat.Bgra;
        var fourCc = sendAsBgra ? NdiFourCCVideoType.Bgra : NdiFourCCVideoType.Rgba;

        if (sendAsBgra)
        {
            var bgra = RentBgraBuffer(planeLength);
            ConvertRgbaToBgra(planeData.AsSpan(0, planeLength), bgra.AsSpan(0, planeLength));

            unsafe
            {
                fixed (byte* pData = bgra)
                {
                    var nativeFrame = BuildFrame(width, height, stride, fourCc, (nint)pData, masterTimestamp);
                    _session.SendVideo(nativeFrame);
                }
            }
        }
        else
        {
            unsafe
            {
                fixed (byte* pData = planeData)
                {
                    var nativeFrame = BuildFrame(width, height, stride, fourCc, (nint)pData, masterTimestamp);
                    _session.SendVideo(nativeFrame);
                }
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_syncLock)
        {
            _running = false;
        }

        if (_bgraScratch != null)
        {
            ArrayPool<byte>.Shared.Return(_bgraScratch, clearArray: false);
            _bgraScratch = null;
        }

        DetachSource();
        _disposed = true;
    }

    private byte[] RentBgraBuffer(int minLength)
    {
        if (_bgraScratch != null && _bgraScratch.Length >= minLength)
            return _bgraScratch;

        if (_bgraScratch != null)
            ArrayPool<byte>.Shared.Return(_bgraScratch, clearArray: false);

        _bgraScratch = ArrayPool<byte>.Shared.Rent(minLength);
        return _bgraScratch;
    }

    private NdiVideoFrameV2 BuildFrame(int width, int height, int strideBytes, NdiFourCCVideoType fourCc, nint pData, double masterTimestamp)
    {
        return new NdiVideoFrameV2
        {
            Xres = width,
            Yres = height,
            FourCC = fourCc,
            FrameRateN = 0,
            FrameRateD = 0,
            PictureAspectRatio = 0f,
            FrameFormatType = NdiFrameFormatType.Progressive,
            Timecode = _timeline.ResolveVideoTimecode100ns(masterTimestamp, _config.UseIncomingVideoTimestamps),
            PData = pData,
            LineStrideInBytes = strideBytes > 0 ? strideBytes : width * 4,
            PMetadata = nint.Zero,
            Timestamp = 0
        };
    }

    private static void ConvertRgbaToBgra(ReadOnlySpan<byte> rgba, Span<byte> bgra)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            bgra[i + 0] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i + 0];
            bgra[i + 3] = rgba[i + 3];
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NDIVideoOutput));
    }
}

