namespace S.Media.Core.Mixing;

/// <summary>
/// Consumer-facing configuration for the audio/video mixer runtime.
/// Drift correction and sync policy internals are managed automatically.
/// </summary>
public sealed class AudioVideoMixerConfig
{
    /// <summary>Number of audio frames to read per pump cycle. Default: 480.</summary>
    public int AudioReadFrames { get; set; } = 480;

    /// <summary>Number of channels expected from audio sources. Default: 2 (stereo).</summary>
    public int SourceChannelCount { get; set; } = 2;

    /// <summary>Sample rate of the audio output. Default: 48000 Hz.</summary>
    public int OutputSampleRate { get; set; } = 48_000;

    /// <summary>
    /// Channel routing map: each element maps an output channel index to a source channel index.
    /// Default: [0, 1] (stereo passthrough).
    /// </summary>
    public int[] RouteMap { get; set; } = [0, 1];

    /// <summary>Maximum number of queued video frames before trimming. Default: 3.</summary>
    public int VideoQueueCapacity { get; set; } = 3;

    /// <summary>
    /// When true, video presentation ticks on the caller thread (required for some GL views).
    /// When false, presentation runs on a background thread.
    /// </summary>
    public bool PresentOnCallerThread { get; set; }

    internal AudioVideoMixerRuntimeOptions ToRuntimeOptions() => new()
    {
        AudioReadFrames = AudioReadFrames,
        SourceChannelCount = SourceChannelCount,
        OutputSampleRate = OutputSampleRate,
        RouteMap = RouteMap,
        VideoQueueCapacity = VideoQueueCapacity,
        PresentOnCallerThread = PresentOnCallerThread,
    };
}

