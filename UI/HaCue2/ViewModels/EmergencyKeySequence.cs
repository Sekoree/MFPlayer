using System.Diagnostics;

namespace HaCue2.ViewModels;

public enum EscapeAction
{
    Stop,
    Panic,
}

/// <summary>Turns one Escape into Stop and a deliberate second Escape into Panic.</summary>
public sealed class EmergencyKeySequence(
    TimeSpan? panicWindow = null,
    Func<long>? timestamp = null)
{
    public static readonly TimeSpan DefaultPanicWindow = TimeSpan.FromMilliseconds(700);

    private readonly TimeSpan _panicWindow = panicWindow ?? DefaultPanicWindow;
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private long _lastEscape;

    public EscapeAction Press()
    {
        var now = _timestamp();
        if (_lastEscape != 0 && Stopwatch.GetElapsedTime(_lastEscape, now) <= _panicWindow)
        {
            _lastEscape = 0;
            return EscapeAction.Panic;
        }

        _lastEscape = now;
        return EscapeAction.Stop;
    }
}
