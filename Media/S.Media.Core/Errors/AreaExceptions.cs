namespace S.Media.Core.Errors;

public sealed class DecodingException : MediaException
{
    public DecodingException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(errorCode, message, correlationId, innerException)
    {
    }
}
