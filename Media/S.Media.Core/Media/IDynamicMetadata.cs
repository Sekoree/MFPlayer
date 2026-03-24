namespace S.Media.Core.Media;

public interface IDynamicMetadata
{
    event EventHandler<MediaMetadataSnapshot>? MetadataUpdated;
}

