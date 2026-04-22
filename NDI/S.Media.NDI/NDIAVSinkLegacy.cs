using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Legacy name for <see cref="NDIAVEndpoint"/>. Kept as an
/// <see cref="ObsoleteAttribute"/> forwarder per the obsoletion policy
/// (checklist §0.4.3) for one release; will be removed thereafter.
/// </summary>
[Obsolete("Renamed to NDIAVEndpoint. This type-forwarder will be removed in the next release.", error: false)]
public sealed class NDIAVSink : NDIAVEndpoint
{
    /// <inheritdoc cref="NDIAVEndpoint(NDISender, VideoFormat?, AudioFormat?, NDIEndpointPreset, string?, bool, int, int, int, int, int, IAudioResampler?, bool, int, int)"/>
    public NDIAVSink(
        NDISender         sender,
        VideoFormat?      videoTargetFormat            = null,
        AudioFormat?      audioTargetFormat            = null,
        NDIEndpointPreset preset                       = NDIEndpointPreset.Balanced,
        string?           name                         = null,
        bool              preferPerformanceOverQuality = false,
        int               videoPoolCount               = 0,
        int               videoMaxPendingFrames        = 0,
        int               audioFramesPerBuffer         = 1024,
        int               audioPoolCount               = 0,
        int               audioMaxPendingBuffers       = 0,
        IAudioResampler?  audioResampler               = null,
        bool              enableAudioDriftCorrection   = false,
        int               audioPtsDiscontinuityThresholdMs = 500,
        int               audioUnderrunRecoveryThresholdMs = 80)
        : base(sender, videoTargetFormat, audioTargetFormat, preset, name,
               preferPerformanceOverQuality, videoPoolCount, videoMaxPendingFrames,
               audioFramesPerBuffer, audioPoolCount, audioMaxPendingBuffers,
               audioResampler, enableAudioDriftCorrection,
               audioPtsDiscontinuityThresholdMs, audioUnderrunRecoveryThresholdMs)
    { }

    /// <inheritdoc cref="NDIAVEndpoint(NDISender, NDIAVSinkOptions?)"/>
    public NDIAVSink(NDISender sender, NDIAVSinkOptions? options) : base(sender, options) { }
}

