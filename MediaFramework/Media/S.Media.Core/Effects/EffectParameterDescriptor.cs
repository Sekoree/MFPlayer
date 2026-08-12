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
    public float Clamp(float value) => float.IsFinite(value)
        ? Math.Clamp(value, Minimum, Maximum)
        : Default;
}
