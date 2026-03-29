using System.Collections.ObjectModel;

namespace S.Media.Core.Media;

public sealed record MediaMetadataSnapshot
{
    /// <summary>UTC time when this snapshot was captured.</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; }

    // ── Well-known fields ────────────────────────────────────────────────────

    /// <summary>Title tag (track or programme name).</summary>
    public string? Title { get; init; }

    /// <summary>Artist or performer name.</summary>
    public string? Artist { get; init; }

    /// <summary>Album or show title.</summary>
    public string? Album { get; init; }

    /// <summary>Release or production year (string to preserve original formatting).</summary>
    public string? Year { get; init; }

    // ── Arbitrary metadata ───────────────────────────────────────────────────

    /// <summary>Additional metadata not covered by well-known fields.</summary>
    public ReadOnlyDictionary<string, string> AdditionalMetadata { get; init; } =
        new(new Dictionary<string, string>());
}
