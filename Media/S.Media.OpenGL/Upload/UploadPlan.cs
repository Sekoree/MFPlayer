using S.Media.Core.Video;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Upload;

public readonly record struct UploadPlan(
    VideoPixelFormat PixelFormat,
    OpenGLCloneMode PreferredPath,
    bool RequiresGpuConversion);

