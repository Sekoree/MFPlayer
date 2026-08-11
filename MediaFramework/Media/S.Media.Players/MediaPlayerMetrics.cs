using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Players;

/// <summary>One-call operational snapshot from <see cref="MediaPlayer.GetMetrics"/>.</summary>
public sealed record MediaPlayerMetrics(
    MediaClockMetricsSnapshot Clock,
    VideoPlayerMetricsSnapshot? Video,
    AudioRouterMetricsSnapshot? AudioRouter,
    IReadOnlyList<VideoOutputPumpMetricsEntry> VideoOutputs,
    IReadOnlyList<AudioOutputPumpMetricsEntry> AudioOutputs,
    PortAudioMetricsSnapshot? PortAudio,
    NDIIngestMetricsSnapshot? NDI);

/// <param name="MasterRegressions">
/// How many times the attached master broke its per-epoch monotonic contract and was clamped, and the
/// worst single regression. <strong>Any non-zero value names a fault in a clock BELOW this one</strong>
/// (or a read torn across its re-anchor) - the clamp keeps the playhead safe, but it also used to hide
/// the breach, so the symptom surfaced layers away from the cause. This is that breach, at its source.
/// </param>
public sealed record MediaClockMetricsSnapshot(
    TimeSpan CurrentPosition,
    string MasterTypeName,
    long MasterRegressions = 0,
    TimeSpan WorstMasterRegression = default);

public sealed record VideoPlayerMetricsSnapshot(
    long DecodedCount,
    long DisplayedCount,
    long DroppedLate,
    long DroppedDrain,
    TimingSnapshot DecodeTiming,
    int QueueDepth,
    int QueueCapacity);

public sealed record AudioRouterMetricsSnapshot(
    long ChunksProduced,
    long TotalEnqueued,
    long TotalProcessed,
    long TotalDropped,
    int OutputCount,
    TimingSnapshot MixTiming);

public sealed record VideoOutputPumpMetricsEntry(
    string OutputId,
    VideoOutputPumpMetrics Metrics);

public sealed record AudioOutputPumpMetricsEntry(
    string OutputId,
    AudioRouter.OutputPumpStats Stats);

public sealed record PortAudioMetricsSnapshot(
    long PlayedSamples,
    long UnderrunSamples,
    long DroppedSamples);

public sealed record NDIIngestMetricsSnapshot(
    long AudioOverflowFloats,
    long VideoOverflowFrames);
