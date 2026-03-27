namespace S.Media.Core.Mixing;

public sealed record MixerSourceDetachOptions
{
    public bool StopOnDetach { get; init; }

    public bool DisposeOnDetach { get; init; }
}
