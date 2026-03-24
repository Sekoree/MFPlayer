using System.Collections.ObjectModel;

namespace S.Media.Core.Media;

public sealed record MediaMetadataSnapshot
{
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public ReadOnlyDictionary<string, string> AdditionalMetadata { get; init; } =
        new(new Dictionary<string, string>());
}

