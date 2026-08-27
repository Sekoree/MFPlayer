using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Schema 3 → 4: the NDI carrier migration (2026-08-26). Old documents joined an A/V sender's two
/// rows three ways at once (Guid links, hint strings, a carries-audio flag) while the RUNTIME joined
/// them by effective name - so the migration joins by the same rule the show actually ran under, and
/// the validator refuses the states the old model let disagree.
/// </summary>
public sealed class CarrierMigrationTests
{
    private static string Schema3(string body) =>
        /*lang=json*/ $$"""{ "schemaVersion": 3, "title": "old", {{body}} }""";

    [Fact]
    public void ALinkedAvPairBecomesOneCarrierAndTheHintsRetire()
    {
        var lineId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        var project = HaCueProjectFile.Deserialize(Schema3($$"""
            "audioLines": [{ "id": "{{lineId}}", "name": "Feed", "kind": "Ndi", "channels": 8,
                             "deviceHint": "HACUE-PROG", "linkedVideoOutputId": "{{outputId}}" }],
            "videoOutputs": [{ "id": "{{outputId}}", "name": "Feed", "kind": "Ndi",
                               "targetHint": "HACUE-PROG", "ndiCarriesAudio": true,
                               "ndiAudioChannels": 2, "linkedAudioLineId": "{{lineId}}" }]
            """));

        var carrier = Assert.Single(project.NdiCarriers);
        Assert.Equal("HACUE-PROG", carrier.Name);
        Assert.Equal(carrier.Id, Assert.Single(project.AudioLines).CarrierId);
        Assert.Equal(carrier.Id, Assert.Single(project.VideoOutputs).CarrierId);

        // The hints retire: the carrier IS the on-wire identity, and a stale copy kept beside it
        // would be the second source of truth the model change removes.
        Assert.Equal("", project.AudioLines[0].DeviceHint);
        Assert.Equal("", project.VideoOutputs[0].TargetHint);

        // The legacy channel-count duplicate (2 on the video record vs the line's 8) is dropped;
        // the LINE's count - what the bay actually opened - wins.
        Assert.Equal(8, project.AudioLines[0].Channels);
    }

    [Fact]
    public void AGuidLinkedPairWhoseNamesDisagreedStaysTwoSenders()
    {
        // The old runtime joined by NAME, whatever the links said: a linked pair whose effective
        // names differed was already two senders on the wire, and honouring the link here would
        // silently merge feeds the old build kept apart.
        var lineId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        var project = HaCueProjectFile.Deserialize(Schema3($$"""
            "audioLines": [{ "id": "{{lineId}}", "name": "Audio side", "kind": "Ndi",
                             "deviceHint": "FEED-A", "linkedVideoOutputId": "{{outputId}}" }],
            "videoOutputs": [{ "id": "{{outputId}}", "name": "Video side", "kind": "Ndi",
                               "targetHint": "FEED-B", "linkedAudioLineId": "{{lineId}}" }]
            """));

        Assert.Equal(2, project.NdiCarriers.Count);
        Assert.Contains(project.NdiCarriers, carrier => carrier.Name == "FEED-A");
        Assert.Contains(project.NdiCarriers, carrier => carrier.Name == "FEED-B");
        Assert.NotEqual(project.AudioLines[0].CarrierId, project.VideoOutputs[0].CarrierId);
    }

    [Fact]
    public void UnlinkedRowsWithEqualEffectiveNamesJoinOneCarrier()
    {
        // No Guid links at all - the pre-link era, where the join WAS the equal name.
        var project = HaCueProjectFile.Deserialize(Schema3("""
            "audioLines": [{ "name": "SHOW", "kind": "Ndi" }],
            "videoOutputs": [{ "name": "SHOW", "kind": "Ndi" }]
            """));

        var carrier = Assert.Single(project.NdiCarriers);
        Assert.Equal("SHOW", carrier.Name);
        Assert.Equal(carrier.Id, project.AudioLines[0].CarrierId);
        Assert.Equal(carrier.Id, project.VideoOutputs[0].CarrierId);
    }

    [Fact]
    public void ASaveWritesTheNewShapeAndReloadsUnchanged()
    {
        var project = HaCueProjectFile.Deserialize(Schema3("""
            "audioLines": [{ "name": "SHOW", "kind": "Ndi", "linkedVideoOutputId": null }],
            "videoOutputs": [{ "name": "SHOW", "kind": "Ndi", "ndiCarriesAudio": true }]
            """));

        var json = HaCueProjectFile.Serialize(project);

        // The retired fields are write-only legacies: read from an old file, never written back.
        Assert.DoesNotContain("linkedVideoOutputId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("linkedAudioLineId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ndiCarriesAudio", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ndiAudioChannels", json, StringComparison.Ordinal);
        Assert.Contains("ndiCarriers", json, StringComparison.Ordinal);
        Assert.Contains($"\"schemaVersion\": {HaCueProject.CurrentSchemaVersion}", json, StringComparison.Ordinal);

        // Idempotent: the second pass finds carriers everywhere and invents nothing.
        var again = HaCueProjectFile.Deserialize(json);
        Assert.Single(again.NdiCarriers);
        Assert.Equal(project.NdiCarriers[0].Id, again.NdiCarriers[0].Id);
    }

    [Fact]
    public void TheValidatorRefusesTheStatesTheOldModelLetDisagree()
    {
        var project = new HaCueProject();
        var carrier = new NdiCarrierDefinition { Name = "DUP" };
        var twin = new NdiCarrierDefinition { Name = "dup" };
        project.NdiCarriers.AddRange([carrier, twin]);
        project.AudioLines.Add(new AudioLineDefinition
        {
            Name = "A", Kind = AudioLineKind.Ndi, CarrierId = carrier.Id,
        });
        project.AudioLines.Add(new AudioLineDefinition
        {
            Name = "B", Kind = AudioLineKind.Ndi, CarrierId = carrier.Id,
        });
        project.AudioLines.Add(new AudioLineDefinition
        {
            Name = "Dangling", Kind = AudioLineKind.Ndi, CarrierId = Guid.NewGuid(),
        });
        project.VideoOutputs.Add(new VideoOutputDefinition
        {
            Name = "No sender", Kind = VideoOutputKind.Ndi,
        });

        var issues = ProjectValidator.Validate(project);
        var errors = issues.Where(issue => issue.Severity == ShowValidationSeverity.Error).ToList();

        Assert.Contains(errors, issue => issue.Message.Contains("More than one NDI sender is called"));
        Assert.Contains(errors, issue => issue.Message.Contains("has 2 audio lines"));
        Assert.Contains(errors, issue => issue.Message.Contains("names a sender that no longer exists"));
        Assert.Contains(errors, issue => issue.Message.Contains("names no sender"));
        // The carrier nothing references is worth a word, not a refusal.
        Assert.Contains(issues, issue =>
            issue.Severity == ShowValidationSeverity.Warning
            && issue.Message.Contains("nothing sends under it"));
    }

    [Fact]
    public void ACleanCarrierProjectValidatesQuiet()
    {
        var project = new HaCueProject();
        var carrier = new NdiCarrierDefinition { Name = "CLEAN" };
        project.NdiCarriers.Add(carrier);
        project.AudioLines.Add(new AudioLineDefinition
        {
            Name = "Clean", Kind = AudioLineKind.Ndi, CarrierId = carrier.Id,
        });

        Assert.DoesNotContain(
            ProjectValidator.Validate(project),
            issue => issue.SubjectKind == "ndiCarrier" && issue.Severity == ShowValidationSeverity.Error);
    }
}
