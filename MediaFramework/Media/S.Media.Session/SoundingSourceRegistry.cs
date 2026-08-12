namespace S.Media.Session;

/// <summary>
/// What a sounding source IS to the show - the classification the master fader, stop-all and Panic all key
/// off. Decided with the owner 2026-07-29: those three cover <see cref="Program"/> audio only (transport cues
/// AND soundboard voices) and they cover exactly the SAME set, so there is one rule to remember under
/// pressure. <see cref="Monitoring"/> is the operator's own path - it must keep sounding at its own level when
/// the show is pulled down or panicked.
/// </summary>
public enum SoundingSourceRole
{
    /// <summary>The audience hears it: transport cues and soundboard voices. Reached by the master fader,
    /// stop-all and Panic alike.</summary>
    Program,

    /// <summary>The operator or an analyser hears it: cue preview/audition, visualizer/meter taps. Never
    /// reached by trim, stop-all or Panic - ducking or killing the audition path when the show is pulled
    /// down would blind the person driving.</summary>
    Monitoring,
}

/// <summary>What stop-all and Panic ask of one program source. Both issue the SAME request - the difference
/// is only the duration the host resolved (Panic's default is 0 ms = the hard cut), never the reach.</summary>
public readonly record struct SoundingStopRequest(TimeSpan FadeDuration, FadeShape Curve)
{
    public SoundingStopRequest(TimeSpan fadeDuration, FadeCurve curve = FadeCurve.Linear)
        : this(fadeDuration, new FadeShape(curve))
    {
    }

    /// <summary>True when the source ramps down; false = hard cut (a non-positive duration).</summary>
    public bool Fade => FadeDuration > TimeSpan.Zero;
}

/// <summary>One entry of the session's sounding bus (<see cref="ShowSession.GetSoundingSourcesAsync"/>) -
/// what the show is currently feeding audio to, how the fader/stops classify it, and the level actually
/// composed for it. A host status panel and the level/stop tests read the bus through this.</summary>
/// <param name="IsSounding">Whether the source is making sound right now. A registered transport group with
/// no clip is idle but still registered - it must learn a trim set while it is idle, or its next fire would
/// start at a stale level.</param>
/// <param name="Level">The composed <c>master × source × fade × envelope × modifier</c> the source's routes carry.</param>
public readonly record struct SoundingSourceInfo(
    string Label, SoundingSourceRole Role, bool IsSounding, float Level);

/// <summary>
/// The ONE level composition in the session: <c>master × source × fade × envelope × modifier</c>. Every sounding source
/// owns one of these and every routed gain write goes through <see cref="Effective"/>, so the five mechanisms
/// compose by construction instead of overwriting each other (the defect this replaces: a soundboard voice's
/// fade-out wrote the ramp level straight onto the route, discarding the tile's own volume, and neither knew
/// about the master trim at all).
/// </summary>
/// <remarks>A class, not a struct: sources hand the live level object to their apply/ramp closures, and a
/// struct copy would silently detach a ramp from the source it is ramping.</remarks>
internal sealed class SoundingLevel
{
    /// <summary>The show-authored gain of the source itself: a soundboard tile's volume. Transport clips
    /// carry theirs per route (<c>AudioRouteTarget.TargetGain</c>) and leave this at unity.</summary>
    public float Source { get; set; } = 1f;

    /// <summary>The persistent fade level fade cues and stop ramps compose from - and the ONLY component a
    /// ramp may capture as its start. Capturing <see cref="Effective"/> instead applies the master trim
    /// TWICE, because the ramp multiplies the live trim onto the captured value on every step (a 0.5 fader
    /// audibly halved a crossfade tail again). Every ramp in the session - transport voices, soundboard
    /// voices, crossfade tails - starts from this.</summary>
    public float Fade { get; set; } = 1f;

    /// <summary>The volume-envelope factor (1 = no automation).</summary>
    public float Envelope { get; set; } = 1f;

    /// <summary>A controller/group modifier over the cue-owned envelope (1 = no modifier).</summary>
    public float Modifier { get; set; } = 1f;

    /// <summary>The session-wide master trim as this source last saw it. Held SEPARATE from the rest so
    /// fades always compose from the operator-authored level, never from a trimmed product.</summary>
    public float Master { get; set; } = 1f;

    /// <summary>The gain actually written to the routes.</summary>
    public float Effective => Source * Fade * Envelope * Modifier * Master;
}

/// <summary>One registered sounding source. <see cref="SoundingSourceRole.Program"/> sources MUST supply the
/// trim and stop hooks - the registration API states the owner's rule rather than leaving it to a bool a
/// caller can forget: whatever the fader reaches, both stops reach.</summary>
/// <param name="Label">Human-readable and UNIQUE per source (per voice, not per cue - a loop crossfade puts
/// two voices of the same cue on the bus at once, and two identical labels would defeat the duplicate check
/// that is how a lingering registration is meant to fail loudly).</param>
/// <param name="SubjectId">The show-level id this source IS - a cue id for a transport voice, the tile id for
/// a soundboard voice. Distinct from <paramref name="Label"/> on purpose: the label identifies a bus ENTRY
/// (and so carries the group and a uniquifier), while an operator-facing alert has to name the thing the
/// operator knows, which the host resolves from this id.</param>
internal sealed record SoundingSourceRegistration(
    Guid Id,
    string Label,
    string SubjectId,
    SoundingSourceRole Role,
    Func<bool> IsSounding,
    Func<float> Level,
    Action<float>? ApplyMasterTrim = null,
    Func<SoundingStopRequest, Task>? Stop = null);

/// <summary>
/// The session's level/stop bus: every sounding source (transport groups, soundboard voices, the cue preview,
/// audio taps) registers here with its classification, and trim / stop-all / Panic drive the ONE enumeration
/// this hands out. Before it there were three independent level authorities that never consulted each other -
/// the master fader walked the transport groups only, so pulling the show to silence still let a soundboard
/// stinger play at full level and stop-all left voices running.
/// <para>Dispatcher-confined exactly like <c>ShowSession._groups</c>: registration, unregistration and
/// enumeration all run on the session dispatcher, so a snapshot can never observe a half-registered source.</para>
/// </summary>
internal sealed class SoundingSourceRegistry
{
    private readonly List<SoundingSourceRegistration> _sources = [];

    /// <summary>Registers program audio - reached by the master fader, stop-all and Panic. Both hooks are
    /// required: a program source that cannot be levelled or cannot be stopped is the exact gap this bus
    /// exists to close.</summary>
    public Guid RegisterProgram(
        string label,
        string subjectId,
        Func<bool> isSounding,
        Func<float> level,
        Action<float> applyMasterTrim,
        Func<SoundingStopRequest, Task> stop)
    {
        ArgumentNullException.ThrowIfNull(applyMasterTrim);
        ArgumentNullException.ThrowIfNull(stop);
        var id = Guid.NewGuid();
        _sources.Add(new SoundingSourceRegistration(
            id, label, subjectId, SoundingSourceRole.Program, isSounding, level, applyMasterTrim, stop));
        return id;
    }

    /// <summary>Registers a monitoring path - the operator's audition or an analysis tap. It takes no trim
    /// and no stop hook because the fader and both stops never reach it; it is registered so the bus knows
    /// the whole picture and so its exclusion is observable rather than implicit.</summary>
    public Guid RegisterMonitoring(string label, Func<bool> isSounding, Func<float> level)
    {
        var id = Guid.NewGuid();
        // No stop hook ⇒ no stop can fail ⇒ nothing ever needs a monitoring source's subject id; the label
        // stands in so the record has no meaningless second name to keep in sync.
        _sources.Add(new SoundingSourceRegistration(
            id, label, label, SoundingSourceRole.Monitoring, isSounding, level));
        return id;
    }

    /// <summary>Drops a registration. Called from the same teardown that releases the source, so a released
    /// voice can never linger in the bus and be levelled or stopped after its player is gone.</summary>
    public void Unregister(Guid id) => _sources.RemoveAll(s => s.Id == id);

    /// <summary>THE enumeration trim, stop-all and Panic share - every program source, unfiltered. Stop
    /// hooks decide for themselves whether they have anything to take down (a source knows; the bus does
    /// not), and trim must reach idle sources too so their next fire starts at the current fader.</summary>
    public IReadOnlyList<SoundingSourceRegistration> ProgramSources() =>
        _sources.Where(s => s.Role == SoundingSourceRole.Program).ToArray();

    /// <summary>The whole bus as immutable info - host status and the tests that prove monitoring is
    /// registered yet excluded.</summary>
    public IReadOnlyList<SoundingSourceInfo> Snapshot() =>
        _sources.Select(s => new SoundingSourceInfo(s.Label, s.Role, Read(s.IsSounding), Read(s.Level))).ToArray();

    private static bool Read(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; } // a source mid-teardown must not fault a status poll
    }

    private static float Read(Func<float> probe)
    {
        try { return probe(); }
        catch { return 0f; }
    }
}
