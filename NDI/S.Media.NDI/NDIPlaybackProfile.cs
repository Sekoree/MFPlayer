namespace S.Media.NDI;

/// <summary>
/// Bundles all output-side latency configuration derived from an <see cref="NDIEndpointPreset"/>.
/// <para>
/// Creating playback infrastructure for live NDI monitoring currently requires coordinating
/// many independent knobs: audio buffer size, PortAudio suggested latency, VSync mode,
/// video mixer live-mode, pre-buffer depths, and clock-origin reset.  This record derives
/// all of those from a single <see cref="NDIEndpointPreset"/>, giving the library consumer
/// one object to flow through setup code.
/// </para>
/// <example><code>
/// var preset  = NDIEndpointPreset.LowLatency;
/// var profile = NDIPlaybackProfile.For(preset);
/// var options = NDISourceOptions.ForPreset(preset, channels: 2);
///
/// avSource = await NDIAVChannel.OpenByNameAsync(name, options, ct);
///
/// output.Open(device, hwFmt, suggestedLatency: profile.AudioSuggestedLatency);
///
/// if (profile.AdaptiveVSync)
///     videoOutput.VsyncMode = VsyncMode.Adaptive;   // S.Media.SDL3
/// if (profile.ResetClockOrigin)
///     videoOutput.ResetClockOrigin();
///
/// avMixer.BypassVideoPtsScheduling = profile.BypassVideoPtsScheduling;
///
/// await Task.WhenAll(
///     avSource.WaitForAudioBufferAsync(profile.AudioPreBufferChunks, ct),
///     avSource.WaitForVideoBufferAsync(profile.VideoPreBufferFrames, ct));
/// </code></example>
/// </summary>
public sealed record NDIPlaybackProfile
{
    /// <summary>
    /// Samples per NDI audio capture call. Smaller values reduce capture-to-playback latency.
    /// Passed to <see cref="NDISourceOptions.AudioFramesPerCapture"/>.
    /// </summary>
    public int AudioFramesPerCapture { get; init; } = 1024;

    /// <summary>
    /// Suggested PortAudio output latency in seconds.
    /// 0 = let the driver / device choose (safest default).
    /// Passed to <c>PortAudioOutput.Open(suggestedLatency:)</c>.
    /// </summary>
    public double AudioSuggestedLatency { get; init; }

    /// <summary>
    /// Minimum audio chunks to pre-buffer before starting playback.
    /// Lower values reduce startup latency; higher values prevent initial underruns.
    /// Passed to <see cref="NDIAVChannel.WaitForAudioBufferAsync"/>.
    /// </summary>
    public int AudioPreBufferChunks { get; init; } = 3;

    /// <summary>
    /// Minimum video frames to pre-buffer before starting playback.
    /// Passed to <see cref="NDIAVChannel.WaitForVideoBufferAsync"/>.
    /// </summary>
    public int VideoPreBufferFrames { get; init; } = 2;

    /// <summary>
    /// When <see langword="true"/>, the router bypasses per-frame PTS scheduling for
    /// video push endpoints and simply forwards the newest available frame on every
    /// push tick.  Use only for live-monitor scenarios where frame-accurate pacing
    /// is not required — it trades PTS correctness for the lowest achievable
    /// presentation latency.  Disabled on every preset: callers opt in explicitly.
    /// Set on <c>IAVRouter.BypassVideoPtsScheduling</c>.
    /// </summary>
    public bool BypassVideoPtsScheduling { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the video output should use adaptive VSync
    /// (swap immediately when a frame is late, VSync when on time).
    /// Map to <c>VsyncMode.Adaptive</c> on the SDL3 video output.
    /// </summary>
    public bool AdaptiveVSync { get; init; }

    /// <summary>
    /// When <see langword="true"/>, call <c>SDL3VideoOutput.ResetClockOrigin()</c> after
    /// wiring the presentation clock so the first frame is never classified as "late".
    /// </summary>
    public bool ResetClockOrigin { get; init; }

    /// <summary>
    /// Whether the source-side should use low-latency polling (higher CPU, faster capture).
    /// Automatically set by <see cref="For"/>; also stored here so the consumer can pass it
    /// to <see cref="NDISourceOptions.LowLatency"/> without re-deriving.
    /// </summary>
    public bool LowLatencyPolling { get; init; }

    /// <summary>The <see cref="NDIEndpointPreset"/> this profile was derived from.</summary>
    public NDIEndpointPreset Preset { get; init; }

    /// <summary>
    /// Creates a playback profile for the given endpoint preset.
    /// All output-side knobs are pre-configured; the consumer just reads properties.
    /// </summary>
    public static NDIPlaybackProfile For(NDIEndpointPreset preset) => preset switch
    {
        NDIEndpointPreset.UltraLowLatency => new()
        {
            Preset                 = preset,
            AudioFramesPerCapture  = 128,
            AudioSuggestedLatency  = 128.0 / 48000.0,   // ~2.7 ms
            AudioPreBufferChunks   = 1,
            VideoPreBufferFrames   = 1,
            BypassVideoPtsScheduling = false,
            AdaptiveVSync          = true,
            ResetClockOrigin       = true,
            LowLatencyPolling      = true,
        },
        NDIEndpointPreset.LowLatency => new()
        {
            Preset                 = preset,
            AudioFramesPerCapture  = 256,
            AudioSuggestedLatency  = 256.0 / 48000.0,   // ~5.3 ms
            AudioPreBufferChunks   = 1,
            VideoPreBufferFrames   = 1,
            BypassVideoPtsScheduling = false,
            AdaptiveVSync          = true,
            ResetClockOrigin       = true,
            LowLatencyPolling      = true,
        },
        NDIEndpointPreset.Safe => new()
        {
            Preset                 = preset,
            AudioFramesPerCapture  = 1024,
            AudioSuggestedLatency  = 0,
            AudioPreBufferChunks   = 4,
            VideoPreBufferFrames   = 3,
            BypassVideoPtsScheduling          = false,
            AdaptiveVSync          = false,
            ResetClockOrigin       = false,
            LowLatencyPolling      = false,
        },
        _ /* Balanced */ => new()
        {
            Preset                 = preset,
            AudioFramesPerCapture  = 1024,
            AudioSuggestedLatency  = 0,
            AudioPreBufferChunks   = 3,
            VideoPreBufferFrames   = 2,
            BypassVideoPtsScheduling          = false,
            AdaptiveVSync          = false,
            ResetClockOrigin       = false,
            LowLatencyPolling      = false,
        },
    };
}

