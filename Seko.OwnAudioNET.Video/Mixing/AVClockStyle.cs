namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// Selects how <see cref="AVMixer"/> advances its shared <see cref="OwnaudioNET.Synchronization.MasterClock"/>.
/// </summary>
public enum AVClockStyle
{
    /// <summary>
    /// Advances strictly by rendered audio buffer size each mix cycle.
    /// This preserves the mixer behavior prior to clock-style support.
    /// </summary>
    AudioDriven = 0,

    /// <summary>
    /// Advances by wall-clock elapsed time measured on the mix thread.
    /// </summary>
    Realtime = 1,

    /// <summary>
    /// Uses wall-clock advancement with bounded correction toward the expected
    /// audio-buffer cadence to reduce long-term drift without hard jumps.
    /// </summary>
    Hybrid = 2
}

