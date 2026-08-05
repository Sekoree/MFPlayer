namespace S.Media.Source.YouTube;

/// <summary>The background queue's view of one prepared source.</summary>
public enum YouTubeCacheState
{
    Missing,
    Queued,
    Downloading,
    Ready,
    Failed,
}

public sealed record YouTubePreparationResult(
    string SourceUri,
    YouTubePreparedMedia? Prepared,
    string? Error)
{
    public bool IsSuccess => Prepared is not null;
}

public sealed record YouTubePreparationSnapshot(
    int Queued,
    int Downloading,
    int Failed,
    string? CurrentVideoId,
    YouTubePreparePhase? Phase,
    double Fraction,
    string? LastError)
{
    public bool HasWork => Queued + Downloading > 0;
}

/// <summary>
/// Bounded, observable background preparation for persisted YouTube source URIs.
/// </summary>
/// <remarks>
/// The queue changes WHEN preparation happens, not the reliable-playback contract: the decoder still
/// opens only complete local assets and never starts a network request from GO. Requests for the same
/// canonical URI share one job, failed jobs can be retried, and deleting a formerly-ready cache file
/// immediately makes the source missing again.
/// </remarks>
public sealed class YouTubePreparationQueue : IDisposable
{
    private sealed class Entry(string sourceUri, string videoId, YouTubeStreamSelection selection)
    {
        public string SourceUri { get; } = sourceUri;
        public string VideoId { get; } = videoId;
        public YouTubeStreamSelection Selection { get; } = selection;
        public YouTubeCacheState State { get; set; } = YouTubeCacheState.Queued;
        public YouTubePreparePhase? Phase { get; set; }
        public double Fraction { get; set; }
        public string? Error { get; set; }
        public string? PreparedAssetPath { get; set; }
        public string? PreparedSubtitlePath { get; set; }
        public bool SubtitleChecked { get; set; }
        public Task<YouTubePreparationResult> Completion { get; set; } = null!;
    }

    private readonly YouTubePreparer _preparer;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _contentRevision;
    private bool _disposed;

    public YouTubePreparationQueue(YouTubePreparer preparer, int maxConcurrentDownloads = 1)
    {
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrentDownloads));
    }

    /// <summary>Raised for progress/readout changes. May be raised from a worker thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when a source changes readiness. May be raised from a worker thread.</summary>
    public event Action? ReadinessChanged;

    public YouTubePreparer Preparer => _preparer;

    /// <summary>
    /// Changes only when committed cache content may have changed, not for queue/progress transitions.
    /// Hosts use it to avoid recompiling a running show merely because a download began.
    /// </summary>
    public long ContentRevision
    {
        get { lock (_gate) return _contentRevision; }
    }

    /// <summary>Queues a URI without making the caller wait for its download.</summary>
    public Task<YouTubePreparationResult> Enqueue(string sourceUri)
    {
        if (!TryCanonical(sourceUri, out var canonical, out var videoId, out var selection))
            return Task.FromResult(new YouTubePreparationResult(
                sourceUri, null, "not a recognizable YouTube source URI"));

        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue(canonical, out var existing)
                && existing.State is YouTubeCacheState.Queued or YouTubeCacheState.Downloading)
                return existing.Completion;

            // A completed entry is reusable only while its committed asset still exists. This is the
            // cache-clear/moved-machine case: replacing it creates a real retry rather than returning
            // yesterday's successful Task while today's file is gone.
            if (existing is not null
                && existing.State == YouTubeCacheState.Ready
                && IsAssetPresent(videoId, selection)
                && IsSubtitleComplete(existing, selection))
                return existing.Completion;

            entry = new Entry(canonical, videoId, selection);
            _entries[canonical] = entry;
            entry.Completion = Task.Run(() => RunAsync(entry));
        }

        Raise(readiness: true);
        return entry.Completion;
    }

    /// <summary>Current readiness, rechecking the committed file every time.</summary>
    public YouTubeCacheState StateOf(string sourceUri)
    {
        if (!TryCanonical(sourceUri, out var canonical, out var videoId, out var selection))
            return YouTubeCacheState.Missing;

        lock (_gate)
        {
            if (_entries.TryGetValue(canonical, out var entry))
            {
                // An asset may have committed before a caption/network failure. Preserve Failed so Fix
                // can retry the unfinished preparation instead of declaring the cue wholly ready.
                if (entry.State is YouTubeCacheState.Queued or YouTubeCacheState.Downloading
                    or YouTubeCacheState.Failed)
                    return entry.State;

                var prepared = entry.PreparedAssetPath;
                return prepared is { Length: > 0 }
                       && File.Exists(prepared)
                       && IsSubtitleComplete(entry, selection)
                    ? YouTubeCacheState.Ready
                    : YouTubeCacheState.Missing;
            }
        }

        return IsConcrete(selection) && IsAssetPresent(videoId, selection)
            ? YouTubeCacheState.Ready
            : YouTubeCacheState.Missing;
    }

    /// <summary>Queue summary, optionally limited to source URIs owned by one open project.</summary>
    public YouTubePreparationSnapshot Snapshot(IEnumerable<string>? sourceUris = null)
    {
        HashSet<string>? wanted = null;
        if (sourceUris is not null)
        {
            wanted = [];
            foreach (var sourceUri in sourceUris)
                if (TryCanonical(sourceUri, out var canonical, out _, out _))
                    wanted.Add(canonical);
        }

        lock (_gate)
        {
            var entries = _entries.Values
                .Where(entry => wanted is null || wanted.Contains(entry.SourceUri))
                .ToList();
            var active = entries
                .Where(entry => entry.State is YouTubeCacheState.Queued or YouTubeCacheState.Downloading)
                .OrderByDescending(entry => entry.State == YouTubeCacheState.Downloading)
                .FirstOrDefault();
            return new YouTubePreparationSnapshot(
                entries.Count(entry => entry.State == YouTubeCacheState.Queued),
                entries.Count(entry => entry.State == YouTubeCacheState.Downloading),
                entries.Count(entry => entry.State == YouTubeCacheState.Failed),
                active?.VideoId,
                active?.Phase,
                active?.Fraction ?? 0,
                entries
                    .Where(entry => entry.State == YouTubeCacheState.Failed)
                    .Select(entry => entry.Error)
                    .LastOrDefault(error => !string.IsNullOrWhiteSpace(error)));
        }
    }

    /// <summary>Expected generated caption sidecar when it exists on this machine.</summary>
    public string? PreparedSubtitlePath(string sourceUri)
    {
        if (!TryCanonical(sourceUri, out var canonical, out var videoId, out var selection))
            return null;
        lock (_gate)
        {
            if (_entries.TryGetValue(canonical, out var entry)
                && entry.PreparedSubtitlePath is { Length: > 0 } prepared)
                return File.Exists(prepared) ? prepared : null;
        }
        var path = _preparer.SubtitlePathFor(
            videoId,
            selection.IncludeVideo ? selection.Video : null,
            selection.Audio,
            selection.SubtitleLanguage,
            selection.IncludeThumbnail);
        return path is { Length: > 0 } && File.Exists(path) ? path : null;
    }

    /// <summary>For cache-management UI which removed files outside the queue.</summary>
    public void NotifyCacheChanged()
    {
        lock (_gate)
            _contentRevision++;
        Raise(readiness: true);
    }

    private async Task<YouTubePreparationResult> RunAsync(Entry entry)
    {
        try
        {
            await _slots.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                SetState(entry, YouTubeCacheState.Downloading);
                var progress = new InlineProgress(step => SetProgress(entry, step));
                var prepared = await _preparer.PrepareAsync(
                    entry.VideoId, entry.Selection, progress, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    entry.PreparedAssetPath = prepared.AssetPath;
                    entry.PreparedSubtitlePath = prepared.SubtitlePath;
                    entry.SubtitleChecked = true;
                }
                SetState(entry, YouTubeCacheState.Ready);
                return new YouTubePreparationResult(entry.SourceUri, prepared, null);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            lock (_gate)
            {
                entry.Error = failure is OperationCanceledException && _disposed
                    ? "application closed"
                    : failure.Message;
            }
            SetState(entry, YouTubeCacheState.Failed);
            return new YouTubePreparationResult(entry.SourceUri, null, entry.Error);
        }
    }

    private void SetState(Entry entry, YouTubeCacheState state)
    {
        lock (_gate)
        {
            entry.State = state;
            if (state == YouTubeCacheState.Ready)
            {
                entry.Phase = YouTubePreparePhase.Ready;
                entry.Fraction = 1;
                entry.Error = null;
                _contentRevision++;
            }
        }
        Raise(readiness: true);
    }

    private void SetProgress(Entry entry, YouTubePrepareProgress progress)
    {
        var changed = false;
        lock (_gate)
        {
            var fraction = Math.Clamp(progress.Fraction, 0, 1);
            changed = entry.Phase != progress.Phase || Math.Abs(entry.Fraction - fraction) >= 0.01;
            if (changed)
            {
                entry.Phase = progress.Phase;
                entry.Fraction = fraction;
            }
        }
        if (changed)
            Raise(readiness: false);
    }

    private bool IsAssetPresent(string videoId, YouTubeStreamSelection selection) =>
        File.Exists(_preparer.AssetPathFor(
            videoId,
            selection.IncludeVideo ? selection.Video : null,
            selection.Audio,
            selection.IncludeThumbnail));

    private static bool IsConcrete(YouTubeStreamSelection selection) =>
        (!selection.IncludeVideo || selection.Video is { Length: > 0 })
        && selection.Audio is { Length: > 0 };

    private static bool IsSubtitleComplete(Entry entry, YouTubeStreamSelection selection) =>
        selection.SubtitleLanguage is not { Length: > 0 }
        || entry.SubtitleChecked
        && (entry.PreparedSubtitlePath is not { Length: > 0 } path || File.Exists(path));

    private static bool TryCanonical(
        string sourceUri,
        out string canonical,
        out string videoId,
        out YouTubeStreamSelection selection)
    {
        canonical = "";
        videoId = "";
        selection = null!;
        if (!YouTubeSourceUri.TryParse(sourceUri, out var parsedId, out var parsedSelection))
            return false;
        videoId = parsedId;
        selection = parsedSelection;
        canonical = YouTubeSourceUri.Build(parsedId, parsedSelection);
        return true;
    }

    private void Raise(bool readiness)
    {
        try { Changed?.Invoke(); }
        catch { /* a readout must not fail a download */ }
        if (!readiness)
            return;
        try { ReadinessChanged?.Invoke(); }
        catch { /* a status subscriber must not fail a download */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _lifetime.Cancel();
    }

    private sealed class InlineProgress(Action<YouTubePrepareProgress> report) : IProgress<YouTubePrepareProgress>
    {
        public void Report(YouTubePrepareProgress value) => report(value);
    }
}
