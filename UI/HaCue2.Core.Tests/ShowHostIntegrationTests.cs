using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Core.Video;
using S.Media.Present.SDL3;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class ShowHostIntegrationTests
{
    [Fact]
    public async Task ProductionRootSelectsTheBestAvailableDesktopCompositor()
    {
        var fixture = new TestProject();
        var project = fixture.Project;
        project.Title = "production-root-test";
        fixture.Cyc.Width = 320;
        fixture.Cyc.Height = 180;
        fixture.Cyc.FramesPerSecond = 25;

        await using var host = await ShowHost.StartAsync(project, backend: null, headless: true);

        var stats = Assert.Single(host.CompositionStats());
        var requested = Environment.GetEnvironmentVariable("HACUE2_COMPOSITOR")
                        ?? Environment.GetEnvironmentVariable("S_MEDIA_COMPOSITOR");
        var expected = string.Equals(requested, "cpu", StringComparison.OrdinalIgnoreCase)
            ? "CPU"
            : SDL3GLVideoCompositor.TryProbe(PixelFormat.Bgra32, out _)
                ? "OpenGL"
                : "CPU";
        Assert.Equal(expected, stats.CompositorBackend);
    }

    [Fact]
    public void ProductionRootProvidesTheUnifiedSubtitleFactory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hacue2-subtitle-{Guid.NewGuid():N}.srt");
        try
        {
            File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:02,000\nHaCue2 subtitle\n");

            using var overlay = ShowSessionWiring.CreateSubtitleOverlay(path, -1, 320, 180);

            Assert.NotNull(overlay);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExplicitCpuFactoryReportsItsMissingGpuCapabilitiesHonestly()
    {
        var selected = SDL3CompositionCompositorFactory.Create(
            new VideoFormat(320, 180, PixelFormat.Bgra32, new Rational(25, 1)),
            requestedBackend: "cpu",
            ownerName: "test");
        using (selected.Compositor)
        {
            Assert.Equal("CPU", selected.BackendName);
            Assert.True(selected.RequiresBgraLayerConversion);
            Assert.False(selected.SupportsWarpMesh);
            Assert.False(selected.SupportsSurfaceLayers);
        }
    }

    [Fact]
    public async Task ExternalGoUsesTheActiveCueListRatherThanTheFirstList()
    {
        var fixture = new TestProject();
        fixture.List.StandbyCueId = fixture.Track.Id;
        var marker = new JumpCueNode { Number = "20", Label = "Second-list jump" };
        var second = new CueList { Name = "Act 2", Cues = [marker], StandbyCueId = marker.Id };
        fixture.Project.CueLists.Add(second);

        await using var host = await ShowHost.StartAsync(fixture.Project, backend: null, headless: true);
        host.SetActiveCueList(second.Id);

        await host.ApplyExternalTriggerAsync(new TriggerAction(
            TriggerTarget.Transport, null, "go", null, "test → go"));

        var state = await host.SnapshotAsync();
        Assert.Equal(fixture.Track.Id, state.Standby[fixture.List.Id]);
        Assert.Contains(state.Problems, problem => problem.Contains("Second-list jump", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Problems, problem => problem.Contains("Preshow bed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutomationCueUsesTheShowPauseSeekAndStopTransport()
    {
        var automation = new AutomationCueNode
        {
            Number = "1",
            Label = "Long controller run",
            DurationMs = 10_000,
        };
        var project = new TestProject().Project;
        project.CueLists = [new CueList { Name = "Act", Cues = [automation] }];

        await using var host = await ShowHost.StartAsync(project, backend: null, headless: true);

        Assert.True(await host.FireAsync(automation.Id));
        Assert.Contains(automation.Id, (await host.SnapshotAsync()).Sounding);

        await host.SetPausedAsync(true);
        var pausedAt = (await host.SnapshotAsync()).Active.Single().Elapsed;
        await Task.Delay(100);
        var stillPausedAt = (await host.SnapshotAsync()).Active.Single().Elapsed;
        Assert.InRange(Math.Abs((stillPausedAt - pausedAt).TotalMilliseconds), 0, 5);

        Assert.Null(await host.SeekCueAsync(automation.Id, TimeSpan.FromSeconds(4)));
        var sought = (await host.SnapshotAsync()).Active.Single().Elapsed;
        Assert.InRange(sought.TotalMilliseconds, 3_995, 4_005);

        await host.SetPausedAsync(false);
        await Task.Delay(75);
        Assert.True((await host.SnapshotAsync()).Active.Single().Elapsed > sought);

        await host.StopCueAsync(automation.Id, TimeSpan.Zero);
        Assert.DoesNotContain(automation.Id, (await host.SnapshotAsync()).Sounding);
    }
}
