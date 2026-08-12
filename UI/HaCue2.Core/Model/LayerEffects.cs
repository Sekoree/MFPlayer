using S.Media.Compositor.Effects;
using S.Media.Core.Effects;

namespace HaCue2.Core.Model;

/// <summary>One persisted scalar parameter of a layer-effect instance.</summary>
public sealed record LayerEffectParameterValue
{
    public string ParameterId { get; set; } = "";
    public double Value { get; set; }
}

/// <summary>An ordered, bypassable layer-effect instance with a stable automation identity.</summary>
public sealed record LayerEffectInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EffectTypeId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<LayerEffectParameterValue> Parameters { get; set; } = [];

    /// <summary>Opaque type-specific settings retained for plugins in addition to scalar parameters.</summary>
    public string? ConfigJson { get; set; }

    public double Read(string parameterId, double fallback) =>
        Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.ParameterId, parameterId, StringComparison.Ordinal))?.Value ?? fallback;
}

/// <summary>An ordered, bypassable cue-local audio effect instance.</summary>
public sealed record AudioEffectInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EffectTypeId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<LayerEffectParameterValue> Parameters { get; set; } = [];
    public string? ConfigJson { get; set; }

    public double Read(string parameterId, double fallback) =>
        Parameters.FirstOrDefault(parameter => parameter.ParameterId == parameterId)?.Value ?? fallback;
}

public static class AudioEffectCatalog
{
    private const string PropertyPrefix = "audio.effect.parameter.";

    public static LayerEffectDefinition Gain { get; } = new(
        S.Media.Routing.GainAudioEffect.EffectId,
        "Gain",
        S.Media.Routing.GainAudioEffect.ParameterDescriptors);

    public static IReadOnlyCollection<LayerEffectDefinition> All => [Gain];

    public static LayerEffectDefinition? Get(string typeId) =>
        string.Equals(typeId, Gain.TypeId, StringComparison.Ordinal) ? Gain : null;

    public static AudioEffectInstance Create(string typeId)
    {
        var definition = Get(typeId);
        return new AudioEffectInstance
        {
            EffectTypeId = typeId,
            Parameters = definition is null
                ? []
                :
                [
                    .. definition.Parameters.Select(parameter => new LayerEffectParameterValue
                    {
                        ParameterId = parameter.Id,
                        Value = parameter.Default,
                    }),
                ],
        };
    }

    public static string PropertyId(string typeId, string parameterId) =>
        $"{PropertyPrefix}{typeId}.{parameterId}";

    public static bool TryResolveProperty(
        string propertyId,
        out LayerEffectDefinition effect,
        out EffectParameterDescriptor parameter)
    {
        foreach (var candidate in All)
        foreach (var component in candidate.Parameters)
            if (propertyId == PropertyId(candidate.TypeId, component.Id))
            {
                effect = candidate;
                parameter = component;
                return true;
            }
        effect = null!;
        parameter = null!;
        return false;
    }
}

/// <summary>Code-owned layer-effect metadata used by authoring, validation, and lowering.</summary>
public sealed record LayerEffectDefinition(
    string TypeId,
    string DisplayName,
    IReadOnlyList<EffectParameterDescriptor> Parameters);

public static class LayerEffectCatalog
{
    private const string DynamicPropertyPrefix = "video.effect.parameter.";

    private static readonly IReadOnlyDictionary<string, LayerEffectDefinition> Definitions =
        new[]
        {
            Definition(ChromaKeyVideoEffect.EffectId, "Chroma key", ChromaKeyVideoEffect.Descriptor),
            Definition(BrightnessContrastVideoEffect.EffectId, "Brightness / contrast",
                BrightnessContrastVideoEffect.Descriptor),
        }.ToDictionary(definition => definition.TypeId, StringComparer.Ordinal);

    public static IReadOnlyCollection<LayerEffectDefinition> All => [.. Definitions.Values];

    public static bool TryGet(string typeId, out LayerEffectDefinition definition) =>
        Definitions.TryGetValue(typeId, out definition!);

    public static LayerEffectDefinition? Get(string typeId) => Definitions.GetValueOrDefault(typeId);

    public static LayerEffectInstance Create(string typeId)
    {
        if (!Definitions.TryGetValue(typeId, out var definition))
            return new LayerEffectInstance { EffectTypeId = typeId };
        return new LayerEffectInstance
        {
            EffectTypeId = typeId,
            Parameters =
            [
                .. definition.Parameters.Select(parameter => new LayerEffectParameterValue
                {
                    ParameterId = parameter.Id,
                    Value = parameter.Default,
                }),
            ],
        };
    }

    /// <summary>Stable automation property ID for a type-local parameter.</summary>
    public static string PropertyId(string typeId, string parameterId) => (typeId, parameterId) switch
    {
        (ChromaKeyVideoEffect.EffectId, "similarity") => AutomationPropertyIds.ChromaSimilarity,
        (ChromaKeyVideoEffect.EffectId, "smoothness") => AutomationPropertyIds.ChromaSmoothness,
        (ChromaKeyVideoEffect.EffectId, "spill") => AutomationPropertyIds.ChromaSpillReduction,
        (BrightnessContrastVideoEffect.EffectId, "brightness") => AutomationPropertyIds.ColorBrightness,
        (BrightnessContrastVideoEffect.EffectId, "contrast") => AutomationPropertyIds.ColorContrast,
        _ => $"{DynamicPropertyPrefix}{typeId}.{parameterId}",
    };

    public static bool TryResolveProperty(
        string propertyId,
        out LayerEffectDefinition effect,
        out EffectParameterDescriptor parameter)
    {
        foreach (var candidate in Definitions.Values)
        foreach (var component in candidate.Parameters)
            if (string.Equals(PropertyId(candidate.TypeId, component.Id), propertyId, StringComparison.Ordinal))
            {
                effect = candidate;
                parameter = component;
                return true;
            }

        effect = null!;
        parameter = null!;
        return false;
    }

    private static LayerEffectDefinition Definition(
        string typeId,
        string displayName,
        S.Media.Core.Video.Effects.VideoLayerEffectDescriptor descriptor) =>
        new(
            typeId,
            displayName,
            [
                .. descriptor.Parameters
                    .SelectMany(parameter => parameter.Authoring ?? [])
                    .Where(parameter => parameter.SupportsAutomation),
            ]);
}

/// <summary>Compatibility and mutation helpers for the schema-3 ordered rack.</summary>
public static class LayerEffectRack
{
    public static IReadOnlyList<LayerEffectInstance> Effective(LayerPlacement placement)
    {
        if (placement.Effects.Count > 0)
            return placement.Effects;

        var effects = new List<LayerEffectInstance>(2);
        if (placement.ChromaKey is { } chroma)
            effects.Add(FromLegacy(chroma, placement.ChromaKeyEnabled));
        if (placement.ColorAdjust is { } colour)
            effects.Add(FromLegacy(colour, placement.ColorAdjustEnabled));
        return effects;
    }

    /// <summary>Moves legacy typed settings into the ordered rack and removes their duplicate write form.</summary>
    public static bool MigrateLegacy(LayerPlacement placement)
    {
        var changed = false;
        if (placement.Effects.Count == 0)
        {
            if (placement.ChromaKey is { } chroma)
            {
                placement.Effects.Add(FromLegacy(chroma, placement.ChromaKeyEnabled));
                changed = true;
            }
            if (placement.ColorAdjust is { } colour)
            {
                placement.Effects.Add(FromLegacy(colour, placement.ColorAdjustEnabled));
                changed = true;
            }
        }

        if (placement.ChromaKey is not null || placement.ColorAdjust is not null)
        {
            placement.ChromaKey = null;
            placement.ColorAdjust = null;
            changed = true;
        }
        return changed;
    }

    public static LayerEffectInstance? Find(LayerPlacement? placement, string typeId) =>
        placement is null
            ? null
            : Effective(placement).FirstOrDefault(effect =>
                string.Equals(effect.EffectTypeId, typeId, StringComparison.Ordinal));

    public static void Write(LayerEffectInstance effect, string parameterId, double value)
    {
        var parameter = effect.Parameters.FirstOrDefault(candidate =>
            string.Equals(candidate.ParameterId, parameterId, StringComparison.Ordinal));
        if (parameter is null)
            effect.Parameters.Add(new LayerEffectParameterValue { ParameterId = parameterId, Value = value });
        else
            parameter.Value = value;
    }

    private static LayerEffectInstance FromLegacy(ChromaKeySpec chroma, bool enabled) => new()
    {
        Id = chroma.Id,
        EffectTypeId = ChromaKeyVideoEffect.EffectId,
        Enabled = enabled,
        Parameters =
        [
            new() { ParameterId = "keyR", Value = chroma.Red },
            new() { ParameterId = "keyG", Value = chroma.Green },
            new() { ParameterId = "keyB", Value = chroma.Blue },
            new() { ParameterId = "similarity", Value = chroma.Similarity },
            new() { ParameterId = "smoothness", Value = chroma.Smoothness },
            new() { ParameterId = "spill", Value = chroma.SpillReduction },
        ],
    };

    private static LayerEffectInstance FromLegacy(ColorAdjustSpec colour, bool enabled) => new()
    {
        Id = colour.Id,
        EffectTypeId = BrightnessContrastVideoEffect.EffectId,
        Enabled = enabled,
        Parameters =
        [
            new() { ParameterId = "brightness", Value = colour.Brightness },
            new() { ParameterId = "contrast", Value = colour.Contrast },
        ],
    };
}
