namespace HaPlay.Models;

// Serialized model. Non-CLR-default properties use `set`, not `init`: the source-generated
// serializer assigns EVERY init property through one object initializer, so a field absent from
// the JSON would load as the CLR default instead of the property initializer (see the FadeCueNode
// doc note in CueList.cs). `init` remains only where the initializer IS the CLR default.

/// <summary>Standalone soundboards file payload (<c>.haplayboards</c>): one or more boards, so the
/// same format serves "save this board" and "save the whole collection".</summary>
public sealed record SoundboardsCollectionDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? Generator { get; init; }

    public List<SoundboardConfig> Soundboards { get; set; } = [];
}

/// <summary>
/// One tile on a <see cref="SoundboardConfig"/> grid. A tile is "bound" once it has a
/// <see cref="FilePath"/>; unbound tiles are placeholders that only show up in edit mode.
/// </summary>
public sealed record SoundboardTileConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Grid cell (zero-based). Tiles keep their cell when others are hidden, so a touch
    /// operator's muscle memory survives outside edit mode.</summary>
    public int Row { get; init; }

    public int Column { get; init; }

    /// <summary>Absolute path of the sound file; null = unbound placeholder.</summary>
    public string? FilePath { get; init; }

    /// <summary>Optional display alias; null/blank = show the filename (without extension).</summary>
    public string? Label { get; init; }

    /// <summary>Target audio output line (<see cref="OutputDefinition.Id"/>);
    /// <see cref="Guid.Empty"/> = use the board default at play time.</summary>
    public Guid OutputLineId { get; init; }

    /// <summary>Linear volume 0..1 (1 = unity gain).</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Fade-out duration applied when the tile is tapped while playing; 0 = stop instantly.</summary>
    public int FadeOutMs { get; init; }

    public bool Loop { get; init; }

    /// <summary>Cached duration from the add-time probe so the grid can label tiles without
    /// reopening decoders on project load. Refreshed whenever the file binding changes.</summary>
    public int? DurationMs { get; init; }
}

/// <summary>
/// One soundboard tab: a fixed grid of tiles plus the per-board defaults that pre-fill newly
/// bound tiles (fast "drop a folder of stingers on the grid" workflow).
/// </summary>
public sealed record SoundboardConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Soundboard";

    public int Rows { get; set; } = 4;

    public int Columns { get; set; } = 6;

    /// <summary>Defaults applied to a tile when a sound is bound to it (and used at play time for
    /// tiles whose own <see cref="SoundboardTileConfig.OutputLineId"/> is empty).</summary>
    public Guid DefaultOutputLineId { get; init; }

    public double DefaultVolume { get; set; } = 1.0;

    public int DefaultFadeOutMs { get; init; }

    public bool DefaultLoop { get; init; }

    /// <summary>Tempo (beats per minute) the board's launch quantization is measured in. Only
    /// meaningful when <see cref="LaunchQuantizeBeats"/> is set. Default 120 so older files load
    /// with a sane tempo the moment quantization is switched on.</summary>
    public double Bpm { get; set; } = 120;

    /// <summary>Global launch quantization for this board, in beats (Ableton-style: one setting for
    /// the whole grid, not per tile). 0 (the default, and what older files load as) = tiles fire the
    /// instant they are tapped - the pre-quantization behavior. 1 = next beat, 4 = next bar at 4/4.
    /// Boundaries are multiples of the quantum from the workspace's own transport origin; there is
    /// no external tempo sync.</summary>
    public double LaunchQuantizeBeats { get; init; }

    public List<SoundboardTileConfig> Tiles { get; set; } = new();
}
