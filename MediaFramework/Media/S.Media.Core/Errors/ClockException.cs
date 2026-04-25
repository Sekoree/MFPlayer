namespace S.Media.Core.Errors;

/// <summary>
/// Thrown when an <c>IMediaClock</c> implementation cannot satisfy a contract
/// (e.g. reading <c>Position</c> on a disposed clock, overriding with an
/// incompatible priority, timer failure).
/// </summary>
/// <remarks>
/// Closes review finding <b>EL1</b>: replaces <see cref="System.InvalidOperationException"/>
/// at clock API boundaries.
/// </remarks>
public class ClockException : MediaException
{
    public ClockException() { }
    public ClockException(string message) : base(message) { }
    public ClockException(string message, Exception inner) : base(message, inner) { }
}

