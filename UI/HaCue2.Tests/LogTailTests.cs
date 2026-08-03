using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.ViewModels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The Diagnostics log tail (register item 27).
/// </summary>
/// <remarks>
/// One logging system, two readers: the panel is a bounded live view over the SAME
/// <c>Microsoft.Extensions.Logging</c> pipeline the file log uses. A second event-collection system
/// would drift from the archive the moment either changed, and the screen an operator reads during a
/// fault would stop matching the file they send afterwards.
/// </remarks>
/// <remarks>
/// These share one process-wide pipeline, which is safe because this assembly disables test
/// parallelization wholesale (see <c>AvaloniaHeadlessTestApp</c>) — the headless session requires it.
/// Each test clears the ring first rather than assuming it is empty.
/// </remarks>
public class LogTailTests
{
    /// <summary>The installed pipeline, or one installed for this test if the app has not run.</summary>
    private static AppLogging Logging => AppLogging.Current ?? AppLogging.Install(new AppSettings());

    private static DiagnosticsViewModel Panel()
    {
        Logging.Ring.Clear();
        return new DiagnosticsViewModel(new ShowRuntime());
    }

    [Fact]
    public void TheFrameworksOwnLoggerReachesTheTail()
    {
        var panel = Panel();

        // Through MediaDiagnostics, not the app's own logger: this is what makes a wedged output pump
        // or a refused device appear in the panel. Without it the tail would only ever show what the
        // app itself wrote, which is the least interesting half.
        MediaDiagnostics.CreateLogger("S.Media.Routing.AudioRouter")
            .LogWarning("OutputPump Record: Submit took 118 ms");

        panel.Refresh();

        var line = Assert.Single(panel.Log);
        Assert.Equal("WARN", line.Level);
        Assert.Contains("118 ms", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCategoryIsShortenedToItsLastSegment()
    {
        var panel = Panel();
        MediaDiagnostics.CreateLogger("S.Media.Routing.AudioRouter").LogInformation("hello");
        panel.Refresh();

        // "S.Media.Routing.AudioRouter" is a column nobody can read at that width, and the last
        // segment is the part that identifies the source.
        Assert.Equal("AudioRouter", panel.Log[0].Category);
    }

    [Fact]
    public void TheFilterHidesLinesBelowItWithoutLosingThem()
    {
        var panel = Panel();
        var logger = MediaDiagnostics.CreateLogger("HaCue2.Test");

        logger.LogDebug("a debug line");
        logger.LogWarning("a warning");

        panel.MinimumLevel = "Warning";
        Assert.Single(panel.Log);

        // Turning the filter DOWN shows what was already captured, rather than only what arrives
        // afterwards — the whole reason the ring captures below the display threshold. A fault that
        // reproduces once is what this is for.
        panel.MinimumLevel = "Debug";
        Assert.Equal(2, panel.Log.Count);
    }

    [Fact]
    public void TheNewestLineIsFirst()
    {
        var panel = Panel();
        var logger = MediaDiagnostics.CreateLogger("HaCue2.Test");

        logger.LogInformation("first");
        logger.LogInformation("second");

        panel.Refresh();

        Assert.Equal("second", panel.Log[0].Message);
    }

    [Fact]
    public void AnExceptionIsShownBesideItsMessage()
    {
        var panel = Panel();

        MediaDiagnostics.CreateLogger("HaCue2.Test")
            .LogError(new InvalidOperationException("device is gone"), "could not open the line");

        panel.Refresh();

        // Both halves: the message says what was being attempted and the exception says why it failed,
        // and a tail showing only one of them sends the reader to the file for the other.
        Assert.Contains("could not open the line", panel.Log[0].Message, StringComparison.Ordinal);
        Assert.Contains("device is gone", panel.Log[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LevelsCarryTheirOwnGel()
    {
        var panel = Panel();
        var logger = MediaDiagnostics.CreateLogger("HaCue2.Test");

        logger.LogError("bad");
        logger.LogWarning("iffy");
        panel.MinimumLevel = "Trace";
        panel.Refresh();

        Assert.True(panel.Log.Single(line => line.Level == "ERROR").IsError);
        Assert.True(panel.Log.Single(line => line.Level == "WARN").IsWarn);
    }

    [Fact]
    public void ResettingCountersClearsTheTail()
    {
        var panel = Panel();
        MediaDiagnostics.CreateLogger("HaCue2.Test").LogInformation("something");
        panel.Refresh();
        Assert.NotEmpty(panel.Log);

        panel.ResetCounters();

        Assert.Empty(panel.Log);
    }

    [Fact]
    public void TheSummaryReportsWhatIsShown()
    {
        var panel = Panel();
        MediaDiagnostics.CreateLogger("HaCue2.Test").LogInformation("one");
        panel.Refresh();

        Assert.Contains("1 line", panel.LogSummary, StringComparison.Ordinal);
        Assert.Contains("Information", panel.LogSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallingIsIdempotent() =>
        // The composition root calls it once, but a second call must hand back the same pipeline
        // rather than replacing the framework's factory out from under a running session.
        Assert.Same(Logging, AppLogging.Install(new AppSettings()));
}
