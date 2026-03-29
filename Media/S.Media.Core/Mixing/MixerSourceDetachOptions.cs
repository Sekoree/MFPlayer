namespace S.Media.Core.Mixing;

/// <summary>
/// Replaced by optional <c>stopOnDetach</c> / <c>disposeOnDetach</c> bool parameters
/// on <see cref="IAVMixer.RemoveAudioSource"/> and <see cref="IAVMixer.RemoveVideoSource"/>.
/// </summary>
[Obsolete("Use the stopOnDetach and disposeOnDetach parameters on RemoveAudioSource / RemoveVideoSource directly. This type will be removed in a future version.")]
public sealed record MixerSourceDetachOptions
{
    public bool StopOnDetach    { get; init; }
    public bool DisposeOnDetach { get; init; }
}
