namespace S.Media.Core.Media;

public interface IDynamicMetadata
{
    /// <summary>
    /// Returns the most recently published metadata snapshot, or <see langword="null"/>
    /// if no metadata has been published yet.
    /// </summary>
    MediaMetadataSnapshot? GetMetadata();

    /// <summary>Raised when a new metadata snapshot is published.</summary>
    event EventHandler<MediaMetadataSnapshot>? MetadataChanged;
}
