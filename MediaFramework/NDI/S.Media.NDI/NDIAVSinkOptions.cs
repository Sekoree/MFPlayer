using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// Configuration options for <see cref="NDIAVEndpoint"/>.
/// Follows the same options-record pattern as <c>NDISourceOptions</c> and <c>FFmpegDecoderOptions</c>.
/// </summary>
public sealed record NDIAVSinkOptions
{
    /// <summary>Target video format. <see langword="null"/> disables the video path.</summary>
    public VideoFormat? VideoTargetFormat { get; init; }

    /// <summary>Target audio format. <see langword="null"/> disables the audio path.</summary>
    public AudioFormat? AudioTargetFormat { get; init; }

    /// <summary>Quality/performance preset that controls pool sizes and queue depths.</summary>
    public NDIEndpointPreset Preset { get; init; } = NDIEndpointPreset.Balanced;

    /// <summary>Display name shown in NDI diagnostics. Defaults to "NDIAVEndpoint".</summary>
    public string? Name { get; init; }

    /// <summary>
    /// When <see langword="true"/>, selects a performance-optimised pixel format
    /// (UYVY422) over the default quality format (RGBA32).
    /// </summary>
    public bool PreferPerformanceOverQuality { get; init; }

    /// <summary>Number of pre-allocated video frame buffers. 0 = use preset default.</summary>
    public int VideoPoolCount { get; init; }

    /// <summary>Maximum number of video frames queued for sending. 0 = use preset default.</summary>
    public int VideoMaxPendingFrames { get; init; }

    /// <summary>Number of audio samples per send buffer. 0 = use default (512).</summary>
    public int AudioFramesPerBuffer { get; init; }

    /// <summary>Number of pre-allocated audio buffers. 0 = use preset default.</summary>
    public int AudioPoolCount { get; init; }

    /// <summary>Maximum number of audio buffers queued for sending. 0 = use preset default.</summary>
    public int AudioMaxPendingBuffers { get; init; }

    /// <summary>External audio resampler. <see langword="null"/> creates a built-in LinearResampler.</summary>
    public IAudioResampler? AudioResampler { get; init; }

    /// <summary>
    /// Enables clock-drift correction on the audio send path.
    /// <para/>
    /// <b>Note:</b> the drift corrector is queue-depth driven and is only meaningful
    /// when the NDI sender was created with <c>clockAudio:true</c> (SDK back-pressure).
    /// On the typical async/unclocked send path the pending queue stays near zero and
    /// the PI controller saturates at <c>+maxCorrection</c>, producing a permanent
    /// +0.5 % rate skew — leave this <see langword="false"/> for <c>clockAudio:false</c>.
    /// </summary>
    public bool EnableAudioDriftCorrection { get; init; }

    /// <summary>
    /// Threshold (in milliseconds) for treating a producer-PTS → cursor delta as a
    /// discontinuity (seek / reset) that should re-anchor the audio timeline.
    /// <para/>
    /// Default is 500 ms.  Steady-state file-level offsets (AAC priming, container
    /// edit-lists ~50–150 ms) stay absorbed by the sample-accurate cursor; only large
    /// jumps re-anchor.  Lower this (e.g. 100 ms) if your pipeline performs short seeks
    /// that must produce an immediate timecode realignment.
    /// </summary>
    public int AudioPtsDiscontinuityThresholdMs { get; init; } = 500;

    /// <summary>
    /// Threshold (in milliseconds) beyond which a lagging audio-timecode cursor is
    /// snapped forward to the latest observed video PTS (underrun recovery).
    /// <para/>
    /// The audio cursor advances at the delivered sample-rate; if the audio decoder
    /// briefly stalls (CPU spike, GC pause) the cursor will lag wall clock / video PTS.
    /// When the lag exceeds this threshold, the next audio buffer is stamped with the
    /// video PTS, preventing a transient stall from turning into a permanent A/V offset
    /// at the receiver.  The missed audio window becomes a short silence.
    /// <para/>
    /// Default is 80 ms.  Lower this to keep A/V tighter (more frequent mini-dropouts
    /// on stalls).  Higher values tolerate more stall before correcting but risk
    /// permanent residual lag if the stall settles just below the threshold.
    /// Set to 0 to disable underrun recovery entirely.
    /// </summary>
    public int AudioUnderrunRecoveryThresholdMs { get; init; } = 80;
}

