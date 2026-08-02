using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Diagnostics;

/// <summary>
/// The in-memory log tail. The framework already funnels every category through one
/// <c>Microsoft.Extensions.Logging</c> factory, so a diagnostics view needs a sink to subscribe to - not
/// a second logging system.
/// </summary>
public class LogRingProviderTests
{
    private static ILogger Logger(LogRingProvider provider, string category = "Test.Category") =>
        provider.CreateLogger(category);

    [Fact]
    public void CapturesLevelCategoryMessageAndException_Separately()
    {
        using var provider = new LogRingProvider();
        var boom = new InvalidOperationException("boom");

        Logger(provider, "S.Media.Routing").LogWarning(boom, "pump pressure on {Line}", "Record");

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("S.Media.Routing", entry.Category);
        Assert.Same(boom, entry.Exception);
        // The fields stay separate so a view can column and filter on them; a pre-formatted line could not.
        Assert.Contains("Record", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersBelowMinimumLevel()
    {
        using var provider = new LogRingProvider(minimumLevel: LogLevel.Warning);
        var log = Logger(provider);

        log.LogDebug("noise");
        log.LogInformation("still noise");
        log.LogWarning("kept");
        log.LogError("kept too");

        Assert.Equal(2, provider.Snapshot().Count);
    }

    [Fact]
    public void MinimumLevel_IsChangeableAtRuntime()
    {
        using var provider = new LogRingProvider(minimumLevel: LogLevel.Warning);
        var log = Logger(provider);

        log.LogInformation("dropped");
        // The whole point of a sink-side level: the host's file provider bakes its level in at logger
        // creation, so a UI picker could not move it. This one can.
        provider.MinimumLevel = LogLevel.Debug;
        log.LogInformation("kept");
        provider.MinimumLevel = LogLevel.Error;
        log.LogWarning("dropped again");

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("kept", entry.Message);
    }

    [Fact]
    public void IsEnabled_TracksTheCurrentLevel_SoCallersCanSkipFormatting()
    {
        using var provider = new LogRingProvider(minimumLevel: LogLevel.Warning);
        var log = Logger(provider);

        Assert.False(log.IsEnabled(LogLevel.Information));
        Assert.True(log.IsEnabled(LogLevel.Error));

        provider.MinimumLevel = LogLevel.Trace;
        Assert.True(log.IsEnabled(LogLevel.Information));

        // None means "log nothing" and must never be enabled, whatever the minimum is.
        provider.MinimumLevel = LogLevel.Trace;
        Assert.False(log.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void KeepsTheMostRecentRecords_InOrder_AndCountsWhatItDropped()
    {
        using var provider = new LogRingProvider(capacity: 3, minimumLevel: LogLevel.Trace);
        var log = Logger(provider);

        for (var i = 1; i <= 5; i++)
            log.LogInformation("{Index}", i);

        var snapshot = provider.Snapshot();
        Assert.Equal(3, snapshot.Count);
        // Oldest first, and the two overwritten records are reported rather than silently missing - a
        // tail that hides a gap is worse than one that admits it.
        Assert.Equal("3", snapshot[0].Message);
        Assert.Equal("5", snapshot[2].Message);
        Assert.Equal(2, provider.DroppedCount);
    }

    [Fact]
    public void Snapshot_IsNonDestructive()
    {
        using var provider = new LogRingProvider(minimumLevel: LogLevel.Trace);
        Logger(provider).LogInformation("once");

        Assert.Single(provider.Snapshot());
        Assert.Single(provider.Snapshot());
    }

    [Fact]
    public void EntryCaptured_LetsAViewRefreshWithoutPolling()
    {
        using var provider = new LogRingProvider(minimumLevel: LogLevel.Trace);
        var seen = new List<string>();
        provider.EntryCaptured += e => seen.Add(e.Message);

        Logger(provider).LogInformation("live");

        Assert.Equal(["live"], seen);
    }

    [Fact]
    public void ThrowingEntrySubscriber_DoesNotBreakLoggingOrLaterSubscribers()
    {
        using var provider = new LogRingProvider(minimumLevel: LogLevel.Trace);
        var seen = new List<string>();
        provider.EntryCaptured += _ => throw new InvalidOperationException("broken view");
        provider.EntryCaptured += entry => seen.Add(entry.Message);

        var error = Record.Exception(() => Logger(provider).LogInformation("still captured"));

        Assert.Null(error);
        Assert.Equal(["still captured"], seen);
        Assert.Equal("still captured", Assert.Single(provider.Snapshot()).Message);
    }

    [Fact]
    public void Clear_EmptiesTheRingAndTheDroppedCount()
    {
        using var provider = new LogRingProvider(capacity: 2, minimumLevel: LogLevel.Trace);
        var log = Logger(provider);
        for (var i = 0; i < 5; i++)
            log.LogInformation("{Index}", i);
        Assert.NotEqual(0, provider.DroppedCount);

        provider.Clear();

        Assert.Empty(provider.Snapshot());
        Assert.Equal(0, provider.DroppedCount);
    }

    [Fact]
    public void ConcurrentWriters_DoNotCorruptTheRing()
    {
        using var provider = new LogRingProvider(capacity: 64, minimumLevel: LogLevel.Trace);
        var log = Logger(provider);

        // Logging happens from pump and audio threads as well as the UI, so writes must be safe.
        Parallel.For(0, 500, i => log.LogInformation("{Index}", i));

        var snapshot = provider.Snapshot();
        Assert.Equal(64, snapshot.Count);
        Assert.All(snapshot, e => Assert.False(string.IsNullOrEmpty(e.Message)));
        Assert.Equal(500 - 64, provider.DroppedCount);
    }
}
