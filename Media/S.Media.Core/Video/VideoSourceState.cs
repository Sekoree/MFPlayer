namespace S.Media.Core.Video;

public enum VideoSourceState
{
    Stopped     = 0,
    Running     = 1,
    /// <summary>Source has consumed all available frames. Distinct from <see cref="Stopped"/> (which is caller-initiated).</summary>
    EndOfStream = 2,
}
