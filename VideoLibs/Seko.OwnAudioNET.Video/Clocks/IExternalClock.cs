namespace Seko.OwnAudioNET.Video.Clocks;

/// <summary>
/// Read-only external timeline clock that can be provided by live inputs (e.g. NDI timecodes).
/// </summary>
public interface IExternalClock
{
    /// <summary>Current external timeline position in seconds.</summary>
    double CurrentSeconds { get; }
}

