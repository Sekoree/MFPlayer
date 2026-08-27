using System.Text.Json.Serialization;

namespace HaPlay.Models;

// Serialized model. Non-CLR-default properties use `set`, not `init`: the source-generated
// serializer assigns EVERY init property through one object initializer, so a field absent from
// the JSON would load as the CLR default instead of the property initializer (see the FadeCueNode
// doc note in CueList.cs). `init` remains only where the initializer IS the CLR default.

/// <summary>
/// Persisted per-player state - playlist + transport flags + selected output lines (matched on load
/// by <see cref="OutputDefinition.DisplayName"/>). Written via <see cref="MediaPlayerConfigJsonContext"/>
/// so the path is NativeAOT-safe.
/// </summary>
public sealed record MediaPlayerConfig
{
    /// <summary>Schema tag - bump when the on-disk shape changes incompatibly.</summary>
    public string Schema { get; set; } = "HaPlayPlayerConfig/v1";

    public string Name { get; set; } = "Player";

    /// <summary>Phase C (§4.3.2) - all playlist tabs owned by this player.</summary>
    public List<PlaylistConfig> PlaylistTabs { get; set; } = new();

    /// <summary>Index into <see cref="PlaylistTabs"/> that was visible when the player was saved.</summary>
    public int SelectedPlaylistTabIndex { get; init; }

    /// <summary>Legacy v1 flat playlist fields. Kept so older player/project files still load cleanly.</summary>
    public List<string> PlaylistPaths { get; set; } = new();

    public string? MediaFilePath { get; init; }

    public string? SelectedPlaylistPath { get; init; }

    public string? FallbackImagePath { get; init; }

    public bool IsLooping { get; init; }

    public bool AutoAdvancePlaylist { get; init; }

    public bool HoldFallbackVideo { get; init; }

    public double MasterVolumeDb { get; init; }

    public bool MasterMuted { get; init; }

    /// <summary>Per-deck panel tint as packed ARGB (null = no tint). Applied as a low-alpha wash to all of the
    /// deck's dockable panels so they stay identifiable when split/floated apart.</summary>
    public uint? TintArgb { get; init; }

    public PlayerOutputPreset OutputPreset { get; set; } = PlayerOutputPreset.AsSource;

    public PlayerTransitionMode TransitionMode { get; set; } = PlayerTransitionMode.Cut;

    public int TransitionDurationMs { get; set; } = 500;

    /// <summary>§4.3.5 - Custom preset width in pixels (only honored when <see cref="OutputPreset"/>
    /// is <see cref="PlayerOutputPreset.Custom"/>). Defaults to 1920 so an empty config produces a
    /// sensible Custom raster instead of zero.</summary>
    public int CustomOutputWidth { get; set; } = 1920;

    /// <summary>§4.3.5 - Custom preset height in pixels (only honored when <see cref="OutputPreset"/>
    /// is <see cref="PlayerOutputPreset.Custom"/>). Defaults to 1080.</summary>
    public int CustomOutputHeight { get; set; } = 1080;

    /// <summary>Display names of output lines that were checked for this player when the config was saved.</summary>
    public List<string> SelectedOutputDisplayNames { get; set; } = new();

    public List<OutputGainConfig> OutputGains { get; set; } = new();

    /// <summary>UI rewrite P5b - auto-preset rules: source channel count → downmix preset applied
    /// to the routing matrix when media with that channel count loads.</summary>
    public List<ChannelPresetRule> ChannelPresetRules { get; set; } = new();

    /// <summary>
    /// Explicit source channel count used to size the audio matrix before a media file is open.
    /// 0 means "auto" for older configs: infer from the active source when possible, otherwise stereo.
    /// </summary>
    public int AudioMatrixInputChannels { get; init; }

    /// <summary>
    /// Per-input-channel attenuation/mute (column trims) for the audio matrix.
    /// Applied on top of every cell that reads from the matching input channel.
    /// </summary>
    public List<InputChannelTrimConfig> InputTrims { get; set; } = new();
}

public sealed record PlaylistConfig
{
    /// <summary>v1 used a flat <c>Paths</c> string list; v2 (Phase C.5 §6.8) carries discriminated
    /// <see cref="Items"/> so live inputs round-trip alongside files. Loaders fall back to <see cref="Paths"/>
    /// when <see cref="Items"/> is empty so v1 playlist files keep working.</summary>
    public string Schema { get; set; } = "HaPlayPlaylist/v2";

    public string Name { get; set; } = "Set A";

    /// <summary>Phase C.5 (§6.8) - discriminated playlist entries. Canonical on write; on read, falls
    /// back to <see cref="Paths"/> when this list is empty.</summary>
    public List<PlaylistItem> Items { get; set; } = new();

    /// <summary>Phase C.5 (§6.8) - selected playlist item by <see cref="PlaylistItem.Id"/>. <see langword="null"/>
    /// means "use first item" or fall back to <see cref="SelectedPath"/> for v1 files.</summary>
    public Guid? SelectedItemId { get; init; }

    /// <summary>Legacy v1 file path list. Kept so v1 playlist files load via <see cref="FilePlaylistItem"/>
    /// projection. Always written <see langword="null"/> by v2 so the field drops out of fresh saves.</summary>
    public List<string>? Paths { get; init; }

    /// <summary>Legacy v1 selected path. Resolved into <see cref="SelectedItemId"/> on load.</summary>
    public string? SelectedPath { get; init; }

    public bool IsLooping { get; init; }

    public bool AutoAdvance { get; init; }

    /// <summary>When auto-advancing, pick the next item in a random (shuffle-bag) order instead of
    /// sequentially. Default false keeps existing playlists sequential.</summary>
    public bool Shuffle { get; init; }

    /// <summary>When auto-advancing past the last item, wrap to the first (loop the whole list)
    /// rather than stopping. Distinct from <see cref="IsLooping"/> which loops the current item.</summary>
    public bool RepeatAll { get; init; }
}

public sealed record OutputGainConfig
{
    public string OutputDisplayName { get; set; } = string.Empty;

    public double GainDb { get; init; }

    public bool Muted { get; init; }

    /// <summary>Phase C (§4.3.4) - per-output channel-mix mode. Falls back to <see cref="AudioRouteMixMode.Stereo"/>
    /// for older configs so a missing field doesn't surprise the loader. When <see cref="MatrixCells"/> is
    /// non-empty, the matrix takes precedence and <see cref="MixMode"/> is the "preset that was last applied".</summary>
    public AudioRouteMixMode MixMode { get; set; } = AudioRouteMixMode.Stereo;

    /// <summary>Phase C (§4.3.4) - full N×M matrix cells. Empty means "fall back to <see cref="MixMode"/>".</summary>
    public List<AudioMatrixCellConfig> MatrixCells { get; set; } = new();
}

public sealed record InputChannelTrimConfig
{
    public int InputChannel { get; init; }

    public double GainDb { get; init; }

    public bool Muted { get; init; }
}

/// <summary>
/// Phase C (§4.3.4) first-cut channel routing mode. The full N×M cell-grid matrix needs the framework
/// "Per-cell channel-mix matrix" gap to land; until then these five fixed presets cover the common
/// stereo source → stereo output reroutes via a single <c>ChannelMap</c> per route.
/// </summary>
public enum AudioRouteMixMode
{
    /// <summary>Identity stereo: source L → out L, source R → out R.</summary>
    Stereo,
    /// <summary>Swap: source L → out R, source R → out L.</summary>
    Swap,
    /// <summary>Mono-from-left: source L → out L + out R; source R discarded.</summary>
    MonoLeft,
    /// <summary>Mono-from-right: source R → out L + out R; source L discarded.</summary>
    MonoRight,
    /// <summary>Silence: both output channels zeroed (logical mute that survives gain changes).</summary>
    Silence,
}

public enum PlayerOutputPreset
{
    AsSource,
    Preset1080p60,
    Preset720p60,
    Custom,
}

public enum PlayerTransitionMode
{
    Cut,
    Fade,
    IdleImage,
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MediaPlayerConfig))]
[JsonSerializable(typeof(PlaylistConfig))]
[JsonSerializable(typeof(AudioMatrixCellConfig))]
[JsonSerializable(typeof(InputChannelTrimConfig))]
[JsonSerializable(typeof(PlaylistItem))]
[JsonSerializable(typeof(FilePlaylistItem))]
[JsonSerializable(typeof(NDIInputPlaylistItem))]
[JsonSerializable(typeof(PortAudioInputPlaylistItem))]
[JsonSerializable(typeof(ImagePlaylistItem))]
[JsonSerializable(typeof(SubtitlePlaylistItem))]
[JsonSerializable(typeof(TextPlaylistItem))]
internal partial class MediaPlayerConfigJsonContext : JsonSerializerContext;
