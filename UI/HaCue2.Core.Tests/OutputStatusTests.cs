using System.Net;
using System.Net.Sockets;
using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The two engine-side facts the Video and Targets screens read: which outputs are not showing
/// anything, and what each action endpoint was last actually sent.
/// </summary>
/// <remarks>
/// Both used to be invented, and both are the kind of value where a plausible wrong answer costs more
/// than a blank one — an output that reads "live" over a window that never opened, or a "last seen"
/// stamp over a console the app has never successfully reached.
/// </remarks>
public class OutputStatusTests
{
    // ── which outputs opened ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOutputThatNamesNoCompositionIsUnopenedAndSaysWhy()
    {
        var output = new VideoOutputDefinition { Name = "Projector A", CompositionId = null };
        var project = new HaCueProject { VideoOutputs = [output] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        // Both halves, because they answer different questions: the sentence goes to the Problems
        // list an operator reads, the id goes to the row that has to render red.
        Assert.Contains(output.Id, outputs.Unopened);
        Assert.Contains(outputs.Failures, failure => failure.Contains("shows no composition"));
    }

    [Fact]
    public void AnOutputPointingAtACompositionThatIsNotInTheShowIsAlsoUnopened()
    {
        var output = new VideoOutputDefinition { Name = "Lobby TV", CompositionId = Guid.NewGuid() };
        var project = new HaCueProject { VideoOutputs = [output] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        // A dangling id, which is what deleting a composition leaves behind. It must not read as an
        // output that is fine.
        Assert.Contains(output.Id, outputs.Unopened);
    }

    [Fact]
    public void AScreenOnAMachineWithNoDisplayIsUnopened()
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var output = new VideoOutputDefinition
        {
            Name = "Projector A",
            Kind = VideoOutputKind.LocalScreen,
            CompositionId = composition.Id,
        };

        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        Assert.Contains(output.Id, outputs.Unopened);
    }

    [Fact]
    public void ARecorderOpensWithNoDisplayAndIsNotReportedAbsent()
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var output = new VideoOutputDefinition
        {
            Name = "Archive",
            Kind = VideoOutputKind.Record,
            CompositionId = composition.Id,
        };

        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        // A recording is not a window. A booth box with the projector unplugged is exactly where an
        // unattended capture belongs, so marking it absent would take the feature away where it is
        // most used.
        Assert.DoesNotContain(output.Id, outputs.Unopened);
        Assert.Empty(outputs.Failures);
    }

    // ── what was sent ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusedSendLeavesNoTrace()
    {
        using var sender = new ActionSender();

        var endpoint = new ActionEndpoint
        {
            Name = "Desk", Kind = EndpointKind.MidiOut, Host = "127.0.0.1", Port = 9_999,
        };

        var refusal = await sender.SendAsync(
            new ActionCueNode { Label = "Go desk", Address = "/eos/cue/7.2" }, endpoint);

        Assert.NotNull(refusal);
        // Nothing reached the desk, so nothing may appear in the column an operator checks to find out
        // whether anything did.
        Assert.Empty(sender.LastSent);
    }

    [Fact]
    public async Task AnUnreachableEndpointLeavesNoTrace()
    {
        using var sender = new ActionSender();

        var endpoint = new ActionEndpoint
        {
            Name = "Desk", Host = "not-an-address", Port = 9_999,
        };

        Assert.NotNull(await sender.SendAsync(
            new ActionCueNode { Label = "Go desk", Address = "/eos/cue/7.2" }, endpoint));

        Assert.Empty(sender.LastSent);
    }

    /// <summary>
    /// A loopback endpoint with something actually bound to it.
    /// </summary>
    /// <remarks>
    /// The receiver is not optional. <c>OSCClient</c> CONNECTS its UDP socket, so a datagram sent to a
    /// closed port comes back as an ICMP port-unreachable and is raised as a <c>SocketException</c> on
    /// the NEXT send — which is correct behaviour and is exactly what a test that sends twice into the
    /// void would trip over, having proved nothing about this class.
    /// </remarks>
    private static (ActionEndpoint Endpoint, UdpClient Receiver) Loopback()
    {
        var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        return (new ActionEndpoint { Name = "Desk", Host = "127.0.0.1", Port = port }, receiver);
    }

    [Fact]
    public async Task ASentMessageIsRecordedAgainstItsEndpointByAddress()
    {
        using var sender = new ActionSender();
        var (endpoint, receiver) = Loopback();
        using var _ = receiver;

        Assert.Null(await sender.SendAsync(
            new ActionCueNode { Label = "Go desk", Address = "/eos/cue/7.2" }, endpoint));

        // Sent, not understood — the column only ever promises the first, which is why it holds the
        // address rather than anything about the desk's reply.
        var sent = Assert.Contains(endpoint.Id, sender.LastSent);
        Assert.StartsWith("/eos/cue/7.2 · ", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLatestSendWinsAndTheColumnHoldsOneRowPerEndpoint()
    {
        using var sender = new ActionSender();
        var (endpoint, receiver) = Loopback();
        using var _ = receiver;

        Assert.Null(await sender.SendAsync(new ActionCueNode { Address = "/eos/cue/7.2" }, endpoint));
        Assert.Null(await sender.SendAsync(new ActionCueNode { Address = "/eos/cue/8" }, endpoint));

        // Two cues sending to one desk is a normal show. The column reports the endpoint's state, not
        // a history, so the second must replace the first rather than accumulate.
        Assert.StartsWith(
            "/eos/cue/8 · ",
            Assert.Contains(endpoint.Id, sender.LastSent),
            StringComparison.Ordinal);
    }

    // ── outputs added or removed while the show runs ──────────────────────────────────────────

    [Fact]
    public void AnOutputAddedAfterTheShowStartedIsOpenedOnTheNextReload()
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var project = new HaCueProject { Compositions = [composition] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);
        Assert.Empty(outputs.Unopened);

        var added = new VideoOutputDefinition
        {
            Name = "Archive",
            Kind = VideoOutputKind.Record,
            CompositionId = composition.Id,
        };

        project.VideoOutputs.Add(added);
        outputs.Sync(project);

        // Opening only at start-up meant a screen added mid-session stayed dark with nothing saying
        // why — and an operator adding a projector during a get-in had no way to find that out.
        Assert.DoesNotContain(added.Id, outputs.Unopened);
        Assert.Contains(outputs.Recorders, entry => entry.Key == added.Id);
    }

    [Fact]
    public void AnOutputRemovedFromTheDocumentStopsBeingOpen()
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var output = new VideoOutputDefinition
        {
            Name = "Archive",
            Kind = VideoOutputKind.Record,
            CompositionId = composition.Id,
        };

        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);
        Assert.Contains(outputs.Recorders, entry => entry.Key == output.Id);

        project.VideoOutputs.Clear();
        outputs.Sync(project);

        // A window left on a screen with nothing in the show pointing at it is worse than one that
        // never opened: it looks like part of the show.
        Assert.Empty(outputs.Recorders);
        Assert.Empty(outputs.Unopened);
    }

    [Fact]
    public void ASyncThatChangesNothingLeavesTheOpenOutputsAlone()
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var output = new VideoOutputDefinition
        {
            Name = "Archive",
            Kind = VideoOutputKind.Record,
            CompositionId = composition.Id,
        };

        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);
        var before = outputs.Recorders[output.Id];

        // Every edit reloads. Re-opening an output because an unrelated cue was renamed would close and
        // re-create the operator's projector window on a keystroke.
        Assert.Empty(outputs.Sync(project));
        Assert.Same(before, outputs.Recorders[output.Id]);
    }
}

