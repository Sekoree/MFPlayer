using S.Media.Decode.FFmpeg;

namespace HaCue2.Machine;

/// <summary>
/// One selectable track inside a media file.
/// </summary>
/// <param name="Index">The container's stream index — what a cue stores when it picks this track.</param>
/// <param name="Signature">
/// Identity of the track's CONTENT, independent of its index. A file that is re-muxed keeps its tracks
/// but can renumber them, and a stored index would then silently point at a different one — the German
/// commentary instead of the music. Comparing signatures is how that is caught.
/// </param>
/// <param name="IsAttachedPicture">Cover art. A video "track" that is one still frame.</param>
public readonly record struct MediaTrack(
    int Index,
    string Label,
    string Signature,
    string? Language,
    int Channels,
    int Width,
    int Height,
    bool IsDefault,
    bool IsAttachedPicture,
    bool IsDecodable);

/// <summary>What a media file turned out to contain.</summary>
/// <param name="Duration">Null when the container does not declare one — a live source, or a stream.</param>
public sealed record MediaFacts
{
    /// <summary>What is known about a file nobody could open. Every list is deliberately empty.</summary>
    public static MediaFacts Unknown { get; } = new();

    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Every audio track, in container order. A concert capture routinely has several — a stereo mix,
    /// an isolated vocal, a room pair — and which one a cue plays is an authoring decision.
    /// </summary>
    public IReadOnlyList<MediaTrack> AudioTracks { get; init; } = [];

    /// <summary>
    /// Every video track. Rare beyond one, but a multi-angle capture or an embedded cover both produce
    /// more, and cover art must be SELECTABLE rather than silently elected.
    /// </summary>
    public IReadOnlyList<MediaTrack> VideoTracks { get; init; } = [];

    public IReadOnlyList<MediaTrack> SubtitleTracks { get; init; } = [];

    public bool HasAudio => AudioTracks.Count > 0;

    /// <summary>Video that is actually moving — cover art alone is not a video track worth showing.</summary>
    public bool HasVideo => VideoTracks.Any(track => !track.IsAttachedPicture);

    /// <summary>Whether anything was learned at all. False is "nobody could open it".</summary>
    public bool IsKnown => Duration is not null || AudioTracks.Count > 0 || VideoTracks.Count > 0;

    /// <summary>
    /// The track a stored selection resolves to, or null for "let the decoder elect one".
    /// </summary>
    /// <remarks>
    /// The index is only trusted when the CONTENT still matches. After a re-mux the same index can be
    /// a different language, so a mismatched signature falls back to automatic election rather than
    /// playing the wrong track — being obviously automatic beats being quietly wrong.
    /// </remarks>
    public static MediaTrack? Resolve(IReadOnlyList<MediaTrack> tracks, int? index, string? signature)
    {
        if (index is not { } wanted || wanted < 0)
            return null;

        // Cast to the NULLABLE struct before FirstOrDefault: on a value type the plain overload hands
        // back a fully-default instance whose string fields are null, which reads as a real track with
        // an empty signature and dereferences to nothing.
        var byIndex = tracks.Cast<MediaTrack?>().FirstOrDefault(track => track!.Value.Index == wanted);

        if (byIndex is { } found
            && (string.IsNullOrEmpty(signature) || found.Signature == signature))
            return found;

        // The index moved: find the same CONTENT if it is still in the file.
        return string.IsNullOrEmpty(signature)
            ? null
            : tracks.Cast<MediaTrack?>().FirstOrDefault(track => track!.Value.Signature == signature);
    }
}

/// <summary>
/// Opens a media file and reports what is in it.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between a cue list that says "—" in every Len column and one that tells the
/// operator how long the show is, and between "this file has audio" and "this file has four audio
/// tracks and you are playing the second". Both are MACHINE facts, not document ones: two machines
/// with different copies of the same file can legitimately disagree, so nothing here is written into
/// the project — it is asked for, cached, and shown.
/// </para>
/// <para>
/// A failure is <see cref="MediaFacts.Unknown"/>, never an exception and never a guess. An unreadable
/// file is a thing the status pass reports; it must not take a view down with it.
/// </para>
/// </remarks>
public static class MediaProbe
{
    /// <summary>Probes one file. Returns <see cref="MediaFacts.Unknown"/> for anything unreadable.</summary>
    public static async Task<MediaFacts> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return MediaFacts.Unknown;

        try
        {
            // Off the calling thread: opening a container touches the disk, and on a network mount or
            // a sleeping drive that is seconds, not milliseconds.
            return await Task.Run(() => Read(path), cancellation).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return MediaFacts.Unknown;
        }
    }

    private static MediaFacts Read(string path)
    {
        // The container probe, not a full decoder build: it gives the duration AND the whole stream
        // table in one pass, which is everything a cue list and a track picker need.
        var container = MediaContainerDecoder.ProbeContainer(path);

        return new MediaFacts
        {
            // Zero means "the container did not say", which is not the same as a zero-length file.
            Duration = container.Duration > TimeSpan.Zero ? container.Duration : null,
            AudioTracks = Tracks(container.Streams, MediaStreamKind.Audio),
            VideoTracks = Tracks(container.Streams, MediaStreamKind.Video),
            SubtitleTracks = Tracks(container.Streams, MediaStreamKind.Subtitle),
        };
    }

    /// <remarks>
    /// Undecodable audio and video are dropped — offering a track nothing can play is worse than not
    /// offering it. SUBTITLES are kept regardless: a subtitle track with no decoder here may still be
    /// a track somebody wants recorded in the show, and it costs nothing to list.
    /// </remarks>
    private static IReadOnlyList<MediaTrack> Tracks(
        IReadOnlyList<MediaStreamInfo> streams, MediaStreamKind kind) =>
    [
        .. streams
            .Where(stream => stream.Kind == kind
                             && (stream.IsDecodable || kind == MediaStreamKind.Subtitle))
            .Select(stream => new MediaTrack(
                stream.Index,
                stream.ToDisplayString(),
                stream.ContentSignature,
                stream.Language,
                stream.Channels,
                stream.Width,
                stream.Height,
                stream.IsDefault,
                stream.IsAttachedPicture,
                stream.IsDecodable)),
    ];
}
