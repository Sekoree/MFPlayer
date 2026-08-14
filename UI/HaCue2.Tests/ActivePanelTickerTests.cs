using System.Diagnostics;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Session;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// F-11 step 2: the Active panel's state owner, tested directly against a fake clock. Before the
/// extraction these rules - reconcile in place, extrapolate only while running - were only
/// reachable through the whole shell and real time.
/// </summary>
public sealed class ActivePanelTickerTests
{
    [Fact]
    public void APollUpdatesRowsInPlaceInsteadOfReplacingThem()
    {
        var runtime = new ShowRuntime();
        using var ticker = new ActivePanelTicker(runtime, ProjectFor, () => 0);

        var id = Guid.NewGuid();
        ticker.Poll([Row(id, position: TimeSpan.FromSeconds(1))]);
        var kept = Assert.Single(ticker.ActiveCues);

        ticker.Poll([Row(id, position: TimeSpan.FromSeconds(2))]);

        // Same object - a replaced row would kill the seek bar mid-drag and drop button hover.
        Assert.Same(kept, Assert.Single(ticker.ActiveCues));
        Assert.Equal(TimeSpan.FromSeconds(2), kept.Position);
    }

    [Fact]
    public void MembershipChangesInsertAndTrim()
    {
        var runtime = new ShowRuntime();
        using var ticker = new ActivePanelTicker(runtime, ProjectFor, () => 0);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ticker.Poll([Row(first), Row(second)]);
        Assert.Equal(2, ticker.ActiveCues.Count);

        ticker.Poll([Row(second)]);

        Assert.Equal(second, Assert.Single(ticker.ActiveCues).CueId);
        Assert.Equal(second, Assert.IsType<ActiveCueRow>(Assert.Single(ticker.Rows)).CueId);
    }

    [Fact]
    public void TheSmoothClockExtrapolatesFromThePollStamp()
    {
        var runtime = new ShowRuntime();
        var now = 0L;
        using var ticker = new ActivePanelTicker(runtime, ProjectFor, () => now);
        ticker.FlatList = true;

        ticker.Poll([Row(Guid.NewGuid(), position: TimeSpan.FromSeconds(1), duration: TimeSpan.FromSeconds(10))]);
        var row = Assert.Single(ticker.ActiveCues);

        now = Stopwatch.Frequency; // one second later, without sleeping
        ticker.TickClocks();

        Assert.Equal(CuePresentation.PreciseClock(TimeSpan.FromSeconds(2)), row.Clock);
        Assert.Equal(0.2, row.Progress, precision: 3);
    }

    [Fact]
    public void APausedShowHoldsItsClocksStill()
    {
        var runtime = new ShowRuntime { IsPaused = true };
        var now = 0L;
        using var ticker = new ActivePanelTicker(runtime, ProjectFor, () => now);
        ticker.FlatList = true;

        ticker.Poll([Row(Guid.NewGuid(), position: TimeSpan.FromSeconds(1), duration: TimeSpan.FromSeconds(10))]);
        var row = Assert.Single(ticker.ActiveCues);
        var held = row.Clock;

        now = Stopwatch.Frequency;
        ticker.TickClocks();

        // Not a poll interval ahead and snapping back - paused means the readout does not move.
        Assert.Equal(held, row.Clock);
    }

    [Fact]
    public void TheFlatListShowsTheRawRowsThemselves()
    {
        var runtime = new ShowRuntime();
        using var ticker = new ActivePanelTicker(runtime, ProjectFor, () => 0);
        ticker.FlatList = true;

        ticker.Poll([Row(Guid.NewGuid())]);

        Assert.Same(Assert.Single(ticker.ActiveCues), Assert.Single(ticker.Rows));
    }

    /// <summary>
    /// The seam, stated as a rule: the reconciliation and extrapolation live in the ticker, and the
    /// view model may not keep a private copy of either.
    /// </summary>
    [Fact]
    public void TheCuesViewModelDoesNotReconcileOrExtrapolateItself()
    {
        var path = Path.Combine(RepoRoot(), "UI", "HaCue2", "ViewModels", "CuesViewModel.cs");
        Assert.True(File.Exists(path), $"expected the view model at {path}");

        var code = StripComments(File.ReadAllText(path));

        Assert.False(
            code.Contains("StructurallySame", StringComparison.Ordinal),
            "Active-row reconciliation belongs to ActivePanelTicker.Poll - the view model hands "
            + "each engine poll over, it does not match rows itself.");
        // GetElapsedTime itself stays legal in the view model (the double-GO guard uses it);
        // what may not come back is extrapolating a row from its poll stamp.
        Assert.False(
            code.Contains("PolledAtTicks", StringComparison.Ordinal),
            "Playhead extrapolation belongs to ActivePanelTicker.TickClocks and its injected "
            + "clock, never to the view model.");
    }

    private static HaCueProject ProjectFor() => ProjectFiles.Create("Show", "/media");

    private static ActiveCueRow Row(Guid id, TimeSpan position = default, TimeSpan? duration = null) => new()
    {
        CueId = id,
        Number = "1",
        Label = "cue",
        Position = position,
        PolledAtTicks = 0,
        Duration = duration,
    };

    private static string StripComments(string source) =>
        string.Join("\n", source.Split('\n').Select(line =>
        {
            var slashes = line.IndexOf("//", StringComparison.Ordinal);
            return slashes >= 0 ? line[..slashes] : line;
        }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
        return dir!.FullName;
    }
}
