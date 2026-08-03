namespace HaCue2.Core.Model;

/// <summary>
/// Which recording formats a project may name.
/// </summary>
/// <remarks>
/// <para>
/// The document half of the format question, and it lives here rather than beside the encoder because
/// it is a rule about what a PROJECT may say. Project status validates patterns without a session, an
/// encoder or a machine — a bad extension has to be findable at the get-in, on a laptop, hours before
/// anybody arms anything.
/// </para>
/// <para>
/// The encoder half — which container and codecs each extension resolves to — belongs with the encoder
/// (<c>HaCue2.Engine.RecordFormats</c>), which covers exactly this list. The two are checked against
/// each other by a test, so an extension offered here can never be one nothing knows how to write.
/// </para>
/// </remarks>
public static class RecordFormatNames
{
    /// <summary>Every extension a pattern may end in, in the order the UI offers them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [".mkv", ".mka", ".mp4", ".m4a", ".mov", ".ts", ".flv"];

    /// <summary>The ones that carry no picture — refused to a video output, fine for an audio line.</summary>
    public static IReadOnlyList<string> AudioOnly { get; } = [".mka", ".m4a"];

    /// <summary>The audio-only default, and what a record line starts with.</summary>
    public const string DefaultAudio = ".mka";

    /// <summary>The video default, and what a record output starts with.</summary>
    public const string DefaultVideo = ".mkv";

    /// <summary>
    /// Extensions somebody reasonably types that no build can mux, and what to use instead.
    /// </summary>
    /// <remarks>
    /// Raw <c>.flac</c> and <c>.wav</c> are the ones that catch people: lossless audio here is FLAC
    /// inside Matroska, which is <c>.mka</c>. Naming the alternative is the whole value of this table —
    /// "unsupported format" leaves an operator guessing at the one thing they need to type.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Alternatives { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".flac"] = ".mka",
            [".wav"] = ".mka",
            [".aiff"] = ".mka",
            [".ogg"] = ".mka",
            [".opus"] = ".mka",
            [".mp3"] = ".mka",
            [".webm"] = ".mkv",
            [".avi"] = ".mkv",
            [".wmv"] = ".mkv",
            [".m4v"] = ".mp4",
            [".mpg"] = ".ts",
            [".mpeg"] = ".ts",
        };

    /// <summary>
    /// How each format reads in a picker or a status line.
    /// </summary>
    /// <remarks>
    /// The codecs are named because they are the operator's real question — "will this be lossless"
    /// and "will this open on the editor's machine" are both answered by the codec, not the extension.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Summaries { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mkv"] = "Matroska — H.264 video, lossless FLAC audio",
            [".mka"] = "Matroska audio — lossless FLAC",
            [".mp4"] = "MP4 — H.264 video, AAC audio",
            [".m4a"] = "MP4 audio — AAC",
            [".mov"] = "QuickTime — H.264 video, AAC audio",
            [".ts"] = "MPEG-TS — H.264 video, AAC audio",
            [".flv"] = "FLV — H.264 video, AAC audio",
        };

    /// <summary>How the format a filename names reads, or null when it names none this build writes.</summary>
    public static string? Describe(string fileName) =>
        Summaries.GetValueOrDefault(Path.GetExtension(fileName ?? ""));

    /// <summary>True when this build can write the format the filename names.</summary>
    public static bool IsKnown(string fileName) =>
        All.Contains(Path.GetExtension(fileName ?? ""), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why a pattern cannot be recorded, or null when it can.
    /// </summary>
    /// <param name="carriesVideo">
    /// Whether the caller has a picture to write. An audio-only container named by a video output is
    /// refused: the recording would silently drop the video leg, producing a file that opens, plays,
    /// and is missing the half the operator was recording.
    /// </param>
    public static string? Problem(string pattern, bool carriesVideo)
    {
        var extension = Path.GetExtension(pattern ?? "");

        if (string.IsNullOrEmpty(extension))
        {
            return "the recording pattern needs a file extension — it is what chooses the format "
                + $"(try {(carriesVideo ? DefaultVideo : DefaultAudio)})";
        }

        if (!IsKnown(pattern!))
        {
            if (!Alternatives.TryGetValue(extension, out var instead))
                return $"“{extension}” is not a format this build can write ({string.Join(", ", All)})";

            // The table's suggestions are the closest EQUIVALENT, which for the audio formats is an
            // audio-only container. Handing that to a video output would answer one refusal with
            // another — ".flac cannot be written, use .mka" is useless advice to somebody recording a
            // picture, since .mka has no room for one.
            if (carriesVideo && AudioOnly.Contains(instead, StringComparer.OrdinalIgnoreCase))
                instead = DefaultVideo;

            return $"“{extension}” cannot be written by this build — use “{instead}” instead";
        }

        if (carriesVideo && AudioOnly.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return $"“{extension}” carries audio only — a video recording needs {DefaultVideo}";

        return null;
    }
}
