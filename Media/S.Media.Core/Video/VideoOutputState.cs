namespace S.Media.Core.Video;

public enum VideoOutputState
{
    Stopped = 0,
    Running = 1,

    /// <summary>
    /// The output is temporarily holding the last presented frame.
    /// Outputs that do not implement pausing return
    /// <see cref="S.Media.Core.Errors.MediaErrorCode.MediaInvalidOperation"/> on a
    /// <c>Pause()</c> call. Reserved for future per-output pause control in the mixer.
    /// </summary>
    Paused = 2,
}

