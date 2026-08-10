using HaCue2.Core.Model;
using S.Media.Session;
using S.Media.Time;

namespace HaCue2.Engine;

/// <summary>One media-backed cue prepared for a timeline edge.</summary>
/// <param name="Cue">The media or text cue to start.</param>
/// <param name="StartPosition">
/// Optional FILE position to arm at. Null means the cue's normal in-point; timeline rehearsal uses a
/// concrete position so a straddling clip is already at the playhead before its clock is released.
/// </param>
public readonly record struct TimelineMediaStart(CueNode Cue, TimeSpan? StartPosition = null);

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

    /// <summary>True while a cue is being fired by an external input binding.</summary>
    bool IsExternalTriggerActive { get; }

    /// <summary>Hands a playable cue to the session. False when it did not start.</summary>
    Task<bool> PlayAsync(
        CueNode cue,
        CueList? list,
        TimeSpan? crossfade = null,
        FadeShape crossfadeCurve = default);

    /// <summary>
    /// Fully prepares media cues, then holds their clocks at a caller-owned start edge.
    /// </summary>
    /// <remarks>
    /// <paramref name="waitForStartEdge"/> is entered only after every viable cue has opened, committed,
    /// filled its audio pre-roll and presented its synchronization frame. It may therefore wait against an
    /// absolute master-clock deadline without making decoder-open time part of the authored timeline.
    /// </remarks>
    Task<IReadOnlyList<Guid>> PlayTimelineMediaAsync(
        IReadOnlyList<TimelineMediaStart> cues,
        CueList? list,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken);

    /// <summary>Prepares visualizer renderers hidden, then reveals them at a caller-owned edge.</summary>
    Task<IReadOnlyList<Guid>> PlayTimelineVisualizersAsync(
        IReadOnlyList<VisualizerCueNode> cues,
        CueList? list,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken);

    /// <summary>Moves a list's cursor, or clears it when the cue is null.</summary>
    Task SetStandbyAsync(CueList list, Guid? cueId);

    /// <summary>Stops one cue, fading it out.</summary>
    Task StopCueAsync(Guid cueId);

    /// <summary>Rewrites a sounding cue's send gains — a fade to a level that is not silence.</summary>
    Task SetCueLevelAsync(Guid cueId, double levelDb);

    /// <summary>Ramps one sounding cue with the fade cue's own duration and resolved shape.</summary>
    Task FadeCueAsync(
        Guid cueId,
        double levelDb,
        TimeSpan duration,
        FadeShape curve,
        bool stopWhenSilent);

    /// <summary>Writes the project's patch cells and ramps the bay toward them over the duration.</summary>
    Task ApplyPatchAsync(
        IReadOnlyList<PatchCell> origin,
        IReadOnlyList<PatchCell> destination,
        TimeSpan duration,
        FadeShape curve);

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

    /// <summary>The authoritative show clock timeline scheduling follows.</summary>
    IPlaybackClock TimelineClock { get; }

    /// <summary>Whether the operator has paused the show. A timeline freezes while this is true.</summary>
    bool TimelinePaused { get; }

    /// <summary>
    /// Cancellable scheduler wait. Kept on the host seam so tests can advance a virtual master clock
    /// without sleeping for the authored duration.
    /// </summary>
    Task DelayTimelineAsync(TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// What the probe says a cue's media runs for, or null when nobody has looked.
    /// </summary>
    /// <remarks>
    /// A MACHINE fact, which is why it comes from the host rather than the document: whether a clip
    /// straddles the playhead depends on how long the file actually is, and only something that has
    /// opened it knows.
    /// </remarks>
    TimeSpan? MediaLength(Guid cueId);

}
