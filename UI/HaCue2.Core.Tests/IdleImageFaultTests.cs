using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// A slate that will not decode must cost one reported line, never the reload - or the start.
/// </summary>
/// <remarks>
/// The decode path throws <c>FFmpegException</c> for any file FFmpeg rejects, which the idle-frame
/// catch filter's original named list did not cover: a corrupt image aborted the document reload
/// halfway mid-show, and at start-up it stopped the engine from starting at all while leaking the
/// already-opened bay, session and screens (nothing disposed the half-built host).
/// </remarks>
public sealed class IdleImageFaultTests
{
    [Fact]
    public async Task ACorruptCompositionIdleImageIsReportedAndTheEngineStillStarts()
    {
        var fixture = new TestProject();
        var garbage = Path.Combine(
            Path.GetTempPath(), $"hacue2-corrupt-idle-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(garbage, "this is not an image at all");

        try
        {
            fixture.Cyc.IdleImagePath = garbage;

            await using var host = await ShowHost.StartAsync(
                fixture.Project, backend: null, headless: true);

            Assert.Contains(host.Problems, problem =>
                problem.Contains("idle image", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    [Fact]
    public async Task ACorruptIdleImageArrivingByEditDoesNotAbortTheReload()
    {
        var fixture = new TestProject();
        var garbage = Path.Combine(
            Path.GetTempPath(), $"hacue2-corrupt-idle-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(garbage, "still not an image");

        try
        {
            await using var host = await ShowHost.StartAsync(
                fixture.Project, backend: null, headless: true);

            fixture.Cyc.IdleImagePath = garbage;
            await host.ReloadAsync(fixture.Project);

            // The reload survived: the problem is reported by name and the host still answers.
            Assert.Contains(host.Problems, problem =>
                problem.Contains("idle image", StringComparison.OrdinalIgnoreCase));
            _ = await host.SnapshotAsync();
        }
        finally
        {
            File.Delete(garbage);
        }
    }
}
