using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// Configuration options for <see cref="NDIAVSink"/>.
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

    /// <summary>Display name shown in NDI diagnostics. Defaults to "NDIAVSink".</summary>
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

    /// <summary>Enables clock-drift correction on the audio send path.</summary>
    public bool EnableAudioDriftCorrection { get; init; }
}

