using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.OpenGL.SDL3;

public sealed class SDL3ShaderPipeline : IDisposable
{
    private readonly Lock _gate = new();
    private bool _initialized;
    private bool _disposed;

    public int EnsureInitialized()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.SDL3EmbedTeardownFailed;
            }

            _initialized = true;
            return MediaResult.Success;
        }
    }

    public int Upload(VideoFrame frame)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            return frame.ValidateForPush();
        }
    }

    public int Draw()
    {
        lock (_gate)
        {
            return _initialized ? MediaResult.Success : (int)MediaErrorCode.SDL3EmbedNotInitialized;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _initialized = false;
            _disposed = true;
        }
    }
}

