using S.Media.Core.Video.Effects;

namespace S.Media.Compositor;

/// <summary>
/// Capability: the compositor can answer, per effect descriptor, whether it has a real
/// implementation for that effect on its backend - the preflight seam for F-14 (2026-08-14 review).
/// </summary>
/// <remarks>
/// <para>
/// The effect contract allows a GPU-only effect (<see cref="VideoLayerEffectDescriptor.CpuKernelFactory"/>
/// = null), and <see cref="CpuVideoCompositor"/> skips such effects by contract rather than failing the
/// composite. That degradation must never be SILENT: a plugin effect disappearing exactly when GPU
/// init failed is the moment an operator is already diagnosing degraded output. This interface lets a
/// host ask BEFORE (or while) compositing which active effects the selected backend actually renders,
/// and surface the answer in output health.
/// </para>
/// <para>
/// A compositor that does not implement this interface is assumed to support every effect - the GLSL
/// body is the mandatory half of the descriptor, so GL-backed compositors always render it. Query
/// through <see cref="VideoCompositorEffectSupport.Supports"/> rather than casting, so that default
/// stays in one place.
/// </para>
/// </remarks>
public interface IEffectCapabilityVideoCompositor
{
    /// <summary>True when this compositor renders <paramref name="descriptor"/> rather than
    /// degrading it to pass-through.</summary>
    bool SupportsEffect(VideoLayerEffectDescriptor descriptor);
}

/// <summary>Preflight helpers over <see cref="IEffectCapabilityVideoCompositor"/>.</summary>
public static class VideoCompositorEffectSupport
{
    /// <summary>Whether <paramref name="compositor"/> renders <paramref name="descriptor"/>.
    /// Compositors without the capability interface are assumed to support every effect.</summary>
    public static bool Supports(IVideoCompositor compositor, VideoLayerEffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(descriptor);
        return compositor is not IEffectCapabilityVideoCompositor caps || caps.SupportsEffect(descriptor);
    }
}
