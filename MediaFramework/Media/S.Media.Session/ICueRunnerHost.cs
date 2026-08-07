namespace S.Media.Session;

/// <summary>
/// The slice of the playback engine that the cue layer drives.
/// </summary>
/// <remarks>
/// <para>
/// The engine/cue-semantics seam, stated as a contract. Everything here is something a cue list needs the
/// engine to <em>do</em>; nothing here is a cue concept. Cues, follow-ons, arming, the GO cursor and the
/// execution log live on the cue side of this interface, and the engine does not know they exist - which is
/// the property that lets a host with no cue list (HaPlay's deck) use the engine directly, and lets the cue
/// layer be lifted into its own app without dragging the engine along.
/// </para>
/// <para>
/// An interface over the existing session rather than a new object, for the same reason as
/// <see cref="ISessionVoiceHost"/>: the dispatcher, the clip table and the show generation are one instance
/// per session, and duplicating any of them would break the serial-confinement invariant the fire path
/// depends on.
/// </para>
/// </remarks>
internal interface ICueRunnerHost
{
    /// <summary>The group a cue with no explicit group of its own plays on.</summary>
    /// <remarks>Comes from the engine because it is the engine's notion of "the default slot"; the cue layer
    /// only needs to be able to ask, and reaching for a constant on the session to find out would put the
    /// concrete type back in the runner's reach.</remarks>
    string DefaultGroupId { get; }

    /// <summary>
    /// Plays a clip on a transport group - the engine's one play primitive, and the only thing a cue's action
    /// ultimately does.
    /// </summary>
    /// <param name="binding">Null is a cue with no clip: the graph refuses to report it Fired.</param>
    ValueTask PlayClipAsync(
        string groupId,
        ShowClipBinding? binding,
        CancellationToken cancellationToken,
        Func<Task>? waitForStartBarrier,
        (TimeSpan Duration, FadeShape Curve)? crossfade);

    /// <summary>Fires one cue on a caller-owned transport group, waiting at the batch barriers: once when
    /// armed (before commit), and — when <paramref name="waitForStartEdge"/> is given — once more when
    /// fully prepared, so every sibling's clocks start on one edge instead of staggering behind the
    /// serialized commits.</summary>
    Task<CueExecutionStatus> FireCueIndependentAtBarrierAsync(
        string cueId, string independentGroupId, Func<Task>? waitForStartBarrier, Func<Task>? waitForStartEdge,
        CancellationToken cancellationToken);

    /// <summary>
    /// The group's GO cursor (the last fired cue number) and the show generation it was read under.
    /// </summary>
    /// <remarks>The cursor is stored per transport group, which is engine state, but what it MEANS - "GO
    /// starts looking after this" - is cue semantics, so the reading happens here and the deciding does not.
    /// The generation lets a cursor advance no-op when a reload swapped the show in between.</remarks>
    Task<(int Cursor, int Generation)> ReadGoCursorAsync(string groupId);

    /// <summary>GO's cursor advance. A no-op when the generation no longer matches.</summary>
    Task AdvanceGoCursorAsync(string groupId, int number, int generation);

    /// <summary>Pre-rolls the next few cues on a group so a GO opens from standby rather than from cold.</summary>
    Task WarmUpcomingAsync(string groupId = ShowSession.DefaultGroup, int count = 2);
}
