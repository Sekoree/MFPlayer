using S.Media.Core.Video;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Upload;

public sealed class OpenGLUploadPlanner
{
    private readonly Lock _gate = new();
    private OpenGLCapabilitySnapshot _capabilities;

    public int UpdateCapabilities(in OpenGLCapabilitySnapshot capabilities)
    {
        lock (_gate)
        {
            _capabilities = capabilities with
            {
                MaxTextureSize = Math.Max(1, capabilities.MaxTextureSize),
            };
        }

        return 0;
    }

    public UploadPlan CreatePlan(VideoFrame frame)
    {
        OpenGLCapabilitySnapshot snapshot;
        lock (_gate)
        {
            snapshot = _capabilities;
        }

        var preferredPath = snapshot.SupportsTextureSharing
            ? OpenGLCloneMode.SharedTexture
            : snapshot.SupportsFboBlit
                ? OpenGLCloneMode.SharedFboBlit
                : OpenGLCloneMode.CopyFallback;

        var requiresGpuConversion = frame.PixelFormat is not (VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32);

        return new UploadPlan(
            PixelFormat: frame.PixelFormat,
            PreferredPath: preferredPath,
            RequiresGpuConversion: requiresGpuConversion);
    }

    public bool Supports(VideoPixelFormat pixelFormat)
    {
        return pixelFormat != VideoPixelFormat.Unknown;
    }
}

