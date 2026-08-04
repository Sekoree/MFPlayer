using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using S.Media.Session;
using S.Media.Source.Text;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Words on a canvas.
/// </summary>
/// <remarks>
/// The document stores WORDS — portable, diffable, translatable — and compiles to a <c>text:</c> URI
/// carrying the whole render spec. The framework's own source draws it, so a card needs no file
/// anywhere, no cache to invalidate and nothing from the app: the words travel with the show and each
/// machine draws them with the faces it has.
/// </remarks>
public class TextCueTests
{
    private static (HaCueProject Project, TextCueNode Card) WithCard(
        Action<TextCueNode>? configure = null)
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var card = new TextCueNode
        {
            Number = "1",
            Label = "Title",
            Text = "ACT ONE",
            Placements = [new LayerPlacement { CompositionId = composition.Id }],
        };

        configure?.Invoke(card);

        return (
            new HaCueProject
            {
                Compositions = [composition],
                CueLists = [new CueList { Name = "Main", Cues = [card] }],
            },
            card);
    }

    private static ShowClipBinding Compile(HaCueProject project) =>
        Assert.Single(ShowCompiler.Compile(project, new ShowCompileContext()).Clips);

    /// <summary>
    /// A card with no words is a cue with NO clip.
    /// </summary>
    /// <remarks>
    /// The same honest state a media cue with no file is in, and the state every text cue is in between
    /// being added and typed into. A card of nothing would be a black rectangle over the show.
    /// </remarks>
    [Fact]
    public void ACardWithNoWordsCompilesToACueWithNoClip()
    {
        var (project, card) = WithCard(item => item.Text = "   ");

        var document = ShowCompiler.Compile(project, new ShowCompileContext());

        Assert.Empty(document.Clips);
        Assert.Contains(document.Cues, cue => cue.Id == card.Id.ToString());
    }

    [Fact]
    public void ACardCompilesToATextUriTheFrameworkCanRead()
    {
        var (project, _) = WithCard();

        var clip = Compile(project);

        Assert.True(TextSourceUri.IsTextUri(clip.MediaPath));

        var spec = TextSourceUri.Decode(clip.MediaPath);

        Assert.NotNull(spec);
        Assert.Equal("ACT ONE", spec.Text);
        // No audio at all: a card standing over a running bed must not interrupt it.
        Assert.Equal(-1, clip.AudioStreamIndex);
    }

    /// <summary>Sizes are fractions in the document and pixels in the spec; this is the conversion.</summary>
    [Fact]
    public void FractionsBecomePixelsAgainstTheCardsOwnCanvas()
    {
        var (project, _) = WithCard(card =>
        {
            card.FontScale = 0.25;
            card.OutlineWidth = 0.01;
        });

        var spec = TextSourceUri.Decode(Compile(project).MediaPath)!;

        // A fraction survives a composition resize; the source wants pixels against its own canvas.
        Assert.Equal(spec.CanvasHeight * 0.25, spec.FontSizePx, 3);
        Assert.Equal(spec.CanvasHeight * 0.01, spec.OutlineWidthPx, 3);
    }

    [Fact]
    public void ColoursBecomeOpaqueArgbAndAnEmptyGroundIsTransparent()
    {
        var (project, _) = WithCard(card =>
        {
            card.Foreground = "#FFCC00";
            card.Background = "";
        });

        var spec = TextSourceUri.Decode(Compile(project).MediaPath)!;

        Assert.Equal(0xFFFFCC00u, spec.ColorArgb);
        // Alpha zero, not black: an empty ground means the card sits over whatever is underneath.
        Assert.Equal(0u, spec.BackgroundArgb);
    }

    [Fact]
    public void AlignmentAndDurationTravelAsThemselves()
    {
        var (project, _) = WithCard(card =>
        {
            card.Align = TextAlign.Right;
            card.Anchor = TextAnchor.Bottom;
            card.DurationMs = 4_000;
        });

        var spec = TextSourceUri.Decode(Compile(project).MediaPath)!;

        Assert.Equal(2, spec.HAlign);
        Assert.Equal(2, spec.VAlign);
        Assert.Equal(4_000, spec.DurationMs);
    }

    [Fact]
    public void ACardsPlacementAndFadesReachTheEngine()
    {
        var (project, _) = WithCard(card =>
        {
            card.FadeInMs = 500;
            card.FadeOutMs = 750;
            card.Placements[0].X = 0.25;
            card.Placements[0].Width = 0.5;
        });

        var clip = Compile(project);

        Assert.Equal(TimeSpan.FromMilliseconds(500), clip.FadeIn);
        Assert.Equal(TimeSpan.FromMilliseconds(750), clip.FadeOut);
        Assert.Equal(0.25, clip.Placement!.DestX, 6);
        Assert.Equal(0.5, clip.Placement.DestWidth, 6);
    }

    /// <summary>A card round-trips through the project file with its own discriminator.</summary>
    [Fact]
    public void ACardSurvivesASave()
    {
        var (project, _) = WithCard(card =>
        {
            card.FontScale = 0.2;
            card.Align = TextAlign.Left;
            card.Anchor = TextAnchor.Bottom;
            card.Foreground = "#FFCC00";
            card.DurationMs = 3_000;
        });

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));

        var card = Assert.IsType<TextCueNode>(restored.CueLists[0].Cues[0]);

        Assert.Equal("ACT ONE", card.Text);
        Assert.Equal(0.2, card.FontScale, 6);
        Assert.Equal(TextAlign.Left, card.Align);
        Assert.Equal(TextAnchor.Bottom, card.Anchor);
        Assert.Equal("#FFCC00", card.Foreground);
        Assert.Equal(3_000, card.DurationMs);
    }

    /// <summary>A card is placeable like any other picture, through the shared accessor.</summary>
    [Fact]
    public void ACardIsAPlaceableCue()
    {
        var (_, card) = WithCard();

        Assert.Single(CuePlacements.Of(card));
        Assert.NotNull(CuePlacements.ListOf(card));
    }
}
