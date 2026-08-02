using S.Media.Session;

namespace S.Control;

/// <summary>
/// A host value a control surface can ride continuously - a master trim, a deck volume, a matrix cell.
/// </summary>
/// <param name="Id">Stable id a binding refers to; survives renames of the display name.</param>
/// <param name="DisplayName">Operator-facing name, for the binding editor and for diagnostics.</param>
/// <param name="Minimum">Lowest value the parameter accepts.</param>
/// <param name="Maximum">Highest value the parameter accepts.</param>
/// <param name="Unit">Unit for display, e.g. <c>dB</c>. Purely presentational.</param>
/// <param name="MappingCurve">Curve applied to normalized controller travel before scaling it into
/// this target's output range.</param>
public sealed record ParameterTarget(
    string Id,
    string DisplayName,
    double Minimum,
    double Maximum,
    string Unit = "",
    FadeCurve MappingCurve = FadeCurve.Linear)
{
    /// <summary>Clamps <paramref name="value"/> into this parameter's range.</summary>
    public double Clamp(double value) => Math.Clamp(value, Minimum, Maximum);

    /// <summary>Where <paramref name="value"/> sits in the range, 0..1.</summary>
    public double Normalize(double value) =>
        Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0d, 1d) : 0d;
}

/// <summary>
/// The set of parameters a host offers to control surfaces, and the accessors that read and write them.
/// </summary>
/// <remarks>
/// This is the piece the framework was missing. Continuous control input already existed as far as a
/// numeric payload - what did not exist was anything a numeric payload could be pointed AT: the trigger
/// action model is <c>(kind, targetId, command)</c> strings with no value semantics, and the show-action
/// surface exposes only transport verbs, so a control surface could fire a cue but could not touch a
/// level. Bindable values live on host view models, so the host registers them here rather than the
/// framework reaching up into them.
/// </remarks>
public sealed class ParameterRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private sealed record Entry(ParameterTarget Target, Func<double> Get, Action<double> Set);

    /// <summary>Registers (or replaces) a parameter. The accessors are invoked on the caller's thread -
    /// a host whose value is thread-affine must marshal inside them.</summary>
    public void Register(ParameterTarget target, Func<double> get, Action<double> set)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(target.Id);
        if (!double.IsFinite(target.Minimum) || !double.IsFinite(target.Maximum)
            || target.Maximum <= target.Minimum)
            throw new ArgumentException($"parameter '{target.Id}' has an empty range", nameof(target));
        if (!Enum.IsDefined(target.MappingCurve))
            throw new ArgumentException($"parameter '{target.Id}' has an unknown mapping curve", nameof(target));

        lock (_gate)
            _entries[target.Id] = new Entry(target, get, set);
    }

    /// <summary>Removes a parameter; bindings to it become inert rather than throwing.</summary>
    public bool Unregister(string id)
    {
        lock (_gate)
            return _entries.Remove(id);
    }

    /// <summary>Every registered parameter, for a binding editor's picker.</summary>
    public IReadOnlyList<ParameterTarget> Targets
    {
        get { lock (_gate) return [.. _entries.Values.Select(e => e.Target)]; }
    }

    public bool TryGetTarget(string id, out ParameterTarget target)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                target = entry.Target;
                return true;
            }
            target = null!;
            return false;
        }
    }

    /// <summary>Reads the parameter's current value.</summary>
    public bool TryGet(string id, out double value)
    {
        Entry? entry;
        lock (_gate)
            _entries.TryGetValue(id, out entry);

        if (entry is null)
        {
            value = 0;
            return false;
        }

        value = entry.Get();
        return true;
    }

    /// <summary>Writes the parameter, clamped to its range. False when nothing is registered under
    /// <paramref name="id"/> - a binding to a parameter the host has withdrawn goes quiet instead of
    /// throwing on every fader move.</summary>
    public bool TrySet(string id, double value)
    {
        Entry? entry;
        lock (_gate)
            _entries.TryGetValue(id, out entry);

        if (entry is null)
            return false;

        entry.Set(entry.Target.Clamp(value));
        return true;
    }
}

/// <summary>How an input's raw value maps onto a parameter.</summary>
/// <param name="TargetId">The <see cref="ParameterTarget.Id"/> to drive.</param>
/// <param name="InputMin">Raw value that maps to the low end (0 for a 7-bit MIDI CC).</param>
/// <param name="InputMax">Raw value that maps to the high end (127 for a 7-bit MIDI CC).</param>
/// <param name="OutputMin">Parameter value at <paramref name="InputMin"/>. Null = the target's minimum.</param>
/// <param name="OutputMax">Parameter value at <paramref name="InputMax"/>. Null = the target's maximum.</param>
/// <param name="SoftTakeover">See <see cref="ContinuousBinding"/>.</param>
/// <param name="SoftTakeoverTolerance">How close, in normalized 0..1 terms, the control must come to the
/// current value before it takes over.</param>
public sealed record ContinuousBindingSpec(
    string TargetId,
    double InputMin = 0,
    double InputMax = 127,
    double? OutputMin = null,
    double? OutputMax = null,
    bool SoftTakeover = true,
    double SoftTakeoverTolerance = 0.02);

/// <summary>
/// Drives one <see cref="ParameterTarget"/> from a control surface's continuous input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Soft takeover</b> is the reason this is not just arithmetic. A physical fader has its own position,
/// and after a cue (or another surface, or the mouse) has moved the parameter, the two disagree. Applying
/// the fader's value on its first move would jump the parameter - audibly, mid-show. With soft takeover
/// the control is ignored until it passes within <see cref="ContinuousBindingSpec.SoftTakeoverTolerance"/>
/// of the current value, and only then latches on. Motorised faders do not need it, which is why it is a
/// per-binding option rather than a global rule.
/// </para>
/// <para>Not thread-safe: one binding belongs to one input, and inputs are serialised by the device layer.</para>
/// </remarks>
public sealed class ContinuousBinding
{
    private readonly ContinuousBindingSpec _spec;
    private readonly ParameterRegistry _registry;
    private bool _latched;

    public ContinuousBinding(ContinuousBindingSpec spec, ParameterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(registry);
        if (!double.IsFinite(spec.InputMin) || !double.IsFinite(spec.InputMax)
            || Math.Abs(spec.InputMax - spec.InputMin) < double.Epsilon)
            throw new ArgumentException("a continuous binding needs a non-empty input range", nameof(spec));
        if (spec.OutputMin is { } outputMin && !double.IsFinite(outputMin)
            || spec.OutputMax is { } outputMax && !double.IsFinite(outputMax))
            throw new ArgumentException("continuous binding output bounds must be finite", nameof(spec));
        if (!double.IsFinite(spec.SoftTakeoverTolerance) || spec.SoftTakeoverTolerance is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(spec), "soft-takeover tolerance must be in 0..1");

        _spec = spec;
        _registry = registry;
        // Without soft takeover the control is authoritative from its first move.
        _latched = !spec.SoftTakeover;
    }

    /// <summary>True once the control has taken the parameter over. Until then its moves are ignored -
    /// which a binding editor should show, or the surface looks broken.</summary>
    public bool IsLatched => _latched;

    /// <summary>Drops the latch: the next moves are ignored again until the control catches the value.
    /// A host calls this when something else moves the parameter (a cue, a snapshot recall).</summary>
    public void ReleaseLatch() => _latched = !_spec.SoftTakeover;

    /// <summary>
    /// Applies one raw input value. Returns true when the parameter was written - false when the control
    /// is still catching up, or its target is not registered.
    /// </summary>
    public bool Apply(double rawValue)
    {
        if (!double.IsFinite(rawValue))
            return false;
        if (!_registry.TryGetTarget(_spec.TargetId, out var target))
            return false;

        var outMin = _spec.OutputMin ?? target.Minimum;
        var outMax = _spec.OutputMax ?? target.Maximum;

        var t = Math.Clamp((rawValue - _spec.InputMin) / (_spec.InputMax - _spec.InputMin), 0d, 1d);
        var shaped = FadeCurves.ShapeProgress(t, target.MappingCurve);
        var mapped = outMin + (outMax - outMin) * shaped;

        if (!_latched)
        {
            if (!_registry.TryGet(_spec.TargetId, out var current))
                return false;

            // Compare in the target's own normalized space, so a tolerance means the same thing whatever
            // the parameter's units and range happen to be.
            if (Math.Abs(target.Normalize(mapped) - target.Normalize(current)) > _spec.SoftTakeoverTolerance)
                return false;

            _latched = true;
        }

        return _registry.TrySet(_spec.TargetId, mapped);
    }
}

/// <summary>
/// Rate-limits a stream of parameter writes, keeping only the newest pending value.
/// </summary>
/// <remarks>
/// A fader sweep arrives at roughly a hundred messages a second, and the host's input path posts one
/// dispatcher item per accepted message. Left alone that floods the UI thread with values that are stale
/// before they run. Coalescing keeps the LATEST value and drops the rest, which is what a continuous
/// control wants - unlike a cue fire, an intermediate fader position has no meaning once a newer one
/// exists. Time is caller-supplied, so this is deterministic under test.
/// </remarks>
public sealed class CoalescingParameterWriter(TimeSpan minimumInterval)
{
    private double _pending;
    private bool _hasPending;
    private TimeSpan _lastWrite = TimeSpan.MinValue;

    /// <summary>Offers a value. Returns true when it should be written now; otherwise it is held as the
    /// pending value (replacing any earlier one) for a later <see cref="TryFlush"/>.</summary>
    public bool TryAccept(double value, TimeSpan now, out double toWrite)
    {
        if (_lastWrite == TimeSpan.MinValue || now - _lastWrite >= minimumInterval)
        {
            _lastWrite = now;
            _hasPending = false;
            toWrite = value;
            return true;
        }

        _pending = value;
        _hasPending = true;
        toWrite = default;
        return false;
    }

    /// <summary>Releases the newest held value once the interval has elapsed, so a sweep that stops
    /// mid-interval still lands on its final position rather than short of it.</summary>
    public bool TryFlush(TimeSpan now, out double toWrite)
    {
        if (!_hasPending || now - _lastWrite < minimumInterval)
        {
            toWrite = default;
            return false;
        }

        _lastWrite = now;
        _hasPending = false;
        toWrite = _pending;
        return true;
    }
}
