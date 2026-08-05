using Microsoft.Extensions.Logging;
using S.Media.Compositor;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Present.SDL3;

/// <summary>
/// Selects the production desktop compositor used by show hosts.
/// </summary>
/// <remarks>
/// The SDL/OpenGL capability probe and its CPU fallback used to live in HaPlay. Keeping the policy in
/// the framework bridge prevents a host from accidentally constructing a CPU-only <c>ShowSession</c>
/// and silently losing warp meshes and compositor-surface layers.
/// </remarks>
public static class SDL3CompositionCompositorFactory
{
    private static readonly ILogger Trace =
        MediaDiagnostics.CreateLogger("S.Media.Present.SDL3.CompositionCompositorFactory");

    /// <summary>
    /// Creates the best available compositor. <paramref name="requestedBackend"/> accepts
    /// <c>cpu</c>, <c>gl</c>, or <c>gpu</c>; null/blank means automatic GL with CPU fallback.
    /// </summary>
    public static DesktopCompositionCompositor Create(
        VideoFormat canvasFormat,
        string? requestedBackend = null,
        string? ownerName = null)
    {
        ownerName = string.IsNullOrWhiteSpace(ownerName) ? "show" : ownerName.Trim();
        requestedBackend = requestedBackend?.Trim();

        if (string.Equals(requestedBackend, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            Trace.LogInformation(
                "{Owner}: composition using CPU compositor (explicit override)", ownerName);
            return Cpu(canvasFormat);
        }

        if (SDL3GLVideoCompositor.TryProbe(canvasFormat.PixelFormat, out var glError))
        {
            var gpu = new SDL3GLVideoCompositor(canvasFormat);
            Trace.LogInformation("{Owner}: composition using OpenGL compositor", ownerName);
            return new DesktopCompositionCompositor(
                gpu,
                RequiresBgraLayerConversion: false,
                BackendName: "OpenGL",
                DisposeOnDriverThread: gpu.DisposeOnOwnerThread,
                SupportsWarpMesh: true,
                SupportsSurfaceLayers: true);
        }

        var explicitlyRequested =
            string.Equals(requestedBackend, "gl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedBackend, "gpu", StringComparison.OrdinalIgnoreCase);

        if (explicitlyRequested)
        {
            Trace.LogWarning(
                "{Owner}: OpenGL compositor requested but unavailable: {Error}; falling back to CPU",
                ownerName,
                glError);
        }
        else
        {
            Trace.LogInformation(
                "{Owner}: OpenGL compositor unavailable: {Error}; using CPU compositor",
                ownerName,
                glError);
        }

        return Cpu(canvasFormat);
    }

    private static DesktopCompositionCompositor Cpu(VideoFormat canvasFormat) =>
        new(
            new CpuVideoCompositor(canvasFormat),
            RequiresBgraLayerConversion: true,
            BackendName: "CPU",
            DisposeOnDriverThread: null,
            SupportsWarpMesh: false,
            SupportsSurfaceLayers: false);
}

/// <summary>Compositor plus the host policy needed by a show-session composition runtime.</summary>
public sealed record DesktopCompositionCompositor(
    IVideoCompositor Compositor,
    bool RequiresBgraLayerConversion,
    string BackendName,
    Action? DisposeOnDriverThread,
    bool SupportsWarpMesh,
    bool SupportsSurfaceLayers);
