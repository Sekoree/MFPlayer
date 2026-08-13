using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Control;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The two ways a cue fires with nobody at the desk: the wall clock, and incoming timecode.
/// </summary>
/// <remarks>
/// Everything here is about a CROSSING rather than an equality, and every rule below exists because
/// the naive version fails in front of an audience: a tick that lands exactly on the second is a
/// coincidence, a deck spooled back to the top must not fire the whole act, and a machine that stalled
/// for a minute must not come back and fire a cue whose moment has passed.
/// </remarks>
public class TriggerClockTests
{
    private const double HalfPastTen = (22 * 3600) + (30 * 60);

    private static (TriggerClocks Clocks, List<TriggerAction> Fired, TriggerInputDefinition Source)
        Schedule(params string[] times) => Build(TriggerInputKind.Schedule, times);

    private static (TriggerClocks Clocks, List<TriggerAction> Fired, TriggerInputDefinition Source)
        Timecode(params string[] labels) => Build(TriggerInputKind.Timecode, labels);

    private static (TriggerClocks, List<TriggerAction>, TriggerInputDefinition) Build(
        TriggerInputKind kind, string[] inputs)
    {
        var cue = new MediaCueNode { Number = "1", Label = "Walk-in" };

        var source = new TriggerInputDefinition
        {
            Name = kind == TriggerInputKind.Schedule ? "House clock" : "Deck",
            Kind = kind,
            Bindings =
            [
                .. inputs.Select(input => new TriggerBinding
                {
                    Input = input,
                    Target = TriggerTarget.Cue,
                    TargetCueId = cue.Id,
                }),
            ],
        };

        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Act 1", Cues = [cue] }],
            TriggerInputs = [source],
        };

        var clocks = new TriggerClocks(project, new TimecodeChase());
        var fired = new List<TriggerAction>();
        clocks.Triggered += fired.Add;

        return (clocks, fired, source);
    }

    private static MidiTimecodeChaseState At(double seconds, int generation = 1, bool chasing = true) =>
        new(
            HasSignal: true,
            IsChasing: chasing,
            Rate: MidiTimecodeRate.Fps25,
            PositionSeconds: seconds,
            Position: MidiTimecodeValue.FromSeconds(seconds, MidiTimecodeRate.Fps25),
            Generation: generation,
            GenerationStartSeconds: 0);

    // ── the wall clock ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFirstPassFiresNothingBecauseThereIsNoWindowYet()
    {
        var (clocks, fired, source) = Schedule("22:30");

        // One reading is a position, not a window. Firing on it would mean an app started at 22:31
        // immediately fired the 22:30 cue.
        clocks.WallPass([source], HalfPastTen + 1);

        Assert.Empty(fired);
    }

    [Fact]
    public void ACrossingFiresOnceAndOnlyOnce()
    {
        var (clocks, fired, source) = Schedule("22:30");

        clocks.WallPass([source], HalfPastTen - 0.5);
        clocks.WallPass([source], HalfPastTen + 0.2);

        Assert.Single(fired);
        Assert.Contains("22:30", Assert.Single(fired).Describe, StringComparison.Ordinal);

        // Every pass afterwards is past the moment. A cue that re-fired four times a second for the
        // rest of the show is the failure this rule exists for.
        clocks.WallPass([source], HalfPastTen + 0.4);
        clocks.WallPass([source], HalfPastTen + 1.0);

        Assert.Single(fired);
    }

    [Fact]
    public void ALateTickStillFiresRatherThanSkipping()
    {
        var (clocks, fired, source) = Schedule("22:30:00");

        clocks.WallPass([source], HalfPastTen - 0.2);
        // A pass a second and a half late - a big document load, a garbage collection. The cue is late,
        // which is better than absent.
        clocks.WallPass([source], HalfPastTen + 1.5);

        Assert.Single(fired);
    }

    [Fact]
    public void AMachineThatStalledForAMinuteDoesNotFireWhatItMissed()
    {
        var (clocks, fired, source) = Schedule("22:30:00");

        clocks.WallPass([source], HalfPastTen - 30);
        // A laptop lid, a suspended process. Firing here would start a cue whose moment is half a
        // minute gone, which is worse than not starting it at all.
        clocks.WallPass([source], HalfPastTen + 30);

        Assert.Empty(fired);
    }

    [Fact]
    public void MidnightIsAWrapRatherThanAJumpBackwards()
    {
        var (clocks, fired, source) = Schedule("00:00:10");

        clocks.WallPass([source], (23 * 3600) + 3599);
        // The reading went DOWN. Judged as a window it would be negative, and every schedule earlier
        // in the day would look like it had just been crossed.
        clocks.WallPass([source], 5);
        clocks.WallPass([source], 11);

        Assert.Single(fired);
    }

    [Fact]
    public void ArmingExternalInputDoesNotCatchUpOnTheEveningSoFar()
    {
        var (clocks, fired, source) = Schedule("22:30");

        clocks.WallPass([source], HalfPastTen - 1);
        clocks.SetEnabled(true);

        // Re-anchored: an operator arming at 22:29:59 has not asked for anything that already passed,
        // and the next pass is the first half of a fresh window.
        clocks.WallPass([source], HalfPastTen + 1);

        Assert.Empty(fired);
    }

    [Fact]
    public void ADisabledSourceIsNotEvenLookedAt()
    {
        var (clocks, fired, source) = Schedule("22:30");
        source.Enabled = false;

        // The per-source enable is the "which of them" half of register item 3. The caller filters,
        // which is what a disabled source means everywhere else in this class.
        clocks.WallPass([], HalfPastTen - 1);
        clocks.WallPass([], HalfPastTen + 1);

        Assert.Empty(fired);
    }

    // ── timecode ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TimecodeFiresOnTheCrossing()
    {
        var (clocks, fired, source) = Timecode("00:00:10:00");

        clocks.TimecodePass([source], At(9.5));
        clocks.TimecodePass([source], At(10.2));

        Assert.Single(fired);
    }

    [Fact]
    public void ARelocateRetiresEverythingBehindItInsteadOfFiringTheWholeAct()
    {
        var (clocks, fired, source) = Timecode("00:00:10:00", "00:00:20:00", "00:00:30:00");

        clocks.TimecodePass([source], At(35, generation: 1));
        clocks.TimecodePass([source], At(36, generation: 1));

        // Spooled back to the top. The chase clock bumps its generation, which is the whole mechanism
        // a scheduler has for telling a relocate from a roll - without it, the next pass would sweep
        // from 36 s down through every target and fire all three.
        clocks.TimecodePass([source], At(0, generation: 2));
        clocks.TimecodePass([source], At(0.5, generation: 2));

        Assert.Empty(fired);
    }

    [Fact]
    public void AStalledSenderFiresNothingAndDoesNotRepeat()
    {
        var (clocks, fired, source) = Timecode("00:00:10:00");

        clocks.TimecodePass([source], At(9.5));

        // The position FREEZES when the sender stalls. Judged as a crossing every quarter-second it
        // would fire the cue and then keep firing it, over a deck that had stopped.
        clocks.TimecodePass([source], At(10.5, chasing: false));
        clocks.TimecodePass([source], At(10.5, chasing: false));

        Assert.Empty(fired);
    }

    [Fact]
    public void TimecodeResumesFromWhereItRestartedRatherThanFromTheStall()
    {
        var (clocks, fired, source) = Timecode("00:00:20:00");

        clocks.TimecodePass([source], At(9.5));
        clocks.TimecodePass([source], At(10, chasing: false));

        // Rolling again. The first pass back is an anchor, not a window - measured against the
        // pre-stall reading it would sweep the whole gap.
        clocks.TimecodePass([source], At(25));
        clocks.TimecodePass([source], At(25.5));

        Assert.Empty(fired);
    }

    [Fact]
    public void ATimecodeBindingCanDriveTheTransportAsWellAsACue()
    {
        var (clocks, fired, source) = Timecode("00:00:10:00");
        source.Bindings[0].Target = TriggerTarget.Transport;
        source.Bindings[0].ParameterId = "stop";

        clocks.TimecodePass([source], At(9.5));
        clocks.TimecodePass([source], At(10.5));

        var action = Assert.Single(fired);
        Assert.Equal(TriggerTarget.Transport, action.Target);
        Assert.Equal("stop", action.ParameterId);
    }

    [Fact]
    public void AClockBindingOnAParameterIsRefusedRatherThanWritingAnArbitraryNumber()
    {
        var (clocks, fired, source) = Schedule("22:30");
        source.Bindings[0].Target = TriggerTarget.Parameter;
        source.Bindings[0].ParameterId = "master.trim";

        clocks.WallPass([source], HalfPastTen - 0.5);
        clocks.WallPass([source], HalfPastTen + 0.5);

        // A clock carries no value. Same rule the note-on path follows, and for the same reason: the
        // alternative is a master trim moved to whatever number the code happened to have.
        Assert.Empty(fired);
    }
}
