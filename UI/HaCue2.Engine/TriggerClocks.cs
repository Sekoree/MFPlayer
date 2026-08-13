using HaCue2.Core.Model;
using S.Control;

namespace HaCue2.Engine;

/// <summary>
/// The two ways a cue fires with nothing arriving on a wire: the wall clock, and incoming timecode.
/// </summary>
/// <remarks>
/// <para>
/// The same document shape as a MIDI or OSC source - a <see cref="TriggerInputDefinition"/> with
/// bindings - because everything an operator can say about one of those applies here too: which cue,
/// which transport verb, per-source enable, and the one External-input master gate over all of them.
/// Only the way the moment ARRIVES is different, so only that is new.
/// </para>
/// <para>
/// <b>Crossings, not equality.</b> A tick that happens to land on 22:30:00.000 is a coincidence; what
/// the operator asked for is "when the clock passes half past ten". So each pass compares the previous
/// reading with the current one and fires what lies between - which also means a tick delayed by a
/// busy machine fires the cue late rather than not at all.
/// </para>
/// <para>
/// <b>A relocate retires everything behind it.</b> When a timecode sender jumps, the chase clock bumps
/// its generation; this re-anchors instead of firing every target between the old position and the new
/// one. Without that, spooling a deck back to the top would fire the whole act at once.
/// </para>
/// </remarks>
public sealed class TriggerClocks : IDisposable
{
    /// <summary>Four times a second - fine enough for a cue called on the clock, cheap enough to ignore.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How far back a crossing may be honoured, in seconds.
    /// </summary>
    /// <remarks>
    /// A machine that stalled for a minute - a big document load, a laptop lid - must not come back and
    /// fire a cue whose moment is long past. Two seconds covers a late tick and refuses a resumption.
    /// </remarks>
    private const double LateLimit = 2;

    private readonly TimecodeChase _chase;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private HaCueProject _project;
    private bool _enabled;
    private bool _ticking;

    /// <summary>Where the wall clock was last read, in seconds since midnight. Negative until the first pass.</summary>
    private double _clockBefore = -1;

    /// <summary>Where the timecode was last read, and which run of it that was.</summary>
    private double _timecodeBefore = -1;
    private int _generation = -1;

    public TriggerClocks(HaCueProject project, TimecodeChase chase)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(chase);

        _project = project;
        _chase = chase;
        _timer = new Timer(_ => Pass(), null, Tick, Tick);
    }

    /// <summary>Raised when a binding's moment arrived. The host decides what firing means.</summary>
    public event Action<TriggerAction>? Triggered;

    /// <summary>
    /// Follows the External-input master gate.
    /// </summary>
    /// <remarks>
    /// Turning it on re-anchors rather than catching up: an operator arming external input at 22:31
    /// has not asked for the 22:30 cue, and firing it would be the app deciding it knew better.
    /// </remarks>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _clockBefore = -1;
            _timecodeBefore = -1;
            _generation = -1;
        }
    }

    /// <summary>Adopts an edited document. No devices to reopen - the clocks do not care.</summary>
    public void Adopt(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        lock (_gate)
            _project = project;
    }

    /// <summary>
    /// One pass of both clocks.
    /// </summary>
    /// <remarks>
    /// Guarded against re-entry the same way the UI sweep is: a pass that outlasts the interval must be
    /// skipped rather than queued, or a slow fire would stack passes behind it and then fire the same
    /// window several times.
    /// </remarks>
    private void Pass()
    {
        HaCueProject project;

        lock (_gate)
        {
            if (!_enabled || _ticking)
                return;

            _ticking = true;
            project = _project;
        }

        try
        {
            var sources = project.TriggerInputs.Where(input => input.Enabled).ToList();

            WallPass(sources, DateTime.Now.TimeOfDay.TotalSeconds);
            TimecodePass(sources, _chase.Read());
        }
        finally
        {
            lock (_gate)
                _ticking = false;
        }
    }

    /// <summary>
    /// Fires the schedules the wall clock has just passed.
    /// </summary>
    /// <remarks>
    /// The reading is passed IN rather than taken here, so the crossing rules - the first pass, the
    /// midnight wrap, the late limit - can be driven at any time of day by a test. A clock consulted
    /// inside this method would make every one of them untestable.
    /// </remarks>
    internal void WallPass(IReadOnlyList<TriggerInputDefinition> sources, double now)
    {
        double before;

        lock (_gate)
        {
            before = _clockBefore;
            _clockBefore = now;
        }

        // The first pass has nothing to compare against, and midnight is a wrap rather than a jump
        // backwards - in both cases the honest answer is that this window cannot be judged.
        if (before < 0 || now < before)
            return;

        foreach (var source in sources.Where(input => input.Kind == TriggerInputKind.Schedule))
        {
            foreach (var binding in source.Bindings)
            {
                if (TriggerTimes.Schedule(binding.Input) is { } at
                    && at > before && at <= now && now - at <= LateLimit)
                    Fire(binding, $"{source.Name} · {binding.Input}");
            }
        }
    }

    /// <summary>Fires the timecode bindings the incoming stream has just passed.</summary>
    /// <remarks>The state is passed in for the same reason the wall reading is.</remarks>
    internal void TimecodePass(
        IReadOnlyList<TriggerInputDefinition> sources, MidiTimecodeChaseState state)
    {
        // Only while it is genuinely CHASING. A stalled or parked sender freezes the position, and
        // firing off a frozen reading would repeat the same cue every quarter of a second.
        if (!state.HasSignal || !state.IsChasing)
        {
            lock (_gate)
                _timecodeBefore = -1;

            return;
        }

        var rate = MidiTimecodeRates.FramesPerSecond(state.Rate);
        var now = state.PositionSeconds;

        double before;
        bool relocated;

        lock (_gate)
        {
            relocated = state.Generation != _generation;
            before = relocated ? -1 : _timecodeBefore;
            _generation = state.Generation;
            _timecodeBefore = now;
        }

        // A locate is not a roll through everything in between: re-anchor and judge from the next pass.
        if (relocated || before < 0 || now < before)
            return;

        foreach (var source in sources.Where(input => input.Kind == TriggerInputKind.Timecode))
        {
            foreach (var binding in source.Bindings)
            {
                if (TriggerTimes.Timecode(binding.Input, rate) is { } at && at > before && at <= now)
                    Fire(binding, $"{source.Name} · {binding.Input}");
            }
        }
    }

    /// <summary>
    /// One binding's moment, as the same action a wire trigger produces.
    /// </summary>
    /// <remarks>
    /// A clock carries no value, so a binding pointing at a PARAMETER is refused here rather than
    /// writing an arbitrary number into a master trim - the same rule the note-on path follows.
    /// </remarks>
    private void Fire(TriggerBinding binding, string describe)
    {
        var action = binding.Target switch
        {
            TriggerTarget.Cue when binding.TargetCueId is { } cueId =>
                new TriggerAction(TriggerTarget.Cue, cueId, "", null, describe),
            TriggerTarget.Transport =>
                new TriggerAction(TriggerTarget.Transport, null, binding.ParameterId, null, describe),
            _ => (TriggerAction?)null,
        };

        if (action is { } fire)
            Triggered?.Invoke(fire);
    }

    public void Dispose() => _timer.Dispose();
}
