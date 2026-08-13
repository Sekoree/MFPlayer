using System.Globalization;

namespace HaCue2.Core.Media;

/// <summary>What a cue's <c>MediaPath</c> turned out to be.</summary>
public enum SourceKind
{
    /// <summary>A file on this machine - relative to the media root, or absolute.</summary>
    File,

    /// <summary>A live NDI sender on the network.</summary>
    Ndi,

    /// <summary>A capture device on this machine - a microphone, a line in, a loopback.</summary>
    Capture,

    /// <summary>A YouTube video, played from the locally prepared asset.</summary>
    YouTube,

    /// <summary>A rendered text card. Compiled, never authored into a media cue.</summary>
    Text,
}

/// <summary>
/// A cue whose source is a URI rather than a file.
/// </summary>
/// <remarks>
/// <para>
/// The framework already opens <c>ndi:</c>, <c>padev:</c>, <c>youtube:</c> and <c>text:</c> through the
/// registry - each has a decoder provider that owns its query grammar, so what a show has to store is
/// exactly the URI. That is why these are ORDINARY media cues here: level, sends, placements, fades and
/// effects all mean the same thing whether the pictures come off a disk or off the network, and giving a
/// camera its own cue type would fork every one of those.
/// </para>
/// <para>
/// What DOES have to change is everything that assumed a media path is a file. A URI must not be joined
/// onto the media root, must not be probed for a duration, must not be reported missing by the status
/// pass, and must not turn up in relink or consolidate - an NDI camera cannot be copied into the show
/// folder. <see cref="MediaPaths"/> asks this type first, so the rule lives in one place.
/// </para>
/// </remarks>
public static class SourceUri
{
    /// <summary>The schemes the registry can open. Anything else is a path.</summary>
    private static readonly (string Scheme, SourceKind Kind)[] Schemes =
    [
        ("ndi", SourceKind.Ndi),
        ("padev", SourceKind.Capture),
        ("youtube", SourceKind.YouTube),
        ("text", SourceKind.Text),
    ];

    /// <summary>
    /// What a stored media path is.
    /// </summary>
    /// <remarks>
    /// The scheme is matched EXACTLY and must be at least two characters, so a Windows drive letter
    /// ("C:\Shows\…") can never be read as one. A path that names no known scheme is a file, including
    /// one carrying a scheme this build does not have - the registry reports the absent provider, which
    /// is a better error than this quietly deciding the show references nothing.
    /// </remarks>
    public static SourceKind KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return SourceKind.File;

        var colon = path.IndexOf(':');
        if (colon < 2)
            return SourceKind.File;

        var scheme = path.AsSpan(0, colon);

        foreach (var (candidate, kind) in Schemes)
            if (scheme.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return kind;

        return SourceKind.File;
    }

    /// <summary>Whether this media path is a source URI rather than a file.</summary>
    public static bool IsSource(string? path) => KindOf(path) != SourceKind.File;

    /// <summary>
    /// Whether the source is LIVE - endless, unseekable, and started by opening it.
    /// </summary>
    /// <remarks>
    /// The property that matters to the rest of the app: a live source has no duration to show, no
    /// trim window that means anything, and opening it claims a device or a network connection. A
    /// prepared YouTube asset is none of those - it is a local file with a URI for a name.
    /// </remarks>
    public static bool IsLive(string? path) => KindOf(path) is SourceKind.Ndi or SourceKind.Capture;

    /// <summary>How to name a source in a list - never the raw URI, which is unreadable at row width.</summary>
    public static string Describe(string? uri) => KindOf(uri) switch
    {
        SourceKind.Ndi => DescribeNdi(ParseNdi(uri!)),
        SourceKind.Capture => DescribeCapture(ParseCapture(uri!)),
        SourceKind.YouTube => $"YouTube · {VideoId(uri!)}",
        SourceKind.Text => "text card",
        _ => uri ?? "",
    };

    private static string DescribeNdi(NdiSourceOptions options) =>
        $"NDI · {(options.Name.Length > 0 ? options.Name : "unnamed")}"
        + (options is { Audio: true, Video: false } ? " · audio only" : "")
        + (options is { Audio: false, Video: true } ? " · video only" : "")
        + (options.LowBandwidth ? " · proxy" : "");

    private static string DescribeCapture(CaptureSourceOptions options) =>
        $"input · {(options.Device.Length > 0 ? options.Device : "default device")}"
        + (options.Channels is { } channels ? $" · {channels}ch" : "");

    /// <summary>The video id out of a <c>youtube://</c> URI, for the row and the label.</summary>
    private static string VideoId(string uri)
    {
        var body = Body(uri, "youtube");
        var query = body.IndexOf('?');
        return query >= 0 ? body[..query] : body;
    }

    /// <summary>
    /// The generated-caption language carried by a YouTube source, or null when captions are not part
    /// of that source. Kept here so portable project operations can distinguish an old machine-cache
    /// path from an ordinary user-added subtitle sidecar without depending on the YouTube provider.
    /// </summary>
    public static string? YouTubeSubtitleLanguage(string? uri)
    {
        if (KindOf(uri) != SourceKind.YouTube)
            return null;

        var (_, values) = Split(uri!, "youtube");
        return values.TryGetValue("sub", out var language) && !string.IsNullOrWhiteSpace(language)
            ? language
            : null;
    }

    // ── ndi ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds <c>ndi://&lt;name&gt;?…</c>, the grammar <c>S.Media.NDI</c> parses.</summary>
    public static string Ndi(NdiSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var query = new List<string>(4);

        // Only the non-default halves are written, so the common case is a URI an operator can read.
        if (!options.Audio)
            query.Add("audio=0");
        if (!options.Video)
            query.Add("video=0");
        if (options.LowBandwidth)
            query.Add("lowBandwidth=1");
        if (options.AudioBufferMs is { } buffer)
            query.Add($"audioBufferMs={buffer.ToString(CultureInfo.InvariantCulture)}");
        if (options.PaceFromIngestClock)
            query.Add("ingestClock=1");

        var name = Uri.EscapeDataString(options.Name.Trim());

        return query.Count == 0 ? $"ndi://{name}" : $"ndi://{name}?{string.Join('&', query)}";
    }

    public static NdiSourceOptions ParseNdi(string uri)
    {
        var (name, values) = Split(uri, "ndi");

        return new NdiSourceOptions(name)
        {
            Audio = Bool(values, "audio", true),
            Video = Bool(values, "video", true),
            LowBandwidth = Bool(values, "lowBandwidth", false),
            AudioBufferMs = Int(values, "audioBufferMs"),
            PaceFromIngestClock = Bool(values, "ingestClock", false),
        };
    }

    // ── capture ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds <c>padev://&lt;device&gt;?…</c>, the grammar the PortAudio capture provider parses.</summary>
    public static string Capture(CaptureSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var query = new List<string>(4);

        // The host API travels because the SAME interface appears under ALSA and under JACK with
        // different names, and a device name alone can match the wrong one.
        if (options.HostApi.Trim().Length > 0)
            query.Add($"hostApiName={Uri.EscapeDataString(options.HostApi.Trim())}");
        if (options.DeviceIndex is { } index)
            query.Add($"globalDeviceIndex={index.ToString(CultureInfo.InvariantCulture)}");
        if (options.Channels is { } channels)
            query.Add($"channels={channels.ToString(CultureInfo.InvariantCulture)}");
        if (options.SampleRate is { } rate)
            query.Add($"sampleRate={rate.ToString(CultureInfo.InvariantCulture)}");

        var device = Uri.EscapeDataString(options.Device.Trim());

        return query.Count == 0 ? $"padev://{device}" : $"padev://{device}?{string.Join('&', query)}";
    }

    public static CaptureSourceOptions ParseCapture(string uri)
    {
        var (device, values) = Split(uri, "padev");

        return new CaptureSourceOptions(device)
        {
            HostApi = Text(values, "hostApiName"),
            DeviceIndex = Int(values, "globalDeviceIndex"),
            Channels = Int(values, "channels"),
            SampleRate = Int(values, "sampleRate"),
        };
    }

    // ── parsing ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything after <c>scheme:</c>, with the optional <c>//</c> removed.</summary>
    private static string Body(string uri, string scheme)
    {
        var rest = uri.Length > scheme.Length + 1 ? uri[(scheme.Length + 1)..] : "";
        return rest.StartsWith("//", StringComparison.Ordinal) ? rest[2..] : rest;
    }

    private static (string Name, Dictionary<string, string> Values) Split(string uri, string scheme)
    {
        var body = Body(uri, scheme);
        var query = body.IndexOf('?');
        var name = Uri.UnescapeDataString(query >= 0 ? body[..query] : body).Trim();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (query >= 0)
        {
            foreach (var part in body[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = part.IndexOf('=');
                values[Uri.UnescapeDataString(equals >= 0 ? part[..equals] : part)] =
                    Uri.UnescapeDataString(equals >= 0 ? part[(equals + 1)..] : "");
            }
        }

        return (name, values);
    }

    private static string Text(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : "";

    private static int? Int(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool Bool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value)
            ? value is "1" or "true" or "on" or "yes"
            : fallback;
}

/// <summary>An NDI sender, and how much of it this cue takes.</summary>
/// <param name="Name">The sender's name as NDI advertises it - "STUDIO (CAM 1)".</param>
public sealed record NdiSourceOptions(string Name)
{
    public bool Audio { get; init; } = true;

    public bool Video { get; init; } = true;

    /// <summary>The sender's proxy stream: a fraction of the bandwidth, at preview resolution.</summary>
    public bool LowBandwidth { get; init; }

    /// <summary>Jitter buffer override in ms; null takes the framework's default.</summary>
    public int? AudioBufferMs { get; init; }

    /// <summary>Pace playback from the sender's ingest clock rather than this machine's wall clock.</summary>
    public bool PaceFromIngestClock { get; init; }
}

/// <summary>A capture device on this machine, addressed by name.</summary>
/// <param name="Device">The device name; empty means the system default input.</param>
public sealed record CaptureSourceOptions(string Device)
{
    /// <summary>Which driver family the name belongs to - the discriminator when both expose it.</summary>
    public string HostApi { get; init; } = "";

    /// <summary>The device's index when it was chosen. A last-resort fallback: indices move across boots.</summary>
    public int? DeviceIndex { get; init; }

    public int? Channels { get; init; }

    public int? SampleRate { get; init; }
}
