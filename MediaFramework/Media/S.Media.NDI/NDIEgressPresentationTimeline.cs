using System.Diagnostics;

namespace S.Media.NDI;

/// <summary>The two streams an NDI egress carrier can stamp against one shared timeline.</summary>
internal enum NDIEgressStream
{
    Video = 0,
    Audio = 1,
}

/// <summary>
/// Single session anchor for NDI timecodes (100 ns ticks, same unit as <see cref="System.TimeSpan.Ticks"/>)
/// when <see cref="Video.NDIVideoTimecodeMode.PresentationRelativeTicks"/> is active on an <see cref="NDIOutput"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two modes, chosen at construction:
/// </para>
/// <para>
/// <b>Shared-domain</b> (default) - audio and video presentation times come from ONE source (a muxed
/// file, a single player), so whichever stream submits first establishes the anchor and both report
/// deltas against it. Exact relative offsets, the original behaviour.
/// </para>
/// <para>
/// <b>Independent-domain</b> - the two halves live in unrelated PTS domains (HaCue2's linked A/V
/// carrier: video is composition/canvas time, audio is the bay's stream position). A shared PTS
/// anchor would put one half hours off, which is why the bay historically bypassed the timeline
/// entirely and the halves drifted. Here the carrier keeps ONE wall-clock epoch; each stream maps
/// its own domain onto it at first submit (timecode = elapsed-at-anchor + (pts − anchorPts)), so
/// within-stream timecodes follow that stream's own smooth PTS while cross-stream timecodes stay
/// correlated in real time. A per-stream reset (video re-configure on edit, audio re-open on bay
/// APPLY) re-maps only that stream against the surviving epoch - timecodes stay monotonic and the
/// other half's base is untouched.
/// </para>
/// <para>
/// Thread-safe: audio and video pumps may call concurrently.
/// </para>
/// </remarks>
internal sealed class NDIEgressPresentationTimeline(bool independentStreamDomains = false)
{
    private readonly Lock _gate = new();

    // Shared-domain state.
    private TimeSpan? _anchor;

    // Independent-domain state: one wall epoch for the carrier, one (pts, elapsed) anchor per stream.
    private long _epochTimestamp;
    private readonly TimeSpan?[] _streamAnchorPts = new TimeSpan?[2];
    private readonly TimeSpan[] _streamAnchorElapsed = new TimeSpan[2];

    /// <summary>True when each stream maps its own PTS domain onto a shared wall-clock epoch.</summary>
    public bool IndependentStreamDomains { get; } = independentStreamDomains;

    /// <summary>Clears every anchor (for example after seek or before a new configure). The wall
    /// epoch survives in independent mode so timecodes continue monotonically instead of jumping
    /// back to zero mid-stream.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _anchor = null;
            _streamAnchorPts[0] = null;
            _streamAnchorPts[1] = null;
        }
    }

    /// <summary>
    /// Clears one stream's mapping in independent mode - its next submit re-maps against the carrier
    /// epoch at the then-current elapsed time. In shared mode both streams answer to one anchor, so a
    /// per-stream reset degrades to <see cref="Reset"/> (a configure there IS a new session).
    /// </summary>
    public void ResetStream(NDIEgressStream stream)
    {
        if (!IndependentStreamDomains)
        {
            Reset();
            return;
        }

        lock (_gate)
            _streamAnchorPts[(int)stream] = null;
    }

    /// <summary>
    /// Returns the NDI timecode (100 ns ticks) for <paramref name="presentationTime"/> on
    /// <paramref name="stream"/>, establishing anchors on first use. A backward PTS jump of more
    /// than one second re-anchors that stream (same rule as <see cref="Video.NDIVideoSender"/>).
    /// </summary>
    public long TimecodeFromPresentationTime(NDIEgressStream stream, TimeSpan presentationTime)
    {
        lock (_gate)
        {
            return IndependentStreamDomains
                ? IndependentTimecodeLocked(stream, presentationTime)
                : SharedTimecodeLocked(presentationTime);
        }
    }

    private long SharedTimecodeLocked(TimeSpan presentationTime)
    {
        if (_anchor is null)
            _anchor = presentationTime;

        var anchor = _anchor.Value;
        var delta = presentationTime - anchor;
        if (delta < TimeSpan.FromSeconds(-1))
        {
            _anchor = presentationTime;
            delta = TimeSpan.Zero;
        }

        return delta < TimeSpan.Zero ? 0L : delta.Ticks;
    }

    private long IndependentTimecodeLocked(NDIEgressStream stream, TimeSpan presentationTime)
    {
        if (_epochTimestamp == 0)
            _epochTimestamp = Stopwatch.GetTimestamp();

        var index = (int)stream;
        var anchorPts = _streamAnchorPts[index];

        // Re-map on first submit and on a >1 s backward jump (a rebased/restarted upstream timeline):
        // the new PTS lands at the CURRENT elapsed time, so the carrier's timecode never regresses
        // and the other stream's mapping is untouched.
        if (anchorPts is null || presentationTime < anchorPts.Value - TimeSpan.FromSeconds(1))
        {
            anchorPts = presentationTime;
            _streamAnchorPts[index] = anchorPts;
            _streamAnchorElapsed[index] = Stopwatch.GetElapsedTime(_epochTimestamp);
        }

        var timecode = _streamAnchorElapsed[index] + (presentationTime - anchorPts.Value);
        return timecode < TimeSpan.Zero ? 0L : timecode.Ticks;
    }
}
