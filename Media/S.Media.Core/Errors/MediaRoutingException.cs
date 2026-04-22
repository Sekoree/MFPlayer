namespace S.Media.Core.Errors;

/// <summary>
/// Thrown when <c>AVRouter</c> routing operations fail: incompatible input/endpoint
/// pair, registering the same id twice, creating a route on a disposed router, etc.
/// </summary>
/// <remarks>
/// Closes review finding <b>EL1</b>: replaces <see cref="System.InvalidOperationException"/>
/// at public router API boundaries (<c>CreateRoute</c>, <c>RegisterEndpoint</c>,
/// <c>SetClock</c>, …). Checklist item §3.21.
/// </remarks>
public class MediaRoutingException : MediaException
{
    public MediaRoutingException() { }
    public MediaRoutingException(string message) : base(message) { }
    public MediaRoutingException(string message, Exception inner) : base(message, inner) { }
}

