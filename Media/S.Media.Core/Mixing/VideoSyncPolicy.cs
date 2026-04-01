using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public static class VideoSyncPolicy
{
    /// <summary>
    /// Selects the next frame to present based on the current sync mode and clock position.
    /// <para>
    /// <b>Mode behaviors:</b>
    /// <list type="bullet">
    ///   <item><see cref="AVSyncMode.Realtime"/>: Drops all queued frames except the newest
    ///     ("drop all but latest"). This is ideal for live sources (NDI, cameras) where latency
    ///     matters more than frame continuity. Stale frames (behind clock by more than
    ///     <see cref="VideoSyncOptions.StaleFrameDropThreshold"/>) are discarded entirely.</item>
    ///   <item><see cref="AVSyncMode.AudioLed"/>: Audio is the master clock. Video frames wait
    ///     until the audio clock reaches their PTS. Late frames are dropped; early frames cause
    ///     a bounded wait.</item>
    ///   <item><see cref="AVSyncMode.Synced"/>: Clock-aligned presentation with configurable
    ///     early tolerance and stale-frame dropping.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static VideoPresenterSyncDecision SelectNextFrame(
        Queue<VideoFrame> queuedVideoFrames,
        AVSyncMode syncMode,
        double clockSeconds,
        in VideoSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(queuedVideoFrames);

        var delay = options.MinDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : options.MinDelay;
        var staleThreshold = options.StaleFrameDropThreshold <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(200)
            : options.StaleFrameDropThreshold;

        if (queuedVideoFrames.Count == 0)
        {
            return new VideoPresenterSyncDecision(null, delay, 0, 0);
        }

        // Realtime: present the freshest frame, coalesce older ones.
        if (syncMode == AVSyncMode.Realtime)
        {
            double realtimeMaxWaitMs = options.MaxWait > TimeSpan.Zero
                ? options.MaxWait.TotalMilliseconds
                : 50.0;

            var selected = queuedVideoFrames.Dequeue();
            var coalescedDrops = 0;
            while (queuedVideoFrames.Count > 0)
            {
                selected.Dispose();
                selected = queuedVideoFrames.Dequeue();
                coalescedDrops++;
            }

            var frameLead = selected.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
            if (frameLead < -staleThreshold)
            {
                selected.Dispose();
                return new VideoPresenterSyncDecision(null, delay, 1, coalescedDrops);
            }

            var effectiveDelay = frameLead > delay
                ? TimeSpan.FromMilliseconds(Math.Min(frameLead.TotalMilliseconds, realtimeMaxWaitMs))
                : delay;

            return new VideoPresenterSyncDecision(selected, effectiveDelay, 0, coalescedDrops);
        }

        // AudioLed: audio is master clock.  Video waits for the audio clock to
        // reach (or nearly reach) the frame's PTS.
        if (syncMode == AVSyncMode.AudioLed)
        {
            double audioLedMaxWaitMs = options.MaxWait > TimeSpan.Zero
                ? options.MaxWait.TotalMilliseconds
                : 50.0;

            // Drop stale frames whose PTS is far behind the audio clock.
            var lateDrops = 0;
            while (queuedVideoFrames.Count > 0)
            {
                var candidate = queuedVideoFrames.Peek();
                var frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                if (frameLead >= -staleThreshold)
                {
                    break;
                }

                _ = queuedVideoFrames.Dequeue();
                candidate.Dispose();
                lateDrops++;
            }

            if (queuedVideoFrames.Count == 0)
            {
                return new VideoPresenterSyncDecision(null, delay, lateDrops, 0);
            }

            // Check the next frame's lead relative to the audio clock.
            {
                var candidate = queuedVideoFrames.Peek();
                var frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);

                // Frame is early — wait for audio to catch up.
                // Use the actual lead as the delay, capped at audioLedMaxWaitMs
                // to stay responsive to cancellation.
                if (frameLead.TotalMilliseconds > 2.0)
                {
                    var waitMs = Math.Clamp(frameLead.TotalMilliseconds, delay.TotalMilliseconds, audioLedMaxWaitMs);
                    return new VideoPresenterSyncDecision(null, TimeSpan.FromMilliseconds(waitMs), lateDrops, 0);
                }

                // Frame is within tolerance — present it, coalescing any additional
                // frames that are also within tolerance.
                var selected = queuedVideoFrames.Dequeue();
                var coalescedDrops = 0;
                while (queuedVideoFrames.Count > 0)
                {
                    var next = queuedVideoFrames.Peek();
                    var nextLead = next.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                    if (nextLead.TotalMilliseconds > 2.0)
                    {
                        break;
                    }

                    selected.Dispose();
                    selected = queuedVideoFrames.Dequeue();
                    coalescedDrops++;
                }

                return new VideoPresenterSyncDecision(selected, delay, lateDrops, coalescedDrops);
            }
        }

        // Synced: clock-aligned presentation.
        var earlyTolerance = options.FrameEarlyTolerance < TimeSpan.Zero
            ? TimeSpan.Zero
            : options.FrameEarlyTolerance;
        var maxWait = options.MaxWait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(2) : options.MaxWait;

        // NOTE: every code path inside this block returns — the outer loop was structurally dead (N9).
        if (queuedVideoFrames.Count > 0)
        {
            var lateDropsSynced = 0;
            var candidate = queuedVideoFrames.Peek();
            var frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);

            if (frameLead < -staleThreshold)
            {
                _ = queuedVideoFrames.Dequeue();
                candidate.Dispose();
                lateDropsSynced++;

                while (queuedVideoFrames.Count > 0)
                {
                    candidate = queuedVideoFrames.Peek();
                    frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                    if (frameLead >= -staleThreshold)
                    {
                        break;
                    }

                    _ = queuedVideoFrames.Dequeue();
                    candidate.Dispose();
                    lateDropsSynced++;
                }

                if (queuedVideoFrames.Count == 0)
                {
                    return new VideoPresenterSyncDecision(null, delay, lateDropsSynced, 0);
                }

                candidate = queuedVideoFrames.Peek();
                frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                if (frameLead > earlyTolerance)
                {
                    var lateDelay = TimeSpan.FromMilliseconds(
                        Math.Clamp(frameLead.TotalMilliseconds, delay.TotalMilliseconds, maxWait.TotalMilliseconds));
                    return new VideoPresenterSyncDecision(null, lateDelay, lateDropsSynced, 0);
                }

                var recovered = queuedVideoFrames.Dequeue();
                var recoveredCoalesced = 0;
                while (queuedVideoFrames.Count > 0)
                {
                    var next = queuedVideoFrames.Peek();
                    var nextLead = next.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                    if (nextLead > earlyTolerance)
                    {
                        break;
                    }

                    recovered.Dispose();
                    recovered = queuedVideoFrames.Dequeue();
                    recoveredCoalesced++;
                }

                return new VideoPresenterSyncDecision(recovered, delay, lateDropsSynced, recoveredCoalesced);
            }

            if (frameLead > earlyTolerance)
            {
                var targetDelay = TimeSpan.FromMilliseconds(
                    Math.Clamp(frameLead.TotalMilliseconds, delay.TotalMilliseconds, maxWait.TotalMilliseconds));
                return new VideoPresenterSyncDecision(null, targetDelay, 0, 0);
            }

            var selected = queuedVideoFrames.Dequeue();
            var coalescedDrops = 0;
            while (queuedVideoFrames.Count > 0)
            {
                var next = queuedVideoFrames.Peek();
                var nextLead = next.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                if (nextLead > earlyTolerance)
                {
                    break;
                }

                selected.Dispose();
                selected = queuedVideoFrames.Dequeue();
                coalescedDrops++;
            }

            return new VideoPresenterSyncDecision(selected, delay, 0, coalescedDrops);
        }

        return new VideoPresenterSyncDecision(null, delay, 0, 0);
    }
}
