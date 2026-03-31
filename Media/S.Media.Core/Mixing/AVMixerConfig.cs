using System.Collections.ObjectModel;
using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

/// <summary>
/// Consumer-facing configuration for the audio/video mixer.
/// Use the static factory methods for the most common setups.
/// </summary>
public sealed class AVMixerConfig
{
    // ── Sync ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Audio/video synchronisation mode applied when <see cref="AVMixer.StartPlayback"/> is called.
    /// <para>
    /// When <see langword="null"/> (the default) the mixer keeps whatever mode was last set via
    /// <see cref="AVMixer.SetSyncMode"/> — useful when you configure the mixer separately
    /// before calling <c>StartPlayback</c>.  Set an explicit value here to override that setting
    /// from within the config object alone.
    /// </para>
    /// Default: <see langword="null"/> (inherit from mixer).
    /// </summary>
    public AVSyncMode? SyncMode { get; init; } = null;

    // ── Audio ────────────────────────────────────────────────────────────────

    /// <summary>Number of channels in the source audio stream.</summary>
    public int SourceChannelCount { get; init; } = 2;

    /// <summary>
    /// Maps output channel indices to source channel indices.
    /// Length determines the output channel count.
    /// </summary>
    public IReadOnlyList<int> RouteMap { get; init; } = [0, 1];

    /// <summary>
    /// Target output sample rate. <c>0</c> = use the primary source's sample rate.
    /// </summary>
    public int OutputSampleRate { get; init; }

    /// <summary>
    /// Number of audio frames the mixer reads and mixes per batch.
    /// Smaller values reduce audio latency; larger values improve CPU efficiency.
    /// For realtime sources (e.g. NDI) use values in the 240–480 range to match typical network packet sizes.
    /// Default: 1024.
    /// </summary>
    public int AudioReadFrames { get; init; } = 1024;

    // ── Video ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Capacity of the internal video <em>decode</em> buffer — the queue that the decode thread
    /// fills ahead of the presentation thread.  A larger value absorbs decoder jitter and reduces
    /// video stutter at the cost of a small amount of additional memory.
    /// <para>
    /// At 25 fps each frame is ~40 ms, so the default of <c>32</c> provides roughly 1.3 s of
    /// decoded-frame headroom.  Lower this on memory-constrained devices.
    /// </para>
    /// Default: 32.
    /// </summary>
    public int VideoDecodeQueueCapacity { get; init; } = 32;

    /// <summary>
    /// Capacity of the per-output presentation queue used when
    /// <see cref="PresentationHostPolicy"/> is <see cref="VideoDispatchPolicy.BackgroundWorker"/>.
    /// Ignored for <see cref="VideoDispatchPolicy.DirectThread"/>.
    /// Default: 8.
    /// </summary>
    public int VideoOutputQueueCapacity { get; init; } = 8;

    /// <summary>
    /// Per-output video queue capacity overrides, keyed by <see cref="IVideoOutput.Id"/>.
    /// <para>
    /// Use object-initializer syntax to set overrides at construction time:
    /// <code>new AVMixerConfig { VideoOutputQueueCapacityOverrides = new Dictionary&lt;Guid, int&gt; { [id] = 16 } }</code>
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<Guid, int> VideoOutputQueueCapacityOverrides { get; init; }
        = ReadOnlyDictionary<Guid, int>.Empty;

    /// <summary>Returns the effective queue capacity for a given output.</summary>
    public int GetVideoOutputQueueCapacity(Guid outputId) =>
        VideoOutputQueueCapacityOverrides.TryGetValue(outputId, out var cap) ? cap : VideoOutputQueueCapacity;

    /// <summary>
    /// Controls how video presentation is threaded by the mixer.
    /// Default: <see cref="VideoDispatchPolicy.DirectThread"/>.
    /// </summary>
    public VideoDispatchPolicy PresentationHostPolicy { get; init; } =
        VideoDispatchPolicy.DirectThread;

    /// <summary>
    /// Timestamp monotonic normalization used when the mixer schedules video frames.
    /// Default: <see cref="VideoTimestampMode.RebaseOnDiscontinuity"/>.
    /// </summary>
    public VideoTimestampMode TimestampMode { get; init; } =
        VideoTimestampMode.RebaseOnDiscontinuity;

    /// <summary>Discontinuity threshold for <see cref="TimestampMode"/>. Default: 50 ms.</summary>
    public TimeSpan DiscontinuityThreshold { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Frames older than this relative to the current clock position will be dropped.
    /// Default: 200 ms.
    /// </summary>
    public TimeSpan OutputStaleFrameThreshold { get; init; } = TimeSpan.FromMilliseconds(200);

    // ── Sync policy ───────────────────────────────────────────────────────────

    /// <summary>
    /// Overrides the video presenter sync-policy options used by the mixer's presentation loop.
    /// When <see langword="null"/> (default), the mixer uses built-in defaults:
    /// stale-drop = 200 ms, maxWait = 50 ms, earlyTolerance = 2 ms, minDelay = 1 ms.
    /// </summary>
    public VideoSyncOptions? PresenterSyncOptions { get; init; }

    // ── Resampling ────────────────────────────────────────────────────────────

    /// <summary>
    /// Optional factory for creating per-source resamplers when a source's sample rate
    /// differs from <see cref="OutputSampleRate"/>.
    /// Parameters: <c>(sourceSampleRate, targetSampleRate)</c> → <see cref="IAudioResampler"/>.
    /// <para>
    /// When <see langword="null"/> (default) the mixer assumes all sources already produce
    /// samples at <see cref="OutputSampleRate"/> and no resampling is performed.
    /// </para>
    /// </summary>
    public Func<int, int, IAudioResampler>? ResamplerFactory { get; init; }

    // ── Factories ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stereo config for a stereo source: routes channels 0→0 and 1→1.
    /// </summary>
    public static AVMixerConfig ForStereo() => new()
    {
        SourceChannelCount = 2,
        RouteMap = [0, 1],
    };

    /// <summary>
    /// Down- or up-mixes any source to stereo output.
    /// Mono sources are duplicated to both channels; multi-channel sources use the front pair.
    /// Zero or negative <paramref name="sourceChannels"/> is clamped to 1.
    /// </summary>
    /// <param name="sourceChannels">Number of channels in the source stream.</param>
    /// <param name="syncMode">
    /// Optional sync mode to embed in the config.  When <see langword="null"/> (the default)
    /// the mixer's current <see cref="IAVMixer.SyncMode"/> is preserved unchanged.
    /// </param>
    public static AVMixerConfig ForSourceToStereo(int sourceChannels,
        AVSyncMode? syncMode = null)
    {
        var channels = Math.Max(1, sourceChannels);
        var routeMap = channels == 1 ? new[] { 0, 0 } : new[] { 0, 1 };
        return new AVMixerConfig
        {
            SourceChannelCount = channels,
            RouteMap = routeMap,
            SyncMode = syncMode,
        };
    }

    /// <summary>
    /// Passthrough config: output channel count equals source channel count, mapped 1-to-1.
    /// Zero or negative <paramref name="channels"/> is clamped to 1.
    /// </summary>
    public static AVMixerConfig ForPassthrough(int channels)
    {
        var count = Math.Max(1, channels);
        var map = new int[count];
        for (var i = 0; i < count; i++) map[i] = i;
        return new AVMixerConfig
        {
            SourceChannelCount = count,
            RouteMap = map,
        };
    }
}
