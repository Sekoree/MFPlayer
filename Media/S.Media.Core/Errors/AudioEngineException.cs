namespace S.Media.Core.Errors;

/// <summary>Thrown when the native audio engine (e.g. PortAudio) returns an error code.</summary>
public sealed class AudioEngineException : MediaException
{
    /// <summary>The raw native error code, if available.</summary>
    public int NativeErrorCode { get; }

    public AudioEngineException(string message, int nativeErrorCode = 0)
        : base(message) => NativeErrorCode = nativeErrorCode;

    public AudioEngineException(string message, Exception inner, int nativeErrorCode = 0)
        : base(message, inner) => NativeErrorCode = nativeErrorCode;
}

