using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Session;

namespace HaCue2.Core.Tests;

/// <summary>
/// A recording stand-in for a running show.
/// </summary>
/// <remarks>
/// <para>
/// Records what a cue asked for instead of doing it, so <see cref="CueExecutor"/>'s decisions can be
/// asserted without a session, a device or a socket. Waits complete instantly — a test of chain logic
/// must not take as long as the show it describes.
/// </para>
/// <para>
/// Scheduled cues are recorded rather than run. A timeline group's whole point is that its children
/// fire at authored offsets on the show's clock, and a fake that ran them immediately would prove the
/// opposite of what the test is asking.
/// </para>
/// </remarks>
internal sealed class FakeCueHost(HaCueProject project) : ICueExecutionHost
{
    public HaCueProject Project { get; } = project;
    public bool IsExternalTriggerActive { get; set; }

    /// <summary>Cues handed to the session, in the order they were fired.</summary>
    public List<Guid> Played { get; } = [];

    /// <summary>Every standby move, as (list, cue) — null cue means the cursor was cleared.</summary>
    public List<(Guid List, Guid? Cue)> Standby { get; } = [];

    public List<Guid> Stopped { get; } = [];
    public List<(Guid Cue, double LevelDb)> Levels { get; } = [];
    public List<(Guid Cue, double LevelDb, TimeSpan Duration, FadeShape Curve, bool Stop)> CueFades { get; } = [];
    public List<(Guid Cue, TimeSpan When, int Depth)> Scheduled { get; } = [];
    public List<(ActionCueNode Cue, ActionEndpoint? Endpoint)> Actions { get; } = [];
    public List<TimeSpan> Waits { get; } = [];
    public List<string> Problems { get; } = [];
    public List<Guid> Faded { get; } = [];
    public List<(Guid Cue, TimeSpan? Duration, FadeShape Curve)> Transitions { get; } = [];

    /// <summary>Every patch ramp, as the cells it landed on and how long it took.</summary>
    public List<(IReadOnlyList<PatchCell> Destination, TimeSpan Duration)> Patches { get; } = [];

    /// <summary>What is sounding. Settable, so "fade everything" has something to act on.</summary>
    public List<Guid> SoundingCues { get; } = [];

    /// <summary>Makes the next <see cref="PlayAsync"/> report failure — a clip that would not open.</summary>
    public bool PlayFails { get; set; }

    /// <summary>Makes waits report cancellation — the show stopping mid-chain.</summary>
    public bool Cancelled { get; set; }

    /// <summary>What <see cref="SendActionAsync"/> answers. Null is success.</summary>
    public string? ActionFailure { get; set; }

    IReadOnlyList<Guid> ICueExecutionHost.Sounding => SoundingCues;

    public Task<bool> PlayAsync(
        CueNode cue,
        CueList? list,
        TimeSpan? crossfade = null,
        FadeShape crossfadeCurve = default)
    {
        if (PlayFails)
            return Task.FromResult(false);

        Played.Add(cue.Id);
        Transitions.Add((cue.Id, crossfade, crossfadeCurve));
        SoundingCues.Add(cue.Id);
        return Task.FromResult(true);
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
        return Task.FromResult(ActionFailure);
    }

    public void MarkFading(Guid cueId) => Faded.Add(cueId);

    public void Forget(Guid cueId) => SoundingCues.Remove(cueId);

    public void Report(string problem) => Problems.Add(problem);

    public Task<bool> DelayAsync(TimeSpan duration)
    {
        Waits.Add(duration);
        return Task.FromResult(!Cancelled);
    }

    public void Schedule(Guid cueId, TimeSpan when, int depth) => Scheduled.Add((cueId, when, depth));

    /// <summary>What a probe would have said. Absent means nobody has looked, exactly as in the app.</summary>
    public Dictionary<Guid, TimeSpan> Lengths { get; } = [];

    /// <summary>Every seek, as (cue, position in FILE time).</summary>
    public List<(Guid Cue, TimeSpan Position)> Seeks { get; } = [];

    public TimeSpan? MediaLength(Guid cueId) =>
        Lengths.TryGetValue(cueId, out var length) ? length : null;

    public Task SeekCueAsync(Guid cueId, TimeSpan position)
    {
        Seeks.Add((cueId, position));
        return Task.CompletedTask;
    }
}
