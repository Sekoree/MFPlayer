using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

internal static class VideoPresenterSyncPolicy
{
    public static VideoPresenterSyncDecision SelectNextFrame(
        Queue<VideoFrame> queuedVideoFrames,
        AudioVideoSyncMode syncMode,
        double clockSeconds,
        in VideoPresenterSyncPolicyOptions options)
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

        if (syncMode == AudioVideoSyncMode.Stable)
        {
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

            return new VideoPresenterSyncDecision(selected, delay, 0, coalescedDrops);
        }

        var earlyTolerance = options.FrameEarlyTolerance < TimeSpan.Zero
            ? TimeSpan.Zero
            : options.FrameEarlyTolerance;

        while (queuedVideoFrames.Count > 0)
        {
            var lateDrops = 0;
            var candidate = queuedVideoFrames.Peek();
            var frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
            if (frameLead < -staleThreshold)
            {
                _ = queuedVideoFrames.Dequeue();
                candidate.Dispose();
                lateDrops++;

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
                    lateDrops++;
                }

                if (queuedVideoFrames.Count == 0)
                {
                    return new VideoPresenterSyncDecision(null, delay, lateDrops, 0);
                }

                candidate = queuedVideoFrames.Peek();
                frameLead = candidate.PresentationTime - TimeSpan.FromSeconds(clockSeconds);
                if (frameLead > earlyTolerance)
                {
                    var lateWait = syncMode == AudioVideoSyncMode.StrictAv
                        ? (options.StrictMaxWait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(3) : options.StrictMaxWait)
                        : (options.HybridMaxWait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(2) : options.HybridMaxWait);
                    var lateDelay = TimeSpan.FromMilliseconds(Math.Clamp(frameLead.TotalMilliseconds, delay.TotalMilliseconds, lateWait.TotalMilliseconds));
                    return new VideoPresenterSyncDecision(null, lateDelay, lateDrops, 0);
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

                return new VideoPresenterSyncDecision(recovered, delay, lateDrops, recoveredCoalesced);
            }

            if (frameLead > earlyTolerance)
            {
                var maxWait = syncMode == AudioVideoSyncMode.StrictAv
                    ? (options.StrictMaxWait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(3) : options.StrictMaxWait)
                    : (options.HybridMaxWait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(2) : options.HybridMaxWait);
                var targetDelay = TimeSpan.FromMilliseconds(Math.Clamp(frameLead.TotalMilliseconds, delay.TotalMilliseconds, maxWait.TotalMilliseconds));
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

