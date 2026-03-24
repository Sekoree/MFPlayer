namespace S.Media.Core.Errors;

public sealed class PlaybackException : MediaException
{
    public PlaybackException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(errorCode, message, correlationId, innerException)
    {
    }
}

public sealed class DecodingException : MediaException
{
    public DecodingException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(errorCode, message, correlationId, innerException)
    {
    }
}

public sealed class MixingException : MediaException
{
    public MixingException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(errorCode, message, correlationId, innerException)
    {
    }
}

public sealed class OutputException : MediaException
{
    public OutputException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(errorCode, message, correlationId, innerException)
    {
    }
}

public sealed class NDIException : MediaException
{
    public NDIException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(errorCode, message, correlationId, innerException)
    {
    }
}

