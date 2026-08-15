using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Video;

namespace HaCue2.Engine;

/// <summary>
/// One NDI video output's feed stage: the shared sender's video half behind the output's OWN
/// frame-rate cap and wire pixel format (register: NDI format options, HaPlay parity).
/// </summary>
/// <remarks>
/// <para>
/// The rate cap drops frames rather than re-timing them - an NDI feed capped to 30 from a 60 fps
/// composition sends every other frame at its true presentation time, which is what a receiver's
/// own pacing expects. The cap is measured against frame PresentationTime, not wall time, so a
/// paused composition does not "bank" sends.
/// </para>
/// <para>
/// UYVY halves the wire bandwidth against BGRA at 4:2:2 cost; the conversion runs on this
/// output's submit path only, so a clean BGRA feed pays nothing. Disposing this output releases
/// the sender LEASE - never the sender's cached video half, which the audio side may still be
/// carrying (see <see cref="NdiSenderHub"/>).
/// </para>
/// </remarks>
internal sealed class NdiFeedVideoOutput : IVideoOutput, IDisposable
{
    private readonly NdiSenderHub.Lease _lease;
    private readonly double _frameRateCap;
    private readonly NdiWireFormat _wire;
    private VideoCpuFrameConverter? _converter;
    private VideoFormat _inputFormat;
    private TimeSpan _minimumSpacing;
    private TimeSpan _nextDue = TimeSpan.MinValue;
    private bool _disposed;

    public NdiFeedVideoOutput(NdiSenderHub.Lease lease, double frameRateCap, NdiWireFormat wire)
    {
        _lease = lease;
        _frameRateCap = frameRateCap;
        _wire = wire;
    }

    public VideoFormat Format => _inputFormat;

    // Accept what the composition produces; the wire choice is this class's own business.
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _lease.Video.AcceptedPixelFormats;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inputFormat = format;
        _minimumSpacing = _frameRateCap > 0
            ? TimeSpan.FromSeconds(1d / _frameRateCap)
            : TimeSpan.Zero;
        _nextDue = TimeSpan.MinValue;

        if (_wire == NdiWireFormat.Uyvy && format.PixelFormat != PixelFormat.Uyvy
            && VideoCpuFrameConverter.CanConvert(format.PixelFormat, PixelFormat.Uyvy, format.Width, format.Height))
        {
            _converter?.Dispose();
            _converter = new VideoCpuFrameConverter();
            _converter.Configure(format.PixelFormat, PixelFormat.Uyvy, format.Width, format.Height);
            _lease.Video.Configure(format with { PixelFormat = PixelFormat.Uyvy });
            return;
        }

        _converter?.Dispose();
        _converter = null;
        _lease.Video.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        if (_disposed)
        {
            frame.Dispose();
            return;
        }

        if (_minimumSpacing > TimeSpan.Zero)
        {
            var pts = frame.PresentationTime;
            if (_nextDue != TimeSpan.MinValue)
            {
                // A rebased/restarted timeline (pts jumped backwards) re-arms immediately rather
                // than waiting out a stale due-time that may be minutes in the future. Addition,
                // never subtraction from the sentinel - TimeSpan.MinValue minus anything throws.
                if (pts + _minimumSpacing < _nextDue)
                {
                    _nextDue = TimeSpan.MinValue;
                }
                else if (pts < _nextDue)
                {
                    frame.Dispose();
                    return;
                }
            }

            _nextDue = pts + _minimumSpacing;
        }

        if (_converter is { } converter)
        {
            VideoFrame converted;
            try
            {
                converted = converter.Convert(frame, frame.ColorTransferHint);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                frame.Dispose();
                _ = failure;
                return; // one unconvertible frame must not fault the composition's pump
            }

            frame.Dispose();
            _lease.Video.Submit(converted);
            return;
        }

        _lease.Video.Submit(frame);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _converter?.Dispose();
        _lease.Dispose();
    }
}
