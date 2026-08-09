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
    /// Frames discarded because a newer one was available at the same presentation opportunity or the
    /// present queue was full. A slow, steady count is the expected cost of two unequal rates; a fast or
    /// bursty one indicates a stall or upstream overproduction.
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

    /// <summary>Maximum present hand-off capacity. Low-latency presenters may take only the newest slot.</summary>
    int PresentQueueDepth { get; }

    /// <summary>
    /// Smoothed wall-clock time from a frame being submitted to the presenter's last observable software
    /// boundary, or
    /// <see cref="TimeSpan.Zero"/> before enough frames have gone through to measure one.
    /// </summary>
    /// <remarks>
    /// This is a diagnostic, not guaranteed physical scanout feedback: a backend may only know when a draw
    /// or swap call returned. It must not be fed back into a shared source/composition timeline. A presenter
    /// with trustworthy scheduled-present feedback may use it locally without changing frame PTS.
    /// </remarks>
    TimeSpan PresentLatency { get; }
}
