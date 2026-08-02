using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class ReferenceTests
{
    /// <summary>
    /// The gap this query closes: today a deleted cue silently orphans every jump and fade pointing at
    /// it, and nothing tells the operator before they press delete.
    /// </summary>
    [Fact]
    public void EveryJumpAndFadeTargetingACueIsFound()
    {
        var fixture = new TestProject();

        var references = ProjectReferences.To(fixture.Project, ProjectReferences.Cue, fixture.Track.Id);

        Assert.Equal(2, references.Count);
        Assert.Contains(references, r => r.Description.Contains("jumps to it"));
        Assert.Contains(references, r => r.Description.Contains("fades it"));
        Assert.All(references, r => Assert.Equal(ProjectReferences.Cue, r.SubjectKind));
    }

    [Fact]
    public void StandbyAndTriggerBindingsCountAsReferencesToACue()
    {
        var fixture = new TestProject();
        fixture.List.StandbyCueId = fixture.Track.Id;
        fixture.Project.TriggerInputs.Add(new TriggerInputDefinition
        {
            Name = "APC mini",
            Bindings = [new TriggerBinding { Input = "note 3 ch 1", TargetCueId = fixture.Track.Id }],
        });

        var references = ProjectReferences.To(fixture.Project, ProjectReferences.Cue, fixture.Track.Id);

        Assert.Contains(references, r => r.Description.Contains("has standby on it"));
        Assert.Contains(references, r => r.Description.Contains("fires it from note 3"));
    }

    [Fact]
    public void EverythingTargetingALogicalOutputIsFound()
    {
        var fixture = new TestProject();
        var patchCue = new PatchCueNode
        {
            Number = 4,
            Label = "Foldback up",
            Levels = [new PatchLevelChange { LogicalChannelId = fixture.FoldL.Id, GainDb = 0 }],
        };
        fixture.List.Cues.Add(patchCue);
        fixture.Fade.TargetChannelIds.Add(fixture.FoldL.Id);

        var references = ProjectReferences.To(
            fixture.Project, ProjectReferences.LogicalOutput, fixture.FoldL.Id);

        Assert.Contains(references, r => r.Description.Contains("changes its level"));
        Assert.Contains(references, r => r.Description.Contains("fades it"));
        Assert.Contains(references, r => r.Description.Contains("snapshot “Act 1”"));
        Assert.Contains(references, r => r.Description.Contains("the patch routes it to 1 device channel"));
    }

    [Fact]
    public void GroupMembershipCountsAsAReference()
    {
        var fixture = new TestProject();

        var references = ProjectReferences.To(
            fixture.Project, ProjectReferences.LogicalOutput, fixture.MainL.Id);

        Assert.Contains(references, r => r.Description.Contains("output group “Main”"));
    }

    /// <summary>The "fed by 6 cues" column on screen 06 counts CUES, not sends.</summary>
    [Fact]
    public void FeedingCuesAreCountedOncePerCue()
    {
        var fixture = new TestProject();
        // A second send from the same cue into the same output — a mono-from-both-channels patch.
        fixture.Track.Sends.Add(new CueAudioSend { SourceChannel = 1, LogicalChannelId = fixture.MainL.Id });

        Assert.Equal(1, ProjectReferences.CuesFeeding(fixture.Project, fixture.MainL.Id));
    }

    [Fact]
    public void PatchCuesRecallingASnapshotAreFound()
    {
        var fixture = new TestProject();
        fixture.List.Cues.Add(new PatchCueNode
        {
            Number = 4, Label = "Act 1 patch", SnapshotId = fixture.Snapshot.Id,
        });

        var references = ProjectReferences.To(
            fixture.Project, ProjectReferences.Snapshot, fixture.Snapshot.Id);

        Assert.Single(references);
        Assert.Contains("recalls it", references[0].Description);
    }

    [Fact]
    public void PlacementsAndVideoOutputsBothReferenceAComposition()
    {
        var fixture = new TestProject();
        fixture.Track.Placement = new LayerPlacement { CompositionId = fixture.Cyc.Id };
        fixture.Project.VideoOutputs.Add(new VideoOutputDefinition
        {
            Name = "Projector A", CompositionId = fixture.Cyc.Id,
        });

        var references = ProjectReferences.To(
            fixture.Project, ProjectReferences.Composition, fixture.Cyc.Id);

        Assert.Contains(references, r => r.Description.Contains("is placed on it"));
        Assert.Contains(references, r => r.Description.Contains("“Projector A” shows it"));
    }

    [Fact]
    public void OutboundLanesReferenceTheirEndpoint()
    {
        var fixture = new TestProject();
        var endpoint = new ActionEndpoint { Name = "Eos" };
        fixture.Project.ActionEndpoints.Add(endpoint);
        fixture.Track.EffectLanes.Add(new EffectLane
        {
            Kind = EffectLaneKind.OscRamp, EndpointId = endpoint.Id, Address = "/eos/chan/1",
        });

        var references = ProjectReferences.To(
            fixture.Project, ProjectReferences.Endpoint, endpoint.Id);

        Assert.Contains(references, r => r.Description.Contains("ramps a value on it"));
    }

    [Fact]
    public void NothingReferencesSomethingNobodyUses()
    {
        var fixture = new TestProject();
        var spare = new LogicalAudioChannel { Name = "Spare" };
        fixture.Project.AudioPatch.LogicalChannels.Add(spare);

        Assert.Empty(ProjectReferences.To(fixture.Project, ProjectReferences.LogicalOutput, spare.Id));
    }
}
