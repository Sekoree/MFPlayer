using S.Media.Source.YouTube;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;

namespace HaCue2.Engine;

/// <summary>
/// The one YouTube gateway and cache the whole app shares.
/// </summary>
/// <remarks>
/// <para>
/// The registry's youtube provider plays from a locally prepared asset and REFUSES to download on the
/// fire path - a GO that started a network transfer would be a cue that takes four minutes to begin,
/// on a machine that may have no network at the venue at all. So preparation happens when the cue is
/// authored, and the provider only ever opens what is already on disk.
/// </para>
/// <para>
/// Which makes a shared preparer load-bearing rather than tidy: the dialog that downloads and the
/// provider that opens have to agree on the cache, and two instances would mean an operator watching a
/// download finish and then a cue that says the video is not prepared.
/// </para>
/// </remarks>
public static class YouTubeRuntime
{
    private static readonly object Gate = new();
    private static YouTubePreparer? _preparer;
    private static YouTubePreparationQueue? _downloads;

    public static YoutubeExplodeGateway Gateway { get; } = new();

    /// <summary>The cache. Its root survives restarts, so a show prepared yesterday plays offline today.</summary>
    public static YouTubePreparer Preparer
    {
        get
        {
            lock (Gate)
                return _preparer ??= new YouTubePreparer(Gateway);
        }
    }

    /// <summary>The bounded background queue shared by authoring, Project status and diagnostics.</summary>
    public static YouTubePreparationQueue Downloads
    {
        get
        {
            lock (Gate)
                return _downloads ??= new YouTubePreparationQueue(Preparer);
        }
    }

    /// <summary>Points YouTube at HaCue's unified cache before any dialog or source module is created.</summary>
    public static void Configure(HaCue2.Machine.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (Gate)
        {
            var root = HaCue2.Machine.MediaCache.YouTubeRootFor(settings);
            if (_preparer is not null && string.Equals(_preparer.CacheRoot, root, StringComparison.Ordinal))
                return;

            _downloads?.Dispose();
            _preparer = new YouTubePreparer(
                Gateway,
                root,
                maxCacheBytes: HaCue2.Machine.MediaCache.ParseBudget(settings.YouTubeBudget));
            _downloads = new YouTubePreparationQueue(_preparer);
        }
    }

    public static PreparedSourceAvailability Availability(string sourceUri) =>
        Downloads.StateOf(sourceUri) switch
        {
            YouTubeCacheState.Ready => PreparedSourceAvailability.Ready,
            YouTubeCacheState.Queued or YouTubeCacheState.Downloading => PreparedSourceAvailability.Preparing,
            YouTubeCacheState.Failed => PreparedSourceAvailability.Failed,
            _ => PreparedSourceAvailability.Missing,
        };

    /// <summary>Machine-local caption sidecars, derived from portable source URIs at compile time.</summary>
    public static IReadOnlyDictionary<Guid, string> PreparedSubtitlePaths(HaCueProject project) =>
        project.AllCues().OfType<MediaCueNode>()
            .Select(cue => (cue.Id, Path: Downloads.PreparedSubtitlePath(cue.MediaPath)))
            .Where(item => item.Path is { Length: > 0 })
            .ToDictionary(item => item.Id, item => item.Path!);

    /// <summary>Cancels background work during application shutdown; atomic cache writes clean up partials.</summary>
    public static void Shutdown()
    {
        lock (Gate)
            _downloads?.Dispose();
    }

    /// <summary>The registry module, built over the shared preparer.</summary>
    public static YouTubeSourceModule Module() => new(Preparer);
}
