using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Words on a canvas: what the document stores, and what the compiler does with it.
/// </summary>
/// <remarks>
/// The split is the whole design. The document keeps WORDS — portable, diffable, translatable — and
/// the app draws them into a picture the engine plays, because rasterising text needs a font stack
/// and a font stack belongs to an application rather than to a show file. These pin the half that has
/// to work without one.
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

    /// <summary>
    /// A card whose picture has not been drawn on this machine is a cue with NO clip.
    /// </summary>
    /// <remarks>
    /// The same honest state a media cue with no file is in, and for the same reason: one unrendered
    /// card must not stop the whole document loading in the middle of a rehearsal. The cue is still
    /// emitted so the cursor can stand on it.
    /// </remarks>
    [Fact]
    public void AnUnrenderedCardCompilesToACueWithNoClip()
    {
        var (project, card) = WithCard();

        var document = ShowCompiler.Compile(project, new ShowCompileContext());

        Assert.Empty(document.Clips);
        Assert.Contains(document.Cues, cue => cue.Id == card.Id.ToString());
    }

    [Fact]
    public void ARenderedCardBecomesAHeldStill()
    {
        var (project, card) = WithCard();

        var document = ShowCompiler.Compile(project, new ShowCompileContext
        {
            RenderedText = new Dictionary<Guid, string> { [card.Id] = "/cache/text/abc.png" },
        });

        var clip = Assert.Single(document.Clips);

        Assert.Equal("/cache/text/abc.png", clip.MediaPath);
        // A single frame arrives and the clip ends immediately, so FREEZE is what makes it a card
        // rather than a flash.
        Assert.Equal(ClipEndBehavior.FreezeLastFrame, clip.EndBehavior);
        // No audio at all: a card standing over a running bed must not interrupt it.
        Assert.Equal(-1, clip.AudioStreamIndex);
    }

    [Fact]
    public void ACardsPlacementAndFadesReachTheEngine()
    {
        var (project, card) = WithCard(item =>
        {
            item.FadeInMs = 500;
            item.FadeOutMs = 750;
            item.Placements[0].X = 0.25;
            item.Placements[0].Width = 0.5;
        });

        var clip = Assert.Single(ShowCompiler.Compile(project, new ShowCompileContext
        {
            RenderedText = new Dictionary<Guid, string> { [card.Id] = "/cache/text/abc.png" },
        }).Clips);

        Assert.Equal(TimeSpan.FromMilliseconds(500), clip.FadeIn);
        Assert.Equal(TimeSpan.FromMilliseconds(750), clip.FadeOut);
        Assert.Equal(0.25, clip.Placement!.DestX, 6);
        Assert.Equal(0.5, clip.Placement.DestWidth, 6);
    }

    /// <summary>Two cues that would draw the same card share one key, and one file.</summary>
    [Fact]
    public void TheRenderKeyIsTheContentAndNothingElse()
    {
        var one = new TextCueNode { Number = "1", Label = "A", Text = "ACT ONE" };
        var two = new TextCueNode { Number = "2", Label = "B", Text = "ACT ONE" };

        // The LABEL and the number differ and the drawing does not, so the key must not.
        Assert.Equal(one.RenderKey, two.RenderKey);

        two.Text = "ACT TWO";
        Assert.NotEqual(one.RenderKey, two.RenderKey);

        // Every field the drawing is made from moves it.
        var three = new TextCueNode { Text = "ACT ONE", Bold = true };
        Assert.NotEqual(one.RenderKey, three.RenderKey);
    }

    /// <summary>A card round-trips through the project file with its own discriminator.</summary>
    [Fact]
    public void ACardSurvivesASave()
    {
        var (project, _) = WithCard(item =>
        {
            item.FontScale = 0.2;
            item.Align = TextAlign.Left;
            item.Anchor = TextAnchor.Bottom;
            item.Foreground = "#FFCC00";
        });

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));

        var card = Assert.IsType<TextCueNode>(restored.CueLists[0].Cues[0]);

        Assert.Equal("ACT ONE", card.Text);
        Assert.Equal(0.2, card.FontScale, 6);
        Assert.Equal(TextAlign.Left, card.Align);
        Assert.Equal(TextAnchor.Bottom, card.Anchor);
        Assert.Equal("#FFCC00", card.Foreground);
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
