using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

internal readonly record struct VideoPresenterSyncDecision(
    VideoFrame? Frame,
    TimeSpan Delay,
    int LateDrops,
    int CoalescedDrops);
