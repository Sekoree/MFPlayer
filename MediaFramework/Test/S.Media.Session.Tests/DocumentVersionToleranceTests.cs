using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Schema-version handling. Two actively developed apps plus an external ABI consumer share this format,
/// so a hard equality check would force a lockstep release for every additive change - but a document
/// from the future must still fail closed, because this build cannot know what it would be ignoring.
/// </summary>
public class DocumentVersionToleranceTests
{
    private static ShowDocument At(int version) => new(
        Version: version,
        Cues: [new CueDefinition("c1", 1, "C1")],
        Clips: [new ShowClipBinding("c1", "fake://a")],
        Compositions: [], Routes: []);

    [Fact]
    public void AcceptsTheCurrentVersion()
    {
        Assert.Empty(ShowDocumentValidator.Validate(At(ShowDocumentValidator.CurrentVersion)));
    }

    [Fact]
    public void AcceptsEveryVersionDownToTheMinimum()
    {
        for (var v = ShowDocumentValidator.MinimumSupportedVersion;
             v <= ShowDocumentValidator.CurrentVersion;
             v++)
        {
            Assert.Empty(ShowDocumentValidator.Validate(At(v)));
        }
    }

    [Fact]
    public void RejectsANewerVersion_LoudlyRatherThanSilentlyIgnoringFields()
    {
        var errors = ShowDocumentValidator.Validate(At(ShowDocumentValidator.CurrentVersion + 1));

        var error = Assert.Single(errors);
        Assert.Contains("unsupported document version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAVersionBelowTheSupportedFloor()
    {
        Assert.NotEmpty(ShowDocumentValidator.Validate(At(ShowDocumentValidator.MinimumSupportedVersion - 1)));
    }

    [Fact]
    public void CustomFadeShapes_AreValidated_EvenWhenDeserializedPastTheConstructor()
    {
        // A document deserialized straight into the record bypasses CustomFadeCurve's constructor checks,
        // so the validator repeats them - otherwise a malformed shape would only surface mid-fade.
        var document = At(ShowDocumentValidator.CurrentVersion) with
        {
            Clips =
            [
                new ShowClipBinding("c1", "fake://a")
                {
                    FadeInShape = new CustomFadeCurve(
                        [new FadeCurvePoint(0, 0), new FadeCurvePoint(2, 5)]),
                },
            ],
        };

        var errors = ShowDocumentValidator.Validate(document);

        Assert.Contains(errors, e => e.Message.Contains("outside 0..1", StringComparison.Ordinal));
    }

    [Fact]
    public void AValidCustomShape_PassesValidation()
    {
        var document = At(ShowDocumentValidator.CurrentVersion) with
        {
            Clips =
            [
                new ShowClipBinding("c1", "fake://a")
                {
                    FadeOutShape = new CustomFadeCurve(
                        [new FadeCurvePoint(0, 1), new FadeCurvePoint(0.5, 0.3), new FadeCurvePoint(1, 0)]),
                },
            ],
        };

        Assert.Empty(ShowDocumentValidator.Validate(document));
    }
}
