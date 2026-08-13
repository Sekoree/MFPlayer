using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Controls;
using HaCue2.Machine;
using HaCue2.Presentation;

namespace HaCue2.ViewModels;

/// <summary>
/// Screen 04b - the clip editor: a file's waveform, its trim window, and a frame from wherever the
/// playhead is.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the trim fields alone could not express the thing they are most needed for.
/// "Thirty minutes off the front and ten off the end of a two-hour recording" was <c>1800.0</c> and
/// <c>length − 600</c> typed into two boxes, against a length nothing on screen showed - arithmetic
/// done in somebody's head, against a number they had to get elsewhere, to place a cut they could not
/// see.
/// </para>
/// <para>
/// <b>Opened for ANY media cue</b>, not only one inside a timeline group. The timeline sheet can
/// already trim by dragging a clip's edges, but it only reaches cues that are on a timeline, and a lane
/// scaled to a whole scene is not something you can land 30:00 on.
/// </para>
/// <para>
/// <b>The scan is background, cancellable and cached.</b> A long recording takes seconds to scan and a
/// ProRes master takes a minute; the fields, the handles and the preview all work from the moment the
/// window opens, and the waveform fills in behind them.
/// </para>
/// </remarks>
public sealed partial class ClipEditorViewModel : ObservableObject, IDisposable
{
    private readonly ProjectJournal? _journal;
    private readonly MediaCueNode? _cue;
    private readonly string _cacheRoot;
    private readonly long? _waveformCacheBytes;
    private CancellationTokenSource? _scan;
    private CancellationTokenSource? _frame;
    private IDisposable? _drag;

    /// <summary>The preview editor, for a window with no document behind it.</summary>
    public ClipEditorViewModel()
    {
        Title = "Clip";
        _cacheRoot = "";
        Length = TimeSpan.FromMinutes(4);
    }

    public ClipEditorViewModel(
        ProjectJournal journal, MediaCueNode cue, string resolvedPath, TimeSpan? length, string cacheRoot,
        long? waveformCacheBytes = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(cue);

        _journal = journal;
        _cue = cue;
        _cacheRoot = cacheRoot;
        _waveformCacheBytes = waveformCacheBytes;
        Path = resolvedPath;
        Length = length;
        Title = $"Clip · Q{CuePresentation.Number(cue.Number)} {cue.Label}";
    }

    public string Title { get; }

    /// <summary>Where the file is, so a scan and a frame grab have something to open.</summary>
    public string Path { get; } = "";

    /// <summary>
    /// What the probe said the file runs for.
    /// </summary>
    /// <remarks>
    /// Null until something has looked, and everything here says so rather than assuming: without a
    /// length there is no fraction to draw a handle at and no way to resolve a from-the-end time.
    /// </remarks>
    public TimeSpan? Length { get; }

    public bool IsProbed => Length is { TotalMilliseconds: > 0 };

    /// <summary>The file's length as the header states it - the number that used to be missing.</summary>
    public string LengthLabel =>
        Length is { } length ? ClipTimes.Format((int)length.TotalMilliseconds) : "not probed";

    public string Hint => IsProbed
        ? $"drag the handles, or type · {ClipTimes.Syntax}"
        : "nothing has probed this file, so it cannot be shown - the trim still travels with the show";

    // ── the waveform ──────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanLabel))]
    private IReadOnlyList<float>? _peaks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanLabel))]
    private bool _isScanning;

    public string ScanLabel => IsScanning
        ? "reading the file…"
        : Peaks is { Count: > 0 } ? "" : "no waveform - this file has no audio this machine can read";

    /// <summary>
    /// Starts the scan, reading a cached one first.
    /// </summary>
    /// <remarks>
    /// Called when the window opens rather than in the constructor: a view-model that reached for a
    /// file on construction is one no preview and no test could build.
    /// </remarks>
    public async void Begin()
    {
        if (Path.Length == 0)
            return;

        if (WaveformCache.Read(_cacheRoot, Path) is { } cached)
        {
            Peaks = cached;
            return;
        }

        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        var token = _scan.Token;

        IsScanning = true;

        try
        {
            // The partial handler is what makes a long file fill in left to right instead of looking
            // broken for a minute. Marshalled by the caller's synchronization context.
            var peaks = await MediaScan.WaveformAsync(
                Path,
                cancellationToken: token,
                onPartial: partial =>
                {
                    if (!token.IsCancellationRequested)
                        Peaks = partial;
                }).ConfigureAwait(true);

            if (token.IsCancellationRequested)
                return;

            Peaks = peaks;

            if (peaks is not null)
                WaveformCache.Write(_cacheRoot, Path, peaks, _waveformCacheBytes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing the window can cancel the Task.Run before its delegate starts. In that case the
            // machine scanner never gets a chance to turn cancellation into a normal null result.
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsScanning = false;
        }
    }

    // ── the trim window ───────────────────────────────────────────────────────────────────────

    /// <summary>Where the in-point sits, as a fraction of the file - what the handle is drawn at.</summary>
    public double TrimInFraction => Fraction(_cue?.TrimInMs ?? 0);

    /// <summary>Where the out-point sits. An unset out-point is the END, which is where it is drawn.</summary>
    public double TrimOutFraction =>
        _cue is { TrimOutMs: > 0 } cue ? Fraction(cue.TrimOutMs) : 1;

    public string TrimInText => ClipTimes.Format(_cue?.TrimInMs ?? 0);

    public string TrimOutText =>
        _cue is { TrimOutMs: > 0 } cue ? ClipTimes.Format(cue.TrimOutMs) : "end";

    /// <summary>What the cue will actually play, which is the number the operator is aiming at.</summary>
    public string KeptLabel
    {
        get
        {
            if (_cue?.TrimmedLength(Length) is not { } kept)
                return "-";

            return $"{ClipTimes.Format((int)kept.TotalMilliseconds)} of {LengthLabel}";
        }
    }

    /// <summary>
    /// Writes a typed in-point, or refuses.
    /// </summary>
    /// <remarks>
    /// Refusing rather than clamping silently: a value the app quietly changed is one the operator
    /// believes they set, and a trim is exactly where that matters.
    /// </remarks>
    public string? SetTrimIn(string text)
    {
        if (ClipTimes.Parse(text, Length) is not { } milliseconds)
            return $"“{text}” is not a time - {ClipTimes.Syntax}";

        if (FileLengthMilliseconds is { } fileEnd && milliseconds >= fileEnd)
            return "the in-point would be at or past the end of the file";

        if (_cue is { TrimOutMs: > 0 } cue && milliseconds >= cue.TrimOutMs)
            return "the in-point would be at or past the out-point";

        Write(trimIn: true, milliseconds, "set the clip's in-point");
        Refresh();
        return null;
    }

    /// <summary>Writes a typed out-point, or refuses. "end" clears it.</summary>
    public string? SetTrimOut(string text)
    {
        if (text.Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            Write(trimIn: false, 0, "play the clip to the end");
            Refresh();
            return null;
        }

        if (ClipTimes.Parse(text, Length) is not { } milliseconds)
            return $"“{text}” is not a time - {ClipTimes.Syntax}";

        if (FileLengthMilliseconds is { } fileEnd && milliseconds > fileEnd)
            return "the out-point would be past the end of the file";

        if (_cue is { } cue && milliseconds <= cue.TrimInMs)
            return "the out-point would be at or before the in-point";

        Write(trimIn: false, milliseconds, "set the clip's out-point");
        Refresh();
        return null;
    }

    /// <summary>
    /// A drag on the waveform: a handle, or the playhead.
    /// </summary>
    /// <remarks>
    /// The whole drag is ONE undo step, like a clip drag on the timeline: a handle dragged across a
    /// two-hour file emits a write per pointer move, and an undo stack full of them would bury every
    /// real change the operator made.
    /// </remarks>
    public void Apply(TrimHandle handle, double at)
    {
        if (FileLengthMilliseconds is not { } fileEnd)
            return;

        var milliseconds = (int)Math.Round(Math.Clamp(at, 0, 1) * fileEnd);

        switch (handle)
        {
            case TrimHandle.In when _cue is { } cue:
                // Never past the out-point: a window whose ends crossed would be a cue that plays
                // nothing, reported by the validator long after the drag that caused it.
                var ceiling = cue.TrimOutMs > 0 ? cue.TrimOutMs - 1 : fileEnd - 1;
                var trimIn = Math.Min(milliseconds, Math.Max(0, ceiling));
                Begin("move the clip's in-point");
                Write(trimIn: true, trimIn, "move the clip's in-point");
                PreviewAt(trimIn);
                break;

            case TrimHandle.Out when _cue is { } cue:
                var trimOut = Math.Min(
                    Math.Max(milliseconds, cue.TrimInMs + 1),
                    fileEnd);
                Begin("move the clip's out-point");
                Write(trimIn: false, trimOut, "move the clip's out-point");
                // Out is an exclusive boundary. The marker and label sit exactly on it, while the
                // picture request sits immediately before it so EOF is not used as a decode position.
                PreviewAt(trimOut, frameBeforePosition: true);
                break;

            default:
                Scrub(milliseconds);
                return;
        }

        Refresh();
    }

    /// <summary>Ends the drag, closing its undo step.</summary>
    public void EndGesture()
    {
        _drag?.Dispose();
        _drag = null;
        _journal?.CloseGroup();
    }

    private void Begin(string description) =>
        _drag ??= _journal?.Composite(description, "cues");

    /// <summary>
    /// Writes one end of the trim window through the journal.
    /// </summary>
    /// <remarks>
    /// The accessors read and write the CUE rather than a captured value, because a drag emits many of
    /// these and each one has to know what it is replacing at the moment it runs - and because that is
    /// what lets consecutive writes on the same property coalesce into one undo step.
    /// </remarks>
    private void Write(bool trimIn, int milliseconds, string description)
    {
        if (_journal is null || _cue is not { } cue)
            return;

        _journal.Do(trimIn
            ? new SetValueCommand<int>(
                cue.Id, "trimIn", "cues",
                () => cue.TrimInMs, value => cue.TrimInMs = value, milliseconds, description)
            : new SetValueCommand<int>(
                cue.Id, "trimOut", "cues",
                () => cue.TrimOutMs, value => cue.TrimOutMs = value, milliseconds, description));
    }

    // ── the playhead and its picture ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadLabel))]
    private double _playhead;

    public string PlayheadLabel => Length is { } length
        ? ClipTimes.Format((int)(Playhead * length.TotalMilliseconds))
        : "-";

    /// <summary>The frame under the playhead, ready to draw. Null when this point has no picture.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrame))]
    private Avalonia.Media.Imaging.Bitmap? _preview;

    public bool HasFrame => Preview is not null;

    /// <summary>
    /// A decoded frame as a bitmap.
    /// </summary>
    /// <remarks>
    /// The conversion lives here rather than in the machine layer for the usual reason: that layer
    /// answers questions about a file and knows nothing about a windowing system. It hands over BGRA
    /// bytes, which is what every toolkit can take.
    /// </remarks>
    private static Avalonia.Media.Imaging.Bitmap? Draw(ClipFrame? frame)
    {
        if (frame is null)
            return null;

        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(frame.Width, frame.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Opaque);

        using var locked = bitmap.Lock();

        for (var row = 0; row < frame.Height; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                frame.Bgra,
                row * frame.Stride,
                locked.Address + (row * locked.RowBytes),
                Math.Min(frame.Stride, locked.RowBytes));
        }

        return bitmap;
    }

    private void Scrub(int milliseconds)
        => PreviewAt(milliseconds);

    /// <summary>Moves the visible preview target and fetches the frame that explains that position.</summary>
    private void PreviewAt(int milliseconds, bool frameBeforePosition = false)
    {
        if (Length is not { } length || length.TotalMilliseconds <= 0)
            return;

        var durationMs = FileLengthMilliseconds ?? 0;
        var positionMs = Math.Clamp(milliseconds, 0, durationMs);
        Playhead = positionMs / length.TotalMilliseconds;

        var frameMs = frameBeforePosition ? positionMs - 1 : positionMs;
        frameMs = Math.Clamp(frameMs, 0, Math.Max(0, durationMs - 1));
        _ = GrabAsync(TimeSpan.FromMilliseconds(frameMs));
    }

    /// <summary>
    /// Fetches the frame under the playhead, abandoning whichever one was in flight.
    /// </summary>
    /// <remarks>
    /// A scrub raises a request per pointer move and a decode takes a hundred milliseconds or so, so
    /// without cancelling the previous one the window would work through a queue of frames nobody is
    /// looking at any more and land on the wrong one.
    /// </remarks>
    private async Task GrabAsync(TimeSpan at)
    {
        if (Path.Length == 0)
            return;

        _frame?.Cancel();
        var request = new CancellationTokenSource();
        _frame = request;
        var token = request.Token;

        try
        {
            var frame = await MediaScan.FrameAsync(Path, at, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
                return;

            var drawn = Draw(frame);
            Preview?.Dispose();
            Preview = drawn;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer pointer position superseded this request before its worker began.
        }
        finally
        {
            if (ReferenceEquals(_frame, request))
                _frame = null;
            request.Dispose();
        }
    }

    private double Fraction(int milliseconds) =>
        Length is { TotalMilliseconds: > 0 } length
            ? Math.Clamp(milliseconds / length.TotalMilliseconds, 0, 1)
            : 0;

    /// <summary>The model stores clip positions as 32-bit milliseconds.</summary>
    private int? FileLengthMilliseconds => Length is { TotalMilliseconds: > 0 } length
        ? (int)Math.Min(int.MaxValue, Math.Round(length.TotalMilliseconds))
        : null;

    /// <summary>Re-announces everything derived from the cue, after an edit here or an undo anywhere.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(TrimInFraction));
        OnPropertyChanged(nameof(TrimOutFraction));
        OnPropertyChanged(nameof(TrimInText));
        OnPropertyChanged(nameof(TrimOutText));
        OnPropertyChanged(nameof(KeptLabel));
    }

    /// <summary>The last refusal from a typed field, or nothing.</summary>
    [ObservableProperty]
    private string _problem = "";

    /// <summary>Records what a field refused, and re-reads the value it rejected.</summary>
    public void NoteProblem(string? problem)
    {
        Problem = problem ?? "";

        // Re-announced either way: on success the field reformats what was typed, and on failure it
        // goes back to the value the document still has rather than leaving text that was not accepted.
        Refresh();
    }

    public void Dispose()
    {
        Preview?.Dispose();
        _scan?.Cancel();
        _scan?.Dispose();
        var frame = _frame;
        _frame = null;
        frame?.Cancel();
        _drag?.Dispose();
    }
}
