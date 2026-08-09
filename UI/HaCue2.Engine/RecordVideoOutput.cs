using S.Media.Core.Video;
using S.Media.Encode.FFmpeg;

namespace HaCue2.Engine;

/// <summary>
/// A record or stream output, as something the compositor can render into whether or not it is armed.
/// </summary>
/// <remarks>
/// <para>
/// The compositor attaches outputs when the show loads and holds them for the life of a composition.
/// Arming happens whenever the operator says so, which is usually much later — so the thing handed to
/// the compositor cannot BE the encode session. This wrapper is the stable half: it exists from open to
/// close, and arming swaps a session in behind it.
/// </para>
/// <para>
/// The alternative — creating the session at open and reloading the document to attach it — would make
/// arming a recording restart every clip on that composition. Pressing record must not interrupt the
/// show it is recording.
/// </para>
/// <para>
/// While disarmed, frames are DROPPED rather than buffered. A recording starts when it is armed; frames
/// held from before would either lie about when it started or grow without bound while nobody records.
/// </para>
/// </remarks>
internal sealed class RecordVideoOutput : IVideoOutput
{
    private readonly object _gate = new();
    private IVideoOutput? _sink;
    private VideoFormat _format;

    /// <summary>
    /// What the compositor may send.
    /// </summary>
    /// <remarks>
    /// The formats <see cref="FFmpegEncodeVideoSink"/> accepts, in its own preference order, declared
    /// even while disarmed — negotiation happens once when the compositor attaches, and it has to
    /// settle on a format the encoder will still want whenever somebody arms. Listing them here means
    /// this list and the sink's must agree; the alternative is opening an encode session against a real
    /// file before anybody has asked to record, only to read a property off it.
    /// </remarks>
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } =
    [
        PixelFormat.I420,
        PixelFormat.Nv12,
        PixelFormat.Yuv420P10Le,
        PixelFormat.P010,
        PixelFormat.Yuv422P,
        PixelFormat.Bgra32,
        PixelFormat.Rgba32,
    ];

    public VideoFormat Format
    {
        get { lock (_gate) return _format; }
    }

    /// <summary>The format the compositor negotiated, for sizing the encode session at arm time.</summary>
    public VideoFormat? Negotiated
    {
        get
        {
            lock (_gate)
                return _format.Width > 0 && _format.Height > 0 ? _format : null;
        }
    }

    public void Configure(VideoFormat format)
    {
        lock (_gate)
        {
            _format = format;
            _sink?.Configure(format);
        }
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IVideoOutput? sink;

        // The lock is released before submitting: the encode sink hands the frame to the session's
        // bounded queue and can block when that queue is full, and holding the gate across it would
        // stall a disarm — the one action an operator takes when a recording is misbehaving.
        lock (_gate)
            sink = _sink;

        if (sink is not null)
            sink.Submit(frame);
        else
            frame.Dispose();
    }

    /// <summary>
    /// Points the output at an armed session's sink, configuring it with the live format.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="IVideoOutput"/> rather than the encode sink because a CONTINUOUS recording
    /// interposes a carrier that fills idle time with black — the wrapper must not care which it has.
    /// </remarks>
    internal void Arm(IVideoOutput sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            if (_format.Width > 0 && _format.Height > 0)
                sink.Configure(_format);

            _sink = sink;
        }
    }

    /// <summary>Stops feeding the session, so it can be flushed and closed.</summary>
    internal void Disarm()
    {
        lock (_gate)
            _sink = null;
    }
}
