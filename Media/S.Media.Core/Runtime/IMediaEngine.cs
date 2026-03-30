namespace S.Media.Core.Runtime;

/// <summary>
/// Minimal lifecycle contract shared by all S.Media.* engine types
/// (<c>PortAudioEngine</c>, <c>MIDIEngine</c>, <c>NDIEngine</c>, …).
/// </summary>
/// <remarks>
/// Engines follow an <c>Initialize → use → Terminate → Dispose</c> lifecycle.
/// <see cref="Terminate"/> performs an ordered shutdown of all managed resources created by the
/// engine (devices, streams, ports) and returns the engine to an uninitialized state.
/// <see cref="IDisposable.Dispose"/> is a safety net that calls <see cref="Terminate"/> if the
/// engine is still initialized at GC / scope exit.
/// </remarks>
public interface IMediaEngine : IDisposable
{
    /// <summary>
    /// <see langword="true"/> while the engine has been successfully initialized and has not yet
    /// been terminated.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Closes all managed resources created by this engine and returns it to an uninitialized
    /// state. Safe to call multiple times; subsequent calls after the first return
    /// <see cref="S.Media.Core.Errors.MediaResult.Success"/> without side effects.
    /// </summary>
    int Terminate();
}

