using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.OpenGL.Upload;

public sealed class OpenGLTextureUploader
{
    private readonly Func<VideoFrame, UploadPlan, int>? _uploadAction;
    private readonly Lock _gate = new();
    private long _lastUploadGeneration;

    public OpenGLTextureUploader(Func<VideoFrame, UploadPlan, int>? uploadAction = null)
    {
        _uploadAction = uploadAction;
    }

    public long LastUploadGeneration
    {
        get
        {
            lock (_gate)
            {
                return _lastUploadGeneration;
            }
        }
    }

    public int Upload(VideoFrame frame, UploadPlan plan)
    {
        var frameValidation = frame.ValidateForPush();
        if (frameValidation != MediaResult.Success)
        {
            return frameValidation;
        }

        if (plan.PixelFormat == VideoPixelFormat.Unknown)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (frame.PixelFormat != plan.PixelFormat)
        {
            return (int)MediaErrorCode.OpenGLClonePixelFormatIncompatible;
        }

        var uploadCode = _uploadAction?.Invoke(frame, plan) ?? MediaResult.Success;
        if (uploadCode != MediaResult.Success)
        {
            return uploadCode;
        }

        lock (_gate)
        {
            _lastUploadGeneration++;
        }

        return MediaResult.Success;
    }

    public int Reset()
    {
        lock (_gate)
        {
            _lastUploadGeneration = 0;
        }

        return MediaResult.Success;
    }
}
