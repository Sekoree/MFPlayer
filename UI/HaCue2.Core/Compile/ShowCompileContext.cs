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
}

/// <summary>The stream choices after signatures have been checked against the current file.</summary>
public sealed record ResolvedMediaTracks(
    int? AudioStreamIndex,
    int? VideoStreamIndex,
    IReadOnlyList<int> SubtitleStreamIndices);
