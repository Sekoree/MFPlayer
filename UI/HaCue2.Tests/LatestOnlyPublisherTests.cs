using System.Collections.Concurrent;
using HaCue2.Machine;
using HaCue2.Session;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HaCue2.Tests;

public sealed class LatestOnlyPublisherTests
{
    [Fact]
    public async Task PointerBurstKeepsOnlyTheNewestValueBehindAnInflightUpdate()
    {
        var received = new ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new LatestOnlyPublisher<string, int>(
            async (_, value) =>
            {
                received.Enqueue(value);
                if (value == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                if (value == 100)
                    latestArrived.TrySetResult();
            },
            TimeSpan.FromMilliseconds(1));

        publisher.Offer("projector", 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var value = 2; value <= 100; value++)
            publisher.Offer("projector", value);
        releaseFirst.TrySetResult();
        await latestArrived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 100], received);
    }

    [Fact]
    public async Task FileLogIsReadableBeforeGracefulShutdown()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-live-log").FullName;
        var provider = new FileLogProvider(new AppSettings
        {
            LogDirectory = directory,
            FileLogLevel = "Debug",
        });

        try
        {
            provider.CreateLogger("HaCue2.Test").LogError(
                new InvalidOperationException("mapping failed"), "live update crashed");

            string? text = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var path = Directory.EnumerateFiles(directory, "hacue2-*.log").SingleOrDefault();
                if (path is not null && new FileInfo(path).Length > 0)
                {
                    // FileShare.ReadWrite, not File.ReadAllTextAsync. The provider is still holding this file
                    // open for writing — FileAccess.Write / FileShare.Read, deliberately, so that a reader may
                    // come in — but ReadAllTextAsync declares only FileShare.Read, and on Windows a share mode
                    // must also permit the access the EXISTING handle holds. FileShare.Read does not permit
                    // Write, so the open was refused: "the process cannot access the file".
                    //
                    // Linux has no mandatory locking, so the plain read passed there and this failed only on
                    // the win-x64 leg — while the one thing the test exists to prove is that the log can be
                    // read BEFORE shutdown, i.e. exactly while it is still being written.
                    await using var live = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(live);
                    text = await reader.ReadToEndAsync();
                    break;
                }

                await Task.Delay(10);
            }

            Assert.NotNull(text);
            Assert.Contains("live update crashed", text, StringComparison.Ordinal);
            Assert.Contains("mapping failed", text, StringComparison.Ordinal);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
