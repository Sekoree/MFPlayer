namespace S.Media.Core.Errors;

/// <summary>Thrown when a buffer underrun or overflow is detected and cannot be silently recovered.</summary>
public sealed class BufferException : MediaException
{
    public int FramesAffected { get; }

    public BufferException(string message, int framesAffected = 0)
        : base(message) => FramesAffected = framesAffected;

    public BufferException(string message, Exception inner, int framesAffected = 0)
        : base(message, inner) => FramesAffected = framesAffected;
}

