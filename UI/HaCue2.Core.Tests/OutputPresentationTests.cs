using HaCue2.Presentation;
using HaCue2.Session;
using HaCue2.ViewModels;
using S.Control;
using S.Media.Routing;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The drawer chips, the composition table and the transport's chase readout.
/// </summary>
/// <remarks>
/// These are the surfaces an operator reads when something is wrong, which makes a plausible-looking
/// wrong answer the expensive failure: a chip that says "fine" over a device that is not there, or a
/// frame rate divided by an interval of nearly zero, is worse than a blank. Every case below is one of
/// those.
/// </remarks>
public class OutputPresentationTests
{
    private static AudioPatchBayDiagnostics Bay(
        string? clockMaster = null, params TerminalDiagnostics[] terminals) =>
        new(48_000, 4, clockMaster, terminals, [], []);

    private static TerminalDiagnostics Terminal(
        Guid lineId,
        TerminalState state = TerminalState.Open,
        long dropped = 0,
        long inFlight = 1) =>
        new(
            lineId.ToString(),
            state,
            Channels: 8,
            NativeSampleRate: 48_000,
            IsClockMaster: false,
            Stats: new AudioRouter.OutputPumpStats(10, 9, dropped, 4),
            InFlight: inFlight);

    // ── the chips ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAbsentLineReadsAbsentEvenWhenSomethingIsStillOpenUnderItsId()
    {
        var show = new TestProject();
        var runtime = new ShowRuntime { AbsentLines = [show.Wedge.Id] };

        // The device answer WINS over the bay's. A line the host could not open can still have a stale
        // terminal in a snapshot taken across the same moment, and "48k · 0 drop" over a wedge nobody
        // can hear is the single most misleading thing this drawer could say.
        var chips = OutputPresentation.Chips(
            show.Project, Bay(null, Terminal(show.Wedge.Id)), runtime, []);

        var wedge = Assert.Single(chips, chip => chip.Name == "Wedge");
        Assert.Equal("device absent", wedge.Detail);
        Assert.True(wedge.IsError);
    }

    [Fact]
    public void ALineWithNoTerminalSaysItIsNotPatchedRatherThanDisappearing()
    {
        var show = new TestProject();

        var chips = OutputPresentation.Chips(show.Project, Bay(), new ShowRuntime(), []);

        // Both lines are still listed. A line that vanished from the drawer would send an operator
        // looking for a device fault that is not there — "not patched" is the actual answer.
        Assert.Equal(2, chips.Count);
        Assert.All(chips, chip => Assert.Equal("not patched", chip.Detail));
        Assert.All(chips, chip => Assert.True(chip.IsIdle));
    }

    [Fact]
    public void TheClockMasterIsMarkedAndOneDropIsAlreadyAmber()
    {
        var show = new TestProject();
        var master = show.Interface.Id.ToString();

        var chips = OutputPresentation.Chips(
            show.Project,
            Bay(master, Terminal(show.Interface.Id, dropped: 1, inFlight: 3)),
            new ShowRuntime(),
            []);

        var line = Assert.Single(chips, chip => chip.Name == "18i20");
        Assert.Equal("master", line.Suffix);
        Assert.Equal("48k · 1 drop · 3/4", line.Detail);
        // One dropped chunk is a click somebody in the room heard. There is no green number here.
        Assert.True(line.IsWarn);
    }

    [Fact]
    public void AVideoOutputWithNoTelemetrySaysSoRatherThanReportingZeroFps()
    {
        var show = new TestProject();
        show.Project.VideoOutputs.Add(new Core.Model.VideoOutputDefinition
        {
            Name = "Projector A",
            CompositionId = show.Cyc.Id,
        });

        var chips = OutputPresentation.Chips(show.Project, Bay(), new ShowRuntime(), []);

        var projector = Assert.Single(chips, chip => chip.Name == "Projector A");
        Assert.Equal("no frames yet", projector.Detail);
        Assert.True(projector.IsIdle);
    }

    [Fact]
    public void AnUnopenedVideoOutputReadsAbsent()
    {
        var show = new TestProject();
        var output = new Core.Model.VideoOutputDefinition
        {
            Name = "Lobby TV",
            CompositionId = show.Cyc.Id,
        };

        show.Project.VideoOutputs.Add(output);
        var runtime = new ShowRuntime { AbsentVideoOutputs = [output.Id] };

        var chips = OutputPresentation.Chips(show.Project, Bay(), runtime, [Stats(show.Cyc.Id)]);

        var lobby = Assert.Single(chips, chip => chip.Name == "Lobby TV");
        Assert.Equal("screen absent", lobby.Detail);
        Assert.True(lobby.IsError);
    }

    // ── the composition table ─────────────────────────────────────────────────────────────────

    private static ClipCompositionRuntimeStats Stats(
        Guid compositionId,
        long composited = 0,
        long behind = 0,
        int layers = 0) =>
        new(
            compositionId.ToString(),
            FramesComposited: composited,
            FramesSubmitted: composited,
            PumpOverruns: 0,
            SlotOverflowFrames: 0,
            LastPumpFrameTime: TimeSpan.Zero,
            MaxPumpFrameTime: TimeSpan.Zero,
            FramesBehindMaster: behind,
            ClockMastered: true,
            LayerCount: layers,
            CanvasPeriod: TimeSpan.FromSeconds(1d / 30));

    [Fact]
    public void ACompositionNobodyHasTimedYetShowsADashRatherThanZero()
    {
        var show = new TestProject();

        var rows = OutputPresentation.Compositions(
            show.Project,
            [Stats(show.Cyc.Id, layers: 3)],
            new Dictionary<string, double>());

        var row = Assert.Single(rows);
        Assert.Equal("Cyc · 1920×1080", row.Name);
        // Zero fps means a composition that has STOPPED. One nobody has measured yet must not read the
        // same, or the column is noise for the first quarter-second of every show.
        Assert.Equal("— / 30", row.Fps.Text);
        Assert.Equal("3", row.Layers);
    }

    [Fact]
    public void ACompositionBehindItsTargetIsAmberOnBothColumns()
    {
        var show = new TestProject();

        var rows = OutputPresentation.Compositions(
            show.Project,
            [Stats(show.Cyc.Id, behind: 6)],
            new Dictionary<string, double> { [show.Cyc.Id.ToString()] = 21.5 });

        var row = Assert.Single(rows);
        Assert.Equal("21.5 / 30", row.Fps.Text);
        Assert.Equal(Gel.Amber, row.Fps.Gel);
        Assert.Equal("6", row.Late.Text);
        Assert.Equal(Gel.Amber, row.Late.Gel);
    }

    [Fact]
    public void TheAuditionCanvasIsListedButNeverAsOneOfTheShows()
    {
        var show = new TestProject();

        var rows = OutputPresentation.Compositions(
            show.Project,
            [Stats(show.Cyc.Id) with { CompositionId = ShowSession.AuditionCompositionId }],
            new Dictionary<string, double>());

        // A monitor rig dropping frames is worth knowing about, so it gets a row — but it has no
        // document behind it and must not read as a composition the operator authored.
        Assert.Equal("Audition monitor", Assert.Single(rows).Name);
    }

    // ── the achieved frame rate ───────────────────────────────────────────────────────────────

    [Fact]
    public void OneSampleIsACountNotARate()
    {
        var rates = new CompositionRates();

        // Nothing to divide by yet. Reporting 0 here would put every composition in the show on amber
        // for its first tick.
        Assert.Empty(rates.Sample([Stats(Guid.NewGuid(), composited: 120)]));
    }

    [Fact]
    public async Task TwoSamplesAcrossARealIntervalGiveARate()
    {
        var rates = new CompositionRates();
        var id = Guid.NewGuid();

        rates.Sample([Stats(id, composited: 0)]);
        await Task.Delay(120);

        var sampled = rates.Sample([Stats(id, composited: 12)]);

        // Twelve frames over roughly an eighth of a second. The bound is loose on purpose — this
        // asserts that the division happened against wall time, not that the box is a metronome.
        Assert.InRange(sampled[id.ToString()], 20, 200);
    }

    [Fact]
    public void TwoReadsInsideTheSameInstantReportNothingRatherThanThousands()
    {
        var rates = new CompositionRates();
        var id = Guid.NewGuid();

        rates.Sample([Stats(id, composited: 0)]);

        // Back-to-back, so the interval is microseconds. Dividing by it would put four figures in the
        // fps column; the honest answer is that this tick measured nothing.
        Assert.Empty(rates.Sample([Stats(id, composited: 4)]));
    }

    [Fact]
    public async Task ACompositionThatWentAwayIsTimedFromItsReturnNotAcrossTheGap()
    {
        var rates = new CompositionRates();
        var id = Guid.NewGuid();

        rates.Sample([Stats(id, composited: 500)]);
        await Task.Delay(60);
        rates.Sample([]);

        // Rebuilt by a reload: the counter restarts at zero. Measured across the gap this would be a
        // large NEGATIVE delta clamped to nothing, and the first real reading afterwards would be
        // wrong too — so the anchor has to have been dropped with the composition.
        Assert.Empty(rates.Sample([Stats(id, composited: 0)]));
    }

    // ── the chase readout ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOperatorsOwnSwitchIsNeverReportedAsAFault() =>
        Assert.Equal(
            "MTC · input off",
            OutputPresentation.Chase(default, inputEnabled: false));

    [Fact]
    public void NoSignalAndUndecodableAreDifferentAnswers()
    {
        Assert.Equal(
            "MTC · no signal",
            OutputPresentation.Chase(default, inputEnabled: true));

        // Timecode that ARRIVES and never assembles — two senders on one port, or a mangled stream.
        // Without a name of its own it looks exactly like an unplugged cable, which sends people to
        // check the wrong end of it.
        Assert.Equal(
            "MTC · undecodable",
            OutputPresentation.Chase(
                new MidiTimecodeChaseState(
                    HasSignal: false, IsChasing: false, MidiTimecodeRate.Fps25,
                    PositionSeconds: 0, Position: default, Generation: 0,
                    GenerationStartSeconds: 0, UndecodedQuarterFrames: 40),
                inputEnabled: true));
    }

    [Fact]
    public void AStalledSenderIsMarkedHeldRatherThanShownAsChasing()
    {
        var position = new MidiTimecodeValue(1, 12, 44, 7, MidiTimecodeRate.Fps25);

        var chasing = OutputPresentation.Chase(
            new MidiTimecodeChaseState(true, true, MidiTimecodeRate.Fps25, 0, position, 1, 0),
            inputEnabled: true);

        var held = OutputPresentation.Chase(
            new MidiTimecodeChaseState(true, false, MidiTimecodeRate.Fps25, 0, position, 1, 0),
            inputEnabled: true);

        Assert.Equal("MTC 01:12:44:07", chasing);
        // The label is the last one the sender actually reached. Saying so is the difference between a
        // frozen readout and a lying one.
        Assert.Equal("MTC 01:12:44:07 · held", held);
    }

    /// <summary>
    /// The curve table is readable without a renderer — which is what makes every view-model that
    /// offers a fade picker constructible without one.
    /// </summary>
    /// <remarks>
    /// This assembly has no Avalonia platform at all, which is exactly the environment that caught the
    /// original defect: <c>CurveOption</c> parsed its thumbnail geometry in its CONSTRUCTOR, so
    /// <see cref="CurveLibrary"/>'s static initializer needed <c>IPlatformRenderInterface</c>. When it
    /// ran first from a test that had no session, .NET cached the resulting
    /// <see cref="TypeInitializationException"/> for the whole process and every later view-model test
    /// in that assembly failed with it — 227 of them on one CI leg, none on the other, decided purely by
    /// which test happened to run first. Reading the table here would throw again the moment the parse
    /// moves back into the constructor.
    /// </remarks>
    [Fact]
    public void TheCurveTableIsReadableWithoutARenderer()
    {
        var curves = CurveLibrary.Curves;

        // The order is load-bearing (the index IS the FadeCurve enum value), so pin the list, not a count.
        Assert.Equal(
            ["linear", "eq-power", "expo", "s-curve", "custom ✎"],
            curves.Select(curve => curve.Name));
        Assert.All(curves, curve => Assert.StartsWith("M", curve.PathData, StringComparison.Ordinal));
    }
}
