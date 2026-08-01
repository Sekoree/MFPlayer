using System.Globalization;
using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// "Copy report": the fastest path from "it glitched in the second half" to something a person can paste
/// into an issue afterwards.
/// </summary>
public class AudioPatchBayReportTests
{
    private const int Rate = 48_000;

    private static float[,] Identity() => new float[,] { { 1f, 0f }, { 0f, 1f } };

    private static AudioPatchBay Bay()
    {
        var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("18i20", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);
        return bay;
    }

    [Fact]
    public void IncludesTheHeader_AndTheBayTopology()
    {
        using var bay = Bay();

        var report = AudioPatchBayReport.Render(bay.SnapshotDiagnostics(), header: "midsummer-2026");

        Assert.Contains("midsummer-2026", report, StringComparison.Ordinal);
        Assert.Contains("48000 Hz mix", report, StringComparison.Ordinal);
        Assert.Contains("2 logical channels", report, StringComparison.Ordinal);
        Assert.Contains("clock master 18i20", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsTerminalsWithTheirCounters()
    {
        using var bay = Bay();

        var report = AudioPatchBayReport.Render(bay.SnapshotDiagnostics());

        Assert.Contains("TERMINALS", report, StringComparison.Ordinal);
        Assert.Contains("18i20", report, StringComparison.Ordinal);
        Assert.Contains(nameof(TerminalState.AdvancingMaster), report, StringComparison.Ordinal);
        Assert.Contains("enqueued", report, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesEachLease_SoAStarvingOneIsIdentifiable()
    {
        using var bay = Bay();
        using var voice = bay.AcquireProducer(1, [1f, 1f], label: "Q13.1 Storm bed");

        var report = AudioPatchBayReport.Render(bay.SnapshotDiagnostics());

        Assert.Contains("PRODUCER LEASES", report, StringComparison.Ordinal);
        Assert.Contains("Q13.1 Storm bed", report, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinguishesMeteringDisabled_FromSilence()
    {
        using var bay = Bay();

        var off = AudioPatchBayReport.Render(bay.SnapshotDiagnostics());
        // A table of dashes reads the same either way, and the difference decides whether a silent show
        // was actually silent.
        Assert.Contains("(metering disabled)", off, StringComparison.Ordinal);

        bay.EnableProgramMetering();
        var on = AudioPatchBayReport.Render(bay.SnapshotDiagnostics());
        Assert.DoesNotContain("(metering disabled)", on, StringComparison.Ordinal);
        Assert.Contains("peak", on, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAnEmptyBay_WithoutThrowing()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);

        var report = AudioPatchBayReport.Render(bay.SnapshotDiagnostics());

        Assert.Contains("(none attached)", report, StringComparison.Ordinal);
        Assert.Contains("(none sounding)", report, StringComparison.Ordinal);
        Assert.Contains("(none - wall-clock fallback)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatsInvariantly_SoACommaDecimalMachineReadsTheSame()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            using var bay = Bay();
            bay.EnableProgramMetering();
            using var voice = bay.AcquireProducer(1, [1f, 1f], label: "lease");

            var report = AudioPatchBayReport.Render(bay.SnapshotDiagnostics());

            // A report pasted from a comma-decimal machine must not read as a different number.
            Assert.Contains(".0 ms", report, StringComparison.Ordinal);
            Assert.DoesNotContain(",0 ms", report, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
