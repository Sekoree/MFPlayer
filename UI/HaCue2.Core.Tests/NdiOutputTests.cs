using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// NDI outputs open rather than being reported as unimplemented.
/// </summary>
/// <remarks>
/// The sender exposes an <c>IVideoOutput</c> and an <c>IAudioOutput</c>, so it attaches exactly like a
/// screen or an interface — which is what made this the obvious one to do after recording. These open a
/// real sender against the machine's NDI runtime; there is no way to prove the wiring works without one.
/// </remarks>
public class NdiOutputTests
{
    private static (HaCueProject Project, VideoOutputDefinition Output) Show(string hint = "")
    {
        var composition = new CompositionDefinition { Name = "Main", Width = 320, Height = 240 };
        var output = new VideoOutputDefinition
        {
            Name = "NDI Prog",
            Kind = VideoOutputKind.Ndi,
            CompositionId = composition.Id,
            TargetHint = hint,
        };

        return (new HaCueProject { Compositions = [composition], VideoOutputs = [output] }, output);
    }

    [NdiRuntimeFact]
    public void AnNdiOutputOpensEvenHeadless()
    {
        var (project, output) = Show("HACUE2-TEST-PROG");

        // headless: true — an NDI feed is not a window, and a booth box with no display is exactly
        // where an unattended NDI send belongs.
        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        Assert.DoesNotContain(outputs.Failures, failure => failure.Contains("not implemented", StringComparison.Ordinal));
        Assert.Contains(outputs.Open, open => open.Id == output.Id);
    }

    [NdiRuntimeFact]
    public void ItSendsTheCompositionItWasPointedAt()
    {
        var (project, output) = Show("HACUE2-TEST-SHOWS");

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        var open = Assert.Single(outputs.Open, item => item.Id == output.Id);
        Assert.Equal(project.Compositions[0].Id, open.CompositionId);
    }

    [Fact]
    public void AnOutputShowingNoCompositionIsStillRefused()
    {
        var (project, _) = Show();
        project.VideoOutputs[0].CompositionId = null;

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        // The kind being implemented does not excuse an output that points at nothing.
        Assert.Empty(outputs.Open);
        Assert.Single(outputs.Failures);
    }

    [NdiRuntimeFact]
    public void TheSourceNameFallsBackToTheOutputsName()
    {
        // A hint is optional; an NDI source without a name is not a thing that can exist, so the
        // output's own name is used rather than the send failing.
        var (project, output) = Show();

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        Assert.Contains(outputs.Open, open => open.Id == output.Id);
    }
}
