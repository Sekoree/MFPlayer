using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// The stop protocol every sounding source shares: who owns a source's levels and its teardown while more
/// than one stop wants it, and how a stop that does NOT own it still returns only once the source is gone.
///
/// <para>It exists once because the rule is one rule. It was written twice - <c>TransportVoice</c>'s
/// fade-out claim and <c>VoicePlayer.VoiceHandle</c>'s stop claim - and the two copies had already drifted
/// in three ways by the time they were merged (2026-07-30 review §3): one disposed the superseded
/// <see cref="CancellationTokenSource"/> and the other deliberately did not, one cancelled a dependent ramp
/// on claim and the other had none to cancel, and one disposed its source on teardown WITHOUT cancelling it
/// first, so a stop ramp kept stepping against a released subject. A protocol that two stop domains must
/// implement identically, under concurrency, is not something to keep in sync by hand.</para>
///
/// <para><b>The rule.</b> The claim is held by whichever stop lands SILENT FIRST. An in-flight claim whose
/// deadline is no later than a newcomer's keeps it, and the newcomer becomes a waiter. A newcomer with an
/// EARLIER deadline supersedes: the incumbent's ramp token is cancelled and the new claim owns the levels
/// and the release from wherever that ramp had reached. That ordering is what lets Panic (0 ms) reach a
/// source a 30 s stop fade already owns, while a second, longer stop cannot chop a short fade off part-way
/// down.</para>
///
/// <para>Dispatcher-confined: <see cref="TryClaim"/> and <see cref="Cancel"/> are called from the session
/// dispatcher only. <see cref="IsClaimed"/> and <see cref="Released"/> are read from ramp steps running off
/// it, hence the volatile read and the completion source.</para>
/// </summary>
internal sealed class SoundingStopClaim
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _claimed;
    private CancellationTokenSource? _cts;
    private DateTime _deadline;

    /// <summary>True once any stop has claimed this source. Checked by every ramp step that is NOT the
    /// claiming stop's - a fade cue, a clip fade-in, a soundboard tile's own fade-out - so an operator stop
    /// preempts them. Never cleared: a superseding stop takes the claim OVER rather than releasing it, so
    /// there is no window in which a fade could slip back in between two stops.</summary>
    public bool IsClaimed => Volatile.Read(ref _claimed) != 0;

    /// <summary>Completes once the source has actually been torn down. A stop that LOSES the claim awaits
    /// this instead of releasing the source itself, so "stopped means stopped" holds for the losing caller
    /// too - it returns when the source is genuinely gone, not when it discovered someone else owns it.</summary>
    public Task Released => _released.Task;

    /// <summary>Signals <see cref="Released"/>. Called at the very END of a teardown, so an awaiting stop
    /// resumes on a source that is already off the level bus and off its device.</summary>
    public void MarkReleased() => _released.TrySetResult();

    /// <summary>Claims this source's levels for a stop that intends it silent by <paramref name="deadline"/>
    /// and returns the claiming ramp's token - or null when this caller does NOT own it (see the type
    /// remarks for the ordering). Dispatcher-confined.</summary>
    public CancellationToken? TryClaim(DateTime deadline)
    {
        if (_cts is { IsCancellationRequested: false } && _deadline <= deadline)
            return null;
        // Cancel, never Dispose. A superseded ramp may still be reading this token off the dispatcher, and a
        // cancelled source with no timer and no registrations holds no unmanaged state, so GC reclaims it.
        // (Disposing after Cancel happens to be safe on .NET - an already-cancelled token short-circuits
        // Task.Delay before it can register - but the guarantee is incidental, and this side of the merge
        // was the one that had reasoned it through.)
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _deadline = deadline;
        Volatile.Write(ref _claimed, 1);
        return _cts.Token;
    }

    /// <summary>Ends any in-flight stop ramp: the source is going away, so no step may write it again.
    /// Called from teardown - which is the half the transport copy was missing, leaving a claimed ramp
    /// stepping against a retired voice until it timed out on its own duration.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Waits for the stop that OWNS this source to finish releasing it, then returns - the losing caller's
    /// half of "stopped means stopped".
    /// <para>Bounded, and the bound is the point: the owner's deadline is no later than
    /// <paramref name="deadline"/> (that is WHY this caller is the one waiting), so it should already be
    /// done - but an unbounded wait would turn a fault inside the owning stop into a Stop/Panic that never
    /// returns. On expiry this caller releases the source itself via <paramref name="releaseFallback"/> and
    /// says so.</para>
    /// </summary>
    /// <param name="subjectKind">What the source IS, for the warning ("cue", "voice").</param>
    /// <param name="subject">Its operator-facing id.</param>
    public async Task AwaitReleaseAsync(
        DateTime deadline, string subjectKind, string subject, Func<Task> releaseFallback)
    {
        var grace = deadline - DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (grace < TimeSpan.FromSeconds(1))
            grace = TimeSpan.FromSeconds(1);
        using var timeout = new CancellationTokenSource();
        var expired = Task.Delay(grace, timeout.Token);
        var finished = await Task.WhenAny(Released, expired).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false); // drop the timer as soon as it is moot
        if (ReferenceEquals(finished, Released))
            return;

        MediaDiagnostics.LogWarning(
            "ShowSession: the stop that claimed {0} '{1}' did not release it within {2} s; "
            + "releasing it from this stop instead.",
            subjectKind, subject, Math.Round(grace.TotalSeconds, 1));
        await releaseFallback().ConfigureAwait(false);
    }
}
