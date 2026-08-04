using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCue2.Core.Model;
using S.Media.Source.YouTube;

namespace HaCue2.ViewModels;

/// <summary>
/// A YouTube video, made into a cue.
/// </summary>
/// <remarks>
/// <para>
/// This has its own window rather than a prompt because of what it does between the URL and the cue.
/// Resolving the manifest is a network round trip, the stream lists that come back are a real choice —
/// a 4 K VP9 leg and a 1080p H.264 leg are different decode costs on show night — and DOWNLOADING is a
/// progress bar measured in minutes. None of that fits a box with two fields and a verb.
/// </para>
/// <para>
/// And the download is not optional. The registry's youtube provider plays only from the prepared local
/// asset and refuses to fetch on the fire path, which is the right refusal: a GO that started a network
/// transfer would be a cue that begins four minutes late, on a machine that may have no network at the
/// venue at all. Preparing here is what makes the cue instant later, and what makes it work offline.
/// </para>
/// </remarks>
public sealed partial class YouTubeCueViewModel : ObservableObject
{
    private readonly IYouTubeGateway _gateway;
    private readonly YouTubePreparer _preparer;
    private readonly MediaCueNode? _editing;
    private YouTubeMediaManifest? _manifest;
    private CancellationTokenSource? _work;

    public YouTubeCueViewModel(
        CuesViewModel cues,
        IYouTubeGateway gateway,
        YouTubePreparer preparer,
        MediaCueNode? editing = null)
    {
        Cues = cues;
        _gateway = gateway;
        _preparer = preparer;
        _editing = editing;

        if (editing is not null)
        {
            Url = editing.MediaPath;
            Name = editing.Label;
        }
    }

    /// <summary>The preview shape, for the designer.</summary>
    public YouTubeCueViewModel()
        : this(null!, new YoutubeExplodeGateway(), new YouTubePreparer(new YoutubeExplodeGateway()))
    {
    }

    private CuesViewModel Cues { get; }

    public string Title => _editing is null ? "Add YouTube cue" : "Edit YouTube cue";

    /// <summary>Raised once the cue exists. The window closes on it.</summary>
    public event Action? Finished;

    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _problem = "";

    /// <summary>Everything below the URL is hidden until a manifest came back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrepare))]
    private bool _hasManifest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanPrepare))]
    private bool _isBusy;

    [ObservableProperty]
    private string _videoNote = "";

    [ObservableProperty]
    private string _cacheNote = "";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressNote = "";

    public bool IsIdle => !IsBusy;

    public bool CanPrepare => HasManifest && !IsBusy;

    public IReadOnlyList<string> VideoStreams { get; private set; } = [];

    public IReadOnlyList<string> AudioStreams { get; private set; } = [];

    public IReadOnlyList<string> SubtitleTracks { get; private set; } = [];

    [ObservableProperty]
    private int _videoIndex;

    [ObservableProperty]
    private int _audioIndex;

    [ObservableProperty]
    private int _subtitleIndex;

    /// <summary>Take only the sound. The cue then has no picture to place, which is a legitimate cue.</summary>
    [ObservableProperty]
    private bool _audioOnly;

    /// <summary>
    /// Embed the video's thumbnail as a still picture stream.
    /// </summary>
    /// <remarks>
    /// What makes an audio-only YouTube cue placeable: the thumbnail rides along as an attached picture
    /// exactly like MP3 cover art, so a music cue can put something on the screen without carrying the
    /// whole video.
    /// </remarks>
    [ObservableProperty]
    private bool _includeThumbnail;

    partial void OnVideoIndexChanged(int value) => RefreshCacheNote();

    partial void OnAudioIndexChanged(int value) => RefreshCacheNote();

    partial void OnAudioOnlyChanged(bool value) => RefreshCacheNote();

    partial void OnIncludeThumbnailChanged(bool value) => RefreshCacheNote();

    /// <summary>Asks YouTube what this video is and what streams it offers.</summary>
    [RelayCommand]
    private async Task ResolveAsync()
    {
        Problem = "";

        if (!YouTubeSourceUri.TryParse(Url.Trim(), out var videoId, out var selection))
        {
            Problem = "that is not a YouTube link — paste a watch, share or youtu.be URL";
            return;
        }

        var token = Restart();
        IsBusy = true;

        try
        {
            var manifest = await _gateway.GetManifestAsync(videoId, token).ConfigureAwait(true);
            _manifest = manifest;

            VideoStreams = [.. manifest.VideoStreams.Select(Describe)];
            AudioStreams = [.. manifest.AudioStreams.Select(Describe)];
            SubtitleTracks =
                ["(no subtitles)", .. manifest.CaptionTracks.Select(Describe)];

            OnPropertyChanged(nameof(VideoStreams));
            OnPropertyChanged(nameof(AudioStreams));
            OnPropertyChanged(nameof(SubtitleTracks));

            // What the URI already asked for, if anything, else the best on offer — the lists come back
            // in the module's own preference order, so index 0 is that.
            VideoIndex = Pick(manifest.VideoStreams.Select(item => item.Descriptor), selection.Video);
            AudioIndex = selection.Audio is { Length: > 0 }
                ? Pick(manifest.AudioStreams.Select(item => item.Descriptor), selection.Audio)
                // Nothing asked for: the track the video calls its own language, not simply the first —
                // a video with a dub track can list the dub ahead of the original.
                : Math.Max(0, manifest.AudioStreams.ToList().FindIndex(item => item.IsDefaultLanguage));
            SubtitleIndex = selection.SubtitleLanguage is { Length: > 0 } language
                ? Math.Max(0, manifest.CaptionTracks.ToList()
                    .FindIndex(item => item.LanguageCode == language) + 1)
                : 0;

            AudioOnly = !selection.IncludeVideo;
            IncludeThumbnail = selection.IncludeThumbnail;

            if (Name.Trim().Length == 0)
                Name = manifest.Title;

            VideoNote = $"{manifest.Title} · {manifest.Author}"
                        + (manifest.Duration is { } length ? $" · {Clock(length)}" : "");

            HasManifest = true;
            RefreshCacheNote();
        }
        catch (OperationCanceledException)
        {
            // superseded, or the window closed
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The provider's own words. The app must not promise every link is playable.
            Problem = $"could not resolve — {failure.Message}";
            HasManifest = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads the chosen streams into the shared cache, then makes the cue.</summary>
    [RelayCommand]
    private async Task PrepareAsync()
    {
        if (_manifest is not { } manifest)
            return;

        Problem = "";
        var token = Restart();
        IsBusy = true;
        Progress = 0;
        ProgressNote = "starting…";

        var selection = Selection();

        try
        {
            var prepared = await _preparer.PrepareAsync(
                manifest.VideoId,
                selection,
                new Progress<YouTubePrepareProgress>(step =>
                {
                    Progress = Math.Clamp(step.Fraction, 0, 1);
                    ProgressNote = $"{Phase(step.Phase)} · {Progress * 100:0}%";
                }),
                token).ConfigureAwait(true);

            // The RESOLVED descriptors, not the requested ones: "best" is a policy that means something
            // different next month, and a show must play the same streams it was built against.
            var uri = YouTubeSourceUri.Build(manifest.VideoId, prepared.ResolvedSelection);
            var duration = (int)Math.Round(manifest.Duration?.TotalMilliseconds ?? 0);

            var cue = _editing;

            if (cue is null)
            {
                cue = Cues.AddSourceCue(uri, Name, duration);
            }
            else
            {
                Cues.SetSource(cue.Id, uri, Name, duration);
            }

            if (cue is not null && prepared.SubtitlePath is { Length: > 0 } subtitles)
            {
                cue.Subtitles.Clear();
                cue.Subtitles.Add(new SubtitleSelection { Path = subtitles });
            }

            Finished?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ProgressNote = "cancelled";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Problem = $"could not download — {failure.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Abandons an in-flight resolve or download. The cache keeps whatever finished.</summary>
    public void Cancel()
    {
        try
        {
            _work?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already finished and cleaned up
        }
    }

    /// <summary>Cancels whatever was running and hands out a token for what is about to.</summary>
    private CancellationToken Restart()
    {
        Cancel();
        _work?.Dispose();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    private YouTubeStreamSelection Selection()
    {
        var video = !AudioOnly && _manifest is { } manifest
                    && VideoIndex >= 0 && VideoIndex < manifest.VideoStreams.Count
            ? manifest.VideoStreams[VideoIndex].Descriptor
            : null;

        var audio = _manifest is { } withAudio
                    && AudioIndex >= 0 && AudioIndex < withAudio.AudioStreams.Count
            ? withAudio.AudioStreams[AudioIndex].Descriptor
            : null;

        var subtitle = _manifest is { } withCaptions
                       && SubtitleIndex > 0 && SubtitleIndex <= withCaptions.CaptionTracks.Count
            ? withCaptions.CaptionTracks[SubtitleIndex - 1].LanguageCode
            : null;

        return new YouTubeStreamSelection(video, audio, subtitle)
        {
            IncludeVideo = !AudioOnly,
            IncludeThumbnail = IncludeThumbnail,
        };
    }

    /// <summary>
    /// Whether this exact selection is already on disk.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud, because it is the difference between a button that finishes instantly and
    /// one that downloads 300 MB — and because changing the audio leg alone keeps the video leg cached,
    /// which is not obvious from anything else on screen.
    /// </remarks>
    private void RefreshCacheNote()
    {
        if (_manifest is not { } manifest)
        {
            CacheNote = "";
            return;
        }

        var selection = Selection();

        CacheNote = _preparer.IsPrepared(
            manifest.VideoId, selection.Video, selection.Audio, selection.IncludeThumbnail)
            ? "already downloaded — adding is instant"
            : "not downloaded yet — adding will fetch it";
    }

    private static int Pick(IEnumerable<string> descriptors, string? wanted)
    {
        if (wanted is not { Length: > 0 })
            return 0;

        var at = descriptors.ToList().IndexOf(wanted);
        return at >= 0 ? at : 0;
    }

    private static string Describe(YouTubeVideoStreamInfo stream) =>
        $"{stream.QualityLabel} · {stream.Codec} · {Size(stream.SizeBytes)}";

    private static string Describe(YouTubeAudioStreamInfo stream) =>
        $"{stream.Codec} · {stream.BitrateBps / 1000} kbps · {Size(stream.SizeBytes)}"
        + (stream.Language is { Length: > 0 } language
            ? $" · {language}{(stream.IsDefaultLanguage ? " (default)" : "")}"
            : "");

    private static string Describe(YouTubeCaptionTrackInfo track) =>
        $"{track.LanguageName} ({track.LanguageCode}){(track.IsAutoGenerated ? " · auto" : "")}";

    private static string Size(long bytes) =>
        bytes <= 0 ? "?" : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private static string Clock(TimeSpan length) =>
        length.TotalHours >= 1
            ? length.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : length.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string Phase(YouTubePreparePhase phase) => phase switch
    {
        YouTubePreparePhase.Resolving => "resolving",
        YouTubePreparePhase.DownloadingVideo => "downloading video",
        YouTubePreparePhase.DownloadingAudio => "downloading audio",
        YouTubePreparePhase.DownloadingThumbnail => "downloading thumbnail",
        YouTubePreparePhase.Remuxing => "assembling",
        _ => "ready",
    };
}
