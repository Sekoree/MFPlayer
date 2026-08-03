using HaCue2.Core.Model;

namespace HaCue2.Engine;

/// <summary>
/// Everything firing a cue needs from a running show.
/// </summary>
/// <remarks>
/// <para>
/// The seam that makes cue execution testable. <see cref="CueExecutor"/> holds the DECISION — what a
/// group's fire mode means, where a jump lands, whether an auto-continue chain carries on — and this
/// interface is every effect that decision can have. Nothing here knows about a session, a bay or a
/// socket, so the whole of the interesting logic can be driven against a recording fake.
/// </para>
/// <para>
/// It exists because the split in this assembly's test coverage was never about importance: the pure
/// arithmetic was tested and every device-holding class was not, purely because it could not be
/// constructed without hardware. The code that decides what every cue does was on the wrong side of
/// that line, and two defects in it — a group's auto-continue firing its first child twice, and effect
/// lanes silently dropped from untrimmed cues — were found by reading rather than by a test.
/// </para>
/// </remarks>
public interface ICueExecutionHost
{
    /// <summary>The document as it stands. Read per call — an edit lands between two cues in a chain.</summary>
    HaCueProject Project { get; }

    /// <summary>Hands a playable cue to the session. False when it did not start.</summary>
    Task<bool> PlayAsync(CueNode cue, CueList? list);

    /// <summary>Moves a list's cursor, or clears it when the cue is null.</summary>
    Task SetStandbyAsync(CueList list, Guid? cueId);

    /// <summary>Stops one cue, fading it out.</summary>
    Task StopCueAsync(Guid cueId);

    /// <summary>Rewrites a sounding cue's send gains — a fade to a level that is not silence.</summary>
    Task SetCueLevelAsync(Guid cueId, double levelDb);

    /// <summary>Writes the project's patch cells and ramps the bay toward them over the duration.</summary>
    Task ApplyPatchAsync(
        IReadOnlyList<PatchCell> origin, IReadOnlyList<PatchCell> destination, TimeSpan duration);

    /// <summary>Sends an action cue. Returns null on success, or the reason it could not.</summary>
    Task<string?> SendActionAsync(ActionCueNode action, ActionEndpoint? endpoint);

    /// <summary>Cue ids currently holding a voice — what "fade everything sounding" means.</summary>
    IReadOnlyList<Guid> Sounding { get; }

    /// <summary>Notes that something has asked a cue to come down, for the Active panel's stripe.</summary>
    void MarkFading(Guid cueId);

    /// <summary>Forgets a cue that has stopped.</summary>
    void Forget(Guid cueId);

    /// <summary>Tells the operator something a cue could not do.</summary>
    void Report(string problem);

    /// <summary>
    /// A pre-wait, a post-wait, or a timeline offset.
    /// </summary>
    /// <remarks>
    /// Injected rather than <c>Task.Delay</c> so a test can run a chain with authored waits instantly.
    /// Real waits would make every test of the chain logic as slow as the show it describes.
    /// </remarks>
    Task<bool> DelayAsync(TimeSpan duration);

    /// <summary>Runs a cue later, on the show's own clock — a timeline group's children.</summary>
    void Schedule(Guid cueId, TimeSpan when, int depth);

    /// <summary>
    /// What the probe says a cue's media runs for, or null when nobody has looked.
    /// </summary>
    /// <remarks>
    /// A MACHINE fact, which is why it comes from the host rather than the document: whether a clip
    /// straddles the playhead depends on how long the file actually is, and only something that has
    /// opened it knows.
    /// </remarks>
    TimeSpan? MediaLength(Guid cueId);

    /// <summary>
    /// Moves a sounding cue to a position inside its own media.
    /// </summary>
    /// <remarks>
    /// In FILE time, not group time: the clip's in-point is already applied, so a caller starting a
    /// clip part-way through has to add the trim itself. Doing it here would apply it twice.
    /// </remarks>
    Task SeekCueAsync(Guid cueId, TimeSpan position);
}
