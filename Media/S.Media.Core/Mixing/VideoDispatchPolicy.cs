namespace S.Media.Core.Mixing;

/// <summary>
/// Controls how video presentation work is hosted by the mixer runtime.
/// </summary>
public enum VideoDispatchPolicy
{
    /// <summary>
    /// Mixer presenter pushes frames directly on its own presentation thread.
    /// </summary>
    DirectThread = 0,

    /// <summary>
    /// Mixer routes frames through per-output background workers,
    /// isolating blocking outputs from the presentation thread.
    /// </summary>
    BackgroundWorker = 1,
}
