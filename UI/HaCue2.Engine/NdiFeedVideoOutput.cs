using HaCue2.Core.Model;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
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
internal sealed class NdiFeedVideoOutput : IVideoOutput, IVideoOutputCooperativeAbort, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaCue2.NdiFeedVideoOutput");

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

        // The operator's wire choice is a real setting: convert when the composition produces
        // something else, and SAY SO when the converter cannot - a silently ignored option reads as
        // a broken feed on the receiver with nothing in the logs to explain it.
        var wireFormat = _wire switch
        {
            NdiWireFormat.Uyvy => PixelFormat.Uyvy,
            NdiWireFormat.Bgra => PixelFormat.Bgra32,
            _ => (PixelFormat?)null,
        };

        if (wireFormat is { } wanted && format.PixelFormat != wanted)
        {
            if (VideoCpuFrameConverter.CanConvert(format.PixelFormat, wanted, format.Width, format.Height))
            {
                _converter?.Dispose();
                _converter = new VideoCpuFrameConverter();
                _converter.Configure(format.PixelFormat, wanted, format.Width, format.Height);
                _lease.Video.Configure(format with { PixelFormat = wanted });
                return;
            }

            Trace.LogWarning(
                "NDI '{Source}': wire format {Wire} requested but {From}→{To} at {W}x{H} is not convertible - sending the composition's format instead",
                _lease.Sender.SourceName, _wire, format.PixelFormat, wanted, format.Width, format.Height);
        }

        _converter?.Dispose();
        _converter = null;
        _lease.Video.Configure(format);
    }

    /// <summary>
    /// <see cref="IVideoOutputCooperativeAbort"/>: forwarded to the shared sender's video half so an
    /// owning pump's teardown is not stuck waiting out a pace interval. Wrapper-must-forward rule -
    /// today HaCue2's pumps do not own this output (<c>disposeInner:false</c>), but a future owning
    /// pump probing the wrapper must not silently lose the capability the inner sender has.
    /// </summary>
    public void RequestSubmitAbort() => (_lease.Video as IVideoOutputCooperativeAbort)?.RequestSubmitAbort();

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
