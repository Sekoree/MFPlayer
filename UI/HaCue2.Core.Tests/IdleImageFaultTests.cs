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

    [Fact]
    public async Task AnUnchangedBrokenIdleImageIsReportedOnceNotOncePerReload()
    {
        var fixture = new TestProject();
        var garbage = Path.Combine(
            Path.GetTempPath(), $"hacue2-persistent-corrupt-idle-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(garbage, "永 not an image");

        try
        {
            fixture.Cyc.IdleImagePath = garbage;
            await using var host = await ShowHost.StartAsync(
                fixture.Project, backend: null, headless: true);

            int IdleProblems() => host.Problems.Count(problem =>
                problem.Contains("idle image", StringComparison.OrdinalIgnoreCase));

            var reported = IdleProblems();
            Assert.Equal(1, reported);

            // Edits reload the document debounced at ~300 ms; an operator typing a label produces a
            // stream of these. A broken slate whose file has not changed must not re-decode and
            // re-report on each one - its failed signature holds until the file itself changes.
            await host.ReloadAsync(fixture.Project);
            await host.ReloadAsync(fixture.Project);

            Assert.Equal(reported, IdleProblems());
            Assert.Equal(0, host.CachedIdleFrameCount);
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    [Fact]
    public async Task AFailedIdleDecodeIsRetriedWhenTheFileAtTheSamePathIsRepaired()
    {
        var fixture = new TestProject();
        var image = Path.Combine(Path.GetTempPath(), $"hacue2-repaired-idle-{Guid.NewGuid():N}.ppm");
        await File.WriteAllTextAsync(image, "not an image");

        try
        {
            fixture.Cyc.IdleImagePath = image;
            await using var host = await ShowHost.StartAsync(
                fixture.Project, backend: null, headless: true);

            Assert.Equal(0, host.CachedIdleFrameCount);

            // A tiny binary PPM is understood by FFmpeg and deliberately differs in size/stamp from
            // the corrupt placeholder. The authored path itself does not change.
            await File.WriteAllBytesAsync(image,
            [
                (byte)'P', (byte)'6', (byte)'\n',
                (byte)'2', (byte)' ', (byte)'1', (byte)'\n',
                (byte)'2', (byte)'5', (byte)'5', (byte)'\n',
                255, 0, 0, 0, 255, 0,
            ]);
            File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddSeconds(1));

            await host.ReloadAsync(fixture.Project);

            Assert.Equal(1, host.CachedIdleFrameCount);
            _ = await host.SnapshotAsync();
        }
        finally
        {
            File.Delete(image);
        }
    }
}
