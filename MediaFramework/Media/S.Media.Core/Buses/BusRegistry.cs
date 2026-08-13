using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Effects;
using S.Media.Core.Video;
using S.Media.Core.Video.Effects;

namespace S.Media.Core.Buses;

/// <summary>Instance parameters for a visual source (visualizer): the video format it should produce
/// and an opaque config blob (preset directory, sensitivity, … - the kind defines the schema).</summary>
public sealed record VisualSourceCreateArgs(
    int Width,
    int Height,
    Rational FrameRate,
    string? ConfigJson = null);

/// <summary>Authoring metadata published with an effect factory - audio insert or per-layer video effect
/// alike. Runtime-only/legacy factories may omit it; hosts then retain the kind but do not offer scalar
/// parameter authoring for it. Published WITHOUT constructing a processing instance, so an insertion menu
/// can list a plugin's parameters without instantiating (and configuring) the effect first.</summary>
public sealed record EffectRegistrationDescriptor(
    string Kind,
    string DisplayName,
    IReadOnlyList<EffectParameterDescriptor> Parameters);

/// <summary>
/// Bus-capability registration (mirrors <c>CompositorRegistry</c>'s kind→factory shape, Doc 05): modules
/// register audio/video effects and audio-visual sources <em>by kind</em>; the UI enumerates the kinds
/// for its insertion menus. Built once at the composition root and injected - no global state.
/// </summary>
public interface IBusRegistryBuilder
{
    /// <summary>Register an audio effect factory (case-insensitive kind; later registration replaces).</summary>
    IBusRegistryBuilder AddAudioEffect(string kind, Func<string?, IAudioBusEffect> factory);

    /// <summary>Register an audio effect factory plus the stable scalar metadata an authoring host can
    /// enumerate without constructing a live processing instance.</summary>
    IBusRegistryBuilder AddAudioEffect(
        string kind,
        string displayName,
        IReadOnlyList<EffectParameterDescriptor> parameters,
        Func<string?, IAudioBusEffect> factory);

    /// <summary>Register a video effect factory.</summary>
    IBusRegistryBuilder AddVideoEffect(string kind, Func<string?, IVideoBusEffect> factory);

    /// <summary>Register an audio-in → video-out generator (visualizer) factory.</summary>
    IBusRegistryBuilder AddVisualSource(string kind, Func<VisualSourceCreateArgs, IAudioVisualSource> factory);

    /// <summary>Register a per-LAYER video effect factory (the compositor's GPU fragment chain with
    /// optional CPU fallback - see <see cref="VideoLayerEffect"/>). A distinct stage from
    /// <see cref="AddVideoEffect"/>'s per-output CPU bus effects: layer effects run inside the
    /// composite pass before blending.</summary>
    IBusRegistryBuilder AddLayerEffect(string kind, Func<string?, VideoLayerEffect> factory);

    /// <summary>Register a per-layer video effect factory WITH its authoring metadata, so a host can list
    /// the effect's parameters without constructing an instance to read them off. The video twin of
    /// <see cref="AddAudioEffect(string, string, IReadOnlyList{EffectParameterDescriptor}, Func{string?, IAudioBusEffect})"/>.</summary>
    IBusRegistryBuilder AddLayerEffect(
        string kind,
        string displayName,
        IReadOnlyList<EffectParameterDescriptor> parameters,
        Func<string?, VideoLayerEffect> factory);

    /// <summary>Register a GEOMETRY-stage layer effect factory (splitting/warp - see
    /// <see cref="IVideoLayerGeometryEffect"/>): resolves the section list a layer is drawn as.
    /// Unlike the color stage, geometry runs in the vertex domain (one draw per section).
    /// A factory should throw on unusable config (there is no meaningful identity geometry) -
    /// <see cref="IBusRegistry.TryCreateGeometryEffect"/> then reports false.</summary>
    IBusRegistryBuilder AddGeometryEffect(string kind, Func<string?, IVideoLayerGeometryEffect> factory);
}

/// <summary>Immutable resolved bus capabilities.</summary>
public interface IBusRegistry
{
    IReadOnlyCollection<string> AudioEffectKinds { get; }

    IReadOnlyCollection<EffectRegistrationDescriptor> AudioEffectDescriptors { get; }

    bool TryGetAudioEffectDescriptor(
        string kind,
        [MaybeNullWhen(false)] out EffectRegistrationDescriptor descriptor);

    /// <summary>Authoring metadata for every layer effect registered with it. Empty for factories
    /// registered through the metadata-free overload.</summary>
    IReadOnlyCollection<EffectRegistrationDescriptor> LayerEffectDescriptors { get; }

    bool TryGetLayerEffectDescriptor(
        string kind,
        [MaybeNullWhen(false)] out EffectRegistrationDescriptor descriptor);

    IReadOnlyCollection<string> VideoEffectKinds { get; }

    IReadOnlyCollection<string> VisualSourceKinds { get; }

    IReadOnlyCollection<string> LayerEffectKinds { get; }

    IReadOnlyCollection<string> GeometryEffectKinds { get; }

    bool TryCreateAudioEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IAudioBusEffect effect);

    bool TryCreateVideoEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoBusEffect effect);

    bool TryCreateVisualSource(string kind, VisualSourceCreateArgs args, [MaybeNullWhen(false)] out IAudioVisualSource source);

    bool TryCreateLayerEffect(string kind, string? configJson, [MaybeNullWhen(false)] out VideoLayerEffect effect);

    bool TryCreateGeometryEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoLayerGeometryEffect effect);
}

/// <summary>Mutable builder for an <see cref="IBusRegistry"/>.</summary>
public sealed class BusRegistryBuilder : IBusRegistryBuilder
{
    private readonly Dictionary<string, Func<string?, IAudioBusEffect>> _audio = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffectRegistrationDescriptor> _audioDescriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, IVideoBusEffect>> _video = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<VisualSourceCreateArgs, IAudioVisualSource>> _visual = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, VideoLayerEffect>> _layer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, IVideoLayerGeometryEffect>> _geometry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffectRegistrationDescriptor> _layerDescriptors =
        new(StringComparer.OrdinalIgnoreCase);

    public IBusRegistryBuilder AddAudioEffect(string kind, Func<string?, IAudioBusEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _audio[kind] = factory;
        _audioDescriptors.Remove(kind);
        return this;
    }

    public IBusRegistryBuilder AddAudioEffect(
        string kind,
        string displayName,
        IReadOnlyList<EffectParameterDescriptor> parameters,
        Func<string?, IAudioBusEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(factory);
        var snapshot = parameters.ToArray();
        if (snapshot.Any(parameter => string.IsNullOrWhiteSpace(parameter.Id))
            || snapshot.GroupBy(parameter => parameter.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("audio-effect parameter IDs must be non-empty and unique.", nameof(parameters));
        _audio[kind] = factory;
        _audioDescriptors[kind] = new EffectRegistrationDescriptor(kind, displayName, snapshot);
        return this;
    }

    public IBusRegistryBuilder AddVideoEffect(string kind, Func<string?, IVideoBusEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _video[kind] = factory;
        return this;
    }

    public IBusRegistryBuilder AddVisualSource(string kind, Func<VisualSourceCreateArgs, IAudioVisualSource> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _visual[kind] = factory;
        return this;
    }

    public IBusRegistryBuilder AddLayerEffect(string kind, Func<string?, VideoLayerEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _layer[kind] = factory;
        _layerDescriptors.Remove(kind);
        return this;
    }

    public IBusRegistryBuilder AddLayerEffect(
        string kind,
        string displayName,
        IReadOnlyList<EffectParameterDescriptor> parameters,
        Func<string?, VideoLayerEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(factory);
        var snapshot = parameters.ToArray();
        if (snapshot.Any(parameter => string.IsNullOrWhiteSpace(parameter.Id))
            || snapshot.GroupBy(parameter => parameter.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("layer-effect parameter IDs must be non-empty and unique.", nameof(parameters));
        _layer[kind] = factory;
        _layerDescriptors[kind] = new EffectRegistrationDescriptor(kind, displayName, snapshot);
        return this;
    }

    public IBusRegistryBuilder AddGeometryEffect(string kind, Func<string?, IVideoLayerGeometryEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _geometry[kind] = factory;
        return this;
    }

    public IBusRegistry Build() =>
        new BusRegistry(_audio, _audioDescriptors, _video, _visual, _layer, _layerDescriptors, _geometry);

    public static IBusRegistry Build(Action<IBusRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BusRegistryBuilder();
        configure(builder);
        return builder.Build();
    }
}

internal sealed class BusRegistry(
    Dictionary<string, Func<string?, IAudioBusEffect>> audio,
    Dictionary<string, EffectRegistrationDescriptor> audioDescriptors,
    Dictionary<string, Func<string?, IVideoBusEffect>> video,
    Dictionary<string, Func<VisualSourceCreateArgs, IAudioVisualSource>> visual,
    Dictionary<string, Func<string?, VideoLayerEffect>> layer,
    Dictionary<string, EffectRegistrationDescriptor> layerDescriptors,
    Dictionary<string, Func<string?, IVideoLayerGeometryEffect>> geometry) : IBusRegistry
{
    private readonly Dictionary<string, Func<string?, IAudioBusEffect>> _audio =
        new(audio, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffectRegistrationDescriptor> _audioDescriptors =
        new(audioDescriptors, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, IVideoBusEffect>> _video =
        new(video, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<VisualSourceCreateArgs, IAudioVisualSource>> _visual =
        new(visual, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, VideoLayerEffect>> _layer =
        new(layer, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffectRegistrationDescriptor> _layerDescriptors =
        new(layerDescriptors, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, IVideoLayerGeometryEffect>> _geometry =
        new(geometry, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> AudioEffectKinds => _audio.Keys;

    public IReadOnlyCollection<EffectRegistrationDescriptor> AudioEffectDescriptors =>
        _audioDescriptors.Values;

    public bool TryGetAudioEffectDescriptor(
        string kind,
        [MaybeNullWhen(false)] out EffectRegistrationDescriptor descriptor) =>
        _audioDescriptors.TryGetValue(kind, out descriptor);

    public IReadOnlyCollection<EffectRegistrationDescriptor> LayerEffectDescriptors =>
        _layerDescriptors.Values;

    public bool TryGetLayerEffectDescriptor(
        string kind,
        [MaybeNullWhen(false)] out EffectRegistrationDescriptor descriptor) =>
        _layerDescriptors.TryGetValue(kind, out descriptor);

    public IReadOnlyCollection<string> VideoEffectKinds => _video.Keys;

    public IReadOnlyCollection<string> VisualSourceKinds => _visual.Keys;

    public IReadOnlyCollection<string> LayerEffectKinds => _layer.Keys;

    public IReadOnlyCollection<string> GeometryEffectKinds => _geometry.Keys;

    public bool TryCreateAudioEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IAudioBusEffect effect)
    {
        effect = null;
        if (!_audio.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }

    public bool TryCreateVideoEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoBusEffect effect)
    {
        effect = null;
        if (!_video.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }

    public bool TryCreateVisualSource(string kind, VisualSourceCreateArgs args, [MaybeNullWhen(false)] out IAudioVisualSource source)
    {
        source = null;
        if (!_visual.TryGetValue(kind, out var factory))
            return false;
        try { source = factory(args); }
        catch { return false; }
        return source is not null;
    }

    public bool TryCreateLayerEffect(string kind, string? configJson, [MaybeNullWhen(false)] out VideoLayerEffect effect)
    {
        effect = null;
        if (!_layer.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }

    public bool TryCreateGeometryEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoLayerGeometryEffect effect)
    {
        effect = null;
        if (!_geometry.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }
}
