namespace S.Media.Core.Effects;

/// <summary>How an effect parameter should be presented and interpolated by an authoring host.</summary>
public enum EffectParameterScale
{
    Linear,
    Decibels,
    Percentage,
}

/// <summary>Stable authoring metadata for one scalar effect parameter.</summary>
/// <param name="Id">Stable ID within the effect type; never a display label or CLR member name.</param>
/// <param name="DisplayName">Localized/user-facing fallback label.</param>
/// <param name="Minimum">Smallest accepted authored value.</param>
/// <param name="Maximum">Largest accepted authored value.</param>
/// <param name="Default">Value used when a project/config omits the parameter.</param>
/// <param name="Unit">Short unit label such as dB or %.</param>
/// <param name="Scale">Presentation/interpolation scale.</param>
/// <param name="SupportsAutomation">Whether the implementation accepts live updates.</param>
public sealed record EffectParameterDescriptor(
    string Id,
    string DisplayName,
    float Minimum,
    float Maximum,
    float Default,
    string Unit = "",
    EffectParameterScale Scale = EffectParameterScale.Linear,
    bool SupportsAutomation = true)
{
    /// <summary>Brings a control value into range. Non-finite input resolves toward the nearest BOUND, not
    /// to <see cref="Default"/>: for a gain parameter, −inf means silence, and answering it with the
    /// default (0 dB) turned an explicit mute into FULL level - the worst possible direction for an audio
    /// failure. NaN has no direction and is the only case that can fall back to the default.</summary>
    public float Clamp(float value) => value switch
    {
        _ when float.IsNaN(value) => Math.Clamp(Default, Minimum, Maximum),
        float.NegativeInfinity => Minimum,
        float.PositiveInfinity => Maximum,
        _ => Math.Clamp(value, Minimum, Maximum),
    };
}
