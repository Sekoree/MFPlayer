using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Session;
using S.Media.Time;

namespace HaCue2.Core.Tests;

/// <summary>
/// A recording stand-in for a running show.
/// </summary>
/// <remarks>
/// <para>
/// Records what a cue asked for instead of doing it, so <see cref="CueExecutor"/>'s decisions can be
/// asserted without a session, a device or a socket. Waits complete instantly - a test of chain logic
/// must not take as long as the show it describes.
/// </para>
/// <para>The timeline clock is virtual: scheduler waits advance it immediately, preserving authored
/// positions without making the test sleep.</para>
/// </remarks>
internal sealed class FakeCueHost(HaCueProject project) : ICueExecutionHost
{
    public HaCueProject Project { get; } = project;
    public bool IsExternalTriggerActive { get; set; }

    /// <summary>Cues handed to the session, in the order they were fired.</summary>
    public List<Guid> Played { get; } = [];

    /// <summary>Every standby move, as (list, cue) - null cue means the cursor was cleared.</summary>
    public List<(Guid List, Guid? Cue)> Standby { get; } = [];

    public List<Guid> Stopped { get; } = [];
    public List<(Guid Cue, double LevelDb)> Levels { get; } = [];
    public List<(Guid Cue, double LevelDb, TimeSpan Duration, FadeShape Curve, bool Stop)> CueFades { get; } = [];
    public List<(Guid Cue, TimeSpan? StartPosition, TimeSpan MasterTime)> TimelineStarts { get; } = [];
    public List<(Guid Cue, TimeSpan StartPosition, TimeSpan MasterTime)> VisualizerStarts { get; } = [];
    public List<(Guid Cue, TimeSpan MasterTime)> ControlStarts { get; } = [];
    public List<(ActionCueNode Cue, ActionEndpoint? Endpoint)> Actions { get; } = [];
    public List<Guid> Automations { get; } = [];
    public List<(Guid Cue, TimeSpan Position)> AutomationStarts { get; } = [];
    public List<TimeSpan> Waits { get; } = [];
    public List<string> Problems { get; } = [];
    public List<Guid> Faded { get; } = [];
    public List<(Guid Cue, TimeSpan? Duration, FadeShape Curve)> Transitions { get; } = [];

    /// <summary>Every patch ramp, as the cells it landed on and how long it took.</summary>
    public List<(IReadOnlyList<PatchCell> Destination, TimeSpan Duration)> Patches { get; } = [];

    /// <summary>What is sounding. Settable, so "fade everything" has something to act on.</summary>
    public List<Guid> SoundingCues { get; } = [];

    /// <summary>Makes the next <see cref="PlayAsync"/> report failure - a clip that would not open.</summary>
    public bool PlayFails { get; set; }

    /// <summary>
    /// When set, decides a <see cref="PlayAsync"/> outcome per cue instead of <see cref="PlayFails"/>.
    /// The hook a race test needs: the executor's advance fires OUTSIDE its state gate, and this is
    /// the only seam where a test can end another cue while that fire is still in flight. Returning
    /// false records nothing, exactly like <see cref="PlayFails"/>; null falls through to it.
    /// </summary>
    public Func<CueNode, Task<bool>>? PlayOverride { get; set; }

    /// <summary>Makes waits report cancellation - the show stopping mid-chain.</summary>
    public bool Cancelled { get; set; }

    /// <summary>What <see cref="SendActionAsync"/> answers. Null is success.</summary>
    public string? ActionFailure { get; set; }

    IReadOnlyList<Guid> ICueExecutionHost.Sounding => SoundingCues;

    public async Task<bool> PlayAsync(
        CueNode cue,
        CueList? list,
        TimeSpan? crossfade = null,
        FadeShape crossfadeCurve = default)
    {
        if (PlayOverride is { } outcome)
        {
            if (!await outcome(cue))
                return false;
        }
        else if (PlayFails)
        {
            return false;
        }

        Played.Add(cue.Id);
        ControlStarts.Add((cue.Id, _timelineClock.ElapsedSinceStart));
        Transitions.Add((cue.Id, crossfade, crossfadeCurve));
        SoundingCues.Add(cue.Id);
        return true;
    }

    /// <summary>Batches recorded as batches, so a test can tell "all together" from "one after another".</summary>
    public List<IReadOnlyList<Guid>> PlayedTogether { get; } = [];

    public async Task<IReadOnlyList<Guid>> PlayTimelineMediaAsync(
        IReadOnlyList<TimelineMediaStart> cues,
        CueList? list,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken)
    {
        await waitForStartEdge(cancellationToken);
        if (PlayFails)
            return [];

        foreach (var start in cues)
        {
            Played.Add(start.Cue.Id);
            Transitions.Add((start.Cue.Id, null, default));
            SoundingCues.Add(start.Cue.Id);
            TimelineStarts.Add((start.Cue.Id, start.StartPosition, _timelineClock.ElapsedSinceStart));
        }

        IReadOnlyList<Guid> started = [.. cues.Select(start => start.Cue.Id)];
        if (started.Count > 1)
            PlayedTogether.Add(started);
        return started;
    }

    public async Task<IReadOnlyList<Guid>> PlayTimelineVisualizersAsync(
        IReadOnlyList<TimelineVisualizerStart> cues,
        CueList? list,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken)
    {
        await waitForStartEdge(cancellationToken);
        if (PlayFails)
            return [];

        foreach (var start in cues)
        {
            Played.Add(start.Cue.Id);
            ControlStarts.Add((start.Cue.Id, _timelineClock.ElapsedSinceStart));
            VisualizerStarts.Add((
                start.Cue.Id, start.StartPosition, _timelineClock.ElapsedSinceStart));
            Transitions.Add((start.Cue.Id, null, default));
            SoundingCues.Add(start.Cue.Id);
        }

        return [.. cues.Select(start => start.Cue.Id)];
    }

    public Task SetStandbyAsync(CueList list, Guid? cueId)
    {
        Standby.Add((list.Id, cueId));
        return Task.CompletedTask;
    }

    public Task StopCueAsync(Guid cueId)
    {
        Stopped.Add(cueId);
        return Task.CompletedTask;
    }

    public Task SetCueLevelAsync(Guid cueId, double levelDb)
    {
        Levels.Add((cueId, levelDb));
        return Task.CompletedTask;
    }

    public Task FadeCueAsync(
        Guid cueId,
        double levelDb,
        TimeSpan duration,
        FadeShape curve,
        bool stopWhenSilent)
    {
        CueFades.Add((cueId, levelDb, duration, curve, stopWhenSilent));
        if (stopWhenSilent)
            Stopped.Add(cueId);
        else
            Levels.Add((cueId, levelDb));
        return Task.CompletedTask;
    }

    public Task ApplyPatchAsync(
        IReadOnlyList<PatchCell> origin,
        IReadOnlyList<PatchCell> destination,
        TimeSpan duration,
        FadeShape curve)
    {
        Patches.Add((destination, duration));
        return Task.CompletedTask;
    }

    public Task<string?> SendActionAsync(ActionCueNode action, ActionEndpoint? endpoint)
    {
        Actions.Add((action, endpoint));
        ControlStarts.Add((action.Id, _timelineClock.ElapsedSinceStart));
        return Task.FromResult(ActionFailure);
    }

    public Task<bool> RunAutomationAsync(
        AutomationCueNode automation,
        CueList? list,
        TimeSpan initialPosition,
        CancellationToken cancellationToken = default)
    {
        Automations.Add(automation.Id);
        AutomationStarts.Add((automation.Id, initialPosition));
        ControlStarts.Add((automation.Id, _timelineClock.ElapsedSinceStart));
        return Task.FromResult(true);
    }

    public void MarkFading(Guid cueId) => Faded.Add(cueId);

    public void Forget(Guid cueId) => SoundingCues.Remove(cueId);

    public void Report(string problem) => Problems.Add(problem);

    public Task<bool> DelayAsync(TimeSpan duration)
    {
        Waits.Add(duration);
        return Task.FromResult(!Cancelled);
    }

    private readonly VirtualPlaybackClock _timelineClock = new();

    public IPlaybackClock TimelineClock => _timelineClock;

    public bool TimelinePaused { get; set; }

    /// <summary>Master time the fake show has "spent paused". Settable, so a test can pause a timeline
    /// without a clock that really stops.</summary>
    public TimeSpan TimelinePausedElapsed { get; set; }

    public Func<TimeSpan, CancellationToken, Task>? TimelineDelayOverride { get; set; }

    public TimeSpan TotalTimelineAdvanced => _timelineClock.TotalAdvanced;

    /// <summary>
    /// Advances the virtual master, accruing paused time while <see cref="TimelinePaused"/> is set.
    /// </summary>
    /// <remarks>
    /// Mirrors what the real host does at its pause TRANSITIONS: paused time is master time the show
    /// spent stopped, and a timeline's coordinate is master-elapsed minus it. A fake that moved the
    /// clock without accruing it would let a timeline advance through a pause - which is precisely the
    /// behaviour <c>PauseFreezesTimelineEvenWhileTheDeviceClockKeepsAdvancing</c> exists to forbid.
    /// </remarks>
    public void AdvanceTimeline(TimeSpan duration)
    {
        if (TimelinePaused && duration > TimeSpan.Zero)
            TimelinePausedElapsed += duration;
        _timelineClock.Advance(duration);
    }

    public void ReanchorTimeline(TimeSpan elapsed) => _timelineClock.Reanchor(elapsed);

    public async Task DelayTimelineAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (TimelineDelayOverride is { } delay)
        {
            await delay(duration, cancellationToken);
            return;
        }

        // Yield before advancing so a voice released at the preceding edge gets its continuation before
        // the virtual master moves toward the next event (real device time naturally has that property).
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        _timelineClock.Advance(duration);
    }

    private sealed class VirtualPlaybackClock : IPlaybackClock
    {
        private long _elapsedTicks;
        private long _totalAdvancedTicks;
        private long _epoch;

        public TimeSpan ElapsedSinceStart => TimeSpan.FromTicks(Volatile.Read(ref _elapsedTicks));
        public TimeSpan TotalAdvanced => TimeSpan.FromTicks(Volatile.Read(ref _totalAdvancedTicks));
        public bool IsAdvancing => true;
        public ClockReading Read() => new(Volatile.Read(ref _epoch), ElapsedSinceStart, IsAdvancing);

        public void Advance(TimeSpan duration)
        {
            var ticks = Math.Max(0, duration.Ticks);
            Interlocked.Add(ref _elapsedTicks, ticks);
            Interlocked.Add(ref _totalAdvancedTicks, ticks);
        }

        public void Reanchor(TimeSpan elapsed)
        {
            Volatile.Write(ref _elapsedTicks, Math.Max(0, elapsed.Ticks));
            Volatile.Write(ref _epoch, PlaybackEpoch.Next());
        }
    }

    /// <summary>What a probe would have said. Absent means nobody has looked, exactly as in the app.</summary>
    public Dictionary<Guid, TimeSpan> Lengths { get; } = [];

    public TimeSpan? MediaLength(Guid cueId) =>
        Lengths.TryGetValue(cueId, out var length) ? length : null;
}
