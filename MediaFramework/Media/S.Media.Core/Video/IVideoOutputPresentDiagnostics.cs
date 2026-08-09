namespace S.Media.Core.Video;

/// <summary>
/// Optional counters from an output that presents on its own device cadence (a vsynced window, say)
/// rather than consuming frames as fast as they are submitted.
/// </summary>
/// <remarks>
/// <para>
/// This is the last stage a frame passes through, and until it reports, frame health cannot be measured
/// end to end. Everything upstream can be perfectly healthy while this stage discards frames: a producer
/// running at an authored rate and a panel running on its own pixel clock are never at exactly the same
/// rate, so one of <see cref="DroppedFrames"/> or <see cref="RepeatedFrames"/> always accrues. Counters
/// upstream of here cannot see either, which makes an unattributed stutter look like nothing is wrong.
/// </para>
/// <para>
/// Reading these is safe from any thread and must not block the presenting thread.
/// </para>
/// </remarks>
public interface IVideoOutputPresentDiagnostics
{
    /// <summary>Frames actually put on the device.</summary>
    long PresentedFrames { get; }

    /// <summary>
    /// Frames discarded because a newer one arrived while the present queue was full - the producer is
    /// outrunning the device. A slow, steady count is the expected cost of two unequal rates; a fast or
    /// bursty one means something upstream is overproducing.
    /// </summary>
    long DroppedFrames { get; }

    /// <summary>
    /// Device cadences that re-showed the previous frame because no new one was ready - the producer is
    /// running slower than the device. The mirror image of <see cref="DroppedFrames"/>; an idle output
    /// holding a still image must not accrue these.
    /// </summary>
    long RepeatedFrames { get; }

    /// <summary>Frames currently waiting for a device cadence.</summary>
    int QueuedFrames { get; }

    /// <summary>Present-queue depth. Each slot is up to one device period of added latency.</summary>
    int PresentQueueDepth { get; }

    /// <summary>
    /// Smoothed wall-clock time from a frame being submitted to it actually reaching the device, or
    /// <see cref="TimeSpan.Zero"/> before enough frames have gone through to measure one.
    /// </summary>
    /// <remarks>
    /// This is what makes video late against audio. Audio output already subtracts its device latency,
    /// so the audible position IS "now"; video composited for "now" then spends this long in the queue
    /// and the swap before anyone sees it, and lands that far behind. A host that wants them aligned has
    /// to run the video pipeline this far AHEAD - which it cannot do without a number, and this is the
    /// number. Measured rather than assumed: it depends on queue depth, refresh rate and how the platform
    /// buffers, none of which are knowable up front.
    /// </remarks>
    TimeSpan PresentLatency { get; }
}
