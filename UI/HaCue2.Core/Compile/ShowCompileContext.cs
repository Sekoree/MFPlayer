namespace HaCue2.Core.Compile;

/// <summary>
/// Machine-specific facts used to turn a portable project into a playable document.
/// </summary>
/// <remarks>
/// The project deliberately stores portable paths and positional stream choices. Playback needs the
/// absolute paths for this machine and stream indices checked against the file currently present.
/// Keeping those answers outside the project prevents opening a show on one machine from rewriting
/// authoring data that is still valid on another.
/// </remarks>
public sealed record ShowCompileContext
{
    public string? ProjectPath { get; init; }

    public IReadOnlyDictionary<Guid, TimeSpan>? Durations { get; init; }

    /// <summary>Only contains cues whose media has been probed on this machine.</summary>
    public IReadOnlyDictionary<Guid, ResolvedMediaTracks> Tracks { get; init; } =
        new Dictionary<Guid, ResolvedMediaTracks>();

    /// <summary>
    /// Where each text cue's rendered picture lives on this machine, by cue id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A text cue stores WORDS; what the engine plays is a picture. Rasterising them needs a font
    /// stack, which belongs to the app rather than to the document or the compiler — so the app renders
    /// each text cue into its media cache and tells the compiler where it put it, exactly as it tells
    /// it what the probe found. The cache is machine-local and derived: deleting it costs a re-render
    /// and nothing else.
    /// </para>
    /// <para>
    /// A text cue with no entry here compiles to a cue with no clip, the same way a media cue with no
    /// file does. That is the honest state on a machine that has not rendered yet, and it keeps one
    /// unrendered cue from stopping the whole document from loading.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<Guid, string> RenderedText { get; init; } =
        new Dictionary<Guid, string>();
}

/// <summary>The stream choices after signatures have been checked against the current file.</summary>
public sealed record ResolvedMediaTracks(
    int? AudioStreamIndex,
    int? VideoStreamIndex,
    IReadOnlyList<int> SubtitleStreamIndices);
