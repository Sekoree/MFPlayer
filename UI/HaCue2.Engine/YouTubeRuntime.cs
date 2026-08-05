using S.Media.Source.YouTube;

namespace HaCue2.Engine;

/// <summary>
/// The one YouTube gateway and cache the whole app shares.
/// </summary>
/// <remarks>
/// <para>
/// The registry's youtube provider plays from a locally prepared asset and REFUSES to download on the
/// fire path — a GO that started a network transfer would be a cue that takes four minutes to begin,
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

    /// <summary>Points YouTube at HaCue's unified cache before any dialog or source module is created.</summary>
    public static void Configure(HaCue2.Machine.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (Gate)
        {
            var root = HaCue2.Machine.MediaCache.YouTubeRootFor(settings);
            if (_preparer is not null && string.Equals(_preparer.CacheRoot, root, StringComparison.Ordinal))
                return;

            _preparer = new YouTubePreparer(
                Gateway,
                root,
                maxCacheBytes: HaCue2.Machine.MediaCache.ParseBudget(settings.YouTubeBudget));
        }
    }

    /// <summary>The registry module, built over the shared preparer.</summary>
    public static YouTubeSourceModule Module() => new(Preparer);
}
