namespace S.Media.Routing;

/// <summary>What one <see cref="ClockMasterWatchdog.Evaluate"/> step concluded.</summary>
public enum ClockMasterWatchdogOutcome
{
    /// <summary>Nothing to watch - the bay has no clock master.</summary>
    NoMaster,

    /// <summary>The master is draining normally.</summary>
    Healthy,

    /// <summary>The master looks stalled but has not yet reached the trip threshold. Reported so a
    /// host can show "output struggling" before anything is done about it.</summary>
    Suspect,

    /// <summary>The master tripped and pacing was handed to another terminal.</summary>
    Recovered,

    /// <summary>The master tripped and there was no eligible terminal to hand pacing to. This is the
    /// honest failure the plan keeps as the fallback: report and let the show stop, rather than
    /// pretend.</summary>
    Unrecoverable,
}

/// <summary>Details of a watchdog step, for logging and for the diagnostics surface.</summary>
/// <param name="Outcome">What the step concluded.</param>
/// <param name="MasterTerminalId">The master at the time of the step (before any handoff).</param>
/// <param name="PromotedTerminalId">The terminal pacing became slaved to, when
/// <see cref="ClockMasterWatchdogOutcome.Recovered"/>.</param>
/// <param name="ConsecutiveStalls">How many consecutive steps have now seen the master stalled.</param>
/// <param name="Reason">Human-readable cause, empty when healthy.</param>
public readonly record struct ClockMasterWatchdogStep(
    ClockMasterWatchdogOutcome Outcome,
    string? MasterTerminalId,
    string? PromotedTerminalId,
    int ConsecutiveStalls,
    string Reason);

/// <summary>
/// Watches the bay's clock-master terminal and hands pacing to another line before a stalling master
/// can fault the router.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A pump wedged in a native <c>Submit</c> is survivable for an ordinary
/// terminal - the router quarantines it and every other line keeps running. The pacing master is the
/// exception: when its <c>WaitForCapacity</c> starts failing, the router's mix loop raises a fault and
/// stops, and a router stopped that way is not restartable. So the master is the one terminal where
/// recovery has to happen <i>before</i> the failure, not after it.
/// </para>
/// <para>
/// <b>Deliberately caller-driven.</b> <see cref="Evaluate"/> is one pure step: it never sleeps, never
/// starts a thread, and never runs on the audio thread. The host ticks it from whatever cadence it
/// already has (a 1 Hz diagnostics poll is ideal). That keeps the whole policy deterministic and
/// testable without timers, and it makes it impossible for the watchdog itself to become the thing
/// that stalls the mix loop.
/// </para>
/// <para>
/// <b>Deliberately trigger-happy.</b> A false positive costs almost nothing: promoting a healthy
/// terminal leaves the program mix untouched and only changes which line the loop paces against, which
/// is inaudible. A false negative costs the show. So the detector trips on a short run of stalled
/// observations rather than waiting for certainty.
/// </para>
/// </remarks>
public sealed class ClockMasterWatchdog
{
    /// <summary>Consecutive stalled observations before pacing is moved. Three steps of a 1 Hz poll is
    /// long enough not to fire on one scheduling hiccup and far shorter than a wedge's join cap.</summary>
    public const int DefaultStallTripCount = 3;

    private readonly AudioPatchBay _bay;
    private readonly int _stallTripCount;

    private string? _watchedTerminalId;
    private long _lastProcessed;
    private int _consecutiveStalls;

    public ClockMasterWatchdog(AudioPatchBay bay, int stallTripCount = DefaultStallTripCount)
    {
        ArgumentNullException.ThrowIfNull(bay);
        ArgumentOutOfRangeException.ThrowIfLessThan(stallTripCount, 1);
        _bay = bay;
        _stallTripCount = stallTripCount;
    }

    /// <summary>Consecutive stalled observations of the current master. Zero while healthy.</summary>
    public int ConsecutiveStalls => _consecutiveStalls;

    /// <summary>
    /// Takes one observation of the clock master and acts on it if it has been stalled long enough.
    /// Safe to call at any cadence; calling it more often only shortens the trip time.
    /// </summary>
    public ClockMasterWatchdogStep Evaluate()
    {
        var masterId = _bay.ClockMasterTerminalId;
        if (masterId is null)
        {
            Reset(null);
            return new ClockMasterWatchdogStep(
                ClockMasterWatchdogOutcome.NoMaster, null, null, 0, string.Empty);
        }

        // A master swapped underneath us restarts the observation window rather than inheriting the
        // previous line's stall count.
        if (!string.Equals(masterId, _watchedTerminalId, StringComparison.Ordinal))
            Reset(masterId);

        if (!_bay.TryGetTerminalStats(masterId, out var stats))
        {
            // Attached but not yet pumping (or just swapped): nothing to conclude.
            _consecutiveStalls = 0;
            return new ClockMasterWatchdogStep(
                ClockMasterWatchdogOutcome.Healthy, masterId, null, 0, string.Empty);
        }

        var inFlight = stats.Enqueued - stats.Processed - stats.Dropped - stats.Abandoned;
        var atCapacity = stats.PumpCapacityChunks > 0 && inFlight >= stats.PumpCapacityChunks;
        var notDraining = stats.Processed == _lastProcessed;
        _lastProcessed = stats.Processed;

        string reason;
        if (stats.IsStuck)
        {
            // Already wedged - no point counting further.
            _consecutiveStalls = _stallTripCount;
            reason = "the clock master's pump is wedged in a native Submit";
        }
        else if (notDraining && atCapacity)
        {
            _consecutiveStalls++;
            reason = $"the clock master has not drained a chunk in {_consecutiveStalls} checks " +
                     $"and its queue is full ({inFlight}/{stats.PumpCapacityChunks})";
        }
        else
        {
            _consecutiveStalls = 0;
            return new ClockMasterWatchdogStep(
                ClockMasterWatchdogOutcome.Healthy, masterId, null, 0, string.Empty);
        }

        if (_consecutiveStalls < _stallTripCount)
        {
            return new ClockMasterWatchdogStep(
                ClockMasterWatchdogOutcome.Suspect, masterId, null, _consecutiveStalls, reason);
        }

        if (!_bay.TryPromoteHealthiestClockMaster(out var promoted))
        {
            return new ClockMasterWatchdogStep(
                ClockMasterWatchdogOutcome.Unrecoverable, masterId, null, _consecutiveStalls,
                $"{reason}, and no other attached terminal can pace the bay");
        }

        Reset(promoted);
        return new ClockMasterWatchdogStep(
            ClockMasterWatchdogOutcome.Recovered, masterId, promoted, 0,
            $"{reason}; pacing moved to '{promoted}'");
    }

    private void Reset(string? terminalId)
    {
        _watchedTerminalId = terminalId;
        _lastProcessed = 0;
        _consecutiveStalls = 0;
    }
}
