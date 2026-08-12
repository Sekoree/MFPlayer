using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Authoring a property-targeted automation track (register item 18).
/// </summary>
/// <remarks>
/// These tests pin the schema-2 authoring surface: native property values, absolute cue time and
/// concrete targets. Schema-1 effect lanes are reader-only migration input.
/// </remarks>
public class EffectLaneTests
{
    private const int Volume = 0;
    private const int Opacity = 1;
    private const int Osc = 2;
    private const int PositionX = 4;
    private const int Rotation = 8;
    private const int ChromaSimilarity = 9;
    private const int ColorContrast = 13;

    private static MediaCueNode SelectBed(ShellViewModel shell)
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);
        return bed;
    }

    [Fact]
    public Task ACueStartsWithNoLanes() => ShellFixture.WithShell(shell =>
    {
        SelectBed(shell);

        // Hidden until added: a cue showing four empty lanes would imply it has automation it does not.
        Assert.Empty(shell.Cues.Inspector.EffectLanes);
        Assert.False(shell.Cues.Inspector.HasEffectLanes);
    });

    [Fact]
    public Task AddingALaneReachesTheDocumentAndIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane(Volume);

        var lane = Assert.Single(bed.AutomationTracks);
        Assert.Equal(AutomationPropertyIds.CueVolume, lane.Target.PropertyId);

        shell.Undo();
        Assert.Empty(((MediaCueNode)shell.Project.FindCue(bed.Id)!).AutomationTracks);
    });

    [Fact]
    public Task ANewLaneOpensOnSomethingTheOperatorCanGrab() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane(Volume);

        // Two absolute-time keys at the authored dB value: the editor has handles immediately and
        // adding the track does not change playback until a key is moved.
        var lane = bed.AutomationTracks[0];
        Assert.Equal(2, lane.Keyframes.Count);
        Assert.Equal(0, lane.Keyframes[0].TimeMs);
        Assert.True(lane.Keyframes[1].TimeMs > 0);
        Assert.All(lane.Keyframes, key => Assert.Equal(bed.LevelDb, key.Value, 3));
    });

    [Fact]
    public Task OnlyOneLanePerKind() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane(Volume);
        shell.Cues.Inspector.AddLane(Volume);

        // A cue with two volume lanes has no defined level, and the compiler takes the first — so the
        // second would be invisible rather than additive.
        Assert.Single(bed.AutomationTracks);
        Assert.False(shell.Cues.Inspector.CanAddLane(Volume));
        Assert.True(shell.Cues.Inspector.CanAddLane(Osc));
    });

    [Fact]
    public Task DifferentKindsCoexist() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane(Volume);
        shell.Cues.Inspector.AddLane(Osc);

        Assert.Equal(2, bed.AutomationTracks.Count);
    });

    [Fact]
    public Task ALaneCanBeRemoved() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane(Volume);

        shell.Cues.Inspector.RemoveLane(0);

        Assert.Empty(bed.AutomationTracks);
    });

    [Fact]
    public Task TheLaneEditorTargetsTheLanesOwnPoints() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane(Volume);

        var editor = shell.Cues.Inspector.LaneEditor(0);
        Assert.NotNull(editor);

        // Drag the second key in the dedicated absolute-time editor. Canvas Y is projected through
        // the volume descriptor, so the document stores native dB rather than a generic factor.
        editor!.Apply(new HaCue2.Controls.CurveGesture(
            HaCue2.Controls.CurveGestureKind.Move, 1, 1, 0.75));
        editor.EndGesture();

        Assert.Equal(2, bed.AutomationTracks[0].Keyframes.Count);
        Assert.Equal(-42, bed.AutomationTracks[0].Keyframes[1].Value, 2);
    });

    [Fact]
    public Task EditingALaneIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane(Volume);

        var editor = shell.Cues.Inspector.LaneEditor(0)!;
        editor.Apply(new HaCue2.Controls.CurveGesture(
            HaCue2.Controls.CurveGestureKind.Move, 1, 1, 0.75));
        editor.EndGesture();

        shell.Undo();

        var lane = ((MediaCueNode)shell.Project.FindCue(bed.Id)!).AutomationTracks[0];
        Assert.Equal(bed.LevelDb, lane.Keyframes[1].Value, 3);
    });

    [Fact]
    public Task AOneKeyTrackIsAValidConstantValue() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane(Volume);
        bed.AutomationTracks[0].Keyframes = [new AutomationKeyframe { TimeMs = 0, Value = -12 }];
        shell.Cues.Inspector.Reload();

        Assert.Contains("1 keyframe", shell.Cues.Inspector.EffectLanes[0].Detail, StringComparison.Ordinal);
    });

    [Fact]
    public Task AnOutboundLaneWithNoEndpointSaysNothingIsSent() => ShellFixture.WithShell(shell =>
    {
        SelectBed(shell);
        shell.Cues.Inspector.AddLane(Osc);

        Assert.Contains(
            "no endpoint",
            shell.Cues.Inspector.EffectLanes[0].Detail,
            StringComparison.Ordinal);
    });

    [Fact]
    public Task AGroupCanCarryLanesAndACommentCannot() => ShellFixture.WithShell(shell =>
    {
        var group = shell.Project.AllCues().OfType<GroupCueNode>().First();
        ShellFixture.Select(shell.Cues, group.Id);
        Assert.True(shell.Cues.Inspector.CanCarryLanes);

        shell.Cues.Inspector.AddLane(Osc);
        Assert.Single(group.AutomationTracks);

        var comment = shell.Project.AllCues().OfType<CommentCueNode>().First();
        ShellFixture.Select(shell.Cues, comment.Id);

        // Nothing to automate on a marker. The button is disabled rather than adding a lane the
        // compiler would ignore.
        Assert.False(shell.Cues.Inspector.CanCarryLanes);
    });

    [Fact]
    public Task AnAutomationCueTargetsGroupModifierSlots() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var group = new GroupCueNode { Number = "900", Label = "Automated group" };
        var automation = new AutomationCueNode { Number = "901", Label = "Group controller" };
        list.Cues.AddRange([group, automation]);
        shell.Cues.Refresh();
        ShellFixture.Select(shell.Cues, automation.Id);

        shell.Cues.Inspector.AutomationTargetCueIndex = shell.Cues.Inspector.AutomationTargetCues
            .Select((label, index) => (label, index))
            .Single(item => item.label.Contains(group.Label, StringComparison.Ordinal)).index;

        Assert.Equal("Group audio trim", shell.Cues.Inspector.VolumeLaneLabel);
        Assert.Equal("Group video opacity", shell.Cues.Inspector.OpacityLaneLabel);
        Assert.True(shell.Cues.Inspector.CanAddVolumeLane);
        Assert.True(shell.Cues.Inspector.CanAddOpacityLane);

        shell.Cues.Inspector.AddLane(Volume);
        shell.Cues.Inspector.AddLane(Opacity);

        Assert.Collection(
            automation.AutomationTracks,
            track =>
            {
                Assert.Equal(group.Id, track.Target.CueId);
                Assert.Equal(AutomationPropertyIds.GroupAudioTrim, track.Target.PropertyId);
            },
            track =>
            {
                Assert.Equal(group.Id, track.Target.CueId);
                Assert.Equal(AutomationPropertyIds.GroupVideoOpacity, track.Target.PropertyId);
            });
    });

    [Fact]
    public Task PlacementTransformLanesAreOfferedAndKeepConcretePlacementIdentity() =>
        ShellFixture.WithShell(shell =>
        {
            var placement = new LayerPlacement
            {
                CompositionId = shell.Project.Compositions[0].Id,
                LayerIndex = 4,
                X = 0.25,
                RotationDegrees = 15,
            };
            var card = new TextCueNode
            {
                Number = "902",
                Label = "Moving card",
                DurationMs = 5_000,
                Placements = [placement],
            };
            shell.Project.CueLists[0].Cues.Add(card);
            shell.Cues.Refresh();
            ShellFixture.Select(shell.Cues, card.Id);

            Assert.True(shell.Cues.Inspector.CanAddPlacementXLane);
            Assert.True(shell.Cues.Inspector.CanAddPlacementRotationLane);
            shell.Cues.Inspector.AddLane(PositionX);
            shell.Cues.Inspector.AddLane(Rotation);

            Assert.Collection(
                card.AutomationTracks,
                track =>
                {
                    Assert.Equal(placement.Id, track.Target.ObjectId);
                    Assert.Equal(AutomationPropertyIds.PlacementX, track.Target.PropertyId);
                    Assert.All(track.Keyframes, key => Assert.Equal(0.25, key.Value, 3));
                },
                track =>
                {
                    Assert.Equal(placement.Id, track.Target.ObjectId);
                    Assert.Equal(AutomationPropertyIds.PlacementRotation, track.Target.PropertyId);
                    Assert.All(track.Keyframes, key => Assert.Equal(15, key.Value, 3));
                });
        });

    [Fact]
    public Task BuiltInEffectParametersAreDiscoverableStableAutomationTargets() =>
        ShellFixture.WithShell(shell =>
        {
            var chroma = new ChromaKeySpec { Similarity = 0.35 };
            var color = new ColorAdjustSpec { Contrast = 1.5 };
            var placement = new LayerPlacement
            {
                CompositionId = shell.Project.Compositions[0].Id,
                ChromaKey = chroma,
                ColorAdjust = color,
            };
            var card = new TextCueNode
            {
                Number = "903",
                Label = "Effect card",
                DurationMs = 3_000,
                Placements = [placement],
            };
            shell.Project.CueLists[0].Cues.Add(card);
            shell.Cues.Refresh();
            ShellFixture.Select(shell.Cues, card.Id);

            Assert.True(shell.Cues.Inspector.HasAutomationChromaKey);
            Assert.True(shell.Cues.Inspector.HasAutomationColorAdjust);
            shell.Cues.Inspector.AddLane(ChromaSimilarity);
            shell.Cues.Inspector.AddLane(ColorContrast);

            Assert.Equal(chroma.Id, card.AutomationTracks[0].Target.ObjectId);
            Assert.Equal(AutomationPropertyIds.ChromaSimilarity, card.AutomationTracks[0].Target.PropertyId);
            Assert.All(card.AutomationTracks[0].Keyframes, key => Assert.Equal(.35, key.Value, 3));
            Assert.Equal(color.Id, card.AutomationTracks[1].Target.ObjectId);
            Assert.Equal(AutomationPropertyIds.ColorContrast, card.AutomationTracks[1].Target.PropertyId);
            Assert.All(card.AutomationTracks[1].Keyframes, key => Assert.Equal(1.5, key.Value, 3));
        });
}
