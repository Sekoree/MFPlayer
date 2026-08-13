using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.Encode.FFmpeg;

namespace HaCue2.Engine;

/// <summary>What one record or stream target is doing, for the devices list and the status pane.</summary>
/// <param name="Id">The audio line's or video output's id.</param>
/// <param name="Destination">The file being written or the URL being pushed to, once armed.</param>
/// <param name="Problem">Why it is not armed, when the operator asked for it and it failed.</param>
/// <param name="Dropped">
/// Frames or audio chunks the encoder could not keep up with. The encode queue is bounded and drops
/// the OLDEST under a burst, deliberately: a recording that blocked would stall the show it is
/// recording. That makes drops the honest measure of a recording going wrong, and the only warning an
/// operator gets before the file starts gapping - so it is carried here rather than left in a metric
/// nobody reads.
/// </param>
public sealed record RecorderStatus(
    Guid Id,
    string Name,
    bool IsVideo,
    bool Armed,
    string? Destination,
    string? Problem,
    long BytesWritten = 0,
    bool Behind = false,
    long Dropped = 0);

/// <summary>
/// The show's recorders and streams.
/// </summary>
/// <remarks>
/// <para>
/// One encode session per armed target. Audio recorders join <see cref="ProjectPatchBay"/> as ordinary
/// terminals, so what lands in the file is exactly what the operator patched to that line - recording
/// the foldback mix instead of the program needs no feature, only a different patch. Video recorders sit
/// behind a <see cref="RecordVideoOutput"/> the compositor has been rendering into all along.
/// </para>
/// <para>
/// <b>Arming is explicit and disarming is a flush.</b> A recording that is never disarmed is a file with
/// no trailer, which most players will not open - so disposal disarms everything and waits for the
/// trailers, and that is the one place in the show's teardown allowed to take a moment.
/// </para>
/// </remarks>
public sealed class ProjectRecorders : IAsyncDisposable
{
    private sealed class Armed
    {
        public required FFmpegEncodeSession Session { get; init; }
        public required string Destination { get; init; }
        public RecordVideoOutput? Video { get; init; }

        /// <summary>Present on a continuous recording: the thing writing black and silence in the gaps.</summary>
        public ContinuousEncodeCarrier? Carrier { get; init; }
    }

    private readonly Dictionary<Guid, Armed> _armed = [];
    private readonly Dictionary<Guid, string> _problems = [];
    private readonly Dictionary<Guid, RecordVideoOutput> _videoOutputs;
    private readonly ProjectPatchBay? _bay;
    private readonly string _defaultDirectory;

    private HaCueProject _project;

    internal ProjectRecorders(
        HaCueProject project,
        ProjectPatchBay? bay,
        IReadOnlyDictionary<Guid, RecordVideoOutput> videoOutputs,
        string defaultDirectory)
    {
        _project = project;
        _bay = bay;
        _videoOutputs = new Dictionary<Guid, RecordVideoOutput>(videoOutputs);
        _defaultDirectory = defaultDirectory;
    }

    /// <summary>Raised when a recorder arms, disarms or fails, so the devices list can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>Every record and stream target the project defines, armed or not.</summary>
    public IReadOnlyList<RecorderStatus> Status()
    {
        var rows = new List<RecorderStatus>();

        foreach (var line in _project.AudioLines.Where(IsRecording))
            rows.Add(Row(line.Id, line.Name, isVideo: false));

        foreach (var output in _project.VideoOutputs.Where(IsRecording))
            rows.Add(Row(output.Id, output.Name, isVideo: true));

        return rows;
    }

    private RecorderStatus Row(Guid id, string name, bool isVideo)
    {
        _problems.TryGetValue(id, out var problem);

        if (!_armed.TryGetValue(id, out var armed))
            return new RecorderStatus(id, name, isVideo, Armed: false, null, problem);

        var metrics = armed.Session.GetMetrics();

        return new RecorderStatus(
            id,
            name,
            isVideo,
            Armed: true,
            armed.Destination,
            // A sink that has faulted is reported through the same field a refusal to arm uses: from
            // the operator's side "it is not recording" is one condition, however it came about.
            metrics.Sinks.FirstOrDefault(sink => !sink.Healthy)?.Error,
            metrics.Sinks.Sum(sink => sink.BytesWritten),
            metrics.VideoQueueDepth > 0,
            metrics.VideoFramesDropped + metrics.AudioChunksDropped);
    }

    /// <summary>True when the target is armed and writing.</summary>
    public bool IsArmed(Guid id) => _armed.ContainsKey(id);

    /// <summary>Arms everything the project asked to start with the show (register item 30's opt-in).</summary>
    public void ArmStartupTargets()
    {
        foreach (var line in _project.AudioLines.Where(line => IsRecording(line) && line.Record?.ArmWithShow == true))
            Arm(line.Id);

        foreach (var output in _project.VideoOutputs.Where(item => IsRecording(item) && item.Record?.ArmWithShow == true))
            Arm(output.Id);
    }

    /// <summary>
    /// Opens a target's file or connection and starts writing.
    /// </summary>
    /// <returns>Why it could not arm, or null once it is recording.</returns>
    public string? Arm(Guid id)
    {
        if (_armed.ContainsKey(id))
            return null;

        var problem = ArmCore(id);

        if (problem is null)
            _problems.Remove(id);
        else
            _problems[id] = problem;

        Changed?.Invoke(this, EventArgs.Empty);
        return problem;
    }

    private string? ArmCore(Guid id)
    {
        var line = _project.AudioLines.FirstOrDefault(item => item.Id == id && IsRecording(item));
        var output = _project.VideoOutputs.FirstOrDefault(item => item.Id == id && IsRecording(item));

        if (line is null && output is null)
            return "that is not a record or stream target";

        var target = line?.Record ?? output?.Record;

        if (target is null)
            return "this target has nowhere to write - set a folder and a filename pattern";

        var streaming = line?.Kind == AudioLineKind.Stream || output?.Kind == VideoOutputKind.Stream;

        return streaming ? ArmStream(id, line, output, target) : ArmFile(id, line, output, target);
    }

    private string? ArmFile(
        Guid id, AudioLineDefinition? line, VideoOutputDefinition? output, RecordTarget target)
    {
        var pattern = target.Pattern.Length > 0
            ? target.Pattern
            : RecordPattern.Default + (line is not null
                ? RecordFormatNames.DefaultAudio
                : RecordFormatNames.DefaultVideo);

        if (RecordFormatNames.Problem(pattern, carriesVideo: output is not null) is { } wrong)
            return wrong;

        var format = RecordFormats.Find(pattern)!;
        var directory = target.Directory.Length > 0 ? target.Directory : _defaultDirectory;

        string path;

        try
        {
            path = Reserve(directory, pattern, format.Extension);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            return $"could not open a file in “{directory}”: {failure.Message}";
        }

        var problem = Start(id, line, output, format, new FileEncodeTarget(path), path, target.Continuous);

        if (problem is not null)
            TryDeleteEmptyReservation(path);

        return problem;
    }

    private string? ArmStream(
        Guid id, AudioLineDefinition? line, VideoOutputDefinition? output, RecordTarget target)
    {
        if (target.Url.Length == 0)
            return "this stream has no URL to push to";

        // The container follows the protocol rather than a filename: an RTMP ingest takes FLV and
        // nothing else, and SRT/UDP take MPEG-TS. Letting a pattern's extension decide here would
        // produce a stream no ingest would accept.
        var extension = target.Url.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) ? ".flv" : ".ts";
        var format = RecordFormats.All.First(item => item.Extension == extension);

        var encodeTarget = target.Url.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)
            ? UrlEncodeTarget.Rtmp(target.Url)
            : target.Url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)
                ? UrlEncodeTarget.Rtsp(target.Url)
                : UrlEncodeTarget.Srt(target.Url);

        // A stream is continuous whatever the document says: an ingest drops a connection that stops
        // sending, so a stream that went quiet between cues would simply die.
        return Start(id, line, output, format, encodeTarget, Redact(target.Url), continuous: true);
    }

    private string? Start(
        Guid id,
        AudioLineDefinition? line,
        VideoOutputDefinition? output,
        RecordFormats.RecordFormat format,
        EncodeIoTarget encodeTarget,
        string destination,
        bool continuous)
    {
        var video = output is not null ? Video(output.Id) : null;

        if (output is not null && video is null)
            return "that output was never opened, so there is nothing to record";

        var negotiated = video?.Negotiated;
        var composition = output?.CompositionId is { } compositionId
            ? _project.Compositions.FirstOrDefault(item => item.Id == compositionId)
            : null;

        // The negotiated format is the truth when the compositor has already attached; the composition
        // is the fallback for arming before the first frame. Getting this wrong scales every frame.
        var width = negotiated?.Width ?? composition?.Width ?? 0;
        var height = negotiated?.Height ?? composition?.Height ?? 0;
        var frameRate = composition?.ExactFrameRate ?? Rational.Zero;

        var channels = line?.Channels ?? 0;
        var rate = _project.AudioPatch.MixSampleRate;

        if (continuous && video is not null
            && (width <= 0 || height <= 0
                || frameRate.Numerator <= 0 || frameRate.Denominator <= 0))
            return "a continuous recording needs the composition's size and frame rate";

        FFmpegEncodeSession session;

        try
        {
            session = FFmpegEncodeSession.Create(
                // Follow RecordVideoOutput's negotiated VideoFormat so fractional/exact canvas rates
                // reach the encoder unchanged; the integer Fps override would retune 60000/1001 to 60.
                RecordFormats.Options(format, channels, rate, width, height, fps: 0), encodeTarget, rate);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Reported, never thrown: a codec this build lacks, an unreachable ingest and a full disk
            // all arrive here, and none of them is a reason to take the show down.
            return failure.Message;
        }

        ContinuousEncodeCarrier? carrier = null;

        if (continuous)
        {
            try
            {
                carrier = new ContinuousEncodeCarrier(
                    session,
                    video is null ? 0 : width,
                    video is null ? 0 : height,
                    video is null ? Rational.Zero : frameRate);
                carrier.Start();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                return Abandon(session, null, failure.Message);
            }
        }

        if (line is not null)
        {
            if (_bay is null)
                return Abandon(session, carrier, "there is no audio bay to record from");

            if ((carrier?.CombinedAudioSink ?? session.CombinedAudioSink) is not { } sink)
                return Abandon(session, carrier, "that format carries no audio");

            if (_bay.AttachRecorder(_project, line.Id, sink) is { } refused)
                return Abandon(session, carrier, refused);
        }

        if (video is not null)
        {
            if ((carrier?.VideoSink ?? session.VideoSink) is not { } videoSink)
                return Abandon(session, carrier, "that format carries no video");

            video.Arm(videoSink);
        }

        // The carrier only fills for the legs something is actually feeding; telling it which are live
        // is what stops it writing black over a picture the compositor is already supplying.
        carrier?.SetPlaybackActive(video is not null, line is not null);

        _armed[id] = new Armed
        {
            Session = session, Destination = destination, Video = video, Carrier = carrier,
        };

        return null;
    }

    private static string Abandon(FFmpegEncodeSession session, ContinuousEncodeCarrier? carrier, string problem)
    {
        carrier?.Dispose();
        session.Dispose();
        return problem;
    }

    /// <summary>
    /// Stops a recording and finishes the file.
    /// </summary>
    /// <remarks>
    /// The order is the whole point: detach FIRST so nothing can submit into a session being finalized,
    /// then flush and write the trailer. Reversing it truncates the file it was meant to complete.
    /// </remarks>
    public async Task DisarmAsync(Guid id)
    {
        if (!_armed.Remove(id, out var armed))
            return;

        armed.Video?.Disarm();

        if (_project.AudioLines.Any(line => line.Id == id))
            _bay?.DetachRecorder(id);

        // Before the flush, for the same reason the detach is: the carrier's own thread submits black
        // and silence, and it must stop reaching for a session that is being finalized.
        armed.Carrier?.Dispose();

        try
        {
            await armed.Session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            _problems[id] = $"the recording may be incomplete: {failure.Message}";
        }
        finally
        {
            armed.Session.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adopts a reloaded document, so status keeps naming what the operator sees.</summary>
    /// <remarks>
    /// Armed sessions are LEFT ALONE. Their file is already open with a chosen format, and an edit to
    /// the pattern cannot retroactively rename it - the change applies to the next arm, which is the
    /// same rule an encode setting follows everywhere else.
    /// </remarks>
    public void Adopt(HaCueProject project) => _project = project ?? throw new ArgumentNullException(nameof(project));

    private RecordVideoOutput? Video(Guid id) => _videoOutputs.GetValueOrDefault(id);

    private static bool IsRecording(AudioLineDefinition line) =>
        line.Kind is AudioLineKind.FileRecord or AudioLineKind.Stream;

    private static bool IsRecording(VideoOutputDefinition output) =>
        output.Kind is VideoOutputKind.Record or VideoOutputKind.Stream;

    /// <summary>
    /// Expands the pattern and takes the first free name, creating the file as it goes.
    /// </summary>
    /// <remarks>
    /// Creating it is what makes the name TAKEN: two outputs arming at once, or a second HaCue2 on the
    /// same folder, would otherwise both pick the same free name and one would overwrite the other's
    /// show. The reservation is handed to the muxer, which opens it for writing.
    /// </remarks>
    private static string Reserve(string directory, string pattern, string extension)
    {
        Directory.CreateDirectory(directory);

        var stem = Path.GetFileNameWithoutExtension(pattern);
        var now = DateTimeOffset.Now;

        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var name = RecordPattern.Expand(stem, new RecordPattern.RecordNaming(Timestamp: now, Attempt: attempt));

            // A pattern with no {n} has nothing to vary per attempt, so the counter is appended to keep
            // the loop able to make progress rather than testing the same name ten thousand times.
            if (attempt > 0 && !stem.Contains("{n}", StringComparison.OrdinalIgnoreCase))
                name += $"-{attempt + 1}";

            var candidate = Path.Combine(directory, name + extension);

            try
            {
                using var reservation = new FileStream(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken, by an earlier show or by another arm a moment ago. Try the next one.
            }
        }

        throw new IOException($"every name this pattern can produce is taken in “{directory}”");
    }

    private static void TryDeleteEmptyReservation(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Best effort. The reason arming failed is the one worth reporting, and a stray empty file
            // must not replace it with a complaint about cleanup.
        }
    }

    /// <summary>
    /// A stream URL with its key removed, for logs and the devices list.
    /// </summary>
    /// <remarks>
    /// An ingest URL's last segment is a credential - printing it into a log the operator later pastes
    /// into a bug report hands out the ability to broadcast as them.
    /// </remarks>
    internal static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return "stream";

        var path = parsed.AbsolutePath.TrimEnd('/');
        var cut = path.LastIndexOf('/');

        return cut > 0
            ? $"{parsed.Scheme}://{parsed.Host}{path[..cut]}/•••"
            : $"{parsed.Scheme}://{parsed.Host}";
    }

    public async ValueTask DisposeAsync()
    {
        // Every armed recording is finished rather than dropped: a file with no trailer is one most
        // players refuse to open, and the show closing is not a reason to lose the recording of it.
        foreach (var id in _armed.Keys.ToList())
            await DisarmAsync(id).ConfigureAwait(false);
    }
}
